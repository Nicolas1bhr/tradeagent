using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
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
/// </summary>
public sealed class TypeSafeWire : IDecisionModel, IDisposable
{
    /// <summary>A host that has not answered in this long has lost the call. Seconds, because the host answers in hundreds of milliseconds.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The most of an answer this reads. Sixty-four questions of 255 options is well under a megabyte.</summary>
    public const int MaxAnswerBytes = 4 * 1024 * 1024;

    readonly HarnessKey _key;
    readonly Func<DateTimeOffset> _now;
    readonly HttpClient _http;

    /// <param name="instrument">What is called: its address, its model ids, its price and limits.</param>
    /// <param name="key">
    /// The decision model's OWN key holder — never <see cref="HarnessKey.Shared"/>, which is the worker's — asked at
    /// every call, for the instrument's origin, and nothing else.
    /// </param>
    /// <param name="requestTimeout">How long one request may take. <see cref="DefaultRequestTimeout"/> when null.</param>
    /// <param name="transport">The handler requests go through. A test's loopback; null in the product.</param>
    /// <param name="now">The wall clock the record's instants are read from.</param>
    public TypeSafeWire(DecisionInstrument instrument, HarnessKey key, TimeSpan? requestTimeout = null,
        HttpMessageHandler? transport = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(key);
        if (ReferenceEquals(key, HarnessKey.Shared))
            throw new ArgumentException("a decision model holds its own key, never the worker's", nameof(key));

        Instrument = instrument;
        _key = key;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = requestTimeout ?? DefaultRequestTimeout,
            MaxResponseContentBufferSize = MaxAnswerBytes
        };
    }

    public DecisionInstrument Instrument { get; }

    /// <inheritdoc />
    public async Task<DecisionAnswer> DecideAsync(DecisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // EVERY STRUCTURAL LIMIT, BEFORE ANYTHING ELSE. Nothing is asked of the key holder for a call that will not go.
        if (DecisionRequests.Refusal(request, Instrument) is { } no) return Refused(no);

        // WHERE THIS CALL GOES, READ ONCE, and the key released for that origin and no other.
        var endpoint = Instrument.Endpoint;
        var release = _key.ReadFor(UrlOrigin.Of(endpoint));
        if (release.Refusal is { } withheld)
            return Refused(Labels.DecisionKeyPastedForAnotherOrigin(withheld.PastedFor, withheld.PointsAt));
        if (release.Key is not { Length: > 0 } key) return Refused(Labels.DecisionKeyNotHeld);

        var sent = await SendAsync(endpoint, key, Body(request), request.Questions, ct);
        return sent.Answer;
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

    /// <summary>What one send came to: the answer, and the two instants either side of it.</summary>
    internal sealed record Sent(DecisionAnswer Answer, DateTimeOffset RequestedAt, DateTimeOffset EndedAt, string? AnswersJson);

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

        int status;
        string text;
        try
        {
            using var response = await _http.SendAsync(message, ct);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Lost("cancelled", null);
        }
        catch (TaskCanceledException)
        {
            // HttpClient's own timeout arrives as a cancellation nobody asked for.
            return Lost("timeout", null);
        }
        catch (HttpRequestException)
        {
            return Lost("transport", null);
        }

        var latency = clock.Elapsed;
        var endedAt = _now();

        if (status is < 200 or > 299)
            return new Sent(Answer(DecisionStatus.FAILED) with { ErrorClass = $"http-{status}", HttpStatus = status, Latency = latency },
                requestedAt, endedAt, null);

        var read = Read(text);
        if (read.Answers is { } answers && !Matches(asked, answers))
            read = read with { Answers = null, Problem = "mismatched-answer" };

        var answer = Answer(read.Answers is null ? DecisionStatus.UNANSWERED : DecisionStatus.ANSWERED) with
        {
            ErrorClass = read.Problem,
            HttpStatus = status,
            Latency = latency,
            AnsweredModel = read.Model,
            Unpinned = !DecisionInstruments.IsPinned(Instrument.Id, read.Model),
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

            var json = TapeJson.Canonical(answers.GetRawText());
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
