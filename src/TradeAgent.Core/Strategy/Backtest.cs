using TradeAgent.Core.Data;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// THE EXECUTION MODEL, DECLARED PER RUN AND RECORDED WITH IT.
///
/// <para>`docs/COUNCIL.md`: "a backtest declares fees, slippage, gaps and conservative
/// stop-versus-target ordering". Declared, not assumed — every one of these four numbers changes the
/// answer, none of them is in the program and none of them is in the dataset, so a result reported
/// without them is a number nobody can reproduce or argue with. They are hashed into the run's id
/// (<see cref="BacktestRequest.RunIdFor"/>), which is what makes a run under one fee and a run under
/// another two different runs rather than one run that changed its mind.</para>
///
/// <para><b><see cref="FeeRate"/> and <see cref="SlippageRate"/> are FRACTIONS, not amounts.</b> A
/// price-unit slippage of 0.50 is a rounding error on Bitcoin and a quarter of the instrument on a
/// two-dollar stock, so one number in price units cannot be declared once and mean anything twice.
/// Slippage is adverse in both directions — a buy pays <c>open * (1 + s)</c>, a sell receives
/// <c>open * (1 - s)</c> — and the fee is charged on every fill, entry and exit alike.</para>
///
/// <para><b><see cref="QuantityIncrement"/> is the run's, because the dataset has none.</b> There is
/// no instrument increment anywhere in `dataset` or `dataset_file` (`DatasetStore`'s rows are counts,
/// hashes and coverage), and `U-runner-2` therefore states an intent's quantity WITHOUT one applied.
/// So the run declares it, a quantity is rounded DOWN to it, and a quantity that rounds to nothing is
/// no trade with the reason said out loud rather than a silent zero-size fill.</para>
/// </summary>
public sealed record ExecutionModel
{
    ExecutionModel(decimal feeRate, decimal slippageRate, decimal quantityIncrement, decimal initialCapital)
    {
        FeeRate = feeRate;
        SlippageRate = slippageRate;
        QuantityIncrement = quantityIncrement;
        InitialCapital = initialCapital;
    }

    /// <summary>
    /// The most one fill may be declared to cost, as a fraction of its notional.
    ///
    /// <para>5% is an order of magnitude above any venue this product could reach, so a number above
    /// it is far more likely to be a basis-point count written as a percentage — <c>0.1</c> meant as
    /// a tenth of a percent — and a run charged a hundred times its real fee is a run whose loss is
    /// the declaration's rather than the strategy's. It is REFUSED with that reading named.</para>
    /// </summary>
    public const decimal MaxFeeRate = 0.05m;

    /// <summary>The most slippage may be declared to be, as a fraction of the price. See <see cref="MaxFeeRate"/>.</summary>
    public const decimal MaxSlippageRate = 0.05m;

    /// <summary>The most capital a run may be declared to start with. A billion is not a laptop's account.</summary>
    public const decimal MaxInitialCapital = 1_000_000_000m;

    /// <summary>
    /// WHAT A RUN THAT DECLARED NOTHING RUNS UNDER: no fee, no slippage, whole units, ten thousand.
    ///
    /// <para>It is frictionless ON PURPOSE and it says so wherever it is reported. The alternative —
    /// defaulting to some venue's real taker fee — would put a number nobody measured into a result
    /// and let it read as a measurement. Zero is the one value that cannot be mistaken for one: a run
    /// under this model is an upper bound on a frictionless market and never a forecast, and
    /// <see cref="Canonical"/> is in the answer and in the run's own id so nobody has to guess which
    /// it was.</para>
    ///
    /// <para>The increment is 1 for the same reason. A whole unit is visibly arbitrary — on a pair
    /// priced in tens of thousands it makes a fractional size round to nothing, which comes back as
    /// "no trade" naming the increment rather than as a quietly missing trade.</para>
    /// </summary>
    public static readonly ExecutionModel Frictionless = new(0m, 0m, 1m, 10_000m);

    /// <summary>Charged on every fill, as a fraction of that fill's notional.</summary>
    public decimal FeeRate { get; }

