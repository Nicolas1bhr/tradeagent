using System.Globalization;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;

namespace TradeAgent.Gateway;

/// <summary>
/// ONE DEPLOYMENT'S RUN, AS THIS PASS REBUILT IT: the evaluator's state, where the replay stopped,
/// and why it stopped there.
///
/// <para><paramref name="BarsReplayed"/> counts the bars the evaluator was stepped on, which are the
/// program's own — minutes for a program that declares no <c>bars</c>, hours for one on <c>bars 1h</c>;
/// <paramref name="BarsSkipped"/> and <paramref name="LastBar"/> are about the minutes the replay read.</para>
/// </summary>
public sealed record ForwardRunState(
    StrategyDeploymentRow Deployment,
    EvaluationState? State,
    int BarsReplayed,
    int BarsSkipped,
    DateTimeOffset? LastBar,
    string? Ended)
{
    /// <summary>The account as the run's own operation ledger reads it at the last bar replayed.</summary>
    public AccountReading? Account { get; init; }
}

/// <summary>
/// THE FROZEN VERSION RUN FORWARD ON PAPER: CLOSED BARS IN, INTENTS THROUGH THE EXISTING GATEWAY.
///
/// <para><c>docs/PRINCIPLES.md</c>: "Frozen strategies execute through the app's deterministic runner
/// and existing gateway", and "no model call is required for each signal or for emergency
/// protection". Both halves are literal here. There is no model client in this file, no prompt, no
/// text: a closed forward bar drives <see cref="StrategyEvaluator.Step"/> over the program's frozen
/// text, and what comes out is a <see cref="StrategyIntent"/> that becomes an ordinary
/// <c>PlaceIntent</c> through <c>TradingGateway.PlaceAsync</c> and every gate it has.</para>
///
/// <para><b>TWO CLOCKS, AS THE BACKTEST HAS THEM</b> (<c>U-timeframe-b</c>). The MINUTE clock is the
/// market's, and everything that protects a position runs on it: the operations that have an answer are
/// settled, the losing half of a stop/target pair is cancelled, the stop and the target go to the venue
/// when the entry fills — and back to it when a gate refuses an exit that took them off — and the maximum
/// hold is enforced, in the pass over the minute that needs it.
/// The DECLARED clock is the program's: only when one of its bars has closed
/// (<see cref="BarResampler"/>) is the evaluator stepped, on that bar. A program that declares no
/// <c>bars</c> is the case where the two clocks are one, and it runs exactly as it always has.</para>
///
/// <para><b>THERE IS NO STATE IN THIS PROCESS THAT A RESTART LOSES.</b> The evaluator's windows are
/// rebuilt on every pass by replaying the forward bars from the deployment's start — every one of
/// them, read page by page (<see cref="EveryBarSince"/>), and through a resampler of its own for a
/// program on declared bars — because indicators, warm-up, session, gaps and the declared bar still
/// forming are a function of the bars and of nothing else; and the position, the pending order and the
/// bars held are read back out of the run's own <c>deployment_op</c> rows and the
/// <c>execution_request</c> rows they join to. Two runners over one database therefore reach the
/// same state, and a runner that died half way through a bar reaches the state it had. An
/// in-memory cursor would have been a fifth thing to keep true across a crash.</para>
///
/// <para><b>What it claims and what it does not.</b> Forward paper observation under declared
/// bar-fill assumptions: the price existed at the open of a minute. Not executability, not queue
/// position, not intrabar ordering — protection is judged at minute granularity, because a minute is
/// the finest bar this product has. <c>docs/CONTRACTS.md</c> "The runner" says it in the same words.</para>
/// </summary>
public sealed class ForwardRuns
{
    readonly TradingGateway _gateway;
    readonly Deployments _deployments;
    readonly StrategyStore _strategies;
    readonly ForwardBarStore _bars;
    readonly VenueStore _venues;
    readonly Func<DateTimeOffset> _now;

    public ForwardRuns(TradingGateway gateway, Database db, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(db);

        _gateway = gateway;
        _deployments = gateway.Deployments;
        _strategies = new StrategyStore(db);
        _bars = new ForwardBarStore(db);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        // THE SERVED READ, ON THE RUNNER'S OWN CLOCK: a check's seven days are measured on it (U-venue-verify).
        _venues = new VenueStore(db, _now);
    }

    /// <summary>The gateway this runner dispatches through. A host that switched platforms builds a new one.</summary>
    public TradingGateway Gateway => _gateway;

    /// <summary>How many deployments one pass looks at. The ledger is small; this is a bound.</summary>
    public const int RunsLookedAt = 200;

    /// <summary>The series the runner reads. The one forward series this product collects.</summary>
    public string Source { get; init; } = ForwardBars.Source;

