using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A READ OF THE WALK THE DISK HOLDS DOES NOT HOLD THE PASS (<c>U-material-pass-2</c>; U-perf-scan's card 1, U-cards-2's card). The hash
/// read was bounded in U-perf-scan; the walk's listings, the stat of each file and the app-origin measure were not, and a pass that met a
/// disk which stopped answering held the loop's one exclusion — no lane leased — for as long as the disk did. Now each is waited for only
/// while the walk makes progress: one that does not come back within its bound is left to finish on its own, the workspace is held until it
/// has (no second read beside it), and the pass ends having recorded nothing of that place, marked nothing missing and moved no window.
///
/// <para>And a pass CUT at its file budget is told to the owner (U-perf-scan's card 2): it records no removals, and nothing said so.</para>
/// </summary>
public class MaterialWalkStallTests
{
    static readonly TimeSpan Prompt = TimeSpan.FromSeconds(30);
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    static async Task<ScanResult> Within(Func<ScanResult> pass)
    {
        var task = Task.Run(pass);
        Assert.True(await Task.WhenAny(task, Task.Delay(Prompt)) == task,
            "the pass did not end within " + Prompt + ": a read the disk held held the pass");
        return await task;
    }

    /// <summary>
    /// A disk under <paramref name="root"/> as the product reads it, whose one read of <paramref name="kind"/> at <paramref name="at"/>
    /// waits on <paramref name="disk"/> while <paramref name="holding"/> says so — the shape of a share that stopped answering.
    /// </summary>
    static WorkspaceReads Holding(string root, string kind, string at, Func<bool> holding, ManualResetEventSlim disk, Action held)
    {
        var real = WorkspaceReads.Under(root);
        void Maybe(string path, string what)
        {
            if (what != kind || !holding() || Path.GetFullPath(path) != Path.GetFullPath(at)) return;
            held();
            disk.Wait();
        }
        return new WorkspaceReads(
            path => { Maybe(path, "there"); return real.FolderIsThere(path); },
            path => { Maybe(path, "list"); return real.List(path); },
            path => { Maybe(path, "stat"); return real.Stat(path); }) { StallAfter = Bound };
    }

