using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// EVERY ID THE GATEWAY SENDS IS ONE A BROKER CAN BE ASKED TO ROUND-TRIP (REVIEW 2026-09-05
/// finding 4, executed as probe P2; and UNVERIFIED 5).
///
/// The operator's press built its per-target request id out of a string it had taken off the
/// CONNECTOR — <c>op-close-{nonce}-{symbol}</c> for a close, <c>op-cancel-{nonce}-{orderId}</c> for
/// a cancel — and handed <c>ClientOrderIdFor</c> of that to the broker as the field safety rule 1
/// requires to come back unchanged. `GatewayPipeServer` enforces `[A-Za-z0-9-]` and a 61-character
/// budget on the agent path FOR EXACTLY THIS REASON; the operator path enforced nothing. Two
/// ordinary CME instrument names break the character set and one breaks the 64-character ceiling:
/// P2 measured `TA-op-close-2d892bd06ab04369-ES 12-25 [CME Globex Futures]` at 58 characters with
/// `' []'` outside the set, and the MES name at 65.
///
/// The id is now minted the way the agent path's `op-{nonce}-{intent}-{index}` is — from things the
/// gateway itself chose — and the symbol lives on the record, where it already was.
/// </summary>
public class PressIdShapeTests(ITestOutputHelper log)
{
    /// <summary>
    /// The shape the pipe server enforces on the way in, re-derived here so the two cannot drift:
    /// the gateway's own budget must equal the one the agent path polices.
    /// </summary>
    [Fact]
    public void The_gateways_id_budget_is_the_one_the_agent_pipe_enforces()
    {
        var pipeBudget = (int)typeof(GatewayPipeServer)
            .GetField("MaxRequestIdChars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        log.WriteLine($"GatewayPipeServer.MaxRequestIdChars : {pipeBudget}");
        log.WriteLine($"TradingGateway.MaxRequestIdChars    : {TradingGateway.MaxRequestIdChars}");
        log.WriteLine($"TradingGateway.MaxClientOrderIdChars: {TradingGateway.MaxClientOrderIdChars}");

        Assert.Equal(pipeBudget, TradingGateway.MaxRequestIdChars);
        Assert.Equal(61, TradingGateway.MaxRequestIdChars);
        Assert.Equal(64, TradingGateway.MaxClientOrderIdChars);
    }

    /// <summary>
    /// P2, TURNED THE RIGHT WAY UP. Both of its instrument names, read back off the broker's book.
    /// </summary>
    [Theory]
    // A perfectly ordinary ATAS instrument name: exchange in brackets, a space, an expiry.
    [InlineData("ES 12-25 [CME Globex Futures]")]
    // The same shape on a micro contract, which is what pushed it past the 64-character ceiling.
    [InlineData("MES 03-26 [CME Globex Futures Micro]")]
    public async Task The_operator_close_all_sends_a_client_order_id_the_agent_pipe_would_accept(string symbol)
    {
        // The full ATAS name IS the instrument here, so it is what the owner's allowlist names.
        var (gw, conn, db) = await TestEnv.Ready(s =>
        {
            s.Risk.MaxOrderQuantity = 5m;
            s.Risk.InstrumentAllowlist = [symbol];
        });
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("a"), "open-1",
            new PlaceIntent(symbol, OrderSide.Buy, OrderType.Market, 2m, null, null, TimeInForce.Day, null));
        Assert.Single(conn.Broker.Positions);

        var before = conn.Broker.Orders.Count;
        await gw.OperatorCloseAllAsync();

        var closing = conn.Broker.Orders.Skip(before).Single();
        var coid = closing.ClientOrderId!;
        log.WriteLine($"client order id sent : {coid}");
        log.WriteLine($"length               : {coid.Length}   (ceiling: {TradingGateway.MaxClientOrderIdChars})");
        log.WriteLine($"characters outside [A-Za-z0-9-] : " +
                      $"'{new string(coid.Where(c => !char.IsAsciiLetterOrDigit(c) && c != '-').Distinct().ToArray())}'");

        Assert.Matches("^[A-Za-z0-9-]+$", coid);
        Assert.True(coid.Length <= TradingGateway.MaxClientOrderIdChars,
            $"the id is {coid.Length} characters against a ceiling of {TradingGateway.MaxClientOrderIdChars}");

        // The symbol did not vanish — it is on the record, where the card and the reconciler read it.
        var row = Assert.Single(gw.Requests.Query("request_id LIKE 'op-close-%' AND intent='PLACE'"));
        log.WriteLine($"record               : {row.RequestId} instrument={row.Instrument}");
        Assert.Equal(symbol, row.Instrument);
        Assert.True(TradingGateway.IsSendableId(row.RequestId), $"'{row.RequestId}' is not a sendable id");
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE SAME RULE ON THE CANCEL SIDE, where the string taken off the connector is a BROKER ORDER
    /// ID rather than an instrument. P2 did not measure this one; it is the same defect, and an
    /// order id is exactly as much the platform's string as a symbol is.
    /// </summary>
    [Fact]
    public async Task The_operator_cancel_all_names_its_legs_without_the_brokers_order_id()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("a"), "open-a",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        await gw.PlaceAsync(new AgentContext("a"), "open-b",
            new PlaceIntent("NQ", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));

        // The broker's own reference for one of them, so the assertion is about a real string.
        var brokerIds = conn.Broker.Orders.Select(o => o.ConnectorOrderId).ToList();
        await gw.OperatorCancelAllAsync();

        var legs = gw.Requests.Query("request_id LIKE 'op-cancel-%' AND intent='CANCEL'");
        foreach (var leg in legs)
            log.WriteLine($"leg : {leg.RequestId}   coid={leg.ClientOrderId}   parameters={leg.ParametersJson}");

        Assert.Equal(2, legs.Count);
        foreach (var leg in legs)
        {
            Assert.Matches("^[A-Za-z0-9-]+$", leg.ClientOrderId);
            Assert.True(leg.ClientOrderId!.Length <= TradingGateway.MaxClientOrderIdChars);
            foreach (var brokerId in brokerIds)
                Assert.DoesNotContain(brokerId, leg.RequestId);
            // ...and the order it is about is still on the record, in ParametersJson.
            Assert.Contains(brokerIds.Single(b => leg.ParametersJson.Contains(b)), leg.ParametersJson);
        }
        Assert.Equal(2, legs.Select(l => l.RequestId).Distinct().Count());
        await gw.DisposeAsync();
    }