    /// <summary>
    /// EVERY ACTIVE DEPLOYMENT, ADVANCED OVER WHATEVER HAS CLOSED SINCE — and it never throws.
    ///
    /// <para>It runs on the same background pass as <c>ReconcilePaperDeploymentsAsync</c> and on the
    /// collector's "a minute closed", and a pass that could not run must leave the ledger alone
    /// rather than take the loop down with it. A cancellation is the one exception, as it is there.</para>
    /// </summary>
    public async Task<IReadOnlyList<ForwardRunState>> AdvanceAsync(CancellationToken ct = default)
    {
        var states = new List<ForwardRunState>();

        foreach (var head in _deployments.All(RunsLookedAt))
        {
            ct.ThrowIfCancellationRequested();
            if (_deployments.ById(head.Id) is not { IsActive: true } row) continue;

            try { states.Add(await AdvanceOneAsync(row, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _gateway.Log.TryEngineering("Gateway", "forward_run_failed", "error", ex: ex,
                    metadataJson: Json.Write(new { deployment = row.Id }));
            }
        }

        return states;
    }

    /// <summary>
    /// ONE DEPLOYMENT: the program re-read, the bars replayed from its start, and the state that
    /// leaves.
    ///
    /// <para><b>The program is re-parsed from the recorded source on every pass.</b> Not kept, not
    /// cached, not read off <c>strategy_version</c>'s restated columns — the text is what was frozen,
    /// and a runner that held a parse across a restart would be running something nobody can point
    /// at afterwards. A text that no longer parses is a sticky fault and ENDS the run.</para>
    /// </summary>
    public async Task<ForwardRunState> AdvanceOneAsync(
        StrategyDeploymentRow deployment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        if (Frozen(_strategies, deployment.VersionId) is not { } program)
            return await EndAsync(deployment, NotFrozen, ct);

        // A PROGRAM REQUIRING A DECLARATION THIS RUNNER DOES NOT IMPLEMENT IS ENDED BEFORE ITS FIRST BAR, IN WORDS —
        // before anything is settled, replayed or sent. See `Refuses`.
        if (Refuses(program) is { } refused)
            return await EndAsync(deployment, refused, ct);

        // WHAT HAS AN ANSWER, FIRST, AND THE CURSOR OVER THE BARS THAT ARE FINISHED. No wire call is
        // made here: it reads each operation's own order row. Doing it before the replay is what lets
        // this pass dispatch at all — the frontier below is the cursor's other half.
        _gateway.SettleAndAdvance(deployment, _now());
        deployment = _deployments.ById(deployment.Id) ?? deployment;
        if (!deployment.IsActive)
            return new ForwardRunState(deployment, null, 0, 0, null, deployment.EndReason);

        var bars = EveryBarSince(deployment.Symbol, deployment.StartedAt, ct);

        // THE PROGRAM IS STEPPED ON THE BARS IT DECLARES, AND THE RUNNER SAYS SO TO THE EVALUATOR, which
        // refuses any other. For a program on declared bars the minutes go through a resampler of this
        // pass's own, fed from the deployment's start like everything else here: a restart mid-hour
        // replays the same minutes into a new one and reaches the same hour still forming. Null for a
        // program on one-minute bars, whose declared bar IS the minute.
        var state = EvaluationState.Start(program, null, program.Bars);
        var grid = state.Grid;
        var resampler = program.Bars == StrategyBars.OneMinute ? null : new BarResampler(grid);
        var books = Books(deployment, bars, grid);

        // THE FRONTIER, AND IT IS THE CURSOR'S OTHER HALF: THE EARLIEST BAR THIS RUN HAS AN OPERATION
        // ON THAT THE CURSOR HAS NOT REACHED. Nothing is planned past it, and that is the whole of
        // the crash guarantee. The cursor is the last bar every one of whose operations RESOLVED, so
        // the first operation beyond it is one with no answer — an order that may be live at the
        // platform — and planning the next bar over it is how a restart sends a second one.
        var written = _deployments.OpsOf(deployment.Id);
        var blocked = written
            .Where(o => o.BarOpenTime > (deployment.CursorOpenTime ?? DateTimeOffset.MinValue))
            .Select(o => (DateTimeOffset?)o.BarOpenTime)
            .Min();

        // WHETHER THIS RUN HAS EVER WRITTEN AN EXIT, kept up as this pass writes one: the cheap first half of whether a
        // refused exit may owe the run its protection back (`PutProtectionBackAsync`), so a catch-up over thousands of
        // live minutes does not read the ledger again on each of them for a run that has never exited.
        var exited = written.Any(o => o.Kind == DeploymentOpKind.Exit);

        var replayed = 0;
        var skipped = 0;
        DateTimeOffset? last = null;
        AccountReading? account = null;
        StrategyIntent? entrySignal = null;
        string? stopRequest = null;
        string? targetRequest = null;
        var decided = new List<KlineBar>(2);

        for (var i = 0; i < bars.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var bar = Kline(bars[i]);

            // A BAR OUT OF ORDER OR SEEN TWICE IS SKIPPED AND COUNTED, NEVER STEPPED. The ledger is
            // keyed `(source, symbol, open_time)` and answers in ascending order, and the pages are
            // joined on the open time each one ended at, so this cannot happen through
            // `EveryBarSince` — it is here because the evaluator's own contract says a bar fed twice
            // counts a lookback twice, and a guard that only holds while an upstream key holds is a
            // guard nobody is keeping.
            if (last is { } previous && bar.OpenTime <= previous)
            {
                skipped++;
                _gateway.Log.TryEngineering("Gateway", "forward_run_bar_skipped", "warn",
                    metadataJson: Json.Write(new
                    {
                        deployment = deployment.Id,
                        bar = bar.OpenTime,
                        after = previous,
                        why = "the bar is not after the one before it; it was not stepped"
                    }));
                continue;
            }

            // A MINUTE THE PROGRAM'S BARS CANNOT BE BUILT FROM ENDS THE RUN, IN WORDS — before anything is
            // applied, exactly as the evaluator's own refusal of a one-minute bar off its series does. The
            // resampler would throw instead, and a pass that throws on the same minute every time is a run
            // left active that never protects anything again; ending it is the protection policy.
            if (resampler?.Refusal(bar) is { } unbuildable)
                return await EndAsync(deployment,
                    $"the minute at {bar.OpenTime.ToString("u", CultureInfo.InvariantCulture)} cannot be built "
                    + $"into this program's {grid.Spelled} bars: {unbuildable}", ct,
                    state, replayed, skipped, last, account);

            // A UTC DAY THAT CLOSED OVER THIS RUN IS ONE THING TO SAY, ONCE. The boundary is the first
            // bar of a new UTC date, and the note is about the day that ended — keyed by the
            // deployment and that date, so replaying the week raises ids the queue already holds.
            if (last is { } before && before.UtcDateTime.Date != bar.OpenTime.UtcDateTime.Date)
                _gateway.TellResearchAboutARun(deployment, MissionEventIds.DayOf(before),
                    $"the UTC day {MissionEventIds.DayOf(before)} closed over it", _now());

            // THE ACCOUNT AT THIS MINUTE, WITH THE BARS HELD COUNTED IN THE PROGRAM'S OWN BARS: through the
            // last of them that has closed by the end of this minute. On one-minute bars that is this minute.
            last = bar.OpenTime;
            account = books.At(bar, ClosedThrough(grid, bar.OpenTime));
            var opening = account;
            var seq = 0;
            var planned = false;

            // A BAR IS LIVE WHEN IT IS PAST THE CURSOR AND NOTHING EARLIER IS STILL UNANSWERED.
            // Everything before the cursor is replayed for its state and nothing else: those bars are
            // accounted for, and re-deciding them would be this software placing an order about a
            // minute that is over.
            var live = bar.OpenTime > (deployment.CursorOpenTime ?? DateTimeOffset.MinValue)
                       && (blocked is not { } wall || bar.OpenTime <= wall);

            // AND FIRST, ON A LIVE MINUTE WHOSE BOOKS READ FLAT, NO STOP OR TARGET OF THE RUN IS LEFT WORKING
            // (`U-runner-refused-close`). The cancel below is keyed to the minute a closing fill landed on and to the
            // pair this replay is tracking, and both can miss: a fill the pass's own first connector read settled
            // lands on a minute that pass then moved the cursor onto, so the pass that sees it replays that minute
            // as over and forgets the pair; and a cancel a gate refused forgets it too. Every stop and target of the
            // run is read off its own operation rows instead, on every live minute it should have none, and
            // whatever is still working is cancelled under this minute's id, however often that was tried before.
            if (live && account.Position == PositionSide.Flat)
                planned |= await CancelProtectionAsync(deployment, bar, () => seq++, ct);

            // WHAT LANDED ON THIS BAR, off the run's own executions. A position that closed takes the
            // other half of its protection off the book with it: a resting sell stop under no position
            // is an order that OPENS a short the moment it fires, and this language cannot spell one.
            var landed = books.At(bar.OpenTime);

            if (landed.Any(f => f.Kind is DeploymentOpKind.Stop or DeploymentOpKind.Target
                                or DeploymentOpKind.Exit or DeploymentOpKind.Flatten))
            {
                if (live)
                    planned |= await CancelRestingAsync(
                        deployment, bar, [stopRequest, targetRequest], () => seq++, ct);
                stopRequest = targetRequest = null;
            }

            // AND THE ENTRY'S PROTECTION GOES TO THE VENUE THE MOMENT THE ENTRY FILLED, at the
            // distances the program declared, measured from the price actually paid.
            if (landed.FirstOrDefault(f => f.Kind == DeploymentOpKind.Entry) is { Quantity: > 0m } got
                && entrySignal is { } signal)
            {
                // The ids are worked out on EVERY pass, live or not: a bar the cursor has passed
                // still put those two orders on the book, and a later bar has to be able to name them
                // to cancel the loser. They are a function of the bar and the sequence, so the replay
                // arrives at the same two strings the run first sent.
                var pair = await ProtectAsync(deployment, bar, signal, got, () => seq++, live, ct);
                stopRequest = pair.Stop;
                targetRequest = pair.Target;
                planned |= pair.Planned;
            }

            // PROTECTION FIRST, BY CODE, IN THE BACKTEST'S ORDER — AND BEFORE THE EVALUATOR IS ASKED
            // ANYTHING. `docs/PRINCIPLES.md`: "no model call is required for each signal or for
            // emergency protection", and `Backtest.Over` step 2 is the order this follows: the bar's
            // own protection is answered on the bar's own range, and only then is the program asked
            // what it wants to do from the account that leaves.
            //
            // The resting stop and the target are the VENUE'S business — they are orders, placed
            // when the entry filled. The MAXIMUM HOLD is the runner's own, because no venue has an
            // order for "this many bars": at the close of the bar that reaches the limit the position
            // is closed at market, and the evaluator then reads a FLAT account. Asked the other way
            // round it would decide from a position this app had already said must not survive the
            // bar, and its own `max_hold_bars` exit would arrive one whole bar later.
            //
            // It is asked on EVERY minute, counted in the program's bars: the count reaches the limit at
            // the close of the declared bar that reaches it — the backtest's step 3 — and stays there, so
            // a close a gate refused is asked for again on the next minute rather than the next hour.
            if (live && account.Position == PositionSide.Long
                && program.MaxHoldBars is { } hold && account.BarsSinceEntry >= hold)
            {
                planned |= await CancelRestingAsync(
                    deployment, bar, [stopRequest, targetRequest], () => seq++, ct);
                stopRequest = targetRequest = null;

                planned |= await FlattenAsync(deployment, bar, account, seq++,
                    $"max_hold_bars {hold} reached at this bar's close", ct);
                account = RunBooks.Flat(account);
            }

            // THE DECLARED CLOCK, AND ONLY THE STEP WAITS FOR IT: the program's bars that closed on this
            // minute, in order. On one-minute bars that is this minute. On declared bars it is the bar
            // whose last minute this is, and before it the bar whose last minutes the data does not have
            // and that this minute is past — both, when a whole bar of minutes is missing in between.
            // What either of them dispatches is written on THIS minute, the one the runner learned it on,
            // so the cursor, the frontier and every request id stay on the minute as they always were.
            decided.Clear();
            if (resampler is null) decided.Add(bar);
            else
            {
                if (resampler.CloseBefore(bar.OpenTime) is { } passed) decided.Add(passed);
                if (resampler.Add(bar) is { } completed) decided.Add(completed);
            }

            // AN EXIT A GATE REFUSED BEFORE THE WIRE GOES OUT AGAIN ON THE MINUTE, NOT AT THE NEXT DECLARED CLOSE
            // (`U-runner-refused-close`). On declared bars the evaluator is asked only when one of its bars closes,
            // and its one-bar hold then suppresses the exit it decided a bar earlier, so a refusal there left the
            // position open for two whole bars of the program's. On a minute that closes no declared bar, while the
            // books read long and the run's latest entry, exit or flatten is an exit refused before the wire, that
            // exit is sent again under THIS minute's id — its intent read back from its own operation, the decision
            // block unchanged, sized from the books — for as long as the decision is inside both of its bounds; and
            // the gateway judges the decision again at dispatch. Never on one-minute bars, where every minute is a
            // declared close and the program decides again itself; never on a minute a declared bar closes on,
            // which is the evaluator's.
            if (live && resampler is not null && decided.Count == 0 && account.Position == PositionSide.Long
                && ExitToSendAgain(deployment) is { } again
                && await SendExitAgainAsync(deployment, bar, again, account, () => seq++, ct))
            {
                planned = true;
                account = RunBooks.Flat(account);
            }

            foreach (var closed in decided)
            {
                // THE ACCOUNT AT THIS MINUTE, AFTER ITS PROTECTION, with the bars held counted to the bar
                // being decided — the one this minute completes is the one the account above was
                // counted to; a bar that ended before this minute is fewer bars after the entry.
                var reading = account.Position == PositionSide.Long
                    ? account with { BarsSinceEntry = books.At(bar, closed.OpenTime).BarsSinceEntry }
                    : account;

                var outcome = StrategyEvaluator.Step(state, closed, reading);
                replayed++;

                // A FAULT IS STICKY AND ENDS THE RUN. `docs/COUNCIL.md`: an interpreter fault is a
                // defined outcome — no new exposure, the app's protection policy. Ending is that policy:
                // the working orders are cancelled and the position is flattened by
                // `EndPaperDeploymentAsync`, under the run's own identity.
                if (outcome.Status == EvaluationStatus.Faulted)
                    return await EndAsync(deployment,
                        "the program faulted on the bar at " + closed.OpenTime.ToString("u", CultureInfo.InvariantCulture)
                        + ": " + (outcome.FaultReason ?? "a defined fault with no reason"), ct,
                        state, replayed, skipped, last, account);

                if (outcome.Intent is not { } signalled) continue;

                // REMEMBERED WHETHER OR NOT IT IS DISPATCHED, because a bar that is already accounted
                // for still produced the entry whose declared stop and target the next bar's fill is
                // measured against. That is what makes a restart place the same protection.
                if (signalled.Kind == IntentKind.Enter) entrySignal = signalled;

                // THE PROGRAM'S OWN EXECUTION BOUNDS, OR THE RUN IS OVER. `IntentDecision.From` is the
                // only thing that builds a decision block and it answers null for a program that
                // declared none — there is then nothing for the dispatch gate to judge staleness
                // against, and running unbounded is the one reading that cannot be right. The referee
                // refuses such a program a promotion (`U-promote-bounds`); a version that reached a
                // deployment without one was judged before that rule existed.
                if (IntentDecision.From(signalled) is not { } decision)
                    return await EndAsync(deployment,
                        "this program declares no execution bounds — no timeframe, no data_freshness "
                        + "and no max_decision_age — so nothing can say how stale its decisions are; "
                        + "the referee refuses such a program a promotion and this run is over", ct,
                        state, replayed, skipped, last, account);

                if (live && await DispatchAsync(deployment, bar, signalled, decision, reading, () => seq++, ct))
                    planned = true;
                exited |= live && signalled.Kind != IntentKind.Enter;
            }

            // AND LAST, AN EXIT REFUSED BEFORE THE WIRE PUTS THE STOP AND THE TARGET BACK, ON THE MINUTE IT WAS REFUSED
            // (`U-runner-exit-hygiene-b`), on both clocks. Every exit takes them off the book before it goes, and a gate
            // that then refuses the exit left the position open with neither. Asked after everything else this minute
            // writes, so the run's latest word — an exit sent again, the maximum hold's close — is already on the ledger;
            // and only of a run that has exited, on a minute that opened long in this pass's books — an exit is decided
            // only from a long reading, and the books re-read below hold every fill these hold, on the same bars.
            if (live && exited && opening.Position == PositionSide.Long)
                planned |= await PutProtectionBackAsync(deployment, program, bars, grid, bar, () => seq++, ct);

            // AND THE FRONTIER MOVES ONTO THIS BAR IF ANYTHING IT WROTE HAS NO ANSWER YET.
            if (planned && blocked is null
                && _deployments.OpsOf(deployment.Id)
                    .Any(o => o.BarOpenTime == bar.OpenTime && !o.IsSettled))
                blocked = bar.OpenTime;
        }

        // AND WHAT THIS PASS ANSWERED, SETTLED, WITH THE CURSOR MOVED OVER THE BARS THAT ARE NOW
        // FINISHED. A refusal is an answer: an operation a gate said no to is over, nothing was sent,
        // and a run that stalled on one would never take another bar.
        _gateway.SettleAndAdvance(deployment, _now());

        return new ForwardRunState(deployment, state, replayed, skipped, last, null) { Account = account };
    }

    /// <summary>
    /// ONE INTENT ONTO THE MONEY PATH: a market order naming the version, the decision block the
    /// program declared, and the deployment and rule it came from — written down, then dispatched
    /// through <c>PlaceAsync</c> and every gate.
    ///
    /// <para><b>An exit closes exactly what the run is holding</b> and carries
    /// <c>OrderIntent.Close</c>, so the gateway sizes it against the position it reads at dispatch and
    /// refuses it if that has moved. An entry is an opening order and is sized by the program's own
    /// declared sizing, rounded DOWN to the venue's increment.</para>
    ///
    /// <para><b>And an exit takes the run's resting stop and target off the book before it goes</b>
    /// (<see cref="CancelProtectionAsync"/>), as the maximum hold does before its close. A market exit fills at
    /// the next open, and a protective order still resting there fills too — or on the minute in progress
    /// before it — and the exit then sells into a flat account: a paper short the run's books cannot spell,
    /// after which the venue never again matches the books and every close the run sends is refused
    /// <c>POSITION_MOVED</c>. Only once the exit is sized, so an exit that rounds to nothing leaves the
    /// protection standing; a gate that then refuses the exit itself before the wire has them put back on that
    /// same minute (<see cref="PutProtectionBackAsync"/>). The update window, the kill switch and the mode
    /// refuse the cancel too, and keep it.</para>
    /// </summary>
    async Task<bool> DispatchAsync(StrategyDeploymentRow deployment, KlineBar bar,
        StrategyIntent signal, IntentDecision decision, AccountReading account, Func<int> seq,
        CancellationToken ct)
    {
        var enter = signal.Kind == IntentKind.Enter;
        var asked = enter ? signal.Quantity : account.Quantity;

        if (Sized(deployment, bar, asked, enter ? "the declared size" : "the exit's close") is not { } quantity)
            return false;

        var cancelled = !enter && await CancelProtectionAsync(deployment, bar, seq, ct);

        var intent = new PlaceIntent(deployment.Symbol,
            enter ? OrderSide.Buy : OrderSide.Sell, OrderType.Market, quantity,
            null, null, TimeInForce.Day, Comment(deployment, signal))
        {
            Intent = enter ? OrderIntent.Open : OrderIntent.Close,
            Decision = decision,
            StrategyVersionId = deployment.VersionId
        };

        var sent = await _gateway.RunDeploymentIntentAsync(deployment,
            Deployments.RequestIdFor(deployment.Id, bar.OpenTime, seq()),
            enter ? DeploymentOpKind.Entry : DeploymentOpKind.Exit, bar.OpenTime, intent, ct);
        return sent || cancelled;
    }

    /// <summary>
    /// THE EXIT TO SEND AGAIN, or null because there is none: the run's LATEST entry, exit or flatten, when it is an
    /// exit refused before the wire (<see cref="Deployments.RefusedBeforeTheWire"/>) whose decision is still inside both of its
    /// bounds at this instant.
    ///
    /// <para><b>The latest, and only the latest.</b> An exit refused earlier and followed by anything that moves the
    /// position — an exit that went out, a flatten, an entry — is over: what came after it is the run's later word.
    /// And an exit that did go out is not sent again whatever happened to it at the venue; what an order with an
    /// answer did is the position's business, read off the books.</para>
    ///
    /// <para><b>Inside both bounds, measured from the decided bar's close</b> (<see cref="IntentDecision.AgeAt"/>),
    /// which is the test <c>TradingGateway</c>'s decision gate makes at dispatch. Asked here as well so that a
    /// decision past its bound stops being sent at all, rather than written and refused once a minute; the
    /// gateway's own answer still decides every one that is sent.</para>
    /// </summary>
    PlaceIntent? ExitToSendAgain(StrategyDeploymentRow deployment)
    {
        var latest = _deployments.OpsOf(deployment.Id)
            .LastOrDefault(o => o.Kind is DeploymentOpKind.Entry or DeploymentOpKind.Exit or DeploymentOpKind.Flatten);

        if (latest is not { Kind: DeploymentOpKind.Exit }
            || !Deployments.RefusedBeforeTheWire(latest, _gateway.Requests.Get(latest.RequestId)))
            return null;

        PlaceIntent? intent;
        try { intent = Json.Read<PlaceIntent>(latest.IntentJson); }
        catch (Exception) { intent = null; }

        if (intent?.Decision is not { } decision) return null;

        var age = decision.AgeAt(_now());
        return age >= TimeSpan.Zero && age <= decision.MaxDecisionAge && age <= decision.DataFreshness
            ? intent
            : null;
    }

    /// <summary>
    /// A REFUSED EXIT, SENT AGAIN: the same intent and the same decision, sized from what the books hold now and
    /// written under this minute's own request id — so its own <c>TA-</c> client order id, and a replay of this
    /// minute collapses onto it rather than sending a second order. It is an exit like any other and takes the
    /// run's resting stop and target off the book first (see <see cref="DispatchAsync"/>).
    /// </summary>
    async Task<bool> SendExitAgainAsync(StrategyDeploymentRow deployment, KlineBar bar, PlaceIntent refused,
        AccountReading account, Func<int> seq, CancellationToken ct)
    {
        if (Sized(deployment, bar, account.Quantity, "the refused exit's close, sent again") is not { } quantity)
            return false;

        var cancelled = await CancelProtectionAsync(deployment, bar, seq, ct);
        var sent = await _gateway.RunDeploymentIntentAsync(deployment,
            Deployments.RequestIdFor(deployment.Id, bar.OpenTime, seq()),
            DeploymentOpKind.Exit, bar.OpenTime, refused with { Quantity = quantity }, ct);
        return sent || cancelled;
    }

    /// <summary>Which run, and which of its own statements. It goes onto the broker order's comment.</summary>
    static string Comment(StrategyDeploymentRow deployment, StrategyIntent signal) =>
        signal.RuleIndex >= 0
            ? $"deployment:{deployment.Id} rule {signal.RuleIndex}"
            : $"deployment:{deployment.Id} {signal.Cause}";

    /// <summary>
    /// THE STOP AND THE TARGET, AS ORDERS AT THE VENUE, the moment the entry filled — at the
    /// DISTANCES the program declared, measured from the price actually paid.
    ///
    /// <para><c>StrategyIntent</c> says it in words: the stop and target are stated at the signal bar
    /// and "an executor that fills at a different price recomputes them from the fill". Carrying the
    /// absolute levels across would put the stop a different distance away than the program declared,
    /// on every fill that is not exactly the signal bar's close — which is every fill.</para>
    ///
    /// <para><b>Both operations are RESOLVED when the venue acknowledges the resting order</b>, and
    /// that is a judgement this unit makes and states. An <c>entry</c>, <c>exit</c> or
    /// <c>flatten</c> is over only on a terminal answer, because its whole purpose is to move the
    /// position NOW. The operation here is "put protection on the book", and it is done the moment
    /// the venue says it has it — with a connector order id, which is a positive answer and not an
    /// absence. Left unresolved, the run's frontier would never move past the bar it was placed on
    /// and protection would stop the program it protects.</para>
    /// </summary>
    async Task<(string? Stop, string? Target, bool Planned)> ProtectAsync(
        StrategyDeploymentRow deployment, KlineBar bar, StrategyIntent signal, RunFill filled,
        Func<int> seq, bool live, CancellationToken ct)
    {
        string? stop = null, target = null;
        var planned = false;

        if (signal.StopPrice is { } declaredStop)
        {
            var level = filled.Price - (signal.ReferencePrice - declaredStop);
            (stop, var sent) = await RestAsync(deployment, bar, DeploymentOpKind.Stop, OrderType.Stop,
                level, filled.Quantity, seq(), live, ct);
            planned |= sent;
        }

        if (signal.TargetPrice is { } declaredTarget)
        {
            var level = filled.Price + (declaredTarget - signal.ReferencePrice);
            (target, var sent) = await RestAsync(deployment, bar, DeploymentOpKind.Target,
                OrderType.Limit, level, filled.Quantity, seq(), live, ct);
            planned |= sent;
        }

        return (stop, target, planned);
    }

    /// <summary>One resting protective order, written down, sent, and settled on the venue's answer.</summary>
    async Task<(string? Request, bool Planned)> RestAsync(StrategyDeploymentRow deployment, KlineBar bar,
        string kind, OrderType type, decimal level, decimal quantity, int seq, bool live,
        CancellationToken ct)
    {
        var request = Deployments.RequestIdFor(deployment.Id, bar.OpenTime, seq);
        if (!live) return (request, false);

        if (Sized(deployment, bar, quantity, $"the {kind}'s size") is not { } sized) return (null, false);
        if (level <= 0m)
        {
            NoTrade(deployment, bar, quantity,
                FormattableString.Invariant(
                    $"the declared {kind} works out at {level} from this fill, which is not a price"));
            return (null, false);
        }

        var intent = new PlaceIntent(deployment.Symbol, OrderSide.Sell, type, sized,
            type == OrderType.Limit ? level : null, type == OrderType.Stop ? level : null,
            TimeInForce.Day, $"deployment:{deployment.Id} {kind}")
        {
            Intent = OrderIntent.Close,
            StrategyVersionId = deployment.VersionId
        };

        await _gateway.RunDeploymentIntentAsync(deployment, request, kind, bar.OpenTime, intent, ct);

        if (_gateway.Requests.Get(request) is
            { ConnectorOrderId.Length: > 0, State: ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED } row)
            _deployments.Resolve(request, $"{row.State} — resting at the venue", _now());

        return (request, true);
    }

    /// <summary>
    /// TAKES THE RUN'S OWN RESTING ORDERS OFF THE BOOK — the loser of a stop/target pair, and both of
    /// them when the app's own protection is about to close the position.
    ///
    /// <para>An order that is not working any more is not cancelled: the read is the request row's,
    /// and a cancel of an order that has already filled is a definite refusal at the connector rather
    /// than a no-op.</para>
    /// </summary>
    async Task<bool> CancelRestingAsync(StrategyDeploymentRow deployment, KlineBar bar,
        IReadOnlyList<string?> requests, Func<int> seq, CancellationToken ct)
    {
        var cancelled = false;

        foreach (var request in requests)
        {
            if (request is not { Length: > 0 }) continue;
            if (_gateway.Requests.Get(request) is not
                { ConnectorOrderId.Length: > 0, State: ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED } row)
                continue;

            cancelled |= await _gateway.CancelDeploymentOrderAsync(deployment,
                Deployments.RequestIdFor(deployment.Id, bar.OpenTime, seq()), bar.OpenTime,
                row.ConnectorOrderId!, ct);
        }

        return cancelled;
    }

    /// <summary>
    /// EVERY STOP AND TARGET OF THIS RUN STILL WORKING AT THE VENUE, TAKEN OFF THE BOOK — read off the run's own
    /// <c>stop</c> and <c>target</c> operations and the order rows they join to, rather than off the pair this
    /// pass's replay happens to be tracking, which a fill seen late or a refused cancel can have lost. What is
    /// not working any more is left alone, exactly as <see cref="CancelRestingAsync"/> leaves it.
    /// </summary>
    Task<bool> CancelProtectionAsync(StrategyDeploymentRow deployment, KlineBar bar, Func<int> seq,
        CancellationToken ct) =>
        CancelRestingAsync(deployment, bar,
            [.. _deployments.OpsOf(deployment.Id)
                .Where(o => o.Kind is DeploymentOpKind.Stop or DeploymentOpKind.Target)
                .Select(o => (string?)o.RequestId)],
            seq, ct);

    /// <summary>
    /// AN EXIT REFUSED BEFORE THE WIRE PUTS THE RUN'S STOP AND TARGET BACK — at their own levels, sized from the books,
    /// under this minute's ids — and this answers whether it wrote anything (<c>U-runner-exit-hygiene-b</c>).
    ///
    /// <para><b>Only after a refusal that sent nothing.</b> The run's LATEST entry, exit or flatten must be an exit
    /// <see cref="Deployments.RefusedBeforeTheWire"/> — the one predicate — so never while an exit is in flight: one at
    /// the wire is about to end the position, and protection put back beside it fills too and sells what the exit sells.
    /// Never past the maximum hold either: its own close is asked again on every minute and takes protection off first.</para>
    ///
    /// <para><b>The books are re-read</b>, not the minute's reading, which a refused re-send has already marked flat:
    /// long, or nothing goes back.</para>
    ///
    /// <para><b>Each of the two goes back while none of its kind of the run may still be working</b>
    /// (<see cref="MayBeWorking"/>) — a second stop beside one that may be resting sells twice — and each is the LATEST
    /// of its kind written since the position's entry, at the level that operation carries. A kind the program never
    /// declared has none, and nothing goes back for it. The orders are new: their own operations, their own <c>dp-</c>
    /// and <c>TA-</c> ids, through every gate a protective order meets — the stale-close read among them, a stop and a
    /// target being <c>OrderIntent.Close</c> — and settled on the venue's acknowledgement as <see cref="RestAsync"/>
    /// settles the entry's. Nothing goes out again under an id that was refused: a re-place that a gate refuses too is
    /// asked for afresh on the next live minute, by this same rule, under that minute's ids.</para>
    /// </summary>
    async Task<bool> PutProtectionBackAsync(StrategyDeploymentRow deployment, StrategyProgram program,
        IReadOnlyList<ForwardBar> bars, BarGrid grid, KlineBar bar, Func<int> seq, CancellationToken ct)
    {
        var ops = _deployments.OpsOf(deployment.Id);

        if (ops.LastOrDefault(o => o.Kind is DeploymentOpKind.Entry or DeploymentOpKind.Exit or DeploymentOpKind.Flatten)
                is not { Kind: DeploymentOpKind.Exit } exit
            || !Deployments.RefusedBeforeTheWire(exit, _gateway.Requests.Get(exit.RequestId)))
            return false;

        var held = Books(deployment, bars, grid).At(bar, ClosedThrough(grid, bar.OpenTime));
        if (held.Position != PositionSide.Long) return false;
        if (program.MaxHoldBars is { } hold && held.BarsSinceEntry >= hold) return false;

        var entry = -1;
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Kind == DeploymentOpKind.Entry) entry = i;
        if (entry < 0) return false;

        var planned = false;
        foreach (var (kind, type) in Protection)
        {
            if (ops.Any(o => o.Kind == kind && MayBeWorking(o))) continue;

            DeploymentOpRow? latest = null;
            for (var i = entry + 1; i < ops.Count; i++)
                if (ops[i].Kind == kind) latest = ops[i];
            if (latest is null || LevelOf(latest, type) is not { } level) continue;

            var (_, sent) = await RestAsync(deployment, bar, kind, type, level, held.Quantity, seq(), live: true, ct);
            planned |= sent;
        }

        return planned;
    }

    /// <summary>A position's two protective orders, in the order <see cref="ProtectAsync"/> places them.</summary>
    static readonly (string Kind, OrderType Type)[] Protection =
        [(DeploymentOpKind.Stop, OrderType.Stop), (DeploymentOpKind.Target, OrderType.Limit)];

    /// <summary>
    /// WHETHER ONE OF THE RUN'S STOPS OR TARGETS MAY STILL BE WORKING AT THE VENUE: it did not end before the wire, and
    /// its order row is not terminal — or there is no row yet behind an operation with no answer. UNKNOWN reads as
    /// working: an order nobody can account for may be resting, and that is the direction that never sells twice.
    /// </summary>
    bool MayBeWorking(DeploymentOpRow op)
    {
        var row = _gateway.Requests.Get(op.RequestId);
        if (Deployments.RefusedBeforeTheWire(op, row)) return false;
        return row is null ? !op.IsSettled : !OrderStateMachine.IsTerminal(row.State);
    }

    /// <summary>The level one stop or target went out at, off its own operation: a stop's trigger, a target's limit.</summary>
    static decimal? LevelOf(DeploymentOpRow op, OrderType type)
    {
        PlaceIntent? intent;
        try { intent = Json.Read<PlaceIntent>(op.IntentJson); }
        catch (Exception) { return null; }

        return type == OrderType.Stop ? intent?.StopPrice : intent?.LimitPrice;
    }

    /// <summary>
    /// THE MAXIMUM HOLD, ENFORCED: one market close of exactly what this run is holding, written
    /// down before it is sent and dispatched under the run's own identity.
    ///
    /// <para>It names the VERSION, because the account is under a standing paper envelope and an
    /// order naming none is refused <c>ENVELOPE_ACCOUNT_RESERVED</c> there — that refusal is right
    /// and this is what satisfies it. <c>OrderIntent.Close</c>, because it is one: the gateway's
    /// unresolved-reducer refusal and its stale-close read are gates a close must pass, and a
    /// protection order that dodged them by calling itself an opening trade would be dodging them on
    /// the one path where being wrong costs a position.</para>
    /// </summary>
    async Task<bool> FlattenAsync(StrategyDeploymentRow deployment, KlineBar bar,
        AccountReading account, int seq, string why, CancellationToken ct)
    {
        if (Sized(deployment, bar, account.Quantity, "the maximum hold's close") is not { } quantity)
            return false;

        var intent = new PlaceIntent(deployment.Symbol, OrderSide.Sell, OrderType.Market, quantity,
            null, null, TimeInForce.Day, $"deployment:{deployment.Id} {why}")
        {
            Intent = OrderIntent.Close,
            StrategyVersionId = deployment.VersionId
        };

        return await _gateway.RunDeploymentIntentAsync(deployment,
            Deployments.RequestIdFor(deployment.Id, bar.OpenTime, seq),
            DeploymentOpKind.Flatten, bar.OpenTime, intent, ct);
    }

    /// <summary>
    /// A SIZE ROUNDED **DOWN** TO THE INSTRUMENT'S VERIFIED INCREMENT, or null because there is no
    /// order to place — and the reason is recorded rather than swallowed.
    ///
    /// <para>Down, never to the nearest: a size rounded up is a position larger than the program
    /// asked for, on money the allocation ceiling was computed against. A size that rounds to nothing
    /// is a real answer and a NO-TRADE with a reason, exactly as it is in the backtest — not a fault,
    /// not a retry, and not a minimum order the app invented.</para>
    ///
    /// <para><b>The increment comes off the venue catalogue's VERIFIED rows and nowhere else</b>,
    /// which is <c>Backtests.Increment</c>'s judgement applied to the same number: an increment
    /// nobody confirmed against the venue's own definition would make every simulated position one
    /// that could not have been taken. Out of the box that list is empty, so a run on an installation
    /// whose owner has recorded no venue row places nothing and says why.</para>
    /// </summary>
    decimal? Sized(StrategyDeploymentRow deployment, KlineBar bar, decimal quantity, string what)
    {
        if (Increment(deployment.Symbol) is not { } step)
        {
            NoTrade(deployment, bar, quantity,
                $"this installation's venue catalogue holds no VERIFIED instrument '{deployment.Symbol}', "
                + "so there is no quantity increment to round a size down to");
            return null;
        }

        var sized = quantity <= 0m ? 0m : decimal.Truncate(quantity / step) * step;
        if (sized > 0m) return sized;

        NoTrade(deployment, bar, quantity,
            FormattableString.Invariant(
                $"{what} came to {quantity}, which rounds down to nothing at the venue's quantity increment of {step}"));
        return null;
    }

    /// <summary>The verified quantity increment for this instrument, or null because no row confirms one.</summary>
    decimal? Increment(string symbol) =>
        _venues.Instruments()
            .FirstOrDefault(i => i.Verified
                                 && string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            ?.QuantityIncrement;

    /// <summary>A size that produced no order, recorded with its reason. Nothing is retried on it.</summary>
    void NoTrade(StrategyDeploymentRow deployment, KlineBar bar, decimal quantity, string why) =>
        _gateway.Log.TryEngineering("Gateway", "forward_run_no_trade", "warn",
            metadataJson: Json.Write(new
            {
                deployment = deployment.Id, bar = bar.OpenTime, quantity, why
            }));

    /// <summary>
    /// THE START OF THE LAST OF THE PROGRAM'S BARS THAT HAS CLOSED BY THE END OF THIS MINUTE: the bar this
    /// minute completes when it is the bar's last minute — the rule <see cref="BarResampler.Add"/> closes a
    /// bar by — and otherwise the bar before the one still forming. On one-minute bars it is the minute.
    /// </summary>
    static DateTimeOffset ClosedThrough(BarGrid grid, DateTimeOffset minute)
    {
        var start = grid.StartOf(minute);
        return minute + StrategyBars.OneMinute >= grid.EndOf(start) ? start : grid.PreviousStart(start);
    }

    /// <summary>
    /// WHY THIS BUILD'S RUNNER CANNOT RUN ANY DEPLOYMENT OF A VERSION, IN WORDS — or null when it can.
    ///
    /// <para>Every reason is the version's own and none of them is about its bars: this installation has no
    /// row for it, its recorded text no longer parses in this build, it parses to a different program with
    /// a different id (<see cref="Frozen"/>), or it requires a declaration this runner does not implement
    /// (<see cref="Refuses"/>). A run of such a version is ended before its first bar with this
    /// sentence, and a replacement would be ended the same way at its first pass — another row, another
    /// flatten, another paid wake for Research — so <c>TradingGateway.StartPaperDeploymentsDue</c> asks this
    /// before it starts one, and once one run of an allocation exists starts none (<c>U-timeframe-b</c>).</para>
    /// </summary>
    public static string? CannotRun(StrategyStore strategies, string versionId)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        return Frozen(strategies, versionId) is not { } program ? NotFrozen : Refuses(program);
    }

    /// <summary>
    /// THE DECLARATION KINDS THIS RUNNER IMPLEMENTS (<c>U-language-v2a</c> item 2; R05 row 10): every kind this build
    /// parses but <c>feature</c>. It computes no feature value — <c>U-runner-features</c> values features at its decision
    /// instant, absent meaning no decision, and lifts this — so a program that reads one is refused here rather than
    /// stepped without its inputs.
    /// </summary>
    public static readonly IReadOnlyList<string> Implements =
        [.. StrategyDeclarations.All.Where(k => k != StrategyDeclarations.Feature)];

    /// <summary>
    /// WHY THIS RUNNER WILL NOT STEP <paramref name="program"/>, IN WORDS — or null when it will: the program requires a
    /// declaration it does not implement (<see cref="Implements"/>). Such a run is ENDED before a bar is stepped, with
    /// this sentence on the deployment's own line for the owner and in the one note Research is sent, and nothing is
    /// ever sent for it; <see cref="CannotRun"/> says the same, so the deployment sweep starts no replacement.
    /// </summary>
    public static string? Refuses(StrategyProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (StrategyDeclarations.Refusal(program, Implements, "this build's paper runner") is not { } words) return null;

        var reads = program.Features.Count == 0
            ? ""
            : $" It reads {string.Join(", ", program.Features.Select(f => $"`{f.Name}`"))}, and this runner computes no feature "
              + "value yet: programs that read features run on paper after a later update.";
        return words + "." + reads + " It was ended before a bar was stepped, and nothing was sent";
    }

    /// <summary>What the runner says when it cannot run a version, on the deployment's line and to Research.</summary>
    const string NotFrozen =
        "the frozen text of the version this run was started on no longer parses in this "
        + "build as that version — it is refused, or this build's language manifest reads it as "
        + "a different program with a different id — so there is nothing to step";

    /// <summary>
    /// The frozen program of a version, re-parsed — or null because it no longer parses, or because it now
    /// parses to a DIFFERENT program than the version the run was started on.
    ///
    /// <para>The second is what a <c>StrategyVersions.Manifest</c> bump does: the same text, read
    /// under another language, indicator or calendar meaning, hashes to another id
    /// (<c>StrategyProgram.StrategyId</c>). Stepping it would trade a program nobody judged under the
    /// id of one somebody did, so the id is checked against the deployment's own version on every pass.
    /// <c>Promotions.Standing</c> withdraws the verdict on the same change and the reconcile pass ends
    /// the run on that; this is the runner refusing to step it in the meantime, whichever pass comes
    /// first (<c>U-evidence-identity</c>).</para>
    /// </summary>
    static StrategyProgram? Frozen(StrategyStore strategies, string versionId)
    {
        if (strategies.VersionById(versionId) is not { } version) return null;
        var parsed = StrategyParser.Parse(version.Source);
        return parsed.Program is { } program
               && string.Equals(program.StrategyId, versionId, StringComparison.Ordinal)
            ? program
            : null;
    }

    async Task<ForwardRunState> EndAsync(
        StrategyDeploymentRow deployment, string why, CancellationToken ct,
        EvaluationState? state = null, int replayed = 0, int skipped = 0,
        DateTimeOffset? last = null, AccountReading? account = null)
    {
        await _gateway.EndPaperDeploymentAsync(deployment.Id, why, ct);
        _gateway.Log.TryEngineering("Gateway", "forward_run_ended", "warn",
            metadataJson: Json.Write(new { deployment = deployment.Id, why }));
        return new ForwardRunState(deployment, state, replayed, skipped, last, why) { Account = account };
    }

    /// <summary>How many bars one read of the ledger asks for: the store's own cap, one page.</summary>
    public const int BarsPerPage = DatasetReader.MaxBars;

    /// <summary>
    /// EVERY FORWARD BAR SINCE THE DEPLOYMENT STARTED, ASCENDING, PAGED UNTIL THE LEDGER IS EXHAUSTED
    /// (<c>U-runner-forward</c>).
    ///
    /// <para><b>What was wrong.</b> One read of <see cref="ForwardBarStore.Since"/> answers at most
    /// a page — ten thousand bars, about 6.9 days of minutes — and the runner took that one page as
    /// the whole run. Nothing after it was ever stepped: a deployment a week old stopped deciding,
    /// and its maximum hold stopped counting with a position still open.</para>
    ///
    /// <para><b>The pages are joined on the open time each one ended at</b>, which is exact: the
    /// ledger is keyed on the open time and answers strictly after it, ascending, so no bar is read
    /// twice and none is skipped. A page that comes back short is the end of the ledger.</para>
    ///
    /// <para><b>The cost is O(age) in time AND in memory, on every pass</b>, and it is the price of
    /// "no state a restart loses": the bars ARE the run's state, so every pass replays them all from
    /// the start and holds them all while it does. <c>docs/CONTRACTS.md</c> "The runner" states the
    /// figure measured on the development Mac.</para>
    /// </summary>
    List<ForwardBar> EveryBarSince(string symbol, DateTimeOffset startedAt, CancellationToken ct)
    {
        var bars = new List<ForwardBar>();
        var after = startedAt;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = _bars.Since(symbol, after, BarsPerPage, Source);
            bars.AddRange(page);
            if (page.Count < BarsPerPage) return bars;
            after = page[^1].OpenTime;
        }
    }

