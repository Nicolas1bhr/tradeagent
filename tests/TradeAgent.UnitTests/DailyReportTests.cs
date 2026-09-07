using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-report item 1. The owner's daily report, composed by the app from one snapshot.
///
/// <para>The part that can be wrong in a way nobody would notice is the arithmetic of ABSENCE: a
/// figure TradeAgent could not work out has to read as a dash with a reason beside it, never as a
/// zero and never as a dash nobody can account for. The sharpest case is the net after costs, which
/// is withheld whenever a single fill's fee was never reported — computed as though that fee were
/// zero it is wrong in the owner's favour every time, and it is the number a profitability claim
/// would rest on.</para>
/// </summary>
public class DailyReportTests
{
    /// <summary>
    /// Midday on the owner's own local day, so the window this report cuts is unambiguous whatever
    /// hour the suite happens to run at.
    /// </summary>
    static DateTimeOffset Midday()
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }

    static Fill F(DateTimeOffset at, OrderSide side, decimal qty, decimal price, decimal? fee, string id) =>
        new("SIM-001", id, at, "ES", side.ToString(), qty, price, "FB-1", null, null, null,
            FillSource.Event, fee, at);

    [Fact]
    public async Task A_day_with_a_fee_the_platform_never_reported_withholds_the_net_and_names_the_fee()
    {
        var (gw, _, _) = await TestEnv.Ready();
        var at = Midday();
        gw.Fills.Record(F(at.AddHours(-2), OrderSide.Buy, 1m, 100m, 2m, "x1"));
        gw.Fills.Record(F(at.AddHours(-1), OrderSide.Sell, 1m, 110m, null, "x2"));

        var report = gw.Reports.Compose(at);
        var text = DailyReportText.Render(report);

        // The figure itself is absent, and it is absent because a cost is unknown.
        Assert.Null(report.Performance.Net);
        Assert.Equal(1, report.Performance.FeesUnknownFills);

        // And the document says both halves: the dash where the number would be, and the reason.
        Assert.Contains("net after costs: —", text);
        Assert.Contains("did not report a fee", text);
        Assert.Contains("missing (net after costs):", text);
    }

    [Fact]
    public async Task A_day_where_every_fee_is_known_prints_the_net_as_a_number()
    {
        var (gw, _, _) = await TestEnv.Ready();
        var at = Midday();
        gw.Fills.Record(F(at.AddHours(-2), OrderSide.Buy, 1m, 100m, 1m, "y1"));
        gw.Fills.Record(F(at.AddHours(-1), OrderSide.Sell, 1m, 110m, 1m, "y2"));

        var report = gw.Reports.Compose(at);

        Assert.NotNull(report.Performance.Net);
        Assert.Equal(0, report.Performance.FeesUnknownFills);
        Assert.DoesNotContain("net after costs: —", DailyReportText.Render(report));
    }

    [Fact]
    public async Task Nothing_the_app_did_not_measure_is_printed_as_a_zero()
    {
        var (gw, _, _) = await TestEnv.Ready();
        var text = DailyReportText.Render(gw.Reports.Compose(Midday()));

        // No loop reported to this report, so the mission state is a dash and the gap says why.
        Assert.Contains("state: —", text);
        Assert.Contains("no mission loop reported to this report", text);

        // Nothing metered the AI, so the day's spending is a dash rather than a free day.
        Assert.Contains("nothing is metering this installation's AI turns", text);

        // The one thing a report written from the ledger alone cannot know, said where it would be.
        Assert.Contains("what is still OPEN is not valued", text);

        // And the operating costs it does not measure at all are named rather than omitted.
        Assert.Contains("no figure here is a complete cost of running this", text);
    }

    [Fact]
    public async Task The_report_says_it_is_measured_never_inferred_and_costs_no_turn()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var before = new MissionEventStore(db).Due(DateTimeOffset.UtcNow.AddYears(1)).Count;

        var draft = gw.Reports.Write(Midday());

        Assert.Contains("Nothing in it is inferred", draft.Text);
        Assert.Contains("no AI turn was spent producing it", draft.Text);
        // THE PROPERTY: writing a report raises no reason to spend the owner's money on a turn.
        Assert.Equal(before, new MissionEventStore(db).Due(DateTimeOffset.UtcNow.AddYears(1)).Count);
    }

    [Fact]
    public async Task The_day_is_written_to_a_file_the_app_owns_with_LF_endings()
    {
        var (gw, _, _) = await TestEnv.Ready();
        var at = Midday();
        var draft = gw.Reports.Write(at);

        var path = DailyReports.FileFor(DailyReports.DayName(at));
        Assert.True(File.Exists(path));
        var written = File.ReadAllText(path);
        Assert.Equal(draft.Text, written);
        Assert.DoesNotContain("\r\n", written);
        Assert.Contains(DailyReports.DayName(at), gw.Reports.Days());
        Assert.Equal(written, gw.Reports.Read(DailyReports.DayName(at)));
    }

    [Fact]
    public async Task A_day_that_has_no_file_is_owed_one_and_a_day_that_has_is_not()
    {
        var (gw, _, _) = await TestEnv.Ready();
        var at = Midday();

        var owed = gw.Reports.Owed(at, lookBackDays: 3);
        Assert.Equal(3, owed.Count);
        Assert.DoesNotContain(DailyReports.DayName(at), owed.Select(DailyReports.DayName));

        gw.Reports.Write(at.AddDays(-1));
        Assert.DoesNotContain(DailyReports.DayName(at.AddDays(-1)),
            gw.Reports.Owed(at, lookBackDays: 3).Select(DailyReports.DayName));
    }

    [Fact]
    public void The_app_refuses_its_own_over_long_draft_and_says_so()
    {
        var at = Midday();
        var report = new DailyReport
        {
            Identity = new ReportIdentity
            {
                Day = DailyReports.DayName(at), From = at, To = at.AddDays(1),
                Timezone = "UTC", SnapshotAt = at
            },
            Decisions = new ReportDecisions
            {
                OwnerMessages = [.. Enumerable.Range(0, 300)
                    .Select(n => new ReportOwnerMessage($"message {n}", at, null))]
            }
        };

        var draft = DailyReportText.Draft(report);

        Assert.NotNull(draft.Rejected);
        Assert.Contains($"past the {DailyReportText.MaxLines} this report may be", draft.Rejected);
        Assert.True(draft.Lines <= DailyReportText.MaxLines);
        // Nothing was shortened behind the reader's back: the file is the refusal, not a trimmed report.
        Assert.DoesNotContain("message 0", draft.Text);
    }

    [Fact]
    public async Task An_ordinary_day_fits_inside_the_limit()
    {
        var (gw, _, _) = await TestEnv.Ready();
        var at = Midday();
        for (var n = 0; n < 40; n++)
            gw.Fills.Record(F(at.AddMinutes(-n - 1), n % 2 == 0 ? OrderSide.Buy : OrderSide.Sell,
                1m, 100m + n, 1m, $"z{n}"));

        var draft = gw.Reports.Write(at);
        Assert.Null(draft.Rejected);
        Assert.True(draft.Lines <= DailyReportText.MaxLines,
            $"an ordinary day rendered {draft.Lines} lines");
    }
}
