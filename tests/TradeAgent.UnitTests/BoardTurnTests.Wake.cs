using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardTurnTests
{
    // ---- item 4: the wake and the packet ---------------------------------------------------------------------------------------------

    static List<MissionEvent> BoardWakes(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT id FROM mission_event WHERE kind=$k ORDER BY id", ("$k", MissionEventKind.Board));
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }).Select(id => new MissionEventStore(db).Get(id)!).ToList();

    static BoardActRequest Act(string seat, string intent, string why = "the seat's own reasons") => new(seat, null, intent, why, [], []);

    /// <summary>
    /// A PROPOSAL WAKES EVERY OTHER ACTIVE SEAT AND NOTHING ELSE DOES. With board-1, board-2 and board-3 active, board-1's proposal raises
    /// ONE <c>board</c> wake for board-2 and one for board-3 — never the proposer, never a dormant seat — each naming the act; a narrowing
    /// raises none. board-2's refusal closes its own wake and no other; board-3's confirmation closes the act and every wake left; an
    /// expired proposal and one the owner vetoes close theirs, each without a turn. In a running loop, a turn of the chair raises no look,
    /// renewal or ceiling for a board seat, and a board seat's own request to be woken raises nothing. RED before the unit: a proposal raised
    /// no wake. Watched red with the proposer woken too.
    /// </summary>
    [Fact]
    public async Task A_proposal_wakes_every_other_active_seat_and_nothing_else_does()
    {
        using (var db = TestEnv.NewDb())
        {
            Activate(db, "board-1", "board-2", "board-3");
            var store = new BoardStore(db);
            var at = DateTimeOffset.UtcNow;

            store.Narrow(Act("board-1", Change("plan_day_total", 40, 35)), at);
            Assert.Empty(BoardWakes(db));

            var proposed = store.Propose(Act("board-1", Change("plan_day_total", 35, 45)), at).Act;
            var wakes = BoardWakes(db);
            Assert.Equal(["board-2", "board-3"], wakes.Select(w => w.Role!).Order(StringComparer.Ordinal));
            Assert.All(wakes, w => Assert.Equal(proposed.Id, w.Payload));
            Assert.All(wakes, w => Assert.Equal(MissionEventIds.Board(proposed.Id, w.Role!), w.Id));
            Assert.Equal(WakeClass.Unblocking, WakePriority.ClassOf(MissionEventKind.Board));

            store.Decide(proposed.Id, "board-2", null, false, proposed.IntentSha256, "not on these figures", at);
            wakes = BoardWakes(db);
            Assert.True(wakes.Single(w => w.Role == "board-2").Consumed, "the refusing seat's wake stayed open");
            Assert.False(wakes.Single(w => w.Role == "board-3").Consumed, "another seat's wake closed on a refusal");

            store.Decide(proposed.Id, "board-3", null, true, proposed.IntentSha256, "the figures allow it", at);
            Assert.All(BoardWakes(db), w => Assert.True(w.Consumed, $"{w.Id} stayed open after the act closed"));
            Assert.All(BoardWakes(db), w => Assert.Equal(MissionEventDisposition.Withdrawn, w.Disposition));

            // EXPIRED, AND VETOED: closed without a turn.
            var expiring = store.Propose(Act("board-2", Change("plan_day_total", 45, 46)), at).Act;
            Assert.Equal(1, store.Expire(at + BoardLimits.ConfirmationLife + TimeSpan.FromMinutes(1)));
            var vetoed = store.Propose(Act("board-3", Change("plan_day_total", 45, 47)), at).Act;
            store.VetoBoardAct(vetoed.Id, at);
            foreach (var act in new[] { expiring, vetoed })
            {
                var its = BoardWakes(db).Where(w => w.Payload == act.Id).ToList();
                Assert.Equal(2, its.Count);
                Assert.All(its, w => Assert.True(w.Consumed && w.ConsumedBy == MissionEventStore.NoLaunch, $"{w.Id} was not closed without a turn"));
            }
        }

        // IN A RUNNING LOOP: the chair's turn raises no look, renewal or ceiling for any board seat, and a board seat's request to be woken
        // raises nothing.
        await using var life = await BoardLives.Begin("board-heartbeat", "board-1", "board-2");
        life.Script(CouncilRoles.Operations, ["usage 10 0 5", "final Looked."]);
        life.Script("board-1", [$"write {MissionLoopWakeFile} {{\"after_seconds\": 60}}", "usage 10 0 5", "final Asked to be woken."]);
        life.Wake(CouncilRoles.Operations);
        life.Wake("board-1");
        await life.RunUntil(() => life.AttemptsOf(CouncilRoles.Operations).Any(a => a.State == AiAttemptState.ENDED)
                                  && life.AttemptsOf("board-1").Any(a => a.State == AiAttemptState.ENDED), "the chair's and board-1's turns ended");
        var boardRows = life.Events().Where(e => BoardSeats.IsBoardId(e.Role)).ToList();
        Assert.True(boardRows.All(e => e.Kind == MissionEventKind.Self && e.Id.StartsWith("self:puppet-life-", StringComparison.Ordinal)),
            $"the clock raised a board seat a wake: {string.Join("; ", boardRows.Select(e => $"{e.Id} {e.Kind} {e.Role}"))}");
        Assert.Single(boardRows);
    }

    const string MissionLoopWakeFile = TradeAgent.AgentRuntime.MissionLoop.WakeFile;

    /// <summary>
    /// THE CONFIRMER'S PROMPT HOLDS NO RATIONALE: board-1's puppet proposes with a rationale carrying a marker and a forecast; board-2,
    /// woken by the proposal, receives an app-composed packet — the act's id, its intent and hash, its direction, its refs, the figures in
    /// force, the expiry and the commands — and not one word of the marker, the forecast, or any position's or watcher's record. Read from
    /// the puppet's recorded prompt. RED before the unit: no board seat turned. Watched red with the act's rationale rendered into the packet.
    /// </summary>
    [Fact]
    public async Task The_confirmers_prompt_holds_no_rationale()
    {
        await using var life = await BoardLives.Begin("board-blind", "board-1", "board-2");
        life.Script("board-1", [BoardLives.Propose(BoardLives.Change("plan_day_total", 40, 50), BoardLives.Rationale), "usage 100 0 10",
            "final Proposed."]);
        life.Script("board-2", ["trade board list", "usage 100 0 10", "final Read the act."]);
        life.Wake("board-1");
        await life.RunUntil(() => life.AttemptsOf("board-2").Any(a => a.State == AiAttemptState.ENDED), "board-2's turn ended");

        var propose = BoardLives.TradeSteps(life, "board-1", 1)[0];
        Assert.True((int?)propose["exit"] == 0, $"the proposal was refused: {BoardLives.Said(propose)}");
        var act = Assert.Single(new BoardStore(life.Host.Db).Acts()).Act;
        var prompt = life.Prompt("board-2", 1)!;
        log.WriteLine(prompt);

        foreach (var shown in new[] { act.Id, act.Intent, act.IntentSha256, "direction: widens", "plan_day_total = 40", "trade board decide",
                     act.ExpiresAt!.Value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) })
            Assert.Contains(shown, prompt, StringComparison.Ordinal);
        foreach (var hidden in new[] { BoardLives.Marker, "too small for the research load", "Operations Director", "ledger", "watcher" })
            Assert.DoesNotContain(hidden, prompt, StringComparison.OrdinalIgnoreCase);

        // AND THE ONE WHO PROPOSED IS NOT WOKEN FOR ITS OWN ACT: board-1 turned once, on the test's own wake.
        Assert.Single(life.AttemptsOf("board-1"));
        Assert.DoesNotContain(life.Events(), e => e.Kind == MissionEventKind.Board && e.Role == "board-1");
    }
}
