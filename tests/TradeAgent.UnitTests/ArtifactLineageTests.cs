using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EVERY ARTIFACT NAMES ITS AUTHOR POSITION AND THE ATTEMPT WHOSE READS PRODUCED IT (<c>U-lineage-check</c>; R26 stage 2 unit 1;
/// VISION § 7.2 and § 11's lineage line). The conformance half reads the schema — every table with an attempt column is an artifact
/// with its trigger or is named as not one — and the database refuses a row that skips either; the writer half proves each kind's
/// writer refuses in words and, through the real op, records both, so <see cref="ArtifactLineage.Of"/> reaches what the attempt
/// read (its packet's records and its calls through TradeAgent). A row written before the rung stays as it was and is labelled.
/// NOTHING HERE REACHES A REAL MODEL: every launch is one the test recorded in the ledger.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class ArtifactLineageTests
{
    const string Head = "pos-11c0ffee0001", Member = "pos-11c0ffee0002";
    const string Team = "team-11c0ffee";
    const string Stamp = "2026-10-31T12:00:00.0000000Z";
    static readonly DateTimeOffset Due = DateTimeOffset.UtcNow.AddDays(7);

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static long Count(Database db, string table) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table}");
        return (long)c.ExecuteScalar()!;
    });

    /// <summary>A team under Research's division, headed by <see cref="Head"/>, with <see cref="Member"/> in it.</summary>
    static void Chart(Database db)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", Team), ("$p", OrgIds.Research), ("$at", Stamp));
        foreach (var p in new[] { Head, Member })
            Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
                ("$id", p), ("$u", Team), ("$h", $"org/{p}"), ("$at", Stamp));
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", Head), ("$t", Team));
    }

    /// <summary>A launch the test recorded, as the ledger would, with one record its packet showed it and one call it made.</summary>
    static string Launch(Database db, string role)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.AssuranceOf(SeatClasses.Harness) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        Exec(db, "INSERT INTO ai_attempt_input(attempt_id, kind, ref_id) VALUES($a, 'lateral', 'lat-000000000009')", ("$a", id));
        new ToolCallStore(db).Record(new ToolCallRow
        {
            At = DateTimeOffset.UtcNow, Attempt = id, Role = role, Tool = Ops.DataExport, Argument = "dataset 1 BTCUSDT 1m v1", Bytes = 42,
            Served = true
        });
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "lineage",
        RequestId = "lineage-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(Json.Write(r.Data)).RootElement;

    static StrategyVersionRow AVersion(string id, string? role, string? attempt) =>
        new(id, "x", "x", "{}", "test", ParseVerdict.Accepted, 0, DateTimeOffset.UtcNow, role, attempt);

    static StrategyRunRow ARun(string id, string version, long dataset, string? role, string? attempt) =>
        new(id, version, dataset, "sha", null, null, "fees=0", "COMPLETED", null, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null,
            "trace", DateTimeOffset.UtcNow, role, attempt);

    /// <summary>
    /// THE CONFORMANCE TEST: every table with an attempt column is either one of <see cref="ArtifactLineage.Kinds"/> — its author and
    /// attempt columns there and its trigger on it — or named in <see cref="ArtifactLineage.NotArtifacts"/> with why. A new table
    /// with an attempt column, or a kind whose trigger a later rung dropped, fails here. RED at base: no list, no trigger.
    /// </summary>
    [Fact]
    public void Every_table_with_an_attempt_column_is_an_artifact_with_its_trigger_or_is_named_as_not_one()
    {
        using var db = TestEnv.NewDb();
        var tables = db.Read(_ =>
        {
            using var c = db.Cmd("""
                SELECT m.name FROM sqlite_master m, pragma_table_info(m.name) p
                 WHERE m.type = 'table' AND p.name IN ('attempt', 'attempt_id') ORDER BY m.name
                """);
            using var r = c.ExecuteReader();
            var names = new List<string>();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        });
        Assert.NotEmpty(tables);
        var unclassified = tables.Where(t => ArtifactLineage.Kinds.All(k => k.Table != t) && !ArtifactLineage.NotArtifacts.ContainsKey(t)).ToList();
        Assert.True(unclassified.Count == 0, "tables with an attempt column that are neither an artifact kind nor named as not one: "
                                              + string.Join(", ", unclassified));
        var stale = ArtifactLineage.NotArtifacts.Keys.Where(t => !tables.Contains(t)).ToList();
        Assert.True(stale.Count == 0, "named as not artifacts but no table with an attempt column: " + string.Join(", ", stale));

        foreach (var k in ArtifactLineage.Kinds)
        {
            var columns = db.Read(_ =>
            {
                using var c = db.Cmd($"SELECT name FROM pragma_table_info('{k.Table}')");
                using var r = c.ExecuteReader();
                var names = new List<string>();
                while (r.Read()) names.Add(r.GetString(0));
                return names;
            });
            Assert.Contains(k.AuthorColumn, columns);
            Assert.Contains(k.AttemptColumn, columns);
            Assert.Equal(1L, Trigger(ArtifactLineage.TriggerName(k), k.Table));
        }
        Assert.Equal(1L, Trigger(ArtifactLineage.RevisionTrigger, "ledger_revision"));

        long Trigger(string name, string table) => db.Read(_ =>
        {
            using var c = db.Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name=$n AND tbl_name=$t", ("$n", name), ("$t", table));
            return (long)c.ExecuteScalar()!;
        });
    }

    /// <summary>
    /// THE DATABASE REFUSES AN ARTIFACT ROW THAT SKIPS EITHER, whichever writer tried: no author, an author that is no position, or
    /// no attempt (null or blank) while a position wrote it — each refused by the kind's trigger before any other constraint is
    /// read. A row naming both passes the trigger (and then meets the table's own NOT NULLs, which this partial insert leaves
    /// unfilled); the referee's run alone may name no attempt. RED at base: each insert reached the table's constraints instead.
    /// </summary>
    [Fact]
    public void The_database_refuses_an_artifact_row_with_no_author_position_or_no_attempt()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        string Insert(ArtifactKindSpec k, string? author, string? attempt)
        {
            var e = Assert.Throws<SqliteException>(() => Exec(db, $"INSERT INTO {k.Table}({k.AuthorColumn}, {k.AttemptColumn}) VALUES($a, $x)",
                ("$a", author), ("$x", attempt)));
            return e.Message;
        }

        foreach (var k in ArtifactLineage.Kinds)
        {
            Assert.Contains("lineage:", Insert(k, Member, null), StringComparison.Ordinal);
            Assert.Contains("lineage:", Insert(k, Member, "  "), StringComparison.Ordinal);
            Assert.Contains("lineage:", Insert(k, Member, "\t\n"), StringComparison.Ordinal);
            Assert.Contains("lineage:", Insert(k, null, "turn-x"), StringComparison.Ordinal);
            Assert.Contains("lineage:", Insert(k, "pos-nobody", "turn-x"), StringComparison.Ordinal);
            Assert.DoesNotContain("lineage:", Insert(k, Member, "turn-x"), StringComparison.Ordinal);
            Assert.Equal(0L, Count(db, k.Table));
        }

        // A REVISION IS NEW TEXT AND NAMES ITS OWN ATTEMPT, by its own trigger (its author is its entry's).
        foreach (var blank in new[] { null, "", "\t\r\n" })
            Assert.Contains("lineage:", Assert.Throws<SqliteException>(() => Exec(db,
                "INSERT INTO ledger_revision(entry_id, attempt) VALUES(1, $x)", ("$x", blank))).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("lineage:", Assert.Throws<SqliteException>(() => Exec(db,
            "INSERT INTO ledger_revision(entry_id, attempt) VALUES(1, 'turn-x')")).Message, StringComparison.Ordinal);

        var run = ArtifactLineage.Spec(ArtifactKind.Run);
        Assert.DoesNotContain("lineage:", Insert(run, AppPrincipals.Referee, null), StringComparison.Ordinal);
        Assert.Contains("lineage:", Insert(ArtifactLineage.Spec(ArtifactKind.Version), AppPrincipals.Referee, null), StringComparison.Ordinal);
    }

    /// <summary>
    /// EACH KIND'S WRITER REFUSES IN WORDS, AND WRITES NOTHING: a version, a run, a ledger entry, a decision, a forecast and a delivery
    /// with no attempt, each through its store; the referee's run, the app's own measurement, is kept with none. RED at base: every
    /// one of them was written with an empty attempt column.
    /// </summary>
    [Fact]
    public void Each_kinds_writer_refuses_an_artifact_with_no_attempt_and_writes_nothing()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var strategies = new StrategyStore(db);
        var org = new OrgStore(db);

        void Refused(Action write, string table)
        {
            var before = Count(db, table);
            var e = Assert.Throws<TradeAgentException>(write);
            Assert.Contains("attempt", e.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", e.Message, StringComparison.Ordinal);
            Assert.Equal(before, Count(db, table));
        }

        Refused(() => strategies.RecordVersion(AVersion("ver-none", Member, null)), "strategy_version");
        Refused(() => strategies.RecordVersion(AVersion("ver-blank", Member, "")), "strategy_version");
        var turn = Launch(db, Member);
        strategies.RecordVersion(AVersion("ver-ok", Member, turn));
        var set = BacktestRequestTests.GivenData(db);
        Refused(() => strategies.RecordRun(ARun("run-none", "ver-ok", set.Id, Member, null), []), "strategy_run");
        strategies.RecordRun(ARun("run-referee", "ver-ok", set.Id, AppPrincipals.Referee, null), []);
        Assert.Equal(AppPrincipals.Referee, strategies.RunById("run-referee")!.Role);

        Refused(() => new ResearchLedger(db).Add(Member, null, LedgerKind.Finding, "a finding", LedgerMark.Claim, null, null, null), "ledger_entry");
        var kept = new ResearchLedger(db).Add(Member, turn, LedgerKind.Finding, "a finding", LedgerMark.Claim, null, null, null);
        Refused(() => new ResearchLedger(db).Revise(Member, null, kept.Id, "a finding, restated", LedgerMark.Claim, "restated", null, null),
            "ledger_revision");
        Refused(() => org.Decide(Member, null, OrgDecisionKind.Routine, ["a", "b"], "a", "why", [], [], DateTimeOffset.UtcNow), "org_decision");
        Assert.Equal(1L, Count(db, "ledger_entry"));
        var decision = org.Decide(Member, turn, OrgDecisionKind.Routine, ["a", "b"], "a", "why", [], [], DateTimeOffset.UtcNow);
        Refused(() => org.Forecast(Member, null, decision.Id, new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-ok", "promoted", Due, 0.3m),
            DateTimeOffset.UtcNow), "org_forecast");

        var m = org.Assign(new MandateIssue(Head, null, Member, MandateKind.Task, "measure the skew", null, MandateShape.Report, false, Due, 5m, 3),
            DateTimeOffset.UtcNow);
        Refused(() => org.Deliver(m.Id, Member, null, "the skew predicts nothing", [], [], DateTimeOffset.UtcNow), "org_delivery");
    }

    /// <summary>
    /// A BACKTEST FROM A LAUNCH WITH NO ATTEMPT IS REFUSED BEFORE THE COMPUTE: the version and the run would have to record one, so the
    /// caller is told in words and no version, run or trial is written. RED at base: the run was computed and recorded attempt-less.
    /// </summary>
    [Fact]
    public async Task A_backtest_from_a_launch_with_no_attempt_is_refused_before_anything_runs()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        var set = BacktestRequestTests.GivenData(db);
        var program = BacktestRequestTests.GivenProgram(CouncilRoles.Research, name: "no-attempt.strategy");

        var e = Assert.Throws<GatewayDeniedException>(() =>
            gw.Backtests.Run(AgentContext.ForAgent(CouncilRoles.Research, CouncilRoles.Research, ""), new BacktestAsk(program, set.Id, Increment: 1m)));
        Assert.Contains("names no attempt", e.Message, StringComparison.Ordinal);
        Assert.Equal(0L, Count(db, "strategy_version"));
        Assert.Equal(0L, Count(db, "strategy_run"));
    }

    /// <summary>
    /// THROUGH THE REAL WRITERS EACH KIND RECORDS ITS AUTHOR AND ITS ATTEMPT, AND THE ATTEMPT REACHES WHAT IT READ: a backtest's version
    /// and run, a ledger entry, a decision with its forecast and a delivery, each made under a launch the ledger holds, read back by
    /// <see cref="ArtifactLineage.Of"/> as recorded — the position, the launch, the record its packet showed it and the call it made
    /// through TradeAgent. The delivery names its author on the row and in the manifest its id seals. RED at base: no delivery
    /// author and no lineage reader.
    /// </summary>
    [Fact]
    public async Task Through_the_real_writers_each_kind_records_its_author_and_the_attempt_that_reaches_its_reads()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        Chart(db);
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());

        void Recorded(string kind, string id, string author, string attempt)
        {
            var l = ArtifactLineage.Of(db, kind, id);
            Assert.NotNull(l);
            Assert.True(l.State == LineageState.Recorded, $"{kind} {id}: {l.State} — {l.Label}");
            Assert.Equal((author, attempt), (l.Author, l.Attempt));
            Assert.Equal([new AttemptGiven("lateral", "lat-000000000009")], l.Given);
            Assert.True(l.PacketRecorded);
            Assert.Equal(Ops.DataExport, Assert.Single(l.Read).Tool);
        }

        // A version and a run, through the backtest the position asked for under its launch.
        var research = Launch(db, CouncilRoles.Research);
        var set = BacktestRequestTests.GivenData(db);
        var ran = gw.Backtests.Run(AgentContext.ForAgent(CouncilRoles.Research, CouncilRoles.Research, research),
            new BacktestAsk(BacktestRequestTests.GivenProgram(CouncilRoles.Research, name: "lineage.strategy"), set.Id, Increment: 1m));
        Recorded(ArtifactKind.Version, ran.Program.StrategyId, ran.Role, research);
        Recorded(ArtifactKind.Run, ran.Result.RunId, ran.Role, research);

        // A ledger entry, through ledger-add.
        var entryTurn = Launch(db, Member);
        var added = await server.CallAsync(Frame(Ops.LedgerAdd, ("kind", LedgerKind.Finding), ("text", "the skew leads"), ("mark", "claim")),
            Member, entryTurn);
        Assert.True(added.Ok, Json.Write(added.Error));
        var entry = Body(added).GetProperty("entry").GetInt64();
        Recorded(ArtifactKind.LedgerEntry, entry.ToString(CultureInfo.InvariantCulture), Member, entryTurn);

        // A decision and its forecast, through decision-record.
        var decideTurn = Launch(db, Member);
        var decided = await server.CallAsync(Frame(Ops.DecisionRecord, ("options", new[] { "a", "b" }), ("choice", "a"), ("rationale", "why"),
            ("forecasts", new object[] { new { predicate = ForecastVocabulary.VerdictClass, subject = ran.Program.StrategyId, target = "promoted",
                by = Due.ToString("O"), probability = 0.3m } })), Member, decideTurn);
        Assert.True(decided.Ok, Json.Write(decided.Error));
        var decision = Body(decided).GetProperty("decision").GetString()!;
        Recorded(ArtifactKind.Decision, decision, Member, decideTurn);
        Recorded(ArtifactKind.Forecast, Assert.Single(gw.Org.ForecastsOn(decision)).Id, Member, decideTurn);

        // A delivery, through mandate-deliver: its author is the holder, on the row and sealed into the manifest.
        var m = gw.Org.Assign(new MandateIssue(Head, null, Member, MandateKind.Task, "measure the skew", null, MandateShape.Report, false, Due, 5m, 3),
            DateTimeOffset.UtcNow);
        var deliverTurn = Launch(db, Member);
        var delivered = await server.CallAsync(Frame(Ops.MandateDeliver, ("mandate", m.Id), ("report", "the skew predicts nothing")), Member, deliverTurn);
        Assert.True(delivered.Ok, Json.Write(delivered.Error));
        var d = Assert.Single(gw.Org.Deliveries(m.Id));
        Assert.Equal(Member, d.Author);
        Assert.Equal(Member, JsonDocument.Parse(gw.Org.Manifest(d.Id)!).RootElement.GetProperty("author").GetString());
        Recorded(ArtifactKind.Delivery, d.Id, Member, deliverTurn);

        // AN EMPTY PACKET RECORD IS NEVER A ZERO: in flight it says the record comes with the commit; ended, it says unknown which.
        var bare = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = bare, StartedAt = DateTimeOffset.UtcNow, Role = Member, ReservedCost = 0.10m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.AssuranceOf(SeatClasses.Harness) is { } a ? SeatClasses.Name(a) : null
        }).Admitted);
        var lone = new ResearchLedger(db).Add(Member, bare, LedgerKind.Finding, "a lone finding", LedgerMark.Claim, null, null, null).Id
            .ToString(CultureInfo.InvariantCulture);
        var inFlight = ArtifactLineage.Of(db, ArtifactKind.LedgerEntry, lone)!;
        Assert.Equal((LineageState.Recorded, false), (inFlight.State, inFlight.PacketRecorded));
        Assert.Contains("has not committed", inFlight.Label, StringComparison.Ordinal);
        Exec(db, "UPDATE ai_attempt SET state='ENDED' WHERE id=$a", ("$a", bare));
        var ended = ArtifactLineage.Of(db, ArtifactKind.LedgerEntry, lone)!;
        Assert.False(ended.PacketRecorded);
        Assert.Contains("unknown which, never read as nothing shown", ended.Label, StringComparison.Ordinal);

        // The referee's run is the app's measurement, labelled so — not "not recorded".
        new StrategyStore(db).RecordRun(ARun("run-referee", ran.Program.StrategyId, set.Id, AppPrincipals.Referee, null), []);
        Assert.Equal(LineageState.AppMeasured, ArtifactLineage.Of(db, ArtifactKind.Run, "run-referee")!.State);
        Assert.Null(ArtifactLineage.Of(db, ArtifactKind.Run, "run-nothing"));
    }

    /// <summary>
    /// A ROW WRITTEN BEFORE THE RUNG STAYS AS IT WAS AND IS LABELLED: a home at rung 52 holding an attempt-less ledger entry and a
    /// delivery with no author reopens through rung 53 — the triggers made, the rows untouched (nothing backfilled), and each read
    /// as not recorded, the label naming the rung; an attempt that names no launch reads as dangling.
    /// </summary>
    [Fact]
    public void A_row_written_before_the_rung_stays_as_it_was_and_reads_not_recorded()
    {
        var path = Path.Combine(TestEnv.Home, $"db-{Guid.NewGuid():n}.db");
        using (var db = new Database(path))
        {
            Chart(db);
            // THE HOME AS RUNG 52 LEFT IT: no lineage triggers, and an entry written without an attempt.
            foreach (var k in ArtifactLineage.Kinds) Exec(db, $"DROP TRIGGER {ArtifactLineage.TriggerName(k)}");
            Exec(db, "INSERT INTO ledger_entry(kind, author, about, source, created_at, attempt) VALUES('finding', $a, NULL, NULL, $at, NULL)",
                ("$a", Member), ("$at", Stamp));
            Exec(db, "INSERT INTO ledger_entry(kind, author, about, source, created_at, attempt) VALUES('finding', $a, NULL, NULL, $at, 'turn-gone')",
                ("$a", Member), ("$at", Stamp));
            Exec(db, $"UPDATE meta SET value='{Versions.ArtifactLineageRung - 1}' WHERE key='schema_version'");
        }

        using var reopened = new Database(path);
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            reopened.Read(_ => { using var c = reopened.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
        Assert.Contains("lineage:", Assert.Throws<SqliteException>(() => Exec(reopened,
            "INSERT INTO ledger_entry(kind, author, created_at, attempt) VALUES('finding', $a, $at, NULL)", ("$a", Member), ("$at", Stamp))).Message,
            StringComparison.Ordinal);

        var old = ArtifactLineage.Of(reopened, ArtifactKind.LedgerEntry, "1")!;
        Assert.Equal((LineageState.NotRecorded, Member, (string?)null), (old.State, old.Author, old.Attempt));
        Assert.Contains($"before schema rung {Versions.ArtifactLineageRung}", old.Label, StringComparison.Ordinal);
        Assert.Contains("never inferred", old.Label, StringComparison.Ordinal);
        Assert.Equal(LineageState.Dangling, ArtifactLineage.Of(reopened, ArtifactKind.LedgerEntry, "2")!.State);
    }
}
