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
        return new AgentSession(manifest, () => script, () => dir,
            () => new Dictionary<string, string>(), presence: presence);
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
}
