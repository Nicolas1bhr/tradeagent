using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The two ATAS rows on the dashboard.
///
/// These exist because both rows read <c>unknown</c> for an entire session on 2026-08-31 during
/// which the bridge was serving quotes and carrying a live order — they were declared in
/// <see cref="Components.All"/> and no code anywhere ever wrote them. So the first test below is the
/// regression that matters: a pass of the reporter must leave neither row saying nothing.
///
/// The rest are about the distinction the rows are FOR. "The gateway cannot talk to ATAS" is already
/// on screen as the trading-connection row; what nobody could tell from a screen was which half was
/// missing, and the three ways a bridge pipe goes quiet are three different mornings.
/// </summary>
public class AtasHealthTests
{
    static AtasDetection Machine(bool installed = true, bool running = true, bool bridge = true,
        string? version = "8.0.14.397") =>
        new(installed, installed ? @"C:\ATAS" : null, installed ? @"C:\strategies" : null, version,
            running, bridge, true);

    [Fact]
    public void The_reporter_writes_both_rows_and_neither_is_left_blank()
    {
        var health = new HealthRegistry();
        Assert.Equal(HealthState.UNKNOWN, health.Get(Components.AtasProcess).State);
        Assert.Equal("", health.Get(Components.AtasProcess).Detail);

        new AtasHealthReporter(new UntouchedProbe()).Report(health, new FakeConnector(), HealthState.READY);

        // Still UNKNOWN on the simulator — correctly, nothing was checked — but no longer silent.
        foreach (var c in new[] { Components.AtasProcess, Components.AtasBridge })
            Assert.False(string.IsNullOrWhiteSpace(health.Get(c).Detail),
                $"{c} was left with nothing to say, which is the defect these rows had for months");
    }

    [Fact]
    public void Not_the_chosen_platform_is_reported_as_not_checked_and_not_as_working()
    {
        // READY here would be a lie about a program that may not even be installed, and FAILED would
        // put a red row in front of somebody who has nothing to fix.
        foreach (var d in new[] { Machine(), Machine(installed: false, running: false, bridge: false) })
        {
            Assert.Equal((HealthState.UNKNOWN, AtasHealth.NotInUse), AtasHealth.ProcessRow(false, d));
            Assert.Equal((HealthState.UNKNOWN, AtasHealth.NotInUse),
                AtasHealth.BridgeRow(false, d, HealthState.READY, null, null));
        }
    }

    [Fact]
    public void The_process_row_separates_not_installed_from_not_started()
    {
        Assert.Equal(HealthState.FAILED, AtasHealth.ProcessRow(true, Machine(installed: false, running: false)).State);
        Assert.Equal(HealthState.DEGRADED, AtasHealth.ProcessRow(true, Machine(running: false)).State);

        var (state, detail) = AtasHealth.ProcessRow(true, Machine());
        Assert.Equal(HealthState.READY, state);
        Assert.Contains("8.0.14.397", detail);
    }

    [Fact]
    public void A_missing_version_does_not_produce_a_row_that_trails_off()
    {
        var (state, detail) = AtasHealth.ProcessRow(true, Machine(version: null));
        Assert.Equal(HealthState.READY, state);
        Assert.Equal("running", detail);
    }

    /// <summary>
    /// The whole point of the bridge row. All three of these are "the pipe is quiet"; only one of
    /// them is fixed by pressing Install bridge, and the third is the one that happens after every
    /// single ATAS restart because ATAS restores a chart strategy stopped.
    /// </summary>
    [Fact]
    public void The_bridge_row_separates_the_three_ways_a_quiet_pipe_happens()
    {
        var notInstalled = AtasHealth.BridgeRow(true, Machine(bridge: false), HealthState.FAILED, null, null).Detail;
        var atasDown = AtasHealth.BridgeRow(true, Machine(running: false), HealthState.FAILED, null, null).Detail;
        var notStarted = AtasHealth.BridgeRow(true, Machine(), HealthState.FAILED, null, null).Detail;

        Assert.Equal(3, new HashSet<string>([notInstalled, atasDown, notStarted]).Count);
        Assert.Contains("not installed", notInstalled);
        Assert.Contains("waiting for ATAS", atasDown);
        Assert.Contains("not started on a chart", notStarted);
    }

