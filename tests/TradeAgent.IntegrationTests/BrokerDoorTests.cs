using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE BROKER'S SHELL-FREE DOOR (<c>U-thinking-seats-b</c>, <c>docs/ORCHESTRATION.md</c> § 2.7): the app's own MCP server on
/// loopback, spoken to here by a real HTTP client on every runner.
/// </summary>
[Collection("containment")]
public class BrokerDoorTests
{
    /// <summary>
    /// ITEM 1, GUARD: THE DOOR IS ON LOOPBACK, ON A PORT CHOSEN AT START, ANSWERS MCP's HANDSHAKE AS JSON AND OPENS NO STREAM.
    /// </summary>
    [Fact]
    public async Task The_door_listens_on_loopback_answers_the_handshake_and_opens_no_stream()
    {
        var grants = new AgentGrants();
        await using var door = Door(grants);
        door.Start();
        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-handshake");

        Assert.NotNull(door.Url);
        Assert.Equal("127.0.0.1", door.Url!.Host);
        Assert.NotEqual(0, door.Url.Port);
        Assert.Equal(BrokerDoor.Route, door.Url.AbsolutePath);

        using var http = Client(research.Token);
        var init = await Post(http, door.Url, Rpc(1, "initialize", new { protocolVersion = "2025-06-18" }));
        Assert.Equal(HttpStatusCode.OK, init.Status);
        Assert.Equal("2025-06-18", init.Json.GetProperty("result").GetProperty("protocolVersion").GetString());

        var note = await Post(http, door.Url, JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
        Assert.Equal(HttpStatusCode.Accepted, note.Status);

        var stream = await http.GetAsync(door.Url);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, stream.StatusCode);
    }

    /// <summary>
    /// (a) THE BEARER IS A LIVE LAUNCH GRANT OR NOTHING IS SERVED. No bearer, a token this app never issued, a grant whose life
    /// is over, a grant still PENDING (minted, not yet bound to its attempt's container), and a grant whose turn has ended inside
    /// the grace: each answered 401 in words, before any method runs.
    /// </summary>
    [Fact]
    public async Task A_door_request_without_a_live_launch_grant_is_refused()
    {
        var clock = DateTimeOffset.UtcNow;
        var grants = new AgentGrants(() => clock);
        await using var door = Door(grants);
        door.Start();

        var expired = grants.IssueHere(CouncilRoles.Research, "attempt-expired", TimeSpan.FromMinutes(1));
        clock += TimeSpan.FromMinutes(2);   // past the expired grant's life
        var pending = grants.Issue(CouncilRoles.Research, "attempt-pending");
        var ended = grants.IssueHere(CouncilRoles.Research, "attempt-ended");
        ended.Dispose();                    // its turn is over; the register keeps it for the grace

        foreach (var (what, bearer, words) in new (string, string?, string)[]
                 {
                     ("no bearer", null, "no launch grant was presented"),
                     ("a token this app never issued", "0123456789abcdef", "not issued by this TradeAgent"),
                     ("an expired grant", expired.Token, "expired"),
                     ("a pending grant", pending.Token, "not yet bound"),
                     ("a grant whose turn has ended", ended.Token, "turn")
                 })
        {
            using var http = Client(bearer);
            var reply = await Post(http, door.Url!, Rpc(1, "initialize"));
            Assert.True(reply.Status == HttpStatusCode.Unauthorized, $"a door request with {what} was served: {(int)reply.Status} {reply.Body}");
            Assert.Contains(words, reply.Body);
        }
    }

