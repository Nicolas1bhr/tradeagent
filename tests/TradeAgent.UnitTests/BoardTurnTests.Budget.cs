using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardTurnTests
{
    // ---- item 2: the board's own budget ----------------------------------------------------------------------------------------------

    static long FundingRows(Database db, string attemptId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM ai_attempt_funding WHERE attempt_id=$id", ("$id", attemptId));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    static string Change(string field, object from, object to) =>
        JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["field"] = field, ["from"] = from, ["to"] = to } });

    /// <summary>
    /// THE BOARD SPENDS INSIDE ITS OWN SHARE AND NO UNIT'S: with the board's share of the plan's day-total in force room for two board
    /// turns and a half, two board seats are admitted — each reserved before its launch, funded by no unit (no
    /// <c>ai_attempt_funding</c> row) — and a third seat's turn is refused by the BOARD's ceiling, its words naming the board's share,
    /// while a position of the legacy pair is admitted beside it on the day's ceiling. The day's ceiling is asked first: with the day
    /// itself full, a board seat is refused by the day. A board turn that cannot be priced is refused, never counted as nothing. RED
    /// before the unit: a board launch had no budget of its own.
    /// </summary>
    [Fact]
    public void The_board_spends_inside_its_own_share_and_no_units()
    {
        using var db = TestEnv.NewDb();
        // THREE SEATS ON THE BROKER CLASS, so each launch is held to both ceilings alike: board-1, board-3 and one the owner founds.
        var fourth = new BoardStore(db).FoundBoardSeat(SeatClasses.Broker, BoardModelClass.Strong, "openai", DateTimeOffset.UtcNow).Id;
        Activate(db, "board-1", "board-3", fourth);
        var share = new BoardStore(db).BudgetShare()!.Value;
        Assert.Equal(BoardFounding.BudgetShare, share);

        // THE RESERVATION ONE TURN MAKES, priced at the owner's rate; the plan's day-total in force sized so the board's share holds 2.5.
        var reservation = Meter(db, _ => SeatClasses.Broker).Reservation("board-1").Cost!.Value;
        Assert.True(reservation > 0m, "a board turn reserved nothing");
        var plan = 2.5m * reservation / share;
        var meter = Meter(db, r => BoardSeats.IsBoardId(r) ? SeatClasses.Broker : SeatClasses.Puppet, cap: () => 1000m, planCap: () => plan);

        var admitted = new List<string>();
        foreach (var seat in new[] { "board-1", "board-3" })
        {
            using var attached = meter.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Broker), seat);
            var turn = meter.Begin("## Board", role: seat);
            Assert.True(turn.Admitted, turn.Refusal);
            Assert.Equal(0, FundingRows(db, turn.Id!));
            Assert.Equal(reservation, new AiAttemptStore(db).Get(turn.Id!)!.ReservedCost);
            admitted.Add(turn.Id!);
        }

        using (meter.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Broker), fourth))
        {
            var third = meter.Begin("## Board", role: fourth);
            Assert.False(third.Admitted, "the board spent past its own share");
            Assert.Equal(AiAdmission.Board, third.Ceiling);
            Assert.Contains("The security board has", third.Refusal, StringComparison.Ordinal);
            Assert.Contains("only the owner changes the board's share", third.Refusal, StringComparison.Ordinal);
            log.WriteLine(third.Refusal);
        }
        var board = new AiAttemptStore(db).BoardTotalsBetween(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(2 * reservation, board.Spent + board.Reserved);

        // A POSITION BESIDE IT IS ADMITTED ON THE DAY'S CEILING: the board's ceiling is the board's alone.
        using (meter.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Puppet), CouncilRoles.Research))
        {
            var research = meter.Begin("## Situation", role: CouncilRoles.Research);
            Assert.True(research.Admitted, research.Refusal);
        }

        // THE DAY FIRST: with the day's own ceilings full, a board seat is refused by the day, not by its share.
        var full = Meter(db, _ => SeatClasses.Broker, cap: () => 0.0001m, planCap: () => 0.0001m);
        using (full.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Broker), fourth))
        {
            var day = full.Begin("## Board", role: fourth);
            Assert.False(day.Admitted, "a board seat was admitted past the day's ceiling");
            Assert.NotEqual(AiAdmission.Board, day.Ceiling);
        }

        // AN UNPRICED BOARD TURN IS NEVER COUNTED AS NOTHING: the rule the meter builds, with no price, refuses.
        var rule = meter.AdmissionRuleFor(fourth, BillingClasses.Plan) with { Reservation = 0m };
        Assert.Equal(share, rule.BoardShare);
        var unpriced = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = $"turn-{Guid.NewGuid():n}"[..17], StartedAt = DateTimeOffset.UtcNow, Role = fourth, ReservedCost = 0m,
            BillingClass = BillingClasses.Plan, Funding = []
        }, admit: rule);
        Assert.False(unpriced.Admitted, "an unpriced board turn was admitted as costing nothing");
        Assert.Equal(Labels.BoardTurnUnpriced, unpriced.Refusal);
    }

    /// <summary>
    /// A BOARD ACT CANNOT RAISE THE BOARD'S SHARE (CHANGE-001; b1 item 8): an intent naming the share is refused as no field of the policy,
    /// a power proposal on the board's budget is a record that changes nothing, and a two-seat widening of the plan's day-total — the
    /// figure the share is OF — leaves the share where the owner set it. Only his in-process press moves it.
    /// </summary>
    [Fact]
    public void A_board_act_cannot_raise_the_boards_share()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var store = new BoardStore(db);
        var at = DateTimeOffset.UtcNow;
        var before = store.BudgetShare();

        foreach (var field in new[] { "budget_share", "board_budget", "board.budget_share" })
        {
            var refused = Assert.Throws<TradeAgentException>(() =>
                store.Propose(new BoardActRequest("board-1", null, Change(field, 0.1m, 0.5m), "more for the board", [], []), at));
            log.WriteLine($"{field}: {refused.Message}");
        }

        var power = store.ProposePower("board-1", null, BoardPowerTarget.BoardBudget, "the board needs more of the day", at);
        Assert.NotNull(power);

        var proposed = store.Propose(new BoardActRequest("board-1", null, Change("plan_day_total", 40m, 50m), "a larger day", [], []), at);
        var confirmed = store.Decide(proposed.Act.Id, "board-2", null, true, proposed.Act.IntentSha256, "agreed on the figures", at);
        Assert.NotNull(confirmed.PolicyVersion);

        Assert.Equal(before, store.BudgetShare());
        Assert.Equal(BoardFounding.BudgetShare, store.BudgetShare());
        Assert.Equal(50m, store.InForce().Fields.Where(f => f.Name == "plan_day_total").Select(f => decimal.Parse(f.Value, CultureInfo.InvariantCulture)).Single());
    }
}
