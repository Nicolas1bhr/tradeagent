using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// "AT A GLANCE" IN WORDS (<c>U-ui-glance</c>), without a window: every figure is the read's, a paper result is called a simulation, and a
/// part that could not be read — or a book that is incomplete — is said as unknown with its reason, never drawn as a zero.
/// </summary>
public class GlanceWordsTests
{
    static GlanceRead Read(Action<Dictionary<string, string>>? unread = null)
    {
        var why = new Dictionary<string, string>(StringComparer.Ordinal);
        unread?.Invoke(why);
        return new GlanceRead
        {
            Names = OrgNames.None,
            Now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero),
            Unread = why
        };
    }

    static GlanceLine Line(GlanceRead r, string key) => GlanceWords.Of(r).Single(l => l.Key == key);

    [Fact]
    public void Each_campaign_says_its_trials_and_verdicts_left_from_its_ledger()
    {
        var r = Read() with { Campaigns = [new GlanceCampaign("btc-4h", 3, 40, 1, 3), new GlanceCampaign("es-open", 40, 40, 3, 3)] };
        var line = Line(r, GlanceWords.Campaigns);
        Assert.Equal("btc-4h: 37 trials left of 40 (3 used); 2 verdicts left of 3 (1 used).", line.Line);
        Assert.Equal("es-open: 0 trials left of 40 (40 used); 0 verdicts left of 3 (3 used).", line.Details);
        Assert.True(line.Caution);   // a spent budget is said in caution
    }

    [Fact]
    public void A_paper_run_is_a_simulation_and_an_incomplete_book_is_unknown_never_zero()
    {
        var r = Read() with
        {
            PaperRuns =
            [
                new GlancePaperRun("v-1a2b3c4d5e6f", "BTCUSDT", "running", new RunBook(0.5m, 60000m, 12.5m, 0.75m, 3, 1, null)),
                new GlancePaperRun("v-ffffeeeedddd", "ETHUSDT", "suspended", new RunBook(0m, 0m, 0m, 0m, 2, 0, "an order reports a fill the ledger lacks")),
                new GlancePaperRun("v-000011112222", "SOLUSDT", "running", new RunBook(0m, 0m, 0m, 0m, 0, 0, null)),
            ]
        };
        var line = Line(r, GlanceWords.Paper);
        Assert.Equal("Paper runs (simulations)", line.Label);
        Assert.Equal("v-1a2b3c4d5e6f in BTCUSDT, running: realised 11.75 after 0.75 in reported costs over 3 fills (1 reported no fee, so the "
                     + "real figure is a little worse); holds 0.5 — simulated.", line.Line);
        Assert.Contains("v-ffffeeeedddd in ETHUSDT, suspended: its result is unknown — its book is incomplete (an order reports a fill the ledger lacks).",
            line.Details, StringComparison.Ordinal);
        Assert.Contains("v-000011112222 in SOLUSDT, running: no fill yet, so no result.", line.Details, StringComparison.Ordinal);
        Assert.Contains("A paper result is a simulation: no real money was made or lost.", line.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_that_could_not_be_read_says_why_and_draws_no_figure()
    {
        var r = Read(u => { u["turns"] = "the database is locked"; u["campaigns"] = "no such table"; });
        Assert.Equal("Who is working could not be read: the database is locked", Line(r, GlanceWords.Working).Line);
        Assert.Equal("The campaigns could not be read: no such table", Line(r, GlanceWords.Campaigns).Line);
        Assert.True(Line(r, GlanceWords.Working).Caution);
        Assert.Equal("Unknown: nothing is metering this installation's AI turns.",
            Line(r with { Spend = AiSpendToday.NotMetered }, GlanceWords.Spend).Line);
    }

    [Fact]
    public void Next_wakes_name_each_active_position_and_a_stopped_loop_says_none_runs()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var r = Read() with
        {
            Loop = "stopped — the AI has not been started",
            Active = ["operations", "research"],
            Wakes = new Dictionary<string, DateTimeOffset> { ["operations"] = now.AddMinutes(-5) }
        };
        var line = Line(r, GlanceWords.Wakes);
        Assert.Equal("1 of 2 active positions have a wake queued; the soonest, Operations Director's, is due since 2026-10-31 11:55 UTC. "
                     + "The AI is stopped, so none runs until it is started.", line.Line);
        Assert.Equal("Operations Director: a wake is due since 2026-10-31 11:55 UTC\nResearch Director: nothing queued", line.Details);
    }
}