    /// <summary>
    /// UNVERIFIED 5: <c>OpenPressRow</c> LATCHED BEFORE IT WROTE.
    ///
    /// The latch is a pause that does not depend on the database, so it went in first — but if the
    /// insert then threw, the latch named a request id with no row, and
    /// <c>ReleaseLatchesTheStoreCanVouchFor</c> skips ids whose row is missing. The pause could not
    /// be lifted by any route except a restart: not a reconcile pass, not the owner confirming the
    /// card, because there was nothing on the card to confirm.
    ///
    /// The row is written first now. Nothing was sent when the insert fails, so there is nothing to
    /// pause OVER; and when the insert succeeds it is already flagged, which is a durable pause the
    /// latch is only the memory copy of.
    /// </summary>
    [Fact]
    public async Task A_press_whose_row_cannot_be_written_leaves_no_latch_that_only_a_restart_clears()
    {
        var file = Path.Combine(TestEnv.Home, $"pressrow-{Guid.NewGuid():n}.db");
        using var db = new Database(file);
        var conn = new FakeConnector(new FakeBroker());
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        await gw.PlaceAsync(new AgentContext("a"), "pr-open", TestEnv.Buy("ES", 1m));

        // CANCEL-ALL, because its press row is the FIRST database write the press makes — close-all
        // writes its composite first, so the composite insert is what a locked database stops and
        // `OpenPressRow` is never reached.
        //
        // Fail that INSERT: an external writer holds the database, and the command gives up on its
        // own timeout rather than the provider's thirty-second default. WAL, so reads still work and
        // what fails is the write this test is about.
        db.Connection.DefaultTimeout = 1;
        using var blocker = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False");
        blocker.Open();
        using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }

        var thrown = await Record.ExceptionAsync(() => gw.OperatorCancelAllAsync());
        log.WriteLine($"press threw : {thrown?.GetType().Name}: {thrown?.Message}");
        log.WriteLine($"cancel calls on the wire : {conn.Broker.Orders.Count(o => o.State == ExecutionState.CANCELLED)}");

        using (var rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }

        // Nothing was written and nothing was sent, so nothing may be latched under an id with no row.
        var rows = gw.Requests.Query("request_id LIKE 'op-cancel-%'");
        log.WriteLine($"press rows written : {rows.Count}");
        log.WriteLine($"unresolved press   : {gw.UnresolvedPressNonce(TradingGateway.CancelPress) ?? "none"}");
        Assert.Empty(rows);
        Assert.Null(gw.UnresolvedPressNonce(TradingGateway.CancelPress));

        // ...and the pause it would have held is clearable without a restart: a reconcile pass that
        // finds nothing wrong lets trading resume.
        var pass = await gw.ReconcileAsync();
        log.WriteLine($"reconcile clean : {pass.Clean} — {string.Join("; ", pass.Details)}");
        await gw.RefreshHealthAsync();
        var authorized = gw.TryAuthorizeExecution(new AgentContext("a"), out var why, out _);
        log.WriteLine($"trading authorized after one reconcile pass : {authorized} ({why})");
        Assert.True(authorized, why);
        await gw.DisposeAsync();
    }
}
