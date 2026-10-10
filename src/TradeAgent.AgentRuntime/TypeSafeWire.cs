using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Decisions;
using TradeAgent.Security;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE TYPESAFE WIRE — one in-repo client for TypeSafe's System One API (<c>POST /v1/systemone</c>), which TypeSafe
/// serves at its own host and OpenRouter at <c>/api/v1/systemone</c> in the same shapes (<c>U-decision-port</c>;
/// R02 § 6.1). Written against the published contract — OpenAPI 3.1.0 "TypeSafe" v0.2.0 and the API reference, read
/// 2026-10-09 — because there is no C# SDK and no unofficial package goes near a key.
///
/// <para><b>One instrument per instance, one request per call.</b> Where the call goes is the instrument's
/// <see cref="DecisionInstrument.Endpoint"/>, read once per call; the key is asked of the holder for THAT origin at
/// the send (<see cref="HarnessKey.ReadFor"/>), and every byte of the call goes to that same string. A key pasted for
/// another origin is withheld and forgotten, and nothing is sent. A redirect is never followed: the key goes to its
/// origin or nowhere.</para>
///
/// <para><b>Never a retry.</b> The host's 429 and 529 ask for a retry with back-off; a retry is another request, and
/// another request is another reservation, so this answers the call FAILED and the caller decides.</para>
///
/// <para><b>The host's rates, before anything else is touched</b> (<c>U-decision-card</c>). Every call is admitted by
/// <see cref="DecisionRateGate"/> — the instrument's requests and tokens a second, TradeAgent's own bound where none is
/// documented, and the hold a 429, 529 or 402 put on it — after the structural refusals and BEFORE the key is read or
/// anything reserved. A refusal there is REFUSED in words and waits for nothing. The one response header read is
/// <c>Retry-After</c>, handed to the gate — read with the status before any body, so a host's 429 holds the instrument
/// whatever its page; a non-2xx's body is never read, and an answer's is read up to <see cref="MaxAnswerBytes"/> under
/// the request's own timeout.</para>
///
/// <para><b>Reserved before it is sent, in the main database, by its own rule.</b> Every call is a row of the launch
/// ledger under <see cref="AppPrincipals.Perception"/>, admitted by <see cref="AiAttemptStore.Begin"/> against the
/// owner's one daily AI cap AND perception's own budget in the transaction that writes the reservation — never through
/// <see cref="TurnMeter"/>, which keys its slots by council role and would hand perception the chair's slot and half the
/// day. The row is held in <see cref="LiveAttempts"/> while the call flies, so a meter built meanwhile does not declare
/// it lost, and it settles on the answered id and the host's billed cost, else the input tokens at the dated price —
/// <c>pricing_basis</c> says which. A call that brings back no usage keeps its reservation as its cost.</para>
///
/// <para><b>Recorded on the tape before it settles.</b> Every call that was sent is a <c>decision_call</c> row
/// (<see cref="TapeStore.RecordDecision"/>), written before the ledger row is closed, so a crash between the two leaves a
/// record and a LAUNCHED row that the next start charges its reservation — never a settled call with no record. No tape
/// open, no call. An answer the tape could not take is not served and is charged as if it never came.</para>
/// </summary>
public sealed class TypeSafeWire : IDecisionModel, IDisposable
{
    /// <summary>A host that has not answered in this long has lost the call. Seconds, because the host answers in hundreds of milliseconds.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The most of an answer this reads. Sixty-four questions of 255 options is well under a megabyte.</summary>
    public const int MaxAnswerBytes = 4 * 1024 * 1024;

    readonly HarnessKey _key;
    readonly AiAttemptStore _attempts;
    readonly Func<TapeStore?> _tape;
    readonly Func<decimal> _cap;
    readonly Func<decimal> _budget;
    readonly Func<string?> _currency;
    readonly LiveAttempts _live;
    readonly Func<DateTimeOffset> _now;
    readonly DecisionRateGate _gate;
    readonly HttpClient _http;