    static KlineBar Kline(ForwardBar b) => new(b.OpenTime, b.Open, b.High, b.Low, b.Close, b.Volume);

    /// <summary>
    /// THE RUN'S OWN BOOKS, REBUILT FROM ITS OWN ROWS — what it is holding, what it paid, what it has
    /// in flight, and since which bar.
    ///
    /// <para><b>Its own operations and nobody else's.</b> The account under a paper envelope may also
    /// hold the owner's own position; a runner that sized against the account's total would be
    /// charging one run for another's exposure. Every figure here comes off this deployment's
    /// <c>deployment_op</c> rows joined to <c>execution_request</c> and <c>fill</c> by request id.</para>
    /// </summary>
    RunBooks Books(StrategyDeploymentRow deployment, IReadOnlyList<ForwardBar> bars, BarGrid grid)
    {
        var ops = _deployments.OpsOf(deployment.Id);
        var fills = _gateway.Fills.Since(deployment.StartedAt)
            .Where(f => f.RequestId is { Length: > 0 })
            .ToLookup(f => f.RequestId!, StringComparer.Ordinal);

        var settled = new List<RunFill>();
        var inFlight = new List<RunOp>();

        foreach (var op in ops)
        {
            var request = _gateway.Requests.Get(op.RequestId);
            DateTimeOffset? filled = null;

            foreach (var fill in fills[op.RequestId])
            {
                var at = FillBar(bars, op.BarOpenTime, fill.At);
                settled.Add(new RunFill(at, op.Kind, fill.Quantity, fill.Price));
                if (filled is null || at < filled) filled = at;
            }

            // IN FLIGHT MEANS AN ORDER THAT COULD MOVE THE POSITION AND HAS NOT ANSWERED — and a
            // resting stop or target is not one. They are WORKING for as long as the position is
            // open, and reading them as "pending" would suppress every exit signal the program has,
            // which is the opposite of protection. `AccountReading.OrderPending` is the authority the
            // evaluator uses to refuse a duplicate ENTRY.
            //
            // AND A REFUSAL THAT SENT NOTHING IS NOT ONE EITHER (`U-runner-refused-close`): see
            // Deployments.RefusedBeforeTheWire. Its row never reaches the wire, and reading that as an
            // order in flight suppressed every exit while long and every entry while flat from then on —
            // the run frozen, its envelope's slot held, by an order that provably does not exist.
            if (request is not null
                && op.Kind is DeploymentOpKind.Entry or DeploymentOpKind.Exit or DeploymentOpKind.Flatten
                && !OrderStateMachine.IsTerminal(request.State)
                && !Deployments.RefusedBeforeTheWire(op, request))
                inFlight.Add(new RunOp(op.BarOpenTime, filled));
        }

        settled.Sort((a, b) => a.Bar.CompareTo(b.Bar));
        return new RunBooks(settled, inFlight, Capital(deployment), grid);
    }

