namespace TradeAgent.Core.Strategy;

/// <summary>
/// A FIGURE THAT IS NOT THERE, AND WHY. The shape <c>ReportGap</c> already has, for the same reason:
/// a dash nobody can account for is worse than a number, because a reader supplies their own
/// explanation for it and is usually wrong.
/// </summary>
public sealed record MetricGap(string Field, string Why);

/// <summary>
/// WHAT A RUN MEASURED — COMPUTED FROM THE TRACE AND FROM NOTHING ELSE.
///
/// <para>`docs/COUNCIL.md`, "The runner and the referee": "metrics computed by the app from its own
/// trace". <see cref="Of"/> takes the trace and takes NOTHING else — not the program, not the
/// execution model, not a counter the runner kept as it went. That is not tidiness: a metric read off
/// the program's text (an exposure count taken from its warm-up, a trade count taken from its rule
/// count) would look right on every fixture whose numbers happen to agree and would be a claim about
/// the program rather than a measurement of what happened. The one input is the structural guarantee,
/// and the mutant this unit watched go red is exactly that defect.</para>
///
/// <para><b>Every unknown is a labelled dash.</b> A null here is an UNKNOWN and never a zero, and
/// <see cref="Missing"/> says which figure and why in words — the rule the owner's daily report already
/// keeps. The sharpest case is <see cref="WinRate"/>: with no closed trade, 0% is a fact about nothing
/// and would read as a strategy that lost every time.</para>
///
/// <para><b>The drawdown is on EQUITY, including the open trade.</b> Peak to trough over the account
/// as it stood at every bar's close — cash plus the open position marked at that close. A drawdown
/// computed from closed trades only is 0 for a run whose one position is still 40% under water, which
/// is the most flattering possible answer and the one this unit was RED on.</para>
/// </summary>
public sealed record BacktestMetrics
{
    /// <summary>What an unknown figure prints as. Never 0, never an empty string.</summary>
    public const string Dash = "—";

    /// <summary>Closed bars the run evaluated. The denominator of everything else.</summary>
    public long Bars { get; init; }

    /// <summary>Bars on which the position was open at any point, the bars it opened and closed on included.</summary>
    public long ExposureBars { get; init; }

    /// <summary>Intents the evaluator emitted.</summary>
    public long Signals { get; init; }

    /// <summary>Entries that actually filled.</summary>
    public int Fills { get; init; }

    /// <summary>Signals that could not be acted on at all. <see cref="Missing"/> says why.</summary>
    public int NoTrades { get; init; }

    /// <summary>Positions that were closed. A position still open at the last bar is not one of these.</summary>
    public int Trades { get; init; }

    /// <summary>Closed trades whose gross result was above zero.</summary>
    public int Wins { get; init; }

    /// <summary>Wins over trades, 0 to 1 — null when nothing closed. See the type's summary.</summary>
    public decimal? WinRate { get; init; }

    /// <summary>The closed trades' result before their fees.</summary>
    public decimal? GrossPnl { get; init; }

    /// <summary>What the closed trades' fills cost, both legs of each.</summary>
    public decimal? Fees { get; init; }

    /// <summary>The closed trades' result after those fees. The only figure that is a result.</summary>
    public decimal? NetPnl { get; init; }

    /// <summary>The worst peak-to-trough fall of the account's equity, in the quote currency.</summary>
    public decimal? MaxDrawdown { get; init; }

    /// <summary>The account at the last bar's close, the open position marked at it.</summary>
    public decimal? FinalEquity { get; init; }

    /// <summary>Minutes inside the window with no bar. Nothing was filled in for them.</summary>
    public long MissingMinutes { get; init; }

    /// <summary>Runs of consecutive missing minutes. One number cannot say whether 60 is an hour or sixty days.</summary>
    public int Gaps { get; init; }

    /// <summary>Defined faults. At most one, because the first one halts the run.</summary>
    public int Faults { get; init; }

    /// <summary>Whether a position was still open when the last bar closed.</summary>
    public bool PositionOpenAtEnd { get; init; }

    /// <summary>Every figure that is not here, and why. See the type's summary.</summary>
    public IReadOnlyList<MetricGap> Missing { get; init; } = [];

