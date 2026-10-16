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
/// WHICH OF THE TWO REFUSALS A REFUSAL IS (<c>docs/ORCHESTRATION.md</c> § 2.5 as amended <c>7509521a</c>), carried as DATA so a caller
/// routes by it and never by parsing words. Neither kind settles, consumes or marks handled anything in this build: the loop steps
/// over a refused position with every wake kept, and the owner's words are never settled by a refusal of either kind.
/// </summary>
public enum RefusalKind
{
    /// <summary>
    /// ABOUT THE POSITION ITSELF: its standing (dormant, ended, unknown) or a capability its declared seat cannot carry. Retrying
    /// will not change it; <c>U-org-wakes</c> (O3) settles its due wakes with a recorded disposition, routing by this kind.
    /// </summary>
    Position,

    /// <summary>
    /// ABOUT THE HOST OR THE LAUNCH: no proven executor cell recorded on the host, an unconfined neighbour, a verified seat that
    /// differs from the declared one, a register nobody set. It steps over and keeps every wake, under the loop's backoff.
    /// </summary>
    HostOrLaunch
}

/// <summary>An activation-gate refusal: its <see cref="RefusalKind"/> as data, and the sentence that says why in the owner's words.</summary>
public sealed record GateRefusal(RefusalKind Kind, string Words);

/// <summary>
/// WHAT THE ACTIVATION GATE READS ABOUT ONE POSITION AND ITS HOST at the instant it is asked, all of it the app's own answer: the
/// chart's standing; the DECLARED class of the seat the position would launch on (the runtime the app would open it on — never a
/// field of <c>runtimes.json</c>); whether this host has a proven executor-cell class recorded (<see cref="CellProofStore"/>); the
/// active positions beyond the legacy pair (<see cref="OrgStore.ActivePositionsBeyondLegacy"/>); and the positions with an open
/// unconfined launch other than this one (the meter's open set).
/// </summary>
public sealed record SeatFacts
{
    public PositionStanding? Standing { get; init; }
    public string? DeclaredClass { get; init; }
    public bool ProvenCell { get; init; }
    public IReadOnlyList<string> ActiveBeyondLegacy { get; init; } = [];
    public IReadOnlyList<string> OpenUnconfined { get; init; } = [];
}

/// <summary>
/// THE REGISTER THE ACTIVATION GATE ASKS, process-wide in the product for the reason <c>AgentGrants.Shared</c> is one: the seam,
/// the loop and the last check before a process or a request all ask the same answer. The app fills it when it composes the AI
/// (<c>AppHost.ComposeTheAi</c>); a test's host is handed its own. <b>Unset, a null answer or a throw is "not known"</b>, which
/// refuses every position but the legacy pair (<see cref="PositionActivation.Decide"/>).
/// </summary>
public sealed class SeatRegister
{
    /// <summary>The register the product uses. Only a test's host is handed another.</summary>
    public static SeatRegister Shared { get; } = new();

    /// <summary>The facts for one position id, read at every ask; null until the app sets it.</summary>
    public Func<string, SeatFacts?>? Facts { get; set; }

