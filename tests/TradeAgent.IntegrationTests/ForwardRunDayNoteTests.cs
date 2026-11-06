using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE NOTE AN AUTHOR READS TELLS THE TRUTH, ONCE (R28 P3 and G1). A run's "net" is what it made, not what it spent: open quantity is marked at the
/// last close. A day's note is that day's figures, said on the live bar that closed the day, so the runner's replay from minute one every 5 s does
/// not publish every past day again each time a fill lands. Over the real runner on the real paper connector; nothing reaches a venue.
/// </summary>
public class ForwardRunDayNoteTests(ITestOutputHelper log)
{
    /// <summary>Ten minutes before a UTC midnight, so minute 10 opens the next day.</summary>
    static readonly DateTimeOffset Evening = new(2026, 9, 19, 23, 50, 0, TimeSpan.Zero);

    const string Day = "2026-09-19", NextDay = "2026-09-20";

    const string Program =
        "instrument BTCUSDT\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\nsize fixed 1\n"
        + "exit when close < 90\nentry when close > 100\n";

    static List<Publication> Notes(ForwardRunnerTests.Rig rig, string day) =>
        [.. new PublicationStore(rig.Db).By(TradingGateway.PaperAllocatorRole)
            .Where(n => n.Content.Contains($"the UTC day {day} closed over it", StringComparison.Ordinal))];

    /// <summary>Costs the run has been charged so far, whatever the paper connector declares, so "net" can be stated exactly.</summary>
    static decimal Costs(ForwardRunnerTests.Rig rig) => rig.Gw.Deployments.FillsOf(rig.Deployment).Sum(f => f.Fee ?? 0m);

