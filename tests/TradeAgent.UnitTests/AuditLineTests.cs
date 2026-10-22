using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Audit;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PUBLIC AUDIT LINE, PART A (<c>U-org-audit-a</c>; ORCHESTRATION § 2.13, ORGANISATION §§ 1–4 and 13, VISION § 5.3): an app
/// principal outside the chart, its own file and ladder, a read-only reader of approved evidence only, and salted samples of accepted
/// deliveries re-checked by code — no model, no money path, no rung of the app's database. NOTHING HERE REACHES A REAL MODEL: every
/// position is written by the test with raw SQL and every launch id is one the test seeded.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public partial class AuditLineTests(ITestOutputHelper log)
{
    /// <summary>A team head under Research's division and a member of that team.</summary>
    const string Head = "pos-a0d17000001", Member = "pos-a0d17000002";

    const string Team = "team-a0d17000";

    /// <summary>The audit line's principal, spelled here independently of the build's constant.</summary>
    const string Audit = "audit";

    const string Stamp = "2026-10-22T12:00:00.0000000Z";

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void Seat(Database db, string id, string unit) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$at", Stamp));

    /// <summary>The chart every test starts from: <see cref="Head"/> heads <see cref="Team"/> with <see cref="Member"/> in it.</summary>
    static void Chart(Database db)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", Team), ("$p", OrgIds.Research), ("$at", Stamp));
        Seat(db, Head, Team);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", Head), ("$t", Team));
        Seat(db, Member, Team);
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "audit-line",
        RequestId = "audit-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    // ---- (a) the identity ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) THE AUDIT PRINCIPAL IS REFUSED WHEREVER A POSITION IS NAMED. Even on a chart that holds a row under its id — as a build
    /// before the rule could have minted — it is never a position: the minting check refuses it as an app principal; a mandate to it
    /// and a note to it are refused naming it one; a decision under it and a note from it are refused as no active position; the
    /// gateway's one helper answers no position for it, so an organisation verb under a grant in its name is refused on the read loop
    /// and through the door. RED at base: <c>PositionIdRefusal("audit")</c> was null — the id could be minted.
    /// </summary>
    [Fact]
    public async Task The_audit_principal_is_refused_wherever_a_position_is_named()
    {
        var minted = OrgIds.PositionIdRefusal(Audit);
        log.WriteLine($"mint: {minted ?? "null — the id could be minted"}");
        Assert.Contains("app's own principals", minted ?? "", StringComparison.Ordinal);
        Assert.Contains(Audit, AppPrincipals.All);
        Assert.True(OrgStore.IsAppPrincipal(Audit));

        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        Chart(db);
        Seat(db, Audit, Team);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;

        var assign = org.AssignRefusal(new MandateIssue(Head, null, Audit, MandateKind.Task, "audit this", null,
            MandateShape.Report, false, now.AddDays(1), 1m, 1), now);
        log.WriteLine($"assign: {assign}");
        Assert.Contains("app's own principals", assign ?? "", StringComparison.Ordinal);

        var to = Assert.Throws<TradeAgentException>(() => org.SendLateral(Head, null, Audit, "a note", [], now));
        log.WriteLine($"note to: {to.Message}");
        Assert.Contains("app's own principals", to.Message, StringComparison.Ordinal);
        var from = Assert.Throws<TradeAgentException>(() => org.SendLateral(Audit, null, Member, "a note", [], now));
        Assert.Contains("no active position", from.Message, StringComparison.Ordinal);
        var decided = Assert.Throws<TradeAgentException>(() => org.Decide(Audit, null, OrgDecisionKind.Routine,
            ["a", "b"], "a", "because", [], [], now));
        Assert.Contains("no active position", decided.Message, StringComparison.Ordinal);
        Assert.Equal(0L, db.Read(_ =>
        {
            using var c = db.Cmd("SELECT (SELECT COUNT(*) FROM org_lateral) + (SELECT COUNT(*) FROM org_decision) + (SELECT COUNT(*) FROM org_mandate)");
            return (long)c.ExecuteScalar()!;
        }));

        Assert.Null(gw.ActivePositionOf(Audit));
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var loop = await server.CallAsync(Frame(Ops.MandateList), Audit, "turn-audit-1");
        Assert.False(loop.Ok);
        Assert.Contains("an app principal", loop.Error!.Message, StringComparison.Ordinal);
        var grant = new AgentGrants().IssueHere(Audit, "turn-audit-2");
        var door = await server.CallAsGrantAsync(Frame(Ops.MandateList), grant);
        Assert.False(door.Ok);
        log.WriteLine($"door: {door.Error!.Message}");
    }

    // ---- (b) the line's file -----------------------------------------------------------------------------------------------

    static string NewFile() => Path.Combine(TestEnv.Home, $"audit-{Guid.NewGuid():n}.db");

    static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    static AuditSweepWrite OneSample(string delivery, long cursor) => new(
        [new AuditSample(delivery, "man-x", "div-research", cursor, Now)],
        [new AuditCheck(delivery, "seal", false, "the manifest's SHA-256 is not the delivery's id")],
        [new AuditFinding(AuditFinding.IdOf(delivery, "seal"), delivery, "man-x", "att-x", "seal", AuditSeverity.Severe, Now)],
        [new AuditFlag(delivery, "man-x", "att-x", "dormant", Now)],
        cursor);

    /// <summary>
    /// (b) THE LINE'S FILE IS ITS OWN APPEND-ONLY LADDER AND REFUSES A NEWER BUILD: <c>state/audit.db</c> is this class's constant, not one
    /// of <see cref="Paths"/>; rung 1 stamps <c>schema</c> = 1 in its own meta table with a 32-byte salt written once; every table but the
    /// version row refuses UPDATE and DELETE; reopened, nothing climbs twice and the salt is the same; a file a newer build wrote is refused
    /// naming both versions and left byte-for-byte as found. RED at base: no <c>AuditStore</c> existed — the line had no file.
    /// </summary>
    [Fact]
    public void The_line_file_is_its_own_append_only_ladder_and_refuses_a_newer_build()
    {
        Assert.Equal(Path.Combine(Paths.State, "audit.db"), AuditStore.DefaultFile);
        Assert.DoesNotContain(typeof(Paths).GetProperties(), p => p.GetValue(null) is string s && s.EndsWith("audit.db", StringComparison.Ordinal));

        var file = NewFile();
        byte[] salt;
        using (var store = new AuditStore(file, Now))
        {
            salt = store.Salt();
            Assert.Equal(AuditStore.SaltBytes, salt.Length);
            Assert.Single(store.Commit(OneSample("dlv-1", 7), Now));
            Assert.Empty(store.Commit(OneSample("dlv-1", 9), Now));
            Assert.Equal(9, store.Cursor());
            store.RecordFailure(Now, "the database could not be read");
        }
        using (var again = new AuditStore(file, Now.AddDays(1)))
        {
            Assert.Equal(salt, again.Salt());
            Assert.Single(again.Samples());
            Assert.Single(again.Checks());
            Assert.Single(again.Findings());
            Assert.Single(again.Flags());
            Assert.Equal(9, again.Cursor());
            Assert.Single(again.Failures());
        }
        using (var other = new AuditStore(NewFile(), Now)) Assert.NotEqual(salt, other.Salt());

        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            Assert.Equal("1", Scalar(c, "SELECT value FROM audit_meta WHERE key='schema'"));
            foreach (var edit in new[]
                     {
                         "UPDATE audit_salt SET at='x'", "DELETE FROM audit_salt",
                         "UPDATE audit_cursor SET last_id=0", "DELETE FROM audit_cursor",
                         "UPDATE audit_sample SET unit_id='x'", "DELETE FROM audit_sample",
                         "UPDATE audit_check SET passed=1", "DELETE FROM audit_check",
                         "UPDATE audit_finding SET severity='info'", "DELETE FROM audit_finding",
                         "UPDATE audit_flag SET what='x'", "DELETE FROM audit_flag",
                         "UPDATE audit_failure SET what='x'", "DELETE FROM audit_failure",
                     })
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = edit;
                var refused = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
                log.WriteLine($"{edit}: {refused.Message}");
            }
        }

        // A NEWER BUILD'S FILE: refused, both versions named, untouched.
        var newer = NewFile();
        using (var c = new SqliteConnection($"Data Source={newer};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE audit_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO audit_meta VALUES('schema', '7');
                CREATE TABLE audit_sample(x TEXT);
                """;
            cmd.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(newer);
        var refusedNewer = Assert.Throws<InvalidOperationException>(() => new AuditStore(newer, Now));
        log.WriteLine(refusedNewer.Message);
        Assert.Contains("version 7", refusedNewer.Message, StringComparison.Ordinal);
        Assert.Contains($"understands {AuditStore.Schema}", refusedNewer.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing has been read from it or written to it", refusedNewer.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(newer));
        Assert.False(File.Exists(newer + "-wal") && new FileInfo(newer + "-wal").Length > 0, "a refused file grew a write-ahead log");
    }

    /// <summary>
    /// A CRASH BETWEEN RUNG ONE'S STATEMENTS AND ITS STAMP REOPENS: every table, trigger, row and the salt in place, the stamp gone — the
    /// file opens, climbs the rung again without an error, stamps 1, and keeps the salt and every row. RED at base: no <c>AuditStore</c>.
    /// </summary>
    [Fact]
    public void A_crash_between_rung_one_and_its_stamp_reopens()
    {
        var file = NewFile();
        byte[] salt;
        using (var store = new AuditStore(file, Now))
        {
            salt = store.Salt();
            store.Commit(OneSample("dlv-1", 3), Now);
        }
        // A CRASH BETWEEN THE RUNG'S STATEMENTS AND ITS STAMP: every table and row in place, the stamp one lower.
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM audit_meta WHERE key='schema'";
            cmd.ExecuteNonQuery();
        }
        using (var again = new AuditStore(file, Now.AddDays(1)))
        {
            Assert.Equal(salt, again.Salt());
            Assert.Single(again.Samples());
            Assert.Equal(3, again.Cursor());
        }
        using var check = new SqliteConnection($"Data Source={file};Pooling=False");
        check.Open();
        Assert.Equal("1", Scalar(check, "SELECT value FROM audit_meta WHERE key='schema'"));
        Assert.Equal("1", Scalar(check, "SELECT COUNT(*) FROM audit_salt"));
    }

    // ---- (c) the register ------------------------------------------------------------------------------------------------

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    /// <summary>
    /// (c) THE READER READS ONLY THE REGISTER AND CANNOT WRITE. Through it, a registered table answers; a table outside the register — the
    /// tool-call ledger, the engineering log, the wakes — is refused by the engine before it runs, as is every write, schema change, pragma
    /// and attach; nothing it read is outside the register; and the database is byte-for-byte as it was. No source of
    /// <c>TradeAgent.Core.Audit</c> names the seat homes, the workspaces, the agent home, the tool-call ledger or the watcher's namespace or
    /// file. RED at base: no <c>AuditEvidence</c> existed.
    /// </summary>
    [Fact]
    public void The_reader_reads_only_the_register_and_cannot_write()
    {
        string file;
        using (var db = TestEnv.NewDb()) file = db.FilePath;
        var before = File.ReadAllBytes(file);

        using (var evidence = new AuditEvidence(file))
        {
            foreach (var table in AuditEvidence.Tables)
                Assert.NotNull(evidence.InOneRead(() => evidence.Rows($"SELECT COUNT(*) FROM {table}")));
            Assert.Equal(AuditEvidence.Tables.Order(StringComparer.Ordinal), evidence.TablesRead.Order(StringComparer.Ordinal));

            foreach (var refused in new[]
                     {
                         "SELECT * FROM tool_call", "SELECT COUNT(*) FROM tool_call", "SELECT argument FROM tool_call",
                         "SELECT * FROM engineering_log", "SELECT * FROM mission_event", "SELECT * FROM meta",
                         "SELECT d.id FROM org_delivery d JOIN tool_call t ON t.id = d.id",
                         "INSERT INTO org_mandate_event(mandate_id, at, kind, actor) VALUES('man-x', 'x', 'accepted', 'x')",
                         "UPDATE org_delivery SET report='x'", "DELETE FROM org_check", "CREATE TABLE audit_here(x)",
                         "DROP TRIGGER org_delivery_sealed_u", "PRAGMA query_only=OFF", $"ATTACH DATABASE '{file}-other' AS other",
                     })
            {
                var thrown = Assert.Throws<SqliteException>(() => evidence.Rows(refused));
                log.WriteLine($"{refused}: {thrown.Message}");
            }
            Assert.All(evidence.TablesRead, t => Assert.Contains(t, AuditEvidence.Tables));
            Assert.DoesNotContain("tool_call", evidence.TablesRead);
        }
        Assert.Equal(before, File.ReadAllBytes(file));

        var root = RepoRoot();
        var sources = Directory.GetFiles(Path.Combine(root, "src", "TradeAgent.Core", "Audit"), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);
        string[] named = ["Paths.SeatHome", "Paths.Workspace", "Paths.AgentHome", "Paths.RoleHome", "Paths.Inbox", "tool_call", "Oversight", "oversight"];
        var naming = sources.SelectMany(f => named.Where(n => File.ReadAllText(f).Contains(n, StringComparison.Ordinal))
            .Select(n => $"{Path.GetFileName(f)} names {n}")).ToList();
        Assert.True(naming.Count == 0, "the audit line names what it may never read: " + string.Join(", ", naming));
    }

    // ---- deliveries, as the app seals and judges them ----------------------------------------------------------------------

    /// <summary>A mandate <see cref="Head"/> issues to <see cref="Member"/>: a task whose output is a report, with open latitude.</summary>
    static MandateRow Issued(OrgStore org, DateTimeOffset now) =>
        org.Assign(new MandateIssue(Head, null, Member, MandateKind.Task, "find out whether the funding skew predicts the next hour", null,
            MandateShape.Report, false, now.AddDays(7), 5m, MandateLimits.MaxRetryLimit), now);

    /// <summary>A delivery the member sealed and the head accepted: its id.</summary>
    static string Accepted(OrgStore org, DateTimeOffset now, string report = "the answer", IReadOnlyList<MandateDeviation>? deviations = null,
        IReadOnlyList<MandateRef>? refs = null, string? attempt = null)
    {
        var m = Issued(org, now);
        var d = org.Deliver(m.Id, Member, attempt, report, deviations ?? [], refs ?? [], now).Delivery.Id;
        org.Accept(d, Head, null, now);
        return d;
    }

    /// <summary>A delivery the head rejected, stating a defect: its id.</summary>
    static string Rejected(OrgStore org, DateTimeOffset now)
    {
        var m = Issued(org, now);
        var d = org.Deliver(m.Id, Member, null, "a rejected answer", [], [], now).Delivery.Id;
        org.Reject(d, Head, null, null, "it answers another question", now);
        return d;
    }

    /// <summary>A delivery nobody has judged: its id.</summary>
    static string Unjudged(OrgStore org, DateTimeOffset now)
    {
        var m = Issued(org, now);
        return org.Deliver(m.Id, Member, null, "an unjudged answer", [], [], now).Delivery.Id;
    }

    static long Count(string file, string table)
    {
        using var c = OversightSeed.Open(file);
        using var q = c.CreateCommand();
        q.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)q.ExecuteScalar()!;
    }

    // ---- (e) the draw ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// (e) THE DRAW IS SALTED, IDEMPOTENT AND SURVIVES A RESTART. The draw is a function of the salt and the delivery's id: the same salt
    /// draws the same ids, another salt draws differently, and over many ids about one in four is drawn at the policy rate. In a home: at
    /// rate 1 every accepted delivery is drawn and no rejected or unjudged one; a second sweep draws nothing; a restart (the file reopened)
    /// draws nothing; and at the policy rate exactly the deliveries the file's salt predicts are drawn. RED at base: no sampler existed.
    /// </summary>
    [Fact]
    public void The_draw_is_salted_idempotent_and_survives_a_restart()
    {
        Assert.Equal(0.25, AuditPolicy.SampleRate);
        var ids = Enumerable.Range(0, 2000).Select(i => Sha256Hex.Of("delivery " + i)).ToList();
        var one = System.Security.Cryptography.RandomNumberGenerator.GetBytes(AuditStore.SaltBytes);
        var two = System.Security.Cryptography.RandomNumberGenerator.GetBytes(AuditStore.SaltBytes);
        var byOne = ids.Where(id => AuditSampler.Draws(one, id, AuditPolicy.SampleRate)).ToList();
        Assert.Equal(byOne, ids.Where(id => AuditSampler.Draws(one, id, AuditPolicy.SampleRate)));
        var byTwo = ids.Where(id => AuditSampler.Draws(two, id, AuditPolicy.SampleRate)).ToList();
        Assert.NotEqual(byOne, byTwo);
        log.WriteLine($"drawn at 1/4: {byOne.Count} and {byTwo.Count} of {ids.Count}; {byOne.Intersect(byTwo).Count()} in both");
        Assert.InRange(byOne.Count, 400, 600);
        Assert.InRange(byTwo.Count, 400, 600);
        Assert.All(ids, id => Assert.True(AuditSampler.Draws(one, id, 1)));
        Assert.All(ids, id => Assert.False(AuditSampler.Draws(one, id, 0)));

        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var accepted = Enumerable.Range(0, 8).Select(i => Accepted(org, now, $"answer {i}")).ToList();
        var rejected = Rejected(org, now);
        var unjudged = Unjudged(org, now);

        var file = NewFile();
        using (var store = new AuditStore(file, now))
        {
            var first = new AuditSweep(db.FilePath, store, rate: 1).Run(now);
            Assert.Null(first.Failure);
            Assert.Equal(accepted.Order(StringComparer.Ordinal), first.Drawn.Select(s => s.DeliveryId).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(first.Drawn, s => s.DeliveryId == rejected || s.DeliveryId == unjudged);
            Assert.All(first.Drawn, s => Assert.Equal(Team, s.UnitId));
            Assert.Empty(new AuditSweep(db.FilePath, store, rate: 1).Run(now.AddMinutes(1)).Drawn);
        }
        using (var restarted = new AuditStore(file, now.AddHours(1)))
        {
            Assert.Empty(new AuditSweep(db.FilePath, restarted, rate: 1).Run(now.AddHours(1)).Drawn);
            Assert.Equal(accepted.Count, restarted.Samples().Count);
        }

        // AT THE POLICY RATE, THE FILE'S OWN SALT DECIDES — and a restart draws the same.
        var policyFile = NewFile();
        using (var store = new AuditStore(policyFile, now))
        {
            var predicted = accepted.Where(d => AuditSampler.Draws(store.Salt(), d, AuditPolicy.SampleRate)).Order(StringComparer.Ordinal).ToList();
            var drawn = new AuditSweep(db.FilePath, store).Run(now);
            Assert.Equal(predicted, drawn.Drawn.Select(s => s.DeliveryId).Order(StringComparer.Ordinal));
        }
        using (var again = new AuditStore(policyFile, now))
            Assert.Empty(new AuditSweep(db.FilePath, again).Run(now).Drawn);
    }
}
