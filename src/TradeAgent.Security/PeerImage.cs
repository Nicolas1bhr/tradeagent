using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Security;

/// <summary>What a peer must be to hold a grant on the agent pipe. Null fields mean "not recorded".</summary>
/// <param name="ExpectedPath">TradeAgent's own <c>trade</c>, where this app deployed it.</param>
/// <param name="ExpectedHash">The SHA-256 of the CLI this app deployed, lowercase hex.</param>
/// <param name="ToolsDir">The managed AI-runtime folder. Nothing under it may ever be the peer.</param>
/// <param name="WorkspaceDir">The agent's own tree. Nothing under it may ever be the peer either.</param>
public sealed record PeerRule(string? ExpectedPath, string? ExpectedHash, string? ToolsDir, string? WorkspaceDir);

/// <summary>
/// WHO IS ON THE OTHER END OF THE AGENT PIPE, decided the way the ATAS bridge already decides it
/// (<c>BridgePipeAuth.ImageVerdict</c>): the kernel is asked which process holds the handle, and a
/// rule this codebase owns says whether that program may.
///
/// The point is the same one the bridge makes. A token is something a peer HAS, and everything on
/// the agent's side of the fence can read the agent's environment; the image is something a peer IS.
/// So the strong claim — "I am the Operations Director's launch" — is accepted only from TradeAgent's
/// own <c>trade</c>, at the path this app deployed it to, hashing to what this app deployed. A copy
/// of <c>trade.exe</c> the agent made in its workspace is a different program by this rule even
/// though it is byte-identical, because the agent can rewrite a file it owns and cannot rewrite one
/// under the app's bin.
///
/// The verdict is one function so it can be tested on any platform. The kernel is asked on all three
/// — Windows names the pipe's client process, macOS and Linux the peer of the accepted Unix-domain
/// socket — and wherever it will not say, the caller gets null, which the verdict refuses as "not
/// identified" rather than reading as "fine". The app configures a <see cref="PeerRule"/> on every
/// platform. Off Windows the verdict resolves the RECORDED folders through the file system, never
/// the kernel's answer (see <c>Same</c>).
/// </summary>
public static class PeerImage
{
    /// <summary>
    /// Null when a program at <paramref name="actualPath"/> may hold a grant on this pipe, and the
    /// reason it may not otherwise.
    ///
    /// The order matters and is deliberate. The two forbidden FOLDERS come first, ahead of the
    /// recorded path, because they are the rule that does not depend on a record: a runtime that had
    /// rewritten TradeAgent's record to name itself would satisfy "you are what we recorded" and
    /// still fail "you are not allowed to run from there".
    /// </summary>
    public static string? Verdict(string? actualPath, string? actualHash, PeerRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(actualPath))
            return "the operating system would not say which program is holding this connection, so " +
                   "the caller could not be identified at all";

        if (!string.IsNullOrWhiteSpace(rule.ToolsDir) && Inside(actualPath!, rule.ToolsDir!))
            return $"the program holding this connection ({Show(actualPath)}) runs from the managed " +
                   "AI-runtime folder. No AI runtime may present a launch grant";

        if (!string.IsNullOrWhiteSpace(rule.WorkspaceDir) && Inside(actualPath!, rule.WorkspaceDir!))
            return $"the program holding this connection ({Show(actualPath)}) runs from inside the " +
                   "agent's own workspace, which the agent can write to";

        if (string.IsNullOrWhiteSpace(rule.ExpectedPath))
            return "TradeAgent did not record where its own trade command is, so the caller could not be checked";

        if (!Same(actualPath!, rule.ExpectedPath!))
            return $"the program holding this connection is {Show(actualPath)}, but TradeAgent's own " +
                   $"trade command is {Show(rule.ExpectedPath)}";

        if (string.IsNullOrWhiteSpace(rule.ExpectedHash))
            return "TradeAgent did not record which trade command it installed, so the caller could not be checked";

        if (string.IsNullOrWhiteSpace(actualHash))
            return $"the program at {Show(actualPath)} could not be read back, so it could not be checked " +
                   "against the one TradeAgent installed";

