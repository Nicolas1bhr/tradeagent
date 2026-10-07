using System.Globalization;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Features;

/// <summary>
/// THE TERMS ONE INPUT OF A FEATURE WAS RECORDED UNDER, as the tape's catalogue row states them: its words, the vendor's
/// page they were read from (empty where they were not re-read — Binance's), and the credit they ask (empty where none).
/// </summary>
public sealed record FeatureInputTerms(string Source, string Series, string Subject, string Terms, string TermsUrl, string Citation);

/// <summary>
/// ONE FEATURE, READ OVER A RANGE OF INSTANTS: a value or a stated absence at each, the clean-history start, each input's
/// terms and why no capital may stand on it — or a refusal in words with no points at all.
/// </summary>
public sealed record FeatureSeriesRead(
    FeatureSpec Spec,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Step,
    IReadOnlyList<FeatureValue> Points,
    FeatureCleanStart CleanHistoryStart,
    IReadOnlyList<FeatureInputTerms> Terms,
    string? LiveRefusal,
    string? Refusal)
{
    /// <summary>Whether the read was refused. A refused read holds no point: none is ever a part of the range asked for.</summary>
    public bool Refused => Refusal is not null;
}

/// <summary>
/// A FEATURE OVER A RANGE, FROM THE TAPE (<c>U-features</c> item 3): the rows each input's range needs, read through
/// <see cref="TapeReader.Window"/> — the agent-facing range read, and the only one — then every point computed by
/// <see cref="FeatureEvaluator"/>'s one body. Nothing is stored; no op, verb or screen reaches this yet.
///
/// <para><b>The audience is required</b> and handed to the reader, so whatever a later tape holdout decides applies here
/// without a line of this file; every row passes through <c>TapeStore.Served</c>, so a quarantined reading arrives without
/// its payload and its value is absent. <b>No "as of" is asked</b>: the tape serves every reading of the range whenever it
/// arrived, and the evaluator's gate decides what counts at each instant — one gate, not two that could disagree.</para>
///
/// <para><b>Bounded, and refused rather than cut short.</b> At most <see cref="MaxPoints"/> instants and at most
/// <see cref="MaxRowsPerInput"/> readings of an input's range: a series over part of the rows its range needs would be
/// another series, and nothing in it would say so.</para>
///
/// <para><b>The clean-history start</b> is the latest, across inputs, of each input's first first-seen of an
/// <c>O-LIVE</c>/<c>O-PIT</c> reading stamped by the end of the range, plus the latency and the window or the lookback
/// (<see cref="FeatureEvaluator.CleanHistoryStart"/>). It is the input's, not the range's: when the input holds readings
/// older than the range, they are read oldest first — a search for the oldest second, then slices forward — until the
/// first clean reading is found and no later-stamped reading could have been first seen before it (this build's live
/// rule bounds how early a live reading can arrive: its cadence plus 30 s). That read is bounded by
/// <see cref="MaxRowsPerInput"/> too; past it the start is stated absent, never guessed.</para>
/// </summary>
public static class FeatureSeries
{
    /// <summary>The most instants one read computes. Named in every refusal of more.</summary>
    public const int MaxPoints = 10_000;

    /// <summary>The most readings one input's range may hold for one read — and, separately, the most its clean-history search reads.</summary>
    public const int MaxRowsPerInput = 50_000;

    /// <summary>
    /// READS <paramref name="spec"/> AT <paramref name="from"/>, <c>from + step</c>, … up to <paramref name="to"/>.
    /// </summary>
    /// <param name="audience">Who is asking — required, and handed to the tape's reader.</param>
    /// <param name="newest">
    /// The newest licence reading of a source (<c>DataLicences.Newest</c>), or null for none at all — which no tape source
    /// has, so every feature reads research-only today.
    /// </param>
    /// <param name="rowCap">
    /// The most readings of an input's range, 1 to <see cref="MaxRowsPerInput"/>, the default. Lower only for a test that
    /// watches the refusal; nothing in the product passes it.
    /// </param>
    public static FeatureSeriesRead Read(TapeReader reader, BarAudience audience, FeatureSpec spec,
        DateTimeOffset from, DateTimeOffset to, TimeSpan step,
        Func<string, DataLicenceReading?>? newest = null, int rowCap = MaxRowsPerInput)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(audience);
        ArgumentNullException.ThrowIfNull(spec);
        if (rowCap is < 1 or > MaxRowsPerInput)
            throw new ArgumentOutOfRangeException(nameof(rowCap), rowCap, $"a series reads 1 to {MaxRowsPerInput} readings of an input");

        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        var terms = spec.Inputs.Select(i => TermsOf(reader, i)).ToList();
        var live = FeatureLicence.LiveRefusal(spec, newest ?? (_ => null));

