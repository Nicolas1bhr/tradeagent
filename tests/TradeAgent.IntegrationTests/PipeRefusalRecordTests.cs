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

    static IpcRequest Assign(string? grant = null, string holder = CouncilRoles.Research, decimal allocation = 5m) => new()
    {
        Op = Ops.MandateAssign, Token = IpcToken.Ensure(), Grant = grant, Session = "refusal-row",
        RequestId = "refrow-" + Guid.NewGuid().ToString("n")[..12],
        Args = new()
        {
            ["holder"] = JsonSerializer.SerializeToElement(holder),
            ["goal"] = JsonSerializer.SerializeToElement("find out whether the skew predicts the next hour"),
            ["kind"] = JsonSerializer.SerializeToElement(MandateKind.Task),
            ["shape"] = JsonSerializer.SerializeToElement(MandateShape.Report),
            ["due"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.AddDays(7).ToString("O")),
            ["allocation"] = JsonSerializer.SerializeToElement(allocation),
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
    /// (the organisation verbs' caller check). Each answer is the one it always was. Each frame from the read loop leaves exactly one
    /// <c>op_refused</c> row naming the op, the code, the role and the attempt; an in-process or door call leaves none, because its caller's
    /// <c>tool_call</c> row is that refusal's one record (fix pass 1, R4 — proved with the real tools in (e7)). A malformed frame — an unknown
    /// op, a quote naming no symbol — is not a refusal and leaves none. RED at base: the role gate left an activity line only, and an
    /// organisation-verb refusal no row at all.
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

        var rows = Rows(db, "op_refused");
        foreach (var r in rows) log.WriteLine($"{r.Severity} {r.Metadata}");
        Assert.Equal(2, rows.Count);

        // IN PROCESS, AND THROUGH THE BROKER DOOR: refused as they always were — their callers record them, so the pipe adds no row.
        Assert.False((await server.CallAsync(Assign(), CouncilRoles.Operations, opsAttempt)).Ok);
        Assert.False((await server.CallAsGrantAsync(Assign(), operations)).Ok);
        Assert.Equal(2, Rows(db, "op_refused").Count);
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
        gw.SetModeUnderTest(TradingMode.LIVE_CONFIRM);
        gw.ActivateLiveUnderTest(false);
        Assert.Equal(nameof(ErrorCode.LIVE_NOT_ACTIVATED), (await client.SendAsync(Buy(operations.Token))).Error?.Code);

        var rows = Rows(db, "op_refused");
        foreach (var r in rows) log.WriteLine($"{r.Severity} {r.Metadata}");
        Assert.Equal([nameof(ErrorCode.AI_TRADING_STOPPED), nameof(ErrorCode.LIVE_NOT_ACTIVATED)], rows.Select(r => r.M("code")));
        Assert.All(rows, r => Assert.Equal((Ops.Buy, CouncilRoles.Operations, "attempt-refrow-owner"), (r.M("op"), r.M("role"), r.M("attempt"))));
    }

    // ---- a chart, as only a test may write it (OrgAssignmentTests' shape) ------------------------------------------------

    const string Head = "pos-r3c0ffee0001", Member = "pos-r3c0ffee0002", Other = "pos-r3c0ffee0003", Team = "team-r3c0ffee";

    /// <summary>A position in a second team, outside the subtree <see cref="Head"/> heads.</summary>
    const string Outsider = "pos-r3c0ffee0004", Elsewhere = "team-r3c0ffe2";

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    /// <summary><see cref="Head"/> heads <see cref="Team"/> under Research, with <see cref="Member"/> and <see cref="Other"/> in it.</summary>
    static void Chart(Database db)
    {
        const string at = "2026-10-11T12:00:00.0000000Z";
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", Team), ("$p", OrgIds.Research), ("$at", at));
        foreach (var id in new[] { Head, Member, Other })
            Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
                ("$id", id), ("$u", Team), ("$h", $"org/{id}"), ("$at", at));
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", Head), ("$t", Team));
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", Elsewhere), ("$p", OrgIds.Research), ("$at", at));
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
            ("$id", Outsider), ("$u", Elsewhere), ("$h", $"org/{Outsider}"), ("$at", at));
    }

    /// <summary>A launch the ledger recorded for <paramref name="position"/> on the app's own HARNESS seat — eligible for the verbs; never a real model.</summary>
    static string Eligible(Database db, string position)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = position, ReservedCost = 0.10m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.AssuranceOf(SeatClasses.Harness) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Decline(string mandate, string reason, string grant) => new()
    {
        Op = Ops.MandateDecline, Token = IpcToken.Ensure(), Grant = grant, Session = "refusal-row", RequestId = "refrow-" + Guid.NewGuid().ToString("n")[..12],
        Args = new()
        {
            ["mandate"] = JsonSerializer.SerializeToElement(mandate), ["reason"] = JsonSerializer.SerializeToElement(reason),
            ["why"] = JsonSerializer.SerializeToElement("'the next hour' could mean the funding hour or the clock hour"),
        }
    };

    /// <summary>
    /// (e6) MALFORMED CONTENT IS NOT A REFUSAL OF RIGHTS (fix pass 1, R3): over the read loop, the mandate's own holder on an eligible seat
    /// declines it with a reason that is no reason — refused as it always was, and it leaves NO <c>op_refused</c> row and no
    /// <c>refusals</c> finding; a position that does not hold the mandate declining it with a real reason is a refusal of rights and leaves
    /// exactly one row, and the sweep one finding. RED before the fix: every <c>OrgStore</c> refusal was marked, so the bad reason left a
    /// row and a finding too — and the BLIND sentence "a malformed request is not a refusal" was false.
    /// </summary>
    [Fact]
    public async Task Malformed_content_is_not_a_refusal_and_a_refusal_of_rights_still_is()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        Chart(db);
        var mandate = gw.Org.Assign(new MandateIssue(Head, null, Member, MandateKind.Task, "find out whether the skew predicts the next hour",
            null, MandateShape.Report, false, DateTimeOffset.UtcNow.AddDays(7), 5m, MandateLimits.DefaultRetryLimit), DateTimeOffset.UtcNow).Id;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var holderAttempt = Eligible(db, Member);
        using (var holder = grants.IssueHere(Member, holderAttempt))
        await using (var client = await Raw.ConnectAsync(pipe))
        {
            Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = holder.Token })).Ok);
            var bogus = await client.SendAsync(Decline(mandate, "bogus", holder.Token));
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), bogus.Error?.Code);
            Assert.Contains("is no reason to decline", bogus.Error!.Message, StringComparison.Ordinal);
        }
        Assert.Empty(Rows(db, "op_refused"));

        var otherAttempt = Eligible(db, Other);
        using (var other = grants.IssueHere(Other, otherAttempt))
        await using (var client = await Raw.ConnectAsync(pipe))
        {
            Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = other.Token })).Ok);
            var notHers = await client.SendAsync(Decline(mandate, MandateDecline.Ambiguous, other.Token));
            Assert.Contains("only its holder delivers, declines or proposes on it", notHers.Error!.Message, StringComparison.Ordinal);
        }

        var row = Assert.Single(Rows(db, "op_refused"));
        log.WriteLine(row.Metadata);
        Assert.Equal((Ops.MandateDecline, Other, otherAttempt), (row.M("op"), row.M("role"), row.M("attempt")));

        var now = DateTimeOffset.UtcNow;
        using var oversight = new Core.Oversight.OversightStore(Path.Combine(TestEnv.Home, $"oversight-{Guid.NewGuid():n}.db"), now);
        var swept = new Core.Oversight.OversightSweep(db.FilePath, oversight).Run(now);
        Assert.Null(swept.Failure);
        var found = Assert.Single(swept.Added, f => f.Detector == "refusals" && f.ScopeId == Core.Oversight.OversightStore.RootScope);
        Assert.Equal(Ops.MandateDecline, found.Measured["op"]);
    }

    /// <summary>
    /// (e8) AN ASSIGNMENT REFUSED ON THE DECIDER'S AUTHORITY LEAVES ONE ROW; ONE REFUSED ON ITS CONTENT NONE (fix pass 2): over the read
    /// loop, an eligible head's <c>mandate-assign</c> to a holder outside the subtree it heads, to <c>operations</c> (RULING Q4), and to its
    /// own position (fix pass 3, seat A's ruling: a rule on whom the decider may assign to) each leaves exactly one <c>op_refused</c> row;
    /// the same head's assignment into its own team with an allocation of zero is refused for what it says and leaves none. RED before
    /// the fix: every <c>AssignRefusal</c> was thrown unmarked, so the two left no row; before fix pass 3 the self-assignment left none.
    /// </summary>
    [Fact]
    public async Task An_assignment_refused_on_authority_leaves_one_row_and_one_refused_on_content_none()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        Chart(db);
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var headAttempt = Eligible(db, Head);
        using var head = grants.IssueHere(Head, headAttempt);
        await using (var client = await Raw.ConnectAsync(pipe))
        {
            Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = head.Token })).Ok);
            var content = await client.SendAsync(Assign(head.Token, holder: Member, allocation: 0m));
            Assert.Contains("the AI allocation must be above zero", content.Error!.Message, StringComparison.Ordinal);
            Assert.Empty(Rows(db, "op_refused"));

            var outside = await client.SendAsync(Assign(head.Token, holder: Outsider));
            Assert.Contains("is not in the subtree you head", outside.Error!.Message, StringComparison.Ordinal);
            var operations = await client.SendAsync(Assign(head.Token, holder: CouncilRoles.Operations));
            Assert.Contains("holds no mandate", operations.Error!.Message, StringComparison.Ordinal);
            Assert.All([outside, operations], r => Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), r.Error!.Code));
            Assert.Equal(2, Rows(db, "op_refused").Count);

            var self = await client.SendAsync(Assign(head.Token, holder: Head));
            Assert.Contains("never to yourself", self.Error!.Message, StringComparison.Ordinal);
        }

        var rows = Rows(db, "op_refused");
        foreach (var r in rows) log.WriteLine(r.Metadata);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal((Ops.MandateAssign, Head, headAttempt), (r.M("op"), r.M("role"), r.M("attempt"))));
    }

    static IpcRequest OrgFrame(string op, string grant, params (string Key, object Value)[] args) => new()
    {
        Op = op, Token = IpcToken.Ensure(), Grant = grant, Session = "refusal-row", RequestId = "refrow-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static IpcRequest Rescope(string mandate, string grant, string why = "two clean failures: the question was too wide") =>
        OrgFrame(Ops.MandateRescope, grant, ("mandate", mandate), ("why", why), ("goal", "summarise in ten lines whether the skew leads"),
            ("kind", MandateKind.Task), ("shape", MandateShape.Report), ("due", DateTimeOffset.UtcNow.AddDays(7).ToString("O")), ("allocation", 3m));

    static IpcRequest Propose(string mandate, string grant, string refId) =>
        OrgFrame(Ops.MandatePropose, grant, ("mandate", mandate), ("claim", "venue B leads venue A by a minute on the skew"),
            ("refs", new[] { new { kind = "mandate", id = refId } }));

    /// <summary>
    /// (e9) THE NEW ORGANISATION VERBS' REFUSALS OF RIGHTS LEAVE ONE ROW EACH; THEIR REFUSALS OF CONTENT NONE (<c>U-org-verbs-a</c>, rebased
    /// onto <c>U-org-watcher-b</c>): over the read loop, a <c>mandate-rescope</c> by a position that does not decide the mandate, a
    /// <c>mandate-propose</c> by one that does not hold it, a second proposal while one is open (one per mandate, as one escalation per
    /// mandate is), and an <c>org-show</c> from a launch recorded unconfined each leave exactly one <c>op_refused</c> row naming its op; the
    /// decider's re-scope with no reason and the holder's proposal citing a record that is not there are refused for what they say and
    /// leave none.
    /// </summary>
    [Fact]
    public async Task The_new_org_verbs_refused_on_rights_leave_one_row_and_refused_on_content_none()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        Chart(db);
        var mandate = gw.Org.Assign(new MandateIssue(Head, null, Member, MandateKind.Task, "find out whether the skew predicts the next hour",
            null, MandateShape.Report, false, DateTimeOffset.UtcNow.AddDays(7), 5m, MandateLimits.DefaultRetryLimit), DateTimeOffset.UtcNow).Id;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        async Task<IpcResponse> As(string position, string attempt, Func<string, IpcRequest> frame)
        {
            using var grant = grants.IssueHere(position, attempt);
            await using var client = await Raw.ConnectAsync(pipe);
            Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant.Token })).Ok);
            return await client.SendAsync(frame(grant.Token));
        }

        // CONTENT: refused as always, and no row.
        var noWhy = await As(Head, Eligible(db, Head), g => Rescope(mandate, g, why: " "));
        Assert.Contains("a re-scope says why", noWhy.Error!.Message, StringComparison.Ordinal);
        var notThere = await As(Member, Eligible(db, Member), g => Propose(mandate, g, "man-000000000000"));
        Assert.False(notThere.Ok);
        Assert.Empty(Rows(db, "op_refused"));

        // RIGHTS: one row each, naming its op.
        var memberAttempt = Eligible(db, Member);
        var notDecider = await As(Member, memberAttempt, g => Rescope(mandate, g));
        Assert.Contains("only its decider may judge, cancel, re-scope or reassign it", notDecider.Error!.Message, StringComparison.Ordinal);
        var otherAttempt = Eligible(db, Other);
        var notHolder = await As(Other, otherAttempt, g => Propose(mandate, g, mandate));
        Assert.Contains("only its holder delivers, declines or proposes on it", notHolder.Error!.Message, StringComparison.Ordinal);
        Assert.True((await As(Member, Eligible(db, Member), g => Propose(mandate, g, mandate))).Ok);
        var secondAttempt = Eligible(db, Member);
        var second = await As(Member, secondAttempt, g => Propose(mandate, g, mandate));
        Assert.Contains("already has an open proposal", second.Error!.Message, StringComparison.Ordinal);
        var unconfinedAttempt = Unconfined(db, Member);
        var unconfined = await As(Member, unconfinedAttempt, g => OrgFrame(Ops.OrgShow, g));
        Assert.Contains("Heading a unit grants no verb by itself", unconfined.Error!.Message, StringComparison.Ordinal);
        Assert.All([notDecider, notHolder, second, unconfined], r => Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), r.Error!.Code));

        var rows = Rows(db, "op_refused");
        foreach (var r in rows) log.WriteLine(r.Metadata);
        Assert.Equal(
            [(Ops.MandateRescope, Member, memberAttempt), (Ops.MandatePropose, Other, otherAttempt), (Ops.MandatePropose, Member, secondAttempt),
             (Ops.OrgShow, Member, unconfinedAttempt)],
            rows.Select(r => (r.M("op"), r.M("role"), r.M("attempt"))));
    }

    /// <summary>
    /// (e7) ONE REFUSAL IS ONE ROW AND ONE FINDING ON THE IN-PROCESS PATHS (fix pass 1, R4): a Research worker's <c>buy</c> through the
    /// harness's tools (<see cref="GatewayPipeServer.CallAsync"/>) and through the door's tools under a grant
    /// (<see cref="GatewayPipeServer.CallAsGrantAsync"/>) is refused as it always was; the caller's own <c>tool_call</c> row
    /// (<c>served = 0</c>) is that refusal's one record, so the pipe writes no <c>op_refused</c> beside it, and a sweep yields one
    /// <c>refusals</c> finding per refusal — never a tool finding and an op finding for the same fact. RED before the fix: each refusal
    /// left both rows, and the sweep found four for two.
    /// </summary>
    [Fact]
    public async Task A_refusal_a_worker_tool_already_recorded_is_one_finding()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var home = Path.Combine(TestEnv.Home, "refrow-home-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(home);
        const string buy = """{"op":"buy","symbol":"ES","quantity":"1"}""";

        var harness = new TradeAgent.AgentRuntime.GrantedWorkerTools(CouncilRoles.Research, () => home, () => "attempt-refrow-tool", () => server,
            ledger: new ToolCallStore(db));
        var byHarness = await harness.InvokeAsync(new TradeAgent.AgentRuntime.ToolRequest("t1", TradeAgent.AgentRuntime.GrantedWorkerTools.Trade, buy));
        Assert.False(byHarness.Served);
        Assert.Contains(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), byHarness.Content, StringComparison.Ordinal);

        using var research = new AgentGrants().IssueHere(CouncilRoles.Research, "attempt-refrow-door");
        var door = TradeAgent.AgentRuntime.BrokerDoor.ToolsFor(research, _ => home, () => server, new ToolCallStore(db));
        var byDoor = await door.InvokeAsync(new TradeAgent.AgentRuntime.ToolRequest("t2", TradeAgent.AgentRuntime.GrantedWorkerTools.Trade, buy));
        Assert.False(byDoor.Served);

        Assert.Equal(2, db.Read(_ =>
        {
            using var c = db.Cmd("SELECT COUNT(*) FROM tool_call WHERE served = 0");
            return Convert.ToInt32(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }));
        var now = DateTimeOffset.UtcNow;
        using var oversight = new Core.Oversight.OversightStore(Path.Combine(TestEnv.Home, $"oversight-{Guid.NewGuid():n}.db"), now);
        var swept = new Core.Oversight.OversightSweep(db.FilePath, oversight).Run(now);
        Assert.Null(swept.Failure);
        var found = swept.Added.Where(f => f.Detector == "refusals" && f.ScopeId == Core.Oversight.OversightStore.RootScope).ToList();
        foreach (var f in found) log.WriteLine($"{f.Subject} {string.Join(",", f.Measured.Select(m => $"{m.Key}={m.Value}"))}");
        Assert.Equal(["attempt:attempt-refrow-door", "attempt:attempt-refrow-tool"], found.Select(f => f.Subject).Order(StringComparer.Ordinal));
        Assert.Empty(Rows(db, "op_refused"));
    }

    /// <summary>
    /// (e10) EACH PIPE EVENT THE OWNER'S LINE COUNTS IS WRITTEN WHERE ITS GLOSS SAYS, AND NOWHERE ITS BLIND SENTENCE SAYS IT IS NOT (fix
    /// pass 3): the pipe is driven to every write site — a frame with no <c>v</c>, a hello with another one, a hello with a wrong machine
    /// token, a hello with a grant TradeAgent refuses, a frame on a connection whose grant ended, a session of <c>operator</c> and one
    /// starting <c>deployment:</c>, a read-loop request refused on rights — and each leaves exactly its one row, whose gloss in
    /// <see cref="Core.Oversight.RefusalsDetector.Recorded"/> names that very case. Then every refusal the sentence names as unseen
    /// (<see cref="Core.Oversight.RefusalsDetector.NotRecorded"/>, fix pass 4) — a versioned frame before hello, a frame that is not valid
    /// JSON or is empty, a handler's refusal of malformed content — is driven and leaves no row; and a post-hello frame naming another
    /// version is served (pre-existing, carded), so the sentence does not call it refused. RED before fix pass 3: no gloss table existed.
    /// </summary>
    [Fact]
    public async Task Each_pipe_event_is_written_where_its_gloss_says_and_nowhere_its_blind_sentence_says_not()
    {
        var pipe = NewPipe();
        var now = DateTimeOffset.UtcNow;
        var grants = new AgentGrants(() => now);
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();
        var gloss = Core.Oversight.RefusalsDetector.Recorded;
        var counted = Core.Oversight.OrgSnapshot.PipeRefusalEvents;
        int All() => counted.Sum(e => Rows(db, e).Count);
        var before = new Dictionary<string, int>(StringComparer.Ordinal);
        void Mark() { foreach (var e in counted) before[e] = Rows(db, e).Count; }
        // EXACTLY ONE ROW, OF THIS EVENT, and its gloss names this case.
        void Wrote(string @event, string glossSays)
        {
            Assert.Contains(glossSays, gloss[@event], StringComparison.Ordinal);
            foreach (var e in counted) Assert.Equal(before[e] + (e == @event ? 1 : 0), Rows(db, e).Count);
        }
        IpcRequest Hello(string? grant = null, int? v = null, string? token = null, string? session = null) => new()
        {
            Op = Ops.Hello, Token = token ?? IpcToken.Ensure(), Grant = grant, Session = session, V = v ?? Versions.ProtocolVersion
        };

        // A FRAME THAT NAMES NO PROTOCOL VERSION — any frame, before hello.
        Mark();
        await using (var c = await Raw.ConnectAsync(pipe))
            Assert.Equal(nameof(ErrorCode.INCOMPATIBLE_PROTOCOL), (await c.SendLineAsync("""{"op":"status"}""")).Error?.Code);
        Wrote("protocol_rejected", "a frame that names no protocol version");

        // A HELLO THAT NAMES ANOTHER ONE.
        Mark();
        await using (var c = await Raw.ConnectAsync(pipe))
            Assert.Equal(nameof(ErrorCode.INCOMPATIBLE_PROTOCOL), (await c.SendAsync(Hello(v: Versions.ProtocolVersion + 1))).Error?.Code);
        Wrote("protocol_rejected", "a hello that names another one");

        // A HELLO WHOSE MACHINE TOKEN DOES NOT MATCH.
        Mark();
        await using (var c = await Raw.ConnectAsync(pipe))
            Assert.Equal(nameof(ErrorCode.IPC_UNAUTHENTICATED), (await c.SendAsync(Hello(token: "not-the-machine-token"))).Error?.Code);
        Wrote("auth_rejected", "a hello whose machine token did not match");

        // A HELLO WHOSE LAUNCH GRANT TRADEAGENT REFUSES.
        Mark();
        await using (var c = await Raw.ConnectAsync(pipe))
            Assert.False((await c.SendAsync(Hello(grant: "no-such-grant"))).Ok);
        Wrote("grant_rejected", "a hello whose launch grant TradeAgent refused");

        // A LATER FRAME ON A CONNECTION WHOSE GRANT HAS ENDED.
        var ending = grants.IssueHere(CouncilRoles.Research, "attempt-refrow-gloss", TimeSpan.FromMinutes(5));
        Mark();
        await using (var c = await Raw.ConnectAsync(pipe))
        {
            Assert.True((await c.SendAsync(Hello(grant: ending.Token))).Ok);
            now = now.AddHours(1);
            Assert.False((await c.SendAsync(new IpcRequest { Op = Ops.Positions, Token = IpcToken.Ensure(), Grant = ending.Token })).Ok);
        }
        Wrote("grant_rejected", "a later frame on a connection whose grant has ended");

        // THE RESERVED SESSIONS: 'operator', and any session starting 'deployment:' — a hello included.
        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-refrow-gloss2");
        foreach (var (session, says) in new[] { ("operator", "the reserved session 'operator'"), ("deployment:x", "a session starting 'deployment:'") })
        {
            Mark();
            await using (var c = await Raw.ConnectAsync(pipe))
                Assert.False((await c.SendAsync(Hello(grant: research.Token, session: session))).Ok);
            Wrote("operator_session_refused", says);
        }

        // A READ-LOOP REQUEST REFUSED ON RIGHTS OR AUTHORITY.
        Mark();
        await using (var c = await Raw.ConnectAsync(pipe))
        {
            Assert.True((await c.SendAsync(Hello(grant: research.Token))).Ok);
            Assert.Equal(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), (await c.SendAsync(Buy(research.Token))).Error?.Code);
        }
        Wrote(Core.RefusalMark.Event, "refused on rights or authority");

        // WHAT THE SENTENCE NAMES AS UNSEEN IS EXACTLY WHAT THE PIPE REFUSES WITHOUT A ROW (fix pass 4), each driven here.
        var blind = new Core.Oversight.RefusalsDetector().Blind;
        Assert.All(Core.Oversight.RefusalsDetector.NotRecorded, unseen => Assert.Contains(unseen, blind, StringComparison.Ordinal));
        var total = All();
        // A frame that names a protocol version but arrives before hello.
        await using (var c = await Raw.ConnectAsync(pipe))
            Assert.Equal(nameof(ErrorCode.IPC_UNAUTHENTICATED), (await c.SendAsync(new IpcRequest { Op = Ops.Positions, Token = IpcToken.Ensure() })).Error?.Code);
        Assert.Equal(total, All());
        await using (var c = await Raw.ConnectAsync(pipe))
        {
            // A frame that is not valid JSON, one that is not an object, and an empty one.
            foreach (var (frame, says) in new[] { ("this is not json", "frame is not valid JSON"), ("[1]", "frame is not valid JSON"), ("null", "empty frame") })
                Assert.Equal(says, (await c.SendLineAsync(frame)).Error?.Message);
            Assert.Equal(total, All());

            // A request the handler refused only because what it said was malformed: a quote that names no symbol.
            Assert.True((await c.SendAsync(Hello(grant: research.Token))).Ok);
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), (await c.SendAsync(new IpcRequest
            {
                Op = Ops.Quote, Token = IpcToken.Ensure(), Grant = research.Token, Args = []
            })).Error?.Code);
            Assert.Equal(total, All());

            // PRE-EXISTING, OUT OF THIS UNIT (seat A's card "the pipe serves a post-hello frame naming another protocol version"): after
            // hello, a frame naming another version is SERVED — no refusal at all, so it is not in the refusals sentence.
            Assert.True((await c.SendAsync(new IpcRequest
            {
                Op = Ops.Positions, Token = IpcToken.Ensure(), Grant = research.Token, V = Versions.ProtocolVersion + 1
            })).Ok);
        }
        Assert.DoesNotContain("names another protocol version", blind.Replace("a hello that names another one", ""), StringComparison.Ordinal);
        Assert.Equal(total, All());
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
    /// no exception text — and the planted op name is in no row of the engineering log at all. Over the READ LOOP, the path that writes the
    /// row (fix pass 1, R4: an in-process caller records its own answer, so it was moved here from <c>CallAsync</c>).
    /// </summary>
    [Fact]
    public async Task The_refusal_row_holds_no_request_payload()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();
        var planted = "planted" + Guid.NewGuid().ToString("n")[..10];

        using var research = grants.IssueHere(CouncilRoles.Research, "attempt-refrow-p");
        await using (var client = await Raw.ConnectAsync(pipe))
        {
            Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = research.Token })).Ok);
            Assert.False((await client.SendAsync(Buy(research.Token, symbol: planted + "SYM", session: planted + "-session", rid: planted + "-rid"))).Ok);
            Assert.False((await client.SendAsync(new IpcRequest
            {
                Op = planted + "-op", Token = IpcToken.Ensure(), Grant = research.Token, Session = planted + "-session"
            })).Ok);
        }

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

        public Task<IpcResponse> SendAsync(IpcRequest req) => SendLineAsync(Core.Json.Write(req));

        /// <summary>Sends exactly <paramref name="frame"/> — a frame the typed request could not express, one with no <c>v</c>.</summary>
        public async Task<IpcResponse> SendLineAsync(string frame)
        {
            await _w.WriteLineAsync(frame);
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
