using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BOARD'S LIVES ON PUPPET SEATS (<c>U-board-b2</c> items 5–7; R24 §§ 4.1–4.3; <c>docs/ORCHESTRATION.md</c> § 7): whole lives of the
/// security board through the real kernel — the host's own composition, the loop, admission on the board's own budget, the activation
/// gate on each seat's recorded class, the session, the real <c>trade</c> with the launch's own grant, the pipe, the board's store and
/// the gateway's policy in force. NO MODEL IS CALLED: every seat is a puppet, made active only by the test host's seam
/// (<c>BoardStore.SetSeatStateUnderTest</c>), and no real money moves.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class PuppetBoardLivesTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    /// <summary>
    /// THE OBSERVABLE RESULT (<c>U-board-b2</c>): board-1 is woken and its puppet proposes a widening through <c>trade board propose</c>
    /// with its launch's own grant; board-2 — woken by that proposal alone — reads in its prompt the intent and its hash and not one word
    /// of board-1's rationale, and confirms through <c>trade board decide</c>; the gateway's policy in force is then version 2, decided by
    /// <c>board</c>. Each seat ran on the class it was founded on, on the board's own budget, funded by no unit; no model ran.
    /// </summary>
    [Fact]
    public async Task A_widening_proposed_by_one_seat_and_confirmed_by_another_is_in_force()
    {
        await using var life = await BoardLives.Begin("observable", "board-1", "board-2");
        var store = new BoardStore(life.Host.Db);
        life.Script("board-1", [BoardLives.Propose(BoardLives.Change("plan_day_total", 40, 50), BoardLives.Rationale), "signal proposed",
            "await confirmer-scripted", "usage 100 0 10", "final Proposed."]);
        life.Wake("board-1");
        await life.RunUntil(() => life.Reached("proposed"), "board-1's puppet proposed");

        var act = Assert.Single(store.Acts()).Act;
        Assert.Equal(("board-1", BoardActKind.Propose), (act.Seat, act.Kind));
        life.Script("board-2", [BoardLives.Decide(act.Id, "confirm", act.IntentSha256, "the figures in force allow it"), "usage 100 0 10",
            "final Confirmed."]);
        life.Release("confirmer-scripted");
        await life.RunUntil(() => life.AttemptsOf("board-2").Any(a => a.State == AiAttemptState.ENDED), "board-2's turn ended");

        var propose = BoardLives.TradeSteps(life, "board-1", 1)[0];
        Assert.True((int?)propose["exit"] == 0, $"the proposal was refused: {BoardLives.Said(propose)}");
        var decide = BoardLives.TradeSteps(life, "board-2", 1)[0];
        Assert.True((int?)decide["exit"] == 0, $"the confirmation was refused: {BoardLives.Said(decide)}");

        // THE POLICY IN FORCE, AS THE GATEWAY READS IT: version 2, decided by the board.
        var inForce = store.InForce();
        Assert.Equal((2, BoardDecider.Board), (inForce.Version, inForce.Decider));
        Assert.Equal(50m, life.Host.Gateway.Policy.PlanDayTotal);

        // THE CONFIRMER'S PROMPT: the intent and its hash, never a word of the proposer's rationale.
        var prompt = life.Prompt("board-2", 1)!;
        Assert.Contains(act.IntentSha256, prompt, StringComparison.Ordinal);
        Assert.Contains(act.Id, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(BoardLives.Marker, prompt, StringComparison.Ordinal);

        // EACH SEAT ON ITS RECORDED CLASS, ON THE BOARD'S OWN BUDGET, FUNDED BY NO UNIT; NO MODEL.
        foreach (var (seat, seatClass) in new[] { ("board-1", SeatClasses.Broker), ("board-2", SeatClasses.Harness) })
        {
            var attempt = Assert.Single(life.AttemptsOf(seat));
            Assert.Equal((AiAttemptState.ENDED, seatClass, RuntimeManifest.PuppetId), (attempt.State, attempt.SeatClass, attempt.Runtime));
            Assert.Empty(new AiAttemptStore(life.Host.Db).FundingOf(attempt.Id));
        }
        // board-2 WAS WOKEN BY THE PROPOSAL ALONE, its one board wake consumed by its one turn.
        var woke = Assert.Single(life.Events(), e => e.Role == "board-2");
        Assert.Equal((MissionEventKind.Board, act.Id), (woke.Kind, woke.Payload));
        Assert.Equal(life.AttemptsOf("board-2")[0].Id, woke.ConsumedBy);
        Assert.Equal(2, BoardLives.AssertNoModel(life));
        output.WriteLine($"observable: {life.Counts()}");
    }

    // ---- item 5: board lives I — the acts ------------------------------------------------------------------------------------------

    static string EnvelopeJson(PuppetLife life, decimal? maxQuantity = null)
    {
        var store = new BoardStore(life.Host.Db);
        var terms = JsonNode.Parse(store.EnvelopeTermsAt(store.InForce().Version)!)!.AsObject();
        if (maxQuantity is { } q) terms["max_quantity"] = q;
        return terms.ToJsonString();
    }

    static string EnvelopeChange(string from, string to) =>
        new JsonArray(new JsonObject { ["field"] = "paper_envelope", ["from"] = JsonNode.Parse(from), ["to"] = JsonNode.Parse(to) }).ToJsonString();

    /// <summary>
    /// A NARROWING BY ONE SEAT REFUSES THE NEXT ORDER ABOVE IT: on a tradable policy (quantity ten), the chair's puppet buys three through
    /// <c>trade buy</c>; board-1's puppet narrows the quantity to two through <c>trade board narrow</c> — one seat, at once, a version decided
    /// by the board — and the chair's next <c>trade buy</c> of three is refused by the gateway's risk limit.
    /// </summary>
    [Fact]
    public async Task A_narrowing_by_one_seat_refuses_the_next_order_above_it()
    {
        await using var life = await BoardLives.Begin("board-narrow", db => BoardLives.Seed(db, BoardLives.Tradable), "board-1");
        await life.Host.Gateway.RefreshHealthAsync();
        const string chair = CouncilRoles.Operations;
        // THE CHAIR BUYS ONCE; the turns its own fill and order wake buy it do nothing.
        life.Script(chair, ["trade buy ES 3", "usage 10 0 5", "final Bought."], ["usage 10 0 5", "final Nothing more."]);
        life.Script("board-1", [BoardLives.Narrow(BoardLives.Change("risk.max_order_quantity", 10, 2), "three at once is more than this book carries"),
            "usage 100 0 10", "final Narrowed."]);

        life.Wake(chair);
        await life.RunUntil(() => life.AttemptsOf(chair).Count(a => a.State == AiAttemptState.ENDED) >= 1, "the chair's first turn ended");
        var first = BoardLives.TradeSteps(life, chair, 1)[0];
        Assert.True((int?)first["exit"] == 0, $"the first order was refused: {BoardLives.Said(first)}");

        life.Wake("board-1");
        await life.RunUntil(() => life.AttemptsOf("board-1").Any(a => a.State == AiAttemptState.ENDED), "board-1's turn ended");
        var narrow = BoardLives.TradeSteps(life, "board-1", 1)[0];
        Assert.True((int?)narrow["exit"] == 0, $"the narrowing was refused: {BoardLives.Said(narrow)}");
        Assert.Equal(2m, life.Host.Gateway.Policy.Risk.MaxOrderQuantity);
        Assert.Equal(BoardDecider.Board, new BoardStore(life.Host.Db).InForce().Decider);

        // THE CHAIR'S NEXT ORDER, AFTER THE ACT: once nothing of the chair's is running or due, its every next turn buys three again.
        await life.RunUntil(() => life.AttemptsOf(chair).All(a => a.State != AiAttemptState.LAUNCHED)
                                  && !life.Events().Any(e => (e.Role ?? chair) == chair && !e.Consumed && e.DueAt <= DateTimeOffset.UtcNow),
            "the chair's own wakes were taken");
        var turns = life.Turns(chair);
        life.Script(chair, ["trade buy ES 3", "usage 10 0 5", "final Tried again."]);
        life.Wake(chair);
        await life.RunUntil(() => life.Turns(chair) > turns && life.AttemptsOf(chair).All(a => a.State != AiAttemptState.LAUNCHED),
            "the chair's turn after the act ended");
        var second = BoardLives.TradeSteps(life, chair, turns + 1)[0];
        output.WriteLine(BoardLives.Said(second));
        Assert.NotEqual(0, (int?)second["exit"]);
        Assert.Contains(nameof(ErrorCode.RISK_LIMIT_EXCEEDED), BoardLives.Said(second), StringComparison.Ordinal);
        Assert.Equal(1, BoardLives.AssertNoModel(life));
    }

    /// <summary>
    /// A WIDENING PROPOSED BY board-1 AND CONFIRMED BY board-2 IS IN FORCE AND THE PAPER ENVELOPE FOLLOWS: on a policy whose standing paper
    /// envelope is on the life's own simulated account, board-1 proposes a larger envelope; until board-2 confirms, the old one stands;
    /// once board-2 confirms through its own turn, the new terms are the policy's and the next health pass grants the envelope under the
    /// act's version, in the security board's words.
    /// </summary>
    [Fact]
    public async Task A_widening_proposed_and_confirmed_moves_the_paper_envelope()
    {
        int seeded = 0;
        await using var life = await BoardLives.Begin("board-envelope", db => seeded = BoardLives.Seed(db, BoardLives.EnvelopeHere), "board-1", "board-2");
        await life.Host.Gateway.RefreshHealthAsync();
        var before = Assert.Single(life.Host.Gateway.Envelopes.All(), e => e.StandsAt(DateTimeOffset.UtcNow));
        Assert.Equal(0.05m, before.MaxQuantity);

        var intent = EnvelopeChange(EnvelopeJson(life), EnvelopeJson(life, 0.1m));
        life.Script("board-1", [BoardLives.Propose(intent, BoardLives.Rationale), "usage 100 0 10", "final Proposed."]);
        life.Wake("board-1");
        await life.RunUntil(() => new BoardStore(life.Host.Db).Acts().Any(a => a.Act.Kind == BoardActKind.Propose), "board-1's puppet proposed");
        var act = new BoardStore(life.Host.Db).Acts().Single(a => a.Act.Kind == BoardActKind.Propose).Act;
        life.Script("board-2", [BoardLives.Decide(act.Id, "confirm", act.IntentSha256, "the paper book has room"), "usage 100 0 10", "final Confirmed."]);
        await life.RunUntil(() => life.AttemptsOf("board-2").Any(a => a.State == AiAttemptState.ENDED), "board-2's turn ended");
        var decide = BoardLives.TradeSteps(life, "board-2", 1)[0];
        Assert.True((int?)decide["exit"] == 0, $"the confirmation was refused: {BoardLives.Said(decide)}");

        Assert.Equal((seeded + 1, BoardDecider.Board), (new BoardStore(life.Host.Db).InForce().Version, new BoardStore(life.Host.Db).InForce().Decider));
        Assert.Equal(0.1m, life.Host.Gateway.Policy.PaperEnvelope!.MaxQuantity);
        await life.Host.Gateway.RefreshHealthAsync();
        var granted = Assert.Single(life.Host.Gateway.Envelopes.All(), e => e.StandsAt(DateTimeOffset.UtcNow));
        Assert.Equal((0.1m, $"granted by the app under the security board's act, version {seeded + 1}"), (granted.MaxQuantity, granted.Reason));
        Assert.Equal(2, BoardLives.AssertNoModel(life));
    }

    /// <summary>
    /// A REFUSAL BY board-2 APPLIES NOTHING: board-1 proposes, board-2 — woken by it — refuses through its own turn, naming the hash. The
    /// policy stays version 1, the act stays open for another seat, the refusal is a record with board-2's own words, and board-3, dormant,
    /// was woken for nothing.
    /// </summary>
    [Fact]
    public async Task A_refusal_by_the_second_seat_applies_nothing()
    {
        await using var life = await BoardLives.Begin("board-refusal", "board-1", "board-2");
        life.Script("board-1", [BoardLives.Propose(BoardLives.Change("plan_day_total", 40, 60), BoardLives.Rationale), "usage 100 0 10", "final Proposed."]);
        life.Wake("board-1");
        await life.RunUntil(() => new BoardStore(life.Host.Db).Acts().Count == 1, "board-1's puppet proposed");
        var store = new BoardStore(life.Host.Db);
        var act = store.Acts()[0].Act;
        life.Script("board-2", [BoardLives.Decide(act.Id, "refuse", act.IntentSha256, "not while nothing is proven"), "usage 100 0 10", "final Refused."]);
        await life.RunUntil(() => life.AttemptsOf("board-2").Any(a => a.State == AiAttemptState.ENDED), "board-2's turn ended");
        var decide = BoardLives.TradeSteps(life, "board-2", 1)[0];
        Assert.True((int?)decide["exit"] == 0, $"the refusal was not recorded: {BoardLives.Said(decide)}");

        Assert.Equal((1, BoardDecider.App), (store.InForce().Version, store.InForce().Decider));
        Assert.Equal(40m, life.Host.Gateway.Policy.PlanDayTotal);
        Assert.Equal(BoardActStatus.Open, store.Status(act.Id));
        var refused = Assert.Single(store.Events(act.Id), e => e.Kind == BoardActEventKind.Refused);
        Assert.Equal(("board-2", "not while nothing is proven", life.AttemptsOf("board-2")[0].Id), (refused.Seat, refused.Rationale, refused.Attempt));
        Assert.DoesNotContain(life.Events(), e => e.Role == "board-3");
        Assert.Equal(2, BoardLives.AssertNoModel(life));
    }

    // ---- item 6: board lives II — the edges ------------------------------------------------------------------------------------------

    static BoardActRow Proposal(PuppetLife life) => new BoardStore(life.Host.Db).Acts().Single(a => a.Act.Kind == BoardActKind.Propose).Act;

    /// <summary>
    /// AN EXPIRED AND A STALE PROPOSAL APPLY NOTHING AND ARE RECORDED. board-1's puppet proposes a larger day; the clock passes the
    /// confirmation life before any seat decides, and the sweep closes it <c>expired</c> — the other seat's wake closed with it, so no turn
    /// is bought for it. Then board-1 proposes again; while it waits, board-3 narrows the same field at once; board-2, woken by the
    /// proposal, confirms through its own turn — and the act closes <c>stale</c>, applying nothing: the narrowing stands.
    /// </summary>
    [Fact]
    public async Task An_expired_or_stale_proposal_applies_nothing_and_is_recorded()
    {
        await using var life = await BoardLives.Begin("board-edges", "board-1", "board-2", "board-3");
        var store = new BoardStore(life.Host.Db);

        // EXPIRED: proposed, never decided, and the clock past its life.
        life.Script("board-1", [BoardLives.Propose(BoardLives.Change("plan_day_total", 40, 60), BoardLives.Rationale), "signal proposed-1",
            "await expired", "usage 100 0 10", "final Proposed."],
            [BoardLives.Propose(BoardLives.Change("plan_day_total", 40, 55), BoardLives.Rationale), "signal proposed-2", "await narrowed",
            "usage 100 0 10", "final Proposed again."]);
        life.Wake("board-1");
        await life.RunUntil(() => life.Reached("proposed-1"), "board-1 proposed");
        var expiring = Proposal(life);
        Assert.Equal(1, store.Expire(DateTimeOffset.UtcNow + BoardLimits.ConfirmationLife + TimeSpan.FromMinutes(1)));
        Assert.Equal(BoardActEventKind.Expired, store.Status(expiring.Id));
        Assert.All(life.Events().Where(e => e.Kind == MissionEventKind.Board && e.Payload == expiring.Id),
            e => Assert.Equal(MissionEventStore.NoLaunch, e.ConsumedBy));
        Assert.Equal(1, store.InForce().Version);
        life.Release("expired");
        await life.RunUntil(() => life.AttemptsOf("board-1").Any(a => a.State == AiAttemptState.ENDED), "board-1's first turn ended");
        Assert.Empty(life.AttemptsOf("board-2"));

        // STALE: proposed against 40; board-3 narrows to 30 at once; board-2's confirmation closes it stale.
        life.Wake("board-1");
        await life.RunUntil(() => life.Reached("proposed-2"), "board-1 proposed again");
        var stale = new BoardStore(life.Host.Db).Acts().Single(a => a.Act.Kind == BoardActKind.Propose && a.Act.Id != expiring.Id).Act;
        var narrowed = life.Host.Gateway.BoardNarrow(new BoardActRequest("board-3", null, BoardLives.Change("plan_day_total", 40, 30),
            "a smaller day first", [], []));
        Assert.Equal(BoardActEventKind.Applied, narrowed.Status);
        life.Script("board-2", [BoardLives.Decide(stale.Id, "confirm", stale.IntentSha256, "the figures allow it"), "usage 100 0 10", "final Confirmed."]);
        life.Release("narrowed");
        await life.RunUntil(() => life.AttemptsOf("board-2").Any(a => a.State == AiAttemptState.ENDED), "board-2's turn ended");

        Assert.Equal(BoardActEventKind.Stale, store.Status(stale.Id));
        Assert.Contains(store.Events(stale.Id), e => e.Kind == BoardActEventKind.Stale && e.Seat == "board-2");
        Assert.Equal((2, 30m), (store.InForce().Version, life.Host.Gateway.Policy.PlanDayTotal));
        Assert.Equal(3, BoardLives.AssertNoModel(life));
    }

    /// <summary>
    /// THE OWNER'S VETO AFTER A CONFIRMED WIDENING RETURNS EVERY FIELD TO THE NARROWER OF NOW AND BEFORE, and two seats of one vendor family
    /// record it. board-1 proposes a larger day and board-2 confirms it in its turn — both on the founding family, so the confirmation is
    /// recorded <c>same_family</c> with the app's words; the owner's in-process veto (two presses) writes a version decided by him whose
    /// day is the narrower of now and before.
    /// </summary>
    [Fact]
    public async Task The_owners_veto_after_a_confirmed_widening_returns_the_narrower_and_one_family_is_recorded()
    {
        await using var life = await BoardLives.Begin("board-veto", "board-1", "board-2");
        var store = new BoardStore(life.Host.Db);
        life.Script("board-1", [BoardLives.Propose(BoardLives.Change("plan_day_total", 40, 70), BoardLives.Rationale), "usage 100 0 10", "final Proposed."]);
        life.Wake("board-1");
        await life.RunUntil(() => store.Acts().Count == 1, "board-1 proposed");
        var act = Proposal(life);
        life.Script("board-2", [BoardLives.Decide(act.Id, "confirm", act.IntentSha256, "the plan has room"), "usage 100 0 10", "final Confirmed."]);
        await life.RunUntil(() => life.AttemptsOf("board-2").Any(a => a.State == AiAttemptState.ENDED), "board-2's turn ended");
        Assert.Equal(70m, life.Host.Gateway.Policy.PlanDayTotal);

        var confirmed = Assert.Single(store.Events(act.Id), e => e.Kind == BoardActEventKind.Confirmed);
        Assert.Equal((true, "openai", "openai"), (confirmed.SameFamily, confirmed.ProposerFamily, confirmed.DeciderFamily));
        Assert.Contains("no active seat of another family could decide it", confirmed.Detail, StringComparison.Ordinal);

        var preview = life.Host.Gateway.PreviewBoardVeto(act.Id);
        Assert.True(preview.Allowed, preview.Words);
        var vetoed = life.Host.Gateway.VetoBoardAct(act.Id);
        Assert.Equal(BoardActEventKind.Vetoed, vetoed.Status);
        var inForce = store.InForce();
        Assert.Equal((3, BoardDecider.Owner, BoardBasis.Veto), (inForce.Version, inForce.Decider, inForce.Basis));
        Assert.Equal(40m, life.Host.Gateway.Policy.PlanDayTotal);
        Assert.Equal(2, BoardLives.AssertNoModel(life));
    }

    /// <summary>
    /// A BOARD SEAT'S REACH IS THE BOARD'S VERBS AND NOTHING ELSE, and the board's verbs are a board seat's alone: board-1's puppet proposes
    /// a change of the board's own budget — a record listed for the owner that changes nothing — and its <c>trade buy</c> and
    /// <c>trade ledger add</c> are refused with nothing written; the chair's puppet's <c>trade board propose</c> is refused, no act made.
    /// </summary>
    [Fact]
    public async Task A_board_seat_reaches_only_board_verbs_and_a_position_reaches_none()
    {
        await using var life = await BoardLives.Begin("board-reach", "board-1");
        var store = new BoardStore(life.Host.Db);
        const string chair = CouncilRoles.Operations;
        life.Script("board-1", ["trade board power-propose board-budget \"the board needs a larger share of the day\"",
            "trade buy ES 1", "trade ledger add finding \"A board seat's note.\" --mark claim", "usage 100 0 10", "final Done."]);
        life.Script(chair, [BoardLives.Propose(BoardLives.Change("plan_day_total", 40, 80), "the chair wants more"), "usage 10 0 5", "final Tried."],
            ["usage 10 0 5", "final Nothing more."]);
        life.Wake("board-1");
        life.Wake(chair);
        await life.RunUntil(() => life.AttemptsOf("board-1").Any(a => a.State == AiAttemptState.ENDED)
                                  && life.AttemptsOf(chair).Any(a => a.State == AiAttemptState.ENDED), "both turns ended");

        var steps = BoardLives.TradeSteps(life, "board-1", 1);
        Assert.True((int?)steps[0]["exit"] == 0, $"the power proposal was refused: {BoardLives.Said(steps[0])}");
        var power = Assert.Single(new BoardReader(life.Host.Db).PowerProposals());
        Assert.Equal(("board-1", BoardPowerTarget.BoardBudget), (power.Seat, power.Target));
        Assert.Equal(BoardFounding.BudgetShare, store.BudgetShare());
        Assert.Equal(1, store.InForce().Version);
        foreach (var refused in steps.Skip(1))
            Assert.True((int?)refused["exit"] != 0, $"a board seat reached {refused["args"]!.ToJsonString()}: {BoardLives.Said(refused)}");
        foreach (var table in new[] { "ledger_entry", "execution_request" })
            Assert.Equal(0L, life.Host.Db.Read(_ =>
            {
                using var c = life.Host.Db.Cmd($"SELECT COUNT(*) FROM {table}");
                return Convert.ToInt64(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }));

        var chairs = BoardLives.TradeSteps(life, chair, 1)[0];
        Assert.True((int?)chairs["exit"] != 0, $"a position made a board act: {BoardLives.Said(chairs)}");
        Assert.Empty(store.Acts());
        output.WriteLine($"reach: {BoardLives.Said(chairs)}");
        Assert.Equal(1, BoardLives.AssertNoModel(life));
    }

    // ---- item 7: the guards ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// NOTHING ACTIVATES A BOARD SEAT IN THE PRODUCT (<c>U-board-b2</c> item 7; containment; the a2 rule that the owner's paper-only presses
    /// stay his while no seat is active): the host's own composition over a fresh home, with no test seam, leaves every founded seat
    /// dormant — no state row is written — and through a loop tick holding a board wake for board-1 it launches nothing: the wake is
    /// settled refused for what the seat is, no launch is recorded, and the board is not active. Watched red with a composition-root line
    /// that marks board-1 active.
    /// </summary>
    [Fact]
    public async Task Nothing_in_the_product_composition_activates_or_launches_a_board_seat()
    {
        using var db = TestEnv.NewDb();
        var connector = new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker());
        await connector.ConnectAsync();
        var host = TradeAgent.App.AppHost.Composed(db, connector, new AgentPresence());
        try
        {
            var reader = new BoardReader(db);
            Assert.Equal(BoardSeats.Founding, reader.Seats().Select(s => s.Id));
            Assert.All(reader.Seats(), s => Assert.Equal(BoardSeatStatus.Dormant, s.Status));
            Assert.False(reader.AnyActive, "a board seat is active in the product composition");

            var wake = MissionEventIds.Board("bact-000000000000", "board-1");
            Assert.True(host.Wakes!.Raise(wake, MissionEventKind.Board, DateTimeOffset.UtcNow.AddMinutes(-1), "bact-000000000000", "board-1"));
            await host.Mission.TurnAsync();

            Assert.DoesNotContain(new AiAttemptStore(db).Between(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)),
                a => BoardSeats.IsBoardId(a.Role));
            var settled = host.Wakes.Get(wake)!;
            Assert.Equal((true, MissionEventDisposition.Refused), (settled.Consumed, settled.Disposition));
            output.WriteLine(settled.DispositionDetail);
            Assert.All(reader.Seats(), s => Assert.Equal(BoardSeatStatus.Dormant, s.Status));
            Assert.False(reader.AnyActive, "a loop tick activated a board seat");
            Assert.Equal(0L, db.Read(_ =>
            {
                using var c = db.Cmd("SELECT COUNT(*) FROM board_seat_state");
                return Convert.ToInt64(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }));
        }
        finally
        {
            try { await host.Mission.PauseAsync(); } catch (Exception) { }
            await host.Gateway.DisposeAsync();
        }
    }
}