    /// <summary>
    /// WHICH BAR A FILL LANDED ON, from two recorded facts and no guess: the bar the operation was
    /// written for, and the instant the execution carries.
    ///
    /// <para>The floor is the first bar STRICTLY AFTER the operation's own bar, which is the paper
    /// connector's declared rule — an order placed at a bar's close fills at the next closed bar's
    /// open and never earlier. Above that floor the answer is the last bar that had OPENED when the
    /// execution was stamped, which is what places a resting stop or target on the bar that actually
    /// triggered it. Neither half is a simulation of the connector: one is its stated contract, the
    /// other is the instant on the row it wrote.</para>
    /// </summary>
    static DateTimeOffset FillBar(
        IReadOnlyList<ForwardBar> bars, DateTimeOffset opBar, DateTimeOffset at)
    {
        var floor = DateTimeOffset.MaxValue;
        var stamped = DateTimeOffset.MinValue;

        foreach (var bar in bars)
        {
            if (bar.OpenTime > opBar && bar.OpenTime < floor) floor = bar.OpenTime;

            // CLOSED, not merely opened. A bar's close IS the next bar's open, and this settlement
            // happens when a bar closes — reading "had opened" would put every fill one bar late, on
            // the minute that had just started.
            if (bar.CloseTime <= at && bar.OpenTime > stamped) stamped = bar.OpenTime;
        }

        return stamped > floor ? stamped : floor;
    }

