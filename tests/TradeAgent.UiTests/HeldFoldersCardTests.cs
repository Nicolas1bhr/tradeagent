using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE DASHBOARD'S "MOVE THESE ASIDE" CARD (<c>U-windows-cards-2</c>), rendered headless in the product's theme over a folder of the
/// test's own: it shows while a folder holds the AI, names the folder and how much it holds and nothing in it, arms on the first press
/// with the sentence of what the second will do, moves the folder aside on the second, and says what moved and how to put it back.
///
/// <para><b>RED on the base</b>: there was no card — a Windows owner with skills of his own had every AI seat refused and nothing to press.</para>
/// </summary>
public class HeldFoldersCardTests
{
    [Fact]
    public void The_card_shows_the_held_folder_arms_with_what_it_will_move_and_moves_it_aside_on_the_second_press()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradeagent-tests", "held-card", Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var skills = Path.Combine(profile, ".agents", "skills");
        Directory.CreateDirectory(Path.Combine(skills, "his-skill"));
        File.WriteAllText(Path.Combine(skills, "his-skill", "SKILL.md"), "his words");
        var seats = RuntimeCatalog.BuiltIn().Select(m => m.Seat).OfType<SeatPlan>().ToList();
        var activity = new List<string>();
        try
        {
            HeadlessUi.Run(() =>
            {
                var card = new HeldFoldersCard(new HeldFoldersSources
                {
                    Find = () => HeldFolders.Find(profile, null, seats),
                    Activity = activity.Add
                });
                var win = new Window { Width = 900, Height = 700, Content = card.Root };
                win.Show();
                win.UpdateLayout();

                Assert.True(card.Root.IsVisible);
                var shown = Texts(card.Root);
                Assert.Contains(shown, t => t.Contains(skills, StringComparison.Ordinal) && t.Contains("holds 1 item", StringComparison.Ordinal));
                Assert.DoesNotContain(shown, t => t.Contains("his-skill", StringComparison.Ordinal) || t.Contains("his words", StringComparison.Ordinal));
                Assert.Equal(HeldFoldersCard.MoveAside, card.MoveButton.Content);

                // THE FIRST PRESS ONLY ARMS, and names what the second will do.
                card.MoveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var aside = Path.Combine(profile, ".agents", $"skills ({HeldFolders.AsideMark})");
                Assert.Equal($"Yes — move 1 item to {aside}; nothing is deleted", card.MoveButton.Content);
                Assert.True(Directory.Exists(skills));

                // The five-second tick keeps the half-press.
                card.Update();
                Assert.Equal($"Yes — move 1 item to {aside}; nothing is deleted", card.MoveButton.Content);

                card.MoveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                win.UpdateLayout();
                Assert.False(Directory.Exists(skills));
                Assert.Equal("his words", File.ReadAllText(Path.Combine(aside, "his-skill", "SKILL.md")));
                Assert.True(card.Root.IsVisible);
                Assert.False(card.MoveButton.IsVisible);
                Assert.Contains("Nothing was deleted", card.Said, StringComparison.Ordinal);
                Assert.Contains("rename that folder to \"skills\" again", card.Said, StringComparison.Ordinal);
                Assert.Contains("it can start", card.Said, StringComparison.Ordinal);
                Assert.Contains(activity, a => a.Contains(aside, StringComparison.Ordinal) && a.Contains("two presses", StringComparison.Ordinal));

                // Closed, and with nothing held, the card goes away.
                var close = card.Root.GetLogicalDescendantsOf<Button>().Single(b => Equals(b.Content, HeldFoldersCard.Close));
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(card.Root.IsVisible);
                win.Close();
            });
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void With_nothing_held_the_card_is_not_shown()
    {
        HeadlessUi.Run(() =>
        {
            var card = new HeldFoldersCard(new HeldFoldersSources { Find = () => [], Activity = _ => { } });
            Assert.False(card.Root.IsVisible);
        });
    }

    static List<string> Texts(Control root) =>
        [.. root.GetLogicalDescendantsOf<TextBlock>().Select(t => t.Text ?? "")];
}

static class LogicalWalk
{
    public static IEnumerable<T> GetLogicalDescendantsOf<T>(this Control root) where T : Control =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>();
}
