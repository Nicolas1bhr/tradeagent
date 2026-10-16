using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A fact about a cell policy's POSIX paths: run on macOS and Linux; skipped on Windows, where a cell is refused at the platform
/// (<see cref="CellPlatform.Refusal"/>, proved there by <see cref="CellLaunchTests"/>) and <c>/usr</c> is not an absolute path.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "a cell policy names POSIX paths; on Windows no cell starts, which CellLaunchTests proves";
    }
}

/// <summary>
/// THE CELL POLICY IS AN APP-OWNED FILE NAMED BY ITS HASH, AND A POLICY THAT WOULD GRANT THE APP'S OWN HOME IS REFUSED
/// (<c>U-cell-linux-a</c> item 1). Every OS: nothing here confines anything; it is the policy's form and its refusals.
/// </summary>
public class CellPolicyTests
{
    static string Dir(string name)
    {
        var d = Path.Combine(TestEnv.Home, "cellpolicy-" + name + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static CellPolicy Policy(string workspace, params string[] readOnly) =>
        new(workspace, readOnly, CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin"), new("HOME", workspace)], CellEgress.ForTests([8443]));

    [UnixFact]
    public void A_policy_file_is_named_by_its_hash_and_reads_back_whole()
    {
        var ws = Dir("ws");
        var policy = Policy(ws);
        var path = policy.Write(Dir("cells"));

        Assert.Matches("^" + policy.Hash + "\\.[0-9a-f]{16}\\.cell\\.json$", Path.GetFileName(path));
        Assert.NotEqual(path, policy.Write(Path.GetDirectoryName(path)));     // one file per launch: the launcher removes only its own
        var back = CellPolicy.Read(path);
        Assert.Equal(policy.Hash, back.Hash);
        Assert.Equal(ws, back.Workspace);
        Assert.Equal(["HOME", "PATH"], back.Environment.Select(e => e.Key));
        Assert.Equal([8443], back.TcpPorts);
        Assert.Equal("test", back.Egress.Name);
    }

    [UnixFact]
    public void A_policy_file_changed_after_it_was_written_is_refused()
    {
        var path = Policy(Dir("ws")).Write(Dir("cells"));
        File.WriteAllText(path, File.ReadAllText(path).Replace("8443", "8444"));

        var ex = Assert.Throws<TradeAgentException>(() => CellPolicy.Read(path));
        Assert.Equal(ErrorCode.CELL_UNAVAILABLE, ex.Code);
        Assert.Contains("does not hash to its name", ex.Message);
    }

    /// <summary>
    /// ONE TABLE OF EGRESS CLASSES (<c>U-cell-linux-d</c> item 2): <c>none</c> is the default and grants no port and no datagram;
    /// <c>web</c> and <c>web-plain</c> grant 443 (and 80) plus item 1's name resolution — DNS over TCP: port 53, glibc's use-vc, the
    /// resolver's configuration readable, datagrams still denied. Each class's profile line says what UDP and DNS are.
    /// </summary>
    [UnixFact]
    public void Egress_classes_are_one_table_and_none_is_the_default()
    {
        var ws = Dir("ws");
        var none = new CellPolicy(ws, [], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin")]);
        Assert.Same(CellEgress.None, none.Egress);
        Assert.Empty(none.TcpPorts);
        Assert.Equal(["PATH"], none.Environment.Select(e => e.Key));
        Assert.Empty(none.ReadOnly);

        Assert.Equal(["none", "web", "web-plain"], CellEgress.Table.Select(c => c.Name));
        Assert.Equal(CellDns.Tcp, CellEgress.Decided);
        Assert.Equal([53, 443], CellEgress.Web.TcpPorts);
        Assert.Equal([53, 80, 443], CellEgress.WebPlain.TcpPorts);
        Assert.All(CellEgress.Table, c => Assert.False(c.DatagramsOpen));

        var web = new CellPolicy(ws, [], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin")], CellEgress.Web);
        Assert.Equal([53, 443], web.TcpPorts);
        Assert.Contains(new KeyValuePair<string, string>("RES_OPTIONS", "use-vc"), web.Environment);
        Assert.Equal(CellNative.RealPath("/etc/resolv.conf") is { } real ? [real] : [], web.ReadOnly);

        Assert.Contains("UDP and every other datagram DENIED", CellEgress.None.Profile);
        Assert.Contains("DNS DENIED", CellEgress.None.Profile);
        Assert.Contains("UDP DENIED (seccomp, hard)", CellEgress.Web.Profile);
        Assert.Contains("DNS OVER TCP — port 53 to ANY host (labelled)", CellEgress.Web.Profile);
        Assert.Contains("port-only: a recorded gap", CellEgress.WebPlain.Profile);

        // A CALLER'S OWN RES_OPTIONS BESIDE THE CLASS'S IS NOT GUESSED BETWEEN: refused, naming what the class sets.
        var clash = new CellPolicy(ws, [], CellPolicy.SystemRoots, [new("RES_OPTIONS", "ndots:5")], CellEgress.Web);
        Assert.Contains("names one variable twice", clash.Problem());
        Assert.Contains("RES_OPTIONS=use-vc", clash.Problem());
    }

    /// <summary>
    /// THE EFFECTIVE PORT SET IS IN THE HASH, AND THE CLASS IS READ BACK ONLY AS THE TABLE HAS IT: two policies differing only in
    /// class hash differently; a file whose class name is in the table with other ports is refused, not read as that class.
    /// </summary>
    [UnixFact]
    public void The_egress_class_is_in_the_hash_and_a_file_naming_another_shape_is_refused()
    {
        var ws = Dir("ws");
        CellPolicy With(CellEgress e) => new(ws, [], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin")], e);
        Assert.Equal(3, new[] { With(CellEgress.None).Hash, With(CellEgress.Web).Hash, With(CellEgress.WebPlain).Hash }.Distinct().Count());
        Assert.Contains("\"TcpPorts\":[53,443]", With(CellEgress.Web).Serialize());

        var cells = Dir("cells");
        var back = CellPolicy.Read(With(CellEgress.WebPlain).Write(cells));
        Assert.Same(CellEgress.WebPlain, back.Egress);
        Assert.Equal([53, 80, 443], back.TcpPorts);

        // "web" claiming port 22: the bytes are re-hashed and renamed so only the class check can refuse them.
        var forged = With(CellEgress.Web).Serialize().Replace("\"Ports\":[443]", "\"Ports\":[22]").Replace("[53,443]", "[22,53]");
        var forgedPath = Path.Combine(cells, Sha256Hex.Of(forged) + ".0123456789abcdef.cell.json");
        File.WriteAllText(forgedPath, forged);
        var ex = Assert.Throws<TradeAgentException>(() => CellPolicy.Read(forgedPath));
        Assert.Contains("names egress class 'web' with ports [22]", ex.Message);
    }

    /// <summary>
    /// FORM 1 IS REFUSED, NAMING BOTH FORMS (no backward compatibility while building, refuse never guess): <c>U-cell-linux-a</c>'s
    /// file with its raw port list, named by its own hash, is not read as a class-less policy.
    /// </summary>
    [UnixFact]
    public void A_form_1_policy_file_is_refused_naming_both_forms()
    {
        var ws = Dir("ws");
        var v1 = "{\"V\":1,\"Workspace\":" + System.Text.Json.JsonSerializer.Serialize(ws) +
                 ",\"ReadOnly\":[],\"Executable\":[\"/usr\"],\"Environment\":[[\"PATH\",\"/usr/bin\"]],\"TcpPorts\":[443]}";
        var path = Path.Combine(Dir("cells"), Sha256Hex.Of(v1) + ".0123456789abcdef.cell.json");
        File.WriteAllText(path, v1);

        var ex = Assert.Throws<TradeAgentException>(() => CellPolicy.Read(path));
        Assert.Equal(ErrorCode.CELL_UNAVAILABLE, ex.Code);
        Assert.Contains("is form 1; this build reads form 2 only", ex.Message);
    }

    /// <summary>A cell's tools keep their temporary files and caches in its workspace and nowhere else (<c>U-cell-linux-d</c> item 4).</summary>
    [UnixFact]
    public void A_cells_tools_keep_their_temp_files_and_caches_in_its_workspace()
    {
        var ws = Dir("ws");
        var env = CellPolicy.ToolEnvironment(ws).ToDictionary(e => e.Key, e => e.Value);
        Assert.Equal(ws, env["TMPDIR"]);
        foreach (var name in new[] { "XDG_CACHE_HOME", "PIP_CACHE_DIR", "npm_config_cache" })
            Assert.StartsWith(ws + "/", env[name]);
        Assert.Null(new CellPolicy(ws, [], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin"), .. CellPolicy.ToolEnvironment(ws)]).Problem());
    }

    [UnixFact]
    public void A_policy_that_grants_the_app_home_or_its_state_is_refused()
    {
        var ws = Dir("ws");
        Assert.Null(Policy(ws).Problem());

        Assert.Contains("TradeAgent's own home", Policy(ws, Path.GetDirectoryName(Paths.Home)!).Problem());
        Assert.Contains("TradeAgent's own home", Policy(ws, "/").Problem());
        Assert.Contains("state", Policy(ws, Paths.State).Problem());
        Assert.Contains("state", Policy(ws, Path.Combine(Paths.State, "data")).Problem());
        Assert.Contains("not an absolute path", Policy(ws, "relative/in").Problem());
        Assert.Throws<TradeAgentException>(() => Policy(Paths.Home).Write(Dir("cells")));
    }

    /// <summary>
    /// THE OWNER'S INBOX IS NO CELL'S (<c>U-cell-linux-d</c> item 5, Q5's evidence; COUNCIL rule 7): a policy naming the inbox, a path
    /// inside it, or the workspace tree that holds it — as the workspace or as an input — is refused in words naming the inbox; the
    /// agent's own tree beside it is not.
    /// </summary>
    [UnixFact]
    public void A_policy_that_grants_the_owners_inbox_or_its_parent_is_refused()
    {
        var ws = Dir("ws");
        var inside = Path.Combine(Paths.Inbox, "drop-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(inside);
        foreach (var why in new[]
        {
            Policy(Paths.Inbox).Problem(), Policy(Paths.Workspace).Problem(), Policy(inside).Problem(),
            Policy(ws, Paths.Inbox).Problem(), Policy(ws, Paths.Workspace).Problem(), Policy(ws, inside).Problem(),
        })
            Assert.Contains("the owner's inbox", why);

        var agent = Path.Combine(Paths.AgentHome, "cell-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(agent);
        Assert.Null(Policy(agent).Problem());
    }

    /// <summary>
    /// THE PROTECTED SET IS JUDGED WHERE IT REALLY IS (triage #3): with the app's home reached through a symlink, a grant naming the
    /// REAL <c>state/</c>, the real home or its real cell folder is refused — not passed because its spelling differs from the home's.
    /// </summary>
    [UnixFact]
    public void A_grant_naming_the_real_state_of_a_home_reached_through_a_symlink_is_refused()
    {
        var real = Dir("realhome");
        Directory.CreateDirectory(Path.Combine(real, "state", "data"));
        Directory.CreateDirectory(Path.Combine(real, "cells"));
        var home = Path.Combine(TestEnv.Home, "homelink-" + Guid.NewGuid().ToString("n")[..8]);
        File.CreateSymbolicLink(home, real);
        var realHome = CellNative.RealPath(real)!;
        var ws = Dir("ws");

        Assert.Null(Policy(ws).Problem(home));
        Assert.Contains("lies inside TradeAgent's state", Policy(ws, Path.Combine(realHome, "state")).Problem(home));
        Assert.Contains("lies inside TradeAgent's state", Policy(ws, Path.Combine(realHome, "state", "data")).Problem(home));
        Assert.Contains("lies inside TradeAgent's state", Policy(ws, Path.Combine(realHome, "cells")).Problem(home));
        Assert.Contains("TradeAgent's own home", Policy(ws, realHome).Problem(home));
    }

    /// <summary>
    /// A READ-ONLY INPUT MAY NOT OVERLAP THE WORKSPACE (triage #4): Landlock's rights accumulate along a path, so an input inside the
    /// read-write workspace would stay writable. Inside it, equal to it, containing it, or reaching into it by a symlink: refused in words.
    /// </summary>
    [UnixFact]
    public void A_read_only_input_inside_equal_to_or_containing_the_workspace_is_refused()
    {
        var outer = Dir("overlap");
        var ws = Path.Combine(outer, "ws");
        var input = Path.Combine(ws, "input");
        Directory.CreateDirectory(input);
        var link = Path.Combine(Dir("elsewhere"), "input-link");
        File.CreateSymbolicLink(link, input);

        foreach (var ro in new[] { input, ws, outer, link })
        {
            var why = Policy(ws, ro).Problem();
            Assert.Contains("read-only input", why);
            Assert.Contains("workspace", why);
        }
        Assert.Null(Policy(ws, Dir("apart")).Problem());
    }
}

/// <summary>A fact about the Linux kernel itself (Landlock): run on Linux; skipped elsewhere, where no cell starts.</summary>
public sealed class LinuxKernelFactAttribute : FactAttribute
{
    public LinuxKernelFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "builds a Landlock ruleset: Linux only (CI's ubuntu job runs it)";
    }
}

/// <summary>
/// A GRANT IS THE DESCRIPTOR THAT WAS CHECKED (triage #2): the ruleset opens each path first and judges where that descriptor really
/// leads, so a symlink retargeted after the policy's check — by another cell sharing the workspace — cannot grant <c>state/</c>.
/// </summary>
public class CellGrantTests
{
    /// <summary>"App-owned" includes the owner (triage #7): a file of another account — root's <c>/etc/passwd</c> — is not this account's.</summary>
    [LinuxKernelFact]
    public void A_file_of_another_account_is_not_this_accounts_own()
    {
        Assert.NotEqual("root", Environment.UserName);
        var mine = Path.Combine(TestEnv.Home, "mine-" + Guid.NewGuid().ToString("n")[..8]);
        File.WriteAllText(mine, "x");
        Assert.Null(CellNative.NotOwnedByThisAccount(mine));
        Assert.Contains("belongs to uid 0, not to this account", CellNative.NotOwnedByThisAccount("/etc/passwd"));
    }

    [LinuxKernelFact]
    public void A_grant_retargeted_to_state_after_the_policy_was_checked_is_refused()
    {
        var root = Path.Combine(TestEnv.Home, "cellgrant-" + Guid.NewGuid().ToString("n")[..8]);
        var ws = Path.Combine(root, "ws");
        var shared = Path.Combine(root, "shared");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(Paths.State);
        var link = Path.Combine(root, "input");
        File.CreateSymbolicLink(link, shared);
        var policy = new CellPolicy(ws, [link], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin")]);
        Assert.Null(policy.Problem());

        CellNative.BeforeGrantOpen = p =>
        {
            if (p != link) return;
            File.Delete(link);
            File.CreateSymbolicLink(link, Paths.State);
        };
        int fd;
        CellNative.Failure? failure;
        try { fd = CellNative.Ruleset(policy, out failure); }
        finally { CellNative.BeforeGrantOpen = null; }

        Assert.True(fd < 0, $"a Landlock ruleset was built granting {link}, which by then led to {Paths.State}");
        Assert.Contains("lies inside TradeAgent's state", failure!.Sentence);
    }

    /// <summary>The overlap rule on descriptors too: a workspace symlink retargeted onto a read-only input after the check is refused (re-review).</summary>
    [LinuxKernelFact]
    public void A_workspace_retargeted_onto_a_read_only_input_after_the_check_is_refused()
    {
        var root = Path.Combine(TestEnv.Home, "cellgrant-ws-" + Guid.NewGuid().ToString("n")[..8]);
        var realWs = Path.Combine(root, "realws");
        var ro = Path.Combine(root, "ro");
        Directory.CreateDirectory(realWs);
        Directory.CreateDirectory(ro);
        var ws = Path.Combine(root, "ws");
        File.CreateSymbolicLink(ws, realWs);
        var policy = new CellPolicy(ws, [ro], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin")]);
        Assert.Null(policy.Problem());

        CellNative.BeforeGrantOpen = p =>
        {
            if (p != ws) return;
            File.Delete(ws);
            File.CreateSymbolicLink(ws, ro);
        };
        int fd;
        CellNative.Failure? failure;
        try { fd = CellNative.Ruleset(policy, out failure); }
        finally { CellNative.BeforeGrantOpen = null; }

        Assert.True(fd < 0, $"a Landlock ruleset was built with the workspace {ws} leading onto the read-only input {ro}");
        Assert.Contains("would not stay read-only", failure!.Sentence);
    }
}

/// <summary>
/// THE SECCOMP FILTER'S DECISIONS, READ BY A CLASSIC-BPF INTERPRETER (<c>U-cell-linux-a</c> item 2), on every OS: the program the cell
/// stage installs, run over the <c>seccomp_data</c> the kernel would hand it. The kernel's own verdicts are <c>CellLinuxTests</c>' on Linux.
/// </summary>
public class CellSeccompTests
{
    const uint Allow = 0x7fff0000, Eacces = 0x00050000 | 13, Eperm = 0x00050000 | 1, Kill = 0x80000000;
    const uint X64 = 0xC000003E, Arm64 = 0xC00000B7, I386 = 0x40000003;

    static uint Run(System.Runtime.InteropServices.Architecture arch, uint auditArch, uint nr, params ulong[] args) =>
        RunProgram(CellNative.SeccompProgram(arch), auditArch, nr, args);

    static uint RunProgram(byte[] code, uint auditArch, uint nr, params ulong[] args)
    {
        var data = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data, nr);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), auditArch);
        for (var i = 0; i < args.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16 + 8 * i), args[i]);
        uint a = 0;
        for (var pc = 0; pc < code.Length / 8;)
        {
            var op = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(pc * 8));
            var (jt, jf) = (code[pc * 8 + 2], code[pc * 8 + 3]);
            var k = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(pc * 8 + 4));
            pc++;
            switch (op)
            {
                case 0x20: a = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)k)); break;
                case 0x15: pc += a == k ? jt : jf; break;
                case 0x35: pc += a >= k ? jt : jf; break;
                case 0x45: pc += (a & k) != 0 ? jt : jf; break;
                case 0x54: a &= k; break;
                case 0x06: return k;
                default: throw new InvalidOperationException($"opcode {op:x}");
            }
        }
        throw new InvalidOperationException("the program fell off its end");
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, X64, 41u, 53u, 425u, 250u, 321u, 298u, 323u)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, Arm64, 198u, 199u, 425u, 219u, 280u, 241u, 282u)]
    public void Socket_is_tcp_over_ip_only_and_the_kernel_side_doors_are_shut(System.Runtime.InteropServices.Architecture arch, uint audit,
        uint socket, uint socketpair, uint ioUringSetup, uint keyctl, uint bpf, uint perf, uint userfaultfd)
    {
        const ulong unix = 1, inet = 2, inet6 = 10, netlink = 16, packet = 17, vsock = 40;
        const ulong stream = 1, dgram = 2, raw = 3, flags = 0x800 | 0x80000;

        Assert.Equal(Allow, Run(arch, audit, socket, inet, stream, 0));
        Assert.Equal(Allow, Run(arch, audit, socket, inet6, stream | flags, 6));
        Assert.Equal(Eacces, Run(arch, audit, socket, unix, stream, 0));
        Assert.Equal(Eacces, Run(arch, audit, socket, unix, dgram | flags, 0));
        Assert.Equal(Eacces, Run(arch, audit, socket, inet, dgram, 0));          // UDP
        Assert.Equal(Eacces, Run(arch, audit, socket, inet, dgram, 1));          // ICMP ping socket
        Assert.Equal(Eacces, Run(arch, audit, socket, inet, stream, 132));       // SCTP over a stream socket: not TCP, not Landlock's
        Assert.Equal(Eacces, Run(arch, audit, socket, inet, raw, 0));
        Assert.Equal(Eacces, Run(arch, audit, socket, netlink, raw, 0));
        Assert.Equal(Eacces, Run(arch, audit, socket, packet, raw, 0));
        Assert.Equal(Eacces, Run(arch, audit, socket, vsock, stream, 0));
        Assert.Equal(Allow, Run(arch, audit, socketpair, unix, stream, 0));
        foreach (var nr in new[] { ioUringSetup, ioUringSetup + 1, ioUringSetup + 2, keyctl, bpf, perf, userfaultfd })
            Assert.Equal(Eperm, Run(arch, audit, nr));
        Assert.Equal(Allow, Run(arch, audit, 0));
        Assert.Equal(Kill, Run(arch, I386, socket, inet, stream, 0));
    }

