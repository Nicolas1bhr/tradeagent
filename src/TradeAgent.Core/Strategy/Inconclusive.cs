using TradeAgent.Core.Db;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// A REFUSAL THE REFEREE COULD NOT MAKE DISCRIMINATE, and the one path it opens into paper (<c>U-referee-v2b</c>).
///
/// <para><c>docs/EDGE-FACTORY.md</c> § 4.5: "one INCONCLUSIVE path into paper, whatever the reason … a trial-charged
/// forward-incubation quota — at most a third of paper slots and one per family at a time". A refusal is INCONCLUSIVE
/// when it says nothing about the version's edge: it closed no trade on the held-back months (too few events), or what it
/// closed did not cover its costs over a holdout too short to tell — under <see cref="ShortHoldout"/>, R04's owner
/// default and its ADDIS floor (<c>docs/research/2026-10-02/R04-evolution-statistics.md</c>:255, :298); at 91 days a true
/// Sharpe-1 version nets above zero only 69% of the time (R10-calc's model).</para>
///
/// <para><b>Closed vocabulary, and a refusal it does not name is not inconclusive.</b> A run that did not complete is the
/// strategy faulting and is never inconclusive; a standing that is not <see cref="PromotionState.Refused"/> — invalidated,
/// unjudged, promoted, paper-eligible — is never inconclusive; a holdout whose end cannot be read counts as SHORT, the
/// reading the referee's own span check takes of a dataset with no end (refused, never guessed long).</para>
///
/// <para><b>Nothing here re-judges a verdict.</b> The refusal stays refused — <c>Promotions.Standing</c>, the
/// promotion row and both policy texts are untouched — and this is a read of it. What it opens is a paper slot, for
/// <see cref="Term"/>, charged one trial, under <see cref="AllocationPolicy.InconclusiveV1"/>; it confers no live
/// authority, because no live reader reads a paper row.</para>
/// </summary>
public static class Inconclusive
{
    /// <summary>Inconclusive: <see cref="PromotionReason.NoTrade"/> — nothing closed, so there are too few events to judge.</summary>
    public const string TooFewEvents = "too-few-events";

    /// <summary>Inconclusive: <see cref="PromotionReason.NotProfitable"/> over a holdout under <see cref="ShortHoldout"/>.</summary>
    public const string ShortHoldoutLoss = "loss-over-a-short-holdout";

    /// <summary>Whether a word is one of this vocabulary's two classes.</summary>
    public static bool IsKnown(string? c) => c is TooFewEvents or ShortHoldoutLoss;

    /// <summary>
    /// THE HOLDOUT A LOSS MUST SPAN TO BE CONCLUSIVE: 365 days. Under it a version that nets below zero after costs is
    /// a version the held-back months could not tell from one with an edge.
    /// </summary>
    public static readonly TimeSpan ShortHoldout = TimeSpan.FromDays(365);

    /// <summary>
    /// ONE TERM: 90 days from the slot's first instant (R04 E5's minimum, <c>R04-evolution-statistics.md</c>:256). One
    /// per version, never extended — renewal is <c>U-incubator</c>'s.
    /// </summary>
    public static readonly TimeSpan Term = TimeSpan.FromDays(90);

    /// <summary>
    /// HOW MANY OF AN ENVELOPE'S SLOTS THE QUOTA MAY HOLD: a third, rounded down — so an envelope of fewer than three
    /// slots holds none.
    /// </summary>
    public static int Slots(int maxDeployments) => maxDeployments < 3 ? 0 : maxDeployments / 3;

    /// <summary>
    /// WHICH INCONCLUSIVE CLASS THIS STANDING IS, or null because it is not inconclusive. <paramref name="holdout"/> is
    /// the verdict's holdout span — its campaign's <c>holdout_from</c> to its dataset's last close
    /// (<c>CampaignStore.HoldoutSpan</c>) — and null when that end cannot be read, which reads as short.
    /// </summary>
    public static string? Of(PromotionStanding? standing, TimeSpan? holdout)
    {
        // A REFUSAL THAT STANDS, AND ONLY THAT. `Invalidated` carries the same row and is read as nothing here: a
        // refusal on evidence TradeAgent no longer vouches for is no reason to observe anything.
        if (standing is not { State: PromotionState.Refused, Promotion: { } promotion }) return null;
        if (!string.Equals(promotion.Verdict, PromotionVerdict.Refused, StringComparison.Ordinal)) return null;

        return promotion.Reason switch
        {
            PromotionReason.NoTrade => TooFewEvents,
            PromotionReason.NotProfitable when holdout is not { } span || span < ShortHoldout => ShortHoldoutLoss,
            _ => null
        };
    }
}