    /// <summary>Adverse on every fill, as a fraction of the price it would otherwise have got.</summary>
    public decimal SlippageRate { get; }

    /// <summary>A quantity is rounded DOWN to a multiple of this. Never up: up is a size nobody asked for.</summary>
    public decimal QuantityIncrement { get; }

    /// <summary>What the account starts with, and the only capital this run can spend.</summary>
    public decimal InitialCapital { get; }

    /// <summary>Whether this model declares any friction at all. Reported, never inferred by a reader.</summary>
    public bool Frictionful => FeeRate > 0m || SlippageRate > 0m;

    /// <summary>
    /// THE MODEL AS IT IS HASHED AND RECORDED. One line, fixed field order, trailing zeros gone
    /// (<see cref="StrategyParser.Number"/>), so <c>0.0010</c> and <c>0.001</c> are one declaration
    /// and not two runs of the same thing.
    /// </summary>
    public string Canonical =>
        $"fees={StrategyParser.Number(FeeRate)};slippage={StrategyParser.Number(SlippageRate)};" +
        $"increment={StrategyParser.Number(QuantityIncrement)};capital={StrategyParser.Number(InitialCapital)}";

    /// <summary>
    /// Declares a model, or refuses and says why. A VALUE rather than an exception, because these four
    /// numbers arrive from an agent's request and a refusal is something it can read and correct.
    /// </summary>
    public static ExecutionModelDeclared Declare(
        decimal? fees = null, decimal? slippage = null, decimal? increment = null, decimal? capital = null)
    {
        var fee = fees ?? Frictionless.FeeRate;
        var slip = slippage ?? Frictionless.SlippageRate;
        var step = increment ?? Frictionless.QuantityIncrement;
        var cash = capital ?? Frictionless.InitialCapital;

        if (fee < 0m)
            return ExecutionModelDeclared.No($"a fee cannot be negative, and this one is {StrategyParser.Number(fee)}");
        if (fee > MaxFeeRate)
            return ExecutionModelDeclared.No(
                $"a fee is a FRACTION of each fill's notional and at most {StrategyParser.Number(MaxFeeRate)} " +
                $"({StrategyParser.Number(MaxFeeRate * 100m)}% of it); {StrategyParser.Number(fee)} is more than " +
                "any venue charges and reads as a percentage written where a fraction belongs — 0.001 is ten " +
                "basis points, 0.1 is ten per cent");
        if (slip < 0m)
            return ExecutionModelDeclared.No(
                $"slippage cannot be negative — it is adverse by definition — and this one is {StrategyParser.Number(slip)}");
        if (slip > MaxSlippageRate)
            return ExecutionModelDeclared.No(
                $"slippage is a FRACTION of the price and at most {StrategyParser.Number(MaxSlippageRate)}; " +
                $"{StrategyParser.Number(slip)} is a different market, not a cost");
        if (step <= 0m)
            return ExecutionModelDeclared.No(
                $"a quantity increment must be above 0, and this one is {StrategyParser.Number(step)}: every " +
                "size would round to nothing and the run would take no trade at all");
        if (cash <= 0m)
            return ExecutionModelDeclared.No(
                $"initial capital must be above 0, and this one is {StrategyParser.Number(cash)}: an account " +
                "with nothing in it can buy nothing, and every entry would be refused for want of capital");
        if (cash > MaxInitialCapital)
            return ExecutionModelDeclared.No(
                $"initial capital is at most {StrategyParser.Number(MaxInitialCapital)}, and this one is " +
                $"{StrategyParser.Number(cash)} — more than this product is for");

        return ExecutionModelDeclared.Yes(new ExecutionModel(fee, slip, step, cash));
    }

    /// <summary>
    /// A quantity rounded DOWN to a multiple of <see cref="QuantityIncrement"/>. Zero is a real answer
    /// and is the caller's to report, not this method's to round away from.
    /// </summary>
    public decimal RoundDown(decimal quantity)
    {
        if (quantity <= 0m) return 0m;
        return decimal.Truncate(quantity / QuantityIncrement) * QuantityIncrement;
    }

