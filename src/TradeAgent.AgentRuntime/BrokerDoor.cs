using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Security;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE BROKER'S SHELL-FREE DOOR (<c>U-thinking-seats-b</c>, <c>docs/ORCHESTRATION.md</c> § 2.7): the app's own MCP server, on
/// loopback, through which a broker-only seat reaches the app without a shell.
///
/// <para><b>One door, one port, chosen at start:</b> a listener on <c>127.0.0.1:0</c> speaking the smallest subset of MCP's
/// streamable HTTP that codex 0.160.1 needs — HTTP/1.1 with keep-alive, a JSON-RPC body per POST answered with a JSON body,
/// <c>initialize</c>, <c>notifications/initialized</c>, <c>ping</c>, <c>tools/list</c> and <c>tools/call</c>. No server-sent
/// events: a GET is answered 405, which the transport reads as "this server opens no stream". codex speaks MCP over stdio or
/// streamable HTTP only, and with <c>CODEX_EXEC_SERVER_URL=none</c> over HTTP only (the thinking-seats survey, § 4), so this is
/// the one transport a broker-only codex seat can use.</para>
///
/// <para><b>It adds no authority.</b> What it serves is the harness's own surface — <see cref="GrantedWorkerTools"/>, the same
/// object, so the same default deny, the same <c>tool_call</c> rows, the same write roots — and every operation still goes
/// through the gateway's own handler under the identity the app assigned. There is no operator op behind it (AUTH-001) and no
/// op the pipe lacks.</para>
/// </summary>
public sealed class BrokerDoor : IAsyncDisposable
{
    /// <summary>The path the door answers on. Anything else is a 404.</summary>
    public const string Route = "/mcp";

    /// <summary>The largest request body the door reads. A tool call's arguments are a page of text, not a dataset.</summary>
    public const int MaxRequestBytes = 1 << 20;

    /// <summary>The largest header block the door reads before it gives up on a request.</summary>
    public const int MaxHeaderBytes = 16 * 1024;

    /// <summary>The MCP revision this door speaks when the client names none it knows.</summary>
    public const string ProtocolVersion = "2025-06-18";

    static readonly string[] KnownProtocols = ["2025-06-18", "2025-03-26"];

    readonly ConcurrentDictionary<Connection, byte> _live = new();
    readonly CancellationTokenSource _stop = new();
    TcpListener? _listener;
    Task? _loop;

    /// <summary>
    /// Where the door is listening, once <see cref="Start"/> has run — <c>http://127.0.0.1:&lt;port&gt;/mcp</c>, the URL a seat's
    /// MCP configuration names (part c). Null before the start and after the stop.
    /// </summary>
    public Uri? Url { get; private set; }

    /// <summary>The launch grants the door recognises: the process-wide register, as the pipe's.</summary>
    public AgentGrants Grants { get; init; } = AgentGrants.Shared;

    /// <summary>What the door's pinned program is called in a refusal: the peer-image rule's <see cref="PeerRule.Program"/>.</summary>
    public const string RuntimeWord = "pinned AI runtime";

    /// <summary>
    /// WHAT THE PROCESS ON THE OTHER END MUST BE: the pinned codex program, by path and digest (<see cref="PeerRule"/>, the pipe's
    /// own rule with the runtime's pin), read per connection. Null — no pin this build recognises — refuses every grant: the
    /// door's only legitimate client is that one program, with no shell and no children, so the image conjunct is exact.
    /// </summary>
    public Func<PeerRule?> Image { get; init; } = () => null;

    /// <summary>
    /// THE PRODUCT'S IMAGE RULE: the codex program this build pins, where this computer has it. The pin is the BUILT-IN
    /// manifest's — code, never <c>runtimes.json</c>, which replaces a manifest whole and which any same-user process can rewrite
    /// (<c>docs/ORCHESTRATION.md</c> § 2.7, "Seat classes live in code"). The managed-runtime folder is NOT forbidden here, as it
    /// is to the pipe's peer: it is where the pinned codex is installed. The agent's workspace is. Null when this build pins no
    /// codex for this computer, which refuses every grant at the door.
    /// </summary>
    public static PeerRule? PinnedCodex()
    {
        var codex = RuntimeCatalog.BuiltIn().FirstOrDefault(m => m.Id == CodexId);
        if (codex?.Install.ExecutableSha256 is not { Length: > 0 } pin) return null;
        return new PeerRule(Program(new CliAgentRuntime(codex).ResolveExecutable()), pin.Trim(), ToolsDir: null,
            WorkspaceDir: Paths.Workspace, RuntimeWord);
    }

