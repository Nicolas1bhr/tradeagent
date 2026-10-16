using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TradeAgent.Security;

/// <summary>
/// WHICH PROCESS HOLDS THE OTHER END OF ONE LOOPBACK TCP CONNECTION (<c>U-thinking-seats-b</c>, <c>docs/ORCHESTRATION.md</c> § 2.7):
/// the question the broker's door asks of every connection it accepts, before a launch grant presented on it is worth anything.
///
/// <para>A Unix-domain socket carries its peer's credentials and a named pipe its client's pid; a TCP connection carries
/// neither (<c>SO_PEERCRED</c> does not apply to it). So the kernel's own table of connections is read instead: the row whose
/// LOCAL end is the client's endpoint and whose REMOTE end is the door's, and the one process that holds that socket —
/// Linux <c>/proc/net/tcp{,6}</c> for the socket's inode, then the <c>/proc/&lt;pid&gt;/fd</c> link that names it; macOS
/// libproc, every process's descriptors and <c>PROC_PIDFDSOCKETINFO</c> on each socket; Windows
/// <c>GetExtendedTcpTable(TCP_TABLE_OWNER_PID_CONNECTIONS)</c>, whose row names the owning pid.</para>
///
/// <para><b>Null at every step that does not answer</b>, and every caller reads null as a refusal: no row, a row another user
/// owns, a socket no readable process holds, a socket MORE than one process holds (which of them is "the peer" has no honest
/// answer), a process whose start cannot be read. The start is read as <see cref="PeerImage.Facts"/> reads it, so the caller
/// can refuse a pid the kernel handed on after the connection was accepted.</para>
///
/// <para>What it is NOT, said where the type is: a process of the same user that is handed the codex process's socket, or
/// ptraces it, is that process by every test this type can make — only a cell stops those (§ 2.8).</para>
/// </summary>
public static class TcpPeer
{
    /// <summary>
    /// The process holding the far end of the connection the door accepted on <paramref name="local"/> from
    /// <paramref name="remote"/>, with its start — or null when the kernel will not say exactly one.
    /// </summary>
    public static PeerFacts? OwnerOf(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        try
        {
            if (OperatingSystem.IsWindows())
                return Windows.OwnerPid(local, remote) is { } wpid && PeerImage.WindowsStart(wpid) is { } created
                    ? new PeerFacts(wpid, created, DateTimeOffset.UtcNow) { Source = "GetExtendedTcpTable" }
                    : null;

            var (pid, source) = OperatingSystem.IsLinux() ? (Linux.OwnerPid(local, remote), "/proc/net/tcp")
                : OperatingSystem.IsMacOS() ? (Darwin.OwnerPid(local, remote), "PROC_PIDFDSOCKETINFO")
                : (null, "");
            return pid is { } p && Core.ProcessTable.Read(p) is { Zombie: false } e
                ? new PeerFacts(p, e.Start, DateTimeOffset.UtcNow) { Source = source }
                : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Two endpoints are the same address and port, an IPv4-mapped IPv6 address read as the IPv4 it maps.</summary>
    static bool Same(IPAddress a, int aPort, IPEndPoint b) =>
        aPort == b.Port && Plain(a).Equals(Plain(b.Address));

    static IPAddress Plain(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    // ---- Linux ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// proc(5), <c>/proc/net/tcp</c>: one row per socket — <c>sl local_address rem_address st tx_queue:rx_queue tr:tm-&gt;when
    /// retrnsmt uid timeout inode</c>. An address is the kernel's in-memory word printed as hex in this machine's own byte
    /// order (four words for IPv6), a port its number in hex. The client's socket is the row whose local end is the client
    /// and whose remote end is the door; its owner is the one process with an <c>fd</c> link to <c>socket:[inode]</c>.
    /// </summary>
    [SupportedOSPlatform("linux")]
    static class Linux
    {
        internal static int? OwnerPid(IPEndPoint door, IPEndPoint client)
        {
            ulong? inode = null;
            foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
            {
                if (!File.Exists(table)) continue;
                foreach (var line in File.ReadLines(table).Skip(1))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 10) continue;
                    if (Endpoint(f[1]) is not { } l || Endpoint(f[2]) is not { } r) continue;
                    if (!Same(l.Address, l.Port, client) || !Same(r.Address, r.Port, door)) continue;
                    // ANOTHER USER'S SOCKET IS NEVER OURS TO SERVE: the row says whose it is, and only this user's is read further.
                    if (!uint.TryParse(f[7], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid != geteuid()) return null;
                    if (!ulong.TryParse(f[9], NumberStyles.None, CultureInfo.InvariantCulture, out var ino) || ino == 0) return null;
                    if (inode is not null && inode != ino) return null;   // two sockets claim the one 4-tuple: no honest answer
                    inode = ino;
                }
            }
            if (inode is not { } socket) return null;

            var link = $"socket:[{socket.ToString(CultureInfo.InvariantCulture)}]";
            int? owner = null;
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                string[] fds;
                try { fds = Directory.GetFiles(Path.Combine(dir, "fd")); }
                catch (Exception) { continue; }    // another user's process, or one that has exited: it is not this user's peer
                foreach (var fd in fds)
                {
                    string? target;
                    try { target = new FileInfo(fd).LinkTarget; }
                    catch (Exception) { continue; }
                    if (target != link) continue;
                    if (owner is not null && owner != pid) return null;   // held by two processes: which is the peer has no answer
                    owner = pid;
                    break;
                }
            }
            return owner;
        }

        /// <summary><c>0100007F:1F90</c> → 127.0.0.1:8080, the address's words written back in this machine's byte order.</summary>
        static IPEndPoint? Endpoint(string field)
        {
            var colon = field.IndexOf(':');
            if (colon <= 0) return null;
            var hex = field[..colon];
            if (!int.TryParse(field.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)) return null;
            if (hex.Length is not (8 or 32)) return null;
            var bytes = new byte[hex.Length / 2];
            for (var w = 0; w < hex.Length / 8; w++)
            {
                if (!uint.TryParse(hex.AsSpan(w * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word)) return null;
                if (BitConverter.IsLittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(w * 4), word);
                else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(w * 4), word);
            }
            return new IPEndPoint(new IPAddress(bytes), port);
        }

        [DllImport("libc")]
        static extern uint geteuid();
    }

    // ---- macOS ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// libproc: every pid (<c>proc_listallpids</c>), each one's descriptors (<c>proc_pidinfo(PROC_PIDLISTFDS)</c>), and for each
    /// socket <c>proc_pidfdinfo(PROC_PIDFDSOCKETINFO)</c> → <c>struct socket_fdinfo</c>. Every offset below was MEASURED with
    /// <c>offsetof</c> against MacOSX15.5.sdk's <c>sys/proc_info.h</c> on the build Mac (LP64; arm64 and x86_64 alike):
    /// <c>sizeof(struct socket_fdinfo)</c> 792, <c>psi</c> at 24; in <c>struct socket_info</c> <c>soi_kind</c> at 232 and
    /// <c>soi_proto</c> at 240, whose <c>pri_tcp.tcpsi_ini</c> is at 0; in <c>struct in_sockinfo</c> <c>insi_fport</c> 0,
    /// <c>insi_lport</c> 4, <c>insi_vflag</c> 24, <c>insi_faddr</c> 32, <c>insi_laddr</c> 48 (each a 16-byte <c>in4in6_addr</c>
    /// whose IPv4 word is at 12). <c>PROX_FDTYPE_SOCKET</c> 2, <c>SOCKINFO_TCP</c> 2, <c>INI_IPV4</c> 1, <c>INI_IPV6</c> 2.
    /// A port is kept in network order in the low 16 bits of an int.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class Darwin
    {
        const int PROC_PIDLISTFDS = 1, PROC_PIDFDSOCKETINFO = 3;
        const int FdInfoSize = 8, PROX_FDTYPE_SOCKET = 2;
        const int SocketFdInfoSize = 792, Kind = 24 + 232, In = 24 + 240;
        const int FPort = In + 0, LPort = In + 4, VFlag = In + 24, FAddr = In + 32, LAddr = In + 48;
        const int SOCKINFO_TCP = 2, INI_IPV4 = 1, INI_IPV6 = 2;

        internal static int? OwnerPid(IPEndPoint door, IPEndPoint client)
        {
            var count = proc_listallpids(null, 0);
            if (count <= 0) return null;
            var pids = new int[count + 64];
            count = proc_listallpids(pids, pids.Length * sizeof(int));
            if (count <= 0) return null;

            int? owner = null;
            var info = new byte[SocketFdInfoSize];
            foreach (var pid in pids.AsSpan(0, Math.Min(count, pids.Length)).ToArray())
            {
                if (pid <= 0) continue;
                var size = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, null, 0);
                if (size <= 0) continue;   // another user's process, or one that has exited: it is not this user's peer
                var fds = new byte[size + FdInfoSize * 32];
                size = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, fds, fds.Length);
                if (size <= 0) continue;
                for (var at = 0; at + FdInfoSize <= size; at += FdInfoSize)
                {
                    if (BitConverter.ToInt32(fds, at + 4) != PROX_FDTYPE_SOCKET) continue;
                    var fd = BitConverter.ToInt32(fds, at);
                    if (proc_pidfdinfo(pid, fd, PROC_PIDFDSOCKETINFO, info, SocketFdInfoSize) != SocketFdInfoSize) continue;
                    if (BitConverter.ToInt32(info, Kind) != SOCKINFO_TCP) continue;
                    if (Endpoint(info, LAddr, LPort) is not { } l || Endpoint(info, FAddr, FPort) is not { } r) continue;
                    if (!Same(l.Address, l.Port, client) || !Same(r.Address, r.Port, door)) continue;
                    if (owner is not null && owner != pid) return null;   // held by two processes: which is the peer has no answer
                    owner = pid;
                }
            }
            return owner;
        }

