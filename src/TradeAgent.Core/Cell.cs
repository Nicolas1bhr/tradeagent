using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeAgent.Core;

/// <summary>
/// WHAT ONE CELL MAY TOUCH (<c>U-cell-linux-a</c>, ORCHESTRATION § 2.8): a workspace it may read, write and run in, inputs it
/// may only read, the roots its programs are run from, the ENTIRE environment it starts with — named with its values, nothing
/// inherited — and its egress class (<see cref="CellEgress"/>, <c>U-cell-linux-d</c>: the TCP ports it may connect to and how it
/// resolves names; <see cref="CellEgress.None"/> when not named). Everything not named here is denied by the kernel: the app's
/// <c>state/</c> and its holdout, the owner's logins and inbox, the home, every other process.
///
/// <para><b>An app-owned file, named by its own hash.</b> The policy reaches the cell stage as a file under
/// <see cref="Directory"/> — never through the environment or the argument list of anything a model can steer — and the file's
/// name begins with the SHA-256 of its bytes, so a file changed after it was written no longer matches its name and is refused.
/// The rest of the name is one launch's own, so the launcher that removes it after its cell exits never removes another's.</para>
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

    /// <summary>
    /// A policy under egress class <paramref name="egress"/> (<see cref="CellEgress.None"/> when null). The class's own needs are
    /// ADDED here, so the policy names everything the cell may touch: its resolver's configuration among the read-only inputs and
    /// its resolver option in the environment, when the class resolves names.
    /// </summary>
    public CellPolicy(string workspace, IEnumerable<string> readOnly, IEnumerable<string> executable,
        IEnumerable<KeyValuePair<string, string>> environment, CellEgress? egress = null)
    {
        Egress = egress ?? CellEgress.None;
        Workspace = workspace;
        ReadOnly = [.. readOnly.Concat(Egress.ReadOnly).Distinct(StringComparer.Ordinal)];
        Executable = [.. executable];
        Environment = [.. environment.Concat(Egress.Environment).Distinct().OrderBy(e => e.Key, StringComparer.Ordinal).ThenBy(e => e.Value, StringComparer.Ordinal)];
    }

    /// <summary>The cell's egress class: which TCP ports it may connect to, and how it resolves names.</summary>
    public CellEgress Egress { get; }

    /// <summary>The one directory the cell may read, write, create in, delete in and run programs from.</summary>
    public string Workspace { get; }

    /// <summary>Files and directories the cell may read and never change.</summary>
    public IReadOnlyList<string> ReadOnly { get; }

    /// <summary>Roots the cell may read and run programs from: the runtime's tools and <see cref="SystemRoots"/>.</summary>
    public IReadOnlyList<string> Executable { get; }

    /// <summary>The whole environment the cell's program starts with, sorted by name. Nothing else is inherited.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Environment { get; }

    /// <summary>The TCP ports the cell may connect to — its class's effective set, DNS's port 53 included. Empty: no TCP at all. No port may be bound.</summary>
    public IReadOnlyList<int> TcpPorts => Egress.TcpPorts;

    /// <summary>
    /// The policy as the bytes the file holds and the hash names: one canonical form, ordinal order throughout. The egress class
    /// is written by name WITH its effective port set and its name resolution, so the hash covers what the kernel will be told.
    /// </summary>
    public string Serialize() => JsonSerializer.Serialize(new Wire(Version, Workspace, [.. ReadOnly], [.. Executable],
        [.. Environment.Select(e => new[] { e.Key, e.Value })],
        new WireEgress(Egress.Name, [.. Egress.Ports], Egress.Dns.ToString(), [.. Egress.TcpPorts])));

    /// <summary>The SHA-256 of <see cref="Serialize"/>, lower-case hex: the policy's identity on the attempt and its file's name.</summary>
    public string Hash => Sha256Hex.Of(Serialize());

    /// <summary>
    /// Writes the policy owner-only under <paramref name="directory"/> (default <see cref="Directory"/>) as
    /// <c>&lt;hash&gt;.&lt;launch&gt;.cell.json</c> (the launch part sixteen random hex digits: one file per launch) and returns the path. Throws when <see cref="Problem"/> has anything to say.
    /// </summary>
    public string Write(string? directory = null)
    {
        if (Problem() is { } why) throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, why);
        var launch = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var path = Path.Combine(directory ?? Directory, Hash + "." + launch + FileSuffix);
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
            throw Refused($"the cell policy {path} is not named <sha256>.<launch>{FileSuffix}");
        var bytes = File.ReadAllBytes(path);
        var hash = Sha256Hex.Of(bytes);
        if (!name.StartsWith(hash + ".", StringComparison.Ordinal))
            throw Refused($"the cell policy {path} does not hash to its name (its bytes hash to {hash}): it changed after the app wrote it");

        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(bytes); }
        catch (JsonException ex) { throw Refused($"the cell policy {path} is not readable ({ex.Message})"); }
        if (wire is null || wire.V != Version)
            throw Refused($"the cell policy {path} is form {wire?.V.ToString(CultureInfo.InvariantCulture) ?? "none"}; this build reads form {Version} only");
        if (wire.Workspace is null || wire.ReadOnly is null || wire.Executable is null || wire.Environment is null || wire.Egress is null
            || wire.Egress.Name is null || wire.Egress.Ports is null || wire.Egress.Dns is null || wire.Egress.TcpPorts is null
            || wire.Environment.Any(e => e is not { Length: 2 }))
            throw Refused($"the cell policy {path} is missing a field");
        if (!Enum.TryParse<CellDns>(wire.Egress.Dns, ignoreCase: false, out var dns) || !Enum.IsDefined(dns) || dns.ToString() != wire.Egress.Dns)
            throw Refused($"the cell policy {path} names a name resolution '{wire.Egress.Dns}' this build does not know");
        var egress = CellEgress.Read(wire.Egress.Name, wire.Egress.Ports, dns)
            ?? throw Refused($"the cell policy {path} names egress class '{wire.Egress.Name}' with ports " +
                             $"[{string.Join(", ", wire.Egress.Ports)}] and name resolution {dns}, which is not a class this build has " +
                             $"({string.Join("; ", CellEgress.Table.Select(c => c.Describe()))})");

        var policy = new CellPolicy(wire.Workspace, wire.ReadOnly, wire.Executable,
            wire.Environment.Select(e => new KeyValuePair<string, string>(e[0], e[1])), egress);
        if (policy.Hash != hash) throw Refused($"the cell policy {path} is not in the one form this build writes");
        if (policy.Problem() is { } why) throw Refused(why);
        return policy;
    }

    /// <summary>
    /// What makes this policy one no cell may run under, or null. Every path absolute; no path that is, or is above, the app's
    /// home; none beneath its <c>state/</c> or its cell policies; a workspace that is a directory; no read-only input inside, equal
    /// to or containing the workspace; environment names a program can read; ports a port can be.
    /// </summary>
    public string? Problem(string? appHome = null)
    {
        var guarded = Guarded.Of(appHome);

        if (string.IsNullOrWhiteSpace(Workspace) || !Path.IsPathFullyQualified(Workspace))
            return $"the cell's workspace '{Workspace}' is not an absolute path";
        foreach (var (what, p) in Granted())
        {
            if (string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p) || p.Contains('\0'))
                return $"the cell's {what} '{p}' is not an absolute path";
            foreach (var form in Forms(p))
                if (guarded.Refuses(form) is { } why) return $"the cell's {what} {p} {why}";
        }
        if (!System.IO.Directory.Exists(Workspace)) return $"the cell's workspace {Workspace} is not a directory";
        // A READ-ONLY INPUT THAT OVERLAPS THE WORKSPACE IS NOT READ-ONLY (triage #4): Landlock adds up the rights of every rule along
        // a path, so an input inside the workspace keeps the workspace's write rights, and one that contains it is written through it.
        foreach (var ro in ReadOnly)
            foreach (var r in Forms(ro))
                foreach (var w in Forms(Workspace))
                    if (Within(r, w) || Within(w, r))
                        return $"the cell's read-only input {ro} lies inside, is, or contains its workspace {Workspace}, where the cell may " +
                               "write, so it would not stay read-only";
        foreach (var (name, value) in Environment)
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
                return $"the cell's environment variable '{name}' cannot be passed to a program";
        if (Environment.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != Environment.Count)
            return "the cell's environment names one variable twice (its egress class sets " +
                   $"{string.Join(", ", Egress.Environment.Select(e => e.Key + "=" + e.Value).DefaultIfEmpty("nothing"))})";
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

    /// <summary>
    /// WHAT NO GRANT MAY REACH, IN EVERY SPELLING (triage #3): the app's home and everything above it, and its <c>state/</c> — the
    /// ledgers, the holdout and the credentials all live there — and its cell policies, each as written AND as the kernel resolves
    /// it. A home reached through a symlink is judged where it really is, so a grant naming the real <c>state/</c> is refused too.
    /// </summary>
    internal sealed record Guarded(IReadOnlyList<string> Homes, IReadOnlyList<string> Inside)
    {
        public static Guarded Of(string? appHome = null)
        {
            var home = appHome ?? Paths.Home;
            var homes = Forms(home).Distinct(StringComparer.Ordinal).ToList();
            var inside = homes.SelectMany(h => new[] { Path.Combine(h, "state"), Path.Combine(h, "cells") })
                .Concat(Forms(Path.Combine(home, "state"))).Concat(Forms(Path.Combine(home, "cells")))
                .Distinct(StringComparer.Ordinal).ToList();
            return new Guarded(homes, inside);
        }

        /// <summary>Why a path — one already resolved, or one spelling of it — may not be granted, or null.</summary>
        public string? Refuses(string form)
        {
            foreach (var home in Homes)
                if (Within(home, form))
                    return $"would grant TradeAgent's own home {home}, which holds its state and the owner's logins";
            foreach (var root in Inside)
                if (Within(form, root))
                    return $"lies inside TradeAgent's state or its cell policies ({root})";
            return null;
        }
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

    /// <summary>
    /// The one form this build writes and reads. No other is read on a guess: form 1 (<c>U-cell-linux-a</c>'s raw port list) is
    /// refused naming both forms — no backward compatibility while building.
    /// </summary>
    internal const int Version = 2;

    const string FileSuffix = ".cell.json";

    static readonly Regex FileName = new("^[0-9a-f]{64}\\.[0-9a-f]{16}\\.cell\\.json$", RegexOptions.CultureInvariant);

    sealed record Wire(int V, string? Workspace, string[]? ReadOnly, string[]? Executable, string[][]? Environment, WireEgress? Egress);

    sealed record WireEgress(string? Name, int[]? Ports, string? Dns, int[]? TcpPorts);
}

