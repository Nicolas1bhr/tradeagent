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

    // ---------------------------------------------------------------- helpers

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

        public async Task<string> SendLine(string frame)
        {
            await _w.WriteLineAsync(frame);
            return await _r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
                   ?? throw new IOException("the gateway closed the connection without answering");
        }

        public async ValueTask DisposeAsync()
        {
            try { await _w.DisposeAsync(); } catch (Exception) { /* the server may have hung up first */ }
            _r.Dispose();
            await _p.DisposeAsync();
        }
    }
}
