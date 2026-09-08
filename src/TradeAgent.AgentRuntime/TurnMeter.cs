using System.Globalization;
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

    /// <summary>One role's slice of the daily ceiling, as a fraction. An equal share by default.</summary>
    readonly Func<string, decimal> _share;

    /// <summary>The model one role runs on, for the reading that names a role.</summary>
    readonly Func<string, string?> _roleModel;

    readonly Func<DateTimeOffset> _now;
    readonly string _path;
    readonly Lock _gate = new();

    /// <summary>
    /// THE ATTEMPT EACH CONVERSATION HAS OPEN, AND THE LENGTH OF THE PROMPT IT WAS LAUNCHED WITH.
    ///
    /// One per role, because one conversation per role is what the app runs and the owner can type
    /// into the chair's while another role's mission turn is in flight. A single slot handed that
    /// chat turn the OTHER role's row to close — the mission turn's reservation resolved by the
    /// chat's usage — and then left the mission turn to open a fresh row with nothing reserved on
    /// it. Two turns, one reservation, and the day's ceiling short by a whole turn.
    ///
    /// A role with no entry is a turn nothing opened, and the context record says so rather than
    /// reporting a zero-length prompt that nobody wrote.
    ///
    /// <c>HeldForTheLoop</c> is whether this launch's CLOSE belongs in the mission loop's committed
    /// transition (<see cref="CommitStaged"/>) or is written the moment the turn ends. The owner's
    /// typed turn is admitted and reserved like any other but publishes nothing and consumes no
    /// wake, so it has no transition to be part of.
    /// </summary>
    readonly Dictionary<string, (string Id, int PromptChars, bool HeldForTheLoop)> _open = [];

    /// <summary>
    /// AN ID MINTED AND NOT YET LAUNCHED UNDER. <see cref="Mint"/> puts one here so the turn's
    /// message can name it; <see cref="Begin"/> takes it. Nothing is committed while it sits here —
    /// a minted id nobody launched under names no row, which is why the relay's fence refuses a
    /// file naming one.
    /// </summary>
    string? _minted;

    /// <summary>
    /// THE CLOSE OF A TURN THE MISSION LOOP OWNS, METERED AND NOT YET COMMITTED.
    ///
    /// <para>A turn's END belongs in the same transaction as what that turn published and what
    /// became of the wakes it consumed (<c>docs/COUNCIL.md</c> rule 6, "one committed transition").
    /// The runtime reports its usage part-way through <see cref="Record"/>, which is before the
    /// relay has even looked at what the turn wrote — so the close is measured here and held until
    /// <see cref="CommitStaged"/> runs it inside the turn's own commit.</para>
    ///
    /// <para><b>Nothing is lost if it is never committed.</b> The row stays LAUNCHED, and the next
    /// meter to open this database turns it LOST with its reservation as its cost. That is the
    /// conservative reading and it is the one this whole table exists to keep.</para>
    /// </summary>
    Action? _staged;

    public TurnMeter(Database db, Func<decimal> cap, Func<string?>? session = null,
        Func<string?>? runtimeId = null, Func<DateTimeOffset>? now = null, string? recordPath = null,
        Func<OwnerPrice?>? owner = null, Func<string?>? model = null, Func<TurnAllowance>? allowance = null,
        Func<string, decimal>? share = null, Func<string, string?>? roleModel = null)
    {
        _db = db;
        _attempts = new AiAttemptStore(db);
        _cap = cap;
        _session = session ?? (() => null);
        _runtimeId = runtimeId ?? (() => null);
        _model = model ?? (() => null);
        _owner = owner ?? (() => null);
        _allowance = allowance ?? (() => TurnAllowance.Default);
        // An equal share, so a meter built with no council behind it still answers a role's question
        // with something honest rather than with zero — a zero share is a role that can never work.
        _share = share ?? (_ => 1m / CouncilRoles.All.Length);
        _roleModel = roleModel ?? (_ => (model ?? (() => null))());
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
    /// <param name="role">
    /// WHOSE CONVERSATION THIS TURN RAN ON, so the row it closes is that conversation's own open
    /// attempt. <see cref="Attach"/> supplies it; a caller that names none is the chair's, which is
    /// what a launch with no role has always meant.
    /// </param>
    public void Record(AgentTurnEnded ended, string? role = null)
    {
        var price = CostCatalog.Price(ended.Usage, _runtimeId(), owner: Owner(), requestedModel: ModelOf(role));
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
            Context = TurnContext.Read(ended.Raw, PromptCharsOfOpenAttempt(role), ended.Usage)
        };

        try { File.AppendAllText(_path, Json.Write(record) + Environment.NewLine); }
        catch (Exception) { /* the row below is what the cap reads */ }

        // WHOSE TURN THIS WAS, TAKEN NOW, AND OUT OF THE MAP. The id has to be captured at this
        // instant rather than read again at commit time: a close held for the loop's transaction
        // that later read `_open` would close whichever attempt happened to be open by then. Taken
        // out so a second end for the same conversation cannot resolve one row twice, and matched by
        // id from here on — End writes only the row it names, and only while it is still LAUNCHED.
        string? id = null;
        int? promptChars = null;
        var held = false;
        lock (_gate)
            if (_open.Remove(Key(role), out var open))
                (id, promptChars, held) = (open.Id, open.PromptChars, open.HeldForTheLoop);

        void Write() => Close(id, promptChars, ended, price, role);

        // A TURN THE MISSION LOOP OPENED IS CLOSED BY THE MISSION LOOP'S COMMIT, not here. A turn
        // the loop does not own is written at once: the owner typing in the chat window is admitted
        // and reserved like any other now, but it publishes nothing and consumes no wake, so there
        // is no transition for its close to be part of — and holding it in a slot the next mission
        // turn also writes to is how one of the two closes gets lost. So is a turn nothing opened.
        if (!held)
        {
            try { Write(); }
            catch (Exception) { /* a database that cannot be written must not end the turn */ }
        }
        else lock (_gate) _staged = Write;

        Changed?.Invoke();
    }

    /// <summary>
    /// COMMITS THE HELD CLOSE, inside whatever transaction is open on this thread. Returns whether
    /// there was one.
    ///
    /// The mission loop calls this as the first step of the turn's committed transition, so
    /// <c>ai_attempt.state</c> moving to ENDED and the artifacts that turn produced land together.
    /// Called with nothing held it does nothing, which is the right answer for a turn that was
    /// never metered at all.
    /// </summary>
    public bool CommitStaged()
    {
        Action? write;
        lock (_gate) { write = _staged; _staged = null; }
        if (write is null) return false;

        write();
        Changed?.Invoke();
        return true;
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
    /// <b>IT IS ALSO THE ADMISSION GATE.</b> The ceiling is compared inside the transaction that
    /// writes the reservation (<see cref="AiAttemptStore.Begin"/>), so two launches cannot both take
    /// the last turn of the day by both reading the total before either commits. The loop's own look
    /// at <see cref="Today"/> stays where it is as a cheap first filter; this is the gate.
    ///
    /// Returns the admission: an id when the row went in, a refusal when some ceiling had no room,
    /// and <see cref="AiAdmission.Unrecorded"/> when nothing could be written at all. That last one
    /// is not a refusal — a turn whose row could not be written is a turn whose cost the ceiling
    /// will not see, which the card already says out loud, and stopping the AI on a database that
    /// will not take a row would be a new way to lose the mission.
    /// </summary>
    /// <param name="consuming">
    /// The <c>mission_event</c> ids this launch is answering, marked consumed in the same commit as
    /// the row below. One transaction rather than two: a kill in between would hand the same wake to
    /// the next launch and charge the owner for both.
    /// </param>
    /// <param name="heldForTheLoop">
    /// Whether the CLOSE of this launch waits for the mission loop's committed transition. True for
    /// the loop's own turns, whose end belongs in the same <c>Database.Write</c> as what they
    /// published; false for the owner's typed turn, which has no such transition — and which must
    /// not be held in a slot the next mission turn writes to, because one of the two closes would
    /// then be lost.
    /// </param>
    public AiAdmission Begin(string prompt, IReadOnlyList<string>? consuming = null, string? role = null,
        bool heldForTheLoop = true)
    {
        var reservation = Reservation(role);

        // A CLOSE NOBODY COMMITTED BELONGS TO A TURN THAT IS OVER. It can only be here because the
        // transition that should have carried it never ran — a cancelled turn, a host with no
        // commit behind it — and writing it now is late rather than wrong. Leaving it would hold a
        // whole reservation against the day's ceiling for a turn that has already ended.
        try { CommitStaged(); }
        catch (Exception) { /* the row stays LAUNCHED and the next restart loses it, which is safe */ }

        string id;
        lock (_gate) { id = _minted ?? NewId(); _minted = null; }

        var attempt = new AiAttempt
        {
            Id = id,
            StartedAt = _now(),
            Runtime = Safe(_runtimeId),
            RequestedModel = ModelOf(role),
            PricingBasis = reservation.Basis,
            ReservedCost = reservation.Cost ?? 0m,
            State = AiAttemptState.LAUNCHED,
            PolicyVersion = Versions.GrantPolicyVersion.ToString(CultureInfo.InvariantCulture),
            InputHash = HashOf(prompt),
            Role = role
        };

        AiAdmission admission;
        try
        {
            admission = _attempts.Begin(attempt, consuming, RuleFor(role, reservation));
            if (admission.Id is { } written)
                lock (_gate) _open[Key(role)] = (written, prompt.Length, heldForTheLoop);
        }
        catch (Exception) { return AiAdmission.Unrecorded; }

        Changed?.Invoke();
        return admission;
    }

    /// <summary>
    /// THE CEILING THIS LAUNCH IS ADMITTED AGAINST, as the owner has it set at this instant. Every
    /// number is read through the functions the composition root handed over rather than captured,
    /// because the owner changes the limit and the split on the Safety page while the AI is working
    /// and the turn about to start is the one that has to obey.
    ///
    /// It carries no totals. Those are read inside the transaction, which is the whole of the fix.
    /// </summary>
    AiAdmissionRule RuleFor(string? role, TurnPrice reservation)
    {
        var now = _now();
        var (from, to) = LocalDay(now);
        var cap = Cap();
        return new AiAdmissionRule
        {
            From = from,
            To = to,
            Role = role,
            Cap = cap,
            RoleCap = role is null ? cap : cap * Share(role),
            Reservation = reservation.Cost ?? 0m,
            ResumesAt = Midnight(now)
        };
    }

    /// <summary>The owner's ceiling, or zero where the settings could not be read. Never a throw into a launch.</summary>
    decimal Cap()
    {
        try { return _cap(); }
        catch (Exception) { return 0m; }
    }

    /// <summary>One role's fraction of the ceiling, or an equal share where the settings would not read.</summary>
    decimal Share(string role)
    {
        try { return _share(role); }
        catch (Exception) { return 1m / CouncilRoles.All.Length; }
    }

    /// <summary>
    /// THE ID THE NEXT <see cref="Begin"/> WILL USE, minted now so the turn's message can name it.
    ///
    /// The message has to carry the id — staged output is bound to its launch by the file name — and
    /// <see cref="Begin"/> hashes that message, so the id cannot be something that call hands back
    /// afterwards. Nothing is written here: a minted id that never reaches <see cref="Begin"/> is an
    /// id no row carries, and the relay's fence refuses a file naming one, which is the correct
    /// reading of "a turn the app could not record".
    /// </summary>
    public string Mint()
    {
        lock (_gate) return _minted ??= NewId();
    }

    /// <summary>44 characters: the minute it started, so a person can read the table, and a guid.</summary>
    string NewId() => $"turn-{_now().UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():n}"[..44];

    /// <summary>
    /// WHAT THE NEXT TURN WOULD COMMIT: the allowance, priced at the model TradeAgent is asking for.
    ///
    /// The requested model is handed to the price list as though the runtime had named it, because
    /// TradeAgent put it on the command line itself — that is a stronger claim than the "dearest in
    /// the catalogue" fallback, which is what a runtime whose model this software cannot choose
    /// still gets. A rate nobody can supply reserves nothing, and then the ceiling holds nothing
    /// back, which the card and the Situation both say rather than hide.
    /// </summary>
    public TurnPrice Reservation(string? role = null)
    {
        try
        {
            var allowance = _allowance();
            var probe = new TurnUsage(allowance.InputTokens, 0, 0, allowance.OutputTokens, 0, null);
            // The ROLE'S model, because that is the one its next turn will actually be run on. A
            // reservation priced at another role's model is a commitment against a rate nobody is
            // going to be charged, in whichever direction that rate happens to differ.
            return CostCatalog.Price(probe, _runtimeId(), owner: Owner(),
                requestedModel: ModelOf(role));
        }
        catch (Exception) { return TurnPrice.Unknown("the reservation could not be priced"); }
    }

    /// <summary>The model a role will be asked for, or the app's single choice where none is named.</summary>
    string? ModelOf(string? role) => role is null ? Safe(_model) : Safe(() => _roleModel(role));

    /// <summary>
    /// Completes this meter's open attempt, or writes a finished row where nothing opened one — the
    /// owner's own typed turn, which runs through no admission gate and therefore reserves nothing.
    /// A row a restart already declared LOST is left exactly as it is.
    /// </summary>
    void Close(string? id, int? promptChars, AgentTurnEnded ended, TurnPrice price, string? role)
    {
        // COMPONENT BY COMPONENT, FROM THE STREAM THE APP KEPT, and never from anything else. See
        // TurnContext: what the stream does not show is named rather than divided up.
        var context = Json.Write(TurnContext.Read(ended.Raw, promptChars, ended.Usage));

        if (id is null)
        {
            var opened = new AiAttempt
            {
                Id = NewId(),
                StartedAt = ended.At - ended.Duration,
                Runtime = Safe(_runtimeId),
                RequestedModel = ModelOf(role),
                PricingBasis = price.Basis,
                ReservedCost = 0m,
                PolicyVersion = Versions.GrantPolicyVersion.ToString(CultureInfo.InvariantCulture),
                Role = role
            };
            // Ungated: nothing admitted this turn, so nothing may refuse it after the vendor has
            // already done the work. The row exists so the bill is recorded, not to decide anything.
            _attempts.Begin(opened);
            id = opened.Id;
        }

        _attempts.End(id, ended.ExitCode, ended.At,
            ended.Usage?.InputTokens, ended.Usage?.CachedInputTokens, ended.Usage?.CacheWriteInputTokens,
            ended.Usage?.OutputTokens, ended.Usage?.ReasoningOutputTokens,
            ended.Usage?.Model, price.Cost, price.Unpriced, context, price.Basis);
    }

    /// <summary>
    /// Subscribes to a conversation's turns, AND MAKES THE OWNER'S OWN TURNS PASS THE SAME GATE the
    /// mission's do. Returns the unsubscribe, so a meter attached to a conversation that is replaced
    /// does not go on metering the old one — or admitting for it.
    ///
    /// <para><b>Why the owner's chat is admitted at all.</b> It was metered and never reserved:
    /// their typed question started the CLI with no row written first, so it could run beside a
    /// mission turn, take the last of the day's allowance, and be recorded only once the vendor had
    /// already been paid. Rule 3 of <c>docs/COUNCIL.md</c> is "every inference launch" — the owner's
    /// included, because the bill does not care who typed.</para>
    ///
    /// <para>It refuses rather than queues nothing: <see cref="AgentSession.Admit"/> keeps the words
    /// and says why, so a chat that goes quiet at the ceiling still answers the person typing.</para>
    /// </summary>
    /// <param name="role">
    /// The council role this conversation belongs to. The chair's IS the Chat page's, so the owner's
    /// turns are charged to Operations, which is where their agenda lives.
    /// </param>
    public IDisposable Attach(IAgentConversation conversation, string? role = null)
    {
        void OnEnded(AgentTurnEnded e) => Record(e, role);
        conversation.TurnEnded += OnEnded;

        // THE OWNER'S TURN IS NOT HELD FOR A TRANSITION IT IS NOT PART OF. It is admitted and
        // reserved exactly as the loop's is; it is closed the moment it ends, because nothing
        // publishes for it and no wake is waiting on it.
        if (conversation is AgentSession session)
            session.Admit = prompt => Begin(prompt, role: role, heldForTheLoop: false);

        return new Detach(() =>
        {
            conversation.TurnEnded -= OnEnded;
            if (conversation is AgentSession s) s.Admit = null;
        });
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
    public AiSpendToday Today => Reading(null);

    /// <summary>
    /// THE SAME READING, NARROWED TO ONE COUNCIL ROLE: what that role spent and committed today, and
    /// the slice of the owner's ceiling it may spend (<c>docs/COUNCIL.md</c>, "Budgets are reserved,
    /// not checked"). The whole-day figures stay on it — a role is bounded by both its share and the
    /// owner's ceiling, and the loop admits a turn only when both have room.
    /// </summary>
    public AiSpendToday TodayFor(string role) => Reading(role);

    AiSpendToday Reading(string? role)
    {
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
            var probe = CostCatalog.Price(Probe, _runtimeId(), catalogue, Owner(), ModelOf(role));
            var canPrice = probe.Cost is not null || (totals.Turns > 0 && totals.Unpriced < totals.Turns);

            // The role's own rows, and its slice of the ceiling. Read separately from the day's
            // totals rather than derived from them: the two are different queries over the same
            // ledger, and a share worked out by subtracting one role from the day would be wrong the
            // moment a third role exists.
            var cap = _cap();
            var mine = role is null ? totals : ReadTotals(now, role);
            var share = role is null ? cap : cap * _share(role);

            return new AiSpendToday
            {
                Metered = true,
                Role = role,
                RoleSpent = mine.Spent,
                RoleReserved = mine.Reserved,
                RoleCap = share,
                Spent = totals.Spent,
                Reserved = totals.Reserved,
                NextTurnReservation = Reservation(role).Cost ?? 0m,
                Cap = cap,
                Model = ModelOf(role),
                Currency = catalogue.Costs?.Currency ?? "",
                Turns = totals.Turns,
                UnpricedTurns = totals.Unpriced,
                UnreportedTurns = totals.Unreported,
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
    AiAttemptTotals ReadTotals(DateTimeOffset now, string? role = null)
    {
        try
        {
            var (from, to) = LocalDay(now);
            return _attempts.TotalsBetween(from, to, role);
        }
        catch (Exception) { return new AiAttemptTotals(0m, 0m, 0, 0, 0, 0); }
    }

    /// <summary>
    /// SHA-256 of the prompt, lower-case hex. The prompt itself is never written down: it carries
    /// the owner's own words, and what the record needs is the ability to say afterwards that THIS
    /// text was the one that entered the model request.
    /// </summary>
    static string HashOf(string text) => Sha256Hex.Of(text);

    /// <summary>The open attempt's prompt length, read WITHOUT closing it — the row is closed later.</summary>
    int? PromptCharsOfOpenAttempt(string? role)
    {
        lock (_gate) return _open.TryGetValue(Key(role), out var held) ? held.PromptChars : null;
    }

    /// <summary>
    /// WHICH OPEN ATTEMPT A TURN BELONGS TO. A launch with no role named is the chair's, for the
    /// same reason a ledger row with no role is: the single agent the council replaced was
    /// Operations, and a turn attributed to nobody is a bill nobody can allocate.
    /// </summary>
    static string Key(string? role) => CouncilRoles.Or(role);

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
