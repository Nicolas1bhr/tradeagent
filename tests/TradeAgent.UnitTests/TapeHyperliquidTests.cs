using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// HYPERLIQUID'S PUBLIC PERPETUAL CONTEXTS ON THE TAPE (<c>U-tape-chain</c>), against a loopback listener and never against
/// the vendor. The bodies are the vendor's own shape as measured on 2026-10-08 (<c>docs/RESEARCH-REQUIRED.md</c>, C5f): a
/// two-element list, the universe beside the contexts, zipped by index, with no time anywhere in it.
/// </summary>
public class TapeHyperliquidTests(ITestOutputHelper log)
{
    const string Series = "asset-ctxs";

    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    /// <summary>
    /// (f) A HYPERLIQUID READING ON TIME FROM ITS OWN ORIGIN IS LIVE, by the store's one rule and this build's row: received
    /// within the 300 s cadence plus 30 s of its source time — the answer's <c>Date</c> — from Hyperliquid's own API
    /// origin is <c>O-LIVE</c>; one second more, or the same reading from any other origin, is <c>O-ARCH</c>. Early by
    /// the cadence plus 30 s is still live, a clock a little behind the vendor's being no reason to call a reading late.
    /// </summary>
    [Fact]
    public void A_hyperliquid_reading_on_time_from_its_own_origin_is_live()
    {
        using var store = new TapeStore(NewFile());
        var date = new DateTimeOffset(2026, 10, 8, 10, 38, 39, TimeSpan.Zero);
        var own = TapeSourceCatalog.HyperliquidBaseUrl + "/info";

        string ClassOf(string url, int seconds, string coin)
        {
            var at = date.AddSeconds(seconds);
            var append = store.Append(new TapeFetch
            {
                Source = TapeSourceCatalog.HyperliquidAssetCtxs, Series = Series, Url = url,
                RequestedAt = at, ReceivedAt = at, HttpStatus = 200
            }, [new TapeItem(coin, date, $$"""{"name":"{{coin}}","openInterest":"40203.71654"}""")]);
            var row = Assert.Single(store.ObservationsOf(append.FetchId));
            log.WriteLine($"{coin} {url} {seconds,5} s -> {row.EvidenceClass}");
            return row.EvidenceClass;
        }

        Assert.Equal(TapeClass.Live, ClassOf(own, 1, "BTC"));
        Assert.Equal(TapeClass.Live, ClassOf(own, 330, "ETH"));
        Assert.Equal(TapeClass.Arch, ClassOf(own, 331, "SOL"));
        Assert.Equal(TapeClass.Live, ClassOf(own, -330, "BNB"));
        Assert.Equal(TapeClass.Arch, ClassOf(own, -331, "XRP"));
        Assert.Equal(TapeClass.Arch, ClassOf("http://127.0.0.1:9/info", 1, "DOGE"));
    }
}
