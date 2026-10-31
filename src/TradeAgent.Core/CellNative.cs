using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TradeAgent.Core;

/// <summary>
/// THE KERNEL CALLS A CELL IS MADE OF (<c>U-cell-linux-a</c> item 2, ORCHESTRATION § 2.8): <c>no_new_privs</c>, a seccomp filter,
/// a Landlock domain — on ONE thread, the one that then calls <c>execve</c>. Linux only: every entry point answers "none" or refuses
/// elsewhere rather than reaching for a symbol the platform does not have.
///
/// <para><b>Why one thread and then execve.</b> Below Landlock ABI 8 a restriction covers only the thread that asked for it and
/// what it starts (the kernel's Landlock documentation); a .NET process has a dozen threads this code does not own. <c>execve</c>
/// ends every other thread and hands the new image the calling thread's no_new_privs, filter and domain, so the program the cell
/// runs is confined from its first instruction and nothing unconfined of this process survives it.</para>
///
/// <para><b>What each layer holds</b> (the survey, RUN on Ubuntu 24.04): Landlock — the files (workspace read-write, inputs
/// read-only, program roots read-and-run, nothing else: not <c>state/</c>, not the home, not the cgroup filesystem), TCP by port,
/// ptrace and <c>/proc/&lt;pid&gt;/environ</c> of anything outside, signals out of the cell and abstract UNIX sockets (ABI 6).
/// seccomp — <c>socket()</c> only for TCP over IPv4 or IPv6 (pathname UNIX sockets are outside Landlock before ABI 9: without
/// this rule a cell ran <c>systemd-run --user</c> and read a planted ledger), no <c>io_uring</c> (its socket and connect
/// operations never pass <c>socket(2)</c>), no kernel keyrings, <c>bpf</c>, <c>perf_event_open</c> or <c>userfaultfd</c>, and any
/// other architecture's system calls killed. no_new_privs — no setuid gain, and Landlock's precondition.</para>
/// </summary>
public static class CellNative
{
    // ---- Landlock (include/uapi/linux/landlock.h) ---------------------------------------------------------------

    /// <summary><c>landlock_create_ruleset</c>, <c>_add_rule</c>, <c>_restrict_self</c> and <c>close_range</c>: one number on every architecture.</summary>
    const long SysLandlockCreateRuleset = 444, SysLandlockAddRule = 445, SysLandlockRestrictSelf = 446, SysCloseRange = 436;

    const uint LANDLOCK_CREATE_RULESET_VERSION = 1;
    const int LANDLOCK_RULE_PATH_BENEATH = 1, LANDLOCK_RULE_NET_PORT = 2;

    const ulong FsExecute = 1UL << 0, FsWriteFile = 1UL << 1, FsReadFile = 1UL << 2, FsReadDir = 1UL << 3,
        FsTruncate = 1UL << 14, FsIoctlDev = 1UL << 15, FsResolveUnix = 1UL << 16;

    /// <summary>Every filesystem right ABI 6 knows (bits 0–15: ABI 1's thirteen, REFER, TRUNCATE, IOCTL_DEV), all handled — so all denied unless a rule grants it.</summary>
    const ulong FsAll = (1UL << 16) - 1;

    /// <summary>
    /// The filesystem rights a ruleset handles on a kernel offering Landlock <paramref name="abi"/>: every right ABI 6 knows, and from
    /// ABI 9 <c>RESOLVE_UNIX</c> too — the lookup of a pathname UNIX socket made outside the cell, so a cell connects or sends to
    /// none outside its workspace even with a socket in hand (seccomp already refuses it one). A kernel refuses a right it does not
    /// know, so an older ABI is never asked for it.
    /// </summary>
    internal static ulong HandledFs(int abi) => abi >= 9 ? FsAll | FsResolveUnix : FsAll;

    /// <summary>The rights that mean anything on a file rather than a directory; a rule on a file may carry only these.</summary>
    const ulong FsFileRights = FsExecute | FsWriteFile | FsReadFile | FsTruncate | FsIoctlDev;

    const ulong NetBindTcp = 1UL << 0, NetConnectTcp = 1UL << 1;
    const ulong ScopeAbstractUnixSocket = 1UL << 0, ScopeSignal = 1UL << 1;

