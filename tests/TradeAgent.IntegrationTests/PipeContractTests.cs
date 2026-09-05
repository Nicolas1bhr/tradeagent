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
        foreach (var name in Enum.GetNames<ConnectorSdk.TimeInForce>())
            Assert.Contains(name, reply.Error.Message);

        Assert.Equal(before, conn.Calls);
        Assert.Empty(conn.Inner.Broker.Orders);
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
        Assert.Equal(Enum.Parse<ConnectorSdk.TimeInForce>(tif, ignoreCase: true), placed.Tif);
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
        Assert.Equal(ConnectorSdk.TimeInForce.Day, placed.Tif);
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
        Assert.Empty(conn.Inner.Broker.Orders);
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

    // ---------------------------------------------------------------- helpers

    /// <summary>A gateway whose connector counts every call and keeps every placement it was given.</summary>
    static async Task<(TradingGateway Gw, CountingConnector Conn, Database Db, GatewayPipeServer Server, PipeClient Client)>
        Counted(Action<TradeAgentSettings>? settings = null)
    {
        var db = TestEnv.NewDb();
        var conn = new CountingConnector(new Connectors.Fake.FakeConnector(new Connectors.Fake.FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Inner.Broker.AccountId;
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

    /// <summary>
    /// Every call to the trading backend, counted, and every placement kept whole.
    ///
    /// "Zero connector calls" is the assertion the refusals above are actually about, and
    /// <c>Broker.Orders</c> cannot make it: a frame refused three reads into the risk check places
    /// no order either. The <see cref="ConnectorSdk.PlaceOrderCommand"/> list is here for the same
    /// reason — <see cref="ConnectorSdk.OrderInfo"/> does not carry the time-in-force, so the only
    /// place the value the agent asked for can be READ BACK is the command itself.
    /// </summary>
    sealed class CountingConnector(Connectors.Fake.FakeConnector inner) : ConnectorSdk.ITradingConnector
    {
        public Connectors.Fake.FakeConnector Inner { get; } = inner;
        int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<ConnectorSdk.PlaceOrderCommand> Placed { get; } = [];

        T Count<T>(T value) { Interlocked.Increment(ref _calls); return value; }

        public string Id => Inner.Id;
        public string DisplayName => Inner.DisplayName;
        public ConnectorSdk.ConnectorCapabilities Capabilities => Inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => Inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => Inner.EmergencyBudget;

        public Task ConnectAsync(CancellationToken ct = default) => Inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => Count(Inner.GetHealthAsync(ct));
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Count(Inner.IsConnectedAsync(ct));
        public Task<IReadOnlyList<ConnectorSdk.AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => Count(Inner.GetAccountsAsync(ct));
        public Task<ConnectorSdk.AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => Count(Inner.GetAccountAsync(a, ct));
        public Task<IReadOnlyList<ConnectorSdk.InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => Count(Inner.GetInstrumentsAsync(ct));
        public Task<ConnectorSdk.QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => Count(Inner.GetQuoteAsync(s, ct));
        public Task<IReadOnlyList<ConnectorSdk.PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => Count(Inner.GetPositionsAsync(a, ct));
        public Task<IReadOnlyList<ConnectorSdk.OrderInfo>> GetOrdersAsync(string a, bool inactive, DateTimeOffset? since, CancellationToken ct = default) => Count(Inner.GetOrdersAsync(a, inactive, since, ct));
        public Task<IReadOnlyList<ConnectorSdk.ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) => Count(Inner.GetExecutionsAsync(a, since, ct));

        public Task<ConnectorSdk.OrderInfo> PlaceOrderAsync(ConnectorSdk.PlaceOrderCommand cmd, CancellationToken ct = default)
        {
            lock (Placed) Placed.Add(cmd);
            return Count(Inner.PlaceOrderAsync(cmd, ct));
        }

        public Task<ConnectorSdk.OrderInfo> ModifyOrderAsync(ConnectorSdk.ModifyOrderCommand c, CancellationToken ct = default) => Count(Inner.ModifyOrderAsync(c, ct));
        public Task CancelOrderAsync(string id, CancellationToken ct = default) => Count(Inner.CancelOrderAsync(id, ct));
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => Count(Inner.CancelAllOrdersAsync(a, ct));
        public Task<ConnectorSdk.OrderInfo?> ClosePositionAsync(string a, string s, string coid, CancellationToken ct = default) => Count(Inner.ClosePositionAsync(a, s, coid, ct));

        public event Action<HealthState>? ConnectionChanged
        { add => Inner.ConnectionChanged += value; remove => Inner.ConnectionChanged -= value; }
        public event Action<ConnectorSdk.QuoteInfo>? QuoteChanged
        { add => Inner.QuoteChanged += value; remove => Inner.QuoteChanged -= value; }
        public event Action<ConnectorSdk.OrderInfo>? OrderChanged
        { add => Inner.OrderChanged += value; remove => Inner.OrderChanged -= value; }
        public event Action<ConnectorSdk.ExecutionInfo>? ExecutionReceived
        { add => Inner.ExecutionReceived += value; remove => Inner.ExecutionReceived -= value; }
        public event Action<ConnectorSdk.PositionInfo>? PositionChanged
        { add => Inner.PositionChanged += value; remove => Inner.PositionChanged -= value; }
        public event Action<ConnectorSdk.AccountInfo>? AccountChanged
        { add => Inner.AccountChanged += value; remove => Inner.AccountChanged -= value; }

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
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
