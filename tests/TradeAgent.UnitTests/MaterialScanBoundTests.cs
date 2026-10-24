using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A CELL CAN NOT STALL THE DATABASE THROUGH THE SCANNER (<c>U-exec-env-fix</c> item 1, the Codex second pass on
/// <c>U-execution-environment</c>). An executor's cell writes its whole home, so it can put anything at a name the app wrote — and the
/// scanner measured "did the app write this?" by hashing every byte of it inside <see cref="MaterialStore.Observe"/>'s database lock,
/// with no bound and no cancellation: a sparse 1-TiB file held every reader and writer of the database for as long as the read took.
///
/// <para>In the serial collection because the lock test uses <see cref="HomeFiles"/>' static seam, which one other class sets.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class MaterialScanBoundTests
{
    /// <summary>How long anything here may take before it counts as stalled: far past a pass over a small home, far short of 1 TiB.</summary>
    static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    static WorkspaceContext Context(string role) => new(
        "Practice simulator", ConnectorIsPaper: true, "SIM-1", TradingMode.PAPER,
        ExecutionAvailable: true, null, new RiskPolicy { InstrumentAllowlist = ["ES"] },
        ConnectorIsBuiltInSimulator: false, Role: role);

    /// <summary>A built Research home, scanned once, its language reference — a name the app wrote — returned by path and by row key.</summary>
    static (Database Db, string Root, AppFileManifest Files, string Reference, string Rel) Built()
    {
        var id = Guid.NewGuid().ToString("n");
        var root = Path.Combine(TestEnv.Home, $"scan-bound-{id}");
        Directory.CreateDirectory(root);
        var files = new AppFileManifest(Path.Combine(TestEnv.Home, $"app-files-{id}.tsv"));
        var db = TestEnv.NewDb();
        var home = WorkspaceBuilder.Build(Context(CouncilRoles.Research), root, files);
        new MaterialScanner(db, root, at => true, files).Scan();
        var reference = Path.Combine(home, "research", ResearchLibrary.ReferenceFile);
        return (db, root, files, reference, $"{CouncilRoles.HomeDir(CouncilRoles.Research)}/{ResearchLibrary.ReferencePath}");
    }

    static Material Row(Database db, string rel) => new MaterialStore(db).Present().Single(m => m.RelPath == rel);

    /// <summary>Did <paramref name="task"/> end, however it ended, within <paramref name="span"/>? A bounded wait: nothing is retried.</summary>
    static async Task<bool> Within(Task task, TimeSpan span) => await Task.WhenAny(task, Task.Delay(span)) == task;

    /// <summary>
    /// THE SETTLING CHECK: a cell replaces the app's reference with a sparse 1-TiB file and a pass runs over it. The pass records the
    /// file promptly, as the agent's and without reading it — a size past anything the app writes into a home is not the app's; a
    /// database read asked beside the pass finishes promptly; and the pass, cancelled, finishes promptly too.
    /// </summary>
    [Fact]
    public async Task A_sparse_terabyte_at_a_name_the_app_wrote_stalls_neither_the_scan_nor_the_database()
    {
        var (db, root, files, reference, rel) = Built();
        var stalled = true;                                                   // a stalled pass holds the lock Dispose would wait on
        File.Delete(reference);
        const long terabyte = 1L << 40;
        Sparse(reference, terabyte);                                          // 1 TiB, no byte of it written
        try
        {
            using var cts = new CancellationTokenSource();
            var scan = Task.Run(() => new MaterialScanner(db, root, at => true, files).Scan(cts.Token));

            var row = Task.Run(() => SpinWait.SpinUntil(() =>
                new MaterialStore(db).Present().Any(m => m.RelPath == rel && m.SizeBytes == terabyte), Prompt));
            var recorded = await Within(row, Prompt + Prompt) && await row;
            var read = Task.Run(() => new MaterialStore(db).Present().Count);
            var readInTime = await Within(read, Prompt);
            cts.Cancel();
            var scanInTime = await Within(scan, Prompt);
            Assert.True(recorded && readInTime && scanInTime,
                $"over a 1-TiB file at the app's name: the pass recorded it in time: {recorded}; a database read beside it finished in time: "
                + $"{readInTime}; the pass, cancelled, finished in time: {scanInTime}");
            stalled = false;
            Assert.Equal(MaterialOrigin.Agent, new MaterialStore(db).Present().Single(m => m.RelPath == rel && m.SizeBytes == terabyte).Origin);
        }
        finally
        {
            if (!stalled) db.Dispose();
        }
    }

    /// <summary>
    /// THE HASH IS TAKEN OUTSIDE THE DATABASE LOCK: a read of a name the app wrote that the disk holds up (the seam, standing in for a
    /// slow disk) leaves the database free — a read asked meanwhile finishes while the pass is still waiting on the file.
    /// </summary>
    [Fact]
    public async Task The_scanners_read_of_a_file_holds_no_database_lock()
    {
        var (db, root, files, reference, rel) = Built();
        using var _ = db;
        File.WriteAllText(reference, "# The strategy language\n\nThe cell's own words.\n");   // a new tuple at the app's name

        var entered = new TaskCompletionSource();
        using var release = new ManualResetEventSlim();
        var held = 0;
        HomeFiles.BeforeOpen = p =>
        {
            if (!p.StartsWith(root, StringComparison.Ordinal) || !p.EndsWith(ResearchLibrary.ReferenceFile, StringComparison.Ordinal)) return;
            if (Interlocked.Exchange(ref held, 1) == 1) return;
            entered.TrySetResult();
            release.Wait(Prompt);
        };
        try
        {
            var scan = Task.Run(() => new MaterialScanner(db, root, at => true, files).Scan());
            Assert.True(await Within(entered.Task, Prompt), "the pass never opened the app's name");

            var read = Task.Run(() => new MaterialStore(db).Present().Count);
            var readInTime = await Within(read, TimeSpan.FromSeconds(5));
            release.Set();
            Assert.True(await Within(scan, Prompt), "the pass did not finish once the file was released");
            await scan;
            Assert.True(readInTime, "a database read waited on the scanner's read of a file: the file is read under the database lock");
            Assert.Equal(MaterialOrigin.Agent, Row(db, rel).Origin);
        }
        finally { HomeFiles.BeforeOpen = null; }
    }

    /// <summary>
    /// THE MEASURING READ STOPS AT ITS BOUND AND AT CANCELLATION: past the bound it answers null after at most one buffer more, and a
    /// cancelled token stops it between reads — the hash the scanner takes of a name the app wrote is neither unbounded nor deaf.
    /// </summary>
    [Fact]
    public void The_bounded_hash_stops_at_its_bound_and_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Sha256Hex.OfAtMost(new MemoryStream(new byte[200_000]), 1_000_000, cts.Token));
        Assert.Null(Sha256Hex.OfAtMost(new MemoryStream(new byte[200_000]), 100_000, CancellationToken.None));
        Assert.Equal(Sha256Hex.Of(new byte[100_000]), Sha256Hex.OfAtMost(new MemoryStream(new byte[100_000]), 100_000, CancellationToken.None));
    }

    /// <summary>A file of <paramref name="length"/> bytes none of which is written: truncate on Unix, a sparse file on Windows.</summary>
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