    /// <param name="instrument">What is called: its address, its model ids, its price and limits.</param>
    /// <param name="key">
    /// The decision model's OWN key holder — never <see cref="HarnessKey.Shared"/>, which is the worker's — asked at
    /// every call, for the instrument's origin, and nothing else.
    /// </param>
    /// <param name="db">The main database, whose launch ledger every call is reserved and settled in.</param>
    /// <param name="tape">
    /// The tape every call that was sent is recorded on, asked at every call: the app's one store, or null while none is
    /// open — and then nothing is reserved or sent.
    /// </param>
    /// <param name="cap">
    /// The owner's daily AI cap, read at every call through the SAME delegate the meter reads, so the two can never be
    /// two ceilings. A throw reads as zero: no call.
    /// </param>
    /// <param name="budget">Perception's own daily budget inside that cap. A throw reads as zero: no call.</param>
    /// <param name="live">The process's register of launches in flight. <see cref="LiveAttempts.Shared"/> when null.</param>
    /// <param name="currency">
    /// The currency the AI's spending is kept in (<c>costs.json</c>'s). A call whose price is in another one is refused,
    /// because adding its dollars to a total kept in euros would be a wrong bill. Null or unreadable refuses too.
    /// </param>
    /// <param name="requestTimeout">How long one request may take. <see cref="DefaultRequestTimeout"/> when null.</param>
    /// <param name="transport">The handler requests go through. A test's loopback; null in the product.</param>
    /// <param name="now">The wall clock the ledger's and the record's instants are read from, and the rate gate's.</param>
    /// <param name="gate">The process's rate gate. <see cref="DecisionRateGate.Shared"/> when null; a test brings its own.</param>
    public TypeSafeWire(DecisionInstrument instrument, HarnessKey key, Database db, Func<TapeStore?> tape, Func<decimal> cap,
        Func<decimal> budget, LiveAttempts? live = null, Func<string?>? currency = null, TimeSpan? requestTimeout = null,
        HttpMessageHandler? transport = null, Func<DateTimeOffset>? now = null, DecisionRateGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tape);
        ArgumentNullException.ThrowIfNull(cap);
        ArgumentNullException.ThrowIfNull(budget);
        if (ReferenceEquals(key, HarnessKey.Shared))
            throw new ArgumentException("a decision model holds its own key, never the worker's", nameof(key));

