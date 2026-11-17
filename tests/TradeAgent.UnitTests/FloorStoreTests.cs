using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT THE FLOOR READS (<c>U-ui-alive</c>): <see cref="FloorStore"/> over ledger rows a test wrote as the app's ledgers would hold them —
/// direct inserts where only a launch or a verb would write the row, the stores where a store writes it. Nothing here reaches a model.
/// </summary>
public class FloorStoreTests : IDisposable
{
    const string Head = "pos-f1c0ffee0001", Executor = "pos-f1c0ffee0002";
    const string Team = "team-f1c0ffee";

    static readonly DateTimeOffset From = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset To = From.AddHours(2);

    readonly Database _db = TestEnv.NewDb();
    readonly FloorStore _floor;

    public FloorStoreTests() => _floor = new FloorStore(_db);

    public void Dispose() => _db.Dispose();

    static string T(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    void Exec(string sql, params (string, object?)[] ps) => _db.Write(_ =>
    {
        using var c = _db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    void Chart()
    {
        Exec("INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", Team), ("$p", OrgIds.Research), ("$at", T(From.AddDays(-5))));
        foreach (var id in new[] { Head, Executor })
            Exec("INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
                ("$id", id), ("$u", Team), ("$h", $"org/{id}"), ("$at", T(From.AddDays(-5))));
        Exec("UPDATE org_unit SET head_position_id=$h WHERE id=$t", ("$h", Head), ("$t", Team));
    }

    void Attempt(string id, string? role, DateTimeOffset started, DateTimeOffset? ended = null, string state = "LAUNCHED",
        int? exit = null, string? cost = null, string? mandate = null, string? requested = "model-asked", string? effective = null) =>
        Exec("""
            INSERT INTO ai_attempt(id, started_at, runtime, requested_model, reserved_cost, state, ended_at, exit_code, effective_model, cost, role, mandate_id)
            VALUES($id,$s,'claude',$req,'0.40',$state,$e,$x,$eff,$cost,$role,$m)
            """,
            ("$id", id), ("$s", T(started)), ("$req", requested), ("$state", state), ("$e", ended is { } e ? T(e) : null), ("$x", exit),
            ("$eff", effective), ("$cost", cost), ("$role", role), ("$m", mandate));

    long Tool(DateTimeOffset at, string tool = "data", string? argument = "data bars BTCUSDT 1h", bool served = true, string? refusal = null,
        TimeSpan? took = null, string? attempt = "turn-a", string? role = Executor) =>
        new ToolCallStore(_db).Record(new ToolCallRow
        {
            At = at, Attempt = attempt, Role = role, Tool = tool, Argument = argument, Bytes = 10, Served = served, Refusal = refusal,
            Duration = took
        });

    [Fact]
    public void The_window_is_exclusive_at_its_start_and_inclusive_at_its_end()
    {
        Tool(From);
        Tool(From.AddSeconds(1));
        Tool(To);
        Tool(To.AddTicks(1));

        var times = _floor.Events(From, To).Where(e => e.Kind == FloorEventKind.Tool).Select(e => e.At).ToList();

        Assert.Equal([From.AddSeconds(1), To], times);
    }

    [Fact]
    public void A_turn_open_across_the_window_is_in_it_and_one_ended_before_it_is_not()
    {
        Attempt("turn-across", Executor, From.AddHours(-1), To.AddHours(1), "ENDED", 0, "0.30");
        Attempt("turn-open", Head, From.AddMinutes(-30));
        Attempt("turn-before", Executor, From.AddHours(-3), From.AddMinutes(-1), "ENDED", 0, "0.10");
        Attempt("turn-after", Executor, To.AddMinutes(1));
        Attempt("turn-inside", Executor, From.AddMinutes(10), From.AddMinutes(20), "ENDED", 0, null, effective: "model-ran");
        Attempt("turn-ends-at-start", Head, From.AddMinutes(-5), From, "LOST", null, "0.40");
        Attempt("turn-no-role", null, From.AddMinutes(5));

        var turns = _floor.Turns(From, To);

        Assert.Equal(["turn-across", "turn-open", "turn-ends-at-start", "turn-inside"], turns.Select(t => t.Id).ToList());
        var open = turns.Single(t => t.Id == "turn-open");
        Assert.Null(open.EndedAt);
        Assert.Equal("LAUNCHED", open.State);
        Assert.Null(open.Cost);
        Assert.Equal("model-asked", open.Model);
        var inside = turns.Single(t => t.Id == "turn-inside");
        Assert.Equal("model-ran", inside.Model);
        Assert.Null(inside.Cost);
        Assert.Equal(0, inside.ExitCode);
        Assert.Equal(0.30m, turns.Single(t => t.Id == "turn-across").Cost);
        Assert.Equal("LOST", turns.Single(t => t.Id == "turn-ends-at-start").State);
    }

    [Fact]
    public void A_turn_end_carries_whether_it_ended_well_how_long_it_took_and_its_cost_and_an_unknown_cost_stays_null()
    {
        Chart();
        Exec("""
            INSERT INTO org_mandate(id, unit_id, decider, holder, kind, objective, checks, latitude, deadline, allocation_ai, retry_limit, created_at)
            VALUES('man-f1c0ffee0001',$u,$d,$h,'task','Collect a second source for the bars and compare the two.','[]','exact',$by,'1.00',3,$at)
            """, ("$u", Team), ("$d", Head), ("$h", Executor), ("$by", T(To.AddDays(1))), ("$at", T(From)));
        Attempt("turn-good", Executor, From.AddMinutes(1), From.AddMinutes(9), "ENDED", 0, "0.31", mandate: "man-f1c0ffee0001");
        Attempt("turn-unpriced", Executor, From.AddMinutes(20), From.AddMinutes(25), "ENDED", 0, null);
        Attempt("turn-failed", Head, From.AddMinutes(30), From.AddMinutes(31), "ENDED", 1, "0.02");
        Attempt("turn-lost", Head, From.AddMinutes(40), From.AddMinutes(50), "LOST", null, "0.40");

        var events = _floor.Events(From, To);
        var ends = events.Where(e => e.Kind == FloorEventKind.TurnEnd).ToDictionary(e => e.Attempt!);

        var good = ends["turn-good"];
        Assert.Equal(Executor, good.Actor);
        Assert.True(good.Ok);
        Assert.Equal(TimeSpan.FromMinutes(8), good.Took);
        Assert.Equal(0.31m, good.Amount);
        Assert.Equal("ENDED", good.Detail);
        Assert.Equal("man-f1c0ffee0001", good.Mandate);
        Assert.Null(ends["turn-unpriced"].Amount);
        Assert.True(ends["turn-unpriced"].Ok);
        Assert.False(ends["turn-failed"].Ok);
        Assert.False(ends["turn-lost"].Ok);
        Assert.Equal("LOST", ends["turn-lost"].Detail);

        var start = events.Single(e => e.Kind == FloorEventKind.TurnStart && e.Attempt == "turn-good");
        Assert.Equal(Executor, start.Actor);
        Assert.Equal("man-f1c0ffee0001", start.Mandate);
        Assert.Equal("model-asked", start.Detail);
        Assert.Equal(From.AddMinutes(1), start.At);
    }

    [Fact]
    public void A_mandates_events_name_the_other_party_and_carry_the_objective_the_report_or_the_reason()
    {
        Chart();
        const string Id = "man-f1c0ffee0002", Objective = "Backtest the opening-range breakout on the last six months and report the costs.";
        Exec("""
            INSERT INTO org_mandate(id, unit_id, decider, holder, kind, objective, checks, latitude, deadline, allocation_ai, retry_limit, created_at)
            VALUES($id,$u,$d,$h,'task',$o,'[]','exact',$by,'1.50',3,$at)
            """, ("$id", Id), ("$u", Team), ("$d", Head), ("$h", Executor), ("$o", Objective), ("$by", T(To.AddDays(1))), ("$at", T(From)));
        void Event(DateTimeOffset at, string kind, string actor, string? reason = null) =>
            Exec("INSERT INTO org_mandate_event(mandate_id, at, kind, actor, attempt_id, reason) VALUES($m,$at,$k,$a,'turn-x',$r)",
                ("$m", Id), ("$at", T(at)), ("$k", kind), ("$a", actor), ("$r", reason));
        void Delivery(string id, DateTimeOffset at, string report) =>
            Exec("INSERT INTO org_delivery(id, mandate_id, author, attempt_id, report, deviations, refs, manifest, at) VALUES($id,$m,$h,'turn-x',$r,'[]','[]','{}',$at)",
                ("$id", id), ("$m", Id), ("$h", Executor), ("$r", report), ("$at", T(at)));

        Event(From.AddMinutes(1), MandateEventKind.Issued, Head);
        Delivery("dlv-1", From.AddMinutes(10), "The first report.");
        Event(From.AddMinutes(10), MandateEventKind.Delivered, Executor);
        Event(From.AddMinutes(20), MandateEventKind.Rejected, Head, "The costs are missing.");
        Delivery("dlv-2", From.AddMinutes(30), "The second report, with the costs.");
        Event(From.AddMinutes(30), MandateEventKind.Delivered, Executor);
        Delivery("dlv-3", From.AddMinutes(50), "A later report that the earlier event must not show.");
        Event(From.AddMinutes(40), MandateEventKind.Accepted, Head);

        var events = _floor.Events(From, To).Where(e => e.Kind == FloorEventKind.Mandate).ToList();

        Assert.Equal(["issued", "delivered", "rejected", "delivered", "accepted"], events.Select(e => e.Detail).ToList());
        Assert.All(events, e => Assert.Equal(Id, e.Mandate));
        Assert.All(events, e => Assert.Equal(Team, e.Unit));
        Assert.All(events, e => Assert.Equal("turn-x", e.Attempt));
        // Ref is the holder on every one of them, whoever wrote the event.
        Assert.All(events, e => Assert.Equal(Executor, e.Ref));

        var issued = events[0];
        Assert.Equal(Head, issued.Actor);
        Assert.Equal(Executor, issued.Target);
        Assert.Equal(Objective, issued.Text);
        Assert.Null(issued.Ok);

        Assert.Equal(Executor, events[1].Actor);
        Assert.Equal(Head, events[1].Target);
        Assert.Equal("The first report.", events[1].Text);

        Assert.Equal("The costs are missing.", events[2].Text);
        Assert.False(events[2].Ok);
        Assert.Equal(Executor, events[2].Target);

        Assert.Equal("The second report, with the costs.", events[3].Text);

        Assert.Equal(Head, events[4].Actor);
        Assert.Equal(Executor, events[4].Target);
        Assert.True(events[4].Ok);
        Assert.Null(events[4].Text);
    }

    [Fact]
    public void An_escalation_with_no_recipient_goes_to_the_owner_and_one_to_a_position_names_it()
    {
        Chart();
        Exec("""
            INSERT INTO org_escalation(id, sender, attempt_id, unit_id, mandate_id, to_position, reason, skipped, wake_id, day, at)
            VALUES('esc-f1c0ffee0001',$s,'turn-e',$u,NULL,NULL,'The data source is down for a day.','[]',NULL,'2026-11-16',$at)
            """, ("$s", Executor), ("$u", Team), ("$at", T(From.AddMinutes(5))));
        Exec("""
            INSERT INTO org_escalation(id, sender, attempt_id, unit_id, mandate_id, to_position, reason, skipped, wake_id, day, at)
            VALUES('esc-f1c0ffee0002',$s,'turn-e',$u,NULL,$to,'The deadline is too short.','[]','wake-1','2026-11-16',$at)
            """, ("$s", Executor), ("$u", Team), ("$to", Head), ("$at", T(From.AddMinutes(6))));

        var events = _floor.Events(From, To).Where(e => e.Kind == FloorEventKind.Escalation).ToList();

        Assert.Equal(2, events.Count);
        Assert.Equal(FloorEvent.Owner, events[0].Target);
        Assert.Equal(Executor, events[0].Actor);
        Assert.Equal(Team, events[0].Unit);
        Assert.Equal("The data source is down for a day.", events[0].Text);
        Assert.Equal("esc-f1c0ffee0001", events[0].Ref);
        Assert.Equal("turn-e", events[0].Attempt);
        Assert.Equal(Head, events[1].Target);
    }

    /// <summary>
    /// A LOOK THAT WANTS THE LAST FEW SECONDS READS ONLY WHAT WAS WRITTEN SINCE A MARK, and loses nothing by it. The ledgers the floor reads
    /// by time have no index on it and grow for the life of the home; marks taken before the window's start leave every row written by
    /// then unread. Marks taken after it could hide a row that belongs in the window, and are refused.
    /// </summary>
    [Fact]
    public void Marks_taken_before_a_window_leave_out_only_what_was_written_before_them()
    {
        Chart();
        void Hire(DateTimeOffset at, string detail) =>
            Exec("INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES($at,$u,$p,'hired',$d,$x)",
                ("$at", T(at)), ("$u", Team), ("$p", Executor), ("$d", Head), ("$x", detail));
        List<string?> Hires(IReadOnlyList<FloorEvent> events) => [.. events.Where(e => e.Kind == FloorEventKind.Chart).Select(e => e.Text)];

        Hire(From.AddMinutes(1), "first");
        Hire(From.AddMinutes(2), "second");
        var marks = _floor.Marks(From.AddMinutes(3));
        Assert.True(marks.Rows["org_event"] > 0);
        Assert.Equal(0, marks.Rows["org_decision"]);                                  // an empty ledger's mark hides nothing
        Hire(From.AddMinutes(10), "third");
        Hire(From.AddMinutes(11), "fourth");

        // THE WINDOW AFTER THE MARKS: the same rows with them as without.
        Assert.Equal(["third", "fourth"], Hires(_floor.Events(From.AddMinutes(5), To)));
        Assert.Equal(["third", "fourth"], Hires(_floor.Events(From.AddMinutes(5), To, after: marks)));

        // MARKS TAKEN AFTER THE WINDOW'S START COULD HIDE A ROW THAT BELONGS IN IT, and are refused rather than trusted.
        Assert.Throws<ArgumentException>(() => _floor.Events(From, To, after: marks));
        // That they do leave rows unread — the reason for the refusal: the same marks under an earlier date hide the first two.
        Assert.Equal(["third", "fourth"], Hires(_floor.Events(From, To, after: marks with { TakenAt = From.AddSeconds(-1) })));
        Assert.Equal(["first", "second", "third", "fourth"], Hires(_floor.Events(From, To)));
    }

    [Fact]
    public void A_tool_call_carries_its_tool_argument_whether_it_was_served_its_refusal_and_its_duration()
    {
        Tool(From.AddMinutes(1), "data", "data bars BTCUSDT 1h", served: true, took: TimeSpan.FromMilliseconds(1234));
        Tool(From.AddMinutes(2), "trade", "trade backtest v1", served: false, refusal: "the version does not parse", took: null);

        var calls = _floor.Events(From, To).Where(e => e.Kind == FloorEventKind.Tool).ToList();

        Assert.Equal(2, calls.Count);
        // LEFT OUT WHEN ASKED: a reader of the day's story takes everything but the tool calls, and loses nothing else by it.
        var story = _floor.Events(From, To, tools: false);
        Assert.DoesNotContain(story, e => e.Kind == FloorEventKind.Tool);
        Assert.Equal(_floor.Events(From, To).Where(e => e.Kind != FloorEventKind.Tool).Select(e => (e.At, e.Kind, e.Actor)),
            story.Select(e => (e.At, e.Kind, e.Actor)));
        Assert.Equal(Executor, calls[0].Actor);
        Assert.Equal("turn-a", calls[0].Attempt);
        Assert.Equal("data", calls[0].Detail);
        Assert.Equal("data bars BTCUSDT 1h", calls[0].Text);
        Assert.True(calls[0].Ok);
        Assert.Null(calls[0].Ref);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), calls[0].Took);
        Assert.False(calls[1].Ok);
        Assert.Equal("the version does not parse", calls[1].Ref);
        Assert.Null(calls[1].Took);
    }

