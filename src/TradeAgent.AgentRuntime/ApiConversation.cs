using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// A CONVERSATION THE APP ITSELF DRIVES, a request at a time.
///
/// <para>Every other conversation in this product is a vendor program the app starts and then reads
/// the output of. This one has no program in it: the app composes the request, counts what it is
/// about to send, sends it, executes any tool the model asked for, decides whether another request is
/// affordable, and sends again — until the model ends the turn or a bound trips. That is the whole
/// difference the harness was built for (<c>docs/COUNCIL.md</c>, "Workers run on an app-owned
/// harness"): on a CLI the caps are advisory because the app cannot see the boundary; here the app
/// IS the boundary.</para>
///
/// <para><b>What a turn is.</b> One <see cref="SendMissionAsync"/> or <see cref="SendAsync"/> is one
/// turn, however many requests it takes. <see cref="IAgentConversation.TurnEnded"/> is raised once,
/// on every path out, carrying the usage SUMMED over every response with the model named on each —
/// summed rather than last-wins for the reason <see cref="TurnUsage.Plus"/> gives: keeping the last
/// of several reports understates the bill, which is the one direction that lets the ceiling be
/// walked past. A three-request turn priced as one request is a turn the owner is not being charged
/// for.</para>
///
/// <para><b>Usage nobody reported stays null.</b> If no response carried a usage object, the turn
/// ends with <c>Usage = null</c> — not a zeroed one — so the launch ledger charges the reservation
/// and the row says the usage never arrived (<c>U-budget-reserve</c>'s rule: the reservation
/// stands).</para>
/// </summary>
/// <param name="workspace">The role's own home, read at every turn: a prepare may have rebuilt it.</param>
/// <param name="apiKey">The pasted key, read at every turn. Null starts nothing at all.</param>
/// <param name="tools">
/// The tool surface for this turn, asked for per turn rather than captured, because the owner's
/// choices and the role's grants can change between turns. Default deny lives in the surface itself.
/// </param>
/// <param name="allowance">
/// The owner's per-turn bound in tokens. Checked BEFORE each request, never after: a check after the
/// request has already paid for the thing it was meant to prevent.
/// </param>
public sealed class ApiConversation(
    RuntimeManifest manifest,
    string role,
    Func<string> workspace,
    Func<string?> apiKey,
    Func<string?> model,
    Func<IWorkerTools> tools,
    Func<string?> attempt,
    Func<TurnAllowance>? allowance = null,
    TimeSpan? requestTimeout = null,
    HttpMessageHandler? transport = null,
    Func<DateTimeOffset>? now = null) : IAgentConversation, IAdmittedConversation, IDisposable
{
    /// <summary>
    /// THE MOST REQUESTS ONE TURN MAY MAKE, however cheap they are.
    ///
    /// The token bounds stop a turn that is spending; this stops a turn that is not. A model that
    /// answers every tool result with another tool call and never produces a message would loop until
    /// something else broke, and "something else" would be the provider's own limits — which is
    /// exactly the arrangement the harness exists to replace.
    /// </summary>
    public const int MaxRequestsPerTurn = 24;

    /// <summary>
    /// THE MOST TOOL OUTPUT ONE TURN MAY PUT INTO CONTEXT, in bytes.
    ///
    /// <para>It is a byte figure of its own rather than a share of <see cref="TurnAllowance"/>, and
    /// that is deliberate: the allowance is in TOKENS, "characters are not tokens and bytes are not
    /// tokens, and converting one to the other would be exactly the estimate this record exists to
    /// avoid" (<see cref="TurnContext"/>). Retrieval is the one quantity the app controls outright
    /// (<c>docs/COUNCIL.md</c> rule 4), so it is bounded in the unit the app actually measures.</para>
    /// </summary>
    public const int MaxRetrievalBytesPerTurn = 512 * 1024;

    /// <summary>The most one tool call may return. Bounded per call as well as per turn.</summary>
    public const int MaxRetrievalBytesPerCall = 64 * 1024;

    /// <summary>The most of the role's mission file that goes into the system message.</summary>
    public const int MaxMissionChars = 64 * 1024;

    /// <summary>The turn ended because the model ended it.</summary>
    public const int Completed = 0;

    /// <summary>The turn ended because a bound tripped. See <see cref="ErrorCode.CONTEXT_BUDGET_EXCEEDED"/>.</summary>
    public const int BudgetExceeded = 1;

    /// <summary>The turn ended because the provider could not be reached or refused the request.</summary>
    public const int ProviderFailed = 2;

    /// <summary>Nothing was started: no key, or the app refused the launch.</summary>
    public const int NotStarted = -1;

    /// <summary>
    /// HOW THE TURN ENDED, AS THE WORD THE LAUNCH LEDGER CARRIES (<c>ai_attempt.context</c>, through
    /// <see cref="AgentTurnEnded.Outcome"/> and <see cref="TurnContext.Ended"/>).
    ///
    /// <para>An exit code cannot say this. <see cref="BudgetExceeded"/> is a turn that did its work and
    /// was stopped by the app before a request it had decided not to send; a provider that refused is a
    /// turn that did not work at all; and both would be "1" and "2" to anybody reading the table in
    /// three weeks. Rule 4 keeps enforcement apart from billing, and a total the owner cannot tell a
    /// bounded turn from a broken one in is the reading that rule forbids.</para>
    ///
    /// <para>The budget word is the error code's own name rather than a sentence, so the row, the
    /// catalogue entry the owner reads and the repair on the Safety page are one thing.</para>
    /// </summary>
    public const string EndedCompleted = "completed";

    /// <inheritdoc cref="EndedCompleted"/>
    public static readonly string EndedOverBudget = nameof(ErrorCode.CONTEXT_BUDGET_EXCEEDED);

    /// <inheritdoc cref="EndedCompleted"/>
    public const string EndedProviderFailed = "provider-failed";

    /// <inheritdoc cref="EndedCompleted"/>
    public const string EndedNotStarted = "not-started";

    /// <inheritdoc cref="EndedCompleted"/>
    public const string EndedCancelled = "cancelled";

    readonly List<ChatTurn> _history = [];
    readonly Lock _historyLock = new();
    readonly List<string> _typedMeanwhile = [];
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    readonly HttpClient _http = new(transport ?? new SocketsHttpHandler(), disposeHandler: true)
    {
        // SECONDS, NOT MINUTES, AND INJECTABLE. A never-answering endpoint must fail fast: the
        // failure that costs most here is the one that looks like thinking, which this codebase has
        // already measured once on Windows (see AgentSession's stdin comment).
        Timeout = requestTimeout ?? ApiAgentRuntime.DefaultRequestTimeout
    };

    CancellationTokenSource? _cts;
    bool _busy;

    public bool Busy => _busy;

    /// <summary>The council role these turns belong to. TradeAgent's own answer, never the model's.</summary>
    public string Role => role;

    public IReadOnlyList<ChatTurn> History
    {
        get { lock (_historyLock) return _history.ToArray(); }
    }

    public event Action<ChatTurn>? TurnAdded;
    public event Action<string>? Delta;
    public event Action? StateChanged;
    public event Action<AgentTurnEnded>? TurnEnded;

    /// <inheritdoc />
    public Func<string, AiAdmission>? Admit { get; set; }

    /// <inheritdoc />
    public Func<string, bool>? RecordTyped { get; set; }

    /// <summary>
    /// Checks the one precondition and says so in the conversation. No process is started because
    /// there is none: the next message is the first thing that touches the provider.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(workspace());
        Append(new ChatTurn(ChatRole.System,
            apiKey() is { Length: > 0 }
                ? $"{manifest.DisplayName} is ready."
                : Labels.HarnessKeyNotHeld,
            _now()));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (_busy) { Queue(message); return; }

        var admission = Admit?.Invoke(message) ?? AiAdmission.Unrecorded;
        if (!admission.Admitted) { Hold(message, admission.Refusal!); return; }

        Append(new ChatTurn(ChatRole.You, message, _now()));
        await RunAsync(message, ct);
    }

    /// <inheritdoc />
    public Task SendMissionAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return Task.CompletedTask;
        if (_busy)
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                "The AI is still working on the previous message.");
        return RunAsync(message, ct);
    }

    /// <inheritdoc />
    public void Queue(string message) => Hold(message, Labels.HeldWhileTheAiIsWorking);

    /// <inheritdoc />
    public IReadOnlyList<string> TakeTyped()
    {
        lock (_historyLock)
        {
            if (_typedMeanwhile.Count == 0) return [];
            var taken = _typedMeanwhile.ToArray();
            _typedMeanwhile.Clear();
            return taken;
        }
    }

    public Task CancelAsync()
    {
        try { _cts?.Cancel(); } catch (Exception) { /* the turn is already over */ }
        return Task.CompletedTask;
    }

    public Task StopAsync() => CancelAsync();

    // ---- the turn ---------------------------------------------------------------------------------

    /// <summary>
    /// One turn: the role's mission and the Situation in, the provider's tool-calling loop executed
    /// BY THIS METHOD, and one <see cref="TurnEnded"/> out whatever happened.
    /// </summary>
    async Task RunAsync(string message, CancellationToken ct)
    {
        var startedAt = _now();
        var transcript = new Transcript();
        var exitCode = NotStarted;
        var outcome = EndedNotStarted;
        TurnUsage? usage = null;

        // REFUSED BEFORE ANYTHING IS SENT. No key is not an error the owner has to read a log for:
        // it is a sentence in the conversation, and the turn still ends so the loop and the ledger
        // both account for it rather than losing a turn that vanished.
        var key = apiKey();
        if (key is not { Length: > 0 })
        {
            Append(new ChatTurn(ChatRole.System, Labels.HarnessKeyNotHeld, _now()));
            transcript.Note("no key is held, so nothing was sent");
            TurnEnded?.Invoke(new AgentTurnEnded(NotStarted, _now() - startedAt, transcript.Text, _now())
            {
                Outcome = EndedNotStarted
            });
            return;
        }

        SetBusy(true);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            (exitCode, usage, outcome) = await RunRequestsAsync(key, message, transcript, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            (exitCode, outcome) = (NotStarted, EndedCancelled);
            Append(new ChatTurn(ChatRole.System, "Stopped.", _now()));
            transcript.Note("the turn was cancelled");
        }
        catch (TradeAgentException ex)
        {
            (exitCode, outcome) = (ProviderFailed, EndedProviderFailed);
            Append(new ChatTurn(ChatRole.System, ex.Message, _now()));
            transcript.Note(ex.Message);
        }
        catch (Exception ex)
        {
            (exitCode, outcome) = (ProviderFailed, EndedProviderFailed);
            Append(new ChatTurn(ChatRole.System, $"The AI could not be reached: {ex.Message}", _now()));
            transcript.Note(ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            TurnEnded?.Invoke(new AgentTurnEnded(exitCode, _now() - startedAt, transcript.Text, _now())
            {
                // NULL WHEN NOTHING REPORTED ANY, never a zero. A turn whose usage never arrived is
                // charged its reservation by the launch ledger; a zeroed usage would be charged
                // nothing and would read as a free turn.
                Usage = usage,

                // THE ROW SAYS HOW IT ENDED. A turn the app stopped on its own bound is a different
                // fact from a turn that failed, and `ai_attempt.exit_code` cannot carry the
                // difference — see EndedCompleted.
                Outcome = outcome
            });
        }
    }

    /// <summary>
    /// The request loop. Returns how the turn ended and what every response together reported.
    /// </summary>
    async Task<(int ExitCode, TurnUsage? Usage, string Outcome)> RunRequestsAsync(string key, string message,
        Transcript transcript, CancellationToken ct)
    {
        var bound = allowance?.Invoke() ?? TurnAllowance.Default;
        var surface = tools();
        var messages = new List<ProviderMessage>
        {
            ProviderMessage.System(Mission()),
            ProviderMessage.User(message)
        };

        TurnUsage? total = null;
        var retrieved = 0;
        var requests = 0;

        while (true)
        {
            // BEFORE THE REQUEST, ALWAYS. Every quantity is compared against its bound here, where
            // refusing costs nothing; after the response the provider has already done the work and
            // the only thing left to bound is the NEXT turn, which is what the daily ceiling does.
            if (Exceeded(bound, total, retrieved, requests) is { } why)
            {
                // STAGED FILES ARE KEPT. Whatever the turn already wrote into `out/` or `trading/` is
                // the app's to validate and publish; deleting it because the turn ran out of context
                // would throw away work the owner has already paid for.
                Append(new ChatTurn(ChatRole.System, why, _now()));
                transcript.Note(why);
                return (BudgetExceeded, total, EndedOverBudget);
            }

            var body = RequestBody(messages, surface, bound);
            requests++;
            // THE INPUT THE APP IS ABOUT TO SEND, COUNTED BY THE APP, beside the attempt the cost is
            // committed against. Bytes, because bytes are what this end can measure — the token
            // figures above are the provider's own and are never derived from these.
            transcript.Request(requests, Encoding.UTF8.GetByteCount(body), attempt());

            var answer = await PostAsync(key, body, ct);
            if (answer.Usage is { } reported) total = total is null ? reported : total.Plus(reported);

            if (answer.Text is { Length: > 0 } text)
            {
                AppendAi(text);
                transcript.Message(text);
            }

            if (answer.Calls.Count == 0) return (Completed, total, EndedCompleted);

            // THE TOOL LOOP IS THE APP'S, not the provider's. Each call is answered by this process,
            // recorded, bounded, and put back into the conversation as a tool message.
            messages.Add(ProviderMessage.Assistant(answer.Text, answer.Calls));
            foreach (var call in answer.Calls)
            {
                var result = await Invoke(surface, call, retrieved, ct);
                retrieved += Encoding.UTF8.GetByteCount(result.Content);
                messages.Add(ProviderMessage.Tool(call.Id, result.Content));
                transcript.Tool(call, result);
                Append(new ChatTurn(ChatRole.Tool,
                    result.Served ? $"Used {call.Name}" : $"Refused {call.Name}", _now()));
            }
        }
    }

    /// <summary>
    /// WHICH BOUND HAS BEEN REACHED, in the owner's words, or null for "another request is allowed".
    ///
    /// The input and output figures are the provider's own reported cumulative totals — the only
    /// honest token counts in this loop — and retrieval is the app's own byte count of what its tools
    /// handed the model. Reaching a bound EXACTLY is already too late for another request: the next
    /// one would carry every token already counted plus whatever it adds.
    /// </summary>
    static string? Exceeded(TurnAllowance bound, TurnUsage? used, int retrieved, int requests)
    {
        if (requests >= MaxRequestsPerTurn)
            return $"This turn has made {requests} requests, which is all one turn may make. "
                   + "Write down where you got to; the next turn starts fresh.";

        if (used is not null && used.InputTokens >= bound.InputTokens)
            return $"This turn has used {used.InputTokens:N0} input tokens of the "
                   + $"{bound.InputTokens:N0} one turn is allowed, so no further request was sent. "
                   + "Anything already written to out/ or trading/ is kept.";

        if (used is not null && used.OutputTokens >= bound.OutputTokens)
            return $"This turn has used {used.OutputTokens:N0} output tokens of the "
                   + $"{bound.OutputTokens:N0} one turn is allowed, so no further request was sent. "
                   + "Anything already written to out/ or trading/ is kept.";

        if (retrieved >= MaxRetrievalBytesPerTurn)
            return $"This turn's tools have returned {retrieved:N0} bytes, which is all one turn may "
                   + $"read ({MaxRetrievalBytesPerTurn:N0}), so no further request was sent. "
                   + "Anything already written to out/ or trading/ is kept.";

        return null;
    }

    /// <summary>
    /// Runs one tool call under this turn's remaining retrieval budget, and never throws into the
    /// loop: a tool that failed is a refusal the model is told about, not the end of the turn.
    /// </summary>
    async Task<ToolAnswer> Invoke(IWorkerTools surface, ToolRequest call, int retrieved, CancellationToken ct)
    {
        var left = MaxRetrievalBytesPerTurn - retrieved;
        if (left <= 0)
            return ToolAnswer.Refused($"this turn has already read its {MaxRetrievalBytesPerTurn:N0} bytes");

        ToolAnswer answer;
        try { answer = await surface.InvokeAsync(call, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { answer = ToolAnswer.Refused($"'{call.Name}' failed: {ex.Message}"); }

        // TRUNCATED HERE AND SAID SO. A surface that returns more than the turn has left is bounded
        // at the boundary rather than trusted, because what enters context is this method's business.
        var bytes = Encoding.UTF8.GetByteCount(answer.Content);
        if (bytes <= left) return answer;

        var cut = Cut(answer.Content, left);
        return answer with { Content = cut + $"\n[cut: this turn has {left:N0} bytes of reading left]" };
    }

    /// <summary>The role's mission file, bounded. The app wrote it; the worker cannot change it.</summary>
    string Mission()
    {
        try
        {
            var file = Path.Combine(workspace(), "AGENTS.md");
            if (!File.Exists(file)) return $"You are the {CouncilRoles.Title(role)}.";
            var text = File.ReadAllText(file);
            return text.Length <= MaxMissionChars ? text : text[..MaxMissionChars];
        }
        // A mission file that cannot be read must not stop the turn: the Situation the app composed
        // still describes the world, and a role with no mission text is a worse turn rather than none.
        catch (Exception) { return $"You are the {CouncilRoles.Title(role)}."; }
    }

    // ---- the wire ---------------------------------------------------------------------------------

    /// <summary>
    /// The request body. The model, the messages, the tools this launch was granted, and the
    /// provider's own MAX-OUTPUT parameter — whose NAME comes off the manifest, because vendors rename
    /// it (<c>max_tokens</c> became <c>max_completion_tokens</c>) and that should be a data fix.
    /// </summary>
    string RequestBody(List<ProviderMessage> messages, IWorkerTools surface, TurnAllowance bound)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model() ?? manifest.DefaultModel ?? "",
            ["messages"] = messages,
            [manifest.MaxOutputParam] = bound.OutputTokens
        };

        if (surface.Offered.Count > 0)
            body["tools"] = surface.Offered
                .Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.Parameters } })
                .ToList();

        return Json.Write(body);
    }

    async Task<ProviderAnswer> PostAsync(string key, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, manifest.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // THE TIMEOUT, NAMED AS ONE. HttpClient reports its own timeout as a cancellation, which
            // read as "the owner pressed stop" — two different facts with two different repairs.
            throw new TradeAgentException(ErrorCode.AI_AUTH_TIMEOUT,
                $"{manifest.DisplayName} did not answer within {_http.Timeout.TotalSeconds:N0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE,
                $"{manifest.DisplayName} could not be reached: {ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new TradeAgentException(ErrorCode.AI_AUTH_FAILED,
                    $"{manifest.DisplayName} answered {(int)response.StatusCode}: {Cut(text.Trim(), 400)}");
            return ProviderAnswer.Read(text);
        }
    }

    // ---- history ----------------------------------------------------------------------------------

    void AppendAi(string text)
    {
        Delta?.Invoke(text);
        Append(new ChatTurn(ChatRole.Ai, text, _now()));
    }

    void Append(ChatTurn turn)
    {
        lock (_historyLock) _history.Add(turn);
        TurnAdded?.Invoke(turn);
    }

    /// <summary>
    /// Keeps the owner's words and says where they went — the same two callers and the same behaviour
    /// <see cref="AgentSession.Queue"/> has, because a message that disappeared into a queue with no
    /// acknowledgement reads exactly like a message that was dropped.
    /// </summary>
    void Hold(string message, string said)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var text = message.Trim();

        var recorded = false;
        try { recorded = RecordTyped?.Invoke(text) ?? false; }
        catch (Exception) { recorded = false; }
        if (!recorded) lock (_historyLock) _typedMeanwhile.Add(text);

        Append(new ChatTurn(ChatRole.You, message, _now()));
        Append(new ChatTurn(ChatRole.System, said, _now()));
    }

    void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        StateChanged?.Invoke();
    }

    static string Cut(string text, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= bytes) return text;
        var chars = text.AsSpan();
        var take = Math.Min(chars.Length, bytes);
        while (take > 0 && Encoding.UTF8.GetByteCount(chars[..take]) > bytes) take--;
        return new string(chars[..take]);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// WHAT THE APP SAW OF THIS TURN, in the newline-delimited-JSON shape <see cref="TurnContext"/>
    /// already reads — so the launch ledger's context record is composed by the same parser for the
    /// harness as for a CLI, rather than by a second one that has to be kept in step.
    /// </summary>
    sealed class Transcript
    {
        readonly StringBuilder _text = new();

        public string Text => _text.ToString();

        public void Request(int n, int bytes, string? attempt) =>
            Line(new { type = "request.sent", request = n, body_bytes = bytes, attempt });

        public void Message(string text) =>
            Line(new { type = "item.completed", item = new { id = $"msg-{_text.Length}", type = "agent_message", text } });

        public void Tool(ToolRequest call, ToolAnswer answer) =>
            Line(new
            {
                type = "item.completed",
                item = new
                {
                    id = call.Id.Length > 0 ? call.Id : $"call-{_text.Length}",
                    type = "tool_call",
                    name = call.Name,
                    served = answer.Served,
                    aggregated_output = answer.Content
                }
            });

        public void Note(string what) => Line(new { type = "turn.note", message = what });

        void Line(object o) => _text.Append(Json.Write(o)).Append('\n');
    }
}

