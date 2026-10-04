using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A MATERIAL PASS THAT COULD NOT READ EVERYTHING IS NOT COMPLETE (U-inbox-unreadable): it does not
/// move the window, it marks nothing missing in the group it could not read, and it says so.
///
/// <para><c>material</c> is the MEASUREMENT table, and <see cref="MaterialOrigin.Inbox"/> claims the
/// one thing a listing cannot show — that the account owner put the file there — only across a window
/// with no live agent (<c>docs/COUNCIL.md</c> rule 7). A pass may shorten the NEXT pass's window only
/// if it looked everywhere. The pass that could not list a folder used to count as complete: the
/// window moved past files it never saw, and a later pass that could list the folder recorded an
/// agent's file in it as the OWNER's — the dangerous direction; and the same pass stamped every
/// recorded file in that folder removed, a deletion nobody observed.</para>
///
/// <para>Refusals are made through the scanner's own seam on the disk (<see cref="WorkspaceReads"/>),
/// not with a permission bit: Windows does not honour one the way macOS and Linux do, and these must
/// say the same thing on all three.</para>
/// </summary>
public class MaterialUnreadableTests
{
    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"unreadable-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        foreach (var d in MaterialScanner.TrackedAgentDirs)
            Directory.CreateDirectory(Path.Combine(root, MaterialScanner.AgentDir, d));
        return (TestEnv.NewDb(), root);
    }

    static void Drop(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// THE MACHINE'S OWN DISK, EXCEPT WHERE THE TEST SAYS IT REFUSES — to list a folder, to describe a
    /// file, or to say whether a folder is there at all — exactly as a refusal arrives from the disk:
    /// an <see cref="UnauthorizedAccessException"/> out of the read.
    /// </summary>
    sealed class Disk
    {
        readonly HashSet<string> _unlistable = new(StringComparer.Ordinal);
        readonly HashSet<string> _undescribable = new(StringComparer.Ordinal);
        readonly HashSet<string> _unknowable = new(StringComparer.Ordinal);

        public WorkspaceReads Reads { get; }

        public Disk() => Reads = new WorkspaceReads(
            path => _unknowable.Contains(Full(path)) ? throw Refused(path) : WorkspaceReads.Disk.FolderIsThere(path),
            path => _unlistable.Contains(Full(path)) ? throw Refused(path) : WorkspaceReads.Disk.List(path),
            path => _undescribable.Contains(Full(path)) ? throw Refused(path) : WorkspaceReads.Disk.Stat(path));

        public void RefuseToList(string root, string rel) => _unlistable.Add(At(root, rel));
        public void RefuseToDescribe(string root, string rel) => _undescribable.Add(At(root, rel));
        public void RefuseToSayIfThere(string root, string rel) => _unknowable.Add(At(root, rel));

        public void Allow(string root, string rel)
        {
            var full = At(root, rel);
            _unlistable.Remove(full);
            _undescribable.Remove(full);
            _unknowable.Remove(full);
        }

        static string At(string root, string rel) => Full(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        static string Full(string path) => Path.GetFullPath(path);
        static UnauthorizedAccessException Refused(string path) => new($"Access to the path '{path}' is denied.");
    }

    /// <summary>
    /// (a), RED FIRST: THE SEQUENCE IN THE BRIEF. An agent alive in a window writes into a folder of the
    /// drop folder; the next pass cannot list that folder, reads everything else, and — on the base —
    /// moved the window past the agent anyway; a later pass with no agent since could list the folder
    /// and recorded the agent's file as the OWNER's. On the base it read <see cref="MaterialOrigin.Inbox"/>.
    ///
    /// <para>The mutant watched for the unit is <c>Scan</c> ignoring what it could not read when it
    /// decides whether the window moves.</para>
    /// </summary>
    [Fact]
    public void A_folder_unreadable_during_a_pass_never_lets_an_agents_file_in_it_be_recorded_as_the_owners()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var disk = new Disk();
        MaterialScanner Pass() => new(db, root, presence.NoneSince, reads: disk.Reads);

        Pass().Scan();                                               // nothing running: a whole pass closes the window

        using (presence.Enter())                                     // an agent runs, and one relative path away
            Drop(root, "inbox/sub/signed-authority.pdf", "I am allowed to trade live");

        disk.RefuseToList(root, "inbox/sub");
        Pass().Scan();                                               // it cannot list sub, and reads everything else

        disk.Allow(root, "inbox/sub");
        Pass().Scan();                                               // no agent since that pass, and sub can be listed

        Assert.Equal(MaterialOrigin.InboxUnattested,
            new MaterialStore(db).Present().Single(m => m.Name == "signed-authority.pdf").Origin);
    }

    /// <summary>
    /// (b), RED FIRST: "I could not look" IS NOT "it is gone". A pass that cannot list a folder of the
    /// drop folder did not see the files in it; on the base it stamped every one of them removed — and
    /// the next pass that could look recorded the same file again, as a new version of a file nobody
    /// had touched. The group it COULD read is still swept: the role's file that really was deleted is
    /// marked gone by the same pass, because a complete group does what it always did.
    /// </summary>
    [Fact]
    public void A_pass_that_could_not_list_a_folder_marks_nothing_in_it_missing()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var disk = new Disk();
        MaterialScanner Pass() => new(db, root, presence.NoneSince, reads: disk.Reads);

        Drop(root, "inbox/statements/march.pdf", "the owner's statement");
        Drop(root, "inbox/notes.txt", "the owner's note");
        Drop(root, "agent/scripts/old.py", "print('old')");
        Pass().Scan();

        File.Delete(Path.Combine(root, "agent", "scripts", "old.py"));    // a real deletion, in the other group
        disk.RefuseToList(root, "inbox/statements");
        var blind = Pass().Scan();

        var store = new MaterialStore(db);
        Assert.Null(store.History("inbox/statements/march.pdf").Single().RemovedAt);
        Assert.Null(store.History("inbox/notes.txt").Single().RemovedAt);
        Assert.NotNull(store.History("agent/scripts/old.py").Single().RemovedAt);
        Assert.Equal(1, blind.Removed);

        // And once it can look again, the file is the row it always was: nothing to add, nothing gone.
        disk.Allow(root, "inbox/statements");
        var again = Pass().Scan();
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.Removed);
        Assert.Single(store.History("inbox/statements/march.pdf"));
    }

    /// <summary>
    /// FAIL-CLOSED FOR AS LONG AS IT LASTS, AND NO LONGER. A folder that stays unreadable keeps every
    /// pass incomplete, so the window stays open over the agent that ran inside it, and a file the owner
    /// hands over meanwhile gets the word that wider window allows — recorded, not withheld, and not
    /// rounded up. The first pass that can read the folder again closes the window, and the owner's
    /// next file is theirs. Nothing in the folder was stamped gone at any point.
    /// </summary>
    [Fact]
    public void A_folder_that_stays_unreadable_holds_the_window_open_until_it_can_be_read()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var disk = new Disk();
        var store = new MaterialStore(db);
        MaterialScanner Pass() => new(db, root, presence.NoneSince, reads: disk.Reads);

        Drop(root, "inbox/locked/old.pdf", "handed over long ago");
        Pass().Scan();                                               // a whole pass: the window closes
        disk.RefuseToList(root, "inbox/locked");

        presence.Enter().Dispose();                                  // an agent ran, and is gone
        Assert.False(Pass().Scan().Complete);                        // the folder cannot be listed

        Drop(root, "inbox/first.pdf", "handed over after that run");
        Assert.False(Pass().Scan().Complete);                        // and still cannot
        Assert.Equal(MaterialOrigin.InboxUnattested, store.Present().Single(m => m.Name == "first.pdf").Origin);

        disk.Allow(root, "inbox/locked");
        Assert.True(Pass().Scan().Complete);                         // readable again: the window closes over the run

        Drop(root, "inbox/second.pdf", "handed over with nothing running since");
        Pass().Scan();
        Assert.Equal(MaterialOrigin.Inbox, store.Present().Single(m => m.Name == "second.pdf").Origin);
        Assert.Null(store.History("inbox/locked/old.pdf").Single().RemovedAt);
    }

    /// <summary>
    /// A ROLE'S HOME IS PART OF THE TREE A PASS MUST HAVE READ to call itself complete: a folder there
    /// that the disk will not list holds the window like one in the drop folder, and leaves the rows in
    /// its own group standing. The drop folder, read in full by the same pass, is swept as it always was.
    /// </summary>
    [Fact]
    public void A_home_folder_the_disk_will_not_list_holds_the_window_and_marks_nothing_in_its_group_missing()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var disk = new Disk();
        var store = new MaterialStore(db);
        var research = CouncilRoles.HomeDir(CouncilRoles.Research);

        Drop(root, $"{research}/data/march.csv", "open,high,low");
        Drop(root, "inbox/old.pdf", "the owner deletes this");
        new MaterialScanner(db, root, presence.NoneSince, reads: disk.Reads).Scan();
        var window = db.GetKv("material_scan_mark");

        File.Delete(Path.Combine(root, "inbox", "old.pdf"));
        disk.RefuseToList(root, $"{research}/data");
        var pass = new MaterialScanner(db, root, presence.NoneSince, reads: disk.Reads).Scan();

        Assert.Equal(new[] { $"{research}/data" }, pass.UnreadableFolders);
        Assert.Null(store.History($"{research}/data/march.csv").Single().RemovedAt);
        Assert.NotNull(store.History("inbox/old.pdf").Single().RemovedAt);
        Assert.Equal(window, db.GetKv("material_scan_mark"));
    }

    /// <summary>
    /// THE DROP FOLDER'S YIELD AND THE PASS AGREE ON WHAT "COULD NOT READ" MEANS. The loop asks
    /// <see cref="MaterialScanner.InboxHoldsUnrecorded"/> before every turn and takes a pass first on yes;
    /// for each of the three reads the disk can refuse — a folder's listing, a listed file's size and
    /// time, whether the drop folder is there at all — the yield says yes and the pass says it is not
    /// complete, names the folder, moves no window and marks nothing missing. A package cache, skipped on
    /// purpose by both, is neither news to the yield nor a hole in the pass. Once the disk answers again,
    /// both say so.
    /// </summary>
    [Theory]
    [InlineData("list", "inbox/statements", "inbox/statements")]
    [InlineData("describe", "inbox/statements/march.pdf", "inbox/statements")]
    [InlineData("say is there", "inbox", "inbox")]
    public void The_drop_folders_yield_and_the_pass_agree_on_what_could_not_be_read(string diskWillNot, string refused, string named)
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var disk = new Disk();
        MaterialScanner Scanner() => new(db, root, presence.NoneSince, reads: disk.Reads);

        Drop(root, "inbox/statements/march.pdf", "the owner's statement");
        Drop(root, "inbox/project/node_modules/left-pad/index.js", "module.exports = 1");
        Assert.True(Scanner().Scan().Complete);
        Assert.False(Scanner().InboxHoldsUnrecorded());
        var window = db.GetKv("material_scan_mark");

        switch (diskWillNot)
        {
            case "list": disk.RefuseToList(root, refused); break;
            case "describe": disk.RefuseToDescribe(root, refused); break;
            default: disk.RefuseToSayIfThere(root, refused); break;
        }

        Assert.True(Scanner().InboxHoldsUnrecorded());
        var pass = Scanner().Scan();
        Assert.False(pass.Complete);
        Assert.Equal(1, pass.Unreadable);
        Assert.Equal(new[] { named }, pass.UnreadableFolders);
        Assert.Equal(0, pass.Removed);
        Assert.Equal(window, db.GetKv("material_scan_mark"));

        disk.Allow(root, refused);
        Assert.False(Scanner().InboxHoldsUnrecorded());
        Assert.True(Scanner().Scan().Complete);
        Assert.NotEqual(window, db.GetKv("material_scan_mark"));
    }

    /// <summary>
    /// (c), THE GUARD: A FOLDER SKIPPED ON PURPOSE IS NOT A FOLDER THAT COULD NOT BE READ. A package
    /// cache, a dot-folder and a tree past the depth limit are skipped by name and by depth, on every
    /// pass alike, so nothing in them is ever recorded under any word — and they must not hold the
    /// window, or one <c>node_modules</c> in the drop folder would hold it for ever and every file the
    /// owner handed over afterwards would read as one TradeAgent cannot attribute.
    /// </summary>
    [Fact]
    public void A_folder_skipped_on_purpose_still_lets_the_window_advance()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        MaterialScanner Pass() => new(db, root, presence.NoneSince);

        Drop(root, "inbox/project/node_modules/left-pad/index.js", "module.exports = 1");
        Drop(root, "inbox/project/.git/HEAD", "ref: refs/heads/main");
        Drop(root, $"inbox/{string.Join('/', Enumerable.Range(1, 14).Select(i => $"d{i}"))}/deep.txt", "past the depth limit");
        Pass().Scan();

        presence.Enter().Dispose();                                  // an agent ran, and is gone

        var windowBefore = db.GetKv("material_scan_mark");
        var pass = Pass().Scan();                                    // the same three places, skipped on purpose
        Assert.Equal(3, pass.Skipped);
        Assert.NotEqual(windowBefore, db.GetKv("material_scan_mark")); // and the window closes over the run above

        Drop(root, "inbox/broker-statement.pdf", "the owner's document");
        Pass().Scan();
        Assert.Equal(MaterialOrigin.Inbox,
            new MaterialStore(db).Present().Single(m => m.Name == "broker-statement.pdf").Origin);
    }

    /// <summary>
    /// RED FIRST, ON THE MACHINE'S OWN DISK: A REFUSAL IS NOT AN ABSENCE. The pass's convenient reads
    /// answered "not there" for a path the disk refused to describe: <c>FileInfo.Exists</c> is false for a
    /// file in a folder that can be listed but not searched, so the pass took the owner's statement for
    /// a file that had gone and stamped it removed; <c>Directory.Exists</c> is false for a drop folder
    /// whose parent cannot be searched, so the pass took the whole inbox for empty, stamped every file
    /// in it removed, and moved the window past everything it never saw. On the base both read
    /// "removed". The reads the product makes (<see cref="WorkspaceReads.Disk"/>) now say absent only
    /// when the disk does.
    ///
    /// <para>Permission bits refuse only on macOS and Linux, so this runs there; Windows has no portable
    /// way to produce the same refusal here, and the seam tests above carry the pass's rule on all three.</para>
    /// </summary>
    [Theory]
    [InlineData("a folder of the inbox can be listed but not searched")]
    [InlineData("the workspace cannot be searched, so whether the inbox is there cannot be read")]
    public void A_place_the_disk_refuses_to_describe_is_never_taken_for_gone(string refusal)
    {
        if (OperatingSystem.IsWindows()) return;   // no portable way to produce it here; the seam covers it

        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        Drop(root, "inbox/statements/march.pdf", "the owner's statement");
        new MaterialScanner(db, root, presence.NoneSince).Scan();
        var window = db.GetKv("material_scan_mark");

        var refused = refusal.StartsWith("a folder") ? Path.Combine(root, "inbox", "statements") : root;
        File.SetUnixFileMode(refused, UnixFileMode.UserRead);      // listable, not searchable
        try { new MaterialScanner(db, root, presence.NoneSince).Scan(); }
        finally { File.SetUnixFileMode(refused, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        Assert.Null(new MaterialStore(db).History("inbox/statements/march.pdf").Single().RemovedAt);
        Assert.Equal(window, db.GetKv("material_scan_mark"));
    }

    // ---- what the owner is told -----------------------------------------------------------------

    /// <summary>
    /// ITEM 2, THROUGH THE APP'S OWN PASS: THE OWNER IS TOLD ONCE WHEN PART OF THE INBOX CANNOT BE READ,
    /// AND ONCE WHEN IT CAN. The host's <see cref="AppHost.ScanMaterials"/> — the thirty-second tick's
    /// and the Inbox page's way in — runs four passes: two that cannot list a folder of the drop folder
    /// and two that can. The activity log gets one warning naming that folder relative to the workspace
    /// and one line saying it clears; no pass that could not look closes the window, and the first that
    /// could does.
    ///
    /// <para>The host's passes read a disk of the test's own — a drop folder holding one folder — and
    /// nothing of the workspace the rest of the assembly shares.</para>
    /// </summary>
    [Fact]
    public async Task The_owner_is_told_once_when_part_of_the_inbox_cannot_be_read_and_once_when_it_can()
    {
        var inbox = Path.GetFullPath(Path.Combine(Paths.Workspace, MaterialScanner.InboxDir));
        var locked = Path.Combine(inbox, $"locked-{Guid.NewGuid():n}"[..15]);
        var refused = true;
        var reads = new WorkspaceReads(
            path => Path.GetFullPath(path) == inbox,
            path =>
            {
                if (Path.GetFullPath(path) == inbox) return ([], [locked]);
                if (Path.GetFullPath(path) == locked && refused)
                    throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
                return ([], []);
            },
            _ => null);

        var connector = new FakeConnector(new FakeBroker());
        await connector.ConnectAsync();
        var host = AppHost.Composed(TestEnv.NewDb(), connector, new AgentPresence(), reads);
        try
        {
            Assert.False(host.ScanMaterials()!.Complete);
            Assert.False(host.ScanMaterials()!.Complete);
            Assert.Null(host.Db.GetKv("material_scan_mark"));

            refused = false;
            Assert.True(host.ScanMaterials()!.Complete);
            Assert.True(host.ScanMaterials()!.Complete);
            Assert.NotNull(host.Db.GetKv("material_scan_mark"));

            var said = host.Gateway.Log.RecentActivity()          // oldest first
                .Where(a => a.Text.Contains("could not be read") || a.Text == AppHost.ReadableAgain)
                .ToList();
            Assert.Equal(2, said.Count);
            Assert.Equal(("warn",
                    $"Part of your inbox could not be read: inbox/{Path.GetFileName(locked)}. TradeAgent will look "
                    + "again; until it can, a file that arrives in your inbox may not be listed as one you gave the AI."),
                (said[0].Level, said[0].Text));
            Assert.DoesNotContain(TestEnv.Home, said[0].Text);
            Assert.Equal(("info", AppHost.ReadableAgain), (said[1].Level, said[1].Text));
        }
        finally
        {
            await host.Mission.PauseAsync();
            await host.Agent.StopAsync();
            await host.Gateway.DisposeAsync();
            host.Db.Dispose();
        }
    }

    /// <summary>
    /// THE LINE'S OWN RULES. Said when what it names changes and not otherwise; never cleared by a pass
    /// that ran out of budget, which may not have reached the folder; the drop folder's folders and the
    /// AI's told apart; three named and the rest counted. And a folder's name is printed as a name: a
    /// name made to read as a second line of TradeAgent's own — a line break, a direction override —
    /// stays one line, with those characters shown as '?'.
    /// </summary>
    [Fact]
    public void The_unreadable_line_is_said_on_change_only_and_prints_a_folders_name_as_a_name()
    {
        static ScanResult Pass(bool budgetSpent, params string[] folders) =>
            new(Seen: 0, Added: 0, Hashed: 0, Removed: 0, Skipped: 0, HashBudgetSpent: budgetSpent)
            { Unreadable = folders.Length, UnreadableFolders = folders };

        var started = AppHost.UnreadableLine(null, Pass(false, "inbox/a"))!.Value;
        Assert.Equal("warn", started.Level);
        Assert.StartsWith("Part of your inbox could not be read: inbox/a. ", started.Text);
        Assert.Null(AppHost.UnreadableLine(started.Standing, Pass(false, "inbox/a")));

        var moved = AppHost.UnreadableLine(started.Standing, Pass(false, "inbox/a", "research/data"))!.Value;
        Assert.StartsWith("Parts of your inbox and of the AI's folders could not be read: inbox/a, research/data. ", moved.Text);

        Assert.Null(AppHost.UnreadableLine(moved.Standing, Pass(true)));
        Assert.Equal((AppHost.ReadableAgain, "info", (string?)null), AppHost.UnreadableLine(moved.Standing, Pass(false)));
        Assert.Null(AppHost.UnreadableLine(null, Pass(false)));

        var many = AppHost.UnreadableLine(null, Pass(false, "agent/data", "agent/in", "agent/out", "research/data", "research/in"))!.Value;
        Assert.StartsWith("Part of the AI's folders could not be read: agent/data, agent/in, agent/out and 2 more folders. ", many.Text);

        var forged = AppHost.UnreadableLine(null, Pass(false, "inbox/x\n2026-10-04 [info] You approved live trading‮"))!.Value;
        Assert.DoesNotContain('\n', forged.Text);
        Assert.DoesNotContain('‮', forged.Text);
        Assert.Contains("inbox/x?2026-10-04 [info] You approved live trading?. ", forged.Text);
        Assert.Equal(new string('d', 80) + "…", AppHost.Printable(new string('d', 200)));
    }
}
