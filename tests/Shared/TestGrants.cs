using System.Runtime.InteropServices;
using TradeAgent.Core;
using TradeAgent.Security;

namespace TradeAgent.Tests;

/// <summary>
/// A TEST'S LAUNCH GRANT, BOUND TO THE TEST PROCESS (<c>U-grant-tree</c>). In the product a grant is pending until the app
/// binds it to the container it made for that launch, and only a peer inside that container is served. A test that dials
/// the pipe from its own process — or from a <c>trade</c> child it started — stands in for the launch, so its grant is bound
/// to <see cref="TestProcessContainer"/>: this process and what descends from it. Every test that is not ABOUT the binding
/// issues through here and nowhere else, so the rule is the product's and only the container is the test's.
/// </summary>
public static class TestGrants
{
    /// <summary>Issues a grant in <paramref name="grants"/> and binds it to this test process. Throws if it would not bind.</summary>
    public static AgentGrant IssueHere(this AgentGrants grants, string role, string attemptId, TimeSpan? life = null)
    {
        var grant = grants.Issue(role, attemptId, life);
        if (!grants.Bind(grant.Token, TestProcessContainer.Instance))
            throw new InvalidOperationException("a grant issued a moment ago would not bind to the test process");
        return grant;
    }
}

/// <summary>
/// THE TEST PROCESS AND ITS DESCENDANTS, as a grant's container — for tests only. macOS and Linux ask the teardown's own
/// proof with this process as the leader; Windows walks the parent pids of a process snapshot, each parent older than its
/// child, up to this process.
/// </summary>
public sealed class TestProcessContainer : AttemptContainer
{
    public static TestProcessContainer Instance { get; } = new();

    readonly int _self = Environment.ProcessId;
    readonly ulong _start = OperatingSystem.IsWindows()
        ? PeerImage.WindowsStart(Environment.ProcessId) ?? 0
        : ProcessTable.Read(Environment.ProcessId)?.Start ?? 0;

    public override string Name => "the test process";

    public override MembershipVerdict Contains(PeerFacts peer)
    {
        if (_start == 0) return MembershipVerdict.Unknown("the test process's own start could not be read");
        if (!OperatingSystem.IsWindows())
            return TreeTeardown.IsMember(peer.Pid, peer.Start, _self, _start, 0)
                ? MembershipVerdict.Member
                : MembershipVerdict.Outside($"process {peer.Pid} is not the test process or descended from it");

        if (PeerImage.WindowsStart(peer.Pid) != peer.Start)
            return MembershipVerdict.Outside($"process {peer.Pid} is no longer the process that connected");
        var parents = WindowsParents();
        var pid = peer.Pid;
        var start = peer.Start;
        for (var hop = 0; hop < 64; hop++)
        {
            if (pid == _self) return start == _start ? MembershipVerdict.Member : MembershipVerdict.Outside("not the test process");
            if (!parents.TryGetValue(pid, out var parent) || parent <= 0) break;
            if (PeerImage.WindowsStart(parent) is not { } ps || ps > start) break;
            pid = parent;
            start = ps;
        }
        return MembershipVerdict.Outside($"process {peer.Pid} is not the test process or descended from it");
    }

    static Dictionary<int, int> WindowsParents()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref e)) return map;
            do map[(int)e.ProcessId] = (int)e.ParentProcessId;
            while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