    [Fact]
    public void Events_from_different_sources_come_back_in_time_order()
    {
        Chart();
        Exec("INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES($at,$u,$p,'hired',$d,'a hire')",
            ("$at", T(From.AddMinutes(30))), ("$u", Team), ("$p", Executor), ("$d", Head));
        // A unit's event names no position, and one the app wrote with no decider is the app's.
        Exec("INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES($at,$u,NULL,'chartered','','a charter')",
            ("$at", T(From.AddMinutes(45))), ("$u", Team));
        Attempt("turn-1", Head, From.AddMinutes(10), From.AddMinutes(40), "ENDED", 0, "0.20");
        Tool(From.AddMinutes(20), attempt: "turn-1", role: Head);
        new OrgStore(_db).Decide(Head, "turn-1", OrgDecisionKind.Routine, ["keep testing", "drop it"], "keep testing",
            "the first week is thin but not negative", [], [], From.AddMinutes(35));
        Assert.True(new MissionEventStore(_db).RecordOwnerMessage("How is it going?", From.AddMinutes(25)));
        // The same moment from two sources keeps the order the sources are listed in: the tool call before the chart event.
        Tool(From.AddMinutes(30), attempt: "turn-1", role: Head);

        var events = _floor.Events(From, To);

        Assert.Equal(
            [FloorEventKind.TurnStart, FloorEventKind.Tool, FloorEventKind.OwnerWords, FloorEventKind.Tool, FloorEventKind.Chart,
             FloorEventKind.Decision, FloorEventKind.TurnEnd, FloorEventKind.Chart],
            events.Select(e => e.Kind).ToList());
        Assert.Equal(events.Select(e => e.At).Order().ToList(), events.Select(e => e.At).ToList());

        var words = events.Single(e => e.Kind == FloorEventKind.OwnerWords);
        Assert.Equal(FloorEvent.Owner, words.Actor);
        Assert.Equal(CouncilRoles.Default, words.Target);
        Assert.Null(words.Text);

        var chart = events.Single(e => e.Kind == FloorEventKind.Chart && e.Detail == "hired");
        Assert.Equal(Head, chart.Actor);
        Assert.Equal(Executor, chart.Target);
        Assert.Equal("hired", chart.Detail);
        Assert.Equal("a hire", chart.Text);

        var unitChart = events.Single(e => e.Kind == FloorEventKind.Chart && e.Detail == "chartered");
        Assert.Null(unitChart.Target);
        Assert.Equal(FloorEvent.App, unitChart.Actor);

        var decision = events.Single(e => e.Kind == FloorEventKind.Decision);
        Assert.Equal(Head, decision.Actor);
        Assert.Equal("keep testing", decision.Text);
        Assert.Equal(OrgDecisionKind.Routine, decision.Detail);
        Assert.StartsWith("dec-", decision.Ref);
    }

