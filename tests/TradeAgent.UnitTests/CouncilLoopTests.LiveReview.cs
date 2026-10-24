using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class CouncilLoopTests
{
    /// <summary>
    /// A REVIEW INTERVAL SET AFTER THE LOOP STARTED GOVERNS ITS NEXT LOOK (<c>U-board-b1</c> fix pass, the review's P2): the loop asks
    /// the interval at every scheduling, so after the policy's interval moves from 30 to 90 minutes, the look Research's turn raises is
    /// 90 minutes out — not the 60 a start-time copy of 30 would give. RED before the fix: 60.
    /// </summary>
    [Fact]
    public async Task A_review_interval_set_after_start_governs_the_next_look()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true };
        var every = TimeSpan.FromMinutes(30);
        var now = Noon();
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = every, LiveReviewEvery = () => every }, now: () => now);

        await loop.TurnAsync();
        var first = Assert.Single(host.Events!.OfKind(MissionEventKind.Review), e => e.For == CouncilRoles.Research && !e.Consumed);
        Assert.Equal(TimeSpan.FromMinutes(30), first.DueAt - now);

        every = TimeSpan.FromMinutes(90);
        now = first.DueAt;
        await loop.TurnAsync();
        var next = Assert.Single(host.Events!.OfKind(MissionEventKind.Review), e => e.For == CouncilRoles.Research && !e.Consumed);
        Assert.Equal(TimeSpan.FromMinutes(90), next.DueAt - now);
    }
}