    /// <summary>
    /// (f) AN ENDED GRANT CLOSES ITS DOOR CONNECTION. A keep-alive connection the grant authenticated is closed by the door when
    /// the turn ends — not left for the next request to find — and a fresh request with the same token is refused.
    /// </summary>
    [Fact]
    public async Task An_ended_grant_closes_its_door_connection()
    {
        var grants = new AgentGrants();
        await using var door = Door(grants);
        door.Start();
        var research = grants.IssueHere(CouncilRoles.Research, "attempt-closing");

        await using var raw = await RawHttp.ConnectAsync(door.Url!);
        var first = await raw.SendAsync(Rpc(1, "initialize"), research.Token);
        Assert.True(first is { Status: 200 }, $"the live grant's first request was not served: {first}");

        research.Dispose();   // the turn ends

        Assert.True(await raw.ClosedWithinAsync(TimeSpan.FromSeconds(10)), "the door connection of an ended grant stayed open");
        using var http = Client(research.Token);
        var again = await Post(http, door.Url!, Rpc(2, "tools/list"));
        Assert.True(again.Status == HttpStatusCode.Unauthorized, $"a fresh request with an ended grant was served: {(int)again.Status} {again.Body}");
    }

    /// <summary>
    /// (b) A TOKEN REPLAYED FROM OUTSIDE ITS ATTEMPT'S CONTAINER IS REFUSED. A real launch, held as the product holds one — a
    /// Windows job, or the session the launcher made — with its grant bound to that container; then THIS process, which is not in
    /// it, presents the live token. The image rule is pointed at this process's own program, so only membership can refuse.
    /// </summary>
    [Fact]
    public async Task A_grant_replayed_to_the_door_from_outside_its_attempt_container_is_refused()
    {
        var grants = new AgentGrants();
        ContainedProcess? contained = null;
        try
        {
            contained = ProcessContainment.Start(Sleeper());
            Assert.NotNull(contained.Container);
            await HeldWithin(contained, TimeSpan.FromSeconds(30));
            var research = grants.Issue(CouncilRoles.Research, "attempt-replayed-door");
            Assert.True(grants.Bind(research.Token, contained.Container!));

            await using var door = Door(grants);
            door.Start();
            using var http = Client(research.Token);
            var reply = await Post(http, door.Url!, Rpc(1, "initialize"));

            Assert.True(reply.Status == HttpStatusCode.Forbidden,
                $"a process outside the attempt's container presented its live grant at the door and was served: {(int)reply.Status} {reply.Body}");
            Assert.Contains("attempt-replayed-door", reply.Body);
            // THE KERNEL NAMED THIS PROCESS, and the container said it is outside: the refusal is about membership, not a lookup.
            Assert.Contains($"process {Environment.ProcessId}", reply.Body);
        }
        finally { contained?.Dispose(); }
    }

    /// <summary>
    /// (c) THE PEER MUST BE THE PINNED RUNTIME. Inside the container, presenting a live grant — and the program the kernel names
    /// is not the one the rule pins: another program's path, the right path with another digest, or no pin recorded at all.
    /// </summary>
    [Fact]
    public async Task A_peer_whose_image_is_not_the_pinned_codex_is_refused()
    {
        var self = PeerImage.PathOf(Environment.ProcessId)!;
        var other = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        foreach (var (what, rule) in new (string, PeerRule?)[]
                 {
                     ("another program", new PeerRule(other, PeerImage.HashOf(other), null, null, BrokerDoor.RuntimeWord)),
                     ("another digest", new PeerRule(self, new string('0', 64), null, null, BrokerDoor.RuntimeWord)),
                     ("no pin at all", null)
                 })
        {
            var grants = new AgentGrants();
            var research = grants.IssueHere(CouncilRoles.Research, "attempt-image");
            await using var door = new BrokerDoor { Grants = grants, Image = () => rule };
            door.Start();
            using var http = Client(research.Token);
            var reply = await Post(http, door.Url!, Rpc(1, "initialize"));
            Assert.True(reply.Status == HttpStatusCode.Forbidden,
                $"a peer that is not the pinned runtime ({what}) was served: {(int)reply.Status} {reply.Body}");
            Assert.Contains(BrokerDoor.RuntimeWord, reply.Body);
        }
    }

