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
    /// <c>U-thinking-seats-c</c> ITEM 6 (owed from U-thinking-seats-b's review R5; CRED-001, Law 2): A RUNTIME REPLACED WITH BYTES OF
    /// THE SAME LENGTH, ITS LAST WRITE PUT BACK, IS NOT THE PIN. A first connection proves the pinned file and primes the digest cache;
    /// the file is then rewritten in place with other bytes of the same length and its last-write time restored — exactly what the old
    /// key (path, length, last write) could not see — and a NEW door connection presenting the same live grant must be refused.
    ///
    /// <para>RED at base: the second connection was served (200) on the cached digest of the bytes that were gone.</para>
    /// </summary>
    [Fact]
    public async Task A_runtime_replaced_with_equal_length_bytes_and_its_mtime_restored_is_not_the_pin()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ta-pin-{Guid.NewGuid():n}"[..20]);
        Directory.CreateDirectory(dir);
        try
        {
            var runtime = Path.Combine(dir, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            var approved = new byte[64 * 1024];
            Random.Shared.NextBytes(approved);
            await File.WriteAllBytesAsync(runtime, approved);
            var rule = new PeerRule(runtime, PeerImage.HashOf(runtime), null, null, BrokerDoor.RuntimeWord);

            var grants = new AgentGrants();
            var research = grants.IssueHere(CouncilRoles.Research, "attempt-same-length");
            await using var door = new BrokerDoor { Grants = grants, Image = () => rule, ImageOf = _ => runtime };
            door.Start();

            using (var first = Client(research.Token))
            {
                var served = await Post(first, door.Url!, Rpc(1, "initialize"));
                Assert.True(served.Status == HttpStatusCode.OK, $"the pinned file itself was refused: {(int)served.Status} {served.Body}");
            }

            // THE SWAP: other bytes, the same length, the last write put back to what the cache saw.
            var written = File.GetLastWriteTimeUtc(runtime);
            var swapped = (byte[])approved.Clone();
            for (var i = 0; i < swapped.Length; i++) swapped[i] ^= 0x5a;
            await File.WriteAllBytesAsync(runtime, swapped);
            File.SetLastWriteTimeUtc(runtime, written);
            Assert.Equal((approved.LongLength, written), (new FileInfo(runtime).Length, File.GetLastWriteTimeUtc(runtime)));

            using var second = Client(research.Token);
            var reply = await Post(second, door.Url!, Rpc(2, "initialize"));
            Assert.True(reply.Status == HttpStatusCode.Forbidden,
                $"a runtime replaced with equal-length bytes and its last write restored was served as the pin: {(int)reply.Status} {reply.Body}");
            Assert.Contains(BrokerDoor.RuntimeWord, reply.Body);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (Exception) { } }
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
    /// (i) AN ORGANISATION VERB ON THE DOOR IS REFUSED TO AN ATTEMPT RECORDED UNCONFINED — by the seat assurance the activation
    /// gate RECORDED for the attempt, never by role or headship (ORCHESTRATION § 2.3, § 2.9). No verb exists on main, so a test
    /// verb stands in; the product's gate today records nothing and refuses every verb; a gate answering as the activation gate
    /// does for an unconfined attempt refuses it in words, does not offer it, and records the call; a gate that passes the attempt
    /// has the verb offered and served — the same role, so only the gate decides.
    /// </summary>
    [Fact]
    public async Task An_org_verb_on_the_door_is_refused_to_an_attempt_recorded_unconfined()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var verb = new BrokerDoor.DoorVerb(new ToolSpec("test-verb-show", "A verb that exists only in this test.", new Dictionary<string, object>
            {
                ["type"] = "object", ["properties"] = new Dictionary<string, object>()
            }),
            (_, _, _) => Task.FromResult(ToolAnswer.Ok("the test verb answered", "test-verb-show")));
        var recorded = new Dictionary<string, string> { ["attempt-unconfined"] = "unconfined", ["attempt-carried"] = "carried" };
        string? AsRecorded(AgentGrant g) => recorded[g.AttemptId] == "unconfined"
            ? $"attempt {g.AttemptId} was recorded unconfined, which carries no organisational decision right"
            : null;

        foreach (var (attempt, gate, served) in new (string, Func<AgentGrant, string?>?, bool)[]
                 {
                     ("attempt-today", null, false),
                     ("attempt-unconfined", AsRecorded, false),
                     ("attempt-carried", AsRecorded, true)
                 })
        {
            var grants = new AgentGrants();
            using var research = grants.IssueHere(CouncilRoles.Research, attempt);
            var folder = Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(folder);
            var ledger = new ToolCallStore(db);
            await using var door = gate is null
                ? new BrokerDoor { Grants = grants, Image = SelfImage, Verbs = [verb], Ledger = ledger,
                    Tools = g => BrokerDoor.ToolsFor(g, _ => folder, () => server, ledger) }
                : new BrokerDoor { Grants = grants, Image = SelfImage, Verbs = [verb], Ledger = ledger, VerbGate = gate,
                    Tools = g => BrokerDoor.ToolsFor(g, _ => folder, () => server, ledger) };
            door.Start();
            using var http = Client(research.Token);

            var listed = (await Post(http, door.Url!, Rpc(1, "tools/list"))).Json.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            var call = (await Post(http, door.Url!, Rpc(2, "tools/call", new { name = "test-verb-show", arguments = new { } })))
                .Json.GetProperty("result");
            var rows = ledger.ForAttempt(attempt);

            if (served)
            {
                Assert.Contains("test-verb-show", listed);
                Assert.False(call.GetProperty("isError").GetBoolean(), $"an attempt the gate passes was refused the verb: {Text(call)}");
                Assert.Equal("the test verb answered", Text(call));
            }
            else
            {
                Assert.DoesNotContain("test-verb-show", listed);
                Assert.True(call.GetProperty("isError").GetBoolean(), $"an organisation verb was served to {attempt}: {Text(call)}");
                Assert.Contains("is an organisation verb", Text(call));
            }
            Assert.True(rows.Count(r => r.Tool == "test-verb-show") == 1, $"the verb call of {attempt} was not recorded once");
        }
    }

    /// <summary>
    /// ITEM 4 PASS: THE PRODUCT'S VERB GATE IS THE ATTEMPT'S RECORDED ASSURANCE AGAINST PART A'S POLICY
    /// (<see cref="CapabilityPolicy.Minimum"/> of <see cref="Capability.DecisionVerbs"/>, MEDIATED — the orchestrator's ruling).
    /// Rows written to <c>ai_attempt</c> as the admission writes them: an attempt recorded unconfined is refused the test verb in
    /// words, not offered it, and the verb never runs; a puppet (mediated) and a confined attempt are offered and served; an
    /// attempt whose assurance cannot be read — a word this build does not know, no word, or no row — is refused.
    /// </summary>
    [Fact]
    public async Task The_door_serves_a_verb_by_the_assurance_recorded_for_the_attempt()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var attempts = new AiAttemptStore(db);
        void Recorded(string id, string? seatClass, string? assurance) => attempts.Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = CouncilRoles.Research, SeatClass = seatClass, Assurance = assurance
        });
        Recorded("attempt-v-unconfined", SeatClasses.Cli, SeatClasses.Name(SeatAssurance.Unconfined));
        Recorded("attempt-v-puppet", SeatClasses.Puppet, SeatClasses.Name(SeatAssurance.Mediated));
        Recorded("attempt-v-confined", SeatClasses.Cell, SeatClasses.Name(SeatAssurance.Confined));
        Recorded("attempt-v-unknown", SeatClasses.Cli, "carried");
        Recorded("attempt-v-none", null, null);

        var ran = new List<string>();
        var verb = new BrokerDoor.DoorVerb(new ToolSpec("test-verb-show", "A verb that exists only in this test.",
                new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() }),
            (g, _, _) => { lock (ran) ran.Add(g.AttemptId); return Task.FromResult(ToolAnswer.Ok("the test verb answered", "test-verb-show")); });

        foreach (var (attempt, served, words) in new (string, bool, string)[]
                 {
                     ("attempt-v-puppet", true, ""),
                     ("attempt-v-confined", true, ""),
                     ("attempt-v-unconfined", false, "is recorded unconfined"),
                     ("attempt-v-unknown", false, "could not be read"),
                     ("attempt-v-none", false, "could not be read"),
                     ("attempt-v-no-row", false, "could not be read")
                 })
        {
            var grants = new AgentGrants();
            using var research = grants.IssueHere(CouncilRoles.Research, attempt);
            var folder = Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(folder);
            var ledger = new ToolCallStore(db);
            await using var door = new BrokerDoor
            {
                Grants = grants, Image = SelfImage, Verbs = [verb], Ledger = ledger, Pipe = () => server,
                VerbGate = BrokerDoor.RecordedAssurance(attempts.Get),
                Tools = g => BrokerDoor.ToolsFor(g, _ => folder, () => server, ledger)
            };
            door.Start();
            using var http = Client(research.Token);

            var listed = (await Post(http, door.Url!, Rpc(1, "tools/list"))).Json.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            var call = (await Post(http, door.Url!, Rpc(2, "tools/call", new { name = "test-verb-show", arguments = new { } })))
                .Json.GetProperty("result");

            if (served)
            {
                Assert.True(listed.Contains("test-verb-show"), $"{attempt} was not offered the verb; tools/call said: {Text(call)}");
                Assert.False(call.GetProperty("isError").GetBoolean(), $"{attempt} was refused the verb: {Text(call)}");
                Assert.Equal("the test verb answered", Text(call));
                Assert.Contains(attempt, ran);
            }
            else
            {
                Assert.DoesNotContain("test-verb-show", listed);
                Assert.True(call.GetProperty("isError").GetBoolean(), $"an organisation verb was served to {attempt}: {Text(call)}");
                Assert.Contains("is an organisation verb", Text(call));
                Assert.Contains(words, Text(call));
                Assert.DoesNotContain(attempt, ran);
            }
            Assert.True(ledger.ForAttempt(attempt).Count(r => r.Tool == "test-verb-show") == 1, $"the verb call of {attempt} was not recorded once");
        }
    }

    /// <summary>
    /// O5a ON THE DOOR (<c>U-org-assignments-a</c> fix pass 1, item 4): THE PRODUCT'S MANDATE VERBS, composed as the app composes
    /// them (<see cref="BrokerDoor.OrganisationVerbs"/> behind <see cref="BrokerDoor.RecordedAssurance"/>), are one per
    /// <see cref="Ops.Organisation"/> op and named apart from every pipe op and harness tool, so the door opens with them. A head's
    /// BROKERED attempt and a member's PUPPET (mediated) attempt are offered all of them and run <c>mandate_list</c> /
    /// <c>mandate_show</c> through the pipe under the grant, seeing their own mandate; the legacy <c>research</c> on an attempt
    /// recorded UNCONFINED — the head of its division — is offered none and refused in words, and the verb never reaches the pipe's
    /// record. Watched red with the verbs served without <see cref="BrokerDoor.VerbGate"/>.
    /// </summary>
    [Fact]
    public async Task The_mandate_verbs_on_the_door_are_served_by_recorded_assurance_never_headship()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        const string head = "pos-d0c0ffee0001", member = "pos-d0c0ffee0002", team = "team-d0c0ffee";
        void Exec(string sql, params (string, object?)[] ps) => db.Write(_ =>
        {
            using var c = db.Cmd(sql, ps);
            return c.ExecuteNonQuery();
        });
        const string stamp = "2026-10-11T12:00:00.0000000Z";
        Exec("INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", team), ("$p", OrgIds.Research), ("$at", stamp));
        foreach (var p in new[] { head, member })
            Exec("INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
                ("$id", p), ("$u", team), ("$h", $"org/{p}"), ("$at", stamp));
        Exec("UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", head), ("$t", team));
        var mandate = gw.Org.Assign(new MandateIssue(head, null, member, MandateKind.Exploration, "does the skew predict the hour", null,
            null, false, DateTimeOffset.UtcNow.AddDays(7), 1m, 2), DateTimeOffset.UtcNow).Id;

        var attempts = new AiAttemptStore(db);
        foreach (var (id, role, seat) in new[]
                 {
                     ("attempt-o-head", head, SeatClasses.Harness), ("attempt-o-member", member, SeatClasses.Puppet),
                     ("attempt-o-research", CouncilRoles.Research, SeatClasses.Cli)
                 })
            Assert.True(attempts.Begin(new AiAttempt
            {
                Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, SeatClass = seat,
                Assurance = SeatClasses.Name(SeatClasses.AssuranceOf(seat)!.Value)
            }).Admitted);

        var verbs = BrokerDoor.OrganisationVerbs(() => server);
        var names = verbs.Select(v => v.Spec.Name).ToList();
        Assert.Equal(Ops.Organisation.Select(BrokerDoor.VerbName), names);
        var taken = GatewaySchema.Ops().Select(o => o.Op).Concat(GrantedWorkerTools.Granted.Select(t => t.Name)).Append(Ops.Hello)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(names, n => Assert.DoesNotContain(n, taken));

        foreach (var (attempt, role, served) in new[]
                 {
                     ("attempt-o-head", head, true), ("attempt-o-member", member, true), ("attempt-o-research", CouncilRoles.Research, false)
                 })
        {
            var grants = new AgentGrants();
            using var grant = grants.IssueHere(role, attempt);
            var folder = Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(folder);
            var ledger = new ToolCallStore(db);
            await using var door = new BrokerDoor
            {
                Grants = grants, Image = SelfImage, Verbs = verbs, Ledger = ledger, Pipe = () => server,
                VerbGate = BrokerDoor.RecordedAssurance(attempts.Get),
                Tools = g => BrokerDoor.ToolsFor(g, _ => folder, () => server, ledger)
            };
            door.Start();
            using var http = Client(grant.Token);

            var listed = (await Post(http, door.Url!, Rpc(1, "tools/list"))).Json.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
            var list = (await Post(http, door.Url!, Rpc(2, "tools/call", new { name = "mandate_list", arguments = new { } })))
                .Json.GetProperty("result");
            var show = (await Post(http, door.Url!, Rpc(3, "tools/call", new { name = "mandate_show", arguments = new { mandate } })))
                .Json.GetProperty("result");

            if (served)
            {
                Assert.All(names, n => Assert.Contains(n, listed));
                Assert.False(list.GetProperty("isError").GetBoolean(), $"{attempt} was refused mandate_list: {Text(list)}");
                Assert.Contains(mandate, Text(list));
                Assert.False(show.GetProperty("isError").GetBoolean(), $"{attempt} was refused mandate_show: {Text(show)}");
                Assert.Contains("does the skew predict the hour", Text(show));
            }
            else
            {
                Assert.Empty(listed.Intersect(names));
                foreach (var refused in new[] { list, show })
                {
                    Assert.True(refused.GetProperty("isError").GetBoolean(), $"a mandate verb was served to {attempt}: {Text(refused)}");
                    Assert.Contains("is an organisation verb", Text(refused));
                    Assert.Contains("is recorded unconfined", Text(refused));
                }
            }
            Assert.Equal(2, ledger.ForAttempt(attempt).Count(r => r.Tool.StartsWith("mandate_", StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// O5b ON THE DOOR (<c>U-org-decisions</c>): THE DECISION, FORECAST, ESCALATION AND LATERAL VERBS join
    /// <see cref="BrokerDoor.OrganisationVerbs"/> as one <c>_</c>-named door verb each, behind the same
    /// <see cref="BrokerDoor.RecordedAssurance"/> gate. A position's BROKERED attempt is offered all eight and runs
    /// <c>decision_record</c> and <c>lateral_list</c> through the pipe under its grant; the legacy <c>research</c> on an attempt recorded
    /// UNCONFINED is offered none and refused in words, and nothing is written.
    /// </summary>
    [Fact]
    public async Task The_decision_and_note_verbs_on_the_door_are_served_by_recorded_assurance_never_headship()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        const string head = "pos-d0c0ffee0011", member = "pos-d0c0ffee0012", team = "team-d0c0ffee01";
        void Exec(string sql, params (string, object?)[] ps) => db.Write(_ =>
        {
            using var c = db.Cmd(sql, ps);
            return c.ExecuteNonQuery();
        });
        const string stamp = "2026-10-11T12:00:00.0000000Z";
        Exec("INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", team), ("$p", OrgIds.Research), ("$at", stamp));
        foreach (var p in new[] { head, member })
            Exec("INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
                ("$id", p), ("$u", team), ("$h", $"org/{p}"), ("$at", stamp));
        Exec("UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", head), ("$t", team));
        gw.Org.SendLateral(member, null, head, "the venue B feed skips the 02:00 print", [], DateTimeOffset.UtcNow);

        var attempts = new AiAttemptStore(db);
        foreach (var (id, role, seat) in new[] { ("attempt-d-head", head, SeatClasses.Harness), ("attempt-d-research", CouncilRoles.Research, SeatClasses.Cli) })
            Assert.True(attempts.Begin(new AiAttempt
            {
                Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, SeatClass = seat,
                Assurance = SeatClasses.Name(SeatClasses.AssuranceOf(seat)!.Value)
            }).Admitted);

        var verbs = BrokerDoor.OrganisationVerbs(() => server);
        string[] eight = [.. new[] { Ops.DecisionRecord, Ops.DecisionList, Ops.DecisionShow, Ops.ForecastAdd, Ops.EscalationRaise,
            Ops.EscalationList, Ops.LateralSend, Ops.LateralList }.Select(BrokerDoor.VerbName)];
        Assert.Equal(["decision_record", "decision_list", "decision_show", "forecast_add", "escalation_raise", "escalation_list",
            "lateral_send", "lateral_list"], eight);
        Assert.All(eight, n => Assert.Contains(verbs, v => v.Spec.Name == n));

        foreach (var (attempt, role, served) in new[] { ("attempt-d-head", head, true), ("attempt-d-research", CouncilRoles.Research, false) })
        {
            var grants = new AgentGrants();
            using var grant = grants.IssueHere(role, attempt);
            var folder = Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(folder);
            var ledger = new ToolCallStore(db);
            await using var door = new BrokerDoor
            {
                Grants = grants, Image = SelfImage, Verbs = verbs, Ledger = ledger, Pipe = () => server,
                VerbGate = BrokerDoor.RecordedAssurance(attempts.Get),
                Tools = g => BrokerDoor.ToolsFor(g, _ => folder, () => server, ledger)
            };
            door.Start();
            using var http = Client(grant.Token);

            var listed = (await Post(http, door.Url!, Rpc(1, "tools/list"))).Json.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
            var record = (await Post(http, door.Url!, Rpc(2, "tools/call", new
            {
                name = "decision_record",
                arguments = new { options = new[] { "read the note", "ignore it" }, choice = "read the note", rationale = "a data defect costs a run" }
            }))).Json.GetProperty("result");
            var notes = (await Post(http, door.Url!, Rpc(3, "tools/call", new { name = "lateral_list", arguments = new { } }))).Json.GetProperty("result");

            if (served)
            {
                Assert.All(eight, n => Assert.Contains(n, listed));
                Assert.False(record.GetProperty("isError").GetBoolean(), $"{attempt} was refused decision_record: {Text(record)}");
                Assert.Contains("dec-", Text(record));
                Assert.False(notes.GetProperty("isError").GetBoolean(), $"{attempt} was refused lateral_list: {Text(notes)}");
                Assert.Contains("the venue B feed skips the 02:00 print", Text(notes));
            }
            else
            {
                Assert.Empty(listed.Intersect(eight));
                foreach (var refused in new[] { record, notes })
                {
                    Assert.True(refused.GetProperty("isError").GetBoolean(), $"an organisation verb was served to {attempt}: {Text(refused)}");
                    Assert.Contains("is recorded unconfined", Text(refused));
                }
            }
        }
        Assert.Single(gw.Org.DecisionsOf(head));
        Assert.Empty(gw.Org.DecisionsOf(CouncilRoles.Research));
    }

    /// <summary>(j), the verbs' half: a verb named like a pipe op — <c>cancel</c> moves money — or a harness tool keeps the door shut.</summary>
    [Fact]
    public async Task A_verb_named_like_a_pipe_op_or_a_harness_tool_keeps_the_door_shut()
    {
        foreach (var name in new[] { Ops.Cancel, GrantedWorkerTools.Trade })
        {
            var verb = new BrokerDoor.DoorVerb(new ToolSpec(name, "a verb with a taken name", new Dictionary<string, object>()),
                (_, _, _) => Task.FromResult(ToolAnswer.Ok("never")));
            await using var door = new BrokerDoor { Grants = new AgentGrants(), Verbs = [verb] };
            var refused = Assert.Throws<InvalidOperationException>(door.Start);
            Assert.Contains($"'{name}'", refused.Message);
            Assert.Null(door.Url);
        }
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
    /// FIX 1 (R1, UNKNOWN-001 and recovery): DISPOSING THE DOOR WAITS FOR AN ORDER IT DISPATCHED. The broker holds the placement
    /// after the gateway wrote its DISPATCHING row; the door is disposed; disposal is still waiting when the broker lets go, and
    /// when it returns the order is settled — durably, with no polling — and its caller got the answer. A door that cancelled
    /// its calls and awaited only its accept loop returned at once, leaving a placement at the broker to settle after AppHost
    /// closed the gateway and the database under it.
    /// </summary>
    [Fact]
    public async Task Disposing_the_door_waits_for_an_order_it_dispatched_to_settle()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _db = db;
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var grants = new AgentGrants();
        using var chair = grants.IssueHere(CouncilRoles.Operations, "attempt-door-drain");
        Assert.Null(chair.OrdersWithheld);
        var door = Surface(grants, server, db);
        door.Start();
        var (entered, release) = HoldTheDispatch(gw, conn);

        using var http = Client(chair.Token);
        var buy = Post(http, door.Url!, Rpc(1, "tools/call", new
        {
            name = GrantedWorkerTools.Trade,
            arguments = new { op = Ops.Buy, symbol = "ES", quantity = "1", request_id = "door-drain-1" }
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var disposal = door.DisposeAsync().AsTask();
        await Task.Delay(500);
        Assert.False(disposal.IsCompleted,
            "the door's disposal returned while an order it had dispatched was still at the broker");
        release.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(60));

        // READ WITH NO POLLING: if disposal waited, the settle is already durable.
        var state = gw.GetRequest("door-drain-1")!.State;
        Assert.True(state == ExecutionState.FILLED, $"the door's order was {state} when the door's disposal returned");
        Assert.Equal(0, Dispatching(gw));
        var reply = await buy.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(reply.Json.GetProperty("result").GetProperty("isError").GetBoolean(),
            $"the caller of a placement the door drained was not told it placed: {reply.Body}");
        await server.DisposeAsync();
        Assert.Single(conn.Broker.Orders);
    }

    /// <summary>
    /// FIX 1, THE SAME DRAIN FROM THE OTHER SIDE: A DOOR CALL IS ONE OF THE PIPE'S TRACKED HANDLERS. A platform switch disposes
    /// the pipe server and not the door (<c>AppHost.SwitchConnectorAsync</c>), so the pipe's own drain — one implementation —
    /// must wait for an order the door dispatched exactly as it waits for one a pipe frame dispatched.
    /// </summary>
    [Fact]
    public async Task The_pipe_servers_drain_waits_for_an_order_the_door_dispatched()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _db = db;
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var grants = new AgentGrants();
        using var chair = grants.IssueHere(CouncilRoles.Operations, "attempt-door-switch");
        await using var door = Surface(grants, server, db);
        door.Start();
        var (entered, release) = HoldTheDispatch(gw, conn);

        using var http = Client(chair.Token);
        var buy = Post(http, door.Url!, Rpc(1, "tools/call", new
        {
            name = GrantedWorkerTools.Trade,
            arguments = new { op = Ops.Buy, symbol = "ES", quantity = "1", request_id = "door-switch-1" }
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var disposal = server.DisposeAsync().AsTask();
        await Task.Delay(500);
        Assert.False(disposal.IsCompleted,
            "the pipe server's disposal returned while an order the door had dispatched through it was still at the broker");
        release.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(ExecutionState.FILLED, gw.GetRequest("door-switch-1")!.State);
        Assert.Equal(0, Dispatching(gw));
        await buy.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// FIX 2 (R2): A LATER LAUNCH OF THE SAME ROLE IS ANOTHER SESSION ON THE DOOR. The first launch's cancel-all completes; a
    /// separately issued launch of the same role, with a working order of its own, reuses that request id — and is refused
    /// with the gateway's own "belongs to another session", never answered from the first sweep's result while its order
    /// stays working. A session named by role alone made every launch of a role one session for ever.
    /// </summary>
    [Fact]
    public async Task A_later_launch_reusing_a_sweeps_request_id_is_another_session()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var grants = new AgentGrants();
        await using var door = Surface(grants, server, db);
        door.Start();
        var sweep = Rpc(1, "tools/call", new
        {
            name = GrantedWorkerTools.Trade, arguments = new { op = Ops.CancelAll, request_id = "door-sweep-1" }
        });

        var first = grants.IssueHere(CouncilRoles.Operations, "attempt-sweep-first");
        using (var http = Client(first.Token))
        {
            var done = await Post(http, door.Url!, sweep);
            Assert.False(done.Json.GetProperty("result").GetProperty("isError").GetBoolean(), $"the first sweep was refused: {done.Body}");
        }
        first.Dispose();

        conn.Faults.Fill = Connectors.Fake.FillBehaviour.LeaveWorking;
        using var later = grants.IssueHere(CouncilRoles.Operations, "attempt-sweep-later");
        Assert.Null(later.OrdersWithheld);
        using var http2 = Client(later.Token);
        var working = await Post(http2, door.Url!, Rpc(2, "tools/call", new
        {
            name = GrantedWorkerTools.Trade,
            arguments = new { op = Ops.Buy, symbol = "ES", quantity = "1", request_id = "door-working-1" }
        }));
        Assert.False(working.Json.GetProperty("result").GetProperty("isError").GetBoolean(), $"the later launch's buy was refused: {working.Body}");

        var reused = await Post(http2, door.Url!, sweep);
        var result = reused.Json.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean(),
            $"a later launch reusing the first launch's sweep id was answered from the first sweep: {reused.Body}");
        Assert.Contains("belongs to another session", Text(result));
        Assert.Contains(conn.Broker.Orders, o => o.State == ExecutionState.WORKING);
    }

    /// <summary>
    /// FIX 3 (R3): THE ROW MEASURES WHAT THE CALLER RECEIVED. A read longer than one door result is cut by the door; the
    /// <c>tool_call</c> row's bytes are the UTF-8 length of the text the MCP reply carried, not of the answer before the cut.
    /// </summary>
    [Fact]
    public async Task A_cut_door_result_is_recorded_at_the_bytes_the_caller_received()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        var home = Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(home, "in"));
        File.WriteAllText(Path.Combine(home, "in", "long.md"), new string('é', BrokerDoor.MaxResultBytes));
        var grants = new AgentGrants();
        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-door-bytes");
        await using var door = Surface(grants, server, db, home);
        door.Start();

        using var http = Client(research.Token);
        var cut = await Post(http, door.Url!, Rpc(1, "tools/call", new { name = GrantedWorkerTools.ReadFile, arguments = new { path = "in/long.md" } }));
        var received = Text(cut.Json.GetProperty("result"));
        Assert.Contains("fetch by id", received);

        var rows = new ToolCallStore(db).ForAttempt("attempt-door-bytes");
        Assert.Single(rows);
        Assert.True(rows[0].Bytes == Encoding.UTF8.GetByteCount(received),
            $"the row says {rows[0].Bytes} bytes were delivered; the caller received {Encoding.UTF8.GetByteCount(received)}");
        // AND THE CAP IS THE WHOLE RESULT'S, THE WORDS SAYING SO INCLUDED: one door result carries at most MaxResultBytes.
        Assert.True(Encoding.UTF8.GetByteCount(received) <= BrokerDoor.MaxResultBytes,
            $"one door result carried {Encoding.UTF8.GetByteCount(received)} bytes against a cap of {BrokerDoor.MaxResultBytes}");
    }

    /// <summary>
    /// THE BROKER HOLDS THE PLACEMENT, AND ONLY IT: every connector call waits on the latch once the gateway has a DISPATCHING
    /// row — the placement's own wire — and none before, so the pre-flight reads run free.
    /// </summary>
    static (TaskCompletionSource Entered, TaskCompletionSource Release) HoldTheDispatch(TradingGateway gw, Connectors.Fake.FakeConnector conn)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Faults.Hold = () =>
        {
            if (Dispatching(gw) == 0) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        return (entered, release);
    }

    static int Dispatching(TradingGateway gw) => gw.Requests.Query("execution_state='DISPATCHING'").Count;

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
            Grants = grants, Image = SelfImage, Pipe = () => server,
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