/// <summary>How a cell resolves names (<c>U-cell-linux-d</c> item 1: the probe's measurement decided which one the classes use).</summary>
public enum CellDns
{
    /// <summary>No name resolution beyond <c>/etc/hosts</c>: no port 53, no datagram socket.</summary>
    None,

    /// <summary>
    /// DNS OVER TCP: datagrams stay DENIED (seccomp), TCP port 53 is granted to ANY host (Landlock rules are port-only — labelled),
    /// glibc is told <c>RES_OPTIONS=use-vc</c>, and the resolver's configuration (<c>/etc/resolv.conf</c> where it really is) is
    /// readable. Seccomp sees <c>socket()</c>'s family, type and protocol, never the address behind <c>connect</c> or <c>sendto</c>,
    /// so "datagrams only to the loopback resolver" cannot be enforced literally; this is its closest honest form.
    /// </summary>
    Tcp,

    /// <summary>
    /// DATAGRAMS OPEN: IPv4 and IPv6 UDP sockets allowed, to ANY host and port (no Landlock UDP rule below ABI 10, kernel 7.2), the
    /// resolver's configuration readable. The probe's mode (B); no class of this build uses it.
    /// </summary>
    Datagrams
}

/// <summary>
/// A CELL'S EGRESS CLASS (<c>U-cell-linux-d</c>, ORCHESTRATION § 2.8 "a network and IPC policy"): ONE table of named classes —
/// <see cref="None"/> (the default: no TCP port, no datagram), <see cref="Web"/> (443) and <see cref="WebPlain"/> (80 and 443), each
/// with item 1's name resolution — and nothing a model can add to it. What every class leaves open is said in
/// <see cref="Profile"/>, and a row of <c>CellLinuxTests</c> asserts each gap, so the day the kernel closes one the row turns red.
///
/// <para><b>What a granted port reaches.</b> Landlock's TCP rules are PORT-ONLY: a cell granted 443 reaches 443 on the internet, on
/// this host, on its loopback (IPv4 and IPv6) and on the LAN alike, and two cells granted one port reach the same outside listener —
/// a channel between them outside the relay. That is why a class with any port never counts as unable to write the owner's inbox: a
/// same-user listener on a granted port could write it for the cell.</para>
///
/// <para><b>No class listens.</b> Landlock handles TCP bind on every cell and grants no bind rule; seccomp denies <c>listen()</c>,
/// which on a socket nobody bound picks an ephemeral port without asking Landlock.</para>
/// </summary>
public sealed class CellEgress
{
    /// <summary>
    /// ITEM 1'S DECISION, taken by <c>A_cell_name_resolution_probe</c>'s measurement on CI's ubuntu-24.04: when glibc, Python, Node
    /// and pip all resolve over TCP with datagrams denied, the classes resolve over TCP and datagrams stay DENIED (hard) for every
    /// cell; otherwise UDP opens for egress-classed cells, labelled, and <see cref="None"/> keeps it denied.
    /// </summary>
    public const CellDns Decided = CellDns.Tcp;