    /// <summary>
    /// WHAT THIS RUN MAY SPEND, and it is the owner's own declared ceiling rather than a balance.
    ///
    /// <para>The allocation's value ceiling when the owner declared one, and its quantity ceiling
    /// priced at the reference otherwise. Nothing here is derived from the account's equity: a
    /// paper account's equity is a number this software made up, and sizing a run against it would
    /// let a simulated profit buy a larger simulated position.</para>
    /// </summary>
    decimal Capital(StrategyDeploymentRow deployment) =>
        _gateway.Allocations.ById(deployment.AllocationId) is { } allocation
            ? allocation.MaxNotional ?? 0m
            : 0m;

    /// <summary>One of this run's own executions, placed on the bar it landed on.</summary>
    readonly record struct RunFill(DateTimeOffset Bar, string Kind, decimal Quantity, decimal Price);

    /// <summary>
    /// ONE ORDER OF THIS RUN THAT COULD STILL MOVE THE POSITION: the bar it was sent on, and the bar
    /// it landed on if it has. It is PENDING at every bar from the first to the second — and at no
    /// bar before it was sent, which is what keeps a replay of the run's own history from reading
    /// every past bar as one where an order was in flight.
    /// </summary>
    readonly record struct RunOp(DateTimeOffset Bar, DateTimeOffset? Filled);