    /// <summary>
    /// (d) A PEER LOOKUP THAT FAILS REFUSES. The kernel would not name the process holding the connection; separately, the
    /// container could not say whether the named process is inside; separately, the named process started after the accept.
    /// </summary>
    [Fact]
    public async Task A_peer_lookup_that_fails_refuses()
    {
        var cases = new (string What, Func<AgentGrants, AgentGrant> Grant, BrokerDoor Door, string Words)[]
        {
            ("an unnamed peer", g => g.IssueHere(CouncilRoles.Research, "attempt-unnamed"),
                new BrokerDoor { Image = SelfImage, PeerOf = (_, _) => null }, "would not say which process"),
            ("a container that could not say", g => Bound(g, new Answers(MembershipVerdict.Unknown("the job could not be asked"))),
                new BrokerDoor { Image = SelfImage }, "the job could not be asked"),
            ("a peer started after the accept", g => g.IssueHere(CouncilRoles.Research, "attempt-late"),
                new BrokerDoor
                {
                    Image = SelfImage,
                    PeerOf = (l, r) => TcpPeer.OwnerOf(l, r) is { } real ? real with { Start = ProcessClock.Now() + 1_000_000_000UL } : null
                }, "started after")
        };
        foreach (var (what, issue, template, words) in cases)
        {
            var grants = new AgentGrants();
            var grant = issue(grants);
            await using var door = new BrokerDoor { Grants = grants, Image = template.Image, PeerOf = template.PeerOf };
            door.Start();
            using var http = Client(grant.Token);
            var reply = await Post(http, door.Url!, Rpc(1, "initialize"));
            Assert.True(reply.Status == HttpStatusCode.Forbidden, $"{what} was served: {(int)reply.Status} {reply.Body}");
            Assert.Contains(words, reply.Body);
        }
    }

