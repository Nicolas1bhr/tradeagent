using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
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
    /// (e) A RESEARCH GRANT ASKING THE DOOR TO BUY IS REFUSED AS ON THE PIPE, AND RECORDED. The door's <c>trade</c> is the harness's,
    /// so the gateway's own role check refuses it with its own code; the call is a <c>tool_call</c> row under the grant's attempt;
    /// and nothing reached the broker.
    /// </summary>
    [Fact]
    public async Task A_research_grant_asking_the_door_to_buy_is_refused_as_on_the_pipe_and_recorded()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var grants = new AgentGrants();
        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-door-buy");
        await using var door = Surface(grants, server, db);
        door.Start();

        using var http = Client(research.Token);
        var reply = await Post(http, door.Url!, Rpc(7, "tools/call",
            new { name = GrantedWorkerTools.Trade, arguments = new { op = Ops.Buy, symbol = "ES", quantity = "1" } }));

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        var result = reply.Json.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean(), $"a research grant's buy through the door was served: {reply.Body}");
        Assert.Contains(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), Text(result));

        var rows = new ToolCallStore(db).ForAttempt("attempt-door-buy");
        Assert.True(rows.Count == 1, $"the door's call was recorded {rows.Count} times under its attempt, not once");
        Assert.Equal(GrantedWorkerTools.Trade, rows[0].Tool);
        Assert.Equal(CouncilRoles.Research, rows[0].Role);
        Assert.False(rows[0].Served);
        Assert.Empty(await conn.GetOrdersAsync(conn.Broker.AccountId, includeInactive: true, since: null));
    }

    /// <summary>
    /// ORDER-001 THROUGH THE DOOR: an Operations launch issued beside another live launch holds no order permission
    /// (<c>U-grant-tree</c>), and the door carries that — its buy is refused with the grant's own words, as on the pipe, never
    /// served because the door handed the gateway the role alone.
    /// </summary>
    [Fact]
    public async Task An_operations_grant_issued_beside_a_neighbour_may_not_buy_through_the_door()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var grants = new AgentGrants();
        using var neighbour = grants.IssueHere(CouncilRoles.Research, "attempt-neighbour");
        using var chair = grants.IssueHere(CouncilRoles.Operations, "attempt-chair-withheld");
        Assert.NotNull(chair.OrdersWithheld);
        await using var door = Surface(grants, server, db);
        door.Start();

        using var http = Client(chair.Token);
        var reply = await Post(http, door.Url!, Rpc(8, "tools/call",
            new { name = GrantedWorkerTools.Trade, arguments = new { op = Ops.Buy, symbol = "ES", quantity = "1" } }));

        var result = reply.Json.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean(),
            $"an Operations launch started beside a neighbour bought through the door: {reply.Body}");
        Assert.Contains("holds no permission to trade", Text(result));
        Assert.Empty(await conn.GetOrdersAsync(conn.Broker.AccountId, includeInactive: true, since: null));
    }

    /// <summary>
    /// (g) GUARD: THE DOOR LISTS EXACTLY THE HARNESS'S TOOLS AND ANSWERS A READ — over a real HTTP client, on every runner: the
    /// names, in order, of <see cref="GrantedWorkerTools.Granted"/>, each with its schema; a <c>read_file</c> of the role's own
    /// brief served; a result past the byte cap cut with the words that say how to get the rest.
    /// </summary>
    [Fact]
    public async Task The_door_lists_exactly_the_harness_tools_and_answers_a_read()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var home = Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(home, "in"));
        File.WriteAllText(Path.Combine(home, "in", "brief.md"), "the brief the app delivered");
        File.WriteAllText(Path.Combine(home, "in", "long.md"), new string('x', BrokerDoor.MaxResultBytes * 2));
        var grants = new AgentGrants();
        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-door-read");
        await using var door = Surface(grants, server, db, home);
        door.Start();

        using var http = Client(research.Token);
        var list = await Post(http, door.Url!, Rpc(1, "tools/list"));
        var tools = list.Json.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(GrantedWorkerTools.Granted.Select(t => t.Name), tools.Select(t => t.GetProperty("name").GetString()));
        Assert.All(tools, t => Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString()));

        var read = await Post(http, door.Url!, Rpc(2, "tools/call", new { name = GrantedWorkerTools.ReadFile, arguments = new { path = "in/brief.md" } }));
        var answer = read.Json.GetProperty("result");
        Assert.False(answer.GetProperty("isError").GetBoolean(), $"the door refused a read of the role's own brief: {read.Body}");
        Assert.Equal("the brief the app delivered", Text(answer));

        var cut = await Post(http, door.Url!, Rpc(3, "tools/call", new { name = GrantedWorkerTools.ReadFile, arguments = new { path = "in/long.md" } }));
        var cutText = Text(cut.Json.GetProperty("result"));
        Assert.True(Encoding.UTF8.GetByteCount(cutText) <= BrokerDoor.MaxResultBytes + 512, $"a door result of {cutText.Length} chars passed the cap");
        Assert.Contains("fetch by id", cutText);
    }

    /// <summary>
    /// (h) GUARD: THE PIPE AND THE HARNESS BEHAVE AS BEFORE. The in-process harness reaches the gateway with the role and attempt
    /// the app assigned, through <see cref="GatewayPipeServer.CallAsync"/>, exactly as before the door — its Research buy refused
    /// with the gateway's code, its Operations buy placed — and the door's refusal of the same Research buy is word for word the
    /// harness's.
    /// </summary>
    [Fact]
    public async Task The_pipe_and_the_harness_behave_as_before()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var home = Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(home);
        const string buy = """{"op":"buy","symbol":"ES","quantity":"1","request_id":"same-buy-1"}""";

        var harness = new GrantedWorkerTools(CouncilRoles.Research, () => home, () => "attempt-harness", () => server,
            session: () => "door-research");
        var byHarness = await harness.InvokeAsync(new ToolRequest("h1", GrantedWorkerTools.Trade, buy));
        Assert.False(byHarness.Served);
        Assert.Contains(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), byHarness.Content);

        var grants = new AgentGrants();
        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-harness");
        await using var door = Surface(grants, server, db, home);
        door.Start();
        using var http = Client(research.Token);
        var byDoor = await Post(http, door.Url!, Rpc(1, "tools/call",
            new { name = GrantedWorkerTools.Trade, arguments = JsonDocument.Parse(buy).RootElement }));
        Assert.Equal(byHarness.Content, Text(byDoor.Json.GetProperty("result")));

        var chair = new GrantedWorkerTools(CouncilRoles.Operations, () => home, () => "attempt-harness-chair", () => server);
        var placed = await chair.InvokeAsync(new ToolRequest("h2", GrantedWorkerTools.Trade,
            """{"op":"buy","symbol":"ES","quantity":"1","request_id":"harness-chair-1"}"""));
        Assert.True(placed.Served, $"the in-process harness's Operations buy was refused: {placed.Content}");
    }

    /// <summary>
    /// (j) GUARD: NO DOOR TOOL SHARES A NAME WITH A PIPE OP IT IS NOT. A tool named like an op of the gateway carries exactly that
    /// op and nothing else — <c>report</c> is the report — so a name a seat reads can never mean one thing on the door and
    /// another on the pipe (the orchestrator's 2026-10-16 ruling: <c>cancel</c> is already an order op that moves money).
    /// </summary>
    [Fact]
    public async Task No_door_tool_shares_a_name_with_a_pipe_op_it_is_not()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var grants = new AgentGrants();
        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-door-names");
        await using var door = Surface(grants, server, db);
        door.Start();
        using var http = Client(research.Token);
        var names = (await Post(http, door.Url!, Rpc(1, "tools/list"))).Json.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        Assert.NotEmpty(names);

        var pipeOps = GatewaySchema.Ops().Select(o => o.Op).Append(Ops.Hello).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(pipeOps.Contains))
            Assert.True(GrantedWorkerTools.OpsOf(name).SequenceEqual([name]),
                $"the door's tool '{name}' shares its name with a pipe op and carries {string.Join(", ", GrantedWorkerTools.OpsOf(name))}");
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
        // THE FILE THAT RUNS, never a link to it: the kernel names a peer by its resolved program (measured: a link refused the pin).
        Assert.True(rule.ExpectedPath is null || new FileInfo(rule.ExpectedPath).LinkTarget is null,
            $"the door's pinned runtime is recorded as a link, {rule.ExpectedPath}, which the kernel never names");
    }

    /// <summary>
    /// A door serving the PRODUCT's surface (<see cref="BrokerDoor.ToolsFor"/>) over a real gateway, its calls recorded in
    /// <paramref name="db"/>; this test process's own program as the pinned runtime, and the grants bound to this process.
    /// </summary>
    static BrokerDoor Surface(AgentGrants grants, GatewayPipeServer server, Database db, string? home = null)
    {
        var folder = home ?? Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(folder);
        var ledger = new ToolCallStore(db);
        return new BrokerDoor
        {
            Grants = grants, Image = SelfImage,
            Tools = grant => BrokerDoor.ToolsFor(grant, _ => folder, () => server, ledger)
        };
    }

    /// <summary>The text of a tools/call result.</summary>
    static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

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
