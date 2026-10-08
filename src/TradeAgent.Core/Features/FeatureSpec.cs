using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Features;

/// <summary>
/// THE KINDS OF FEATURE, AND THE TWO WAYS A CHANGE IS MEASURED — the words a spec is written in, spelled once.
/// </summary>
public static class FeatureKinds
{
    /// <summary>The newest reading that had arrived, its field as a decimal.</summary>
    public const string Latest = "latest";

    /// <summary>The latest now against the latest one lookback earlier: <see cref="Diff"/> or <see cref="Ratio"/>.</summary>
    public const string Change = "change";

    /// <summary>The mean of the window's values.</summary>
    public const string Mean = "mean";

    /// <summary>The least of the window's values.</summary>
    public const string Min = "min";

    /// <summary>The greatest of the window's values.</summary>
    public const string Max = "max";

    /// <summary>The share of the window's values at or below the newest one.</summary>
    public const string PctRank = "pct-rank";

    /// <summary>A change as the latest minus the earlier.</summary>
    public const string Diff = "diff";

    /// <summary>A change as the latest divided by the earlier.</summary>
    public const string Ratio = "ratio";

    /// <summary>Every kind, in the order a refusal names them.</summary>
    public static readonly IReadOnlyList<string> All = [Latest, Change, Mean, Min, Max, PctRank];

    /// <summary>Whether a kind is computed over a window of readings and so declares <c>window_s</c> and <c>min_rows</c>.</summary>
    public static bool IsWindow(string? kind) => kind is Mean or Min or Max or PctRank;
}

/// <summary>
/// ONE SERIES A FEATURE READS: a source and series of the tape, one subject of it, and the field of each reading's
/// payload that is read as a decimal.
/// </summary>
public sealed record FeatureInput(string Source, string Series, string Subject, string Field)
{
    /// <summary>How a sentence names the series it reads: <c>binance-um-premium/premium-index BTCUSDT</c>.</summary>
    public string Name => $"{Source}/{Series} {Subject}";
}

/// <summary>A parsed spec, or the refusal in words. Never both, never neither.</summary>
public sealed class FeatureParse
{
    FeatureParse(FeatureSpec? spec, string why)
    {
        Spec = spec;
        Why = why;
    }

    /// <summary>The spec, or null when the text was refused.</summary>
    public FeatureSpec? Spec { get; }

    /// <summary>The refusal, in words, or the empty string when the text parsed.</summary>
    public string Why { get; }

    public bool Ok => Spec is not null;

    internal static FeatureParse Yes(FeatureSpec spec) => new(spec, "");

    internal static FeatureParse No(string why) => new(null, why);
}

/// <summary>
/// A FEATURE: A DETERMINISTIC SPEC, AS DATA, OVER ONE SERIES OF THE TAPE — known by its hash and computed by the app
/// in decimal from only what had arrived by its instant (<c>U-features</c>; <c>docs/EDGE-FACTORY.md</c> § 4.3).
///
/// <para><b>A spec proposes; the app computes; nothing is stored.</b> An agent may write a spec. What a value IS —
/// which rows count at an instant, how a field becomes a number, when there is no value — is
/// <see cref="FeatureEvaluator"/>'s, in app code, from raw market rows (§ 6.1: no model output on the signal path).
/// A spec carries no value, no class and no row, so it cannot claim one (§ 6.3).</para>
///
/// <para><b>The parse is TOTAL.</b> Every text produces a <see cref="FeatureParse"/>: a spec, or a refusal that says
/// why in words. An unknown key, an unknown kind, a source the tape does not read as market numbers, an unknown series
/// or symbol, a field that is a row's time or symbol, a missing duration, a negative one, one that is not whole
/// seconds — each is refused, never defaulted, never repaired. A spec that parsed has stated every fact its kind has,
/// so <see cref="FeatureCanonical"/> has no default to write.</para>
///
/// <para><b>Look-ahead cannot be written.</b> There is no key that reads ahead of a feature's instant, an unknown key
/// is refused, and a negative latency — the one number that could move the gate past arrival — is refused as
/// look-ahead. The evaluator enforces the same from the other side: a row counts only once it had arrived.</para>
///
/// <para><b>One meaning, one id.</b> <see cref="Id"/> is <c>Sha256Hex.Of(Canonical + "\n" + Manifest)</c>: two
/// spellings of one spec are one id, and every fact it states moves it. <c>StrategyVersions.Manifest</c> does not
/// move for features; <see cref="FeatureVersions.Manifest"/> is theirs.</para>
/// </summary>
public sealed class FeatureSpec
{
    /// <summary>The most a spec's text may hold, in UTF-8 bytes. A spec is a handful of keys.</summary>
    public const int MaxSpecBytes = 4_096;