    /// <summary>GUARD: the kernel names THIS process as the peer of a loopback connection it made, on every runner.</summary>
    [Fact]
    public async Task The_kernel_names_the_process_on_the_other_end_of_a_door_connection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var peer = TcpPeer.OwnerOf((IPEndPoint)accepted.Client.LocalEndPoint!, (IPEndPoint)accepted.Client.RemoteEndPoint!);
        Assert.True(peer is not null, "the kernel would not name the process on the other end of a loopback connection");
        Assert.Equal(Environment.ProcessId, peer!.Pid);
        Assert.True(peer.Start > 0 && peer.Start <= ProcessClock.Now(), $"the peer's start {peer.Start} is not a start before now");
    }

    /// <summary>
    /// GUARD: THE PRODUCT'S DOOR PINS THE BUILT-IN CODEX — its digest from code, the workspace forbidden, the managed-runtime
    /// folder (where the pin is installed) not — and a door with no pin refuses (see (c)).
    /// </summary>
    [Fact]
    public void The_product_door_pins_the_built_in_codex()
    {
        var pin = RuntimeCatalog.BuiltIn().Single(m => m.Id == BrokerDoor.CodexId).Install.ExecutableSha256;
        var rule = BrokerDoor.PinnedCodex();
        if (pin is not { Length: > 0 })
        {
            Assert.Null(rule);
            return;
        }
        Assert.NotNull(rule);
        Assert.Equal(pin, rule!.ExpectedHash);
        Assert.Equal(Paths.Workspace, rule.WorkspaceDir);
        Assert.Null(rule.ToolsDir);
        Assert.Equal(BrokerDoor.RuntimeWord, rule.Program);
    }

    static BrokerDoor Door(AgentGrants grants) => new() { Grants = grants, Image = SelfImage };

    /// <summary>THIS test process's own program as the pinned runtime, so a test's own connection passes the image rule.</summary>
    static PeerRule SelfImage()
    {
        var self = PeerImage.PathOf(Environment.ProcessId);
        return new PeerRule(self, PeerImage.HashOf(self), null, null, BrokerDoor.RuntimeWord);
    }

    static AgentGrant Bound(AgentGrants grants, AttemptContainer container)
    {
        var grant = grants.Issue(CouncilRoles.Research, "attempt-unsure");
        Assert.True(grants.Bind(grant.Token, container));
        return grant;
    }

    /// <summary>A container that gives one answer.</summary>
    sealed class Answers(MembershipVerdict verdict) : AttemptContainer
    {
        public override string Name => "a container that answers one way";
        public override MembershipVerdict Contains(PeerFacts peer) => verdict;
    }

    /// <summary>A process that waits a minute and does nothing else: the attempt whose container (b) replays from outside.</summary>
    static ProcessStartInfo Sleeper()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "ping -n 60 127.0.0.1 > nul" } }
            : new ProcessStartInfo("/bin/sleep") { ArgumentList = { "60" } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            psi.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return psi;
    }

    /// <summary>On macOS and Linux the session is the launcher's to make; waits until it is proved (Windows: held at start).</summary>
    static async Task HeldWithin(ContainedProcess contained, TimeSpan bound)
    {
        var until = DateTime.UtcNow + bound;
        while (!contained.Held && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.True(contained.Held, $"the launch was not held within {bound.TotalSeconds:0} s: {contained.Mechanism}");
    }

    // ---- the client --------------------------------------------------------------------------------------------------------

    internal static HttpClient Client(string? bearer = null)
    {
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (bearer is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    internal static string Rpc(int id, string method, object? parameters = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { }
        });

    internal sealed record Reply(HttpStatusCode Status, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    /// <summary>
    /// A client that speaks HTTP/1.1 by hand on ONE connection, so a test can see the door close it. <see cref="HttpClient"/>
    /// reconnects behind the caller's back, which is right for the product and the wrong instrument here.
    /// </summary>
    internal sealed class RawHttp : IAsyncDisposable
    {
        readonly TcpClient _tcp = new();
        NetworkStream _stream = null!;
        Uri _url = null!;

        public static async Task<RawHttp> ConnectAsync(Uri url)
        {
            var raw = new RawHttp { _url = url };
            await raw._tcp.ConnectAsync(IPAddress.Loopback, url.Port);
            raw._stream = raw._tcp.GetStream();
            return raw;
        }

        /// <summary>One POST and its reply, or null when the door closed the connection instead of answering.</summary>
        public async Task<(int Status, string Body)?> SendAsync(string body, string? bearer)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"POST {_url.AbsolutePath} HTTP/1.1\r\nHost: 127.0.0.1:{_url.Port}\r\nContent-Type: application/json\r\n" +
                       $"Accept: application/json, text/event-stream\r\nContent-Length: {bytes.Length}\r\n" +
                       (bearer is null ? "" : $"Authorization: Bearer {bearer}\r\n") + "\r\n";
            try
            {
                await _stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await _stream.WriteAsync(bytes);
                var header = new List<byte>();
                while (header.Count < 4 || !(header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n'))
                {
                    var one = new byte[1];
                    if (await _stream.ReadAsync(one) == 0) return null;
                    header.Add(one[0]);
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var status = int.Parse(lines[0].Split(' ')[1]);
                var length = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Select(l => int.Parse(l[15..].Trim())).FirstOrDefault();
                var reply = new byte[length];
                for (var have = 0; have < length;)
                {
                    var read = await _stream.ReadAsync(reply.AsMemory(have));
                    if (read == 0) return null;
                    have += read;
                }
                return (status, Encoding.UTF8.GetString(reply));
            }
            catch (IOException) { return null; }
        }

        /// <summary>Whether the door closed this connection within <paramref name="bound"/>: a read that ends, or fails.</summary>
        public async Task<bool> ClosedWithinAsync(TimeSpan bound)
        {
            using var cts = new CancellationTokenSource(bound);
            try { return await _stream.ReadAsync(new byte[1], cts.Token) == 0; }
            catch (OperationCanceledException) { return false; }
            catch (IOException) { return true; }
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal static async Task<Reply> Post(HttpClient http, Uri url, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await http.SendAsync(request);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
