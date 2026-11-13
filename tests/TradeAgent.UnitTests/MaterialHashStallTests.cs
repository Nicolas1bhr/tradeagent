using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A HASH READ THE OPERATING SYSTEM HOLDS DOES NOT HOLD THE PASS (<c>U-perf-scan</c>; U-cards-2's card). The hash pass's wall-time bound
/// acts between reads, and a read stalled inside the OS — a disk that stopped answering, a share that went away — is not between reads:
/// it held the pass, and with it the pass's exclusion and the loop that ran it, for as long as the disk did. Now the pass waits for the
/// read only as long as its bound, leaves a read that has not come back to finish on its own, says so, and reads no file of that
/// workspace again until it has — so a stalled disk costs one waiting read, never one more per pass.
/// </summary>
public class MaterialHashStallTests
{
    static readonly TimeSpan Prompt = TimeSpan.FromSeconds(30);

    static async Task<ScanResult> Bounded(Func<ScanResult> pass)
    {
        var task = Task.Run(pass);
        Assert.True(await Task.WhenAny(task, Task.Delay(Prompt)) == task,
            "the pass did not end within " + Prompt + ": a read held by the disk held the pass");
        return await task;
    }

    [Fact]
    public async Task A_read_the_disk_holds_is_left_behind_at_the_bound_and_no_other_read_starts_until_it_returns()
    {
        var root = Path.Combine(TestEnv.Home, $"hash-stall-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "b.txt"), "the second");
        using var db = TestEnv.NewDb();
        using var disk = new ManualResetEventSlim();
        var reads = 0;
        // THE DISK HOLDS THE FIRST READ and no cancellation reaches it: the shape of a read blocked inside the OS.
        string Held(Stream s, CancellationToken ct)
        {
            if (Interlocked.Increment(ref reads) == 1) disk.Wait();
            return Sha256Hex.Of(s, ct);
        }
        var scanner = new MaterialScanner(db, root, _ => true) { HashTimePerPass = TimeSpan.FromSeconds(1), Hasher = Held };

        try
        {
            var first = await Bounded(() => scanner.Scan());
            Assert.Equal((2, 0), (first.Added, first.Hashed));
            Assert.Equal(2, first.HashDeferred);
            Assert.Contains(first.HashDeferrals, d => d.Contains("did not come back from the disk", StringComparison.Ordinal));

            var second = await Bounded(() => scanner.Scan());
            Assert.Equal((0, 2), (second.Hashed, second.HashDeferred));
            Assert.Equal(1, Volatile.Read(ref reads));   // no second read beside the one still waiting
        }
        finally { disk.Set(); }

        // THE DISK ANSWERS: the read finishes on its own, and the next pass reads both files.
        var deadline = DateTimeOffset.UtcNow + Prompt;
        ScanResult again;
        do
        {
            await Task.Delay(50);
            again = await Bounded(() => scanner.Scan());
        } while (again.Hashed == 0 && DateTimeOffset.UtcNow < deadline);
        Assert.Equal((2, 0), (again.Hashed, again.HashDeferred));
        Assert.All(new MaterialStore(db).Present(), m => Assert.NotNull(m.Sha256));
    }
}
