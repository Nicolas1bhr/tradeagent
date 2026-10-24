using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE REAL SURFACE PROBE (<c>U-thinking-seats-c</c>; the thinking-seats survey § 3, option (b)): a DRY <c>codex exec</c> of the seat
/// class's own argument template and pinned model against a Responses endpoint the app hosts on loopback, a stub of the app's door in
/// the door's slot, and a throwaway home — never the seat home, which holds the sign-in, and never the owner's own. Its first model
/// request carries the tools that binary, template and model offer; they are returned as captured and the codex is ended. No real model
/// is asked anything, nothing is spent, and the throwaway home is deleted.
///
/// <para>MEASURED 2026-10-17 on the dev Mac (codex-cli 0.160.1): ~1 s a run. THE MODEL'S METADATA IS PINNED (<c>U-cell-model-catalog</c>):
/// the dry run and a launched seat both name the app's own catalog (<c>model_catalog_json</c>, <see cref="SeatPlan.ModelCatalog"/>), so
/// neither reads the binary's bundled entry, the account's model list or the fallback — the dry run and a live turn agree on the tools.
/// What it still cannot see: whether the owner's plan serves gpt-6-luna on the app's edited metadata (classic tools, no code mode) —
/// no 4xx, the door called as a function and served, the offered set the expected one — UNKNOWN until one real turn, on his word.</para>
/// </summary>
public sealed class CodexSurfaceProbe : ISurfaceProbe
{
    public static readonly CodexSurfaceProbe Instance = new();

    /// <summary>The provider id the dry run names on its command line; the mock answers it and nothing else is configured.</summary>
    public const string ProviderId = "tradeagent_probe";

    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    public Task<IReadOnlyList<string>> OfferedAsync(SurfaceAsk ask, CancellationToken ct)
    {
        var seat = ask.Manifest.Seat ?? throw new InvalidOperationException($"{ask.Manifest.DisplayName} has no seat profile");
        // THE SHIPPED CATALOG, HELD TO ITS PIN (U-cell-model-catalog): the bytes a launched seat's home holds, or no dry run at all.
        var (catalog, refusal) = SeatProfile.ShippedCatalog(seat);
        if (refusal is not null) throw new InvalidOperationException(refusal);
        return OfferedAsync(ask, catalog, ct);
    }

