using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE OWNER'S RELEASE LIFTS THE HOLD AND NOTHING ELSE (<c>U-reopen-2</c>, item 2).
///
/// <para>A scope held for review is out of reach of code: nothing in this product lifts one but a
/// person, in process, on a two-press card with a required note. What the press does NOT do is the
/// half that matters here. It does not write the receipt, it does not admit an order, and it does
/// not certify the state of the book — every condition <c>U-reopen-1</c> put on a reopen still has
/// to be met by a tick that looked. The owner is answering the question the hold asked ("should this
/// go on trading at all"), and they are not being asked to vouch for a platform the software can
/// read for itself.</para>
///
/// <para>Everything goes through <c>RecordingConnector</c>, so "nothing was sent" is a count at the
/// wire rather than a claim about the gateway's intentions.</para>
/// </summary>
public class LossReleaseTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Day = TimeSpan.FromHours(24);
    const string Note = "Checked ATAS and the journal: the ES strategy is being run over by the open, paused it";

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready()
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            EmergencyBudget = Unresolved.PressBudget
        });
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    /// <summary>One whole episode: opened, driven through the budget, closed on two pulls, flattened.</summary>
    static async Task BreachAndFlatten(TradingGateway gw, RecordingConnector conn, TestClock clock,
        string tag, decimal budget, ITestOutputHelper log)
    {
        gw.Update(s => s.Risk.MaxDailyLoss = budget);
        await gw.PlaceAsync(new AgentContext("a"), tag, TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        var closing = await gw.LossWatchAsync();
        log.WriteLine($"{tag,-14} closed  : [{string.Join(", ", closing.Closed)}]");
        Assert.NotEmpty(closing.Closed);

        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);
    }

    /// <summary>
    /// Two episodes, the second of them held for review, with the second flatten's close acknowledged
    /// so the only thing left standing is the hold. Returns the second breach.
    /// </summary>
    static async Task<LossBreachRecord> HeldForReview(TradingGateway gw, RecordingConnector conn,
        TestClock clock, ITestOutputHelper log)
    {
        var account = conn.Broker.AccountId;

        await BreachAndFlatten(gw, conn, clock, "episode-one", 1_000m, log);
        var first = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        Assert.Single((await gw.LossWatchAsync()).Reopened);

        clock.MoveTo(new DateTimeOffset(2026, 3, 13, 9, 0, 0, TimeSpan.Zero));
        await BreachAndFlatten(gw, conn, clock, "episode-two", 2_000m, log);

        var second = gw.DayClosed(account)!;
        Assert.NotNull(gw.ReopenReading().HeldForReview);
        return second;
    }

    /// <summary>
    /// A RELEASE DOES NOT REOPEN ANYTHING — the item, and the mutant with it.
    ///
    /// <para>The flatten's close is taken by the broker and its acknowledgement is lost, so the
    /// PLATFORM's book is flat and TradeAgent is holding an order it cannot account for. That is the
    /// most dangerous state a reopen has to refuse, because every simple check passes — and it is
    /// exactly the state an owner pressing "reopen after review" is most likely to be in, having
    /// looked at ATAS and seen nothing open.</para>
    ///
    /// <para>So the press lifts the hold, and every condition <c>U-reopen-1</c> put on the receipt is
    /// still standing behind it. A release that wrote the receipt would put an agent back on an
    /// account with an unreconciled order on it.</para>
    /// </summary>
    [Fact]
    public async Task A_release_lifts_the_hold_and_the_reopen_still_has_to_be_earned()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await BreachAndFlatten(gw, conn, clock, "episode-one", 1_000m, log);
        var first = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        Assert.Single((await gw.LossWatchAsync()).Reopened);

        // EPISODE TWO, AND THE FLATTEN'S CLOSE IS LOST ON THE WAY BACK. The position goes; the row
        // stays flagged and UNKNOWN.
        clock.MoveTo(new DateTimeOffset(2026, 3, 13, 9, 0, 0, TimeSpan.Zero));
        gw.Update(s => s.Risk.MaxDailyLoss = 2_000m);
        await gw.PlaceAsync(new AgentContext("a"), "episode-two", TestEnv.Buy("ES"));
        conn.Faults.DropAfterBrokerAccept = 1;
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);

        var second = gw.DayClosed(account)!;
        log.WriteLine($"flatten flat   : {gw.FlattenToday(account)?.Flat}");
        log.WriteLine($"unconfirmed    : {gw.HasUnconfirmedWork()}");
        log.WriteLine($"book           : [{string.Join(" | ", conn.Broker.Positions.Where(p => p.Quantity != 0m).Select(p => $"{p.Symbol} {p.Quantity}"))}]");
        Assert.True(gw.HasUnconfirmedWork());
        Assert.NotNull(db.GetKv(LossHold.HoldKey(conn.Id, second)));

        var held = gw.ReopenReading();
        Assert.NotNull(held.HeldForReview);

        // THE OWNER LOOKS, AND PRESSES. One row, with the note and the episodes it acknowledges.
        var result = gw.ReleaseHold(Note);
        log.WriteLine($"release        : ok={result.Ok} [{string.Join(", ", result.Released)}]");
        Assert.True(result.Ok);
        var release = Json.Read<LossReleaseRecord>(db.GetKv(LossHold.ReleaseKey(conn.Id, second))!)!;
        log.WriteLine($"why            : {release.Why}");
        Assert.Equal(Note, release.Note);
        Assert.Equal(LossBreach.KeyFor(second), release.BreachKey);
        Assert.Equal(2, release.Episodes.Count);
        Assert.Contains(Note, release.Why, StringComparison.Ordinal);

        // THE HOLD IS LIFTED.
        var after = gw.ReopenReading();
        log.WriteLine($"held for review: {after.HeldForReview ?? "none"}");
        Assert.Null(after.HeldForReview);
        Assert.Equal(release.At, after.ReleasedAt);

        // AND NOTHING ELSE IS. Past every eligibility instant, on a book the platform reads flat,
        // the leg TradeAgent cannot account for still refuses the receipt — and nothing dispatches.
        clock.MoveTo(LossReopen.EligibleAt(second.ConfirmedAt, Day) + TimeSpan.FromHours(1));
        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Empty(pass.Reopened);
        Assert.Null(db.GetKv(LossReopen.KeyFor(conn.Id, second)));
        Assert.NotNull(gw.DayClosed(account));

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "released-but-flagged", TestEnv.Buy("ES")));

        // THE ORDER IS REFUSED BEFORE THE LOSS GATE EVEN SEES IT: a leg this app cannot account for
        // pauses ALL order flow, exactly as an owner's emergency press does, and that rule is ahead
        // of the closure's own. Either refusal is the right one; what this measures is that a
        // released hold does not put an order on the wire.
        log.WriteLine($"refusal        : {denied.Code}");
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, denied.Code);
        Assert.Equal(places, conn.Places);
        Assert.Null(gw.GetRequest("released-but-flagged"));

        // AND WHAT IS HOLDING IT IS NOW THE FLAGGED LEG AND NOT THE REVIEW.
        var reading = gw.ReopenReading();
        log.WriteLine($"held by        : {reading.Held}");
        Assert.NotNull(reading.Held);
        Assert.Null(reading.HeldForReview);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A RELEASED SCOPE THAT HAS EARNED ITS REOPEN GETS IT — the other half, so the release is not
    /// merely inert. The hold was the only thing in the way; once it is lifted the tick writes the
    /// receipt on the evidence it takes then, and the account trades again.
    /// </summary>
    [Fact]
    public async Task A_released_scope_reopens_on_the_next_tick_that_finds_everything_in_order()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;
        var second = await HeldForReview(gw, conn, clock, log);

        // WITHOUT THE PRESS, TIME CHANGES NOTHING AT ALL.
        clock.MoveTo(LossReopen.EligibleAt(second.ConfirmedAt, Day) + TimeSpan.FromHours(1));
        Assert.Empty((await gw.LossWatchAsync()).Reopened);
        Assert.NotNull(gw.DayClosed(account));

        Assert.True(gw.ReleaseHold(Note).Ok);

        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Single(pass.Reopened);
        Assert.Null(gw.DayClosed(account));

        var places = conn.Places;
        var fresh = await gw.PlaceAsync(new AgentContext("a"), "after-the-release", TestEnv.Buy("ES"));
        log.WriteLine($"after release  : {fresh.State}, places {places} -> {conn.Places}");
        Assert.Equal(ExecutionState.FILLED, fresh.State);
        Assert.Equal(places + 1, conn.Places);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A BLANK NOTE RELEASES NOTHING, AND A SECOND PRESS CANNOT REWRITE THE FIRST.
    ///
    /// <para>The note is the only durable trace of a person overruling the software's own refusal, so
    /// an empty one is refused in words having written nothing — the rule the unconfirmed-orders card
    /// already keeps. And the row is written once at the SQL layer, like the receipt: a second press
    /// must not be able to move the instant or the words the first one claims.</para>
    /// </summary>
    [Fact]
    public async Task A_blank_note_releases_nothing_and_a_second_press_rewrites_nothing()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var second = await HeldForReview(gw, conn, clock, log);
        var key = LossHold.ReleaseKey(conn.Id, second);

        foreach (var blank in new[] { "", "   ", "\t\n " })
        {
            var refused = gw.ReleaseHold(blank);
            log.WriteLine($"blank note     : ok={refused.Ok} — {refused.Why}");
            Assert.False(refused.Ok);
            Assert.Empty(refused.Released);
            Assert.Null(db.GetKv(key));
        }

        Assert.NotNull(gw.ReopenReading().HeldForReview);

        var first = gw.ReleaseHold(Note);
        Assert.True(first.Ok);
        var written = db.GetKv(key);
        Assert.NotNull(written);

        // THE SECOND PRESS. Nothing is held any more, so it writes nothing and says so.
        var again = gw.ReleaseHold("a different note entirely");
        log.WriteLine($"second press   : ok={again.Ok} — {again.Why}");
        Assert.False(again.Ok);
        Assert.Empty(again.Released);
        Assert.Equal(written, db.GetKv(key));

        // AND A DIRECT SECOND WRITE IS REFUSED AT THE SQL LAYER.
        Assert.False(db.AddKvOnce(key, Json.Write(new LossReleaseRecord { Note = "a second writer" })));
        Assert.Equal(written, db.GetKv(key));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// NOTHING AGENT-FACING REACHES THE RELEASE — no pipe op, no <c>trade</c> verb, no setting.
    /// <c>CLAUDE.md</c>'s rule that operator authority is in-process only, applied to the one
    /// permission this unit adds: an agent that wanted its account let back in has nowhere to ask.
    /// </summary>
    [Fact]
    public void No_op_and_no_verb_reaches_the_release()
    {
        var surface = string.Join(" ", GatewaySchema.Ops().Select(o => o.Op + " " + o.Cli));
        log.WriteLine($"ops            : {GatewaySchema.Ops().Length} on the pipe");
        Assert.DoesNotContain("release", surface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("loss_hold", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("reopen", surface, StringComparison.OrdinalIgnoreCase);
    }
}
