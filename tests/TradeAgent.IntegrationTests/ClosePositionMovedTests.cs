using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// U-press-inflight item 2, READ BACK OVER THE PIPE — because the schema now promises the agent a
/// word for it and a promise nobody drove is how `cancel_and_modify_outcomes` came to describe
/// deleted rules for four months (REVIEW 2026-09-05, finding 8).
///
/// A `close` refused at dispatch because the position moved leaves its record at `CREATED` and
/// touches no wire, so the leg's word must be `not-sent` — "nothing reached the broker from that leg
/// and the position is untouched" — and not `sent-not-confirmed`, which sets
/// `needs_reconciliation` and pauses every further order including the retry the message advises.
/// </summary>
public class ClosePositionMovedTests(ITestOutputHelper log)
{
    /// <summary>
    /// The fill lands inside the QUOTE read, which `PlaceAsync` makes after `close` has already
    /// decided its side and its size. `FakeConnector.GetQuoteAsync` raises `QuoteChanged` from
    /// inside itself, so this needs no timing and no second thread: it is the same window Codex F3
    /// names, entered deterministically.
    /// </summary>
    static void MovePositionDuringTheNextQuoteRead(FakeConnector conn, OrderSide side, decimal qty)
    {
        var done = 0;
        conn.QuoteChanged += _ =>
        {
            if (Interlocked.Exchange(ref done, 1) == 1) return;
            conn.Broker.Accept(new PlaceOrderCommand("SOMEBODY-ELSE", conn.Broker.AccountId, "ES",
                side, OrderType.Market, qty, null, null, TimeInForce.Day, "not TradeAgent"),
                FillBehaviour.FillImmediately);
        };
    }

    [Fact]
    public async Task A_close_leg_whose_position_moved_before_the_wire_answers_not_sent()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "pm-open", TestEnv.Buy(qty: 2m));
        Assert.Equal(2m, conn.Broker.Positions.Single(p => p.Symbol == "ES").Quantity);

        MovePositionDuringTheNextQuoteRead(conn, OrderSide.Sell, 1m);

        var pipe = "ta-posmoved-" + Guid.NewGuid().ToString("n")[..12];
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, Session = "agent-1", RequestId = "pm-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(reply.Ok, reply.Error?.Message);
        var root = JsonSerializer.SerializeToElement(reply.Data);
        log.WriteLine(root.ToString());

        var leg = root.GetProperty("outcomes").EnumerateArray().Single();
        var notClosed = root.GetProperty("not_closed").EnumerateArray().Single();

        Assert.Equal("not-sent", leg.GetProperty("outcome").GetString());
        Assert.Equal("not-sent", notClosed.GetProperty("outcome").GetString());
        Assert.Equal("CREATED", notClosed.GetProperty("state").GetString());
        Assert.Equal(0, root.GetProperty("closed").GetInt32());
        Assert.Equal(0, root.GetProperty("attempted").GetInt32());
        Assert.Equal(1, root.GetProperty("not_sent").GetInt32());

        // Nothing was sent, so the position is exactly what the intervening fill left.
        Assert.DoesNotContain(conn.Broker.Orders, o => o.ClientOrderId!.Contains("closeall"));
        Assert.Equal(1m, conn.Broker.Positions.Single(p => p.Symbol == "ES").Quantity);
        Assert.False(gw.HasUnconfirmedWork());
    }

    /// <summary>
    /// And the single `close` verb says the same thing in the words the schema promises, over the
    /// same pipe: POSITION_MOVED, naming both sizes, with nothing sent.
    /// </summary>
    [Fact]
    public async Task A_single_close_whose_position_moved_is_refused_with_position_moved()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "pm2-open", TestEnv.Buy(qty: 2m));
        MovePositionDuringTheNextQuoteRead(conn, OrderSide.Sell, 1m);

        var pipe = "ta-posmoved2-" + Guid.NewGuid().ToString("n")[..12];
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Close, Session = "agent-1", RequestId = "pm2-close",
            Args = new() { ["symbol"] = JsonSerializer.SerializeToElement("ES") }
        }).WaitAsync(TimeSpan.FromSeconds(20));

        log.WriteLine($"{reply.Error?.Code} — {reply.Error?.Message}");
        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.POSITION_MOVED), reply.Error!.Code);
        Assert.Contains("was 2 when this close was sized and is 1 now", reply.Error.Message);
        Assert.DoesNotContain(conn.Broker.Orders, o => o.ClientOrderId == "TA-pm2-close");
        Assert.False(gw.HasUnconfirmedWork());
    }
}
