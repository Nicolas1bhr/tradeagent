using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>What one change does to the authority a policy grants: nothing, less, or more.</summary>
public enum BoardDirectionKind
{
    /// <summary>The value is the same — not a change at all.</summary>
    Unchanged,

    /// <summary>Less authority, or the same: one seat applies it at once.</summary>
    Narrows,

    /// <summary>More authority — or a field this build has no direction for: two seats, a proposer and a confirmer.</summary>
    Widens
}

/// <summary>
/// ONE CHANGE OF AN INTENT: a field of the policy's body by its path (<c>risk.max_order_quantity</c>), the value it holds now as the
/// act claims it, and the value it is to hold — each the canonical JSON text of a value of the field's own type.
/// </summary>
public sealed record BoardChange(string Field, string From, string To);

/// <summary>
/// A BOARD ACT'S EXACT INTENT (<c>U-board-b1</c>; R24 § 4.2, VISION § 6.2 — "a decision binds to the exact intent it authorised"):
/// 1 to <see cref="MaxChanges"/> typed changes over the fields <see cref="BoardDirection"/> has a direction for, STRICTLY parsed —
/// an unknown field, <c>live_activated</c>, a live mode, a value outside the field's type, a change that changes nothing or a field
/// named twice is refused, never read on a guess — and written ONE way: the changes in their fields' ordinal order, each value
/// normalised to the spelling the policy's body uses, the whole canonical JSON (<see cref="TapeJson.Canonical"/>). <see cref="Sha256"/>
/// is that text's hash, which a confirmation names. Pure: no store is read here.
///
/// <para><b>What no intent can name</b> (ORDER-001, SIGNAL-001, EVID-001): an order, its price, its time or its size, and any evidence
/// setting — the fields are the policy's, which holds none (a guard reflects over both).</para>
/// </summary>
public sealed record BoardIntent(IReadOnlyList<BoardChange> Changes, string Canonical, string Sha256)
{
    /// <summary>The most changes one act carries.</summary>
    public const int MaxChanges = 10;

    /// <summary>The largest intent text, in UTF-8 bytes (the store's CHECK is wider).</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>What the whole intent does: <see cref="BoardDirectionKind.Narrows"/> only when EVERY change narrows (or leaves alone).</summary>
    public BoardDirectionKind Direction =>
        Changes.Any(c => BoardDirection.Of(c.Field, c.From, c.To) == BoardDirectionKind.Widens)
            ? BoardDirectionKind.Widens
            : BoardDirectionKind.Narrows;

    /// <summary>Whether one seat may apply it: every change narrows.</summary>
    public bool Narrows => Direction == BoardDirectionKind.Narrows;

