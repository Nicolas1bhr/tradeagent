using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE SECURITY BOARD AS DATA, PART A1 (<c>U-board-a1</c>; R24 §§ 4.1, 4.5): three dormant seats outside the chart, one founding
/// policy decided by <c>app</c> that the gateway reads its mode and activation from, live closed by construction, a home an earlier
/// build armed for real money refused in words and kept, and a policy that fails to verify failing closed. NOTHING HERE REACHES A
/// MODEL OR A BROKER: every connector is the simulator, every position is written with raw SQL.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class BoardTests(ITestOutputHelper log)
{
    const int BoardRung = 49;

    static string? Scalar(Database db, string sql, params (string, object?)[] ps) => db.Read(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return Convert.ToString(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    static int Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    /// <summary>One statement on a connection of its own, outside every store — a damaged or older file, made the way one is made.</summary>
    static void Raw(string file, string sql)
    {
        using var raw = new SqliteConnection($"Data Source={file};Pooling=False");
        raw.Open();
        using var c = raw.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    static string NewFile() => Path.Combine(TestEnv.Home, $"board-{Guid.NewGuid():n}.db");

    static async Task<(TradingGateway Gw, FakeConnector Conn)> Gateway(Database db, bool simulated = true)
    {
        var conn = new FakeConnector(new FakeBroker { IsSimulated = simulated });
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn);
    }

    // ---- the founding record -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A FRESH HOME OPENS WITH ONE POLICY ROW, VERSION 1, DECIDED BY THE APP ON THE FOUNDING BASIS — its body the founding policy's
    /// canonical text and its hash that body's — and the gateway's status shows the mode, the activation and the limits it states.
    /// RED before rung 49: no <c>board_policy</c> table.
    /// </summary>
    [Fact]
    public async Task Founding_policy_is_one_app_record_and_the_gateway_reads_it()
    {
        using var db = TestEnv.NewDb();
        var rows = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT version, decider, basis, mode, live_activated, body, body_sha256, supersedes FROM board_policy");
            using var r = c.ExecuteReader();
            var all = new List<(long, string, string, string, long, string, string, object)>();
            while (r.Read())
                all.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetValue(7)));
            return all;
        });
        var (version, decider, basis, mode, live, body, sha, supersedes) = Assert.Single(rows);
        log.WriteLine($"v{version} {decider}/{basis} {mode} live={live} sha={sha}");
        Assert.Equal((1L, "app", "founding", "PAPER", 0L), (version, decider, basis, mode, live));
        Assert.Equal(DBNull.Value, supersedes);
        Assert.Equal(BoardPolicyBody.Canonical(BoardFounding.V1), body);
        Assert.Equal(Sha256Hex.Of(body), sha);
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM board_event WHERE kind='founded' AND policy_version=1"));

        var inForce = new BoardReader(db).InForce();
        Assert.False(inForce.CouldNotBeRead, inForce.Why);
        Assert.Equal((1, "app", "founding", sha), (inForce.Version, inForce.Decider, inForce.Basis, inForce.BodySha256));
        Assert.Equal(BoardFounding.V1, inForce.Policy);
        Assert.All(inForce.Fields, f => Assert.Equal(1, f.SetByVersion));
        Assert.Contains(inForce.Fields, f => f.Name == "mode" && f.Value == "PAPER");
        Assert.Contains(inForce.Fields, f => f.Name == "risk.max_order_quantity" && f.Value == "1");
        var history = Assert.Single(new BoardReader(db).History());
        Assert.Equal((1, "app", "founding", (string?)null, (string?)null, (decimal?)null),
            (history.Version, history.Decider, history.Basis, history.Rationale, history.EvidenceClass, history.Score));

        // THE FOUNDING VALUES ARE THE BUILD'S CURRENT DEFAULTS where the settings still hold one — every limit but the allowlist,
        // which has no default and is the orchestrator's ruled value, with the paper envelope (R24 § 4.5, Q3).
        var defaults = new TradeAgentSettings();
        Assert.Equal([Labels.InstrumentAllowlist], RiskPolicy.Changes(defaults.Risk, BoardFounding.V1.Risk));
        Assert.Equal(["BTCUSDT"], BoardFounding.V1.Risk.InstrumentAllowlist);
        Assert.Equal(new PaperEnvelopeTerms(TradeAgent.Connectors.Paper.PaperConnector.ConnectorId, TradeAgent.Connectors.Paper.PaperConnector.TheAccount,
            "BTCUSDT", 0.05m, 5_000m, TradingGateway.PaperDeploymentsPerEnvelope, null), BoardFounding.V1.PaperEnvelope);
        Assert.Contains("0.05 BTC", Scalar(db, "SELECT detail FROM board_event WHERE kind='founded'"), StringComparison.Ordinal);
        Assert.Equal((defaults.AiPlanDailyCapacity, defaults.MissionReviewMinutes, defaults.HeadCeilingHours, defaults.AiLanes,
                defaults.AiLanesPerPool, defaults.AiLanesPerCellClass, defaults.AiTurnAllowanceInputTokens, defaults.AiTurnAllowanceOutputTokens),
            (BoardFounding.V1.PlanDayTotal, BoardFounding.V1.ReviewMinutes, BoardFounding.V1.HeadCeilingHours, BoardFounding.V1.Lanes,
                BoardFounding.V1.LanesPerPool, BoardFounding.V1.LanesPerCellClass, BoardFounding.V1.TurnAllowanceInput, BoardFounding.V1.TurnAllowanceOutput));
        Assert.Equal(defaults.RewakeFloorMinutes.OrderBy(k => k.Key), BoardFounding.V1.RewakeFloorMinutes.OrderBy(k => k.Key));

        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        var status = await gw.StatusAsync();
        Assert.Equal((TradingMode.PAPER, false), (status.Mode, status.LiveActivated));
        // THE LIMITS THE STATUS SHOWS ARE THE POLICY'S, but for the allowlist, which the gateway still reads from the settings row
        // until U-board-a2 moves every limit's read onto the policy (item 8).
        Assert.Equal([Labels.InstrumentAllowlist], RiskPolicy.Changes(status.Risk, inForce.Policy.Risk));
    }

    /// <summary>
    /// LIVE IS CLOSED BY CONSTRUCTION: the schema itself refuses a policy row in either real-money mode or with real money switched
    /// on, and refuses every UPDATE and DELETE of the board's tables. RED before rung 49: no table to refuse anything.
    /// </summary>
    [Fact]
    public void No_row_of_the_board_store_holds_a_live_mode_or_an_activation()
    {
        using var db = TestEnv.NewDb();
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='board_policy'"));
        var body = BoardPolicyBody.Canonical(BoardFounding.V1);
        const string insert = "INSERT INTO board_policy(version, decider, basis, written_at, mode, live_activated, body, body_sha256, supersedes) "
                              + "VALUES(2, 'app', 'founding', '2026-10-23T00:00:00.0000000Z', $mode, $live, $body, $sha, 1)";

        foreach (var (mode, live) in new[] { ("LIVE_CONFIRM", 0), ("LIVE_AUTONOMOUS", 0), ("PAPER", 1), ("LIVE_AUTONOMOUS", 1) })
        {
            var refused = Assert.Throws<SqliteException>(() => Exec(db, insert,
                ("$mode", mode), ("$live", live), ("$body", body), ("$sha", Sha256Hex.Of(body))));
            log.WriteLine($"insert {mode}/{live}: {refused.Message}");
            Assert.Contains("CHECK constraint failed", refused.Message, StringComparison.Ordinal);
        }

        foreach (var sql in new[]
                 {
                     "UPDATE board_policy SET mode='LIVE_CONFIRM' WHERE version=1",
                     "UPDATE board_policy SET live_activated=1 WHERE version=1",
                     "UPDATE board_policy SET mode='LIVE_AUTONOMOUS', live_activated=1 WHERE version=1",
                     "DELETE FROM board_policy",
                     "UPDATE board_seat SET status='active'",
                     "DELETE FROM board_seat",
                     "UPDATE board_event SET detail='x'",
                     "DELETE FROM board_event"
                 })
        {
            var refused = Assert.Throws<SqliteException>(() => Exec(db, sql));
            log.WriteLine($"{sql}: {refused.Message}");
        }

        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM board_policy"));
        Assert.Equal("PAPER|0", Scalar(db, "SELECT mode || '|' || live_activated FROM board_policy WHERE version=1"));
    }

    /// <summary>
    /// NO BOARD ROW IS REWRITTEN BY A REPLACE (fix pass 1 of <c>U-board-a1</c>, the review's finding on rung 49, then numbered 47): <c>INSERT OR REPLACE</c>
    /// deletes the row it collides with without firing a DELETE trigger while recursive triggers are off — every connection's default —
    /// so the update and delete seals alone let a seat's model class, a policy version or an event be rewritten. Each table refuses an
    /// insert over a key it already holds, whatever the statement's conflict clause, and the rows are unchanged. RED before the fix:
    /// the replace of <c>board-1</c> succeeded.
    /// </summary>
    [Fact]
    public void No_board_row_is_rewritten_by_a_replace()
    {
        using var db = TestEnv.NewDb();
        string Rows() => Scalar(db, "SELECT (SELECT group_concat(id || ':' || seat_class || ':' || model_class || ':' || vendor_family || ':' || status, ',') FROM board_seat)"
                                     + " || '#' || (SELECT group_concat(version || ':' || body_sha256 || ':' || written_at, ',') FROM board_policy)"
                                     + " || '#' || (SELECT group_concat(id || ':' || kind || ':' || detail, ',') FROM board_event)")!;
        var before = Rows();
        var seat1 = Scalar(db, "SELECT model_class FROM board_seat WHERE id='board-1'");
        var other = seat1 == "strong" ? "standard" : "strong";
        var eventId = Scalar(db, "SELECT MIN(id) FROM board_event");
        var body = Scalar(db, "SELECT body FROM board_policy WHERE version=1")!;

        foreach (var verb in new[] { "INSERT OR REPLACE", "REPLACE", "INSERT OR IGNORE" })
            foreach (var sql in new[]
                     {
                         $"{verb} INTO board_seat(id, seat_class, model_class, vendor_family, status, founded_at) "
                         + $"VALUES('board-1', 'harness', '{other}', 'another', 'dormant', '2026-10-23T00:00:00.0000000Z')",
                         $"{verb} INTO board_policy(version, decider, basis, written_at, mode, live_activated, body, body_sha256, supersedes) "
                         + "VALUES(1, 'app', 'founding', '2026-10-23T00:00:00.0000000Z', 'OBSERVE', 0, $body, $sha, NULL)",
                         $"{verb} INTO board_event(id, at, kind, policy_version, home_schema, budget_share, detail) "
                         + $"VALUES({eventId}, '2026-10-23T00:00:00.0000000Z', 'earlier-live-set-aside', NULL, NULL, NULL, 'rewritten')"
                     })
            {
                var refused = Assert.Throws<SqliteException>(() => Exec(db, sql, ("$body", body + " "), ("$sha", Sha256Hex.Of(body + " "))));
                log.WriteLine($"{sql[..Math.Min(60, sql.Length)]}…: {refused.Message}");
            }

        // NOR BY A REPLACE OVER ITS ROWID UNDER ANOTHER KEY (fix pass 1, second step, the re-review): `board-4` replacing board-1's
        // rowid would delete board-1's composition record and write another; a version or an event replaced through the rowid the same.
        foreach (var sql in new[]
                 {
                     "INSERT OR REPLACE INTO board_seat(rowid, id, seat_class, model_class, vendor_family, status, founded_at) "
                     + $"VALUES((SELECT rowid FROM board_seat WHERE id='board-1'), 'board-4', 'harness', '{other}', 'another', 'dormant', 'x')",
                     "INSERT OR REPLACE INTO board_policy(rowid, decider, basis, written_at, mode, live_activated, body, body_sha256, supersedes) "
                     + "VALUES(1, 'app', 'founding', '2026-10-23T00:00:00.0000000Z', 'OBSERVE', 0, $body, $sha, NULL)",
                     "INSERT OR REPLACE INTO board_event(rowid, at, kind, policy_version, home_schema, budget_share, detail) "
                     + $"VALUES({eventId}, '2026-10-23T00:00:00.0000000Z', 'earlier-live-set-aside', NULL, NULL, NULL, 'rewritten')"
                 })
        {
            var refused = Assert.Throws<SqliteException>(() => Exec(db, sql, ("$body", body + " "), ("$sha", Sha256Hex.Of(body + " "))));
            log.WriteLine($"{sql[..Math.Min(60, sql.Length)]}…: {refused.Message}");
        }

        // AN UPSERT IS AN UPDATE, AND THE UPDATE SEAL ALREADY REFUSES IT; asked here so the two seals are read as one promise.
        Assert.Throws<SqliteException>(() => Exec(db,
            "INSERT INTO board_seat(id, seat_class, model_class, vendor_family, status, founded_at) VALUES('board-1', 'harness', 'strong', 'x', 'dormant', 'x') "
            + "ON CONFLICT(id) DO UPDATE SET model_class=excluded.model_class"));

        Assert.Equal(before, Rows());
        Assert.Equal(seat1, Scalar(db, "SELECT model_class FROM board_seat WHERE id='board-1'"));
    }

    /// <summary>
    /// THE FOUNDING COMPOSITION: three seats, <c>board-1..3</c>, on at least two model classes, each broker, cell or harness and none a
    /// <c>cli</c>, each with its vendor family, all dormant — and the schema refuses a <c>cli</c> seat and an active one. Nothing
    /// launches one: no board seat is a position the supervisor builds a home for. RED before rung 49: no <c>board_seat</c> table.
    /// </summary>
    [Fact]
    public void Founding_composition_is_three_dormant_seats_on_two_model_classes()
    {
        using var db = TestEnv.NewDb();
        var rows = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT id, seat_class, model_class, vendor_family, status FROM board_seat ORDER BY id");
            using var r = c.ExecuteReader();
            var all = new List<(string Id, string Class, string Model, string Vendor, string Status)>();
            while (r.Read()) all.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            return all;
        });
        foreach (var s in rows) log.WriteLine($"{s.Id}: {s.Class} {s.Model} {s.Vendor} {s.Status}");
        Assert.Equal(["board-1", "board-2", "board-3"], rows.Select(s => s.Id));
        Assert.All(rows, s => Assert.Contains(s.Class, new[] { SeatClasses.Broker, SeatClasses.Cell, SeatClasses.Harness }));
        Assert.DoesNotContain(rows, s => s.Class == SeatClasses.Cli);
        Assert.True(rows.Select(s => s.Model).Distinct().Count() >= 2);
        Assert.All(rows, s => Assert.False(string.IsNullOrWhiteSpace(s.Vendor)));
        Assert.All(rows, s => Assert.Equal(BoardSeatStatus.Dormant, s.Status));

        var reader = new BoardReader(db);
        Assert.Equal(rows.Select(s => s.Id), reader.Seats().Select(s => s.Id));
        Assert.False(reader.AnyActive);

        foreach (var (cls, status) in new[] { (SeatClasses.Cli, "dormant"), (SeatClasses.Broker, "active") })
        {
            var refused = Assert.Throws<SqliteException>(() => Exec(db,
                "INSERT INTO board_seat(id, seat_class, model_class, vendor_family, status, founded_at) VALUES('board-4',$c,'strong','openai',$s,'x')",
                ("$c", cls), ("$s", status)));
            Assert.Contains("CHECK constraint failed", refused.Message, StringComparison.Ordinal);
        }

        var org = new OrgStore(db);
        Assert.DoesNotContain(org.Positions(), p => BoardSeats.IsBoardId(p.Id));
        Assert.DoesNotContain(org.Roster(), p => BoardSeats.IsBoardId(p.Id));
    }

    // ---- outside the chart ------------------------------------------------------------------------------------------------

    const string Head = "pos-b0a4d000001", Member = "pos-b0a4d000002", Team = "team-b0a4d000";
    const string Stamp = "2026-10-23T03:00:00.0000000Z";

    static void Seat(Database db, string id, string unit) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$at", Stamp));

    static IpcRequest Frame(string op) => new()
    {
        Op = op,
        Session = "board-a1",
        RequestId = "board-" + Guid.NewGuid().ToString("n")[..12],
        Args = new Dictionary<string, JsonElement>()
    };

    /// <summary>
    /// A BOARD SEAT IS REFUSED WHEREVER A POSITION IS NAMED — even on a chart holding an <c>org_position</c> row under its id, as a
    /// build before the rule could have written: refused as a minted id, as a mandate's holder, as a note's recipient and sender, as
    /// a decider, as the caller of an organisation verb on the read loop and through the door; and the chart's readers never return
    /// it. RED before item 3: <c>PositionIdRefusal("board-1")</c> was null and the seated row read as an active position.
    /// </summary>
    [Fact]
    public async Task The_board_is_outside_the_chart()
    {
        const string Board = "board-1";
        var minted = OrgIds.PositionIdRefusal(Board);
        log.WriteLine($"mint: {minted ?? "null — the id could be minted"}");
        Assert.Contains("security board", minted ?? "", StringComparison.Ordinal);
        Assert.True(BoardSeats.IsBoardId(Board));

        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", Team), ("$p", OrgIds.Research), ("$at", Stamp));
        Seat(db, Head, Team);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", Head), ("$t", Team));
        Seat(db, Member, Team);
        Seat(db, Board, Team);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;

        var assign = org.AssignRefusal(new MandateIssue(Head, null, Board, MandateKind.Task, "decide this", null,
            MandateShape.Report, false, now.AddDays(1), 1m, 1), now);
        log.WriteLine($"assign: {assign}");
        Assert.Contains("security board", assign ?? "", StringComparison.Ordinal);
        var to = Assert.Throws<TradeAgentException>(() => org.SendLateral(Head, null, Board, "a note", [], now));
        Assert.Contains("security board", to.Message, StringComparison.Ordinal);
        var from = Assert.Throws<TradeAgentException>(() => org.SendLateral(Board, null, Member, "a note", [], now));
        Assert.Contains("security board", from.Message, StringComparison.Ordinal);
        var decided = Assert.Throws<TradeAgentException>(() => org.Decide(Board, null, OrgDecisionKind.Routine,
            ["a", "b"], "a", "because", [], [], now));
        Assert.Contains("security board", decided.Message, StringComparison.Ordinal);
        Assert.Equal("0", Scalar(db, "SELECT (SELECT COUNT(*) FROM org_lateral) + (SELECT COUNT(*) FROM org_decision) + (SELECT COUNT(*) FROM org_mandate)"));

        Assert.Null(org.Standing(Board));
        Assert.DoesNotContain(org.Positions(), p => p.Id == Board);
        Assert.DoesNotContain(org.Roster(), p => p.Id == Board);
        Assert.DoesNotContain(Board, org.ActivePositionsBeyondLegacy());

        Assert.Null(gw.ActivePositionOf(Board));
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var loop = await server.CallAsync(Frame(Ops.MandateList), Board, "turn-board-1");
        Assert.False(loop.Ok);
        log.WriteLine($"loop: {loop.Error!.Message}");
        var door = await server.CallAsGrantAsync(Frame(Ops.MandateList), new AgentGrants().IssueHere(Board, "turn-board-2"));
        Assert.False(door.Ok);
    }

    // ---- an earlier armed home --------------------------------------------------------------------------------------------

    /// <summary>
    /// A HOME AN EARLIER BUILD ARMED FOR REAL MONEY PLACES NOTHING AND KEEPS ITS ROW: a settings row at schema 46 saying
    /// <c>LIVE_AUTONOMOUS</c> with real money switched on refuses every order under <c>HOME_ARMED_BEFORE_THE_BOARD</c> in words naming 46,
    /// 49 and the press, leaves the raw row byte for byte — a kill-switch press included — and pauses execution. The owner's one press
    /// keeps the row under <c>settings_before_board</c> and writes the current shape; the refusal lifts, the mode is PAPER from the
    /// founding policy, and no policy row was made of it. RED at the shape commit: the row's mode governed.
    /// </summary>
    [Fact]
    public async Task An_earlier_home_armed_for_real_money_places_nothing_and_keeps_its_row()
    {
        var file = NewFile();
        var broker = new FakeBroker { IsSimulated = false };
        var raw = "{\"mode\":\"LIVE_AUTONOMOUS\",\"live_activated\":true,\"ai_trading_stopped\":false,"
                  + $"\"selected_account_id\":\"{broker.AccountId}\",\"risk\":{{\"max_order_quantity\":10,\"max_notional_per_order\":10000000,"
                  + "\"max_open_positions\":10,\"max_orders_per_minute\":100,\"instrument_allowlist\":[\"ES\",\"NQ\"]}}";

        // A HOME AT SCHEMA 46: this build's file with the board's rung undone, and the row an earlier build wrote.
        using (var made = new Database(file)) { }
        Raw(file, "DROP TABLE IF EXISTS board_event; DROP TABLE IF EXISTS board_seat; DROP TABLE IF EXISTS board_policy; "
                  + "UPDATE meta SET value='46' WHERE key='schema_version';");
        Raw(file, $"INSERT INTO kv(key,value) VALUES('settings','{raw}') ON CONFLICT(key) DO UPDATE SET value=excluded.value;");

        using var db = new Database(file);
        var conn = new FakeConnector(broker);
        await using var gw = new TradingGateway(db, conn, new HealthRegistry());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "armed-1", TestEnv.Buy()));
        log.WriteLine($"{denied.Code}: {denied.Message}");
        Assert.Equal(ErrorCode.HOME_ARMED_BEFORE_THE_BOARD, denied.Code);
        Assert.Contains("schema 46", denied.Message, StringComparison.Ordinal);
        Assert.Contains($"schema {BoardRung}", denied.Message, StringComparison.Ordinal);
        Assert.Contains(Labels.SetAsideEarlierLiveSetting, denied.Message, StringComparison.Ordinal);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(HealthState.PAUSED, gw.Health.Snapshot().Single(h => h.Component == Components.ExecutionCapability).State);
        var earlier = Assert.IsType<EarlierLiveSetting>(gw.EarlierLiveSetting);
        Assert.Equal(("LIVE_AUTONOMOUS", "true", 46), (earlier.Mode, earlier.LiveActivated, earlier.HomeSchema));
        Assert.Equal(raw, db.GetKv("settings"));

        // THE KILL SWITCH STILL WORKS, AND WRITES NOTHING OVER THE ROW: it is kept as found until the owner's own press.
        gw.StopAiTrading("a test press");
        gw.EnableAiTrading();
        Assert.Equal(raw, db.GetKv("settings"));
        Assert.Null(db.GetKv("settings_before_board"));

        gw.SetAsideEarlierLiveSetting();
        Assert.Equal(raw, db.GetKv("settings_before_board"));
        var now = db.GetKv("settings")!;
        log.WriteLine($"current shape: {now}");
        Assert.DoesNotContain("\"mode\"", now, StringComparison.Ordinal);
        Assert.DoesNotContain("live_activated", now, StringComparison.Ordinal);
        Assert.Null(gw.EarlierLiveSetting);
        var status = await gw.StatusAsync();
        Assert.Equal((TradingMode.PAPER, false), (status.Mode, status.LiveActivated));
        var after = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "armed-2", TestEnv.Buy()));
        log.WriteLine($"after the press: {after.Code}: {after.Message}");
        Assert.Equal(ErrorCode.MODE_ACCOUNT_MISMATCH, after.Code);
        Assert.Equal("1|PAPER|0", Scalar(db, "SELECT COUNT(*) || '|' || MAX(mode) || '|' || MAX(live_activated) FROM board_policy"));
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM board_event WHERE kind='earlier-live-set-aside'"));
    }

    /// <summary>
    /// AN EARLIER ARMED ROW THIS BUILD CANNOT OTHERWISE READ IS STILL REFUSED AND STILL KEPT (fix pass 1 of <c>U-board-a1</c>, the
    /// review's finding on <c>LoadSettings</c>): the raw <c>mode</c>/<c>live_activated</c> check runs on the raw row before anything
    /// else can fail, so a live row with an unrelated field this build cannot read — or a row cut off mid-way — refuses every order
    /// under <c>HOME_ARMED_BEFORE_THE_BOARD</c>, and neither an ordinary save nor a kill-switch press rewrites it; only the owner's
    /// set-aside press does, keeping it under its own key. RED before the fix: deserialisation returned first, so the armed row was
    /// an unreadable one, its refusal another code, and the first save wrote over it.
    /// </summary>
    [Theory]
    [InlineData("{\"mode\":\"LIVE_AUTONOMOUS\",\"live_activated\":true,\"ai_trading_stopped\":\"bad\"}")]
    [InlineData("{\"mode\":\"LIVE_AUTONOMOUS\",\"live_activated\":true,\"risk\":[]}")]
    [InlineData("{\"mode\":\"LIVE_AUTONOMOUS\",\"live_activated\":true,\"risk\":{\"max_order_qu")]
    public async Task An_earlier_armed_row_with_a_field_this_build_cannot_read_is_refused_and_kept(string raw)
    {
        using var db = TestEnv.NewDb();
        db.SetKv("settings", raw);
        var conn = new FakeConnector(new FakeBroker { IsSimulated = false });
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var earlier = gw.EarlierLiveSetting;
        log.WriteLine($"earlier setting: {earlier}");
        Assert.NotNull(earlier);

        // AN ORDINARY SAVE AND A KILL-SWITCH PRESS WRITE NOTHING OVER THE ROW.
        gw.Update(_ => { });
        gw.StopAiTrading("a test press");
        Assert.Equal(raw, db.GetKv("settings"));

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "armed-damaged", TestEnv.Buy()));
        log.WriteLine($"{denied.Code}: {denied.Message}");
        Assert.Equal(ErrorCode.HOME_ARMED_BEFORE_THE_BOARD, denied.Code);
        Assert.Contains(Labels.SetAsideEarlierLiveSetting, denied.Message, StringComparison.Ordinal);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(earlier.Sentence, gw.Health.Snapshot().Single(h => h.Component == Components.ExecutionCapability).Detail);
        await gw.DisposeAsync();

        // A RESTART FINDS IT STILL THERE, AND STILL REFUSES.
        var conn2 = new FakeConnector(new FakeBroker { IsSimulated = false });
        await using var again = new TradingGateway(db, conn2, new HealthRegistry());
        Assert.NotNull(again.EarlierLiveSetting);
        Assert.Equal(raw, db.GetKv("settings"));

        // ONLY THE OWNER'S PRESS MOVES IT, AND IT KEEPS THE ROW AS FOUND.
        again.SetAsideEarlierLiveSetting();
        Assert.Null(again.EarlierLiveSetting);
        Assert.Equal(raw, db.GetKv(TradingGateway.SettingsBeforeBoardKey));
        Assert.NotEqual(raw, db.GetKv("settings"));
    }

    /// <summary>A mode an earlier build wrote that is not live is ignored: no refusal, the founding policy governs.</summary>
    [Fact]
    public async Task An_earlier_practice_setting_is_ignored()
    {
        using var db = TestEnv.NewDb();
        db.SetKv("settings", "{\"mode\":\"PAPER\",\"live_activated\":false,\"ai_trading_stopped\":false}");
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        Assert.Null(gw.EarlierLiveSetting);
        Assert.Equal(TradingMode.PAPER, (await gw.StatusAsync()).Mode);
    }

    // ---- fail closed and recovery -----------------------------------------------------------------------------------------

    /// <summary>
    /// A POLICY THAT FAILS TO VERIFY FAILS CLOSED: a body changed under its hash, or a hash changed under its body — written with the
    /// seal removed, as a damaged file would be — reads as the unreadable policy: OBSERVE, so nothing is dispatched; a plan day-total of
    /// zero, so nothing is admitted on the plan; execution paused; and the row is kept as it was. RED before rung 49.
    /// </summary>
    [Theory]
    [InlineData("body")]
    [InlineData("hash")]
    public async Task An_unreadable_policy_fails_closed(string tampered)
    {
        var file = NewFile();
        using (var made = new Database(file)) { }
        Raw(file, "DROP TRIGGER IF EXISTS board_policy_sealed_u; " + (tampered == "body"
            ? "UPDATE board_policy SET body = replace(body, '\"plan_day_total\":40', '\"plan_day_total\":400') WHERE version=1;"
            : "UPDATE board_policy SET body_sha256 = '" + new string('0', 64) + "' WHERE version=1;"));
        string Row(Database d) => Scalar(d, "SELECT body || '|' || body_sha256 FROM board_policy WHERE version=1")!;

        using var db = new Database(file);
        var kept = Row(db);
        Assert.Contains(tampered == "body" ? "400" : new string('0', 64), kept, StringComparison.Ordinal);

        var inForce = new BoardReader(db).InForce();
        log.WriteLine($"in force: unreadable={inForce.CouldNotBeRead} why={inForce.Why}");
        Assert.True(inForce.CouldNotBeRead);
        Assert.Equal(BoardPolicy.Unreadable(), inForce.Policy);

        var (gw, conn) = await Gateway(db);
        await using var _gw = gw;
        gw.Update(s => { s.SelectedAccountId = conn.Broker.AccountId; s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments]; });
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "unreadable-1", TestEnv.Buy()));
        log.WriteLine($"{denied.Code}: {denied.Message}");
        Assert.Equal(ErrorCode.MODE_FORBIDS_EXECUTION, denied.Code);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(TradingMode.OBSERVE, (await gw.StatusAsync()).Mode);
        Assert.Equal(HealthState.PAUSED, gw.Health.Snapshot().Single(h => h.Component == Components.ExecutionCapability).State);

        var host = TradeAgent.App.AppHost.Composed(db, conn, new AgentPresence());
        await using var _host = host;
        Assert.Equal(0m, host.Meter!.TodayIn(BillingClasses.Plan).Cap);
        Assert.Equal(kept, Row(db));
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM board_policy"));
    }

    /// <summary>
    /// RUNG 49 RE-RUN AFTER A CRASH BEFORE ITS STAMP WRITES NOTHING TWICE: the tables and the founding record in place, the stamp one
    /// lower — the rung runs again and leaves one policy row, three seats and one founding event, stamped 49. RED before rung 49.
    /// </summary>
    [Fact]
    public void Rung_47_rerun_after_a_crash_before_its_stamp_writes_nothing_twice()
    {
        var file = NewFile();
        string Counts(Database d) => Scalar(d,
            "SELECT (SELECT COUNT(*) FROM board_policy) || '/' || (SELECT COUNT(*) FROM board_seat) || '/' || (SELECT COUNT(*) FROM board_event)")!;
        string first;
        using (var db = new Database(file))
        {
            first = Counts(db);
            Assert.Equal("1/3/1", first);
        }
        Raw(file, $"UPDATE meta SET value='{BoardRung - 1}' WHERE key='schema_version';");
        using (var rerun = new Database(file))
        {
            Assert.Equal(first, Counts(rerun));
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), Scalar(rerun, "SELECT value FROM meta WHERE key='schema_version'"));
            Assert.False(new BoardReader(rerun).InForce().CouldNotBeRead);
        }
    }

    // ---- guards -----------------------------------------------------------------------------------------------------------

    static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }

    static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// NO APP PATH SELECTS A LIVE MODE OR SWITCHES REAL MONEY ON: the settings hold neither, the gateway has no public method that
    /// takes a mode or an activation, the policy's store has no public writer at all, the test host's seam is internal and nothing in
    /// <c>src/</c> calls it, and the dev host reads no <c>mode</c>, <c>live</c> or <c>risk</c> line. RED at the shape commit:
    /// <c>SetMode</c> and <c>ActivateLive</c> were public.
    /// </summary>
    [Fact]
    public void No_app_path_selects_a_live_mode()
    {
        Assert.Null(typeof(TradeAgentSettings).GetProperty("Mode"));
        Assert.Null(typeof(TradeAgentSettings).GetProperty("LiveActivated"));

        var gateway = typeof(TradingGateway).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        Assert.DoesNotContain(gateway, m => m.Name is "SetMode" or "ActivateLive");
        Assert.DoesNotContain(gateway, m => m.GetParameters().Any(p => p.ParameterType == typeof(TradingMode)));
        Assert.DoesNotContain(gateway, m => m.Name.Contains("UnderTest", StringComparison.Ordinal));
        Assert.Null(typeof(TradingGateway).GetProperty("Policy")?.SetMethod is { IsPublic: true } ? "public setter" : null);
        Assert.DoesNotContain(typeof(BoardStore).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            m => m.GetParameters().Any(p => p.ParameterType == typeof(BoardPolicy) || p.ParameterType == typeof(TradingMode)));

        var callers = SourceFiles()
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(l => Regex.IsMatch(l.line, @"(SetModeUnderTest|ActivateLiveUnderTest)\s*\(") && !l.line.Contains("internal void", StringComparison.Ordinal))
            .Select(l => $"{Path.GetFileName(l.f)}:{l.i + 1}")
            .ToList();
        Assert.Empty(callers);

        var host = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TradeAgent.GatewayHost", "Program.cs"));
        foreach (var verb in new[] { "case \"mode\"", "case \"live\"", "case \"risk\"" })
            Assert.DoesNotContain(verb, host, StringComparison.Ordinal);
    }

    /// <summary>ONLY <c>BoardStore</c> WRITES THE BOARD'S TABLES: no other file in <c>src/</c> inserts, updates, replaces or deletes a <c>board_*</c> row.</summary>
    [Fact]
    public void Only_BoardStore_writes_board_tables()
    {
        var writes = new Regex(@"(INSERT\s+(OR\s+\w+\s+)?INTO|REPLACE\s+INTO|UPDATE|DELETE\s+FROM)\s+board_", RegexOptions.IgnoreCase);
        var writers = SourceFiles()
            .Where(f => writes.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        log.WriteLine($"writers: {string.Join(", ", writers)}");
        Assert.Equal(["BoardStore.cs"], writers);
    }

    /// <summary>
    /// THE POLICY HOLDS NO EVIDENCE SETTING AND NOTHING OF THE OWNER'S OWN: no friction, holdout, price, campaign, judge capital,
    /// verdict or trial member, no paid-key cap and no perception budget (EVID-001, AUTH-001) — by the type's members and by the
    /// body's own keys.
    /// </summary>
    [Fact]
    public void The_policy_holds_no_evidence_setting()
    {
        string[] forbidden = ["fee", "slippage", "friction", "holdout", "price", "campaign", "judge", "verdict", "trial", "exploration",
            "costcap", "cost_cap", "perception", "watcher", "audit", "composition", "budget"];
        var members = typeof(BoardPolicy).GetProperties().Select(p => p.Name.ToLowerInvariant())
            .Concat(BoardPolicyBody.Flatten(BoardPolicyBody.Canonical(BoardFounding.V1)).Select(f => f.Path.ToLowerInvariant()))
            .ToList();
        foreach (var m in members) log.WriteLine(m);
        Assert.DoesNotContain(members, m => forbidden.Any(f => m.Contains(f, StringComparison.Ordinal)));
    }
}