    [Fact]
    public void A_limit_keeps_the_newest_still_oldest_first()
    {
        for (var i = 1; i <= 10; i++) Tool(From.AddMinutes(i), argument: $"data bars BTCUSDT {i}");
        Attempt("turn-late", Executor, From.AddMinutes(11));

        var events = _floor.Events(From, To, limit: 4);

        Assert.Equal([From.AddMinutes(8), From.AddMinutes(9), From.AddMinutes(10), From.AddMinutes(11)], events.Select(e => e.At).ToList());
        Assert.Equal(FloorEventKind.TurnStart, events[^1].Kind);
    }

    [Fact]
    public void Span_is_nulls_where_nothing_was_recorded_and_the_first_and_last_moment_otherwise()
    {
        // A NEW HOME IS NOT EMPTY: the app seeds the root, the two divisions and their directors with one chart event each, at the open.
        var seeded = _floor.Span();
        Assert.NotNull(seeded.First);
        Assert.Equal(seeded.First, seeded.Last);

        Exec("DELETE FROM org_event");
        Assert.Equal((null, null), _floor.Span());

        Attempt("turn-1", Executor, From.AddMinutes(5));
        Tool(From.AddMinutes(50));
        Assert.Equal((From.AddMinutes(5), From.AddMinutes(50)), _floor.Span());
    }

    [Fact]
    public void Founded_names_when_each_unit_and_position_first_existed()
    {
        Chart();

        var founded = _floor.Founded();

        Assert.Equal(From.AddDays(-5), founded[Team]);
        Assert.Equal(From.AddDays(-5), founded[Head]);
        Assert.Equal(From.AddDays(-5), founded[Executor]);
        // The seeded chart is there too: the root, the two divisions and their directors.
        Assert.Contains(OrgIds.Root, founded.Keys);
        Assert.Contains(OrgIds.Research, founded.Keys);
        Assert.Contains(CouncilRoles.Research, founded.Keys);
        Assert.Equal(founded[OrgIds.Root], founded[CouncilRoles.Operations]);
    }
}
