using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// What the agent pipe REFUSES, and what it answers when it does.
///
/// Five properties, all of them about the same thing: a frame the gateway cannot name must not be
/// half-understood. A protocol it does not speak must not authenticate; an enumerated value it does
/// not recognise must not become a default; a cap stated in bytes must be counted in bytes; the
/// status served to a caller must be the caller's; and a record the agent did not write must not be
/// readable through an id it can spell.
///
/// Milestone review 2026-09-05: Codex F8, findings 7 and 10, UNVERIFIED 6.
/// </summary>
public class PipeContractTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-pipec-" + Guid.NewGuid().ToString("n")[..12];

    static IpcRequest Buy(string? session = "agent-1", Dictionary<string, JsonElement>? extra = null)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["symbol"] = JsonSerializer.SerializeToElement("ES"),
            ["quantity"] = JsonSerializer.SerializeToElement("1")
        };
        if (extra is not null) foreach (var (k, v) in extra) args[k] = v;
        return new IpcRequest
        {
            Op = Ops.Buy, Session = session,
            RequestId = "pipec-" + Guid.NewGuid().ToString("n")[..8],
            Args = args
        };
    }

    // ── 1. Protocol before session ─────────────────────────────────────────────────────────────
    // Codex F8, first half: "A protocol-incompatible hello still authenticates". The hello reply
    // carried `compatible: false` as INFORMATION and set `authenticated = true` anyway, so a peer
    // built against a protocol this build does not speak went on to trade over it.

    /// <summary>
    /// A hello that names a protocol version this build does not speak is refused, no session comes
    /// of it, and the order that follows it is refused as unauthenticated rather than placed.
    /// </summary>
    [Fact]
    public async Task A_hello_naming_a_protocol_this_build_does_not_speak_gets_no_session()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var raw = await RawFrames.Connect(pipe);
        var hello = await raw.Send(new IpcRequest
        {
            V = Versions.ProtocolVersion + 1, Op = Ops.Hello, Token = IpcToken.Peek(), Session = "agent-1"
        });

        log.WriteLine($"hello(v={Versions.ProtocolVersion + 1}) : ok={hello.Ok} code={hello.Error?.Code} — {hello.Error?.Message}");
        Assert.False(hello.Ok, $"a hello naming protocol {Versions.ProtocolVersion + 1} was accepted: {Json.Write(hello.Data)}");
        Assert.Equal(nameof(ErrorCode.INCOMPATIBLE_PROTOCOL), hello.Error!.Code);
        Assert.Contains(Versions.ProtocolVersion.ToString(), hello.Error.Message);

        var buy = await raw.Send(Buy());
        log.WriteLine($"the buy that follows  : ok={buy.Ok} code={buy.Error?.Code}");
        Assert.False(buy.Ok, "the connection was authenticated by a hello that named another protocol");
        Assert.Equal(nameof(ErrorCode.IPC_UNAUTHENTICATED), buy.Error!.Code);
        Assert.Empty(conn.Broker.Orders);

        var events = ProtocolRefusals(db);
        Assert.Single(events);
        log.WriteLine($"engineering line      : {events[0]}");
    }

    /// <summary>
    /// The ORDER of the two checks, which is the whole of the fix: a hello carrying both a version
    /// this build does not speak and a token it would refuse is answered on the protocol. The
    /// version is a property of the frame; the token is a credential, and a credential is not read
    /// out of a frame whose shape is not agreed.
    /// </summary>
    [Fact]
    public async Task The_version_is_checked_before_the_token_is_read()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var dbh = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var raw = await RawFrames.Connect(pipe);
        var hello = await raw.Send(new IpcRequest
        {
            V = Versions.ProtocolVersion + 1, Op = Ops.Hello, Token = "not-the-token", Session = "agent-1"
        });

        log.WriteLine($"hello(bad v, bad token) : code={hello.Error?.Code}");
        Assert.False(hello.Ok);
        Assert.Equal(nameof(ErrorCode.INCOMPATIBLE_PROTOCOL), hello.Error!.Code);
    }

    /// <summary>
    /// The other direction. A hello at the version this build speaks still authenticates, and the
    /// session it opens still trades — a fix that refused everything would pass the test above.
    /// </summary>
    [Fact]
    public async Task The_current_protocol_version_still_authenticates_and_trades()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var raw = await RawFrames.Connect(pipe);
        var hello = await raw.Send(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Peek(), Session = "agent-1" });
        Assert.True(hello.Ok, $"the current protocol version was refused: {hello.Error?.Message}");

        var buy = await raw.Send(Buy());
        log.WriteLine($"v={Versions.ProtocolVersion} hello then buy : ok={buy.Ok} orders={conn.Broker.Orders.Count}");
        Assert.True(buy.Ok, $"the current version authenticated but could not trade: {buy.Error?.Message}");
        Assert.Single(conn.Broker.Orders);
        Assert.Empty(ProtocolRefusals(db));
    }

    /// <summary>A frame that omits <c>v</c> altogether means the current version, as it always has.</summary>
    [Fact]
    public async Task A_hello_that_omits_the_version_field_is_read_as_the_current_one()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var dbh = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var raw = await RawFrames.Connect(pipe);
        var hello = await raw.SendLine($$"""{"id":"h","op":"hello","token":"{{IpcToken.Peek()}}"}""");
        log.WriteLine($"hello with no 'v' : {hello}");
        Assert.Contains("\"ok\":true", hello);
    }

    // ── 2. Every enumerated field fails closed ─────────────────────────────────────────────────
    // Codex F8, second half: "malformed or undefined TIF values silently become Day/ATAS Default,
    // potentially turning an intended IOC/FOK order into a resting order". `Enum.TryParse` is two
    // failures in one line: a misspelling falls through to the `Day` default, and a NUMBER parses —
    // TryParse accepts the underlying value — so `tif: "999"` became an undefined TimeInForce and
    // was carried to the connector as one.
    //
    // The closed vocabularies the frame carries are `tif` (buy/sell), `all` (orders), `origin`
    // (material-list) and `kind` (material-note). `side` and `type` are NOT carried: side is the op
    // and type is derived from which prices are present.

    [Theory]
    [InlineData("ImmediateOrCancle")]   // the misspelling from the review
    [InlineData("999")]                 // a number: Enum.TryParse accepts it as an undefined value
    [InlineData("IOC")]                 // a plausible abbreviation that is not one of the names
    [InlineData("day ")]                // one stray character
    public async Task A_tif_the_gateway_cannot_name_is_refused_and_nothing_reaches_the_connector(string tif)
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var before = conn.Calls;
        var reply = await client.SendAsync(Buy(extra: new()
        {
            ["tif"] = JsonSerializer.SerializeToElement(tif)
        })).WaitAsync(TimeSpan.FromSeconds(10));

        log.WriteLine($"tif={tif,-20} ok={reply.Ok} code={reply.Error?.Code} " +
                      $"connector saw: {string.Join(", ", conn.Placed.Select(p => $"{p.Tif} ({(int)p.Tif})"))}");
        Assert.False(reply.Ok, $"tif '{tif}' was accepted and became something the agent did not ask for");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains("tif", reply.Error.Message);
        foreach (var name in Enum.GetNames<TradeAgent.ConnectorSdk.TimeInForce>())
            Assert.Contains(name, reply.Error.Message);

        Assert.Equal(before, conn.Calls);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>Every name still works, and arrives at the connector as itself.</summary>
    [Theory]
    [InlineData("Day")]
    [InlineData("GoodTillCancel")]
    [InlineData("ImmediateOrCancel")]
    [InlineData("fillorkill")]           // case-insensitive, as it always was
    public async Task Each_named_tif_is_accepted_and_carried_to_the_connector_unchanged(string tif)
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(Buy(extra: new()
        {
            ["tif"] = JsonSerializer.SerializeToElement(tif)
        })).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(reply.Ok, $"tif '{tif}' was refused: {reply.Error?.Message}");
        var placed = Assert.Single(conn.Placed);
        log.WriteLine($"tif={tif,-18} -> connector saw {placed.Tif}");
        Assert.Equal(Enum.Parse<TradeAgent.ConnectorSdk.TimeInForce>(tif, ignoreCase: true), placed.Tif);
    }

    /// <summary>An absent <c>tif</c> keeps the default the contract states: Day.</summary>
    [Fact]
    public async Task An_absent_tif_is_the_documented_default_of_day()
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(Buy()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(reply.Ok, reply.Error?.Message);
        var placed = Assert.Single(conn.Placed);
        log.WriteLine($"tif omitted -> connector saw {placed.Tif}");
        Assert.Equal(TradeAgent.ConnectorSdk.TimeInForce.Day, placed.Tif);
    }

    /// <summary>
    /// <c>side</c> and <c>type</c> are not fields of this protocol — the side is the op and the type
    /// is read off which prices are present. A frame that names one is refused rather than obeyed in
    /// the opposite direction: <c>{"op":"buy","side":"sell"}</c> silently bought.
    /// </summary>
    [Theory]
    [InlineData("side", "sell")]
    [InlineData("type", "Limit")]
    public async Task A_field_this_protocol_does_not_carry_is_refused_rather_than_ignored(string field, string value)
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var before = conn.Calls;
        var reply = await client.SendAsync(Buy(extra: new()
        {
            [field] = JsonSerializer.SerializeToElement(value)
        })).WaitAsync(TimeSpan.FromSeconds(10));

        log.WriteLine($"buy with {field}={value} : ok={reply.Ok} code={reply.Error?.Code} — {reply.Error?.Message}");
        Assert.False(reply.Ok, $"'{field}' was carried on a buy frame and silently ignored");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains(field, reply.Error.Message);
        Assert.Equal(before, conn.Calls);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// The same rule on the one boolean the frame carries. <c>all</c> was <c>Str("all") is "true"</c>,
    /// so <c>"yes"</c> meant "working orders only" — the opposite of what was asked, silently.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("all")]
    public async Task An_all_flag_the_gateway_cannot_name_is_refused(string value)
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Orders, Session = "agent-1",
            Args = new() { ["all"] = JsonSerializer.SerializeToElement(value) }
        }).WaitAsync(TimeSpan.FromSeconds(10));

        log.WriteLine($"orders all={value,-5} : ok={reply.Ok} code={reply.Error?.Code} — {reply.Error?.Message}");
        Assert.False(reply.Ok, $"all='{value}' was read as 'working orders only' without saying so");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains("all", reply.Error.Message);
    }

    /// <summary>Both spellings the CLI and a JSON client actually send still work, both ways.</summary>
    [Fact]
    public async Task The_all_flag_still_takes_the_words_it_documents()
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        foreach (var v in new object[] { "true", "false", true, false, "TRUE" })
        {
            var reply = await client.SendAsync(new IpcRequest
            {
                Op = Ops.Orders, Session = "agent-1",
                Args = new() { ["all"] = JsonSerializer.SerializeToElement(v) }
            }).WaitAsync(TimeSpan.FromSeconds(10));
            log.WriteLine($"orders all={v} ({v.GetType().Name}) : ok={reply.Ok} {reply.Error?.Message}");
            Assert.True(reply.Ok, $"all={v} was refused: {reply.Error?.Message}");
        }

        var omitted = await client.SendAsync(new IpcRequest { Op = Ops.Orders, Session = "agent-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(omitted.Ok, omitted.Error?.Message);
    }

    /// <summary>
    /// The two vocabularies that were already closed stay closed, so the rule is one rule rather
    /// than a fix in one place.
    /// </summary>
    [Theory]
    [InlineData(Ops.MaterialList, "origin", "inbox-ish")]
    [InlineData(Ops.MaterialNote, "kind", "wrote")]
    public async Task The_ledger_vocabularies_are_refused_by_name_too(string op, string field, string value)
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var args = new Dictionary<string, JsonElement> { [field] = JsonSerializer.SerializeToElement(value) };
        if (op == Ops.MaterialNote) args["text"] = JsonSerializer.SerializeToElement("hello");

        var reply = await client.SendAsync(new IpcRequest { Op = op, Session = "agent-1", Args = args })
            .WaitAsync(TimeSpan.FromSeconds(10));

        log.WriteLine($"{op} {field}={value} : ok={reply.Ok} code={reply.Error?.Code} — {reply.Error?.Message}");
        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains(value, reply.Error.Message);
    }

    // ── 3. The frame cap counts bytes ──────────────────────────────────────────────────────────
    // Finding 10, probe P9. `ReadFrame` compared `StringBuilder.Length` — UTF-16 CHARS after
    // decoding — against `MaxFrameBytes`, so a frame of legal multi-byte JSON was accepted at 2.6x
    // the stated cap and buffered whole in the server. `ReadFrame` runs BEFORE the hello check, so
    // the peer that reaches it need not have authenticated at all.

    /// <summary>
    /// The frame the review measured, unauthenticated: 2,700,096 bytes against a cap the contract
    /// states as 1 MiB. It is refused and the peer is dropped.
    /// </summary>
    [Fact]
    public async Task A_frame_over_the_cap_in_bytes_is_refused_even_before_the_hello()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var dbh = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var raw = await RawFrames.Connect(pipe);

        // Raw, because the point is the BYTES: Json.Write escapes non-ASCII to \uXXXX, which is what
        // hid this. JSON strings may carry raw UTF-8 and the server decodes with a StreamReader, so
        // each of these was 3 bytes in and 1 char out.
        var padding = new string('中', 900_000);
        var frame = "{\"v\":1,\"id\":\"p9frame\",\"op\":\"material-note\",\"session\":\"agent-1\","
                  + "\"args\":{\"kind\":\"note\",\"text\":\"" + padding + "\"}}";
        var bytes = Encoding.UTF8.GetByteCount(frame);
        log.WriteLine($"frame bytes on the wire  : {bytes:N0}   (stated cap: {1 << 20:N0})");
        log.WriteLine($"frame chars after decode : {frame.Length:N0}");
        Assert.True(bytes > 2 * (1 << 20), $"the frame was only {bytes} bytes");

        var reply = await raw.TrySendLine(frame);
        log.WriteLine($"reply : {reply?[..Math.Min(160, reply?.Length ?? 0)] ?? "<the connection was dropped>"}");
        Assert.Null(reply);                                 // dropped, as the backpressure rules drop a peer

        // And DROPPED, not merely ignored: a perfectly good hello on the same connection is not
        // answered either, because there is no longer a connection to answer it.
        var after = await raw.TrySendLine($$"""{"v":1,"id":"after","op":"hello","token":"{{IpcToken.Peek()}}"}""");
        log.WriteLine($"a valid hello afterwards : {after ?? "<no connection>"}");
        Assert.Null(after);
    }

    /// <summary>
    /// The boundary from the other side: a frame of the same shape that fits IN BYTES is still
    /// served. A cap that counted bytes by refusing everything would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_multi_byte_frame_that_fits_the_cap_in_bytes_is_still_answered()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var dbh = db;
        var pipe = NewPipe();
        var token = IpcToken.Ensure();
        await using var server = new GatewayPipeServer(gw, token, pipe);
        server.Start();

        await using var raw = await RawFrames.Connect(pipe);
        Assert.Contains("\"ok\":true", await raw.SendLine($$"""{"v":1,"id":"h","op":"hello","token":"{{token}}"}"""));

        // Three bytes per character, sized so the WHOLE frame lands just under 1 MiB.
        var padding = new string('中', 340_000);
        var frame = "{\"v\":1,\"id\":\"fits\",\"op\":\"material-note\",\"session\":\"agent-1\","
                  + "\"args\":{\"kind\":\"note\",\"text\":\"" + padding + "\"}}";
        var bytes = Encoding.UTF8.GetByteCount(frame);
        log.WriteLine($"frame bytes : {bytes:N0} of {1 << 20:N0}   chars : {frame.Length:N0}");
        Assert.InRange(bytes, 1_000_000, 1 << 20);

        var reply = await raw.TrySendLine(frame);
        log.WriteLine($"reply : {reply?[..Math.Min(120, reply?.Length ?? 0)] ?? "<dropped>"}");
        Assert.NotNull(reply);
        Assert.Contains("\"ok\":true", reply);
    }

    /// <summary>
    /// The cap is counted on the way IN, not after the fact: an ASCII frame past it is refused too,
    /// so the rule is one rule and not a special case for wide characters.
    /// </summary>
    [Fact]
    public async Task An_ascii_frame_over_the_cap_is_refused_the_same_way()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var dbh = db;
        var pipe = NewPipe();
        var token = IpcToken.Ensure();
        await using var server = new GatewayPipeServer(gw, token, pipe);
        server.Start();

        await using var raw = await RawFrames.Connect(pipe);
        Assert.Contains("\"ok\":true", await raw.SendLine($$"""{"v":1,"id":"h","op":"hello","token":"{{token}}"}"""));

        var frame = "{\"v\":1,\"id\":\"big\",\"op\":\"material-note\",\"session\":\"agent-1\","
                  + "\"args\":{\"kind\":\"note\",\"text\":\"" + new string('x', 1 << 20) + "\"}}";
        log.WriteLine($"frame bytes : {Encoding.UTF8.GetByteCount(frame):N0}");

        Assert.Null(await raw.TrySendLine(frame));
        Assert.Null(await raw.TrySendLine($$"""{"v":1,"id":"after","op":"hello","token":"{{token}}"}"""));
    }

    // ── 4. Status answers for the caller ───────────────────────────────────────────────────────
    // Finding 7, probe P7. `StatusAsync` asks `TryAuthorizeExecution(AgentContext.Operator, …)`, and
    // the kill-switch arm is `AiTradingStopped && !ctx.IsOperator` — so it is skipped, and the agent
    // reads `execution_available: true` with no reason while its own buy is refused
    // AI_TRADING_STOPPED. `schema` embeds the same status.

    /// <summary>
    /// The kill switch is down. What the agent is TOLD and what the agent is ALLOWED must be the
    /// same answer, in `status` and in the `schema` that embeds it.
    /// </summary>
    [Theory]
    [InlineData(Ops.Status)]
    [InlineData(Ops.Schema)]
    public async Task The_status_an_agent_reads_is_computed_with_the_agents_own_authority(string op)
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        gw.StopAiTrading("the owner pressed Stop AI trading");

        var answer = await client.SendAsync(new IpcRequest { Op = op, Session = "agent-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        var s = Status(answer, op);

        var buy = await client.SendAsync(Buy()).WaitAsync(TimeSpan.FromSeconds(10));

        log.WriteLine($"{op}.ai_trading_stopped        : {s.GetProperty("ai_trading_stopped")}");
        log.WriteLine($"{op}.execution_available       : {s.GetProperty("execution_available")}");
        log.WriteLine($"{op}.execution_blocked_reason  : " +
                      $"{(s.TryGetProperty("execution_blocked_reason", out var r) ? r.ToString() : "<absent>")}");
        log.WriteLine($"the same session's buy      : ok={buy.Ok} code={buy.Error?.Code}");

        Assert.False(s.GetProperty("execution_available").GetBoolean(),
            "the agent was told execution is available while every order from that session is refused");
        Assert.True(s.TryGetProperty("execution_blocked_reason", out var reason),
            "execution is not available and no reason was given");
        Assert.False(string.IsNullOrWhiteSpace(reason.GetString()));

        Assert.False(buy.Ok);
        Assert.Equal(nameof(ErrorCode.AI_TRADING_STOPPED), buy.Error!.Code);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// The other direction, and the one a fix that simply reported `false` would fail: with the kill
    /// switch up the agent is told execution IS available, with no reason, and its buy goes through.
    /// </summary>
    [Theory]
    [InlineData(Ops.Status)]
    [InlineData(Ops.Schema)]
    public async Task With_nothing_blocking_it_the_agent_is_told_execution_is_available(string op)
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var answer = await client.SendAsync(new IpcRequest { Op = op, Session = "agent-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        var s = Status(answer, op);

        log.WriteLine($"{op}.execution_available : {s.GetProperty("execution_available")}  " +
                      $"reason present: {s.TryGetProperty("execution_blocked_reason", out _)}");
        Assert.True(s.GetProperty("execution_available").GetBoolean(), "an unblocked agent was told it may not trade");
        Assert.False(s.TryGetProperty("execution_blocked_reason", out _), "a reason was given for a block that is not there");

        var buy = await client.SendAsync(Buy()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(buy.Ok, buy.Error?.Message);
        Assert.Single(conn.Broker.Orders);
    }

    /// <summary>
    /// The Dashboard keeps the OPERATOR's answer, which is a different answer to the same question
    /// and the reason this could not be fixed by changing what the kill switch means. In process,
    /// with the switch down, the operator may still act.
    /// </summary>
    [Fact]
    public async Task The_operators_own_status_still_reports_the_operators_authority()
    {
        var (gw, _, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        gw.StopAiTrading("the owner pressed Stop AI trading");

        var inProcess = await gw.StatusAsync();
        var overThePipe = Status(await client.SendAsync(new IpcRequest { Op = Ops.Status, Session = "agent-1" })
            .WaitAsync(TimeSpan.FromSeconds(10)), Ops.Status);

        log.WriteLine($"operator (in process) : available={inProcess.ExecutionAvailable} reason={inProcess.ExecutionBlockedReason ?? "<none>"}");
        log.WriteLine($"agent (over the pipe) : available={overThePipe.GetProperty("execution_available")}");

        Assert.True(inProcess.ExecutionAvailable, "the kill switch stopped the OWNER trading");
        Assert.Null(inProcess.ExecutionBlockedReason);
        Assert.False(overThePipe.GetProperty("execution_available").GetBoolean());
    }

    /// <summary>The status object out of a `status` reply, or out of the `schema` that embeds it.</summary>
    static JsonElement Status(IpcResponse reply, string op)
    {
        var data = JsonSerializer.SerializeToElement(reply.Data);
        return op == Ops.Schema ? data.GetProperty("current") : data;
    }

    // ── 5. An agent reads only its own records ─────────────────────────────────────────────────
    // UNVERIFIED 6: "`Ops.Order` discloses operator press records to the agent. `FindOrder` →
    // `gateway.GetRequest(id)` has no prefix restriction, so `trade order op-close-<nonce>-ES`
    // returns the operator's own press record." The pipe is meant to carry no operator authority;
    // the operator's RECORD of using it was on the channel anyway, with the session that wrote it,
    // its parameters, its state and its error text.

    /// <summary>
    /// The operator presses Close All; the agent asks for the press record by its id. It is answered
    /// exactly as an id that was never minted is answered — the SAME reply, so nothing about the
    /// press can be inferred from the difference either.
    /// </summary>
    [Fact]
    public async Task An_operator_press_record_is_not_readable_over_the_agent_channel()
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        // A position for the press to close, then the press itself.
        Assert.True((await client.SendAsync(Buy()).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        var press = await gw.OperatorCloseAllAsync();
        var pressIds = gw.Unreconciled().Select(r => r.RequestId)
            .Where(r => r.StartsWith(TradingGateway.ClosePress, StringComparison.Ordinal)).ToList();
        log.WriteLine($"press rows : {string.Join(", ", pressIds)}   ({press.Summary})");
        Assert.NotEmpty(pressIds);

        var leg = pressIds.First(r => r.EndsWith("-ES", StringComparison.Ordinal));
        var asked = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Order, Session = "agent-1", Args = new() { ["id"] = JsonSerializer.SerializeToElement(leg) }
        }).WaitAsync(TimeSpan.FromSeconds(10));

        log.WriteLine($"trade order {leg} -> ok={asked.Ok} data={Json.Write(asked.Data)}");

        // The reply for an id nobody ever minted, for comparison.
        var absent = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Order, Session = "agent-1",
            Args = new() { ["id"] = JsonSerializer.SerializeToElement("op-close-deadbeefdeadbeef-ES") }
        }).WaitAsync(TimeSpan.FromSeconds(10));
        log.WriteLine($"an id nobody minted   -> ok={absent.Ok} data={Json.Write(absent.Data)}");

        Assert.Equal(Json.Write(absent.Data), Json.Write(asked.Data));
        Assert.DoesNotContain(AgentContext.OperatorSessionId, Json.Write(asked.Data) ?? "");
    }

    /// <summary>
    /// Both directions, and this is the half that a fix by blanket prefix would break: the agent's
    /// OWN records still resolve — the buy it placed, and the sweep leg the gateway minted for it,
    /// whose id the agent was handed in the sweep's own reply and which also begins <c>op-</c>.
    /// </summary>
    [Fact]
    public async Task An_agents_own_records_still_resolve_including_the_legs_of_its_own_sweep()
    {
        var (gw, conn, db, server, client) = await Counted(s => s.Risk.MaxOrderQuantity = 5m);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        conn.Faults.Fill = Connectors.Fake.FillBehaviour.LeaveWorking;
        var buy = Buy(extra: new() { ["limit"] = JsonSerializer.SerializeToElement("1") });
        Assert.True((await client.SendAsync(buy).WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        var sweep = await client.SendAsync(new IpcRequest
        {
            Op = Ops.CancelAll, Session = "agent-1", RequestId = "pipec-" + Guid.NewGuid().ToString("n")[..8]
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(sweep.Ok, sweep.Error?.Message);

        var legId = JsonSerializer.SerializeToElement(sweep.Data)
            .GetProperty("outcomes")[0].GetProperty("request_id").GetString()!;
        log.WriteLine($"the agent's own sweep leg : {legId}");
        Assert.StartsWith("op-", legId);

        foreach (var id in new[] { buy.RequestId!, legId })
        {
            var reply = await client.SendAsync(new IpcRequest
            {
                Op = Ops.Order, Session = "agent-1", Args = new() { ["id"] = JsonSerializer.SerializeToElement(id) }
            }).WaitAsync(TimeSpan.FromSeconds(10));
            var data = JsonSerializer.SerializeToElement(reply.Data);
            log.WriteLine($"trade order {id} -> ok={reply.Ok} request_id={(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("request_id", out var q) ? q.GetString() : "<none>")}");
            Assert.True(reply.Ok, reply.Error?.Message);
            Assert.Equal(id, data.GetProperty("request_id").GetString());
        }
    }

    /// <summary>
    /// A different agent session cannot read a gateway-minted leg belonging to another one, so the
    /// rule is about whose record it is and not only about the operator.
    /// </summary>
    [Fact]
    public async Task Another_sessions_minted_leg_does_not_resolve()
    {
        var (gw, conn, db, server, client) = await Counted(s => s.Risk.MaxOrderQuantity = 5m);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        conn.Faults.Fill = Connectors.Fake.FillBehaviour.LeaveWorking;
        Assert.True((await client.SendAsync(Buy(extra: new() { ["limit"] = JsonSerializer.SerializeToElement("1") }))
            .WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        var sweep = await client.SendAsync(new IpcRequest
        {
            Op = Ops.CancelAll, Session = "agent-1", RequestId = "pipec-" + Guid.NewGuid().ToString("n")[..8]
        }).WaitAsync(TimeSpan.FromSeconds(20));
        var legId = JsonSerializer.SerializeToElement(sweep.Data)
            .GetProperty("outcomes")[0].GetProperty("request_id").GetString()!;

        var otherSession = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Order, Session = "agent-2", Args = new() { ["id"] = JsonSerializer.SerializeToElement(legId) }
        }).WaitAsync(TimeSpan.FromSeconds(10));

        log.WriteLine($"agent-2 asking for agent-1's leg {legId} -> {Json.Write(otherSession.Data) ?? "<nothing>"}");
        Assert.Null(otherSession.Data);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A gateway whose connector counts every call — reads included — and keeps every placement whole.
    ///
    /// <see cref="RecordingConnector.Calls"/> rather than the broker's book, because "zero connector
    /// calls" is the assertion these refusals are actually about and an empty book cannot make it: a
    /// frame refused three reads into the risk check places no order either.
    /// </summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, GatewayPipeServer Server, PipeClient Client)>
        Counted(Action<TradeAgentSettings>? settings = null)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new Connectors.Fake.FakeConnector(new Connectors.Fake.FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        return (gw, conn, db, server, client);
    }

    /// <summary>Every <c>protocol_rejected</c> line the pipe server wrote, oldest first.</summary>
    static List<string> ProtocolRefusals(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT metadata FROM engineering_log WHERE component='Ipc' AND event='protocol_rejected' ORDER BY id");
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.IsDBNull(0) ? "{}" : r.GetString(0));
        return rows;
    });

    /// <summary>
    /// Frames on the wire with no handshake of its own — <see cref="PipeClient"/> says hello for you,
    /// which is the frame several of these tests are about.
    /// </summary>
    sealed class RawFrames : IAsyncDisposable
    {
        NamedPipeClientStream _p = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<RawFrames> Connect(string pipe)
        {
            var c = new RawFrames { _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._p.ConnectAsync(10_000);
            c._r = new StreamReader(c._p, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._p, new UTF8Encoding(false), 65536, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> Send(IpcRequest req) => Json.Read<IpcResponse>(await SendLine(Json.Write(req)))!;

        public async Task<string> SendLine(string frame) =>
            await TrySendLine(frame) ?? throw new IOException("the gateway closed the connection without answering");

        /// <summary>Null means the gateway hung up rather than answering — which is a result, not a fault.</summary>
        public async Task<string?> TrySendLine(string frame)
        {
            try
            {
                await _w.WriteLineAsync(frame);
                return await _r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return null;      // the handle went away mid-write: the peer was dropped
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _w.DisposeAsync(); } catch (Exception) { /* the server may have hung up first */ }
            _r.Dispose();
            await _p.DisposeAsync();
        }
    }
}
