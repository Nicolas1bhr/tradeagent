using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Platform = TradeAgent.Platforms.Connectors;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A PAPER PRESS CLOSE ANSWERED WHILE ANOTHER PLATFORM IS CONNECTED IS JUDGED BY PAPER'S OWN HISTORY (<c>U-windows-cards-2</c>; the
/// U-windows-cards card 1). The owner switched from TradeAgent's paper venue to another platform with a press close paper answered and never
/// finished; he answers it on the card. Paper's book is a file of the app's, read without connecting it, so its history is asked exactly
/// as the connected paper's would be: a close it still holds live outranks his answer, and a final listing his answer contradicts is
/// refused — nothing is written either way. The connected platform's history is never asked about it.
/// </summary>
public class PaperHistoryElsewhereTests : IDisposable
{
    readonly string _book = Path.Combine(Path.GetTempPath(), "tradeagent-tests", "paper-elsewhere", Guid.NewGuid().ToString("N"), "paper-PAPER-1.db");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_book)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A press close sent to paper that paper took and has not finished: WORKING, as the stream last left it.</summary>
    static ExecutionRequest PaperPressClose(ExecutionRequestStore store, string nonce, DateTimeOffset at)
    {
        var row = new ExecutionRequest
        {
            RequestId = $"{TradingGateway.ClosePress}-{nonce}-0", ConnectorId = Platform.Paper, AccountId = PaperConnector.TheAccount,
            Instrument = "ES", Intent = RequestIntent.PLACE, ParametersJson = "{}", ClientOrderId = $"TA-{TradingGateway.ClosePress}-{nonce}-0",
            CreatedAt = at, State = ExecutionState.CREATED, Mode = TradingMode.PAPER
        };
        store.TryCreate(row);
        store.Transition(row.RequestId, ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition(row.RequestId, ExecutionState.DISPATCHING, ExecutionState.WORKING, connectorOrderId: $"PP-{nonce}");
        return store.Get(row.RequestId)!;
    }

    /// <summary>Paper's book holds that close in <paramref name="state"/>.</summary>
    void BookHolds(ExecutionRequest row, ExecutionState state)
    {
        using var book = new PaperBook(_book, PaperConnector.TheAccount, "USDT", 10_000m);
        if (book.ByClientOrderId(row.ClientOrderId!) is null)
            Assert.True(book.TryInsert(new OrderInfo(row.ConnectorOrderId!, row.ClientOrderId, PaperConnector.TheAccount, "ES", OrderSide.Sell,
                OrderType.Market, 1m, 0m, null, null, ExecutionState.WORKING, null, row.CreatedAt), TimeInForce.Day));
        book.SetState(row.ClientOrderId!, state);
    }

    async Task<TradingGateway> OnTheSimulator(Database db, FakeBroker broker, bool readsPaper = true)
    {
        var venue = new FakeConnector(broker);
        var gw = new TradingGateway(db, venue, new HealthRegistry(),
            new GatewayOptions { HistoryElsewhere = readsPaper ? Platform.HistoryElsewhere(_book) : null });
        gw.Update(s => s.SelectedAccountId = "SIM-001");
        await venue.ConnectAsync();
        await gw.RefreshHealthAsync();
        return gw;
    }

    /// <summary>
    /// PAPER'S BOOK HOLDS THE CLOSE LIVE: HIS ANSWER IS REFUSED, NAMING PAPER, AND NOTHING IS WRITTEN. Then the book holds it FILLED and he
    /// answers "no longer working": refused as a contradiction, naming the answer that matches. Then he answers "It was filled": written,
    /// with the row saying paper's own history was asked.
    ///
    /// <para><b>RED on the base</b> (an answer about another platform's close stood alone): "It is no longer working" over a close paper
    /// still holds WORKING was written CANCELLED, unflagged — a close that can still fill the moment paper is connected again.</para>
    /// </summary>
    [Fact]
    public async Task A_paper_press_close_answered_on_another_platform_is_judged_by_paper_s_own_book()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        var row = PaperPressClose(store, "pp1", DateTimeOffset.UtcNow);
        BookHolds(row, ExecutionState.WORKING);
        var broker = new FakeBroker();
        await using var gw = await OnTheSimulator(db, broker);
        Assert.NotEqual(Platform.Paper, gw.Connector.Id);

        var live = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.AnswerFromTheCardAsync(row.RequestId, ExecutionState.CANCELLED, "checked: it is gone (test)"));
        Assert.Contains("the order history of paper, where it was sent, holds it as WORKING", live.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was recorded", live.Message, StringComparison.Ordinal);
        Assert.Equal(ExecutionState.WORKING, store.Get(row.RequestId)!.State);

        BookHolds(row, ExecutionState.FILLED);
        var contradicted = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.AnswerFromTheCardAsync(row.RequestId, ExecutionState.CANCELLED, "checked: it is gone (test)"));
        Assert.Contains("holds it as FILLED: it filled there, so the answer that matches it is \"It was filled\"", contradicted.Message,
            StringComparison.Ordinal);
        Assert.Equal(ExecutionState.WORKING, store.Get(row.RequestId)!.State);

        var answered = await gw.AnswerFromTheCardAsync(row.RequestId, ExecutionState.FILLED, "checked: it filled (test)");
        Assert.Equal(ExecutionState.FILLED, answered.State);
        Assert.False(answered.NeedsReconciliation);
        Assert.Contains("checked: it filled (test)", answered.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("paper's own order history was asked", answered.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONNECTED PLATFORM IS NEVER ASKED ABOUT IT: the simulator's book lists an order under the same client id as working, and paper's
    /// book holds the close CANCELLED — his "no longer working" stands. And a host that reads no other platform keeps the rule before
    /// this unit: his answer stands alone, the row saying the connected platform was not asked.
    /// </summary>
    [Fact]
    public async Task The_connected_platform_s_history_is_never_asked_about_a_paper_close()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        var row = PaperPressClose(store, "pp2", DateTimeOffset.UtcNow);
        BookHolds(row, ExecutionState.CANCELLED);
        var broker = new FakeBroker();
        broker.Accept(new PlaceOrderCommand(row.ClientOrderId!, "SIM-001", "ES", OrderSide.Sell, OrderType.Market, 1m, null, null,
            TimeInForce.Day, null), FillBehaviour.LeaveWorking);
        await using (var gw = await OnTheSimulator(db, broker))
        {
            var answered = await gw.AnswerFromTheCardAsync(row.RequestId, ExecutionState.CANCELLED, "checked: it is gone (test)");
            Assert.Equal(ExecutionState.CANCELLED, answered.State);
            Assert.Contains("paper's own order history was asked", answered.LastError ?? "", StringComparison.Ordinal);
        }

        var other = PaperPressClose(store, "pp3", DateTimeOffset.UtcNow);
        BookHolds(other, ExecutionState.WORKING);
        await using var blind = await OnTheSimulator(db, broker, readsPaper: false);
        var stood = await blind.AnswerFromTheCardAsync(other.RequestId, ExecutionState.CANCELLED, "checked: it is gone (test)");
        Assert.Equal(ExecutionState.CANCELLED, stood.State);
        Assert.Contains("whose order history says nothing about it, so it was not asked", stood.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// PAPER'S BOOK READ WITHOUT CONNECTING IT CHANGES NOTHING IN IT: no file is made where there is none (no book: no order), and a
    /// book's working order stays working — nothing is settled by the read.
    /// </summary>
    [Fact]
    public async Task Paper_s_book_is_read_without_making_or_settling_anything()
    {
        var reader = Platform.HistoryElsewhere(_book);
        Assert.True(reader.Reads(Platform.Paper));
        Assert.False(reader.Reads(Platform.Atas));
        Assert.False(reader.Reads(Platform.Simulator));
        Assert.Empty(await reader.GetOrdersAsync(Platform.Paper, PaperConnector.TheAccount, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.False(File.Exists(_book));

        using var db = TestEnv.NewDb();
        var row = PaperPressClose(new ExecutionRequestStore(db), "pp4", DateTimeOffset.UtcNow);
        BookHolds(row, ExecutionState.WORKING);
        var listed = Assert.Single(await reader.GetOrdersAsync(Platform.Paper, "any-account", row.CreatedAt.AddMinutes(-5)));
        Assert.Equal(row.ClientOrderId, listed.ClientOrderId);
        Assert.Equal(ExecutionState.WORKING, listed.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetOrdersAsync(Platform.Atas, "a", DateTimeOffset.UtcNow));
    }
}
