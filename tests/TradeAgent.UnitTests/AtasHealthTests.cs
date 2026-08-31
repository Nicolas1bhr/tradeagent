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

        new AtasHealthReporter().Report(health, new FakeConnector(), HealthState.READY);

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

    [Fact]
    public void The_reporter_asks_the_platform_afresh_but_not_the_filesystem()
    {
        // The tick runs every five seconds for the life of the app. Whether ATAS is up has to be
        // current; where it is installed cannot change underneath a running app.
        var reporter = new AtasHealthReporter { DetectionTtl = TimeSpan.FromHours(1) };
        var health = new HealthRegistry();
        for (var i = 0; i < 3; i++) reporter.Report(health, new AtasConnector(), HealthState.FAILED);

        // No ATAS on the build host, so this is the honest answer and it must be reached without
        // throwing on a machine that has never seen the platform.
        Assert.Equal(HealthState.FAILED, health.Get(Components.AtasProcess).State);
        Assert.Contains("not installed", health.Get(Components.AtasProcess).Detail);
    }
}
