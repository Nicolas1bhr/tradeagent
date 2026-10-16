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

    /// <summary>
    /// The door's own record: a line per decision, never a token. The app writes it to its engineering log; a test reads it.
    /// </summary>
    public Action<string>? Log { get; init; }

    /// <summary>Opens the door on an ephemeral loopback port. Once; a second call does nothing.</summary>
    public void Start()
    {
        if (_listener is not null) return;
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
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new HttpReader(stream);
            while (!ct.IsCancellationRequested)
            {
                var request = await reader.ReadAsync(ct);
                if (request is null) return;
                var response = await Answer(connection, request, ct);
                var close = response.Close || request.WantsClose;
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

    async Task<HttpAnswer> Answer(Connection connection, HttpRequest request, CancellationToken ct)
    {
        if (request.Malformed is { } why) return HttpAnswer.Text(400, why, close: true);
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
                result = new Dictionary<string, object> { ["tools"] = Array.Empty<object>() };
                break;
            case "tools/call":
                result = ToolResult("this door serves no tool yet", served: false);
                break;
            default:
                return HttpAnswer.Json(200, RpcError(id, -32601, $"'{method}' is not a method this door answers"));
        }
        await Task.CompletedTask;
        Log?.Invoke($"door: {method} answered");
        return HttpAnswer.Json(200, RpcResult(id.Value, result));
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
        public TcpClient Client { get; } = client;

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
