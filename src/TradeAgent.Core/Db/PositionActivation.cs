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
/// WHAT A REFUSAL TO TURN IS ABOUT (<c>docs/ORCHESTRATION.md</c> § 2.5, the orchestrator's ruling of 2026-10-11, <c>U-org-wakes</c>).
/// The loop acts on the kind and never on the words.
/// </summary>
public enum RefusalKind
{
    /// <summary>
    /// ABOUT THE POSITION ITSELF — its standing (dormant, ended, unknown) or a capability its seat cannot carry. Its due wakes
    /// SETTLE, recorded <see cref="MissionEventDisposition.Refused"/>, and are never retried: no pass can make it runnable.
    /// </summary>
    Position,

    /// <summary>
    /// ABOUT THE HOST OR THE LAUNCH — no proven cell on this host, the no-unconfined-neighbour rule, no launcher, a held order
    /// grant, an unwritable ledger or chart. The pass STEPS OVER the position and keeps every wake: the host may answer
    /// differently at the next look.
    /// </summary>
    HostOrLaunch
}

/// <summary>
/// WHY A POSITION MAY NOT TURN, typed (<c>U-org-wakes</c>): the kind the loop acts on and the sentence the owner reads. The owner's
/// words are never settled by a refusal of either kind — they stay owed and show undelivered with <see cref="Words"/>.
/// </summary>
public sealed record TurnRefusal(RefusalKind Kind, string Words);

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

    /// <summary>
    /// THE SAME ANSWER, TYPED (<c>U-org-wakes</c>): null where <see cref="Refusal"/> is null; otherwise its sentence with what it
    /// is about. A position the chart does not hold as active — unknown, dormant, ended, or another id's row — is refused
    /// <see cref="RefusalKind.Position"/>: nothing a later pass can do makes it runnable. An ACTIVE position this build will not
    /// activate is refused <see cref="RefusalKind.HostOrLaunch"/>: what is missing is this build's gate and this host's proven
    /// cell (ORCHESTRATION § 2.3), a fact about the host and never about the position, so its wakes are kept.
    /// <paramref name="standing"/> null is the chart's answer "no such position"; a chart that could not be READ never reaches
    /// here — its caller throws, and a throw is no verdict.
    /// </summary>
    public static TurnRefusal? Typed(string id, PositionStanding? standing) => Refusal(id, standing) is not { } words
        ? null
        : new TurnRefusal(
            standing is not null && standing.Id == id && standing.Status == OrgPositionStatus.Active
                ? RefusalKind.HostOrLaunch
                : RefusalKind.Position,
            words);
}
