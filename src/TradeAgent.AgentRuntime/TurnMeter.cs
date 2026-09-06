using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT ONE TURN USED, AS THE RUNTIME ITSELF REPORTED IT. Never a number this software worked out.
///
/// Measured, not assumed. Codex CLI 0.153.4 on macOS, 2026-09-06,
/// <c>codex exec -s read-only --skip-git-repo-check --json "say hi"</c> — the fourth and last line
/// of stdout, verbatim:
///
/// <code>
/// {"type":"turn.completed","usage":{"input_tokens":17232,"cached_input_tokens":12928,"cache_write_input_tokens":0,"output_tokens":6,"reasoning_output_tokens":0}}
/// </code>
///
/// Two things that event does NOT carry, and both matter downstream:
///
/// <list type="bullet">
/// <item><b>No model name.</b> Nothing in that run's four events named a model — not
/// <c>thread.started</c>, not <c>turn.started</c>, not the message item. So <see cref="Model"/> is
/// null for this runtime, and the price has to come from a model the OWNER names in
/// <c>costs.json</c> for that runtime. A model guessed here would become a price, and a guessed
/// price is a guessed bill.</item>
/// <item><b>Cached input is a SUBSET of <see cref="InputTokens"/></b>, not a number beside it —
/// 12,928 of the 17,232 were served from cache. Adding the two would bill the cached half twice, so
/// <see cref="UncachedInputTokens"/> is what carries the full input rate.</item>
/// </list>
///
/// <see cref="ReasoningOutputTokens"/> is likewise a subset of <see cref="OutputTokens"/> and is
/// recorded for the owner to read, never added to the bill a second time.
/// </summary>
public sealed record TurnUsage(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    string? Model)
{
    /// <summary>Input tokens that were NOT served from cache — the ones at the full input rate.</summary>
    public long UncachedInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    /// <summary>Nothing was reported. Distinct from "not reported at all", which is a null usage.</summary>
    public bool IsEmpty =>
        InputTokens == 0 && CachedInputTokens == 0 && CacheWriteInputTokens == 0 &&
        OutputTokens == 0 && ReasoningOutputTokens == 0;

    /// <summary>
    /// Two usage events from ONE run, added. A run that reports its tokens more than once is
    /// summed rather than last-wins, because the direction of a wrong bill matters: summing a
    /// cumulative report over-states what the turn cost and can only stop the AI early, while
    /// keeping the last of several per-turn reports under-states it and lets the cap be walked past.
    /// Codex 0.153.4 emits exactly one, so on the measured runtime the two are the same number.
    /// </summary>
    public TurnUsage Plus(TurnUsage other) => new(
        InputTokens + other.InputTokens,
        CachedInputTokens + other.CachedInputTokens,
        CacheWriteInputTokens + other.CacheWriteInputTokens,
        OutputTokens + other.OutputTokens,
        ReasoningOutputTokens + other.ReasoningOutputTokens,
        Model ?? other.Model);

    /// <summary>
    /// The usage one stream event carries, or null when it carries none.
    ///
    /// The field names are the measured ones first and the other spellings these tools use after,
    /// because this parser has to keep working when a vendor renames a field — which is the whole
    /// reason the manifests beside it are data. What it will NOT do is invent: an event with no
    /// recognisable token count answers null, and a null usage becomes "cost unknown" on the card
    /// rather than a zero, because a turn that cost something unmeasured is not a free turn.
    /// </summary>
    public static TurnUsage? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        foreach (var container in Containers(e))
        {
            var input = Num(container, "input_tokens", "prompt_tokens", "input");
            var output = Num(container, "output_tokens", "completion_tokens", "output");
            var cached = Num(container, "cached_input_tokens", "cache_read_input_tokens", "cached_tokens");
            var write = Num(container, "cache_write_input_tokens", "cache_creation_input_tokens");
            var reasoning = Num(container, "reasoning_output_tokens", "reasoning_tokens");

            if (input is null && output is null && cached is null && write is null) continue;

            return new TurnUsage(input ?? 0, cached ?? 0, write ?? 0, output ?? 0, reasoning ?? 0,
                Str(container, "model") ?? Str(e, "model"));
        }

