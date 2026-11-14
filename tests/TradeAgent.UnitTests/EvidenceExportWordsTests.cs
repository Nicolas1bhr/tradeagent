using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-EVIDENCE-COUNTS item 2 (A2-3): THE COUNTS SAY WHAT THEY COUNT. M is the lineage's charged trials, TradeAgent's own backtests; an
/// export of research months into a position's folder lets its code search variants nobody charges. Words only: the report's verdict
/// line says what M counts every time it shows M, and says beside the figures when the version's author had exported research months
/// of the verdict's pair before submitting the version — exactly then, and never for another pair, another position or a later export.
/// </summary>
public class EvidenceExportWordsTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const string Exported = "had exported research months of BTCUSDT before it submitted it";

    /// <summary>What M counts, as every line that shows M says it (written out, so the test reads the words and not the constant).</summary>
    const string ChargedOnly =
        "M counts charged trials, which are TradeAgent's own backtests only: a position that exported research months may have searched more";

    /// <summary>An export the app served, recorded as <c>GatewayPipeServer.DataExport</c> records one.</summary>
    static void Export(Database db, string role, long dataset, string pair, DateTimeOffset at) =>
        Assert.NotEqual(0, new ToolCallStore(db).Record(new ToolCallRow
        {
            At = at,
            Attempt = "attempt-export",
            Role = role,
            Tool = Ops.DataExport,
            Argument = $"dataset {dataset.ToString(CultureInfo.InvariantCulture)} {pair} 1m v1, 59 bars "
                       + $"{Bar0:u} to {Bar0.AddMinutes(58):u} -> data/export-{pair}-1m-dataset{dataset}-x.csv",
            Bytes = 4096,
            Served = true
        }));

    static string VerdictLine(RefereeVerdictTests.World w) => Assert.Single(
        DailyReportText.Render(w.Gw.Reports.Compose(Midday())).Split('\n'),
        l => l.Contains($"PROMOTED version {w.VersionId[..12]}", StringComparison.Ordinal));

    /// <summary>
    /// THE EXPORT WORDS APPEAR EXACTLY WHEN THE AUTHOR EXPORTED THAT PAIR'S RESEARCH MONTHS BEFORE THE VERSION. Every line that shows M
    /// says what it counts. Another pair's export, another position's, and the author's own made after it submitted the version add
    /// nothing; the author's export of the verdict's pair before it does. RED at base: neither sentence was on the line. The watched
    /// mutant (the pair not asked: any served export by the author) says the export words after the other pair's export.
    /// </summary>
    [Fact]
    public async Task The_verdict_line_says_what_M_counts_and_names_an_export_of_its_pair_by_the_author_before_the_version()
    {
        var w = await RefereeVerdictTests.Given(frozenAt: Bar0);
        using var owned = w.Db;
        await using var gateway = w.Gw;
        var verdict = new Referee(w.Db, () => At).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.Promoted, verdict.Why);

        var line = VerdictLine(w);
        Assert.Contains("M 0 (0 without a readable stream), N_eff 0. " + ChargedOnly, line, StringComparison.Ordinal);
        Assert.Contains("TradeAgent's own backtests only: a position that exported research months may have searched more", line,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Exported, line, StringComparison.Ordinal);

        // ANOTHER PAIR, ANOTHER POSITION, AND THE AUTHOR'S OWN AFTER IT SUBMITTED THE VERSION: none is this version's search.
        Export(w.Db, CouncilRoles.Research, 99, "ETHUSDT", Bar0.AddDays(-1));
        Export(w.Db, CouncilRoles.Operations, w.Set.Id, "BTCUSDT", Bar0.AddDays(-1));
        Export(w.Db, CouncilRoles.Research, w.Set.Id, "BTCUSDT", Bar0.AddDays(1));
        Assert.DoesNotContain(Exported, VerdictLine(w), StringComparison.Ordinal);

        // THE AUTHOR'S EXPORT OF THE VERDICT'S PAIR BEFORE IT SUBMITTED THE VERSION.
        Export(w.Db, CouncilRoles.Research, w.Set.Id, "BTCUSDT", Bar0.AddDays(-1));
        line = VerdictLine(w);
        Assert.Contains($"The position that wrote this version {Exported}, so it may have searched more than M says and these figures "
            + "may overstate it", line, StringComparison.Ordinal);
        Assert.Contains(ChargedOnly, line, StringComparison.Ordinal);
    }

    /// <summary>Midday on the owner's local day, so the report's window is unambiguous (as <c>RefereeVerdictTests</c> reads it).</summary>
    static DateTimeOffset Midday()
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }
}
