using TradeAgent.Core.Data;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// ONE INDICATOR'S VALUE OVER CLOSED BARS — incremental, undefined until it is warm, and nothing else.
///
/// <para>`docs/STRATEGY-LANGUAGE.md` holds the table these classes implement: the value, the
/// INITIALISATION and the bars needed, for each of the seven. Initialisation is the half that is easy
/// to get silently wrong — an EMA seeded with its first close rather than with the simple mean of the
/// first `p` values is a different indicator wearing the same name, and every backtest over it is
/// evidence about a program nobody wrote. `StrategyVersions.IndicatorSemanticsVersion` is hashed into
/// every `StrategyId` for exactly that reason.</para>
///
/// <para><b>Undefined is a value here, not a zero.</b> <see cref="Value"/> is null until the
/// indicator has seen the bars its table row says it needs; the evaluator refuses the event rather
/// than reading a half-filled window. Nothing in a chart would look wrong if it answered 0.</para>
///
/// <para><b>Decimal, never double.</b> Prices arrive as the vendor published them and stay decimal
/// the whole way: decimal addition and subtraction are exact, so a running sum cannot drift from the
/// window it stands for, and the same program on two machines produces the same digits.</para>
/// </summary>
public abstract class IndicatorSeries
{
    /// <summary>This indicator's value on the bar it was last fed, or null while it is not warm.</summary>
    public decimal? Value { get; protected set; }

    /// <summary>
    /// Feeds one CLOSED bar and returns the operations that cost, for the per-event budget.
    ///
    /// <paramref name="newSession"/> is true on the first bar of a session in the program's zone and
    /// <paramref name="inOpeningRange"/> is true when the bar belongs to the declared `opening_range`
    /// interval; both are the evaluator's readings of the calendar, because an indicator does not own
    /// a clock.
    /// </summary>
    public abstract int Push(KlineBar bar, bool newSession, bool inOpeningRange);

    /// <summary>How many decimals this indicator holds, for <see cref="StrategyLimits.MaxStateBytes"/>.</summary>
    public abstract int StateSlots { get; }

    /// <summary>The series one declaration asks for. Every kind is here; there is no default case.</summary>
    public static IndicatorSeries For(IndicatorDecl decl) => decl.Kind switch
    {
        IndicatorKind.Sma => new SmaSeries(decl.Source, decl.Period),
        IndicatorKind.Ema => new EmaSeries(decl.Source, decl.Period),
        IndicatorKind.Rsi => new RsiSeries(decl.Source, decl.Period),
        IndicatorKind.Atr => new AtrSeries(decl.Period),
        IndicatorKind.Highest => new ExtremeSeries(decl.Source, decl.Period, highest: true),
        IndicatorKind.Lowest => new ExtremeSeries(decl.Source, decl.Period, highest: false),
        IndicatorKind.OpeningRangeHigh => new OpeningRangeSeries(high: true),
        _ => new OpeningRangeSeries(high: false)
    };

    /// <summary>One series of one bar. `atr` and the accumulators do not go through here.</summary>
    protected static decimal Of(KlineBar bar, BarSeries series) => series switch
    {
        BarSeries.Open => bar.Open,
        BarSeries.High => bar.High,
        BarSeries.Low => bar.Low,
        BarSeries.Close => bar.Close,
        _ => bar.Volume
    };
}

/// <summary>The mean of the last `p` values. No initialisation; `p` bars.</summary>
sealed class SmaSeries(BarSeries source, int period) : IndicatorSeries
{
    readonly decimal[] window = new decimal[period];
    int head;
    int count;
    decimal sum;

    public override int StateSlots => period + 1;

    public override int Push(KlineBar bar, bool newSession, bool inOpeningRange)
    {
        var value = Of(bar, source);

        // Exact: decimal subtraction of a value this sum really contains cannot round.
        if (count == period) sum -= window[head];
        else count++;

        window[head] = value;
        sum += value;
        head = (head + 1) % period;

        Value = count == period ? sum / period : null;
        return 1;
    }
}