        return null;
    }

    /// <summary>
    /// Where a usage object is looked for: the event's own <c>usage</c>, the same one level down
    /// under the wrappers these CLIs use, and the event itself for a runtime that puts the counts
    /// flat on the line.
    /// </summary>
    static IEnumerable<JsonElement> Containers(JsonElement e)
    {
        foreach (var name in new[] { "usage", "token_usage" })
            if (e.TryGetProperty(name, out var u) && u.ValueKind == JsonValueKind.Object)
                yield return u;

        foreach (var wrapper in new[] { "info", "item", "data" })
        {
            if (!e.TryGetProperty(wrapper, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in new[] { "usage", "token_usage" })
                if (w.TryGetProperty(name, out var u) && u.ValueKind == JsonValueKind.Object)
                    yield return u;
        }

        yield return e;
    }

    static long? Num(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (!e.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        }
        return null;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;
}

/// <summary>
/// What one turn cost, or the sentence saying why nobody can say. Never both, and never neither.
/// </summary>
public sealed record TurnPrice(decimal? Cost, string Currency, string? Unpriced)
{
    public static TurnPrice Unknown(string why) => new(null, "", why);
}

/// <summary>One model's prices, per MILLION tokens, in <see cref="AgentCosts.Currency"/>.</summary>
public sealed class ModelPrice
{
    public string Model { get; set; } = "";
    public decimal InputPerMillion { get; set; }

    /// <summary>Null means the input rate: a vendor that does not discount cache reads charges it.</summary>
    public decimal? CachedInputPerMillion { get; set; }

    /// <summary>Null means the input rate. OpenAI does not bill cache writes separately; others do.</summary>
    public decimal? CacheWritePerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }
}

/// <summary>
/// <c>costs.json</c>: what the owner's AI tool charges them, beside <c>runtimes.json</c> and read
/// the same way.
///
/// <b>It ships EMPTY of prices, and that is the decision, not an omission.</b> A price is a claim
/// about somebody else's bill, and this software cannot see that bill: the same Codex CLI costs
/// per-token on an API key and nothing per-token on a ChatGPT subscription, and the published
/// per-million rates change on OpenAI's schedule, not on this repository's. So a shipped number
/// would be wrong for a whole class of owners and stale for the rest, and it would be wrong
/// invisibly — which is the failure mode this file exists to avoid. With no prices, every turn is
/// recorded with its tokens and reported "cost unknown", in those words, on the card.
/// </summary>
public sealed class AgentCosts
{
    /// <summary>The currency every number here is in, and the one the cap is read in.</summary>
    public string Currency { get; set; } = "USD";

    public List<ModelPrice> Models { get; set; } = [];

    /// <summary>
    /// The model to price a runtime's turns at WHEN THE RUNTIME DOES NOT SAY WHICH IT USED — keyed
    /// by <see cref="RuntimeManifest.Id"/>. Codex 0.153.4's <c>--json</c> stream names no model
    /// anywhere (see <see cref="TurnUsage"/>), so without an entry here its turns are unpriced. The
    /// owner names it because only they know what they are signed in as.
    /// </summary>
    public Dictionary<string, string> RuntimeModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The prices, or the one sentence saying why there are none. Never both.</summary>
public sealed record CostCatalogRead(AgentCosts? Costs, string? Unreadable);

/// <summary>
/// Reading <c>costs.json</c>, under <c>VendorFile</c>'s rule: an ABSENT file is the shipped
/// configuration and says nothing; a file that EXISTS and cannot be parsed is a refusal, in the
/// owner's words, and nothing shipped stands in for it.
///
/// What a refusal means here is narrower than it is for <c>runtimes.json</c>, deliberately. That
/// file decides which program runs and under what sandbox, so an unreadable one stops the AI. This
/// one only prices what the AI already did: an unreadable one makes every turn "cost unknown" and
/// therefore makes the daily cap unenforceable — which the card says, rather than the software
/// quietly pricing turns at zero and reporting a cap that is holding nothing back.
/// </summary>
public static class CostCatalog
{
    public static string OverridePath => Path.Combine(Paths.Home, Labels.CostsFile);

    /// <summary>
    /// Shipped with no model prices. See <see cref="AgentCosts"/> for why that is the honest default
    /// rather than a table of last month's published rates.
    /// </summary>
    public static AgentCosts BuiltIn() => new();

    public static CostCatalogRead Read()
    {
        var file = VendorFile.Read<AgentCosts>(OverridePath);
        if (file.Unreadable is { } why) return new(null, Labels.CostsCouldNotBeRead(why));
        return new(file.Value ?? BuiltIn(), null);
    }

    /// <summary>
    /// WHAT THIS TURN COST, or why nobody can say. Every branch that cannot produce a number
    /// produces a sentence instead; none of them produces a zero.
    /// </summary>
    public static TurnPrice Price(TurnUsage? usage, string? runtimeId, CostCatalogRead? catalogue = null)
    {
        var read = catalogue ?? Read();
        if (read.Unreadable is { } why) return TurnPrice.Unknown(why);

        var costs = read.Costs ?? BuiltIn();
        if (usage is null)
            return TurnPrice.Unknown("the AI tool did not report how many tokens the turn used");

        var model = usage.Model ?? Declared(costs, runtimeId);
        if (model is null)
            return TurnPrice.Unknown(
                $"the AI tool did not say which model it used, and {Labels.CostsFile} does not name one for it");

        var price = costs.Models.FirstOrDefault(m => string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase));
        if (price is null)
            return TurnPrice.Unknown($"{Labels.CostsFile} has no price for {model}");

