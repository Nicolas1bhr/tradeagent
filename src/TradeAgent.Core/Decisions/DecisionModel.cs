using System.Buffers;
using System.Text;
using System.Text.Json;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Decisions;

/// <summary>
/// ONE BOUNDED DECISION MODEL — a "System One" instrument such as Jev — BEHIND ONE REPLACEABLE PORT
/// (<c>U-decision-port</c>; <c>docs/EDGE-FACTORY.md</c> § 4.2).
///
/// <para>A call is a state and typed questions in, a full distribution per question out. Whoever implements this
/// keeps four properties, and the port is the place they are stated so no adapter can choose to skip one:</para>
/// <list type="number">
/// <item><b>Refused before sending.</b> Every structural limit — <see cref="DecisionRequests.Refusal"/> — is checked
/// before anything is reserved or leaves the machine. A refusal costs nothing and says why in words.</item>
/// <item><b>Reserved before sending</b> in the main database's launch ledger, under the owner's one daily AI cap and
/// perception's own budget, in ONE transaction; one request per reservation, never a second on the same one.</item>
/// <item><b>Recorded before settling</b>: every call that was sent is a row of the tape (<c>decision_call</c>), the
/// answer with it — and a call with no answer keeps its reservation as its cost. Unknown is never zero.</item>
/// <item><b>Pinned</b>: an answer from a model other than the instrument's built-in pin is recorded UNPINNED and is
/// not evidence.</item>
/// </list>
///
/// <para><b>Nothing here annotates and no agent can call it.</b> There is no verb and no pipe op that reaches a
/// decision model, its budget, its pin or its key; the owner's Perception card is <c>U-decision-card</c>'s.</para>
/// </summary>
public interface IDecisionModel
{
    /// <summary>
    /// One call: refused before sending, or reserved, sent ONCE, recorded and settled. Never throws for a refusal,
    /// a host's error or a lost answer — those are <see cref="DecisionAnswer.Status"/> — so a caller cannot lose a
    /// reservation's accounting by forgetting a catch.
    /// </summary>
    Task<DecisionAnswer> DecideAsync(DecisionRequest request, CancellationToken ct = default);
}

/// <summary>The three primitives a System One model answers (docs.typesafe.ai/api, read 2026-10-09).</summary>
public enum DecisionKind
{
    /// <summary>Which one of these options? A probability for each, the likeliest, and a confidence.</summary>
    Choice,

    /// <summary>Does this condition hold? The probability of yes.</summary>
    Noul,

    /// <summary>Where on this ordered scale? A probability per level, the weighted position, and a confidence.</summary>
    Score
}

/// <summary>One option of a <see cref="DecisionKind.Choice"/>: its name, and what it means — or null for its name alone.</summary>
public sealed record DecisionOption(string Name, string? Description = null);

/// <summary>
/// ONE TYPED QUESTION, as the app writes it. Instructions are text; a Choice carries options, a Score its ordered
/// levels (position is the score, from zero), a Noul optionally what a yes and a no mean. Build one through
/// <see cref="Choice"/>, <see cref="Noul"/> or <see cref="Score"/>.
/// </summary>
public sealed record DecisionQuestion
{
    public required DecisionKind Kind { get; init; }
    public required string Instructions { get; init; }
    public IReadOnlyList<DecisionOption> Options { get; init; } = [];
    public IReadOnlyList<string> Levels { get; init; } = [];
    public string? True { get; init; }
    public string? False { get; init; }

    public static DecisionQuestion Choice(string instructions, params DecisionOption[] options) =>
        new() { Kind = DecisionKind.Choice, Instructions = instructions, Options = options };

    public static DecisionQuestion Noul(string instructions, string? whenTrue = null, string? whenFalse = null) =>
        new() { Kind = DecisionKind.Noul, Instructions = instructions, True = whenTrue, False = whenFalse };

    public static DecisionQuestion Score(string instructions, params string[] levels) =>
        new() { Kind = DecisionKind.Score, Instructions = instructions, Levels = levels };
}

/// <summary>
/// WHICH QUESTION SCHEMA A CALL ASKED: its id, its version, and the SHA-256 of its questions as they go on the wire.
///
/// <para>The hash is computed here, from the questions, and never taken from a caller: <see cref="Of"/> is the one way
/// to make a reference whose hash means anything, and the port refuses a request whose questions do not hash to the
/// reference it carries. So a recorded answer's schema hash is a measurement of what was asked, not a label.</para>
/// </summary>
public sealed record SchemaRef(string Id, int Version, string Sha)
{
    /// <summary>The reference for <paramref name="questions"/>, hashed over their canonical wire form.</summary>
    public static SchemaRef Of(string id, int version, IReadOnlyDictionary<string, DecisionQuestion> questions) =>
        new(id, version, TapeJson.Sha256(DecisionRequests.QuestionsJson(questions)));
}