/// <summary>
/// `v*a + prev*(1-a)`, `a = 2/(p+1)`, with `prev` seeded with the SIMPLE MEAN of the first `p`
/// values — so the first value an EMA has is its SMA, and `p` bars is all it needs.
///
/// <para><b>`a` is computed once and `1-a` from that same `a`.</b> For most periods `2/(p+1)` is not
/// exact in decimal (`p=20` is 0.0952380952380952380952380952), so the two factors have to be the
/// two halves of one number or the weights would not sum to one. And the multiply-add form is the
/// documented one: `prev + a*(v-prev)` is the same algebra and not the same digits.</para>
/// </summary>
sealed class EmaSeries(BarSeries source, int period) : IndicatorSeries
{
    readonly decimal weight = 2m / (period + 1);
    readonly decimal carry = 1m - 2m / (period + 1);
    decimal seed;
    int count;
    decimal previous;

    public override int StateSlots => 5;

    public override int Push(KlineBar bar, bool newSession, bool inOpeningRange)
    {
        var value = Of(bar, source);
        count++;

        if (count < period)
        {
            seed += value;
            Value = null;
            return 1;
        }

        if (count == period)
        {
            seed += value;
            previous = seed / period;
        }
        else
        {
            previous = value * weight + previous * carry;
        }

        Value = previous;
        return 1;
    }
}

/// <summary>
/// Wilder's RSI: `100 - 100/(1 + avgGain/avgLoss)`, seeded with the MEAN GAIN AND MEAN LOSS OF THE
/// FIRST `p` CHANGES and smoothed after that as `avg = (avg*(p-1) + x)/p` — so `p` changes take
/// `p+1` bars.
///
/// <para>A zero average loss reads as 100, which `docs/STRATEGY-LANGUAGE.md` states and which is
/// also the only definition that is not a division by zero. A flat series has no gain and no loss,
/// and it reads 100 by the same rule rather than by a second one.</para>
/// </summary>
sealed class RsiSeries(BarSeries source, int period) : IndicatorSeries
{
    decimal? previous;
    int changes;
    decimal sumGain;
    decimal sumLoss;
    decimal averageGain;
    decimal averageLoss;

    public override int StateSlots => 7;

    public override int Push(KlineBar bar, bool newSession, bool inOpeningRange)
    {
        var value = Of(bar, source);

        if (previous is not { } prior)
        {
            previous = value;
            Value = null;
            return 1;
        }

        previous = value;
        var change = value - prior;
        var gain = change > 0m ? change : 0m;
        var loss = change < 0m ? -change : 0m;
        changes++;

        if (changes < period)
        {
            sumGain += gain;
            sumLoss += loss;
            Value = null;
            return 1;
        }

        if (changes == period)
        {
            sumGain += gain;
            sumLoss += loss;
            averageGain = sumGain / period;
            averageLoss = sumLoss / period;
        }
        else
        {
            averageGain = (averageGain * (period - 1) + gain) / period;
            averageLoss = (averageLoss * (period - 1) + loss) / period;
        }

        Value = averageLoss == 0m ? 100m : 100m - 100m / (1m + averageGain / averageLoss);
        return 1;
    }
}

/// <summary>
/// Wilder's mean of the true range `max(high-low, |high-prevClose|, |low-prevClose|)`, seeded with
/// the mean of the first `p` true ranges and smoothed as `atr = (atr*(p-1) + tr)/p`.
///
/// <para>A true range needs the PREVIOUS close, so the first bar has none and `p` ranges take `p+1`
/// bars. `atr` takes no series: the three terms are what a gap through the previous close looks like,
/// and a version over closes alone would price a gap at zero.</para>
/// </summary>
sealed class AtrSeries(int period) : IndicatorSeries
{
    decimal? previousClose;
    int ranges;
    decimal sum;
    decimal average;

