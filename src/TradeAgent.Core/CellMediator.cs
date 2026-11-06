using System.Runtime.InteropServices;
using System.Text;

namespace TradeAgent.Core;

/// <summary>
/// THE ANSWER TO A CELL'S MODE AND TIME CHANGES (<c>U-cell-metadata</c>; R29 F-D1). Landlock has no right for them and seccomp cannot
/// read a path, so the filter stops the calls at the kernel (<c>SECCOMP_RET_USER_NOTIF</c>) and the launch's supervisor — outside the
/// cell, the cell's parent, there for its whole life — answers each one. It is the only code that changes anything on a cell's behalf,
/// so what it may do is small and fixed:
///
/// <list type="bullet">
/// <item>It never lets the cell's call run: a path can be swapped after any check. It resolves the object ITSELF, to a descriptor of
/// its own, judges what that descriptor leads to and acts on that descriptor, so what was judged is what is changed.</item>
/// <item>Only an object inside the workspace the ruleset granted, as the kernel names it. Anything else is EPERM: outside the grants,
/// and what a grant lets a cell only read — the kit, its inputs, its programs — which is what a read-only descriptor reaches.</item>
/// <item>Only a mode or a time, and only what the cell asked. Nothing else of the cell's reaches it: no verb, no pipe, no grant.</item>
/// <item>A magic link is refused (<c>RESOLVE_NO_MAGICLINKS</c>): <c>/proc/self</c> here is the supervisor, not the cell.</item>
/// <item>A failure is a refusal. If this thread or this process goes, the kernel fails every such call with ENOSYS: nothing is let through.</item>
/// </list>
///
/// <para><b>What a refusal costs.</b> The path is read from the caller's memory, which Yama's default lets a process do only to its
/// descendants: a process of the cell that has lost its parent line to the supervisor (a daemon whose parent exited) gets EPERM even in
/// the workspace. A descriptor-based call needs no memory and is not affected. A host at Yama scope 2 or 3 refuses them all, and the
/// proof's workspace row then fails rather than the cell quietly losing them.</para>
///
/// <para>Linux only: <see cref="Open"/> is called on Linux, and nothing here reaches for a symbol elsewhere.</para>
/// </summary>
public sealed class CellMediator
{
    /// <summary>The stage's flag naming the descriptor of its end of the channel: <c>trade --cell-exec --handoff &lt;fd&gt; &lt;policy-file&gt; &lt;program&gt;</c>.</summary>
    public const string HandoffFlag = "--handoff";

    const int AF_UNIX = 1, SOCK_SEQPACKET = 5, SOCK_CLOEXEC = 0x80000, F_SETFD = 2, SOL_SOCKET = 1, SCM_RIGHTS = 1, MSG_CMSG_CLOEXEC = 0x40000000;
    const int O_RDONLY = 0, O_CLOEXEC = 0x80000, O_PATH = 0x200000;
    const int AT_FDCWD = -100, AT_SYMLINK_NOFOLLOW = 0x100, AT_EMPTY_PATH = 0x1000;
    const ulong RESOLVE_NO_MAGICLINKS = 0x02;
    const long SysOpenat2 = 437;
    const int EPERM = 1, ENOENT = 2, EINTR = 4, EBADF = 9, EFAULT = 14, EINVAL = 22, ENAMETOOLONG = 36, ELOOP = 40, EOPNOTSUPP = 95;
    const int PathMax = 4096;

    /// <summary><c>SECCOMP_IOCTL_NOTIF_RECV</c>, <c>_SEND</c>, <c>_ID_VALID</c>: the same numbers on x86-64 and arm64.</summary>
    const ulong NotifRecv = 0xC0502100, NotifSend = 0xC0182101, NotifIdValid = 0x40082102;

    /// <summary><c>struct seccomp_notif</c>: u64 id, u32 pid, u32 flags, then <c>struct seccomp_data</c>: nr, arch, ip, six arguments.</summary>
    const int NotifSize = 80, NotifPid = 8, NotifData = 16, DataArgs = 16;

    /// <summary><c>struct seccomp_notif_resp</c>: u64 id, s64 val, s32 error, u32 flags.</summary>
    const int RespSize = 24;

    [DllImport("libc", SetLastError = true)]
    static extern int socketpair(int domain, int type, int protocol, nint sv);