/// <summary>
/// A CONVERSATION WHOSE LAUNCHES PASS THE METER'S ADMISSION GATE, and whose owner's words survive a
/// restart.
///
/// <para>Both of these used to be properties on <see cref="AgentSession"/>, found by a type test in
/// <c>TurnMeter.Attach</c>. That was correct while there was one implementation and silently wrong
/// the moment there were two: a harness conversation would have been metered after the fact and
/// never admitted, so a role on the harness could take the last of the day's allowance and be
/// recorded once the provider had already been paid — the exact defect <c>U-budget-reserve</c>
/// removed from the owner's own chat.</para>
/// </summary>
public interface IAdmittedConversation
{
    /// <summary>
    /// OPENS THE DURABLE RECORD AND COMMITS THE COST before the turn runs, and answers whether it may
    /// run at all. Set by <c>TurnMeter.Attach</c>; null in a host with no meter, and then the turn
    /// runs and the row is written when it ends.
    /// </summary>
    Func<string, AiAdmission>? Admit { get; set; }

    /// <summary>Writes the owner's words somewhere that survives this process, and says whether it did.</summary>
    Func<string, bool>? RecordTyped { get; set; }
}

/// <summary>
/// One message on the wire. A record with nullable members rather than a hierarchy, because this is
/// a JSON shape and not a domain model: what the provider's contract accepts is one object with some
/// fields absent.
/// </summary>
public sealed record ProviderMessage
{
    public string Role { get; init; } = "user";
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
    public IReadOnlyList<ProviderToolCall>? ToolCalls { get; init; }

