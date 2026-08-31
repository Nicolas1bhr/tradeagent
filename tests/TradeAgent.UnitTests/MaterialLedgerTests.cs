using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The ledger exists so that in a fortnight nobody has to guess where a file came from. These tests
/// are aimed at the ways a record stops being worth reading: forgetting what it replaced, inventing
/// a deletion it did not observe, drowning in build output, or letting the thing being recorded
/// rewrite the recording.
/// </summary>
public class MaterialLedgerTests
{
    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"ws-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        foreach (var d in MaterialScanner.TrackedAgentDirs) Directory.CreateDirectory(Path.Combine(root, d));
        Directory.CreateDirectory(Path.Combine(root, "scratch"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return (TestEnv.NewDb(), root);
    }

    static void Drop(string root, string relPath, string content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void A_file_the_owner_drops_is_recorded_with_a_hash_we_computed_ourselves()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "inbox/notes.txt", "hello");

        var result = new MaterialScanner(db, root).Scan();
        Assert.Equal(1, result.Added);

        var item = Assert.Single(new MaterialStore(db).Present(MaterialOrigin.Inbox));
        Assert.Equal("inbox/notes.txt", item.RelPath);
        Assert.Equal(5, item.SizeBytes);
        // sha256("hello"), so the hash is the file's and not something we made up.
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", item.Sha256);
    }

