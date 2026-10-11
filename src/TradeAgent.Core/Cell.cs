using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeAgent.Core;

/// <summary>
/// WHAT ONE CELL MAY TOUCH (<c>U-cell-linux-a</c>, ORCHESTRATION § 2.8): a workspace it may read, write and run in, inputs it
/// may only read, the roots its programs are run from, the ENTIRE environment it starts with — named with its values, nothing
/// inherited — and the TCP ports it may connect to (none when empty). Everything not named here is denied by the kernel: the
/// app's <c>state/</c> and its holdout, the owner's logins, the home, every other process.
///
/// <para><b>An app-owned file, named by its own hash.</b> The policy reaches the cell stage as a file under
/// <see cref="Directory"/> — never through the environment or the argument list of anything a model can steer — and the file's
/// name IS the SHA-256 of its bytes, so a file changed after it was written no longer matches its name and is refused.</para>
///
/// <para><b>Refused, never trimmed.</b> A policy that would grant the app's home, anything above it, or anything beneath its
/// <c>state/</c> or its cell policies is not a weaker cell: <see cref="Problem"/> names it and nothing starts.</para>
/// </summary>
public sealed record CellPolicy
{
    /// <summary>The roots every cell's programs are run from: the system's binaries and libraries, its configuration, and <c>/proc</c>.</summary>
    public static readonly IReadOnlyList<string> SystemRoots = ["/usr", "/lib", "/lib64", "/lib32", "/bin", "/sbin", "/etc", "/proc"];

    /// <summary>Where the app writes cell policies: under its own home, never in a workspace.</summary>
    public static string Directory => Path.Combine(Paths.Home, "cells");

    public CellPolicy(string workspace, IEnumerable<string> readOnly, IEnumerable<string> executable,
        IEnumerable<KeyValuePair<string, string>> environment, IEnumerable<int>? tcpPorts = null)
    {
        Workspace = workspace;
        ReadOnly = [.. readOnly];
        Executable = [.. executable];
        Environment = [.. environment.OrderBy(e => e.Key, StringComparer.Ordinal)];
        TcpPorts = [.. (tcpPorts ?? []).Distinct().Order()];
    }

    /// <summary>The one directory the cell may read, write, create in, delete in and run programs from.</summary>
    public string Workspace { get; }

    /// <summary>Files and directories the cell may read and never change.</summary>
    public IReadOnlyList<string> ReadOnly { get; }

    /// <summary>Roots the cell may read and run programs from: the runtime's tools and <see cref="SystemRoots"/>.</summary>
    public IReadOnlyList<string> Executable { get; }

    /// <summary>The whole environment the cell's program starts with, sorted by name. Nothing else is inherited.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Environment { get; }

    /// <summary>TCP ports the cell may connect to. Empty: no TCP at all. No port may be bound.</summary>
    public IReadOnlyList<int> TcpPorts { get; }

    /// <summary>The policy as the bytes the file holds and the hash names: one canonical form, ordinal order throughout.</summary>
    public string Serialize() => JsonSerializer.Serialize(new Wire(Version, Workspace, [.. ReadOnly], [.. Executable],
        [.. Environment.Select(e => new[] { e.Key, e.Value })], [.. TcpPorts]));

    /// <summary>The SHA-256 of <see cref="Serialize"/>, lower-case hex: the policy's identity on the attempt and its file's name.</summary>
    public string Hash => Sha256Hex.Of(Serialize());

