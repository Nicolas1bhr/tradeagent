using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Features;

/// <summary>
/// ONE VALUE OF A FEATURE AT ONE INSTANT: a decimal, or a stated absence — never a zero standing in for one — with the
/// worst evidence class of the rows it was computed from and a SHA-256 of those rows.
///
/// <para><see cref="Class"/> is <c>TapeClass.Lower</c> folded over the rows read, so a value is never cleaner than its
/// dirtiest input (R07 § 5.3); null when no row was read. <see cref="RowsSha256"/> is the SHA-256 of one line per row
/// read, <c>id:revision:payload_sha256</c>, in id order — the empty text's hash when none was — so a value names the
/// exact readings it stands on.</para>
/// </summary>
public sealed record FeatureValue(
    DateTimeOffset At, decimal? Value, string? Absent, string? Class, string RowsSha256, int Rows)
{
    /// <summary>Whether there is a value. When there is not, <see cref="Absent"/> says why.</summary>
    public bool Present => Value is not null;
}

/// <summary>
/// WHEN A FEATURE'S VALUES CAN FIRST BE CLEAN: an instant, or null with the reason (<c>docs/EDGE-FACTORY.md</c> § 4.3;
/// R07 § 5.4).
/// </summary>
public sealed record FeatureCleanStart(DateTimeOffset? At, string? Absent)
{
    /// <summary>
    /// WHY THIS IS NOT NECESSARILY THE INPUT'S OWN START, in words — or null, when it is (<c>U-tape-holdout</c>). Set when a
    /// holdout window before the range bounded the search: no reading stamped before the window's close was read, so
    /// <see cref="At"/> is the start since then — the input's own is that instant or earlier, never later — and an
    /// <see cref="Absent"/> is an absence since then. A caller that reads <see cref="At"/> reads this with it.
    /// </summary>
    public string? Bounded { get; init; }
}

/// <summary>What one reading's field reads as: a decimal, or why it does not.</summary>
internal readonly record struct FieldReading(decimal? Value, string? Why);

/// <summary>
/// THE FEATURE EVALUATOR: PURE, AND THE ONLY GATE (<c>U-features</c> item 2; <c>docs/EDGE-FACTORY.md</c> § 4.3, § 6.1).
///
/// <para><b>Rows in, a value out, nothing else.</b> No clock, no tape, no store, no model: the value of a spec at an
/// instant is a function of the rows handed in and the instant, so a series served from the tape and the same spec
/// over every row the tape holds are the same values (<c>FeatureSeriesTests</c>). Whoever hands rows in, rows that had
/// not arrived by the instant change nothing — the gate is HERE, not in the reader: <see cref="FeatureSeries"/> asks
/// the tape with no "as of" at all.</para>
///
/// <para><b>The gate</b> (<see cref="Counts"/>). A row counts at <c>t</c> only if its FIRST-SEEN time is at or before
/// <c>t − latency</c> and its source time is at or before <c>t</c>. First-seen is when TradeAgent first had it — its
/// arrival — except an <c>O-PIT</c> first reading, which counts from its label: the vendor's own first-seen time,
/// vouched for by the vendor's checksum and its storage's date (<c>TapeStore.ArchiveClassOf</c>); a later revision of it
/// counts from its own arrival. The source time alone never admits a row: a reading stamped an hour ago that arrived a
/// second ago did not exist here an hour ago. Of the rows counted, each datum is read at its highest counted revision —
/// the newest reading of it TradeAgent had by then.</para>
///
/// <para><b>Absent, never zero, never skipped over.</b> No value when the newest counted reading is stamped more than
/// <c>max_age_s</c> before the instant (or there is none); when a window holds fewer than <c>min_rows</c> readings; when a
/// reading's field is missing or is not a plain decimal; when a payload is withheld by the tape's screen — a withheld
/// reading is never dropped so the rest can be averaged; and when a ratio's base is 0. Each absence says why.</para>
///
/// <para><b>Decimal, from the vendor's text.</b> A field is read from the payload as recorded: a JSON string or number
/// whose text is a plain decimal — an optional minus, digits, at most one point, at most 28 digits after it and 28
/// significant in all — exactly; anything else is unreadable and the value absent. Sums, differences, minima and maxima
/// are exact; a quotient (a mean, a ratio, a rank) is <see cref="decimal"/>'s, rounded as it rounds, the same on every
/// machine.</para>
/// </summary>
public static class FeatureEvaluator
{
    /// <summary>
    /// WHEN TRADEAGENT FIRST HAD A READING: its arrival — or, for an <c>O-PIT</c> first reading, its label, the vendor's
    /// own first-seen time. A later revision is first seen when it arrived, whatever its class.
    /// </summary>
    public static DateTimeOffset FirstSeen(TapeObservation row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.EvidenceClass == TapeClass.Pit && row.Revision == 1 ? row.SourceTime : row.ReceivedAt;
    }

