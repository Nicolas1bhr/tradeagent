using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// U-scope-identity — A LOSS-LINE SCOPE IS <c>(connector, account, symbol)</c>, CARRIED ON EVERY
/// RECORD AND READ OFF THE ROW.
///
/// <para>Six tests, every one of them REVIEW 2026-09-16's own probe brought over from
/// <c>review-probes-c</c> and renamed, with the assertions turned from the DEFECT they recorded to
/// the behaviour this unit owes: <c>P1</c>, <c>P1b</c>, <c>P1c</c> (finding 1), <c>C2</c>
/// (finding 3) and <c>P3</c>, <c>P3b</c> (finding 2). Each was red on <c>4bb0846</c>.</para>
///
/// <para>Nothing reaches a wire here but the simulator behind <see cref="RecordingConnector"/> —
/// the venue-qualified probes put one relabelling seam in front of it so the gateway sees a
/// platform whose instrument is called <c>ES:H6</c>, and nothing about the gateway is faked.</para>
/// </summary>
public class LossScopeIdentityTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A PLATFORM THAT NAMES ITS INSTRUMENT THE WAY MANY VENUES DO — <c>VENUE:SYMBOL</c>.
    ///
    /// <para>The simulator behind <see cref="RecordingConnector"/>, relabelled at the seam: every
    /// instrument, quote, position, order and fill the inner broker calls <paramref name="inward"/>
    /// is presented to the gateway as <paramref name="outward"/> and translated back on the way in.
    /// The symbol is the PLATFORM's string and not one TradeAgent chooses, which is the whole of
    /// finding 1.</para>
    /// </summary>
    sealed class VenueSymbolConnector(RecordingConnector inner, string inward, string outward) : ITradingConnector
    {
        public RecordingConnector Inner { get; } = inner;
        public FakeBroker Broker => Inner.Broker;

        public int Places => Inner.Places;
        public int Closes => Inner.Closes;
        public int Cancels => Inner.Cancels;

        string In(string s) => string.Equals(s, outward, StringComparison.Ordinal) ? inward : s;
        string Out(string s) => string.Equals(s, inward, StringComparison.Ordinal) ? outward : s;

        InstrumentInfo Out(InstrumentInfo i) => i with { Symbol = Out(i.Symbol) };
        QuoteInfo? Out(QuoteInfo? q) => q is null ? null : q with { Symbol = Out(q.Symbol) };
        PositionInfo Out(PositionInfo p) => p with { Symbol = Out(p.Symbol) };
        OrderInfo Out(OrderInfo o) => o with { Symbol = Out(o.Symbol) };
        ExecutionInfo Out(ExecutionInfo e) => e with { Symbol = Out(e.Symbol) };

        public string Id => Inner.Id;
        public string DisplayName => Inner.DisplayName;
        public ConnectorCapabilities Capabilities => Inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => Inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => Inner.EmergencyBudget;
        public Task ConnectAsync(CancellationToken ct = default) => Inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => Inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => Inner.GetAccountsAsync(ct);
        public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => Inner.GetAccountAsync(a, ct);

        public async Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) =>
            [.. (await Inner.GetInstrumentsAsync(ct)).Select(Out)];

        public async Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) =>
            Out(await Inner.GetQuoteAsync(In(s), ct));

        public async Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) =>
            [.. (await Inner.GetPositionsAsync(a, ct)).Select(Out)];

        public async Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inactive, DateTimeOffset? since, CancellationToken ct = default) =>
            [.. (await Inner.GetOrdersAsync(a, inactive, since, ct)).Select(Out)];

        public async Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) =>
            [.. (await Inner.GetExecutionsAsync(a, since, ct)).Select(Out)];

        public async Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default) =>
            Out(await Inner.PlaceOrderAsync(cmd with { Symbol = In(cmd.Symbol) }, ct));

        public async Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) =>
            Out(await Inner.ModifyOrderAsync(c, ct));

        public Task CancelOrderAsync(string id, CancellationToken ct = default) => Inner.CancelOrderAsync(id, ct);

        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) =>
            Inner.CancelAllOrdersAsync(a, ct);

        public async Task<OrderInfo?> ClosePositionAsync(string a, string s, string coid, CancellationToken ct = default)
        {
            var r = await Inner.ClosePositionAsync(a, In(s), coid, ct);
            return r is null ? null : Out(r);
        }

        // THE STREAM IS RELABELLED TOO, or the fill ledger would record the inner name and the test
        // would be measuring a gateway that had been told two different things.
        readonly Dictionary<Delegate, Delegate> _wrapped = [];

        Action<T> Wrap<T>(Action<T> handler, Func<T, T> map)
        {
            lock (_wrapped)
            {
                if (_wrapped.TryGetValue(handler, out var had)) return (Action<T>)had;
                Action<T> w = x => handler(map(x));
                _wrapped[handler] = w;
                return w;
            }
        }

        Action<T>? Unwrap<T>(Action<T> handler)
        {
            lock (_wrapped) return _wrapped.Remove(handler, out var had) ? (Action<T>)had : null;
        }

        public event Action<HealthState>? ConnectionChanged { add => Inner.ConnectionChanged += value; remove => Inner.ConnectionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => Inner.AccountChanged += value; remove => Inner.AccountChanged -= value; }

        public event Action<QuoteInfo>? QuoteChanged
        {
            add { if (value is not null) Inner.QuoteChanged += Wrap(value, q => q with { Symbol = Out(q.Symbol) }); }
            remove { if (value is not null && Unwrap(value) is { } w) Inner.QuoteChanged -= w; }
        }

        public event Action<OrderInfo>? OrderChanged
        {
            add { if (value is not null) Inner.OrderChanged += Wrap(value, Out); }
            remove { if (value is not null && Unwrap(value) is { } w) Inner.OrderChanged -= w; }
        }

        public event Action<ExecutionInfo>? ExecutionReceived
        {
            add { if (value is not null) Inner.ExecutionReceived += Wrap(value, Out); }
            remove { if (value is not null && Unwrap(value) is { } w) Inner.ExecutionReceived -= w; }
        }

        public event Action<PositionInfo>? PositionChanged
        {
            add { if (value is not null) Inner.PositionChanged += Wrap(value, Out); }
            remove { if (value is not null && Unwrap(value) is { } w) Inner.PositionChanged -= w; }
        }

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    static (TradingGateway Gw, VenueSymbolConnector Conn, Database Db, TestClock Clock) OnVenue(string symbol)
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new VenueSymbolConnector(
            new RecordingConnector(new FakeConnector(new FakeBroker()) { EmergencyBudget = Unresolved.PressBudgetFor(1) }),
            "ES", symbol);
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [symbol, "NQ", "MES", "YM"];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;   // not enforced, so no multiplier is asked for
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 0m;          // only the PER-SYMBOL budget may close anything
            s.Risk.MaxLossPerTrade = 100m;
        });
        return (gw, conn, db, clock);
    }

    static PlaceIntent Buy(string symbol, decimal qty = 1m) =>
        new(symbol, OrderSide.Buy, OrderType.Market, qty, null, null, TimeInForce.Day, null);

    /// <summary>
    /// P1, RENAMED AND TURNED ROUND — A CLOSURE ON A VENUE-QUALIFIED SYMBOL IS VISIBLE TO EVERY
    /// READER, AND THE SCOPE IT CLOSED STOPS TRADING.
    ///
    /// <para>The same losing day twice, once on <c>ES:H6</c> and once on plain <c>ES</c>, against
    /// the same simulator and the same figures, so the only difference is the symbol's own name.
    /// The record is written by <c>LossWatchAsync</c> on two agreeing pulls and the buy afterwards
    /// goes through <c>PlaceAsync</c>: nothing here reaches past the gateway's own code.</para>
    ///
    /// <para>The figure recovers completely before the second buy — flattened, and the price back —
    /// so from there only the RECORD can refuse, which is the whole reason the record exists.</para>
    /// </summary>
    [Fact]
    public async Task A_venue_qualified_symbols_closure_is_visible_to_every_reader()
    {
        foreach (var symbol in new[] { "ES:H6", "ES" })
        {
            var (gw, conn, db, clock) = OnVenue(symbol);
            await conn.ConnectAsync();
            await gw.RefreshHealthAsync();

            await gw.PlaceAsync(new AgentContext("a"), "p1-open", Buy(symbol));
            conn.Broker.PriceOffset = -20m;

            clock.Advance(Tick);
            await gw.LossWatchAsync();
            clock.Advance(Tick);
            var closing = await gw.LossWatchAsync();

            var rows = db.KvStartingWith("loss_breach:").Select(x => x.Key).ToList();
            log.WriteLine($"[{symbol}]");
            log.WriteLine($"  breach rows on disk   : {string.Join(" ", rows)}");
            log.WriteLine($"  the watch closed      : {string.Join(",", closing.Closed)}");
            log.WriteLine($"  SymbolClosed says     : {(gw.SymbolClosed(conn.Broker.AccountId, symbol) is null ? "NOTHING IS CLOSED" : "closed")}");
            log.WriteLine($"  SymbolsClosedToday    : [{string.Join(",", gw.SymbolsClosedToday(conn.Broker.AccountId))}]");

            conn.Broker.PriceOffset = 0m;
            gw.Update(s => s.Risk.MaxLossPerTrade = 1_000_000m);
            clock.Advance(Tick);

            var before = conn.Places;
            string verdict;
            try
            {
                await gw.PlaceAsync(new AgentContext("a"), "p1-after", Buy(symbol));
                verdict = "SENT";
            }
            catch (GatewayDeniedException ex) { verdict = ex.Code.ToString(); }
            catch (TradeAgentException ex) { verdict = ex.Code.ToString(); }

            log.WriteLine($"  a new buy on it       : {verdict}");
            log.WriteLine($"  orders that reached the wire after the closure: {conn.Places - before}");
            log.WriteLine("");

            Assert.NotEmpty(rows);
            Assert.NotEmpty(closing.Closed);
            Assert.NotNull(gw.SymbolClosed(conn.Broker.AccountId, symbol));
            Assert.Equal([symbol], gw.SymbolsClosedToday(conn.Broker.AccountId));
            Assert.Equal("LOSS_BUDGET_REACHED", verdict);
            Assert.Equal(0, conn.Places - before);
        }
    }

    /// <summary>
    /// P1b, RENAMED AND TURNED ROUND — THE KEY THE PRODUCT MINTS ROUND-TRIPS THROUGH THE PRODUCT'S
    /// OWN READER FOR EVERY NAME A PLATFORM CAN HAND IT, AND TWO SCOPES NEVER ADDRESS ONE ROW.
    ///
    /// <para>The narrowest statement of finding 1 and of the review's UNVERIFIED 2 (the account id
    /// with a colon, which makes a whole DAY closure invisible), with no gateway in it at all.</para>
    ///
    /// <para>The last assertion is the one the review's "what would fix it" column did not have to
    /// state: a key is an ADDRESS, and an address two different scopes can both mint is a second
    /// breach that is silently never written.</para>
    /// </summary>
    [Fact]
    public void The_breach_key_round_trips_for_every_name_a_platform_can_hand_it()
    {
        var at = Noon;
        foreach (var symbol in new[] { "ES", "ES:H6", "BINANCE:BTCUSDT" })
        {
            var key = LossBreach.SymbolKey("SIM-1", symbol, at);
            var back = LossBreach.ScopeOf(key, "SIM-1");
            log.WriteLine($"{key,-46} -> {(back is null ? "NOT A KEY THIS READER KNOWS" : $"day={back.Value.Day} symbol={back.Value.Symbol}")}");
            Assert.Equal((LossBreach.Stamp(at), symbol), back);
        }

        foreach (var account in new[] { "SIM-1", "RITHMIC:SIM-1" })
        {
            var key = LossBreach.DayKey(account, at);
            var back = LossBreach.ScopeOf(key, account);
            log.WriteLine($"{key,-46} -> {(back is null ? "NOT A KEY THIS READER KNOWS" : $"day={back.Value.Day} symbol={back.Value.Symbol ?? "(account)"}")}");
            Assert.Equal((LossBreach.Stamp(at), (string?)null), back);
        }

        // NOTHING ON DISK MOVES. A name with no delimiter in it mints exactly the key it always did,
        // so every closure and every receipt written before this unit is still found by its own key.
        Assert.Equal("loss_breach:SIM-1:ES:2026-03-10", LossBreach.SymbolKey("SIM-1", "ES", at));
        Assert.Equal("loss_breach:SIM-1:2026-03-10", LossBreach.DayKey("SIM-1", at));

        // TWO SCOPES, TWO ADDRESSES. The account `ACC:ES` losing its day and the account `ACC`
        // losing ES are different facts, and they were the same row.
        log.WriteLine($"day of ACC:ES   -> {LossBreach.DayKey("ACC:ES", at)}");
        log.WriteLine($"ES of ACC       -> {LossBreach.SymbolKey("ACC", "ES", at)}");
        Assert.NotEqual(LossBreach.DayKey("ACC:ES", at), LossBreach.SymbolKey("ACC", "ES", at));

        // AND ONE ACCOUNT'S SCAN NEVER SWEEPS UP ANOTHER'S.
        Assert.StartsWith(LossBreach.AccountPrefix("ACC"), LossBreach.SymbolKey("ACC", "ES", at), StringComparison.Ordinal);
        Assert.DoesNotContain(LossBreach.AccountPrefix("ACC"), LossBreach.DayKey("ACC:ES", at), StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE ROW A BUILD BEFORE THIS UNIT ALREADY WROTE IS STILL SEEN — the half of finding 1 that
    /// a change to how keys are MINTED cannot reach on its own.
    ///
    /// <para>An installation that reached a per-instrument budget on <c>ES:H6</c> before this unit
    /// has a row on disk under the key that build minted: <c>loss_breach:SIM-001:ES:H6:2026-03-10</c>,
    /// five colon-separated parts, which no decoder that counts delimiters can take apart. Escaping
    /// what is minted from here on does nothing for it. The scope is read off the ROW, so it is seen
    /// — and the scope it closed stops trading on the very first tick after the upgrade.</para>
    ///
    /// <para>The row is written with the product's own writer at the product's own legacy key; only
    /// the key shape is spelled out here, because that shape is the fixture.</para>
    /// </summary>
    [Fact]
    public async Task A_closure_written_under_an_older_builds_key_is_still_seen()
    {
        const string symbol = "ES:H6";
        var (gw, conn, db, clock) = OnVenue(symbol);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var account = conn.Broker.AccountId;
        var legacy = $"loss_breach:{account}:{symbol}:{LossBreach.Stamp(Noon)}";
        db.SetKv(legacy, Json.Write(new LossBreachRecord
        {
            // NO CONNECTOR AND NO MODE EITHER: the build that wrote this row had no such fields.
            // It still CLOSES — refusing new risk is the safe direction on any platform — and it is
            // never flattened, which is the asymmetry `docs/CONTRACTS.md` records as deliberate.
            Account = account, Symbol = symbol, Day = LossBreach.Stamp(Noon),
            FirstSeenAt = Noon, ConfirmedAt = Noon, FirstPull = 1, ConfirmingPull = 2,
            Loss = 900m, TradeBudget = 100m, Currency = "USD",
            Why = "TradeAgent closed ES:H6 to new risk at 12:00 UTC."
        }));

        log.WriteLine($"the row on disk       : {legacy}");
        log.WriteLine($"SymbolClosed says     : {(gw.SymbolClosed(account, symbol) is null ? "NOTHING IS CLOSED" : "closed")}");
        log.WriteLine($"SymbolsClosedToday    : [{string.Join(",", gw.SymbolsClosedToday(account))}]");

        var before = conn.Places;
        string verdict;
        try
        {
            await gw.PlaceAsync(new AgentContext("a"), "legacy-after", Buy(symbol));
            verdict = "SENT";
        }
        catch (GatewayDeniedException ex) { verdict = ex.Code.ToString(); }

        log.WriteLine($"a new buy on it       : {verdict}");
        log.WriteLine($"orders that reached the wire: {conn.Places - before}");

        Assert.NotNull(gw.SymbolClosed(account, symbol));
        Assert.Equal([symbol], gw.SymbolsClosedToday(account));
        Assert.Equal("LOSS_BUDGET_REACHED", verdict);
        Assert.Equal(0, conn.Places - before);
    }

    /// <summary>
    /// P1c, RENAMED AND TURNED ROUND — THE SCOPE IS CLOSED ONCE AND FLATTENED ONCE, HOWEVER MANY
    /// TICKS GO BY.
    ///
    /// <para>"One episode until it is reopened" is enforced by <c>alreadyClosed</c>, which is built
    /// out of <c>OpenClosures</c>. A reader that cannot see the row writes ANOTHER breach record and
    /// sends ANOTHER app flatten at the instrument on every pair of agreeing pulls, and
    /// <c>PriorBreaches</c> cannot see them either, so no strike is ever counted however many there
    /// are. Eight ticks with the price on the floor, and the agent asking again on each one.</para>
    /// </summary>
    [Fact]
    public async Task A_venue_qualified_scope_is_closed_and_flattened_exactly_once()
    {
        foreach (var symbol in new[] { "ES:H6", "ES" })
        {
            var (gw, conn, db, clock) = OnVenue(symbol);
            await conn.ConnectAsync();
            await gw.RefreshHealthAsync();

            await gw.PlaceAsync(new AgentContext("a"), "p1c-open", Buy(symbol));
            conn.Broker.PriceOffset = -20m;

            for (var i = 0; i < 8; i++)
            {
                clock.Advance(Tick);
                await gw.LossWatchAsync();
                try { await gw.PlaceAsync(new AgentContext("a"), $"p1c-again-{i}", Buy(symbol)); }
                catch (GatewayDeniedException) { }
            }

            log.WriteLine($"[{symbol}] breach rows={db.KvStartingWith("loss_breach:").Count} "
                          + $"flatten rows={db.KvStartingWith("loss_flatten:").Count} "
                          + $"hold rows={db.KvStartingWith("loss_hold:").Count} "
                          + $"closes at the wire={conn.Closes} places at the wire={conn.Places}");

            Assert.Single(db.KvStartingWith("loss_breach:"));
            Assert.Single(db.KvStartingWith("loss_flatten:"));
            Assert.Equal(1, conn.Closes);
            Assert.Equal(1, conn.Places);
        }
    }

    /// <summary>
    /// C2, RENAMED AND TURNED ROUND — A PAPER BREACH NEVER FLATTENS ANOTHER PLATFORM'S BOOK, AND
    /// NEVER ANOTHER MODE'S.
    ///
    /// <para>An ordinary paper day, closed and flattened on the simulator; then the owner switches
    /// to a platform carrying a NON-simulated account with the same id, a position they put on by
    /// hand and a resting bid, on an installation that has set no loss budget at all. The flatten's
    /// OUTCOME key carries the connector, so the killed-flatten sweep on the new platform finds no
    /// outcome for a breach that was fully flattened elsewhere and re-runs it.</para>
    ///
    /// <para>Three arms, because the record is bound to BOTH halves of its platform identity and
    /// each half has to hold on its own: both different; the SAME connector id in a different mode —
    /// the installation that switched from paper to live without changing broker; and a DIFFERENT
    /// connector id in the same mode — the owner who attached a second platform and has not switched
    /// mode at all, where the mode check alone would let the closes through.</para>
    /// </summary>
    [Theory]
    [InlineData("atas", true, "another platform, in live mode")]
    [InlineData("fake", true, "the same platform, in live mode")]
    [InlineData("atas", false, "another platform, still in paper")]
    public async Task A_paper_breach_never_flattens_another_platform_or_another_mode(
        string liveId, bool inLiveMode, string what)
    {
        var db = TestEnv.NewDb();
        var clock = new TestClock(Noon);

        // ---- the simulator: an ordinary losing day, confirmed and flattened.
        var paper = new RecordingConnector(
            new FakeConnector(new FakeBroker()) { EmergencyBudget = Unresolved.PressBudgetFor(1) }, "fake");
        var gwA = new TradingGateway(db, paper, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gwA.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = paper.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
        });
        await paper.ConnectAsync();
        await gwA.RefreshHealthAsync();
        await gwA.PlaceAsync(new AgentContext("a"), "c2-open", TestEnv.Buy("ES"));
        paper.Broker.PriceOffset = -20m;
        clock.Advance(Tick); await gwA.LossWatchAsync();
        clock.Advance(Tick); var closing = await gwA.LossWatchAsync();

        log.WriteLine($"[{what}]");
        log.WriteLine($"paper breach            : {string.Join(",", closing.Closed)}");
        log.WriteLine($"paper flatten           : flat={gwA.FlattenToday(paper.Broker.AccountId)?.Flat}");
        Assert.NotEmpty(closing.Closed);

        // ---- the owner switches. Another account carrying the same id, on `liveId`. It is a REAL
        // one wherever the arm is in live mode; in the paper arm it is a second simulator, and the
        // book is still the owner's own and still not this breach's to close.
        var liveBroker = new FakeBroker { IsSimulated = !inLiveMode };
        var live = new RecordingConnector(
            new FakeConnector(liveBroker) { EmergencyBudget = Unresolved.PressBudgetFor(1) }, liveId);
        await live.ConnectAsync();

        live.Faults.Fill = FillBehaviour.FillImmediately;
        await live.Inner.PlaceOrderAsync(new PlaceOrderCommand("OWNER-1", liveBroker.AccountId, "ES",
            OrderSide.Buy, OrderType.Market, 4m, null, null, TimeInForce.Day, "the owner's own position"));
        live.Faults.Fill = FillBehaviour.LeaveWorking;
        await live.Inner.PlaceOrderAsync(new PlaceOrderCommand("OWNER-2", liveBroker.AccountId, "ES",
            OrderSide.Buy, OrderType.Limit, 2m, 50m, null, TimeInForce.Day, "the owner's own resting bid"));
        live.Faults.Fill = FillBehaviour.FillImmediately;

        var before = (live.Places, live.Closes, live.Cancels);
        log.WriteLine($"live book before        : {string.Join(",", (await live.GetPositionsAsync(liveBroker.AccountId)).Select(p => $"{p.Symbol} {p.Quantity}"))}"
                      + $"  working={(await live.GetOrdersAsync(liveBroker.AccountId, false, null)).Count(o => o.State == ExecutionState.WORKING)}");

        var gwB = new TradingGateway(db, live, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gwB.Update(s =>
        {
            s.Mode = inLiveMode ? TradingMode.LIVE_CONFIRM : TradingMode.PAPER;
            s.LiveActivated = inLiveMode;
            s.SelectedAccountId = liveBroker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 0m;      // this installation has set NO budget at all
            s.Risk.MaxLossPerTrade = 0m;
        });

        clock.Advance(Tick);
        await gwB.RefreshHealthAsync();      // the health pass: the watch, then the killed-flatten sweep

        var after = (live.Places, live.Closes, live.Cancels);
        var positions = await live.GetPositionsAsync(liveBroker.AccountId);
        var working = (await live.GetOrdersAsync(liveBroker.AccountId, false, null)).Count(o => o.State == ExecutionState.WORKING);

        log.WriteLine($"live cancels sent       : {after.Cancels - before.Cancels}");
        log.WriteLine($"live closes sent        : {after.Closes - before.Closes}");
        log.WriteLine($"live places sent        : {after.Places - before.Places}");
        log.WriteLine($"live book after         : {string.Join(",", positions.Select(p => $"{p.Symbol} {p.Quantity}"))}  working={working}");
        log.WriteLine($"live flatten record     : flat={gwB.FlattenToday(liveBroker.AccountId)?.Flat}");
        log.WriteLine("");

        // AND THE FLATTEN REFUSES WHEN IT IS ASKED DIRECTLY, not only when the sweep declines to ask.
        // They are two separate pieces of code and the refusal is this one's: the sweep's filter only
        // keeps the engineering log from announcing work it is about to decline.
        var standing = gwB.DayClosed(liveBroker.AccountId);
        Assert.NotNull(standing);
        Assert.Null(await gwB.FlattenForBreachAsync(standing));

        after = (live.Places, live.Closes, live.Cancels);
        positions = await live.GetPositionsAsync(liveBroker.AccountId);
        working = (await live.GetOrdersAsync(liveBroker.AccountId, false, null)).Count(o => o.State == ExecutionState.WORKING);

        // NOTHING REACHED THE OWNER'S REAL BOOK, and it is still exactly where they left it.
        Assert.Equal(0, after.Cancels - before.Cancels);
        Assert.Equal(0, after.Closes - before.Closes);
        Assert.Equal(0, after.Places - before.Places);
        Assert.Equal(4m, positions.Single(p => p.Symbol == "ES").Quantity);
        Assert.Equal(1, working);

        // AND NO LIVE FLATTEN WAS RECORDED. On the arm that keeps the connector id the only row
        // `FlattenToday` can find is the PAPER gateway's own, untouched; on the other there is none.
        var recorded = gwB.FlattenToday(liveBroker.AccountId);
        if (recorded is not null)
        {
            Assert.Equal(TradingMode.PAPER, recorded.Mode);
            Assert.Equal("fake", recorded.Connector);
        }
    }

    /// <summary>
    /// A KILLED PAPER FLATTEN NEVER RE-RUNS AGAINST A LIVE BOOK ON THE SAME PLATFORM — the case the
    /// connector id alone cannot answer, because it is the same one.
    ///
    /// <para>A closure with no outcome beside it is a flatten that was killed, and the health pass
    /// re-runs it. On the same connector id a FINISHED flatten leaves an outcome row that stops a
    /// second run; a killed one leaves none, so this is the arm where only the MODE can refuse: a
    /// paper day's closure, killed before it wrote anything, and the owner then in LIVE_CONFIRM on
    /// the same broker over the same database, with a position they put on by hand and a resting
    /// bid.</para>
    ///
    /// <para>The row is stamped the way <c>Compose</c> stamps one, so `stamped` is what the arm
    /// varies: a row written BEFORE this unit names no platform and no mode, and is never flattened
    /// either — the asymmetry `docs/CONTRACTS.md` records as deliberate, and the arm that is red on
    /// `4bb0846`.</para>
    /// </summary>
    [Theory]
    [InlineData(false, "a row written before this unit: no platform, no mode")]
    [InlineData(true, "a row this build wrote, stamped fake/PAPER")]
    public async Task A_killed_paper_flatten_never_re_runs_in_live_mode_on_the_same_platform(
        bool stamped, string what)
    {
        var db = TestEnv.NewDb();
        var clock = new TestClock(Noon);
        var broker = new FakeBroker { IsSimulated = false };
        var live = new RecordingConnector(
            new FakeConnector(broker) { EmergencyBudget = Unresolved.PressBudgetFor(1) }, "fake");
        await live.ConnectAsync();

        live.Faults.Fill = FillBehaviour.FillImmediately;
        await live.Inner.PlaceOrderAsync(new PlaceOrderCommand("OWNER-1", broker.AccountId, "ES",
            OrderSide.Buy, OrderType.Market, 4m, null, null, TimeInForce.Day, "the owner's own position"));
        live.Faults.Fill = FillBehaviour.LeaveWorking;
        await live.Inner.PlaceOrderAsync(new PlaceOrderCommand("OWNER-2", broker.AccountId, "ES",
            OrderSide.Buy, OrderType.Limit, 2m, 50m, null, TimeInForce.Day, "the owner's own resting bid"));
        live.Faults.Fill = FillBehaviour.FillImmediately;

        // KILLED BETWEEN THE RECORD AND THE FIRST CLOSE: the breach is on disk and no outcome is.
        var breach = new LossBreachRecord
        {
            Account = broker.AccountId,
            Connector = stamped ? "fake" : "",
            Mode = stamped ? TradingMode.PAPER : null,
            Day = LossBreach.Stamp(Noon), FirstSeenAt = Noon, ConfirmedAt = Noon,
            FirstPull = 1, ConfirmingPull = 2, Loss = 2_000m, DayBudget = 1_000m, Currency = "USD",
            Why = "TradeAgent closed today to new risk at 12:00 UTC."
        };
        db.SetKv(LossBreach.DayKey(broker.AccountId, Noon), Json.Write(breach));

        var gw = new TradingGateway(db, live, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.LIVE_CONFIRM;
            s.LiveActivated = true;
            s.SelectedAccountId = broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 0m;
            s.Risk.MaxLossPerTrade = 0m;
        });

        var before = (live.Places, live.Closes, live.Cancels);
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();          // the health pass IS the killed-flatten sweep

        // AND ASKED DIRECTLY TOO: the sweep's filter and the flatten's refusal are two pieces of
        // code, and the refusal is this one's.
        var standing = gw.DayClosed(broker.AccountId);
        Assert.NotNull(standing);
        Assert.Null(await gw.FlattenForBreachAsync(standing));

        var after = (live.Places, live.Closes, live.Cancels);
        var positions = await live.GetPositionsAsync(broker.AccountId);
        var working = (await live.GetOrdersAsync(broker.AccountId, false, null)).Count(o => o.State == ExecutionState.WORKING);

        log.WriteLine($"[{what}]");
        log.WriteLine($"live cancels sent       : {after.Cancels - before.Cancels}");
        log.WriteLine($"live closes sent        : {after.Closes - before.Closes}");
        log.WriteLine($"live book after         : {string.Join(",", positions.Select(p => $"{p.Symbol} {p.Quantity}"))}  working={working}");
        log.WriteLine($"live flatten record     : {gw.FlattenToday(broker.AccountId)?.Flat.ToString() ?? "none"}");

        Assert.Equal(0, after.Cancels - before.Cancels);
        Assert.Equal(0, after.Closes - before.Closes);
        Assert.Equal(0, after.Places - before.Places);
        Assert.Equal(4m, positions.Single(p => p.Symbol == "ES").Quantity);
        Assert.Equal(1, working);
        Assert.Null(gw.FlattenToday(broker.AccountId));
    }

    /// <summary>
    /// P3, RENAMED AND TURNED ROUND — ONE ACCOUNT'S LOSS NEVER CLOSES ANOTHER ACCOUNT'S DAY.
    ///
    /// <para>Two accounts on one platform in one database. <c>ACC-B</c> has never traded: it has no
    /// fill at the platform and no position, and the figure TradeAgent measures for it must be its
    /// own.</para>
    /// </summary>
    [Fact]
    public async Task One_accounts_loss_never_closes_another_accounts_day()
    {
        var db = TestEnv.NewDb();
        var clock = new TestClock(Noon);

        // ---- account A loses a day.
        var a = new RecordingConnector(
            new FakeConnector(new FakeBroker { AccountId = "ACC-A" }) { EmergencyBudget = Unresolved.PressBudgetFor(1) }, "fake");
        var gwA = new TradingGateway(db, a, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gwA.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = "ACC-A";
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
        });
        await a.ConnectAsync();
        await gwA.RefreshHealthAsync();
        await gwA.PlaceAsync(new AgentContext("x"), "p3-a-open", TestEnv.Buy("ES"));
        a.Broker.PriceOffset = -20m;
        clock.Advance(Tick); await gwA.LossWatchAsync();
        clock.Advance(Tick); var closedA = await gwA.LossWatchAsync();
        log.WriteLine($"account A closed        : {string.Join(",", closedA.Closed)}");
        log.WriteLine($"account A realised      : {gwA.LedgerPnl(TradingGateway.StartOfDay(clock.GetUtcNow()), "today").Realized}");
        Assert.NotEmpty(closedA.Closed);

        // ---- account B: a different account on the same platform, which has never traded.
        var b = new RecordingConnector(
            new FakeConnector(new FakeBroker { AccountId = "ACC-B" }) { EmergencyBudget = Unresolved.PressBudgetFor(1) }, "fake");
        var gwB = new TradingGateway(db, b, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gwB.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = "ACC-B";
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
        });
        await b.ConnectAsync();
        await gwB.RefreshHealthAsync();

        var figure = gwB.LedgerPnl(TradingGateway.StartOfDay(clock.GetUtcNow()), "today");
        log.WriteLine($"account B has traded    : {(await b.GetExecutionsAsync("ACC-B", null)).Count} fills at the platform");
        log.WriteLine($"what TradeAgent says B lost today: {figure.Realized}");

        clock.Advance(Tick); await gwB.LossWatchAsync();
        clock.Advance(Tick); await gwB.LossWatchAsync();
        log.WriteLine($"breach rows now         : {string.Join(" ", db.KvStartingWith("loss_breach:").Select(x => x.Key))}");
        log.WriteLine($"B's day closed          : {gwB.DayClosed("ACC-B") is not null}");

        var verdict = "SENT";
        try { await gwB.PlaceAsync(new AgentContext("x"), "p3-b-buy", TestEnv.Buy("ES")); }
        catch (GatewayDeniedException ex) { verdict = ex.Code.ToString(); }
        log.WriteLine($"a first buy on account B: {verdict}");

        Assert.Equal(0m, figure.Realized);
        Assert.Equal(0, figure.Fills);
        Assert.Null(gwB.DayClosed("ACC-B"));
        Assert.Equal([LossBreach.DayKey("ACC-A", clock.GetUtcNow())],
            db.KvStartingWith("loss_breach:").Select(x => x.Key).ToArray());
        Assert.Equal("SENT", verdict);
    }

    /// <summary>
    /// P3b, RENAMED AND TURNED ROUND — ONE ACCOUNT'S PROFIT NEVER KEEPS ANOTHER TRADING PAST ITS
    /// OWN BUDGET. The direction of finding 2 that costs money.
    ///
    /// <para><c>ACC-B</c> holds a long that is 1250 USD down against its own 1000 USD daily budget,
    /// once with a winning account's fills beside it in the database and once on a clean one. The
    /// two arms have to answer the same, because the second account's fills are not B's day.</para>
    /// </summary>
    [Fact]
    public async Task One_accounts_profit_never_keeps_another_trading_past_its_budget()
    {
        foreach (var withAWinnerBeside in new[] { true, false })
        {
            var db = TestEnv.NewDb();
            var clock = new TestClock(Noon);

            if (withAWinnerBeside)
            {
                var a = new RecordingConnector(new FakeConnector(new FakeBroker { AccountId = "ACC-A" }), "fake");
                var gwA = new TradingGateway(db, a, new HealthRegistry(), new GatewayOptions { Clock = clock });
                gwA.Update(s =>
                {
                    s.Mode = TradingMode.PAPER;
                    s.SelectedAccountId = "ACC-A";
                    s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
                    s.Risk.MaxOrderQuantity = 10m;
                    s.Risk.MaxNotionalPerOrder = 0m;
                    s.Risk.MaxOpenPositions = 10;
                    s.Risk.MaxOrdersPerMinute = 100;
                });
                await a.ConnectAsync();
                await gwA.RefreshHealthAsync();
                await gwA.PlaceAsync(new AgentContext("x"), "p3b-a-buy", TestEnv.Buy("ES"));
                a.Broker.PriceOffset = 30m;
                await gwA.PlaceAsync(new AgentContext("x"), "p3b-a-sell",
                    new PlaceIntent("ES", OrderSide.Sell, OrderType.Market, 1m, null, null, TimeInForce.Day, null));
            }

            var b = new RecordingConnector(
                new FakeConnector(new FakeBroker { AccountId = "ACC-B" }) { EmergencyBudget = Unresolved.PressBudgetFor(1) }, "fake");
            var gwB = new TradingGateway(db, b, new HealthRegistry(), new GatewayOptions { Clock = clock });
            gwB.Update(s =>
            {
                s.Mode = TradingMode.PAPER;
                s.SelectedAccountId = "ACC-B";
                s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
                s.Risk.MaxOrderQuantity = 10m;
                s.Risk.MaxNotionalPerOrder = 0m;
                s.Risk.MaxOpenPositions = 10;
                s.Risk.MaxOrdersPerMinute = 100;
                s.Risk.MaxDailyLoss = 1_000m;
            });
            await b.ConnectAsync();
            await gwB.RefreshHealthAsync();
            await gwB.PlaceAsync(new AgentContext("x"), "p3b-b-buy", TestEnv.Buy("ES"));
            b.Broker.PriceOffset = -25m;

            clock.Advance(Tick); await gwB.LossWatchAsync();
            clock.Advance(Tick); var pass = await gwB.LossWatchAsync();

            var figure = gwB.LedgerPnl(TradingGateway.StartOfDay(clock.GetUtcNow()), "today");
            log.WriteLine($"[a winning account beside it: {withAWinnerBeside}]");
            log.WriteLine($"  what TradeAgent measures   : realised={figure.Realized}");
            log.WriteLine($"  B's day reached its budget : {pass.DayReached}");
            log.WriteLine($"  B's day closed             : {gwB.DayClosed("ACC-B") is not null}");
            log.WriteLine("");

            Assert.True(pass.DayReached);
            Assert.NotNull(gwB.DayClosed("ACC-B"));
            Assert.Equal(-1250m, figure.Realized);
        }
    }
}
