using System.Reflection;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Audit;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;
using Seam = TradeAgent.Tests.Integration.OversightInvisibilityTests;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE AUDIT LINE IS OUTSIDE THE ORGANISATION AND CAN NEITHER INSTRUCT NOR BLOCK IT (<c>U-org-audit-a</c>; ORCHESTRATION § 2.13, ORGANISATION
/// §§ 3–4 — "outside every information grant"). Three guards on the watcher's harness (<c>U-org-watcher-a</c>): UNTOUCHED — a sweep leaves
/// the organisation's database and the logs exactly as they were; STRUCTURAL — no agent-reachable assembly holds, takes or returns anything
/// of the line and no source of theirs names it; DIFFERENTIAL — no organisation view answers differently because the line ran. The line is
/// public (its existence is doctrine and its principal's name is in the refusals); what it found is not the organisation's to read until part
/// c relays it.
/// </summary>
[Collection("containment")]
public class AuditInvisibilityTests(ITestOutputHelper log)
{
    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    /// <summary>
    /// A DELIVERY THE APP SEALED WITH A DEVIATION AND EVERY CHECK PASSED, ACCEPTED — the one kind of sample that flags the dormant reader —
    /// under a team the test writes into the chart with raw SQL.
    /// </summary>
    static void AcceptedWithADeviation(Database db, DateTimeOffset now)
    {
        const string head = "pos-a0d17100001", member = "pos-a0d17100002", team = "team-a0d17100", at = "2026-10-22T12:00:00.0000000Z";
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", team), ("$p", OrgIds.Research), ("$at", at));
        foreach (var p in new[] { head, member })
            Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
                ("$id", p), ("$u", team), ("$h", $"org/{p}"), ("$at", at));
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", head), ("$t", team));
        var org = new OrgStore(db);
        var m = org.Assign(new MandateIssue(head, null, member, MandateKind.Task, "an objective", null, MandateShape.Report, false,
            now.AddDays(7), 5m, 2), now);
        var d = org.Deliver(m.Id, member, "turn-test", "the answer", [new MandateDeviation("widened it", "it holds", "one run")], [], now);
        org.Accept(d.Delivery.Id, head, null, now);
    }

    // ---- (h) untouched -------------------------------------------------------------------------------------------------

    /// <summary>
    /// (h) GUARD: THE SWEEP LEAVES THE ORGANISATION'S DATABASE AND LOGS UNCHANGED. Every table of the app's database, every row, before and
    /// after a sweep at rate 1 that draws every accepted delivery, writes severe findings and a flag; and the <c>logs/</c> folder, file by
    /// file. Pins an absence: red with the sweep writing anything to either.
    /// </summary>
    [Fact]
    public void The_sweep_leaves_the_organisations_database_and_logs_unchanged()
    {
        string file;
        var now = OversightSeed.Now();
        using (var db = TestEnv.NewDb())
        {
            file = db.FilePath;
            AcceptedWithADeviation(db, now);
        }
        foreach (var d in OversightSeed.Detectors) OversightSeed.Seed(file, d, now);
        Directory.CreateDirectory(Paths.Logs);

        var beforeRows = Seam.Dump(file);
        var beforeLogs = Seam.LogFiles();
        using var store = new AuditStore(Path.Combine(TestEnv.Home, $"audit-{Guid.NewGuid():n}.db"), now);
        var swept = new AuditSweep(file, store, rate: 1).Run(now);
        Assert.Null(swept.Failure);
        Assert.Equal(3, swept.Drawn.Count);
        Assert.Contains(store.Findings(), f => f.Severity == AuditSeverity.Severe);
        Assert.Single(store.Flags());

        var afterRows = Seam.Dump(file);
        Assert.Equal(beforeRows.Keys, afterRows.Keys);
        foreach (var table in beforeRows.Keys)
            Assert.True(beforeRows[table] == afterRows[table], $"the sweep changed the organisation's table {table}");
        Assert.Equal(beforeLogs, Seam.LogFiles());
    }

    // ---- (i) structural ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (i) GUARD: NO AGENT-REACHABLE ASSEMBLY HOLDS THE AUDIT LINE. No type of <c>TradeAgent.Gateway</c>, <c>TradeAgent.AgentRuntime</c> or
    /// <c>TradeAgent.TradeCli</c> holds, takes or returns a <c>TradeAgent.Core.Audit</c> type; and no source of those three or of
    /// <c>TradeAgent.GatewayHost</c> names the namespace, the file or a table of it. Pins an absence.
    /// </summary>
    [Fact]
    public void No_agent_reachable_assembly_holds_the_audit_line()
    {
        const string ns = "TradeAgent.Core.Audit";
        Assert.Equal(ns, typeof(AuditStore).Namespace);
        static bool Of(Type t) =>
            (t.Namespace == ns) || (t.HasElementType && Of(t.GetElementType()!)) || (t.IsGenericType && t.GetGenericArguments().Any(Of));
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Assembly[] reachable = [typeof(GatewayPipeServer).Assembly, typeof(BrokerDoor).Assembly, typeof(TradeAgent.TradeCli.PipeClient).Assembly];
        var holders = reachable.SelectMany(a => a.GetTypes()).SelectMany(t =>
                t.GetFields(all).Where(f => Of(f.FieldType)).Select(f => $"{t.FullName}.{f.Name}")
                    .Concat(t.GetProperties(all).Where(p => Of(p.PropertyType)).Select(p => $"{t.FullName}.{p.Name}"))
                    .Concat(t.GetMethods(all).Where(m => Of(m.ReturnType) || m.GetParameters().Any(p => Of(p.ParameterType)))
                        .Select(m => $"{t.FullName}.{m.Name}()")))
            .ToList();
        Assert.True(holders.Count == 0, "an agent-reachable assembly holds the audit line: " + string.Join(", ", holders));

        var root = Seam.RepoRoot();
        string[] named = [ns, AuditStore.FileName, "audit_", nameof(AuditStore), nameof(AuditLine), nameof(AuditSweep), nameof(AuditEvidence)];
        var sources = new[] { "TradeAgent.Gateway", "TradeAgent.AgentRuntime", "TradeAgent.TradeCli", "TradeAgent.GatewayHost" }
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);
        var naming = sources.Where(f => named.Any(n => File.ReadAllText(f).Contains(n, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.True(naming.Count == 0, "an agent-reachable source names the audit line: " + string.Join(", ", naming));
    }

    // ---- (j) the differential -------------------------------------------------------------------------------------------

    /// <summary>
    /// (j) GUARD: NO ORGANISATION VIEW ANSWERS DIFFERENTLY WHEN THE LINE HAS RUN. The watcher's harness: three homes seeded identically; in
    /// one, the audit line sweeps at rate 1, drawing every accepted delivery and writing its checks and severe findings; then each answers
    /// everything it can answer the organisation — every pipe op as Research and as Operations, every door verb, the Situations and the
    /// pinned totals, every wall-clock instant masked. The swept home answers byte for byte as the unswept one, but for the surfaces the two
    /// unswept homes already disagree on, which the watcher's guard names.
    /// </summary>
    [Fact]
    public async Task No_org_view_answers_differently_when_the_line_has_run()
    {
        var now = OversightSeed.Now();
        await using var swept = await Seam.Seeded(now);
        await using var control = await Seam.Seeded(now);
        await using var second = await Seam.Seeded(now);

        using (var store = new AuditStore(Path.Combine(TestEnv.Home, $"audit-{Guid.NewGuid():n}.db"), now))
        {
            var result = new AuditSweep(swept.Db.FilePath, store, rate: 1).Run(now);
            Assert.Null(result.Failure);
            Assert.NotEmpty(result.Drawn);
            Assert.Contains(store.Findings(), f => f.Severity == AuditSeverity.Severe);
        }

        var c = await Seam.Pass(control, now);
        var d = await Seam.Pass(second, now);
        var s = await Seam.Pass(swept, now);
        Assert.Equal(c.Keys, s.Keys);
        var unstable = c.Keys.Where(k => c[k] != d[k]).ToList();
        foreach (var k in unstable) log.WriteLine($"unstable between two unswept homes: {k}\n  1: {Seam.Around(c[k], d[k])}\n  2: {Seam.Around(d[k], c[k])}");
        Assert.Equal(Seam.Unstable.Keys.Order(StringComparer.Ordinal), unstable.Select(Seam.Surface).Distinct().Order(StringComparer.Ordinal));

        log.WriteLine($"{s.Count} surfaces compared ({Seam.AllOps().Count} ops × 2 roles, {swept.Doors.Sum(x => x.Tools.Offered.Count)} door verbs, 2 Situations, the totals)");
        var moved = s.Keys.Where(k => !Seam.Unstable.ContainsKey(Seam.Surface(k)) && s[k] != c[k]).ToList();
        foreach (var k in moved) log.WriteLine($"MOVED: {k}\n  unswept: {Seam.Around(c[k], s[k])}\n  swept:   {Seam.Around(s[k], c[k])}");
        Assert.True(moved.Count == 0, "the organisation can see that the audit line ran: " + string.Join(", ", moved));
    }
}