    /// <summary>The DNS port a <see cref="CellDns.Tcp"/> class adds: 53, to any host.</summary>
    public const int DnsPort = 53;

    /// <summary>The name a test-only table carries; never a class of the table.</summary>
    internal const string TestName = "test";

    /// <summary>No TCP connect to any port, no listening, no datagram: names resolve from <c>/etc/hosts</c> only. The default.</summary>
    public static CellEgress None { get; } = new("none", [], CellDns.None);

    /// <summary>HTTPS: TCP 443 to any host, and names (<see cref="Decided"/>).</summary>
    public static CellEgress Web { get; } = new("web", [443], Decided);

    /// <summary>HTTP and HTTPS: TCP 80 and 443 to any host, and names (<see cref="Decided"/>).</summary>
    public static CellEgress WebPlain { get; } = new("web-plain", [80, 443], Decided);

    /// <summary>The one table. A class is one of these, by name.</summary>
    public static IReadOnlyList<CellEgress> Table { get; } = [None, Web, WebPlain];

    CellEgress(string name, IEnumerable<int> ports, CellDns dns)
    {
        Name = name;
        Ports = [.. ports.Distinct().Order()];
        Dns = dns;
        TcpPorts = dns == CellDns.Tcp ? [.. Ports.Append(DnsPort).Distinct().Order()] : Ports;
    }

