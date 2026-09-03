using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// Whether the agent can promote itself to the operator by saying so.
///
/// Operator authority is meant to be in-process only: the kill switch, LIVE_CONFIRM approval, mode,
/// live activation. <see cref="VerticalSliceTests.Operator_authority_is_not_reachable_from_the_agent_channel"/>
/// proves there is no pipe OP for any of it — and that was the whole of the proof. It enumerates op
/// NAMES. It never sends a value.
///
/// But authority was not carried by the op, it was carried by a string: <c>IsOperator</c> was
/// <c>SessionId == "operator"</c>, and <c>SessionId</c> came off the wire — the pipe server built the
/// context straight from <c>req.Session</c>, and `trade` copies <c>TRADEAGENT_SESSION</c> into that
/// field verbatim. So `TRADEAGENT_SESSION=operator trade buy ...` asked for operator authority in
/// the one place nobody was checking, and got it: past the kill switch, past the approval gate.
///
/// These tests go over the real pipe, because that is the boundary the claim is about. A unit test
/// on the gateway would have proved nothing: the gateway was behaving correctly, it was being handed
/// a context it had no way to doubt.
/// </summary>
public class OperatorContextTests
{
    static string NewPipe() => "ta-opctx-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>The session string that used to mean "I am the human at the keyboard".</summary>
    const string Reserved = "operator";

    /// <summary>The engineering event recorded when a frame asks for the reserved session.</summary>
    const string RefusedEvent = "operator_session_refused";

    /// <summary>A live-confirm gateway with live armed, plus a client already past hello.</summary>
    static async Task<(TradingGateway Gw, Connectors.Fake.FakeConnector Conn, Database Db, GatewayPipeServer Server, PipeClient Client)>
        LiveConfirm()
    {
        var (gw, conn, db) = await TestEnv.Ready(s => s.Mode = TradingMode.LIVE_CONFIRM);
        gw.ActivateLive(true);
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        return (gw, conn, db, server, client);
    }

    static IpcRequest Buy(string? session) => new()
    {
        Op = Ops.Buy,
        Session = session,
        RequestId = "opctx-" + Guid.NewGuid().ToString("n")[..8],
        Args = new()
        {
            ["symbol"] = JsonSerializer.SerializeToElement("ES"),
            ["quantity"] = JsonSerializer.SerializeToElement("1")
        }
    };

