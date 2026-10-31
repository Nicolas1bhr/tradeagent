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
        var decided = Assert.Throws<TradeAgentException>(() => org.Decide(Audit, "turn-test", OrgDecisionKind.Routine,
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
                         "UPDATE audit_check SET outcome='passed'", "DELETE FROM audit_check",
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
        IReadOnlyList<MandateRef>? refs = null, string? attempt = "turn-test")
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
        var d = org.Deliver(m.Id, Member, "turn-test", "a rejected answer", [], [], now).Delivery.Id;
        org.Reject(d, Head, null, null, "it answers another question", now);
        return d;
    }

    /// <summary>A delivery nobody has judged: its id.</summary>
    static string Unjudged(OrgStore org, DateTimeOffset now)
    {
        var m = Issued(org, now);
        return org.Deliver(m.Id, Member, "turn-test", "an unjudged answer", [], [], now).Delivery.Id;
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

    // ---- (d) the seal ----------------------------------------------------------------------------------------------------

    /// <summary>Every text value in the audit file, every table — what a test searches for words the line must never copy in.</summary>
    static string AllText(string file)
    {
        using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        c.Open();
        var tables = new List<string>();
        using (var t = c.CreateCommand())
        {
            t.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var r = t.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        var all = new System.Text.StringBuilder();
        foreach (var table in tables)
        {
            using var q = c.CreateCommand();
            q.CommandText = $"SELECT * FROM \"{table}\"";
            using var r = q.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    if (r.GetValue(i) is string text) all.Append(text).Append('\n');
        }
        return all.ToString();
    }

    /// <summary>
    /// (d) A DELIVERY REWRITTEN PAST ITS TRIGGERS IS A SEVERE FINDING. An unconfined shell can drop a trigger: with the seal's update trigger
    /// dropped, one accepted delivery has its report AND its manifest's report rewritten together — columns and manifest agree, and only the
    /// re-hash sees it — and another its report column alone. Swept at rate 1, each is a <c>severe</c> <c>seal</c> finding citing the
    /// delivery, mandate and attempt by id; an untouched one passes; and the rewritten words are nowhere in the audit file. RED at base: no
    /// seal check existed. THE WATCHED MUTANT: the seal comparing the stored id with itself (the re-hash skipped) lets the first through.
    /// </summary>
    [Fact]
    public void A_delivery_rewritten_past_its_triggers_is_a_severe_finding()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var whole = Accepted(org, now, "the honest answer", attempt: "turn-honest-1");
        var rehashed = Accepted(org, now, "the first answer", attempt: "turn-first-1");
        var column = Accepted(org, now, "the second answer", attempt: "turn-second-1");
        var authored = Accepted(org, now, "the third answer", attempt: "turn-third-1");
        const string forged = "a better answer written later";
        using (var c = OversightSeed.Open(db.FilePath))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                DROP TRIGGER org_delivery_sealed_u;
                UPDATE org_delivery SET report = $forged, manifest = json_set(manifest, '$.report', $forged) WHERE id = $rehashed;
                UPDATE org_delivery SET report = $forged WHERE id = $column;
                UPDATE org_delivery SET author = $head WHERE id = $authored;
                """;
            cmd.Parameters.AddWithValue("$head", Head);
            cmd.Parameters.AddWithValue("$authored", authored);
            cmd.Parameters.AddWithValue("$forged", forged);
            cmd.Parameters.AddWithValue("$rehashed", rehashed);
            cmd.Parameters.AddWithValue("$column", column);
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(forged, org.Delivery(rehashed)!.Report);

        var file = NewFile();
        using var store = new AuditStore(file, now);
        var swept = new AuditSweep(db.FilePath, store, rate: 1).Run(now);
        Assert.Null(swept.Failure);
        Assert.Equal(4, swept.Drawn.Count);
        foreach (var k in store.Checks().Where(k => k.Name == AuditCheckName.Seal)) log.WriteLine($"{k.DeliveryId[..12]} seal {k.Passed}: {k.Reason}");

        Assert.True(Assert.Single(store.Checks(whole), k => k.Name == AuditCheckName.Seal).Passed);
        // THE AUTHOR IS SEALED TOO (U-lineage-check): a delivery whose author column was rewritten fails the seal, naming the author.
        Assert.Contains("author", Assert.Single(store.Checks(authored), k => k.Name == AuditCheckName.Seal && !k.Passed).Reason, StringComparison.Ordinal);
        foreach (var (rewritten, attempt) in new[] { (rehashed, "turn-first-1"), (column, "turn-second-1"), (authored, "turn-third-1") })
        {
            Assert.False(Assert.Single(store.Checks(rewritten), k => k.Name == AuditCheckName.Seal).Passed);
            var finding = Assert.Single(store.Findings(), f => f.DeliveryId == rewritten && f.Check == AuditCheckName.Seal);
            Assert.Equal(AuditSeverity.Severe, finding.Severity);
            Assert.Equal((org.Delivery(rewritten)!.MandateId, attempt), (finding.MandateId, finding.AttemptId));
        }
        Assert.DoesNotContain(store.Findings(), f => f.DeliveryId == whole);
        var text = AllText(file);
        foreach (var words in new[] { forged, "the honest answer", "the first answer", "the second answer" })
            Assert.DoesNotContain(words, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A COLUMN OR AN EVENT REWRITTEN OUT OF SHAPE IS FOUND AND NEVER STOPS THE SWEEP. Past dropped triggers (and, for a check result, ignored
    /// CHECK constraints): one accepted delivery's refs and deviations become JSON TradeAgent never writes; one's report becomes a BLOB; one's
    /// refs become <c>[null]</c>; one's check result a word that is no number; one's manifest names a key twice and is re-sealed under the
    /// hash of that, its acceptance rewritten to name the new id; one acceptance's evidence names no delivery and another's is a BLOB. The
    /// sweep never fails: every rewritten delivery is drawn and FAILS CLOSED into a severe finding citing it; each unreadable acceptance is the
    /// line's own failure, in its file; the cursor moves past them all and the delivery accepted after them is still drawn, with no finding.
    /// RED before fix pass 1 (the review's defect 1): an InvalidCast, NullReference, Format or Argument exception escaped one delivery's
    /// checks, the sweep recorded a failure and its cursor never moved again — every later acceptance unaudited.
    /// </summary>
    [Fact]
    public void A_column_or_an_event_rewritten_out_of_shape_is_found_and_never_stops_the_sweep()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var bent = Accepted(org, now, "bent");
        var lost = Accepted(org, now, "lost");
        var blob = Accepted(org, now, "blob");
        var nulled = Accepted(org, now, "nulled");
        var unnumbered = Accepted(org, now, "unnumbered");
        var doubled = Accepted(org, now, "doubled");
        var blobEvent = Accepted(org, now, "blob event");
        var later = Accepted(org, now, "later");
        var twice = "{\"report\":\"one\"," + org.Manifest(doubled)![1..];
        var resealed = Sha256Hex.Of(twice);
        using (var c = OversightSeed.Open(db.FilePath))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                DROP TRIGGER org_delivery_sealed_u;
                DROP TRIGGER org_mandate_event_sealed_u;
                DROP TRIGGER org_check_sealed_u;
                UPDATE org_delivery SET refs = '["run:x"]', deviations = 'not json' WHERE id = $bent;
                UPDATE org_mandate_event SET evidence = '{"not":"a list"}' WHERE kind = 'accepted' AND evidence LIKE '%' || $lost || '%';
                UPDATE org_delivery SET report = CAST('blob' AS BLOB) WHERE id = $blob;
                UPDATE org_delivery SET refs = '[null]' WHERE id = $nulled;
                UPDATE org_check SET passed = 'yes' WHERE delivery_id = $unnumbered;
                UPDATE org_delivery SET id = $resealed, manifest = $twice WHERE id = $doubled;
                UPDATE org_mandate_event SET evidence = json_array($resealed) WHERE kind = 'accepted' AND evidence LIKE '%' || $doubled || '%';
                UPDATE org_mandate_event SET evidence = X'00' WHERE kind = 'accepted' AND evidence LIKE '%' || $blobEvent || '%';
                """;
            foreach (var (k, v) in new[] { ("$bent", bent), ("$lost", lost), ("$blob", blob), ("$nulled", nulled), ("$unnumbered", unnumbered),
                         ("$doubled", doubled), ("$resealed", resealed), ("$twice", twice), ("$blobEvent", blobEvent) })
                cmd.Parameters.AddWithValue(k, v);
            cmd.ExecuteNonQuery();
        }

        using var store = new AuditStore(NewFile(), now);
        var swept = new AuditSweep(db.FilePath, store, rate: 1).Run(now);
        Assert.Null(swept.Failure);
        Assert.Equal([bent, blob, nulled, unnumbered, resealed, later], swept.Drawn.Select(s => s.DeliveryId));
        foreach (var k in store.Checks()) log.WriteLine($"{k.DeliveryId[..12]} {k.Name} {k.Passed}: {k.Reason}");
        Assert.All(store.Checks(bent), k => Assert.False(k.Passed));
        Assert.Equal(AuditSeverity.Severe, Assert.Single(store.Findings(), f => f.DeliveryId == bent && f.Check == AuditCheckName.Seal).Severity);
        foreach (var rewritten in new[] { blob, nulled, unnumbered, resealed })
        {
            var cited = store.Findings().Where(f => f.DeliveryId == rewritten).ToList();
            Assert.Contains(cited, f => f.Severity == AuditSeverity.Severe);
            Assert.All(cited, f => Assert.Equal(store.Samples().Single(s => s.DeliveryId == rewritten).MandateId, f.MandateId));
        }
        Assert.DoesNotContain(store.Findings(), f => f.DeliveryId == later);
        Assert.Equal(store.Samples().Max(s => s.EventId), store.Cursor());
        var failures = store.Failures().Select(f => f.What).ToList();
        foreach (var f in failures) log.WriteLine(f);
        Assert.Equal(2, failures.Count);
        Assert.All(failures, f => Assert.Contains("names no delivery TradeAgent can read", f, StringComparison.Ordinal));
        var text = AllText(store.File);
        foreach (var words in new[] { "not json", "blob", "yes", "one" })
            Assert.DoesNotContain($"'{words}'", text, StringComparison.Ordinal);
    }

    /// <summary>The id of the accepted event naming <paramref name="delivery"/>.</summary>
    static long AcceptedEventOf(string file, string delivery)
    {
        using var c = OversightSeed.Open(file);
        using var q = c.CreateCommand();
        q.CommandText = "SELECT id FROM org_mandate_event WHERE kind = 'accepted' AND evidence LIKE '%' || $d || '%'";
        q.Parameters.AddWithValue("$d", delivery);
        return (long)q.ExecuteScalar()!;
    }

    /// <summary>
    /// AN ACCEPTANCE NAMING A DELIVERY ANOTHER ACCEPTANCE ALREADY NAMED IS THE LINE'S OWN FAILURE (fix pass 2). Past a dropped trigger, the
    /// second of two acceptances is rewritten to name the first's delivery: the sweep draws that delivery once, records the second event as
    /// its own failure naming both events, never fails, and its cursor passes the second event. RED before fix pass 2: two samples of one
    /// delivery in one write broke the check table's key, the sweep rolled back — cursor included — every minute.
    /// </summary>
    [Fact]
    public void An_acceptance_naming_a_delivery_already_named_is_the_lines_own_failure()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var first = Accepted(org, now, "first");
        var second = Accepted(org, now, "second");
        var (one, two) = (AcceptedEventOf(db.FilePath, first), AcceptedEventOf(db.FilePath, second));
        using (var c = OversightSeed.Open(db.FilePath))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DROP TRIGGER org_mandate_event_sealed_u; UPDATE org_mandate_event SET evidence = json_array($first) WHERE id = $two";
            cmd.Parameters.AddWithValue("$first", first);
            cmd.Parameters.AddWithValue("$two", two);
            cmd.ExecuteNonQuery();
        }

        using var store = new AuditStore(NewFile(), now);
        var swept = new AuditSweep(db.FilePath, store, rate: 1).Run(now);
        Assert.Null(swept.Failure);
        Assert.Equal([first], swept.Drawn.Select(s => s.DeliveryId));
        Assert.True(store.Cursor() >= two, $"the cursor stopped at {store.Cursor()}, before event {two}");
        var failure = Assert.Single(store.Failures()).What;
        log.WriteLine(failure);
        Assert.Contains($"accepted event {two}", failure, StringComparison.Ordinal);
        Assert.Contains($"event {one}", failure, StringComparison.Ordinal);
        Assert.Equal(AuditCheckName.All.Length, store.Checks(first).Count);
    }

    /// <summary>
    /// AN ACCEPTANCE NAMING NO MANDATE IS THE LINE'S OWN FAILURE (fix pass 2): with foreign keys off, an accepted event's mandate is rewritten
    /// to one that does not exist — it is not drawn, a failure names the event, and the sweep goes on. RED before fix pass 2: the inner join
    /// dropped the event, neither drawn nor recorded, and the cursor passed it in silence.
    /// </summary>
    [Fact]
    public void An_acceptance_naming_no_mandate_is_the_lines_own_failure()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var orphan = Accepted(org, now, "orphan");
        var later = Accepted(org, now, "later");
        var orphaned = AcceptedEventOf(db.FilePath, orphan);
        using (var c = OversightSeed.Open(db.FilePath))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DROP TRIGGER org_mandate_event_sealed_u; UPDATE org_mandate_event SET mandate_id = 'm-nowhere' WHERE id = $e";
            cmd.Parameters.AddWithValue("$e", orphaned);
            cmd.ExecuteNonQuery();
        }

        using var store = new AuditStore(NewFile(), now);
        var swept = new AuditSweep(db.FilePath, store, rate: 1).Run(now);
        Assert.Null(swept.Failure);
        Assert.Equal([later], swept.Drawn.Select(s => s.DeliveryId));
        var failure = Assert.Single(store.Failures()).What;
        log.WriteLine(failure);
        Assert.Contains($"accepted event {orphaned}", failure, StringComparison.Ordinal);
        Assert.Contains("names no mandate", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("m-nowhere", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DELIVERY WHOSE REFS ARE ALL LENSES IS NOT RUN, NEVER PASSED: the lens registry is not a file this line opens, so a delivery naming
    /// only a lens has nothing the line can re-resolve — both ref checks store NOT RUN, never passed, and no finding. Mixed refs count only
    /// the checkable ones, and say how many lenses were not re-resolved. RED before fix pass 1 (the review's defect 2): both stored passed =
    /// 1, "every one of the 0 records named".
    /// </summary>
    [Fact]
    public void A_delivery_whose_refs_are_all_lenses_is_not_run_never_passed()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var lensOnly = Accepted(org, now, "a lens", refs: [new MandateRef(MandateRefKind.Lens, "funding-skew@1")]);
        var mixed = Issued(org, now);
        VersionOf(db.FilePath, "ver-mixed", "turn-mixed-1", mixed.Id);
        var both = org.Deliver(mixed.Id, Member, "turn-mixed-1", "a version and a lens", [],
            [new MandateRef(MandateRefKind.Version, "ver-mixed"), new MandateRef(MandateRefKind.Lens, "funding-skew@2")], now).Delivery.Id;
        org.Accept(both, Head, null, now);

        using var store = new AuditStore(NewFile(), now);
        Assert.Null(new AuditSweep(db.FilePath, store, rate: 1).Run(now).Failure);
        foreach (var k in store.Checks()) log.WriteLine($"{k.DeliveryId[..12]} {k.Name} {k.Outcome}: {k.Reason}");
        foreach (var name in new[] { AuditCheckName.RefsExistNow, AuditCheckName.RefsOfThisMandateNow })
        {
            var notRun = Assert.Single(store.Checks(lensOnly), k => k.Name == name);
            Assert.Equal(AuditOutcome.NotRun, notRun.Outcome);
            Assert.False(notRun.Passed);
            Assert.Contains("1 lens record was not re-resolved", notRun.Reason, StringComparison.Ordinal);
            var counted = Assert.Single(store.Checks(both), k => k.Name == name);
            Assert.Equal(AuditOutcome.Passed, counted.Outcome);
            Assert.Contains("every one of the 1 records", counted.Reason, StringComparison.Ordinal);
            Assert.Contains("1 lens record was not re-resolved", counted.Reason, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(store.Findings(), f => f.Check is AuditCheckName.RefsExistNow or AuditCheckName.RefsOfThisMandateNow);
        using var c = new SqliteConnection($"Data Source={store.File};Mode=ReadOnly;Pooling=False");
        c.Open();
        Assert.Equal("0", Scalar(c, $"SELECT COUNT(*) FROM audit_check WHERE delivery_id = '{lensOnly}' AND outcome = 'passed' AND check_name LIKE 'refs-%'"));
    }

    // ---- (f) the refs, now, and the acceptance ----------------------------------------------------------------------------

    /// <summary>A launch the ledger recorded as charged to <paramref name="mandate"/>, and a strategy version it wrote — as only a test may.</summary>
    static void VersionOf(string file, string version, string attempt, string mandate)
    {
        using var c = OversightSeed.Open(file);
        OversightSeed.Insert(c, "ai_attempt", ("id", attempt), ("started_at", Stamp), ("state", "ENDED"), ("role", Member), ("mandate_id", mandate));
        OversightSeed.Insert(c, "strategy_version", ("id", version), ("created_at", Stamp), ("role", Member), ("attempt", attempt));
    }

    /// <summary>
    /// (f) A SAMPLE WHOSE REFS VANISHED OR CAME FROM ANOTHER MANDATE IS FOUND. One delivery names a version its own mandate's launch wrote —
    /// every check passed at the seal — and the version is gone by the audit: <c>refs-exist-now</c> and <c>refs-of-this-mandate-now</c>
    /// fail. Another names a version a launch of ANOTHER mandate wrote (and says so as a deviation, so its acceptance is clean): only
    /// <c>refs-of-this-mandate-now</c> fails. Each failure is an <c>info</c> finding citing ids. RED at base: no ref was re-resolved.
    /// </summary>
    [Fact]
    public void A_sample_whose_refs_vanished_or_came_from_another_mandate_is_found()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;

        var own = Issued(org, now);
        VersionOf(db.FilePath, "ver-own", "turn-own-1", own.Id);
        var vanished = org.Deliver(own.Id, Member, "turn-own-1", "the version", [], [new MandateRef(MandateRefKind.Version, "ver-own")], now);
        Assert.All(vanished.Checks, k => Assert.True(k.Passed, k.Reason));
        org.Accept(vanished.Delivery.Id, Head, null, now);
        using (var c = OversightSeed.Open(db.FilePath))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM strategy_version WHERE id = 'ver-own'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        var elsewhere = Issued(org, now);
        var mine = Issued(org, now);
        VersionOf(db.FilePath, "ver-other", "turn-other-1", elsewhere.Id);
        var borrowed = org.Deliver(mine.Id, Member, "turn-test", "the version", [new MandateDeviation("used a version made elsewhere", "it fits", "none")],
            [new MandateRef(MandateRefKind.Version, "ver-other")], now);
        org.Accept(borrowed.Delivery.Id, Head, null, now);

        using var store = new AuditStore(NewFile(), now);
        Assert.Null(new AuditSweep(db.FilePath, store, rate: 1).Run(now).Failure);
        foreach (var k in store.Checks()) log.WriteLine($"{k.DeliveryId[..12]} {k.Name} {k.Passed}: {k.Reason}");

        string[] Failed(string d) => [.. store.Findings().Where(f => f.DeliveryId == d).Select(f => f.Check).Order(StringComparer.Ordinal)];
        Assert.Equal([AuditCheckName.RefsExistNow, AuditCheckName.RefsOfThisMandateNow], Failed(vanished.Delivery.Id));
        Assert.Equal([AuditCheckName.RefsOfThisMandateNow], Failed(borrowed.Delivery.Id));
        Assert.All(store.Findings(), f => Assert.Equal(AuditSeverity.Info, f.Severity));
        Assert.Equal((own.Id, "turn-own-1"), store.Findings().Where(f => f.DeliveryId == vanished.Delivery.Id).Select(f => (f.MandateId, f.AttemptId)).Distinct().Single());
        Assert.DoesNotContain("ver-own", AllText(store.File), StringComparison.Ordinal);
        Assert.DoesNotContain("ver-other", AllText(store.File), StringComparison.Ordinal);
    }

    /// <summary>A report longer than the relay's cap: it fails the app's <c>report-size</c> check at the seal.</summary>
    static readonly string TooLong = string.Join("\n", Enumerable.Range(1, MandateLimits.ReportLines + 1).Select(i => $"line {i}"));

    /// <summary>
    /// (f) AN ACCEPTED DELIVERY WITH A FAILED CHECK AND NO DEVIATION IS FOUND: its report fails <c>report-size</c> at the seal and the head
    /// accepted it anyway, naming nothing — <c>accepted-clean</c> fails, an <c>info</c> finding. The same failure on a delivery that named a
    /// deviation is clean. RED at base: no acceptance was audited.
    /// </summary>
    [Fact]
    public void An_accepted_delivery_with_a_failed_check_and_no_deviation_is_found()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var silent = Accepted(org, now, TooLong);
        var said = Accepted(org, now, TooLong, deviations: [new MandateDeviation("a longer report", "the detail matters", "none")]);
        Assert.Contains(org.Checks(silent), k => !k.Passed);

        using var store = new AuditStore(NewFile(), now);
        Assert.Null(new AuditSweep(db.FilePath, store, rate: 1).Run(now).Failure);
        var finding = Assert.Single(store.Findings());
        Assert.Equal((silent, AuditCheckName.AcceptedClean, AuditSeverity.Info), (finding.DeliveryId, finding.Check, finding.Severity));
        Assert.True(Assert.Single(store.Checks(said), k => k.Name == AuditCheckName.AcceptedClean).Passed);
    }

    // ---- (g) the dormant reader ------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) A FLAG RECORDS THE DORMANT AUDIT AGENT AND LAUNCHES NOTHING: a sample that named deviations and passed every check is one a model
    /// reader would be wanted for — a flag row in the line's own words that the audit agent did NOT run, and no launch: the launch ledger is
    /// unchanged and no wake was raised. A sample with no deviation carries no flag. RED at base: nothing flagged.
    /// </summary>
    [Fact]
    public void A_flag_records_the_dormant_audit_agent_and_launches_nothing()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var deviated = Accepted(org, now, "the answer, a little wider", attempt: "turn-wide-1",
            deviations: [new MandateDeviation("widened the window", "the effect holds", "one extra run")]);
        var plain = Accepted(org, now, "the answer");
        var (attempts, wakes) = (Count(db.FilePath, "ai_attempt"), Count(db.FilePath, "mission_event"));

        using var store = new AuditStore(NewFile(), now);
        Assert.Null(new AuditSweep(db.FilePath, store, rate: 1).Run(now).Failure);
        Assert.Empty(store.Findings());
        var flag = Assert.Single(store.Flags());
        log.WriteLine(flag.What);
        Assert.Equal((deviated, "turn-wide-1", AuditChecks.DormantFlag), (flag.DeliveryId, flag.AttemptId, flag.What));
        Assert.Contains("audit agent NOT RUN", flag.What, StringComparison.Ordinal);
        Assert.DoesNotContain(store.Flags(), f => f.DeliveryId == plain);
        Assert.Equal((attempts, wakes), (Count(db.FilePath, "ai_attempt"), Count(db.FilePath, "mission_event")));
    }

    // ---- the line on its timer -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE LINE OPENS ITS OWN FILE, AND HOLDS A REFUSAL WITHOUT RUNNING: over a fresh file it opens with no refusal, its file made, its
    /// cadence a minute; over a file a newer build wrote it holds the refusal naming both versions, starts nothing, and leaves the file as
    /// found; both quit cleanly. Its file is under <c>state/</c>, outside every workspace and home. RED at base: no line existed.
    /// </summary>
    [Fact]
    public async Task The_line_opens_its_own_file_and_holds_a_refusal_without_running()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), AuditLine.Cadence);
        var file = Path.GetFullPath(AuditStore.DefaultFile);
        Assert.StartsWith(Path.GetFullPath(Paths.State) + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
        foreach (var root in new[] { Paths.Workspace, Paths.AgentHome, Paths.SeatHome, Paths.RoleHome(CouncilRoles.Research) })
            Assert.False(file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal), $"{file} is under {root}");

        using var db = TestEnv.NewDb();
        var fresh = NewFile();
        await using (var line = new AuditLine(db.FilePath, fresh))
        {
            Assert.Null(line.Refusal);
            line.Start();
            line.Start();
            Assert.True(File.Exists(fresh));
        }

        var newer = NewFile();
        using (var c = new SqliteConnection($"Data Source={newer};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE audit_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO audit_meta VALUES('schema', '9');";
            cmd.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(newer);
        await using (var held = new AuditLine(db.FilePath, newer))
        {
            log.WriteLine(held.Refusal);
            Assert.Contains("version 9", held.Refusal ?? "", StringComparison.Ordinal);
            Assert.Contains($"understands {AuditStore.Schema}", held.Refusal ?? "", StringComparison.Ordinal);
            held.Start();
        }
        Assert.Equal(before, File.ReadAllBytes(newer));
    }
}
