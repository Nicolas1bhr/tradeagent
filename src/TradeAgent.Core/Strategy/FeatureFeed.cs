using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Features;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// WHAT ONE DECLARED FEATURE CAME TO OVER A RUN, as the trace's <c>Feature</c> line states it (<c>U-language-v2a</c>):
/// its name and id; the last close it was read at; its clean-history start as of that close, or why there is none, and
/// — where a holdout window before the run bounded the search for it — why that start is not necessarily the input's
/// own (<see cref="FeatureCleanStart.Bounded"/>, <c>U-tape-holdout</c>); how many evaluated bars closed before that
/// start — evaluated, never trimmed — and how many had no value; and the worst evidence class of every value read, or
/// null where none carried one.
/// </summary>
public sealed record FeatureRunSummary(
    string Name,
    string Id,
    DateTimeOffset AsOf,
    DateTimeOffset? CleanHistoryStart,
    string? NoCleanHistory,
    string? CleanHistoryBounded,
    long BarsBefore,
    long BarsAbsent,
    string? WorstClass);

/// <summary>One close's values, in the program's declared order — or why the run halts there.</summary>
internal readonly record struct FeatureAnswer(IReadOnlyList<FeatureValue>? Values, string? Halt);

/// <summary>
/// A PROGRAM'S FEATURES, READ FOR A RUN AS THEY HAD ARRIVED BY EACH EVALUATED BAR'S CLOSE (<c>U-language-v2a</c> item 3;
/// <c>docs/EDGE-FACTORY.md</c> § 4.3, § 6.1).
///
/// <para><b>At the close, and only there.</b> <see cref="Backtest.Run"/> asks for the values at
/// <see cref="BarGrid.EndOf"/> of each bar it evaluates — the instant the bar's decision is taken — and hands them to the
/// evaluator, which refuses any value not stamped with that instant. Every value is <see cref="FeatureSeries.Read"/>'s,
/// so it is <see cref="FeatureEvaluator"/>'s one body over raw market rows: a row counts only once first seen by the
/// close minus the spec's latency, and a reading that arrived after the close moves no decision at it. No model output
/// is anywhere on this path (§ 6.1).</para>
///
/// <para><b>Under the run's own audience, and the tape's holdout.</b> One <see cref="BarAudience"/> reads the bars and the
/// features of a run — <c>Backtest.Over</c> builds this feed's <see cref="TapeHoldout"/> from the audience it hands the bar
/// feed, with the same dataset ledger — so a holdout decided for one is decided for the other: the referee's pass-through
/// at a verdict, a pipe caller's at <c>backtest</c>. The tape is held back over every dataset's holdout window
/// (<c>U-tape-holdout</c>), so a slice whose reads would reach one is NOT READ AT ALL: the run halts there in the
/// holdout's words, which <see cref="Withheld"/> keeps, and <c>Backtest.Over</c> — having asked <see cref="Refusal"/>
/// over the run's whole window before a bar was read — answers it as a refusal, never as a run on the values outside
/// the window. A slice is never made smaller to fit around one.</para>
///
/// <para><b>Lazily, in deterministic slices, inside the series' caps.</b> The values are read a slice at a time, forward,
/// as the run asks: on the bar's own grid for a uniform bar, and on the HOURLY grid for a daily bar cut at its zone's
/// midnight — 23 to 25 hours long — because every zone a program may name keeps whole-hour offsets, so each such close
/// is one of its points. A slice is a whole number of days and at most <see cref="FeatureSeries.MaxPoints"/> instants,
/// the first one sized to half the row cap at this build's cadence for the densest input; one refused for holding more
/// readings than one read serves is halved, and the next slice starts at the size that last succeeded — a function of
/// the run and the tape, so the same run reads the same slices. The slicing decides how the tape is read and never what
/// a value is: every point is the same evaluator's over the same rows, whatever slice it was read in. A single day still over the
/// caps HALTS the run in words, a defined fault exactly as <see cref="Backtest.MaxTracedBars"/> is, and never a read cut
/// short. No slice reaches past the run's own last close (<paramref name="until"/>).</para>
///
/// <para><b>Every value read is in the run's id.</b> <see cref="ValuesSha256"/> hashes one line per value handed out —
/// the feature's id, the instant, and the digest of the rows it stands on — so the same request over another tape is
/// another run, and the same tape is the same run.</para>
/// </summary>
public sealed class FeatureFeed : IDisposable
{
    readonly TapeReader _reader;
    readonly TapeHoldout _holdout;
    readonly IReadOnlyList<FeatureDecl> _features;
    readonly TimeSpan _step;
    readonly TimeSpan _reach;
    readonly long _pointsPerDay;
    readonly DateTimeOffset? _until;
    readonly IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    readonly List<DateTimeOffset> _closes = [];
    readonly long[] _absent;
    readonly string?[] _worst;