    /// <summary>In at 102 at minute 3's open, and flat 110 up to the bar before midnight.</summary>
    static async Task LongInto(ForwardRunnerTests.Rig rig)
    {
        rig.Bar(1, 99m, 101m, 98m, 101m);
        await rig.Runner.AdvanceAsync();
        rig.Bar(2, 101m, 102m, 100m, 101m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        rig.Bar(3, 102m, 103m, 101m, 102m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        for (var minute = 4; minute <= 9; minute++)
        {
            rig.Bar(minute, 110m, 110m, 110m, 110m);
            await rig.Gw.RefreshHealthAsync();
            await rig.Runner.AdvanceAsync();
        }

        var entry = Assert.Single(rig.Gw.Deployments.FillsOf(rig.Deployment));
        Assert.Equal(102m, entry.Price);
    }

    /// <summary>
    /// A LONG POSITION READS AS WHAT IT MADE (R28 P3): one bought at 102 and last closing at 110 is up 8 less its costs.
    ///
    /// <para>RED at the base: <c>net -102</c>, the cash that left, a loss of about its notional on a position that is up.</para>
    /// </summary>
    [Fact]
    public async Task A_day_note_marks_what_is_open_at_that_days_last_close()
    {
        await using var rig = await ForwardRunnerTests.ReadyAsync(Program, origin: Evening);
        await LongInto(rig);

        rig.Bar(10, 111m, 111m, 111m, 111m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        var note = Assert.Single(Notes(rig, Day));
        log.WriteLine(note.Content);
        var net = 8m - Costs(rig);
        Assert.Contains($"net {AllocationRow.Num(net)} after", note.Content, StringComparison.Ordinal);
        Assert.Contains("with 1 still open at an average of 102, marked at the last close of 110", note.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE NOTE A DAY, WHATEVER THE RUN DOES NEXT (R28 G1): passes replaying the boundary and a fill landing the next day leave the day's note alone,
    /// and so does a second runner replaying from minute one. The next day's note is that day's own figures, a realised loss with nothing open.
    ///
    /// <para>RED at the base: the fill made a past day's content new, so the same day had a second publication, delivery and <c>in/</c> file.</para>
    /// </summary>
    [Fact]
    public async Task A_past_day_is_said_once_and_a_later_fill_does_not_say_it_again()
    {
        await using var rig = await ForwardRunnerTests.ReadyAsync(Program, origin: Evening);
        await LongInto(rig);

        rig.Bar(10, 111m, 111m, 111m, 111m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        var said = Assert.Single(Notes(rig, Day));

        await rig.Runner.AdvanceAsync();
        await rig.Runner.AdvanceAsync();

        // THE NEXT DAY: a close under the exit, and the sell fills at 81 the way the buy filled at 102.
        rig.Bar(11, 88m, 88m, 88m, 88m);
        await rig.Runner.AdvanceAsync();
        rig.Bar(12, 85m, 85m, 85m, 85m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        rig.Bar(13, 81m, 85m, 80m, 85m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        Assert.Equal(2, rig.Gw.Deployments.FillsOf(rig.Deployment).Count);

        await new ForwardRuns(rig.Gw, rig.Db, () => rig.Clock.At).AdvanceAsync();

        var notes = Notes(rig, Day);
        foreach (var n in notes) log.WriteLine($"{n.Id[..12]}: {n.Content[..90]}");
        Assert.Equal(said.Id, Assert.Single(notes).Id);

        // THE NEXT UTC MIDNIGHT: that day's own note says the realised figure, with nothing open.
        rig.Minutes(14, 1450, _ => 85m);
        await rig.Runner.AdvanceAsync();

        var next = Assert.Single(Notes(rig, NextDay));
        log.WriteLine(next.Content);
        var net = -21m - Costs(rig);
        Assert.Contains($"net {AllocationRow.Num(net)} after", next.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("still open", next.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A FILL ON THE DAY'S LAST BAR IS THAT DAY'S (Codex review): it is stamped when it settled, after midnight, and it landed on the 23:59 bar. Its day's
    /// note counts it and marks it at that bar's close. RED with the day cut on the fill's stamp: <c>0 simulated fills</c>, a day that closed long read as flat.
    /// </summary>
    [Fact]
    public async Task A_fill_on_the_days_last_bar_is_in_that_days_note()
    {
        await using var rig = await ForwardRunnerTests.ReadyAsync(Program, origin: Evening);

        for (var minute = 1; minute <= 6; minute++)
        {
            rig.Bar(minute, 99m, 99m, 99m, 99m);
            await rig.Runner.AdvanceAsync();
        }

        rig.Bar(7, 99m, 101m, 98m, 101m);
        await rig.Runner.AdvanceAsync();
        rig.Bar(8, 101m, 102m, 100m, 101m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        rig.Bar(9, 102m, 110m, 101m, 110m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        var fill = Assert.Single(rig.Gw.Deployments.FillsOf(rig.Deployment));
        log.WriteLine($"the fill: {fill.Price}, stamped {fill.At:u}");
        Assert.True(fill.At >= Evening.AddMinutes(10), "the fixture is about a fill stamped after the day it landed on");

        rig.Bar(10, 111m, 111m, 111m, 111m);
        await rig.Runner.AdvanceAsync();

        var note = Assert.Single(Notes(rig, Day));
        log.WriteLine(note.Content);
        Assert.Contains("1 simulated fill,", note.Content, StringComparison.Ordinal);
        Assert.Contains($"net {AllocationRow.Num(8m - Costs(rig))} after", note.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A BAR REPLAYED BEHIND THE CURSOR SAYS NOTHING (R28 G1): every pass replays from minute one, and a day boundary the cursor has passed is accounted
    /// for. A note is said on a live bar or not at all, even for a day that has none. RED with the note said on every boundary replayed.
    /// </summary>
    [Fact]
    public async Task A_day_boundary_behind_the_cursor_says_nothing()
    {
        await using var rig = await ForwardRunnerTests.ReadyAsync(Program, origin: Evening);
        rig.Minutes(1, 12, _ => 99m);

        Assert.True(rig.Gw.Deployments.AdvanceCursor(rig.Deployment.Id, Evening.AddMinutes(11)));
        await rig.Runner.AdvanceAsync();

        Assert.Empty(Notes(rig, Day));
    }

    /// <summary>A RUN'S END SAYS ITS P&amp;L TOO (R28 P3): a close that rests until the next open leaves the run holding at the end, marked at the newest close.</summary>
    [Fact]
    public async Task A_runs_end_note_marks_what_is_still_open_at_the_last_close()
    {
        await using var rig = await ForwardRunnerTests.ReadyAsync(Program, origin: Evening);
        await LongInto(rig);

        var id = rig.Deployment.Id;
        await rig.Gw.EndPaperDeploymentAsync(id, "test: the run ended by itself");

        var note = Assert.Single(new PublicationStore(rig.Db).By(TradingGateway.PaperAllocatorRole),
            n => n.Content.Contains("test: the run ended by itself", StringComparison.Ordinal));
        log.WriteLine(note.Content);
        var net = 8m - Costs(rig);
        Assert.Contains($"net {AllocationRow.Num(net)} after", note.Content, StringComparison.Ordinal);
        Assert.Contains("marked at the last close of 110", note.Content, StringComparison.Ordinal);
    }
}
