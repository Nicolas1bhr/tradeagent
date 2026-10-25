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
/// THE OWNER'S "FOR YOU" PAGE AND ITS READS (<c>U-org-surface-b</c>; R24 §§ 4.3, 4.7; ORCHESTRATION §§ 2.5, 2.9): what reached the
/// root, his words still owed and why, the board's power proposals, and his presses — a veto that only takes away, and an activation
/// that is his only until a board seat is active. Store-level reads here; the chart-level checks are <see cref="OrgStructureTests"/>'.
/// </summary>
public class OwnerInboxTests
{
    static readonly DateTimeOffset At = DateTimeOffset.UtcNow.AddMinutes(-30);

    /// <summary>
    /// (1a) THE OWED WORDS ACROSS ROLES CARRY THEIR BLOCK REASON: the owner's words to the chair (the chair's own row), words to the
    /// Research Director blocked by a refused launch, and a third set already taken by a launch that ENDED (no turn can close it) — the
    /// one read answers the first two, oldest first, each with its addressee, the time received and its words, the blocked one with
    /// the block's reason as the app wrote it, and exactly what <see cref="MissionEventStore.OwnerWordsOpen"/> answers per role.
    /// RED at base: no <c>MissionEventStore.OwnerWordsOwed</c>.
    /// </summary>
    [Fact]
    public void Owed_words_across_roles_carry_their_block_reason()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        Assert.True(events.RecordOwnerMessage("what is the chair doing today", At));
        Assert.True(events.Raise("owner:research-1", MissionEventKind.Owner, At.AddMinutes(1),
            Json.Write(new MissionOwnerMessage("research, look at the funding skew", At.AddMinutes(1))), CouncilRoles.Research));
        const string why = "the Research Director's launch was refused: its folder could not be rebuilt.";
        Assert.Equal(1, events.BlockOwnerWords(CouncilRoles.Research, DateTimeOffset.UtcNow, why));

