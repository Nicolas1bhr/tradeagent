using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-report item 2. The Report page's WORDS, and the one thing this page must never do: present the
/// Operations Director's note for some other day, or some other publication entirely, as the note on
/// the day the owner is reading. A note is a paid claim by an agent and the report is a measurement;
/// the page shows them side by side and says out loud when there is no note at all.
/// </summary>
public class ReportPageTests
{
    static DateTimeOffset Midday(int daysBack = 0)
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddDays(-daysBack).AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }

    static Publication Pub(string kind, string content, DateTimeOffset at) => new()
    {
        Id = Publication.IdOf(CouncilRoles.Operations, kind, content),
        Role = CouncilRoles.Operations,
        Revision = 1,
        Kind = kind,
        // NOBODY, deliberately: a note is for the owner's report, and a recipient here would commit a
        // delivery and a paid task for a role that was never asked to read it.
        Recipients = "",
        CreatedAt = at,
        Content = content
    };

    [Fact]
    public void A_day_with_no_report_says_so_and_offers_the_press_that_makes_one()
    {
        var w = ReportPage.Words("2026-09-08", body: null, note: null);

        Assert.Contains("No report has been written for 2026-09-08 yet", w.Written);
        Assert.Contains("Write it now", w.Written);
        Assert.Equal("", w.Body);
    }

    [Fact]
    public void A_report_that_exists_says_it_was_measured_and_that_nothing_in_it_is_inferred()
    {
        var w = ReportPage.Words("2026-09-08", body: "# TradeAgent — 2026-09-08\n", note: null);

        Assert.Contains("written by TradeAgent from what it measured", w.Written);
        Assert.Contains("Nothing in it is inferred", w.Written);
        Assert.Contains("no AI turn was spent producing it", w.Written);
    }

    [Fact]
    public void A_day_with_no_note_says_there_is_none_rather_than_leaving_a_blank()
    {
        var w = ReportPage.Words("2026-09-08", body: "x", note: null);

        Assert.False(w.HasNote);
        Assert.Contains("No note for this day", w.Note);
        Assert.Contains("only when something material changed", w.Note);
    }

    [Fact]
    public void A_day_with_a_note_shows_the_note_the_role_actually_wrote()
    {
        var w = ReportPage.Words("2026-09-08", body: "x", note: Pub(PublicationKind.Note,
            "the allowlist changed; NQ is no longer tradable", Midday()));

        Assert.True(w.HasNote);
        Assert.Equal("the allowlist changed; NQ is no longer tradable", w.Note);
    }

    [Fact]
    public async Task A_note_from_another_day_is_not_shown_as_this_days_note()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var store = new PublicationStore(db);
        store.Commit(Pub(PublicationKind.Note, "yesterday's note", Midday(1)), Midday(1));

        Assert.NotNull(gw.Reports.NoteFor(DailyReports.DayName(Midday(1))));
        Assert.Null(gw.Reports.NoteFor(DailyReports.DayName(Midday())));
    }

    [Fact]
    public async Task Another_kind_of_publication_on_the_same_day_is_not_shown_as_the_note()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var store = new PublicationStore(db);
        store.Commit(Pub(PublicationKind.Brief, "an agenda for Research", Midday()), Midday());

        Assert.Null(gw.Reports.NoteFor(DailyReports.DayName(Midday())));
    }
}
