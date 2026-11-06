using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// WHAT A STOP DOES TO THE LEDGER'S QUOTAS (R30 D19; R28 P4), AND WHAT IT CANNOT REACH: an inconclusive slot was held 90 days and never freed, because
/// occupancy is counted from allocations and a stop ended none. A stop now frees it, and it reaches no live row.
/// </summary>
public partial class PaperAllocationGateTests
{
    /// <summary>
    /// A STOP FREES THE PAIR'S ONE INCONCLUSIVE SLOT (R28 P4, by consequence of D19): of three slots one is the quota, and A2 waits behind A1 in the
    /// same lineage. A1's run stopped, both the envelope's third and the lineage fence read the slot as free and A2 takes it. A1 is never given another:
    /// its one term is spent. RED while a stop left the row standing: the slot stayed A1's for the rest of its 90 days.
    /// </summary>
    [Fact]
    public async Task A_stop_frees_the_inconclusive_slot_and_never_gives_the_version_another()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        var envelope = Slots(gw, 3);
        var a1 = Judged(db, PromotionVerdict.Refused, 110, PromotionReason.NoTrade, at: At.AddHours(-5));
        var a2 = Judged(db, PromotionVerdict.Refused, 111, PromotionReason.NoTrade, CampaignOf(gw, a1), At.AddHours(-4));

        Assert.Equal(1, gw.AllocatePaperDue(At));
        Assert.Equal(new[] { a1 }, Inconclusives(gw, envelope, At));

        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        await gw.StopPaperDeploymentAsync(gw.Deployments.Open().Single().Id, "pos-head");

        Assert.Empty(Inconclusives(gw, envelope, At));
        var written = gw.AllocatePaperDue(At);

        log.WriteLine($"after the stop: written {written}, on paper {string.Join(", ", Inconclusives(gw, envelope, At).Select(v => v[..12]))}");
        Assert.Equal(1, written);
        Assert.Equal(new[] { a2 }, Inconclusives(gw, envelope, At));
        Assert.Equal(0, gw.AllocatePaperDue(At));
        Assert.Single(gw.Allocations.For(a1), a => a.PolicyVersion == AllocationPolicy.InconclusiveV1);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A ROW IN FORCE IS NOT PUSHED OFF THE LIST BY NEWER ROWS THAT ARE NOT (the Codex review's P2): the bound counts what stands. With the bound read first, a
    /// stop row newer than a standing allocation hid it, and enough stops would have stopped the older version's replacement ever being started.
    /// </summary>
    [Fact]
    public async Task The_paper_list_bounds_what_stands_and_not_what_was_written()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        var envelope = Slots(gw, 2);
        var kept = Judged(db, PromotionVerdict.PaperEligible, 103);
        Assert.True(gw.Allocations.RecordPaper(PaperRowFor(gw, kept, envelope, quantity: 1m), At).Ok);

        var later = At.AddMinutes(1);
        var stopped = Judged(db, PromotionVerdict.PaperEligible, 104);
        var row = gw.Allocations.RecordPaper(PaperRowFor(gw, stopped, envelope, quantity: 1m) with { EffectiveFrom = later, At = later }, later);
        Assert.True(row.Ok, row.Why);
        Assert.True(gw.Allocations.RecordStop(row.Allocation!.Id, "stopped by pos-head", later.AddMinutes(1)).Ok);

        var listed = gw.Allocations.PaperStanding(later.AddMinutes(2), limit: 1);
        Assert.Equal(kept, Assert.Single(listed).Allocation.VersionId);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A STOP REACHES NOTHING BUT PAPER (the unit's "no path toward live"): a live row offered to it is refused in words and stays in force; a paper stop
    /// of a version that also holds capital leaves the capital where it is; a stop row is refused by both writers that could otherwise make it stand; and
    /// an id this ledger does not hold writes nothing.
    /// </summary>
    [Fact]
    public async Task A_stop_reaches_nothing_but_paper()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        var envelope = await Envelope(gw, quantity: 5m);
        var version = Judged(db, PromotionVerdict.Promoted);
        var promotion = gw.Promotions.Standing(version).Promotion!;

        var live = gw.Allocations.Record(new AllocationRow("", version, promotion.Id, AllocationPolicy.V1, 2m, null, "USD", At, null, "test: capital", At));
        Assert.True(live.Ok, live.Why);

        var refused = gw.Allocations.RecordStop(live.Allocation!.Id, "stopped by pos-head", At);
        log.WriteLine($"a live row: {refused.Ok} — {refused.Why}");
        Assert.False(refused.Ok);
        Assert.Contains("paper", refused.Why, StringComparison.Ordinal);
        Assert.False(gw.Allocations.RecordStop("no-such-row", "stopped by pos-head", At).Ok);
        Assert.DoesNotContain(gw.Allocations.For(version), a => a.IsStop);

        Assert.True(gw.Allocations.RecordPaper(PaperRowFor(gw, version, envelope), At).Ok);
        var paper = gw.Allocations.For(version).Single(a => a.IsPaper);
        var stop = gw.Allocations.RecordStop(paper.Id, "stopped by pos-head", At);
        Assert.True(stop.Ok, stop.Why);

        Assert.Null(gw.Allocations.StandingForPaper(version, gw.Connector.Id, envelope.AccountId, At));
        Assert.Equal(live.Allocation.Id, gw.Allocations.StandingForLive(version, At)!.Allocation.Id);

        var asPaper = gw.Allocations.RecordPaper(stop.Allocation! with { Id = "", EffectiveTo = null }, At);
        Assert.False(asPaper.Ok);
        var asLive = gw.Allocations.Record(stop.Allocation! with { Id = "", Scope = null, EffectiveTo = null });
        Assert.False(asLive.Ok);
        Assert.Single(gw.Allocations.For(version), a => a.IsStop);
        await gw.DisposeAsync();
    }
}