/// <summary>
/// ONE CALL TO ONE INSTRUMENT. <see cref="State"/> is JSON — a string, an object or an array — and it is
/// canonicalised before it is hashed or sent; its TEXT is never kept anywhere, only its hash and
/// <see cref="Sources"/>, the references the caller built it from (a tape row's id, a fetch's), so the record can
/// say what was asked about without holding a copy of the owner's or a vendor's text.
/// </summary>
public sealed record DecisionRequest
{
    /// <summary>The instrument's id: <c>typesafe-direct</c> or <c>openrouter-jev</c> (<see cref="DecisionInstruments"/>).</summary>
    public required string Instrument { get; init; }

    public required SchemaRef Schema { get; init; }
    public required string State { get; init; }
    public required IReadOnlyDictionary<string, DecisionQuestion> Questions { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
}

/// <summary>Where one call ended.</summary>
public enum DecisionStatus
{
    /// <summary>Refused by the app before anything was reserved or sent. It cost nothing and is not recorded.</summary>
    REFUSED,

    /// <summary>Sent, and the host answered with the answers and its usage.</summary>
    ANSWERED,

    /// <summary>Sent, and the host answered with an error status. No answer: the reservation stands as the cost.</summary>
    FAILED,

    /// <summary>
    /// Sent, and nothing usable came back — a timeout, a broken connection, a body this build cannot read or that
    /// answers other questions than were asked, a stop, or an answer the tape could not record. No answer: the
    /// reservation stands as the cost, unless the host still said what it used.
    /// </summary>
    UNANSWERED
}

/// <summary>
/// ONE QUESTION'S ANSWER, UNROUNDED — the numbers exactly as the host served them, parsed once and never re-formatted.
/// <see cref="Probabilities"/> is keyed by option name for a Choice and by level index (<c>"0"</c>, <c>"1"</c> …) for a
/// Score; a Noul's whole distribution is <see cref="Noul"/>, the probability of yes.
/// </summary>
public sealed record DecisionDistribution
{
    public required string Question { get; init; }
    public required DecisionKind Kind { get; init; }
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();
    public string? Choice { get; init; }
    public double? Noul { get; init; }
    public double? Score { get; init; }
    public double? Confidence { get; init; }
}

/// <summary>
/// WHAT ONE CALL CAME TO. Every field an instrument's answer is judged by: who answered (the id the host NAMED, never
/// the one asked for), whether that is the pin, what it used, what it cost — the list-price estimate and the host's own
/// billed figure kept apart — how long it took, and the answers themselves.
/// </summary>
public sealed record DecisionAnswer
{
    public required DecisionStatus Status { get; init; }
    public required string Instrument { get; init; }

    /// <summary>Why the app refused, in words. Set exactly when <see cref="Status"/> is REFUSED.</summary>
    public string? Refusal { get; init; }

    /// <summary>What went wrong, as one word a reader can group by: <c>http-429</c>, <c>timeout</c>, <c>transport</c> …</summary>
    public string? ErrorClass { get; init; }

    /// <summary>The launch-ledger row this call was reserved and settled on. Null when it was refused.</summary>
    public string? AttemptId { get; init; }

    public string? RequestedModel { get; init; }

    /// <summary>The model id the HOST'S answer named — which is the only identification that is not this app quoting itself.</summary>
    public string? AnsweredModel { get; init; }

    /// <summary>
    /// The answer did not come from the instrument's built-in pin, so it is not evidence. True for every call that
    /// named no model at all. Decided by the tape's store when the call is recorded, never by a caller.
    /// </summary>
    public bool Unpinned { get; init; } = true;

    /// <summary>The host's own id for this response (OpenRouter's <c>id</c>); TypeSafe returns none.</summary>
    public string? HostResponseId { get; init; }

    public int? HttpStatus { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }

    /// <summary>Input and output tokens at the instrument's dated list price — a calculation, never a bill.</summary>
    public decimal? EstimatedCost { get; init; }

    /// <summary>What the host reported it billed (OpenRouter's <c>usage.cost</c>), or null where it reports none.</summary>
    public decimal? BilledCost { get; init; }

    /// <summary>Measured by this app on a monotonic clock, from the send to the last byte; the API reports no timing.</summary>
    public TimeSpan? Latency { get; init; }

    public IReadOnlyDictionary<string, DecisionDistribution> Answers { get; init; } = new Dictionary<string, DecisionDistribution>();
}

/// <summary>
/// THE STRUCTURAL LIMITS, CHECKED BEFORE ANYTHING IS RESERVED OR SENT, and the canonical wire forms the hashes are taken
/// over. Here rather than in an adapter so every adapter refuses the same requests in the same words.
/// </summary>
public static class DecisionRequests
{
    /// <summary>The most source references one call may carry; each at most <see cref="MaxSourceChars"/>.</summary>
    public const int MaxSources = 64;

