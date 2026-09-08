using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-report item 3. The three dispositions the APP reaches without a turn — <c>delegated</c>,
/// <c>blocked</c>, <c>superseded</c> — and the deadline the owner's report measures against.
///
/// <para>The failure these close is a silent one: a message the owner typed, nobody answered, and
/// nothing anywhere said so. Paying for a turn to notice that would be spending their money to tell
/// them what the app already knew, so every outcome here is written by code and the report prints it
/// as a line.</para>
/// </summary>
public class OwnerDispositionTests
{
    static DateTimeOffset Midday()
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }

    [Fact]
    public async Task A_message_nobody_has_taken_by_its_deadline_reads_as_overdue_in_the_report()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var events = new MissionEventStore(db);
        var at = Midday();

        // Typed thirty hours ago, and nothing has become of it since.
        events.RecordOwnerMessage("stop buying NQ", at.AddHours(-30));

        var report = gw.Reports.Compose(at);
        var text = DailyReportText.Render(report);

        var line = Assert.Single(report.Decisions.OwnerMessages);
        Assert.Equal("stop buying NQ", line.Text);
        Assert.Null(line.Disposition);
        Assert.True(line.Overdue);
        Assert.Equal(at.AddHours(-30) + TimeSpan.FromHours(24), line.DueBy);
        Assert.Contains("stop buying NQ", text);
        Assert.Contains("OVERDUE", text);
    }

    [Fact]
    public async Task A_message_still_inside_its_deadline_is_listed_without_the_overdue_word()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var at = Midday();
        new MissionEventStore(db).RecordOwnerMessage("how is it going", at.AddHours(-1));

        var line = Assert.Single(gw.Reports.Compose(at).Decisions.OwnerMessages);
        Assert.False(line.Overdue);
        Assert.DoesNotContain("OVERDUE", DailyReportText.Render(gw.Reports.Compose(at)));
    }

    /// <summary>
    /// THE MIGRATION, AND THE PLACE THE CURRENT SCHEMA NUMBER IS PINNED EXACTLY. It belongs with the
    /// migration that last moved it — the convention <c>U-wakes</c> set and <c>U-council-thin</c>
    /// carried, so that an additive migration turns exactly one assertion red rather than every
    /// version pin in the suite.
    /// </summary>
    [Fact]
    public async Task The_disposition_detail_arrives_at_schema_ten()
    {
        var (_, _, db) = await TestEnv.Ready();
        Assert.Equal(10, Versions.DatabaseSchemaVersion);
        Assert.Equal("10", db.Read(_ =>
        {
            using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
            return c.ExecuteScalar() as string;
        }));

        // Additive: every row written before it reads as a disposition that points at nothing.
        var events = new MissionEventStore(db);
        events.RecordOwnerMessage("anything at all", Midday());
        Assert.Null(events.OfKind(MissionEventKind.Owner)[0].DispositionDetail);
    }

    [Fact]
    public void The_deadline_ships_at_a_day()
    {
        Assert.Equal(24, new TradeAgentSettings().OwnerReplyDeadlineHours);
    }

    [Fact]
    public async Task A_message_that_has_an_outcome_is_never_overdue_however_old_it_is()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var events = new MissionEventStore(db);
        var at = Midday();

        events.RecordOwnerMessage("what did you do yesterday", at.AddHours(-30));
        var id = events.OfKind(MissionEventKind.Owner)[0].Id;
        events.Consume([id], "attempt-1", at.AddHours(-29));
        events.Settle(id, MissionEventDisposition.Answered);

        // Settled, so it is not owed an answer — and not on today, so it is not listed at all.
        Assert.Empty(gw.Reports.Compose(at).Decisions.OwnerMessages);
    }

    [Fact]
    public async Task The_same_words_sent_again_supersede_the_copy_nobody_has_taken()
    {
        var (_, _, db) = await TestEnv.Ready();
        var events = new MissionEventStore(db);
        var at = Midday();

        Assert.True(events.RecordOwnerMessage("close everything", at));
        Assert.True(events.RecordOwnerMessage("close everything", at.AddSeconds(20)));

        var rows = events.OfKind(MissionEventKind.Owner);
        Assert.Equal(2, rows.Count);
        Assert.Equal(MissionEventDisposition.Superseded, rows[0].Disposition);
        Assert.Equal(rows[1].Id, rows[0].DispositionDetail);
        // The later one is untouched and is the only one still waiting for a turn.
        Assert.Null(rows[1].Disposition);
    }

    [Fact]
    public async Task Different_words_are_never_superseded_because_the_subject_is_not_a_judgment_to_make()
    {
        var (_, _, db) = await TestEnv.Ready();
        var events = new MissionEventStore(db);
        var at = Midday();

        events.RecordOwnerMessage("close everything", at);
        events.RecordOwnerMessage("close everything please", at.AddSeconds(20));

        Assert.All(events.OfKind(MissionEventKind.Owner), e => Assert.Null(e.Disposition));
    }

    [Fact]
    public async Task A_message_a_turn_could_not_be_afforded_for_is_blocked_and_still_owed_a_turn()
    {
        var (_, _, db) = await TestEnv.Ready();
        var events = new MissionEventStore(db);
        var at = Midday();
        events.RecordOwnerMessage("how are we doing", at);
        var id = events.OfKind(MissionEventKind.Owner)[0].Id;

        Assert.True(events.SettleWithoutTurn(id, MissionEventDisposition.Blocked, "the ceiling is reached"));

        var row = events.Get(id)!;
        Assert.Equal(MissionEventDisposition.Blocked, row.Disposition);
        Assert.Equal("the ceiling is reached", row.DispositionDetail);
        // STILL OWED A TURN: the row is unconsumed, so the next affordable turn takes it.
        Assert.False(row.Consumed);
        Assert.Contains(id, events.Due(at.AddMinutes(1)).Select(e => e.Id));
    }

    [Fact]
    public async Task An_outcome_of_a_turn_cannot_be_recorded_without_one()
    {
        var (_, _, db) = await TestEnv.Ready();
        var events = new MissionEventStore(db);
        events.RecordOwnerMessage("anything", Midday());
        var id = events.OfKind(MissionEventKind.Owner)[0].Id;

        foreach (var claim in new[] { MissionEventDisposition.Answered, MissionEventDisposition.Failed })
            Assert.Throws<ArgumentException>(() => events.SettleWithoutTurn(id, claim));
    }

    [Fact]
    public async Task A_message_delegated_into_a_brief_keeps_that_disposition_when_the_turn_settles()
    {
        var (_, _, db) = await TestEnv.Ready();
        var events = new MissionEventStore(db);
        var at = Midday();
        events.RecordOwnerMessage("look into the opening range", at);
        var id = events.OfKind(MissionEventKind.Owner)[0].Id;
        events.Consume([id], "attempt-7", at);

        Assert.Equal(1, events.Delegated("attempt-7", "pub-abc"));
        Assert.Equal(MissionEventDisposition.Delegated, events.Get(id)!.Disposition);

        // The turn ALSO replied, and the ordinary outcome must not erase which brief it became: the
        // relay publishes before the loop settles, so without the guard `answered` lands last.
        events.Settle(id, MissionEventDisposition.Answered);
        Assert.Equal(MissionEventDisposition.Delegated, events.Get(id)!.Disposition);
        Assert.Equal("pub-abc", events.Get(id)!.DispositionDetail);
    }

    [Fact]
    public async Task A_wake_no_launch_consumed_is_not_delegated_by_somebody_elses_publication()
    {
        var (_, _, db) = await TestEnv.Ready();
        var events = new MissionEventStore(db);
        var at = Midday();
        events.RecordOwnerMessage("a question", at);

        Assert.Equal(0, events.Delegated("attempt-9", "pub-xyz"));
        Assert.Null(events.OfKind(MissionEventKind.Owner)[0].Disposition);
    }
}