    /// <summary>
    /// THE STRICT READ: the intent <paramref name="json"/> states — a JSON list of objects with exactly <c>field</c>, <c>from</c> and
    /// <c>to</c> — or the sentence refusing it. Nothing is defaulted, cut or filled in.
    /// </summary>
    public static (BoardIntent? Intent, string? Why) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, "an intent is a list of changes, each {\"field\", \"from\", \"to\"}, and none was given");
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) return (null, $"an intent is at most {MaxBytes:N0} bytes");
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException ex) { return (null, $"the intent is not JSON ({ex.Message})"); }
        if (root.ValueKind != JsonValueKind.Array) return (null, "an intent is a JSON list of changes, each {\"field\", \"from\", \"to\"}");
        var count = root.GetArrayLength();
        if (count is 0 or > MaxChanges) return (null, $"an intent carries 1 to {MaxChanges} changes, and this one carries {count}");

        var changes = new List<BoardChange>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return (null, "each change is an object {\"field\", \"from\", \"to\"}");
            var keys = item.EnumerateObject().Select(p => p.Name).ToList();
            if (keys.Count != 3 || !keys.Contains("field") || !keys.Contains("from") || !keys.Contains("to"))
                return (null, "each change has exactly 'field', 'from' and 'to', and nothing else");
            var fieldElement = item.GetProperty("field");
            if (fieldElement.ValueKind != JsonValueKind.String) return (null, "a change's 'field' is the path of a policy field, as text");
            var field = fieldElement.GetString()!;
            if (field == BoardDirection.Chief)
            {
                // THE CHIEF (U-org-chief item 3): its own types — the sitting chief's id or null, a seat or null — read as strictly.
                var (chiefFrom, chiefFromWhy) = BoardDirection.ChiefFrom(item.GetProperty("from"));
                if (chiefFrom is null) return (null, $"'{field}' from: {chiefFromWhy}");
                var (chiefTo, chiefToWhy) = BoardDirection.ChiefTo(item.GetProperty("to"));
                if (chiefTo is null) return (null, $"'{field}' to: {chiefToWhy}");
                if (BoardDirection.Of(field, chiefFrom, chiefTo) == BoardDirectionKind.Unchanged)
                    return (null, $"'{field}' would hold what it holds: a change that changes nothing is no change");
                if (changes.Any(c => c.Field == field)) return (null, $"'{field}' is named twice: one change per field");
                changes.Add(new BoardChange(field, chiefFrom, chiefTo));
                continue;
            }
            if (BoardDirection.Refusal(field) is { } notAField) return (null, notAField);
            var spec = BoardDirection.Spec(field)!;
            var (from, fromWhy) = BoardDirection.Normalise(spec, item.GetProperty("from"));
            if (from is null) return (null, $"'{field}' from: {fromWhy}");
            var (to, toWhy) = BoardDirection.Normalise(spec, item.GetProperty("to"));
            if (to is null) return (null, $"'{field}' to: {toWhy}");
            if (BoardDirection.Of(field, from, to) == BoardDirectionKind.Unchanged)
                return (null, $"'{field}' would hold what it holds: a change that changes nothing is no change");
            if (changes.Any(c => c.Field == field)) return (null, $"'{field}' is named twice: one change per field");
            changes.Add(new BoardChange(field, from, to));
        }
        if (changes.Count > 1 && changes.Any(c => c.Field == BoardDirection.Chief))
            return (null, $"a change of '{BoardDirection.Chief}' is an act of its own: it is never mixed with the policy's fields");
        return (Of(changes), null);
    }

    /// <summary>
    /// THE ONE SPELLING of changes already normalised (what <see cref="Parse"/> read, or what the app composes): sorted by field,
    /// canonical, hashed. Every value must already be a value of its field's type.
    /// </summary>
    public static BoardIntent Of(IEnumerable<BoardChange> changes)
    {
        var sorted = changes.OrderBy(c => c.Field, StringComparer.Ordinal).ToList();
        var array = new JsonArray();
        foreach (var c in sorted)
            array.Add(new JsonObject { ["field"] = c.Field, ["from"] = JsonNode.Parse(c.From), ["to"] = JsonNode.Parse(c.To) });
        var canonical = TapeJson.Canonical(array.ToJsonString());
        return new BoardIntent(sorted, canonical, Sha256Hex.Of(canonical));
    }
}

/// <summary>
/// THE DIRECTION OF EVERY CHANGE, IN ONE TABLE (<c>U-board-b1</c>; R24 § 4.2: "narrowing takes one seat; widening and money take two").
/// Each field the board may change is listed with how its values order by the authority they grant — WIDER IS NOT ALWAYS LARGER, and
/// zero means something different on different fields, each read exactly as the code that enforces the field reads it:
/// <list type="bullet">
/// <item>a quantity, count, allowance, lane or the plan's day-total: LOWER narrows (zero refuses all, so larger is always wider);</item>
/// <item>the notional cap, both loss budgets and the valuation exit: lower narrows, but ZERO is "not enforced" — the widest value each
/// has (<see cref="RiskPolicy.Widenings"/>'s own reading);</item>
/// <item>the loss closure and the strike window, and each re-wake floor: HIGHER narrows (zero is no closure, no strike rule, no floor —
/// the widest);</item>
/// <item>the review interval and the head's ceiling: HIGHER narrows, and ZERO is none at all — no review tick, no forced wake — the
/// NARROWEST value each has (<c>MissionLoop</c> and <c>AppHost.LevelCeilings</c>);</item>
/// <item>the turn allowance: a figure at or below zero is the app's default, and is compared as that default (<see cref="TurnAllowance.From"/>);</item>
/// <item>the allowlist narrows only by removal; the shares only if no role's share — an absent or invalid one read as the equal
/// share <see cref="BoardPolicy.ShareForRole"/> gives it — rises; the envelope to none, or on the same venue, account and instrument
/// with no ceiling, count or life larger (no expiry is the longest); PAPER to OBSERVE.</item>
/// </list>
/// A FIELD NOT IN THE TABLE, OR ANY CHANGE THAT IS NOT A NARROWING, IS A WIDENING: this table only ever lets one seat through when it
/// can show less authority. Pure; read by the store, the veto and the guards.
/// </summary>
public static class BoardDirection
{
    /// <summary>How a field's values order by the authority they grant.</summary>
    public enum Order
    {
        /// <summary>OBSERVE, then PAPER; a live mode is refused at the parse and ranks widest here.</summary>
        Mode,

