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
/// <para><b><see cref="QuantityIncrement"/> is the run's, and since `U-venue-catalog` a run that
/// declares none can be given the INSTRUMENT's.</b> There is still no increment on `dataset` or
/// `dataset_file` — those rows are counts, hashes and coverage — but a dataset now names the venue and
/// instrument its bars are of, and `Backtests.Increment` looks that pair up in `venue_instrument`
/// before it declares the model. A declared number always wins; an instrument the catalogue does not
/// hold, or has not verified, is REFUSED rather than defaulted. A quantity is rounded DOWN to whichever
/// number came out, and one that rounds to nothing is no trade with the reason said out loud rather
/// than a silent zero-size fill. `U-runner-2` still states a live intent's quantity WITHOUT one
/// applied.</para>
///
/// <para><b>Where the increment came FROM is not in <see cref="Canonical"/>.</b> The number is —
/// it decides the answer — but its provenance is recorded beside the run (`strategy_run.increment_source`)
/// and never hashed: a run's identity is the model it ran under, and folding the provenance in would
/// move every run id this installation has already written.</para>
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
    /// THE SHA-256 OF EVERY FEATURE VALUE THE RUN READ (<see cref="FeatureFeed.ValuesSha256"/>), or null for a program that
    /// declares none — which is every program written before <c>U-language-v2a</c>. Set by <see cref="Backtest.Run"/> on
    /// the request it answers with, once the run has read them.
    /// </summary>
    public string? FeaturesSha256 { get; init; }

    /// <summary>
    /// THE RUN'S IDENTITY: <c>sha256(version id + dataset id + the dataset's normalised sha256 +
    /// window + execution model)</c>, in that order, newline separated — and, only for a program that reads
    /// features, the SHA-256 of every feature value it read as a sixth line.
    ///
    /// <para>The dataset's SHA-256 is in there as well as its id, and that is not redundancy. A
    /// dataset row can be REJECTED later because a raw archive changed under it; with only the id in
    /// the hash, a run over the old bytes and a run over the new ones would collide on one id and one
    /// of the two results would silently stand for both.</para>
    ///
    /// <para><b>The feature values are in there for the same reason</b> (<c>U-language-v2a</c>): the tape is not the
    /// dataset, it grows and it is revised, and a run over one tape and a run over another are two runs. Each value is
    /// named by the feature's id, its instant and the digest of the rows it stands on, so the run id names the exact
    /// readings it was computed from — and a promotion, which hashes the run id, binds them too. A program that reads
    /// no feature hashes the five lines it always did: every v1 run id is unchanged.</para>
    /// </summary>
    public string RunIdFor(string versionId) =>
        Sha256Hex.Of(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{versionId}\n{DatasetId}\n{DatasetSha256}\n{Window}\n{Model.Canonical}")
            + (FeaturesSha256 is { } features ? "\n" + features : ""));
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
///
/// <para><b>"A bar" above is two bars for a program that declares one</b> (`bars 1h`,
/// <see cref="StrategyProgram.Bars"/>). Decisions are taken at the DECLARED bar's close and the maximum
/// hold counts declared bars; the fill of a decision, the stop, the target and the conservative ordering
/// stay on the MINUTE — the next minute's open, each minute's own range. An hourly bar whose range
/// touched both levels is not a stop if a minute inside it reached the target first and alone: the
/// minutes carry that ordering, and an hour's range would throw it away. <see cref="Run"/> has the two
/// clocks.</para>
/// </summary>
public static class Backtest
{
    /// <summary>
    /// THE MOST BARS ONE RUN EVALUATES BEFORE IT HALTS, AND SAYS SO — counted in the program's DECLARED
    /// bars, because those are what put a line in the trace.
    ///
    /// <para>Every evaluated bar puts a line in the trace, and the trace is what every figure is computed
    /// from, so a run holds one event per evaluated bar in memory while it goes. 200,000 one-minute bars
    /// is about 139 days and tens of megabytes of trace, which is a bound a low-spec laptop can carry; a
    /// full twelve months of a minute program is four windows of a quarter each, and the halt's reason
    /// says so. A program on hourly bars evaluates 8,760 of them in a year, so a year is one run: the
    /// minutes between its closes are stepped for fills and protection and put no line in the trace
    /// unless something filled on them.</para>
    ///
    /// <para>It HALTS rather than truncating, exactly as `DatasetReader`'s own cap REFUSES rather than
    /// truncating: a result quietly computed over the first part of a window is a result about a period
    /// nobody chose and cannot see the edge of. The halt is a defined fault, so the metrics carry
    /// "the run halted at bar 200000" in their own list of what is missing.</para>
    /// </summary>
    public const int MaxTracedBars = 200_000;