    /// <summary>The devices every program expects, and only those: no terminal, no other device.</summary>
    static readonly (string Path, ulong Rights)[] Devices =
    [
        ("/dev/null", FsReadFile | FsWriteFile | FsTruncate | FsIoctlDev),
        ("/dev/zero", FsReadFile), ("/dev/random", FsReadFile), ("/dev/urandom", FsReadFile)
    ];

    // ---- seccomp (include/uapi/linux/seccomp.h, filter.h, audit.h) -------------------------------------------------

    const uint SeccompRetKillProcess = 0x80000000, SeccompRetErrno = 0x00050000, SeccompRetAllow = 0x7fff0000;
    const uint EPERM = 1, EACCES = 13;
    const uint AuditArchX64 = 0xC000003E, AuditArchArm64 = 0xC00000B7;
    const uint X32SyscallBit = 0x40000000;
    const uint AF_UNIX = 1, AF_INET = 2, AF_INET6 = 10, SOCK_STREAM = 1, SOCK_DGRAM = 2, SOCK_SEQPACKET = 5, SOCK_NONBLOCK = 0x800,
        SOCK_CLOEXEC = 0x80000, IPPROTO_TCP = 6, IPPROTO_UDP = 17, MSG_FASTOPEN = 0x20000000;

    /// <summary>
    /// The system calls the filter reads by number: <c>socket</c>, <c>socketpair</c>, <c>listen</c>, the three sends whose flags it
    /// reads, and the denied ones; everything else passes to Landlock.
    /// </summary>
    sealed record Table(uint AuditArch, uint Socket, uint Socketpair, uint Listen, uint Sendto, uint Sendmsg, uint Sendmmsg, uint[] Denied);

    static Table? TableFor(Architecture arch) => arch switch
    {
        // io_uring_setup/enter/register, keyctl, add_key, request_key, bpf, perf_event_open, userfaultfd
        Architecture.X64 => new(AuditArchX64, 41, 53, 50, 44, 46, 307, [425, 426, 427, 250, 248, 249, 321, 298, 323]),
        Architecture.Arm64 => new(AuditArchArm64, 198, 199, 201, 206, 211, 269, [425, 426, 427, 219, 217, 218, 280, 241, 282]),
        _ => null
    };

    const int PR_SET_SECCOMP = 22, PR_SET_NO_NEW_PRIVS = 38, SECCOMP_MODE_FILTER = 2;
    const int O_CLOEXEC = 0x80000, O_PATH = 0x200000, ENOENT = 2, ENOTDIR = 20;

    /// <summary><c>O_NOFOLLOW</c>: 0400000 on x86-64, 0100000 on arm64 (its own <c>asm/fcntl.h</c>).</summary>
    static int O_NOFOLLOW => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    const int AT_FDCWD = -100, AT_SYMLINK_NOFOLLOW = 0x100, AT_EMPTY_PATH = 0x1000;
    const uint STATX_TYPE = 0x1, STATX_MODE = 0x2, STATX_UID = 0x8;
    const int S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFLNK = 0xA000;
    const uint CLOSE_RANGE_CLOEXEC = 1U << 2;
    const int SIGPIPE = 13, SIG_SETMASK = 2;

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    static extern long Syscall3(long number, nint a1, nint a2, nint a3);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    static extern long Syscall4(long number, nint a1, nint a2, nint a3, nint a4);