    public const int MaxSourceChars = 200;

    /// <summary>The most questions one call may carry. Not a vendor figure: the app's own bound on one request.</summary>
    public const int MaxQuestions = 64;

    /// <summary>
    /// THE REASON <paramref name="request"/> MAY NOT BE SENT TO <paramref name="instrument"/>, or null when it may.
    ///
    /// <para><b>Token budgets are bounded in BYTES, the unit this app measures.</b> The host counts tokens with a
    /// tokenizer this app does not have, and its own examples bill about 300 tokens for 70 bytes, so no byte count is
    /// a token count and none is recorded as one. What the app can promise is narrower and checkable: a request whose
    /// state and questions are larger IN BYTES than the instrument's documented token budget is refused here. That
    /// refuses some requests the host would take — English runs several bytes to a token — and it is not what bounds
    /// the money: the reservation is (<see cref="DecisionInstrument.Reservation"/>).</para>
    /// </summary>
    public static string? Refusal(DecisionRequest request, DecisionInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(instrument);

        if (!string.Equals(request.Instrument, instrument.Id, StringComparison.Ordinal))
            return $"the call names the instrument '{request.Instrument}' and was handed to '{instrument.Id}'";
        if (DecisionInstruments.IsAlias(instrument.RequestModel))
            return $"'{instrument.RequestModel}' is an alias, which moves when a new release ships; a call names a versioned model";
        if (UrlOrigin.Of(instrument.Endpoint) is null)
            return $"the instrument '{instrument.Id}' has no address a request can go to";

        if (request.Questions is not { Count: > 0 } questions) return "a call asks at least one question";
        if (questions.Count > MaxQuestions) return $"a call asks at most {MaxQuestions} questions; this one asks {questions.Count}";

        var limits = instrument.Limits;
        foreach (var (name, q) in questions)
        {
            if (!IsName(name))
                return $"'{name}' is not a question name: lower-case letters, digits and '_' only, at most 64";
            if (q is null) return $"the question '{name}' is empty";
            if (string.IsNullOrWhiteSpace(q.Instructions)) return $"the question '{name}' has no instructions";

            switch (q.Kind)
            {
                case DecisionKind.Choice:
                    if (q.Options.Count < 2 || q.Options.Count > limits.MaxChoiceOptions)
                        return $"the Choice '{name}' has {q.Options.Count} options; a Choice has between 2 and {limits.MaxChoiceOptions}";
                    if (q.Options.Any(o => o is null || string.IsNullOrWhiteSpace(o.Name)))
                        return $"the Choice '{name}' has an option with no name";
                    if (q.Options.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count() != q.Options.Count)
                        return $"the Choice '{name}' names one option twice";
                    break;

                case DecisionKind.Score:
                    if (q.Levels.Count < limits.MinScoreLevels || q.Levels.Count > limits.MaxScoreLevels)
                        return $"the Score '{name}' has {q.Levels.Count} levels; a Score has between {limits.MinScoreLevels} and {limits.MaxScoreLevels}";
                    if (q.Levels.Any(string.IsNullOrWhiteSpace))
                        return $"the Score '{name}' has a level with no description";
                    break;

                case DecisionKind.Noul:
                    if (q.Options.Count > 0 || q.Levels.Count > 0)
                        return $"the Noul '{name}' carries options or levels, which only a Choice or a Score has";
                    break;

                default:
                    return $"the question '{name}' is of no kind this build asks";
            }
        }

        var sha = TapeJson.Sha256(QuestionsJson(questions));
        if (request.Schema is null || !string.Equals(request.Schema.Sha, sha, StringComparison.Ordinal))
            return "the questions do not hash to the schema reference the call carries, so the record could not say what was asked";
        if (string.IsNullOrWhiteSpace(request.Schema.Id) || request.Schema.Version < 1)
            return "the schema reference names no id or no version";

        if (request.Sources.Count > MaxSources) return $"a call carries at most {MaxSources} source references";
        if (request.Sources.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > MaxSourceChars))
            return $"a source reference is empty or longer than {MaxSourceChars} characters";

        string state;
        try { state = CanonicalState(request.State); }
        catch (JsonException ex) { return "the state is not one JSON value: " + ex.Message; }
        catch (ArgumentException ex) { return ex.Message; }

