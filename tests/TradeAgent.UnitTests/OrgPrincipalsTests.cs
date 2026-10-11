using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A THIRD POSITION IS A ROW, NOT A CONSTANT (<c>U-org-principals</c>): its own home, open-attempt slot, loop counters
/// and ledger authorship, keyed by its exact id and never folded into the chair — representable, never runnable on its
/// own. Each third position here is seeded by the TEST with raw SQL, as no writer of the chart exists yet.
///
/// <para>The class shares the vendor-override collection because (h) puts a probe runtime into <c>runtimes.json</c>
/// under the shared test home, which the tests that corrupt that file on purpose must not race.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgPrincipalsTests : IDisposable
{
    /// <summary>An id shaped as the app would mint one: opaque, lower-case, never a name an agent chose.</summary>
    const string Third = "pos-7f3a90c1";

    const string Probe = "principals-probe";

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    /// <summary>A position written straight into the chart, as only a test may: under Research's division, active.</summary>
    static void Seat(Database db, string id, string unit = OrgIds.Research, string status = OrgPositionStatus.Active) =>
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
                VALUES($id, $unit, $home, $status, '2026-10-11T12:00:00.0000000Z')
                """, ("$id", id), ("$unit", unit), ("$home", $"org/{id}"), ("$status", status));
            return c.ExecuteNonQuery();
        });

    static long Attempts(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM ai_attempt");
        return Convert.ToInt64(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    static WorkspaceContext Context(string role) => new(
        "Practice simulator", ConnectorIsPaper: true, "SIM-1", TradingMode.PAPER,
        ExecutionAvailable: true, null, new RiskPolicy { InstrumentAllowlist = ["ES"] },
        ConnectorIsBuiltInSimulator: false, Role: role);

    static string Root()
    {
        var root = Path.Combine(TestEnv.Home, $"principals-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
        return root;
    }

    // ---- (a) homes ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) A THIRD POSITION'S HOME IS ITS OWN. RED at base: <c>HomeDir</c> sent every id but Research's to the chair's
    /// folder, <c>workspace/agent</c>, so its canon overwrote the chair's and its CLI would resume the chair's session.
    /// </summary>
    [Fact]
    public void A_third_positions_home_is_its_own_not_the_chairs()
    {
        using var db = TestEnv.NewDb();
        Seat(db, Third);

        Assert.Equal($"org/{Third}", CouncilRoles.HomeDir(Third));
        Assert.Equal("agent", CouncilRoles.HomeDir(CouncilRoles.Operations));
        Assert.Equal("research", CouncilRoles.HomeDir(CouncilRoles.Research));

        var root = Root();
        var files = new AppFileManifest(Path.Combine(root, "app-files.tsv"));
        var home = WorkspaceBuilder.Build(Context(Third), root, files);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "org", Third)), Path.GetFullPath(home));
        Assert.True(File.Exists(Path.Combine(home, Canon.CliFile)), $"no canon in {home}");
        Assert.False(File.Exists(Path.Combine(root, "agent", Canon.CliFile)), "the third position's canon went into the chair's home");
        Assert.True(files.Names(AppFileManifest.Key($"org/{Third}", Canon.CliFile)));

        // AND THE WALKER WALKS IT: a file in the third position's home is on the record the agent cannot edit.
        Directory.CreateDirectory(Path.Combine(home, "trading"));
        File.WriteAllText(Path.Combine(home, "trading", "notes.md"), "mine");
        new MaterialScanner(db, root, appFiles: files).Scan();
        Assert.Contains($"org/{Third}/trading/notes.md",
            new MaterialStore(db).Present(MaterialOrigin.Agent).Select(m => m.RelPath));
    }

    static RuntimeManifest ProbeRuntime()
    {
        var dir = Path.Combine(TestEnv.Home, $"principals-probe-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(dir, "probe.ps1");
            File.WriteAllText(script,
                "if ($args.Count -gt 0 -and $args[0] -eq 'version') { 'principals-probe 1.0.0'; exit 0 }\r\n"
                + "'Nothing needs doing this turn.'\r\nexit 0\r\n");
            string[] run = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script];
            return new RuntimeManifest
            {
                Id = Probe,
                DisplayName = "Principals probe",
                Executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                VersionArgs = [.. run, "version"],
                ExecArgs = [.. run, "{prompt}"],
                ResumeArgs = [.. run, "{prompt}"]
            };
        }

        var sh = Path.Combine(dir, "probe.sh");
        File.WriteAllText(sh,
            "#!/bin/sh\n[ \"$1\" = \"version\" ] && { echo 'principals-probe 1.0.0'; exit 0; }\n"
            + "echo 'Nothing needs doing this turn.'\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new RuntimeManifest
        {
            Id = Probe,
            DisplayName = "Principals probe",
            Executable = sh,
            VersionArgs = ["version"],
            ExecArgs = ["{prompt}"],
            ResumeArgs = ["{prompt}"]
        };
    }
}