    /// <summary>What a buy actually pays for one unit at <paramref name="price"/>.</summary>
    public decimal Buy(decimal price) => price * (1m + SlippageRate);

    /// <summary>What a sell actually receives for one unit at <paramref name="price"/>.</summary>
    public decimal Sell(decimal price) => price * (1m - SlippageRate);

    /// <summary>The fee on a fill of <paramref name="quantity"/> at <paramref name="price"/>.</summary>
    public decimal Fee(decimal quantity, decimal price) => quantity * price * FeeRate;
}

/// <summary>
/// A DECLARED EXECUTION MODEL, OR WHY THERE IS NONE. The shape <see cref="StrategyParse"/> and
/// <see cref="BarFeedOpen"/> already have: one of the two, never both, never an exception.
/// </summary>
public sealed record ExecutionModelDeclared
{
    ExecutionModelDeclared(ExecutionModel? model, string? refusal)
    {
        Model = model;
        Refusal = refusal;
    }

    /// <summary>The model, or null when the declaration was refused.</summary>
    public ExecutionModel? Model { get; }

    /// <summary>Why there is none, or null when there is one.</summary>
    public string? Refusal { get; }

    public bool Ok => Model is not null;

    /// <summary>The refusal's text, or the empty string when a model came out.</summary>
    public string Why => Refusal ?? "";

    internal static ExecutionModelDeclared Yes(ExecutionModel model) => new(model, null);

    internal static ExecutionModelDeclared No(string reason) => new(null, reason);
}

/// <summary>Why a position ended. The first three are the program's; the last two are protection's.</summary>
public enum ExitReason
{
    /// <summary>A declared `exit when` rule fired.</summary>
    Rule,

    /// <summary>The declared `session_exit` wall clock was reached.</summary>
    SessionExit,

    /// <summary>The declared `max_hold_bars` was reached. Protection, not a rule — see <see cref="Backtest"/>.</summary>
    MaxHoldBars,

    /// <summary>The declared stop was touched. Wins over <see cref="Target"/> on a bar that touched both.</summary>
    Stop,

    /// <summary>The declared target was touched.</summary>
    Target
}

/// <summary>
/// ONE CLOSED TRADE, as the backtest's own bookkeeping made it.
///
/// <para><see cref="Pnl"/> is GROSS — <c>(exit - entry) * quantity</c> — and <see cref="Fees"/> is
/// what both of its fills cost. The two are separate because a strategy that is profitable before
/// costs and unprofitable after them is a different thing from one that is unprofitable either way,
/// and a single net figure cannot tell a reader which they have.</para>
/// </summary>
public sealed record BacktestTrade(
    int Ordinal,
    DateTimeOffset EntryBar,
    decimal EntryPrice,
    DateTimeOffset ExitBar,
    decimal ExitPrice,
    decimal Quantity,
    ExitReason Reason,
    decimal Fees,
    decimal Pnl)
{
    /// <summary>What this trade made after the fees of both its fills.</summary>
    public decimal Net => Pnl - Fees;
}

/// <summary>Whether a run reached the end of its window.</summary>
public enum BacktestOutcome
{
    /// <summary>Every bar in the window was evaluated.</summary>
    COMPLETED,

    /// <summary>A defined fault halted it. The figures cover the bars before the halt and say so.</summary>
    FAULTED
}

