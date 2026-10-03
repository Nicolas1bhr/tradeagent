namespace TradeAgent.Core.Db;

/// <summary>
/// THE FIVE IDS THE APP SEEDS, AND THE ONLY FIXED ONES (<c>docs/ORGANISATION.md</c> § 3).
///
/// <para>The schema-28 rung writes the root and the two divisions under these ids, and the two positions under
/// the legacy role strings themselves (<see cref="CouncilRoles.Operations"/>, <see cref="CouncilRoles.Research"/>)
/// — so every <c>role</c> a store wrote before the rung names a position after it, and no historical row changes
/// meaning. Every id after these is minted at random by the app; an id an agent asks for grants nothing.</para>
/// </summary>
public static class OrgIds
{
    /// <summary>
    /// The organisation itself. Its head is NULL, and NULL here means THE OWNER, in-process: no row names them,
    /// so no position can ever become them.
    /// </summary>
    public const string Root = "org";

    /// <summary>The division the legacy <c>operations</c> position heads — the only order permission, as today.</summary>
    public const string Operations = "div-operations";

    /// <summary>The division the legacy <c>research</c> position heads.</summary>
    public const string Research = "div-research";
}

/// <summary>
/// WHAT A UNIT IS. <b>Strings rather than an enum</b>, for the reason <see cref="MissionEventKind"/> gives: the
/// column is read by a person as often as by this code, and a kind from a newer build must read as itself. The
/// store validates against <see cref="All"/> when it writes; SQL holds no list of them.
/// </summary>
public static class OrgUnitKind
{
    /// <summary>The organisation. Exactly one, and it is the only unit with no parent — that much SQL enforces.</summary>
    public const string Root = "root";

    public const string Division = "division";

    /// <summary>An earned division inside a division (<c>docs/ORGANISATION.md</c> § 3).</summary>
    public const string Subdivision = "subdivision";

    /// <summary>One strategy family: one mechanism over a declared universe.</summary>
    public const string Team = "team";

    /// <summary>A head's staff, such as the chief's Risk &amp; Portfolio analyst.</summary>
    public const string Staff = "staff";

    public static readonly string[] All = [Root, Division, Subdivision, Team, Staff];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;
}

/// <summary>
/// A UNIT'S LIFE: chartered → active ⇄ dormant → closed. Closing never deletes a row. Strings, validated by the
/// store, for the reason <see cref="OrgUnitKind"/> gives.
/// </summary>
public static class OrgUnitStatus
{
    public const string Chartered = "chartered";
    public const string Active = "active";
    public const string Dormant = "dormant";
    public const string Closed = "closed";

    public static readonly string[] All = [Chartered, Active, Dormant, Closed];

    public static bool IsKnown(string? status) => status is not null && Array.IndexOf(All, status) >= 0;
}

/// <summary>A POSITION'S LIFE. An ended position keeps its row, its history and its home.</summary>
public static class OrgPositionStatus
{
    public const string Active = "active";
    public const string Dormant = "dormant";
    public const string Ended = "ended";

    public static readonly string[] All = [Active, Dormant, Ended];

    public static bool IsKnown(string? status) => status is not null && Array.IndexOf(All, status) >= 0;
}

/// <summary>
/// WHAT AN <c>org_event</c> RECORDS. One kind so far: the rung's own seed. The acts that restructure the chart —
/// charter, merge, close, hire, retire, a child envelope — arrive with the verbs that perform them.
/// </summary>
public static class OrgEventKind
{
    /// <summary>A row the schema-28 rung wrote: nobody decided it, the upgrade did.</summary>
    public const string Seeded = "seeded";
}

/// <summary>
/// WHO DECIDED AN ACT ON THE CHART. Today only the app: the rung's seed is the one writer there is.
/// </summary>
public static class OrgDecider
{
    public const string App = "app";
}
