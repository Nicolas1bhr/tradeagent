using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardTurnTests
{
    // ---- item 3: seat, runtime, home, grant ------------------------------------------------------------------------------------------

    /// <summary>
    /// EACH BOARD SEAT HAS ITS OWN HOME AND A GRANT THAT CANNOT TRADE: in a life with the three founding seats active, each seat's home is
    /// its own folder under <c>workspace/board/</c> — never a position's, never another seat's — holding the app's board canon and no
    /// library, inbox or folder another role writes; its seat runs on the test host's puppet and is recorded at no model's pin; its turn's
    /// grant carries the seat's own id (the act it makes names the seat and its own attempt) and may place no order; and a file it leaves in
    /// its folder is published to no one. RED before the unit: a board id had no home and no turn.
    /// </summary>
    [Fact]
    public async Task Each_board_seat_has_its_own_home_and_a_grant_that_cannot_trade()
    {
        await using var life = await BoardLives.Begin("board-homes", "board-1", "board-2", "board-3");
        var host = life.Host;
        var board = Path.Combine(Paths.Workspace, CouncilRoles.BoardDir);
        var homes = BoardSeats.Founding.ToDictionary(s => s, host.HomeFor);
        Assert.Equal(3, homes.Values.Distinct(StringComparer.Ordinal).Count());
        foreach (var (seat, home) in homes)
        {
            Assert.Equal(Path.Combine(board, seat), home);
            Assert.True(File.Exists(Path.Combine(home, Canon.CliFile)), $"{seat}'s home holds no canon");
            Assert.Contains($"Security board seat {seat}", File.ReadAllText(Path.Combine(home, Canon.CliFile)), StringComparison.Ordinal);
            foreach (var none in new[] { WorkspaceBuilder.InDir, WorkspaceBuilder.OutDir, "research", "strategies", ".tradeagent/context.json" })
                Assert.False(Directory.Exists(Path.Combine(home, none)) || File.Exists(Path.Combine(home, none)), $"{seat}'s home holds {none}");
            foreach (var role in CouncilRoles.All)
                Assert.False(home.StartsWith(host.HomeFor(role) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || home == host.HomeFor(role),
                    $"{seat}'s home is inside {role}'s");

            // ITS RUNTIME: the test host's puppet, never recorded at a broker class's pinned model.
            var spec = host.SeatFor(seat);
            Assert.Equal(RuntimeManifest.PuppetId, spec.Runtime);
            Assert.NotEqual(BrokerSeat.ModelOf(SeatClasses.Broker), spec.Model);
        }
        Assert.False(CouncilRoles.IsPositionId("board-1"), "a board id became a position id");
        Assert.StartsWith($"{CouncilRoles.OrgDir}/", CouncilRoles.HomeDir("pos-5e55cc000001"), StringComparison.Ordinal);

        // ITS GRANT: the seat's own id, and no order.
        var grant = new AgentGrants().Issue("board-1", "turn-board-grant");
        Assert.Equal("board-1", grant.Role);
        Assert.False(grant.MayPlaceOrders, "a board seat's grant may place orders");

        // AND ITS TURN: the act it makes over the pipe names the seat and the attempt the ledger recorded for it; a file it leaves in out/
        // is published to no one.
        life.Script("board-1", [BoardLives.Narrow(BoardLives.Change("plan_day_total", 40, 30), "a smaller day"),
            $"write {WorkspaceBuilder.OutDir}/report-x.md a note nobody reads", "usage 100 0 10", "final Narrowed."]);
        life.Wake("board-1");
        await life.RunUntil(() => life.AttemptsOf("board-1").Any(a => a.State == AiAttemptState.ENDED), "board-1's turn ended");
        var narrow = BoardLives.TradeSteps(life, "board-1", 1)[0];
        Assert.True((int?)narrow["exit"] == 0, $"the narrowing was refused: {BoardLives.Said(narrow)}");
        var act = Assert.Single(new BoardStore(host.Db).Acts()).Act;
        var attempt = Assert.Single(life.AttemptsOf("board-1"));
        Assert.Equal(("board-1", attempt.Id), (act.Seat, act.Attempt));
        Assert.Equal(30m, host.Gateway.Policy.PlanDayTotal);
        Assert.Equal(0L, host.Db.Read(_ =>
        {
            using var c = host.Db.Cmd("SELECT COUNT(*) FROM publication WHERE role=$a", ("$a", "board-1"));
            return Convert.ToInt64(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }));
        log.WriteLine($"homes: {string.Join(", ", homes.Values)}");
    }
}
