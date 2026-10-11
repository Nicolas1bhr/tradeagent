using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

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
        new(workspace, readOnly, CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin"), new("HOME", workspace)], [8443]);

    [Fact]
    public void A_policy_file_is_named_by_its_hash_and_reads_back_whole()
    {
        var ws = Dir("ws");
        var policy = Policy(ws);
        var path = policy.Write(Dir("cells"));

        Assert.Equal(policy.Hash + ".cell.json", Path.GetFileName(path));
        var back = CellPolicy.Read(path);
        Assert.Equal(policy.Hash, back.Hash);
        Assert.Equal(ws, back.Workspace);
        Assert.Equal(["HOME", "PATH"], back.Environment.Select(e => e.Key));
        Assert.Equal([8443], back.TcpPorts);
    }

    [Fact]
    public void A_policy_file_changed_after_it_was_written_is_refused()
    {
        var path = Policy(Dir("ws")).Write(Dir("cells"));
        File.WriteAllText(path, File.ReadAllText(path).Replace("8443", "8444"));

        var ex = Assert.Throws<TradeAgentException>(() => CellPolicy.Read(path));
        Assert.Equal(ErrorCode.CELL_UNAVAILABLE, ex.Code);
        Assert.Contains("does not hash to its name", ex.Message);
    }

    [Fact]
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
}

/// <summary>
/// THE SECCOMP FILTER'S DECISIONS, READ BY A CLASSIC-BPF INTERPRETER (<c>U-cell-linux-a</c> item 2), on every OS: the program the cell
/// stage installs, run over the <c>seccomp_data</c> the kernel would hand it. The kernel's own verdicts are <c>CellLinuxTests</c>' on Linux.
/// </summary>
public class CellSeccompTests
{
    const uint Allow = 0x7fff0000, Eacces = 0x00050000 | 13, Eperm = 0x00050000 | 1, Kill = 0x80000000;
    const uint X64 = 0xC000003E, Arm64 = 0xC00000B7, I386 = 0x40000003;

    static uint Run(System.Runtime.InteropServices.Architecture arch, uint auditArch, uint nr, params ulong[] args)
    {
        var code = CellNative.SeccompProgram(arch);
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

    [Fact]
    public void An_x32_system_call_is_refused_on_x64()
    {
        Assert.Equal(Eperm, Run(System.Runtime.InteropServices.Architecture.X64, X64, 0x40000000u + 41, 2, 1, 0));
    }
}