    [DllImport("libc", SetLastError = true)]
    static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
    static extern nint sendmsg(int fd, nint msg, int flags);

    [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    static extern nint recvmsg(int fd, nint msg, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    static extern int ioctl(int fd, ulong request, nint arg);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    static extern long Openat2(long number, int dirfd, nint path, nint how, nuint size);

    [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
    static extern nint pread(int fd, nint buf, nuint count, long offset);

    [DllImport("libc", SetLastError = true)]
    static extern int fchmodat(int dirfd, string path, uint mode, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int utimensat(int dirfd, string path, nint times, int flags);

    /// <summary>A launch's channel: the supervisor's end, closed on exec, and the one the stage inherits.</summary>
    public sealed class Channel
    {
        internal Channel(int supervisor, int stage) => (Supervisor, Stage) = (supervisor, stage);

        internal int Supervisor { get; }
        public int Stage { get; }

        /// <summary>Called once the stage is started: lets go of its end and answers the cell on a thread of its own until this process ends.</summary>
        public void Serve()
        {
            close(Stage);
            new Thread(() => new CellMediator().Run(Supervisor)) { IsBackground = true, Name = "cell-mediator" }.Start();
        }
    }

    /// <summary>The channel of one launch, a pair of sockets whose messages arrive whole. Throws when the kernel gives none: no channel, no cell.</summary>
    public static Channel Open()
    {
        var pair = Marshal.AllocHGlobal(8);
        try
        {
            if (socketpair(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0, pair) != 0)
                throw new InvalidOperationException($"the cell's channel to its supervisor could not be made (errno {Marshal.GetLastPInvokeError()})");
            var (supervisor, stage) = (Marshal.ReadInt32(pair, 0), Marshal.ReadInt32(pair, 4));
            if (fcntl(stage, F_SETFD, 0) != 0)
                throw new InvalidOperationException($"the cell's channel could not be left open for the stage (errno {Marshal.GetLastPInvokeError()})");
            return new Channel(supervisor, stage);
        }
        finally { Marshal.FreeHGlobal(pair); }
    }

    /// <summary><c>struct msghdr</c> with one iovec and a control buffer, all in one block of unmanaged memory: 56 bytes of header, then 16 of iovec, then 24 of control.</summary>
    const int MsgIov = 16, MsgIovlen = 24, MsgControl = 32, MsgControllen = 40, MsgSize = 56, IovSize = 16, ControlSize = 24;

    static nint Message(nint data, int length)
    {
        var msg = Marshal.AllocHGlobal(MsgSize + IovSize + ControlSize);
        for (var i = 0; i < MsgSize + IovSize + ControlSize; i++) Marshal.WriteByte(msg, i, 0);
        var iov = msg + MsgSize;
        Marshal.WriteIntPtr(iov, 0, data);
        Marshal.WriteInt64(iov, 8, length);
        Marshal.WriteIntPtr(msg, MsgIov, iov);
        Marshal.WriteInt64(msg, MsgIovlen, 1);
        Marshal.WriteIntPtr(msg, MsgControl, iov + IovSize);
        Marshal.WriteInt64(msg, MsgControllen, ControlSize);
        return msg;
    }

    /// <summary>What the stage sends, made in unmanaged memory before the filter is in place: the granted workspace's real path, with room for the listener.</summary>
    internal static nint Handover(string workspace)
    {
        var bytes = Encoding.UTF8.GetBytes(workspace);
        var data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        var msg = Message(data, bytes.Length);
        var control = msg + MsgSize + IovSize;
        Marshal.WriteInt64(control, 0, 20);                  // CMSG_LEN(4)
        Marshal.WriteInt32(control, 8, SOL_SOCKET);
        Marshal.WriteInt32(control, 12, SCM_RIGHTS);
        return msg;
    }

    /// <summary>Sends the listener. The stage does not wait for the supervisor: a descriptor in flight stays open, and a cell's call waits for its answer.</summary>
    internal static int Send(int channel, int listener, nint handover)
    {
        Marshal.WriteInt32(handover + MsgSize + IovSize, 16, listener);
        return sendmsg(channel, handover, 0) < 0 ? -1 : 0;
    }

    /// <summary>The listener and the workspace the stage sent, or nothing when the stage ended without sending them.</summary>
    static (int Listener, string Workspace)? Receive(int channel)
    {
        var data = Marshal.AllocHGlobal(PathMax + 1);
        var msg = Message(data, PathMax + 1);
        try
        {
            nint n;
            do n = recvmsg(channel, msg, MSG_CMSG_CLOEXEC);
            while (n < 0 && Marshal.GetLastPInvokeError() == EINTR);
            if (n <= 0 || Marshal.ReadInt64(msg, MsgControllen) < 20) return null;
            var control = msg + MsgSize + IovSize;
            if (Marshal.ReadInt32(control, 8) != SOL_SOCKET || Marshal.ReadInt32(control, 12) != SCM_RIGHTS) return null;
            var bytes = new byte[(int)n];
            Marshal.Copy(data, bytes, 0, (int)n);
            return (Marshal.ReadInt32(control, 16), Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            Marshal.FreeHGlobal(msg);
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>
    /// The answering loop, one call at a time. A failure inside it is a refusal of that call; a failure of the listener ends the loop, and
    /// closing it makes the kernel fail every later call of the family.
    /// </summary>
    void Run(int channel)
    {
        var received = Receive(channel);
        close(channel);
        if (received is not { } r) return;
        var (notif, resp) = (Marshal.AllocHGlobal(NotifSize), Marshal.AllocHGlobal(RespSize));
        var arch = RuntimeInformation.ProcessArchitecture;
        try
        {
            while (true)
            {
                for (var i = 0; i < NotifSize; i++) Marshal.WriteByte(notif, i, 0);
                if (ioctl(r.Listener, NotifRecv, notif) != 0)
                {
                    if (Marshal.GetLastPInvokeError() is EINTR or ENOENT) continue;   // ENOENT: the caller went before this was read
                    break;
                }
                var id = Marshal.ReadInt64(notif, 0);
                var pid = Marshal.ReadInt32(notif, NotifPid);
                var nr = (uint)Marshal.ReadInt32(notif, NotifData);
                var args = new ulong[6];
                for (var i = 0; i < 6; i++) args[i] = (ulong)Marshal.ReadInt64(notif, NotifData + DataArgs + i * 8);
                int error;
                try { error = Decode(arch, nr, args) is { } call ? new Request(r.Listener, id, pid, call, r.Workspace).Answer() : EPERM; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { error = EPERM; }
                Marshal.WriteInt64(resp, 0, id);
                Marshal.WriteInt64(resp, 8, 0);
                Marshal.WriteInt32(resp, 16, -error);
                Marshal.WriteInt32(resp, 20, 0);
                ioctl(r.Listener, NotifSend, resp);   // ENOENT: the caller was killed while it was answered
            }
        }
        finally { close(r.Listener); }
    }

    /// <summary>How a call carries its times: <c>struct timespec[2]</c>, <c>struct timeval[2]</c>, <c>struct utimbuf</c>, or none (a mode).</summary>
    internal enum Times { None, Timespec, Timeval, Utimbuf }

    /// <summary>One mediated call in its one shape: which object, by descriptor or by path from a directory, and the mode or the times.</summary>
    internal sealed record Call(int Dirfd, ulong Path, bool ByDescriptor, uint Mode, Times Shape, ulong TimesAt, int Flags);

    /// <summary>
    /// THE MEDIATED FAMILY'S ARGUMENTS, BY NUMBER (<see cref="CellNative.TableFor"/>): what the kernel's own entry points read, and null for
    /// a number that is not of the family. <c>utimensat</c> with no path is <c>futimens</c>.
    /// </summary>
    internal static Call? Decode(Architecture arch, uint nr, IReadOnlyList<ulong> a)
    {
        static int Fd(ulong v) => unchecked((int)(uint)v);
        static Call Chmod(int dirfd, ulong path, ulong mode, int flags) => new(dirfd, path, false, (uint)mode, Times.None, 0, flags);
        static Call Stamp(int dirfd, ulong path, ulong times, Times shape, int flags) => new(dirfd, path, path == 0, 0, shape, times, flags);
        if (nr == CellNative.SysFchmodat2) return Chmod(Fd(a[0]), a[1], a[2], Fd(a[3]));
        return (arch, nr) switch
        {
            (Architecture.X64, 90) => Chmod(AT_FDCWD, a[0], a[1], 0),
            (Architecture.X64, 91) or (Architecture.Arm64, 52) => new(Fd(a[0]), 0, true, (uint)a[1], Times.None, 0, 0),
            (Architecture.X64, 268) or (Architecture.Arm64, 53) => Chmod(Fd(a[0]), a[1], a[2], 0),
            (Architecture.X64, 132) => Stamp(AT_FDCWD, a[0], a[1], Times.Utimbuf, 0),
            (Architecture.X64, 235) => Stamp(AT_FDCWD, a[0], a[1], Times.Timeval, 0),
            (Architecture.X64, 261) => Stamp(Fd(a[0]), a[1], a[2], Times.Timeval, 0),
            (Architecture.X64, 280) or (Architecture.Arm64, 88) => Stamp(Fd(a[0]), a[1], a[2], Times.Timespec, Fd(a[3])),
            _ => null
        };
    }

    /// <summary>
    /// WHAT THE KERNEL NAMES AN OPEN DESCRIPTOR, JUDGED: inside the granted workspace, by whole components. A file unlinked while open keeps
    /// the name it had with <c> (deleted)</c> after it, so what was the workspace's stays the cell's — a temporary file's mode, say —
    /// and a pipe, a socket and an anonymous file have no name there at all. One name is enough, because a cell cannot give what lies
    /// outside a second name inside: Landlock refuses a hard link from a place the ruleset grants no <c>REFER</c>.
    /// </summary>
    internal static bool Inside(string? target, string workspace) => target is not null && CellPolicy.Within(target, workspace, windows: false);

    /// <summary>One call being answered. Its descriptors live for the answer and no longer.</summary>
    sealed class Request(int listener, long id, int pid, Call call, string workspace)
    {
        int _mem = -1;

        /// <summary>The errno to answer with, 0 when the call was carried out.</summary>
        public int Answer()
        {
            if ((call.Flags & ~(AT_SYMLINK_NOFOLLOW | AT_EMPTY_PATH)) != 0) return EINVAL;
            if (call.ByDescriptor && (call.Flags & AT_SYMLINK_NOFOLLOW) != 0) return EINVAL;
            if (call.ByDescriptor && call.Dirfd == AT_FDCWD) return call.Shape == Times.None ? EBADF : EFAULT;
            int dir = -1, obj = -1;
            try
            {
                if (!call.ByDescriptor || call.TimesAt != 0) _mem = open($"/proc/{pid}/mem", O_RDONLY | O_CLOEXEC);
                // THE TASK IS STILL THE ONE THAT ASKED: a pid can be reused, and what was just opened under it counts only if this holds.
                if (!StillAsking()) return ENOENT;
                byte[]? path = null;
                if (!call.ByDescriptor)
                {
                    if (_mem < 0) return EPERM;
                    path = Text(call.Path, out var unreadable);
                    if (path is null) return unreadable;
                    if (path.Length == 1 && (call.Flags & AT_EMPTY_PATH) == 0) return ENOENT;
                }
                // AN ABSOLUTE PATH IGNORES ITS DIRECTORY, AS THE KERNEL DOES: an invalid one is no error then.
                var absolute = path is { Length: > 1 } && path[0] == (byte)'/';
                if (!absolute)
                {
                    dir = open(call.Dirfd == AT_FDCWD ? $"/proc/{pid}/cwd" : $"/proc/{pid}/fd/{call.Dirfd}", O_PATH | O_CLOEXEC);
                    if (dir < 0) return Marshal.GetLastPInvokeError() == ENOENT ? EBADF : EPERM;
                    if (!StillAsking()) return ENOENT;
                }
                if (path is not { Length: > 1 }) return Act(dir);
                obj = Resolve(absolute ? AT_FDCWD : dir, path);
                if (obj < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    return errno == ELOOP ? EPERM : errno;
                }
                return Act(obj);
            }
            finally
            {
                if (obj >= 0) close(obj);
                if (dir >= 0) close(dir);
                if (_mem >= 0) close(_mem);
            }
        }

        bool StillAsking()
        {
            var value = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(value, id);
                return ioctl(listener, NotifIdValid, value) == 0;
            }
            finally { Marshal.FreeHGlobal(value); }
        }

        /// <summary>The object itself, as a descriptor of this process. A path reaches it as the kernel would resolve it, but never through a magic link.</summary>
        int Resolve(int dir, byte[] path)
        {
            var nofollow = (call.Flags & AT_SYMLINK_NOFOLLOW) != 0;
            var (name, how) = (Marshal.AllocHGlobal(path.Length), Marshal.AllocHGlobal(24));
            try
            {
                Marshal.Copy(path, 0, name, path.Length);
                Marshal.WriteInt64(how, 0, O_PATH | O_CLOEXEC | (nofollow ? ONoFollow : 0));
                Marshal.WriteInt64(how, 8, 0);
                Marshal.WriteInt64(how, 16, (long)RESOLVE_NO_MAGICLINKS);
                return (int)Openat2(SysOpenat2, dir, name, how, 24);
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(how);
            }
        }

        /// <summary><c>O_NOFOLLOW</c>: 0400000 on x86-64, 0100000 on arm64.</summary>
        static int ONoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

        /// <summary>The judged descriptor, acted on by its own name: what was judged is what changes.</summary>
        int Act(int obj)
        {
            var self = $"/proc/self/fd/{obj}";
            string? target;
            try { target = new FileInfo(self).LinkTarget; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return EPERM; }
            if (!Inside(target, workspace)) return EPERM;
            if (call.Shape == Times.None)
            {
                // A MODE ON A SYMLINK ITSELF IS NOT A THING LINUX HAS: fchmodat2 with AT_SYMLINK_NOFOLLOW answers EOPNOTSUPP there.
                if ((call.Flags & AT_SYMLINK_NOFOLLOW) != 0 && new FileInfo(target!).LinkTarget is not null) return EOPNOTSUPP;
                return fchmodat(AT_FDCWD, self, call.Mode, 0) == 0 ? 0 : Marshal.GetLastPInvokeError();
            }
            var times = Stamps(out var unreadable);
            try
            {
                if (unreadable != 0) return unreadable;
                return utimensat(AT_FDCWD, self, times, 0) == 0 ? 0 : Marshal.GetLastPInvokeError();
            }
            finally { if (times != 0) Marshal.FreeHGlobal(times); }
        }

        /// <summary>The cell's times as the <c>struct timespec[2]</c> <c>utimensat</c> takes (zero when the call named none: now), or the errno.</summary>
        nint Stamps(out int unreadable)
        {
            unreadable = 0;
            if (call.TimesAt == 0) return 0;
            if (_mem < 0) { unreadable = EPERM; return 0; }
            var size = call.Shape == Times.Utimbuf ? 16 : 32;
            var raw = Marshal.AllocHGlobal(size);
            var spec = Marshal.AllocHGlobal(32);
            try
            {
                if (pread(_mem, raw, (nuint)size, unchecked((long)call.TimesAt)) != size) { unreadable = EFAULT; Marshal.FreeHGlobal(spec); return 0; }
                for (var i = 0; i < 2; i++)
                {
                    var (seconds, fraction) = call.Shape == Times.Utimbuf
                        ? (Marshal.ReadInt64(raw, i * 8), 0L)
                        : (Marshal.ReadInt64(raw, i * 16), Marshal.ReadInt64(raw, i * 16 + 8));
                    if (call.Shape == Times.Timeval)
                    {
                        if (fraction is < 0 or >= 1_000_000) { unreadable = EINVAL; Marshal.FreeHGlobal(spec); return 0; }
                        fraction *= 1000;
                    }
                    Marshal.WriteInt64(spec, i * 16, seconds);
                    Marshal.WriteInt64(spec, i * 16 + 8, fraction);
                }
                return spec;
            }
            finally { Marshal.FreeHGlobal(raw); }
        }

        /// <summary>A NUL-terminated path out of the caller's memory, NUL included; never decoded, because a file name need not be text.</summary>
        byte[]? Text(ulong address, out int unreadable)
        {
            unreadable = 0;
            var buffer = Marshal.AllocHGlobal(PathMax);
            try
            {
                var got = 0;
                while (got < PathMax)
                {
                    var n = (int)pread(_mem, buffer + got, (nuint)(PathMax - got), unchecked((long)address) + got);
                    if (n <= 0) { unreadable = EFAULT; return null; }
                    for (var i = got; i < got + n; i++)
                    {
                        if (Marshal.ReadByte(buffer, i) != 0) continue;
                        var path = new byte[i + 1];
                        Marshal.Copy(buffer, path, 0, i + 1);
                        return path;
                    }
                    got += n;
                }
                unreadable = ENAMETOOLONG;
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
