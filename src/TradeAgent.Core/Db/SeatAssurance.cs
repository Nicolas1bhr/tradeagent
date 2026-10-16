namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT A SEAT'S BOUNDARY IS, weakest first (<c>docs/ORCHESTRATION.md</c> § 2.3, § 5; <c>U-thinking-seats-a</c>). The order is the
/// comparison: a capability whose minimum is <see cref="Mediated"/> is carried by a mediated, a brokered or a confined seat.
///
/// <para><b>Code, never data.</b> A seat's assurance is answered by <see cref="SeatClasses.AssuranceOf"/> from its class, and a class is
/// what the APP established for the launch — never a field of <c>runtimes.json</c>, which replaces a manifest whole and which any
/// same-user process can rewrite (ORCHESTRATION § 2.7, "Seat classes live in code").</para>
/// </summary>
public enum SeatAssurance
{
    /// <summary>A CLI seat started as the owner's user with a shell: it can run any code that user can.</summary>
    Unconfined = 0,

    /// <summary>A broker-only CLI seat: no model-reachable tool crosses the boundary, but the process itself could.</summary>
    Mediated = 1,

    /// <summary>The app-owned API harness: no process, every tool the app's own code (in-process).</summary>
    Brokered = 2,

    /// <summary>A seat started in a cell whose confinement a provider's benchmark proved on this host.</summary>
    Confined = 3
}

/// <summary>What a launch may do, as the activation gate weighs it (ORCHESTRATION § 2.3). Each has a minimum assurance.</summary>
public enum Capability
{
    /// <summary>Reading what the app hands a turn and what its own home holds.</summary>
    Reads,

    /// <summary>Asking the app for evidence: a backtest, a verdict, a lens, a ledger entry (<c>U-org-rights</c>).</summary>
    EvidenceRequests,

    /// <summary>The organisation's decision verbs (<c>U-org-verbs</c>; none exists yet).</summary>
    DecisionVerbs,

    /// <summary>Running code: what any seat with a shell has, whatever its position asked for.</summary>
    Code,

    /// <summary>Starting helpers under the launch's own mandate; they inherit their parent's minimum.</summary>
    Helpers,

    /// <summary>Placing orders: <c>operations</c> only (<see cref="CouncilRoles.MayPlaceOrders"/>, ORDER-001), never by headship.</summary>
    Orders
}

/// <summary>
/// THE SEAT CLASSES THIS BUILD KNOWS, as CODE (ORCHESTRATION § 2.7): what each one's assurance is, whether it runs a shell and
/// whether it runs a model. A class this build does not know is answered with no assurance, which every caller refuses — refuse,
/// never guess (<c>CLAUDE.md</c>).
///
/// <para><b>A later class slots in as a row of the three answers below.</b> A <c>puppet</c> seat (<c>U-puppet-runtime</c>) runs no
/// model and no shell: it is exempt from the proven-cell refusal (<see cref="RunsModel"/> false) and is no unconfined neighbour.
/// Whichever of the two units lands second adds it here.</para>
/// </summary>
public static class SeatClasses
{
    /// <summary>A vendor CLI started with a shell, today every codex, OpenCode and custom seat — with a seat plan or without one.</summary>
    public const string Cli = "cli";

    /// <summary>A broker-only CLI seat: the app's verbs through its own door, no shell (<c>U-thinking-seats</c> part c).</summary>
    public const string Broker = "broker";

    /// <summary>The app-owned API harness (<c>ApiConversation</c>): no process, the app's own tools.</summary>
    public const string Harness = "harness";

    /// <summary>A seat started in a confined cell (<c>U-cell-linux-b..d</c>; no seat starts in one yet).</summary>
    public const string Cell = "cell";

    public static readonly string[] All = [Cli, Broker, Harness, Cell];

    public static bool IsKnown(string? seatClass) => seatClass is not null && Array.IndexOf(All, seatClass) >= 0;

