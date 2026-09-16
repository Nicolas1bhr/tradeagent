using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-scope-identity — THE AVERAGE-COST BOOK IS ONE ACCOUNT'S, AND THE ARITHMETIC IS WHERE THAT IS
/// ENFORCED.
///
/// <para><c>TradingGateway.LedgerPnl</c> now hands <see cref="Pnl.Compute"/> one
/// <c>(connector, account)</c> pair's rows, which is the first line. This is the second and it is
/// not decoration: average cost is a running quantity, so two accounts' fills in one book do not
/// make a smaller answer, they make a WRONG one — account A's buy and account B's sell read as one
/// round trip that neither of them made (REVIEW 2026-09-16, finding 2). Anything that ever hands
/// this function two accounts — a future all-accounts report, a backtest reader — gets the right
/// answer because the arithmetic refuses to mix them.</para>
/// </summary>
public class PnlScopeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    static Fill F(string account, DateTimeOffset at, string side, decimal price, string id) =>
        new(account, id, at, "ES", side, 1m, price, "FB-1", null, null, null,
            FillSource.Event, 0m, at);

    /// <summary>
    /// TWO ACCOUNTS, ONE INSTRUMENT, ONE LEDGER. <c>ACC-A</c> is long one from 4000 and has closed
    /// nothing; <c>ACC-B</c> is short one from 4100 and has closed nothing. Neither has realised
    /// anything, and the only way to get a number out of this is to let one account's sell close the
    /// other's buy.
    /// </summary>
    [Fact]
    public void One_accounts_sell_never_closes_another_accounts_buy()
    {
        var report = Pnl.Compute(new PnlInputs
        {
            AllFills =
            [
                F("ACC-A", Noon, "Buy", 4_000m, "a1"),
                F("ACC-B", Noon.AddMinutes(1), "Sell", 4_100m, "b1")
            ],
            Positions = null,
            Window = "all",
            AsOf = Noon.AddHours(1)
        });

        log.WriteLine($"realised over both accounts' fills: {report.Realized}");
        log.WriteLine($"fills counted                     : {report.Fills}");

        // Neither account has closed anything, so nothing is realised. 100 would be A's long being
        // closed by B's sell — a round trip that never happened, on money that is not one owner's.
        Assert.Equal(0m, report.Realized);
        Assert.Equal(2, report.Fills);
    }
}