        FeatureSeriesRead Refused(string why) => new(spec, from, to, step, [],
            new FeatureCleanStart(null, "the read was refused, so no clean-history start was looked for"), terms, live, why);

        if (to < from)
            return Refused($"a series runs forward, and its end {FeatureEvaluator.Stamp(to)} is before its start {FeatureEvaluator.Stamp(from)}");
        if (step < TimeSpan.FromSeconds(1))
            return Refused($"a series steps at least one second, and this one steps {Seconds(step)}");

        var points = (to - from).Ticks / step.Ticks + 1;
        if (points > MaxPoints)
            return Refused(
                $"{points.ToString(CultureInfo.InvariantCulture)} instants from {FeatureEvaluator.Stamp(from)} to "
                + $"{FeatureEvaluator.Stamp(to)} every {Seconds(step)}: a read serves at most {MaxPoints.ToString(CultureInfo.InvariantCulture)}, "
                + "and it is REFUSED rather than cut short. Ask for a shorter range or a longer step.");

        // THE RANGE EACH INPUT NEEDS: from its reach before the first instant to the last — no row stamped earlier can
        // change any value (FeatureEvaluator.Reach), and none stamped later can count.
        var reach = FeatureEvaluator.Reach(spec);
        var rangeStart = FeatureEvaluator.Minus(from, reach);

        var read = new Dictionary<FeatureInput, List<TapeObservation>>();
        foreach (var input in spec.Inputs)
        {
            var (rows, refusal) = Range(reader, audience, input, rangeStart, to, rowCap);
            if (refusal is not null) return Refused(refusal);
            read[input] = rows;
        }

        var clean = FeatureEvaluator.Combine(spec,
            spec.Inputs.Select(i => FirstClean(reader, audience, i, rangeStart, to, read[i], rowCap)));

        // EVERY POINT BY THE ONE EVALUATOR, over the rows its reach holds — the rows it would ignore left out, which is
        // why a point here equals the pure evaluator over every row (FeatureSeriesTests) — each payload parsed once.
        var sorted = read[spec.Input].OrderBy(r => r.SourceTime).ThenBy(r => r.Id).ToArray();
        var memo = new Dictionary<long, FieldReading>();
        FieldReading Field(TapeObservation row) =>
            memo.TryGetValue(row.Id, out var known) ? known : memo[row.Id] = FeatureEvaluator.ReadField(row, spec.Input.Field);

        var values = new List<FeatureValue>((int)points);
        for (var k = 0L; k < points; k++)
        {
            var t = from + TimeSpan.FromTicks(step.Ticks * k);
            values.Add(FeatureEvaluator.Evaluate(spec, Between(sorted, FeatureEvaluator.Minus(t, reach), t), t, Field));
        }