    /// <summary>
    /// THE GATE: whether <paramref name="row"/> counts at <paramref name="t"/> under <paramref name="latency"/> — first
    /// seen by <c>t − latency</c>, and stamped no later than <c>t</c>. Every value this type computes reads rows through
    /// this and nothing else.
    /// </summary>
    public static bool Counts(TapeObservation row, DateTimeOffset t, TimeSpan latency)
    {
        ArgumentNullException.ThrowIfNull(row);
        return FirstSeen(row) <= Minus(t, latency) && row.SourceTime <= t;
    }

    /// <summary>
    /// HOW FAR BEFORE AN INSTANT, BY SOURCE TIME, A SPEC LOOKS: the max age for a latest, the lookback and the max age
    /// for a change, the latency and the window for a window. A row stamped earlier than <c>t − Reach</c> never affects
    /// the value at <c>t</c>, which is what lets a series read only the rows its range needs.
    /// </summary>
    public static TimeSpan Reach(FeatureSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Kind switch
        {
            FeatureKinds.Latest => spec.MaxAge,
            FeatureKinds.Change => spec.Lookback!.Value + spec.MaxAge,
            _ => spec.Latency + spec.Window!.Value
        };
    }

    /// <summary>
    /// THE VALUE OF <paramref name="spec"/> AT <paramref name="t"/> FROM <paramref name="rows"/>: a decimal or a stated
    /// absence, with the worst class and the digest of the rows read. Rows of any other series or subject are ignored,
    /// and so is every row the gate does not count — handing in more rows can never change a value.
    /// </summary>
    public static FeatureValue At(FeatureSpec spec, IEnumerable<TapeObservation> rows, DateTimeOffset t)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Evaluate(spec, rows, t, row => ReadField(row, spec.Input.Field));
    }

    /// <summary>
    /// <see cref="At"/> with the field read through <paramref name="field"/> — the series' own memo of
    /// <see cref="ReadField"/>, so a reading's payload is parsed once per read and not once per point. One body, so the
    /// series and the pure evaluator cannot compute two different things.
    /// </summary>
    internal static FeatureValue Evaluate(FeatureSpec spec, IEnumerable<TapeObservation> rows, DateTimeOffset t,
        Func<TapeObservation, FieldReading> field)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(field);
        t = t.ToUniversalTime();

        // THE INPUT'S ROWS AND NO OTHER: a row of another series or subject never counts, whoever hands it in.
        var mine = new List<TapeObservation>();
        foreach (var row in rows)
            if (row is not null && Of(spec.Input, row)) mine.Add(row);

        return spec.Kind switch
        {
            FeatureKinds.Latest => Latest(spec, mine, t, field),
            FeatureKinds.Change => Change(spec, mine, t, field),
            _ => Window(spec, mine, t, field)
        };
    }

    // ------------------------------------------------------------------------------------------- the kinds

    static FeatureValue Latest(FeatureSpec spec, List<TapeObservation> mine, DateTimeOffset t,
        Func<TapeObservation, FieldReading> field)
    {
        if (LatestAt(spec, mine, t) is not { } latest) return Absent(t, NothingFresh(spec, t), []);
        var reading = field(latest);
        return reading.Value is { } value ? Present(t, value, [latest]) : Absent(t, reading.Why!, [latest]);
    }

    /// <summary>The latest at <c>t</c> against the latest at <c>t − lookback</c> — each the latest as it was known then.</summary>
    static FeatureValue Change(FeatureSpec spec, List<TapeObservation> mine, DateTimeOffset t,
        Func<TapeObservation, FieldReading> field)
    {
        var lookback = spec.Lookback!.Value;
        var earlier = Minus(t, lookback);
        var now = LatestAt(spec, mine, t);
        var then = LatestAt(spec, mine, earlier);

        var read = new List<TapeObservation>();
        if (now is not null) read.Add(now);
        if (then is not null && (now is null || then.Id != now.Id)) read.Add(then);

        if (now is null) return Absent(t, NothingFresh(spec, t), read);
        if (then is null)
            return Absent(t, NothingFresh(spec, earlier) + $", one lookback ({Seconds(lookback)}) before {Stamp(t)}", read);

        var a = field(now);
        if (a.Value is not { } x) return Absent(t, a.Why!, read);
        var b = field(then);
        if (b.Value is not { } y) return Absent(t, b.Why!, read);

        if (spec.Mode == FeatureKinds.Ratio && y == 0m)
            return Absent(t,
                $"the ratio's base — {spec.Input.Field} of the reading stamped {Stamp(then.SourceTime)} (row {then.Id}) — is 0",
                read);

        return Arithmetic(t, read, spec, () => spec.Mode == FeatureKinds.Ratio ? x / y : x - y);
    }

    /// <summary>
    /// The window: the counted readings stamped after <c>t − latency − window</c>, each datum at its highest counted
    /// revision, oldest first — its newest the latest reading counted at <c>t</c>.
    /// </summary>
    static FeatureValue Window(FeatureSpec spec, List<TapeObservation> mine, DateTimeOffset t,
        Func<TapeObservation, FieldReading> field)
    {
        var minRows = spec.MinRows!.Value;
        var gate = Minus(t, spec.Latency);
        var floor = Minus(gate, spec.Window!.Value);
        var window = Known(mine, t, spec.Latency, stamped => stamped > floor).Values
            .OrderBy(r => r.SourceTime).ThenBy(r => r.Id)
            .ToList();

        if (window.Count < minRows)
            return Absent(t,
                $"the window — readings of {spec.Input.Name} stamped after {Stamp(floor)} that had arrived by {Stamp(gate)} — "
                + $"holds {Readings(window.Count)}, fewer than min_rows {minRows.ToString(CultureInfo.InvariantCulture)}",
                window);

        var newest = window[^1];
        if (newest.SourceTime < Minus(t, spec.MaxAge))
            return Absent(t,
                $"the newest reading in the window is stamped {Stamp(newest.SourceTime)}, more than max_age_s "
                + $"({Seconds(spec.MaxAge)}) before {Stamp(t)}",
                window);

        // EVERY READING OR NONE: one that cannot be read makes the value absent; it is never left out of the window.
        var values = new decimal[window.Count];
        for (var i = 0; i < window.Count; i++)
        {
            var reading = field(window[i]);
            if (reading.Value is not { } value) return Absent(t, reading.Why!, window);
            values[i] = value;
        }

        return Arithmetic(t, window, spec, () => spec.Kind switch
        {
            FeatureKinds.Mean => Sum(values) / values.Length,
            FeatureKinds.Min => values.Min(),
            FeatureKinds.Max => values.Max(),
            _ => (decimal)values.Count(v => v <= values[^1]) / values.Length
        });
    }

    // ------------------------------------------------------------------------------------------- the gate, applied

    /// <summary>
    /// The newest counted reading stamped no earlier than <c>at − max_age</c>, or null: none had arrived, or the newest
    /// that had is older than the max age. Ties on the source time — which one subject's distinct data never have —
    /// go to the row the tape wrote last.
    /// </summary>
    static TapeObservation? LatestAt(FeatureSpec spec, List<TapeObservation> mine, DateTimeOffset at)
    {
        var floor = Minus(at, spec.MaxAge);
        TapeObservation? best = null;
        foreach (var row in Known(mine, at, spec.Latency, stamped => stamped >= floor).Values)
            if (best is null || row.SourceTime > best.SourceTime || (row.SourceTime == best.SourceTime && row.Id > best.Id))
                best = row;
        return best;
    }

    /// <summary>
    /// EVERY DATUM COUNTED AT <paramref name="at"/> WITHIN A SOURCE-TIME BOUND, AT ITS HIGHEST COUNTED REVISION — the
    /// one place the gate is applied. A revision that had not arrived leaves the reading before it standing.
    /// </summary>
    static Dictionary<string, TapeObservation> Known(List<TapeObservation> mine, DateTimeOffset at, TimeSpan latency,
        Func<DateTimeOffset, bool> stamped)
    {
        var known = new Dictionary<string, TapeObservation>(StringComparer.Ordinal);
        foreach (var row in mine)
        {
            if (!stamped(row.SourceTime) || !Counts(row, at, latency)) continue;
            if (!known.TryGetValue(row.NaturalKey, out var held)
                || row.Revision > held.Revision
                || (row.Revision == held.Revision && row.Id < held.Id))
                known[row.NaturalKey] = row;
        }
        return known;
    }

    static bool Of(FeatureInput input, TapeObservation row) =>
        string.Equals(row.Source, input.Source, StringComparison.Ordinal)
        && string.Equals(row.Series, input.Series, StringComparison.Ordinal)
        && string.Equals(row.Subject, input.Subject, StringComparison.Ordinal);

    // ------------------------------------------------------------------------------------------- the clean-history start

    /// <summary>
    /// THE FIRST FIRST-SEEN OF A CLEAN READING OF <paramref name="input"/>: the earliest <see cref="FirstSeen"/> of an
    /// <c>O-LIVE</c> or <c>O-PIT</c> row of it stamped at or before <paramref name="to"/> — or null, because none is.
    /// </summary>
    public static DateTimeOffset? FirstClean(FeatureInput input, IEnumerable<TapeObservation> rows, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rows);

        DateTimeOffset? first = null;
        foreach (var row in rows)
        {
            if (row is null || !Of(input, row) || row.SourceTime > to) continue;
            if (row.EvidenceClass is not (TapeClass.Live or TapeClass.Pit)) continue;
            var seen = FirstSeen(row);
            if (first is null || seen < first) first = seen;
        }
        return first;
    }

    /// <summary>
    /// THE CLEAN-HISTORY START OF <paramref name="spec"/> OVER <paramref name="rows"/> (R07 § 5.4): the latest, across its
    /// inputs, of each input's <see cref="FirstClean"/>, plus the latency and the window or the lookback — the first
    /// instant whose value can stand wholly on readings recorded after every input was first clean. Null, with the
    /// reason, while any input has no clean reading stamped at or before <paramref name="to"/>.
    /// </summary>
    public static FeatureCleanStart CleanHistoryStart(FeatureSpec spec, IEnumerable<TapeObservation> rows, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rows);
        var all = rows as IReadOnlyCollection<TapeObservation> ?? rows.ToList();
        return Combine(spec, spec.Inputs.Select(input =>
            FirstClean(input, all, to) is { } first
                ? (input, (DateTimeOffset?)first, (string?)null)
                : (input, null, NoClean(input, to))));
    }

    /// <summary>The words for an input with no clean reading stamped by <paramref name="to"/>.</summary>
    internal static string NoClean(FeatureInput input, DateTimeOffset to) =>
        $"no reading of {input.Name} stamped at or before {Stamp(to)} is O-LIVE or O-PIT, so no value of this feature is clean yet";

    /// <summary>The clean-history start from each input's first clean first-seen — one rule for the pure path and the series.</summary>
    internal static FeatureCleanStart Combine(FeatureSpec spec,
        IEnumerable<(FeatureInput Input, DateTimeOffset? First, string? Why)> firsts)
    {
        DateTimeOffset? latest = null;
        foreach (var (input, first, why) in firsts)
        {
            if (first is not { } seen) return new FeatureCleanStart(null, why ?? $"no clean reading of {input.Name} is known");
            if (latest is null || seen > latest) latest = seen;
        }

        if (latest is not { } start) return new FeatureCleanStart(null, "the feature reads no input");
        return new FeatureCleanStart(Plus(start, spec.Latency + (spec.Window ?? spec.Lookback ?? TimeSpan.Zero)), null);
    }

    // ------------------------------------------------------------------------------------------- one field, as a decimal

    /// <summary>
    /// ONE READING'S FIELD AS A DECIMAL, OR WHY NOT: withheld by the screen, not a JSON object, no such field, or a value
    /// that is not a JSON string or number holding a plain decimal. Never a value the vendor did not publish: the
    /// vendor's text is parsed exactly, and only text that fits a decimal exactly is read.
    /// </summary>
    internal static FieldReading ReadField(TapeObservation row, string field)
    {
        if (row.Payload is null)
            return new FieldReading(null, row.Quarantine is { } q
                ? $"the payload of row {row.Id} is withheld: the tape's screen quarantines it ({q.Rule}, screen v{q.Version})"
                : $"the payload of row {row.Id} is withheld");

        try
        {
            using var doc = JsonDocument.Parse(row.Payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new FieldReading(null, $"the payload of row {row.Id} is not a JSON object");
            if (!doc.RootElement.TryGetProperty(field, out var value))
                return new FieldReading(null, $"row {row.Id} has no field '{field}'");

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
            return text is not null && TryPlainDecimal(text, out var d)
                ? new FieldReading(d == 0m ? 0m : d, null)
                : new FieldReading(null, $"row {row.Id}'s field '{field}' is not a plain decimal");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new FieldReading(null, $"the payload of row {row.Id} is not JSON this build reads");
        }
    }

    /// <summary>
    /// A PLAIN DECIMAL, READ EXACTLY: an optional '-', one or more digits, and optionally a '.' and one or more digits;
    /// at most 28 digits after the point and 28 significant digits in all, so <see cref="decimal"/> holds it without
    /// rounding. No '+', no exponent, no white space, no thousands separator.
    /// </summary>
    static bool TryPlainDecimal(string text, out decimal value)
    {
        value = 0m;
        var i = text.Length > 0 && text[0] == '-' ? 1 : 0;
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        if (i == start) return false;

        var fraction = 0;
        if (i < text.Length && text[i] == '.')
        {
            var point = ++i;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            fraction = i - point;
            if (fraction == 0) return false;
        }
        if (i != text.Length || fraction > 28) return false;

        var significant = text[start..].Replace(".", "", StringComparison.Ordinal).TrimStart('0').Length;
        if (significant > 28) return false;

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);
    }

    // ------------------------------------------------------------------------------------------- what a value carries

    static FeatureValue Present(DateTimeOffset t, decimal value, IReadOnlyList<TapeObservation> read) =>
        new(t, value == 0m ? 0m : value, null, Worst(read), Digest(read), read.Count);

    static FeatureValue Absent(DateTimeOffset t, string why, IReadOnlyList<TapeObservation> read) =>
        new(t, null, why, Worst(read), Digest(read), read.Count);

    static FeatureValue Arithmetic(DateTimeOffset t, IReadOnlyList<TapeObservation> read, FeatureSpec spec, Func<decimal> compute)
    {
        try { return Present(t, compute(), read); }
        catch (OverflowException) { return Absent(t, $"the {spec.Kind} is beyond what a decimal holds", read); }
    }

    static decimal Sum(decimal[] values)
    {
        var sum = 0m;
        foreach (var v in values) sum += v;
        return sum;
    }

    /// <summary>The worst class among the rows read, by <see cref="TapeClass.Lower"/>; null when none was read.</summary>
    static string? Worst(IReadOnlyList<TapeObservation> read)
    {
        string? worst = null;
        foreach (var row in read) worst = worst is null ? row.EvidenceClass : TapeClass.Lower(worst, row.EvidenceClass);
        return worst;
    }

    /// <summary>The SHA-256 of the rows read: one line <c>id:revision:payload_sha256</c> each, in id order.</summary>
    static string Digest(IReadOnlyList<TapeObservation> read)
    {
        var text = new StringBuilder();
        foreach (var row in read.OrderBy(r => r.Id))
            text.Append(row.Id.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(row.Revision.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(row.PayloadSha256).Append('\n');
        return Sha256Hex.Of(text.ToString());
    }

    static string NothingFresh(FeatureSpec spec, DateTimeOffset at) =>
        $"no reading of {spec.Input.Name} stamped at or after {Stamp(Minus(at, spec.MaxAge))} had arrived by "
        + $"{Stamp(Minus(at, spec.Latency))} — the newest is older than max_age_s ({Seconds(spec.MaxAge)}), or there is none";

    static string Readings(int n) => n == 1 ? "1 reading" : $"{n.ToString(CultureInfo.InvariantCulture)} readings";

    static string Seconds(TimeSpan span) => ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";

    /// <summary>An instant as every sentence here spells it: UTC, to the millisecond, the invariant culture.</summary>
    internal static string Stamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>t − span</c>, or the earliest instant there is when that would be earlier still.</summary>
    internal static DateTimeOffset Minus(DateTimeOffset t, TimeSpan span) =>
        t.UtcTicks - DateTimeOffset.MinValue.UtcTicks <= span.Ticks
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(t.UtcTicks - span.Ticks, TimeSpan.Zero);

    /// <summary><c>t + span</c>, or the latest instant there is when that would be later still.</summary>
    internal static DateTimeOffset Plus(DateTimeOffset t, TimeSpan span) =>
        DateTimeOffset.MaxValue.UtcTicks - t.UtcTicks <= span.Ticks
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(t.UtcTicks + span.Ticks, TimeSpan.Zero);
}