    IReadOnlyList<FeatureValue>[]? _slice;
    DateTimeOffset _sliceFrom;
    DateTimeOffset _sliceTo;
    long _days;
    IReadOnlyList<FeatureRunSummary>? _summaries;

    /// <param name="reader">The tape, read-only.</param>
    /// <param name="holdout">
    /// Who is asking and the holdout windows they may not read — the run's own bar audience with its dataset ledger,
    /// handed to every read (<c>U-tape-holdout</c>).
    /// </param>
    /// <param name="program">The program whose declared features are read, in its declared order.</param>
    /// <param name="until">The run's last possible close, or null for none known: no slice reaches past it.</param>
    public FeatureFeed(TapeReader reader, TapeHoldout holdout, StrategyProgram program, DateTimeOffset? until = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(holdout);
        ArgumentNullException.ThrowIfNull(program);
        if (program.Features.Count == 0)
            throw new ArgumentException("this program reads no feature, so there is nothing to feed it", nameof(program));

        _reader = reader;
        _holdout = holdout;
        _features = program.Features;
        _until = until?.ToUniversalTime();

        // THE LONGEST REACH: how far before a close, by source time, any declared feature's read looks
        // (FeatureEvaluator.Reach) — so a slice's reads, together, cover its first close less this to its last.
        _reach = ReachOf(_features);

        var grid = BarGrid.For(program);
        _step = grid.Zone is null ? grid.Length : TimeSpan.FromHours(1);
        _pointsPerDay = Math.Max(1, TimeSpan.TicksPerDay / _step.Ticks);

        // THE FIRST SLICE: as many days as the point cap allows, and no more than half the row cap of the densest input
        // at this build's own cadence for it — a guess that only saves refused reads, never decides a value, because a
        // slice still over the caps is halved like any other. A function of the program and this build: deterministic.
        var densest = _features.SelectMany(f => f.Spec.Inputs)
            .Select(i => TapeSourceCatalog.BuiltInLiveRule(i.Source)?.Cadence ?? TimeSpan.FromSeconds(1))
            .Min();
        var rowsPerDay = Math.Max(1, TimeSpan.TicksPerDay / Math.Max(TimeSpan.TicksPerSecond, densest.Ticks));
        _days = Math.Max(1, Math.Min(FeatureSeries.MaxPoints / _pointsPerDay, FeatureSeries.MaxRowsPerInput / 2 / rowsPerDay));

        _absent = new long[_features.Count];
        _worst = new string?[_features.Count];
    }

    /// <summary>Who read the features, in the words a refusal uses: the run's own audience.</summary>
    public string ReadAs => _holdout.Who;

    /// <summary>
    /// THE TAPE HOLDOUT'S WORDS FOR THE SLICE THIS FEED WOULD NOT READ, or null while every read it made was outside every
    /// holdout window. Set when the run halted on it: a cutoff set after <see cref="Refusal"/> was asked counts at the
    /// next slice (<c>TapeHoldout</c> reads the ledger at each read), and the run is then answered as refused in these
    /// words — never recorded as a run on the values before it.
    /// </summary>
    public string? Withheld { get; private set; }

    /// <summary>
    /// WHY THIS FEED MAY NOT BE READ FOR A RUN WHOSE FIRST EVALUATED BAR CLOSES AT <paramref name="firstClose"/>, in the
    /// tape holdout's words — or null, because every read it could make, from that close less the longest reach to the
    /// run's last close, is outside every holdout window this reader may not read. Asked before a bar is read; the
    /// referee's holdout has no window, so it is always null at a verdict.
    /// </summary>
    public string? Refusal(DateTimeOffset firstClose) => Withholds(firstClose.ToUniversalTime(), _until, whole: true);