    /// <summary>The facts for <paramref name="id"/>, or null when the register is unset, answered null, or threw.</summary>
    public SeatFacts? Read(string id)
    {
        try { return Facts?.Invoke(id); }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// THE ACTIVATION SEAM AND THE ACTIVATION GATE (<c>U-org-principals</c>, <c>U-thinking-seats-a</c>; ORCHESTRATION § 2.3): whether a
/// position may open a conversation and launch at all. One rule (<see cref="Decide"/>), asked FIRST in <c>AppHost.ConversationFor</c>
/// and by the loop with the DECLARED seat, and again — fail closed — with the VERIFIED seat in <c>TurnMeter.Begin</c> and immediately
/// before the process or the request (<see cref="AtLaunch"/>).
///
/// <para><b>What it holds.</b> A position runs only on a seat whose assurance meets the minimum of every capability the launch has —
/// the position's plus the seat's own (a CLI seat has code) — read from code (<see cref="CapabilityPolicy"/>). <b>And literally, the
/// owner's answer:</b> until this host has a PROVEN executor-cell class recorded, no position other than the legacy pair activates
/// on any real-model seat, a harness head included; no flag or setting lifts it. <b>No unconfined neighbour:</b> while a position
/// beyond the legacy pair is active, every unconfined launch — the legacy pair and the owner's chat included — is refused, and a
/// position beyond the pair is refused while an unconfined launch is open. The legacy pair otherwise keep their existing two-role
/// behaviour on unconfined seats. Positions are written by nothing until <c>U-org-verbs</c>, so the neighbour rule has no production
/// effect before then.</para>
/// </summary>
public static class PositionActivation
{
    /// <summary>The process-wide register the seam asks (<see cref="SeatRegister.Shared"/>).</summary>
    public static SeatRegister Seats => SeatRegister.Shared;

    /// <summary>
    /// Null when <paramref name="id"/> may open a conversation, otherwise the sentence that says why not — <see cref="Decide"/>'s
    /// words over the process-wide register. <paramref name="standing"/> is the chart's answer for that id
    /// (<see cref="OrgStore.Standing"/>), or null where the chart holds no such position or could not be read — which refuses.
    /// </summary>
    public static string? Refusal(string id, PositionStanding? standing) => Decide(id, standing, Seats.Read(id))?.Words;

    /// <summary>
    /// THE GATE'S ONE RULE: null when <paramref name="id"/> may run, otherwise the refusal with its kind. <paramref name="facts"/>
    /// null is a register that is unset, answered null or threw. <paramref name="verifiedClass"/> is the seat actually about to
    /// start, at launch; null at the seam, where only the declared seat is known.
    /// </summary>
    public static GateRefusal? Decide(string id, PositionStanding? standing, SeatFacts? facts, string? verifiedClass = null)
    {
        if (standing is null || standing.Id != id || standing.Status != OrgPositionStatus.Active)
            return new(RefusalKind.Position, $"'{id}' is not an active position, so no conversation is opened for it and nothing is "
                                             + "folded into another position's.");

        if (facts is null) return standing.IsLegacy ? null : Unset(id);

        if (verifiedClass is not null && facts.DeclaredClass is not null && verifiedClass != facts.DeclaredClass)
            return new(RefusalKind.HostOrLaunch,
                $"'{id}' was to run on a {facts.DeclaredClass} seat and the seat about to start is a {verifiedClass} seat, so nothing "
                + "is started. The next turn checks again.");

        var seatClass = verifiedClass ?? facts.DeclaredClass;

        if (standing.IsLegacy)
        {
            // THE LEGACY PAIR, FOR THEIR EXISTING TWO-ROLE BEHAVIOUR — unless they would be an unconfined neighbour. A seat whose
            // class is not known is taken to be unconfined: the neighbour rule is never passed on a guess.
            var assurance = SeatClasses.AssuranceOf(seatClass) ?? SeatAssurance.Unconfined;
            if (assurance == SeatAssurance.Unconfined && facts.ActiveBeyondLegacy.Count > 0)
                return new(RefusalKind.HostOrLaunch,
                    $"'{id}' runs unconfined, and {Names(facts.ActiveBeyondLegacy)} {Be(facts.ActiveBeyondLegacy)} active beyond the "
                    + "legacy pair on this host: the legacy pair run confined too, or not there (ORCHESTRATION § 2.3). Nothing is "
                    + "started. To run it, end or put to sleep the positions named, or run the legacy pair in a cell.");
            return null;
        }

        // NO DECLARED SEAT IS AN UNSET REGISTER, whatever seat is about to start: the launch is checked against what the app said
        // it would run on, and nothing said so.
        if (facts.DeclaredClass is null) return Unset(id);

        if (SeatClasses.AssuranceOf(seatClass) is not { } seat)
            return new(RefusalKind.HostOrLaunch,
                $"'{id}' would run on a '{seatClass}' seat, a class this build does not know, "
                + "so nothing is started for it. A seat's class is the app's own answer, never read from a file.");

        if (CapabilityPolicy.Binding(CapabilityPolicy.CapabilitiesOf(standing, seatClass!), seat) is { } missed)
            return new(RefusalKind.Position,
                $"'{id}' cannot run on a {seatClass} seat: {Words(missed)} needs a {SeatClasses.Name(CapabilityPolicy.Minimum(missed))} "
                + $"seat and a {seatClass} seat is {SeatClasses.Name(seat)}"
                + (missed == Capability.Code ? " — its shell is code, and code needs a confined cell" : "")
                + ". Nothing is started for it.");

        if (SeatClasses.RunsModel(seatClass!) && !facts.ProvenCell)
            return new(RefusalKind.HostOrLaunch,
                $"'{id}' is not activated on a real-model seat ({seatClass}): this host has no proven executor cell recorded, and until "
                + "it has one no position beyond the legacy pair runs on a real model (the owner's answer, ORCHESTRATION § 2.3). "
                + "Nothing is started; its wakes are kept.");

        if (facts.OpenUnconfined.Count > 0)
            return new(RefusalKind.HostOrLaunch,
                $"'{id}' is not started while an unconfined launch is open on this host ({Names(facts.OpenUnconfined)}): no position "
                + "beyond the legacy pair runs beside an unconfined neighbour. It is tried again when that turn has ended.");

        return null;
    }

    /// <summary>
    /// THE LAST CHECK, IMMEDIATELY BEFORE THE PROCESS OR THE REQUEST, on a host that may have no meter: <see cref="Decide"/> with the
    /// seat actually about to start. A register that is unset, answered null or threw passes the legacy pair and refuses every other
    /// id — the same fail-closed answer as the seam's.
    /// </summary>
    public static GateRefusal? AtLaunch(string role, string verifiedClass, SeatRegister? seats = null)
    {
        var facts = (seats ?? Seats).Read(role);
        if (facts is null) return CouncilRoles.IsKnown(role) ? null : Unset(role);
        return Decide(role, facts.Standing, facts, verifiedClass);
    }

    static GateRefusal Unset(string id) => new(RefusalKind.HostOrLaunch,
        $"'{id}' is a position beyond the legacy pair, and nothing has told this host which seat it would run on (the activation "
        + "gate's register is not set), so nothing is started for it. Its wakes are kept.");

    static string Names(IReadOnlyList<string> ids) => string.Join(", ", ids.Select(i => $"'{i}'"));

    static string Be(IReadOnlyList<string> ids) => ids.Count == 1 ? "is" : "are";

    static string Words(Capability c) => c switch
    {
        Capability.Reads => "reading",
        Capability.EvidenceRequests => "asking for evidence",
        Capability.DecisionVerbs => "a decision verb",
        Capability.Code => "running code",
        Capability.Helpers => "starting helpers",
        Capability.Orders => "placing orders",
        _ => c.ToString()
    };
}