        /// <summary>Lower narrows; zero refuses all.</summary>
        Lower,

        /// <summary>Lower narrows, but zero is "not enforced" — the widest.</summary>
        LowerZeroUnenforced,

        /// <summary>Higher narrows; zero is the widest.</summary>
        Higher,

        /// <summary>Higher narrows; zero is "none at all" — the narrowest.</summary>
        HigherZeroNone,

        /// <summary>Lower narrows; at or below zero is the app's default figure.</summary>
        Allowance,

        /// <summary>Narrows only by removal.</summary>
        Allowlist,

        /// <summary>Narrows only if no role's effective share rises.</summary>
        Shares,

        /// <summary>Narrows only if no level's effective floor falls.</summary>
        Floors,

        /// <summary>Narrows to none, or to the same venue, account and instrument with nothing larger.</summary>
        Envelope
    }

    /// <summary>The number a field holds.</summary>
    public enum Number { None, Decimal, Int, Long }

    /// <summary>One row of the table: the field's path, its order, the number it holds, the least value an intent may give it, and its default where one stands in.</summary>
    public sealed record FieldSpec(string Path, Order Order, Number Number, decimal Least, long? Default = null);

    /// <summary>
    /// THE CHIEF, AS AN ACT NAMES IT (<c>U-org-chief</c> item 3; R24 §§ 4.1, Q12: "the board appoints by a two-seat act"): no field of the
    /// policy but the chart's root head — <c>from</c> the sitting chief's position id or null, <c>to</c> a seat
    /// (<c>{"runtime", "model"}</c>) or null — ONE change, an act of its own, never mixed with the policy's fields. EVERY change of it
    /// WIDENS (<see cref="Of"/>): an appointment, a replacement and a removal each take two seats, a proposer and a confirmer; only the
    /// owner removes a chief alone, in-process. The confirmation applies it through the chart's one writer and writes no policy version.
    /// </summary>
    public const string Chief = "org.chief";

    /// <summary>The <c>from</c> of a change of the chief, normalised: <c>null</c> (no chief sits) or a position id's JSON string — or why not.</summary>
    public static (string? Value, string? Why) ChiefFrom(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return ("null", null);
        if (e.ValueKind != JsonValueKind.String || e.GetString() is not { } id || !id.StartsWith(OrgIds.PositionPrefix, StringComparison.Ordinal)
            || !CouncilRoles.IsPositionId(id))
            return (null, $"the sitting chief is named by its position id ({OrgIds.PositionPrefix}…), or null when no chief sits");
        return (JsonSerializer.Serialize(id), null);
    }