    /// <summary>A figure as it is shown: the number, or the dash. There is no third rendering.</summary>
    public static string Show(decimal? value) =>
        value is { } d ? StrategyParser.Number(d) : Dash;

    /// <summary>
    /// THE METRICS OF ONE TRACE. One pass, one input.
    ///
    /// <para>Deliberately not given the program, the model or the runner's own counters. See the
    /// type's summary for why that is the whole point of the signature.</para>
    /// </summary>
    public static BacktestMetrics Of(BacktestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        long bars = 0, exposure = 0, signals = 0, missingMinutes = 0;
        var fills = 0;
        var noTrades = 0;
        var gaps = 0;
        var faults = 0;
        var trades = 0;
        var wins = 0;
        var gross = 0m;
        var fees = 0m;
        decimal? peak = null;
        var drawdown = 0m;
        decimal? finalEquity = null;
        var openAtEnd = false;
        var reasons = new List<string>();
        string? halted = null;
        long haltedAt = 0;

        foreach (var e in trace.Events)
            switch (e.Kind)
            {
                case BacktestEventKind.Bar:
                    bars++;
                    if (e.Exposed) exposure++;
                    if (e.Equity is { } equity)
                    {
                        // PEAK TO TROUGH ON THE ACCOUNT, the open position marked at this close
                        // included. The peak starts at the first bar's equity, which is the declared
                        // capital before anything happened.
                        peak = peak is { } high && high > equity ? high : equity;
                        if (peak - equity > drawdown) drawdown = peak.Value - equity;
                        finalEquity = equity;
                    }
                    openAtEnd = e.Position > 0m;
                    break;

                case BacktestEventKind.Gap:
                    gaps++;
                    missingMinutes += e.Minutes ?? 0;
                    break;

                case BacktestEventKind.Signal:
                    signals++;
                    break;

                case BacktestEventKind.Fill:
                    fills++;
                    break;

                case BacktestEventKind.Exit:
                    trades++;
                    if (e.Pnl > 0m) wins++;
                    gross += e.Pnl ?? 0m;
                    fees += e.Fee ?? 0m;
                    break;

                case BacktestEventKind.NoTrade:
                    noTrades++;
                    if (e.Reason is { Length: > 0 } why && !reasons.Contains(why)) reasons.Add(why);
                    break;

                default:
                    faults++;
                    halted = e.Reason;
                    haltedAt = e.Ordinal;
                    break;
            }

        var missing = new List<MetricGap>();

        if (bars == 0)
        {
            missing.Add(new MetricGap("every figure",
                "no closed bar of this dataset fell inside the window, so nothing was measured at all"));

            return new BacktestMetrics { Missing = missing };
        }

        if (trades == 0)
            missing.Add(new MetricGap("win rate",
                "no position was closed in this run, and a win rate over no trade is not 0% — it is "
                + "nothing at all"));

        if (openAtEnd)
            missing.Add(new MetricGap("net pnl",
                "a position was still open when the last bar closed. Its result is NOT in net pnl, "
                + "which covers closed trades only; it is in the equity the drawdown is measured on"));

        if (halted is not null)
            missing.Add(new MetricGap("the end of the window",
                $"the run halted at bar {haltedAt}: {halted}. Every figure here covers the bars "
                + "before the halt and nothing after it"));

        if (missingMinutes > 0)
            missing.Add(new MetricGap("coverage",
                $"{missingMinutes} minute(s) inside the window have no bar, in {gaps} run(s). Nothing "
                + "was filled in for them, so the strategy was not evaluated there at all"));

        foreach (var why in reasons)
            missing.Add(new MetricGap("a signal that was not acted on", why));

        return new BacktestMetrics
        {
            Bars = bars,
            ExposureBars = exposure,
            Signals = signals,
            Fills = fills,
            NoTrades = noTrades,
            Trades = trades,
            Wins = wins,
            WinRate = trades == 0 ? null : (decimal)wins / trades,
            GrossPnl = gross,
            Fees = fees,
            NetPnl = gross - fees,
            MaxDrawdown = drawdown,
            FinalEquity = finalEquity,
            MissingMinutes = missingMinutes,
            Gaps = gaps,
            Faults = faults,
            PositionOpenAtEnd = openAtEnd,
            Missing = missing
        };
    }
}
