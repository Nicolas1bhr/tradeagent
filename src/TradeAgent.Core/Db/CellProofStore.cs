using System.Runtime.InteropServices;

namespace TradeAgent.Core.Db;

/// <summary>One proven executor-cell class: the provider whose benchmark passed, where, on which build, under which profile, and when.</summary>
public sealed record CellProof(string Provider, string Platform, string Build, string ProfileHash, DateTimeOffset ProvenAt);

/// <summary>
/// THE HOST'S PROVEN EXECUTOR-CELL CLASSES, READ — and only read (rung 39, <c>U-thinking-seats-a</c>). A row is written by the app alone,
/// when a cell provider's benchmark passes on this host (<c>U-cell-linux-c</c>/<c>-e</c>); nothing in this build writes one, no verb or
/// pipe op reaches the table, and no setting stands in for a row.
///
/// <para><b>A proof counts on the platform and build that recorded it, and nowhere else.</b> A home carried to another machine, or a
/// build that replaced the one the benchmark ran under, holds a proof of something that is not what runs now; it is not read as one —
/// refuse, never guess (<c>CLAUDE.md</c>). The benchmark runs again.</para>
/// </summary>
public sealed class CellProofStore(Database db)
{
    /// <summary>This process's platform, as a proof records it: the operating system and the architecture.</summary>
    public static string ThisPlatform =>
        (OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other")
        + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>The proofs that count here: this platform's, under this build, oldest first.</summary>
    public IReadOnlyList<CellProof> Proven() => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT provider, platform, build, profile_hash, proven_at FROM cell_proof
             WHERE platform = $platform AND build = $build
             ORDER BY proven_at, id
            """, ("$platform", ThisPlatform), ("$build", Versions.App));
        using var r = c.ExecuteReader();
        var rows = new List<CellProof>();
        while (r.Read())
            rows.Add(new CellProof(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)));
        return (IReadOnlyList<CellProof>)rows;
    });

    /// <summary>Whether this host holds a proof that counts here. A table that cannot be read holds none.</summary>
    public bool HoldsProof()
    {
        try { return Proven().Count > 0; }
        catch (Exception) { return false; }
    }
}