        Instrument = instrument;
        _key = key;
        _attempts = new AiAttemptStore(db);
        _tape = tape;
        _cap = cap;
        _budget = budget;
        _live = live ?? LiveAttempts.Shared;
        _currency = currency ?? (() => CostCatalog.Read().Costs?.Currency);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _gate = gate ?? DecisionRateGate.Shared;
        // The answer's bound is MaxAnswerBytes, applied where the body is read (SendAsync): the headers come first, so the
        // client's own buffer limit would never be reached.
        _http = new HttpClient(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = requestTimeout ?? DefaultRequestTimeout
        };
    }

    public DecisionInstrument Instrument { get; }

    /// <inheritdoc />
    public async Task<DecisionAnswer> DecideAsync(DecisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // EVERY STRUCTURAL LIMIT, BEFORE ANYTHING ELSE. Nothing is asked of the key holder for a call that will not go.
        if (DecisionRequests.Refusal(request, Instrument) is { } no) return Refused(no);
        if (CurrencyRefusal() is { } money) return Refused(money);

        // NO TAPE, NO CALL: an answer nobody can record is a measurement nobody can use, and its money would be spent on
        // nothing.
        if (_tape() is null) return Refused(NoTape);

        // THE HOST'S RATE, BEFORE THE KEY IS READ OR ANYTHING RESERVED (U-decision-card): a call the rate refuses asks the
        // holder for nothing and writes no row. Admitted, it is counted at its bound until it settles below — or withdrawn,
        // if something after this refuses it, because then the host saw nothing.
        var admitted = _gate.Admit(Instrument, _now());
        if (admitted.Ticket is not { } ticket)
            return Refused(admitted.Refusal ?? "the call was refused by the decision models' rate gate, so nothing was sent");

        var mayHaveGone = false;
        try
        {
            // WHERE THIS CALL GOES, READ ONCE, and the key released for that origin and no other. Before the reservation:
            // a call that cannot be sent is not a call anybody is charged for.
            var endpoint = Instrument.Endpoint;
            var release = _key.ReadFor(UrlOrigin.Of(endpoint));
            if (release.Refusal is { } withheld)
                return Refused(Labels.DecisionKeyPastedForAnotherOrigin(withheld.PastedFor, withheld.PointsAt));
            if (release.Key is not { Length: > 0 } key) return Refused(Labels.DecisionKeyNotHeld);

            // RESERVED IN THE MAIN DATABASE BEFORE A BYTE LEAVES — admitted against the day and perception's budget in the
            // one transaction that writes the reservation. Refused: nothing written, nothing sent.
            var body = Body(request);
            var (id, refusal) = Reserve(body);
            if (id is null) return Refused(refusal ?? "the call could not be reserved, so nothing was sent");

            _live.Enter(id);
            try
            {
                // ONE REQUEST ON THIS RESERVATION, and never a second.
                mayHaveGone = true;
                var sent = await SendAsync(endpoint, key, body, request.Questions, ct);

                // COUNTED AT WHAT THE HOST SAID IT USED, OR STILL AT ITS BOUND — and held, if the host said "not now".
                var a = sent.Answer;
                ticket.Settle(sent.EndedAt, a.InputTokens is { } used ? used + (a.OutputTokens ?? 0) : null, a.HttpStatus,
                    sent.RetryAfter);

                // RECORDED BEFORE IT SETTLES, with the verdict the store reached: the pin is compared there and nowhere else.
                var answer = sent.Answer with { AttemptId = id };
                try
                {
                    var record = (_tape() ?? throw new InvalidOperationException("the tape was closed while the call was in flight"))
                        .RecordDecision(Record(id, endpoint, request, sent));
                    answer = answer with { Unpinned = record.Unpinned };
                }
                catch (Exception)
                {
                    // NOT RECORDED, SO NOT SERVED, AND CHARGED AS IF IT NEVER CAME: the reservation stands as its cost.
                    answer = Answer(DecisionStatus.UNANSWERED) with
                    {
                        AttemptId = id, ErrorClass = "not-recorded", HttpStatus = sent.Answer.HttpStatus, Latency = sent.Answer.Latency
                    };
                }

                Settle(id, answer, sent.EndedAt);
                return answer;
            }
            finally
            {
                // SETTLED, OR LEFT LAUNCHED BY A SETTLE THAT FAILED — which the next meter to open the database turns LOST,
                // keeping the reservation as the cost. Either way nothing in this process is waiting for it any more.
                _live.Leave(id);
            }
        }
        finally
        {
            // A CALL THAT NEVER LEFT GIVES ITS PLACE BACK; one that may have left and settled nothing above stays counted
            // at its bound for its second. Settle and withdraw each act once, so after a settle this does nothing.
            if (mayHaveGone) ticket.Settle(_now(), null, null, null);
            else ticket.Withdraw();
        }
    }

    /// <summary>The refusal when no tape is open to record the call on.</summary>
    public const string NoTape =
        "TradeAgent is not recording market context, so a decision model's answer could not be recorded; nothing was sent.";

    /// <summary>
    /// THE RECORD OF ONE SENT CALL: who was asked and who answered, the schema's and the state's hashes and the state's
    /// sources — never its text — the instants and the latency, the tokens, both costs apart, and the answers as served.
    /// </summary>
    DecisionCall Record(string id, string endpoint, DecisionRequest request, Sent sent)
    {
        var a = sent.Answer;
        return new DecisionCall
        {
            AttemptId = id,
            Instrument = Instrument.Id,
            Url = endpoint,
            RequestedModel = Instrument.RequestModel,
            AnsweredModel = a.AnsweredModel,
            HostResponseId = a.HostResponseId,
            Schema = request.Schema,
            StateSha256 = TapeJson.Sha256(DecisionRequests.CanonicalState(request.State)),
            Sources = request.Sources,
            RequestedAt = sent.RequestedAt,
            EndedAt = sent.EndedAt,
            LatencyMs = a.Latency is { } l ? (long)Math.Round(l.TotalMilliseconds) : null,
            HttpStatus = a.HttpStatus,
            Status = a.Status,
            ErrorClass = a.ErrorClass,
            InputTokens = a.InputTokens,
            OutputTokens = a.OutputTokens,
            EstimatedCost = a.EstimatedCost,
            BilledCost = a.BilledCost,
            PriceBasis = a.BilledCost is not null ? BilledBasis : a.InputTokens is not null ? Instrument.PriceBasis : null,
            Answers = sent.AnswersJson
        };
    }

    /// <summary>
    /// WHAT <c>pricing_basis</c> SAYS when a call is settled on the host's own billed figure rather than on the list
    /// price — the one AI figure in this product that is an API charge somebody reported rather than an equivalent this
    /// build calculated (<c>docs/COUNCIL.md</c> rule 4: three figures, never one).
    /// </summary>
    public const string BilledBasis = "billed: the host's own usage.cost on the answer";

    /// <summary>Why the call's price cannot be added to the AI's day, or null because it can.</summary>
    string? CurrencyRefusal()
    {
        string? kept;
        try { kept = _currency(); }
        catch (Exception) { kept = null; }

        return string.Equals(kept, Instrument.Currency, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{Instrument.DisplayName} is priced in {Instrument.Currency} and TradeAgent's AI spending is kept in "
              + $"{(string.IsNullOrEmpty(kept) ? "a currency it cannot read" : kept)}, so the call could not be counted against "
              + "the daily limit. Nothing was sent.";
    }

    /// <summary>
    /// THE ROW, ADMITTED OR NOT, IN ONE TRANSACTION (<see cref="AiAttemptStore.Begin"/>). The rule carries everything but
    /// the day's totals, which the store reads under its own lock — the comparison two racing calls cannot both pass.
    /// </summary>
    (string? Id, string? Refusal) Reserve(string body)
    {
        var now = _now();
        var (from, to) = OwnerDay.Window(now);
        var attempt = new AiAttempt
        {
            Id = $"decision-{now.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():n}"[..44],
            StartedAt = now,
            Runtime = Instrument.Id,
            RequestedModel = Instrument.RequestModel,
            PricingBasis = Instrument.PriceBasis,
            ReservedCost = Instrument.Reservation,
            // WHAT ENTERED THE REQUEST, AS A HASH: the canonical body, never the body.
            InputHash = Sha256Hex.Of(body),
            Role = AppPrincipals.Perception
        };
        var rule = new AiAdmissionRule
        {
            From = from,
            To = to,
            Role = AppPrincipals.Perception,
            Cap = ReadOrZero(_cap),
            RoleCap = ReadOrZero(_budget),
            Reservation = attempt.ReservedCost,
            ResumesAt = OwnerDay.Midnight(now)
        };

        try
        {
            var admission = _attempts.Begin(attempt, admit: rule);
            return admission.Admitted ? (admission.Id, null) : (null, admission.Refusal);
        }
        catch (Exception ex)
        {
            // UNLIKE A TURN, A CALL THE LEDGER WILL NOT TAKE IS NOT MADE. A turn the app failed to write down still runs
            // because stopping the mission on a database hiccup is a new way to lose it; nothing is lost by not asking a
            // decision model a question, and a call nobody reserved is spending no ceiling sees.
            return (null, $"the launch ledger could not be written ({ex.Message}), so nothing was sent");
        }
    }

    static decimal ReadOrZero(Func<decimal> read)
    {
        try { return read(); }
        catch (Exception) { return 0m; }
    }

    /// <summary>
    /// SETTLES THE ROW ON WHAT CAME BACK: the answered id, and the host's billed cost where it reported one, else the input
    /// and output tokens at the dated price. With no usage the tokens and the cost are left unknown, and
    /// <see cref="AiAttemptStore.End"/> keeps the reservation as the cost — unknown is never zero. A settle that throws
    /// leaves the row LAUNCHED, which the next restart turns LOST at its reservation: the conservative direction.
    /// </summary>
    void Settle(string id, DecisionAnswer a, DateTimeOffset endedAt)
    {
        var input = a.InputTokens;
        long? output = input is null ? null : a.OutputTokens ?? 0;
        var cost = a.BilledCost ?? (input is { } i ? Instrument.Estimate(i, output ?? 0) : null);
        var basis = a.BilledCost is not null ? BilledBasis : input is not null ? Instrument.PriceBasis : null;

        var context = Json.Write(new
        {
            decision = new
            {
                instrument = Instrument.Id,
                status = a.Status.ToString(),
                error_class = a.ErrorClass,
                http_status = a.HttpStatus,
                latency_ms = a.Latency is { } l ? (long?)Math.Round(l.TotalMilliseconds) : null
            }
        });

        try
        {
            _attempts.End(id, a.Status == DecisionStatus.ANSWERED ? 0 : 2, endedAt, input, null, null, output, null,
                a.AnsweredModel, cost, null, context, basis);
        }
        catch (Exception) { /* left LAUNCHED: see above */ }
    }

    /// <summary>
    /// THE REQUEST BODY, CANONICAL: the request id, the questions in their wire form and the state, keys in ordinal
    /// order. The state's text goes on the wire and nowhere else.
    /// </summary>
    internal string Body(DecisionRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", Instrument.RequestModel);
            w.WritePropertyName("questions");
            w.WriteRawValue(DecisionRequests.QuestionsJson(request.Questions));
            w.WritePropertyName("state");
            w.WriteRawValue(DecisionRequests.CanonicalState(request.State));
            w.WriteEndObject();
        }
        return TapeJson.Canonical(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// What one send came to: the answer, the two instants either side of it, and the instant the host asked to be left
    /// until (its <c>Retry-After</c>, as seconds after <see cref="EndedAt"/> or as a date), or null where it named none or
    /// named it unreadably.
    /// </summary>
    internal sealed record Sent(DecisionAnswer Answer, DateTimeOffset RequestedAt, DateTimeOffset EndedAt, string? AnswersJson,
        DateTimeOffset? RetryAfter = null);

    /// <summary>
    /// ONE POST, AND WHAT CAME BACK — never a throw for a host's error, a timeout or a body this cannot read: each is a
    /// status and an error class, because the caller's accounting must not depend on its catch.
    /// </summary>
    internal async Task<Sent> SendAsync(string endpoint, string key, string body,
        IReadOnlyDictionary<string, DecisionQuestion> asked, CancellationToken ct)
    {
        var requestedAt = _now();
        var clock = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // ONE DEADLINE OVER THE WHOLE EXCHANGE, the wire's own figure: sent with the headers read first, HttpClient's
        // timeout ends at the headers, so the body's read is bounded by this — the same figure, raised by nothing.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_http.Timeout);

        HttpResponseMessage response;
        try
        {
            // THE STATUS AND THE HEADERS FIRST, the body after: a host's "not now" is read even when its page is not.
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Lost("cancelled", null);
        }
        catch (OperationCanceledException)
        {
            // HttpClient's own timeout, or the exchange's deadline, arrives as a cancellation nobody asked for.
            return Lost("timeout", null);
        }
        catch (HttpRequestException)
        {
            return Lost("transport", null);
        }
        catch (Exception)
        {
            // ANYTHING ELSE THE SEND THREW — a disposed client, a request the handler would not take — is still a call
            // that was reserved and may have gone: it is answered, settled and recorded like any other lost answer.
            return Lost("send-failed", null);
        }

        int status;
        string text;
        using (response)
        {
            status = (int)response.StatusCode;

            // A HOST'S ERROR IS ITS STATUS: FAILED http-<status>, its Retry-After handed to the gate, and its body never read
            // — a page larger than the wire reads, or cut short, cannot take the status or the hold away with it.
            if (status is < 200 or > 299)
            {
                // THE ONE HEADER READ (U-decision-card): the host's word on when to come back. Parsed by the platform's own
                // reader; a value it cannot read comes back null, which is the same as none.
                var retry = response.Headers.RetryAfter;
                var failedAt = _now();
                return new Sent(Answer(DecisionStatus.FAILED) with { ErrorClass = $"http-{status}", HttpStatus = status, Latency = clock.Elapsed },
                    requestedAt, failedAt, null, retry?.Delta is { } wait ? failedAt + wait : retry?.Date);
            }

            // AN ANSWER, READ UP TO THE BOUND AND UNDER THE SAME DEADLINE: one it cannot read whole is UNANSWERED, its status
            // kept.
            try
            {
                await response.Content.LoadIntoBufferAsync(MaxAnswerBytes, deadline.Token);
                text = await response.Content.ReadAsStringAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Lost("cancelled", status);
            }
            catch (OperationCanceledException)
            {
                return Lost("timeout", status);
            }
            catch (Exception)
            {
                // Over MaxAnswerBytes, or cut short by the host or the network.
                return Lost("transport", status);
            }
        }

        var latency = clock.Elapsed;
        var endedAt = _now();

        var read = Read(text);
        if (read.Answers is { } answers && !Matches(asked, answers))
            read = read with { Answers = null, Problem = "mismatched-answer" };

        var answer = Answer(read.Answers is null ? DecisionStatus.UNANSWERED : DecisionStatus.ANSWERED) with
        {
            ErrorClass = read.Problem,
            HttpStatus = status,
            Latency = latency,
            AnsweredModel = read.Model,
            HostResponseId = read.Id,
            InputTokens = read.Input,
            OutputTokens = read.Output,
            EstimatedCost = read.Input is { } i ? Instrument.Estimate(i, read.Output ?? 0) : null,
            BilledCost = read.Billed,
            Answers = read.Answers ?? new Dictionary<string, DecisionDistribution>()
        };
        return new Sent(answer, requestedAt, endedAt, read.AnswersJson);

        Sent Lost(string why, int? code) =>
            new(Answer(DecisionStatus.UNANSWERED) with { ErrorClass = why, HttpStatus = code, Latency = clock.Elapsed },
                requestedAt, _now(), null);
    }

    DecisionAnswer Answer(DecisionStatus status) => new()
    {
        Status = status,
        Instrument = Instrument.Id,
        RequestedModel = Instrument.RequestModel
    };

    DecisionAnswer Refused(string why) => Answer(DecisionStatus.REFUSED) with { Refusal = why };

    // ---- the answer ---------------------------------------------------------------------------------------

    /// <summary>What a body said, read without inventing anything. <see cref="Answers"/> null is "nothing usable came back".</summary>
    internal sealed record Reading(
        string? Model, string? Id, long? Input, long? Output, decimal? Billed,
        IReadOnlyDictionary<string, DecisionDistribution>? Answers, string? AnswersJson, string? Problem);

    /// <summary>
    /// READS ONE 200 BODY. The usage is read whatever the answers turned out to be — a host that answered badly still
    /// said what it billed — and the answers only if EVERY one is a well-formed answer of a known kind. The answers
    /// object is also kept as served, canonical, numbers exactly as written: it is what the record keeps.
    /// </summary>
    internal static Reading Read(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new(null, null, null, null, null, null, null, "unreadable-answer"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(null, null, null, null, null, null, null, "unreadable-answer");

            var model = Str(root, "model");
            var id = Str(root, "id");
            long? input = null, output = null;
            decimal? billed = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = Whole(usage, "input_tokens");
                output = Whole(usage, "output_tokens");
                if (usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number
                    && decimal.TryParse(cost.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c >= 0m)
                    billed = c;
            }

            if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
                return new(model, id, input, output, billed, null, null, "no-answers");

            string json;
            try { json = TapeJson.Canonical(answers.GetRawText()); }
            catch (JsonException) { return new(model, id, input, output, billed, null, null, "malformed-answer"); }

            var read = new Dictionary<string, DecisionDistribution>(StringComparer.Ordinal);
            foreach (var a in answers.EnumerateObject())
            {
                if (Distribution(a.Name, a.Value) is not { } d)
                    return new(model, id, input, output, billed, null, json, "malformed-answer");
                read[a.Name] = d;
            }

            return input is null || model is null
                ? new(model, id, input, output, billed, read, json, model is null ? "no-model" : "no-usage")
                : new(model, id, input, output, billed, read, json, null);
        }
    }

    /// <summary>
    /// WHETHER THE ANSWERS ARE ANSWERS TO WHAT WAS ASKED: one per question and no other, each of its question's kind, a
    /// Choice's distribution over exactly its options with the chosen one among them, a Score's over exactly its levels.
    /// A distribution over something else is not a measurement of the question, whatever it says.
    /// </summary>
    static bool Matches(IReadOnlyDictionary<string, DecisionQuestion> asked, IReadOnlyDictionary<string, DecisionDistribution> answers)
    {
        if (answers.Count != asked.Count) return false;
        foreach (var (name, q) in asked)
        {
            if (!answers.TryGetValue(name, out var a) || a.Kind != q.Kind) return false;
            switch (q.Kind)
            {
                case DecisionKind.Choice:
                    if (!a.Probabilities.Keys.Order(StringComparer.Ordinal)
                            .SequenceEqual(q.Options.Select(o => o.Name).Order(StringComparer.Ordinal))
                        || !a.Probabilities.ContainsKey(a.Choice ?? "")) return false;
                    break;
                case DecisionKind.Score:
                    if (!a.Probabilities.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                            Enumerable.Range(0, q.Levels.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)))
                        return false;
                    break;
            }
        }
        return true;
    }

    /// <summary>One answer, or null when it is not a well-formed answer of a kind this build asks.</summary>
    static DecisionDistribution? Distribution(string name, JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object) return null;
        switch (Str(a, "type"))
        {
            case "noul":
                return Number(a, "noul") is { } p and >= 0 and <= 1
                    ? new DecisionDistribution { Question = name, Kind = DecisionKind.Noul, Noul = p }
                    : null;

            case "choice":
                if (Probabilities(a) is not { Count: > 0 } choices || Str(a, "choice") is not { } chosen) return null;
                return new DecisionDistribution
                {
                    Question = name, Kind = DecisionKind.Choice, Probabilities = choices, Choice = chosen,
                    Confidence = Number(a, "confidence")
                };

            case "score":
                if (Probabilities(a) is not { Count: > 0 } levels || Number(a, "score") is not { } score) return null;
                return new DecisionDistribution
                {
                    Question = name, Kind = DecisionKind.Score, Probabilities = levels, Score = score,
                    Confidence = Number(a, "confidence")
                };

            default:
                return null;
        }
    }

    static IReadOnlyDictionary<string, double>? Probabilities(JsonElement a)
    {
        if (!a.TryGetProperty("probabilities", out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var read = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var e in p.EnumerateObject())
        {
            if (e.Value.ValueKind != JsonValueKind.Number || !e.Value.TryGetDouble(out var v) || v is < 0 or > 1) return null;
            read[e.Name] = v;
        }
        return read;
    }

    static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    static long? Whole(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public void Dispose() => _http.Dispose();
}
