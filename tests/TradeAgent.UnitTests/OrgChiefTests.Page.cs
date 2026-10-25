using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class OrgChiefTests
{
    // ---- item 7: his presses on the organisation's card ------------------------------------------------------------------------------

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// The page over a test's own store: the chief's presses run the owner's in-process route as the host does — the words of what was done,
    /// or the refusal's — over a closed list of two seats, a puppet's and a CLI's. No host, no model.
    /// </summary>
    static OrgSurfaceSources Sources(Database db) => new()
    {
        Org = () => new OrgStore(db),
        Attempts = () => new AiAttemptStore(db),
        Currency = () => "USD",
        Chief = new ChiefPresses(
            () => new TradeAgent.Core.Board.BoardReader(db).AnyActive,
            () => [Puppet, Codex],
            seat => Said(() => $"appointed {new OrgStore(db).AppointChief(seat, Known, Now).Chief}"),
            (chief, seat) => Said(() => $"replaced by {new OrgStore(db).ReplaceChief(chief, seat, Known, Now).Chief}"),
            chief => Said(() => new OrgStore(db).Chief() is { } sitting && sitting == chief
                ? $"activated {new OrgStore(db).Activate(sitting, _ => SeatClasses.Puppet, 3, Now, () => false).Id}"
                : $"{new OrgStore(db).Chief()} is the chief now, not {chief}: nothing was activated"),
            chief => Said(() => $"removed {new OrgStore(db).VacateChief(chief, Now).Was}"))
    };

    static string Said(Func<string> press)
    {
        try { return press(); }
        catch (TradeAgentException ex) { return ex.Message; }
    }

    static ChiefCard Card(OrganisationPage page)
    {
        page.Update();
        return page.Chief ?? throw new InvalidOperationException("the organisation's card carries no chief presses");
    }

    /// <summary>
    /// (7a) APPOINTING THE CHIEF TAKES TWO PRESSES AND THE FIRST WRITES NOTHING: on the organisation's card, labelled "yours until the security
    /// board is active", a press with no seat chosen says so and arms nothing; the seat is chosen from a closed list of presses; the first
    /// press of "Appoint a chief" only arms it — nothing written — and the second appoints the chief, DORMANT. Then "Replace the chief",
    /// "Activate" and "Remove the chief" are drawn, each two presses, and the card is the same instance throughout (built once). RED at base:
    /// the card had no presses.
    /// </summary>
    [Fact]
    public void Appointing_the_chief_takes_two_presses_and_the_first_writes_nothing()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var page = new OrganisationPage(Sources(db));
        var card = Card(page);
        var root = page.CardOf(OrgIds.Root);
        Assert.Contains(Labels.ChiefYoursUntilBoard, string.Join("\n", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(card.Root).OfType<TextBlock>().Select(t => t.Text)));
        Assert.True(card.Appoint.IsVisible);
        Assert.False(card.Replace.IsVisible || card.Activate.IsVisible || card.Remove.IsVisible);
        Assert.Equal(2, card.SeatChoices.Count);

        var before = Written(db);
        Press(card.Appoint);
        Assert.False(Ui.IsArmed(card.Appoint));
        Assert.Equal(Labels.ChiefChooseSeat, card.Done.Text);
        Press(card.SeatChoices[0]);
        Press(card.Appoint);
        Assert.True(Ui.IsArmed(card.Appoint));
        Assert.Equal(before, Written(db));
        Assert.Null(org.Chief());

        Press(card.Appoint);
        var chief = org.Chief()!;
        Assert.Equal((OrgPositionStatus.Dormant, "puppet"), (org.Position(chief)!.Status, org.Position(chief)!.SeatRuntime));
        Assert.Contains(chief, card.Done.Text, StringComparison.Ordinal);
        page.Update();
        Assert.Same(card, page.Chief);
        Assert.Same(root, page.CardOf(OrgIds.Root));
        Assert.False(card.Appoint.IsVisible);
        Assert.True(card.Replace.IsVisible && card.Activate.IsVisible && card.Remove.IsVisible);

        Press(card.Activate);
        Assert.Equal(OrgPositionStatus.Dormant, org.Position(chief)!.Status);
        Press(card.Activate);
        Assert.Equal(OrgPositionStatus.Active, org.Position(chief)!.Status);
        page.Update();
        Assert.False(card.Activate.IsVisible);

        Press(card.Remove);
        Assert.Equal(chief, org.Chief());
        Press(card.Remove);
        Assert.Null(org.Chief());
        page.Update();
        Assert.True(card.Appoint.IsVisible);
        Assert.False(card.Remove.IsVisible);
    }

    /// <summary>
    /// (7b) THE APPOINTMENT PRESSES ARE GONE ONCE A BOARD SEAT IS ACTIVE: with a chief sitting and a half-pressed "Replace the chief", a board
    /// seat made active through U-board-b1's own path hides the seat list, "Appoint a chief", "Replace the chief" and "Activate" — the half-press
    /// disarmed — says the board appoints now, and leaves "Remove the chief", which still ends the chief. A press that arrives anyway is
    /// refused by the store in words naming the board, nothing written. RED at base: the card had no presses.
    /// </summary>
    [Fact]
    public void The_appointment_presses_are_gone_once_a_board_seat_is_active()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var page = new OrganisationPage(Sources(db));
        var chief = org.AppointChief(Puppet, Known, Now).Chief!;
        var card = Card(page);
        Press(card.SeatChoices[1]);
        Press(card.Replace);
        Assert.True(Ui.IsArmed(card.Replace));

        ActivateBoard(db, "board-1");
        page.Update();
        Assert.False(card.Appoint.IsVisible || card.Replace.IsVisible || card.Activate.IsVisible);
        Assert.False(Ui.IsArmed(card.Replace));
        Assert.False(card.SeatRow.IsVisible);
        Assert.Contains(Labels.ChiefIsTheBoards, string.Join("\n", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(card.Root).OfType<TextBlock>().Select(t => t.Text)));
        Assert.True(card.Remove.IsVisible);

        var before = Written(db);
        Press(card.Replace);
        Press(card.Replace);
        Assert.Contains("the security board's now that a board seat is active", card.Done.Text, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        Assert.Equal(chief, org.Chief());

        Press(card.Remove);
        Press(card.Remove);
        Assert.Null(org.Chief());
        Assert.Equal(OrgPositionStatus.Ended, org.Position(chief)!.Status);
    }

    /// <summary>
    /// (7c) A HALF-PRESS IS ABOUT THE CHIEF IT WAS ARMED FOR (fix pass 1; Codex P1): "Remove the chief" armed while A sits, then a board act
    /// replaces A with B. A refresh that shows B disarms the press, so the next press only arms it again and B still sits; and with no
    /// refresh between, the second press acts on A, the chief it was armed for — refused as moved, nothing written, B still sitting. Anything
    /// that removes permission is two presses about its subject. RED at base: the second press removed B, never confirmed.
    /// </summary>
    [Fact]
    public void A_half_press_is_about_the_chief_it_was_armed_for()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var board = Board(db);
        var page = new OrganisationPage(Sources(db));
        var a = org.AppointChief(Puppet, Known, Now).Chief!;
        ActivateBoard(db, "board-1", "board-2");
        var card = Card(page);

        string Replaced(string from)
        {
            var p = board.Propose(Act("board-1", ChiefIntent(from, Puppet)), Now);
            board.Decide(p.Act.Id, "board-2", null, true, p.Act.IntentSha256, "my own reading", Now);
            return org.Chief()!;
        }

        // REFRESHED BETWEEN: the press armed for A is disarmed once the card shows B.
        Press(card.Remove);
        Assert.True(Ui.IsArmed(card.Remove));
        var b = Replaced(a);
        page.Update();
        Assert.False(Ui.IsArmed(card.Remove), $"the press armed for {a} stayed armed once {b} sat");
        Press(card.Remove);
        Assert.Equal(b, org.Chief());
        Assert.True(Ui.IsArmed(card.Remove));

        // NOT REFRESHED BETWEEN: armed for B, the board seats C; the second press names B, and is refused as moved.
        var c = Replaced(b);
        var before = Written(db);
        Press(card.Remove);
        Assert.Equal(c, org.Chief());
        Assert.Equal(before, Written(db));
        Assert.Contains($"{c} is the chief now, not {b}", card.Done.Text, StringComparison.Ordinal);
        Assert.False(Ui.IsArmed(card.Remove));
    }
}