        return new FeatureSeriesRead(spec, from, to, step, values, clean, terms, live, null);
    }

    /// <summary>
    /// EVERY READING OF ONE INPUT STAMPED IN [<paramref name="from"/>, <paramref name="to"/>], paged newest first on the
    /// reader's own cursor — or a refusal past <paramref name="cap"/>, having read one row more than it and no further.
    /// </summary>
    static (List<TapeObservation> Rows, string? Refusal) Range(TapeReader reader, BarAudience audience, FeatureInput input,
        DateTimeOffset from, DateTimeOffset to, int cap)
    {
        var rows = new List<TapeObservation>();
        long? before = null;
        while (true)
        {
            var page = reader.Window(audience, new TapeQuery
            {
                Source = input.Source,
                Series = input.Series,
                Subject = input.Subject,
                From = from,
                To = to,
                Before = before,
                Limit = Math.Min(TapeReader.MaxRows, cap - rows.Count + 1)
            });
            rows.AddRange(page.Rows);

            if (rows.Count > cap)
                return ([],
                    $"{input.Name} holds more than {cap.ToString(CultureInfo.InvariantCulture)} readings stamped from "
                    + $"{FeatureEvaluator.Stamp(from)} to {FeatureEvaluator.Stamp(to)}, the most one read of an input holds. The read "
                    + "is REFUSED rather than cut short: a series over part of the readings its range needs would be another "
                    + "series. Ask for a shorter range.");

            if (!page.More || page.Rows.Count == 0) return (rows, null);
            before = page.Rows[^1].Id;
        }
    }

    /// <summary>
    /// THE INPUT'S FIRST FIRST-SEEN OF A CLEAN READING STAMPED BY <paramref name="to"/>, or why it is not stated. When the
    /// input holds nothing stamped before the range, the range's own rows are all of them; otherwise its oldest readings
    /// are read forward until the answer is settled.
    /// </summary>
    static (FeatureInput Input, DateTimeOffset? First, string? Why) FirstClean(TapeReader reader, BarAudience audience,
        FeatureInput input, DateTimeOffset rangeStart, DateTimeOffset to, List<TapeObservation> inRange, int cap)
    {
        if (rangeStart == DateTimeOffset.MinValue || !Holds(reader, audience, input, rangeStart.AddTicks(-1)))
            return Found(input, FeatureEvaluator.FirstClean(input, inRange, to), to);

        // ONLY A BUILT-IN ROW'S READINGS CAN BE CLEAN (TapeStore.ClassOf, ArchiveClassOf) — and the parse admits no other.
        if (TapeSourceCatalog.BuiltInLiveRule(input.Source) is not { } rule)
            return (input, null, $"no reading of {input.Name} can be O-LIVE or O-PIT: it is not one of this build's rows");

        // HOW EARLY A CLEAN READING CAN HAVE BEEN FIRST SEEN BEFORE ITS STAMP: a live one, by the cadence plus 30 s; an
        // O-PIT first reading, not at all (its first-seen IS its stamp). Once the scan is past the first clean reading by
        // that much, nothing stamped later can have been first seen before it.
        var early = rule.Cadence + TapeSourceCatalog.LiveTolerance;
        var cadence = rule.Cadence > TimeSpan.FromSeconds(1) ? rule.Cadence : TimeSpan.FromSeconds(1);
        var width = cadence * 16;

        var scanned = new List<TapeObservation>();
        DateTimeOffset? first = null;
        for (var a = Oldest(reader, audience, input, rangeStart); a <= to;)
        {
            if (first is { } found && a > FeatureEvaluator.Plus(found, early)) break;

            var b = FeatureEvaluator.Plus(a, width).AddTicks(-1);
            if (b > to) b = to;

            var (rows, refusal) = Range(reader, audience, input, a, b, cap - scanned.Count);
            if (refusal is not null)
                return (input, null,
                    $"the clean-history start of {input.Name} is not settled within the first {cap.ToString(CultureInfo.InvariantCulture)} "
                    + "readings it holds, so it is not stated");

            scanned.AddRange(rows);
            first = FeatureEvaluator.FirstClean(input, scanned, to);

            if (b >= to) break;
            a = b.AddTicks(1);
            if (width < cadence * 1024) width *= 2;
        }

        return Found(input, first, to);
    }

    static (FeatureInput Input, DateTimeOffset? First, string? Why) Found(FeatureInput input, DateTimeOffset? first, DateTimeOffset to) =>
        (input, first, first is null ? FeatureEvaluator.NoClean(input, to) : null);

    /// <summary>
    /// THE START OF THE SECOND HOLDING THE INPUT'S OLDEST READING, by binary search over whole seconds — about 36 reads of
    /// at most one row each. The caller has seen a reading stamped before <paramref name="below"/>.
    /// </summary>
    static DateTimeOffset Oldest(TapeReader reader, BarAudience audience, FeatureInput input, DateTimeOffset below)
    {
        // P(n): a reading stamped by the end of second n (counted from 0001-01-01) is held. P(hi) holds; P(-1) is vacuous.
        long lo = -1, hi = (below.UtcTicks - 1) / TimeSpan.TicksPerSecond;
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            if (Holds(reader, audience, input, new DateTimeOffset((mid + 1) * TimeSpan.TicksPerSecond - 1, TimeSpan.Zero))) hi = mid;
            else lo = mid;
        }
        return new DateTimeOffset(hi * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    /// <summary>Whether the input holds any reading stamped at or before <paramref name="at"/>.</summary>
    static bool Holds(TapeReader reader, BarAudience audience, FeatureInput input, DateTimeOffset at) =>
        reader.Window(audience, new TapeQuery
        {
            Source = input.Source,
            Series = input.Series,
            Subject = input.Subject,
            To = at,
            Limit = 1
        }).Rows.Count > 0;

    /// <summary>The readings of a source-time-ordered array stamped in [<paramref name="lo"/>, <paramref name="hi"/>].</summary>
    static ArraySegment<TapeObservation> Between(TapeObservation[] sorted, DateTimeOffset lo, DateTimeOffset hi)
    {
        var a = FirstWhere(sorted, r => r.SourceTime >= lo);
        var b = FirstWhere(sorted, r => r.SourceTime > hi);
        return new ArraySegment<TapeObservation>(sorted, a, Math.Max(0, b - a));
    }

    /// <summary>The first index of an ordered array at which a monotone condition holds, or its length.</summary>
    static int FirstWhere(TapeObservation[] sorted, Func<TapeObservation, bool> holds)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (holds(sorted[mid])) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    static FeatureInputTerms TermsOf(TapeReader reader, FeatureInput input) =>
        reader.Row(input.Source) is { } row
            ? new FeatureInputTerms(input.Source, input.Series, input.Subject, row.Terms, row.TermsUrl, row.Citation)
            : new FeatureInputTerms(input.Source, input.Series, input.Subject,
                "the tape's catalogue names no such source, so the terms it was recorded under are not known", "", "");

    static string Seconds(TimeSpan span) => span.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s";
}