    /// <summary>
    /// <c>socketpair()</c> IS A UNIX STREAM OR SEQPACKET PAIR ONLY (triage #1): a datagram pair end could be re-connected or
    /// <c>sendto</c>'d to any pathname datagram listener, so it is EACCES, flags masked as for <c>socket()</c>.
    /// </summary>
    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, X64, 53u)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, Arm64, 199u)]
    public void Socketpair_is_a_unix_stream_or_seqpacket_pair_only(System.Runtime.InteropServices.Architecture arch, uint audit, uint socketpair)
    {
        const ulong unix = 1, inet = 2, stream = 1, dgram = 2, raw = 3, seqpacket = 5, flags = 0x800 | 0x80000;

        Assert.Equal(Allow, Run(arch, audit, socketpair, unix, stream, 0));
        Assert.Equal(Allow, Run(arch, audit, socketpair, unix, stream | flags, 0));
        Assert.Equal(Allow, Run(arch, audit, socketpair, unix, seqpacket, 0));
        Assert.Equal(Allow, Run(arch, audit, socketpair, unix, seqpacket | flags, 0));
        Assert.Equal(Eacces, Run(arch, audit, socketpair, unix, dgram, 0));
        Assert.Equal(Eacces, Run(arch, audit, socketpair, unix, dgram | flags, 0));
        Assert.Equal(Eacces, Run(arch, audit, socketpair, unix, raw, 0));
        Assert.Equal(Eacces, Run(arch, audit, socketpair, inet, stream, 0));
    }

    /// <summary>From Landlock ABI 9 the ruleset also handles RESOLVE_UNIX (bit 16), so a pathname socket outside the workspace cannot be looked up; below it, never asked for.</summary>
    [Fact]
    public void The_ruleset_handles_resolve_unix_from_abi_9_only()
    {
        Assert.Equal(0xFFFFUL, CellNative.HandledFs(6));
        Assert.Equal(0xFFFFUL, CellNative.HandledFs(8));
        Assert.Equal(0x1FFFFUL, CellNative.HandledFs(9));
        Assert.Equal(0x1FFFFUL, CellNative.HandledFs(10));
    }

    /// <summary>
    /// THE TWO TCP DOORS LANDLOCK DOES NOT SEE ARE SHUT (<c>U-cell-linux-d</c>): <c>listen()</c> — which picks an ephemeral port on a
    /// socket nobody bound — is EACCES; a send carrying <c>MSG_FASTOPEN</c>, which connects without <c>connect()</c>, is EACCES on
    /// <c>sendto</c>, <c>sendmsg</c> and <c>sendmmsg</c>, wherever the flag sits among the others; a plain send passes.
    /// </summary>
    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, X64, 50u, 44u, 46u, 307u)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, Arm64, 201u, 206u, 211u, 269u)]
    public void Listen_and_a_fast_open_send_are_refused(System.Runtime.InteropServices.Architecture arch, uint audit,
        uint listen, uint sendto, uint sendmsg, uint sendmmsg)
    {
        const ulong fastOpen = 0x20000000, noSignal = 0x4000, dontWait = 0x40;

        Assert.Equal(Eacces, Run(arch, audit, listen, 3, 128));
        Assert.Equal(Eacces, Run(arch, audit, sendto, 3, 0, 1, fastOpen, 0, 16));
        Assert.Equal(Eacces, Run(arch, audit, sendto, 3, 0, 1, fastOpen | noSignal | dontWait, 0, 16));
        Assert.Equal(Eacces, Run(arch, audit, sendmsg, 3, 0, fastOpen | noSignal));
        Assert.Equal(Eacces, Run(arch, audit, sendmmsg, 3, 0, 1, fastOpen));
        Assert.Equal(Allow, Run(arch, audit, sendto, 3, 0, 1, noSignal | dontWait, 0, 16));
        Assert.Equal(Allow, Run(arch, audit, sendmsg, 3, 0, noSignal));
        Assert.Equal(Allow, Run(arch, audit, sendmmsg, 3, 0, 1, 0));
    }

    /// <summary>
    /// DATAGRAMS ARE THE PROBE'S OPTION ONLY (<c>U-cell-linux-d</c> item 1, mode B): with it an IPv4 or IPv6 UDP socket opens; ICMP,
    /// raw and every other family stay EACCES; without it — every class of the table — UDP is EACCES as before.
    /// </summary>
    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, X64, 41u)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, Arm64, 198u)]
    public void Datagrams_open_only_with_the_probes_option(System.Runtime.InteropServices.Architecture arch, uint audit, uint socket)
    {
        const ulong unix = 1, inet = 2, inet6 = 10, netlink = 16, stream = 1, dgram = 2, raw = 3, flags = 0x800 | 0x80000;
        var open = CellNative.SeccompProgram(arch, datagrams: true);

        Assert.Equal(Allow, RunProgram(open, audit, socket, inet, dgram, 0));
        Assert.Equal(Allow, RunProgram(open, audit, socket, inet6, dgram | flags, 17));
        Assert.Equal(Allow, RunProgram(open, audit, socket, inet, stream, 6));
        Assert.Equal(Eacces, RunProgram(open, audit, socket, inet, dgram, 1));      // ICMP ping socket
        Assert.Equal(Eacces, RunProgram(open, audit, socket, inet, raw, 17));
        Assert.Equal(Eacces, RunProgram(open, audit, socket, unix, dgram, 0));
        Assert.Equal(Eacces, RunProgram(open, audit, socket, netlink, raw, 0));
        Assert.Equal(Eacces, Run(arch, audit, socket, inet, dgram, 0));
        Assert.Equal(Eacces, Run(arch, audit, socket, inet6, dgram, 17));
        Assert.DoesNotContain(CellEgress.Table, c => c.DatagramsOpen);
    }

    [Fact]
    public void An_x32_system_call_is_refused_on_x64()
    {
        Assert.Equal(Eperm, Run(System.Runtime.InteropServices.Architecture.X64, X64, 0x40000000u + 41, 2, 1, 0));
    }
}