        if (!string.Equals(actualHash, rule.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            return $"the program at {Show(actualPath)} is not the trade command TradeAgent installed";

        return null;
    }

    /// <summary>
    /// The image path of the process that dialled in, as the kernel reports it, on Windows, macOS and
    /// Linux alike. Null when the kernel will not say or the query fails — which the caller must read
    /// as "not identified", and refuse.
    /// </summary>
    public static string? ClientPath(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return UnixClientPath(pipe);
        try
        {
            return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? ImagePathOf(pid) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The same question on macOS and Linux, where .NET's named pipe is a Unix-domain socket and the
    /// handle of a connected server stream is the ACCEPTED socket's descriptor — the one the kernel
    /// keeps the peer's identity on. The process id comes off that socket and the image off that
    /// process; null at every step that does not answer, so a kernel that will not say is refused.
    ///
    /// What the pid is, said rather than implied: macOS answers with the last process to use the
    /// far end's socket, Linux with the process that called connect. So a process of the same user
    /// can still be named as a program it is not — connect, keep the socket in a child, exec the real
    /// trade — exactly as a Windows process can duplicate a pipe handle out of a real trade.exe: the
    /// same-user gap <c>U-contain-2</c> exists for, not one this closes.
    /// </summary>
    static string? UnixClientPath(NamedPipeServerStream pipe)
    {
        try
        {
            int? pid = null;
            var handle = pipe.SafePipeHandle;
            var held = false;
            try
            {
                handle.DangerousAddRef(ref held);
                var socket = (int)handle.DangerousGetHandle();
                if (OperatingSystem.IsMacOS()) pid = Darwin.PeerPid(socket);
                else if (OperatingSystem.IsLinux()) pid = LinuxPeer.PeerPid(socket);
            }
            finally { if (held) handle.DangerousRelease(); }

            if (pid is not { } p) return null;
            if (OperatingSystem.IsMacOS()) return Darwin.ImagePathOf(p);
            if (OperatingSystem.IsLinux()) return LinuxPeer.ImagePathOf(p);
            return null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The hash of a file on disk, or null when it cannot be read.
    ///
    /// Cached on (path, length, last write) because this runs once per connection and the file is a
    /// whole CLI. A swap changes at least one of the three, and a swap that somehow changed none of
    /// them still fails the PATH rule when it is anywhere but the app's own bin.
    /// </summary>
    public static string? HashOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new FileInfo(path!);
            if (!info.Exists) return null;
            var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

            lock (Cache)
                if (Cache.TryGetValue(key, out var known)) return known;

            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = Core.Sha256Hex.Of(stream);

            lock (Cache)
            {
                if (Cache.Count > 32) Cache.Clear();
                Cache[key] = hash;
            }
            return hash;
        }
        catch (Exception) { return null; }
    }

    static readonly Dictionary<string, string> Cache = [];

    [SupportedOSPlatform("windows")]
    static string? ImagePathOf(uint pid)
    {
        // PROCESS_QUERY_LIMITED_INFORMATION and QueryFullProcessImageName, the same pair the bridge
        // uses and for the same reason: reading another process's module list fails across a
        // bitness boundary, and this call does not care.
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            var size = (uint)buf.Length;
            return QueryFullProcessImageName(h, 0, buf, ref size) && size > 0
                ? new string(buf, 0, (int)size)
                : null;
        }
        finally { CloseHandle(h); }
    }

    static string Norm(string p) => p.Trim().Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Whether the program the kernel named is the one the rule recorded.
    ///
    /// Off Windows the kernel names a program by its RESOLVED path — no symbolic link in it, so on
    /// macOS a home under <c>$TMPDIR</c> (<c>/var/folders/…</c>) comes back as <c>/private/var/…</c> —
    /// while the rule holds the path the app was given. So the recorded path is also tried with its
    /// FOLDER resolved. Only the folder: a <c>trade</c> that is itself a link to somewhere else is a
    /// program somewhere else, and the kernel's own answer is never re-resolved, because a link made
    /// after the kernel answered could otherwise make any path read as the recorded one.
    /// </summary>
    static bool Same(string actual, string expected)
    {
        if (Equal(actual, expected)) return true;
        if (OperatingSystem.IsWindows()) return false;
        var folder = Path.GetDirectoryName(expected);
        return !string.IsNullOrEmpty(folder) && RealPath(folder) is { } real
               && Equal(actual, Path.Combine(real, Path.GetFileName(expected)));
    }

    /// <summary>
    /// Whether the program the kernel named runs from inside a forbidden folder — the folder as
    /// recorded, or as resolved, for the reason <see cref="Same"/> gives. A forbidden folder is
    /// resolved WHOLE: its own last component being a link changes nothing about what runs inside it.
    /// </summary>
    static bool Inside(string path, string dir) =>
        Under(path, dir) || (!OperatingSystem.IsWindows() && RealPath(dir) is { } real && Under(path, real));

    static bool Equal(string a, string b) =>
        string.Equals(Norm(a), Norm(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static bool Under(string path, string dir)
    {
        var d = Norm(dir);
        if (d.Length == 0) return false;
        var p = Norm(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.StartsWith(d + "/", cmp);
    }

    /// <summary>
    /// The path with every symbolic link in it resolved, or null when it cannot be (it does not
    /// exist, or this is Windows, where nothing here asks).
    ///
    /// realpath(3) into a caller's buffer, which both variants of it accept: macOS's man page asks for
    /// "a buffer capable of storing at least PATH_MAX characters" and Linux's stores "up to a maximum
    /// of PATH_MAX bytes". PATH_MAX is 1024 on macOS (sys/syslimits.h) and 4096 on Linux
    /// (include/uapi/linux/limits.h), so one buffer of 4096 covers both.
    /// </summary>
    static string? RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            var buffer = new byte[4096];
            if (realpath(path, buffer) == IntPtr.Zero) return null;
            var end = Array.IndexOf(buffer, (byte)0);
            return end > 0 ? Encoding.UTF8.GetString(buffer, 0, end) : null;
        }
        catch (Exception) { return null; }
    }

    [DllImport("libc", SetLastError = true)]
    static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] resolved);

    /// <summary>
    /// macOS: <c>getsockopt(SOL_LOCAL, LOCAL_PEERPID)</c> on the accepted socket, then
    /// <c>proc_pidpath</c>. Every constant and signature below is from the SDK's headers on the
    /// build Mac (MacOSX15.5.sdk) and agrees with Apple's open-source xnu (apple-oss-distributions/xnu).
    /// "libc" is libSystem on macOS, which re-exports libsystem_kernel, where both calls live.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class Darwin
    {
        /// <summary>sys/un.h: <c>#define SOL_LOCAL 0</c> — "Level number of get/setsockopt for local domain sockets".</summary>
        const int SOL_LOCAL = 0;

        /// <summary>sys/un.h: <c>#define LOCAL_PEERPID 0x002 /* retrieve peer pid */</c>.</summary>
        const int LOCAL_PEERPID = 0x002;

        /// <summary>
        /// sys/proc_info.h: <c>PROC_PIDPATHINFO_MAXSIZE (4*MAXPATHLEN)</c>, and MAXPATHLEN is PATH_MAX,
        /// 1024 (sys/param.h, sys/syslimits.h). proc_pidpath refuses a buffer smaller than
        /// PROC_PIDPATHINFO_SIZE or larger than this (xnu libsyscall/wrappers/libproc/libproc.c).
        /// </summary>
        const int PROC_PIDPATHINFO_MAXSIZE = 4 * 1024;

        /// <summary>
        /// The pid on the other end, or null. xnu answers LOCAL_PEERPID with the far socket's
        /// <c>last_pid</c>, or ENOTCONN once that end has gone (bsd/kern/uipc_usrreq.c).
        /// </summary>
        internal static int? PeerPid(int socket)
        {
            var length = (uint)sizeof(int);     // pid_t is __int32_t (sys/_types.h)
            return getsockopt(socket, SOL_LOCAL, LOCAL_PEERPID, out var pid, ref length) == 0
                   && length == sizeof(int) && pid > 0
                ? pid
                : null;
        }

        /// <summary>The image of a process, or null. proc_pidpath returns the path's length, 0 on failure.</summary>
        internal static string? ImagePathOf(int pid)
        {
            var buffer = new byte[PROC_PIDPATHINFO_MAXSIZE];
            var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
            return length > 0 && length <= buffer.Length ? Encoding.UTF8.GetString(buffer, 0, length) : null;
        }

        /// <summary>sys/socket.h: <c>int getsockopt(int, int, int, void * __restrict, socklen_t * __restrict)</c>; socklen_t is __uint32_t.</summary>
        [DllImport("libc", SetLastError = true)]
        static extern int getsockopt(int socket, int level, int option, out int value, ref uint length);

        /// <summary>libproc.h: <c>int proc_pidpath(int pid, void * buffer, uint32_t buffersize)</c>.</summary>
        [DllImport("libc", SetLastError = true)]
        static extern int proc_pidpath(int pid, byte[] buffer, uint buffersize);
    }

    /// <summary>
    /// Linux: <c>getsockopt(SOL_SOCKET, SO_PEERCRED)</c> on the accepted socket, then
    /// <c>/proc/&lt;pid&gt;/exe</c>. unix(7): SO_PEERCRED "returns the credentials of the peer process
    /// connected to this socket … those that were in effect at the time of the call to connect(2)",
    /// and these options "are specified with a SOL_SOCKET type even though they are AF_UNIX specific".
    /// </summary>
    [SupportedOSPlatform("linux")]
    static class LinuxPeer
    {
        /// <summary>
        /// include/uapi/asm-generic/socket.h: <c>#define SOL_SOCKET 1</c>, <c>#define SO_PEERCRED 17</c>.
        /// The GENERIC values, which x86, arm, arm64, riscv, loongarch and s390 use. powerpc, mips,
        /// alpha, sparc and parisc define their own (powerpc's SO_PEERCRED is 21, and 17 there is
        /// SO_SNDLOWAT), so on those the caller is not identified rather than identified wrongly.
        /// </summary>
        const int SOL_SOCKET = 1;
        const int SO_PEERCRED = 17;

        static bool GenericSocketNumbers => RuntimeInformation.ProcessArchitecture is
            Architecture.X64 or Architecture.X86 or Architecture.Arm64 or Architecture.Arm or Architecture.Armv6
            or Architecture.RiscV64 or Architecture.LoongArch64 or Architecture.S390x;

        /// <summary>The pid on the other end, or null — including a peer whose pid this namespace cannot see (0).</summary>
        internal static int? PeerPid(int socket)
        {
            if (!GenericSocketNumbers) return null;
            var length = (uint)Marshal.SizeOf<UCred>();
            return getsockopt(socket, SOL_SOCKET, SO_PEERCRED, out var cred, ref length) == 0
                   && length == Marshal.SizeOf<UCred>() && cred.Pid > 0
                ? cred.Pid
                : null;
        }

        /// <summary>
        /// proc(5): /proc/pid/exe is "a symbolic link containing the actual pathname of the executed
        /// command". An unlinked one reads back with " (deleted)" appended, which matches no rule and
        /// is refused; one this process may not read throws, and that is null too.
        /// </summary>
        internal static string? ImagePathOf(int pid) => new FileInfo($"/proc/{pid}/exe").LinkTarget;

        /// <summary>
        /// unix(7): <c>struct ucred { pid_t pid; uid_t uid; gid_t gid; }</c> — three 32-bit fields, as
        /// the kernel's own include/linux/socket.h spells them (<c>__u32</c> each).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        struct UCred
        {
            public int Pid;
            public uint Uid;
            public uint Gid;
        }

        /// <summary>getsockopt(2): <c>int getsockopt(int sockfd, int level, int optname, void *optval, socklen_t *optlen)</c>.</summary>
        [DllImport("libc", SetLastError = true)]
        static extern int getsockopt(int socket, int level, int option, out UCred value, ref uint length);
    }

    /// <summary>A path on its way to a refusal message: one line, printable, and short.</summary>
    static string Show(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "'<unknown>'"
            : "'" + new string(path!.Where(c => !char.IsControl(c)).Take(120).ToArray()).Trim() + "'";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