    [Theory]
    [InlineData("there")]
    [InlineData("list")]
    [InlineData("stat")]
    public async Task A_read_of_the_walk_the_disk_holds_is_left_behind_and_the_pass_marks_nothing_missing_and_moves_no_window(string kind)
    {
        var root = Path.Combine(TestEnv.Home, $"walk-stall-{kind}-{Guid.NewGuid():n}");
        var data = Path.Combine(root, CouncilRoles.HomeDir(CouncilRoles.Operations), "data");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "a.txt"), "the owner's");
        var prices = Path.Combine(data, "prices.csv");
        File.WriteAllText(prices, "the AI's");
        using var db = TestEnv.NewDb();
        using var disk = new ManualResetEventSlim();
        var hold = false;
        var held = 0;
        var reads = Holding(root, kind, kind == "stat" ? prices : data, () => hold, disk, () => Interlocked.Increment(ref held));
        var scanner = new MaterialScanner(db, root, _ => true, reads: reads);

        var first = await Within(() => scanner.Scan());
        Assert.True(first.Complete);
        Assert.Equal(2, first.Added);
        var mark = db.GetKv("material_scan_at");
        Assert.NotNull(mark);

        try
        {
            hold = true;
            File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "b.txt"), "dropped while the disk stalls");
            var stalled = await Within(() => scanner.Scan());
            Assert.Equal(1, Volatile.Read(ref held));
            Assert.True(stalled.WalkStalled, "the result says a read of the walk did not come back");
            Assert.False(stalled.Complete);
            Assert.Equal(0, stalled.Removed);
            Assert.Contains(new MaterialStore(db).Present(), m => m.Name == "prices.csv");
            Assert.Equal(mark, db.GetKv("material_scan_at"));

            // WHILE IT WAITS, NO PASS READS THE WORKSPACE AGAIN: no second held read, and the pass ends at once.
            var again = await Within(() => scanner.Scan());
            Assert.Equal(1, Volatile.Read(ref held));
            Assert.True(again.WalkStalled);
            Assert.Equal(0, again.Removed);
            Assert.True(scanner.InboxHoldsUnrecorded(), "a held workspace answers yes without reading it");
            Assert.Equal(mark, db.GetKv("material_scan_at"));
        }
        finally { hold = false; disk.Set(); }

        // THE DISK ANSWERS: the read finishes on its own, and the next pass looks everywhere again.
        var deadline = DateTimeOffset.UtcNow + Prompt;
        ScanResult back;
        do
        {
            await Task.Delay(50);
            back = await Within(() => scanner.Scan());
        } while (back.WalkStalled && DateTimeOffset.UtcNow < deadline);
        Assert.True(back.Complete);
        Assert.Contains(new MaterialStore(db).Present(), m => m.Name == "b.txt");
        Assert.NotEqual(mark, db.GetKv("material_scan_at"));
    }

    /// <summary>
    /// A HELD LISTING OF THE OWNER'S INBOX (the opus review: every other case holds the AI's folders, walked after the inbox): the file
    /// dropped meanwhile is not recorded, the file already recorded is not marked removed, the AI's folders are not walked either, and the
    /// window stays where it was.
    /// </summary>
    [Fact]
    public async Task A_held_listing_of_the_inbox_records_nothing_of_it_and_marks_nothing_missing()
    {
        var root = Path.Combine(TestEnv.Home, $"walk-stall-inbox-{Guid.NewGuid():n}");
        var inbox = Path.Combine(root, MaterialScanner.InboxDir);
        var data = Path.Combine(root, CouncilRoles.HomeDir(CouncilRoles.Operations), "data");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(inbox, "a.txt"), "the owner's");
        using var db = TestEnv.NewDb();
        using var disk = new ManualResetEventSlim();
        var hold = false;
        var reads = Holding(root, "list", inbox, () => hold, disk, () => { });
        var scanner = new MaterialScanner(db, root, _ => true, reads: reads);
        Assert.True((await Within(() => scanner.Scan())).Complete);
        var window = db.GetKv("material_scan_at");

        try
        {
            hold = true;
            File.WriteAllText(Path.Combine(inbox, "b.txt"), "dropped while the disk stalls");
            File.WriteAllText(Path.Combine(data, "prices.csv"), "the AI's, written while the disk stalls");
            var stalled = await Within(() => scanner.Scan());
            Assert.True(stalled.WalkStalled);
            Assert.Equal((0, 0), (stalled.Added, stalled.Removed));
            var rows = new MaterialStore(db).Present().Select(m => m.Name).ToList();
            Assert.Equal(["a.txt"], rows);
            Assert.Equal(window, db.GetKv("material_scan_at"));
        }
        finally { hold = false; disk.Set(); }
    }

    /// <summary>
    /// THE APP-ORIGIN MEASURE IS A READ OF THE DISK TOO: a home's file the app names is read to tell the app's bytes from the AI's, and a
    /// read the disk holds there ends the walk as a held listing does. Nothing is written of it — above all not the weaker word in place of
    /// a measurement that never finished.
    /// </summary>
    [Fact]
    public async Task An_app_origin_measure_the_disk_holds_ends_the_walk_and_writes_no_row_for_the_file()
    {
        var root = Path.Combine(TestEnv.Home, $"walk-stall-measure-{Guid.NewGuid():n}");
        var home = CouncilRoles.HomeDir(CouncilRoles.Operations);
        Directory.CreateDirectory(Path.Combine(root, home, "scripts"));
        File.WriteAllText(Path.Combine(root, home, "scripts", "run.py"), "print('the app wrote this')");
        var files = new AppFileManifest(Path.Combine(TestEnv.Home, $"walk-stall-files-{Guid.NewGuid():n}.tsv"));
        files.Record(home, "scripts/run.py", "print('the app wrote this')");
        using var db = TestEnv.NewDb();
        using var disk = new ManualResetEventSlim();
        var measures = 0;
        string? Held(Stream s, long max, CancellationToken ct)
        {
            Interlocked.Increment(ref measures);
            disk.Wait();
            return Sha256Hex.OfAtMost(s, max, ct);
        }
        var reads = WorkspaceReads.Under(root) with { StallAfter = Bound };
        var scanner = new MaterialScanner(db, root, _ => true, files, reads: reads) { Measurer = Held };
        try
        {
            var stalled = await Within(() => scanner.Scan());
            Assert.True(stalled.WalkStalled);
            Assert.Equal(1, Volatile.Read(ref measures));
            Assert.DoesNotContain(new MaterialStore(db).Present(), m => m.Name == "run.py");
        }
        finally { disk.Set(); }

        var deadline = DateTimeOffset.UtcNow + Prompt;
        ScanResult back;
        do
        {
            await Task.Delay(50);
            back = await Within(() => scanner.Scan());
        } while (back.WalkStalled && DateTimeOffset.UtcNow < deadline);
        Assert.Equal(MaterialOrigin.App, new MaterialStore(db).Present().Single(m => m.Name == "run.py").Origin);
    }

    /// <summary>
    /// THE OWNER'S LINE NAMES THE REAL CAUSE: a walk the disk held is said as that, and in place of the hash line the same held workspace
    /// also causes (its rows are deferred "because a read did not come back") — one cause, one line, a warning. Asked of the line itself:
    /// the app's own pass scans the assembly's shared workspace, and a read held there would hold every other class's pass beside it.
    /// </summary>
    [Fact]
    public void A_walk_the_disk_held_is_said_as_that_in_place_of_the_hash_line()
    {
        var walk = new ScanResult(0, 0, 0, 0, 0, false) { WalkStalled = true, HashStalled = true, HashDeferred = 3 };
        Assert.Equal((AppHost.WalkStalledLine, "warn"), AppHost.DeferredLine(walk));
        Assert.Equal((AppHost.WalkStalledLine, "warn"), AppHost.DeferredLine(walk with { HashDeferred = 0, HashStalled = false }));
        Assert.Equal((AppHost.HashStalledLine, "warn"), AppHost.DeferredLine(walk with { WalkStalled = false }));
        Assert.Equal((AppHost.HashDeferredLine, "info"), AppHost.DeferredLine(walk with { WalkStalled = false, HashStalled = false }));
        Assert.Null(AppHost.DeferredLine(new ScanResult(5, 0, 0, 0, 0, false)));
        foreach (var word in new[] { "material", "scan", "pass", "hash", "workspace" })
            Assert.DoesNotContain(word, AppHost.WalkStalledLine, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A PASS CUT AT ITS FILE BUDGET IS SAID (U-perf-scan's card 2): 20,001 files in the owner's inbox, so every pass stops looking at
    /// 20,000 and records no removals. The owner is told once, in plain words — where, and what it costs — however many passes stand cut;
    /// and once, when a pass looks everywhere again. RED at the base: nothing in the app read the cut, and nothing was said.
    /// </summary>
    [Fact]
    public async Task A_pass_cut_at_its_file_budget_is_told_to_the_owner_once_in_plain_words()
    {
        var inbox = Path.GetFullPath(Path.Combine(Paths.Workspace, MaterialScanner.InboxDir));
        var count = 20_001;
        var listed = Enumerable.Range(0, count).Select(i => Path.Combine(inbox, $"cut-{i:D5}.txt")).ToArray();
        var at = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var reads = new WorkspaceReads(
            path => Path.GetFullPath(path) == inbox,
            path => Path.GetFullPath(path) == inbox ? (listed[..count], []) : ([], []),
            _ => (1, at));

        var connector = new FakeConnector(new FakeBroker());
        await connector.ConnectAsync();
        var host = AppHost.Composed(TestEnv.NewDb(), connector, new AgentPresence(), reads);
        try
        {
            var cut = host.ScanMaterials()!;
            Assert.Equal(MaterialScanner.InboxDir, cut.CutIn);
            Assert.False(host.ScanMaterials()!.Complete);

            count = 3;
            Assert.True(host.ScanMaterials()!.Complete);
            Assert.True(host.ScanMaterials()!.Complete);

            var expected = $"TradeAgent stopped looking after {20_000:N0} files in your inbox, so a file removed from your inbox or "
                           + "the AI's folders is not noticed until it can look through all of them. Fewer files in your inbox lets it look "
                           + "everywhere again.";
            var said = host.Gateway.Log.RecentActivity()
                .Where(a => a.Text.StartsWith("TradeAgent stopped looking", StringComparison.Ordinal) || a.Text == AppHost.LooksEverywhereAgain)
                .ToList();
            Assert.Equal(2, said.Count);
            Assert.Equal(("warn", expected), (said[0].Level, said[0].Text));
            Assert.Equal(("info", AppHost.LooksEverywhereAgain), (said[1].Level, said[1].Text));
            foreach (var word in new[] { "inbox/", "material", "FileLimit", "pass", "scan", TestEnv.Home })
                Assert.DoesNotContain(word, said[0].Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await host.Mission.PauseAsync();
            await host.Agent.StopAsync();
            await host.Gateway.DisposeAsync();
            host.Db.Dispose();
        }
    }
}