    /// <summary>
    /// THE DRY RUN ON <paramref name="catalog"/>'s METADATA: the bytes are written into the throwaway codex home and the class's argv names
    /// that copy in <see cref="SeatPlan.CatalogSlot"/>, as a launch names the seat home's. Internal for the row that proves codex reads
    /// the catalog and not its bundled or fallback metadata (an altered entry changes what is offered).
    /// </summary>
    internal async Task<IReadOnlyList<string>> OfferedAsync(SurfaceAsk ask, byte[]? catalog, CancellationToken ct)
    {
        var manifest = ask.Manifest;
        var seat = manifest.Seat ?? throw new InvalidOperationException($"{manifest.DisplayName} has no seat profile");
        var token = "probe-" + Guid.NewGuid().ToString("n");
        using var mock = new LoopbackHttp(MockResponses);
        using var door = new LoopbackHttp((req, _) => StubDoor(req, token));
        mock.Start();
        door.Start();

        var root = Path.Combine(Path.GetTempPath(), "tradeagent-surface-" + Guid.NewGuid().ToString("n")[..12]);
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(Path.Combine(root, "codex"));
        Directory.CreateDirectory(Path.Combine(root, "home"));
        Directory.CreateDirectory(work);
        try
        {
            // THE CATALOG IN THE THROWAWAY HOME, where the seat home keeps its own (U-cell-model-catalog).
            string? catalogPath = null;
            if (catalog is not null)
            {
                catalogPath = Path.Combine(root, "codex", Path.GetFileName(seat.ModelCatalog is { Length: > 0 } c ? c : "model-catalog.json"));
                File.WriteAllBytes(catalogPath, catalog);
            }
            // THE CLASS'S OWN ARGV, as a turn builds it — the stub door in the door's slot — and the mock pinned as the provider.
            string[] provider =
            [
                "-c", $"model_provider=\"{ProviderId}\"",
                "-c", $"model_providers.{ProviderId}.name=\"TradeAgent surface probe\"",
                "-c", $"model_providers.{ProviderId}.base_url=\"{mock.Url}v1\"",
                "-c", $"model_providers.{ProviderId}.wire_api=\"responses\""
            ];
            var args = AgentArgs.Build(manifest.ExecArgs, "probe", manifest.JsonFlag, manifest.UnattendedArgs,
                [.. manifest.ModelArgs.Select(a => a.Replace("{model}", ask.Model, StringComparison.Ordinal))],
                [.. seat.ArgsFor(ask.SeatClass, new Uri(door.Url, BrokerDoor.Route), catalogPath), .. provider],
                manifest.PromptOnStdin, manifest.StdinMarker, out var promptInArgv);

            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            CliAgentRuntime.SetCommand(psi, ask.Exe, args);
            // THE WHITELIST AND THE GRANT'S VARIABLE, THEN A THROWAWAY HOME OVER EVERY NAME THE SEAT HOME SETS, THEN THE CLASS'S OWN.
            AgentEnvironment.Apply(psi, new Dictionary<string, string> { [Security.AgentGrants.Variable] = token }, manifest.KeepEnvironment);
            foreach (var name in seat.Environment.Keys) psi.Environment[name] = Path.Combine(root, "home");
            psi.Environment["CODEX_HOME"] = Path.Combine(root, "codex");
            psi.Environment["HOME"] = Path.Combine(root, "home");
            if (seat.ClassEnvironment.TryGetValue(ask.SeatClass, out var own))
                foreach (var (k, v) in own) psi.Environment[k] = v;

            using var held = ProcessContainment.Start(psi);
            var p = held.Process;
            try
            {
                if (!promptInArgv) await p.StandardInput.WriteAsync("probe");
                p.StandardInput.Close();
            }
            catch (Exception) { /* it may have exited already; what it sent, or did not, is the answer */ }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(Deadline);
            var stdout = p.StandardOutput.ReadToEndAsync(timer.Token);
            var stderr = p.StandardError.ReadToEndAsync(timer.Token);
            try { await Task.WhenAny(mock.FirstBody, p.WaitForExitAsync(timer.Token)).WaitAsync(timer.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                held.Kill();
                throw new InvalidOperationException($"{Path.GetFileName(ask.Exe)} made no model request within {Deadline.TotalSeconds:0} seconds");
            }

            if (!mock.FirstBody.IsCompleted)
            {
                held.Kill();
                var why = Ansi.Strip(await Quietly(stderr) + "\n" + await Quietly(stdout)).Split('\n')
                    .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no output";
                throw new InvalidOperationException(
                    $"{Path.GetFileName(ask.Exe)} exited {(p.HasExited ? p.ExitCode.ToString(CultureInfo.InvariantCulture) : "?")} " +
                    $"without asking the model: {why}");
            }

            var body = await mock.FirstBody;
            // CODEX'S OWN WORDS ABOUT THE RUN, READ BEFORE ITS TOOLS COUNT (U-cell-model-catalog, rider (b)): the mock answers the request
            // with a finished response, so codex ends its turn on its own; its warnings — fallback metadata above all — are in what it
            // printed. One the manifest names (ToolsUnavailablePattern) refuses the seat, quoted, whatever tools were offered.
            try { await p.WaitForExitAsync(timer.Token).WaitAsync(TimeSpan.FromSeconds(10), timer.Token); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !ct.IsCancellationRequested) { /* ended below */ }
            held.Kill();
            var said = Ansi.Strip(await Quietly(stdout) + "\n" + await Quietly(stderr));
            if (UnavailableLine(manifest, said) is { } warned)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(ask.Exe)} said \"{warned}\" in its dry run on {ask.Model}, so the tools it would offer are not the ones " +
                    "a check has seen");
            return Tools(body);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* the OS temp cleaner takes it */ }
        }
    }

    /// <summary>The first line of <paramref name="said"/> the manifest's <see cref="RuntimeManifest.ToolsUnavailablePattern"/> matches, its JSON message unwrapped; null for none.</summary>
    static string? UnavailableLine(RuntimeManifest manifest, string said)
    {
        if (manifest.ToolsUnavailablePattern is not { Length: > 0 } pattern) return null;
        foreach (var raw in said.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try { if (!System.Text.RegularExpressions.Regex.IsMatch(line, pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) continue; }
            catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException) { return null; }
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("item", out var item) && item.TryGetProperty("message", out var m) && m.GetString() is { } message)
                    return message.Trim();
            }
            catch (JsonException) { }
            return line.Length > 300 ? line[..300] + "…" : line;
        }
        return null;
    }

    static async Task<string> Quietly(Task<string> read)
    {
        try { return await read.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// THE TOOLS A RESPONSES REQUEST OFFERS: its <c>tools</c>, a namespace's own dotted under it, and every <c>additional_tools</c> input
    /// item's — the shape the survey's <c>tools.py</c> read in runs e1 and e7. A kind other than <c>function</c> is bracketed.
    /// </summary>
    public static IReadOnlyList<string> Tools(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var names = new List<string>();
        if (root.TryGetProperty("tools", out var tools)) Walk(tools, "", names);
        if (root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
            foreach (var item in input.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var t) && t.GetString() == "additional_tools"
                    && item.TryGetProperty("tools", out var more))
                {
                    var extra = new List<string>();
                    Walk(more, "", extra);
                    names.AddRange(extra.Select(n => "(additional_tools) " + n));
                }
        return names;
    }

    static void Walk(JsonElement tools, string prefix, List<string> into)
    {
        if (tools.ValueKind != JsonValueKind.Array) return;
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;
            var type = tool.TryGetProperty("type", out var t) ? t.GetString() : null;
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (type == "namespace")
            {
                if (tool.TryGetProperty("tools", out var inner)) Walk(inner, prefix + name + ".", into);
                continue;
            }
            into.Add(prefix + (name ?? type ?? "?") + (type is null or "function" ? "" : $"[{type}]"));
        }
    }

    /// <summary>THE MOCK RESPONSES ENDPOINT: every <c>/responses</c> request is answered with one short message; others 404.</summary>
    static (int Status, string Type, byte[] Body) MockResponses(LoopbackHttp.Request req, LoopbackHttp self)
    {
        if (req.Method != "POST" || !req.Path.EndsWith("/responses", StringComparison.Ordinal)) return (404, "text/plain", []);
        self.Capture(req.Body);
        var id = "resp_" + Guid.NewGuid().ToString("n")[..8];
        var events = new object[]
        {
            new { type = "response.created", response = new { id } },
            new { type = "response.output_item.done", item = new { type = "message", role = "assistant", id = "msg_" + id,
                content = new[] { new { type = "output_text", text = "surface probe" } } } },
            new { type = "response.completed", response = new { id, usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 } } }
        };
        var sse = new StringBuilder();
        foreach (var e in events)
        {
            var json = JsonSerializer.Serialize(e);
            using var d = JsonDocument.Parse(json);
            sse.Append("event: ").Append(d.RootElement.GetProperty("type").GetString()).Append('\n').Append("data: ").Append(json).Append("\n\n");
        }
        return (200, "text/event-stream", Encoding.UTF8.GetBytes(sse.ToString()));
    }

    /// <summary>
    /// A STUB OF THE APP'S DOOR, for the dry run only: the bearer is the probe's throwaway token, and it serves one tool that does
    /// nothing — the door's tools are deferred behind <c>tool_search</c>, so which ones it serves does not change the offered set.
    /// </summary>
    static (int Status, string Type, byte[] Body) StubDoor(LoopbackHttp.Request req, string token)
    {
        if (req.Method == "GET") return (405, "text/plain", []);
        if (req.Method == "DELETE") return (200, "text/plain", []);
        if (req.Header("authorization") != "Bearer " + token) return (401, "text/plain", []);
        using var doc = JsonDocument.Parse(req.Body.Length == 0 ? "{}" : req.Body);
        var msg = doc.RootElement;
        if (!msg.TryGetProperty("id", out var id)) return (202, "text/plain", []);
        var method = msg.TryGetProperty("method", out var m) ? m.GetString() : null;
        object result = method switch
        {
            "initialize" => new
            {
                protocolVersion = msg.TryGetProperty("params", out var p) && p.TryGetProperty("protocolVersion", out var v)
                    ? v.GetString() : BrokerDoor.ProtocolVersion,
                capabilities = new { tools = new { } },
                serverInfo = new { name = BrokerSeat.DoorServer, version = "probe" }
            },
            "tools/list" => new { tools = new[] { new { name = "probe", description = "The surface probe's stub.", inputSchema = new { type = "object", properties = new { } } } } },
            "tools/call" => new { content = new[] { new { type = "text", text = "probe" } }, isError = false },
            _ => new { }
        };
        var reply = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = JsonSerializer.Deserialize<object>(id.GetRawText()), result });
        return (200, "application/json", Encoding.UTF8.GetBytes(reply));
    }
}

