using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;

namespace TradeAgent.Gateway;

/// <summary>
/// ONE DEPLOYMENT'S RUN, AS THIS PASS REBUILT IT: the evaluator's state, where the replay stopped,
/// and why it stopped there.
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
/// <para><b>THERE IS NO STATE IN THIS PROCESS THAT A RESTART LOSES.</b> The evaluator's windows are
/// rebuilt on every pass by replaying the forward bars from the deployment's start — indicators,
/// warm-up, session and gaps are a function of the bars and of nothing else — and the position, the
/// pending order and the bars held are read back out of the run's own <c>deployment_op</c> rows and
/// the <c>execution_request</c> rows they join to. Two runners over one database therefore reach the
/// same state, and a runner that died half way through a bar reaches the state it had. An
/// in-memory cursor would have been a fifth thing to keep true across a crash.</para>
///
/// <para><b>What it claims and what it does not.</b> Forward paper observation under declared
/// bar-fill assumptions: the price existed at the open of a bar. Not executability, not queue
/// position, not intrabar ordering — protection is judged at bar granularity, because a bar is what
/// this product has. <c>docs/CONTRACTS.md</c> "The runner" says it in the same words.</para>
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
        _venues = new VenueStore(db);
        _now = now ?? (() => DateTimeOffset.UtcNow);
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

        if (Frozen(deployment) is not { } program)
            return await EndAsync(deployment,
                "the frozen text of the version this run was started on no longer parses in this "
                + "build, so there is nothing to step", ct);

        // WHAT HAS AN ANSWER, FIRST, AND THE CURSOR OVER THE BARS THAT ARE FINISHED. No wire call is
        // made here: it reads each operation's own order row. Doing it before the replay is what lets
        // this pass dispatch at all — the frontier below is the cursor's other half.
        _gateway.SettleAndAdvance(deployment, _now());
        deployment = _deployments.ById(deployment.Id) ?? deployment;
        if (!deployment.IsActive)
            return new ForwardRunState(deployment, null, 0, 0, null, deployment.EndReason);

        var bars = _bars.Since(deployment.Symbol, deployment.StartedAt, source: Source);
        var state = EvaluationState.Start(program, null, ForwardBars.BarLength);
        var books = Books(deployment, bars);

        // THE FRONTIER, AND IT IS THE CURSOR'S OTHER HALF: THE EARLIEST BAR THIS RUN HAS AN OPERATION
        // ON THAT THE CURSOR HAS NOT REACHED. Nothing is planned past it, and that is the whole of
        // the crash guarantee. The cursor is the last bar every one of whose operations RESOLVED, so
        // the first operation beyond it is one with no answer — an order that may be live at the
        // platform — and planning the next bar over it is how a restart sends a second one.
        var blocked = _deployments.OpsOf(deployment.Id)
            .Where(o => o.BarOpenTime > (deployment.CursorOpenTime ?? DateTimeOffset.MinValue))
            .Select(o => (DateTimeOffset?)o.BarOpenTime)
            .Min();

        var replayed = 0;
        var skipped = 0;
        DateTimeOffset? last = null;
        AccountReading? account = null;
        StrategyIntent? entrySignal = null;
        string? stopRequest = null;
        string? targetRequest = null;

        for (var i = 0; i < bars.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var bar = Kline(bars[i]);

            // A BAR OUT OF ORDER OR SEEN TWICE IS SKIPPED AND COUNTED, NEVER STEPPED. The ledger is
            // keyed `(source, symbol, open_time)` and answers in ascending order, so this cannot
            // happen through `Since` — it is here because the evaluator's own contract says a bar
            // fed twice counts a lookback twice, and a guard that only holds while an upstream key
            // holds is a guard nobody is keeping.
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

            // A UTC DAY THAT CLOSED OVER THIS RUN IS ONE THING TO SAY, ONCE. The boundary is the first
            // bar of a new UTC date, and the note is about the day that ended — keyed by the
            // deployment and that date, so replaying the week raises ids the queue already holds.
            if (last is { } before && before.UtcDateTime.Date != bar.OpenTime.UtcDateTime.Date)
                _gateway.TellResearchAboutARun(deployment, MissionEventIds.DayOf(before),
                    $"the UTC day {MissionEventIds.DayOf(before)} closed over it", _now());

            last = bar.OpenTime;
            account = books.At(i, bar);
            var seq = 0;
            var planned = false;

            // A BAR IS LIVE WHEN IT IS PAST THE CURSOR AND NOTHING EARLIER IS STILL UNANSWERED.
            // Everything before the cursor is replayed for its state and nothing else: those bars are
            // accounted for, and re-deciding them would be this software placing an order about a
            // minute that is over.
            var live = bar.OpenTime > (deployment.CursorOpenTime ?? DateTimeOffset.MinValue)
                       && (blocked is not { } wall || bar.OpenTime <= wall);

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

            var outcome = StrategyEvaluator.Step(state, bar, account);
            replayed++;

            // A FAULT IS STICKY AND ENDS THE RUN. `docs/COUNCIL.md`: an interpreter fault is a
            // defined outcome — no new exposure, the app's protection policy. Ending is that policy:
            // the working orders are cancelled and the position is flattened by
            // `EndPaperDeploymentAsync`, under the run's own identity.
            if (outcome.Status == EvaluationStatus.Faulted)
                return await EndAsync(deployment,
                    "the program faulted on the bar at " + bar.OpenTime.ToString("u")
                    + ": " + (outcome.FaultReason ?? "a defined fault with no reason"), ct,
                    state, replayed, skipped, last, account);

            if (outcome.Intent is { } signalled)
            {
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

                if (live && await DispatchAsync(deployment, bar, signalled, decision, account, seq++, ct))
                    planned = true;
            }

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
    /// </summary>
    async Task<bool> DispatchAsync(StrategyDeploymentRow deployment, KlineBar bar,
        StrategyIntent signal, IntentDecision decision, AccountReading account, int seq,
        CancellationToken ct)
    {
        var enter = signal.Kind == IntentKind.Enter;
        var asked = enter ? signal.Quantity : account.Quantity;

        if (Sized(deployment, bar, asked, enter ? "the declared size" : "the exit's close") is not { } quantity)
            return false;

        var intent = new PlaceIntent(deployment.Symbol,
            enter ? OrderSide.Buy : OrderSide.Sell, OrderType.Market, quantity,
            null, null, TimeInForce.Day, Comment(deployment, signal))
        {
            Intent = enter ? OrderIntent.Open : OrderIntent.Close,
            Decision = decision,
            StrategyVersionId = deployment.VersionId
        };

        return await _gateway.RunDeploymentIntentAsync(deployment,
            Deployments.RequestIdFor(deployment.Id, bar.OpenTime, seq),
            enter ? DeploymentOpKind.Entry : DeploymentOpKind.Exit, bar.OpenTime, intent, ct);
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
                $"the declared {kind} works out at {level} from this fill, which is not a price");
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
            $"{what} came to {quantity}, which rounds down to nothing at the venue's quantity "
            + $"increment of {step}");
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

    /// <summary>The frozen program of this run's version, re-parsed, or null because it no longer parses.</summary>
    StrategyProgram? Frozen(StrategyDeploymentRow deployment)
    {
        if (_strategies.VersionById(deployment.VersionId) is not { } version) return null;
        var parsed = StrategyParser.Parse(version.Source);
        return parsed.Ok ? parsed.Program : null;
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
    RunBooks Books(StrategyDeploymentRow deployment, IReadOnlyList<ForwardBar> bars)
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
            if (request is not null
                && op.Kind is DeploymentOpKind.Entry or DeploymentOpKind.Exit or DeploymentOpKind.Flatten
                && !OrderStateMachine.IsTerminal(request.State))
                inFlight.Add(new RunOp(op.BarOpenTime, filled));
        }

        settled.Sort((a, b) => a.Bar.CompareTo(b.Bar));
        return new RunBooks(settled, inFlight, Capital(deployment));
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
    sealed class RunBooks(IReadOnlyList<RunFill> fills, IReadOnlyList<RunOp> inFlight, decimal capital)
    {
        /// <summary>This run's own executions that landed on one bar, in the order they landed.</summary>
        public IReadOnlyList<RunFill> At(DateTimeOffset bar) =>
            [.. fills.Where(f => f.Bar == bar)];

        public AccountReading At(int ordinal, KlineBar bar)
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

            var held = since is { } entry && ordinal >= 0 ? BarsSince(entry, bar.OpenTime) : 0;
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
        /// HOW MANY BARS THIS POSITION HAS BEEN HELD, counted in the run's own bar interval. Whole
        /// intervals between the bar the entry filled on and this one, which is the count
        /// `max_hold_bars` is stated in and the count the backtest's `ordinal - entryOrdinal` is.
        /// </summary>
        static int BarsSince(DateTimeOffset entry, DateTimeOffset bar)
        {
            var elapsed = (bar - entry).Ticks / ForwardBars.BarLength.Ticks;
            return elapsed <= 0 ? 0 : (int)Math.Min(elapsed, int.MaxValue);
        }
    }
}
