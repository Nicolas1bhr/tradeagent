using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- item 4: two seats widen ------------------------------------------------------------------------------------------------

    const string Concealed = "PROPOSER-REASON-7f3a", ThirdWords = "THIRD-SEAT-REASON-91c2";

    static string Raise(decimal from = 40m, decimal to = 50m) => Change("plan_day_total", from, to);

    /// <summary>A seat the owner would found on another vendor family — written raw, as the test's precondition (item 8 founds one in-process).</summary>
    static void FoundOnAnotherFamily(Database db, string id = "board-4", string family = "anthropic") =>
        Exec(db, "INSERT INTO board_seat(id, seat_class, model_class, vendor_family, status, founded_at) VALUES($id, 'broker', 'strong', $f, 'dormant', $at)",
            ("$id", id), ("$f", family), ("$at", Stamp));

    /// <summary>
    /// THE CONFIRMER SEES NO RATIONALE UNTIL ITS DECISION IS SEALED: before its own word, a seat reading a proposal is given the intent, its
    /// hash and direction, the refs, the app's figures in force and the expiry — never the proposer's rationale or forecasts, nor another
    /// seat's sealed refusal — and once it has decided, all of it. The blind view has no member that could carry a rationale. Watched red
    /// with the rationale returned to an unsealed seat.
    /// </summary>
    [Fact]
    public void The_confirmer_sees_no_rationale_until_its_decision_is_sealed()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2", "board-3");
        var store = new BoardStore(db);
        var now = DateTimeOffset.UtcNow;
        var forecast = new ForecastClaim(ForecastVocabulary.UnitAdmissible, OrgIds.Research, ForecastVocabulary.Admitted, now.AddDays(3), 0.73m);
        var proposed = store.Propose(Act("board-1", Raise(), Concealed, forecasts: [forecast]), now);
        Assert.Equal(BoardActStatus.Open, proposed.Status);
        Assert.Null(proposed.PolicyVersion);
        Assert.Equal(40m, new BoardReader(db).InForce().Policy.PlanDayTotal);

        string Shown(string seat)
        {
            var (blind, whole) = store.ShowTo(seat, proposed.Act.Id);
            return JsonSerializer.Serialize<object?>((object?)blind ?? whole);
        }

        var blindTo2 = store.ShowTo("board-2", proposed.Act.Id);
        Assert.NotNull(blindTo2.Blind);
        Assert.Null(blindTo2.Whole);
        Assert.Equal((proposed.Act.IntentSha256, "widens", proposed.Act.ExpiresAt), (blindTo2.Blind!.IntentSha256, blindTo2.Blind.Direction, blindTo2.Blind.ExpiresAt));
        Assert.Equal("40", Assert.Single(blindTo2.Blind.InForce, f => f.Name == "plan_day_total").Value);
        var text = Shown("board-2");
        log.WriteLine(text);
        Assert.DoesNotContain(Concealed, text, StringComparison.Ordinal);
        Assert.DoesNotContain("0.73", text, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(BoardActBlindView).GetProperties(), p => p.Name.Contains("Rationale", StringComparison.Ordinal)
                                                                            || p.Name.Contains("Forecast", StringComparison.Ordinal));

        // ANOTHER SEAT'S SEALED REFUSAL is not shown to the one still deciding either.
        store.Decide(proposed.Act.Id, "board-3", null, confirm: false, proposed.Act.IntentSha256, ThirdWords, now);
        text = Shown("board-2");
        Assert.DoesNotContain(Concealed, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ThirdWords, text, StringComparison.Ordinal);

        // ONCE SEALED, ALL: the refusing seat, the proposer, and the confirmer after its confirmation.
        Assert.Contains(Concealed, Shown("board-3"), StringComparison.Ordinal);
        Assert.Contains(Concealed, Shown("board-1"), StringComparison.Ordinal);
        var confirmed = store.Decide(proposed.Act.Id, "board-2", null, confirm: true, proposed.Act.IntentSha256, "my own reading", now);
        Assert.Equal(BoardActEventKind.Confirmed, confirmed.Status);
        text = Shown("board-2");
        Assert.Contains(Concealed, text, StringComparison.Ordinal);
        Assert.Contains(ThirdWords, text, StringComparison.Ordinal);
        Assert.Contains("0.73", text, StringComparison.Ordinal);

        // THE LIST NEVER CARRIES A RATIONALE.
        Assert.DoesNotContain(Concealed, JsonSerializer.Serialize(store.ActList()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A CONFIRMATION BINDS TO THE INTENT'S HASH: a decision naming another hash — a different intent's, or none — is refused with nothing
    /// written, and the one naming this intent's writes the version in its own write: decided by <c>board</c>, the proposal behind it, the
    /// proposer and the confirmer its deciders.
    /// </summary>
    [Fact]
    public void A_confirmation_binds_to_the_intents_hash()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var store = new BoardStore(db);
        var now = DateTimeOffset.UtcNow;
        var act = store.Propose(Act("board-1", Raise(), Concealed), now).Act;
        var other = BoardIntent.Parse(Raise(40m, 60m)).Intent!.Sha256;
        var before = Counts(db);

        foreach (var sha in new[] { other, "", act.IntentSha256.ToUpperInvariant() })
        {
            var refused = Assert.Throws<TradeAgentException>(() => store.Decide(act.Id, "board-2", null, true, sha, "yes", now));
            log.WriteLine(refused.Message);
            Assert.Contains("is not the hash", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(before, Counts(db));
        Assert.Equal(40m, new BoardReader(db).InForce().Policy.PlanDayTotal);

        var confirmed = store.Decide(act.Id, "board-2", null, true, act.IntentSha256, "the evidence holds", now);
        Assert.Equal((BoardActEventKind.Confirmed, 2), (confirmed.Status, confirmed.PolicyVersion));
        var inForce = new BoardReader(db).InForce();
        Assert.Equal((2, BoardDecider.Board, BoardBasis.Act, 50m), (inForce.Version, inForce.Decider, inForce.Basis, inForce.Policy.PlanDayTotal));
        Assert.Equal(["board-1", "board-2"], new BoardReader(db).History()[^1].Deciders);
        Assert.Equal(act.Id, Scalar(db, "SELECT act_id FROM board_policy WHERE version=2"));
    }

    /// <summary>THE PROPOSER CANNOT CONFIRM ITS OWN ACT — nor decide it at all — and nothing is written; a second seat confirms it.</summary>
    [Fact]
    public void The_proposer_cannot_confirm_its_own_act()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var store = new BoardStore(db);
        var now = DateTimeOffset.UtcNow;
        var act = store.Propose(Act("board-1", Raise()), now).Act;
        var before = Counts(db);
        foreach (var confirm in new[] { true, false })
        {
            var refused = Assert.Throws<TradeAgentException>(() => store.Decide(act.Id, "board-1", null, confirm, act.IntentSha256, "mine", now));
            Assert.True(RefusalMark.IsMarked(refused));
            Assert.Contains("never decides its own act", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(before, Counts(db));
        Assert.Equal(BoardActStatus.Open, store.Status(act.Id));

        // A NARROWING IS NEVER PROPOSED: one seat applies it.
        var narrowing = Assert.Throws<TradeAgentException>(() => store.Propose(Act("board-1", Change("plan_day_total", 40, 30)), now));
        Assert.Contains("board-narrow", narrowing.Message, StringComparison.Ordinal);
        Assert.Equal(before, Counts(db));
    }

    /// <summary>
    /// AN EXPIRED OR STALE PROPOSAL APPLIES NOTHING AND IS RECORDED: a proposal no second seat confirmed within the confirmation life is
    /// closed <c>expired</c> by the sweep's tick — and lazily by a decision that comes after it — and a confirmation of a proposal whose
    /// field another act has moved since closes it <c>stale</c>; the policy in force is the one before, each time.
    /// </summary>
    [Fact]
    public void An_expired_or_stale_proposal_applies_nothing_and_is_recorded()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2", "board-3");
        var store = new BoardStore(db);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromHours(24), BoardLimits.ConfirmationLife);

        // ON THE SWEEP'S TICK.
        var swept = store.Propose(Act("board-1", Raise()), now).Act;
        Assert.Equal(0, store.Expire(now.AddHours(23)));
        Assert.Equal(1, store.Expire(now.AddHours(25)));
        Assert.Equal(0, store.Expire(now.AddHours(26)));
        Assert.Equal(BoardActEventKind.Expired, store.Status(swept.Id));
        Assert.Throws<TradeAgentException>(() => store.Decide(swept.Id, "board-2", null, true, swept.IntentSha256, "late", now.AddHours(26)));

        // LAZILY, AT A DECISION AFTER THE EXPIRY.
        var lazy = store.Propose(Act("board-1", Raise()), now).Act;
        var late = store.Decide(lazy.Id, "board-2", null, true, lazy.IntentSha256, "late", now.AddHours(25));
        Assert.Equal((BoardActEventKind.Expired, (int?)null), (late.Status, late.PolicyVersion));
        Assert.Equal("app", store.Events(lazy.Id)[^1].Seat);
        Assert.Equal(1, int.Parse(Scalar(db, "SELECT MAX(version) FROM board_policy")!));

        // STALE: board-3 narrows the very field before board-2 confirms.
        var stale = store.Propose(Act("board-1", Raise()), now).Act;
        store.Narrow(Act("board-3", Change("plan_day_total", 40, 30)), now);
        var closed = store.Decide(stale.Id, "board-2", null, true, stale.IntentSha256, "yes", now);
        log.WriteLine(closed.Words);
        Assert.Equal(BoardActEventKind.Stale, closed.Status);
        Assert.Contains("plan_day_total holds 30, not 40", closed.Words, StringComparison.Ordinal);
        Assert.Equal(BoardActEventKind.Stale, store.Status(stale.Id));
        Assert.Equal(30m, new BoardReader(db).InForce().Policy.PlanDayTotal);
        Assert.Throws<TradeAgentException>(() => store.Decide(stale.Id, "board-3", null, true, stale.IntentSha256, "yes", now));
    }

    /// <summary>
    /// A REFUSAL IS A RECORD: the refusing seat's own rationale is sealed in its event with the two seats' families, the act stays open —
    /// a refusal applies nothing and closes nothing — the same seat cannot decide it again, and another seat may still confirm it.
    /// </summary>
    [Fact]
    public void A_refusal_is_a_record()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2", "board-3");
        var store = new BoardStore(db);
        var now = DateTimeOffset.UtcNow;
        var act = store.Propose(Act("board-1", Raise()), now).Act;
        var refused = store.Decide(act.Id, "board-2", "turn-board-2", false, act.IntentSha256, "no evidence for a larger day", now);
        Assert.Equal((BoardActStatus.Open, (int?)null), (refused.Status, refused.PolicyVersion));
        var e = store.Events(act.Id)[^1];
        Assert.Equal((BoardActEventKind.Refused, "board-2", "turn-board-2", "no evidence for a larger day", "openai", "openai", (bool?)true),
            (e.Kind, e.Seat, e.Attempt, e.Rationale, e.ProposerFamily, e.DeciderFamily, e.SameFamily));
        Assert.Equal(40m, new BoardReader(db).InForce().Policy.PlanDayTotal);
        var again = Assert.Throws<TradeAgentException>(() => store.Decide(act.Id, "board-2", null, true, act.IntentSha256, "changed my mind", now));
        Assert.Contains("one decision per seat per act", again.Message, StringComparison.Ordinal);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, "UPDATE board_act_event SET rationale='rewritten'"));

        Assert.Equal(BoardActEventKind.Confirmed, store.Decide(act.Id, "board-3", null, true, act.IntentSha256, "it holds", now).Status);
        Assert.Equal(50m, new BoardReader(db).InForce().Policy.PlanDayTotal);
    }

    /// <summary>
    /// SAME FAMILY IS RECORDED, AND REFUSED WHEN ANOTHER FAMILY IS ACTIVE: on the owner's one plan every seat is one vendor family, and a
    /// confirmation from it is written with <c>same_family</c> and the words saying why; once an active seat of another family could
    /// confirm, a same-family confirmation is refused with nothing written and that seat's is recorded as two families.
    /// </summary>
    [Fact]
    public void Same_family_is_recorded_and_refused_when_another_family_is_active()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var store = new BoardStore(db);
        var now = DateTimeOffset.UtcNow;

        var first = store.Propose(Act("board-1", Raise()), now).Act;
        store.Decide(first.Id, "board-2", null, true, first.IntentSha256, "yes", now);
        var same = store.Events(first.Id)[^1];
        Assert.Equal((true, "openai", "openai"), (same.SameFamily!.Value, same.ProposerFamily, same.DeciderFamily));
        Assert.Contains("no active seat of another family", same.Detail, StringComparison.Ordinal);

        FoundOnAnotherFamily(db);
        Activate(db, "board-4");
        var second = store.Propose(Act("board-1", Raise(50m, 60m)), now).Act;
        var before = Counts(db);
        var refused = Assert.Throws<TradeAgentException>(() => store.Decide(second.Id, "board-2", null, true, second.IntentSha256, "yes", now));
        log.WriteLine(refused.Message);
        Assert.Contains("board-4 is active on the vendor family 'anthropic'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Counts(db));

        Assert.Equal(BoardActEventKind.Confirmed, store.Decide(second.Id, "board-4", null, true, second.IntentSha256, "yes", now).Status);
        var cross = store.Events(second.Id)[^1];
        Assert.Equal((false, "openai", "anthropic"), (cross.SameFamily!.Value, cross.ProposerFamily, cross.DeciderFamily));
        Assert.Equal(60m, new BoardReader(db).InForce().Policy.PlanDayTotal);
    }
}