    public static ProviderMessage System(string content) => new() { Role = "system", Content = content };
    public static ProviderMessage User(string content) => new() { Role = "user", Content = content };

    public static ProviderMessage Assistant(string? content, IReadOnlyList<ToolRequest> calls) => new()
    {
        Role = "assistant",
        Content = content,
        ToolCalls = [.. calls.Select(c => new ProviderToolCall
        {
            Id = c.Id,
            Function = new ProviderToolFunction { Name = c.Name, Arguments = c.Arguments }
        })]
    };

    public static ProviderMessage Tool(string callId, string content) =>
        new() { Role = "tool", ToolCallId = callId, Content = content };
}

public sealed record ProviderToolCall
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "function";
    public ProviderToolFunction Function { get; init; } = new();
}

public sealed record ProviderToolFunction
{
    public string Name { get; init; } = "";
    public string Arguments { get; init; } = "";
}

/// <summary>
/// ONE RESPONSE, READ WITHOUT INVENTING ANYTHING. A body this parser does not recognise yields no
/// text and no calls, which ends the turn — the same rule <see cref="TurnUsage.Read"/> keeps: a shape
/// nobody here understands contributes nothing rather than a guess.
/// </summary>
public sealed record ProviderAnswer(string? Text, IReadOnlyList<ToolRequest> Calls, TurnUsage? Usage)
{
    public static ProviderAnswer Read(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new ProviderAnswer(null, [], null); }

        using (doc)
        {
            var root = doc.RootElement;
            var usage = TurnUsage.Read(root);

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
                return new ProviderAnswer(null, [], usage);

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return new ProviderAnswer(null, [], usage);

            var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            var calls = new List<ToolRequest>();
            if (message.TryGetProperty("tool_calls", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var call in list.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object) continue;
                    var id = Str(call, "id") ?? "";
                    if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
                    var name = Str(fn, "name");
                    if (name is not { Length: > 0 }) continue;
                    calls.Add(new ToolRequest(id, name, Str(fn, "arguments") ?? ""));
                }

            return new ProviderAnswer(text, calls, usage);
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
