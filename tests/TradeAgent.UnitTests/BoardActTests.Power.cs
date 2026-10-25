using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- item 8: CHANGE-001 — the board's own power is proposable, never writable from a board seat --------------------------------

    static string Power(Database db) => Scalar(db,
        "SELECT (SELECT group_concat(id || ':' || model_class || ':' || vendor_family, ',') FROM board_seat) || '#' || "
        + "(SELECT COUNT(*) FROM board_seat_state) || '#' || (SELECT group_concat(kind || ':' || ifnull(budget_share, '-'), ',') FROM board_event) "
        + "|| '#' || (SELECT MAX(version) FROM board_policy)")!;

    /// <summary>
    /// A POWER PROPOSAL CHANGES NOTHING: an active seat's <c>board-power-propose</c> — of its composition, its budget, a connection — is
    /// one record the owner reads, and the seats, their states, the budget share, every board event and the policy are exactly as they
    /// were; a target outside the closed list, a dormant seat and a position are refused with nothing written. RED before item 8:
    /// unknown operation.
    /// </summary>
    [Fact]
    public async Task A_power_proposal_changes_nothing()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Activate(db, "board-1");
        var before = Power(db);

        foreach (var target in new[] { BoardPowerTarget.Composition, BoardPowerTarget.BoardBudget, BoardPowerTarget.Connection })
        {
            var r = await server.CallAsync(Frame(Ops.BoardPowerPropose, ("target", target), ("text", "add a seat on a second vendor family, and its budget")),
                "board-1", Launch(db, "board-1"));
            Assert.True(r.Ok, TradeAgent.Core.Json.Write(r.Error));
            Assert.StartsWith(BoardStore.PowerPrefix, Body(r).GetProperty("proposal").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(before, Power(db));
        Assert.Equal(3, new BoardReader(db).PowerProposals().Count);
        Assert.Equal(BoardFounding.BudgetShare, new BoardReader(db).BudgetShare());

        foreach (var (role, target, says) in new[]
                 {
                     ("board-1", "watcher", "nothing a seat may propose"),
                     ("board-2", BoardPowerTarget.Composition, "dormant, not active"),
                     (CouncilRoles.Research, BoardPowerTarget.Composition, "No position holds the board's verbs")
                 })
        {
            var refused = await server.CallAsync(Frame(Ops.BoardPowerPropose, ("target", target), ("text", "more")), role, Launch(db, role));
            Assert.False(refused.Ok);
            Assert.Contains(says, refused.Error!.Message, StringComparison.Ordinal);
        }
        Assert.Equal(3, new BoardReader(db).PowerProposals().Count);
    }

    /// <summary>
    /// ONLY THE OWNER CHANGES THE BOARD'S COMPOSITION OR BUDGET: no op of the pipe and no argument of one names the board's budget, its
    /// seats or its composition, and no intent field does; in process, the owner's presses set the board's share (taken over the
    /// founding share), found a seat dormant on another family and retire one — each re-checking the composition rule (never a shell,
    /// two model classes at least) — and a retired seat acts no more and never becomes active again. Watched red with the two-class rule
    /// dropped from the retirement.
    /// </summary>
    [Fact]
    public async Task Only_the_owners_presses_change_the_boards_composition_or_budget()
    {
        // THE ORGANISATION'S OWN RETIREMENT (`org-retire`, U-org-verbs-d) retires a POSITION ON THE CHART, never a board seat: its one
        // target is `position`, and a board seat's id is refused as a position's (OrgIds.PositionIdRefusal), so no board seat is ever
        // on the chart for it to reach. Its NAME alone is exempt from the word "retire"; its arguments are checked like every op's.
        var orgRetire = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.OrgRetire);
        Assert.Contains(orgRetire.Args, a => a.Name == "position");
        Assert.NotNull(OrgIds.PositionIdRefusal("board-1"));
        foreach (var op in GatewaySchema.Ops())
            foreach (var word in new[] { "budget", "composition", "seat_class", "model_class", "vendor", "retire", "found", "veto", "budget_share" })
            {
                if (!(op.Op == Ops.OrgRetire && word == "retire"))
                    Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(op.Args, a => a.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
        Assert.DoesNotContain(BoardDirection.Fields, f => f.Path.Contains("budget", StringComparison.Ordinal) || f.Path.Contains("seat", StringComparison.Ordinal));

        using var db = TestEnv.NewDb();
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        Assert.Equal(0.25m, gw.SetBoardBudgetShare(0.25m));
        Assert.Equal(0.25m, new BoardReader(db).BudgetShare());
        Assert.Throws<TradeAgentException>(() => gw.SetBoardBudgetShare(0m));
        Assert.Throws<TradeAgentException>(() => gw.SetBoardBudgetShare(1.5m));

        var founded = gw.FoundBoardSeat(SeatClasses.Broker, BoardModelClass.Strong, "anthropic");
        Assert.Equal(("board-4", BoardSeatStatus.Dormant, "anthropic"), (founded.Id, founded.Status, founded.VendorFamily));
        var shell = Assert.Throws<TradeAgentException>(() => gw.FoundBoardSeat(SeatClasses.Cli, BoardModelClass.Strong, "openai"));
        Assert.Contains("never one with a shell", shell.Message, StringComparison.Ordinal);
        Assert.Equal(4, new BoardReader(db).Seats().Count);

        // board-3 is the one standard seat: retiring it would leave one model class.
        var lastStandard = Assert.Throws<TradeAgentException>(() => gw.RetireBoardSeat("board-3"));
        log.WriteLine(lastStandard.Message);
        Assert.Contains("fewer than two model classes", lastStandard.Message, StringComparison.Ordinal);
        Assert.Equal(BoardSeatStatus.Dormant, new BoardStore(db).Seat("board-3")!.Status);

        Activate(db, "board-1");
        Assert.Equal(BoardSeatStatus.Retired, gw.RetireBoardSeat("board-1").Status);
        Assert.Throws<TradeAgentException>(() => gw.RetireBoardSeat("board-1"));
        var acting = Assert.Throws<TradeAgentException>(() => gw.BoardNarrow(Act("board-1", Change("plan_day_total", 40, 30))));
        Assert.Contains("retired, not active", acting.Message, StringComparison.Ordinal);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Activate(db, "board-1"));
        Assert.Equal(["budget-share", "seat-founded", "seat-retired"],
            new[] { "budget-share", "seat-founded", "seat-retired" }.Where(k => Scalar(db, "SELECT COUNT(*) FROM board_event WHERE kind=$k", ("$k", k)) == "1"));
    }
}
