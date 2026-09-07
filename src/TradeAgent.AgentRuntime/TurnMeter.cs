using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// null for this runtime, and no model is guessed here: a model guessed at this layer would become
/// a price indistinguishable from a measured one. What happens instead is one layer up and says so
/// out loud — <see cref="CostCatalog.Price"/> charges the DEAREST model in that runtime's catalogue
/// and labels the figure an estimate on every screen it reaches (<c>U-prices</c>). An owner who
/// knows better names the model in <c>costs.json</c>, or their own two numbers on the Safety
/// page.</item>
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
/// WHAT THE APP COULD SEE OF ONE TURN'S CONTEXT, COMPONENT BY COMPONENT, AND HOW MUCH OF IT IT
/// COULD NOT SEE AT ALL.
///
/// <para><b>The units are the units the stream gives.</b> Characters are not tokens and bytes are not
/// tokens, and converting one to the other would be exactly the estimate this record exists to
/// avoid — a number that looks measured, is not, and would end up beside real ones on a screen. So
/// the prompt is counted in characters, tool output in bytes, and the token figures are the
/// runtime's own.</para>
///
/// <para><b>Therefore <see cref="UnattributedInputTokens"/> is large, and that is the finding.</b>
/// The only part of the input the stream accounts for in tokens is the part it says came from cache.
/// Everything else — the CLI's system prompt, the tool schemas, the instruction files, the
/// conversation so far — arrives as one number with no breakdown, so it is labelled unattributed
/// rather than divided up by guesswork. <c>docs/COUNCIL.md</c> rule 4 asks for the observable
/// components measured and the remainder named; naming the remainder honestly is most of the value.</para>
///
/// <para>Measured against a real turn: codex-cli 0.153.4 on this Mac, 2026-09-07, asked to run
/// <c>ls</c>. The command item arrives twice with one id — once <c>item.started</c> with
/// <c>"aggregated_output":""</c>, once <c>item.completed</c> with the output on it — so items are
/// counted by id and the largest output seen for an id is the one that counts.</para>
/// </summary>
public sealed record TurnContext
{
    /// <summary>Characters of the prompt THE APP WROTE, or null where the app did not write it.</summary>
    public int? PromptChars { get; init; }

    /// <summary>Distinct command or tool items the stream showed, counted by item id.</summary>
    public int CommandItems { get; init; }

    /// <summary>Bytes of tool output the stream carried, where an item carried any.</summary>
    public int ToolOutputBytes { get; init; }

    /// <summary>Bytes of stream the app kept for this turn. The whole of what it had to look at.</summary>
    public int StreamBytes { get; init; }

    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long UncachedInputTokens { get; init; }

    /// <summary>
    /// Input tokens no component of this record accounts for. The cached ones are accounted for
    /// because the runtime said so; nothing else in the stream is reported in tokens at all.
    /// </summary>
    public long UnattributedInputTokens { get; init; }

    public long OutputTokens { get; init; }
    public long ReasoningOutputTokens { get; init; }

    /// <summary>The sentence that stops the numbers above being read as a full breakdown.</summary>
    public string Note { get; init; } = Unmeasured;

    public const string Unmeasured =
        "characters and bytes are not tokens and are never converted to them; the only input this "
        + "stream accounts for in tokens is the cached part, so the rest is unattributed";

