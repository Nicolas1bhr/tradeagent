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
    /// grant, an unwritable ledger or chart, a verified seat that differs from
    /// the declared one, a register nobody set. The pass STEPS OVER the position and keeps every wake: the host may answer
    /// differently at the next look.
    /// </summary>
    HostOrLaunch
}

/// <summary>
/// WHY A POSITION MAY NOT TURN, typed (<c>U-org-wakes</c>): the kind the loop acts on and the sentence the owner reads. The owner's
/// words are never settled by a refusal of either kind — they stay owed and show undelivered with <see cref="Words"/>. The
/// activation gate's every refusal is one of these (<c>U-thinking-seats-a</c> adopts O3's type; there is no second one).
/// </summary>
public sealed record TurnRefusal(RefusalKind Kind, string Words);

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

    /// <summary>
    /// The ACTIVE positions other than this one whose declared seat runs a model below Confined — the legacy pair counted — read
    /// for the owner's unconfined-seat ceiling (<see cref="UnconfinedSeatCeiling"/>; <c>U-org-verbs-d</c> item 5).
    /// </summary>
    public IReadOnlyList<string> UnconfinedActive { get; init; } = [];

    /// <summary>The owner's unconfined-seat setting as stored; it only lowers the code's ceiling (<see cref="UnconfinedSeatCeiling.Effective"/>).</summary>
    public int UnconfinedSeatSetting { get; init; } = UnconfinedSeatCeiling.Ceiling;
}

/// <summary>
/// THE OWNER'S UNCONFINED-SEAT CEILING (<c>U-org-verbs-d</c> item 5; ORGANISATION § 10 :319-321; VISION CRED-001: growth past three
/// waits for containment; R24 § 4.3 keeps it his): every seat that runs a model below <see cref="SeatAssurance.Confined"/> can read
/// what its process can — today the owner's own AI login — so a position on such a seat is refused activation and launch while that
/// many such ACTIVE positions, the legacy pair counted, reach <c>min(setting, 3)</c>. The 3 is CODE: a setting lowers it and never
/// raises it (a value above 3 reads as 3, as <c>LanePolicy.Clamp</c> does), because a setting lives where an unconfined neighbour can
/// write it. A seat that runs no model (a puppet) or runs in a proven cell is exempt; a class nobody declared is counted. The legacy
/// pair are counted and never refused by it: they are the two that ran before the chart, and anything about <c>operations</c> is
/// the security board's (R24 § 4.1), never a seat count's.
/// </summary>
public static class UnconfinedSeatCeiling
{
    /// <summary>The most unconfined seats, in code (ORGANISATION § 10's default: the legacy pair and the chief).</summary>
    public const int Ceiling = 3;

    /// <summary>A setting's value as the ceiling: at least 0, at most <see cref="Ceiling"/>.</summary>
    public static int Effective(int setting) => Math.Clamp(setting, 0, Ceiling);

    /// <summary>Whether a seat of <paramref name="seatClass"/> counts: it runs a model below Confined, or nobody declared it.</summary>
    public static bool Counts(string? seatClass) =>
        seatClass is null || (SeatClasses.RunsModel(seatClass) && (SeatClasses.AssuranceOf(seatClass) ?? SeatAssurance.Unconfined) < SeatAssurance.Confined);

    /// <summary>
    /// WHY <paramref name="id"/>, on a seat of <paramref name="seatClass"/>, MAY NOT BE ACTIVATED OR STARTED beside
    /// <paramref name="active"/> — the active positions on counted seats — under the owner's <paramref name="setting"/>, or null. A
    /// refusal about the host (<see cref="RefusalKind.HostOrLaunch"/>): its wakes are kept, and it runs once a seat frees or a cell is
    /// proven. Both counts are named.
    /// </summary>
    public static TurnRefusal? Refusal(string id, string? seatClass, IReadOnlyList<string> active, int setting)
    {
        if (CouncilRoles.IsKnown(id) || !Counts(seatClass)) return null;
        var others = active.Where(a => a != id).Distinct(StringComparer.Ordinal).ToList();
        var ceiling = Effective(setting);
        if (others.Count < ceiling) return null;
        return new(RefusalKind.HostOrLaunch,
            $"'{id}' would run a model on a {seatClass ?? "undeclared"} seat, below a proven cell, and {others.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
            + $"such position{(others.Count == 1 ? " is" : "s are")} active ({string.Join(", ", others.Select(o => $"'{o}'"))}) — the owner's "
            + $"unconfined-seat ceiling is {ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture)} (his setting "
            + $"{setting.ToString(System.Globalization.CultureInfo.InvariantCulture)}, never above {Ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
            + "until containment is proven). Nothing is activated or started for it; its wakes are kept.");
    }
}