/// <summary>
/// WHAT A RUN IS A RUN OF: one dataset's bytes, one window, one declared execution model.
///
/// <para>Everything here is in the run's id, and nothing else is. The interpreter build is not — the
/// app's version moves with every build without changing what a program means, and everything that
/// does change its meaning is already inside the version id via `StrategyVersions.Manifest`. No clock
/// is either, which is the whole of "deterministic": the same request over the same bytes is the same
/// run id on Monday and in a year.</para>
/// </summary>
public sealed record BacktestRequest(
    long DatasetId,
    string DatasetSha256,
    ExecutionModel Model,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null)
{
    /// <summary>The window as it is hashed: two ISO instants, or <c>all</c> for a side that is open.</summary>
    public string Window =>
        $"{(From is { } f ? f.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : "all")}" +
        $"..{(To is { } t ? t.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : "all")}";

    /// <summary>
    /// THE RUN'S IDENTITY: <c>sha256(version id + dataset id + the dataset's normalised sha256 +
    /// window + execution model)</c>, in that order, newline separated.
    ///
    /// <para>The dataset's SHA-256 is in there as well as its id, and that is not redundancy. A
    /// dataset row can be REJECTED later because a raw archive changed under it; with only the id in
    /// the hash, a run over the old bytes and a run over the new ones would collide on one id and one
    /// of the two results would silently stand for both.</para>
    /// </summary>
    public string RunIdFor(string versionId) =>
        Sha256Hex.Of($"{versionId}\n{DatasetId}\n{DatasetSha256}\n{Window}\n{Model.Canonical}");
}