    [Fact]
    public void A_named_refusal_wins_over_anything_derived_from_the_connection()
    {
        // An unauthenticated or wrong-protocol peer must never render as a healthy bridge, whatever
        // the connection state says: the refusal is the specific news, and a pipe that is up is not
        // evidence about a peer that was turned down on it.
        const string refusal = "the ATAS bridge did not authenticate — it presented no proof";
        foreach (var connection in new[] { HealthState.READY, HealthState.DEGRADED, HealthState.FAILED })
        {
            var row = AtasHealth.BridgeRow(true, Machine(), connection, new BridgeHello(), refusal);
            Assert.Equal(HealthState.FAILED, row.State);
            Assert.Equal(refusal, row.Detail);
        }
    }

    /// <summary>
    /// CONNECTED IS NOT THE SAME AS ABLE TO TRADE. The bridge refuses any order whose client order
    /// id it could not write to the witness file — rule 1 rests on that record — and a permanent
    /// local failure at that path refuses EVERY order, forever. A READY row over a bridge in that
    /// state is this screen lying to the one person who could fix it, and until the row said so the
    /// owner would see orders failing with no reason anywhere in the app.
    ///
    /// DEGRADED rather than FAILED: the pipe is up and everything that does not place an order still
    /// works. And it names the file, because "orders are being refused" without the path is a
    /// symptom nobody can act on.
    /// </summary>
    [Fact]
    public void A_bridge_that_cannot_write_its_write_ahead_record_is_not_reported_as_ready()
    {
        var hello = new BridgeHello
        {
            BridgeVersion = "0.9.1",
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,   // a v2 peer never reaches BridgeRow: _hello stays null for it
            WitnessFailure = @"ERROR coid-witness rewrite did not land. file=C:\Users\m\AppData\Local\TradeAgent\bridge\coid-witness.json UnauthorizedAccessException: Access to the path is denied."
        };

        var (state, detail) = AtasHealth.BridgeRow(true, Machine(), HealthState.READY, hello, null);

        Assert.Equal(HealthState.DEGRADED, state);
        Assert.Contains("orders are being refused", detail);
        Assert.Contains("coid-witness.json", detail);
    }

    /// <summary>
    /// A BRIDGE THAT DOES NOT REPORT THE FIELD IS NOT A HEALTHY BRIDGE ANY MORE — it is an older
    /// build, and it must be refused rather than read as one with nothing to say.
    ///
    /// The silence is not the problem; what the silence hides is. A version-2 bridge writes the
    /// witness, ignores whether the rewrite reached the disk, and sends the order anyway. Reading
    /// its null as "no trouble" is precisely the wrong inference: it cannot report trouble it does
    /// not look for. The protocol number is what separates those two, and it is bumped, so such a
    /// bridge never becomes <c>AtasConnector.Bridge</c> at all — it arrives as a refusal string,
    /// which this row already renders FAILED ahead of anything derived from the connection.
    /// </summary>
    [Fact]
    public void A_bridge_speaking_the_previous_protocol_is_refused_rather_than_believed()
    {
        Assert.False(Versions.BridgeCompatible(2));
        Assert.True(Versions.BridgeCompatible(Versions.BridgeProtocolVersion));

        const string refusal = "bridge 0.1.1 speaks protocol 2, this build speaks 3 — reinstall the add-on";
        var (state, detail) = AtasHealth.BridgeRow(true, Machine(), HealthState.READY, null, refusal);
        Assert.Equal(HealthState.FAILED, state);
        Assert.Equal(refusal, detail);
    }

    /// <summary>A current bridge with nothing to report is still READY.</summary>
    [Fact]
    public void A_current_bridge_with_no_witness_trouble_is_ready()
    {
        var hello = new BridgeHello
        {
            BridgeVersion = "0.9.1",
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            WitnessFailure = null
        };
        Assert.Equal(HealthState.READY, AtasHealth.BridgeRow(true, Machine(), HealthState.READY, hello, null).State);
    }

    [Fact]
    public void A_connected_bridge_says_what_it_is()
    {
        var hello = new BridgeHello { BridgeVersion = "0.9.1", BridgeProtocolVersion = 2 };
        var (state, detail) = AtasHealth.BridgeRow(true, Machine(), HealthState.READY, hello, null);
        Assert.Equal(HealthState.READY, state);
        Assert.Contains("0.9.1", detail);
        Assert.Contains("2", detail);
    }

