using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 5 (R25 D9): A POSITION LISTS AND STOPS ITS OWN SUBTREE'S PAPER RUNS; ORDERS STAY OPERATIONS' ALONE. The versions and runs are rows
/// the test wrote — no program runs and no model.
/// </summary>
public partial class OrgAutonomyTests
{
    /// <summary>A version row authored by <paramref name="author"/>, as the strategy ledger records one.</summary>
    static string Version(Database db, string author, string tag)
    {
        var id = Sha256Hex.Of("u-org-autonomy " + tag);
        Exec(db, """
            INSERT INTO strategy_version(id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt)
            VALUES($id, $src, $src, 'm', 'b', 'ok', 0, $at, $role, 'turn-test')
            """, ("$id", id), ("$src", "instrument BTCUSDT\n# " + tag), ("$at", Stamp), ("$role", author));
        return id;
    }

    /// <summary>A running deployment of <paramref name="version"/> in <paramref name="mode"/>.</summary>
    static string Run(TradingGateway gw, string version, string mode = "PAPER")
    {
        // ON THE TEST GATEWAY'S OWN PLATFORM AND ACCOUNT, so a stop ends it rather than suspending it as moved.
        var started = gw.Deployments.Start(new StrategyDeploymentRow("", version, "alloc-" + version[..8], "env-" + version[..8], gw.Connector.Id,
            gw.ClosureAccountId is { Length: > 0 } account ? account : "PAPER-1", "BTCUSDT", mode, DeploymentState.Active, null,
            DateTimeOffset.UtcNow, null, null, null));
        Assert.True(started.Ok, started.Why);
        return started.Deployment!.Id;
    }

    static List<string> Listed(IpcResponse r) =>
        [.. Body(r).GetProperty("deployments").EnumerateArray().Select(d => d.GetProperty("id").GetString()!)];

    /// <summary>
    /// (5a) DEPLOYMENT-LIST SHOWS A POSITION ITS OWN SUBTREE'S RUNS ONLY: the head sees its member's run and not Research's; the member sees
    /// its own; the legacy pair read the whole list as before. Both verbs are in a broker and a cell seat's reach; no order op is.
    /// RED at base: neither verb on the door, and the list answered every run to every caller.
    /// </summary>
    [Fact]
    public async Task Deployment_list_shows_a_position_its_own_subtrees_paper_runs_only()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var mine = Run(gw, Version(db, Member, "member's"));
        var theirs = Run(gw, Version(db, CouncilRoles.Research, "research's"));
        // A RUN OF ITS OWN SUBTREE IN ANY MODE BUT PAPER IS NOT ITS TO SEE (R25 D9; the Codex review).
        var live = Run(gw, Version(db, Member, "member's live"), mode: "LIVE_CONFIRM");

        var head = await server.CallAsync(Frame(Ops.DeploymentList), Head, Launch(db, Head));
        Assert.True(head.Ok, head.Error?.Message);
        Assert.Equal([mine], Listed(head));
        Assert.Equal([mine], Listed(await server.CallAsync(Frame(Ops.DeploymentList), Member, Launch(db, Member, SeatClasses.Cell))));
        var research = await server.CallAsync(Frame(Ops.DeploymentList), CouncilRoles.Research, Launch(db, CouncilRoles.Research, SeatClasses.Cli));
        Assert.Equal(new[] { mine, theirs, live }.Order(StringComparer.Ordinal), Listed(research).Order(StringComparer.Ordinal));

        foreach (var runtime in new[] { RuntimeClass.Broker, RuntimeClass.Cell })
        {
            var reach = AgentReach.For(Head, runtime).Verbs;
            Assert.Contains(reach, v => v.Op == Ops.DeploymentList);
            Assert.Contains(reach, v => v.Op == Ops.DeploymentStop);
            Assert.DoesNotContain(reach, v => Ops.IsMutating(v.Op) && v.Op != Ops.DeploymentStop);
        }
        Assert.DoesNotContain(AgentReach.For(CouncilRoles.Research, RuntimeClass.Cli).Verbs, v => v.Op == Ops.DeploymentStop);
    }

    /// <summary>
    /// (5b) A POSITION STOPS ITS OWN SUBTREE'S PAPER RUN AND NOTHING ELSE: the head ends its member's paper run; another subtree's run, a run
    /// in any mode but paper, a caller on an unconfined seat and the legacy research are each refused in words with nothing ended; and the
    /// head's order is still refused — orders stay operations' alone. RED at base: a position's deployment-stop was refused as an order.
    /// </summary>
    [Fact]
    public async Task A_position_stops_its_own_subtrees_paper_run_and_nothing_else()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var mine = Run(gw, Version(db, Member, "member's"));
        var theirs = Run(gw, Version(db, CouncilRoles.Research, "research's"));
        var live = Run(gw, Version(db, Member, "member's live"), mode: "LIVE_CONFIRM");

        foreach (var (id, role, seat, words) in new (string, string, string, string)[]
                 {
                     (theirs, Head, SeatClasses.Broker, "not authored in"),
                     (live, Head, SeatClasses.Broker, "only a PAPER run"),
                     (mine, Member, SeatClasses.Cli, "Heading a unit grants no verb by itself"),
                     (mine, CouncilRoles.Research, SeatClasses.Cli, "moves money")
                 })
        {
            var refused = await server.CallAsync(Frame(Ops.DeploymentStop, ("id", id)), role, Launch(db, role, seat));
            Assert.False(refused.Ok, $"{role} stopped {id}");
            Assert.Contains(words, refused.Error!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(gw.Deployments.ById(id)!.IsEnded);
        }

        var stopped = await server.CallAsync(Frame(Ops.DeploymentStop, ("id", mine)), Head, Launch(db, Head));
        Assert.True(stopped.Ok, stopped.Error?.Message);
        Assert.True(gw.Deployments.ById(mine)!.IsEnded);
        Assert.Contains(Head, gw.Deployments.ById(mine)!.EndReason, StringComparison.Ordinal);

        var buy = await server.CallAsync(Frame(Ops.Buy, ("symbol", "BTCUSDT"), ("quantity", 0.01m)), Head, Launch(db, Head));
        Assert.False(buy.Ok);
        Assert.Equal(ErrorCode.ROLE_MAY_NOT_TRADE.ToString(), buy.Error!.Code);
    }
}
