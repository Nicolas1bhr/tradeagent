using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// ONE ANSWER INTO ITEMS, OR THE REASON IT IS NOT ONE — the parser family
/// <see cref="TapeSourceCatalog.JsonParser"/>: a JSON object, or a list of them, one item each.
/// <see cref="TryReadItems"/> is the second family's, an exchange's announcements.
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
    /// ONE ANSWER OF AN EXCHANGE'S ANNOUNCEMENTS INTO ITEMS, OR THE REASON IT IS NOT ONE — the parser family
    /// <see cref="TapeSourceCatalog.AnnouncementParser"/> (<c>U-tape-events</c>).
    ///
    /// <para><b>The items are the list at <see cref="TapeSeriesEntry.ItemsPath"/></b>, every list on the way
    /// flattened. Each becomes one item: its subject the digest of its <see cref="TapeSeriesEntry.IdField"/>
    /// (<see cref="ItemSubject"/>), its source time the vendor's own <see cref="TapeSeriesEntry.TimeField"/>,
    /// its payload the item whole — so "the same announcement" is one URL at one publication time, an edit
    /// to it is a revision, and a new URL or a new time is a new key.</para>
    ///
    /// <para><b>What refuses the whole answer, in words, and stores nothing</b> — for the reason
    /// <see cref="TryRead"/> gives: a body that does not read is a recorded failure, never "the items it
    /// could read". A path the body does not hold, which is also what an error envelope looks like (OKX
    /// answers some errors with a 200 and an empty <c>data</c>, so a list that is absent is not taken for a
    /// list that is empty); an item that is not an object, has no text naming it, has no time this build can
    /// read, or is over 64 KB; and ONE KEY NAMED TWICE WITH DIFFERENT CONTENTS — the store would write the
    /// first as a revision and the second as the next one, and the same answer read again a minute later
    /// would write two more, for ever. The same item twice with the same contents is one item read twice.
    /// <b>An empty list is an empty page</b>: a delivery of nothing new, not a failure.</para>
    ///
    /// <para>No reason here quotes the vendor's text: a reason is this build's words and is read as such,
    /// and an item names itself in one by its subject, a digest, never by its URL or its title.</para>
    /// </summary>
    public static bool TryReadItems(string? body, TapeSeriesEntry series, out IReadOnlyList<TapeItem> items,
        out string? why)
    {
        ArgumentNullException.ThrowIfNull(series);
        items = [];
        why = null;

        var path = series.ItemsPath ?? "";
        var steps = path.Split('.');
        if (path.Length == 0 || steps.Any(s => s.Length == 0)) { why = "the series names no path to its items"; return false; }
        if (string.IsNullOrEmpty(series.IdField)) { why = "the series names no field that identifies an item"; return false; }
        if (string.IsNullOrWhiteSpace(body)) { why = "the body was empty"; return false; }

        var read = new List<TapeItem>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!TryList(doc.RootElement, steps, out var elements))
            {
                why = $"the answer holds no '{path}' list of items";
                return false;
            }

            foreach (var e in elements)
            {
                if (e.ValueKind != JsonValueKind.Object) { why = $"an item in '{path}' is not an object"; return false; }

                if (!e.TryGetProperty(series.IdField, out var id) || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    why = $"an item has no '{series.IdField}' naming it";
                    return false;
                }

                if (!TryTime(e, series.TimeField, out var at))
                {
                    why = $"an item has no '{series.TimeField}' this build can read as a time";
                    return false;
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
                    why = $"an item is {bytes.ToString(CultureInfo.InvariantCulture)} bytes and the tape keeps at most "
                          + $"{TapeStore.MaxPayloadBytes.ToString(CultureInfo.InvariantCulture)} per observation";
                    return false;
                }

                var subject = ItemSubject(id.GetString()!);
                var key = TapeStore.NaturalKey(subject, at);
                if (seen.TryGetValue(key, out var earlier) && !string.Equals(earlier, payload, StringComparison.Ordinal))
                {
                    why = $"the answer names the item {subject} twice at "
                          + $"{at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)} with different contents, "
                          + "and the tape cannot say which of the two the vendor meant";
                    return false;
                }
                seen[key] = payload;

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
    /// THE SUBJECT OF AN ANNOUNCEMENT: the first 32 hex characters, lower case, of the SHA-256 of the text
    /// that names it (its URL). A digest, because a URL is neither short enough nor plain enough to be half
    /// of a natural key (<see cref="TapeStore.IsSubject"/>); 128 bits, because two announcements whose URLs
    /// collided in them would be two documents nobody will ever write.
    /// </summary>
    public static string ItemSubject(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32];
    }

    /// <summary>
    /// THE ITEMS AT <paramref name="steps"/>, or false because the body does not hold them. Each step is a
    /// property every object reached so far must have, and a list reached on the way is "each of its
    /// elements"; what the last step reaches must be lists, whose elements are the items. A list on the way
    /// that is EMPTY leaves nothing to take the next step from, so the path is not there — which is how an
    /// error envelope with an empty <c>data</c> stays a failure. An empty LAST list is a page with nothing on it.
    /// </summary>
    static bool TryList(JsonElement root, string[] steps, out List<JsonElement> items)
    {
        items = [];
        List<JsonElement> reached = [root];

        foreach (var step in steps)
        {
            var next = new List<JsonElement>();
            foreach (var node in reached)
            {
                IEnumerable<JsonElement> each = node.ValueKind == JsonValueKind.Array ? node.EnumerateArray() : [node];
                foreach (var e in each)
                {
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(step, out var value)) return false;
                    next.Add(value);
                }
            }

            if (next.Count == 0) return false;
            reached = next;
        }

        foreach (var list in reached)
        {
            if (list.ValueKind != JsonValueKind.Array) return false;
            items.AddRange(list.EnumerateArray());
        }

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