/// <summary>
/// THE BACKTEST: A FROZEN PROGRAM, CLOSED BARS AND A DECLARED EXECUTION MODEL IN; A TRACE, ITS TRADES
/// AND THE METRICS COMPUTED FROM IT OUT. NO ORDER, NO AUTHORITY, NO CLOCK.
///
/// <para><b>It places nothing and it grants nothing.</b> `CLAUDE.md`: a backtest places no order and
/// grants no authority. Nothing in this file touches a connector, a broker, an order, a mode or the
/// kill switch; the only thing it writes is a measurement, and even that is the store's job and not
/// this one's.</para>
///
/// <para><b>Signals fill at the next bar's open. Protection fills where protection fires.</b> That is
/// one rule with two halves and both are needed. A signal is computed from a bar's CLOSE, so the
/// earliest thing it can be acted on is the next bar's open — filling at the close that produced it
/// would be reading a price the decision had not been made at yet, and it is the single most common
/// way a backtest flatters itself. A stop or a target is NOT a decision taken at a close: it is an
/// order already resting at the venue, and it fires the moment the price is touched. Modelling
/// protection as a next-open decision would hold a position through the whole bar that broke it.</para>
///
/// <para><b>Conservative ordering, stated rather than assumed.</b> A bar whose low touched the stop
/// and whose high touched the target counts as the STOP. Bars carry no intrabar ordering
/// (`docs/COUNCIL.md`, "Data": they "establish no actual fill, queue position or intrabar ordering"),
/// so which came first is unknowable from the data, and the only honest choice is the one that cannot
/// invent a winning trade out of a bar that may have been a losing one. A bar that OPENED through the
/// stop fills at that open and not at the stop, for the same reason: a market that gapped past a stop
/// does not fill you at it.</para>
///
/// <para><b>Protection is the backtest's bookkeeping and the evaluator emits nothing for it.</b> The
/// stop, the target and the maximum holding time are checked here, on every bar from the one the
/// entry filled on — the fill was at that bar's open, so the rest of its range happened after it —
/// and they are checked BEFORE the evaluator is asked anything on that bar. So when protection has
/// closed the position, the account the evaluator reads is flat and its own `max_hold_bars` branch
/// cannot fire a second exit. That ordering is what makes the maximum holding time a promise: if the
/// bar that reaches the limit is one the evaluator cannot evaluate — warming up, a value undefined,
/// an interpreter fault — the position still closes.</para>
/// </summary>
public static class Backtest
{
    /// <summary>
    /// One run over bars in ascending order. Everything it answers is computed here from those bars;
    /// nothing is read from a clock, a file, the network or a random number, so the same inputs give
    /// the same trace on every machine and in a year.
    /// </summary>
    public static BacktestResult Run(
        StrategyProgram program,
        BacktestRequest request,
        IEnumerable<KlineBar> bars,
        EvaluationLimits? limits = null,
        TimeSpan? barInterval = null,
        CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(bars);

        var model = request.Model;
        var state = EvaluationState.Start(program, limits, barInterval);
        var trace = new List<BacktestEvent>();
        var trades = new List<BacktestTrade>();

        var cash = model.InitialCapital;
        var position = 0m;
        var entryPrice = 0m;
        var entryFees = 0m;
        var entryBar = default(DateTimeOffset);
        var entryOrdinal = 0L;
        decimal? stopPrice = null;
        decimal? targetPrice = null;

        StrategyIntent? pending = null;
        var pendingQuantity = 0m;
        var ordinal = 0L;
        string? fault = null;

        foreach (var bar in bars)
        {
            ordinal++;

            if (stop.IsCancellationRequested)
            {
                fault = $"the run was stopped after {ordinal - 1} bars, before the bar at {bar.OpenTime:O}";
                trace.Add(BacktestEvent.Fault(ordinal, bar.OpenTime, fault));
                break;
            }

            try
            {
                var exposedAtOpen = position > 0m;

                // 1. THE PENDING SIGNAL FILLS AT THIS BAR'S OPEN. It was decided at the previous
                //    bar's close and this is the first price after that decision.
                if (pending is { } intent)
                {
                    if (intent.Kind == IntentKind.Enter)
                    {
                        var price = model.Buy(bar.Open);
                        var fee = model.Fee(pendingQuantity, price);
                        var cost = pendingQuantity * price + fee;

                        if (cost > cash)
                        {
                            trace.Add(BacktestEvent.NoTrade(ordinal, bar.OpenTime, pendingQuantity, price,
                                $"the declared capital cannot pay for this fill: {StrategyParser.Number(pendingQuantity)} " +
                                $"at {StrategyParser.Number(price)} plus {StrategyParser.Number(fee)} in fees is " +
                                $"{StrategyParser.Number(cost)}, and {StrategyParser.Number(cash)} is what is left"));
                        }
                        else
                        {
                            cash -= cost;
                            position = pendingQuantity;
                            entryPrice = price;
                            entryFees = fee;
                            entryBar = bar.OpenTime;
                            entryOrdinal = ordinal;
                            stopPrice = intent.StopPrice;
                            targetPrice = intent.TargetPrice;
                            trace.Add(BacktestEvent.Fill(ordinal, bar.OpenTime, position, price, fee, cash));
                        }
                    }
                    else if (position > 0m)
                    {
                        Close(bar, model.Sell(bar.Open), Reason(intent.Cause));
                    }

                    pending = null;
                    pendingQuantity = 0m;
                }

                // 2. PROTECTION, ON THIS BAR'S OWN RANGE, BEFORE THE EVALUATOR IS ASKED ANYTHING.
                if (position > 0m)
                {
                    var held = ordinal - entryOrdinal;

                    if (stopPrice is { } level && bar.Low <= level)
                        // A BAR THAT OPENED THROUGH THE STOP FILLS AT THAT OPEN. A market that gapped
                        // past a resting stop does not fill you at the stop.
                        Close(bar, model.Sell(Math.Min(level, bar.Open)), ExitReason.Stop);
                    else if (targetPrice is { } target && bar.High >= target)
                        // The target exactly, even when the bar opened above it. That open is the
                        // better price and taking it would be the flattering reading of a bar whose
                        // intrabar ordering the data does not carry.
                        Close(bar, model.Sell(target), ExitReason.Target);
                    else if (program.MaxHoldBars is { } maximum && held >= maximum)
                        Close(bar, model.Sell(bar.Close), ExitReason.MaxHoldBars);
                }

                // 3. THE EVENT. The account reading is what this backtest's own books say, at this
                //    bar's close, after its fills and its protection.
                var equity = cash + position * bar.Close;
                var account = new AccountReading(
                    cash, equity, position > 0m ? PositionSide.Long : PositionSide.Flat, position,
                    position > 0m ? entryPrice : 0m,
                    // NOTHING IS EVER PENDING AT AN EVENT IN A BACKTEST: an intent emitted at the
                    // previous close has already filled or been refused at this bar's open.
                    OrderPending: false,
                    position > 0m ? (int)Math.Min(ordinal - entryOrdinal, int.MaxValue) : 0);

                var missingBefore = state.MissingMinutes;
                var outcome = StrategyEvaluator.Step(state, bar, account);
                if (state.MissingMinutes > missingBefore)
                    trace.Add(BacktestEvent.Gap(ordinal, bar.OpenTime, state.MissingMinutes - missingBefore));

                trace.Add(BacktestEvent.Closed(ordinal, bar.OpenTime, outcome.Status.ToString(),
                    equity, position, exposedAtOpen || position > 0m));

                if (outcome.Status == EvaluationStatus.Faulted)
                {
                    fault = outcome.FaultReason;
                    trace.Add(BacktestEvent.Fault(ordinal, bar.OpenTime, fault ?? "a defined fault with no reason"));
                    break;
                }

                if (outcome.Intent is not { } signal) continue;

                trace.Add(BacktestEvent.Signal(ordinal, bar.OpenTime, signal.Kind.ToString(),
                    signal.Quantity, signal.ReferencePrice, signal.Cause.ToString()));

                if (signal.Kind == IntentKind.Exit)
                {
                    // An exit is not sized: it closes what is open, which is what the evaluator was
                    // told is open.
                    if (position > 0m) { pending = signal; pendingQuantity = position; }
                    continue;
                }

                var sized = model.RoundDown(signal.Quantity);
                if (sized <= 0m)
                {
                    trace.Add(BacktestEvent.NoTrade(ordinal, bar.OpenTime, signal.Quantity, signal.ReferencePrice,
                        $"the declared size came to {StrategyParser.Number(signal.Quantity)}, which rounds down to " +
                        $"nothing at the run's quantity increment of {StrategyParser.Number(model.QuantityIncrement)} " +
                        "— declare a smaller increment, or more capital"));
                    continue;
                }

                pending = signal;
                pendingQuantity = sized;
            }
            catch (OverflowException)
            {
                // A DEFINED OUTCOME, as it is inside the evaluator: the numbers a program and a
                // declared capital can reach are bounded by nothing this end controls, and a crash
                // would be the app's rather than the run's.
                fault = $"the arithmetic of this run overflowed the largest number this build can hold, " +
                        $"on the bar at {bar.OpenTime:O}";
                trace.Add(BacktestEvent.Fault(ordinal, bar.OpenTime, fault));
                break;
            }
        }

        // AN INTENT THE RUN ENDED ON NEVER FILLED, and that is recorded rather than dropped: a
        // strategy whose last signal had nowhere to execute took no trade there.
        if (pending is { } last)
            trace.Add(BacktestEvent.NoTrade(ordinal, last.Bar, pendingQuantity, last.ReferencePrice,
                "the run's window ended before the next bar, so this signal had no open to fill at"));

        var events = new BacktestTrace(trace);

        return new BacktestResult(
            request.RunIdFor(program.StrategyId), program.StrategyId, request, events, trades,
            BacktestMetrics.Of(events), state.Counters,
            fault is null ? BacktestOutcome.COMPLETED : BacktestOutcome.FAULTED, fault);

        void Close(KlineBar bar, decimal price, ExitReason reason)
        {
            var fee = model.Fee(position, price);
            cash += position * price - fee;

            trades.Add(new BacktestTrade(
                trades.Count, entryBar, entryPrice, bar.OpenTime, price, position, reason,
                entryFees + fee, (price - entryPrice) * position));

            trace.Add(BacktestEvent.Exit(ordinal, bar.OpenTime, position, price, entryFees + fee,
                (price - entryPrice) * position, cash, reason.ToString()));

            position = 0m;
            entryPrice = 0m;
            entryFees = 0m;
            stopPrice = null;
            targetPrice = null;
        }
    }