/// <summary>
/// THE REGISTER THE ACTIVATION GATE ASKS, process-wide in the product for the reason <c>AgentGrants.Shared</c> is one: the seam,
/// the loop and the last check before a process or a request all ask the same answer. The app fills it when it composes the AI
/// (<c>AppHost.ComposeTheAi</c>); a test's host is handed its own. <b>Unset, a null answer or a throw is "not known"</b>, which
/// refuses every position beyond the legacy pair (<see cref="PositionActivation.Decide"/>), and the pair's unconfined launch unless
/// the neighbour read below answers.
///
/// <para><b>The neighbour read stands on its own</b> (<see cref="Neighbours"/>; review of <c>U-thinking-seats-a</c>, item 2): the
/// facts about one position can fail to read for reasons that say nothing about the host, and the legacy pair's unconfined launch
/// must still be asked whether a position beyond them is active. On a register the app has set, the legacy pair run unconfined
/// only when that read SUCCEEDS and names nobody; a read that fails refuses them, about the host — and so does a register nothing
/// set at all (fix pass 2, item 5): the product sets the shared one first thing in <c>ComposeTheAi</c>, before any conversation
/// exists, so a path that forgot it fails closed. Only a test host (<see cref="PositionActivation.TestHost"/>) keeps the legacy
/// pair's behaviour from before the gate on an unset register.</para>
/// </summary>
public sealed class SeatRegister
{
    /// <summary>The register the product uses. Only a test's host is handed another.</summary>
    public static SeatRegister Shared { get; } = new();

    /// <summary>The facts for one position id, read at every ask; null until the app sets it.</summary>
    public Func<string, SeatFacts?>? Facts { get; set; }

    /// <summary>
    /// THE CHART'S STANDING FOR ONE POSITION ALONE (<c>U-execution-environment</c>), read at every ask: what a session and a home read
    /// for the position's KIND, which decides the class of seat it starts (<c>RuntimeManifest.SessionSeatClassFor</c>) — cheaper than
    /// the whole facts and with no class in it to go round in a circle. Unset, the facts' standing is read instead.
    /// </summary>
    public Func<string, PositionStanding?>? Standing { get; set; }