    /// <summary>The longest latency, in seconds: a day.</summary>
    public const long MaxLatencySeconds = 86_400;

    /// <summary>The longest max age, lookback or window, in seconds: 366 days.</summary>
    public const long MaxSpanSeconds = 366L * 86_400;

    /// <summary>
    /// The most readings a window may require: <see cref="FeatureSeries.MaxRowsPerInput"/>, the most one read of an
    /// input's range holds — a window that needed more could never be served.
    /// </summary>
    public const int MaxMinRows = FeatureSeries.MaxRowsPerInput;

    internal FeatureSpec(string kind, string? mode, FeatureInput input, TimeSpan latency, TimeSpan maxAge,
        TimeSpan? lookback, TimeSpan? window, int? minRows)
    {
        Kind = kind;
        Mode = mode;
        Input = input;
        Inputs = [input];
        Latency = latency;
        MaxAge = maxAge;
        Lookback = lookback;
        Window = window;
        MinRows = minRows;

        // Computed once, here, because a spec's identity must not depend on when it is asked for.
        Canonical = FeatureCanonical.Of(this);
        Manifest = FeatureVersions.Manifest;
        Id = Sha256Hex.Of(Canonical + "\n" + Manifest);
    }

    /// <summary>One of <see cref="FeatureKinds.All"/>.</summary>
    public string Kind { get; }

    /// <summary><see cref="FeatureKinds.Diff"/> or <see cref="FeatureKinds.Ratio"/> for a change; null for every other kind.</summary>
    public string? Mode { get; }

    /// <summary>The one series this feature reads.</summary>
    public FeatureInput Input { get; }

    /// <summary>
    /// Every series the feature reads — one, for every kind this build knows. The series read, the clean-history
    /// start and the licence are each stated per input, so a kind over several inputs adds no second rule.
    /// </summary>
    public IReadOnlyList<FeatureInput> Inputs { get; }

    /// <summary>How long after a reading first arrived it may count: a row counts at <c>t</c> only once first seen by <c>t − Latency</c>.</summary>
    public TimeSpan Latency { get; }

    /// <summary>The oldest a reading's source time may be, before the instant, for a value to stand on it.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>How far back a change looks; null for every other kind.</summary>
    public TimeSpan? Lookback { get; }

    /// <summary>The span of a window, before <c>t − Latency</c>; null for kinds without one.</summary>
    public TimeSpan? Window { get; }

    /// <summary>The fewest readings a window must hold for a value; null for kinds without one.</summary>
    public int? MinRows { get; }

    /// <summary>The typed, ordered text — <see cref="FeatureCanonical"/>. What is hashed.</summary>
    public string Canonical { get; }

    /// <summary>The semantic versions in force — <see cref="FeatureVersions.Manifest"/> — hashed in beside the text.</summary>
    public string Manifest { get; }

    /// <summary>THE IDENTITY: <c>Sha256Hex.Of(Canonical + "\n" + Manifest)</c>, 64 lower-case hex characters.</summary>
    public string Id { get; }

    // ------------------------------------------------------------------------------------------- the parse

    const string KindKey = "kind";
    const string ModeKey = "mode";
    const string InputKey = "input";
    const string LatencyKey = "latency_s";
    const string MaxAgeKey = "max_age_s";
    const string LookbackKey = "lookback_s";
    const string WindowKey = "window_s";
    const string MinRowsKey = "min_rows";