        // A THIRD, taken by a launch that has ENDED: no turn can close it, so it is not owed (the loop settles and re-raises it).
        Assert.True(events.RecordOwnerMessage("an older question", At.AddMinutes(2)));
        var taken = events.OfKind(MissionEventKind.Owner).Single(e => e.Payload!.Contains("an older question", StringComparison.Ordinal));
        var attempt = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = attempt, StartedAt = At.AddMinutes(3), ReservedCost = 0.10m, Role = CouncilRoles.Operations
        }, [taken.Id]).Admitted);
        Assert.True(new AiAttemptStore(db).End(attempt, 0, At.AddMinutes(4), null, null, null, null, null, null, null, null, null));

        var owed = events.OwnerWordsOwed();
        Assert.Equal(new[] { CouncilRoles.Operations, CouncilRoles.Research }, owed.Select(w => w.Addressee));
        Assert.Equal(new[] { "what is the chair doing today", "research, look at the funding skew" }, owed.Select(w => w.Text));
        Assert.Equal(new[] { At, At.AddMinutes(1) }, owed.Select(w => w.ReceivedAt));
        Assert.Equal(new string?[] { null, why }, owed.Select(w => w.BlockedWhy));
        Assert.Equal(events.OwnerWordsOpen(CouncilRoles.Operations).Concat(events.OwnerWordsOpen(CouncilRoles.Research)).Select(e => e.Id)
            .Order(StringComparer.Ordinal), owed.Select(w => w.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(owed, w => w.Id == taken.Id);
    }

    // ---- item 4: the veto of a board act, two presses ------------------------------------------------------------------------------

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static async Task OnTheInbox(Func<OwnerInboxPage, AppHost, Database, Task> body)
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try { await body(new OwnerInboxPage(OwnerInboxSources.For(host)), host, db); }
        finally { await host.Gateway.DisposeAsync(); }
    }

    static string Change(string field, object from, object to) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["field"] = field, ["from"] = from, ["to"] = to } });

    /// <summary>Seats made ACTIVE through U-board-b1's own seam — the only writer of <c>active</c> in this build — never a hand-built row.</summary>
    static void Active(Database db, params string[] seats)
    {
        foreach (var seat in seats) new BoardStore(db).SetSeatStateUnderTest(seat, BoardSeatStatus.Active, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// (4a) A VETO TAKES TWO PRESSES AND THE FIRST WRITES NOTHING (R24 Q2): a raise of the plan's day-total from 40 to 50, proposed by one
    /// seat and confirmed by another, shows on the owner's page with "Veto this act". The first press writes nothing — the policy's version,
    /// its decider and the act's status are what they were — and shows each field's value now and what the veto restores; a tick keeps it
    /// armed and showing. The second writes a version decided by the owner on the basis "veto" that sets the field back to 40, closes the
    /// act "vetoed", says so in the owner's words, and the card goes. RED at base: no <c>OwnerInboxPage</c>. The watched mutant: the press
    /// built with <c>Ui.ConfirmIf(label, () => null, veto)</c> — one press — turns this red.
    /// </summary>
    [Fact]
    public Task A_veto_takes_two_presses_and_the_first_writes_nothing() => OnTheInbox((page, host, db) =>
    {
        Active(db, "board-1", "board-2");
        var board = new BoardStore(db);
        var act = board.Propose(new BoardActRequest("board-1", null, Change("plan_day_total", 40, 50), "the plan runs short by noon", [], []),
            DateTimeOffset.UtcNow);
        board.Decide(act.Act.Id, "board-2", null, true, act.Act.IntentSha256, "agreed: the noon stop costs a review", DateTimeOffset.UtcNow);
        page.Update();
        var raised = new BoardReader(db).InForce();
        Assert.Equal(BoardDecider.Board, raised.Decider);

        var card = page.BoardVetoes.CardOf(act.Act.Id)!;
        var press = card.GetLogicalDescendants().OfType<Button>().Single();
        Assert.Equal(Labels.VetoBoardActPress, press.Content);
        var restores = card.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains('→') == true);
        Assert.False(restores.IsVisible);

        Press(press);
        var after = new BoardReader(db).InForce();
        Assert.Equal((raised.Version, raised.Decider, raised.BodySha256), (after.Version, after.Decider, after.BodySha256));
        Assert.Equal(BoardActEventKind.Confirmed, board.Status(act.Act.Id));
        Assert.True(Ui.IsArmed(press));
        Assert.True(restores.IsVisible);
        Assert.Contains(Labels.VetoField("plan_day_total", "50", "40"), restores.Text, StringComparison.Ordinal);
        page.Update();
        Assert.True(Ui.IsArmed(press));
        Assert.True(restores.IsVisible);

        Press(press);
        var vetoed = new BoardReader(db).InForce();
        Assert.Equal((raised.Version + 1, BoardDecider.Owner, BoardBasis.Veto), (vetoed.Version, vetoed.Decider, vetoed.Basis));
        Assert.Equal("40", vetoed.Fields.Single(f => f.Name == "plan_day_total").Value);
        Assert.Equal(BoardActEventKind.Vetoed, board.Status(act.Act.Id));
        Assert.Contains(host.Gateway.Log.RecentActivity(), r => r.Text.StartsWith($"You vetoed the security board's act {act.Act.Id}", StringComparison.Ordinal));
        Assert.Null(page.BoardVetoes.CardOf(act.Act.Id));
        return Task.CompletedTask;
    });

    /// <summary>
    /// (4b) A NARROWING ACT OFFERS NO VETO (R24 Q2: a veto never widens, and the UI offers none where the store would refuse): one seat
    /// lowers the plan's day-total from 40 to 30 at once; the gateway's preview refuses it, and the owner's page shows no card and no
    /// press for it — the empty line says why.
    /// </summary>
    [Fact]
    public Task A_narrowing_act_offers_no_veto() => OnTheInbox((page, host, db) =>
    {
        Active(db, "board-3");
        var narrowed = new BoardStore(db).Narrow(new BoardActRequest("board-3", null, Change("plan_day_total", 40, 30), "less is safer", [], []),
            DateTimeOffset.UtcNow);
        Assert.Equal(BoardActEventKind.Applied, narrowed.Status);
        page.Update();
        Assert.False(host.Gateway.PreviewBoardVeto(narrowed.Act.Id).Allowed);
        Assert.Null(page.BoardVetoes.CardOf(narrowed.Act.Id));
        Assert.Empty(page.BoardVetoCards.GetLogicalDescendants().OfType<Button>());
        Assert.Contains(page.BoardVetoCards.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Labels.ForYouBoardVetoesNone);
        return Task.CompletedTask;
    });
}