    /// <summary>
    /// THE RUN'S POSITION AS OF ANY BAR, walked forward from its own executions. Long or flat: the
    /// language cannot spell a third value and neither can this.
    /// </summary>
    sealed class RunBooks(IReadOnlyList<RunFill> fills, IReadOnlyList<RunOp> inFlight, decimal capital, BarGrid grid)
    {
        /// <summary>This run's own executions that landed on one bar, in the order they landed.</summary>
        public IReadOnlyList<RunFill> At(DateTimeOffset bar) =>
            [.. fills.Where(f => f.Bar == bar)];

        /// <summary>
        /// THE RUN'S ACCOUNT AT THE CLOSE OF ONE MINUTE, from its own executions up to that minute, with the
        /// bars held counted on the program's grid through the bar that opens at or contains
        /// <paramref name="through"/>.
        /// </summary>
        public AccountReading At(KlineBar bar, DateTimeOffset through)
        {
            var quantity = 0m;
            var average = 0m;
            var realised = 0m;
            DateTimeOffset? since = null;

            foreach (var fill in fills)
            {
                if (fill.Bar > bar.OpenTime) break;

                if (string.Equals(fill.Kind, DeploymentOpKind.Entry, StringComparison.Ordinal))
                {
                    average = quantity + fill.Quantity <= 0m
                        ? fill.Price
                        : (average * quantity + fill.Price * fill.Quantity) / (quantity + fill.Quantity);
                    quantity += fill.Quantity;
                    since ??= fill.Bar;
                }
                else
                {
                    var closed = Math.Min(quantity, fill.Quantity);
                    realised += (fill.Price - average) * closed;
                    quantity -= closed;
                    if (quantity <= 0m) { quantity = 0m; average = 0m; since = null; }
                }
            }

            var held = since is { } entry ? BarsSince(entry, through) : 0;
            var equity = capital + realised + (quantity > 0m ? (bar.Close - average) * quantity : 0m);

            var pending = inFlight.Any(o =>
                o.Bar <= bar.OpenTime && (o.Filled is not { } at || at > bar.OpenTime));

            return new AccountReading(
                capital, equity, quantity > 0m ? PositionSide.Long : PositionSide.Flat,
                quantity, quantity > 0m ? average : 0m, pending, held);
        }

        /// <summary>
        /// THE SAME READING WITH THE POSITION CLOSED — what the evaluator is handed on a bar the
        /// app's own protection closed at the close. Not a fresh read of the ledger: the close has
        /// been SENT and has not filled, and reading the book again here would hand the program back
        /// the position it was just told is over.
        /// </summary>
        public static AccountReading Flat(AccountReading account) => account with
        {
            Position = PositionSide.Flat,
            Quantity = 0m,
            AverageFillPrice = 0m,
            BarsSinceEntry = 0,
            OrderPending = true
        };

        /// <summary>
        /// HOW MANY BARS THIS POSITION HAS BEEN HELD, counted in the PROGRAM'S OWN BARS. Whole bars of its
        /// grid between the one the entry filled in and the one <paramref name="through"/> falls in, which
        /// is the count `max_hold_bars` is stated in and the count the backtest's `ordinal - entryOrdinal`
        /// is: on one-minute bars the minutes between the two, on hourly bars the hours.
        /// </summary>
        int BarsSince(DateTimeOffset entry, DateTimeOffset through)
        {
            var elapsed = grid.Between(entry, through);
            return elapsed <= 0 ? 0 : (int)Math.Min(elapsed, int.MaxValue);
        }
    }
}