    /// <summary>
    /// LIVE_CONFIRM exists so a person sees every live order before it goes. A frame that calls
    /// itself the operator must not walk past that, and the measurement that matters is not the
    /// error code — it is whether the broker got an order.
    /// </summary>
    [Fact]
    public async Task A_frame_calling_itself_the_operator_cannot_skip_the_approval_gate()
    {
        var (gw, conn, db, server, client) = await LiveConfirm();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var req = Buy(Reserved);
        var reply = await client.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok,
            $"a frame with session='{Reserved}' placed a LIVE order with nobody approving it: {Json.Write(reply.Data)}");
        Assert.Empty(conn.Broker.Orders);
        Assert.NotEqual(ExecutionState.FILLED, gw.GetRequest(req.RequestId!)?.State ?? ExecutionState.CANCELLED);
    }

    /// <summary>
    /// The kill switch is the one control that has to work when everything else has failed. It was
    /// <c>AiTradingStopped &amp;&amp; !ctx.IsOperator</c> — so a frame that claimed to be the operator
    /// turned it off for itself.
    /// </summary>
    [Fact]
    public async Task A_frame_calling_itself_the_operator_cannot_trade_through_the_kill_switch()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _1 = db;
        gw.StopAiTrading("test: the owner pressed stop");
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(Buy(Reserved)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok,
            $"a frame with session='{Reserved}' traded through the kill switch: {Json.Write(reply.Data)}");
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// The kill switch itself still works, and still says so in the words the CLI prints. This is
    /// the control for the test above: it proves the refusal there is about the reserved session and
    /// not about the gate being broken for everyone.
    /// </summary>
    [Fact]
    public async Task An_ordinary_session_is_still_refused_by_the_kill_switch_with_its_own_error()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _1 = db;
        gw.StopAiTrading("test: the owner pressed stop");
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(Buy("agent")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.AI_TRADING_STOPPED), reply.Error!.Code);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// The reserved word is refused at the door rather than quietly downgraded, and the attempt is
    /// on the engineering record with the op that carried it. An agent probing for an escalation is
    /// worth a trace even when the probe fails — a refusal nobody can see afterwards is not evidence.
    /// </summary>
    [Fact]
    public async Task The_reserved_session_is_refused_by_name_and_the_attempt_is_recorded()
    {
        var (gw, _, db, server, client) = await LiveConfirm();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(Buy(Reserved)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains(Reserved, reply.Error.Message);

        var (found, op) = ReadRefusal(db);
        Assert.True(found, $"no '{RefusedEvent}' engineering event after a frame asked for the reserved session");
        Assert.Equal(Ops.Buy, op);
    }

    /// <summary>
    /// The other direction, and the one that would catch a fix that simply broke placing: an
    /// ordinary agent session in LIVE_CONFIRM still parks for a person, exactly as before.
    /// </summary>
    [Fact]
    public async Task An_ordinary_agent_session_still_parks_for_approval()
    {
        var (gw, conn, db, server, client) = await LiveConfirm();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var req = Buy("agent");
        var reply = await client.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.APPROVAL_REQUIRED), reply.Error!.Code);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest(req.RequestId!)!.State);
        Assert.Empty(conn.Broker.Orders);

        // And a person can still approve it, which is the point of parking rather than refusing.
        Assert.Equal(ExecutionState.FILLED, (await gw.ApproveAsync(req.RequestId!)).State);
        Assert.Single(conn.Broker.Orders);
    }

    /// <summary>
    /// The real operator is unaffected. This is the whole reason the flag exists, and a fix that
    /// simply deleted operator authority would pass every test above and fail this one: in-process
    /// code holding <see cref="AgentContext.Operator"/> still places without approval.
    /// </summary>
    [Fact]
    public async Task The_in_process_operator_still_places_without_approval()
    {
        var (gw, conn, db) = await TestEnv.Ready(s => s.Mode = TradingMode.LIVE_CONFIRM);
        using var _1 = db;
        gw.ActivateLive(true);

        var placed = await gw.PlaceAsync(AgentContext.Operator, "operator-1", TestEnv.Buy());

        Assert.Equal(ExecutionState.FILLED, placed.State);
        Assert.Single(conn.Broker.Orders);
        Assert.True(AgentContext.Operator.IsOperator);
    }

    /// <summary>
    /// The class gate, asserted on the property rather than on the source text.
    ///
    /// The stronger gate would be a private constructor, so the compiler refuses the escalation at
    /// every call site. That is not what is here, and the reason is blast radius: <c>AgentContext</c>
    /// is constructed by name in about fifty-five places across two test files this unit does not
    /// own. So the type is the gate instead — no string reaches the operator flag by any public
    /// route — and this test is what holds that true. A source grep would have been weaker again: it
    /// would fail on a comment and pass on a reflective back door.
    /// </summary>
    [Fact]
    public void No_string_can_produce_an_operator_context()
    {
        foreach (var s in new[] { "operator", "Operator", "OPERATOR", "operator ", " operator", "\toperator" })
        {
            Assert.False(new AgentContext(s).IsOperator, $"new AgentContext(\"{s}\") claimed operator authority");
            Assert.False(AgentContext.ForAgent(s).IsOperator, $"ForAgent(\"{s}\") claimed operator authority");
        }

        // And there is no copy route at all any more: AgentContext is a sealed class, so the
        // record `with` that used to carry IsOperator=true off Operator does not exist to be used.
        Assert.False(typeof(AgentContext).GetMethods().Any(m => m.Name == "<Clone>$"),
            "AgentContext still has a record clone method, so `with` can still copy operator authority");

        // And the one that is meant to be, still is.
        Assert.True(AgentContext.Operator.IsOperator);
        Assert.Equal(Reserved, AgentContext.Operator.SessionId);
    }

    /// <summary>
    /// EVERY SPELLING OF THE RESERVED WORD, OVER THE REAL PIPE.
    ///
    /// The type refuses operator authority whatever the string is, so none of these can escalate.
    /// The tripwire is what is on trial here: it exists so a probe is VISIBLE, and a tripwire that
    /// only catches the exact lowercase spelling catches only an agent that was not trying. Without
    /// the variants, `OrdinalIgnoreCase` could be narrowed to `Ordinal` and the `Trim()` deleted with
    /// nothing failing.
    ///
    /// Asserted on the refusal AND on the log, because a probe that is silently downgraded rather
    /// than refused leaves the operator with no way to know it happened.
    /// </summary>
    [Theory]
    [InlineData("Operator")]
    [InlineData("OPERATOR")]
    [InlineData("oPeRaToR")]
    [InlineData(" operator")]
    [InlineData("operator ")]
    [InlineData("\toperator")]
    [InlineData("  OPERATOR  ")]
    public async Task Every_spelling_of_the_reserved_session_is_refused_and_recorded(string spelling)
    {
        var (gw, conn, db, server, client) = await LiveConfirm();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(Buy(spelling)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok, $"session='{spelling}' was accepted: {Json.Write(reply.Data)}");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Empty(conn.Broker.Orders);

        var (found, op) = ReadRefusal(db);
        Assert.True(found, $"session='{spelling}' was refused but nothing was recorded, so the probe is invisible");
        Assert.Equal(Ops.Buy, op);
    }

    /// <summary>
    /// THE SAME TRIPWIRE ON THE HELLO FRAME, WHICH IS THE ONE FRAME KIND IT WAS MISSING FROM.
    ///
    /// Codex F10 on d25dbb4: the reserved-session check lived only in <c>Handle</c>, and a hello
    /// frame never reaches <c>Handle</c> — the read loop answers it and continues. So a valid-token
    /// hello carrying " operator " was answered with SUCCESS. Nothing escalated, because no
    /// <c>AgentContext</c> is built on that path; what failed is the contract, which says client use
    /// of the reserved string is refused with INVALID_REQUEST and recorded. On the FIRST frame of
    /// every connection it was neither, and a tripwire with a hole in the first frame is a tripwire
    /// an agent walks through on its way in.
    ///
    /// The reserved hello is the first frame here rather than a second one after a clean handshake,
    /// because that is the shape an agent probing for the name would actually send.
    /// </summary>
    [Theory]
    [InlineData("operator")]
    [InlineData("Operator")]
    [InlineData("OPERATOR")]
    [InlineData("oPeRaToR")]
    [InlineData(" operator")]
    [InlineData("operator ")]
    [InlineData("\toperator")]
    [InlineData("  OPERATOR  ")]
    public async Task Every_spelling_of_the_reserved_session_is_refused_on_the_hello_frame_too(string spelling)
    {
        var (gw, conn, db) = await TestEnv.Ready(s => s.Mode = TradingMode.LIVE_CONFIRM);
        using var _1 = db;
        gw.ActivateLive(true);
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var raw = await RawFrames.Connect(pipe);
        var hello = await raw.Send(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Peek(), Session = spelling });

        Assert.False(hello.Ok, $"a hello with session='{spelling}' was accepted: {Json.Write(hello.Data)}");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), hello.Error!.Code);

        // And the channel never became usable under that name: the refusal does not authenticate.
        var buy = await raw.Send(Buy(spelling));
        Assert.False(buy.Ok, "the connection was authenticated by a hello that was refused");
        Assert.Equal(nameof(ErrorCode.IPC_UNAUTHENTICATED), buy.Error!.Code);
        Assert.Empty(conn.Broker.Orders);

        var (found, op) = ReadRefusal(db);
        Assert.True(found, $"hello session='{spelling}' was refused but nothing was recorded, so the probe is invisible");
        Assert.Equal(Ops.Hello, op);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Frames on the wire with no handshake of its own — <see cref="PipeClient"/> says hello for you,
    /// which is exactly the frame under test here.
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
            c._w = new StreamWriter(c._p, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> Send(IpcRequest req)
        {
            await _w.WriteLineAsync(Json.Write(req));
            var line = await _r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                       ?? throw new IOException("the gateway closed the connection without answering");
            return Json.Read<IpcResponse>(line)!;
        }

        public async ValueTask DisposeAsync()
        {
            await _w.DisposeAsync();
            _r.Dispose();
            await _p.DisposeAsync();
        }
    }


    static (bool Found, string Op) ReadRefusal(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT metadata FROM engineering_log WHERE component='Ipc' AND event=$e ORDER BY id LIMIT 1",
            ("$e", RefusedEvent));
        using var r = c.ExecuteReader();
        if (!r.Read()) return (false, "");
        using var doc = JsonDocument.Parse(r.IsDBNull(0) ? "{}" : r.GetString(0));
        return (true, doc.RootElement.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "");
    });
}
