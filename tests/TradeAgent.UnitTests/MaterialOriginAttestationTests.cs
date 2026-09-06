using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// REVIEW 2026-09-05b finding 5, red first: <c>material</c> is the MEASUREMENT table, and
/// <see cref="MaterialOrigin.Inbox"/> is the one value in it that claims something a directory
/// listing cannot show — that the ACCOUNT OWNER handed the file over.
///
/// Probe P5a on <c>review-probes-b</c> ran the whole thing from the agent's side: its working
/// directory was the workspace, <c>inbox/</c> was one relative path inside it, and the file it
/// wrote there came back stamped <c>Inbox</c> — "what the account owner handed the agent". No
/// escape of any kind was needed. These assert it cannot.
///
/// Both halves are here because either alone is a half-measure. The agent's tree is now a SIBLING
/// of the drop folder rather than its parent, so nothing sandboxes the climb but nothing invites
/// it; and the claim itself is attested against agent process lifetime, so a file that appears
/// while the agent is running is recorded honestly as <see cref="MaterialOrigin.InboxUnattested"/>
/// instead of being attributed to somebody who was not there.
/// </summary>
public class MaterialOriginAttestationTests
{
    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"attest-{Guid.NewGuid():n}");
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
    /// P5a, lifted. The same two files, the same one scan. The owner's is attested; the one that
    /// appeared while an agent process was alive is not, and the ledger says which is which.
    /// </summary>
    [Fact]
    public void A_file_that_appeared_while_the_agent_was_running_is_not_recorded_as_the_owners()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();

        // Nothing running: the owner really did drop something in.
        Drop(root, "inbox/broker-statement.pdf", "the owner's document");
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        // Now an agent process runs, and one relative path away — ../inbox — a file appears.
        using (presence.Enter())
        {
            Drop(root, "inbox/signed-authority.pdf", "I am allowed to trade live");
            new MaterialScanner(db, root, presence.NoneSince).Scan();
        }

        var store = new MaterialStore(db);
        var owners = store.Present().Single(m => m.RelPath.EndsWith("broker-statement.pdf"));
        var forged = store.Present().Single(m => m.RelPath.EndsWith("signed-authority.pdf"));

        Assert.Equal(MaterialOrigin.Inbox, owners.Origin);
        Assert.Equal(MaterialOrigin.InboxUnattested, forged.Origin);
        // And the weaker word is not reachable by asking for the stronger one.
        Assert.DoesNotContain(store.Present(MaterialOrigin.Inbox), m => m.RelPath.EndsWith("signed-authority.pdf"));
    }

    /// <summary>
    /// The other direction, so this is a measurement and not a refusal to measure: with no agent
    /// process anywhere in the window, the owner's file is <see cref="MaterialOrigin.Inbox"/> and
    /// says so. An agent that ran and EXITED before the window still spoils it — the file could
    /// have been written on its way out.
    /// </summary>
    [Fact]
    public void A_file_dropped_while_no_agent_runs_is_still_recorded_as_the_owners()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();

        Drop(root, "inbox/first.pdf", "before any agent existed");
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        presence.Enter().Dispose();                 // an agent ran, and is gone
        Drop(root, "inbox/second.pdf", "dropped after that run, in the same window");
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        Drop(root, "inbox/third.pdf", "and this window has no agent in it at all");
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        var store = new MaterialStore(db);
        Assert.Equal(MaterialOrigin.Inbox, store.Present().Single(m => m.Name == "first.pdf").Origin);
        Assert.Equal(MaterialOrigin.InboxUnattested, store.Present().Single(m => m.Name == "second.pdf").Origin);
        Assert.Equal(MaterialOrigin.Inbox, store.Present().Single(m => m.Name == "third.pdf").Origin);
    }

    /// <summary>
    /// The scanner walks the agent's tree at <c>agent/</c>, beside the inbox. The agent's working
    /// directory is that tree, so the shortest path it can type reaches its own folders and not the
    /// owner's — which is the structural half of the fix.
    /// </summary>
    [Fact]
    public void The_agents_tree_is_a_sibling_of_the_inbox_and_not_its_parent()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();

        Drop(root, "agent/scripts/backtest.py", "print('hi')");
        Drop(root, "inbox/handed-over.csv", "1,2,3");
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        var store = new MaterialStore(db);
        Assert.Equal("agent/scripts/backtest.py", store.Present(MaterialOrigin.Agent).Single().RelPath);
        Assert.Equal("inbox/handed-over.csv", store.Present(MaterialOrigin.Inbox).Single().RelPath);

        // And the shape itself: the inbox is not inside the tree the agent is started in.
        Assert.False(Directory.Exists(Path.Combine(root, MaterialScanner.AgentDir, MaterialScanner.InboxDir)),
            "the drop folder must not be reachable as a subdirectory of the agent's working directory");
    }

    /// <summary>
    /// A sweep for one origin must not stamp the other one gone. Both words come out of the same
    /// walk of the same directory, so a MarkMissing that named only <see cref="MaterialOrigin.Inbox"/>
    /// would report every unattested row as removed on the strength of a pass that had just seen it.
    /// </summary>
    [Fact]
    public void A_pass_that_sees_both_inbox_words_reports_neither_as_removed()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();

        Drop(root, "inbox/attested.txt", "dropped by the owner");
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        using (presence.Enter())
        {
            Drop(root, "inbox/unattested.txt", "appeared while the agent ran");
            Assert.Equal(0, new MaterialScanner(db, root, presence.NoneSince).Scan().Removed);
        }

        var third = new MaterialScanner(db, root, presence.NoneSince).Scan();

        Assert.Equal(0, third.Removed);
        Assert.Equal(2, new MaterialStore(db).Present().Count);
        Assert.All(new MaterialStore(db).Present(), m => Assert.Null(m.RemovedAt));
    }

    /// <summary>The witness itself: unsure is never "nobody was here".</summary>
    [Fact]
    public void The_presence_witness_answers_no_whenever_it_is_unsure()
    {
        var presence = new AgentPresence();
        var start = DateTimeOffset.UtcNow;

        Assert.True(presence.NoneSince(start));         // nothing has ever run
        Assert.Null(presence.LastAliveAt);

        var window = presence.Enter();
        Assert.Equal(1, presence.Live);
        Assert.False(presence.NoneSince(start));        // running now
        Assert.False(presence.NoneSince(DateTimeOffset.UtcNow.AddHours(1)));

        window.Dispose();
        Assert.Equal(0, presence.Live);
        Assert.False(presence.NoneSince(start));        // it ran inside this window
        Assert.NotNull(presence.LastAliveAt);
        Assert.True(presence.NoneSince(presence.LastAliveAt!.Value.AddTicks(1)));

        window.Dispose();                                // disposing twice must not lower it twice
        Assert.Equal(0, presence.Live);
    }
}
