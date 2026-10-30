using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// <c>U-WAKE-ONCE-HOST-FACTS-TEST</c> (the card from U-chief-wake-once's landing): THE WAKE-ONCE PRESS'S CHECKS AT THE APP-HOST LEVEL. The store's
/// refusals are tested over facts the test hands it (<c>OrgChiefTests</c>); what had no test is the app reading those facts from itself —
/// above all the Codex review's pause-race fix: the AI counts as WORKING ON ITS OWN only while the owner's setting is on AND the loop is
/// running. While a Pause unwinds a turn the card still reads Working and the loop is already stopped; a host that read the setting alone
/// would wake a chief that no loop will take. A real composed host (<see cref="PuppetLife"/>: the host's own loop, meter and gateway over a
/// puppet seat), an active puppet chief and a proof the host holds; no model and no AI cost.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class ChiefWakeHostFactsTests : IDisposable
{
    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    /// <summary>An ACTIVE puppet chief and a cell proof this host holds, written before the host composes.</summary>
    static void ChiefAndProof(Database db)
    {
        var org = new OrgStore(db);
        var chief = org.AppointChief(new ChiefSeat("puppet", "scripted-1"), runtime => runtime is "puppet", DateTimeOffset.UtcNow).Chief!;
        org.Activate(chief, _ => SeatClasses.Puppet, 3, DateTimeOffset.UtcNow, () => false);
        new CellProofStore(db).Record("puppet-proof", "profile-1", ["none"]);
    }

    /// <summary>
    /// (1) THE AI IS WORKING ON ITS OWN ONLY WHILE THE SETTING IS ON AND THE LOOP IS RUNNING. The owner's setting is on from the start. Loop not
    /// running (never started, or paused with the setting still on — the pause race): press one is refused in words, and press two over an
    /// armed preview writes nothing. Loop running: the preview is armed and names the chief. Setting off with the loop running: refused again.
    /// WATCHED MUTANT: <c>AppHost.WakeFacts</c> reading the setting alone (loop running not asked) → the first and the paused refusals fail.
    /// </summary>
    [Fact]
    public async Task The_wake_once_press_counts_the_ai_as_working_only_with_the_setting_on_and_the_loop_running()
    {
        await using var life = await PuppetLife.Begin("wake-facts", ChiefAndProof, settings: s => s.AiWorksOnItsOwn = true);
        var host = life.Host;
        var chief = new OrgStore(host.Db).ActiveChief()!;
        life.Script(chief, ["usage 10 0 5", "final Noted."]);
        long Wakes() => host.Wakes!.OfKind(MissionEventKind.OwnerWake).Count;
        Assert.True(host.CellProofHeld, "the host holds no proof, so the press would be refused for that reason and not the one under test");
        Assert.True(host.Gateway.Settings.AiWorksOnItsOwn);

        // THE SETTING IS ON AND THE LOOP HAS NOT BEEN STARTED: the AI is started (a conversation exists) but not working on its own.
        Assert.False(host.Mission.Running);
        var idle = host.PreviewChiefWake();
        Assert.Contains("The AI is paused", idle.Refusal, StringComparison.Ordinal);
        Assert.Contains("Let the AI work on its own", idle.Refusal, StringComparison.Ordinal);

        // THE LOOP RUNS: press one arms, naming the chief.
        host.Mission.Start();
        Assert.True(host.Mission.Running);
        var armed = host.PreviewChiefWake();
        Assert.Null(armed.Refusal);
        Assert.Equal(chief, armed.Chief);

        // THE PAUSE RACE: the loop stops with the owner's setting still on. Press two over the preview armed a moment ago is refused in the
        // same words, and nothing is written.
        await host.Mission.PauseAsync();
        Assert.False(host.Mission.Running);
        Assert.True(host.Gateway.Settings.AiWorksOnItsOwn);
        var before = Wakes();
        var pressed = host.WakeChiefOnce(armed);
        Assert.Contains("The AI is paused", pressed, StringComparison.Ordinal);
        Assert.Equal(before, Wakes());

        // THE LOOP RUNS AGAIN BUT THE OWNER HAS TURNED THE SETTING OFF: still refused.
        host.Mission.Start();
        host.Gateway.Update(s => s.AiWorksOnItsOwn = false);
        Assert.Contains("Let the AI work on its own", host.PreviewChiefWake().Refusal, StringComparison.Ordinal);
        Assert.Equal(before, Wakes());

        // BOTH TRUE AGAIN: press two over a fresh preview writes the one wake the press owes.
        host.Gateway.Update(s => s.AiWorksOnItsOwn = true);
        var again = host.PreviewChiefWake();
        Assert.Null(again.Refusal);
        Assert.Contains($"You woke the chief {chief} once", host.WakeChiefOnce(again), StringComparison.Ordinal);
        Assert.Equal(before + 1, Wakes());
    }
}
