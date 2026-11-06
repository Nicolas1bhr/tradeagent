namespace TradeAgent.Core.Strategy;

/// <summary>
/// WHETHER A HOLDOUT IS LONG ENOUGH FOR THE REFEREE TO BE CONCLUSIVE, IN THE OWNER'S WORDS (<c>U-evidence-holdout-length</c> item 3).
///
/// <para>Two rules of the referee read a year: <see cref="Inconclusive.ShortHoldout"/> calls a loss over a shorter holdout inconclusive
/// — a version it refuses for one reaches paper only through the inconclusive path — and <see cref="AlphaWealth.MinHoldoutDays"/> runs
/// the α-test only over that many known days. A span under the first can therefore never be conclusive; one at or above it CAN be (the
/// α-test still counts the run's known days). The span is <c>CampaignStore.HoldoutSpan</c>'s — the cutoff to the dataset's last close —
/// and one whose end cannot be read is short, as the referee reads it. A sentence, never a gate: nothing here changes a verdict.</para>
/// </summary>
public static class HoldoutLength
{
    /// <summary>The span in days, as the owner reads it: whole days, rounded down, so 364.9 days is never shown as 365.</summary>
    public static int Days(TimeSpan span) => (int)Math.Floor(Math.Max(0, span.TotalDays));

    /// <summary>Whether a holdout of <paramref name="span"/> can yield a conclusive verdict: at least <see cref="Inconclusive.ShortHoldout"/>.</summary>
    public static bool CanBeConclusive(TimeSpan? span) => span is { } s && s >= Inconclusive.ShortHoldout;

    /// <summary>
    /// The sentence: <paramref name="subject"/> ("Its holdout", "A holdout from 2026-07-01 on BTCUSDT's newest download") and how long it
    /// is, then whether a verdict over it can be conclusive — or why none can, and the one path a refused version still has to paper.
    /// </summary>
    public static string Words(TimeSpan? span, string subject = "Its holdout")
    {
        var year = (int)Inconclusive.ShortHoldout.TotalDays;
        // TRUE OF EVERY VERDICT, the favourable one included (the Codex review's P2): over a short holdout the referee still promotes a
        // version whose result clears its policy — the statistical test is sealed and gates nothing — so the sentence says that too.
        if (span is not { } s)
            return $"{subject}'s end cannot be read, so it counts as under {year} days — too short for a conclusive verdict.";
        return CanBeConclusive(s)
            ? $"{subject} spans {Days(s):N0} days — long enough for the referee to reach a conclusive verdict."
            : $"{subject} spans {Days(s):N0} days, under {year} — too short for a conclusive verdict: the referee's statistical test needs "
              + $"{AlphaWealth.MinHoldoutDays:N0} known days, a version refused for a loss over it reaches paper only through the inconclusive "
              + "path, and one profitable over it reaches paper on that result alone.";
    }
}
