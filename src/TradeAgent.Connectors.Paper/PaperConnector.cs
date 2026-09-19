using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;

namespace TradeAgent.Connectors.Paper;

/// <summary>
/// What a <see cref="PaperConnector"/> is made of. Everything here is DECLARED at construction: the
/// account's currency and starting equity, the clock, the friction and where the bars come from.
/// </summary>
public sealed record PaperConnectorOptions
{
    /// <summary>Where the prices come from. Empty by default, and the status line says so.</summary>
    public IPaperBarSource Source { get; init; } = new MemoryBarSource();

    /// <summary>The one account. Simulated, always, and there is no second one to select by mistake.</summary>
    public string AccountId { get; init; } = PaperConnector.TheAccount;

    public string Currency { get; init; } = "USDT";

    /// <summary>What the account starts with, declared at creation. 10,000 by default.</summary>
    public decimal StartingEquity { get; init; } = 10_000m;

    /// <summary>
    /// The connector's clock, and it is injected because an order's fill is defined against it: a
    /// MARKET order fills on the first closed bar whose open time is AFTER it was placed, so a
    /// connector reading the machine clock could not be asked that question at all.
    /// </summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// The fee and slippage fractions AT THE MOMENT OF THE FILL — a function rather than a value
    /// because the owner may change them between one fill and the next, and the fill records what it
    /// was actually simulated under.
    /// </summary>
    public Func<PaperFriction> Friction { get; init; } = () => PaperFriction.None;

    /// <summary>The book's file, or null for <c>state/paper-&lt;account&gt;.db</c>.</summary>
    public string? BookFile { get; init; }

    /// <summary>
    /// The venue catalogue to take instruments from, or null for this installation's own
    /// (<c>VenueCatalog.Read()</c>: the built-ins plus <c>venues.json</c>).
    /// </summary>
    public VenueCatalogRead? Catalogue { get; init; }
}

/// <summary>
/// THE APP'S OWN PAPER CONNECTOR: real advancing prices in, simulated fills out, and NO WAY TO REACH
/// A VENUE.
///
/// <para><b>What it is for.</b> A strategy that has passed a backtest has been measured against bars
/// that were already written down. Paper trading is the next question — the same execution model
/// against prices that are still arriving — and it needs a connector the gateway can drive exactly
/// like any other, because the point is to exercise the real path: the risk gates, the dispatch
/// gate, the request ledger, the reconciler.</para>
///
/// <para><b>What it cannot do, by construction.</b> This assembly links against the connector SDK
/// and TradeAgent's own core and nothing else. There is no HTTP client, no socket, no vendor SDK and
/// no venue credential anywhere in it, and a test reads the assembly's reference list back and holds
/// it to that. Prices arrive through <see cref="IPaperBarSource"/>, which is a query against
/// something else; orders go into a SQLite file of its own. "It cannot reach a venue" is therefore a
/// fact about what it is linked against rather than a promise about what its methods do today.</para>
///
/// <para><b>The four rules on <c>IAtasAdapter</c> apply here and are not softened by the fills being
/// simulated.</b> The client order id is the book's PRIMARY KEY and comes back on every order and
/// every execution, so <see cref="ConnectorCapabilities.SupportsClientOrderId"/> is earned.
/// The file holds every order and every fill ever written and nothing prunes it, so the history
/// really reaches back to any timestamp asked for and
/// <see cref="ConnectorCapabilities.SupportsOrderHistory"/> is earned. A
/// <see cref="ConnectorRejectedException"/> is thrown for a DEFINITE refusal only — an instrument
/// this catalogue does not hold, a size that rounds down to nothing, a cancellation of an order that
/// has already filled — and everything ambiguous, an I/O error on the book or a bar source that
/// throws, propagates so the gateway records UNKNOWN and reconciles. And nothing here drives a user
/// interface.</para>
///
/// <para><b>A paper fill is a declared simulation and never evidence.</b> It says a price existed at
/// the open of a bar; it says nothing about whether that price was executable, what the queue looked
/// like, or what would have happened to a real order of that size. Every fill carries the friction
/// it was simulated under in its own words — including FRICTIONLESS, which is the connector saying
/// it modelled no cost at all rather than measuring a venue that charges none.</para>
/// </summary>
public sealed class PaperConnector : ITradingConnector, IConnectorStatusDetail
{
    /// <summary>The id the rest of the app recognises this platform by. Spelled once.</summary>
    public const string ConnectorId = "paper";

