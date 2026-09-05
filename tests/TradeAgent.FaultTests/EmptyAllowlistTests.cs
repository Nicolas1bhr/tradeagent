using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// AN EMPTY INSTRUMENT ALLOWLIST ALLOWS NOTHING (item 2 of U-settings-closed).
///
/// <c>InstrumentAllowed</c> began <c>InstrumentAllowlist.Count == 0 ||</c>, so "the owner has named
/// nothing" and "the owner has permitted everything" were the same stored value. Three different
/// situations reach it and only one of them is a decision: a fresh install, a settings row that
/// could not be read, and an owner who cleared the box meaning "stop trading these". Reading any of
/// them as a wildcard is the software inventing a permission.
/// </summary>
public class EmptyAllowlistTests(ITestOutputHelper log)
{
    /// <summary>Through the gateway, not just the policy object: an order is actually refused.</summary>
    [Fact]
    public async Task An_owner_who_clears_the_allowlist_stops_every_order()
    {
        var (gw, conn, db) = await TestEnv.Ready(s => s.Risk.InstrumentAllowlist = ["ES"]);
        using var dbh = db;

        var placed = await gw.PlaceAsync(new AgentContext("a"), "al-1", TestEnv.Buy());
        Assert.Equal(ExecutionState.FILLED, placed.State);
        log.WriteLine($"with ES on the list  : {placed.State}, orders = {conn.Broker.Orders.Count}");

        gw.Update(s => s.Risk.InstrumentAllowlist.Clear());

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "al-2", TestEnv.Buy()));
        log.WriteLine($"after clearing it    : {denied.Code} — {denied.Message}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Contains("allowed instrument list", denied.Message);
        Assert.Single(conn.Broker.Orders);            // still just the first one
        await gw.DisposeAsync();
    }

    /// <summary>
    /// The other direction, through the gateway: a populated list allows exactly its members and
    /// refuses everything else. Without this, "fails closed" is indistinguishable from "is broken".
    /// </summary>
    [Fact]
    public async Task A_populated_allowlist_still_allows_exactly_its_members()
    {
        var (gw, conn, db) = await TestEnv.Ready(s => s.Risk.InstrumentAllowlist = ["ES"]);
        using var dbh = db;

        var placed = await gw.PlaceAsync(new AgentContext("a"), "al-es", TestEnv.Buy("ES"));
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "al-nq", TestEnv.Buy("NQ")));

        log.WriteLine($"ES : {placed.State}");
        log.WriteLine($"NQ : {denied.Code} — {denied.Message}");
        Assert.Equal(ExecutionState.FILLED, placed.State);
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Single(conn.Broker.Orders);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// The sentence the app shows for an empty list. It used to say the opposite — "Leave empty to
    /// allow any the platform offers" — and a screen that says the opposite of what the gate does is
    /// worse than a screen that says nothing.
    /// </summary>
    [Fact]
    public void The_app_says_that_an_empty_list_allows_nothing()
    {
        log.WriteLine($"Labels.NoInstrumentAllowed : {Labels.NoInstrumentAllowed}");
        Assert.Contains("No instrument is allowed", Labels.NoInstrumentAllowed);
        Assert.Contains("until you add one", Labels.NoInstrumentAllowed);
        Assert.DoesNotContain("any", Labels.NoInstrumentAllowed);
    }

    /// <summary>
    /// AGENTS.md is the agent's own copy of the limits, and it said "any the platform offers" from
    /// the same <c>Count == 0</c> test. An agent told it may touch anything, by a gate that will
    /// refuse everything, spends its whole session placing orders that cannot succeed.
    /// </summary>
    [Fact]
    public void The_agents_own_briefing_says_none_rather_than_any()
    {
        var empty = WorkspaceBuilder.Instructions(Brief(new RiskPolicy()));
        var named = WorkspaceBuilder.Instructions(Brief(new RiskPolicy { InstrumentAllowlist = ["ES"] }));

        var emptyLine = empty.Split('\n').Single(l => l.Contains("- instruments:"));
        var namedLine = named.Split('\n').Single(l => l.Contains("- instruments:"));
        log.WriteLine($"empty list : {emptyLine.Trim()}");
        log.WriteLine($"named list : {namedLine.Trim()}");

        Assert.DoesNotContain("any the platform offers", emptyLine);
        Assert.Contains("refused", emptyLine);
        Assert.Contains("ES", namedLine);
    }

    static WorkspaceContext Brief(RiskPolicy risk) =>
        new("Practice simulator", true, "SIM-001", TradingMode.PAPER, true, null, risk);
}
