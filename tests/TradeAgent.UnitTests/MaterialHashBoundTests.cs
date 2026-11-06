using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE HASH PASS IS BOUNDED IN BYTES AND IN WALL TIME (<c>U-cards-2</c> item 2, U-runway-fixes' card). <c>HashesPerPass</c> bounded the
/// COUNT of files a pass hashed and nothing bounded what each cost: one 4 GB drop — or a sparse terabyte in the owner's inbox — held
/// the pass for as long as the whole read took. Now a pass hashes up to a byte budget and a time budget; what it does not reach or
/// finish is DEFERRED — counted, said in words in the result, the row left present and unhashed — and read again next pass.
/// </summary>
public class MaterialHashBoundTests
{
    /// <summary>How long a pass may take here: far past a one-second bound, far short of reading 1 TiB.</summary>
    static readonly TimeSpan Prompt = TimeSpan.FromSeconds(30);

    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"hash-bound-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        return (TestEnv.NewDb(), root);
    }

    static string Inbox(string root, string name) => Path.Combine(root, MaterialScanner.InboxDir, name);

    static async Task<ScanResult> Bounded(Func<ScanResult> pass)
    {
        var task = Task.Run(pass);
        Assert.True(await Task.WhenAny(task, Task.Delay(Prompt)) == task,
            "the pass did not end within " + Prompt + ": the hash pass is unbounded");
        return await task;
    }

    static bool Hashed(Database db, string rel) => new MaterialStore(db).Present().Single(m => m.RelPath == rel).Sha256 is not null;

    /// <summary>
    /// A SPARSE TERABYTE IN THE INBOX WITH TWO SMALL FILES BESIDE IT. Pass one hashes the small files and DEFERS the terabyte (the byte
    /// budget): it ends promptly, the words name the file and why. Pass two reads the terabyte first and alone, and the time budget
    /// stops it: it ends promptly, the row stays present and unhashed, and the words say it did not finish. Before the bound each of
    /// these passes read the whole terabyte (minutes), unbounded and uncancelled but by the caller.
    /// </summary>
    [Fact]
    public async Task A_sparse_terabyte_in_the_inbox_stalls_no_pass_and_is_deferred_in_words_never_skipped_silently()
    {
        var (db, root) = Workspace();
        using var _ = db;
        File.WriteAllText(Inbox(root, "a.txt"), "the first small file");
        File.WriteAllText(Inbox(root, "b.txt"), "the second small file");
        Sparse(Inbox(root, "huge.bin"), 1L << 40);
        var scanner = new MaterialScanner(db, root, at => true) { HashBytesPerPass = 1 << 20, HashTimePerPass = TimeSpan.FromSeconds(1) };

        var first = await Bounded(() => scanner.Scan());
        Assert.Equal(2, first.Hashed);
        Assert.Equal(1, first.HashDeferred);
        var said = Assert.Single(first.HashDeferrals);
        Assert.Contains("inbox/huge.bin", said, StringComparison.Ordinal);
        Assert.Contains("not hashed this pass", said, StringComparison.Ordinal);
        Assert.Contains("tried again next pass", said, StringComparison.Ordinal);
        Assert.True(Hashed(db, "inbox/a.txt") && Hashed(db, "inbox/b.txt"));
        Assert.False(Hashed(db, "inbox/huge.bin"));

        var second = await Bounded(() => scanner.Scan());
        Assert.Equal(0, second.Hashed);
        Assert.Equal(1, second.HashDeferred);
        Assert.Contains("did not finish", Assert.Single(second.HashDeferrals), StringComparison.Ordinal);
        Assert.False(Hashed(db, "inbox/huge.bin"));
        Assert.Contains("hash-deferred=1", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, new MaterialStore(db).Present().Count);   // the row is there: deferred is not forgotten
    }

    /// <summary>
    /// THE REMAINDER IS RESUMED: three files of 400 KB under a 1 MB budget — the first pass hashes two and defers the third in words, the
    /// next pass hashes it, and nothing is deferred once everything has been hashed. A deferral is a postponement, never a loss.
    /// </summary>
    [Fact]
    public async Task What_a_pass_defers_for_its_byte_budget_is_hashed_by_the_next_pass()
    {
        var (db, root) = Workspace();
        using var _ = db;
        foreach (var name in new[] { "one.bin", "two.bin", "three.bin" })
            File.WriteAllBytes(Inbox(root, name), new byte[400_000].Select((_, i) => (byte)(i % 251 + name.Length)).ToArray());
        var scanner = new MaterialScanner(db, root, at => true) { HashBytesPerPass = 1_000_000 };

        var first = await Bounded(() => scanner.Scan());
        Assert.Equal((2, 1), (first.Hashed, first.HashDeferred));
        Assert.Contains("1,000,000 bytes", Assert.Single(first.HashDeferrals), StringComparison.Ordinal);

        var second = await Bounded(() => scanner.Scan());
        Assert.Equal((1, 0), (second.Hashed, second.HashDeferred));
        Assert.Empty(second.HashDeferrals);
        Assert.All(new MaterialStore(db).Present(), m => Assert.NotNull(m.Sha256));
    }

    /// <summary>
    /// A FILE NO PASS CAN FINISH DOES NOT HOLD THE BATCH: the batch is one file wide and the sparse terabyte came first. Asked by row id it
    /// would be the whole batch of every pass and the small file behind it would never be hashed, and never be said.
    /// </summary>
    [Fact]
    public async Task A_file_no_pass_can_finish_does_not_keep_the_files_behind_it_unhashed()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Sparse(Inbox(root, "huge.bin"), 1L << 40);
        var scanner = new MaterialScanner(db, root, at => true) { HashesPerPass = 1, HashTimePerPass = TimeSpan.FromSeconds(1) };

        var first = await Bounded(() => scanner.Scan());
        Assert.Equal((0, 1), (first.Hashed, first.HashDeferred));

        File.WriteAllText(Inbox(root, "later.txt"), "a small file that arrived behind it");
        var second = await Bounded(() => scanner.Scan());

        Assert.Equal(1, second.Hashed);
        Assert.True(Hashed(db, "inbox/later.txt"));
        Assert.False(Hashed(db, "inbox/huge.bin"));
    }

    static void Sparse(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (OperatingSystem.IsWindows() && !DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"could not mark the file sparse (error {Marshal.GetLastPInvokeError()})");
        fs.SetLength(length);
    }

    const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);
}