    /// <summary>
    /// Reads one turn's stream. Never throws and never invents: a line that is not JSON, or an event
    /// shape nobody here recognises, contributes nothing rather than a guess.
    /// </summary>
    public static TurnContext Read(string raw, int? promptChars, TurnUsage? usage)
    {
        // Counted by ITEM ID, largest output per id. A command arrives as `item.started` with an
        // empty output and again as `item.completed` with the output on it; counting the lines would
        // report two commands and, worse, would report the output twice the day a runtime streams it
        // in pieces.
        var outputs = new Dictionary<string, int>(StringComparer.Ordinal);
        var anonymous = 0;

        foreach (var line in (raw ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] != '{') continue;

            JsonElement item;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("item", out var e) || e.ValueKind != JsonValueKind.Object)
                    continue;
                item = e.Clone();
            }
            catch (JsonException) { continue; }

            if (!IsToolItem(item)) continue;

            var id = Text(item, "id") ?? $"#{anonymous++}";
            var bytes = OutputBytes(item);
            outputs[id] = Math.Max(outputs.TryGetValue(id, out var had) ? had : 0, bytes);
        }

        var input = usage?.InputTokens ?? 0;
        var cached = usage?.CachedInputTokens ?? 0;

        return new TurnContext
        {
            PromptChars = promptChars,
            CommandItems = outputs.Count,
            ToolOutputBytes = outputs.Values.Sum(),
            StreamBytes = Encoding.UTF8.GetByteCount(raw ?? ""),
            InputTokens = input,
            CachedInputTokens = cached,
            UncachedInputTokens = usage?.UncachedInputTokens ?? 0,
            UnattributedInputTokens = Math.Max(0, input - cached),
            OutputTokens = usage?.OutputTokens ?? 0,
            ReasoningOutputTokens = usage?.ReasoningOutputTokens ?? 0
        };
    }

    /// <summary>
    /// An item the AI RAN rather than wrote. By exclusion on purpose: the measured runtime calls its
    /// one <c>command_execution</c>, and a vendor that renames it should make this count MORE, not
    /// silently nothing — an uncounted tool item makes the context look smaller than it was, which
    /// is the direction that misleads.
    /// </summary>
    static bool IsToolItem(JsonElement item) =>
        Text(item, "type") is { } type
        && !type.Equals("agent_message", StringComparison.OrdinalIgnoreCase)
        && !type.Equals("reasoning", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bytes of output an item carries, where it carries any. <c>aggregated_output</c> is the
    /// measured field on codex 0.153.4; the others are the spellings the neighbouring CLIs use, in
    /// the same spirit as <see cref="TurnUsage.Read"/>.
    /// </summary>
    static int OutputBytes(JsonElement item)
    {
        foreach (var name in new[] { "aggregated_output", "output", "stdout", "result" })
            if (Text(item, name) is { } s) return Encoding.UTF8.GetByteCount(s);
        return 0;
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
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

    /// <summary>
    /// Present when <see cref="Cost"/> is an upper bound rather than a bill — the runtime named no
    /// model and the dearest entry in its catalogue was charged. On the line as well as on the
    /// screens, because this file is the evidence behind a day's total and a reader adding it up
    /// has to be able to see which turns were estimates.
    /// </summary>
    public string? Estimated { get; init; }

    /// <summary>The rate was the owner's own two numbers rather than any list price.</summary>
    public bool PricedByOwner { get; init; }

    /// <summary>What the app could see of this turn's context. See <see cref="TurnContext"/>.</summary>
    public TurnContext? Context { get; init; }
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
    /// <summary>The per-turn detail, in the app's state directory. Appended to, never rewritten.</summary>
    public static string RecordPath => Path.Combine(Paths.State, "agent-turns.jsonl");

    readonly Database _db;
    readonly AiAttemptStore _attempts;
    readonly Func<string?> _session;
    readonly Func<string?> _runtimeId;
    readonly Func<string?> _model;
    readonly Func<OwnerPrice?> _owner;
    readonly Func<decimal> _cap;
    readonly Func<TurnAllowance> _allowance;
    readonly Func<DateTimeOffset> _now;
    readonly string _path;
    readonly Lock _gate = new();

    /// <summary>The attempt this meter opened and has not closed. At most one: turns are serial.</summary>
    string? _open;

    /// <summary>
    /// Characters of the prompt that attempt was launched with. Null when nothing opened an attempt
    /// — the owner's own typed turn — and then the context record says so rather than reporting a
    /// zero-length prompt that nobody wrote.
    /// </summary>
    int? _openPromptChars;

    public TurnMeter(Database db, Func<decimal> cap, Func<string?>? session = null,
        Func<string?>? runtimeId = null, Func<DateTimeOffset>? now = null, string? recordPath = null,
        Func<OwnerPrice?>? owner = null, Func<string?>? model = null, Func<TurnAllowance>? allowance = null)
    {
        _db = db;
        _attempts = new AiAttemptStore(db);
        _cap = cap;
        _session = session ?? (() => null);
        _runtimeId = runtimeId ?? (() => null);
        _model = model ?? (() => null);
        _owner = owner ?? (() => null);
        _allowance = allowance ?? (() => TurnAllowance.Default);
        _now = now ?? (() => DateTimeOffset.Now);
        _path = recordPath ?? RecordPath;

        // EVERY ATTEMPT LEFT OPEN IS LOST HERE, AND KEEPS ITS RESERVATION AS ITS COST.
        //
        // This is the line that makes the ceiling a cap. A meter opening this database is either the
        // app starting or a second meter being built over the same file; in both cases a row still
        // LAUNCHED belongs to a process that is gone, and nobody will ever report what that turn
        // used. The vendor has already done the work and billed for it. Releasing the reservation
        // here would mean the allowance came back every time the AI was killed mid-turn — which is
        // exactly what happened before this table existed, and what nothing after it may do.
        try { _attempts.LoseOpen(_now()); }
        catch (Exception) { /* a database that cannot be written must not stop the app starting */ }
    }

    /// <summary>
    /// A turn with nothing in it, used only to ask the price list "could you price this runtime at
    /// all?". Zero tokens at any rate is zero, so the ANSWER is never a number worth having — what
    /// is worth having is whether an answer came back at all.
    /// </summary>
    static readonly TurnUsage Probe = new(0, 0, 0, 0, 0, null);

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
        var price = CostCatalog.Price(ended.Usage, _runtimeId(), owner: Owner());
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
            Unpriced = price.Unpriced,
            Estimated = price.Estimated,
            PricedByOwner = price.ByOwner,
            Context = TurnContext.Read(ended.Raw, PromptCharsOfOpenAttempt(), ended.Usage)
        };

        try { File.AppendAllText(_path, Json.Write(record) + Environment.NewLine); }
        catch (Exception) { /* the row below is what the cap reads */ }

        try { Close(ended, price); }
        catch (Exception) { /* a database that cannot be written must not end the turn */ }

        Changed?.Invoke();
    }

    /// <summary>
    /// OPENS THE DURABLE RECORD OF THE TURN ABOUT TO RUN, AND COMMITS ITS COST — before the process
    /// starts, which is the only moment at which committing it means anything.
    ///
    /// <paramref name="prompt"/> is hashed, never stored: it is the Situation the app wrote, and
    /// which prompt entered a model request is one of the things round 4 of <c>docs/COUNCIL.md</c>
    /// named as unrecoverable afterwards — while the prompt itself carries the owner's own words and
    /// belongs in no table.
    ///
    /// Returns the attempt id, or null when nothing could be written. Null is not a refusal: the
    /// admission gate is read separately from <see cref="Today"/>, and a turn that could not be
    /// recorded is a turn whose cost the ceiling will not see, which the card already says out loud.
    /// </summary>
    public string? Begin(string prompt)
    {
        var reservation = Reservation();
        var attempt = new AiAttempt
        {
            Id = $"turn-{_now().UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():n}"[..44],
            StartedAt = _now(),
            Runtime = Safe(_runtimeId),
            RequestedModel = Safe(_model),
            PricingBasis = reservation.Basis,
            ReservedCost = reservation.Cost ?? 0m,
            State = AiAttemptState.LAUNCHED,
            PolicyVersion = Versions.GrantPolicyVersion.ToString(CultureInfo.InvariantCulture),
            InputHash = HashOf(prompt)
        };

        try
        {
            lock (_gate)
            {
                _attempts.Begin(attempt);
                _open = attempt.Id;
                _openPromptChars = prompt.Length;
            }
            Changed?.Invoke();
            return attempt.Id;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// WHAT THE NEXT TURN WOULD COMMIT: the allowance, priced at the model TradeAgent is asking for.
    ///
    /// The requested model is handed to the price list as though the runtime had named it, because
    /// TradeAgent put it on the command line itself — that is a stronger claim than the "dearest in
    /// the catalogue" fallback, which is what a runtime whose model this software cannot choose
    /// still gets. A rate nobody can supply reserves nothing, and then the ceiling holds nothing
    /// back, which the card and the Situation both say rather than hide.
    /// </summary>
    public TurnPrice Reservation()
    {
        try
        {
            var allowance = _allowance();
            var probe = new TurnUsage(allowance.InputTokens, 0, 0, allowance.OutputTokens, 0, Safe(_model));
            return CostCatalog.Price(probe, _runtimeId(), owner: Owner());
        }
        catch (Exception) { return TurnPrice.Unknown("the reservation could not be priced"); }
    }

    /// <summary>
    /// Completes this meter's open attempt, or writes a finished row where nothing opened one — the
    /// owner's own typed turn, which runs through no admission gate and therefore reserves nothing.
    /// A row a restart already declared LOST is left exactly as it is.
    /// </summary>
    void Close(AgentTurnEnded ended, TurnPrice price)
    {
        string? id;
        int? promptChars;
        lock (_gate) { id = _open; promptChars = _openPromptChars; _open = null; _openPromptChars = null; }

        // COMPONENT BY COMPONENT, FROM THE STREAM THE APP KEPT, and never from anything else. See
        // TurnContext: what the stream does not show is named rather than divided up.
        var context = Json.Write(TurnContext.Read(ended.Raw, promptChars, ended.Usage));

        if (id is null)
        {
            var opened = new AiAttempt
            {
                Id = $"turn-{_now().UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():n}"[..44],
                StartedAt = ended.At - ended.Duration,
                Runtime = Safe(_runtimeId),
                RequestedModel = Safe(_model),
                PricingBasis = price.Basis,
                ReservedCost = 0m,
                PolicyVersion = Versions.GrantPolicyVersion.ToString(CultureInfo.InvariantCulture)
            };
            _attempts.Begin(opened);
            id = opened.Id;
        }

        _attempts.End(id, ended.ExitCode, ended.At,
            ended.Usage?.InputTokens, ended.Usage?.CachedInputTokens, ended.Usage?.CacheWriteInputTokens,
            ended.Usage?.OutputTokens, ended.Usage?.ReasoningOutputTokens,
            ended.Usage?.Model, price.Cost, price.Unpriced, context, price.Basis);
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
            var totals = ReadTotals(now);
            var catalogue = CostCatalog.Read();

            // ASKED OF THE PRICE LIST, not inferred from the day's history — which on the first turn
            // of the day is empty and would make an unpriceable installation look priced.
            //
            // A turn that HAS priced today settles it the other way: a runtime whose stream names its
            // own model prices without an entry in RuntimeModels, and the probe below cannot know
            // that in advance because it has no model to offer.
            var probe = CostCatalog.Price(Probe, _runtimeId(), catalogue, Owner());
            var canPrice = probe.Cost is not null || (totals.Turns > 0 && totals.Unpriced < totals.Turns);

            return new AiSpendToday
            {
                Metered = true,
                Spent = totals.Spent,
                Reserved = totals.Reserved,
                NextTurnReservation = Reservation().Cost ?? 0m,
                Cap = _cap(),
                Currency = catalogue.Costs?.Currency ?? "",
                Turns = totals.Turns,
                UnpricedTurns = totals.Unpriced,
                EstimatedTurns = totals.Estimated,
                // The label describes how THIS INSTALLATION is being priced, so it is the probe's
                // answer first — true before the day's first turn, which is the moment the card is
                // most likely to be read — and the day's own history second, for the runtime whose
                // stream names its model and whose probe therefore cannot know in advance.
                Estimated = probe.Estimated ?? (totals.Estimated > 0 ? Labels.PricedAtHighestListPrice : null),
                PricedByOwner = probe.ByOwner,
                CanPrice = canPrice,
                WhyNoPrice = canPrice ? null : probe.Unpriced,
                ResumesAt = Midnight(now)
            };
        }
    }

    // ---- the day, summed over the launch ledger ---------------------------------------------------
    //
    // The two kv counters this replaces were written when a turn FINISHED, which made a killed turn
    // free: the vendor had been asked to do the work and had billed for it, and the day's total
    // never moved. Summing rows that were written BEFORE the launch is what makes the ceiling a cap
    // rather than a report — and it needs no day-string, because a row carries the instant it
    // started and the window below is the owner's own local day.

    /// <summary>
    /// The local day <paramref name="now"/> falls in, as the half-open instant window a row's
    /// <c>started_at</c> is tested against. Local because the reset belongs at the owner's midnight:
    /// an owner in Ljubljana whose day rolled over at 01:00 would be reading a today that is not
    /// theirs. Each boundary takes the offset in force AT that boundary, so the day either side of a
    /// daylight-saving change is twenty-three or twenty-five hours long rather than silently wrong.
    /// </summary>
    static (DateTimeOffset From, DateTimeOffset To) LocalDay(DateTimeOffset now)
    {
        var start = now.ToLocalTime().Date;
        var end = start.AddDays(1);
        return (new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)),
                new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)));
    }

    /// <summary>The next LOCAL midnight — when today's totals stop being today's.</summary>
    static DateTimeOffset Midnight(DateTimeOffset now) => LocalDay(now).To;

    /// <summary>
    /// Today's rows, or zeroes when none of them can be read. A read that throws answers zero rather
    /// than throwing into a card repaint, and a zero here is visibly the same as a day with no turns
    /// — which is why the card keys its wording on <see cref="AiSpendToday.CanPrice"/> instead.
    /// </summary>
    AiAttemptTotals ReadTotals(DateTimeOffset now)
    {
        try
        {
            var (from, to) = LocalDay(now);
            return _attempts.TotalsBetween(from, to);
        }
        catch (Exception) { return new AiAttemptTotals(0m, 0m, 0, 0, 0, 0); }
    }

    /// <summary>
    /// SHA-256 of the prompt, lower-case hex. The prompt itself is never written down: it carries
    /// the owner's own words, and what the record needs is the ability to say afterwards that THIS
    /// text was the one that entered the model request.
    /// </summary>
    static string HashOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The open attempt's prompt length, read WITHOUT closing it — the row is closed later.</summary>
    int? PromptCharsOfOpenAttempt()
    {
        lock (_gate) return _openPromptChars;
    }

    static string? Safe(Func<string?> f)
    {
        try { return f(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The owner's rate, or none. Swallowing a throw here is the same rule as <see cref="Safe"/>:
    /// this is called from the end of a turn, and a settings read that failed must cost the RATE,
    /// which falls back to the dearer list price, rather than the turn.
    /// </summary>
    OwnerPrice? Owner()
    {
        try { return _owner(); }
        catch (Exception) { return null; }
    }
}
