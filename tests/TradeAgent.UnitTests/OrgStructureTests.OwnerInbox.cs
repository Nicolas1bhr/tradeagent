using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S "FOR YOU" PAGE OVER A REAL CHART (<c>U-org-surface-b</c> items 2, 4, 7): escalations at the root and owed words, the
/// structural veto and the owner's activation in two presses, and the page built once — over the chart <see cref="OrgStructureTests"/>
/// builds, with the real verbs and a test host (<see cref="AppHost.Composed"/>). A board seat is made active only through U-board-b1's
/// own seam (<see cref="BoardStore.SetSeatStateUnderTest"/>), never a hand-built table.
/// </summary>
public partial class OrgStructureTests
{
    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static string Texts(Control root) =>
        string.Join("\n", root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    static Button PressIn(Control card) => card.GetLogicalDescendants().OfType<Button>().Single();

    static async Task OnTheInbox(Database db, Func<OwnerInboxPage, AppHost, Task> body)
    {
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try { await body(new OwnerInboxPage(OwnerInboxSources.For(host)), host); }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// (2) ESCALATIONS AT THE ROOT AND OWED WORDS SHOW ON THE OWNER'S INBOX: the subdivision head's escalation climbs past the division
    /// (whose head's recorded seat carries no verbs) to the root — a record with no wake — and the owner's words to the chair are blocked by a refused launch; a board seat proposes a
    /// change of its own composition. The page shows the escalation's reason as the head's quoted words, the head it skipped and why
    /// as the app's; the owner's words quoted as his, the block's reason as the app's; the proposal quoted and "applied by nothing". It
    /// raises no wake and, handed no press, holds none. RED at base: no <c>OwnerInboxPage</c>.
    /// </summary>
    [Fact]
    public void Escalations_at_the_root_and_owed_words_show_on_the_owners_inbox()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var escalation = org.RaiseEscalation(SubHead, null, "the division cannot fund the “skew” team and the chart has no head above it", null, Now);
        Assert.Null(escalation.Recipient);
        Assert.NotEmpty(escalation.Skipped);
        var events = new MissionEventStore(db);
        Assert.True(events.RecordOwnerMessage("chair, pause the skew work", Now.AddMinutes(-5)));
        const string blocked = "the Operations Director's launch was refused: the day's plan ceiling is reached.";
        Assert.Equal(1, events.BlockOwnerWords(CouncilRoles.Operations, Now, blocked));
        var board = new BoardStore(db);
        board.SetSeatStateUnderTest("board-1", BoardSeatStatus.Active, Now);
        board.ProposePower("board-1", null, BoardPowerTarget.Composition, "add a seat from a third vendor family", Now);
        var wakes = Count(db, "mission_event");

        var page = new OwnerInboxPage(new OwnerInboxSources { Org = () => org, Events = () => events, Board = () => new BoardReader(db) });
        var text = Texts(page.Root);

        Assert.Contains($"{SubHead} wrote: “the division cannot fund the ‘skew’ team and the chart has no head above it”", text, StringComparison.Ordinal);
        Assert.Contains(Labels.EscalationSkipped(escalation.Skipped), text, StringComparison.Ordinal);
        Assert.Contains($"On its way it skipped: {Div}: ", text, StringComparison.Ordinal);
        Assert.Contains($"{Labels.ForYouYou} wrote: “chair, pause the skew work”", text, StringComparison.Ordinal);
        Assert.Contains($"Not delivered: {blocked}", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"“{blocked}", text, StringComparison.Ordinal);
        Assert.Contains("board-1 wrote: “add a seat from a third vendor family”", text, StringComparison.Ordinal);
        Assert.Contains(Labels.PowerAppliedByNothing, text, StringComparison.Ordinal);
        Assert.Single(page.EscalationCards.Children);
        Assert.Single(page.OwedCards.Children);
        Assert.Single(page.PowerCards.Children);

        page.Update();
        Assert.Equal(wakes, Count(db, "mission_event"));
        Assert.Empty(page.Root.GetLogicalDescendants().OfType<Button>());
        Assert.Empty(page.Root.GetLogicalDescendants().OfType<TextBox>());
    }

    /// <summary>
    /// (4c) THE STRUCTURAL VETO RESTORES DORMANT IN TWO PRESSES: the division head closes a team (head and member ended); on the owner's
    /// page its card's first press writes nothing — the team stays closed, every count where it was, and a tick keeps the press armed —
    /// and the second restores the team DORMANT with both positions DORMANT, one <c>vetoed</c> event decided by the owner, one Activity
    /// line in his words, and the card gone. RED at base: no <c>AppHost.VetoStructure</c>.
    /// </summary>
    [Fact]
    public Task The_structural_veto_restores_dormant_in_two_presses()
    {
        var db = Chart();
        Aged(db, Old, Div, OrgUnitKind.Team, 15, OldHead);
        Seat(db, OldMember, Old);
        var closed = new OrgStore(db).Close(CloseAsk(Old), Now);
        return OnTheInbox(db, (page, host) =>
        {
            using var _ = db;
            var key = closed.EventId.ToString(CultureInfo.InvariantCulture);
            var press = PressIn(page.StructureVetoes.CardOf(key)!);
            Assert.Equal(Labels.VetoStructurePress, press.Content);
            var before = Written(db);

            Press(press);
            Assert.Equal(before, Written(db));
            Assert.Equal(OrgUnitStatus.Closed, new OrgStore(db).Units().Single(u => u.Id == Old).Status);
            page.Update();
            Assert.True(Ui.IsArmed(press));
            Assert.Same(press, PressIn(page.StructureVetoes.CardOf(key)!));

            Press(press);
            var org = new OrgStore(db);
            Assert.Equal(OrgUnitStatus.Dormant, org.Units().Single(u => u.Id == Old).Status);
            Assert.All(new[] { OldHead, OldMember }, p => Assert.Equal(OrgPositionStatus.Dormant, org.Position(p)!.Status));
            Assert.Equal(1, Count(db, "org_event", $"kind = 'vetoed' AND decider = '{OrgDecider.Owner}' AND detail LIKE 'event={key} %'"));
            Assert.Contains(host.Gateway.Log.RecentActivity(), r => r.Text.StartsWith($"You vetoed the organisation's change {key} (closed)", StringComparison.Ordinal));
            Assert.Null(page.StructureVetoes.CardOf(key));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// ACTIVATE IS TWO PRESSES AND REFUSED ONCE A BOARD SEAT IS ACTIVE (the orchestrator's ruling of 2026-10-24): two dormant members and
    /// a dormant position heading the root (the chief) — the page offers the two members, never the chief, labelled "yours until the security
    /// board is active". The first press writes nothing and a tick keeps it armed; the second activates, one <c>activated</c> event decided
    /// by the owner — and the activation gate still refuses the position's launch, because no cell proof is recorded (the press starts
    /// nothing). Then a press armed on the second hire, a board seat made active through U-board-b1's seam: the tick says it is the
    /// board's and hides the presses, and the armed second press is refused in words naming the board, nothing written. RED at base: no
    /// <c>AppHost.ActivatePosition</c>.
    /// </summary>
    [Fact]
    public Task Activate_is_two_presses_and_refused_once_a_board_seat_is_active()
    {
        // A TEAM OF TWO DORMANT POSITIONS beside the legacy pair, and nothing else active: the owner's unconfined-seat ceiling (3, code)
        // counts every active position this test host declares no seat for, so a chart of active heads would refuse the activation
        // on the ceiling first — a refusal of its own, OrgStructureTests.A_fourth_unconfined_seat_is_refused_whatever_the_setting_says.
        var db = TestEnv.NewDb();
        const string team = "team-ac71c0ffee", first = "pos-ac71c0ffee01", second = "pos-ac71c0ffee02", chief = "pos-c41efc0ffee1";
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", team), ("$p", OrgIds.Research), ("$at", Stamp));
        Seat(db, first, team, OrgPositionStatus.Dormant);
        Seat(db, second, team, OrgPositionStatus.Dormant);
        Seat(db, chief, OrgIds.Root, OrgPositionStatus.Dormant);
        Exec(db, "UPDATE org_unit SET head_position_id = $p WHERE id = $root", ("$p", chief), ("$root", OrgIds.Root));
        return OnTheInbox(db, (page, host) =>
        {
            using var _ = db;
            Assert.NotNull(page.Dormant.CardOf(first));
            Assert.NotNull(page.Dormant.CardOf(second));
            Assert.Null(page.Dormant.CardOf(chief));
            Assert.Contains(Labels.ActivateIsYoursUntilTheBoard, Texts(page.Root), StringComparison.Ordinal);
            Assert.Contains(Labels.YoursUntilTheBoardIsActive, Labels.ActivateIsYoursUntilTheBoard, StringComparison.Ordinal);

            var press = PressIn(page.Dormant.CardOf(first)!);
            var before = Written(db);
            Press(press);
            Assert.Equal(before, Written(db));
            page.Update();
            Assert.True(Ui.IsArmed(press));
            Press(press);
            Assert.True(new OrgStore(db).Position(first)!.Status == OrgPositionStatus.Active, Texts(page.Root));
            Assert.Equal(1, Count(db, "org_event", $"kind = 'activated' AND position_id = '{first}' AND decider = '{OrgDecider.Owner}'"));
            Assert.Null(page.Dormant.CardOf(first));
            // THE PRESS STARTS NOTHING: this host's seam still refuses the launch, and the activation gate in code refuses every
            // real-model seat of the now-active position before a RECORDED proven cell, whatever the press did; only a puppet passes.
            Assert.NotNull(host.ActivationRefusal(first));
            var standing = new OrgStore(db).Standing(first);
            foreach (var seat in new[] { SeatClasses.Broker, SeatClasses.Harness })
                Assert.Contains("no proven executor cell recorded", PositionActivation.Decide(first, standing, new SeatFacts
                {
                    Standing = standing, DeclaredClass = seat, ProvenCell = false, UnconfinedActive = [], UnconfinedSeatSetting = 3
                })!.Words, StringComparison.Ordinal);
            Assert.Null(PositionActivation.Decide(first, standing, new SeatFacts
            {
                Standing = standing, DeclaredClass = SeatClasses.Puppet, ProvenCell = false, UnconfinedActive = [], UnconfinedSeatSetting = 3
            }));

            var armed = PressIn(page.Dormant.CardOf(second)!);
            Press(armed);
            new BoardStore(db).SetSeatStateUnderTest("board-1", BoardSeatStatus.Active, Now);
            page.Update();
            Assert.False(page.ActivateCards.IsVisible);
            Assert.Contains(Labels.ActivateIsTheBoards, Texts(page.Root), StringComparison.Ordinal);
            var unchanged = Written(db);
            Press(armed);
            Assert.Equal(unchanged, Written(db));
            Assert.Equal(OrgPositionStatus.Dormant, new OrgStore(db).Position(second)!.Status);
            Assert.Contains("the security board's now that a board seat is active", Texts(page.Root), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// THE OWNER'S INBOX IS BUILT ONCE AND UPDATED IN PLACE (CLAUDE.md): with every list holding a card — an escalation, an owed word, a
    /// power proposal, a confirmed board act, a vetoable close and a dormant hire — a veto armed by its first press survives three ticks:
    /// the same card objects, the same number of controls, the press still armed. And the page's presses are exactly the veto and
    /// activation labels: no answer press, no box.
    /// </summary>
    [Fact]
    public Task The_owners_inbox_is_built_once_and_updated_in_place()
    {
        var db = Chart();
        var org = new OrgStore(db);
        org.RaiseEscalation(Head, null, "nobody below can take this", null, Now);
        Assert.True(new MissionEventStore(db).RecordOwnerMessage("chair, a question", Now.AddMinutes(-5)));
        Aged(db, Old, Div, OrgUnitKind.Team, 15, OldHead);
        var hired = org.Hire(new HireRequest(Head, null, Old, false, "a member for the team", ["hire"], [CurrentRun(db)], [Admissible()]), Now).Position.Id;
        Aged(db, Closing, Div, OrgUnitKind.Team, 15);
        var closed = org.Close(CloseAsk(Closing), Now);
        var board = new BoardStore(db);
        board.SetSeatStateUnderTest("board-1", BoardSeatStatus.Active, Now);
        board.SetSeatStateUnderTest("board-2", BoardSeatStatus.Active, Now);
        board.ProposePower("board-1", null, BoardPowerTarget.BoardBudget, "a larger share for the board", Now);
        var intent = System.Text.Json.JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["field"] = "plan_day_total", ["from"] = 40, ["to"] = 50 } });
        var act = board.Propose(new BoardActRequest("board-1", null, intent, "the plan runs short by noon", [], []), Now);
        board.Decide(act.Act.Id, "board-2", null, true, act.Act.IntentSha256, "agreed", Now);
        // the activation list is shown only while no seat is active: the seats above make it the board's, so its card is kept hidden.
        return OnTheInbox(db, (page, host) =>
        {
            using var _ = db;
            Assert.NotNull(page.Escalations.CardOf(org.EscalationsToOwner().Rows.Single().Id));
            Assert.Single(page.OwedCards.Children);
            Assert.Single(page.PowerCards.Children);
            var actCard = page.BoardVetoes.CardOf(act.Act.Id)!;
            var closeCard = page.StructureVetoes.CardOf(closed.EventId.ToString(CultureInfo.InvariantCulture))!;
            var hireCard = page.StructureVetoes.CardOf(org.Vetoable().Rows.Single(r => r.PositionId == hired).EventId.ToString(CultureInfo.InvariantCulture))!;
            var dormantCard = page.Dormant.CardOf(hired)!;
            var veto = PressIn(actCard);
            Press(veto);
            Assert.True(Ui.IsArmed(veto));

            var before = page.Root.GetLogicalDescendants().Count();
            var cards = new Control[] { actCard, closeCard, hireCard, dormantCard };
            for (var i = 0; i < 3; i++) page.Update();
            Assert.Equal(before, page.Root.GetLogicalDescendants().Count());
            Assert.Same(actCard, page.BoardVetoes.CardOf(act.Act.Id));
            Assert.Same(closeCard, page.StructureVetoes.CardOf(closed.EventId.ToString(CultureInfo.InvariantCulture)));
            Assert.Same(dormantCard, page.Dormant.CardOf(hired));
            Assert.All(cards, c => Assert.Contains(page.Root, c.GetLogicalAncestors()));
            Assert.True(Ui.IsArmed(veto));
            Assert.Equal(new BoardReader(db).InForce().Version, new BoardReader(db).Act(act.Act.Id)!.Events.Max(e => e.PolicyVersion ?? 0));

            var labels = page.Root.GetLogicalDescendants().OfType<Button>()
                .Select(b => Ui.IsArmed(b) ? Labels.VetoBoardActPress : (string)b.Content!).Distinct().Order(StringComparer.Ordinal);
            Assert.Equal(new[] { Labels.ActivatePositionPress, Labels.VetoBoardActPress, Labels.VetoStructurePress }.Order(StringComparer.Ordinal), labels);
            Assert.Empty(page.Root.GetLogicalDescendants().OfType<TextBox>());
            return Task.CompletedTask;
        });
    }
}
