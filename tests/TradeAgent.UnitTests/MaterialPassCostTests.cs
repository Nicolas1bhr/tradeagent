using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT ONE MATERIAL PASS COSTS THE DISK, AND THAT ITS BUDGET NEVER INVENTS A DELETION (<c>U-perf-scan</c>; R28 G2).
///
/// <para>Every sighting used to be its own durable commit at <c>synchronous=FULL</c> — 4,903 fsyncs every 30 seconds over 4,900
/// unchanged files, on the loop that also runs the trading pass. A pass's sightings now go in a handful of commits, written after the
/// walk and outside every read of a file's bytes.</para>
/// </summary>
public class MaterialPassCostTests
{
    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"pass-cost-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        return (TestEnv.NewDb(), root);
    }

    static void Drop(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void A_pass_over_six_hundred_unchanged_files_commits_a_handful_of_writes_not_one_per_file()
    {
        var (db, root) = Workspace();
        using var _ = db;
        for (var i = 0; i < 300; i++) Drop(root, $"inbox/drop/f{i}.txt", $"owner {i}");
        for (var i = 0; i < 300; i++) Drop(root, $"agent/data/d{i % 3}/f{i}.csv", $"agent {i}");
        var first = new MaterialScanner(db, root, _ => true) { HashesPerPass = 0 }.Scan();
        Assert.Equal(600, first.Added);

        var before = db.Commits;
        var again = new MaterialScanner(db, root, _ => true) { HashesPerPass = 0 }.Scan();
        var commits = db.Commits - before;

        Assert.Equal((600, 0, 0), (again.Seen, again.Added, again.Removed));
        Assert.True(commits <= 6, $"a pass over 600 unchanged files committed {commits} writes; each is an fsync of the journal");
        // Still the same record: every row was seen by this pass.
        Assert.All(new MaterialStore(db).Present(), m => Assert.True(m.LastSeenAt >= m.FirstSeenAt));
        Assert.Equal(600, new MaterialStore(db).Present().Count);
    }

    [Fact]
    public void A_first_pass_records_every_new_file_in_a_handful_of_commits_with_the_word_each_was_seen_with()
    {
        var (db, root) = Workspace();
        using var _ = db;
        for (var i = 0; i < 200; i++) Drop(root, $"inbox/f{i}.txt", $"owner {i}");
        for (var i = 0; i < 200; i++) Drop(root, $"agent/scripts/s{i}.py", $"print({i})");

        var before = db.Commits;
        var pass = new MaterialScanner(db, root, _ => true) { HashesPerPass = 0 }.Scan();

        Assert.Equal(400, pass.Added);
        Assert.True(db.Commits - before <= 6, $"a first pass over 400 files committed {db.Commits - before} writes");
        var store = new MaterialStore(db);
        Assert.Equal(200, store.Present(MaterialOrigin.Inbox).Count);
        Assert.Equal(200, store.Present(MaterialOrigin.Agent).Count);
        Assert.All(store.Present(MaterialOrigin.Agent), m => Assert.True(m.Runnable));
    }

    /// <summary>
    /// PAST THE OLD 5,000 A PASS STILL LOOKS EVERYWHERE: it was the limit, so a workspace over it never had a complete pass — removals
    /// went unrecorded and the inbox's window froze.
    /// </summary>
    [Fact]
    public void A_pass_over_six_thousand_files_is_complete_and_records_a_removal()
    {
        var (db, root) = Workspace();
        using var _ = db;
        for (var i = 0; i < 6000; i++) Drop(root, $"agent/data/d{i / 500}/f{i}.csv", $"{i}");
        Assert.True(new MaterialScanner(db, root, _ => true) { HashesPerPass = 0 }.Scan().Complete);

        File.Delete(Path.Combine(root, "agent", "data", "d0", "f0.csv"));
        var pass = new MaterialScanner(db, root, _ => true) { HashesPerPass = 0 }.Scan();

        Assert.True(pass.Complete, "a pass over 6,000 files ran out of budget");
        Assert.Equal((5999, 1), (pass.Seen, pass.Removed));
    }

    /// <summary>
    /// THE BUDGET'S EDGE: a walk that stopped collecting at the budget with folders left unvisited, whose count landed exactly on the
    /// budget, read as a complete pass — and marked every file in the folders it never listed removed. "I did not look" is not "it is
    /// gone".
    /// </summary>
    [Fact]
    public void A_walk_stopped_exactly_at_the_budget_with_folders_unvisited_marks_nothing_missing()
    {
        var (db, root) = Workspace();
        using var _ = db;
        foreach (var folder in new[] { "a", "b", "c" })
            for (var i = 0; i < 2; i++) Drop(root, $"inbox/{folder}/f{i}.txt", $"{folder} {i}");
        new MaterialScanner(db, root, _ => true).Scan();
        Assert.Equal(6, new MaterialStore(db).Present(MaterialOrigin.Inbox).Count);

        var pass = new MaterialScanner(db, root, _ => true) { FileLimit = 4 }.Scan();

        Assert.True(pass.HashBudgetSpent, "a walk that left folders unvisited is a pass that ran out of budget");
        Assert.Equal(0, pass.Removed);
        Assert.Equal(6, new MaterialStore(db).Present(MaterialOrigin.Inbox).Count);
    }
}
