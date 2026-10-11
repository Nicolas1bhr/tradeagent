namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT A POSITION IS DERIVED TO BE FROM THE CHART, never stored beside it (<c>docs/ORCHESTRATION.md</c> § 2.1): it heads
/// a unit, sits on a staff unit, or is a member.
/// </summary>
public static class OrgPositionKind
{
    public const string Head = "head";
    public const string Staff = "staff";
    public const string Member = "member";
}

/// <summary>
/// ONE POSITION'S STANDING, as the chart holds it at the instant it is read (<see cref="OrgStore.Standing"/>). The kind is
/// derived, the home is computed from the id (<see cref="CouncilRoles.HomeDir"/>) and never a path read from a row, and
/// <see cref="IsLegacy"/> is whether the id is one of the two positions that ran before the chart (<c>operations</c>,
/// <c>research</c>). Nothing here grants anything: what a position may run is <see cref="PositionActivation"/>'s answer.
/// </summary>
public sealed record PositionStanding(string Id, string UnitId, string Status, string Kind, bool IsLegacy, string HomeDir);

/// <summary>
/// THE ACTIVATION SEAM (<c>U-org-principals</c>; ORCHESTRATION § 2.3): whether a position may open a conversation at all,
/// asked FIRST in <c>AppHost.ConversationFor</c> so a refused position opens nothing and writes no row.
///
/// <para><b>Representable, never runnable — fail closed.</b> This build has no activation gate (capability × seat
/// assurance, <c>U-thinking-seats</c>), so the answer is null ONLY for the legacy pair, for their existing two-role
/// behaviour; every other id is refused in a sentence. <c>U-thinking-seats</c> replaces this method's body and keeps its
/// signature.</para>
/// </summary>
public static class PositionActivation
{
    /// <summary>
    /// Null when <paramref name="id"/> may open a conversation, otherwise the sentence that says why not.
    /// <paramref name="standing"/> is the chart's answer for that id (<see cref="OrgStore.Standing"/>), or null where the
    /// chart holds no such position or could not be read — which refuses.
    /// </summary>
    public static string? Refusal(string id, PositionStanding? standing)
    {
        if (standing is null || standing.Id != id || standing.Status != OrgPositionStatus.Active)
            return $"'{id}' is not an active position, so no conversation is opened for it and nothing is folded "
                   + "into another position's.";
        if (!standing.IsLegacy)
            return $"'{id}' is an active position, and this build has no activation gate for it (U-thinking-seats): a "
                   + "position after the legacy pair is representable, never runnable on its own, so no conversation "
                   + "is opened for it.";
        return null;
    }
}
