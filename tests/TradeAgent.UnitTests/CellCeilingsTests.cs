using System.Globalization;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A CELL CANNOT STARVE THE OWNER'S SERVER OF MEMORY, SWAP OR DISK BANDWIDTH (<c>U-cell-ceilings</c>; r31 A4 § 5 and A4-1(b)). Every OS: the files each
/// attempt's cgroup and the one parent of all attempts are written with, read from a plain directory standing in for a delegated root. What the kernel
/// does with them is <c>CellLinuxTests</c>' and the cell benchmark's rows, on Linux in CI.
/// </summary>
public class CellCeilingsTests
{
    static string Root(string name)
    {
        var d = Path.Combine(TestEnv.Home, "ceilings-" + name + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static string Of(string dir, string file) => File.ReadAllText(Path.Combine(dir, file)).Trim();

    static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// (1) ONE ATTEMPT'S MEMORY: <c>memory.max</c> at half of this computer's memory or 8 GiB, the less; <c>memory.high</c> below it, where the kernel slows
    /// and reclaims the attempt before its OOM; and <c>memory.swap.max</c> 0, so an attempt at its ceiling is never pushed into swap the owner's services pay for.
    /// </summary>
    [Fact]
    public void An_attempts_memory_is_held_below_its_ceiling_and_never_swapped()
    {
        var attempt = AttemptCgroups.StandIn(Root("memory")).Create("memory");
        var max = long.Parse(Of(attempt.Dir, "memory.max"), CultureInfo.InvariantCulture);
        Assert.Equal(AttemptCgroups.MemoryMax, max);
        Assert.InRange(max, 1, 8L << 30);
        var high = long.Parse(Of(attempt.Dir, "memory.high"), CultureInfo.InvariantCulture);
        Assert.InRange(high, max / 2, max - 1);
        Assert.Equal("0", Of(attempt.Dir, "memory.swap.max"));
    }

    /// <summary>
    /// (2) ALL ATTEMPTS TOGETHER: every attempt is made beneath one parent, <c>attempts/</c>, whose own <c>memory.max</c> and <c>pids.max</c> bound their sum —
    /// two attempts at their own task ceilings would pass it, so it is a bound of its own and not each one's again — and whose <c>memory.swap.max</c> is 0.
    /// </summary>
    [Fact]
    public void Every_attempt_is_made_beneath_one_parent_whose_ceilings_bound_their_sum()
    {
        var root = Root("sum");
        var cgroups = AttemptCgroups.StandIn(root);
        var parent = Path.Combine(root, "attempts");
        var (a, b) = (cgroups.Create("a"), cgroups.Create("b"));
        Assert.Equal(parent, Path.GetDirectoryName(a.Dir));
        Assert.Equal(parent, Path.GetDirectoryName(b.Dir));
        var pids = int.Parse(Of(parent, "pids.max"), CultureInfo.InvariantCulture);
        Assert.InRange(pids, AttemptCgroups.PidsMax + 1, 2 * AttemptCgroups.PidsMax - 1);
        var memory = long.Parse(Of(parent, "memory.max"), CultureInfo.InvariantCulture);
        Assert.InRange(memory, AttemptCgroups.MemoryMax, long.MaxValue);
        Assert.Equal("0", Of(parent, "memory.swap.max"));
    }

    /// <summary>
    /// (1b) THE CAP: one attempt's <c>memory.max</c> is half of this computer's memory up to 8 GiB and no more, so a server of 64 GB keeps 56 GB for the
    /// owner's other services while one attempt runs; <c>memory.high</c> is four fifths of it, in whole pages.
    /// </summary>
    [Fact]
    public void One_attempts_memory_is_half_of_this_computer_or_eight_gib_the_less()
    {
        Assert.Equal(8L << 30, AttemptCgroups.MemoryCap);
        Assert.Equal(Math.Min(AttemptCgroups.SumMemoryMax, AttemptCgroups.MemoryCap) / Environment.SystemPageSize * Environment.SystemPageSize,
            AttemptCgroups.MemoryMax);
        Assert.Equal(AttemptCgroups.MemoryMax / 5 * 4 / Environment.SystemPageSize * Environment.SystemPageSize, AttemptCgroups.MemoryHigh);
        Assert.True(AttemptCgroups.SumPidsMax < 2 * AttemptCgroups.PidsMax, "two attempts at their own ceiling must pass the sum's, or the sum bounds nothing");
    }

    /// <summary>
    /// (1c) WHERE THIS KERNEL ACCOUNTS NO SWAP PER CGROUP, nothing is written that is not there, the line says so, and the attempt still starts: the
    /// memory and task ceilings hold without it.
    /// </summary>
    [Fact]
    public void Where_swap_is_not_accounted_the_attempt_starts_and_the_line_says_so()
    {
        var cgroups = AttemptCgroups.StandIn(Root("noswap"), swap: false);
        var attempt = cgroups.Create("noswap");
        Assert.False(File.Exists(Path.Combine(attempt.Dir, "memory.swap.max")));
        Assert.Equal(N(AttemptCgroups.MemoryHigh), Of(attempt.Dir, "memory.high"));
        Assert.NotNull(cgroups.SwapGap);
        Assert.Contains("swap not bounded", attempt.Ceilings, StringComparison.Ordinal);
        Assert.Contains("swap not bounded", cgroups.Ceilings, StringComparison.Ordinal);
    }

    /// <summary>
    /// (2b) THE SUM'S MEMORY AND ITS SLOWING POINT: the parent's <c>memory.max</c> is half of this computer's memory, its <c>memory.high</c> four fifths of it,
    /// and the restart's sweep ends and removes the attempts beneath it and leaves the parent itself.
    /// </summary>
    [Fact]
    public void The_parents_ceilings_are_the_sums_and_the_sweep_leaves_the_parent()
    {
        var root = Root("sweep");
        var cgroups = AttemptCgroups.StandIn(root);
        Assert.Equal(Path.Combine(root, AttemptCgroups.AttemptsLeaf), cgroups.Attempts);
        Assert.Equal(N(AttemptCgroups.SumMemoryMax), Of(cgroups.Attempts!, "memory.max"));
        Assert.Equal(N(AttemptCgroups.SumMemoryMax / 5 * 4 / Environment.SystemPageSize * Environment.SystemPageSize), Of(cgroups.Attempts!, "memory.high"));
        Assert.Equal(N(AttemptCgroups.SumPidsMax), Of(cgroups.Attempts!, "pids.max"));
        var left = cgroups.Create("left");
        var report = cgroups.Sweep(before: DateTime.MinValue);
        Assert.Contains(left.Dir, report.Removed);
        Assert.True(Directory.Exists(cgroups.Attempts));
        Assert.Contains($"all attempts together pids.max {AttemptCgroups.SumPidsMax}", left.Ceilings, StringComparison.Ordinal);
    }

    /// <summary>
    /// (3) THE WRITE RATE: where the io controller is delegated and the home is on one disk, each attempt's <c>io.max</c> and the parent's bound writes on
    /// that disk at 200 MiB/s, the reads left to the kernel; where not, no <c>io.max</c> is written, the line says why, and the attempt still starts.
    /// </summary>
    [Fact]
    public void An_attempts_writes_are_bounded_on_the_homes_disk_where_io_is_delegated()
    {
        Assert.Equal(200L << 20, AttemptCgroups.WriteBps);
        var bounded = AttemptCgroups.StandIn(Root("io"), io: "8:0");
        var attempt = bounded.Create("io");
        Assert.Equal("8:0 wbps=209715200", Of(attempt.Dir, "io.max"));
        Assert.Equal("8:0 wbps=209715200", Of(bounded.Attempts!, "io.max"));
        Assert.Contains("io.max writes 200 MiB/s on the home's disk 8:0", attempt.Ceilings, StringComparison.Ordinal);
        Assert.Null(bounded.IoGap);

        var open = AttemptCgroups.StandIn(Root("noio"));
        var plain = open.Create("noio");
        Assert.False(File.Exists(Path.Combine(plain.Dir, "io.max")));
        Assert.Null(open.Io);
        Assert.Contains("writes not bounded", plain.Ceilings, StringComparison.Ordinal);
    }

    /// <summary>
    /// (3b) THE KERNEL'S OWN READ-BACK: <c>io.max</c> prints every key, <c>max</c> for those not set, and a disk only once it is bounded; the write rate is
    /// read for the named disk alone.
    /// </summary>
    [Theory]
    [InlineData("8:0 rbps=max wbps=209715200 riops=max wiops=max", "8:0", true)]
    [InlineData("8:16 rbps=max wbps=209715200 riops=max wiops=max\n8:0 rbps=max wbps=max riops=max wiops=max", "8:0", false)]
    [InlineData("8:0 rbps=max wbps=104857600 riops=max wiops=max", "8:0", false)]
    [InlineData("", "8:0", false)]
    [InlineData("259:0 rbps=max wbps=209715200 riops=max wiops=max", "259:0", true)]
    public void The_write_rate_is_read_back_for_the_named_disk(string ioMax, string disk, bool bounded) =>
        Assert.Equal(bounded, AttemptCgroups.ReadsBack(ioMax, disk));

    /// <summary>
    /// (3c) THE HOME'S MOUNT, from <c>/proc/self/mountinfo</c>'s lines: the deepest mount point holding the path by whole components (never a sibling that
    /// shares a prefix), the later of two mounts on one point, the octal escape of a space read back, the type after the lone <c>-</c> past optional fields.
    /// </summary>
    [Fact]
    public void The_homes_mount_is_the_deepest_that_holds_it_by_whole_components()
    {
        string[] mountinfo =
        [
            "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw",
            "30 22 259:2 / /var/lib rw,relatime shared:9 master:3 - xfs /dev/nvme0n1p2 rw",
            "31 22 0:41 / /var/lib-other rw - tmpfs tmpfs rw",
            "40 30 0:52 / /var/lib/trade\\040agent rw - btrfs /dev/sdb rw,subvol=/home",
            "41 30 259:5 / /var/lib/trade\\040agent rw shared:12 - ext4 /dev/nvme1n1p1 rw",
        ];
        Assert.Equal(new BlockDevice.Mounted("/var/lib/trade agent", "ext4", "259:5"),
            BlockDevice.Mount(mountinfo, "/var/lib/trade agent/.local/share/TradeAgent"));
        Assert.Equal(new BlockDevice.Mounted("/var/lib", "xfs", "259:2"), BlockDevice.Mount(mountinfo, "/var/lib/tradeagent"));
        Assert.Equal(new BlockDevice.Mounted("/", "ext4", "8:1"), BlockDevice.Mount(mountinfo, "/var/lib-otherwise"));
        Assert.Equal(new BlockDevice.Mounted("/var/lib-other", "tmpfs", "0:41"), BlockDevice.Mount(mountinfo, "/var/lib-other/x"));
    }
}