    static readonly string[] InputKeys = ["source", "series", "subject", "field"];

    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    /// <summary>The keys a kind has, every one required, in the order a refusal names them.</summary>
    static IReadOnlyList<string> KeysOf(string kind) => kind switch
    {
        FeatureKinds.Latest => [KindKey, InputKey, LatencyKey, MaxAgeKey],
        FeatureKinds.Change => [KindKey, ModeKey, InputKey, LatencyKey, MaxAgeKey, LookbackKey],
        _ => [KindKey, InputKey, LatencyKey, MaxAgeKey, WindowKey, MinRowsKey]
    };

    /// <summary>
    /// Reads one spec. Returns a <see cref="FeatureSpec"/> or a refusal in words, and never throws, for any text.
    /// </summary>
    public static FeatureParse Parse(string? json)
    {
        try
        {
            return ParseCore(json);
        }
        catch (Exception ex)
        {
            // INSURANCE, NOT CONTROL FLOW, as StrategyParser.Parse has it: nothing below raises, and the cost of that
            // being wrong once is a text an agent wrote taking the app down. Said to be the parser's defect, because it is.
            return FeatureParse.No(
                $"this text could not be read as a feature spec: the parser itself failed with {ex.GetType().Name}. "
                + "That is a defect in the parser rather than in the spec — nothing was parsed.");
        }
    }