    /// <summary>
    /// A TEST-ONLY TABLE: explicit ports and name resolution, so the rows can grant ephemeral ports (an unprivileged runner cannot
    /// listen on 443) and the probe can open datagrams. Internal: no product code names ports outside <see cref="Table"/>.
    /// </summary>
    internal static CellEgress ForTests(IEnumerable<int> ports, CellDns dns = CellDns.None) => new(TestName, ports, dns);

    /// <summary>The class by its table name, or null.</summary>
    public static CellEgress? Named(string name) => Table.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// The class a policy file names, or null when it is not one this build has: a table name whose ports or resolution differ from
    /// the table's is not that class, read on a guess.
    /// </summary>
    internal static CellEgress? Read(string name, IReadOnlyList<int> ports, CellDns dns)
    {
        if (name == TestName) return ForTests(ports, dns);
        return Named(name) is { } c && c.Dns == dns && c.Ports.SequenceEqual(ports) ? c : null;
    }

    /// <summary>The class's name: one of <see cref="Table"/>, or <c>test</c>.</summary>
    public string Name { get; }

    /// <summary>The class's own ports, without the DNS port.</summary>
    public IReadOnlyList<int> Ports { get; }

    /// <summary>How the class resolves names.</summary>
    public CellDns Dns { get; }

