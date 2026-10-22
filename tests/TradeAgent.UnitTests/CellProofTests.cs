using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PROOF AND THE ASSURANCE RECORD (<c>U-cell-linux-c</c> items 5 and 6, rung 43): a proof counts on its own machine, full kernel
/// release, BOOT and build only, and stops counting once revoked; every table of the record refuses an edit; a launch's attempt side
/// is the weaker of its cell and its harness.
/// </summary>
public class CellProofTests
{
    static readonly HostIdentity Here = new(Sha256Hex.Of("machine"), "6.17.0-1010-azure") { Boot = Sha256Hex.Of("boot-1") };

    static long Scalar(Database db, string sql) => db.Read(_ =>
    {
        using var c = db.Cmd(sql);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>ROW (i), second half: a proof counts only on its boot, kernel and build — and never once revoked.</summary>
    [Fact]
    public void A_proof_counts_only_on_its_boot_kernel_and_build()
    {
        using var db = TestEnv.NewDb();
        var id = new CellProofStore(db, () => Here, () => "1.0.0+trade-sha256:aa").Record("p", "profile-1", ["none"]);
        Assert.Equal(id, Assert.Single(new CellProofStore(db, () => Here, () => "1.0.0+trade-sha256:aa").Proven()).Id);

        Assert.Empty(new CellProofStore(db, () => Here with { Boot = Sha256Hex.Of("boot-2") }, () => "1.0.0+trade-sha256:aa").Proven());
        Assert.Empty(new CellProofStore(db, () => Here with { OsRelease = "6.17.0-1011-azure" }, () => "1.0.0+trade-sha256:aa").Proven());
        Assert.Empty(new CellProofStore(db, () => Here, () => "1.0.0+trade-sha256:bb").Proven());
        Assert.Empty(new CellProofStore(db, () => null, () => "1.0.0+trade-sha256:aa").Proven());

        new CellProofStore(db, () => Here, () => "1.0.0+trade-sha256:aa").Revoke(id, "the profile recomputed to another hash");
        Assert.Empty(new CellProofStore(db, () => Here, () => "1.0.0+trade-sha256:aa").Proven());
        Assert.False(new CellProofStore(db, () => Here, () => "1.0.0+trade-sha256:aa").HoldsProof());
    }

    /// <summary>
    /// R3 (fix pass 1): NO DEPLOYED HASH, NO BUILD, NO PROOF. A hash file that is missing, empty or not one SHA-256 names no build — never
    /// a shared word like <c>none</c> — and a store with no build writes no proof (refused in words) and counts none, even one written
    /// under a build before.
    /// </summary>
    [Fact]
    public void A_missing_deployed_trade_hash_names_no_build_and_no_proof_is_written_or_counted()
    {
        foreach (var text in new string?[] { null, "", "   ", "none", new string('a', 63), new string('g', 64) })
            Assert.Null(CellProofStore.BuildOf(text));
        Assert.Equal($"{Versions.App}+trade-sha256:{new string('a', 64)}", CellProofStore.BuildOf(new string('A', 64) + "\n"));

        using var db = TestEnv.NewDb();
        var ex = Assert.Throws<TradeAgentException>(() => new CellProofStore(db, () => Here, () => null).Record("p", "profile-1", ["none"]));
        Assert.Contains("state/trade.sha256", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM cell_proof"));

        new CellProofStore(db, () => Here, () => "1.0.0+trade-sha256:aa").Record("p", "profile-1", ["none"]);
        Assert.Empty(new CellProofStore(db, () => Here, () => null).Proven());
        Assert.False(new CellProofStore(db, () => Here, () => null).HoldsProof());
    }

    /// <summary>A proof's build names the deployed trade, so two builds of one version are two builds; a host's release is the full string.</summary>
    [Fact]
    public void A_proofs_build_is_the_version_and_the_deployed_trade_and_its_release_is_the_full_kernel_string()
    {
        Assert.StartsWith(Versions.App + "+trade-sha256:", CellProofStore.ThisBuild(), StringComparison.Ordinal);
        Assert.NotNull(HostIdentity.This);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(File.ReadAllText("/proc/sys/kernel/osrelease").Trim(), HostIdentity.This!.OsRelease);
            Assert.Equal(64, HostIdentity.This.Boot.Length);
        }
        else Assert.Equal(HostIdentity.NoBoot, HostIdentity.This!.Boot);
    }

    /// <summary>Rung 43's tables refuse every edit and delete — a proof, a revocation, an assurance and its rows are written once.</summary>
    [Fact]
    public void The_proof_and_assurance_tables_refuse_every_edit()
    {
        using var db = TestEnv.NewDb();
        // RUNG 43 REACHED, not rung 43 the last: a later rung (U-org-seats' 44) stamps the fresh home past it, and the tables stay this rung's.
        Assert.InRange(int.Parse(db.Read(_ => { using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return (string)c.ExecuteScalar()!; }), CultureInfo.InvariantCulture),
            43, Versions.DatabaseSchemaVersion);
        var id = new CellProofStore(db, () => Here, () => "b").Record("p", "profile", ["none", "web"], [new ProofRow("web-443-granted", false, "let through"), new ProofRow("gap-web-lan-443", true, "reached")]);
        // THE PROOF NAMES ITS CLASSES AND KEEPS ITS ROWS, A GAP AS A GAP (fix pass 1, R4); one naming no class is refused.
        Assert.Equal(["none", "web"], Assert.Single(new CellProofStore(db, () => Here, () => "b").Proven()).Egress);
        Assert.Equal([new ProofRow("web-443-granted", false, "let through"), new ProofRow("gap-web-lan-443", true, "reached")],
            new CellProofStore(db, () => Here, () => "b").RowsOf(id));
        Assert.Throws<TradeAgentException>(() => new CellProofStore(db, () => Here, () => "b").Record("p", "profile", []));
        new CellProofStore(db, () => Here, () => "b").Revoke(id, "why");
        var rows = CellAssuranceStore.WithAttemptSide(
        [
            new AssuranceRow("state", AssuranceSide.Cell, EnforcementClass.HardPlatform, "Landlock"),
            new AssuranceRow("state", AssuranceSide.Harness, EnforcementClass.Mediated, "unconfined harness"),
        ]);
        new CellAssuranceStore(db).Record(new CellAssurance("att-1", "p", "pol", "profile", id, "6.17", 7, "landlock", "none", "attempt-1", "h", rows));
        foreach (var sql in new[]
        {
            "UPDATE cell_proof SET build = 'x'", "DELETE FROM cell_proof", "UPDATE cell_proof_revocation SET reason = 'x'",
            "DELETE FROM cell_proof_revocation", "UPDATE cell_assurance SET egress = 'web'", "DELETE FROM cell_assurance",
            "UPDATE cell_assurance_row SET class = 'HARD_PLATFORM'", "DELETE FROM cell_assurance_row",
            "UPDATE cell_proof_row SET kind = 'PASS'", "DELETE FROM cell_proof_row"
        })
        {
            var ex = Assert.ThrowsAny<Exception>(() => db.Write(_ => { using var c = db.Cmd(sql); return c.ExecuteNonQuery(); }));
            Assert.Contains("never", ex.Message);
        }
        Assert.ThrowsAny<Exception>(() => new CellAssuranceStore(db).Record(
            new CellAssurance("att-1", "p", "pol", "profile", id, "6.17", 7, "landlock", "none", "attempt-1", "h", rows)));
        Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM cell_assurance"));
    }

    /// <summary>
    /// The attempt side of each property is the WEAKER of its cell and its harness: in route (a) the harness is an unconfined codex
    /// holding the login, so the attempt's <c>state</c> and <c>inbox</c> read MEDIATED, and a reader with no row takes nothing as hard.
    /// </summary>
    [Fact]
    public void The_attempt_side_is_the_weaker_of_the_cell_and_the_harness()
    {
        using var db = TestEnv.NewDb();
        var id = new CellProofStore(db, () => Here, () => "b").Record("p", "profile", ["none", "web"], [new ProofRow("web-443-granted", false, "let through"), new ProofRow("gap-web-lan-443", true, "reached")]);
        var rows = CellAssuranceStore.WithAttemptSide(
        [
            new AssuranceRow("inbox", AssuranceSide.Cell, EnforcementClass.HardPlatform, "Landlock and Guarded"),
            new AssuranceRow("inbox", AssuranceSide.Harness, EnforcementClass.Mediated, "no model tool crosses"),
            new AssuranceRow("descendants", AssuranceSide.Cell, EnforcementClass.HardPlatform, "cgroup"),
            new AssuranceRow("descendants", AssuranceSide.Harness, EnforcementClass.HardPlatform, "cgroup"),
            new AssuranceRow("cmdline", AssuranceSide.Cell, EnforcementClass.Gap, "/proc/*/cmdline readable"),
        ]);
        var a = new CellAssurance("att-2", "p", "pol", "profile", id, "6.17", 7, "landlock", "none", "attempt-2", "h", rows);
        new CellAssuranceStore(db).Record(a);
        var store = new CellAssuranceStore(db);
        Assert.Equal(EnforcementClass.Mediated, store.AttemptClassOf("att-2", "inbox"));
        Assert.Equal(EnforcementClass.HardPlatform, store.AttemptClassOf("att-2", "descendants"));
        Assert.Equal(EnforcementClass.Gap, store.AttemptClassOf("att-2", "cmdline"));
        Assert.Null(store.AttemptClassOf("att-none", "inbox"));
        Assert.Equal(EnforcementClass.Gap, a.AttemptClass);
        Assert.Equal("GAP", db.Read(_ => { using var c = db.Cmd("SELECT attempt_class FROM cell_assurance WHERE attempt_id='att-2'"); return (string)c.ExecuteScalar()!; }));
    }
}