        var stateBytes = Encoding.UTF8.GetByteCount(state);
        var sizes = questions.Select(kv => Encoding.UTF8.GetByteCount(QuestionJson(kv.Value))).ToList();
        if (stateBytes + sizes.Sum() > limits.MaxRequestTokens)
            return $"the state and questions are {stateBytes + sizes.Sum():N0} bytes; {instrument.Id} takes {limits.MaxRequestTokens:N0} tokens "
                   + "a request, and a request larger than that in bytes is refused before sending";
        if (stateBytes + sizes.Max() > limits.MaxStateAndQuestionTokens)
            return $"the state and the longest question are {stateBytes + sizes.Max():N0} bytes; {instrument.Id} takes "
                   + $"{limits.MaxStateAndQuestionTokens:N0} tokens for them, and a request larger than that in bytes is refused before sending";

        return null;
    }

    /// <summary>
    /// The state, canonical: one JSON value that is a string, an object or an array — what the API accepts — with its
    /// keys in ordinal order and every number as written.
    /// </summary>
    public static string CanonicalState(string state)
    {
        var canonical = TapeJson.Canonical(state ?? "");
        return canonical.Length > 0 && canonical[0] is '"' or '{' or '['
            ? canonical
            : throw new ArgumentException("the state is a string, an object or an array, and this is none of them");
    }

    /// <summary>The questions as they go on the wire, canonical: what <see cref="SchemaRef.Sha"/> is the hash of.</summary>
    public static string QuestionsJson(IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (name, q) in questions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.WritePropertyName(name);
                WriteQuestion(w, q);
            }
            w.WriteEndObject();
        }
        return TapeJson.Canonical(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>One question's wire form, canonical.</summary>
    public static string QuestionJson(DecisionQuestion question)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) WriteQuestion(w, question);
        return TapeJson.Canonical(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>The wire's word for a kind: <c>choice</c>, <c>noul</c>, <c>score</c>.</summary>
    public static string Wire(DecisionKind kind) => kind switch
    {
        DecisionKind.Choice => "choice",
        DecisionKind.Noul => "noul",
        DecisionKind.Score => "score",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    static void WriteQuestion(Utf8JsonWriter w, DecisionQuestion q)
    {
        w.WriteStartObject();
        w.WriteString("type", Wire(q.Kind));
        w.WriteString("instructions", q.Instructions);
        switch (q.Kind)
        {
            case DecisionKind.Choice:
                w.WriteStartObject("criteria");
                foreach (var o in q.Options)
                {
                    if (o.Description is { } d) w.WriteString(o.Name, d);
                    else w.WriteNull(o.Name);
                }
                w.WriteEndObject();
                break;

            case DecisionKind.Score:
                w.WriteStartArray("criteria");
                foreach (var level in q.Levels) w.WriteStringValue(level);
                w.WriteEndArray();
                break;

            case DecisionKind.Noul when q.True is not null || q.False is not null:
                w.WriteStartObject("criteria");
                if (q.True is { } t) w.WriteString("true", t);
                if (q.False is { } f) w.WriteString("false", f);
                w.WriteEndObject();
                break;
        }
        w.WriteEndObject();
    }

    static bool IsName(string? s) =>
        s is { Length: >= 1 and <= 64 } && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

/// <summary>
/// ONE CALL THAT WAS SENT, AS THE PORT HANDS IT TO THE TAPE — everything about it except the two facts the tape's store
/// decides for itself: the ORIGIN, read off <see cref="Url"/>, and whether the answer is UNPINNED, read off this build's
/// own pin for <see cref="Instrument"/>. Keyed by the launch-ledger row it was reserved on, so one reservation is one
/// record and never two. The state is here as its hash and its sources only — its text is kept nowhere.
/// </summary>
public sealed record DecisionCall
{
    public required string AttemptId { get; init; }
    public required string Instrument { get; init; }
    public required string Url { get; init; }
    public required string RequestedModel { get; init; }
    public string? AnsweredModel { get; init; }
    public string? HostResponseId { get; init; }
    public required SchemaRef Schema { get; init; }
    public required string StateSha256 { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public long? LatencyMs { get; init; }
    public int? HttpStatus { get; init; }

    /// <summary>ANSWERED, FAILED or UNANSWERED: a REFUSED call was never sent and is never recorded.</summary>
    public required DecisionStatus Status { get; init; }

    public string? ErrorClass { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }

    /// <summary>The tokens at the dated list price. Apart from <see cref="BilledCost"/>, never in its place.</summary>
    public decimal? EstimatedCost { get; init; }

    public decimal? BilledCost { get; init; }
    public string? PriceBasis { get; init; }

    /// <summary>The answers object exactly as the host served it, canonical: every probability unrounded.</summary>
    public string? Answers { get; init; }
}

/// <summary>
/// A RECORDED CALL, AS THE TAPE HOLDS IT: the call, the origin its URL has, the pin this build held for its instrument
/// when it was written, and the verdict — <see cref="Unpinned"/> true means it is not evidence, whatever it says.
/// </summary>
public sealed record DecisionCallRecord(DecisionCall Call, string? Origin, string? Pin, bool Unpinned);