    /// <summary>The class's assurance, or null for a class this build does not know.</summary>
    public static SeatAssurance? AssuranceOf(string? seatClass) => seatClass switch
    {
        Cli => SeatAssurance.Unconfined,
        Broker => SeatAssurance.Mediated,
        Harness => SeatAssurance.Brokered,
        Cell => SeatAssurance.Confined,
        _ => null
    };

    /// <summary>Whether a seat of the class has a shell, and so the <see cref="Capability.Code"/> capability whatever it asked for.</summary>
    public static bool RunsShell(string seatClass) => seatClass is Cli or Cell;

    /// <summary>Whether a seat of the class asks a real model. Every class this build knows does; an unknown one is taken to.</summary>
    public static bool RunsModel(string seatClass) => seatClass is Cli or Broker or Harness or Cell || !IsKnown(seatClass);

    /// <summary>The word a ledger row records for an assurance.</summary>
    public static string Name(SeatAssurance assurance) => assurance switch
    {
        SeatAssurance.Unconfined => "unconfined",
        SeatAssurance.Mediated => "mediated",
        SeatAssurance.Brokered => "brokered",
        SeatAssurance.Confined => "confined",
        _ => throw new ArgumentOutOfRangeException(nameof(assurance))
    };
}

/// <summary>
/// THE CAPABILITY POLICY, AS CODE — the owner's 2026-10-11 answer, "nothing runs until executors are confined", with its
/// decision-verb minimum decided on his behalf (ORCHESTRATION § 2.3). Read by the activation gate when a position opens and again,
/// fail-closed, at every launch; never read from a file.
/// </summary>
public static class CapabilityPolicy
{
    /// <summary>
    /// THE WEAKEST SEAT THAT MAY CARRY <paramref name="capability"/>: reads, evidence requests and decision verbs need at least a
    /// mediated (broker-only) seat; code needs a confined cell; helpers inherit <paramref name="parent"/>'s minimum, and a helper
    /// whose parent is not known needs a cell. Orders need a cell beyond the legacy pair's carve-out — and WHO may hold them at
    /// all is <see cref="CouncilRoles.MayPlaceOrders"/>'s answer alone, untouched here (ORDER-001).
    /// </summary>
    public static SeatAssurance Minimum(Capability capability, SeatAssurance? parent = null) => capability switch
    {
        Capability.Reads or Capability.EvidenceRequests or Capability.DecisionVerbs => SeatAssurance.Mediated,
        Capability.Code => SeatAssurance.Confined,
        Capability.Helpers => parent ?? SeatAssurance.Confined,
        Capability.Orders => SeatAssurance.Confined,
        _ => SeatAssurance.Confined
    };

    /// <summary>
    /// WHAT A LAUNCH OF <paramref name="standing"/> ON A <paramref name="seatClass"/> SEAT CAN DO: the position's own capabilities —
    /// today reads and evidence requests (<c>U-org-rights</c>), and orders for <c>operations</c> alone — plus the seat's intrinsic
    /// ones: a seat with a shell has <see cref="Capability.Code"/> whatever the position asked for.
    /// </summary>
    public static IReadOnlyList<Capability> CapabilitiesOf(PositionStanding standing, string seatClass)
    {
        var all = new List<Capability> { Capability.Reads, Capability.EvidenceRequests };
        if (CouncilRoles.MayPlaceOrders(standing.Id)) all.Add(Capability.Orders);
        if (SeatClasses.RunsShell(seatClass)) all.Add(Capability.Code);
        return all;
    }

    /// <summary>
    /// THE CAPABILITY THAT BINDS: of <paramref name="capabilities"/>, the one a seat of <paramref name="assurance"/> cannot carry whose
    /// minimum is the strongest — so a CLI seat is told that its code needs a cell, not merely that its reads need a mediated seat —
    /// or null when the seat carries them all. Ties go to the earlier capability.
    /// </summary>
    public static Capability? Binding(IEnumerable<Capability> capabilities, SeatAssurance assurance)
    {
        Capability? binding = null;
        foreach (var c in capabilities)
            if (assurance < Minimum(c) && (binding is not { } b || Minimum(c) > Minimum(b))) binding = c;
        return binding;
    }
}
