using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A BOOK NOBODY CAN VALUE IS ITS OWN FAILURE, WITH ITS OWN CLOCK AND ITS OWN WAY OUT.
///
/// <para><c>U-flatten-1</c> records nothing on a suspect print and <c>U-flatten-2</c> flattens
/// nothing from one. This is the other half: a feed that goes silent leaves a position open with
/// nobody measuring it, the owner's loss budget then bounds nothing, and silent indefinite exposure
/// is a failure mode too. The answer is an episode with a start instant that AGES, a precautionary
/// cancel of what could make the position bigger, and — past a bound the owner sets — a close under
/// its own reason <c>VALUATION_LOST</c>.</para>
///
/// <para><b>Never a budget breach.</b> Nothing here writes a <c>loss_breach</c> row, closes a day or
/// an instrument, or counts a strike. The assertions below hold that as hard as they hold the close
/// itself: an owner told their budget went when their prices went has been told the wrong thing
/// about their money.</para>
///
/// <para><b>Every press in this file takes <see cref="Unresolved.PressBudget"/>.</b> Every verdict
/// is the book or a record, and everything between the instant an exit opens its deadline and the
/// instant its leg goes out is durable SQLite at <c>synchronous=FULL</c> — one such commit has
/// measured 2234 ms on windows-latest, which is the whole of the simulator's two seconds
/// (<c>U-press-win-3</c>). Nothing here is loosened; no fixture here has a deadline as its verdict.</para>
/// </summary>
public class ValuationLossTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One tick of the watch's own interval, and then some: the gap between two pulls.</summary>
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A feed that has stopped: every quote the platform serves is older than
    /// <see cref="GatewayOptions.MaxQuoteAge"/>, which is what a silent feed looks like from inside
    /// this gateway — a price is still there, and it is a memory rather than a reading.
    /// </summary>
    static readonly TimeSpan Silent = TimeSpan.FromMinutes(10);

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready(
        decimal exitAfterMinutes = 15m, Action<TradeAgentSettings>? settings = null)
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
            s.Risk.ValuationLossExitMinutes = exitAfterMinutes;
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    static decimal Held(RecordingConnector conn, string symbol) =>
        conn.Broker.Positions.FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0m;

    /// <summary>
    /// The watch as the product runs it: on the health pass every host already runs, never by name.
    /// A clock that only moves when a test asks for a tick is not the product.
    /// </summary>
    static async Task Ticks(TradingGateway gw, TestClock clock, int howMany)
    {
        for (var i = 0; i < howMany; i++)
        {
            clock.Advance(Tick);
            await gw.RefreshHealthAsync();
        }
    }

    static ValuationUnavailableRecord? Episode(Database db, RecordingConnector conn, string symbol) =>
        db.GetKv(ValuationLoss.KeyFor(conn.Id, conn.Broker.AccountId, symbol)) is { } json
            ? Json.Read<ValuationUnavailableRecord>(json)
            : null;

    static ValuationExitRecord? Exit(Database db, RecordingConnector conn, string symbol) =>
        db.KvStartingWith($"{ValuationLoss.ExitPrefix}{ValuationLoss.Scope(conn.Id, conn.Broker.AccountId)}:{symbol}:")
            .Select(r => Json.Read<ValuationExitRecord>(r.Value))
            .FirstOrDefault();

    // ---------------------------------------------------------------- item 1

    /// <summary>
    /// UNAVAILABLE IS A STATE WITH A CLOCK, AND THE CLOCK AGES (item 1).
    ///
    /// <para>Ten ticks of a silent feed over an open position. Before this unit the watch answered
    /// "the figure could not be worked out", recorded nothing at all, and the resting buy under the
    /// position stayed on the book waiting to make it bigger. Now the episode is a row: one
    /// <c>Since</c>, written on the first tick that could not value it and carried forward
    /// unchanged, so that after ten ticks it is ten ticks old and not one.</para>
    ///
    /// <para><b>The mutant this watches.</b> A cached quote always exists once one has arrived, so a
    /// valuation check that asked whether a price had EVER been seen rather than whether a FRESH,
    /// in-epoch one had would value the position on every tick, end the episode on every tick, and
    /// the unavailability would never age — the exit's whole decision is an arithmetic over an
    /// instant that would then always be now. See <c>TradingGateway.CanBeValued</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_silent_feed_over_an_open_position_is_recorded_from_the_first_tick_and_ages()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));

        // A RESTING OPENER: a buy the book takes and does not fill. It is what makes an unvaluable
        // position able to get BIGGER while nobody is measuring it.
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        await gw.PlaceAsync(new AgentContext("a"), "opener", new PlaceIntent("ES", OrderSide.Buy,
            OrderType.Limit, 1m, conn.Broker.Quote("ES", Noon).Bid - 100m, null, TimeInForce.Day, null));
        conn.Faults.Fill = FillBehaviour.FillImmediately;
        var opener = conn.Broker.Orders.Single(o => o.State == ExecutionState.WORKING).ConnectorOrderId;
        log.WriteLine($"resting opener        : {opener}");

        // THE FEED GOES SILENT. Every quote the platform serves from here is a memory.
        conn.Faults.QuoteAge = Silent;
        var firstSilentTick = clock.GetUtcNow() + Tick;
        await Ticks(gw, clock, 10);

        var episode = Episode(db, conn, "ES");
        log.WriteLine($"episode               : {(episode is null ? "(none)" : Json.Write(episode))}");
        log.WriteLine($"book after ten ticks  : [{string.Join(" | ", conn.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}"))}]");

        Assert.NotNull(episode);
        Assert.True(episode.Standing);

        // THE FIRST TICK THAT COULD NOT VALUE IT, and not the tenth. This is the assertion the
        // mutant fails: a clock reset by a stale cached quote never gets past one tick.
        Assert.Equal(firstSilentTick, episode.Since);
        Assert.Equal(TimeSpan.FromSeconds(180), episode.Age(clock.GetUtcNow()));
        log.WriteLine($"unavailable for       : {episode.Age(clock.GetUtcNow())}");

        // AND THE ORDER THAT COULD HAVE MADE IT BIGGER IS GONE — through the app's own cancel kind.
        Assert.Contains(opener, episode.CancelledOrders);
        Assert.Empty(episode.CancelsNotSettled);
        Assert.DoesNotContain(conn.Broker.Orders.Where(o => !OrderStateMachine.IsTerminal(o.State)),
            o => o.ConnectorOrderId == opener);

        // AND NOTHING HAS BEEN CLOSED AND NO BUDGET HAS BEEN REACHED. This is item 1 only.
        Assert.Equal(2m, Held(conn, "ES"));
        Assert.Null(gw.DayClosed(conn.Broker.AccountId));
    }

    /// <summary>
    /// A PRICE THAT COMES BACK ENDS THE EPISODE, AND THE NEXT SILENCE IS A NEW ONE WITH A NEW CLOCK.
    ///
    /// <para>The bound is on CONTINUOUS unavailability, so the thing that ends an episode has to be
    /// the one thing that makes the position measurable again — a fresh, in-epoch, executable mark —
    /// and nothing else. It is the other half of the first test: that one holds that the clock does
    /// not reset while the feed is silent, this one holds that it DOES reset when it is not.</para>
    /// </summary>
    [Fact]
    public async Task A_price_that_arrives_ends_the_episode_and_a_later_silence_starts_a_new_one()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));

        conn.Faults.QuoteAge = Silent;
        await Ticks(gw, clock, 3);
        var first = Episode(db, conn, "ES");
        Assert.NotNull(first);
        Assert.True(first.Standing);
        log.WriteLine($"first episode since   : {first.Since:HH:mm:ss}");

        conn.Faults.QuoteAge = TimeSpan.Zero;
        await Ticks(gw, clock, 1);
        var cleared = Episode(db, conn, "ES");
        Assert.NotNull(cleared);
        Assert.False(cleared.Standing);
        log.WriteLine($"cleared at            : {cleared.ClearedAt:HH:mm:ss}");

        conn.Faults.QuoteAge = Silent;
        await Ticks(gw, clock, 2);
        var second = Episode(db, conn, "ES");
        Assert.NotNull(second);
        Assert.True(second.Standing);
        log.WriteLine($"second episode since  : {second.Since:HH:mm:ss}");

        Assert.True(second.Since > first.Since);
        Assert.Equal(TimeSpan.FromSeconds(20), second.Age(clock.GetUtcNow()));
    }

    // ---------------------------------------------------------------- item 2

    /// <summary>
    /// THE DATA-LOSS EXIT, BOUNDED AND DISTINCT (item 2).
    ///
    /// <para>A book that was valued and then went silent stays open for exactly as long as the
    /// owner's bound allows and is then closed — by code, with nobody pressing anything, under the
    /// reason <c>VALUATION_LOST</c>, with its own record. Before this unit it stayed open for ever:
    /// the gate refused new risk on the unknown and the position already there went on being exposed
    /// with nothing measuring it.</para>
    ///
    /// <para><b>The mutant this watches.</b> Folding the exit's reason into the breach record: the
    /// day would then read closed, <c>loss_day_closed_at</c> would carry an instant, section 4 would
    /// print a loss budget that was reached, and the strike counter would have an episode to count.
    /// None of it happened. Every one of those is asserted as ABSENT here, which is why the mutant
    /// cannot survive this fixture.</para>
    /// </summary>
    [Fact]
    public async Task A_position_nobody_can_value_past_the_bound_is_closed_under_its_own_reason()
    {
        // One minute: three ticks of the watch's twenty seconds.
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));

        // VALUED FIRST, so the silence is a LOSS of something rather than an installation that never
        // had a price at all.
        await Ticks(gw, clock, 1);
        Assert.Null(Episode(db, conn, "ES"));

        conn.Faults.QuoteAge = Silent;
        await Ticks(gw, clock, 2);
        log.WriteLine($"before the bound      : ES {Held(conn, "ES")}, closes on the wire {conn.Closes}");
        Assert.Equal(2m, Held(conn, "ES"));

        await Ticks(gw, clock, 2);

        var exit = Exit(db, conn, "ES");
        log.WriteLine($"exit record           : {(exit is null ? "(none)" : Json.Write(exit))}");
        log.WriteLine($"position after        : ES {Held(conn, "ES")}");

        Assert.NotNull(exit);
        Assert.Equal("VALUATION_LOST", exit.Reason);
        Assert.True(exit.Flat);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Contains("VALUATION_LOST", exit.Why, StringComparison.Ordinal);

        // AND IT IS NOT A BREACH. Not one row, not one closed scope, not one strike — asserted here
        // rather than left to the record's own words, because the words are what the mutant rewrites.
        Assert.Null(gw.DayClosed(account));
        Assert.Empty(gw.SymbolsClosedToday(account));
        Assert.Empty(db.KvStartingWith("loss_breach:"));
        Assert.Empty(db.KvStartingWith("loss_hold:"));
        Assert.Empty(db.KvStartingWith("loss_flatten:"));

        var status = Json.Write(await gw.StatusAsync());
        log.WriteLine(status);
        Assert.Contains("loss_valuation_exit", status, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_day_closed_at", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// WITH THE CONNECTION DOWN, NOTHING IS SENT, AND THE PAUSE HOLDS (item 2, second half).
    ///
    /// <para><c>CLAUDE.md</c> rule 3 applied where it bites: an unavailable valuation is not a
    /// refusal by anybody, and a connection that is not up cannot carry a close. So the clock goes on
    /// running — the episode is exactly as long as it is, and the surfaces go on saying so — and the
    /// exit does not run. A position closed on a connection that was down would be an order this
    /// gateway believes it sent and cannot account for.</para>
    /// </summary>
    [Fact]
    public async Task With_the_connection_down_the_exit_sends_nothing_and_the_episode_goes_on_ageing()
    {
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
        conn.Faults.QuoteAge = Silent;
        await Ticks(gw, clock, 2);
        Assert.NotNull(Episode(db, conn, "ES"));

        var closesBefore = conn.Closes;
        conn.Faults.Disconnected = true;
        await Ticks(gw, clock, 6);

        var episode = Episode(db, conn, "ES");
        log.WriteLine($"episode while down    : {(episode is null ? "(none)" : Json.Write(episode))}");
        log.WriteLine($"closes on the wire    : {closesBefore} -> {conn.Closes}");
        log.WriteLine($"position              : ES {Held(conn, "ES")}");

        Assert.Equal(closesBefore, conn.Closes);
        Assert.Equal(2m, Held(conn, "ES"));
        Assert.Null(Exit(db, conn, "ES"));

        Assert.NotNull(episode);
        Assert.True(episode.Standing);
        Assert.Null(episode.ExitKey);

        // THE CLOCK RAN THE WHOLE TIME. The episode is as old as it is, and the moment the platform
        // is reachable again the exit is owed — the pause is not a reset.
        Assert.True(episode.Age(clock.GetUtcNow()) >= TimeSpan.FromMinutes(1),
            $"the episode should have gone on ageing while the connection was down, and it is {episode.Age(clock.GetUtcNow())}");
    }

    /// <summary>
    /// ZERO SWITCHES THE EXIT OFF AND THE EPISODE IS STILL RECORDED AND STILL SAID.
    ///
    /// <para>Zero is the WIDEST value the setting has, exactly as it is on the notional cap and both
    /// loss budgets, and the widest value of a limit is the one an owner is most entitled to be told
    /// about. So the clock still runs, the openers are still cancelled, the surfaces still carry the
    /// episode — and the sentence says in words that TradeAgent will NOT close it.</para>
    /// </summary>
    [Fact]
    public async Task A_zero_bound_closes_nothing_and_says_so_while_still_recording_the_episode()
    {
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 0m);
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
        conn.Faults.QuoteAge = Silent;
        await Ticks(gw, clock, 20);

        var episode = Episode(db, conn, "ES");
        log.WriteLine($"episode               : {(episode is null ? "(none)" : episode.Why)}");
        log.WriteLine($"position              : ES {Held(conn, "ES")}");

        Assert.NotNull(episode);
        Assert.True(episode.Standing);
        Assert.Equal(2m, Held(conn, "ES"));
        Assert.Null(Exit(db, conn, "ES"));
        Assert.Contains("will NOT close it", episode.Why, StringComparison.Ordinal);
    }
}