/// <summary>
/// A CELL LAUNCH IS A CELL OR NOTHING (<c>U-cell-linux-a</c> item 4): <see cref="TradeAgent.AgentRuntime.ProcessContainment"/>'s cell
/// overload refuses a platform below Landlock ABI 6 in words naming both versions, and never starts the program bare when the launcher
/// is missing or will not start — while a launch that did not ask for a cell keeps today's bare fallback. Every OS: the platform is
/// a fake (<see cref="CellPlatform"/> is a record), so nothing here depends on the kernel the test runs on.
/// </summary>
public class CellLaunchTests
{
    static CellPlatform Linux(int abi, string? lsm = "lockdown,capability,landlock,yama,apparmor") =>
        new(true, "6.8.0-64-generic", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            abi, abi > 0 ? null : "errno 38: the kernel has no Landlock", lsm, "1");

    static (CellPolicy Policy, string Marker, System.Diagnostics.ProcessStartInfo Psi) Probe(string name)
    {
        var dir = Path.Combine(TestEnv.Home, "cell-launch-" + name + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "ran");
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true }
            : new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? $"echo ran> \"{marker}\"" : $"echo ran > '{marker}'");
        return (new CellPolicy(dir, [], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin")]), marker, psi);
    }

    static async Task AssertNeverRan(string marker)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(marker), "the program ran although its cell launch was refused");
    }

    [Fact]
    public async Task A_cell_launch_below_landlock_6_is_refused_naming_both_versions()
    {
        var (policy, marker, psi) = Probe("abi5");
        var ex = Assert.Throws<TradeAgentException>(() =>
            TradeAgent.AgentRuntime.ProcessContainment.Start(psi, policy, ["/bin/true"], Linux(5)));
        Assert.Equal(ErrorCode.CELL_UNAVAILABLE, ex.Code);
        Assert.Contains("6.8.0-64-generic", ex.Message);
        Assert.Contains("Landlock ABI 5", ex.Message);
        Assert.Contains("ABI 6 or newer", ex.Message);

        foreach (var (platform, words) in new (CellPlatform, string)[]
        {
            (Linux(0), "Landlock ABI none"),
            (Linux(7, "lockdown,capability,yama,apparmor"), "does not run Landlock"),
            (Linux(7, null), "could not be read"),
            (Linux(7) with { Linux = false, Kernel = "Darwin 25.5.0" }, "Linux only"),
            (Linux(7) with { Architecture = "s390x" }, "x64 and arm64 only"),
        })
        {
            var refused = Assert.Throws<TradeAgentException>(() =>
                TradeAgent.AgentRuntime.ProcessContainment.Start(psi, policy, ["/bin/true"], platform));
            Assert.Contains(words, refused.Message);
            Assert.Contains("minimum: 6", refused.Message);
        }
        Assert.Null(Linux(6).Refusal());
        await AssertNeverRan(marker);
    }

    [Fact]
    public async Task A_cell_launch_never_falls_back_to_a_bare_start()
    {
        var (policy, marker, psi) = Probe("bare");
        var missing = Assert.Throws<TradeAgentException>(() =>
            TradeAgent.AgentRuntime.ProcessContainment.Start(psi, policy, [], Linux(7)));
        Assert.Equal(ErrorCode.CELL_UNAVAILABLE, missing.Code);
        Assert.Contains("is not installed", missing.Message);
        await AssertNeverRan(marker);
        if (OperatingSystem.IsWindows()) return;

        // A LAUNCHER THAT WILL NOT START: today's non-cell launch runs the program bare and says so; a cell launch refuses.
        var notExecutable = Path.Combine(Path.GetDirectoryName(marker)!, "trade");
        File.WriteAllText(notExecutable, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(notExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // The attempt's cgroup is a plain directory standing in for a delegated root (U-cell-linux-b), so the launch gets as far as
        // the launcher's start — and the attempt it made for the launch is removed with the refusal.
        var root = Path.Combine(Path.GetDirectoryName(marker)!, "cgroup-root");
        var refused = Assert.Throws<TradeAgentException>(() =>
            TradeAgent.AgentRuntime.ProcessContainment.Start(psi, policy, [notExecutable], Linux(7), AttemptCgroups.StandIn(root)));
        Assert.Equal(ErrorCode.CELL_UNAVAILABLE, refused.Code);
        Assert.Contains("would not start", refused.Message);
        Assert.Equal("/bin/sh", psi.FileName);        // the caller's command, put back as it was written
        Assert.Empty(Directory.GetDirectories(root));
        await AssertNeverRan(marker);

        using var bare = TradeAgent.AgentRuntime.ProcessContainment.Start(psi, [notExecutable]);
        await bare.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(marker), "a launch that never asked for a cell lost today's bare fallback");
        Assert.StartsWith("not held", bare.Mechanism);
        Assert.Null(bare.Cell);
    }

    [Fact]
    public void The_cell_stage_refuses_off_linux_or_a_policy_file_that_is_not_there()
    {
        var error = new StringWriter();
        var code = CellStage.Run([Path.Combine(TestEnv.Home, "no-such-" + Guid.NewGuid().ToString("n") + ".cell.json"), "true"], error);
        Assert.Equal(CellStage.Failed, code);
        Assert.Contains("the cell was not entered", error.ToString());
        Assert.Contains(OperatingSystem.IsLinux() && CellPlatform.Read().Refusal() is null ? "does not exist" : "minimum: 6", error.ToString());
    }
}
