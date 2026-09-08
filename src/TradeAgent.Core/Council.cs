namespace TradeAgent.Core;

/// <summary>
/// THE ROLES, WHICH SURVIVE THE AGENTS THAT FILL THEM (<c>docs/COUNCIL.md</c>, "The shape").
///
/// <para>A role is a workspace folder, an app-managed mission file, a model and a slice of the day's
/// allowance. An agent is one bounded attempt at one task for one role. Everything before this unit
/// assumed the two were the same thing: one <c>AgentHome</c>, one <c>AGENTS.md</c>, one cached
/// conversation, one model, one cap.</para>
///
/// <para><b>Strings rather than an enum</b>, for the reason <see cref="Db.MissionEventKind"/> gives:
/// these are written into database columns and read by a person as often as by this code, and a role
/// arriving from a newer build must read as itself rather than as whichever enum member happens to be
/// zero. <see cref="IsKnown"/> is how this build says it does not know one.</para>
///
/// <para><b>Rank is organisation; access is grants.</b> Nothing here confers authority. Round 4 of
/// the doctrine settled that directory depth grants nothing — the app grants named artifact revisions
/// to authenticated roles, and a role folder is a VIEW of those grants. What separates the two roles
/// below today is a convention stated in each one's mission file and enforced by nothing, which is
/// said out loud in <c>WorkspaceBuilder</c> rather than implied.</para>
/// </summary>
public static class CouncilRoles
{
    /// <summary>
    /// The chair. Scheduling, the allocation of the budget across roles, execution readiness, and
    /// the owner's words — which enter ITS agenda first, with a receipt and a deadline.
    ///
    /// Its home is the workspace the single agent already had, so <c>trading/PLAN.md</c> and
    /// <c>trading/JOURNAL.md</c> — the only two files that cross a fresh session — keep their path
    /// and their history.
    /// </summary>
    public const string Operations = "operations";

    /// <summary>Hypotheses, experimental design, data and backtests. It publishes reports upward.</summary>
    public const string Research = "research";

    /// <summary>
    /// The roles this build runs, in the order the scheduler prefers on a tie. Operations first
    /// because it is the chair and because the owner's words are on its agenda.
    /// </summary>
    public static readonly string[] All = [Operations, Research];

    /// <summary>What a row with no role means: every event written before this unit is Operations'.</summary>
    public const string Default = Operations;

    public static bool IsKnown(string? role) => role is not null && Array.IndexOf(All, role) >= 0;

    /// <summary>
    /// The role of a row that names none. NULL rather than an empty string is what an additive
    /// migration leaves behind, and reading it as Operations is the only answer that does not
    /// silently reassign work the chair already did.
    /// </summary>
    public static string Or(string? role) => IsKnown(role) ? role! : Default;

    /// <summary>The role's name in the owner's words, for a card and for a mission file.</summary>
    public static string Title(string role) => role switch
    {
        Operations => "Operations Director",
        Research => "Research Director",
        _ => role
    };

    /// <summary>
    /// The role's folder name under <c>workspace/</c>.
    ///
    /// Operations keeps <c>agent</c>: it is where the existing install's plan, journal, strategies
    /// and data already are, and a rename would either strand them or require a move that could
    /// silently choose between two versions of a file.
    /// </summary>
    public static string HomeDir(string role) => role == Research ? Research : "agent";

    /// <summary>
    /// WHAT THE APP DELIVERS INTO, inside each role's home. Here rather than on
    /// <c>WorkspaceBuilder</c> — which is where the agent-facing copy of it lives — because
    /// <see cref="MaterialScanner"/> is in this assembly and has to walk it: what a role was HANDED
    /// is half of "what did this role read and write", and a ledger that recorded only the half the
    /// agent typed would be a record of the conversation with one side missing.
    /// </summary>
    public const string InDir = "in";

    /// <summary>What the role hands back, and the only path out of its folder. See <see cref="InDir"/>.</summary>
    public const string OutDir = "out";
}