    /// <summary>The one account. Simulated; there is no other, and no live one to reach by mistake.</summary>
    public const string TheAccount = "PAPER-1";

    readonly PaperConnectorOptions _opt;
    readonly IPaperBarSource _source;
    readonly VenueCatalogRead _catalogue;
    readonly SemaphoreSlim _settling = new(1, 1);
    PaperBook? _book;

    public PaperConnector(PaperConnectorOptions? options = null)
    {
        _opt = options ?? new PaperConnectorOptions();
        _source = _opt.Source;
        _catalogue = _opt.Catalogue ?? VenueCatalog.Read();
    }

    public string Id => ConnectorId;
    public string DisplayName => "TradeAgent paper — real prices, simulated fills";

    /// <summary>
    /// Everything but streaming, and every one of them is earned rather than declared — see the type
    /// summary. <see cref="ConnectorCapabilities.SupportsStreaming"/> is FALSE because this connector
    /// publishes no quote of its own between bars: a price only exists here when a bar closes, and a
    /// stream that repeated the last close would be inventing ticks.
    /// </summary>
    public ConnectorCapabilities Capabilities => new(
        IsPaper: true,
        SupportsClientOrderId: true,
        SupportsOrderHistory: true,
        SupportsModify: true,
        SupportsClosePosition: true,
        SupportsStreaming: false);

    /// <summary>
    /// The book is a local file and the bar source is a query; the longest single thing one operation
    /// waits on is SQLite's own busy timeout, which the book sets to five seconds.
    /// </summary>
    public TimeSpan WorstCaseOperationPath { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The same two seconds every other connector gives an emergency, so the rule is measured here too.</summary>
    public TimeSpan EmergencyBudget { get; init; } = TimeSpan.FromSeconds(2);

    public event Action<HealthState>? ConnectionChanged;
    public event Action<QuoteInfo>? QuoteChanged;
    public event Action<OrderInfo>? OrderChanged;
    public event Action<ExecutionInfo>? ExecutionReceived;
    public event Action<PositionInfo>? PositionChanged;
    public event Action<AccountInfo>? AccountChanged;

    /// <summary>
    /// What this connector is doing and under what friction, in one line — including the thing an
    /// owner most needs to know and no capability flag can say: whether any bars are arriving at all.
    /// </summary>
    public string? StatusDetail
    {
        get
        {
            if (_book is null) return "the paper book has not been opened yet";
            var friction = _opt.Friction();
            var bars = _book.SymbolsSeen().Count;
            var prices = bars == 0
                ? "no bar has arrived yet, so nothing can be quoted and nothing will fill"
                : $"settled bars for {bars} instrument(s)";
            return $"simulated fills only, never a venue; {prices}; {friction.Sentence}";
        }
    }

    // ---- connection ---------------------------------------------------------------------------

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _book ??= new PaperBook(BookFile(), _opt.AccountId, _opt.Currency, _opt.StartingEquity);
        _source.BarClosed += OnBarClosed;
        ConnectionChanged?.Invoke(HealthState.READY);
        return Task.CompletedTask;
    }

    string BookFile() => _opt.BookFile ?? Path.Combine(Paths.State, $"paper-{Safe(_opt.AccountId)}.db");

    /// <summary>An account id is a declared constant here, but a file name is still not the place to trust one.</summary>
    static string Safe(string id) =>
        new([.. id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);

    public Task<HealthState> GetHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(_book is null ? HealthState.STARTING : HealthState.READY);

    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(_book is not null);

    PaperBook Book => _book ?? throw new ConnectorTransportException(
        "the paper book is not open; nothing was read and nothing was placed");

    // ---- what this platform describes -----------------------------------------------------------

    /// <summary>
    /// THE INSTRUMENTS THIS CONNECTOR WILL TRADE, AND THEY ARE THE CATALOGUE'S VERIFIED SPOT ROWS.
    ///
    /// <para>Verified only, and that is the same judgement <c>Backtests.Increment</c> makes for the
    /// same reason: a size is rounded DOWN to the quantity increment, so an increment nobody has
    /// confirmed against the venue's own instrument definition would make every simulated position
    /// one that could not have been taken. Out of the box that is an EMPTY list — Binance spot's
    /// BTCUSDT ships <c>verified = false</c> — and an empty list is the honest answer: the account
    /// owner confirms the row in <c>venues.json</c>, which is the one-line data fix
    /// <c>docs/DECISIONS.md</c>:73-78 asks for, and then this connector will trade it.</para>
    ///
    /// <para>The simulator's own venue is excluded. Its four futures are
    /// <c>FakeBroker.Instruments</c> — the practice simulator's own prices, which this connector does
    /// not have and must not appear to offer.</para>
    /// </summary>
    public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) =>
        Task.FromResult(Instruments);