    [Fact]
    public void An_executable_is_flagged_as_one()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "inbox/setup.exe", "MZ...");
        Drop(root, "inbox/readme.txt", "words");
        new MaterialScanner(db, root).Scan();

        var items = new MaterialStore(db).Present(MaterialOrigin.Inbox);
        Assert.True(items.Single(i => i.Name == "setup.exe").Runnable);
        Assert.False(items.Single(i => i.Name == "readme.txt").Runnable);
    }

    [Fact]
    public void Replacing_a_file_keeps_the_version_it_replaced()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var store = new MaterialStore(db);

        Drop(root, "inbox/model.bin", "version one");
        new MaterialScanner(db, root).Scan();
        Drop(root, "inbox/model.bin", "version two, which is longer");
        new MaterialScanner(db, root).Scan();

        // Provenance that forgets what it replaced cannot answer "what was I running last week".
        var history = store.History("inbox/model.bin");
        Assert.Equal(2, history.Count);
        Assert.Single(history, h => h.Present);
        Assert.Single(history, h => !h.Present);
        Assert.Equal(2, history.Select(h => h.Sha256).Distinct().Count());
    }

    [Fact]
    public void A_deleted_file_is_marked_gone_rather_than_forgotten()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "inbox/temporary.csv", "a,b,c");
        new MaterialScanner(db, root).Scan();

        File.Delete(Path.Combine(root, "inbox", "temporary.csv"));
        var result = new MaterialScanner(db, root).Scan();

        Assert.Equal(1, result.Removed);
        Assert.Empty(new MaterialStore(db).Present(MaterialOrigin.Inbox));
        var gone = Assert.Single(new MaterialStore(db).History("inbox/temporary.csv"));
        Assert.NotNull(gone.RemovedAt);
    }

    [Fact]
    public void A_scan_that_ran_out_of_budget_never_reports_a_file_as_removed()
    {
        // The failure this guards against is a record that invents deletions. A truncated pass has
        // not seen the rest of the tree, and "I did not look" must never be written down as "it is
        // gone" — one false deletion makes the whole ledger something you have to double-check.
        var (db, root) = Workspace();
        using var _ = db;
        for (var i = 0; i < 6; i++) Drop(root, $"inbox/file{i}.txt", $"contents {i}");
        new MaterialScanner(db, root).Scan();
        Assert.Equal(6, new MaterialStore(db).Present(MaterialOrigin.Inbox).Count);

        var truncated = new MaterialScanner(db, root) { FileLimit = 2 }.Scan();

        Assert.True(truncated.HashBudgetSpent);
        Assert.Equal(0, truncated.Removed);
        Assert.Equal(6, new MaterialStore(db).Present(MaterialOrigin.Inbox).Count);
    }

    [Fact]
    public void Package_and_build_directories_are_not_tracked()
    {
        // One npm install is forty thousand files. Tracking them would bury the dozen rows the
        // ledger exists to show, which is the dump it is supposed to prevent, one level up.
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "inbox/project/index.js", "console.log(1)");
        Drop(root, "inbox/project/node_modules/left-pad/index.js", "module.exports = 1");
        Drop(root, "scripts/obj/Debug/thing.dll", "binary");
        Drop(root, "inbox/project/.git/HEAD", "ref: refs/heads/main");

        new MaterialScanner(db, root).Scan();

        var paths = new MaterialStore(db).Present().Select(m => m.RelPath).ToList();
        Assert.Contains("inbox/project/index.js", paths);
        Assert.DoesNotContain(paths, p => p.Contains("node_modules"));
        Assert.DoesNotContain(paths, p => p.Contains("/obj/"));
        Assert.DoesNotContain(paths, p => p.Contains(".git"));
    }

    [Fact]
    public void Scratch_and_logs_are_left_alone_and_the_agents_own_work_is_recorded()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "scratch/half-finished.py", "pass");
        Drop(root, "logs/run.log", "started");
        Drop(root, "strategies/breakout.py", "def go(): pass");

        new MaterialScanner(db, root).Scan();

        var items = new MaterialStore(db).Present();
        var one = Assert.Single(items);
        Assert.Equal("strategies/breakout.py", one.RelPath);
        Assert.Equal(MaterialOrigin.Agent, one.Origin);
    }

    [Fact]
    public void Rescanning_an_unchanged_workspace_adds_nothing()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "inbox/steady.txt", "unchanged");

        Assert.Equal(1, new MaterialScanner(db, root).Scan().Added);
        var second = new MaterialScanner(db, root).Scan();

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Removed);
        Assert.Equal(0, second.Hashed);          // already hashed; a repeat pass re-reads nothing
        Assert.Single(new MaterialStore(db).Present());
    }

    [Fact]
    public void A_note_is_a_claim_and_cannot_change_what_was_observed()
    {
        // The invariant the two tables exist to hold. If an agent's account of itself could edit the
        // observation, the record would be exactly as trustworthy as the agent — which is the thing
        // it is there to avoid needing to assume.
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "inbox/tool.exe", "payload");
        new MaterialScanner(db, root).Scan();

        var store = new MaterialStore(db);
        var before = store.Present().Single();
        store.AddNote("agent", "session-1", MaterialNoteKind.Ran, before.Sha256, null,
            "ran it against the sample data", DateTimeOffset.UtcNow);

        var after = store.Present().Single();
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.Equal(before.FirstSeenAt, after.FirstSeenAt);

        var note = Assert.Single(store.NotesFor(before.Sha256!));
        Assert.Equal(MaterialNoteKind.Ran, note.Kind);
        Assert.Equal("agent", note.Author);
    }

    [Fact]
    public void A_derivation_links_the_file_it_came_from()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "inbox/raw.csv", "1,2,3");
        Drop(root, "data/cleaned.csv", "1,2");
        new MaterialScanner(db, root).Scan();

        var store = new MaterialStore(db);
        var raw = store.Present().Single(m => m.Name == "raw.csv");
        var cleaned = store.Present().Single(m => m.Name == "cleaned.csv");
        store.AddNote("agent", "s", MaterialNoteKind.Derived, cleaned.Sha256, raw.Sha256,
            "dropped the third column", DateTimeOffset.UtcNow);

        // Reachable from either end, which is what makes "where did this come from" answerable
        // starting from whichever file you happen to be holding.
        Assert.Single(store.NotesFor(raw.Sha256!));
        Assert.Single(store.NotesFor(cleaned.Sha256!));
    }

    [Fact]
    public void A_hash_prefix_finds_the_file_it_belongs_to()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Drop(root, "inbox/find-me.txt", "hello");
        new MaterialScanner(db, root).Scan();

        var store = new MaterialStore(db);
        Assert.Equal("inbox/find-me.txt", store.ByShaPrefix("2cf24dba5fb0")?.RelPath);
        Assert.Null(store.ByShaPrefix("ffffffffffff"));
    }
}