    /// <summary>
    /// ONE RUN OVER ONE DATASET BY ITS LEDGER ID — or a REFUSAL that says why there is none.
    ///
    /// <para><b>This is the only way in that can refuse, and it is where a REJECTED dataset stops.</b>
    /// <see cref="Data.BarFeed.Open"/> takes the ledger's verdict ONCE, at the start of the run, by
    /// re-hashing every raw archive file and the normalised file; a dataset whose bytes have changed
    /// under it serves nothing and the refusal names it. A run whose dataset changed state half way
    /// through would have no one dataset its result was about.</para>
    ///
    /// <para><b>The dataset's SHA-256 comes from that verified record and goes into the run's id.</b>
    /// So a rejection discovered next month can be traced to every run that fed on those bytes —
    /// <c>StrategyStore.RunsOfDataset</c> — instead of leaving results attached to a dataset id whose
    /// contents nobody can identify any more.</para>
    /// </summary>
    public static BacktestOpened Over(
        Db.DatasetStore datasets,
        long datasetId,
        StrategyProgram program,
        ExecutionModel model,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        EvaluationLimits? limits = null,
        CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(model);

        if (from is { } lo && to is { } hi && lo > hi)
            return BacktestOpened.No(
                $"the window starts at {lo:O} and ends at {hi:O}, which is a window with nothing in it");

        var open = Data.BarFeed.Open(datasets, datasetId);
        if (open.Feed is not { } feed) return BacktestOpened.No(open.Why);

        var request = new BacktestRequest(
            feed.Dataset.Id, feed.Dataset.NormalisedSha256, model, from, to);

        try
        {
            return BacktestOpened.Yes(Run(program, request, feed.Bars(from, to), limits, null, stop));
        }
        catch (IOException ex)
        {
            // The ledger vouched for the file a moment ago; a read that fails now is a disk problem
            // and not a result. A refusal rather than an exception, for the reason `BarFeedOpen` is one.
            return BacktestOpened.No(
                $"the normalised file of dataset {datasetId} could not be read: {ex.Message}");
        }
    }