/// <summary>
/// A LOOPBACK HTTP/1.1 SERVER FOR THE DRY RUN, one request per connection (<c>Connection: close</c>): 127.0.0.1 and an ephemeral port,
/// a bounded header and body, <c>Content-Length</c> or chunked bodies, gzip/deflate/brotli request bodies decoded.
/// </summary>
sealed class LoopbackHttp(Func<LoopbackHttp.Request, LoopbackHttp, (int Status, string Type, byte[] Body)> handle) : IDisposable
{
    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    const int MaxHeaderBytes = 64 * 1024, MaxBodyBytes = 16 << 20;

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly TaskCompletionSource<string> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>This server's base address, ending in a slash.</summary>
    public Uri Url { get; private set; } = new("http://127.0.0.1/");

    /// <summary>The first body a handler captured.</summary>
    public Task<string> FirstBody => _first.Task;

    public void Capture(string body) => _first.TrySetResult(body);

    public void Start()
    {
        _listener.Start();
        Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}/");
        _ = Task.Run(AcceptAll);
    }

    async Task AcceptAll()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var request = await Read(stream, timeout.Token);
            if (request is null) return;
            var (status, type, body) = handle(request, this);
            var head = $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} {(status == 200 ? "OK" : "Status")}\r\n" +
                       $"Content-Type: {type}\r\nContent-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
            await stream.FlushAsync(timeout.Token);
        }
        catch (Exception) { /* a connection that broke is a request that was not answered */ }
    }

    static async Task<Request?> Read(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[4096];
        int end;
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return null;
            buffer.Write(one, 0, n);
            end = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
            if (end >= 0) break;
            if (buffer.Length > MaxHeaderBytes) return null;
        }
        var all = buffer.ToArray();
        var lines = Encoding.ASCII.GetString(all, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
            if (line.IndexOf(':') is var colon and > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();

        var body = new MemoryStream();
        body.Write(all, end + 4, all.Length - end - 4);
        if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (!ChunkedDone(body.GetBuffer(), (int)body.Length))
            {
                var n = await stream.ReadAsync(one, ct);
                if (n == 0 || body.Length > MaxBodyBytes) return null;
                body.Write(one, 0, n);
            }
            body = Dechunk(body.ToArray());
        }
        else if (headers.TryGetValue("content-length", out var cl) && long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            if (length > MaxBodyBytes) return null;
            while (body.Length < length)
            {
                var n = await stream.ReadAsync(one, ct);
                if (n == 0) return null;
                body.Write(one, 0, n);
            }
        }

        var bytes = body.ToArray();
        if (headers.TryGetValue("content-encoding", out var encoding)) bytes = Decode(bytes, encoding);
        return new Request(first[0], first[1], headers, Encoding.UTF8.GetString(bytes));
    }

    static byte[] Decode(byte[] bytes, string encoding)
    {
        Stream? inflate = encoding.Trim().ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress),
            "deflate" => new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress),
            "identity" or "" => null,
            _ => throw new InvalidOperationException($"a request body encoded as '{encoding}', which the surface probe cannot read")
        };
        if (inflate is null) return bytes;
        using (inflate)
        {
            using var plain = new MemoryStream();
            inflate.CopyTo(plain);
            return plain.ToArray();
        }
    }

    static bool ChunkedDone(byte[] data, int length) => IndexOf(data, length, "\r\n0\r\n\r\n"u8) >= 0
        || length >= 5 && IndexOf(data, Math.Min(length, 5), "0\r\n\r\n"u8) == 0;

    static MemoryStream Dechunk(byte[] data)
    {
        var plain = new MemoryStream();
        var at = 0;
        while (at < data.Length)
        {
            var crlf = IndexOf(data[at..], data.Length - at, "\r\n"u8);
            if (crlf < 0) break;
            var size = int.Parse(Encoding.ASCII.GetString(data, at, crlf).Split(';')[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            at += crlf + 2;
            if (size == 0) break;
            plain.Write(data, at, size);
            at += size + 2;
        }
        return plain;
    }

    static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> what) => data.AsSpan(0, length).IndexOf(what);

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
        _stop.Dispose();
    }
}
