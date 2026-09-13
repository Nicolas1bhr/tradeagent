using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// WHAT A DATASET'S BARS COUNT AS (<c>dataset.evaluation_class</c>).
///
/// <para>Two words and no more, because there are exactly two things a run over a dataset can be:
/// evidence about a strategy, or a check that the plumbing works.</para>
/// </summary>
public static class EvaluationClass
{
    /// <summary>
    /// Real collected history. A run over it is a research trial and is CHARGED against the
    /// campaign, because it is evidence somebody will be judged on.
    /// </summary>
    public const string Research = "research";

    /// <summary>
    /// Bars made up to exercise the machinery — a handful of minutes written by a test or by the
    /// owner. <c>docs/COUNCIL.md</c>: "fixture runs establishing plumbing only". A run over one is
    /// charged NOTHING and is never evidence, so a team cannot buy trials by inventing bars.
    /// </summary>
    public const string Fixture = "fixture";

    public static bool IsKnown(string? c) => c is Research or Fixture;

    /// <summary>What a row that names no class means. Every row written before schema 14 is research.</summary>
    public static string Or(string? c) => IsKnown(c) ? c! : Research;
}

/// <summary>
/// WHO IS ASKING FOR BARS, AND THEREFORE WHETHER THE HOLDOUT IS AMONG THEM.
///
/// <para><b>There are two audiences and the second one cannot be minted outside this assembly.</b>
/// <see cref="Pipe"/> is every caller on the agent-facing channel — the Research Director, the
/// Operations Director, and a connection that proved no role at all — and it may never read a bar at
/// or after a dataset's <c>holdout_from</c>. The other is the referee's, held by
/// <c>Strategy.Referee</c>, reached only through a verdict that was CHARGED and RECORDED first, and
/// <c>internal</c> so that no assembly outside <c>TradeAgent.Core</c> — the gateway and its pipe
/// server included — can produce one at all. `HoldoutLedgerTests` holds that list of public doors to
/// exactly one entry by name.</para>
///
/// <para><b>It is a required argument on both readers, deliberately.</b>
/// <see cref="DatasetReader.Read"/> and <see cref="BarFeed.Open"/> cannot be called without one, so a
/// new op that wants bars has to say who is asking before it gets any. The check then happens INSIDE
/// the reader rather than beside it: a caller that forgets to look at the refusal is handed an empty
/// window, never a holdout bar. That is the whole reason the parameter exists instead of a helper
/// somebody has to remember — a leaked holdout cannot become unseen (<c>docs/COUNCIL.md</c>:212).</para>
/// </summary>
public sealed class BarAudience
{
    BarAudience(bool mayReadHoldout, string who)
    {
        MayReadHoldout = mayReadHoldout;
        Who = who;
    }

    /// <summary>Whether a bar at or after the cutoff may be served to this audience.</summary>
    public bool MayReadHoldout { get; }

    /// <summary>Who this is, in the words a refusal uses.</summary>
    public string Who { get; }

    /// <summary>
    /// A CALLER ON THE AGENT-FACING PIPE. Never reads the holdout, whatever role it proved.
    ///
    /// <para><paramref name="role"/> is for the refusal's wording and for nothing else. A null role is
    /// not "not research" and is not a default that could be read as the chair: it is a caller that
    /// proved nothing, and it gets the same refusal as both directors.</para>
    /// </summary>
    public static BarAudience Pipe(string? role) => new(false,
        CouncilRoles.IsKnown(role) ? $"the {CouncilRoles.Title(role!)}'s launch" : "a caller that proved no role");

    /// <summary>
    /// THE ONE DOOR PAST THE CUTOFF. In-process, internal, and reached only from a charged verdict —
    /// see <c>Strategy.Referee.RequestVerdict</c>. There is no pipe op and no CLI verb behind it.
    /// </summary>
    internal static BarAudience Referee { get; } = new(true, "the referee");
}

/// <summary>
/// THE HOLDOUT RULE, IN ONE PLACE: WHICH WINDOWS OF A DATASET MAY BE SERVED AT ALL.
///
/// <para><b>A holdout is a TIME CUTOFF on a dataset, not a second dataset.</b> `docs/COUNCIL.md` says
/// "holdout data the research process cannot reach" and does not say how, so this is a choice, stated
/// in `docs/CONTRACTS.md` as one: <c>dataset.holdout_from</c> is a UTC instant, INCLUSIVE, and every
/// bar whose open time is at or after it is private evaluation evidence. Splitting the file in two
/// would have given the research process a dataset id it could ask for and a second set of hashes to
/// keep true; a cutoff cannot be asked for, because asking is what this refuses.</para>
///
/// <para><b>A window that reaches the cutoff is REFUSED, never clipped.</b> An answer quietly cut
/// short at the cutoff is a different window from the one that was asked for, and nothing in the
/// reply would say so — the caller would attribute a result to a year it never ran over. It is the
/// same judgement <see cref="DatasetReader.MaxBars"/> already makes about the bar cap, applied to the
/// one boundary that cannot be undone.</para>
///
/// <para><b>An unbounded window is a window that reaches it.</b> "Every bar of this dataset" over a
/// dataset with a cutoff is a request for the holdout, so it is refused and the caller is told the
/// cutoff and asked to name a <c>to</c>. Reading "no end" as "up to the cutoff" would be clipping
/// with extra steps.</para>
/// </summary>
public static class Holdout
{
    /// <summary>
    /// WHY THESE BARS ARE NOT SERVED, in the words the caller reads, or null when they are.
    ///
    /// <para>The default is the refusal: it answers null only for an audience allowed past the cutoff,
    /// a dataset that has no cutoff at all, or a window whose END is proved to be before it.</para>
    /// </summary>
    public static string? Refusal(
        DatasetRecord set, BarAudience audience, DateTimeOffset? from, DateTimeOffset? to)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(audience);

        if (audience.MayReadHoldout) return null;
        if (set.HoldoutFrom is not { } cutoff) return null;
        if (to is { } hi && hi < cutoff) return null;

        var asked = (from, to) switch
        {
            ({ } lo, { } hi2) => $"the window asked for runs {lo:u} to {hi2:u}",
            ({ } lo, null) => $"the window asked for starts at {lo:u} and names no end",
            (null, { } hi2) => $"the window asked for ends at {hi2:u}",
            _ => "the window asked for names neither a start nor an end, so it is the whole dataset"
        };

        return $"dataset {set.Id} ({set.Pair} {set.Interval} {set.Version}) holds out every bar from "
            + $"{cutoff:u} onwards, and {asked}, which reaches them. Those bars are the private "
            + "evaluation evidence the research process never sees: TradeAgent serves them to no caller "
            + $"on this channel — not to {audience.Who}, not to either director, not to a connection "
            + "that proved no role at all. This is REFUSED rather than quietly cut short at the cutoff, "
            + "because an answer silently clipped is a different window from the one you asked for, and "
            + "a bar once served can never become holdout again. Ask for a window that ENDS before the "
            + $"cutoff: 'to' must be earlier than {cutoff:u}.";
    }
}
