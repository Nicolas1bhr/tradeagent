using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// THE DEFAULTS THE OWNER ACCEPTS IN ONE PRESS DURING SETUP (<c>U-evidence-campaign</c> item 1; R25 D7): which months of the
/// first download are held back as the private test the AI is judged on. The Settings page's two presses — Download and
/// "Hold these bars back" — stay where they are; this is what the setup step presses for him, with the date chosen here
/// rather than typed.
///
/// <para><b>The last <see cref="HoldoutMonths"/> complete calendar months of the download</b>, from the first instant of
/// the earliest of them, UTC. Whole months because the archive is published in whole months and a boundary that fell
/// inside one would hold back a part of a file nobody can point at; the most recent ones because a holdout is the
/// months nearest the present a strategy will trade in. Three of twelve keeps nine months for research and is a choice,
/// stated: <c>Inconclusive.ShortHoldout</c> calls any holdout under a year short, so a loss over these months is
/// inconclusive rather than a refutation — which is what three months can honestly say.</para>
///
/// <para><b>It never chooses a date for a dataset that already has one.</b> A cutoff the owner set is his, and moving it
/// later while no campaign holds it would RELEASE months he held back; the setup step opens the campaign at the cutoff
/// that is there (<c>EvidenceCampaigns.SetUpAsync</c>).</para>
/// </summary>
public static class EvidenceDefaults
{
    /// <summary>How many of the download's most recent complete months are held back.</summary>
    public const int HoldoutMonths = 3;

    /// <summary>
    /// THE CUTOFF THE SETUP STEP HOLDS BACK FROM, or null with nothing to hold: no bar recorded, or a download too short to
    /// keep at least one research month before the held months. Midnight UTC on the first day of the earliest held month.
    /// </summary>
    public static DateTimeOffset? Cutoff(DatasetRecord set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.FirstBar is not { } first || set.LastBar is not { } last) return null;

        var newest = last.UtcDateTime;
        var cutoff = new DateTimeOffset(newest.Year, newest.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-(HoldoutMonths - 1));

        // AT LEAST ONE WHOLE RESEARCH MONTH BEFORE IT: a holdout with nothing before it is a campaign nobody can run a trial
        // under, so no verdict could ever be asked of it.
        return first.ToUniversalTime() <= cutoff.AddMonths(-1) ? cutoff : null;
    }
}