    static FeatureParse ParseCore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return FeatureParse.No("a feature spec is one JSON object, and this text is empty");

        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxSpecBytes)
            return FeatureParse.No($"a feature spec is at most {MaxSpecBytes} bytes, and this text is {bytes}");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, Options); }
        catch (JsonException ex)
        {
            // THE POSITION, NOT THE LIBRARY'S SENTENCE: its advice ("change the reader options") is about this parser,
            // which reads one spelling on purpose — no comments, no trailing commas.
            return FeatureParse.No(
                "a feature spec is one JSON object, with no comments and no trailing commas, and this text is not JSON "
                + $"from line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return FeatureParse.No($"a feature spec is one JSON object, and this text is {Words(root.ValueKind)}");

            if (Properties(root) is not { } props) return Twice(root, "the spec");

            if (!props.TryGetValue(KindKey, out var kindValue) || Text(kindValue) is not { } kind)
                return FeatureParse.No($"a feature spec names its kind as text: {string.Join(", ", FeatureKinds.All)}");
            if (!FeatureKinds.All.Contains(kind, StringComparer.Ordinal))
                return FeatureParse.No($"'{Clip(kind)}' is not a kind of feature; the kinds are {string.Join(", ", FeatureKinds.All)}");

            var keys = KeysOf(kind);
            foreach (var name in props.Keys)
                if (!keys.Contains(name, StringComparer.Ordinal))
                    return FeatureParse.No(
                        $"'{Clip(name)}' is not a key of a {kind} feature; its keys are {string.Join(", ", keys)}, every one "
                        + "required. No key reads ahead of the instant a feature is computed at.");

            foreach (var name in keys)
                if (!props.ContainsKey(name))
                    return FeatureParse.No($"a {kind} feature names its {name}; its keys are {string.Join(", ", keys)}, every one required");

            string? mode = null;
            if (kind == FeatureKinds.Change)
            {
                mode = Text(props[ModeKey]);
                if (mode is not (FeatureKinds.Diff or FeatureKinds.Ratio))
                    return FeatureParse.No(
                        $"a change's mode is '{FeatureKinds.Diff}' (the latest minus the earlier) or '{FeatureKinds.Ratio}' "
                        + $"(the latest divided by the earlier), and this one is {Shown(props[ModeKey])}");
            }

            if (ReadInput(props[InputKey], out var why) is not { } input) return FeatureParse.No(why);

            if (Whole(props[LatencyKey], LatencyKey, 0, MaxLatencySeconds, out why) is not { } latency)
                return FeatureParse.No(why);
            if (Whole(props[MaxAgeKey], MaxAgeKey, 1, MaxSpanSeconds, out why) is not { } maxAge)
                return FeatureParse.No(why);

            long? lookback = null, window = null, minRows = null;
            if (kind == FeatureKinds.Change
                && (lookback = Whole(props[LookbackKey], LookbackKey, 1, MaxSpanSeconds, out why)) is null)
                return FeatureParse.No(why);
            if (FeatureKinds.IsWindow(kind))
            {
                if ((window = Whole(props[WindowKey], WindowKey, 1, MaxSpanSeconds, out why)) is null)
                    return FeatureParse.No(why);
                if ((minRows = Whole(props[MinRowsKey], MinRowsKey, 1, MaxMinRows, out why, "readings")) is null)
                    return FeatureParse.No(why);
            }

            return FeatureParse.Yes(new FeatureSpec(kind, mode, input, TimeSpan.FromSeconds(latency),
                TimeSpan.FromSeconds(maxAge),
                lookback is { } l ? TimeSpan.FromSeconds(l) : null,
                window is { } w ? TimeSpan.FromSeconds(w) : null,
                minRows is { } n ? (int)n : null));
        }
    }

    /// <summary>
    /// THE SERIES A FEATURE READS, CHECKED AGAINST THIS BUILD'S ROWS AND NOTHING A FILE SAYS: a source the tape reads as
    /// market numbers (<see cref="TapeSourceCatalog.MarketParsers"/> — Binance's market rows and, since <c>U-tape-chain</c>,
    /// Hyperliquid's contexts), one of its series, one of the subjects that row records
    /// (<see cref="TapeSourceCatalog.SubjectsOf"/>), and a field that is neither the series' time nor its symbol. A row
    /// <c>tape-sources.json</c> adds is not here: whether a spec parses is a fact about the build, never about a file an
    /// agent can write.
    /// </summary>
    static FeatureInput? ReadInput(JsonElement value, out string why)
    {
        why = "";
        if (value.ValueKind != JsonValueKind.Object)
        {
            why = $"a feature's input is an object naming its {string.Join(", ", InputKeys)}, and this one is {Words(value.ValueKind)}";
            return null;
        }

        if (Properties(value) is not { } props)
        {
            why = Twice(value, "the input").Why;
            return null;
        }

        foreach (var name in props.Keys)
            if (!InputKeys.Contains(name, StringComparer.Ordinal))
            {
                why = $"'{Clip(name)}' is not a key of a feature's input; an input names its {string.Join(", ", InputKeys)}, every one required";
                return null;
            }

        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in InputKeys)
        {
            if (!props.TryGetValue(name, out var v))
            {
                why = $"a feature's input names its {name}; an input names its {string.Join(", ", InputKeys)}, every one required";
                return null;
            }
            if (Text(v) is not { } s)
            {
                why = $"a feature's input names its {name} as text, and this one is {Words(v.ValueKind)}";
                return null;
            }
            text[name] = s;
        }

        var (source, series, subject, field) = (text["source"], text["series"], text["subject"], text["field"]);

        var shipped = TapeSourceCatalog.Shipped();
        var markets = shipped
            .Where(r => TapeSourceCatalog.MarketParsers.Contains(r.Parser, StringComparer.Ordinal))
            .Select(r => r.Id)
            .ToList();
        var row = shipped.FirstOrDefault(r => string.Equals(r.Id, source, StringComparison.Ordinal));
        if (row is null)
        {
            why = $"'{Clip(source)}' is not a source the tape records; a feature reads one of {string.Join(", ", markets)}";
            return null;
        }
        if (!TapeSourceCatalog.MarketParsers.Contains(row.Parser, StringComparer.Ordinal))
        {
            why = $"'{source}' is read by the parser '{row.Parser}': its rows are text, not market numbers, and a feature "
                  + $"reads only sources read by '{string.Join("' or '", TapeSourceCatalog.MarketParsers)}' — "
                  + string.Join(", ", markets);
            return null;
        }

        var entry = row.Series.FirstOrDefault(s => string.Equals(s.Id, series, StringComparison.Ordinal));
        if (entry is null)
        {
            why = $"'{Clip(series)}' is not a series of {source}; its series are {string.Join(", ", row.Series.Select(s => s.Id))}";
            return null;
        }

        // THE ROW'S OWN SUBJECTS: the universe's symbols for Binance's rows, Hyperliquid's own coin names for its contexts.
        var subjects = TapeSourceCatalog.SubjectsOf(row);
        if (!subjects.Contains(subject, StringComparer.Ordinal))
        {
            why = $"the tape records {source} for {string.Join(", ", subjects)}, and '{Clip(subject)}' is not one of them";
            return null;
        }

        if (!IsField(field))
        {
            why = $"'{Clip(field)}' is not a field name: letters, digits and '_' only, at most 64";
            return null;
        }
        if (string.Equals(field, entry.TimeField, StringComparison.Ordinal))
        {
            why = $"'{field}' is the time of {series} — the source time every reading already carries — and not a value a feature reads";
            return null;
        }
        if (!string.IsNullOrEmpty(entry.SymbolField) && string.Equals(field, entry.SymbolField, StringComparison.Ordinal))
        {
            why = $"'{field}' is the symbol of {series}, and not a value a feature reads";
            return null;
        }

        return new FeatureInput(source, series, subject, field);
    }

    /// <summary>
    /// A WHOLE NUMBER, WRITTEN AS DIGITS, WITHIN ITS BOUNDS — or null with the reason. <c>5.0</c>, <c>5e0</c> and
    /// <c>"5"</c> are refused, so a duration has one spelling; a negative latency is refused as the look-ahead it is.
    /// </summary>
    static long? Whole(JsonElement value, string key, long min, long max, out string why, string unit = "seconds")
    {
        why = "";
        if (value.ValueKind != JsonValueKind.Number)
        {
            why = $"{key} is a whole number of {unit}, written in digits, and this one is {Words(value.ValueKind)}";
            return null;
        }
        if (!value.TryGetInt64(out var n))
        {
            why = $"{key} is a whole number of {unit}, written in digits, and {Clip(value.GetRawText())} is not one this build reads";
            return null;
        }
        if (n < 0)
        {
            why = key == LatencyKey
                ? $"{key} is {n}: a negative latency would count a reading before it had arrived, which is look-ahead, and a feature cannot say it"
                : $"{key} is {n}, and it is never negative";
            return null;
        }
        if (n < min || n > max)
        {
            why = $"{key} is {n}; it is {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)} {unit}";
            return null;
        }
        return n;
    }

    /// <summary>An object's properties by name, or null when it names one twice.</summary>
    static Dictionary<string, JsonElement>? Properties(JsonElement obj)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
            if (!props.TryAdd(p.Name, p.Value)) return null;
        return props;
    }

    /// <summary>The refusal for an object that names a key twice, naming it.</summary>
    static FeatureParse Twice(JsonElement obj, string where)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var name = obj.EnumerateObject().Select(p => p.Name).First(n => !seen.Add(n));
        return FeatureParse.No(
            $"{where} names '{Clip(name)}' twice; which of the two a reader keeps depends on the reader, and a spec whose "
            + "meaning depends on who reads it is refused");
    }

    /// <summary>A JSON string's text, or null for anything else — a string that is not valid text included.</summary>
    static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>A value as a refusal shows it: its text, clipped, or what kind of value it is.</summary>
    static string Shown(JsonElement value) => Text(value) is { } s ? $"'{Clip(s)}'" : Words(value.ValueKind);

    static string Words(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        _ => "null"
    };

    /// <summary>A field name: letters, digits and '_' — the same rule the tape's catalogue keeps for its own.</summary>
    static bool IsField(string s) =>
        s.Length is >= 1 and <= 64 && s.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    /// <summary>What a refusal may echo of a text an agent wrote: forty characters, no control characters.</summary>
    static string Clip(string text, int max = 40)
    {
        var clean = new string(text.Select(c => char.IsControl(c) ? '·' : c).ToArray());
        return clean.Length <= max ? clean : clean[..max] + "…";
    }
}
