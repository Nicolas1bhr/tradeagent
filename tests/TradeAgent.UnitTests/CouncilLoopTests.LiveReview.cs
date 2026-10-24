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

    /// <summary>
    /// A CLOCK NARROWED TO NONE WITHDRAWS ITS PENDING LOOKS AND CEILINGS (<c>U-board-b1</c> fix pass 2, the re-review's P2): a narrowing
    /// applies at once, so when the review interval and the levels' ceilings become zero ("none") a look and a head's ceiling already
    /// pending are closed WITHOUT a turn — withdrawn, with the reason — and nothing launches when their due time passes. RED before the
    /// fix: both launched. Watched red with the ceiling's withdrawal removed.
    /// </summary>
    [Fact]
    public async Task A_clock_narrowed_to_none_withdraws_its_pending_looks_and_ceilings()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Unit(db, "team-head", OrgIds.Research, null);
        Seat(db, Pos, "team-head", heads: true);
        var now = Noon();
        Turned(db, Pos, now.AddHours(-2));
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Clock = () => now }.Add(Pos);
        var every = TimeSpan.FromMinutes(30);
        var ceiling = MissionOptions.DefaultCeiling;
        var loop = new MissionLoop(host, new MissionOptions
        {
            ReviewEvery = every, LiveReviewEvery = () => every,
            LiveCeilings = () => OrgLevel.All.ToDictionary(l => l, _ => ceiling, StringComparer.Ordinal)
        }, now: () => now);

        await loop.TurnAsync();
        var look = Assert.Single(host.Events!.OfKind(MissionEventKind.Review), e => e.For == CouncilRoles.Research && !e.Consumed);
        var floor = Assert.Single(Ceilings(host, Pos), e => !e.Consumed);
        Assert.Empty(host.Opened);

        every = TimeSpan.Zero;
        ceiling = TimeSpan.Zero;
        now = (look.DueAt > floor.DueAt ? look.DueAt : floor.DueAt).AddMinutes(1);
        await loop.TurnAsync();

        Assert.Empty(host.Opened);
        foreach (var id in new[] { look.Id, floor.Id })
        {
            var row = Row(host, id)!;
            Assert.True(row.Consumed, $"{id} is still pending");
            Assert.Equal(MissionEventDisposition.Withdrawn, row.Disposition);
            Assert.Null(row.ConsumedBy);
        }
    }
}
