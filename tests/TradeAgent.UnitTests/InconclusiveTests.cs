using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHICH REFUSALS ARE INCONCLUSIVE (<c>U-referee-v2b</c> item 1), AND THE VERDICT PATH THE QUOTA READS, BYTE FOR BYTE.
///
/// <para><c>Inconclusive.Of</c> is a read of a refused standing and nothing else: no trade on the holdout, or a loss over a
/// holdout under 365 days (an end that cannot be read counts as short). A faulted run, a loss over a long holdout, and
/// every standing that is not <c>refused</c> are not inconclusive. And the referee's words and both policy texts are the
/// base's exactly — the quota reads a refused verdict and never re-judges it (EDGE § 6.5).</para>
/// </summary>
public class InconclusiveTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset At = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    static PromotionRow Row(string verdict, string reason) => new(
        "p", "v", 1, "policy", "build", 1, "sha", "model", "eval", "run", verdict, reason, At);

    static PromotionStanding Standing(string state, string verdict, string reason) =>
        new(state, "why", Row(verdict, reason));

    static PromotionStanding Refused(string reason) =>
        Standing(PromotionState.Refused, PromotionVerdict.Refused, reason);

    [Fact]
    public void A_no_trade_or_short_holdout_loss_is_inconclusive_and_a_fault_or_long_holdout_loss_is_not()
    {
        var shortHoldout = TimeSpan.FromDays(91);
        var longHoldout = TimeSpan.FromDays(365);

        var noTrade = Inconclusive.Of(Refused(PromotionReason.NoTrade), longHoldout);
        var shortLoss = Inconclusive.Of(Refused(PromotionReason.NotProfitable), shortHoldout);
        var justShort = Inconclusive.Of(Refused(PromotionReason.NotProfitable), longHoldout - TimeSpan.FromTicks(1));
        var unreadable = Inconclusive.Of(Refused(PromotionReason.NotProfitable), null);
        var longLoss = Inconclusive.Of(Refused(PromotionReason.NotProfitable), longHoldout);
        var faulted = Inconclusive.Of(Refused(PromotionReason.DidNotComplete), shortHoldout);
        var precedes = Inconclusive.Of(Refused(PromotionReason.PrecedesTheFreeze), shortHoldout);

        // NOT A REFUSAL THAT STANDS: the same no-trade row, read as invalidated, and every other state.
        var invalidated = Inconclusive.Of(
            Standing(PromotionState.Invalidated, PromotionVerdict.Refused, PromotionReason.NoTrade), shortHoldout);
        var eligible = Inconclusive.Of(
            Standing(PromotionState.PaperEligible, PromotionVerdict.PaperEligible, PromotionReason.MetOnHistory), shortHoldout);
        var promoted = Inconclusive.Of(
            Standing(PromotionState.Promoted, PromotionVerdict.Promoted, PromotionReason.Met), shortHoldout);
        var unjudged = Inconclusive.Of(new PromotionStanding(PromotionState.Unjudged, "why", null), shortHoldout);

        log.WriteLine($"no trade             : {noTrade ?? "null"}");
        log.WriteLine($"loss over 91 days    : {shortLoss ?? "null"}");
        log.WriteLine($"loss just under 365  : {justShort ?? "null"}");
        log.WriteLine($"loss, end unreadable : {unreadable ?? "null"}");
        log.WriteLine($"loss over 365 days   : {longLoss ?? "null"}");
        log.WriteLine($"faulted              : {faulted ?? "null"}");
        log.WriteLine($"precedes the freeze  : {precedes ?? "null"}");
        log.WriteLine($"invalidated no trade : {invalidated ?? "null"}");

        Assert.Equal(Inconclusive.TooFewEvents, noTrade);
        Assert.Equal(Inconclusive.ShortHoldoutLoss, shortLoss);
        Assert.Equal(Inconclusive.ShortHoldoutLoss, justShort);
        Assert.Equal(Inconclusive.ShortHoldoutLoss, unreadable);
        Assert.Null(longLoss);
        Assert.Null(faulted);
        Assert.Null(precedes);
        Assert.Null(invalidated);
        Assert.Null(eligible);
        Assert.Null(promoted);
        Assert.Null(unjudged);
        Assert.Null(Inconclusive.Of(null, shortHoldout));

        // THE QUOTA'S ARITHMETIC: a third, rounded down, and none below three slots.
        Assert.Equal([0, 0, 0, 1, 1, 1, 2], Enumerable.Range(0, 7).Select(Inconclusive.Slots));
        Assert.Equal(TimeSpan.FromDays(90), Inconclusive.Term);
        Assert.NotEqual(AllocationPolicy.V1, AllocationPolicy.InconclusiveV1);
    }

    /// <summary>
    /// (g) THE VERDICT PATH IS THE BASE'S, BYTE FOR BYTE: both policy texts' shas, and the referee's note on an
    /// inconclusive refusal of each class — pinned at main <c>023413fa</c>, before this unit.
    /// </summary>
    [Fact]
    public void The_verdict_path_the_quota_reads_is_byte_identical_to_the_base()
    {
        var v1 = CampaignPolicy.Sha256Of(CampaignPolicy.V1);
        var paper = CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1);
        var noTrade = Sha256Hex.Of(RefereeFeedback.Text(Row(PromotionVerdict.Refused, PromotionReason.NoTrade)));
        var loss = Sha256Hex.Of(RefereeFeedback.Text(Row(PromotionVerdict.Refused, PromotionReason.NotProfitable)));

        log.WriteLine($"V1      : {v1}");
        log.WriteLine($"PaperV1 : {paper}");
        log.WriteLine($"no trade: {noTrade}");
        log.WriteLine($"loss    : {loss}");

        Assert.Equal("227cce19f0bcda880ed926e5760fc596391d345cce558a722bed9bf0f292cd70", v1);
        Assert.Equal("489fcd999eb5afd14557d84a1cb991a65201f70e5cde2ff1eaa358dd2df466e8", paper);
        Assert.Equal("87b0ca6d1ceebc7bf6eda1a97467cbcf2af5b29af37ff7a7c350ef92f7141262", noTrade);
        Assert.Equal("abb4d280442bc50533e0d5e711c896292a7333be8eabd9830126ebf93a4ce618", loss);
    }
}
