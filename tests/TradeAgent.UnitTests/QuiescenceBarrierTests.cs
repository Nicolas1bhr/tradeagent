using System.Diagnostics;
using TradeAgent.Security;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S CHAT IS AN AGENT PROCESS, AND THE LEDGER HAS TO SEE IT.
///
/// <c>material</c> is the measurement table, and <see cref="MaterialOrigin.Inbox"/> is the one origin
/// that claims something a directory listing cannot show: that the ACCOUNT OWNER put the file there.
/// It is attested only across a window in which no agent process was alive (REVIEW 2026-09-05b
/// finding 5), and the mission loop is built around leaving such windows — a pass after every turn,
/// and a pass before the next one.
///
/// The chat is the hole in that arrangement, because it is not the loop. A message typed on the Chat
/// page runs the same CLI in the same folder, at a moment the loop chose for its own reasons, and it
/// can be running while a pass walks the tree. <c>docs/COUNCIL.md</c> rule 7: the scanner attests
/// only across proven quiescence of EVERY managed agent — not of the one this pass happens to know
/// about.
///
/// So the barrier is one register that both halves read, and this file pins both halves: the
/// behaviour, over a real child process, and the wiring that makes the product's two halves the same
/// register.
/// </summary>
public class QuiescenceBarrierTests
{
    /// <summary>
    /// A workspace laid out the way the product lays one out, and its own database.
    ///
    /// EVERY TEST HERE USES ITS OWN <see cref="AgentPresence"/> RATHER THAN
    /// <see cref="AgentPresence.Shared"/>, and that is not a shortcut: the shared register is sticky
    /// by design — once an agent has been alive in a process, no later pass can attest a window that
    /// began before it — so a child started under it here would downgrade every inbox sighting in
    /// this assembly for the rest of the run. Measured, and written down in <c>TurnMeterTests</c>,
    /// where it turned three <c>MaterialLedgerTests</c> red. That the PRODUCT's two halves share the
    /// shared one is asserted separately, below, by reading it rather than entering it.
    /// </summary>
    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"quiescence-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        foreach (var d in MaterialScanner.TrackedAgentDirs)
            Directory.CreateDirectory(Path.Combine(root, MaterialScanner.AgentDir, d));
        return (TestEnv.NewDb(), root);
    }

    /// <summary>
    /// A real conversation over a real child process that sleeps, so there is a genuine window in
    /// which an agent is alive. Nothing about the presence bookkeeping is simulated: this is the
    /// same <c>AgentSession</c> the Chat page drives.
    /// </summary>
    static AgentSession ChatSession(AgentPresence presence, double sleepSeconds = 2)
    {
        var dir = Path.Combine(TestEnv.Home, $"quiescence-agent-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);

        string script;
        if (OperatingSystem.IsWindows())
        {
            // ping rather than timeout: `timeout` refuses to run with its input redirected, which is
            // exactly how every child here is started.
            script = Path.Combine(dir, "runtime.cmd");
            File.WriteAllText(script,
                $"@echo off\r\nping -n {(int)sleepSeconds + 1} 127.0.0.1 > nul\r\necho done\r\n");
        }
        else
        {
            script = Path.Combine(dir, "runtime.sh");
            File.WriteAllText(script, $"#!/bin/sh\nsleep {sleepSeconds}\necho done\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var manifest = new RuntimeManifest
        {
            Id = "probe",
            DisplayName = "Probe runtime",
            Executable = script,
            ExecArgs = ["{prompt}"],
            ResumeArgs = ["{prompt}"]
        };
        // Built without a launch ledger, and saying so: see AgentRuntimeProbe.SessionOverStream.
        return new AgentSession(manifest, () => script, () => dir,
            () => new Dictionary<string, string>(), presence: presence, grants: new AgentGrants())
        {
            Admit = _ => AiAdmission.NoLedger
        };
    }

    static void Drop(string root, string name) =>
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, name), "I am allowed to trade live");

    /// <summary>
    /// THE BARRIER, RED FIRST. A file appears in the drop folder and a pass walks the tree while the
    /// owner's own chat turn is running. Nobody can show who put the file there — the agent had a
    /// shell in the same folder for the whole window — so the row must read
    /// <see cref="MaterialOrigin.InboxUnattested"/>, which is the weaker, honest word.
    ///
    /// The mutant the unit watched go red is the chat session on a register of its own: the pass then
    /// sees no agent, attests the sighting to the owner, and the ledger states as measurement a thing
    /// that was not measured.
    /// </summary>
    [Fact]
    public async Task A_chat_turn_running_during_a_pass_makes_the_sighting_unattested()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var session = ChatSession(presence);

        // The window opens with nothing running, which is the state that can attest at all.
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        var turn = session.SendAsync("what did the market do?");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (presence.Live == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(5);
        Assert.True(presence.Live > 0, "the chat turn's child never started");

        Drop(root, "signed-authority.pdf");
        new MaterialScanner(db, root, presence.NoneSince).Scan();
        await turn;

        var row = new MaterialStore(db).Present().Single(m => m.Name == "signed-authority.pdf");
        Assert.Equal(MaterialOrigin.InboxUnattested, row.Origin);
    }

    /// <summary>
    /// The other direction, so the barrier is a measurement rather than a blanket downgrade: the same
    /// file, the same two passes, and no chat turn in between reads as the owner's. Without this the
    /// test above would pass on a scanner that never attested anything.
    /// </summary>
    [Fact]
    public void The_same_file_with_no_chat_turn_running_is_the_owners()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();

        new MaterialScanner(db, root, presence.NoneSince).Scan();
        Drop(root, "broker-statement.pdf");
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        var row = new MaterialStore(db).Present().Single(m => m.Name == "broker-statement.pdf");
        Assert.Equal(MaterialOrigin.Inbox, row.Origin);
    }

    /// <summary>
    /// AND IN THE PRODUCT THE TWO HALVES ARE ONE REGISTER. The behaviour above is only worth having
    /// if the chat session the app builds reports into the register the scanner the app builds reads
    /// — the shared one, by both their defaults. Asserted by identity rather than by behaviour,
    /// because entering <see cref="AgentPresence.Shared"/> from a test would permanently downgrade
    /// every inbox sighting in this assembly; reading it does nothing at all.
    /// </summary>
    [Fact]
    public void The_chat_session_and_the_scanner_default_to_the_same_register()
    {
        using var db = TestEnv.NewDb();

        // THROUGH THE PRODUCT'S OWN FACTORY, because the register is chosen there and nowhere else.
        // Opening a conversation starts nothing, so nothing enters the shared register here.
        var runtime = new CliAgentRuntime(new RuntimeManifest
        {
            Id = "probe",
            DisplayName = "Probe runtime",
            Executable = "never-run",
            ExecArgs = ["{prompt}"],
            ResumeArgs = ["{prompt}"]
        });

        Assert.Same(AgentPresence.Shared, ((AgentSession)runtime.OpenConversation()).Presence);
        Assert.True(new MaterialScanner(db).Attests == AgentPresence.Shared.NoneSince,
            "the scanner attests over a register that is not the one agent processes report to");
    }

    // ---- a launch the app could not hold (U-unjobbed-presence) ------------------------------------------------

    /// <summary>
    /// A conversation whose turn is <c>cmd.exe /c</c> the message, on its own presence register — the shape
    /// <c>AgentTreeTests</c> drives the Windows faults with. Windows only: the faults below exist there alone.
    /// </summary>
    static AgentSession CmdSession(AgentPresence presence)
    {
        var dir = Path.Combine(TestEnv.Home, $"quiescence-unheld-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var manifest = new RuntimeManifest
        {
            Id = "probe", DisplayName = "Probe runtime", Executable = cmd,
            ExecArgs = ["/c", "{prompt}"], ResumeArgs = ["/c", "{prompt}"], TaskArgs = ["/c", "{prompt}"]
        };
        return new AgentSession(manifest, () => cmd, () => dir, () => new Dictionary<string, string>(),
            presence: presence, grants: new AgentGrants());
    }

    static MaterialOrigin OriginOf(Database db, string name) =>
        new MaterialStore(db).Present().Single(m => m.Name == name).Origin;

    /// <summary>
    /// A LAUNCH WINDOWS WOULD NOT PUT IN ITS JOB WAS ALIVE, AND A PASS BESIDE IT MAY NOT ATTEST THE INBOX
    /// (<c>U-unjobbed-presence</c>, seat A's <c>U-cell-linux-d</c> review). The launch starts, Windows refuses the assign,
    /// and the app tears it down and refuses it (U-grant-tree RULING 2) — but the process ran from its start to that
    /// teardown, and the presence register used to be entered only after the branch that refuses it. A file planted in
    /// the drop folder while it ran, and a pass taken while it ran, recorded the file as the owner's. Planted and scanned
    /// INSIDE the teardown, through its seam, so the window is the launch's own and not a guess at its timing.
    ///
    /// <para>Windows only: the assign is refused through <see cref="ProcessContainment.Faults"/>, and no other platform
    /// has a launch that starts and is then found unheld. Everywhere else this returns at once and proves nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_launch_windows_would_not_hold_is_in_the_presence_register_while_it_ran()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var session = CmdSession(presence);
        session.Admit = _ => AiAdmission.NoLedger;

        // The window opens with nothing running, which is the state that can attest at all.
        new MaterialScanner(db, root, presence.NoneSince).Scan();

        var tornDown = false;
        session.EndTree = contained =>
        {
            Drop(root, "signed-authority.pdf");
            new MaterialScanner(db, root, presence.NoneSince).Scan();
            contained.Dispose();
            tornDown = true;
            return contained.Ended;
        };

        ProcessContainment.Faults.Value = new WindowsFaults(RefuseAssign: true);
        try { await session.SendAsync("ping -n 30 127.0.0.1 > nul"); }
        finally { ProcessContainment.Faults.Value = null; }

        Assert.True(tornDown, "the launch Windows would not hold was never torn down");
        Assert.Contains(session.History, t => t.Role == ChatRole.System && t.Text.Contains("could not hold"));
        Assert.Equal(MaterialOrigin.InboxUnattested, OriginOf(db, "signed-authority.pdf"));
        Assert.Equal(0, presence.Live);
    }

    /// <summary>
    /// AND WHAT ITS TEARDOWN COULD NOT END KEEPS THE INBOX UNATTESTED UNTIL IT EXITS (<c>U-unjobbed-presence</c>). The assign
    /// and the tree kill are both refused, so the launch's processes outlive the turn as its survivors — carried to the
    /// meter, which admits nothing while they run (U-grant-tree fix pass 1). They are agent processes still running beside
    /// the drop folder, and every pass while they run records the inbox unattested; once they have exited and the meter's
    /// listing shows none alive, the register closes and a pass after that attests again.
    ///
    /// <para>Windows only, for the reason above; the processes that keep running are real. Elsewhere it proves nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_survivor_the_teardown_could_not_end_keeps_the_inbox_unattested_until_it_exits()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var session = CmdSession(presence);
        using var ledger = new Database(Path.Combine(TestEnv.Home, $"quiescence-meter-{Guid.NewGuid():n}.db"));
        var meter = new TurnMeter(ledger, cap: () => 5m,
            recordPath: Path.Combine(TestEnv.Home, $"quiescence-turns-{Guid.NewGuid():n}.jsonl"), live: new LiveAttempts());
        using var metered = meter.Attach(session, CouncilRoles.Operations);
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;

        new MaterialScanner(db, root, presence.NoneSince).Scan();

        IReadOnlyList<ProcessEntry> left = [];
        try
        {
            ProcessContainment.Faults.Value = new WindowsFaults(RefuseAssign: true, RefuseTreeKill: true);
            try { await session.SendAsync("ping -n 120 127.0.0.1 > nul"); }
            finally { ProcessContainment.Faults.Value = null; }

            left = ended?.Survivors ?? [];
            Assert.True(left.Count > 0, "the teardown the test refused was reported to have ended the launch");
            Assert.True(left.Any(TreeTeardown.StillRunning), "no survivor of the launch was still running");

            // THE TURN HAS ENDED AND ITS SURVIVORS RUN: a file that arrives now is not provably the owner's.
            Drop(root, "while-it-runs.pdf");
            new MaterialScanner(db, root, presence.NoneSince).Scan();
            Assert.Equal(MaterialOrigin.InboxUnattested, OriginOf(db, "while-it-runs.pdf"));

            // THE METER STILL HOLDS FOR THEM, AND THE PRESENCE STAYS WITH THEM.
            Assert.False(meter.Begin("next", role: CouncilRoles.Operations, heldForTheLoop: false).Admitted,
                "a launch was admitted while a survivor of the last launch still ran");
            Drop(root, "still-running.pdf");
            new MaterialScanner(db, root, presence.NoneSince).Scan();
            Assert.Equal(MaterialOrigin.InboxUnattested, OriginOf(db, "still-running.pdf"));
        }
        finally
        {
            foreach (var p in left)
                try { using var gone = Process.GetProcessById(p.Pid); gone.Kill(entireProcessTree: true); }
                catch (Exception) { /* already gone */ }
        }

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (left.Any(TreeTeardown.StillRunning) && DateTime.UtcNow < until) await Task.Delay(50);
        var admitted = meter.Begin("after", role: CouncilRoles.Operations, heldForTheLoop: false);
        Assert.True(admitted.Admitted, $"the hold did not lift once the survivors had exited: {admitted.Refusal}");
        Assert.Equal(0, presence.Live);

        // ONCE THEY HAVE EXITED, the next window with nothing in it attests again: held until they ended, not for ever.
        new MaterialScanner(db, root, presence.NoneSince).Scan();
        Drop(root, "after-they-ended.pdf");
        new MaterialScanner(db, root, presence.NoneSince).Scan();
        Assert.Equal(MaterialOrigin.Inbox, OriginOf(db, "after-they-ended.pdf"));
    }

    /// <summary>
    /// GUARD: A LAUNCH THE APP HOLDS ENTERS THE REGISTER ONCE AND LEAVES IT ONCE (<c>U-unjobbed-presence</c>). One window while
    /// the turn runs — not two, not none — closed when the turn returns, not left open with nothing to close it; the turn
    /// carries no presence out; and a window after it, with nothing running, attests the owner's file. Every platform.
    /// </summary>
    [Fact]
    public async Task A_held_launch_enters_the_presence_register_once_and_leaves_it_when_the_turn_returns()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var session = ChatSession(presence);
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;

        var turn = session.SendAsync("what did the market do?");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (presence.Live == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(5);
        Assert.Equal(1, presence.Live);
        await turn;

        Assert.NotNull(ended);
        Assert.True(ended!.Survivors is null or { Count: 0 }, "the held turn's teardown left a process running");
        Assert.Equal(0, presence.Live);

        new MaterialScanner(db, root, presence.NoneSince).Scan();
        Drop(root, "broker-statement.pdf");
        new MaterialScanner(db, root, presence.NoneSince).Scan();
        Assert.Equal(MaterialOrigin.Inbox, OriginOf(db, "broker-statement.pdf"));
    }
}