    /// <summary>
    /// Writes the policy owner-only under <paramref name="directory"/> (default <see cref="Directory"/>) as <c>&lt;hash&gt;.cell.json</c>
    /// and returns the path. Throws when <see cref="Problem"/> has anything to say.
    /// </summary>
    public string Write(string? directory = null)
    {
        if (Problem() is { } why) throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, why);
        var path = Path.Combine(directory ?? Directory, Hash + FileSuffix);
        OwnerOnlyFile.Write(path, Encoding.UTF8.GetBytes(Serialize()));
        return path;
    }

    /// <summary>
    /// Reads a policy file, or throws <see cref="TradeAgentException"/> (<see cref="ErrorCode.CELL_UNAVAILABLE"/>) saying why
    /// not: a name that is not a hash, bytes that do not hash to the name, a form this build does not write, or a
    /// <see cref="Problem"/>.
    /// </summary>
    public static CellPolicy Read(string path)
    {
        var name = Path.GetFileName(path);
        if (!FileName.IsMatch(name))
            throw Refused($"the cell policy {path} is not named <sha256>{FileSuffix}");
        var bytes = File.ReadAllBytes(path);
        var hash = Sha256Hex.Of(bytes);
        if (!name.StartsWith(hash, StringComparison.Ordinal))
            throw Refused($"the cell policy {path} does not hash to its name (its bytes hash to {hash}): it changed after the app wrote it");

        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(bytes); }
        catch (JsonException ex) { throw Refused($"the cell policy {path} is not readable ({ex.Message})"); }
        if (wire is null || wire.V != Version)
            throw Refused($"the cell policy {path} is form {wire?.V.ToString(CultureInfo.InvariantCulture) ?? "none"}; this build reads form {Version} only");
        if (wire.Workspace is null || wire.ReadOnly is null || wire.Executable is null || wire.Environment is null || wire.TcpPorts is null
            || wire.Environment.Any(e => e is not { Length: 2 }))
            throw Refused($"the cell policy {path} is missing a field");

        var policy = new CellPolicy(wire.Workspace, wire.ReadOnly, wire.Executable,
            wire.Environment.Select(e => new KeyValuePair<string, string>(e[0], e[1])), wire.TcpPorts);
        if (policy.Hash != hash) throw Refused($"the cell policy {path} is not in the one form this build writes");
        if (policy.Problem() is { } why) throw Refused(why);
        return policy;
    }

    /// <summary>
    /// What makes this policy one no cell may run under, or null. Every path absolute; no path that is, or is above, the app's
    /// home; none beneath its <c>state/</c> or its cell policies; a workspace that is a directory; environment names a program
    /// can read; ports a port can be.
    /// </summary>
    public string? Problem(string? appHome = null)
    {
        var home = Full(appHome ?? Paths.Home);
        var state = Path.Combine(home, "state");
        var cells = Path.Combine(home, "cells");

        if (string.IsNullOrWhiteSpace(Workspace) || !Path.IsPathFullyQualified(Workspace))
            return $"the cell's workspace '{Workspace}' is not an absolute path";
        foreach (var (what, p) in Granted())
        {
            if (string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p) || p.Contains('\0'))
                return $"the cell's {what} '{p}' is not an absolute path";
            foreach (var form in Forms(p))
            {
                if (Within(home, form))
                    return $"the cell's {what} {p} would grant TradeAgent's own home {home}, which holds its state and the owner's logins";
                if (Within(form, state) || Within(form, cells))
                    return $"the cell's {what} {p} lies inside TradeAgent's state or its cell policies";
            }
        }
        if (!System.IO.Directory.Exists(Workspace)) return $"the cell's workspace {Workspace} is not a directory";
        foreach (var (name, value) in Environment)
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
                return $"the cell's environment variable '{name}' cannot be passed to a program";
        if (Environment.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != Environment.Count)
            return "the cell's environment names one variable twice";
        if (TcpPorts.Where(p => p is < 1 or > 65535).Select(p => (int?)p).FirstOrDefault() is { } bad)
            return $"the cell's TCP port {bad} is not a port";
        return null;
    }

    IEnumerable<(string What, string Path)> Granted()
    {
        yield return ("workspace", Workspace);
        foreach (var p in ReadOnly) yield return ("read-only input", p);
        foreach (var p in Executable) yield return ("program root", p);
    }

    /// <summary>The path as written and, where it exists, as the kernel resolves it: a symlink is judged by where it leads.</summary>
    static IEnumerable<string> Forms(string p)
    {
        yield return Full(p);
        if (CellNative.RealPath(p) is { } real) yield return Full(real);
    }

    static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    /// <summary>Whether <paramref name="inner"/> is <paramref name="outer"/> or beneath it, by whole path components.</summary>
    internal static bool Within(string inner, string outer)
    {
        if (outer == "/") return true;
        return inner == outer || inner.StartsWith(outer + "/", StringComparison.Ordinal);
    }

    static TradeAgentException Refused(string why) => new(ErrorCode.CELL_UNAVAILABLE, why);

    /// <summary>The one form this build writes and reads. No other is read on a guess.</summary>
    const int Version = 1;

    const string FileSuffix = ".cell.json";

    static readonly Regex FileName = new("^[0-9a-f]{64}\\.cell\\.json$", RegexOptions.CultureInvariant);

    sealed record Wire(int V, string? Workspace, string[]? ReadOnly, string[]? Executable, string[][]? Environment, int[]? TcpPorts);
}