    [Fact]
    public void A_bridge_that_has_stopped_answering_is_not_reported_as_gone()
    {
        // DEGRADED, not FAILED: the pipe is still up and the orders on it are still real. Reporting
        // it as gone is how a stale heartbeat turns into somebody re-adding a strategy that is
        // already there — and two bridges then compete for one named pipe.
        var (state, _) = AtasHealth.BridgeRow(true, Machine(), HealthState.DEGRADED, new BridgeHello(), null);
        Assert.Equal(HealthState.DEGRADED, state);
    }

    /// <summary>
    /// The reporter's two verdicts, driven from the test rather than from whatever the build machine
    /// happens to have installed and running.
    ///
    /// This is the whole reason <see cref="IAtasProbe"/> exists. Before it, this pair was one test
    /// that constructed the reporter and asserted FAILED, which held on every machine without ATAS
    /// and inverted to READY on the Windows box that had ATAS installed and running: the assertion
    /// was about the host, not about the reporter.
    /// </summary>
    [Fact]
    public void The_process_verdict_comes_from_the_probe_in_both_directions()
    {
        var connector = new AtasConnector();

        var absent = new FakeProbe { Detection = Machine(installed: false, bridge: false), Running = false };
        var health = new HealthRegistry();
        new AtasHealthReporter(absent).Report(health, connector, HealthState.FAILED);
        Assert.Equal(HealthState.FAILED, health.Get(Components.AtasProcess).State);
        Assert.Contains("not installed", health.Get(Components.AtasProcess).Detail);

        var up = new FakeProbe { Detection = Machine(), Running = true };
        var running = new HealthRegistry();
        new AtasHealthReporter(up).Report(running, connector, HealthState.FAILED);
        Assert.Equal(HealthState.READY, running.Get(Components.AtasProcess).State);
        Assert.Contains("8.0.14.397", running.Get(Components.AtasProcess).Detail);
    }

    [Fact]
    public void The_reporter_asks_the_platform_afresh_but_not_the_filesystem()
    {
        // The tick runs every five seconds for the life of the app. Whether ATAS is up has to be
        // current; where it is installed cannot change underneath a running app.
        var probe = new FakeProbe { Detection = Machine(), Running = false };
        var reporter = new AtasHealthReporter(probe) { DetectionTtl = TimeSpan.FromHours(1) };
        var health = new HealthRegistry();
        var connector = new AtasConnector();

        reporter.Report(health, connector, HealthState.FAILED);
        Assert.Equal(HealthState.DEGRADED, health.Get(Components.AtasProcess).State);

        // ATAS is started. Two more ticks, all of them well inside the TTL.
        probe.Running = true;
        for (var i = 0; i < 2; i++) reporter.Report(health, connector, HealthState.FAILED);

        // The new answer reached the row without the detection ever being taken a second time.
        Assert.Equal(HealthState.READY, health.Get(Components.AtasProcess).State);
        Assert.Equal(1, probe.Detects);
        Assert.Equal(2, probe.RunningChecks);
    }

    /// <summary>
    /// A probe that answers out of these two properties and never looks at the machine the tests are
    /// running on. <see cref="Detect"/> folds <see cref="Running"/> in the way the real one does, so
    /// the one knob is the one fact that changes while the app is up.
    /// </summary>
    sealed class FakeProbe : IAtasProbe
    {
        public AtasDetection Detection { get; set; } = Machine();
        public bool Running { get; set; }
        public int Detects { get; private set; }
        public int RunningChecks { get; private set; }

        public AtasDetection Detect()
        {
            Detects++;
            return Detection with { Running = Running };
        }

        public bool IsRunning()
        {
            RunningChecks++;
            return Running;
        }
    }

    /// <summary>Fails the test if anything asks it, which on the simulator path nothing may.</summary>
    sealed class UntouchedProbe : IAtasProbe
    {
        public AtasDetection Detect() =>
            throw new Xunit.Sdk.XunitException("the simulator health tick detected an ATAS install");

        public bool IsRunning() =>
            throw new Xunit.Sdk.XunitException("the simulator health tick enumerated processes");
    }
}