    /// <summary>
    /// THE KIND OF <paramref name="id"/> AS THE CHART DERIVES IT (<see cref="OrgPositionKind"/>), or null where nothing could say: no
    /// register set, no such position, a read that failed. Null is never a Member — a session that cannot read the kind starts the
    /// class it would start for a kind nobody named, and the gate, which reads the chart itself, refuses that class where it is not the
    /// declared one.
    /// </summary>
    public string? KindOf(string id)
    {
        try { return (Standing is { } standing ? standing(id) : Facts?.Invoke(id)?.Standing)?.Kind; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// THE ACTIVE POSITIONS BEYOND THE LEGACY PAIR, read at every ask and on its own, so a failure to read one position's facts
    /// never skips the no-unconfined-neighbour rule; null until the app sets it.
    /// </summary>
    public Func<IReadOnlyList<string>>? Neighbours { get; set; }

    /// <summary>Whether the app has set this register at all.</summary>
    public bool IsSet => Facts is not null || Neighbours is not null;

    /// <summary>The facts for <paramref name="id"/>, or null when the register is unset, answered null, or threw.</summary>
    public SeatFacts? Read(string id)
    {
        try { return Facts?.Invoke(id); }
        catch (Exception) { return null; }
    }

    /// <summary>The active positions beyond the legacy pair, or null when that read is unset, answered null, or threw.</summary>
    public IReadOnlyList<string>? ReadNeighbours()
    {
        try { return Neighbours?.Invoke(); }
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
/// behaviour on unconfined seats. <c>org-hire</c> (<c>U-org-verbs-c</c>) writes a position DORMANT and no organisation verb activates
/// one, so the neighbour rule has no production effect until a position is activated.</para>
/// </summary>
public static class PositionActivation
{
    /// <summary>The process-wide register the seam asks (<see cref="SeatRegister.Shared"/>).</summary>
    public static SeatRegister Seats => SeatRegister.Shared;

    /// <summary>
    /// WHETHER THIS PROCESS IS A TEST HOST — internal, set only by the unit-test assembly's module initializer
    /// (<c>InternalsVisibleTo</c>); nothing in the product sets it and no file, setting or pipe op reaches it. Only there does a
    /// register nothing set leave the legacy pair's unconfined launch as it was before the gate.
    /// </summary>
    internal static bool TestHost { get; set; }

    /// <summary>
    /// Null when <paramref name="id"/> may open a conversation, otherwise the sentence that says why not — <see cref="Decide"/>'s
    /// words over the process-wide register. <paramref name="standing"/> is the chart's answer for that id
    /// (<see cref="OrgStore.Standing"/>), or null where the chart holds no such position or could not be read — which refuses.
    /// </summary>
    public static string? Refusal(string id, PositionStanding? standing) => Ask(id, standing, Seats)?.Words;

    /// <summary>
    /// <see cref="Decide"/> OVER A REGISTER, the way the seam, the loop and the last check ask it: the facts for <paramref name="id"/>
    /// as the register reads them — and where they cannot be read for one of the legacy pair, the register's own neighbour read
    /// (<see cref="SeatRegister.Neighbours"/>) decides, so a failed read never passes an unconfined launch beside an active position.
    /// </summary>
    public static TurnRefusal? Ask(string id, PositionStanding? standing, SeatRegister seats, string? verifiedClass = null) =>
        Ask(id, standing, seats, verifiedClass, TestHost);

    /// <summary><see cref="Ask(string, PositionStanding?, SeatRegister, string?)"/> as a process that is, or is not, a test host.</summary>
    internal static TurnRefusal? Ask(string id, PositionStanding? standing, SeatRegister seats, string? verifiedClass, bool testHost)
    {
        if (seats.Read(id) is { } facts) return Decide(id, standing, facts, verifiedClass);
        if (standing is not { IsLegacy: true } || standing.Id != id || standing.Status != OrgPositionStatus.Active)
            return Decide(id, standing, null, verifiedClass);
        return Neighbour(id, seats, verifiedClass, testHost);
    }

    /// <summary>
    /// THE GATE'S ONE RULE: null when <paramref name="id"/> may run, otherwise the refusal with its kind. <paramref name="facts"/>
    /// null is a register that is unset, answered null or threw. <paramref name="verifiedClass"/> is the seat actually about to
    /// start, at launch; null at the seam, where only the declared seat is known.
    /// </summary>
    public static TurnRefusal? Decide(string id, PositionStanding? standing, SeatFacts? facts, string? verifiedClass = null)
    {
        if (standing is null || standing.Id != id || standing.Status != OrgPositionStatus.Active)
            return new(RefusalKind.Position, $"'{id}' is not an active position, so no conversation is opened for it and nothing is "
                                             + "folded into another position's.");

        // FACTS NOBODY COULD READ: every position beyond the pair is refused as unset, and so is one of the pair, whose
        // unconfined launch cannot be shown to have no neighbour from here (Ask reads the register's neighbour read instead).
        if (facts is null) return !standing.IsLegacy ? Unset(id) : Unconfined(verifiedClass) ? Unreadable(id) : null;

        if (verifiedClass is not null && facts.DeclaredClass is not null && verifiedClass != facts.DeclaredClass)
            return new(RefusalKind.HostOrLaunch,
                $"'{id}' was to run on a {facts.DeclaredClass} seat and the seat about to start is a {verifiedClass} seat, so nothing "
                + "is started. The next turn checks again.");

        var seatClass = verifiedClass ?? facts.DeclaredClass;

        if (standing.IsLegacy)
        {
            // THE LEGACY PAIR, FOR THEIR EXISTING TWO-ROLE BEHAVIOUR — unless they would be an unconfined neighbour. A seat whose
            // class is not known is taken to be unconfined: the neighbour rule is never passed on a guess.
            return Unconfined(seatClass) && facts.ActiveBeyondLegacy.Count > 0 ? Neighboured(id, facts.ActiveBeyondLegacy) : null;
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

        // THE OWNER'S UNCONFINED-SEAT CEILING (U-org-verbs-d item 5): a model below a proven cell counts, and three is the most.
        if (UnconfinedSeatCeiling.Refusal(id, seatClass, facts.UnconfinedActive, facts.UnconfinedSeatSetting) is { } over) return over;

        if (facts.OpenUnconfined.Count > 0)
            return new(RefusalKind.HostOrLaunch,
                $"'{id}' is not started while an unconfined launch is open on this host ({Names(facts.OpenUnconfined)}): no position "
                + "beyond the legacy pair runs beside an unconfined neighbour. It is tried again when that turn has ended.");

        return null;
    }

    /// <summary>
    /// THE SAME ANSWER, TYPED (<c>U-org-wakes</c>): the activation gate's refusal with its kind, over the process-wide register
    /// (<see cref="Ask"/>), or null. <see cref="RefusalKind.Position"/> for a position the chart does not hold as active or a
    /// capability its declared seat cannot carry; <see cref="RefusalKind.HostOrLaunch"/> for what is about this host or this
    /// launch (<c>U-thinking-seats-a</c>).
    /// </summary>
    public static TurnRefusal? Typed(string id, PositionStanding? standing) => Ask(id, standing, Seats);

    /// <summary>
    /// THE LAST CHECK, IMMEDIATELY BEFORE THE PROCESS OR THE REQUEST, on a host that may have no meter: <see cref="Decide"/> with the
    /// seat actually about to start. A register that is unset, answered null or threw refuses every id beyond the legacy pair; the
    /// pair themselves are asked the no-unconfined-neighbour rule of the register's own neighbour read (<see cref="Neighbour"/>) —
    /// the same fail-closed answer as the seam's.
    /// </summary>
    public static TurnRefusal? AtLaunch(string role, string verifiedClass, SeatRegister? seats = null) =>
        AtLaunch(role, verifiedClass, seats, TestHost);

    /// <summary><see cref="AtLaunch(string, string, SeatRegister?)"/> as a process that is, or is not, a test host.</summary>
    internal static TurnRefusal? AtLaunch(string role, string verifiedClass, SeatRegister? seats, bool testHost)
    {
        var register = seats ?? Seats;
        if (register.Read(role) is { } facts) return Decide(role, facts.Standing, facts, verifiedClass);
        return CouncilRoles.IsKnown(role) ? Neighbour(role, register, verifiedClass, testHost) : Unset(role);
    }

    /// <summary>
    /// THE NO-UNCONFINED-NEIGHBOUR RULE FOR ONE OF THE LEGACY PAIR WHOSE FACTS COULD NOT BE READ, asked of the register's own
    /// neighbour read: a seat that is not unconfined is no neighbour; on a register the app set, the launch passes only when that
    /// read succeeds and names nobody, and a read that fails refuses it, about the host. A register nothing set refuses it too, about
    /// the host (fix pass 2, item 5): a production path that forgot the register is never passed on a guess. Only a test host
    /// (<see cref="TestHost"/>) keeps the pair's behaviour from before the gate there.
    /// </summary>
    static TurnRefusal? Neighbour(string id, SeatRegister seats, string? seatClass, bool testHost)
    {
        if (!Unconfined(seatClass)) return null;
        if (!seats.IsSet) return testHost ? null : UnsetNeighbours(id);
        if (seats.ReadNeighbours() is not { } active) return Unreadable(id);
        return active.Count > 0 ? Neighboured(id, active) : null;
    }

    /// <summary>Whether a seat of <paramref name="seatClass"/> is unconfined; a class this build does not know is taken to be.</summary>
    static bool Unconfined(string? seatClass) =>
        (SeatClasses.AssuranceOf(seatClass) ?? SeatAssurance.Unconfined) == SeatAssurance.Unconfined;

    static TurnRefusal Neighboured(string id, IReadOnlyList<string> active) => new(RefusalKind.HostOrLaunch,
        $"'{id}' runs unconfined, and {Names(active)} {Be(active)} active beyond the legacy pair on this host: the legacy pair run "
        + "confined too, or not there (ORCHESTRATION § 2.3). Nothing is started. To run it, end or put to sleep the positions named, "
        + "or run the legacy pair in a cell.");

    static TurnRefusal UnsetNeighbours(string id) => new(RefusalKind.HostOrLaunch,
        $"'{id}' runs unconfined, and nothing has told this host whether any position beyond the legacy pair is active (the "
        + "activation gate's register is not set), so nothing is started: an unconfined launch is never passed beside a neighbour "
        + "nobody could rule out. Its wakes are kept.");

    static TurnRefusal Unreadable(string id) => new(RefusalKind.HostOrLaunch,
        $"'{id}' runs unconfined, and TradeAgent could not read whether any position beyond the legacy pair is active on this host, "
        + "so nothing is started: an unconfined launch is never passed beside a neighbour nobody could rule out. Its wakes are kept.");

    /// <summary>
    /// THE CHART COULD NOT BE READ for <paramref name="id"/> — a refusal about the HOST, never about the position (review of
    /// <c>U-thinking-seats-a</c>, item 4): a store or read failure says nothing about the position's standing, so its wakes are kept
    /// under backoff rather than settled as though it were dormant, ended or unknown. Only a chart READ as holding no active
    /// position by that id is <see cref="RefusalKind.Position"/>.
    /// </summary>
    public static TurnRefusal ChartUnreadable(string id, string why) => new(RefusalKind.HostOrLaunch,
        $"TradeAgent could not read the organisation chart for '{id}' ({why}), so nothing is started for it. Its wakes are kept and "
        + "it is tried again.");

    static TurnRefusal Unset(string id) => new(RefusalKind.HostOrLaunch,
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
