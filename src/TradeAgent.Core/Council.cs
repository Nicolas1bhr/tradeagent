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

    /// <summary>
    /// WHETHER A ROLE MAY MOVE MONEY, and the one place that question is answered.
    ///
    /// Only the chair. Research proposes, designs experiments and reads; <c>docs/COUNCIL.md</c> gives
    /// it no order permission, and the Strategist teams under it none either. A caller that proved no
    /// role at all is not the chair by default — see <c>AgentContext.Role</c> — so an unknown or
    /// absent role answers false here rather than falling through <see cref="Or"/> to Operations.
    /// </summary>
    public static bool MayPlaceOrders(string? role) => role == Operations;

    /// <summary>The role's name in the owner's words, for a card and for a mission file.</summary>
    public static string Title(string role) => role switch
    {
        Operations => "Operations Director",
        Research => "Research Director",
        _ => role
    };

    /// <summary>
    /// A POSITION'S FOLDER NAME under <c>workspace/</c>, computed from its id and never read back from a row
    /// (<c>docs/ORCHESTRATION.md</c> § 2.1).
    ///
    /// <para>Operations keeps <c>agent</c>: it is where the existing install's plan, journal, strategies and data
    /// already are. Research keeps <c>research</c>. Every other position lives under <c>org/&lt;id&gt;</c> — its own
    /// folder, so its canon never overwrites the chair's and its CLI never resumes the chair's session (a vendor's
    /// <c>resume --last</c> is scoped by the working directory).</para>
    ///
    /// <para><b>An id that fails <see cref="IsPositionId"/> is refused, never mapped</b> (<c>U-org-principals</c>):
    /// before this, any string that was not Research's went to the chair's folder.</para>
    /// </summary>
    /// <exception cref="TradeAgentException">INVALID_REQUEST for an id that is not a position id.</exception>
    public static string HomeDir(string role) => role switch
    {
        Operations => "agent",
        Research => Research,
        _ when IsPositionId(role) => $"{OrgDir}/{role}",
        _ => throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
            $"'{role}' is not a position id, so it has no home: a position id is lower-case letters, digits and "
            + "hyphens, never a device name Windows reserves and never one of the app's own principals. Nothing was "
            + "mapped to another position's folder.")
    };

    /// <summary>The folder every position after the legacy pair lives under: <c>workspace/org/&lt;id&gt;</c>.</summary>
    public const string OrgDir = "org";

    /// <summary>
    /// WHETHER AN ID CAN NAME A POSITION'S HOME: <c>[a-z0-9-]+</c>, not a device name Windows reserves (a folder
    /// called <c>con</c> or <c>com1</c> cannot be created there), and not an app principal, which is code and never
    /// a position. Whether a position by that id EXISTS, and is active, is the chart's question, not this one.
    /// </summary>
    public static bool IsPositionId(string? id) =>
        id is { Length: > 0 }
        && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && Array.IndexOf(ReservedDeviceNames, id) < 0
        && !Db.OrgStore.IsAppPrincipal(id);

    /// <summary>The names Windows reserves for devices, in the only case <see cref="IsPositionId"/> admits.</summary>
    static readonly string[] ReservedDeviceNames =
    [
        "con", "prn", "aux", "nul",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    ];

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