        var cost =
            usage.UncachedInputTokens * price.InputPerMillion +
            usage.CachedInputTokens * (price.CachedInputPerMillion ?? price.InputPerMillion) +
            usage.CacheWriteInputTokens * (price.CacheWritePerMillion ?? price.InputPerMillion) +
            // Reasoning tokens are part of the output count, not a sixth line: adding them would
            // bill the same tokens twice on every runtime that reports both.
            usage.OutputTokens * price.OutputPerMillion;

        return new TurnPrice(cost / 1_000_000m, costs.Currency, null);
    }

    static string? Declared(AgentCosts costs, string? runtimeId) =>
        runtimeId is { Length: > 0 } id && costs.RuntimeModels.TryGetValue(id, out var m) && m.Length > 0
            ? m
            : null;
}

/// <summary>One line of <c>state/agent-turns.jsonl</c>. Written by the app; nothing reads it back.</summary>
public sealed record TurnRecord
{
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset Ended { get; init; }
    public double Seconds { get; init; }

    /// <summary>The runtime's own session id, when it has told us one. Diagnostics only.</summary>
    public string? Session { get; init; }

    public string? Runtime { get; init; }
    public int ExitCode { get; init; }

    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? CacheWriteInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningOutputTokens { get; init; }
    public string? Model { get; init; }

    public decimal? Cost { get; init; }
    public string? Currency { get; init; }

    /// <summary>Why <see cref="Cost"/> is absent. Present exactly when it is.</summary>
    public string? Unpriced { get; init; }
}

/// <summary>
/// WHAT THE AI HAS COST TODAY, AND THE CEILING IT STOPS AT.
///
/// One line per turn goes to <c>state/agent-turns.jsonl</c> — in the APP's state directory, never
/// the agent's own home. The agent can write anywhere in its workspace, so a bill kept there would
/// be a bill the party being billed can edit; it is the same rule the material ledger keeps between
/// what the scanner measured and what the agent says about it.
///
/// Today's two totals live in <c>kv</c>, so there is no schema change and no new table: the file is
/// the detail and the row is the running sum. The day is stored beside them as a LOCAL date, so the
/// reset is at the owner's midnight rather than UTC's — an owner in Ljubljana whose cap reset at
/// 01:00 or 02:00 would be reading a "today" that is not theirs.
/// </summary>
public sealed class TurnMeter
{
    public const string DayKey = "ai_meter_day";
    public const string CostKey = "ai_meter_cost";
    public const string TurnsKey = "ai_meter_turns";
    public const string UnpricedKey = "ai_meter_unpriced";

    /// <summary>The per-turn detail, in the app's state directory. Appended to, never rewritten.</summary>
    public static string RecordPath => Path.Combine(Paths.State, "agent-turns.jsonl");

    readonly Database _db;
    readonly Func<string?> _session;
    readonly Func<string?> _runtimeId;
    readonly Func<decimal> _cap;
    readonly Func<DateTimeOffset> _now;
    readonly string _path;
    readonly Lock _gate = new();

    public TurnMeter(Database db, Func<decimal> cap, Func<string?>? session = null,
        Func<string?>? runtimeId = null, Func<DateTimeOffset>? now = null, string? recordPath = null)
    {
        _db = db;
        _cap = cap;
        _session = session ?? (() => null);
        _runtimeId = runtimeId ?? (() => null);
        _now = now ?? (() => DateTimeOffset.Now);
        _path = recordPath ?? RecordPath;
    }

    /// <summary>Raised after a turn has been recorded, so the card can redraw on the new total.</summary>
    public event Action? Changed;

    /// <summary>
    /// Meters one finished turn: the line in the file, then the running totals.
    ///
    /// It never throws. This is called from the end of a turn and a meter that could break a turn
    /// would be a bill that stops the work — so a file that cannot be appended to costs the DETAIL
    /// of one turn, and the totals the cap reads are written separately and still move.
    /// </summary>
    public void Record(AgentTurnEnded ended)
    {
        var price = CostCatalog.Price(ended.Usage, _runtimeId());
        var record = new TurnRecord
        {
            Started = ended.At - ended.Duration,
            Ended = ended.At,
            Seconds = Math.Round(ended.Duration.TotalSeconds, 3),
            Session = Safe(_session),
            Runtime = Safe(_runtimeId),
            ExitCode = ended.ExitCode,
            InputTokens = ended.Usage?.InputTokens,
            CachedInputTokens = ended.Usage?.CachedInputTokens,
            CacheWriteInputTokens = ended.Usage?.CacheWriteInputTokens,
            OutputTokens = ended.Usage?.OutputTokens,
            ReasoningOutputTokens = ended.Usage?.ReasoningOutputTokens,
            Model = ended.Usage?.Model,
            Cost = price.Cost,
            Currency = price.Cost is null ? null : price.Currency,
            Unpriced = price.Unpriced
        };

        try { File.AppendAllText(_path, Json.Write(record) + Environment.NewLine); }
        catch (Exception) { /* the totals below are what the cap reads */ }

        try { AddToToday(price.Cost); }
        catch (Exception) { /* a database that cannot be written must not end the turn */ }

        Changed?.Invoke();
    }

