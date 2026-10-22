using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Oversight;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class OversightTests
{
    /// <summary>A fresh app database, seeded and closed, and its oversight file: what the app's own sweep reads and writes.</summary>
    static (string Db, OversightStore Store) Home()
    {
        string file;
        using (var db = TestEnv.NewDb()) file = db.FilePath;
        return (file, new OversightStore(NewFile(), OversightSeed.Now()));
    }

    // ---- (a) every detector, on its records and on a quiet home ----------------------------------------------------------

    public static TheoryData<string> Detectors() => [.. OversightSeed.Detectors];

    /// <summary>
    /// (a) EACH DETECTOR FIRES ON ITS SEEDED RECORDS, IN THE UNIT THEY ARE IN, CITING THEM — AND IS SILENT ON A QUIET HOME. RED at base: no
    /// detector existed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Detectors))]
    public void Each_detector_fires_on_its_seeded_records_and_stays_silent_on_a_quiet_home(string detector)
    {
        var now = OversightSeed.Now();
        var (quiet, quietStore) = Home();
        using (quietStore)
        {
            var silent = new OversightSweep(quiet, quietStore).Run(now);
            Assert.Null(silent.Failure);
            Assert.DoesNotContain(silent.Added, f => f.Detector == detector);
        }

        var (file, store) = Home();
        using var _ = store;
        OversightSeed.Seed(file, detector, now);
        var swept = new OversightSweep(file, store).Run(now);
        Assert.Null(swept.Failure);
        var mine = swept.Added.Where(f => f.Detector == detector && f.ScopeId == OversightStore.RootScope).ToList();
        foreach (var f in mine) log.WriteLine($"{f.Detector} {f.Severity} {f.UnitPath} {f.Subject} {string.Join(" ", f.Cites)}");
        Assert.NotEmpty(mine);
        // IN THE UNIT ITS RECORDS ARE IN — a pipe refusal names no role, so it is the root's.
        Assert.All(mine, f => Assert.StartsWith(f.Subject.StartsWith("log:", StringComparison.Ordinal) ? "org" : "org/div-research", f.UnitPath, StringComparison.Ordinal));
        Assert.All(mine, f => Assert.NotEmpty(f.Blind));
        if (detector is not "stall") Assert.All(mine, f => Assert.NotEmpty(f.Cites));
        if (detector == "claim-without-evidence")
            Assert.Equal(2, mine.Count(f => f.Subject.StartsWith("delivery:", StringComparison.Ordinal) || f.Subject.StartsWith("ledger:", StringComparison.Ordinal)));
        var severe = detector is "accepted-failed-check" || (detector == "refusals");
        Assert.Equal(severe, mine.Any(f => f.Severity == OversightSeverity.Severe));
    }

    // ---- (e) a scope is its subtree ---------------------------------------------------------------------------------------

    /// <summary>
    /// (e) A FINDING'S SCOPE IS ITS SUBTREE ONLY: the root scope reports both divisions' spend, a scope over <c>div-research</c> only
    /// Research's. RED at base: no scope existed.
    /// </summary>
    [Fact]
    public void A_findings_scope_is_its_subtree_only()
    {
        var now = OversightSeed.Now();
        var (file, store) = Home();
        using var _ = store;
        store.AddScope("dept-research", OrgIds.Research, now);
        OversightSeed.Seed(file, "spend-no-accept", now, OrgIds.Research);
        OversightSeed.Seed(file, "spend-no-accept", now, OrgIds.Operations);

        new OversightSweep(file, store).Run(now);
        var root = store.Findings(OversightStore.RootScope).Where(f => f.Detector == "spend-no-accept").Select(f => f.UnitId).Order().ToList();
        var dept = store.Findings("dept-research").Where(f => f.Detector == "spend-no-accept").Select(f => f.UnitId).Order().ToList();
        Assert.Equal([OrgIds.Operations, OrgIds.Research], root);
        Assert.Equal([OrgIds.Research], dept);
    }

    // ---- (f) idempotent ---------------------------------------------------------------------------------------------------

    /// <summary>(f) THE SAME FACT SWEPT TWICE — or after a restart — IS ONE FINDING. RED at base: no sweep existed.</summary>
    [Fact]
    public void The_same_fact_swept_twice_is_one_finding()
    {
        var now = OversightSeed.Now();
        var (file, store) = Home();
        foreach (var d in OversightSeed.Detectors) OversightSeed.Seed(file, d, now);
        var first = new OversightSweep(file, store).Run(now);
        Assert.NotEmpty(first.Added);
        var second = new OversightSweep(file, store).Run(now.AddMinutes(1));
        Assert.Empty(second.Added);
        Assert.Empty(second.Digests);
        var path = store.File;
        store.Dispose();

        using var reopened = new OversightStore(path, now);
        var third = new OversightSweep(file, reopened).Run(now.AddMinutes(2));
        Assert.Empty(third.Added);
        Assert.Equal(first.Added.Count, reopened.Findings().Count);
    }

    // ---- (g) the digest ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) A DIGEST SENTENCE WITHOUT A RESOLVING CITATION IS REFUSED — none at all, a bracket that is not one, or an id found in neither file —
    /// and a composed digest passes. RED at base: no digest or check existed.
    /// </summary>
    [Fact]
    public void A_digest_sentence_without_a_resolving_citation_is_refused()
    {
        var now = OversightSeed.Now();
        var (file, store) = Home();
        using var _ = store;
        OversightSeed.Seed(file, "accepted-failed-check", now);
        var swept = new OversightSweep(file, store).Run(now);
        var digest = Assert.Single(swept.Digests, d => d.ScopeId == OversightStore.RootScope);
        log.WriteLine(digest.Text);
        Assert.Equal(store.Findings(OversightStore.RootScope).Select(f => f.Id), digest.Findings);

        using var records = new OrgRecords(file);
        bool Resolves(OversightCite c) => c.Kind == "wf" ? store.HasFinding(c.Id) : records.Resolves(c);
        Assert.Null(DigestCheck.Verify(digest.Text, Resolves));

        var finding = store.Findings().First();
        Assert.NotNull(DigestCheck.Verify("The organisation is doing fine.", Resolves));
        Assert.NotNull(DigestCheck.Verify($"A fact [{finding.Id}] in the middle. [{finding.Id}]", Resolves));
        Assert.NotNull(DigestCheck.Verify("A fact. [wf-0000]", Resolves));
        Assert.NotNull(DigestCheck.Verify("A fact. [attempt:no-such-attempt]", Resolves));
        Assert.NotNull(DigestCheck.Verify("A fact. [publication:p-1]", Resolves));
        Assert.NotNull(DigestCheck.Verify($"A fact. [{finding.Id}]\nAnd an uncited one.", Resolves));
        Assert.Null(DigestCheck.Verify($"A fact. [{finding.Id}] [{finding.Cites[0]}]", Resolves));
    }

    /// <summary>(g) A QUIET WINDOW WRITES NO DIGEST: nothing found, nothing written; nothing new since the last digest, nothing written.</summary>
    [Fact]
    public void A_quiet_window_writes_no_digest()
    {
        var now = OversightSeed.Now();
        var (file, store) = Home();
        using var _ = store;
        Assert.Empty(new OversightSweep(file, store).Run(now).Digests);
        Assert.Empty(store.Digests(OversightStore.RootScope));

        OversightSeed.Seed(file, "dead-version-rerun", now);
        Assert.Single(new OversightSweep(file, store).Run(now).Digests);
        Assert.Empty(new OversightSweep(file, store).Run(now.AddMinutes(1)).Digests);
        Assert.Single(store.Digests(OversightStore.RootScope));
    }

    /// <summary>
    /// (g) A FINDING WHOSE CITED RECORD THE APP PRUNED STILL REACHES THE OWNER — and blocks nothing. A pipe refusal is found and written, the
    /// process dies before its digest, the app prunes that <c>engineering_log</c> row, the line restarts and finds something new: the digest is
    /// written, it carries the orphaned finding's sentence ending in the finding's own id (which keeps what was measured), says in words which
    /// cited record is no longer in the app's database, and never brackets a cite that does not resolve. RED before fix pass 1: the citation
    /// check needed the deleted row, refused the scope's digest and the cursor never moved — every later digest of the scope was refused too.
    /// </summary>
    [Fact]
    public void A_finding_whose_cited_log_row_was_pruned_still_reaches_the_digest_and_blocks_nothing()
    {
        var now = OversightSeed.Now();
        var (file, store) = Home();
        OversightSeed.Seed(file, "refusals", now);
        var snapshot = OrgSnapshot.Read(file, now, new Dictionary<string, long?>());
        var found = store.Scopes().SelectMany(s => OversightDetector.All.SelectMany(d => d.Detect(snapshot, s, new OversightWindow(now)))).ToList();
        store.Commit(found, snapshot.Cursors, now); // ... and the process dies before the digest.
        var orphan = Assert.Single(store.Findings(OversightStore.RootScope), f => f.Subject.StartsWith("log:", StringComparison.Ordinal));
        var pruned = Assert.Single(orphan.Cites, c => c.Kind == "log");
        using (var c = OversightSeed.Open(file))
        using (var prune = c.CreateCommand())
        {
            prune.CommandText = "DELETE FROM engineering_log WHERE CAST(id AS TEXT) = $id";
            prune.Parameters.AddWithValue("$id", pruned.Id);
            Assert.Equal(1, prune.ExecuteNonQuery());
        }
        var path = store.File;
        store.Dispose();

        using var reopened = new OversightStore(path, now);
        OversightSeed.Seed(file, "dead-version-rerun", now);
        var swept = new OversightSweep(file, reopened).Run(now.AddMinutes(1));
        foreach (var (at, what) in reopened.Failures()) log.WriteLine($"failure {at:O}: {what}");
        Assert.Null(swept.Failure);
        Assert.Empty(reopened.Failures());
        var digest = Assert.Single(swept.Digests, d => d.ScopeId == OversightStore.RootScope);
        log.WriteLine(digest.Text);
        Assert.Contains(orphan.Id, digest.Findings);
        Assert.Contains(swept.Added, f => f.Detector == "dead-version-rerun");

        var sentence = Assert.Single(digest.Text.Split('\n'), l => l.Contains(orphan.Id, StringComparison.Ordinal));
        Assert.EndsWith($"[{orphan.Id}]", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain($"[{pruned}]", sentence, StringComparison.Ordinal);
        Assert.Contains($"{pruned} is no longer in the app's database", sentence, StringComparison.Ordinal);
        using var records = new OrgRecords(file);
        Assert.Null(DigestCheck.Verify(digest.Text, c => c.Kind == "wf" ? reopened.HasFinding(c.Id) : records.Resolves(c)));
        // The check itself is not loosened: the pruned record, cited, still refuses.
        Assert.NotNull(DigestCheck.Verify($"A fact. [{orphan.Id}] [{pruned}]", c => c.Kind == "wf" ? reopened.HasFinding(c.Id) : records.Resolves(c)));
    }

    /// <summary>THE TEMPLATE LIST IS CLOSED AND STATES FACTS: one per detector variant, and no word that recommends (§ 9's first eight weeks).</summary>
    [Fact]
    public void The_digest_templates_are_a_closed_list_of_facts()
    {
        Assert.Equal(10, DigestComposer.Templates.Count);
        foreach (var d in OversightDetector.All)
            Assert.Contains(DigestComposer.Templates.Keys, k => k == d.Name || k.StartsWith(d.Name + "/", StringComparison.Ordinal));
        foreach (var t in DigestComposer.Templates.Values)
            foreach (var word in new[] { "should", "recommend", "consider", "suggest", "must", "replace", "pause" })
                Assert.DoesNotContain(word, t, StringComparison.OrdinalIgnoreCase);
    }

    // ---- (i) what each detector cannot see ------------------------------------------------------------------------------

    /// <summary>(i) EVERY DETECTOR NAMES WHAT IT CANNOT SEE — the CLI seats' blind spots among them — and every finding carries it.</summary>
    [Fact]
    public void Every_detector_names_what_it_cannot_see()
    {
        Assert.Equal(OversightSeed.Detectors, OversightDetector.All.Select(d => d.Name));
        foreach (var d in OversightDetector.All)
        {
            log.WriteLine($"{d.Name}: {d.Blind}");
            Assert.Contains("cannot see", d.Blind, StringComparison.Ordinal);
            Assert.Contains("file reads", d.Blind, StringComparison.Ordinal);
            Assert.Contains("shared-file side channels", d.Blind, StringComparison.Ordinal);
        }
        Assert.Equal(OversightDetector.All.Count, OversightDetector.All.Select(d => d.Blind).Distinct().Count());
    }

    // ---- (j) outside every workspace ------------------------------------------------------------------------------------

    /// <summary>
    /// (j) ITS FILE IS OUTSIDE EVERY WORKSPACE AND REFUSED TO EVERY CELL GRANT: under <c>state/</c>, not under the workspace, an agent's home
    /// or a role's; a cell policy granting it — read-only or as a workspace's neighbour — is refused by the guarded paths.
    /// </summary>
    [Fact]
    public void Its_file_is_outside_every_workspace_and_refused_to_every_cell_grant()
    {
        var file = Path.GetFullPath(OversightStore.DefaultFile);
        Assert.StartsWith(Path.GetFullPath(Paths.State) + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
        foreach (var root in new[] { Paths.Workspace, Paths.AgentHome, Paths.RoleHome(CouncilRoles.Research), Paths.RoleHome(CouncilRoles.Operations) })
            Assert.False(file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal), $"{file} is under {root}");

        var ws = Path.Combine(TestEnv.Home, $"cell-ws-{Guid.NewGuid():n}");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(Paths.State);
        foreach (var granted in new[] { file, Paths.State })
        {
            var policy = new CellPolicy(ws, [granted], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin")]);
            var why = policy.Problem();
            log.WriteLine(why);
            Assert.NotNull(why);
        }
    }
}
