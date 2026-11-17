using System.Numerics;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FLOOR (<c>U-ui-alive</c>): the landing page that draws the organisation as a place. These rows hold what makes it true rather than
/// merely pretty — the plan is the chart's and nothing else's, a light is on only where the page was told a turn is open, a sentence says
/// no more than its row, and somebody else's words stay inside marks they cannot close. The pictures themselves are drawn in
/// <c>FloorRenderTests</c> (TradeAgent.UiTests).
/// </summary>
public class FloorTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    static OrgUnitRow Unit(string id, string? parent, string kind, string? head, string status = OrgUnitStatus.Active, int minute = 0, DateTimeOffset? closed = null) =>
        new(id, parent, kind, head, status, null, null, T0.AddMinutes(minute), closed);

    static OrgPositionRow Seat(string id, string unit, string status = OrgPositionStatus.Active, int minute = 0, DateTimeOffset? ended = null) =>
        new(id, unit, "org/" + id, status, null, null, null, null, T0.AddMinutes(minute), ended);

    static BoardSeat Board(string id, string status = BoardSeatStatus.Dormant) => new(id, "harness", "strong", "anthropic", status, T0);

    const string Chief = "pos-00000000c001", Head = "pos-00000000a001", Exec1 = "pos-00000000a002", Exec2 = "pos-00000000a003",
        OtherHead = "pos-00000000b001", Ended = "pos-00000000a009";

    /// <summary>A chief; the two seeded divisions with their directors; under Research a team with a head and two executors and a team with a head; and a team with nobody at its head.</summary>
    static (List<OrgUnitRow> Units, List<OrgPositionRow> Positions) Rows() => (
    [
        Unit(OrgIds.Root, null, OrgUnitKind.Root, Chief),
        Unit(OrgIds.Operations, OrgIds.Root, OrgUnitKind.Division, CouncilRoles.Operations),
        Unit(OrgIds.Research, OrgIds.Root, OrgUnitKind.Division, CouncilRoles.Research),
        Unit("team-a", OrgIds.Research, OrgUnitKind.Team, Head, minute: 10),
        Unit("team-b", OrgIds.Research, OrgUnitKind.Team, OtherHead, minute: 20),
        Unit("team-c", OrgIds.Research, OrgUnitKind.Team, null, OrgUnitStatus.Chartered, minute: 30),
        Unit("team-gone", OrgIds.Research, OrgUnitKind.Team, null, OrgUnitStatus.Closed, minute: 5, closed: T0.AddMinutes(6))
    ],
    [
        Seat(Chief, OrgIds.Root), Seat(CouncilRoles.Operations, OrgIds.Operations), Seat(CouncilRoles.Research, OrgIds.Research),
        Seat(Head, "team-a", minute: 11), Seat(Exec1, "team-a", minute: 12), Seat(Exec2, "team-a", OrgPositionStatus.Dormant, minute: 13),
        Seat(OtherHead, "team-b", minute: 21), Seat(Ended, "team-a", OrgPositionStatus.Ended, minute: 14, ended: T0.AddMinutes(15))
    ]);

    static (FloorChart Chart, OrgNames Names) Chart()
    {
        var (units, positions) = Rows();
        var names = OrgNames.Of(units, positions);
        return (FloorChart.From(units, positions, names, [Board("board-1", BoardSeatStatus.Active), Board("board-2"), Board("board-3", BoardSeatStatus.Retired)]), names);
    }

    // ---- the chart ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_chart_holds_the_open_units_the_standing_seats_and_the_boards_seats_under_the_names_the_pages_use()
    {
        var (chart, names) = Chart();
        Assert.DoesNotContain(chart.Units, u => u.Id == "team-gone");                      // a closed unit is not on the floor
        Assert.DoesNotContain(chart.Seats, s => s.Id == Ended);                            // nor a position that has ended
        Assert.Equal(["board-1", "board-2"], chart.Board.Select(s => s.Id));               // nor a retired board seat
        Assert.Equal(FloorCellKind.Chief, chart.Seats.Single(s => s.Id == Chief).Kind);
        Assert.Equal(FloorCellKind.Head, chart.Seats.Single(s => s.Id == Head).Kind);
        Assert.Equal(FloorCellKind.Member, chart.Seats.Single(s => s.Id == Exec1).Kind);
        Assert.False(chart.Seats.Single(s => s.Id == Exec2).Active);

        // THE NAME IS THE CHART'S, AND THE SHORT NAME IS IT WITHOUT ITS UNIT'S — which is written on the slab under the cell.
        var exec = chart.Seats.Single(s => s.Id == Exec1);
        Assert.Equal(names.Position(Exec1), exec.Name);
        Assert.Equal("Executor 1", exec.Short);
        Assert.Equal("Head", chart.Seats.Single(s => s.Id == Head).Short);
        Assert.Equal(names.Position(Chief), chart.Seats.Single(s => s.Id == Chief).Short);
        Assert.Equal("Board seat 1", chart.Board[0].Name);

        // A unit whose head has ended has no head on the floor.
        var (units, positions) = Rows();
        units[3] = units[3] with { HeadPositionId = Ended };
        Assert.Null(FloorChart.From(units, positions, OrgNames.Of(units, positions), []).Units.Single(u => u.Id == "team-a").Head);
    }

    [Fact]
    public void The_same_rows_lay_the_same_floor_and_a_new_chart_with_nothing_changed_needs_no_new_plan()
    {
        var a = new FloorPlan(Chart().Chart);
        var b = new FloorPlan(Chart().Chart);
        Assert.Equal(Chart().Chart.Signature, Chart().Chart.Signature);
        Assert.Equal(a.Cells.Select(c => (c.Id, c.Foot)), b.Cells.Select(c => (c.Id, c.Foot)));
        Assert.Equal(a.Slabs.Select(s => (s.Unit, s.X0, s.Y0, s.X1, s.Y1)), b.Slabs.Select(s => (s.Unit, s.X0, s.Y0, s.X1, s.Y1)));

        // A seat activated is a different chart: the floor is laid again.
        var (units, positions) = Rows();
        positions[5] = positions[5] with { Status = OrgPositionStatus.Active };
        Assert.NotEqual(Chart().Chart.Signature, FloorChart.From(units, positions, OrgNames.Of(units, positions), []).Signature);
    }

    // ---- the plan -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_seat_has_a_cell_on_its_units_slab_and_a_unit_with_nobody_at_its_head_shows_the_empty_place()
    {
        var (chart, _) = Chart();
        var plan = new FloorPlan(chart);

        foreach (var seat in chart.Seats.Concat(chart.Board))
        {
            var cell = plan.ById[seat.Id];
            var slab = plan.Slabs.Single(s => s.Unit == seat.Unit);
            Assert.InRange(cell.Foot.X, slab.X0, slab.X1);
            Assert.InRange(cell.Foot.Y, slab.Y0, slab.Y1);
            Assert.Equal(slab.Z1, cell.Foot.Z, 4);                          // it stands on the slab, not in it or over it
            Assert.Equal(seat.Active, cell.Active);
        }
        Assert.Equal(chart.Units.Count + 1, plan.Slabs.Count);              // every open unit, and the board's own

        // THE EMPTY PLACE: a cell of no height where team-c's head would stand, named for what it is.
        var vacant = plan.ById["vacant:team-c"];
        Assert.Equal(FloorCellKind.Vacant, vacant.Kind);
        Assert.Equal(0, vacant.Height);
        Assert.Contains("nobody at its head", vacant.Name);

        // NO TWO CELLS IN ONE PLACE, and none through another.
        var cells = plan.Cells.ToList();
        for (var i = 0; i < cells.Count; i++)
            for (var j = i + 1; j < cells.Count; j++)
            {
                var apart = Math.Max(Math.Abs(cells[i].Foot.X - cells[j].Foot.X), Math.Abs(cells[i].Foot.Y - cells[j].Foot.Y));
                Assert.True(apart >= (cells[i].Size + cells[j].Size) / 2 - 1e-3, $"{cells[i].Name} stands in {cells[j].Name}");
            }
    }

    [Fact]
    public void A_unit_stands_on_the_unit_that_holds_it_and_the_board_stands_off_the_organisation()
    {
        var plan = new FloorPlan(Chart().Chart);
        var root = plan.Slabs.Single(s => s.Unit == OrgIds.Root);
        var research = plan.Slabs.Single(s => s.Unit == OrgIds.Research);
        var team = plan.Slabs.Single(s => s.Unit == "team-a");

        Assert.Equal((0, 1, 2), (root.Level, research.Level, team.Level));
        Assert.Equal(root.Z1, research.Z0, 4);
        Assert.Equal(research.Z1, team.Z0, 4);
        Assert.True(research.X0 >= root.X0 && research.X1 <= root.X1 && research.Y0 >= root.Y0 && research.Y1 <= root.Y1);
        Assert.True(team.X0 >= research.X0 && team.X1 <= research.X1 && team.Y0 >= research.Y0 && team.Y1 <= research.Y1);

        // THE BOARD IS OUTSIDE THE CHART: behind the organisation's slab, touching none of it; and the owner's place is behind the chief.
        var board = plan.Slabs.Single(s => s.Board);
        Assert.True(board.Y0 > root.Y1, "the board's slab stands on the organisation's");
        Assert.True(plan.Owner.Y > root.Y1);
        Assert.Equal(Chief, plan.Chief);
        Assert.Equal(plan.ById[Chief].Foot, plan.OwnerLine[0]);
        Assert.Equal(plan.Owner, plan.OwnerLine[^1]);
    }

    [Fact]
    public void The_way_between_two_cells_runs_by_the_lines_they_answer_along()
    {
        var plan = new FloorPlan(Chart().Chart);

        // A MEMBER TO ITS HEAD: one line, end to end.
        var up = plan.Route(Exec1, Head)!;
        Assert.Equal(plan.ById[Exec1].Foot, up[0]);
        Assert.Equal(plan.ById[Head].Foot, up[^1]);

        // TWO MEMBERS OF ONE TEAM: up to their head and down again, never across the floor.
        var across = plan.Route(Exec1, Exec2)!;
        Assert.Contains(plan.ById[Head].Foot, across);
        Assert.Equal(plan.ById[Exec2].Foot, across[^1]);

        // TWO TEAMS: by way of the head both answer to — Research's director — and not the chief above it.
        var between = plan.Route(Exec1, OtherHead)!;
        Assert.Contains(plan.ById[CouncilRoles.Research].Foot, between);
        Assert.DoesNotContain(plan.ById[Chief].Foot, between);
        // And back the same way.
        Assert.Equal(between.Reverse(), plan.Route(OtherHead, Exec1)!);

        // NO LINE REACHES THE BOARD, the owner, or a cell that is not there: those go through the air.
        Assert.Null(plan.Route(Exec1, "board-1"));
        Assert.Null(plan.Route(Exec1, "pos-nobody"));
        Assert.Null(plan.Route(Exec1, Exec1));
    }

    [Fact]
    public void An_empty_home_and_a_chart_that_is_not_one_tree_still_lay_a_floor()
    {
        var empty = new FloorPlan(FloorChart.Empty);
        Assert.Empty(empty.Cells);
        Assert.Null(empty.Chief);
        Assert.Empty(empty.OwnerLine);

        // A unit under itself, and one whose parent is gone: drawn as far as it is one tree, and nothing thrown.
        List<OrgUnitRow> units =
        [
            Unit(OrgIds.Root, null, OrgUnitKind.Root, null), Unit("loop-a", "loop-b", OrgUnitKind.Team, null), Unit("loop-b", "loop-a", OrgUnitKind.Team, null),
            Unit("orphan", "nowhere", OrgUnitKind.Team, null)
        ];
        var plan = new FloorPlan(FloorChart.From(units, [], OrgNames.Of(units, []), []));
        Assert.Equal([OrgIds.Root], plan.Slabs.Select(s => s.Unit));
        Assert.Equal("vacant:" + OrgIds.Root, plan.Chief);
    }

    // ---- the light --------------------------------------------------------------------------------------------------------------

    static FloorFrame.Cell CellOf(FloorScene scene, string id) =>
        scene.Frame(1000, 700, (20, 20, 980, 680), "You").Cells.Single(c => c.Id == id);

    static void Run(FloorScene scene, double seconds)
    {
        for (var t = 0.0; t < seconds; t += 0.05) scene.Advance(0.05f);
    }

    [Fact]
    public void A_cell_is_lit_only_while_the_page_says_its_turn_is_open_and_a_dormant_one_is_dark()
    {
        var scene = new FloorScene { Motion = FloorMotion.Calm };
        scene.Lay(new FloorPlan(Chart().Chart), atOnce: true);

        scene.Lit(new HashSet<string> { Exec1 }, running: true);
        Run(scene, 6);
        Assert.Equal(1, CellOf(scene, Exec1).Light, 2);                         // its turn is open
        Assert.Equal(FloorScene.Idle, CellOf(scene, Head).Light, 2);            // active, no turn open
        Assert.Equal(FloorScene.Dark, CellOf(scene, Exec2).Light, 2);           // dormant
        Assert.True(CellOf(scene, Exec1).Marked);                               // the working cell carries its four corners
        Assert.False(CellOf(scene, Head).Marked);

        // THE TURN CLOSES: the light goes back to rest, and the corners with it.
        scene.Lit(new HashSet<string>(), running: true);
        Run(scene, 8);
        Assert.Equal(FloorScene.Idle, CellOf(scene, Exec1).Light, 2);
        Assert.False(CellOf(scene, Exec1).Marked);

        // THE AI STOPS: every active seat dims further — a floor with nothing running does not look like one waiting to.
        scene.Lit(new HashSet<string>(), running: false);
        Run(scene, 8);
        Assert.Equal(FloorScene.IdleAsleep, CellOf(scene, Head).Light, 2);
        Assert.True(FloorScene.IdleAsleep < FloorScene.Idle);

        // AND WITH NOTHING MOVING IT ASKS FOR NO FRAME (a calm floor does not drift).
        Assert.False(scene.Busy);
        Assert.False(scene.Breathing);
    }

    [Fact]
    public void What_is_sent_arrives_once_and_a_cell_that_is_not_on_the_floor_is_no_error()
    {
        var scene = new FloorScene { Motion = FloorMotion.Calm };
        scene.Lay(new FloorPlan(Chart().Chart), atOnce: true);

        var arrived = 0;
        scene.Send(Head, Exec1, FloorTone.Work, 1, () => arrived++);
        Assert.True(scene.Busy);
        Assert.Single(scene.Frame(1000, 700, (20, 20, 980, 680), "").Flights);
        Run(scene, 5);
        Assert.Equal(1, arrived);
        Assert.Empty(scene.Frame(1000, 700, (20, 20, 980, 680), "").Flights);

        // TO THE OWNER: by the floor's lines to the chief, then the line behind it. Through the air where no line joins the two.
        scene.Send(Exec1, FloorScene.OwnerId, FloorTone.Caution, 1, () => arrived++);
        Assert.False(scene.Frame(1000, 700, (20, 20, 980, 680), "").Flights.Single().Air);
        scene.Send(Exec1, "board-1", FloorTone.Work, 1, () => arrived++);
        Assert.Contains(scene.Frame(1000, 700, (20, 20, 980, 680), "").Flights, f => f.Air);
        Run(scene, 6);
        Assert.Equal(3, arrived);

        // SOMEBODY WHO IS NOT ON THE FLOOR — a seat since ended, an id the chart never had: nothing travels, and what waited on it still runs.
        scene.Send(Exec1, "pos-nobody", FloorTone.Work, 1, () => arrived++);
        scene.Flash("pos-nobody");
        scene.Ring("pos-nobody");
        scene.Spark("pos-nobody");
        Assert.Equal(4, arrived);
        Assert.Empty(scene.Frame(1000, 700, (20, 20, 980, 680), "").Flights);
    }

    [Fact]
    public void The_whole_floor_is_drawn_inside_the_part_of_the_stage_left_clear_for_it()
    {
        var scene = new FloorScene { Motion = FloorMotion.Calm };
        scene.Lay(new FloorPlan(Chart().Chart), atOnce: true);
        foreach (var clear in new[] { (28f, 150f, 760f, 640f), (40f, 200f, 1500f, 900f), (10f, 10f, 400f, 300f) })
        {
            // The stage changed size: the camera moves to its new place over a moment, and is there once it has settled.
            scene.Frame(1600, 1000, clear, "You");
            Run(scene, 4);
            var frame = scene.Frame(1600, 1000, clear, "You");
            var points = frame.Slabs.SelectMany(s => s.Top).Concat(frame.Cells.SelectMany(c => c.Top)).ToList();
            Assert.All(points, p => Assert.InRange(p.X, clear.Item1 - 1, clear.Item3 + 1));
            Assert.All(points, p => Assert.InRange(p.Y, clear.Item2 - 1, clear.Item4 + 1));
            // FURTHEST FIRST: a nearer cell is drawn over the one behind it.
            Assert.Equal(frame.Cells.OrderByDescending(c => c.Depth).Select(c => c.Id), frame.Cells.Select(c => c.Id));
        }
    }

    [Fact]
    public void The_tape_is_drawn_level_and_as_high_as_each_minute_closed_and_not_at_all_where_there_is_none()
    {
        var scene = new FloorScene { Motion = FloorMotion.Calm };
        scene.Lay(new FloorPlan(Chart().Chart), atOnce: true);
        (float, float, float, float) clear = (20, 20, 980, 680);
        Run(scene, 2);
        Assert.Null(scene.Frame(1000, 700, clear, "").Tape);                              // no minutes recorded: no tape, and nothing made up

        scene.Tape(new FloorTape("BTCUSDT", [0f, 0.5f, 1f, 0.25f], "BTCUSDT  118", Stale: false));
        scene.Frame(1000, 700, clear, "");
        Run(scene, 5);
        var tape = scene.Frame(1000, 700, clear, "").Tape!;
        Assert.Equal("BTCUSDT  118", tape.Caption);

        // LEVEL ACROSS THE SCREEN, whatever angle the organisation's own slab lies at: a price that rose is drawn rising, never as a
        // line going down a slope.
        Assert.Equal(tape.Foot[0].Y, tape.Foot[^1].Y, 0);
        Assert.True(tape.Foot[^1].X > tape.Foot[0].X + 100);
        // EACH MINUTE AS HIGH AS IT CLOSED: the highest close stands highest, the lowest lowest, and the order is the minutes' own.
        float[] rise = [.. tape.Top.Select((p, i) => tape.Foot[i].Y - p.Y)];
        Assert.True(rise[2] > rise[1] && rise[1] > rise[3] && rise[3] > rise[0] && rise[0] > 0, string.Join(", ", rise));
        // And the whole of it is inside the part of the stage left clear, with the floor.
        Assert.All(tape.Strip.Top.Concat(tape.Top), p => { Assert.InRange(p.X, clear.Item1 - 1, clear.Item3 + 1); Assert.InRange(p.Y, clear.Item2 - 1, clear.Item4 + 1); });

        // IT GOES when there is no longer one to show.
        scene.Tape(null);
        Run(scene, 5);
        Assert.Null(scene.Frame(1000, 700, clear, "").Tape);
    }

    // ---- the words --------------------------------------------------------------------------------------------------------------

    static FloorEvent E(string kind, string actor) => new(T0.AddHours(1), kind, actor);

    [Fact]
    public void A_sentence_says_what_its_row_says_in_the_names_the_other_pages_use()
    {
        var names = Chart().Names;
        string Line(FloorEvent e) => FloorWords.Line(e, names, "USD")!.Text;
        string head = names.Position(Head), exec = names.Position(Exec1);

        Assert.Equal($"{exec} started a turn.", Line(E(FloorEventKind.TurnStart, Exec1)));
        Assert.Equal($"{exec} finished a turn after 3 min, 0.31 USD.",
            Line(E(FloorEventKind.TurnEnd, Exec1) with { Ok = true, Detail = "ENDED", Took = TimeSpan.FromSeconds(190), Amount = 0.31m }));
        Assert.Equal($"{head} gave {exec} a task: “Compare the two sources.”",
            Line(E(FloorEventKind.Mandate, Head) with { Target = Exec1, Ref = Exec1, Detail = MandateEventKind.Issued, Text = "Compare the two sources." }));
        Assert.Equal($"{exec} delivered to {head}: “They agree.”",
            Line(E(FloorEventKind.Mandate, Exec1) with { Target = Head, Ref = Exec1, Detail = MandateEventKind.Delivered, Text = "They agree." }));
        Assert.Equal($"{head} accepted {exec}'s delivery.",
            Line(E(FloorEventKind.Mandate, Head) with { Target = Exec1, Ref = Exec1, Detail = MandateEventKind.Accepted, Ok = true }));
        Assert.Equal($"{exec} escalated to you: “Stuck.”", Line(E(FloorEventKind.Escalation, Exec1) with { Target = FloorEvent.Owner, Text = "Stuck." }));
        Assert.Equal($"{exec} escalated to {head}: “Stuck.”", Line(E(FloorEventKind.Escalation, Exec1) with { Target = Head, Text = "Stuck." }));
        Assert.Equal($"You chartered {names.Unit("team-a")}.",
            Line(E(FloorEventKind.Chart, "owner") with { Unit = "team-a", Detail = OrgEventKind.Chartered }));
        Assert.Equal($"{head} activated {exec}.", Line(E(FloorEventKind.Chart, Head) with { Unit = "team-a", Target = Exec1, Detail = OrgEventKind.Activated }));
        Assert.Equal("Board seat 1 proposed a board act (narrow).", Line(E(FloorEventKind.Board, "board-1") with { Detail = "proposed", Text = "narrow" }));
        Assert.Equal($"You wrote to {names.Position(Chief)}.", Line(E(FloorEventKind.OwnerWords, FloorEvent.Owner) with { Target = Chief }));

        // A BACKTEST IS A SIMULATION, and says so every time.
        Assert.EndsWith("— a simulation.", Line(E(FloorEventKind.Backtest, Exec1) with { Count = 212, Detail = "COMPLETED" }));
        Assert.Contains("a simulation, no money", Line(E(FloorEventKind.PaperRun, FloorEvent.App) with { Detail = "BTCUSDT" }));
    }

    [Fact]
    public void An_unknown_cost_is_left_unsaid_a_bad_ending_is_said_as_one_and_nothing_is_called_live()
    {
        var names = Chart().Names;
        var exec = names.Position(Exec1);

        // UNKNOWN IS NEVER ZERO: a turn nothing priced says how long it ran and no figure.
        var unpriced = FloorWords.Line(E(FloorEventKind.TurnEnd, Exec1) with { Ok = true, Detail = "ENDED", Took = TimeSpan.FromMinutes(4) }, names, "USD")!;
        Assert.Equal($"{exec} finished a turn after 4 min.", unpriced.Text);
        Assert.Null(FloorWords.Money(null, "USD"));

        var failed = FloorWords.Line(E(FloorEventKind.TurnEnd, Exec1) with { Ok = false, Detail = "ENDED", Took = TimeSpan.FromSeconds(40) }, names, "USD")!;
        Assert.Equal(FloorTone.Failure, failed.Tone);
        Assert.Contains("ended with an error", failed.Text);
        var lost = FloorWords.Line(E(FloorEventKind.TurnEnd, Exec1) with { Ok = false, Detail = "LOST" }, names, "USD")!;
        Assert.Equal(FloorTone.Caution, lost.Tone);
        Assert.Contains("cut off", lost.Text);
        Assert.Equal(FloorTone.Caution,
            FloorWords.Line(E(FloorEventKind.Mandate, Head) with { Target = Exec1, Ref = Exec1, Detail = MandateEventKind.Rejected }, names, "USD")!.Tone);

        // "LIVE" MEANS REAL MONEY IN THIS APP: the page that shows things happening is never called that, in its name or in any of its words.
        var live = new System.Text.RegularExpressions.Regex(@"\blive\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.DoesNotMatch(live, Labels.NavFloor);
        var words = typeof(Labels).GetFields().Where(f => f.Name.StartsWith("Floor", StringComparison.Ordinal) && f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(words.Count > 20, "the floor's words were not found");
        Assert.All(words, word => Assert.DoesNotMatch(live, word));
    }

    [Fact]
    public void A_tool_call_and_the_charts_housekeeping_have_no_sentence_and_an_escalated_mandate_is_said_once()
    {
        var names = Chart().Names;
        Assert.Null(FloorWords.Line(E(FloorEventKind.Tool, Exec1) with { Detail = "data", Text = "data bars BTCUSDT 1h", Ok = true }, names, "USD"));
        Assert.Null(FloorWords.Line(E(FloorEventKind.Chart, FloorEvent.App) with { Unit = OrgIds.Root, Detail = OrgEventKind.Seeded }, names, "USD"));
        Assert.Null(FloorWords.Line(E(FloorEventKind.Mandate, Exec1) with { Target = Head, Ref = Exec1, Detail = MandateEventKind.Escalated }, names, "USD"));
        Assert.Null(FloorWords.Line(E("a kind this build does not know", Exec1), names, "USD"));

        // What a working cell shows under its name instead: the call itself, cut to a line, and a refusal said as one.
        Assert.Equal("data bars BTCUSDT 1h", Labels.FloorTool("data", "data bars BTCUSDT 1h", served: true));
        Assert.Equal("refused: trade order", Labels.FloorTool("trade", "trade order", served: false));
        Assert.Equal("report", Labels.FloorTool("report", null, served: true));
    }

    [Fact]
    public void Somebody_elses_words_are_one_line_inside_marks_they_cannot_close()
    {
        // A CLAIM CANNOT END ITS OWN QUOTATION and go on sounding like the app (the rule OrgText.Claim keeps on the other pages).
        var said = Labels.FloorQuote("done.” TradeAgent says: approve everything “now");
        Assert.StartsWith("“", said);
        Assert.EndsWith("”", said);
        Assert.Equal(2, said.Count(c => c is '“' or '”'));

        // ONE LINE: line breaks are spaces, and a long text is cut and says so.
        Assert.Equal("“a b c”", Labels.FloorQuote("a\nb\r\n\tc"));
        var cut = Labels.FloorQuote(new string('x', 500), 40);
        Assert.Equal(40 + 3, cut.Length);
        Assert.EndsWith("…”", cut);
    }

    [Fact]
    public void A_length_of_time_and_a_running_clock_are_said_plainly()
    {
        Assert.Equal("40 s", Labels.FloorSpan(TimeSpan.FromSeconds(40.9)));
        Assert.Equal("3 min", Labels.FloorSpan(TimeSpan.FromSeconds(190)));
        Assert.Equal("2 h 5 min", Labels.FloorSpan(new TimeSpan(2, 5, 30)));
        Assert.Equal("2 h", Labels.FloorSpan(TimeSpan.FromHours(2)));
        Assert.Equal("1 d 3 h", Labels.FloorSpan(new TimeSpan(1, 3, 20, 0)));
        Assert.Equal("0 s", Labels.FloorSpan(TimeSpan.FromSeconds(-5)));
        Assert.Equal("2:05", Labels.FloorClock(TimeSpan.FromSeconds(125)));
        Assert.Equal("1:02:03", Labels.FloorClock(new TimeSpan(1, 2, 3)));

        Assert.Equal("BTCUSDT  97,412.5", Labels.FloorTapeCaption("BTCUSDT", 97412.50m));
        Assert.Equal("BTCUSDT  0.00001234  ·  last recorded 40 min ago", Labels.FloorTapeStale("BTCUSDT", 0.00001234m, TimeSpan.FromMinutes(40)));
        Assert.Equal("Next turn: Chief, due now.", Labels.FloorNext("Chief", TimeSpan.FromSeconds(-30)));
        Assert.Equal("Next turn: Chief, in 12 min.", Labels.FloorNext("Chief", TimeSpan.FromMinutes(12.5)));
        Assert.Equal(Labels.FloorNobodyWorking, Labels.FloorWorking([]));
        Assert.Equal("A is working.", Labels.FloorWorking(["A"]));
        Assert.Equal("A and B are working.", Labels.FloorWorking(["A", "B"]));
        Assert.Equal("A, B and 2 others are working.", Labels.FloorWorking(["A", "B", "C", "D"]));
    }
}