    static ExitReason Reason(IntentCause cause) => cause switch
    {
        IntentCause.SessionExit => ExitReason.SessionExit,
        IntentCause.MaxHoldBars => ExitReason.MaxHoldBars,
        _ => ExitReason.Rule
    };
}

/// <summary>
/// EVERYTHING ONE RUN PRODUCED: what it was a run of, what happened bar by bar, the trades that came
/// out of it, and the metrics computed from the trace.
///
/// <para><see cref="Metrics"/> is NOT a second account of the run. It is computed from
/// <see cref="Trace"/> and from nothing else (<see cref="BacktestMetrics.Of"/>), which is what
/// `docs/COUNCIL.md` means by "metrics computed by the app from its own trace": a figure that came from
/// anywhere else — the program's text, a counter a caller kept, a number an agent reported — is a claim
/// wearing a measurement's clothes.</para>
/// </summary>
public sealed record BacktestResult(
    string RunId,
    string VersionId,
    BacktestRequest Request,
    BacktestTrace Trace,
    IReadOnlyList<BacktestTrade> Trades,
    BacktestMetrics Metrics,
    EvaluationCounters Counters,
    BacktestOutcome Outcome,
    string? FaultReason)
{
    public bool Faulted => Outcome == BacktestOutcome.FAULTED;
}

/// <summary>
/// A RUN, OR WHY THERE IS NONE. The shape <see cref="StrategyParse"/>, <see cref="BarFeedOpen"/> and
/// <see cref="ExecutionModelDeclared"/> already have, for the same reason: a caller that forgot to
/// check gets a null result rather than a figure about bars nobody will vouch for.
/// </summary>
public sealed record BacktestOpened
{
    BacktestOpened(BacktestResult? result, string? refusal)
    {
        Result = result;
        Refusal = refusal;
    }

    /// <summary>The run, or null when it was refused.</summary>
    public BacktestResult? Result { get; }

    /// <summary>Why there is none, or null when there is one.</summary>
    public string? Refusal { get; }

    public bool Ok => Result is not null;

    /// <summary>The refusal's text, or the empty string when a run came out.</summary>
    public string Why => Refusal ?? "";

    internal static BacktestOpened Yes(BacktestResult result) => new(result, null);

    internal static BacktestOpened No(string reason) => new(null, reason);
}
