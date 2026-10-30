using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class CouncilLoopTests
{
    /// <summary>
    /// (w6) THE OWNER'S WAKE-ONCE IS ONE LAUNCH OF THE CHIEF, AND ITS CEILING INTERVAL IS LEFT AS IT WAS (<c>U-chief-wake-once</c>). An active
    /// puppet chief on the seam host — never a model — is floored at its ceiling a day out and launched by nothing; the owner's press raises
    /// one wake, and the next pass launches the chief ONCE on it, told why; the wake is consumed by that launch; the chief's floor is then
    /// its new turn plus the SAME interval; and later passes launch nothing more — the press bought one turn and no clock.
    /// </summary>
    [Fact]
    public async Task The_owners_wake_once_is_one_launch_of_the_chief_and_leaves_its_ceiling_interval()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var now = Noon();
        var org = new OrgStore(db);
        var chief = org.AppointChief(new ChiefSeat("puppet", "scripted-1"), r => r == "puppet", now).Chief!;
        org.Activate(chief, _ => SeatClasses.Puppet, 3, now, () => false);
        var host = new CouncilHost(db, root, new Concurrency()) { Clock = () => now }.Add(chief);
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        await loop.TurnAsync();
        Assert.Empty(host.Opened);
        var floor = Assert.Single(Ceilings(host, chief));
        Assert.Equal(now + MissionOptions.DefaultCeiling, floor.DueAt);

        var facts = new ChiefWakeFacts(AgentStarted: true, WorksOnItsOwn: true, ProvenCell: true, MissionOptions.DefaultCeiling);
        var woken = org.WakeChiefOnce(org.PreviewChiefWake(facts), facts, now);
        await loop.TurnAsync();

        var launch = Assert.Single(host.Opened);
        Assert.Equal(chief, launch.Role);
        Assert.Contains("Wake the chief once", launch.Prompt, StringComparison.Ordinal);
        var taken = Row(host, woken.WakeId)!;
        var turned = new AiAttemptStore(db).LastWoken(chief)!;
        Assert.True(taken.Consumed);
        Assert.Equal(turned.Id, taken.ConsumedBy);
        var next = Assert.Single(Ceilings(host, chief), e => !e.Consumed);
        Assert.Equal(turned.StartedAt + MissionOptions.DefaultCeiling, next.DueAt);

        now = now.AddHours(1);
        await loop.TurnAsync();
        await loop.TurnAsync();
        Assert.Single(host.Opened);
    }
}