        static IPEndPoint? Endpoint(byte[] info, int addr, int port)
        {
            var p = BinaryPrimitives.ReadUInt16BigEndian(BitConverter.IsLittleEndian
                ? [info[port], info[port + 1]]
                : [info[port + 2], info[port + 3]]);
            var flag = info[VFlag];
            if ((flag & INI_IPV4) != 0) return new IPEndPoint(new IPAddress(info.AsSpan(addr + 12, 4)), p);
            if ((flag & INI_IPV6) != 0) return new IPEndPoint(new IPAddress(info.AsSpan(addr, 16)), p);
            return null;
        }

        [DllImport("libc", SetLastError = true)]
        static extern int proc_listallpids(int[]? buffer, int buffersize);

        [DllImport("libc", SetLastError = true)]
        static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[]? buffer, int buffersize);

        [DllImport("libc", SetLastError = true)]
        static extern int proc_pidfdinfo(int pid, int fd, int flavor, byte[] buffer, int buffersize);
    }

    // ---- Windows ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// iphlpapi <c>GetExtendedTcpTable</c> with <c>TCP_TABLE_OWNER_PID_CONNECTIONS</c> (4): a <c>DWORD dwNumEntries</c> and then
    /// <c>MIB_TCPROW_OWNER_PID</c> rows of six DWORDs — state, local address, local port, remote address, remote port, owning pid
    /// (tcpmib.h). An address is the IPv4 word in network order; a port is in network order in the low 16 bits. IPv4 only: the
    /// door listens on 127.0.0.1 and nothing else.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static class Windows
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_CONNECTIONS = 4, NO_ERROR = 0, ERROR_INSUFFICIENT_BUFFER = 122, Row = 24;

        internal static int? OwnerPid(IPEndPoint door, IPEndPoint client)
        {
            if (Plain(door.Address).AddressFamily != AddressFamily.InterNetwork) return null;
            var size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            for (var tries = 0; tries < 4 && result == ERROR_INSUFFICIENT_BUFFER; tries++)
            {
                size += Row * 16;
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    result = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                    if (result != NO_ERROR) continue;
                    var rows = Marshal.ReadInt32(buffer);
                    int? owner = null;
                    for (var i = 0; i < rows; i++)
                    {
                        var at = buffer + 4 + i * Row;
                        var l = new IPEndPoint(new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(at, 4))), Port(Marshal.ReadInt32(at, 8)));
                        var r = new IPEndPoint(new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(at, 12))), Port(Marshal.ReadInt32(at, 16)));
                        if (!Same(l.Address, l.Port, client) || !Same(r.Address, r.Port, door)) continue;
                        var pid = Marshal.ReadInt32(at, 20);
                        if (pid <= 0 || (owner is not null && owner != pid)) return null;
                        owner = pid;
                    }
                    return owner;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }

        static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern int GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort, int family,
            int tableClass, uint reserved);
    }
}
