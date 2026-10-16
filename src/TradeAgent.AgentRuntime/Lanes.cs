namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT A LAUNCH'S SEAT CAN BE TRUSTED NOT TO REACH, as far as lanes are concerned (<c>docs/ORCHESTRATION.md</c> §§ 2.3, 2.5).
/// <see cref="Unconfined"/> is every seat today and the reading of anything not established: a broker-only (MEDIATED) seat
/// counts as unconfined for lanes until a unit shows it cannot write <c>state/</c> or the inbox (the orchestrator's ruling Q2,
/// 2026-10-16). <see cref="Confined"/> is a seat the activation gate records as confined in a PROVEN cell class on the host.
/// </summary>
public enum LaneAssurance
{
    Unconfined,
    Confined
}

/// <summary>
/// ONE LAUNCH'S LANE PROFILE: its established assurance, the cell class it runs in, and whether its cell profile records the
/// owner's inbox as PROVEN unwritable. Read through <see cref="IMissionHost.LaneOf"/>, whose default is
/// <see cref="Unconfined"/> — the reading until <c>U-thinking-seats-a</c>'s <c>SeatAssurance</c> is wired through that seam
/// (whichever lands second wires it). The only producer of anything else today is a test host's own profile: never settings,
/// never <c>runtimes.json</c>, both files an unconfined neighbour can rewrite.
/// </summary>
public sealed record LaneProfile(LaneAssurance Assurance, string CellClass, bool InboxUnwritable)
{
    /// <summary>Every seat today, and every seat whose assurance nothing has established.</summary>
    public static readonly LaneProfile Unconfined = new(LaneAssurance.Unconfined, "", false);

    public bool IsConfined => Assurance == LaneAssurance.Confined;
}

/// <summary>
/// A QUOTA POOL (<c>docs/ORCHESTRATION.md</c> § 2.5; the ruling Q4): the provider runtime, the account and the plan window a
/// launch's usage is drawn from — shared by every seat on it and by the owner's own use outside the app. Derived in code from
/// what the launch already carries (its runtime; the seat home's account, unknown until a seat home names one; the plan window,
/// vendor data). A part that is not known is null, and an unknown part COLLAPSES: it shares with every value of that part, so
/// two launches are counted apart only where every part of theirs is known and differs. Fail toward fewer lanes.
/// </summary>
public readonly record struct QuotaPool(string? Runtime, string? Account, string? Window)
{
    /// <summary>The pool of a launch nothing could be learned about: it shares with every pool there is.</summary>
    public static readonly QuotaPool Unknown = new(null, null, null);

    /// <summary>The pool from its parts, blank read as unknown.</summary>
    public static QuotaPool Of(string? runtime, string? account, string? window) =>
        new(Known(runtime), Known(account), Known(window));

    static string? Known(string? part) => string.IsNullOrWhiteSpace(part) ? null : part.Trim();

    /// <summary>
    /// WHETHER TWO LAUNCHES DRAW ON ONE POOL as far as anyone can tell: every part equal, or unknown on either side. A vendor's
    /// limit on one holds the other, and the two count against one pool's limit.
    /// </summary>
    public bool SharesWith(QuotaPool other) =>
        Same(Runtime, other.Runtime) && Same(Account, other.Account) && Same(Window, other.Window);

    static bool Same(string? a, string? b) => a is null || b is null || string.Equals(a, b, StringComparison.Ordinal);

    public override string ToString() => $"{Runtime ?? "?"}/{Account ?? "?"}/{Window ?? "?"}";
}

/// <summary>
/// THE OWNER'S LANE POLICY, as the loop reads it each tick: how many positions may turn at once, and how many on one quota pool
/// and in one cell class. Settings, edited on the Safety page only (raising is two-press) — no pipe op, verb or packet field
/// names any of them. A setting may LOWER concurrency and never raise it past <see cref="LanePolicy.UnconfinedCeiling"/> for
/// unconfined lanes: the settings file lives under <c>state/</c>, which an unconfined neighbour can rewrite (§ 2.3).
/// </summary>
public sealed record LaneSettings(int Lanes, int PerPool, int PerCellClass)
{
    /// <summary>One lane, one per pool, one per cell class: N = 1 until grants are bound and cells exist (R1).</summary>
    public static readonly LaneSettings Default = new(1, 1, 1);
}

/// <summary>One launch a lane holds, or one the dispatcher is about to start: whose, on which pool, in which seat.</summary>
public readonly record struct LaneLaunch(string Position, QuotaPool Pool, LaneProfile Profile);

/// <summary>
/// THE LANE RULE, in code (<c>docs/ORCHESTRATION.md</c> § 2.5; the orchestrator's ruling Q2, 2026-10-16).
/// </summary>
public static class LanePolicy
{
    /// <summary>
    /// AT MOST ONE UNCONFINED MODEL ATTEMPT IN THE LOOP AT A TIME — a CONSTANT, never read from any setting. Settings live under
    /// <c>state/</c>, which an unconfined neighbour's shell can rewrite, so a lane count read from them alone would let an
    /// unconfined agent raise its own concurrency. Lanes beyond this one are for seats recorded as confined in a proven cell
    /// class. The owner's own lane (his chat) is outside the loop and outside this count.
    /// </summary>
    public const int UnconfinedCeiling = 1;

    /// <summary>The most lanes any setting can ask for.</summary>
    public const int MaxLanes = 8;

    /// <summary>A setting's value as a count: at least one, at most <see cref="MaxLanes"/>.</summary>
    public static int Clamp(int value) => Math.Clamp(value, 1, MaxLanes);

    /// <summary>
    /// WHY <paramref name="candidate"/> MAY NOT START BESIDE <paramref name="running"/>, in words, or null when it may — the lane
    /// count, the unconfined ceiling, its quota pool's limit and its cell class's limit, the least of them binding
    /// (<c>Effective = min(setting, pool limit, cell-class limit)</c>, with the unconfined ceiling in code over all of them).
    /// </summary>
    public static string? Refuses(LaneSettings settings, LaneLaunch candidate, IReadOnlyCollection<LaneLaunch> running)
    {
        var lanes = Clamp(settings.Lanes);
        if (running.Count >= lanes)
            return $"every one of the {lanes} lane{(lanes == 1 ? "" : "s")} the AI may use at once is turning";

        if (!candidate.Profile.IsConfined && running.Count(r => !r.Profile.IsConfined) >= UnconfinedCeiling)
            return "another position is turning on a seat nothing confines, and only one such turn runs at a time";

        var perPool = Clamp(settings.PerPool);
        if (running.Count(r => r.Pool.SharesWith(candidate.Pool)) >= perPool)
            return $"its AI account's quota pool ({candidate.Pool}) already has {perPool} turn{(perPool == 1 ? "" : "s")} running";

        if (candidate.Profile.IsConfined)
        {
            var perClass = Clamp(settings.PerCellClass);
            if (running.Count(r => r.Profile.IsConfined && string.Equals(r.Profile.CellClass, candidate.Profile.CellClass,
                    StringComparison.Ordinal)) >= perClass)
                return $"its cell class '{candidate.Profile.CellClass}' already has {perClass} turn{(perClass == 1 ? "" : "s")} running";
        }

        return null;
    }
}