    /// <summary>
    /// The <c>to</c> of a change of the chief, normalised: <c>null</c> (remove the chief) or exactly <c>{"model", "runtime"}</c> — a runtime
    /// id of 1 to 64 lower-case letters, digits and <c>. - _</c>, a model of 1 to 128 letters, digits and <c>. - _ : /</c> — or why not.
    /// Whether this host knows the runtime is the store's question, asked at the act.
    /// </summary>
    public static (string? Value, string? Why) ChiefTo(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return ("null", null);
        if (e.ValueKind != JsonValueKind.Object) return (null, "a chief's seat is {\"runtime\", \"model\"}, or null to remove the chief");
        var keys = e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        if (!keys.SequenceEqual(["model", "runtime"], StringComparer.Ordinal))
            return (null, "a chief's seat has exactly 'runtime' and 'model', and nothing else");
        if (e.GetProperty("runtime") is not { ValueKind: JsonValueKind.String } rt || rt.GetString() is not { Length: > 0 and <= 64 } runtime
            || !runtime.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '.' or '-' or '_'))
            return (null, "a chief's runtime is named in 1 to 64 lower-case letters, digits and . - _");
        if (e.GetProperty("model") is not { ValueKind: JsonValueKind.String } md || md.GetString() is not { Length: > 0 and <= 128 } model
            || !model.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '/'))
            return (null, "a chief's model is named in 1 to 128 letters, digits and . - _ : /");
        return (Canon(new JsonObject { ["model"] = model, ["runtime"] = runtime }.ToJsonString()), null);
    }

    /// <summary>A change of the chief, read: the sitting chief it names (null: none) and the seat it appoints (null: the chief removed).</summary>
    public static (string? From, ChiefSeat? To) ReadChief(BoardChange change)
    {
        if (change.Field != Chief) throw new ArgumentException($"'{change.Field}' is no change of the chief", nameof(change));
        var from = Element(change.From);
        var to = Element(change.To);
        return (from.ValueKind == JsonValueKind.Null ? null : from.GetString(),
            to.ValueKind == JsonValueKind.Null ? null : new ChiefSeat(to.GetProperty("runtime").GetString()!, to.GetProperty("model").GetString()!));
    }

    /// <summary>THE TABLE — every field of the policy one board act may change, and nothing else (the chief, <see cref="Chief"/>, is the chart's).</summary>
    public static readonly IReadOnlyList<FieldSpec> Fields =
    [
        new("mode", Order.Mode, Number.None, 0),
        new("risk.max_order_quantity", Order.Lower, Number.Decimal, 0),
        new("risk.max_notional_per_order", Order.LowerZeroUnenforced, Number.Decimal, 0),
        new("risk.max_open_positions", Order.Lower, Number.Int, 0),
        new("risk.max_orders_per_minute", Order.Lower, Number.Int, 0),
        new("risk.instrument_allowlist", Order.Allowlist, Number.None, 0),
        new("risk.max_loss_per_trade", Order.LowerZeroUnenforced, Number.Decimal, 0),
        new("risk.max_daily_loss", Order.LowerZeroUnenforced, Number.Decimal, 0),
        new("risk.loss_min_closure_hours", Order.Higher, Number.Decimal, 0),
        new("risk.loss_strike_window_days", Order.Higher, Number.Int, 0),
        new("risk.valuation_loss_exit_minutes", Order.LowerZeroUnenforced, Number.Decimal, 0),
        new("plan_day_total", Order.Lower, Number.Decimal, 0),
        new("role_shares", Order.Shares, Number.None, 0),
        new("turn_allowance_input", Order.Allowance, Number.Long, 1, TurnAllowance.Default.InputTokens),
        new("turn_allowance_output", Order.Allowance, Number.Long, 1, TurnAllowance.Default.OutputTokens),
        new("lanes", Order.Lower, Number.Int, 1),
        new("lanes_per_pool", Order.Lower, Number.Int, 1),
        new("lanes_per_cell_class", Order.Lower, Number.Int, 1),
        new("review_minutes", Order.HigherZeroNone, Number.Int, 0),
        new("head_ceiling_hours", Order.HigherZeroNone, Number.Int, 0),
        new("rewake_floor_minutes", Order.Floors, Number.None, 0),
        new("paper_envelope", Order.Envelope, Number.None, 0)
    ];

    /// <summary>The row for <paramref name="field"/>, or null — and then every change of it is a widening, and no intent may name it.</summary>
    public static FieldSpec? Spec(string? field) => Fields.FirstOrDefault(f => string.Equals(f.Path, field, StringComparison.Ordinal));

    /// <summary>Why <paramref name="field"/> cannot be named in an intent, or null when it can.</summary>
    public static string? Refusal(string field) => field switch
    {
        "live_activated" => "real money is switched on by no act of this build: 'live_activated' is not a field an intent may name",
        _ when field.StartsWith("paper_envelope.", StringComparison.Ordinal) =>
            $"'{field}' is part of the envelope, which an act changes whole: name 'paper_envelope' with its terms, or null for none",
        _ when Spec(field) is null =>
            $"'{field}' is no field the board's acts change: {string.Join(", ", Fields.Select(f => f.Path).Append(Chief))}",
        _ => null
    };

    /// <summary>
    /// THE DIRECTION OF ONE CHANGE of <paramref name="field"/> from <paramref name="from"/> to <paramref name="to"/> (canonical JSON
    /// values of the field's type). A field not in the table WIDENS.
    /// </summary>
    public static BoardDirectionKind Of(string field, string from, string to)
    {
        // THE CHIEF: every change of it widens — two seats, its removal included (U-org-chief; the orchestrator's Q2).
        if (field == Chief) return SameValue(from, to) ? BoardDirectionKind.Unchanged : BoardDirectionKind.Widens;
        if (Spec(field) is not { } spec) return BoardDirectionKind.Widens;
        JsonElement a, b;
        try { (a, b) = (Element(from), Element(to)); }
        catch (JsonException) { return BoardDirectionKind.Widens; }
        if (Same(a, b)) return BoardDirectionKind.Unchanged;
        try { return Wider(spec, a, b) ? BoardDirectionKind.Widens : BoardDirectionKind.Narrows; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            return BoardDirectionKind.Widens;
        }
    }

    /// <summary>
    /// THE NARROWER OF TWO VALUES of <paramref name="field"/> — the owner's veto's rule (R24 Q2): a value no wider than EITHER, so that
    /// restoring it never widens what is in force. On an order where neither is narrower (two allowlists that each name what the other
    /// does not; shares or floors that each move the other way; two envelopes on one venue that each allow more of something), it is
    /// the meet of both: the instruments both allow, each role's lower share, each level's higher floor, each ceiling's lower figure —
    /// and none at all for envelopes on two different venues. Canonical JSON.
    /// </summary>
    public static string NarrowerOf(string field, string a, string b)
    {
        var spec = Spec(field) ?? throw new ArgumentException($"'{field}' is no field the board's acts change", nameof(field));
        var (x, y) = (Element(a), Element(b));
        if (Same(x, y)) return Canon(a);
        switch (spec.Order)
        {
            case Order.Allowlist:
                if (!Wider(spec, x, y)) return Canon(b);
                if (!Wider(spec, y, x)) return Canon(a);
                var allowedByB = y.EnumerateArray().Select(e => e.GetString()!).ToList();
                var both = new JsonArray();
                foreach (var i in x.EnumerateArray().Select(e => e.GetString()!))
                    if (allowedByB.Any(j => string.Equals(i, j, StringComparison.OrdinalIgnoreCase))) both.Add(i);
                return Canon(both.ToJsonString());
            case Order.Shares:
            {
                var result = new JsonObject();
                foreach (var key in Keys(x, y))
                {
                    var pickA = Share(x, key) <= Share(y, key);
                    var from = pickA ? x : y;
                    if (from.TryGetProperty(key, out var v)) result[key] = JsonNode.Parse(v.GetRawText());
                }
                return Canon(result.ToJsonString());
            }
            case Order.Floors:
            {
                var result = new JsonObject();
                foreach (var key in Keys(x, y))
                {
                    var pickA = Floor(x, key) >= Floor(y, key);
                    var from = pickA ? x : y;
                    if (from.TryGetProperty(key, out var v)) result[key] = JsonNode.Parse(v.GetRawText());
                }
                return Canon(result.ToJsonString());
            }
            case Order.Envelope:
            {
                if (!Wider(spec, x, y)) return Canon(b);
                if (!Wider(spec, y, x)) return Canon(a);
                if (x.ValueKind == JsonValueKind.Null || y.ValueKind == JsonValueKind.Null) return "null";
                if (!SamePair(x, y)) return "null";
                var days = (Days(x), Days(y)) switch
                {
                    (null, var d) => d,
                    (var d, null) => d,
                    var (dx, dy) => Math.Min(dx!.Value, dy!.Value)
                };
                var meet = new JsonObject
                {
                    ["account"] = x.GetProperty("account").GetString(),
                    ["connector"] = x.GetProperty("connector").GetString(),
                    ["days"] = days is { } dd ? JsonValue.Create(dd) : null,
                    ["deployments"] = Math.Min(x.GetProperty("deployments").GetInt32(), y.GetProperty("deployments").GetInt32()),
                    ["instrument"] = x.GetProperty("instrument").GetString(),
                    ["max_notional"] = JsonNode.Parse(NotionalRank(x) <= NotionalRank(y)
                        ? x.GetProperty("max_notional").GetRawText() : y.GetProperty("max_notional").GetRawText()),
                    ["max_quantity"] = JsonNode.Parse(x.GetProperty("max_quantity").GetDecimal() <= y.GetProperty("max_quantity").GetDecimal()
                        ? x.GetProperty("max_quantity").GetRawText() : y.GetProperty("max_quantity").GetRawText())
                };
                return Canon(meet.ToJsonString());
            }
            default:
                // A TOTAL ORDER: whichever is not the wider.
                return Wider(spec, x, y) ? Canon(a) : Canon(b);
        }
    }

    /// <summary>
    /// A VALUE OF THE FIELD'S TYPE, NORMALISED to the spelling the policy's body writes, or the sentence refusing it: a number of the
    /// field's kind at or above its least value (never a string, never a fraction for a count), a mode that is OBSERVE or PAPER, a
    /// list of distinct instrument names, shares that are fractions, floors for the levels there are, an envelope with every term.
    /// </summary>
    public static (string? Value, string? Why) Normalise(FieldSpec spec, JsonElement e)
    {
        switch (spec.Order)
        {
            case Order.Mode:
                if (e.ValueKind != JsonValueKind.String) return (null, "a mode is OBSERVE or PAPER, as text");
                var mode = e.GetString();
                if (mode is nameof(TradingMode.LIVE_CONFIRM) or nameof(TradingMode.LIVE_AUTONOMOUS))
                    return (null, $"{mode} is a real-money mode, and no act of this build selects one");
                if (mode is not (nameof(TradingMode.OBSERVE) or nameof(TradingMode.PAPER))) return (null, $"'{mode}' is no mode: OBSERVE or PAPER");
                return (JsonSerializer.Serialize(mode), null);
            case Order.Allowlist:
            {
                if (e.ValueKind != JsonValueKind.Array) return (null, "an allowlist is a list of instrument names");
                var names = new List<string>();
                foreach (var i in e.EnumerateArray())
                {
                    if (i.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(i.GetString()) || i.GetString()!.Length > 64)
                        return (null, "each instrument is a name of 1 to 64 characters");
                    if (names.Any(n => string.Equals(n, i.GetString(), StringComparison.OrdinalIgnoreCase)))
                        return (null, $"'{i.GetString()}' is named twice");
                    names.Add(i.GetString()!);
                }
                if (names.Count > 200) return (null, "an allowlist names at most 200 instruments");
                return (Canon(JsonSerializer.Serialize(names)), null);
            }
            case Order.Shares:
            {
                if (e.ValueKind != JsonValueKind.Object) return (null, "the shares are an object of role to fraction");
                var shares = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name.Length is 0 or > 64) return (null, "a role is named in 1 to 64 characters");
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDecimal(out var s) || s <= 0m || s > 1m)
                        return (null, $"{p.Name}'s share is a fraction above 0 and at most 1");
                    shares[p.Name] = s;
                }
                if (shares.Count > 50) return (null, "at most 50 shares");
                return (Canon(JsonSerializer.Serialize(shares)), null);
            }
            case Order.Floors:
            {
                if (e.ValueKind != JsonValueKind.Object) return (null, "the floors are an object of level to minutes");
                var floors = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    if (!RewakeFloors.DefaultMinutes.ContainsKey(p.Name))
                        return (null, $"'{p.Name}' is no level: {string.Join(", ", RewakeFloors.DefaultMinutes.Keys)}");
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var m) || m < 0)
                        return (null, $"{p.Name}'s floor is a whole number of minutes, 0 or more");
                    floors[p.Name] = m;
                }
                return (Canon(JsonSerializer.Serialize(floors)), null);
            }
            case Order.Envelope:
            {
                if (e.ValueKind == JsonValueKind.Null) return ("null", null);
                if (e.ValueKind != JsonValueKind.Object) return (null, "an envelope is null, or its seven terms");
                string[] keys = ["account", "connector", "days", "deployments", "instrument", "max_notional", "max_quantity"];
                var given = e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
                if (!given.SequenceEqual(keys, StringComparer.Ordinal)) return (null, $"an envelope has exactly {string.Join(", ", keys)}");
                foreach (var k in new[] { "account", "connector", "instrument" })
                    if (e.GetProperty(k).ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetProperty(k).GetString())
                        || e.GetProperty(k).GetString()!.Length > 64)
                        return (null, $"the envelope's {k} is a name of 1 to 64 characters");
                foreach (var k in new[] { "max_quantity", "max_notional" })
                    if (e.GetProperty(k).ValueKind != JsonValueKind.Number || !e.GetProperty(k).TryGetDecimal(out var d) || d <= 0m)
                        return (null, $"the envelope's {k} is a number above 0");
                if (e.GetProperty("deployments").ValueKind != JsonValueKind.Number || !e.GetProperty("deployments").TryGetInt32(out var n) || n < 1)
                    return (null, "the envelope's deployments are a whole number, 1 or more");
                var daysElement = e.GetProperty("days");
                int? days = null;
                if (daysElement.ValueKind != JsonValueKind.Null)
                {
                    if (daysElement.ValueKind != JsonValueKind.Number || !daysElement.TryGetInt32(out var dd) || dd < 1)
                        return (null, "the envelope's days are a whole number, 1 or more, or null for no expiry");
                    days = dd;
                }
                var body = new JsonObject
                {
                    ["account"] = e.GetProperty("account").GetString(),
                    ["connector"] = e.GetProperty("connector").GetString(),
                    ["days"] = days is { } v ? JsonValue.Create(v) : null,
                    ["deployments"] = n,
                    ["instrument"] = e.GetProperty("instrument").GetString(),
                    ["max_notional"] = JsonValue.Create(e.GetProperty("max_notional").GetDecimal()),
                    ["max_quantity"] = JsonValue.Create(e.GetProperty("max_quantity").GetDecimal())
                };
                return (Canon(body.ToJsonString()), null);
            }
            default:
                if (e.ValueKind != JsonValueKind.Number) return (null, "a number, never text");
                switch (spec.Number)
                {
                    case Number.Int when e.TryGetInt32(out var i) && i >= spec.Least:
                        return (JsonSerializer.Serialize(i), null);
                    case Number.Long when e.TryGetInt64(out var l) && l >= spec.Least:
                        return (JsonSerializer.Serialize(l), null);
                    case Number.Decimal when e.TryGetDecimal(out var d) && d >= spec.Least:
                        return (JsonSerializer.Serialize(d), null);
                    default:
                        return (null, spec.Number == Number.Decimal
                            ? $"a number, {spec.Least.ToString(CultureInfo.InvariantCulture)} or more"
                            : $"a whole number, {spec.Least.ToString(CultureInfo.InvariantCulture)} or more");
                }
        }
    }

    /// <summary>Whether two canonical values are the same value (numbers compared as numbers: 1 and 1.0 are one value).</summary>
    public static bool SameValue(string a, string b)
    {
        try { return Same(Element(a), Element(b)); }
        catch (JsonException) { return false; }
    }

    // ---- the orders ---------------------------------------------------------------------------------------------------------------

    /// <summary>Whether <paramref name="b"/> grants MORE than <paramref name="a"/> on the field's own order.</summary>
    static bool Wider(FieldSpec spec, JsonElement a, JsonElement b) => spec.Order switch
    {
        Order.Mode => ModeRank(b) > ModeRank(a),
        Order.Lower => Dec(b) > Dec(a),
        Order.LowerZeroUnenforced => Unenforced(b) > Unenforced(a),
        Order.Higher => Dec(b) < Dec(a),
        Order.HigherZeroNone => NoneIsNarrowest(b) > NoneIsNarrowest(a),
        Order.Allowance => Effective(spec, b) > Effective(spec, a),
        Order.Allowlist => b.EnumerateArray().Any(i => !a.EnumerateArray()
            .Any(j => string.Equals(i.GetString(), j.GetString(), StringComparison.OrdinalIgnoreCase))),
        Order.Shares => Keys(a, b).Any(k => Share(b, k) > Share(a, k)),
        Order.Floors => Keys(a, b).Any(k => Floor(b, k) < Floor(a, k)),
        Order.Envelope => a.ValueKind == JsonValueKind.Null
            ? b.ValueKind != JsonValueKind.Null
            : b.ValueKind != JsonValueKind.Null
              && (!SamePair(a, b)
                  || b.GetProperty("max_quantity").GetDecimal() > a.GetProperty("max_quantity").GetDecimal()
                  || NotionalRank(b) > NotionalRank(a)
                  || b.GetProperty("deployments").GetInt32() > a.GetProperty("deployments").GetInt32()
                  || (Days(b) ?? long.MaxValue) > (Days(a) ?? long.MaxValue)),
        _ => true
    };

    static int ModeRank(JsonElement e) => e.GetString() switch
    {
        nameof(TradingMode.OBSERVE) => 0,
        nameof(TradingMode.PAPER) => 1,
        _ => 2
    };

    static decimal Dec(JsonElement e) => e.GetDecimal();

    /// <summary>Zero (or below) is "not enforced": the widest — ranked above every bound.</summary>
    static decimal Unenforced(JsonElement e) => e.GetDecimal() is var d && d <= 0m ? decimal.MaxValue : d;

    /// <summary>Higher narrows and zero (or below) is "none at all": the narrowest — ranked below every interval.</summary>
    static decimal NoneIsNarrowest(JsonElement e) => e.GetDecimal() is var d && d <= 0m ? decimal.MinValue : -d;

    static decimal Effective(FieldSpec spec, JsonElement e) => e.GetDecimal() is var d && d > 0m ? d : spec.Default ?? 0;

    static decimal NotionalRank(JsonElement envelope) =>
        envelope.GetProperty("max_notional").GetDecimal() is var d && d <= 0m ? decimal.MaxValue : d;

    static long? Days(JsonElement envelope) =>
        envelope.GetProperty("days").ValueKind == JsonValueKind.Null ? null : envelope.GetProperty("days").GetInt64();

    static bool SamePair(JsonElement a, JsonElement b) =>
        new[] { "connector", "account", "instrument" }.All(k => string.Equals(a.GetProperty(k).GetString(), b.GetProperty(k).GetString(), StringComparison.Ordinal));

    /// <summary>A role's share as <see cref="BoardPolicy.ShareForRole"/> reads it: its own when a fraction, else the equal share.</summary>
    static decimal Share(JsonElement shares, string role) =>
        shares.TryGetProperty(role, out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDecimal(out var d) && d > 0m && d <= 1m
            ? d
            : 1m / CouncilRoles.All.Length;

    /// <summary>A level's floor as <see cref="RewakeFloors.From"/> reads it: its own, never below zero, else the level's default.</summary>
    static decimal Floor(JsonElement floors, string level) =>
        floors.TryGetProperty(level, out var m) && m.ValueKind == JsonValueKind.Number
            ? Math.Max(0m, m.GetDecimal())
            : RewakeFloors.DefaultMinutes.TryGetValue(level, out var d) ? d : 0m;

    static IEnumerable<string> Keys(JsonElement a, JsonElement b) =>
        a.EnumerateObject().Select(p => p.Name).Concat(b.EnumerateObject().Select(p => p.Name)).Distinct(StringComparer.Ordinal);

    // ---- JSON values ------------------------------------------------------------------------------------------------------------

    static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static string Canon(string json) => TapeJson.Canonical(json);

    /// <summary>Deep equality, numbers by value and object keys in any order.</summary>
    static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Number:
                return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : a.GetRawText() == b.GetRawText();
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Same(p.First, p.Second));
            case JsonValueKind.Object:
                var pa = a.EnumerateObject().ToList();
                var pb = b.EnumerateObject().ToList();
                return pa.Count == pb.Count && pa.All(p => b.TryGetProperty(p.Name, out var v) && Same(p.Value, v));
            default:
                return true;
        }
    }
}
