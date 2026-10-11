using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Decisions;

namespace TradeAgent.Core.Perception;

/// <summary>
/// ONE LENS'S QUESTION ABOUT ONE OBSERVATION, READY TO ASK: the registered lens, the state code built, the one source it
/// was built from, the port's schema reference and the questions — every part of a call but the instrument, which is
/// the owner's.
/// </summary>
public sealed record LensAsk(LensRecord Lens, string State, IReadOnlyList<string> Sources, SchemaRef Schema,
    IReadOnlyDictionary<string, DecisionQuestion> Questions)
{
    /// <summary>The call to <paramref name="instrument"/>: everything in it from here, nothing from its caller but the instrument.</summary>
    public DecisionRequest For(string instrument) => new()
    {
        Instrument = instrument,
        Schema = Schema,
        State = State,
        Questions = Questions,
        Sources = Sources
    };
}

/// <summary>A lens's question about an observation, or the reason there is none, in words. Never both, never neither.</summary>
public sealed record LensStateResult(LensAsk? Ask, string? Refusal);

/// <summary>
/// THE STATE A LENS ASKS ABOUT, BUILT BY CODE FROM ONE OBSERVATION OF ITS TARGET (<c>U-lenses</c>; EDGE § 6 item 4:
/// "a state is built by code from declared fields") — the declared fields of the item, each cut to its cap, and the
/// facts code computes: the source, the series, the subject, the vendor's source time, the arrival instant, the age at
/// arrival, the evidence class, which fields were cut, and the candidate assets matched against this build's universe.
///
/// <para><b>Pure and deterministic</b>: the same lens and the same observation are the same state, byte for byte, so
/// the tape's store can rebuild it when the call is recorded and refuse a record whose state is anything else. No text
/// from the lens's proposer enters a state — only the item's own fields and code facts.</para>
///
/// <para><b>Its sources are built here and taken from no caller</b>: exactly one, <c>tape_obs:&lt;id&gt;</c>, the
/// observation it was built from.</para>
/// </summary>
public static class LensState
{
    /// <summary>The one source reference of a lens's call: the observation the state was built from.</summary>
    public static string SourceOf(long observationId) =>
        "tape_obs:" + observationId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The observation a source reference names, or null because it names none — a reference that is not exactly
    /// <c>tape_obs:</c> and a positive whole number.
    /// </summary>
    public static long? ObservationOf(string? source)
    {
        const string prefix = "tape_obs:";
        if (source is null || !source.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var digits = source[prefix.Length..];
        return digits.Length is >= 1 and <= 18 && digits.All(char.IsAsciiDigit) && digits[0] != '0'
            ? long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// THE QUESTION <paramref name="lens"/> ASKS ABOUT <paramref name="observation"/>, or why it asks none: an observation
    /// that is not there, one of another source or series than the lens's target, one the tape's screen quarantines and
    /// one whose payload a read withheld are each refused in words — so a quarantined item never reaches a state, and a
    /// state is never built from a payload its reader was not served.
    /// </summary>
    public static LensStateResult For(LensRecord lens, TapeObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(lens);
        if (lens.Parsed is not { } parsed)
            return new(null, $"the lens '{lens.Id}' version {lens.Version} is not one this build's validator ({Lens.ValidatorVersion}) admits, so it asks nothing");
        if (observation is null) return new(null, "there is no observation to ask about");
        if (observation.Source != parsed.Target.Source || observation.Series != parsed.Target.Series)
            return new(null, $"tape_obs:{observation.Id} is a '{observation.Source}/{observation.Series}' reading, and the lens "
                             + $"'{lens.Id}' looks only at '{parsed.Target.Source}/{parsed.Target.Series}'");
        if (observation.Quarantine is { } q)
            return new(null, $"tape_obs:{observation.Id} is quarantined by the tape's screen (rule '{q.Rule}', version {q.Version}), "
                             + "so no state is built from it");
        if (observation.Payload is not { } payload)
            return new(null, $"tape_obs:{observation.Id}'s payload was withheld from this read, so no state is built from it");

        IReadOnlyDictionary<string, string?> values;
        try { values = Values(parsed, payload); }
        catch (JsonException) { return new(null, $"tape_obs:{observation.Id}'s payload does not read as JSON, so no state is built from it"); }

        var state = Build(parsed, observation.Subject, observation.SourceTime, observation.ReceivedAt, observation.EvidenceClass, values);
        return new(new LensAsk(lens, state, [SourceOf(observation.Id)], parsed.Schema(lens.Version), parsed.Questions), null);
    }

    /// <summary>
    /// The question about what an as-of read served — or the read's own refusal, when a holdout window withheld the row,
    /// and "nothing had arrived" when there was none.
    /// </summary>
    public static LensStateResult For(LensRecord lens, TapeAsOf read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.Row is null)
            return new(null, read.Refusal is { } held ? "the read was refused, so no state is built: " + held : "nothing had arrived to ask about");
        return For(lens, read.Row);
    }

    /// <summary>
    /// THE MOST BYTES A STATE OF <paramref name="lens"/> CAN HOLD: the builder's own output for the worst observation —
    /// every field at its cap in a character that escapes to six bytes, the longest subject a tape row can have, the
    /// longest age and every candidate asset. What a lens's parse checks against the port's budgets, so a registered lens
    /// never builds a state its call would be refused for.
    /// </summary>
    public static long CapBytes(Lens lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        var worst = lens.Fields.ToDictionary(f => f.Path, f => (string?)new string('\u0001', f.MaxChars + 1), StringComparer.Ordinal);
        var assets = string.Join(' ', Assets.SelectMany(a => a.Words));
        // The assets ride on a field's text in a real item; here they are counted on their own, whole.
        var state = Build(lens, new string('x', 40), DateTimeOffset.MaxValue, DateTimeOffset.MinValue, TapeClass.Hind,
            worst, assets);
        return Encoding.UTF8.GetByteCount(state);
    }

    // ------------------------------------------------------------------------------------------------ the builder

    /// <summary>The coins a candidate asset is matched for: this build's universe, with the names the press uses for them.</summary>
    internal static readonly IReadOnlyList<(string Symbol, string[] Words)> Assets =
    [
        ("BTCUSDT", ["btc", "bitcoin", "xbt"]),
        ("ETHUSDT", ["eth", "ethereum", "ether"]),
        ("SOLUSDT", ["sol", "solana"]),
        ("BNBUSDT", ["bnb"]),
        ("XRPUSDT", ["xrp", "ripple"]),
        ("DOGEUSDT", ["doge", "dogecoin"])
    ];

    /// <summary>
    /// THE STATE, CANONICAL. <paramref name="values"/> are the declared fields' texts as the item carries them (null where
    /// it carries none); each is cut to its cap, never split inside a surrogate pair. <paramref name="extra"/> is counted
    /// in the asset match and written nowhere — <see cref="CapBytes"/>' worst case.
    /// </summary>
    internal static string Build(Lens lens, string subject, DateTimeOffset sourceTime, DateTimeOffset receivedAt,
        string evidenceClass, IReadOnlyDictionary<string, string?> values, string extra = "")
    {
        var cut = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var truncated = new List<string>();
        foreach (var f in lens.Fields)
        {
            values.TryGetValue(f.Path, out var v);
            if (v is not null && v.Length > f.MaxChars)
            {
                var n = char.IsHighSurrogate(v[f.MaxChars - 1]) ? f.MaxChars - 1 : f.MaxChars;
                v = v[..n];
                truncated.Add(f.Path);
            }
            cut[f.Path] = v;
        }

        var words = Words(string.Join(' ', cut.Values.Where(v => v is not null)) + " " + extra);
        var assets = Assets.Where(a => a.Words.Any(words.Contains)).Select(a => a.Symbol).ToList();

        var age = (long)Math.Floor((receivedAt - sourceTime).TotalSeconds);

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("age_seconds_at_arrival", age);
            w.WriteStartArray("candidate_assets");
            foreach (var a in assets) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteString("evidence_class", evidenceClass);
            w.WriteStartObject("fields");
            foreach (var (path, v) in cut)
            {
                if (v is null) w.WriteNull(path);
                else w.WriteString(path, v);
            }
            w.WriteEndObject();
            w.WriteString("received_at", Sql.T(receivedAt));
            w.WriteString("series", lens.Target.Series);
            w.WriteString("source", lens.Target.Source);
            w.WriteString("source_time", Sql.T(sourceTime));
            w.WriteString("subject", subject);
            w.WriteStartArray("truncated");
            foreach (var t in truncated) w.WriteStringValue(t);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return TapeJson.Canonical(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>The text's words, lower-case, split at everything that is not a letter or a digit.</summary>
    static HashSet<string> Words(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); continue; }
            if (sb.Length > 0) { set.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) set.Add(sb.ToString());
        return set;
    }

    /// <summary>One field of a payload as text: a string as it is, any other value as its JSON, absent as null.</summary>
    internal static IReadOnlyDictionary<string, string?> Values(Lens lens, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var f in lens.Fields)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(f.Path, out var v)
                || v.ValueKind == JsonValueKind.Null)
                values[f.Path] = null;
            else values[f.Path] = v.ValueKind == JsonValueKind.String ? v.GetString() : TapeJson.Canonical(v.GetRawText());
        }
        return values;
    }
}
