using TradeAgent.App;
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

    /// <summary>
    /// A pass that ran out of budget did not look everywhere, so it must not record itself as having
    /// covered the period since the last one. If it did, an agent run inside that period would be
    /// walked past and the next pass would attest a window it has no business attesting.
    /// </summary>
    [Fact]
    public void A_pass_that_ran_out_of_budget_does_not_shorten_the_window_the_next_one_attests_over()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();

        Drop(root, "inbox/one.txt", "dropped by the owner");
        new MaterialScanner(db, root, presence.NoneSince).Scan();   // a whole pass: the window moves

        presence.Enter().Dispose();                                 // and then an agent ran

        // A pass whose budget is spent before it looks at anything. It stamps nothing removed, and
        // it must not close the window over the run above either.
        Assert.True(new MaterialScanner(db, root, presence.NoneSince) { FileLimit = 0 }.Scan().HashBudgetSpent);

        Drop(root, "inbox/two.txt", "and this appeared after that run");
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        var store = new MaterialStore(db);
        Assert.Equal(MaterialOrigin.Inbox, store.Present().Single(m => m.Name == "one.txt").Origin);
        Assert.Equal(MaterialOrigin.InboxUnattested, store.Present().Single(m => m.Name == "two.txt").Origin);
    }

    /// <summary>
    /// ONE WALL CLOCK FOR THE REGISTER AND THE SCANNER, as one machine has one, and the test holds the
    /// hand that moves it.
    /// </summary>
    sealed class Clock(DateTimeOffset start)
    {
        DateTimeOffset _at = start;
        public DateTimeOffset Now() => _at;
        public void Move(TimeSpan by) => _at += by;
    }

    static readonly DateTimeOffset TenOClock = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// U-INBOX-ORDER (a), RED FIRST: THE MACHINE'S CLOCK STEPS BACK INSIDE AN AGENT'S WINDOW. A pass
    /// closes the window at 10:00; an agent starts at 10:01 and writes into the drop folder; the clock
    /// steps back an hour — an NTP correction, a clock set by hand — and the agent exits at 09:01 by
    /// the wall. The next pass compared that exit with 10:00, heard "nobody since", and recorded the
    /// agent's file as the OWNER's: the false claim this ledger exists to prevent. On the base it read
    /// <see cref="MaterialOrigin.Inbox"/>.
    ///
    /// <para>The mutant watched for the unit is the comparison put back on wall time.</para>
    /// </summary>
    [Fact]
    public void A_backward_clock_step_across_an_agents_window_never_records_its_file_as_the_owners()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var clock = new Clock(TenOClock);
        var presence = new AgentPresence(clock.Now);
        MaterialScanner Pass() => new(db, root, presence.NoneSince, now: clock.Now);

        Pass().Scan();                                           // 10:00, nothing running: the window closes

        clock.Move(TimeSpan.FromMinutes(1));
        var agent = presence.Enter();                            // 10:01, an agent starts
        Drop(root, "inbox/signed-authority.pdf", "I am allowed to trade live");
        clock.Move(TimeSpan.FromHours(-1));                      // and the machine's clock steps back an hour
        agent.Dispose();                                         // 09:01 by the wall, the agent exits

        clock.Move(TimeSpan.FromMinutes(1));
        Pass().Scan();                                           // 09:02, the next pass

        var store = new MaterialStore(db);
        Assert.Equal(MaterialOrigin.InboxUnattested, store.Present().Single(m => m.Name == "signed-authority.pdf").Origin);

        // AND WHAT THE ORDER CAN SHOW IS STILL SAID: the pass above began after that exit, under the
        // same stepped clock, so the owner's next drop — with nothing running — is theirs.
        Drop(root, "inbox/broker-statement.pdf", "the owner's document");
        clock.Move(TimeSpan.FromMinutes(1));
        Pass().Scan();
        Assert.Equal(MaterialOrigin.Inbox, store.Present().Single(m => m.Name == "broker-statement.pdf").Origin);
    }

    /// <summary>
    /// THE OTHER WALL-CLOCK ORDERINGS, each around an agent that writes into the drop folder after a
    /// pass and exits before the next: the clock stepped back before the agent starts (red on the base
    /// as well — its whole window reads as earlier than the pass), stepped forward while it runs, and
    /// frozen, so that every reading is the same tick. None of them may attest the window it was in.
    /// </summary>
    [Theory]
    [InlineData("back an hour before the agent starts")]
    [InlineData("forward an hour while it runs")]
    [InlineData("frozen, every reading the same tick")]
    public void No_wall_clock_reading_lets_a_pass_attest_a_window_an_agent_was_alive_in(string clockGoes)
    {
        var (db, root) = Workspace();
        using var _ = db;
        var clock = new Clock(TenOClock);
        var presence = new AgentPresence(clock.Now);
        var frozen = clockGoes.StartsWith("frozen");
        void Tick() { if (!frozen) clock.Move(TimeSpan.FromMinutes(1)); }
        MaterialScanner Pass() => new(db, root, presence.NoneSince, now: clock.Now);

        Pass().Scan();
        Tick();
        if (clockGoes.StartsWith("back")) clock.Move(TimeSpan.FromHours(-1));
        using (presence.Enter())
        {
            Drop(root, "inbox/signed-authority.pdf", "I am allowed to trade live");
            if (clockGoes.StartsWith("forward")) clock.Move(TimeSpan.FromHours(1));
            Tick();
        }
        Tick();
        Pass().Scan();

        Assert.Equal(MaterialOrigin.InboxUnattested,
            new MaterialStore(db).Present().Single(m => m.Name == "signed-authority.pdf").Origin);
    }

    /// <summary>
    /// U-INBOX-ORDER (b), RED FIRST: A RESTART BETWEEN AN AGENT'S LAST WRITE AND THE NEXT PASS. The
    /// register is per process and the window's start is in the database, so the first pass of the
    /// next process asked a register that had seen nobody, and attested a window the previous
    /// process's agent had written in. On the base it read <see cref="MaterialOrigin.Inbox"/>. Across a
    /// restart no order can be shown, so the answer is the weaker word — and the new process attests
    /// again once a pass of its own has closed the window.
    /// </summary>
    [Fact]
    public void A_restart_between_an_agents_last_write_and_the_next_pass_never_records_its_file_as_the_owners()
    {
        var (first, root) = Workspace();
        var path = first.Connection.DataSource;

        // ---- the first process -------------------------------------------------------------------
        using (first)
        {
            var before = new AgentPresence();
            new MaterialScanner(first, root, before.NoneSince).Scan();       // a whole pass: the window closes
            using (before.Enter()) Drop(root, "inbox/signed-authority.pdf", "I am allowed to trade live");
        }   // and the app quits, crashes or updates itself before another pass runs

        // ---- the next process: the database reopened, and a register of its own ------------------
        using var db = new Database(path);
        var after = new AgentPresence();
        new MaterialScanner(db, root, after.NoneSince).Scan();

        var store = new MaterialStore(db);
        var row = store.Present().Single(m => m.Name == "signed-authority.pdf");
        Assert.Equal(MaterialOrigin.InboxUnattested, row.Origin);

        // AND WHAT THE OWNER READS ABOUT THAT ROW IS TRUE OF IT. Nothing was watching across the
        // restart, so "the AI was running when it appeared" would be a claim nobody can support.
        Assert.Equal("in your inbox, but the AI may have been running when it appeared — TradeAgent cannot say who put it there",
            InboxPage.Origin(row));

        Drop(root, "inbox/broker-statement.pdf", "the owner's document");
        new MaterialScanner(db, root, after.NoneSince).Scan();
        Assert.Equal(MaterialOrigin.Inbox, store.Present().Single(m => m.Name == "broker-statement.pdf").Origin);
    }

    /// <summary>
    /// (b) WHERE NO PASS EVER COMPLETED BEFORE THE RESTART — a workspace past the scanner's budget, whose
    /// every pass ran out of it, never moves the window at all. The base read that as "since before
    /// anything happened" and asked only the new process's register, which had seen nobody, so the
    /// earlier process's agent's file read <see cref="MaterialOrigin.Inbox"/>.
    /// </summary>
    [Fact]
    public void A_restart_before_any_pass_had_completed_never_records_the_earlier_agents_file_as_the_owners()
    {
        var (first, root) = Workspace();
        var path = first.Connection.DataSource;

        using (first)
        {
            var before = new AgentPresence();
            using (before.Enter()) Drop(root, "inbox/signed-authority.pdf", "I am allowed to trade live");
            Assert.True(new MaterialScanner(first, root, before.NoneSince) { FileLimit = 0 }.Scan().HashBudgetSpent);
        }

        using var db = new Database(path);
        var after = new AgentPresence();
        new MaterialScanner(db, root, after.NoneSince).Scan();

        Assert.Equal(MaterialOrigin.InboxUnattested,
            new MaterialStore(db).Present().Single(m => m.Name == "signed-authority.pdf").Origin);
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