    [DllImport("libc", SetLastError = true)]
    static extern int prctl(int option, nuint a2, nuint a3, nuint a4, nuint a5);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int OpenPath(nint path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int execve(nint path, nint argv, nint envp);

    [DllImport("libc", SetLastError = true)]
    static extern nint signal(int signum, nint handler);

    [DllImport("libc", SetLastError = true)]
    static extern int pthread_sigmask(int how, nint set, nint oldset);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    static extern nint realpath(string path, nint resolved);

    [DllImport("libc", EntryPoint = "free")]
    static extern void free(nint p);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int statx(int dirfd, string path, int flags, uint mask, nint buf);

    [DllImport("libc", SetLastError = true)]
    static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    static extern int fstatfs(int fd, nint buf);

    /// <summary><c>CGROUP2_SUPER_MAGIC</c> and <c>CGROUP_SUPER_MAGIC</c> (<c>include/uapi/linux/magic.h</c>).</summary>
    const long Cgroup2Magic = 0x63677270, Cgroup1Magic = 0x27e0eb;

    /// <summary>
    /// Whether <paramref name="fd"/> lies on a cgroup filesystem, by <c>fstatfs</c>'s <c>f_type</c> (the first word of
    /// <c>struct statfs</c> on every 64-bit architecture) — wherever that filesystem is mounted. Null when the kernel would not say.
    /// </summary>
    static bool? OnCgroupFs(int fd)
    {
        var buf = Zeroed(256);
        try { return fstatfs(fd, buf) != 0 ? null : Marshal.ReadInt64(buf, 0) is Cgroup2Magic or Cgroup1Magic; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// The owner and file type of what <paramref name="fd"/> holds (<paramref name="path"/> null), or of <paramref name="path"/>
    /// itself without following a final symlink — or null when the kernel would not say. <c>struct statx</c> is the same on every
    /// architecture: <c>stx_uid</c> at 20, <c>stx_mode</c> at 28.
    /// </summary>
    static (uint Uid, int Type)? Stat(int fd, string? path)
    {
        var buf = Zeroed(256);
        try
        {
            var r = path is null
                ? statx(fd, "", AT_EMPTY_PATH, STATX_TYPE | STATX_MODE | STATX_UID, buf)
                : statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_TYPE | STATX_MODE | STATX_UID, buf);
            if (r != 0) return null;
            return ((uint)Marshal.ReadInt32(buf, 20), (ushort)Marshal.ReadInt16(buf, 28) & S_IFMT);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Why <paramref name="path"/> is not owned by the account this process runs as (its effective uid), or null when it is. Linux
    /// only; .NET itself has no owner API.
    /// </summary>
    internal static string? NotOwnedByThisAccount(string path)
    {
        if (!OperatingSystem.IsLinux()) return $"{path}'s owner can be read on Linux only";
        var me = geteuid();
        return Stat(-1, path) is not { } st ? $"{path}'s owner could not be read"
            : st.Uid != me ? $"{path} belongs to uid {st.Uid}, not to this account (uid {me})"
            : null;
    }

    /// <summary>Where an open descriptor really leads, as the kernel names it (<c>/proc/self/fd/&lt;n&gt;</c>), or null.</summary>
    static string? Target(int fd)
    {
        try { return new FileInfo($"/proc/self/fd/{fd}").LinkTarget; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The Landlock ABI the running kernel reports, or 0 and the reason it did not: <c>ENOSYS</c> (not built in) or
    /// <c>EOPNOTSUPP</c> (built in, switched off at boot).
    /// </summary>
    public static (int Abi, string? Error) LandlockAbi()
    {
        if (!OperatingSystem.IsLinux()) return (0, "not Linux");
        try
        {
            var abi = Syscall3(SysLandlockCreateRuleset, 0, 0, (nint)LANDLOCK_CREATE_RULESET_VERSION);
            if (abi > 0) return ((int)abi, null);
            var errno = Marshal.GetLastPInvokeError();
            return (0, errno switch
            {
                38 => "errno 38: the kernel has no Landlock",
                95 => "errno 95: Landlock is switched off in this kernel's boot configuration",
                _ => $"errno {errno}"
            });
        }
        catch (Exception ex) { return (0, ex.GetType().Name); }
    }

    /// <summary>Where an existing path leads once every symlink in it is followed, or null when it does not exist (or on Windows).</summary>
    public static string? RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            var p = realpath(path, 0);
            if (p == 0) return null;
            try { return Marshal.PtrToStringUTF8(p); }
            finally { free(p); }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Why entering a cell stopped: the step, and the errno the kernel gave it (0 when the step is not a call).</summary>
    public sealed record Failure(string Step, int Errno)
    {
        public string Sentence => Errno == 0
            ? $"the cell stage stopped at {Step}"
            : $"the cell stage stopped at {Step}: errno {Errno} ({Marshal.GetPInvokeErrorMessage(Errno)})";
    }

    /// <summary>
    /// ENTERS THE CELL AND BECOMES <paramref name="program"/>: never returns when it works. In order, on the calling thread:
    /// the Landlock ruleset is built from <paramref name="policy"/> (nothing is restricted yet); <c>no_new_privs</c>; the seccomp
    /// filter; <c>landlock_restrict_self</c>; every descriptor above 2 marked close-on-exec; SIGPIPE and the signal mask put back
    /// to what a program expects; <c>execve</c> with an environment built from the policy alone. A failure at any step returns
    /// that step — the caller exits 126 and starts nothing.
    /// </summary>
    /// <param name="program">An absolute path the policy lets the cell run.</param>
    public static Failure Enter(CellPolicy policy, string program, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!OperatingSystem.IsLinux()) return new Failure("the platform check (not Linux)", 0);
        if (TableFor(RuntimeInformation.ProcessArchitecture) is null)
            return new Failure($"the platform check (no filter for {RuntimeInformation.ProcessArchitecture})", 0);
        if (!Path.IsPathFullyQualified(program)) return new Failure($"resolving the program ({program} is not an absolute path)", 0);

        // EVERYTHING THE LAST CALLS NEED IS MADE NOW, while the thread may still read the runtime's files: the P/Invoke stubs
        // bound, the strings and arrays in unmanaged memory. After landlock_restrict_self nothing here opens a file.
        Marshal.PrelinkAll(typeof(CellNative));
        var path = Utf8(program);
        var argv = Vector([program, .. args]);
        var envp = Vector([.. policy.Environment.Select(e => e.Key + "=" + e.Value)]);
        var filter = Program(SeccompProgram(RuntimeInformation.ProcessArchitecture, policy.Egress.DatagramsOpen));
        var emptyMask = Zeroed(128);

        var ruleset = Ruleset(policy, out var failure);
        if (ruleset < 0) return failure!;

        if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0) return Fail("prctl(PR_SET_NO_NEW_PRIVS)");
        if (prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, (nuint)filter, 0, 0) != 0) return Fail("installing the seccomp filter");
        if (Syscall3(SysLandlockRestrictSelf, ruleset, 0, 0) != 0) return Fail("landlock_restrict_self");
        close(ruleset);

        if (Syscall3(SysCloseRange, 3, unchecked((nint)0xFFFFFFFFu), (nint)CLOSE_RANGE_CLOEXEC) != 0) return Fail("close_range(3, ~0, CLOEXEC)");
        signal(SIGPIPE, 0);                       // SIG_DFL: the runtime ignores SIGPIPE, and an ignored signal survives execve
        pthread_sigmask(SIG_SETMASK, emptyMask, 0);

        execve(path, argv, envp);
        return Fail($"execve {program}");
    }

    /// <summary>
    /// A TEST SEAM, in-process only: called with each path just before the ruleset opens it, so a test can retarget a symlink the
    /// policy's check already passed. Nothing outside this assembly's tests can set it; the cell stage never does.
    /// </summary>
    internal static Action<string>? BeforeGrantOpen;

    /// <summary>The ruleset's descriptor, or -1 with the failure: every right handled, each policy path granted only its own.</summary>
    internal static int Ruleset(CellPolicy policy, out Failure? failed)
    {
        Failure? failure = null;
        failed = null;
        var handled = HandledFs(LandlockAbi().Abi);
        var attr = Zeroed(24);
        Marshal.WriteInt64(attr, 0, (long)handled);
        // TCP BIND AND CONNECT ARE HANDLED FOR EVERY CELL — egress class `none` included (U-cell-linux-d): a right the ruleset does not
        // handle is a right Landlock never checks, so a cell with no port would connect anywhere. Ports are granted below, binds never.
        Marshal.WriteInt64(attr, 8, (long)(NetBindTcp | NetConnectTcp));
        Marshal.WriteInt64(attr, 16, (long)(ScopeAbstractUnixSocket | ScopeSignal));
        var fd = (int)Syscall3(SysLandlockCreateRuleset, attr, 24, 0);
        if (fd < 0) { failed = Fail("landlock_create_ruleset"); return -1; }

        // OPENED FIRST, THEN JUDGED, AND THE DESCRIPTOR JUDGED IS THE ONE GRANTED (triage #2): the policy's check ran on paths, and
        // a path can be retargeted after it — by another cell sharing the workspace — so what is checked here is where the open
        // descriptor really leads, against the app's protected set in every spelling, and that same descriptor goes to the kernel.
        // The same for the overlap rule (triage #4): a read-only input is judged against where the workspace's descriptor really
        // led, so a workspace symlink retargeted onto an input after the check cannot hand that input the workspace's write rights.
        var guarded = CellPolicy.Guarded.Of();
        string? workspace = null;
        bool Grant(string p, ulong rights, bool required, bool readOnly = false, bool programRoot = false)
        {
            BeforeGrantOpen?.Invoke(p);
            var name = Utf8(p);
            var parent = OpenPath(name, O_PATH | O_NOFOLLOW | O_CLOEXEC);
            if (parent >= 0 && Stat(parent, null) is { Type: S_IFLNK })
            {
                // A SYMLINK AS THE LAST COMPONENT (Ubuntu's /lib, /bin, /sbin): opened through, and judged below by where it led.
                close(parent);
                parent = OpenPath(name, O_PATH | O_CLOEXEC);
            }
            if (parent < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (!required && errno is ENOENT or ENOTDIR) return true;   // a root this machine does not have grants nothing
                failure = new Failure($"opening {p}", errno);
                return false;
            }
            var target = Target(parent);
            var stat = Stat(parent, null);
            var why = target is null || !target.StartsWith('/') || target.EndsWith(" (deleted)", StringComparison.Ordinal)
                ? "names no path the kernel can show"
                // A PROGRAM ROOT IS JUDGED AS ONE HERE TOO (U-execution-environment item 0): the policy's path check lets a root lie
                // strictly inside tools/ — the codex the app installs, read and run, never written — and this descriptor check, which
                // judged it as any grant, refused every route (a) cell over the installed codex (CI run 39741425317).
                : stat is null ? "could not be examined" : guarded.Refuses(target, executable: programRoot, writable: required);
            // ANY CGROUP FILESYSTEM, BY THE DESCRIPTOR ITSELF (U-cell-linux-b fix pass 1, R2): a second mount of the hierarchy, or one
            // reached by a bind mount, has another path; the filesystem the descriptor is on does not lie.
            if (why is null && OnCgroupFs(parent) is not false)
                why = "lies on a cgroup filesystem (or the kernel would not say which filesystem it is on): a cell there could move itself " +
                      "out of its attempt's cgroup or lift its own ceilings";
            if (why is null && readOnly && workspace is not null &&
                (CellPolicy.Within(target!, workspace) || CellPolicy.Within(workspace, target!)))
                why = $"lies inside, is, or contains the workspace's real {workspace}, where the cell may write, so it would not stay read-only";
            if (why is not null)
            {
                close(parent);
                failure = new Failure($"granting {p}, which leads to {target ?? "nothing"}: it {why}", 0);
                return false;
            }
            if (stat!.Value.Type != S_IFDIR) rights &= FsFileRights;
            if (required) workspace ??= target;
            var rule = Zeroed(12);                   // struct landlock_path_beneath_attr is packed: u64 rights, s32 fd
            Marshal.WriteInt64(rule, 0, (long)rights);
            Marshal.WriteInt32(rule, 8, parent);
            var r = Syscall4(SysLandlockAddRule, fd, LANDLOCK_RULE_PATH_BENEATH, rule, 0);
            if (r != 0) failure = Fail($"granting {p}");
            close(parent);
            return r == 0;
        }

        var ok = Grant(policy.Workspace, handled, required: true);
        foreach (var p in policy.ReadOnly) ok = ok && Grant(p, FsReadFile | FsReadDir, required: false, readOnly: true);
        foreach (var p in policy.Executable) ok = ok && Grant(p, FsExecute | FsReadFile | FsReadDir, required: false, programRoot: true);
        foreach (var (p, rights) in Devices) ok = ok && Grant(p, rights, required: false);
        foreach (var port in policy.TcpPorts)
        {
            if (!ok) break;
            var rule = Zeroed(16);                   // struct landlock_net_port_attr: u64 rights, u64 port
            Marshal.WriteInt64(rule, 0, (long)NetConnectTcp);
            Marshal.WriteInt64(rule, 8, port);
            if (Syscall4(SysLandlockAddRule, fd, LANDLOCK_RULE_NET_PORT, rule, 0) != 0) { failure = Fail($"granting TCP port {port}"); ok = false; }
        }
        if (ok) return fd;
        close(fd);
        failed = failure;
        return -1;
    }

    /// <summary>
    /// THE SECCOMP FILTER, AS CLASSIC BPF (one 8-byte <c>sock_filter</c> per instruction). Another architecture's calls: killed. On
    /// x64, the x32 numbers: EPERM. <c>io_uring_*</c>, the keyring calls, <c>bpf</c>, <c>perf_event_open</c>, <c>userfaultfd</c>:
    /// EPERM. <c>socket()</c>: allowed only for AF_INET or AF_INET6, type SOCK_STREAM (SOCK_NONBLOCK and SOCK_CLOEXEC masked off),
    /// protocol 0 or IPPROTO_TCP — Landlock then governs the port — and EACCES for every other family, type or protocol: AF_UNIX,
    /// AF_NETLINK, AF_PACKET, AF_VSOCK, datagrams and ICMP among them. With <paramref name="datagrams"/> (the probe's mode (B), which
    /// no class of this build uses) an AF_INET or AF_INET6 SOCK_DGRAM socket of protocol 0 or IPPROTO_UDP is allowed too, to any host
    /// and port. <c>socketpair()</c>: allowed only for an AF_UNIX stream or
    /// seqpacket pair (flags masked), which runtimes wake themselves with and whose ends reach only each other — a connected stream
    /// end's <c>connect</c> and addressed <c>sendto</c> are EISCONN, a seqpacket end's address is ignored for its peer; a DATAGRAM
    /// pair is EACCES, because its end can be connected or <c>sendto</c>'d to any pathname datagram listener on the host (journald,
    /// <c>/dev/log</c>).
    ///
    /// <para><b>The two TCP doors Landlock does not see</b> (<c>U-cell-linux-d</c>; Landlock hooks <c>bind</c> and <c>connect</c>
    /// only): <c>listen()</c> — on a socket nobody bound it picks an ephemeral port itself, so a cell would serve without a bind —
    /// EACCES; and a send with <c>MSG_FASTOPEN</c> (<c>sendto</c>, <c>sendmsg</c>, <c>sendmmsg</c>), which opens a TCP connection
    /// without the <c>connect</c> call Landlock checks — EACCES. Everything else: allowed, and Landlock's.</para>
    /// </summary>
    /// <summary>
    /// WHAT THE KERNEL IS TOLD, AS ONE STRING (<c>U-cell-linux-c</c> item 5): the seccomp program's bytes for this processor and the
    /// Landlock rights handled at <paramref name="abi"/> — part of a proof's profile, so a build that changes either holds no proof.
    /// </summary>
    public static string KernelSurface(Architecture architecture, int abi) =>
        $"seccomp {Convert.ToHexString(TableFor(architecture) is null ? [] : SeccompProgram(architecture)).ToLowerInvariant()}; " +
        $"landlock-fs {HandledFs(abi):x}";

    internal static byte[] SeccompProgram(Architecture architecture, bool datagrams = false)
    {
        var t = TableFor(architecture) ?? throw new PlatformNotSupportedException($"no seccomp filter for {architecture}");
        var b = new Bpf();
        b.Ld(4).Jeq(t.AuditArch, null, "kill");
        b.Ld(0);
        if (architecture == Architecture.X64) b.Jge(X32SyscallBit, "eperm", null);
        foreach (var nr in t.Denied) b.Jeq(nr, "eperm", null);
        b.Jeq(t.Listen, "eacces", null);
        b.Jeq(t.Sendto, "flags3", null);
        b.Jeq(t.Sendmmsg, "flags3", null);
        b.Jeq(t.Sendmsg, "flags2", null);
        b.Jeq(t.Socket, "socket", null);
        b.Jeq(t.Socketpair, "socketpair", "allow");
        b.Label("flags3").Ld(40).Jset(MSG_FASTOPEN, "eacces", "allow");
        b.Label("flags2").Ld(32).Jset(MSG_FASTOPEN, "eacces", "allow");
        b.Label("socket").Ld(16).Jeq(AF_INET, "type", null).Jeq(AF_INET6, "type", "eacces");
        b.Label("type").Ld(24).And(~(SOCK_NONBLOCK | SOCK_CLOEXEC)).Jeq(SOCK_STREAM, "stream", datagrams ? null : "eacces");
        if (datagrams) b.Jeq(SOCK_DGRAM, null, "eacces").Ld(32).Jeq(0, "allow", null).Jeq(IPPROTO_UDP, "allow", "eacces");
        b.Label("stream").Ld(32).Jeq(0, "allow", null).Jeq(IPPROTO_TCP, "allow", "eacces");
        b.Label("socketpair").Ld(16).Jeq(AF_UNIX, null, "eacces");
        b.Ld(24).And(~(SOCK_NONBLOCK | SOCK_CLOEXEC)).Jeq(SOCK_STREAM, "allow", null).Jeq(SOCK_SEQPACKET, "allow", "eacces");
        b.Label("allow").Ret(SeccompRetAllow);
        b.Label("eacces").Ret(SeccompRetErrno | EACCES);
        b.Label("eperm").Ret(SeccompRetErrno | EPERM);
        b.Label("kill").Ret(SeccompRetKillProcess);
        return b.Assemble();
    }

    /// <summary>A tiny classic-BPF assembler: loads, equality, ≥ and bit-test jumps to labels, an AND, returns.</summary>
    sealed class Bpf
    {
        readonly List<(ushort Code, string? True, string? False, uint K)> _code = [];
        readonly Dictionary<string, int> _labels = [];

        public Bpf Label(string name) { _labels[name] = _code.Count; return this; }
        public Bpf Ld(uint offset) => Add(0x20, null, null, offset);                  // BPF_LD | BPF_W | BPF_ABS
        public Bpf Jeq(uint k, string? t, string? f) => Add(0x15, t, f, k);          // BPF_JMP | BPF_JEQ | BPF_K
        public Bpf Jge(uint k, string? t, string? f) => Add(0x35, t, f, k);          // BPF_JMP | BPF_JGE | BPF_K
        public Bpf Jset(uint k, string? t, string? f) => Add(0x45, t, f, k);         // BPF_JMP | BPF_JSET | BPF_K
        public Bpf And(uint k) => Add(0x54, null, null, k);                          // BPF_ALU | BPF_AND | BPF_K
        public Bpf Ret(uint k) => Add(0x06, null, null, k);                          // BPF_RET | BPF_K

        Bpf Add(ushort code, string? t, string? f, uint k) { _code.Add((code, t, f, k)); return this; }

        public byte[] Assemble()
        {
            var bytes = new byte[_code.Count * 8];
            for (var i = 0; i < _code.Count; i++)
            {
                var (code, t, f, k) = _code[i];
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 8), code);
                bytes[i * 8 + 2] = Offset(i, t);
                bytes[i * 8 + 3] = Offset(i, f);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8 + 4), k);
            }
            return bytes;
        }