    /// <summary>
    /// Subscribes to a conversation's turns. Returns the unsubscribe, so a meter attached to a
    /// conversation that is replaced does not go on metering the old one.
    /// </summary>
    public IDisposable Attach(IAgentConversation conversation)
    {
        void OnEnded(AgentTurnEnded e) => Record(e);
        conversation.TurnEnded += OnEnded;
        return new Detach(() => conversation.TurnEnded -= OnEnded);
    }

    sealed class Detach(Action off) : IDisposable
    {
        public void Dispose() => off();
    }

    /// <summary>
    /// Today's bill, the cap it is measured against, and the local midnight a capped loop restarts
    /// at. Read on every card repaint, so it does its own day-rollover check rather than relying on
    /// anything having run at midnight.
    /// </summary>
    public AiSpendToday Today
    {
        get
        {
            var now = _now();
            var (cost, turns, unpriced) = ReadTotals(Today_(now));
            return new AiSpendToday
            {
                Metered = true,
                Spent = cost,
                Cap = _cap(),
                Currency = CostCatalog.Read().Costs?.Currency ?? "",
                Turns = turns,
                UnpricedTurns = unpriced,
                WhyNoPrice = unpriced > 0 ? LastUnpricedReason() : null,
                ResumesAt = Midnight(now)
            };
        }
    }

    /// <summary>
    /// The reason the most recent unpriced turn gave, so the card can say WHY the cost is unknown
    /// rather than only that it is. Read from the file's tail; a file that cannot be read gives the
    /// general sentence rather than a wrong specific one.
    /// </summary>
    string? LastUnpricedReason()
    {
        try
        {
            foreach (var line in ReadTail(_path, 40).Reverse())
            {
                var record = Json.Read<TurnRecord>(line);
                if (record?.Unpriced is { Length: > 0 } why) return why;
            }
        }
        catch (Exception) { /* fall through to the general sentence */ }
        return $"{Labels.CostsFile} does not price these turns";
    }

    static IEnumerable<string> ReadTail(string path, int lines)
    {
        if (!File.Exists(path)) return [];
        var all = File.ReadLines(path).Where(l => l.Trim().Length > 0).ToList();
        return all.Count <= lines ? all : all.Skip(all.Count - lines);
    }

    // ---- the kv totals -------------------------------------------------------------------------

    static string Today_(DateTimeOffset now) => now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The next LOCAL midnight — when today's totals stop being today's.</summary>
    static DateTimeOffset Midnight(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var tomorrow = local.Date.AddDays(1);
        return new DateTimeOffset(tomorrow, TimeZoneInfo.Local.GetUtcOffset(tomorrow));
    }

    /// <summary>
    /// The totals, or zeroes when the row is another day's. The stale row is NOT cleared here: a
    /// read must not write, and the next turn's write replaces it with today's anyway.
    /// </summary>
    (decimal Cost, int Turns, int Unpriced) ReadTotals(string today)
    {
        if (!string.Equals(_db.GetKv(DayKey), today, StringComparison.Ordinal)) return (0m, 0, 0);
        return (Dec(_db.GetKv(CostKey)), Int(_db.GetKv(TurnsKey)), Int(_db.GetKv(UnpricedKey)));
    }

    void AddToToday(decimal? cost)
    {
        lock (_gate)
        {
            var today = Today_(_now());
            var (had, turns, unpriced) = ReadTotals(today);

            _db.SetKv(DayKey, today);
            _db.SetKv(CostKey, (had + (cost ?? 0m)).ToString(CultureInfo.InvariantCulture));
            _db.SetKv(TurnsKey, (turns + 1).ToString(CultureInfo.InvariantCulture));
            _db.SetKv(UnpricedKey, (unpriced + (cost is null ? 1 : 0)).ToString(CultureInfo.InvariantCulture));
        }
    }

    static decimal Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    static int Int(string? s) =>
        int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : 0;

    static string? Safe(Func<string?> f)
    {
        try { return f(); }
        catch (Exception) { return null; }
    }
}