    /// <summary>
    /// THE DECLARATION KINDS THE BACKTEST IMPLEMENTS: every one this build parses (<see cref="StrategyDeclarations.All"/>).
    /// A program requiring a kind not on this list would be refused in words (<see cref="StrategyDeclarations.Refusal"/>),
    /// never run without it — <c>FeatureProgramBacktestTests</c> holds that every kind the parser reads is on it.
    ///
    /// <para><b><c>max_capital_fraction</c> among them</b> (<c>U-size-cap</c>): an entry here is sized by the evaluator
    /// (<c>StrategyEvaluator.Quantity</c>), which applies the cap to the cash this run's books hand it as capital, so the
    /// signal, the rounding and the cash check below all see the capped size and never the uncapped one.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> Implements = StrategyDeclarations.All;

    /// <summary>How a refusal names this reader.</summary>
    const string Reader = "the backtest";

    /// <summary>
    /// One run over CLOSED ONE-MINUTE BARS in ascending order, evaluated on the bars the program declares
    /// (<see cref="StrategyProgram.Bars"/>). Everything it answers is computed here from those bars;
    /// nothing is read from a clock, a file, the network or a random number, so the same inputs give the
    /// same trace on every machine and in a year.
    ///
    /// <para><b>Two clocks.</b> The MINUTE clock is the market's: on every minute a signal waiting from
    /// the last decision fills at that minute's open, and the stop and the target fire on that minute's
    /// range — the rules of the type's summary, applied minute by minute exactly as they always were. The
    /// DECLARED clock is the program's: only when one of its bars has closed (<see cref="BarResampler"/>)
    /// is the maximum hold taken at that bar's close and the evaluator asked, on that bar — so its
    /// indicators, lookback, history and `max_hold_bars` count declared bars, and the trace has one line
    /// per evaluated bar plus a line for every fill, exit and refusal, never one per minute. A program that
    /// declares no `bars` is the case where the two clocks are one: its declared bar IS the minute, and
    /// the loop below is the loop this build has always had, line for line in its trace.</para>
    ///
    /// <para><b>The declared bar is the program's and never the caller's.</b> There is no parameter for
    /// it: <see cref="Over"/>, the referee behind it and every test run a program on the bar it declared,
    /// because a program judged on any other bar is a different strategy wearing its id.</para>
    ///
    /// <para><b>A run over history that ends inside a declared bar closes it, as a partial bar</b>: its
    /// remaining minutes happened and the data does not have them, which is what a missing minute is. A
    /// signal from it has no open to fill at, and says so.</para>
    /// </summary>
    public static BacktestResult Run(
        StrategyProgram program,
        BacktestRequest request,
        IEnumerable<KlineBar> bars,
        EvaluationLimits? limits = null,
        CancellationToken stop = default,
        FeatureFeed? features = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(bars);

        // A PROGRAM THAT READS A FEATURE IS RUN WITH ITS VALUES OR NOT AT ALL — never as though every one were absent,
        // which would be a run of a program that decides nothing, recorded under the id of one that decides on them.
        if (program.Features.Count > 0 && features is null)
            throw new ArgumentException(
                $"this program reads {program.Features.Count} feature(s), and this run was handed no feed of their values: "
                + "a program that reads a feature is run with its values or not at all", nameof(features));
        if (program.Features.Count == 0 && features is not null)
            throw new ArgumentException("this program reads no feature, and this run was handed a feed of feature values",
                nameof(features));

        // A PROGRAM REQUIRING A DECLARATION THIS RUNNER DOES NOT IMPLEMENT IS NOT RUN WITHOUT IT. `Over` refuses it in
        // words before anything is read; a caller reaching this directly is a defect, and is told so.
        if (StrategyDeclarations.Refusal(program, Implements, Reader) is { } unimplemented)
            throw new ArgumentException(unimplemented, nameof(program));

        var model = request.Model;
        var state = EvaluationState.Start(program, limits);
        var trace = new List<BacktestEvent>();
        var trades = new List<BacktestTrade>();

        // NULL FOR A PROGRAM ON ONE-MINUTE BARS: the minute is its declared bar, handed to the evaluator
        // as it came, and the evaluator itself refuses a minute out of order or off its grid — after that
        // minute's fills and protection, exactly as it always did. A declared bar longer than a minute is
        // built here, and the resampler makes the same refusals one level down, before anything is applied.
        var resampler = program.Bars == StrategyBars.OneMinute ? null : new BarResampler(state.Grid);

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
        string? fault = null;

        // THE DECLARED BAR'S PLACE IN THE RUN, 1-based: every event of one declared bar shares it — its
        // fills, its exits and its close. `evaluated` is how many declared bars have closed and been asked.
        var ordinal = 0L;
        var evaluated = 0L;

        // THE DECLARED BAR STILL FORMING: whether the position was open at any point in it, and the last
        // minute it has had, which is the minute its maximum hold is taken on.
        var exposed = false;
        KlineBar? lastMinute = null;

        foreach (var minute in bars)
        {
            ordinal = evaluated + 1;

            if (stop.IsCancellationRequested)
            {
                Halt(ordinal, minute.OpenTime,
                    $"the run was stopped after {evaluated} bars, before the bar at {minute.OpenTime:O}");
                break;
            }

            if (resampler is not null)
            {
                // The resampler's words were spelled as trace words where it made them; carried, not spelled twice.
                if (resampler.Refusal(minute) is { } refused)
                {
                    Halt(ordinal, minute.OpenTime, $"{refused}");
                    break;
                }

                // A DECLARED BAR WHOSE LAST MINUTES THE DATA DOES NOT HAVE HAS CLOSED BY NOW — this minute
                // is past its end — and it is decided BEFORE this minute is applied: a signal from its close
                // fills at this minute's open, the first price after it.
                if (resampler.CloseBefore(minute.OpenTime) is { } passed)
                {
                    if (!Decide(passed)) break;
                    ordinal = evaluated + 1;
                }
            }

            var opens = resampler is null || !resampler.Forming;

            // A NEW DECLARED BAR OPENS ON THIS MINUTE, AND THE CAP COUNTS THE BARS EVALUATED.
            if (opens && evaluated >= MaxTracedBars)
            {
                if (resampler is null)
                    Halt(ordinal, minute.OpenTime,
                        $"this window holds more than the {MaxTracedBars} bars one run may trace " +
                        $"(about {MaxTracedBars / 1440} days of one-minute bars), so the run halted at " +
                        $"the bar before {minute.OpenTime:O}. Ask for a shorter window with --from and --to; " +
                        $"a year is four runs of a quarter each.");
                else
                    Halt(ordinal, minute.OpenTime,
                        $"this window holds more than the {MaxTracedBars} bars of {state.Grid.Spelled} one run may " +
                        $"evaluate, so the run halted at the bar before {minute.OpenTime:O}. Ask for a shorter " +
                        $"window with --from and --to.");
                break;
            }

            if (!OnMinute(minute, opens)) break;

            var closed = resampler is null ? minute : resampler.Add(minute);
            if (closed is { } bar && !Decide(bar)) break;
        }

        // THE DATA ENDED INSIDE A DECLARED BAR: it closes, as the partial bar it is. See the summary.
        if (fault is null && resampler?.Flush() is { } last) Decide(last);

        // AN INTENT THE RUN ENDED ON NEVER FILLED, and that is recorded rather than dropped: a
        // strategy whose last signal had nowhere to execute took no trade there.
        if (pending is { } waiting)
            trace.Add(BacktestEvent.NoTrade(ordinal, waiting.Bar, pendingQuantity, waiting.ReferencePrice,
                $"the run's window ended before the next bar, so this signal had no open to fill at"));

        // WHAT EACH FEATURE CAME TO, ONE LINE EACH, LAST — under the trace's hash like every other line: its id, its
        // clean-history start as of the last close read, the bars evaluated before it, the bars it had no value at and
        // the worst class of the values read. A program that reads no feature writes none, so its trace is the one it
        // always had, byte for byte.
        var summaries = features?.Summaries() ?? [];
        foreach (var summary in summaries)
            trace.Add(BacktestEvent.Feature(ordinal, summary));

        var answered = features is null ? request : request with { FeaturesSha256 = features.ValuesSha256 };
        var events = new BacktestTrace(trace);

        return new BacktestResult(
            answered.RunIdFor(program.StrategyId), program.StrategyId, answered, events, trades,
            BacktestMetrics.Of(events), state.Counters,
            fault is null ? BacktestOutcome.COMPLETED : BacktestOutcome.FAULTED, fault)
        {
            Features = summaries,
            FeaturesReadAs = features?.ReadAs
        };

        // THE MINUTE CLOCK: the signal waiting from the last decision fills, then protection fires, on this
        // minute's own prices. False when the run halted on it.
        bool OnMinute(KlineBar minute, bool opens)
        {
            try
            {
                if (opens)
                {
                    exposed = position > 0m;
                    lastMinute = null;
                }

                // 1. THE PENDING SIGNAL FILLS AT THIS MINUTE'S OPEN. It was decided at the previous
                //    declared bar's close and this is the first price after that decision.
                if (pending is { } intent)
                {
                    if (intent.Kind == IntentKind.Enter)
                    {
                        var price = model.Buy(minute.Open);
                        var fee = model.Fee(pendingQuantity, price);
                        var cost = pendingQuantity * price + fee;

                        if (cost > cash)
                        {
                            trace.Add(BacktestEvent.NoTrade(ordinal, minute.OpenTime, pendingQuantity, price,
                                $"the declared capital cannot pay for this fill: {pendingQuantity} " +
                                $"at {price} plus {fee} in fees is {cost}, and {cash} is what is left"));
                        }
                        else
                        {
                            cash -= cost;
                            position = pendingQuantity;
                            entryPrice = price;
                            entryFees = fee;
                            entryBar = minute.OpenTime;
                            entryOrdinal = ordinal;
                            stopPrice = intent.StopPrice;
                            targetPrice = intent.TargetPrice;
                            trace.Add(BacktestEvent.Fill(ordinal, minute.OpenTime, position, price, fee, cash));
                        }
                    }
                    else if (position > 0m)
                    {
                        Close(minute, ordinal, model.Sell(minute.Open), Reason(intent.Cause));
                    }

                    pending = null;
                    pendingQuantity = 0m;
                }

                // 2. PROTECTION, ON THIS MINUTE'S OWN RANGE, BEFORE THE EVALUATOR IS ASKED ANYTHING.
                if (position > 0m)
                {
                    if (stopPrice is { } level && minute.Low <= level)
                        // A MINUTE THAT OPENED THROUGH THE STOP FILLS AT THAT OPEN. A market that gapped
                        // past a resting stop does not fill you at the stop.
                        Close(minute, ordinal, model.Sell(Math.Min(level, minute.Open)), ExitReason.Stop);
                    else if (targetPrice is { } target && minute.High >= target)
                        // The target exactly, even when the minute opened above it. That open is the
                        // better price and taking it would be the flattering reading of a minute whose
                        // intrabar ordering the data does not carry.
                        Close(minute, ordinal, model.Sell(target), ExitReason.Target);
                }

                exposed |= position > 0m;
                lastMinute = minute;
                return true;
            }
            catch (OverflowException)
            {
                // A DEFINED OUTCOME, as it is inside the evaluator: the numbers a program and a
                // declared capital can reach are bounded by nothing this end controls, and a crash
                // would be the app's rather than the run's.
                Halt(ordinal, minute.OpenTime,
                    $"the arithmetic of this run overflowed the largest number this build can hold, " +
                    $"on the bar at {minute.OpenTime:O}");
                return false;
            }
        }

        // THE DECLARED CLOCK: one of the program's bars has closed. The maximum hold is taken at its close,
        // then the evaluator is asked, on that bar. False when the run halted on it.
        bool Decide(KlineBar bar)
        {
            var barOrdinal = evaluated + 1;

            try
            {
                // 3. THE MAXIMUM HOLD, AT THE CLOSE OF THE DECLARED BAR THAT REACHES IT — protection, and
                //    before the evaluator is asked, so the account it reads is already flat.
                if (position > 0m && program.MaxHoldBars is { } maximum && barOrdinal - entryOrdinal >= maximum)
                    Close(lastMinute!, barOrdinal, model.Sell(bar.Close), ExitReason.MaxHoldBars);

                // 4. THE EVENT. The account reading is what this backtest's own books say, at this
                //    bar's close, after its fills and its protection.
                var equity = cash + position * bar.Close;
                var account = new AccountReading(
                    cash, equity, position > 0m ? PositionSide.Long : PositionSide.Flat, position,
                    position > 0m ? entryPrice : 0m,
                    // NOTHING IS EVER PENDING AT AN EVENT IN A BACKTEST: an intent emitted at the
                    // previous close has already filled or been refused at the first minute after it.
                    OrderPending: false,
                    position > 0m ? (int)Math.Min(barOrdinal - entryOrdinal, int.MaxValue) : 0);

                // THE FEATURES AT THIS BAR'S CLOSE — the instant its decision is taken — as they had arrived by then.
                // A day of them more than one read serves halts the run here, in words, before anything is decided.
                IReadOnlyList<Features.FeatureValue>? values = null;
                if (features is not null)
                {
                    var read = features.At(state.Grid.EndOf(bar.OpenTime));
                    if (read.Halt is { } over)
                    {
                        Halt(barOrdinal, bar.OpenTime, $"{over}");
                        return false;
                    }
                    values = read.Values;
                }

                var missingBefore = state.MissingMinutes;
                var outcome = StrategyEvaluator.Step(state, bar, account, values);
                evaluated++;

                if (state.MissingMinutes > missingBefore)
                    trace.Add(BacktestEvent.Gap(barOrdinal, bar.OpenTime, state.MissingMinutes - missingBefore));

                trace.Add(BacktestEvent.Closed(barOrdinal, bar.OpenTime, outcome.Status.ToString(),
                    equity, position, exposed || position > 0m));

                if (outcome.Status == EvaluationStatus.Faulted)
                {
                    // Spelled as trace words where the evaluator made them; carried, not spelled twice.
                    Halt(barOrdinal, bar.OpenTime, $"{outcome.FaultReason ?? "a defined fault with no reason"}");
                    return false;
                }

                if (outcome.Intent is not { } signal) return true;

                trace.Add(BacktestEvent.Signal(barOrdinal, bar.OpenTime, signal.Kind.ToString(),
                    signal.Quantity, signal.ReferencePrice, signal.Cause.ToString()));

                if (signal.Kind == IntentKind.Exit)
                {
                    // An exit is not sized: it closes what is open, which is what the evaluator was
                    // told is open.
                    if (position > 0m) { pending = signal; pendingQuantity = position; }
                    return true;
                }

                var sized = model.RoundDown(signal.Quantity);
                if (sized <= 0m)
                {
                    trace.Add(BacktestEvent.NoTrade(barOrdinal, bar.OpenTime, signal.Quantity, signal.ReferencePrice,
                        $"the declared size came to {signal.Quantity}, which rounds down to " +
                        $"nothing at the run's quantity increment of {model.QuantityIncrement} " +
                        $"— declare a smaller increment, or more capital"));
                    return true;
                }

                pending = signal;
                pendingQuantity = sized;
                return true;
            }
            catch (OverflowException)
            {
                Halt(barOrdinal, bar.OpenTime,
                    $"the arithmetic of this run overflowed the largest number this build can hold, " +
                    $"on the bar at {bar.OpenTime:O}");
                return false;
            }
        }

        // THE RUN HALTS HERE AND NOWHERE ELSE: its words spelled as trace words (TraceText, the only thing a
        // fault line takes), kept as the run's fault and written as the trace's last line — one text, so the
        // fault the result carries and the line its hash covers cannot differ.
        void Halt(long at, DateTimeOffset when, TraceText why)
        {
            var line = BacktestEvent.Fault(at, when, why);
            fault = line.Reason;
            trace.Add(line);
        }

        // A POSITION CLOSES on the minute it closed on, stamped with that minute and the declared bar it
        // belongs to.
        void Close(KlineBar minute, long at, decimal price, ExitReason reason)
        {
            var fee = model.Fee(position, price);
            cash += position * price - fee;

            trades.Add(new BacktestTrade(
                trades.Count, entryBar, entryPrice, minute.OpenTime, price, position, reason,
                entryFees + fee, (price - entryPrice) * position));

            trace.Add(BacktestEvent.Exit(at, minute.OpenTime, position, price, entryFees + fee,
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
    /// <para><b>It is also where the HOLDOUT stops.</b> The window and the caller's audience, as ONE
    /// <see cref="Data.TapeHoldout"/> with this ledger, are handed to <see cref="Data.BarFeed.Open"/> together, so a
    /// run whose window reaches the dataset's <c>holdout_from</c> — or whose market span reaches ANOTHER dataset's
    /// holdout window, of any pair (<c>U-bar-holdout</c>) — is refused before a single bar is evaluated — and refused rather than run over
    /// the part it is allowed to see, because a metric over a window the caller did not ask for is a
    /// figure about nothing. A backtest is the reading that matters: `data-bars` hands over prices, and a
    /// run hands over what the prices did, which is the same leak laundered through a metric.</para>
    ///
    /// <para><b>The dataset's SHA-256 comes from that verified record and goes into the run's id.</b>
    /// So a rejection discovered next month can be traced to every run that fed on those bytes —
    /// <c>StrategyStore.RunsOfDataset</c> — instead of leaving results attached to a dataset id whose
    /// contents nobody can identify any more.</para>
    ///
    /// <para><b>And it runs the program on the bar the program declares.</b> The dataset serves closed
    /// minutes and <see cref="Run"/> reads <see cref="StrategyProgram.Bars"/> off the program it was
    /// given, so a `trade backtest` and the referee's holdout run — both of which come through here —
    /// judge an hourly program on hours, and no caller can hand it any other bar.</para>
    ///
    /// <para><b>A program that reads a feature is read its features from <paramref name="tape"/>, under the same
    /// <paramref name="audience"/> as its bars</b> (<c>U-language-v2a</c>; <see cref="FeatureFeed"/>), at each
    /// evaluated bar's close and no later than the run's own last one. With no tape it is REFUSED here, before a bar
    /// is read — never run as though every value were absent. A program that reads none never touches the tape.</para>
    ///
    /// <para><b>The tape is held back over every dataset's holdout window</b> (<c>U-tape-holdout</c>): the audience
    /// goes to the feed as a <see cref="Data.TapeHoldout"/> with this same <paramref name="datasets"/> ledger, and a run
    /// whose features would read the tape inside any window — from the longest reach before its first bar's close to
    /// its last bar's close — is REFUSED here in the holdout's words, flagged <see cref="BacktestOpened.IsHoldout"/>,
    /// before a bar is read; a cutoff set while it runs halts it at the next slice, and it is refused the same. Never a
    /// run on the values outside the window. The referee's audience reads every window, so a verdict is never refused
    /// here.</para>
    /// </summary>
    public static BacktestOpened Over(
        Db.DatasetStore datasets,
        long datasetId,
        StrategyProgram program,
        ExecutionModel model,
        Data.BarAudience audience,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        EvaluationLimits? limits = null,
        CancellationToken stop = default,
        Db.TapeReader? tape = null)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(model);

        if (from is { } lo && to is { } hi && lo > hi)
            return BacktestOpened.No(
                $"the window starts at {lo:O} and ends at {hi:O}, which is a window with nothing in it");

        if (StrategyDeclarations.Refusal(program, Implements, Reader) is { } unimplemented)
            return BacktestOpened.No(unimplemented);

        if (program.Features.Count > 0 && tape is null)
            return BacktestOpened.No(NoTape(program));

        // ONE HOLDOUT FOR THE BARS AND THE FEATURES (U-bar-holdout): the caller's audience with this ledger, so a run over
        // one dataset whose window reaches ANOTHER dataset's holdout window — of any pair — is refused at the open in the
        // holdout's words, exactly as its features would be over the tape.
        var holdout = Data.TapeHoldout.Of(audience, datasets);

        var open = Data.BarFeed.Open(datasets, datasetId, holdout, from, to);
        if (open.Feed is not { } feed) return BacktestOpened.No(open.Why, open.IsHoldout);

        var request = new BacktestRequest(
            feed.Dataset.Id, feed.Dataset.NormalisedSha256, model, from, to);

        // THE RUN'S FIRST AND LAST POSSIBLE CLOSES: the closes of the declared bars holding the first and the last minute
        // the window can hold of the dataset. No feature read reaches past the last — a read of instants the run cannot
        // ask about is a read of months it was never meant to see — and the tape's holdout is asked over both.
        var closes = ClosesOf(program, feed.Dataset, from, to);

        // ONE AUDIENCE FOR THE BARS AND THE FEATURES, AND THE TAPE'S HOLDOUT FOR IT (U-tape-holdout): a run whose features
        // would read the tape inside a dataset's holdout window is REFUSED in the holdout's words before a bar is read.
        using var features = program.Features.Count > 0
            ? new FeatureFeed(tape!, holdout, program, closes.Until)
            : null;
        if (features?.Refusal(closes.FirstClose) is { } withheld)
            return BacktestOpened.No(withheld, isHoldout: true);

        try
        {
            var result = Run(program, request, feed.Bars(from, to), limits, stop, features);

            // A CUTOFF SET WHILE THE RUN READ halted it before the slice reaching the new window: refused in those
            // words, never answered as a run of the program on the values before it.
            return features?.Withheld is { } late ? BacktestOpened.No(late, isHoldout: true) : BacktestOpened.Yes(result);
        }
        catch (IOException ex)
        {
            // The ledger vouched for the file a moment ago; a read that fails now is a disk problem
            // and not a result. A refusal rather than an exception, for the reason `BarFeedOpen` is one.
            return BacktestOpened.No(
                $"the normalised file of dataset {datasetId} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// THE CLOSES A RUN OF <paramref name="program"/> OVER <paramref name="set"/> FROM <paramref name="from"/> TO
    /// <paramref name="to"/> CAN EVALUATE: the declared bars holding the first and the last minute the window can hold of
    /// the dataset. What <see cref="Over"/> hands a feature feed, and what a reader of a RECORDED run asks the tape's
    /// holdout over again (<c>U-run-trace</c>) — one arithmetic, so the two cannot disagree about where a run's reads lie.
    /// </summary>
    internal static RunCloses ClosesOf(StrategyProgram program, Db.DatasetRecord set, DateTimeOffset? from, DateTimeOffset? to)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(set);

        var grid = BarGrid.For(program);
        var firstMinute = from is { } f && (set.FirstBar is not { } fb || f >= fb) ? f : set.FirstBar ?? from;
        var lastMinute = to is { } t && (set.LastBar is not { } lb || t <= lb) ? t : set.LastBar ?? to;
        return new RunCloses(grid, firstMinute, lastMinute is { } m ? grid.EndOf(grid.StartOf(m)) : null);
    }

    /// <summary>
    /// A RUN'S FIRST AND LAST POSSIBLE CLOSES (<see cref="ClosesOf"/>): <see cref="Until"/>, the close of the declared bar
    /// holding the last minute, or null for none known; and <see cref="FirstClose"/>, the close of the one holding the first
    /// — computed when it is asked, as it always was, because only a program that reads features asks it.
    /// </summary>
    internal readonly record struct RunCloses(BarGrid Grid, DateTimeOffset? FirstMinute, DateTimeOffset? Until)
    {
        public DateTimeOffset FirstClose => FirstMinute is { } first ? Grid.EndOf(Grid.StartOf(first)) : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// WHY A PROGRAM THAT READS FEATURES CANNOT BE RUN WHERE NO TAPE IS OPEN, in words: the one refusal every caller
    /// makes before it charges anything — the trial budget at <c>backtest</c>, the verdict budget at the referee.
    /// </summary>
    public static string NoTape(StrategyProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return $"this program reads {program.Features.Count} feature(s) — "
               + string.Join(", ", program.Features.Select(f => $"`{f.Name}`"))
               + " — and this installation has no market-context tape open to read them from, so it cannot be run: a "
               + "program that reads a feature is run with its values as they had arrived, or not at all, never as though "
               + "every value were absent. TradeAgent opens the tape itself when it starts; the account owner's activity "
               + "log says why it is not open.";
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

    /// <summary>
    /// WHAT EACH DECLARED FEATURE CAME TO OVER THE RUN, in the program's order — the same facts as the trace's
    /// <c>Feature</c> lines — or empty for a program that reads none.
    /// </summary>
    public IReadOnlyList<FeatureRunSummary> Features { get; init; } = [];

    /// <summary>
    /// WHO READ THE FEATURES, in the words a refusal uses — the run's own bar audience — or null for a program that
    /// reads none. A string, never the audience itself: no public member hands one out.
    /// </summary>
    public string? FeaturesReadAs { get; init; }
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

    /// <summary>
    /// Whether the refusal is the HOLDOUT's, carried up from <see cref="BarFeedOpen.IsHoldout"/> so the
    /// pipe can answer HOLDOUT_WITHHELD rather than MARKET_DATA_UNAVAILABLE. "This installation does not
    /// have those bars" and "you may not see those bars" have different repairs, and an agent told the
    /// first would ask the owner to collect data it already has.
    /// </summary>
    public bool IsHoldout { get; private init; }

    internal static BacktestOpened Yes(BacktestResult result) => new(result, null);

    internal static BacktestOpened No(string reason, bool isHoldout = false) =>
        new(null, reason) { IsHoldout = isHoldout };
}
