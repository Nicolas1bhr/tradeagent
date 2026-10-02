using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// ONE ANSWER INTO ITEMS, OR THE REASON IT IS NOT ONE — the parser family
/// <see cref="TapeSourceCatalog.JsonParser"/>: a JSON object, or a list of them, one item each.
///
/// <para><b>A body that does not read is a recorded failure and never an item</b> — the rule
/// <c>ForwardBars.TryParse</c> follows, for the same reason: an error page served with a 200, or an
/// answer about a symbol nobody asked for, stored as "the items it could read" would be a fetch that
/// claims a delivery it never made. So one bad item refuses the whole answer, in words, and the
/// collector writes the attempt with that reason and nothing else.</para>
///
/// <para><b>What is kept is the vendor's item, whole.</b> Every field, in the canonical spelling of
/// <see cref="TapeJson"/>, its decimal strings untouched. The subject is the symbol the item names —
/// or, for a series whose items name none (Binance's taker ratio), the symbol that was asked for. An
/// answer for every symbol at once is kept to <see cref="TapeSourceCatalog.Universe"/>; anything else
/// in it is not this installation's to record.</para>
/// </summary>
public static class TapeParse
{
    /// <param name="body">What the host answered.</param>
    /// <param name="series">The series asked for: which field is the time and which, if any, the symbol.</param>
    /// <param name="askedSymbol">The symbol the request named, or null for an answer about every symbol.</param>
    /// <param name="universe">The symbols this installation records.</param>
    public static bool TryRead(string? body, TapeSeriesEntry series, string? askedSymbol,
        IReadOnlyCollection<string> universe, out IReadOnlyList<TapeItem> items, out string? why)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(universe);
        items = [];
        why = null;

        if (string.IsNullOrWhiteSpace(body)) { why = "the body was empty"; return false; }

        var read = new List<TapeItem>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            List<JsonElement> elements;
            if (root.ValueKind == JsonValueKind.Object) elements = [root];
            else if (root.ValueKind == JsonValueKind.Array) elements = [.. root.EnumerateArray()];
            else
            {
                why = $"the body is {root.ValueKind} and an answer here is an object or a list of objects";
                return false;
            }

            foreach (var e in elements)
            {
                if (e.ValueKind != JsonValueKind.Object) { why = "an item in the body is not an object"; return false; }

                if (!TryTime(e, series.TimeField, out var at))
                {
                    why = $"an item has no '{series.TimeField}' this build can read as a time";
                    return false;
                }

                string? named = null;
                if (!string.IsNullOrEmpty(series.SymbolField))
                {
                    if (!e.TryGetProperty(series.SymbolField, out var s) || s.ValueKind != JsonValueKind.String)
                    {
                        why = $"an item has no '{series.SymbolField}' naming its symbol";
                        return false;
                    }
                    named = s.GetString();
                }

                string subject;
                if (askedSymbol is not null)
                {
                    // AN ANSWER ABOUT ANOTHER SYMBOL IS NOT AN ANSWER: refused, never filed under the one asked for.
                    if (named is not null && !string.Equals(named, askedSymbol, StringComparison.Ordinal))
                    {
                        why = $"an item is about {named}, not the {askedSymbol} that was asked for";
                        return false;
                    }
                    subject = askedSymbol;
                }
                else
                {
                    if (named is null || !universe.Contains(named, StringComparer.Ordinal)) continue;
                    subject = named;
                }

                string payload;
                try { payload = TapeJson.Canonical(e.GetRawText()); }
                catch (JsonException ex)
                {
                    why = "an item cannot be kept as one payload: " + ex.Message.ReplaceLineEndings(" ");
                    return false;
                }

                var bytes = Encoding.UTF8.GetByteCount(payload);
                if (bytes > TapeStore.MaxPayloadBytes)
                {
                    why = $"an item is {bytes} bytes and the tape keeps at most {TapeStore.MaxPayloadBytes} per observation";
                    return false;
                }

                read.Add(new TapeItem(subject, at, payload));
            }
        }
        catch (JsonException ex)
        {
            why = "the body is not JSON: " + ex.Message.ReplaceLineEndings(" ");
            return false;
        }

        items = read;
        return true;
    }

    /// <summary>
    /// The item's time, from a JSON number or a numeric string, in milliseconds or microseconds by its
    /// magnitude (<see cref="KlineNormaliser.TryUnitOf"/>, the reading the archive and the forward bars
    /// already take), and refused as a time at all when it is neither.
    /// </summary>
    static bool TryTime(JsonElement item, string field, out DateTimeOffset at)
    {
        at = default;
        if (string.IsNullOrEmpty(field) || !item.TryGetProperty(field, out var e)) return false;

        long raw;
        if (e.ValueKind == JsonValueKind.Number) { if (!e.TryGetInt64(out raw)) return false; }
        else if (e.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(e.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out raw)) return false;
        }
        else return false;

        if (KlineNormaliser.TryUnitOf(raw) is not { } unit) return false;
        at = unit == KlineTimeUnit.Milliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
            : DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).AddTicks(raw % 1000L * 10L);
        return true;
    }
}