    public override int StateSlots => 6;

    public override int Push(KlineBar bar, bool newSession, bool inOpeningRange)
    {
        if (previousClose is not { } prior)
        {
            previousClose = bar.Close;
            Value = null;
            return 1;
        }

        var range = bar.High - bar.Low;
        var up = Math.Abs(bar.High - prior);
        var down = Math.Abs(bar.Low - prior);
        var trueRange = Math.Max(range, Math.Max(up, down));

        previousClose = bar.Close;
        ranges++;

        if (ranges < period)
        {
            sum += trueRange;
            Value = null;
            return 1;
        }

        if (ranges == period)
        {
            sum += trueRange;
            average = sum / period;
        }
        else
        {
            average = (average * (period - 1) + trueRange) / period;
        }

        Value = average;
        return 1;
    }
}

/// <summary>
/// The largest or smallest of the last `p` values of a series. No initialisation; `p` bars.
///
/// <para>The window is rescanned only when the value that LEFT it was the extreme, so the usual bar
/// costs one operation and the worst costs `p`. The worst is what
/// <see cref="StrategyLimits.MaxOperationsPerEvent"/> is sized against, because a per-event budget
/// that only holds on average is not a bound on an event.</para>
/// </summary>
sealed class ExtremeSeries(BarSeries source, int period, bool highest) : IndicatorSeries
{
    readonly decimal[] window = new decimal[period];
    int head;
    int count;
    decimal extreme;

    public override int StateSlots => period + 1;

    public override int Push(KlineBar bar, bool newSession, bool inOpeningRange)
    {
        var value = Of(bar, source);
        var full = count == period;
        var leaving = full ? window[head] : 0m;

        window[head] = value;
        head = (head + 1) % period;
        if (!full) count++;

        if (count < period)
        {
            Value = null;
            return 1;
        }

        int operations;
        if (!full || leaving == extreme)
        {
            // The window has only just filled, or the value that left WAS the extreme. The new value
            // is already in the window, so one scan settles it.
            extreme = window[0];
            for (var i = 1; i < period; i++)
                if (Better(window[i], extreme)) extreme = window[i];
            operations = period;
        }
        else
        {
            if (Better(value, extreme)) extreme = value;
            operations = 1;
        }

        Value = extreme;
        return operations;
    }

    bool Better(decimal candidate, decimal against) => highest ? candidate > against : candidate < against;
}

/// <summary>
/// The highest high or lowest low of the bars in the declared `opening_range` interval, RESET EACH
/// SESSION and undefined until that interval's first bar of the session has closed.
///
/// <para>It is not a lookback: its window is a wall-clock interval in the program's zone, so a
/// session whose opening range has no bars at all — a venue outage, a holiday — leaves it undefined,
/// the evaluator does not evaluate that event, and the program takes no trade it could not have
/// measured. That is what a missing session IS here; nothing is carried over from yesterday.</para>
/// </summary>
sealed class OpeningRangeSeries(bool high) : IndicatorSeries
{
    decimal? extreme;

    public override int StateSlots => 2;

    public override int Push(KlineBar bar, bool newSession, bool inOpeningRange)
    {
        if (newSession) extreme = null;

        if (inOpeningRange)
        {
            var value = high ? bar.High : bar.Low;
            extreme = extreme is not { } held ? value
                : high ? Math.Max(held, value) : Math.Min(held, value);
        }

        Value = extreme;
        return 1;
    }
}

