using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EVERY MATERIAL PASS THE APP RUNS TAKES THE LOOP'S ONE EXCLUSION, so no pass measures beside a role
/// that is launching (U-inbox-order item 2).
///
/// <para><c>docs/COUNCIL.md</c> rule 7: the inbox scanner attests only across proven quiescence of
/// every managed agent, and a material row is written ONCE. The mission loop's own pass already went
/// through its exclusion — a pass refuses while a role holds its turn lease, and a lease is refused
/// while a pass runs — but the two passes the app takes on its own account did not: the thirty-second
/// tick of the background loop and the Inbox page's pass after a drop both called
/// <see cref="AppHost.ScanMaterials"/>, which walked the tree whatever the loop was doing. A pass taken
/// while a role launches spends every sighting it makes on the weaker word, for good.</para>
///
/// <para><b>What is driven here</b> is the host's own composition (<c>AppHost.Composed</c>) with a real
/// runtime started through the press's own path, and the host's real loop taking a real turn — held,
/// without a sleep, at the first thing it does once the role's lease is taken: it tells its listeners
/// the card now reads "working". The runtime is a one-line program, written into
/// <c>runtimes.json</c> under the shared test home, so this class runs in the vendor-file collection.
/// Its processes report to a register of their own, because the process-wide one is sticky.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class MaterialPassExclusionTests : IDisposable
{
    const string Probe = "pass-exclusion-probe";

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    /// <summary>
    /// U-INBOX-ORDER (c), RED FIRST: A ROLE HOLDS ITS LEASE AND HAS NOT YET STARTED ITS PROCESS, AND THE
    /// APP ASKS FOR A PASS — exactly what the thirty-second tick and the Inbox page ask for. The pass
    /// does not run: nothing is walked and the window is not moved. On the base it ran beside the
    /// launch.
    /// </summary>
    [Fact]
    public async Task No_scan_runs_while_a_role_is_launching()
    {
        var host = await Composed();
        try
        {
            await host.StartTheAiAsync();
            var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
            Assert.True(host.Wakes!.Raise(MissionEventIds.Review(earlier), MissionEventKind.Review, earlier,
                role: CouncilRoles.Operations));

            // THE HELD SEAM: the loop announces the lease on its own thread before it does anything else
            // with it, and this keeps it there until the app's pass has been asked for.
            using var launching = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            host.Mission.Changed += () =>
            {
                if (launching.IsSet || host.Mission.Status.State != MissionState.Working) return;
                launching.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "the test never let the launch go on");
            };

            var turn = Task.Run(() => host.Mission.TurnAsync());
            Assert.True(launching.Wait(TimeSpan.FromSeconds(60)), "no role ever took its turn lease");

            // Both readings of the window are taken while the launch is still held: once it goes on, the
            // loop's own pass behind the turn moves the window, as it should.
            var windowBefore = host.Db.GetKv("material_scan_at");
            ScanResult? scanned;
            string? windowAfter;
            try
            {
                scanned = host.ScanMaterials();
                windowAfter = host.Db.GetKv("material_scan_at");
            }
            finally { release.Set(); }
            await turn;

            Assert.Null(scanned);
            Assert.Equal(windowBefore, windowAfter);

            // AND ONLY WHILE A ROLE IS TURNING: with the turn over and its lease dropped, the same call
            // walks the tree again.
            Assert.NotNull(host.ScanMaterials());
        }
        finally { await Close(host); }
    }

    // ---- the host -----------------------------------------------------------------------------------

    /// <summary>
    /// The host's own composition over a database whose setup is finished and whose chosen runtime is
    /// the probe below, as the last session left it.
    /// </summary>
    static async Task<AppHost> Composed()
    {
        RuntimeCatalog.SaveOverrides([ProbeRuntime()]);

        var db = TestEnv.NewDb();
        var connector = new FakeConnector(new FakeBroker());
        await using (var before = new TradingGateway(db, connector, new HealthRegistry()))
            before.Update(s => s.SelectedRuntimeId = Probe);
        new OnboardingStore(db).Complete(OnboardingStep.SETUP_COMPLETE);

        await connector.ConnectAsync();
        return AppHost.Composed(db, connector, new AgentPresence());
    }

    /// <summary>The loop and the AI stopped before the database they write to is closed.</summary>
    static async Task Close(AppHost host)
    {
        await host.Mission.PauseAsync();
        await host.Agent.StopAsync();
        await host.Gateway.DisposeAsync();
        host.Db.Dispose();
    }

    /// <summary>
    /// A RUNTIME THAT RUNS on every platform this suite runs on: it answers its version, and prints a
    /// line and exits 0 for any turn. Windows goes through <c>powershell -File</c> rather than a
    /// <c>.cmd</c>, which <c>cmd.exe</c> would run, parsing the Situation the turn is handed as a command
    /// line of its own (the reason <c>ResumeOnStartTests</c> gives for its probe).
    /// </summary>
    static RuntimeManifest ProbeRuntime()
    {
        var dir = Path.Combine(TestEnv.Home, $"pass-exclusion-probe-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(dir, "probe.ps1");
            File.WriteAllText(script,
                "if ($args.Count -gt 0 -and $args[0] -eq 'version') { 'pass-exclusion-probe 1.0.0'; exit 0 }\r\n"
                + "'Nothing needs doing this turn.'\r\nexit 0\r\n");
            string[] run = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script];
            return new RuntimeManifest
            {
                Id = Probe,
                DisplayName = "Pass exclusion probe",
                Executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                VersionArgs = [.. run, "version"],
                ExecArgs = [.. run, "{prompt}"],
                ResumeArgs = [.. run, "{prompt}"]
            };
        }

        var sh = Path.Combine(dir, "probe.sh");
        File.WriteAllText(sh,
            "#!/bin/sh\n[ \"$1\" = \"version\" ] && { echo 'pass-exclusion-probe 1.0.0'; exit 0; }\n"
            + "echo 'Nothing needs doing this turn.'\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new RuntimeManifest
        {
            Id = Probe,
            DisplayName = "Pass exclusion probe",
            Executable = sh,
            VersionArgs = ["version"],
            ExecArgs = ["{prompt}"],
            ResumeArgs = ["{prompt}"]
        };
    }
}