/// <summary>
/// WHAT THIS MACHINE CAN CONFINE A CELL WITH, read rather than assumed: the kernel's release, the Landlock ABI the kernel itself
/// reports (<c>landlock_create_ruleset(NULL, 0, VERSION)</c>), the active security modules (<c>/sys/kernel/security/lsm</c>), and
/// whether unprivileged user namespaces are restricted (<c>kernel.apparmor_restrict_unprivileged_userns</c>, recorded; no cell
/// uses a namespace).
///
/// <para><b>ABI 6 or refused.</b> ABI 6 is the one whose signal and abstract-socket scopes stop a cell of the app's own user from
/// killing the app or reaching its abstract sockets; below it a cell would be weaker than its policy says, so it is not started at
/// all (seat A's amendment iii). An ABI the kernel would not report, or a security-module list nobody can read, refuses the same
/// way: refuse, never guess.</para>
/// </summary>
public sealed record CellPlatform(bool Linux, string Kernel, string Architecture, int LandlockAbi, string? LandlockError,
    string? Lsm, string? UsernsRestricted)
{
    /// <summary>The lowest Landlock ABI a cell runs on: 6, kernel 6.12 (signal and abstract-UNIX-socket scoping).</summary>
    public const int MinimumAbi = 6;

    /// <summary>This machine, now.</summary>
    public static CellPlatform Read()
    {
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (!OperatingSystem.IsLinux())
            return new CellPlatform(false, RuntimeInformation.OSDescription, arch, 0, "not Linux", null, null);

        var (abi, error) = CellNative.LandlockAbi();
        return new CellPlatform(true, ReadLine("/proc/sys/kernel/osrelease") ?? RuntimeInformation.OSDescription, arch,
            abi, error, ReadLine("/sys/kernel/security/lsm"), ReadLine("/proc/sys/kernel/apparmor_restrict_unprivileged_userns"));
    }

    /// <summary>The ABI as the refusal names it.</summary>
    public string AbiFound => LandlockAbi > 0 ? LandlockAbi.ToString(CultureInfo.InvariantCulture) : $"none ({LandlockError ?? "not reported"})";

    /// <summary>
    /// The sentence that refuses a cell on this machine, naming the kernel, the ABI found and the minimum — or null when a cell
    /// can run here.
    /// </summary>
    public string? Refusal()
    {
        var versions = $"Landlock ABI found: {AbiFound}; minimum: {MinimumAbi}";
        const string none = "TradeAgent will not start the program, in a cell or outside one";
        if (!Linux)
            return $"A cell runs on Linux only, and this computer runs {Kernel} ({versions}). {none}.";
        if (Architecture is not ("x64" or "arm64"))
            return $"A cell's system-call filter knows x64 and arm64 only, and this is {Architecture} on Linux {Kernel} ({versions}). {none}.";
        if (Lsm is null)
            return $"Linux {Kernel} did not say which security modules are active (/sys/kernel/security/lsm could not be read), so " +
                   $"Landlock cannot be shown to be on ({versions}). {none}.";
        if (!Lsm.Split(',').Contains("landlock", StringComparer.Ordinal))
            return $"Linux {Kernel} does not run Landlock (active security modules: {Lsm}; {versions}). {none}.";
        if (LandlockAbi < MinimumAbi)
            return $"Linux {Kernel} offers Landlock ABI {AbiFound} and a cell needs ABI {MinimumAbi} or newer (kernel 6.12 or later), " +
                   $"whose signal and socket scoping keep a cell from the app itself. {none}.";
        return null;
    }

    /// <summary>One line for the Mechanism row: what held the cell.</summary>
    public string Summary => $"Landlock ABI {AbiFound} on Linux {Kernel} ({Architecture}; modules {Lsm ?? "unknown"})";

    static string? ReadLine(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception) { return null; }
    }
}
