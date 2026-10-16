using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A PUPPET SEAT NEVER RUNS OUTSIDE A TEST HOST (U-puppet-runtime; docs/ORCHESTRATION.md § 7; AUTH-001, ORDER-001, CRED-001).
///
/// <para>A puppet is handed a real launch grant — the chair's may place orders — so the one thing it must never be is a way to
/// point the app at another program. Three locks, each tested here: the flag that makes a manifest a puppet is not data
/// (<c>[JsonIgnore] internal</c>, so <c>runtimes.json</c> cannot set it); the id is reserved (an entry using it refuses the
/// file's read, in words); and <see cref="AgentSession"/> refuses to start one unless the test host's flag is set, before any
/// grant is issued.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class PuppetSeatTests : IDisposable
{
    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    /// <summary>
    /// (a) RED FIRST: AN ENTRY IN <c>runtimes.json</c> NAMED <c>puppet</c> — any casing — REFUSES THE FILE'S READ, in words naming
    /// the entry, and nothing starts: the start path's <c>Require</c> refuses with the file's own code. On the base the entry was
    /// read like any other: a program of the file's choosing, under the id the tests' puppet answers to.
    /// </summary>
    [Fact]
    public void A_puppet_manifest_named_in_runtimes_json_is_refused_and_nothing_starts()
    {
        foreach (var id in new[] { "puppet", "Puppet" })
        {
            RuntimeCatalog.SaveOverrides([new RuntimeManifest
            {
                Id = id, DisplayName = "Not a puppet", Executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                ExecArgs = ["{prompt}"]
            }]);

            var read = RuntimeCatalog.Read();
            Assert.True(read.Runtimes.Count == 0, $"runtimes.json naming '{id}' was read: {read.Runtimes.Count} runtimes");
            Assert.NotNull(read.Unreadable);
            Assert.Contains($"'{id}'", read.Unreadable, StringComparison.Ordinal);
            Assert.Contains("reserved", read.Unreadable, StringComparison.Ordinal);

            var refused = Assert.Throws<TradeAgentException>(() => RuntimeCatalog.Require(id));
            Assert.Equal(ErrorCode.RUNTIME_COMMANDS_UNREADABLE, refused.Code);
            Assert.Null(RuntimeCatalog.Find("codex"));
        }
    }

    /// <summary>
    /// (b) RED FIRST, AND THE WATCHED MUTANT'S TEST: A PUPPET OUTSIDE A TEST HOST IS REFUSED BEFORE ANY GRANT IS ISSUED — no grant
    /// minted, no process started (the puppet writes a line before anything else on every start, and there is none), and the
    /// conversation says why in words naming the manifest. The meter's seat check refuses it too, so no row would be written,
    /// and the runtime finds no program for it, so not even its version probe starts. In the test host the very same session
    /// runs the turn: the refusal is the host's, not the manifest's.
    /// </summary>
    [Fact]
    public async Task A_puppet_outside_a_test_host_is_refused_before_any_grant_is_issued()
    {
        var scripts = Puppet.NewScripts("outside");
        var home = Path.Combine(scripts, "agent");
        Directory.CreateDirectory(home);
        var manifest = Puppet.Manifest(scripts);
        var grants = new AgentGrants();
        var session = new AgentSession(manifest, () => Puppet.DotnetHost, () => home,
            () => new Dictionary<string, string> { ["DOTNET_ROOT"] = Puppet.DotnetRoot },
            presence: new AgentPresence(), grants: grants)
        {
            Admit = _ => AiAdmission.NoLedger
        };
        var launches = Path.Combine(scripts, "launches.log");

        Assert.True(PuppetSeats.TestHost, "this assembly's module initializer did not mark it a test host");
        SeatVerdict seat;
        string? program;
        PuppetSeats.TestHost = false;
        try
        {
            await session.SendMissionAsync("A turn nobody may run.");
            seat = session.CheckSeat();
            program = new CliAgentRuntime(manifest).ResolveExecutable();
        }
        finally { PuppetSeats.TestHost = true; }

        Assert.True(grants.Live == 0, $"a puppet outside a test host was issued {grants.Live} launch grant(s)");
        Assert.False(File.Exists(launches), $"a puppet outside a test host started: {(File.Exists(launches) ? File.ReadAllText(launches) : "")}");
        var said = Assert.Single(session.History, t => t.Role == ChatRole.System && t.Text.Contains("puppet seat", StringComparison.Ordinal));
        Assert.Contains("'Puppet seat' (puppet)", said.Text, StringComparison.Ordinal);
        Assert.Contains("not a test host", said.Text, StringComparison.Ordinal);
        Assert.True(seat.Refused, "the meter's seat check admitted a puppet outside a test host");
        Assert.Equal(ErrorCode.AI_SEAT_REFUSED, seat.Code);
        Assert.Null(program);

        // THE CONTROL: the same session, in the test host, starts the puppet with a grant of its own.
        await session.SendMissionAsync("A turn the test host runs.");
        Assert.True(File.Exists(launches), "the puppet did not start in the test host");
        Assert.Equal(1, grants.Live);
        Assert.Contains(session.History, t => t.Role == ChatRole.Ai && t.Text == "Nothing to do.");
    }

    /// <summary>
    /// THE FLAG AND THE DECLARED CLASS ARE NOT DATA: a <c>runtimes.json</c> entry that writes them, in either spelling, reads as an
    /// ordinary manifest of the class nobody declared, and a puppet manifest written out carries neither.
    /// </summary>
    [Fact]
    public void The_puppet_flag_and_its_seat_class_are_never_read_from_runtimes_json()
    {
        File.WriteAllText(RuntimeCatalog.OverridePath, """
            [{ "id": "probe-puppetish", "display_name": "Probe", "executable": "probe", "exec_args": ["{prompt}"],
               "puppet": true, "Puppet": true, "puppet_seat_class": "cli", "PuppetSeatClass": "cli" }]
            """);
        var read = RuntimeCatalog.Find("probe-puppetish");
        Assert.NotNull(read);
        Assert.False(read!.Puppet);
        Assert.Equal(SeatPlan.PuppetClass, read.PuppetSeatClass);

        using var written = JsonDocument.Parse(Json.Write(Puppet.Manifest(Puppet.NewScripts("json"), SeatPlan.CliClass)));
        var names = written.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("puppet", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// (f) GUARD — green at base and after: EVERY EXISTING FAKE CLI RUNS EXACTLY AS IT DID. The puppet's refusal is the puppet's
    /// alone: the canned-stream CLI most suites drive (<see cref="AgentRuntimeProbe.SessionOverStream"/>, codex's recorded
    /// stream) runs the same turn, metered the same, with the test host's flag set and unset, and records no seat class; its
    /// seat check refuses nothing; and a probe runtime written into <c>runtimes.json</c> under any id but the reserved one is
    /// read as before (the ResumeOnStart and AgentTree probes' shape).
    /// </summary>
    [Fact]
    public async Task Every_existing_fake_cli_test_is_unchanged()
    {
        foreach (var testHost in new[] { true, false })
        {
            PuppetSeats.TestHost = testHost;
            try
            {
                var session = AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream, name: $"puppet-guard-{testHost}");
                AgentTurnEnded? ended = null;
                session.TurnEnded += e => ended = e;
                await session.SendMissionAsync("say hi");

                Assert.NotNull(ended);
                Assert.Equal(0, ended!.ExitCode);
                Assert.Equal(17232, ended.Usage?.InputTokens);
                Assert.Contains(session.History, t => t.Role == ChatRole.Ai && t.Text == "Hi!");
                Assert.False(session.CheckSeat().Refused);
            }
            finally { PuppetSeats.TestHost = true; }
        }

        RuntimeCatalog.SaveOverrides([new RuntimeManifest
        {
            Id = "resume-probe", DisplayName = "Resume probe", Executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            VersionArgs = ["version"], ExecArgs = ["{prompt}"], ResumeArgs = ["{prompt}"]
        }]);
        var read = RuntimeCatalog.Read();
        Assert.Null(read.Unreadable);
        Assert.NotNull(RuntimeCatalog.Find("resume-probe"));
        Assert.False(RuntimeCatalog.Find("resume-probe")!.Puppet);
    }
}
