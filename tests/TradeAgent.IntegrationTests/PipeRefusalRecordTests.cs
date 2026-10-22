using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// EVERY REFUSAL AT THE PIPE LEAVES ONE STRUCTURED ROW (<c>U-org-watcher-b</c> item 4, seat A's ruling (b)): a refusal marker carried
/// in-process on the refusal — never on the wire — read at <c>Handle</c>'s one choke point, which the read loop, <c>CallAsync</c> and the
/// broker door's <c>CallAsGrantAsync</c> all reach; written as <c>engineering_log</c> <c>Ipc</c>/<c>op_refused</c> with {op, code, role,
/// attempt} and nothing of the request. A malformed frame is not a refusal and leaves no row. No real model is called: every launch here
/// is a row the test wrote, as <c>OrgAssignmentTests</c> does.
/// </summary>
[Collection("containment")]
public class PipeRefusalRecordTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-refrow-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>A launch the ledger recorded for <paramref name="role"/> on an UNCONFINED seat — never a real model.</summary>
    static string Unconfined(Database db, string role)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = SeatClasses.Cli,
            Assurance = SeatClasses.AssuranceOf(SeatClasses.Cli) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Assign(string? grant = null) => new()
    {
        Op = Ops.MandateAssign, Token = IpcToken.Ensure(), Grant = grant, Session = "refusal-row",
        RequestId = "refrow-" + Guid.NewGuid().ToString("n")[..12],
        Args = new()
        {
            ["holder"] = JsonSerializer.SerializeToElement(CouncilRoles.Research),
            ["goal"] = JsonSerializer.SerializeToElement("find out whether the skew predicts the next hour"),
            ["kind"] = JsonSerializer.SerializeToElement(MandateKind.Task),
            ["shape"] = JsonSerializer.SerializeToElement(MandateShape.Report),
            ["due"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.AddDays(7).ToString("O")),
            ["allocation"] = JsonSerializer.SerializeToElement(5m),
        }
    };

    static IpcRequest Buy(string? grant, string symbol = "ES", string session = "refusal-row", string? rid = null) => new()
    {
        Op = Ops.Buy, Token = IpcToken.Ensure(), Grant = grant, Session = session, RequestId = rid ?? "refrow-" + Guid.NewGuid().ToString("n")[..12],
        Args = new() { ["symbol"] = JsonSerializer.SerializeToElement(symbol), ["quantity"] = JsonSerializer.SerializeToElement("1") }
    };

    /// <summary>One <c>Ipc</c> engineering row as written.</summary>
    sealed record Row(string Event, string Severity, string? Session, string? RequestId, string? Metadata, string? Exception)
    {
        public JsonElement Meta => JsonDocument.Parse(Metadata ?? "{}").RootElement;
        public string M(string key) => Meta.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    }

    static List<Row> Rows(Database db, string @event)
    {
        using var c = new SqliteConnection($"Data Source={db.FilePath};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT event, severity, session, request_id, metadata, exception FROM engineering_log WHERE component='Ipc' AND event=$e ORDER BY id";
        q.Parameters.AddWithValue("$e", @event);
        var rows = new List<Row>();
        using var r = q.ExecuteReader();
        while (r.Read())
            rows.Add(new Row(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return rows;
    }

    static string AllLog(Database db)
    {
        using var c = new SqliteConnection($"Data Source={db.FilePath};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT * FROM engineering_log";
        var all = new StringBuilder();
        using var r = q.ExecuteReader();
        while (r.Read())
            for (var i = 0; i < r.FieldCount; i++) all.Append(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('\u001f');
        return all.ToString();
    }

    /// <summary>
    /// (e) EVERY REFUSAL AT THE PIPE LEAVES ONE STRUCTURED ROW, BY EVERY PATH INTO <c>Handle</c>: a Research <c>buy</c> over the READ LOOP
    /// (the role gate); an unconfined Operations launch's <c>mandate-assign</c> over the read loop, in process, and through the broker DOOR
    /// (the organisation verbs' caller check). Each answer is the one it always was; each leaves exactly one <c>op_refused</c> row naming the
    /// op, the code, the role and the attempt. A malformed frame — an unknown op, a quote naming no symbol — is not a refusal and leaves none.
    /// RED at base: the role gate left an activity line only, and an organisation-verb refusal no row at all.
    /// </summary>
    [Fact]
    public async Task Every_refusal_at_the_pipe_leaves_one_structured_row()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        // THE READ LOOP: Research may not trade.
        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-refrow-r");
        await using (var client = await Raw.ConnectAsync(pipe))
        {
            Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = research.Token })).Ok);
            var buy = await client.SendAsync(Buy(research.Token));
            Assert.Equal(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), buy.Error?.Code);
            // A MALFORMED FRAME IS NOT A REFUSAL: an op nobody knows.
            var unknown = await client.SendAsync(new IpcRequest { Op = "no-such-op", Token = IpcToken.Ensure(), Session = "refusal-row" });
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), unknown.Error?.Code);
        }

        // THE READ LOOP: an unconfined Operations launch holds no organisation verb.
        var opsAttempt = Unconfined(db, CouncilRoles.Operations);
        using var operations = grants.IssueHere(CouncilRoles.Operations, opsAttempt);
        await using (var client = await Raw.ConnectAsync(pipe))
        {
            Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = operations.Token })).Ok);
            var assign = await client.SendAsync(Assign(operations.Token));
            Assert.False(assign.Ok);
            Assert.Contains("Heading a unit grants no verb by itself", assign.Error!.Message, StringComparison.Ordinal);
            // AND A QUOTE THAT NAMES NO SYMBOL is malformed — not refused.
            var malformed = await client.SendAsync(new IpcRequest
            {
                Op = Ops.Quote, Token = IpcToken.Ensure(), Session = "refusal-row", RequestId = "refrow-malformed", Args = []
            });
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), malformed.Error?.Code);
        }

        // IN PROCESS, AND THROUGH THE BROKER DOOR.
        Assert.False((await server.CallAsync(Assign(), CouncilRoles.Operations, opsAttempt)).Ok);
        Assert.False((await server.CallAsGrantAsync(Assign(), operations)).Ok);

        var rows = Rows(db, "op_refused");
        foreach (var r in rows) log.WriteLine($"{r.Severity} {r.Metadata}");
        Assert.Equal(4, rows.Count);
        Assert.Equal(("buy", nameof(ErrorCode.ROLE_MAY_NOT_TRADE), CouncilRoles.Research, "attempt-refrow-r"),
            (rows[0].M("op"), rows[0].M("code"), rows[0].M("role"), rows[0].M("attempt")));
        foreach (var r in rows.Skip(1))
            Assert.Equal((Ops.MandateAssign, nameof(ErrorCode.INVALID_REQUEST), CouncilRoles.Operations, opsAttempt),
                (r.M("op"), r.M("code"), r.M("role"), r.M("attempt")));
        Assert.All(rows, r => Assert.Equal("info", r.Severity));
    }

    /// <summary>
    /// (e4) THE OWNER'S OWN AUTHORITY REFUSING AN AGENT'S ORDER LEAVES ONE ROW (fix pass 1, R1): an Operations launch's <c>buy</c> over the
    /// read loop while the owner has stopped AI trading, and again in a live mode he has not switched on, is refused with the code it always
    /// was, and each leaves exactly one <c>op_refused</c> row naming it. RED before the fix: the kill switch's and the live gate's codes were
    /// on no list and unmarked, so neither left a row.
    /// </summary>
    [Fact]
    public async Task The_owners_own_authority_refusing_an_order_leaves_one_row()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        using var operations = grants.IssueHere(CouncilRoles.Operations, "attempt-refrow-owner");
        await using var client = await Raw.ConnectAsync(pipe);
        Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = operations.Token })).Ok);

        gw.StopAiTrading("test: the owner pressed stop");
        Assert.Equal(nameof(ErrorCode.AI_TRADING_STOPPED), (await client.SendAsync(Buy(operations.Token))).Error?.Code);
        gw.EnableAiTrading();
        gw.Update(s => { s.Mode = TradingMode.LIVE_CONFIRM; s.LiveActivated = false; });
        Assert.Equal(nameof(ErrorCode.LIVE_NOT_ACTIVATED), (await client.SendAsync(Buy(operations.Token))).Error?.Code);

        var rows = Rows(db, "op_refused");
        foreach (var r in rows) log.WriteLine($"{r.Severity} {r.Metadata}");
        Assert.Equal([nameof(ErrorCode.AI_TRADING_STOPPED), nameof(ErrorCode.LIVE_NOT_ACTIVATED)], rows.Select(r => r.M("code")));
        Assert.All(rows, r => Assert.Equal((Ops.Buy, CouncilRoles.Operations, "attempt-refrow-owner"), (r.M("op"), r.M("role"), r.M("attempt"))));
    }

    /// <summary>
    /// (e1) GUARD: AN ENDED GRANT KEEPS ITS ONE <c>grant_rejected</c> ROW. A grant whose turn ended under a live connection is refused before
    /// <c>Handle</c> (<c>EndedGrantRefusal</c>), as it was: one <c>grant_rejected</c> row, and no <c>op_refused</c> beside it. Green at base.
    /// </summary>
    [Fact]
    public async Task An_ended_grant_keeps_its_one_grant_rejected_row()
    {
        var pipe = NewPipe();
        var now = DateTimeOffset.UtcNow;
        var grants = new AgentGrants(() => now);
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var chair = grants.IssueHere(CouncilRoles.Operations, "attempt-refrow-ended", TimeSpan.FromMinutes(5));
        await using var client = await Raw.ConnectAsync(pipe);
        Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = chair.Token })).Ok);
        now = now.AddHours(1);
        var after = await client.SendAsync(new IpcRequest { Op = Ops.Positions, Token = IpcToken.Ensure(), Session = "refusal-row", Grant = chair.Token });
        Assert.False(after.Ok);

        Assert.Single(Rows(db, "grant_rejected"));
        Assert.Empty(Rows(db, "op_refused"));
    }

    /// <summary>
    /// (e2) GUARD: THE REFUSAL ROW HOLDS NO REQUEST PAYLOAD. A refused buy whose symbol, session and request id are planted strings, and an
    /// op of a planted name: the row carries the op as a known const and nothing else of the frame — no session, no request id, no argument,
    /// no exception text — and the planted op name is in no row of the engineering log at all.
    /// </summary>
    [Fact]
    public async Task The_refusal_row_holds_no_request_payload()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var planted = "planted" + Guid.NewGuid().ToString("n")[..10];

        Assert.False((await server.CallAsync(Buy(null, symbol: planted + "SYM", session: planted + "-session", rid: planted + "-rid"),
            CouncilRoles.Research, "attempt-refrow-p")).Ok);
        Assert.False((await server.CallAsync(new IpcRequest { Op = planted + "-op", Session = planted + "-session" },
            CouncilRoles.Research, "attempt-refrow-p")).Ok);

        var row = Assert.Single(Rows(db, "op_refused"));
        log.WriteLine(row.Metadata);
        Assert.Equal((null, null, null), (row.Session, row.RequestId, row.Exception));
        Assert.Equal(["attempt", "code", "op", "role"], row.Meta.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(planted, AllLog(db), StringComparison.Ordinal);
        Assert.Equal("unknown", Core.RefusalMark.OpName(planted + "-op"));
        Assert.Equal(Ops.Buy, Core.RefusalMark.OpName(Ops.Buy));
    }

    /// <summary>A client that sends exactly the frame it is given (<c>LaunchGrantTests.Raw</c>).</summary>
    sealed class Raw : IAsyncDisposable
    {
        NamedPipeClientStream _pipe = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<Raw> ConnectAsync(string pipeName)
        {
            var c = new Raw { _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._pipe.ConnectAsync(5000);
            c._r = new StreamReader(c._pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> SendAsync(IpcRequest req)
        {
            await _w.WriteLineAsync(Core.Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            return Core.Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        public ValueTask DisposeAsync()
        {
            _r.Dispose();
            try { _w.Dispose(); }
            catch (IOException) { /* the gateway closed a connection whose grant ended: nothing left to flush to */ }
            return _pipe.DisposeAsync();
        }
    }
}
