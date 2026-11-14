using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.App;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE "FREE SPACE" CARD (<c>U-free-space</c>), rendered headless in the product's theme over a home of the test's own: shown while the
/// guard's pause stands, it removes NOTHING on its first press — that press lists what TradeAgent can let go, with the total, and arms the
/// button with the sentence of what the second will do — and the second removes exactly that, reads the disk again and says what was freed.
///
/// <para><b>RED on the base</b>: there was no card — the pause said "Free some space" and nothing in the app could.</para>
/// </summary>
public class FreeSpaceCardTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "tradeagent-tests", "free-card", Guid.NewGuid().ToString("N")[..12]);
    readonly string _home;
    readonly string _installer;
    readonly string _tape;

    public FreeSpaceCardTests()
    {
        _home = Path.Combine(_root, "TradeAgent");
        _installer = Path.Combine(_home, "updates", "0.2.0", "TradeAgent-Setup-0.2.0.exe");
        _tape = Path.Combine(_home, "state", "tape.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_installer)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_tape)!);
        File.WriteAllText(_installer, new string('x', 4096));
        File.WriteAllText(_tape, "the tape");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    FreeSpaceSources Sources(List<string> activity, Func<bool> shown, bool calm = false)
    {
        var roots = new FreeSpaceRoots(_home, v => v == "0.2.0");
        return new FreeSpaceSources
        {
            Look = () => FreeSpace.Inventory(roots, DateTimeOffset.UtcNow),
            Remove = plan => FreeSpace.Remove(plan, roots, DateTimeOffset.UtcNow),
            Read = () => new DiskSpace(30L << 30, 100L << 30),
            Shown = shown,
            Activity = activity.Add,
            Calm = calm,
            Off = work => Task.FromResult(work())
        };
    }

    const string Armed = "Yes — remove this item (4 KB). Your current records, the tape and your positions are kept.";

    /// <summary>
    /// THE TWO-PRESS GUARD: the first press removes nothing. A card whose first press removed (one press doing the removal) fails here.
    /// </summary>
    [Fact]
    public void The_first_press_only_lists_and_arms_and_the_second_removes_what_it_listed()
    {
        var activity = new List<string>();
        HeadlessUi.Run(() =>
        {
            var card = new FreeSpaceCard(Sources(activity, () => true));
            var win = new Window { Width = 900, Height = 700, Content = card.Root };
            win.Show();
            win.UpdateLayout();

            // DISARMED: the press, the promise of what is never on the list, and no list yet.
            Assert.True(card.Root.IsVisible);
            Assert.Equal(FreeSpaceCard.FreeSpaceLabel, card.FreeButton.Content);
            Assert.DoesNotContain(Texts(card.Root), t => t.Contains("installer", StringComparison.Ordinal));
            Assert.Same(Theme.CautionSoft, card.Root.Background);
            Assert.Same(Theme.Caution, card.Root.BorderBrush);

            // PRESS ONE: the list and the total on the card, the armed sentence on the button, and NOTHING removed.
            card.FreeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            win.UpdateLayout();
            Assert.True(File.Exists(_installer), "the first press removed the installer: one press did the removal");
            Assert.Equal(Armed, card.FreeButton.Content);
            var shown = Texts(card.Root);
            Assert.Contains(shown, t => t.StartsWith("The downloaded installer of TradeAgent 0.2.0, already installed — 4 KB, last written today", StringComparison.Ordinal));
            Assert.Contains("1 item, 4 KB in all.", shown);
            Assert.Empty(activity);

            // The five-second tick keeps the half-press.
            card.Update();
            Assert.Equal(Armed, card.FreeButton.Content);

            // PRESS TWO: exactly what was listed, the disk read again, the words, the activity log.
            card.FreeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            card.Pending!.GetAwaiter().GetResult();
            win.UpdateLayout();
            Assert.False(File.Exists(_installer));
            Assert.True(File.Exists(_tape));
            Assert.Equal(FreeSpaceCard.FreeSpaceLabel, card.FreeButton.Content);
            Assert.StartsWith("Removed 1 item and freed 4 KB. Your current records, the tape and your positions were kept.", card.Said, StringComparison.Ordinal);
            Assert.Contains("30 GB is free on this disk now.", card.Said, StringComparison.Ordinal);
            Assert.Contains(activity, a => a.Contains("two presses", StringComparison.Ordinal) && a.Contains("1 installer already installed", StringComparison.Ordinal));
            Assert.DoesNotContain(Texts(card.Root), t => t.StartsWith("The downloaded installer", StringComparison.Ordinal));

            // Closed, the words go; the pause still stands, so the card stays.
            var close = card.Root.GetLogicalDescendantsOf<Button>().Single(b => Equals(b.Content, FreeSpaceCard.Close));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(card.Said);
            Assert.True(card.Root.IsVisible);
            win.Close();
        });
    }

    [Fact]
    public void A_press_over_nothing_to_remove_arms_nothing_and_says_so()
    {
        Directory.Delete(Path.GetDirectoryName(_installer)!, recursive: true);
        var activity = new List<string>();
        HeadlessUi.Run(() =>
        {
            var card = new FreeSpaceCard(Sources(activity, () => true));
            card.FreeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(FreeSpaceCard.FreeSpaceLabel, card.FreeButton.Content);
            Assert.Equal(FreeSpaceCard.Nothing, card.Said);
            Assert.True(File.Exists(_tape));
            Assert.Empty(activity);
        });
    }

    [Fact]
    public void Without_a_pause_the_dashboard_card_is_not_shown_and_the_settings_card_is_a_calm_one()
    {
        HeadlessUi.Run(() =>
        {
            var hidden = new FreeSpaceCard(Sources([], () => false));
            Assert.False(hidden.Root.IsVisible);

            var settings = new FreeSpaceCard(Sources([], () => true, calm: true));
            Assert.True(settings.Root.IsVisible);
            Assert.Same(Theme.BgElevated, settings.Root.Background);
            Assert.Same(Theme.Line, settings.Root.BorderBrush);
        });
    }

    /// <summary>The guard's pause names the press instead of "Free some space" or "Remove some files".</summary>
    [Fact]
    public void The_pause_words_name_the_press()
    {
        var bytes = DiskWatch.PauseFor(new DiskSpace(1L << 30, 100L << 30), DateTimeOffset.UtcNow);
        var files = DiskWatch.PauseFor(new DiskSpace(50L << 30, 100L << 30, 100_000, 6_553_600), DateTimeOffset.UtcNow);
        foreach (var said in new[] { DiskWords.Paused(bytes, null), DiskWords.Paused(files, null) })
        {
            Assert.Contains($"Press \"{FreeSpaceCard.FreeSpaceLabel}\" to see what TradeAgent itself can remove, then press \"{DashboardPage.LetTheAiWork}\"", said, StringComparison.Ordinal);
            Assert.DoesNotContain("some space", said, StringComparison.Ordinal);
            Assert.DoesNotContain("some files", said, StringComparison.Ordinal);
        }
    }

    /// <summary>THE OWNER-WORDS GUARD over every sentence the card can say: no file name, folder path, command, id or build word.</summary>
    [Fact]
    public void Every_sentence_of_the_card_is_in_the_owners_words()
    {
        var now = DateTimeOffset.UtcNow;
        var older = new LetGoItem(LetGoKind.OlderHome, Path.Combine(_root, "TradeAgent-from-0.1.2-20261031-101500"), [new LetGoFile("x", 3L << 30, now.UtcDateTime)], [], now.UtcDateTime);
        var installer = new LetGoItem(LetGoKind.Installer, Path.Combine(_home, "updates", "0.2.0"), [new LetGoFile("y", 90L << 20, now.UtcDateTime)], [], now.UtcDateTime.AddDays(-3));
        var check = new LetGoItem(LetGoKind.CheckLeftover, Path.Combine(_home, "cell-bench", "0123456789ab"), [new LetGoFile("z", 12_000, now.UtcDateTime)], [], now.UtcDateTime.AddDays(-1));
        var plan = new FreeSpacePlan([older, installer, check], now);
        var done = new FreeSpaceDone([older, installer],
            [(check, LetGoSkip.Changed), (check, LetGoSkip.HoldsLink), (check, LetGoSkip.InUse), (installer, LetGoSkip.NoLongerLettable), (older, LetGoSkip.InUse), (installer, LetGoSkip.Busy)], 3L << 30, 2);
        var words = new List<string>
        {
            FreeSpaceCard.Armed(plan), FreeSpaceCard.Armed(new FreeSpacePlan([installer], now)), FreeSpaceCard.Done(done, new DiskSpace(30L << 30, 100L << 30)),
            FreeSpaceCard.Done(done, null), FreeSpaceCard.Logged(done), FreeSpaceCard.Nothing, FreeSpaceCard.LinuxReleases
        };
        words.AddRange(plan.Items.Select(i => FreeSpaceCard.Line(i, now)));
        HeadlessUi.Run(() => words.AddRange(Texts(new FreeSpaceCard(Sources([], () => true)).Root)));

        var hits = words.SelectMany(w => OwnerWordShapes.Of(w).Select(h => $"{h}: {w}")).ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits));
        Assert.Contains("Yes — remove these 3 items (3.1 GB), the older folder's own records and tape with it. Your current records, the tape and your positions are kept.", words);
        Assert.Contains("Removed 2 items and freed 3 GB. Your current records, the tape and your positions were kept; the older folder's own records and tape went with it.",
            words.Select(w => w.Split(" An older")[0].Split(" The installer")[0].Split(" A leftover")[0]));
        Assert.Contains(words, w => w.StartsWith("An older TradeAgent's folder, set aside when you started fresh on 31 Oct 2026.", StringComparison.Ordinal)
                                    || w.StartsWith("An older TradeAgent's folder, set aside when you started fresh on 1 Nov 2026.", StringComparison.Ordinal));
    }

    static List<string> Texts(Control root) =>
        [.. root.GetLogicalDescendantsOf<TextBlock>().Select(t => t.Text ?? "")];
}