/// <summary>
/// EVERY DECLARED INDICATOR OF ONE PROGRAM, plus the recent values a rule may reach back for.
///
/// <para>A rule can say `@fastma[2]`, and a crossing reads both of its sides on this bar and on the
/// bar before, so a nested crossing reaches further still. The ring is therefore
/// <see cref="StrategyLimits.MaxHistoryDepth"/> plus the deepest nesting a condition may have plus
/// the bar itself — sized from the limits rather than guessed, so no program the parser accepts can
/// ask for a value this does not hold.</para>
///
/// <para><b>A ring advances once per BAR, never once per minute.</b> A minute with no bar is not a
/// bar: it advances no lookback and nothing is written for it. That is the same rule
/// `KlineNormaliser` keeps when it counts a gap and fills none, held on the other side of the file.</para>
/// </summary>
public sealed class IndicatorSet
{
    /// <summary>How far back a value is kept: the history limit, the nesting limit, and this bar.</summary>
    public const int Ring = StrategyLimits.MaxHistoryDepth + StrategyLimits.MaxExpressionDepth + 1;

    readonly IndicatorSeries[] series;
    readonly decimal?[][] history;
    readonly Dictionary<string, int> byName;
    int written;

    IndicatorSet(IReadOnlyList<IndicatorDecl> declarations)
    {
        series = new IndicatorSeries[declarations.Count];
        history = new decimal?[declarations.Count][];
        byName = new Dictionary<string, int>(declarations.Count, StringComparer.Ordinal);

        for (var i = 0; i < declarations.Count; i++)
        {
            series[i] = IndicatorSeries.For(declarations[i]);
            history[i] = new decimal?[Ring];
            byName[declarations[i].Name] = i;
        }
    }

    /// <summary>The declared indicators of one program, none of them warm yet.</summary>
    public static IndicatorSet For(StrategyProgram program) => new(program.Indicators);

    /// <summary>How many decimals every indicator and every kept value hold together.</summary>
    public int StateSlots => series.Sum(s => s.StateSlots) + series.Length * Ring;

    /// <summary>Feeds one closed bar to every indicator and returns the operations that cost.</summary>
    public int Push(KlineBar bar, bool newSession, bool inOpeningRange)
    {
        var operations = 0;
        var slot = written % Ring;

        for (var i = 0; i < series.Length; i++)
        {
            operations += series[i].Push(bar, newSession, inOpeningRange);
            history[i][slot] = series[i].Value;
        }

        written++;
        return operations;
    }

    /// <summary>
    /// A declared indicator's value <paramref name="back"/> bars ago, or null when it is not warm,
    /// when the run has not seen that many bars, or when the reach is past the ring.
    /// </summary>
    public decimal? Value(string name, int back)
    {
        if (back < 0 || back >= Ring) return null;
        if (!byName.TryGetValue(name, out var index)) return null;
        if (back >= written) return null;

        return history[index][(written - 1 - back) % Ring];
    }
}

/// <summary>
/// THE LAST FEW CLOSED BARS, so `close[3]` has something to read.
///
/// <para>Same ring as <see cref="IndicatorSet.Ring"/> and for the same reason: the history limit, the
/// nesting a crossing may add, and the bar that has just closed. A gap advances nothing here
/// either — `close[1]` is the PREVIOUS BAR, which may be an hour earlier if the venue was down, and
/// the run's gap count is what says so.</para>
/// </summary>
public sealed class BarHistory
{
    readonly KlineBar[] bars = new KlineBar[IndicatorSet.Ring];
    int written;

    /// <summary>How many bars have been pushed, ever.</summary>
    public long Count { get; private set; }

    /// <summary>How many decimals this holds, for the state-size limit.</summary>
    public int StateSlots => IndicatorSet.Ring * 6;

    public void Push(KlineBar bar)
    {
        bars[written % IndicatorSet.Ring] = bar;
        written++;
        Count++;
    }

    /// <summary>The bar <paramref name="back"/> closed bars ago; 0 is the one that has just closed.</summary>
    public bool TryAt(int back, out KlineBar bar)
    {
        bar = default!;
        if (back < 0 || back >= IndicatorSet.Ring || back >= written) return false;

        bar = bars[(written - 1 - back) % IndicatorSet.Ring];
        return true;
    }
}