    /// <summary>
    /// THE LONGEST REACH OF <paramref name="features"/>: how far before a close, by source time, any of their reads looks
    /// (<see cref="FeatureEvaluator.Reach"/>).
    /// </summary>
    internal static TimeSpan ReachOf(IReadOnlyList<FeatureDecl> features) => features.Max(f => FeatureEvaluator.Reach(f.Spec));

    /// <summary>
    /// WHERE THE READS THAT VALUE CLOSES FROM <paramref name="first"/> TO <paramref name="last"/> (null: no end known) START,
    /// AND THE TAPE HOLDOUT'S WORDS FOR THEM — from <paramref name="first"/> less <paramref name="reach"/> to
    /// <paramref name="last"/> — or null when they reach no window. The one arithmetic a feed decides a run or a slice by,
    /// and a reader decides by whether a recorded run's reads reach a window now (<c>U-run-trace</c>).
    /// </summary>
    internal static (DateTimeOffset From, string Why)? Reaching(
        TapeHoldout holdout, TimeSpan reach, DateTimeOffset first, DateTimeOffset? last)
    {
        ArgumentNullException.ThrowIfNull(holdout);
        var from = FeatureEvaluator.Minus(first, reach);
        return holdout.Refusal(from, last) is { } why ? (from, why) : null;
    }