    /// <summary>The effective TCP connect set the kernel is told: <see cref="Ports"/>, and 53 when names resolve over TCP.</summary>
    public IReadOnlyList<int> TcpPorts { get; }

    /// <summary>Whether the cell may make IPv4 and IPv6 UDP sockets (seccomp). False for every class of the table.</summary>
    public bool DatagramsOpen => Dns == CellDns.Datagrams;

    /// <summary>What the class adds to the cell's environment: glibc's resolver told to use TCP when names resolve over TCP.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Environment =>
        Dns == CellDns.Tcp ? [new("RES_OPTIONS", "use-vc")] : [];

    /// <summary>
    /// What the class adds to the cell's read-only inputs: the resolver's configuration where it really is — on Ubuntu
    /// <c>/etc/resolv.conf</c> is a link into <c>/run/systemd/resolve</c>, outside every program root, and Landlock judges a file
    /// where the link leads. Nothing when the class resolves no names.
    /// </summary>
    public IReadOnlyList<string> ReadOnly =>
        Dns != CellDns.None && CellNative.RealPath(ResolverConfiguration) is { } real ? [real] : [];

    /// <summary>The resolver configuration glibc reads.</summary>
    public const string ResolverConfiguration = "/etc/resolv.conf";

    /// <summary>One line: the class, its ports and its resolution.</summary>
    public string Describe() =>
        $"{Name}: TCP [{string.Join(", ", Ports)}], names {Dns switch { CellDns.Tcp => "over TCP 53", CellDns.Datagrams => "over UDP", _ => "from /etc/hosts only" }}";

    /// <summary>
    /// THE CLASS'S LINE IN THE ASSURANCE PROFILE: what TCP, UDP, DNS and listening are, each DENIED (and by what) or OPEN and labelled.
    /// </summary>
    public string Profile => Dns switch
    {
        CellDns.None when Ports.Count == 0 =>
            $"egress {Name}: TCP connect DENIED to every port (Landlock); no listening (Landlock bind, seccomp listen); UDP and every " +
            "other datagram DENIED (seccomp); DNS DENIED — names from /etc/hosts only",
        CellDns.None =>
            $"egress {Name}: TCP connect to port {string.Join(", ", Ports)} on ANY host — loopback, this host and the LAN alike (Landlock " +
            "rules are port-only: a recorded gap); no listening (Landlock bind, seccomp listen); UDP DENIED (seccomp); DNS DENIED — " +
            "names from /etc/hosts only",
        CellDns.Tcp =>
            $"egress {Name}: TCP connect to port {string.Join(", ", Ports)} on ANY host — loopback, this host and the LAN alike (Landlock " +
            "rules are port-only: a recorded gap); no listening (Landlock bind, seccomp listen); UDP DENIED (seccomp, hard); DNS OVER " +
            "TCP — port 53 to ANY host (labelled), glibc's use-vc, the resolver's configuration readable",
        _ =>
            $"egress {Name}: TCP connect to port {string.Join(", ", Ports)} on ANY host (port-only, a recorded gap); no listening; UDP " +
            "OPEN — ANY host and port, IPv4 and IPv6 (no Landlock UDP rule below ABI 10; labelled); DNS over UDP"
    };
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
                   $"whose signal and socket scoping keep a cell from the app itself ({versions}). {none}.";
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
