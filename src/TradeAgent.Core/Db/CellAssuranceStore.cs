using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>An enforcement class (ORCHESTRATION § 5; VISION § 6.3), weakest last in strength: GAP is an open door asserted, never hidden.</summary>
public enum EnforcementClass
{
    Gap = 0,
    Advisory = 1,
    Mediated = 2,
    HardApp = 3,
    HardPlatform = 4
}

/// <summary>Which side of a launch a property's class belongs to: the cell, the harness that holds the login, or the attempt (the weaker of the two).</summary>
public enum AssuranceSide { Cell, Harness, Attempt }

/// <summary>One property of one side of a cell launch, its class and the evidence it rests on.</summary>
public sealed record AssuranceRow(string Property, AssuranceSide Side, EnforcementClass Class, string Evidence);

/// <summary>What a cell launch ran under, as written once at its start.</summary>
public sealed record CellAssurance(string AttemptId, string Provider, string PolicyHash, string ProfileHash, long ProofId, string Kernel,
    int LandlockAbi, string Lsm, string Egress, string Cgroup, string Harness, IReadOnlyList<AssuranceRow> Rows)
{
    /// <summary>The attempt's own class: the weakest attempt-side row (the weaker side of each property).</summary>
    public EnforcementClass AttemptClass =>
        Rows.Where(r => r.Side == AssuranceSide.Attempt).Select(r => r.Class).DefaultIfEmpty(EnforcementClass.Gap).Min();
}

/// <summary>
/// THE ASSURANCE PROFILE ON THE ATTEMPT (<c>U-cell-linux-c</c> item 6; ORCHESTRATION § 2.8 Acceptance; the survey's Q-b): per property
/// and side, the enforcement class, with the attempt side the WEAKER of the cell and the harness. Written once by the app at a cell
/// launch — the rung's triggers refuse any edit — and read by what decides lanes and presence (§ 2.5), which in route (a) stay where
/// they are: the harness is an unconfined codex holding the login, so the attempt's <c>state</c> and <c>inbox</c> are MEDIATED.
/// </summary>
public sealed class CellAssuranceStore(Database db)
{
    /// <summary>The database's word for a class.</summary>
    public static string Word(EnforcementClass c) => c switch
    {
        EnforcementClass.HardPlatform => "HARD_PLATFORM",
        EnforcementClass.HardApp => "HARD_APP",
        EnforcementClass.Mediated => "MEDIATED",
        EnforcementClass.Advisory => "ADVISORY",
        _ => "GAP"
    };

    static EnforcementClass Parse(string word) => word switch
    {
        "HARD_PLATFORM" => EnforcementClass.HardPlatform,
        "HARD_APP" => EnforcementClass.HardApp,
        "MEDIATED" => EnforcementClass.Mediated,
        "ADVISORY" => EnforcementClass.Advisory,
        _ => EnforcementClass.Gap
    };

    static string Word(AssuranceSide s) => s switch { AssuranceSide.Cell => "cell", AssuranceSide.Harness => "harness", _ => "attempt" };

    /// <summary>
    /// The rows of a launch with each property's ATTEMPT side added — the weaker of its cell and harness sides; a property with only
    /// one side named is that side's. Rows already naming the attempt side are kept as given only when no side pair exists.
    /// </summary>
    public static IReadOnlyList<AssuranceRow> WithAttemptSide(IEnumerable<AssuranceRow> sides)
    {
        var list = sides.Where(r => r.Side != AssuranceSide.Attempt).ToList();
        var attempt = list.GroupBy(r => r.Property, StringComparer.Ordinal).Select(g =>
        {
            var weakest = g.OrderBy(r => r.Class).First();
            return new AssuranceRow(g.Key, AssuranceSide.Attempt, weakest.Class, $"the weaker side: {Word(weakest.Side)} ({weakest.Evidence})");
        });
        return [.. list, .. attempt];
    }

    /// <summary>Writes one launch's assurance, once, in one transaction. A second write for the same attempt is refused by its key.</summary>
    public void Record(CellAssurance a)
    {
        var at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        db.Write(_ =>
        {
            using (var c = db.Cmd("""
                INSERT INTO cell_assurance(attempt_id, provider, policy_hash, profile_hash, proof_id, kernel, landlock_abi, lsm, egress, cgroup,
                                           harness, attempt_class, recorded_at)
                VALUES($a, $provider, $policy, $profile, $proof, $kernel, $abi, $lsm, $egress, $cgroup, $harness, $class, $at)
                """, ("$a", a.AttemptId), ("$provider", a.Provider), ("$policy", a.PolicyHash), ("$profile", a.ProfileHash), ("$proof", a.ProofId),
                ("$kernel", a.Kernel), ("$abi", a.LandlockAbi), ("$lsm", a.Lsm), ("$egress", a.Egress), ("$cgroup", a.Cgroup), ("$harness", a.Harness),
                ("$class", Word(a.AttemptClass)), ("$at", at)))
                c.ExecuteNonQuery();
            foreach (var r in a.Rows)
            {
                using var c = db.Cmd("""
                    INSERT INTO cell_assurance_row(attempt_id, property, side, class, evidence) VALUES($a, $p, $s, $c, $e)
                    """, ("$a", a.AttemptId), ("$p", r.Property), ("$s", Word(r.Side)), ("$c", Word(r.Class)), ("$e", r.Evidence));
                c.ExecuteNonQuery();
            }
            return 0;
        });
    }

    /// <summary>
    /// The attempt-side class of <paramref name="property"/> for <paramref name="attemptId"/>, or null — no row, or an unreadable one,
    /// which every reader takes as the weakest (presence DEFAULT IN, fail-closed).
    /// </summary>
    public EnforcementClass? AttemptClassOf(string attemptId, string property)
    {
        try
        {
            return db.Read(_ =>
            {
                using var c = db.Cmd("SELECT class FROM cell_assurance_row WHERE attempt_id = $a AND property = $p AND side = 'attempt'",
                    ("$a", attemptId), ("$p", property));
                return c.ExecuteScalar() is string w ? Parse(w) : (EnforcementClass?)null;
            });
        }
        catch (Exception) { return null; }
    }
}