    /// <summary>
    /// The holdout's refusal of the reads that value closes from <paramref name="first"/> to <paramref name="last"/> (null:
    /// no end known), in words saying what was asked and how a backtest avoids it — or null when it reaches no window.
    /// </summary>
    string? Withholds(DateTimeOffset first, DateTimeOffset? last, bool whole)
    {
        if (Reaching(_holdout, _reach, first, last) is not { } reached) return null;
        var (from, why) = reached;

        var names = string.Join(", ", _features.Select(f => $"`{f.Name}`"));
        var reach = ((long)_reach.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return $"this program's feature(s) — {names} — are read at the close of every bar a run evaluates, from the tape "
               + $"stamped up to {reach} s before it, and {(whole ? "this run's" : "the next slice of this run's")} closes "
               + $"from {FeatureEvaluator.Stamp(first)} to "
               + (last is { } end ? FeatureEvaluator.Stamp(end) : "the end of a dataset that records no last bar")
               + $" would read it from {FeatureEvaluator.Stamp(from)} — which the tape's holdout withholds: {why} In a "
               + "backtest's terms: its window must end early enough that its LAST bar closes before the holdout window "
               + $"opens, or start late enough that {reach} s before its first bar's close is at or after the window's "
               + (whole
                   ? "close. Nothing was run: a run on the values outside the window would be another run"
                   : "close. The run halted before that slice was read: a run on the values outside the window would be another run");
    }

    /// <summary>The step the tape is read at: the bar for a uniform grid, an hour for a daily bar cut in a zone.</summary>
    public TimeSpan Step => _step;

    /// <summary>
    /// THE SHA-256 OF EVERY VALUE HANDED OUT SO FAR, one line each — <c>feature id, instant, rows sha256</c> — in the order
    /// the run read them. What <see cref="BacktestRequest.RunIdFor"/> adds to a feature program's run id.
    /// </summary>
    public string ValuesSha256 => Convert.ToHexStringLower(_digest.GetCurrentHash());

    /// <summary>How many closes have been answered.</summary>
    public int Closes => _closes.Count;

    /// <summary>
    /// EVERY DECLARED FEATURE'S VALUE AT <paramref name="close"/>, in the program's order — or the words the run halts
    /// on, because one day of a feature's values is more than one read serves, or because the slice holding it would
    /// read the tape inside a holdout window.
    /// </summary>
    internal FeatureAnswer At(DateTimeOffset close)
    {
        close = close.ToUniversalTime();
        if (_slice is null || close < _sliceFrom || close > _sliceTo || (close - _sliceFrom).Ticks % _step.Ticks != 0)
            if (Read(close) is { } halt) return new FeatureAnswer(null, halt);

        var index = (int)((close - _sliceFrom).Ticks / _step.Ticks);
        var values = new FeatureValue[_features.Count];
        var instant = Encoding.UTF8.GetBytes(" " + close.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + " ");
        for (var i = 0; i < _features.Count; i++)
        {
            var value = _slice![i][index];
            values[i] = value;

            _digest.AppendData(Encoding.UTF8.GetBytes(_features[i].Spec.Id));
            _digest.AppendData(instant);
            _digest.AppendData(Encoding.UTF8.GetBytes(value.RowsSha256 + "\n"));

            if (!value.Present) _absent[i]++;
            if (value.Class is { } cls) _worst[i] = _worst[i] is { } held ? TapeClass.Lower(held, cls) : cls;
        }

        _closes.Add(close);
        _summaries = null;
        return new FeatureAnswer(values, null);
    }

    /// <summary>
    /// A SLICE STARTING AT <paramref name="from"/>: as many whole days as the last slice that was served, halved while any
    /// feature's read is refused for its size — or the words the run halts on, once a single day is refused, or at once
    /// when the slice's reads would reach a holdout window (<see cref="Withheld"/>).
    /// </summary>
    string? Read(DateTimeOffset from)
    {
        while (true)
        {
            var to = from + TimeSpan.FromTicks(_step.Ticks * (_days * _pointsPerDay - 1));
            if (_until is { } last && last >= from && to > last) to = last;

            // A SLICE WHOSE READS WOULD REACH A HOLDOUT WINDOW IS NOT READ, and never made smaller to fit around one: a
            // run on the values outside the window would be another run (U-tape-holdout).
            if (Withholds(from, to, whole: false) is { } withheld) return Withheld = withheld;

            var slice = new IReadOnlyList<FeatureValue>[_features.Count];
            string? refused = null;
            var at = -1;
            for (var i = 0; i < _features.Count && refused is null; i++)
            {
                var read = FeatureSeries.Read(_reader, _holdout, _features[i].Spec, from, to, _step);
                if (read.Refusal is { } why) { refused = why; at = i; }
                else slice[i] = read.Points;
            }

            if (refused is null)
            {
                _slice = slice;
                _sliceFrom = from;
                _sliceTo = to;
                return null;
            }

            // A CUTOFF SET WHILE THE SLICE WAS READ is the holdout's refusal, not the caps': it is answered in its words.
            if (Withholds(from, to, whole: false) is { } late) return Withheld = late;

            if (_days == 1)
                return string.Create(CultureInfo.InvariantCulture,
                    $"feature `{_features[at].Name}` could not be read for the bars closing from {from.UtcDateTime:O} to "
                    + $"{to.UtcDateTime:O}: {refused} One day of its values is more than one read of the tape serves, so the "
                    + $"run halted here rather than read part of what they stand on");

            _days = Math.Max(1, _days / 2);
        }
    }

    /// <summary>
    /// WHAT EACH DECLARED FEATURE CAME TO OVER THE CLOSES ANSWERED SO FAR, in the program's order — empty while none has
    /// been. Its clean-history start is read as of the last close answered, so a run states the start its own last bar
    /// could know and nothing the tape learnt after it — and, where a holdout window before the run bounded the search for
    /// it, says so in the series' own words (<see cref="FeatureCleanStart.Bounded"/>), so a start found since that
    /// window's close is never read as the input's own.
    /// </summary>
    public IReadOnlyList<FeatureRunSummary> Summaries()
    {
        if (_summaries is not null) return _summaries;
        if (_closes.Count == 0) return _summaries = [];

        var last = _closes[^1];
        var all = new List<FeatureRunSummary>(_features.Count);
        for (var i = 0; i < _features.Count; i++)
        {
            var read = FeatureSeries.Read(_reader, _holdout, _features[i].Spec, last, last, _step);
            var start = read.Refused ? null : read.CleanHistoryStart.At;
            var why = read.Refused
                ? $"the read that would state it was refused: {read.Refusal}"
                : read.CleanHistoryStart.Absent;
            var bounded = read.Refused ? null : read.CleanHistoryStart.Bounded;

            all.Add(new FeatureRunSummary(_features[i].Name, _features[i].Spec.Id, last, start, start is null ? why : null,
                bounded, start is { } s ? Before(s) : _closes.Count, _absent[i], _worst[i]));
        }

        return _summaries = all;
    }

    /// <summary>How many of the closes answered are before <paramref name="start"/>. They are ascending.</summary>
    long Before(DateTimeOffset start)
    {
        int lo = 0, hi = _closes.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_closes[mid] < start) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public void Dispose() => _digest.Dispose();
}