        byte Offset(int at, string? label)
        {
            if (label is null) return 0;
            var jump = _labels[label] - (at + 1);
            if (jump is < 0 or > 255) throw new InvalidOperationException($"BPF jump to {label} out of range");
            return (byte)jump;
        }
    }

    /// <summary>A <c>struct sock_fprog</c> in unmanaged memory: u16 length, then (8-aligned) the pointer to the instructions.</summary>
    static nint Program(byte[] instructions)
    {
        var code = Marshal.AllocHGlobal(instructions.Length);
        Marshal.Copy(instructions, 0, code, instructions.Length);
        var prog = Zeroed(16);
        Marshal.WriteInt16(prog, 0, (short)(instructions.Length / 8));
        Marshal.WriteIntPtr(prog, 8, code);
        return prog;
    }

    static nint Utf8(string s) => Marshal.StringToCoTaskMemUTF8(s);

    /// <summary>A NULL-terminated array of C strings. Never freed: the process becomes another program or exits.</summary>
    static nint Vector(IReadOnlyList<string> items)
    {
        var v = Zeroed((items.Count + 1) * nint.Size);
        for (var i = 0; i < items.Count; i++) Marshal.WriteIntPtr(v, i * nint.Size, Utf8(items[i]));
        return v;
    }

    static nint Zeroed(int bytes)
    {
        var p = Marshal.AllocHGlobal(bytes);
        for (var i = 0; i < bytes; i++) Marshal.WriteByte(p, i, 0);
        return p;
    }

    static Failure Fail(string step) => new(step, Marshal.GetLastPInvokeError());
}
