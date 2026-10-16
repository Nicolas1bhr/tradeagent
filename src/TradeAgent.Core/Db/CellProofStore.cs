using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TradeAgent.Core.Db;

/// <summary>
/// One proven executor-cell class: the provider whose benchmark passed, where — the platform, this host's machine identity and its
/// operating-system release — on which build, under which profile, and when.
/// </summary>
public sealed record CellProof(string Provider, string Platform, string Machine, string OsRelease, string Build, string ProfileHash,
    DateTimeOffset ProvenAt);

/// <summary>
/// THIS HOST'S IDENTITY, as a cell proof records it and is matched on (review of <c>U-thinking-seats-a</c>, item 3): a stable machine
/// id the app reads itself — <c>/etc/machine-id</c> on Linux, <c>IOPlatformUUID</c> on macOS, <c>MachineGuid</c> on Windows — and the
/// kernel or operating-system release. A home copied to another machine, or a host whose kernel changed, holds no proof here.
///
/// <para><b>The machine id never leaves the owner's database, and not even raw there:</b> <see cref="Machine"/> is a SHA-256 over
/// the id under this purpose's own prefix, so the row matches this host and names nothing else. It is never written to a log line,
/// a report or the repository. <b>An identity that cannot be read is none</b> (<see cref="This"/> null), and a host with none holds
/// no proof — refuse, never guess (<c>CLAUDE.md</c>).</para>
/// </summary>
public sealed record HostIdentity(string Machine, string OsRelease)
{
    static readonly Lazy<HostIdentity?> Here = new(Read);

    /// <summary>This host's identity, read once per process; null where the machine id could not be read.</summary>
    public static HostIdentity? This => Here.Value;

    static HostIdentity? Read()
    {
        try
        {
            var id = MachineId()?.Trim();
            if (string.IsNullOrEmpty(id)) return null;
            return new HostIdentity(Sha256Hex.Of("tradeagent-cell-proof-machine:" + id), Environment.OSVersion.Version.ToString());
        }
        catch (Exception) { return null; }
    }

    static string? MachineId()
    {
        if (OperatingSystem.IsLinux()) return File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id") : null;
        if (OperatingSystem.IsWindows()) return WindowsMachineGuid();
        if (OperatingSystem.IsMacOS()) return MacPlatformUuid();
        return null;
    }

    [SupportedOSPlatform("windows")]
    static string? WindowsMachineGuid()
    {
        using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
            Microsoft.Win32.RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }

    /// <summary>The platform UUID the system reports for the machine, read from <c>ioreg</c> with no window.</summary>
    static string? MacPlatformUuid()
    {
        var psi = new ProcessStartInfo("/usr/sbin/ioreg", ["-rd1", "-c", "IOPlatformExpertDevice"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            return null;
        }
        foreach (var line in output.GetAwaiter().GetResult().Split('\n'))
        {
            if (!line.Contains("\"IOPlatformUUID\"", StringComparison.Ordinal)) continue;
            var parts = line.Split('"');
            return parts.Length >= 4 ? parts[3] : null;
        }
        return null;
    }
}

/// <summary>
/// THE HOST'S PROVEN EXECUTOR-CELL CLASSES, READ — and only read (rung 39, <c>U-thinking-seats-a</c>). A row is written by the app alone,
/// when a cell provider's benchmark passes on this host (<c>U-cell-linux-c</c>/<c>-e</c>); nothing in this build writes one, no verb or
/// pipe op reaches the table, and no setting stands in for a row.
///
/// <para><b>A proof counts on the host, platform, kernel release and build that recorded it, and nowhere else</b> (review item 3).
/// A home carried to another machine — even one of the same platform and build — a kernel that changed under it, or a build that
/// replaced the one the benchmark ran under holds a proof of something that is not what runs now; it is not read as one — refuse,
/// never guess (<c>CLAUDE.md</c>). The benchmark runs again, here. A host whose identity cannot be read holds none.</para>
/// </summary>
/// <param name="host">This host's identity; null is <see cref="HostIdentity.This"/>. Only a test passes another.</param>
public sealed class CellProofStore(Database db, Func<HostIdentity?>? host = null)
{
    /// <summary>This process's platform, as a proof records it: the operating system and the architecture.</summary>
    public static string ThisPlatform =>
        (OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other")
        + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>The proofs that count here: this host's, on this platform and kernel release, under this build, oldest first.</summary>
    public IReadOnlyList<CellProof> Proven()
    {
        if ((host ?? (() => HostIdentity.This))() is not { } here) return [];
        return db.Read(_ =>
        {
            using var c = db.Cmd("""
                SELECT provider, platform, machine, os_release, build, profile_hash, proven_at FROM cell_proof
                 WHERE platform = $platform AND machine = $machine AND os_release = $release AND build = $build
                 ORDER BY proven_at, id
                """, ("$platform", ThisPlatform), ("$machine", here.Machine), ("$release", here.OsRelease), ("$build", Versions.App));
            using var r = c.ExecuteReader();
            var rows = new List<CellProof>();
            while (r.Read())
                rows.Add(new CellProof(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    DateTimeOffset.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind)));
            return (IReadOnlyList<CellProof>)rows;
        });
    }

    /// <summary>Whether this host holds a proof that counts here. A table that cannot be read, or an unreadable identity, holds none.</summary>
    public bool HoldsProof()
    {
        try { return Proven().Count > 0; }
        catch (Exception) { return false; }
    }
}
