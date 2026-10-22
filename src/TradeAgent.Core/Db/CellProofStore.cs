using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TradeAgent.Core.Db;

/// <summary>
/// One proven executor-cell class: the provider whose benchmark passed, where — the platform, this host's machine identity and its
/// operating-system release — on which build, under which profile, and when.
/// </summary>
public sealed record CellProof(string Provider, string Platform, string Machine, string OsRelease, string Build, string ProfileHash,
    DateTimeOffset ProvenAt)
{
    /// <summary>The row's id: what a launch's assurance and a revocation name.</summary>
    public long Id { get; init; }

    /// <summary>
    /// THE EGRESS CLASSES THE PROOF'S ROWS MEASURED (fix pass 1, R4; L4): a launch whose cell names a class not here is refused. Empty
    /// on a row that names none, which admits no class.
    /// </summary>
    public IReadOnlyList<string> Egress { get; init; } = [];
}

/// <summary>
/// ONE ROW OF THE BENCHMARK THAT WROTE A PROOF, as the proof keeps it (<c>cell_proof_row</c>): its name, whether it is a GAP — a reach
/// measured and recorded as such, never a pass — and what it saw.
/// </summary>
public sealed record ProofRow(string Name, bool Gap, string Seen);

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
    /// <summary>
    /// A SHA-256 over this boot's id (<c>U-cell-linux-c</c> item 5) — <c>/proc/sys/kernel/random/boot_id</c> on Linux, never the id
    /// itself — so a proof counts on the boot that measured it alone. Off Linux no cell runs and there is no binding: a fixed
    /// word, said as such.
    /// </summary>
    public string Boot { get; init; } = NoBoot;

    /// <summary>The boot word off Linux, where no cell runs and a proof binds no boot.</summary>
    public const string NoBoot = "no-boot-binding-off-linux";

    static readonly Lazy<HostIdentity?> Here = new(Read);

    /// <summary>This host's identity, read once per process; null where the machine id could not be read.</summary>
    public static HostIdentity? This => Here.Value;

    static HostIdentity? Read()
    {
        try
        {
            var id = MachineId()?.Trim();
            if (string.IsNullOrEmpty(id)) return null;
            // THE FULL KERNEL RELEASE (item 5): /proc/sys/kernel/osrelease as the kernel says it, flavour suffix and all — two kernels
            // of one numeric version are two kernels. Off Linux, the operating system's own full description.
            var release = OperatingSystem.IsLinux() ? File.ReadAllText("/proc/sys/kernel/osrelease").Trim() : RuntimeInformation.OSDescription;
            if (release.Length == 0) return null;
            var boot = NoBoot;
            if (OperatingSystem.IsLinux())
            {
                var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                if (bootId.Length == 0) return null;
                boot = Sha256Hex.Of("tradeagent-cell-proof-boot:" + bootId);
            }
            return new HostIdentity(Sha256Hex.Of("tradeagent-cell-proof-machine:" + id), release) { Boot = boot };
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
public sealed class CellProofStore(Database db, Func<HostIdentity?>? host = null, Func<string?>? build = null)
{
    public static string ThisPlatform =>
        (OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other")
        + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>
    /// THE BUILD A PROOF IS BOUND TO (<c>U-cell-linux-c</c> item 5): <see cref="Versions.App"/> AND the SHA-256 of the <c>trade</c> this
    /// install deployed (<c>ToolDeployer.DeployedHashFile</c>, <c>state/trade.sha256</c>) — two builds of one version are two builds, and
    /// the launcher the cells run through is the one the proof measured. <b>No deployed hash: NULL, and no proof</b> (fix pass 1, R3) —
    /// never a word like <c>none</c>, which every install without a hash would share, so a proof would count for builds it never
    /// measured: refuse, never guess.
    /// </summary>
    public static string? ThisBuild()
    {
        if (TestHostBuild is { } test) return test;
        string? recorded = null;
        try { recorded = File.ReadAllText(Path.Combine(Paths.State, "trade.sha256")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* none deployed */ }
        return BuildOf(recorded);
    }

    /// <summary>The build a deployed hash file's text names: the version and the hash, or NULL unless it is one SHA-256 in hex.</summary>
    public static string? BuildOf(string? deployedHash)
    {
        var hash = deployedHash?.Trim().ToLowerInvariant();
        return hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit) ? $"{Versions.App}+trade-sha256:{hash}" : null;
    }

    /// <summary>
    /// A TEST HOST'S BUILD, set only by the test assembly (<c>InternalsVisibleTo</c>; the <c>PuppetSeats.TestHost</c> pattern) so its
    /// proofs do not follow the rows that rewrite <c>state/trade.sha256</c> mid-run. Nothing in the product sets it, and no file,
    /// setting or pipe op reaches it.
    /// </summary>
    internal static string? TestHostBuild { get; set; }

    /// <summary>Why a store with no build holds and writes no proof, in words.</summary>
    const string NoBuild = "TradeAgent has no record of the trade command it installed (state/trade.sha256), so no cell proof can be bound to " +
                           "this build: a proof bound to no build would count for builds it never measured. The next start records it and measures again";

    HostIdentity? Here => (host ?? (() => HostIdentity.This))();

    string? Build => (build ?? ThisBuild)();

    /// <summary>
    /// THE PROOFS THAT COUNT HERE AND NOW: this platform, machine, full kernel release, BOOT and build, and not revoked. Empty when this
    /// host's identity cannot be read, or this build's deployed hash is not recorded.
    /// </summary>
    public IReadOnlyList<CellProof> Proven()
    {
        if (Here is not { } here) return [];
        if (Build is not { } built) return [];
        return db.Read(_ =>
        {
            using var c = db.Cmd("""
                SELECT id, provider, platform, machine, os_release, build, profile_hash, proven_at, egress FROM cell_proof
                 WHERE platform = $platform AND machine = $machine AND os_release = $release AND build = $build AND boot = $boot
                   AND id NOT IN (SELECT proof_id FROM cell_proof_revocation)
                 ORDER BY proven_at, id
                """, ("$platform", ThisPlatform), ("$machine", here.Machine), ("$release", here.OsRelease), ("$build", built),
                ("$boot", here.Boot));
            using var r = c.ExecuteReader();
            var rows = new List<CellProof>();
            while (r.Read())
                rows.Add(new CellProof(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                    DateTimeOffset.Parse(r.GetString(7), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind))
                {
                    Id = r.GetInt64(0),
                    Egress = r.IsDBNull(8) ? [] : r.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries)
                });
            return (IReadOnlyList<CellProof>)rows;
        });
    }

    /// <summary>Whether ANY provider holds a counted proof here (the activation gate's one host-wide fact). Unreadable is no.</summary>
    public bool HoldsProof()
    {
        try { return Proven().Count > 0; }
        catch (Exception) { return false; }
    }

    /// <summary>The newest counted proof of <paramref name="provider"/>, or null — the one a cell launch of that provider runs under.</summary>
    public CellProof? ProofOf(string provider)
    {
        try { return Proven().LastOrDefault(p => p.Provider == provider); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// WRITES ONE PROOF — called only by the host benchmark when EVERY row held (<c>CellBenchmark</c>): bound to this host's platform,
    /// machine, full release and boot and this build, with the profile it measured, the egress classes its rows proved and every row
    /// it ran (a GAP kept as one), in the one transaction a write is. Returns its id; refuses a host whose identity cannot be read, a build
    /// whose deployed hash is not recorded, and a proof that names no egress class.
    /// </summary>
    public long Record(string provider, string profileHash, IReadOnlyList<string> egress, IReadOnlyList<ProofRow>? rows = null)
    {
        if (Here is not { } here)
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, "this host's identity could not be read, so no cell proof can be bound to it");
        if (egress.Count == 0 || egress.Any(e => e.Length == 0 || e.Contains(',', StringComparison.Ordinal)))
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, "a cell proof names the egress classes its rows measured, and this one named none");
        if (Build is not { } built) throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, NoBuild);
        return db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO cell_proof(provider, platform, machine, os_release, build, profile_hash, proven_at, boot, egress)
                VALUES($provider, $platform, $machine, $release, $build, $profile, $at, $boot, $egress);
                SELECT last_insert_rowid();
                """, ("$provider", provider), ("$platform", ThisPlatform), ("$machine", here.Machine), ("$release", here.OsRelease),
                ("$build", built), ("$profile", profileHash), ("$at", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                ("$boot", here.Boot), ("$egress", string.Join(',', egress)));
            var id = (long)c.ExecuteScalar()!;
            foreach (var row in rows ?? [])
            {
                using var r = db.Cmd("INSERT INTO cell_proof_row(proof_id, name, kind, seen) VALUES($id, $name, $kind, $seen)",
                    ("$id", id), ("$name", row.Name), ("$kind", row.Gap ? "GAP" : "PASS"), ("$seen", row.Seen));
                r.ExecuteNonQuery();
            }
            return id;
        });
    }

    /// <summary>The rows of proof <paramref name="proofId"/>, in the order the benchmark ran them.</summary>
    public IReadOnlyList<ProofRow> RowsOf(long proofId) =>
        db.Read(_ =>
        {
            using var c = db.Cmd("SELECT name, kind, seen FROM cell_proof_row WHERE proof_id = $id ORDER BY id", ("$id", proofId));
            using var r = c.ExecuteReader();
            var rows = new List<ProofRow>();
            while (r.Read()) rows.Add(new ProofRow(r.GetString(0), r.GetString(1) == "GAP", r.GetString(2)));
            return (IReadOnlyList<ProofRow>)rows;
        });

    /// <summary>Appends a revocation of proof <paramref name="proofId"/>: a launch's canary found it no longer true. The proof stops counting.</summary>
    public void Revoke(long proofId, string reason) =>
        db.Write(_ =>
        {
            using var c = db.Cmd("INSERT INTO cell_proof_revocation(proof_id, reason, revoked_at) VALUES($id, $reason, $at)",
                ("$id", proofId), ("$reason", reason), ("$at", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            return c.ExecuteNonQuery();
        });
}