    /// <summary>
    /// THE FILE THAT RUNS, not the name it was found by. A runtime found on PATH is often a link (the vendor's own installer puts
    /// <c>~/.local/bin/codex</c> → its release folder's <c>bin/codex</c>), and the kernel names a peer by the program it runs,
    /// resolved — so the rule records the link's final target, which is also the file the pin was hashed through. MEASURED on the
    /// dev Mac with codex 0.160.1: recording the link refused the pinned codex inside its own attempt. A path that cannot be
    /// resolved is kept as found, and then refuses as not the pin rather than passing.
    /// </summary>
    static string? Program(string? found)
    {
        if (found is null) return null;
        try { return File.ResolveLinkTarget(found, returnFinalTarget: true)?.FullName ?? found; }
        catch (Exception) { return found; }
    }

    /// <summary>The built-in manifest whose program the door's peer must be.</summary>
    public const string CodexId = "codex";

    /// <summary>
    /// WHICH PROCESS HOLDS THE OTHER END OF A CONNECTION THE DOOR ACCEPTED, from its two endpoints — the door's, the client's —
    /// asked once per accepted connection. Overridable so a test can hand the check an answer the kernel will not give on
    /// demand; the product never sets it.
    /// </summary>
    public Func<IPEndPoint, IPEndPoint, PeerFacts?> PeerOf { get; init; } = TcpPeer.OwnerOf;

    /// <summary>The program a pid runs, as the kernel names it. Overridable for the same reason; the product never sets it.</summary>
    public Func<int, string?> ImageOf { get; init; } = PeerImage.PathOf;

    /// <summary>
    /// THE SURFACE ONE GRANT IS SERVED, or null for none: the harness's own <see cref="GrantedWorkerTools"/> under the grant's role
    /// and attempt (<see cref="ToolsFor"/>). Asked per request, so a grant's surface is the one the app composes now.
    /// </summary>
    public Func<AgentGrant, IWorkerTools?> Tools { get; init; } = _ => null;

    /// <summary>
    /// The most one tool result carries over the door. A longer answer is cut at a character boundary, with words saying how
    /// big it was and that the rest is fetched by id — the harness's own retrieval bound per call.
    /// </summary>
    public const int MaxResultBytes = GrantedWorkerTools.MaxBytesPerCall;

    /// <summary>
    /// THE SURFACE THE APP SERVES A GRANT ON THE DOOR — the harness's, the same class with the same default deny, rows and write
    /// roots (<c>docs/ORCHESTRATION.md</c> § 2.9, one authority implementation) — under the grant's role, its attempt, and its
    /// ORDER permission as issued: an Operations launch issued beside a neighbour reaches the gateway as the pipe would serve it,
    /// withheld (<see cref="Gateway.GatewayPipeServer.CallAsGrantAsync"/>), never as its role alone.
    /// </summary>
    public static IWorkerTools ToolsFor(AgentGrant grant, Func<string, string> home, Func<Gateway.GatewayPipeServer?> server,
        Core.Db.ToolCallStore? ledger) =>
        new GrantedWorkerTools(grant.Role,
            home: () => home(grant.Role),
            attempt: () => grant.AttemptId,
            gateway: () => server() is { } s ? new GrantCalls(s, grant) : null,
            ledger: ledger,
            session: () => $"door-{grant.Role}");

    /// <summary>The gateway seen through one grant: every call carries the grant itself, whatever role and attempt it is handed.</summary>
    sealed class GrantCalls(Gateway.GatewayPipeServer server, AgentGrant grant) : IGatewayCalls
    {
        public Task<IpcResponse> CallAsync(IpcRequest req, string? role, string? attemptId, CancellationToken ct = default) =>
            server.CallAsGrantAsync(req, grant, ct);
    }

