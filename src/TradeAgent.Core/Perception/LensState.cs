using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Perception;

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
