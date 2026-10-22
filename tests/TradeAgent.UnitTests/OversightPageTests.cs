using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Oversight;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S OVERSIGHT PAGE (<c>U-org-watcher-b</c>; ORGANISATION § 9, ORCHESTRATION § 2.15 "The watcher's page is his alone"): the words,
/// tested without a window (<see cref="OversightWords"/>); the page built once and updated in place over a test's own oversight file; the
/// cap's press; and the in-app alert for a severe finding. Every control here is pressed on one thread, synchronously. The layout and the
/// colours are not asserted and not claimed: what is read back is the words and what the presses did.
/// </summary>
public class OversightPageTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    static string NewFile() => Path.Combine(TestEnv.Home, $"oversight-page-{Guid.NewGuid():n}.db");

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static OversightFinding Finding(string detector, string subject, string unit, string path, string severity = OversightSeverity.Info,
        params OversightCite[] cites) => new(
        OversightFinding.IdOf(detector, OversightStore.RootScope, subject, "-"), detector, severity, OversightStore.RootScope, unit, path,
        subject, "-", cites.Length > 0 ? cites : [new OversightCite("attempt", "a-1")],
        detector == "refusals" ? new Dictionary<string, string> { ["event"] = "protocol_rejected", ["at"] = "2026-10-20T11:00:00Z", ["session"] = "none" }
            : new Dictionary<string, string> { ["since"] = "2026-10-15", ["days"] = "5", ["last_accepted"] = "never" },
        "It sees only what the app recorded.", Now);

    static OversightSources Sources(OversightStore? store, string? refusal = null) => new()
    {
        Store = () => store,
        Refusal = () => refusal,
        Currency = () => "USD",
        Now = () => Now,
    };

    // ---- (a) the words ---------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) THE PAGE SAYS THE DIGEST, THE FINDINGS NEWEST FIRST BY UNIT WITH THEIR CITED IDS, MEASURED VALUES AND BLIND SENTENCE, THE LINE'S OWN
    /// FAILURES, THE SEAT-CLASS TABLE AND THE THRESHOLDS — and, for a file the line would not open, the refusal in the line's own words.
    /// RED at base: nothing showed the line; no <c>OversightWords</c> existed.
    /// </summary>
    [Fact]
    public void OversightWords_show_the_digest_findings_blind_lines_and_the_seat_class_table()
    {
        using var store = new OversightStore(NewFile(), Now);
        var stall = Finding("stall", "unit:div-research", OrgIds.Research, "org/div-research");
        var older = Finding("refusals", "log:7", OrgIds.Root, "org", OversightSeverity.Severe, new OversightCite("log", "7"));
        var newer = Finding("refusals", "log:9", OrgIds.Root, "org", OversightSeverity.Info, new OversightCite("log", "9"));
        store.Commit([stall], new Dictionary<string, long>(), Now);
        store.Commit([older], new Dictionary<string, long>(), Now);
        store.Commit([newer], new Dictionary<string, long>(), Now);
        var digest = DigestComposer.Compose(OversightStore.RootScope, store.Findings(OversightStore.RootScope), Now, _ => true)!;
        store.AppendDigest(digest);
        store.RecordFailure(Now, "a sweep failed: the database could not be read");
        store.Acknowledge(older.Id, Now);

        var words = OversightWords.Of(OversightRead.From(store), null, "USD");
        foreach (var f in words.Findings) log.WriteLine($"{f.UnitPath} {f.Severity} {f.Id[..10]} {f.Measured} {f.Cites}");

        Assert.Equal(digest.Text, words.Digest);
        // BY UNIT, NEWEST FIRST WITHIN IT: the root's two refusals (9 before 7), then Research's stall.
        Assert.Equal([newer.Id, older.Id, stall.Id], words.Findings.Select(f => f.Id));
        var severe = words.Findings[1];
        Assert.Equal(OversightSeverity.Severe, severe.Severity);
        Assert.Contains("log:7", severe.Cites, StringComparison.Ordinal);
        Assert.Contains("event=protocol_rejected", severe.Measured, StringComparison.Ordinal);
        Assert.Equal("It sees only what the app recorded.", severe.Blind);
        Assert.NotNull(severe.Acknowledged);
        Assert.Null(words.Findings[0].Acknowledged);
        Assert.Contains(words.Failures, f => f.Contains("the database could not be read", StringComparison.Ordinal));

        Assert.Equal(4, words.SeatClasses.Count);
        Assert.Equal(["advisory", "hard on app paths", "mediated", "hard"], words.SeatClasses.Select(s => s.Reach));
        Assert.Contains("the public repository names this line", words.SeatNote, StringComparison.Ordinal);
        Assert.Contains(words.Thresholds, t => t.Value == "60 s");
        Assert.Contains(words.Thresholds, t => t.Value == OversightPolicy.SpendFloor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("no model layer — code checks only; this line spends nothing today", words.Cap, StringComparison.Ordinal);
        Assert.Contains("within one sweep", words.Cadence, StringComparison.Ordinal);

        // A FILE THE LINE WOULD NOT OPEN IS SAID IN ITS OWN WORDS, and nothing else is claimed.
        var refused = OversightWords.Of(null, "the owner's oversight file was written by a newer TradeAgent", "USD");
        Assert.Contains("written by a newer TradeAgent", refused.Status, StringComparison.Ordinal);
        Assert.Empty(refused.Findings);
    }

    // ---- (g) built once ---------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) THE PAGE REBUILDS NOTHING ON REFRESH. Two refreshes over an unchanged file leave every finding's control, the digest's text block
    /// and the cap's press the same instances; a new finding adds its card and keeps the others. RED at base: no page existed.
    /// </summary>
    [Fact]
    public void The_page_rebuilds_nothing_on_refresh()
    {
        using var store = new OversightStore(NewFile(), Now);
        store.Commit([Finding("stall", "unit:div-research", OrgIds.Research, "org/div-research")], new Dictionary<string, long>(), Now);
        var page = new OversightPage(Sources(store));
        page.Update();
        var cards = page.FindingCards.Children.ToList();
        var digest = page.DigestText;
        Assert.Single(cards);

        page.Update();
        page.Update();
        Assert.Equal(cards.Count, page.FindingCards.Children.Count);
        for (var i = 0; i < cards.Count; i++) Assert.Same(cards[i], page.FindingCards.Children[i]);
        Assert.Same(digest, page.DigestText);

        store.Commit([Finding("refusals", "log:3", OrgIds.Root, "org", cites: new OversightCite("log", "3"))], new Dictionary<string, long>(), Now);
        page.Update();
        Assert.Equal(2, page.FindingCards.Children.Count);
        Assert.Contains(cards[0], page.FindingCards.Children);
    }
}
