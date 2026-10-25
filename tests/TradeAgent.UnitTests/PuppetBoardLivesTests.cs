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
    }
}