    /// <summary>
    /// The door's own record: a line per decision, never a token. The app writes it to its engineering log; a test reads it.
    /// </summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Opens the door on an ephemeral loopback port, and subscribes to the register's endings while it is open — here and not in
    /// a constructor, because <see cref="Grants"/> is an <c>init</c> property. Once; a second call does nothing.
    /// </summary>
    public void Start()
    {
        if (_listener is not null) return;
        Grants.Ended += OnGrantEnded;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listener = listener;
        Url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}{Route}");
        _loop = Task.Run(() => AcceptLoop(_stop.Token));
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (ct.IsCancellationRequested) return;
                continue;
            }
            _ = Task.Run(() => Serve(client, ct), CancellationToken.None);
        }
    }

    async Task Serve(TcpClient client, CancellationToken ct)
    {
        var connection = new Connection(client);
        _live[connection] = 0;
        try
        {
            // WHO IS ON THE OTHER END, ASKED ONCE, AT THE ACCEPT: the process the kernel names for this connection, when it
            // started and what program it runs. Nothing here is a verdict yet — the grant it presents names the container it
            // is checked against — and nothing that fails here is a pass: a null is refused when a grant is presented.
            connection.Accepted = Core.ProcessClock.Now();
            try
            {
                connection.Peer = PeerOf((IPEndPoint)client.Client.LocalEndPoint!, (IPEndPoint)client.Client.RemoteEndPoint!);
                connection.PeerProgram = connection.Peer is { } named ? ImageOf(named.Pid) : null;
            }
            catch (Exception) { connection.Peer = null; }
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new HttpReader(stream);
            while (!ct.IsCancellationRequested)
            {
                var request = await reader.ReadAsync(ct);
                if (request is null) return;
                // INSIDE THE CALL from here to the reply: an ending that arrives now closes the connection after it, rather
                // than cutting a tool call in half. An ending that arrived before refuses the request at the token check.
                if (!connection.EnterCall()) return;
                HttpAnswer response;
                bool endedMidCall;
                try { response = await Answer(connection, request, ct); }
                finally { endedMidCall = connection.LeaveCall(); }
                var close = response.Close || request.WantsClose || endedMidCall;
                await response.WriteAsync(stream, close, ct);
                if (close) return;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away, or the door is closing. Normal.
        }
        finally
        {
            _live.TryRemove(connection, out _);
            connection.Close();
        }
    }

    /// <summary>
    /// A LAUNCH GRANT HAS ENDED, SO THE DOOR CONNECTIONS IT AUTHENTICATED CLOSE — as the pipe's do. A connection inside a call
    /// answers that call and closes on the way out; every other closes now. Idempotent: an ending may be raised twice.
    /// </summary>
    void OnGrantEnded(string token)
    {
        foreach (var connection in _live.Keys)
        {
            if (!AgentGrants.SameToken(connection.Token, token)) continue;
            if (connection.End()) connection.Close();
        }
    }

    async Task<HttpAnswer> Answer(Connection connection, HttpRequest request, CancellationToken ct)
    {
        if (request.Malformed is { } why) return HttpAnswer.Text(400, why, close: true);

        // A BROWSER PAGE IS NEVER A CALLER. Loopback is reachable from any page the owner opens, and a page's request carries an
        // Origin; the only legitimate client — the seat's own runtime — sends none (MCP, streamable HTTP: validate Origin).
        if (request.Header("Origin") is not null)
        {
            Log?.Invoke("door: refused a request that carried an Origin");
            return HttpAnswer.Text(403, "the door does not answer a request sent by a web page", close: true);
        }

        if (Authenticate(connection, request) is { } refused) return refused;

        if (request.Target != Route) return HttpAnswer.Text(404, $"the door answers on {Route} only");
        if (request.Method != "POST")
            return HttpAnswer.Text(405, "the door answers a POST of one JSON-RPC message and opens no stream") with
            {
                Headers = [("Allow", "POST")]
            };

        return await Rpc(connection, request.Body, ct);
    }

    async Task<HttpAnswer> Rpc(Connection connection, byte[] body, CancellationToken ct)
    {
        JsonElement message;
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return HttpAnswer.Json(200, RpcError(null, -32700, $"the body is not JSON this door can read ({ex.Message})"));
        }

        if (message.ValueKind != JsonValueKind.Object)
            return HttpAnswer.Json(200, RpcError(null, -32600, "the door takes one JSON-RPC message per request, never a batch"));

        var id = message.TryGetProperty("id", out var i) && i.ValueKind is JsonValueKind.String or JsonValueKind.Number ? i.Clone() : (JsonElement?)null;
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (method is null) return HttpAnswer.Json(200, RpcError(id, -32600, "a JSON-RPC request names its method"));

        // A NOTIFICATION IS ACCEPTED AND ANSWERED WITH NOTHING (MCP, streamable HTTP: 202, no body).
        if (id is null) return HttpAnswer.Empty(202);

        var parameters = message.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        object result;
        switch (method)
        {
            case "initialize":
                result = Initialize(parameters);
                break;
            case "ping":
                result = new Dictionary<string, object>();
                break;
            case "tools/list":
                result = new Dictionary<string, object>
                {
                    ["tools"] = (Tools(connection.Grant!)?.Offered ?? []).Select(t => new Dictionary<string, object>
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Parameters
                    }).ToArray()
                };
                break;
            case "tools/call":
                result = await Call(connection.Grant!, id.Value, parameters, ct);
                break;
            default:
                return HttpAnswer.Json(200, RpcError(id, -32601, $"'{method}' is not a method this door answers"));
        }
        Log?.Invoke($"door: {method} accepted for the {CouncilRoles.Title(connection.Grant!.Role)}'s attempt {connection.Grant.AttemptId}");
        return HttpAnswer.Json(200, RpcResult(id.Value, result));
    }

    /// <summary>
    /// WHO IS CALLING, ON EVERY REQUEST: the bearer must be a launch grant the register calls live NOW — not absent, unknown,
    /// expired or pending, and not one whose turn is over (the pipe's grace is for a <c>trade</c> started late; the door's only
    /// client is the launch's own process, which has no call left to finish) — and a connection serves the one grant it first
    /// proved, never a token swapped in later. Null means carry on; a refusal is a 401 in words, and the connection closes:
    /// one chance per connection, as on the pipe.
    /// </summary>
    HttpAnswer? Authenticate(Connection connection, HttpRequest request)
    {
        var bearer = Bearer(request.Header("Authorization"));
        var verdict = Grants.Verify(bearer);
        string? why = verdict.State switch
        {
            GrantState.Valid when verdict.Grant!.TurnOver =>
                $"the turn the launch grant presented was issued for (attempt {verdict.Grant.AttemptId}) is over",
            GrantState.Valid when connection.Token is { } proved && !AgentGrants.SameToken(proved, bearer) =>
                "this connection proved a different launch grant, and a connection serves the one it proved",
            GrantState.Valid => null,
            _ => verdict.Reason
        };

        if (why is not null)
        {
            Log?.Invoke($"door: refused a request: {why}");
            return HttpAnswer.Text(401, why, close: true) with { Headers = [("WWW-Authenticate", "Bearer")] };
        }

        var grant = verdict.Grant!;
        if (connection.Token is null)
        {
            // THE FIRST GRANT THIS CONNECTION PRESENTS IS BOUND TO IT HERE, OR THE CONNECTION IS REFUSED. Once: the process on
            // the other end of an accepted connection cannot change, so the answer cannot either.
            if (Binding(connection, grant) is { } outside)
            {
                var refusal = $"this connection presented the launch grant of the {CouncilRoles.Title(grant.Role)}'s attempt " +
                              $"{grant.AttemptId}, and {outside} — it is served only to the {RuntimeWord} inside " +
                              $"{grant.Container?.Name ?? "the container TradeAgent made for that attempt"}";
                Log?.Invoke($"door: refused a connection: {refusal}");
                return HttpAnswer.Text(403, refusal, close: true);
            }
            connection.Token = grant.Token;
            Log?.Invoke($"door: bound a connection from process {connection.Peer!.Pid} to the {CouncilRoles.Title(grant.Role)}'s " +
                        $"attempt {grant.AttemptId}");
        }
        connection.Grant = grant;
        return null;
    }

    /// <summary>
    /// NULL WHEN THE PEER IS THE PINNED RUNTIME INSIDE THE GRANT'S CONTAINER, and the reason otherwise — the pipe's three
    /// conjuncts over a TCP peer (<c>docs/ORCHESTRATION.md</c> § 2.3): the kernel named the process and it started no later than
    /// the accept (a pid handed on since is not the process that connected); the program it runs is the pin (<see cref="Image"/>);
    /// and <see cref="AttemptContainer.Contains"/> answers Member — Outside and Unknown are both refusals. The membership is the
    /// container's own answer, as landed; the door adds nothing beside the image rule.
    /// </summary>
    string? Binding(Connection connection, AgentGrant grant)
    {
        if (grant.Container is not { } container) return "it is not yet bound to a container";
        if (connection.Peer is not { } peer) return "the operating system would not say which process holds this connection";
        if (connection.Accepted == 0 || peer.Start > connection.Accepted)
            return $"process {peer.Pid}, which the kernel names as the peer, started after the connection was accepted, " +
                   "so it is not the process that connected";
        if (Image() is not { } rule)
            return $"TradeAgent has no {RuntimeWord} recorded to check process {peer.Pid} against";
        if (PeerImage.Verdict(connection.PeerProgram, PeerImage.HashOf(connection.PeerProgram), rule) is { } notThePin)
            return notThePin;
        var verdict = container.Contains(peer);
        return verdict.Is == Membership.Member ? null : verdict.Why;
    }

    /// <summary>The token of an <c>Authorization: Bearer</c> header, or null for any other header or none.</summary>
    static string? Bearer(string? header) =>
        header is { Length: > 7 } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;

    /// <summary>
    /// ONE TOOL CALL, THROUGH THE HARNESS'S OWN SURFACE under the grant: any name — the surface's default deny answers a name it
    /// does not grant, and records it — with the model's arguments as the harness receives them, the answer cut to
    /// <see cref="MaxResultBytes"/>. A refusal is a result with <c>isError</c>, in words, never a transport error.
    /// </summary>
    async Task<Dictionary<string, object>> Call(AgentGrant grant, JsonElement id, JsonElement parameters, CancellationToken ct)
    {
        var name = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var n)
                   && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (name is null) return ToolResult("a tools/call names its tool", served: false);
        var arguments = parameters.TryGetProperty("arguments", out var a) && a.ValueKind != JsonValueKind.Null ? a.GetRawText() : "";

        if (Tools(grant) is not { } tools) return ToolResult("the door serves no tool to this launch", served: false);
        var answer = await tools.InvokeAsync(new ToolRequest(id.ToString(), name, arguments), ct);
        return ToolResult(Bounded(answer.Content), answer.Served);
    }

    /// <summary>The text cut to <see cref="MaxResultBytes"/> at a character boundary, saying so, or the text whole.</summary>
    static string Bounded(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= MaxResultBytes) return text;
        var cut = MaxResultBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
        return Encoding.UTF8.GetString(bytes, 0, cut) +
               $"\n[truncated — fetch by id: the answer was {bytes.Length:N0} bytes and one door result carries at most {MaxResultBytes:N0}]";
    }

    static Dictionary<string, object> Initialize(JsonElement parameters)
    {
        var asked = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var v)
                    && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Dictionary<string, object>
        {
            ["protocolVersion"] = asked is not null && KnownProtocols.Contains(asked) ? asked : ProtocolVersion,
            ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object> { ["listChanged"] = false } },
            ["serverInfo"] = new Dictionary<string, object> { ["name"] = "tradeagent", ["version"] = Versions.App }
        };
    }

    static Dictionary<string, object> ToolResult(string text, bool served) => new()
    {
        ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } },
        ["isError"] = !served
    };

    static string RpcResult(JsonElement id, object result) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    static string RpcError(JsonElement? id, int code, string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
        });

    /// <summary>Closes the door: the listener first, then every connection it holds.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        Grants.Ended -= OnGrantEnded;
        await _stop.CancelAsync();
        try { _listener?.Stop(); } catch (Exception) { /* already closed */ }
        foreach (var connection in _live.Keys) connection.Close();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* the loop ends on the cancel; a stop is not held up by it */ }
        }
        Url = null;
        _stop.Dispose();
    }

    // ---- one connection -------------------------------------------------------------------------------------------------

    sealed class Connection(TcpClient client)
    {
        readonly Lock _gate = new();
        bool _inCall, _ended;

        public TcpClient Client { get; } = client;

        /// <summary>When the door accepted the connection, in the kernel's own unit (<see cref="Core.ProcessClock"/>).</summary>
        public ulong Accepted { get; set; }

        /// <summary>The process the kernel named as the other end at the accept, or null when it would not say.</summary>
        public PeerFacts? Peer { get; set; }

        /// <summary>The program that process runs, as the kernel names it, or null.</summary>
        public string? PeerProgram { get; set; }

        /// <summary>The token this connection proved on its first served request; never replaced.</summary>
        public string? Token { get; set; }

        /// <summary>The grant the current request was verified under.</summary>
        public AgentGrant? Grant { get; set; }

        /// <summary>Marks the connection inside a call: an ending that arrives now waits for the reply.</summary>
        public bool EnterCall()
        {
            lock (_gate)
            {
                if (_ended) return false;
                _inCall = true;
                return true;
            }
        }

        /// <summary>Leaves the call; true when the grant ended while it ran, so the connection closes after this reply.</summary>
        public bool LeaveCall()
        {
            lock (_gate)
            {
                _inCall = false;
                return _ended;
            }
        }

        /// <summary>The grant ended: true when the connection may close NOW, false when a call is in flight.</summary>
        public bool End()
        {
            lock (_gate)
            {
                _ended = true;
                return !_inCall;
            }
        }

        public void Close()
        {
            try { Client.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { /* already gone */ }
            try { Client.Dispose(); } catch (Exception) { /* already gone */ }
        }
    }

    // ---- HTTP/1.1, the part of it the door needs ------------------------------------------------------------------------

    sealed record HttpRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body, bool WantsClose,
        string? Malformed = null)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    sealed record HttpAnswer(int Status, string? ContentType, byte[] Body, bool Close = false)
    {
        public IReadOnlyList<(string Name, string Value)> Headers { get; init; } = [];

        public static HttpAnswer Json(int status, string json, bool close = false) =>
            new(status, "application/json", Encoding.UTF8.GetBytes(json), close);

        public static HttpAnswer Text(int status, string text, bool close = false) =>
            new(status, "application/json",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = text })), close);

        public static HttpAnswer Empty(int status) => new(status, null, []);

        public async Task WriteAsync(Stream stream, bool close, CancellationToken ct)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(Status)).Append("\r\n");
            if (ContentType is not null) head.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            foreach (var (name, value) in Headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
            head.Append(close ? "Connection: close\r\n" : "Connection: keep-alive\r\n");
            head.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
            if (Body.Length > 0) await stream.WriteAsync(Body, ct);
            await stream.FlushAsync(ct);
        }

        static string Reason(int status) => status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            _ => "Status"
        };
    }

    /// <summary>
    /// Reads one request at a time off a connection: a request line, headers to the blank line, and a body of exactly its
    /// <c>Content-Length</c>. No chunked request bodies — codex sends a length — and every bound is a refusal that closes.
    /// </summary>
    sealed class HttpReader(Stream stream)
    {
        readonly byte[] _buffer = new byte[MaxHeaderBytes + 4096];
        int _start, _end;

        public async Task<HttpRequest?> ReadAsync(CancellationToken ct)
        {
            int headerEnd;
            while ((headerEnd = HeaderEnd()) < 0)
            {
                if (_end - _start >= MaxHeaderBytes) return Bad("the request's headers are longer than the door reads");
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
                if (read == 0) return null;
                _end += read;
            }

            var head = Encoding.ASCII.GetString(_buffer, _start, headerEnd - _start);
            _start = headerEnd + 4;
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                return Bad("the request line is not HTTP/1.x");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) return Bad("a header line has no name");
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                // A HEADER SAID TWICE IS REFUSED, not merged: two lengths or two credentials on one request have no honest reading.
                if (!headers.TryAdd(name, value)) return Bad($"the header '{name}' is sent more than once");
            }

            if (headers.ContainsKey("Transfer-Encoding")) return Bad("the door reads a body by its Content-Length only");
            var length = 0;
            if (headers.TryGetValue("Content-Length", out var said)
                && (!int.TryParse(said, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaxRequestBytes))
                return Bad($"the body's length is not one the door reads (at most {MaxRequestBytes} bytes)");

            var body = new byte[length];
            var have = Math.Min(length, _end - _start);
            Buffer.BlockCopy(_buffer, _start, body, 0, have);
            _start += have;
            while (have < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(have), ct);
                if (read == 0) return null;
                have += read;
            }

            var target = first[1];
            var query = target.IndexOf('?');
            if (query >= 0) target = target[..query];
            var close = first[2] == "HTTP/1.0"
                ? !string.Equals(headers.GetValueOrDefault("Connection"), "keep-alive", StringComparison.OrdinalIgnoreCase)
                : string.Equals(headers.GetValueOrDefault("Connection"), "close", StringComparison.OrdinalIgnoreCase);
            return new HttpRequest(first[0], target, headers, body, close);
        }

        int HeaderEnd()
        {
            for (var i = _start; i + 3 < _end; i++)
                if (_buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
                    return i;
            return -1;
        }

        static HttpRequest Bad(string why) => new("", "", new Dictionary<string, string>(), [], true, why);
    }
}