    IReadOnlyList<InstrumentInfo> Instruments =>
    [
        .. _catalogue.Venues
            .Where(v => !string.Equals(v.Id, VenueCatalog.Simulator, StringComparison.Ordinal))
            .SelectMany(v => v.Instruments.Where(i => i.Verified)
                // TickValue is the tick and ContractSize is ONE, because these are spot rows: a unit
                // is a unit of the asset, so a tick of price on one unit is worth a tick. Neither is
                // a guess standing in for a number nobody has — a futures multiplier would be.
                .Select(i => new InstrumentInfo(i.Symbol, $"{i.Symbol} on {v.DisplayName}", v.Id,
                    i.TickSize, i.TickSize, 1m)))
    ];

    VenueInstrumentEntry? Row(string symbol) =>
        _catalogue.Venues
            .Where(v => !string.Equals(v.Id, VenueCatalog.Simulator, StringComparison.Ordinal))
            .SelectMany(v => v.Instruments)
            .FirstOrDefault(i => i.Verified && string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

    // ---- reads, every one of them off the book's own file ----------------------------------------

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) =>
        [await AccountAsync(ct)];

    public async Task<AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default) =>
        string.Equals(accountId, Book.AccountId, StringComparison.Ordinal) ? await AccountAsync(ct) : null;

    /// <summary>
    /// The account as the file says it stands: equity is the starting figure plus what closed trades
    /// realised minus what the declared fee took, and the unrealised figure is marked off the LAST
    /// CLOSED BAR — or NULL, because there has not been one. Null is not zero here: a position this
    /// connector cannot mark is a position whose loss nothing may read as absent.
    /// </summary>
    async Task<AccountInfo> AccountAsync(CancellationToken ct)
    {
        await SettleAsync(null, ct);
        var equity = Book.StartingEquity + Book.Realised - Book.Fees;
        return new AccountInfo(Book.AccountId, "TradeAgent paper account", Book.Currency,
            equity, equity, Unrealised(), IsSimulated: true, TradingEnabled: true);
    }

    decimal? Unrealised()
    {
        decimal total = 0m;
        foreach (var p in Book.Positions())
        {
            if (Book.LastClose(p.Symbol) is not { } close) return null;
            total += (close - p.AveragePrice) * p.Quantity;
        }
        return total;
    }

    /// <summary>
    /// The last closed bar's close, as a quote, and NOTHING ELSE — no bid, no ask and no invented
    /// spread. A bar is not a book: it says what traded, not what was on offer, and a connector that
    /// manufactured a spread around a close would be handing the gateway's value checks a number the
    /// venue never showed. <see cref="QuoteInfo.At"/> is the bar's CLOSE time, so a stale bar reads
    /// as a stale quote rather than a fresh one.
    /// </summary>
    public async Task<QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        await SettleAsync(symbol, ct);
        if (Book.LastClose(symbol) is not { } close) return null;
        var at = Book.SettledThrough(symbol) + (Book.Interval(symbol) ?? TimeSpan.Zero);
        var quote = new QuoteInfo(symbol, null, null, close, null, null, at);
        QuoteChanged?.Invoke(quote);
        return quote;
    }

    public async Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default)
    {
        await SettleAsync(null, ct);
        return
        [
            .. Book.Positions().Select(p => new PositionInfo($"PP-{p.Symbol}", Book.AccountId, p.Symbol,
                p.Quantity, p.AveragePrice,
                Book.LastClose(p.Symbol) is { } c ? (c - p.AveragePrice) * p.Quantity : null))
        ];
    }

    public async Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive,
        DateTimeOffset? since, CancellationToken ct = default)
    {
        await SettleAsync(null, ct);
        return Book.Orders(includeInactive, since);
    }

    public async Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since,
        CancellationToken ct = default)
    {
        await SettleAsync(null, ct);
        return [.. Book.Fills(since).Select(Executed)];
    }

    ExecutionInfo Executed(PaperFill f) => new(f.ExecutionId, f.ConnectorOrderId, f.ClientOrderId,
        Book.AccountId, f.Symbol, f.Side, f.Quantity, f.Price, f.At) { Fee = f.Fee };

    // ---- mutations -------------------------------------------------------------------------------

    /// <summary>
    /// Accepts an order into the book. It is ACCEPTED at once and WORKING; it does not fill here,
    /// because what it fills at is the next closed bar's open and that bar has not arrived.
    ///
    /// <para>A repeated client order id returns the order already in the book rather than writing a
    /// second one. The id is the book's primary key, which is what makes that possible at all, and
    /// it is the behaviour rule 1 is for: after a lost acknowledgement the gateway retries with the
    /// same id and must not end up with two positions.</para>
    /// </summary>
    public async Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default)
    {
        TransportLedger.Attempt();
        await SettleAsync(cmd.Symbol, ct);

        if (Book.ByClientOrderId(cmd.ClientOrderId) is { } already)
        {
            TransportLedger.Record(TransportOutcome.ReplyReceived);
            return already;
        }

        // DEFINITE refusals, and only definite ones. An instrument this catalogue does not hold
        // verified is a no the book can prove; so is a size that rounds down to nothing at the step
        // the venue trades in. Neither is ambiguous and neither may be retried.
        if (Row(cmd.Symbol) is not { } row)
        {
            TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorRejectedException(
                $"this installation's venue catalogue holds no VERIFIED instrument '{cmd.Symbol}' on a "
                + "venue the paper connector trades, so there is no quantity increment to round a size "
                + "to. Ask the account owner to record the instrument in venues.json.");
        }

        var quantity = decimal.Truncate(cmd.Quantity / row.QuantityIncrement) * row.QuantityIncrement;
        if (quantity <= 0m)
        {
            TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorRejectedException(
                $"{cmd.Quantity} {cmd.Symbol} rounds DOWN to nothing at this instrument's quantity "
                + $"increment of {row.QuantityIncrement}");
        }

        var order = new OrderInfo(Book.NextOrderId(), cmd.ClientOrderId, Book.AccountId, cmd.Symbol,
            cmd.Side, cmd.Type, quantity, 0m, cmd.LimitPrice, cmd.StopPrice, ExecutionState.WORKING,
            null, _opt.Clock());

        if (!Book.TryInsert(order, cmd.Tif))
        {
            // The unique key answered: somebody else wrote this id while we were deciding. Theirs is
            // the order, and there is exactly one.
            TransportLedger.Record(TransportOutcome.ReplyReceived);
            return Book.ByClientOrderId(cmd.ClientOrderId)!;
        }

        TransportLedger.Record(TransportOutcome.ReplyReceived);
        OrderChanged?.Invoke(order);
        return order;
    }

    /// <summary>Immediate while the order is WORKING. A filled or cancelled order is a definite no.</summary>
    public async Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default)
    {
        TransportLedger.Attempt();
        await SettleAsync(null, ct);

        var existing = Book.ByConnectorOrderId(cmd.ConnectorOrderId);
        if (existing is null)
        {
            TransportLedger.Record(TransportOutcome.ReplyReceived);
            throw new ConnectorRejectedException($"order {cmd.ConnectorOrderId} is not in this book");
        }
        if (existing.State != ExecutionState.WORKING)
        {
            TransportLedger.Record(TransportOutcome.ReplyReceived);
            throw new ConnectorRejectedException(
                $"order {cmd.ConnectorOrderId} is {existing.State} and can no longer be modified");
        }

        var quantity = cmd.Quantity ?? existing.Quantity;
        Book.SetPrices(existing.ClientOrderId!, cmd.LimitPrice ?? existing.LimitPrice,
            cmd.StopPrice ?? existing.StopPrice, quantity);
        TransportLedger.Record(TransportOutcome.ReplyReceived);

        var updated = Book.ByConnectorOrderId(cmd.ConnectorOrderId)!;
        OrderChanged?.Invoke(updated);
        return updated;
    }

    public async Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default)
    {
        TransportLedger.Attempt();
        await SettleAsync(null, ct);

        var existing = Book.ByConnectorOrderId(connectorOrderId);
        TransportLedger.Record(TransportOutcome.ReplyReceived);
        if (existing is null) throw new ConnectorRejectedException($"order {connectorOrderId} is not in this book");
        if (existing.State != ExecutionState.WORKING)
            throw new ConnectorRejectedException(
                $"order {connectorOrderId} is {existing.State} and can no longer be cancelled");

        Book.SetState(existing.ClientOrderId!, ExecutionState.CANCELLED);
        OrderChanged?.Invoke(Book.ByConnectorOrderId(connectorOrderId)!);
    }

    public async Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default)
    {
        TransportLedger.Attempt();
        await SettleAsync(null, ct);
        TransportLedger.Record(TransportOutcome.ReplyReceived);

        var working = Book.Orders(includeInactive: false, since: null);
        foreach (var o in working)
        {
            Book.SetState(o.ClientOrderId!, ExecutionState.CANCELLED);
            OrderChanged?.Invoke(Book.ByConnectorOrderId(o.ConnectorOrderId)!);
        }
        return [.. working.Select(o => o.ConnectorOrderId)];
    }

    /// <summary>A market order the other way, sized from the position. Null when there is nothing to close.</summary>
    public async Task<OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId,
        CancellationToken ct = default)
    {
        await SettleAsync(symbol, ct);
        var held = Book.Positions().FirstOrDefault(p =>
            string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (held is null || held.Quantity == 0m) return null;

        return await PlaceOrderAsync(new PlaceOrderCommand(clientOrderId, accountId, held.Symbol,
            held.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy, OrderType.Market,
            Math.Abs(held.Quantity), null, null, TimeInForce.Day, "close position")
        { Intent = OrderIntent.Close }, ct);
    }

    // ---- settlement ------------------------------------------------------------------------------

    void OnBarClosed(ClosedBar bar)
    {
        // The event says something moved; the query is what settles. Fire and forget, because a bar
        // source must not be blocked by a book — and every read below settles again anyway, so a
        // settlement lost here costs nothing but time.
        _ = Task.Run(async () =>
        {
            try { await SettleAsync(bar.Symbol, CancellationToken.None); }
            catch (Exception) { /* the next read will settle, and will throw where a caller can see it */ }
        });
    }

    /// <summary>
    /// Brings the book up to date with every bar the source has for <paramref name="symbol"/> — or
    /// for every symbol this book has ever seen, plus every symbol it holds a working order in.
    ///
    /// <para><b>It replays; it does not deduplicate.</b> Whatever the source hands over is applied in
    /// open-time order, and the only thing standing between a re-served bar and a second fill is the
    /// fills table's <c>UNIQUE (client_order_id, bar_open_time)</c>. That is deliberate: a watermark
    /// is right until the process that held it restarts, and this has to be safe precisely then.</para>
    ///
    /// <para>Nothing here converts a failure into a refusal. A bar source that throws and an I/O
    /// error on the book both propagate, because the caller must record UNKNOWN and reconcile rather
    /// than read a broken read as "the broker said no".</para>
    /// </summary>
    async Task SettleAsync(string? symbol, CancellationToken ct)
    {
        if (_book is null) return;
        await _settling.WaitAsync(ct);
        try
        {
            foreach (var s in SymbolsToSettle(symbol))
            {
                var bars = await _source.SinceAsync(s, Book.SettledThrough(s), ct);
                foreach (var bar in bars.OrderBy(b => b.OpenTime)) Apply(s, bar);
            }
        }
        finally { _settling.Release(); }
    }

    IReadOnlyList<string> SymbolsToSettle(string? symbol)
    {
        if (symbol is { Length: > 0 }) return [symbol];
        var seen = new HashSet<string>(Book.SymbolsSeen(), StringComparer.OrdinalIgnoreCase);
        foreach (var o in Book.Orders(includeInactive: false, since: null)) seen.Add(o.Symbol);
        foreach (var p in Book.Positions()) seen.Add(p.Symbol);
        return [.. seen.OrderBy(x => x, StringComparer.Ordinal)];
    }

    /// <summary>
    /// ONE CLOSED BAR, SETTLED IN THE BACKTEST'S ORDER (<c>docs/CONTRACTS.md</c>, "The backtest").
    ///
    /// <para>MARKET first, at the OPEN plus adverse slippage: it is the earliest price in the bar and
    /// the earliest one an order placed before the bar could have had. Then STOPs, which fill at the
    /// OPEN when the bar gapped through them and at their own level otherwise. Then LIMITs, at their
    /// level — and only if no stop fired on that instrument in this bar, because a bar that touched
    /// both a position's stop and its target counts as the STOP. Bars carry no intrabar ordering, and
    /// the other reading invents a winning trade.</para>
    /// </summary>
    void Apply(string symbol, KlineBar bar)
    {
        var friction = _opt.Friction();

        // INACTIVE ORDERS ARE IN THE CANDIDATE SET ON PURPOSE, and only a CANCELLED or REJECTED one
        // is dropped here. Whether an order has already had this bar is the fills table's key to
        // answer — <see cref="PaperBook.FilledBefore"/> only rules out an EARLIER bar — because an
        // order excluded for being FILLED would make a replay harmless for a reason nothing
        // enforces, and the day the constraint went missing nothing would notice.
        var candidates = Book.Orders(includeInactive: true, since: null)
            .Where(o => o.State is not (ExecutionState.CANCELLED or ExecutionState.REJECTED))
            .Where(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Where(o => bar.OpenTime > o.At)
            .Where(o => !Book.FilledBefore(o.ClientOrderId!, bar.OpenTime))
            .OrderBy(o => o.At).ThenBy(o => o.ConnectorOrderId, StringComparer.Ordinal)
            .ToList();

        foreach (var o in candidates.Where(o => o.Type == OrderType.Market))
            Fill(o, bar, Slipped(bar.Open, o.Side, friction), friction);

        var protectionFired = false;
        foreach (var o in candidates.Where(o => o.Type is OrderType.Stop or OrderType.StopLimit))
        {
            if (o.StopPrice is not { } level) continue;
            var triggered = o.Side == OrderSide.Buy ? bar.High >= level : bar.Low <= level;
            if (!triggered) continue;
            // Gapped THROUGH the level at the open: the fill is the open, which is worse, and that is
            // the whole point of the rule. Otherwise it is the level itself.
            var gapped = o.Side == OrderSide.Buy ? bar.Open >= level : bar.Open <= level;
            Fill(o, bar, gapped ? bar.Open : level, friction);
            protectionFired = true;
        }

        if (!protectionFired)
            foreach (var o in candidates.Where(o => o.Type == OrderType.Limit))
            {
                if (o.LimitPrice is not { } level) continue;
                var touched = o.Side == OrderSide.Buy ? bar.Low <= level : bar.High >= level;
                if (touched) Fill(o, bar, level, friction);
            }

        Book.MarkSettled(symbol, bar.OpenTime, bar.Close);
    }

    /// <summary>Adverse always: a buy pays up, a sell gets less. Slippage that helped would be a gift.</summary>
    static decimal Slipped(decimal price, OrderSide side, PaperFriction friction) =>
        side == OrderSide.Buy
            ? price * (1m + friction.SlippageFraction)
            : price * (1m - friction.SlippageFraction);

    void Fill(OrderInfo order, KlineBar bar, decimal price, PaperFriction friction)
    {
        var attempt = new PaperFill(
            // The book gives it its own: see PaperBook.TryFill on why an id spelled from the order
            // and the bar would quietly become the constraint this connector relies on.
            ExecutionId: "",
            ClientOrderId: order.ClientOrderId!,
            ConnectorOrderId: order.ConnectorOrderId,
            Symbol: order.Symbol,
            Side: order.Side,
            Quantity: order.Quantity,
            Price: price,
            Fee: order.Quantity * price * friction.FeeFraction,
            BarOpenTime: bar.OpenTime,
            At: _opt.Clock(),
            Friction: friction.Sentence);

        // The constraint decides. A re-served bar gets here and writes nothing at all.
        if (Book.TryFill(attempt) is not { } fill) return;

        OrderChanged?.Invoke(Book.ByConnectorOrderId(order.ConnectorOrderId)!);
        ExecutionReceived?.Invoke(Executed(fill));
        foreach (var p in Book.Positions())
            if (string.Equals(p.Symbol, order.Symbol, StringComparison.OrdinalIgnoreCase))
                PositionChanged?.Invoke(new PositionInfo($"PP-{p.Symbol}", Book.AccountId, p.Symbol,
                    p.Quantity, p.AveragePrice, Book.LastClose(p.Symbol) is { } c ? (c - p.AveragePrice) * p.Quantity : null));
        var equity = Book.StartingEquity + Book.Realised - Book.Fees;
        AccountChanged?.Invoke(new AccountInfo(Book.AccountId, "TradeAgent paper account", Book.Currency,
            equity, equity, Unrealised(), IsSimulated: true, TradingEnabled: true));
    }

    public ValueTask DisposeAsync()
    {
        _source.BarClosed -= OnBarClosed;
        _book?.Dispose();
        _book = null;
        _settling.Dispose();
        return ValueTask.CompletedTask;
    }
}
