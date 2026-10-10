using System.Globalization;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Tests;

/// <summary>
/// A TAPE OF THE TEST'S OWN, IN A TEMPORARY FILE, WRITTEN THROUGH <see cref="TapeStore"/> — the collector's one writer —
/// with readings of BTCUSDT's premium index stamped and received when the test says (<c>U-language-v2a</c>'s helper,
/// lifted here by <c>U-runner-features</c> so the paper runner's tests write the tape the backtest's tests do). No
/// network: the built-in address is named on each fetch and never asked anything.
/// </summary>
public static class TapeReadings
{
    /// <summary>The premium index's own address on this build's row: a reading recorded from it on time is O-LIVE.</summary>
    public static string BuiltInUrl => TapeSourceCatalog.BinanceUmBaseUrl + "/fapi/v1/premiumIndex";

    /// <summary>A new tape file in this test process's home. Nothing is created until a store opens it.</summary>
    public static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    /// <summary>The fetch one reading arrived on, received at <paramref name="receivedAt"/>.</summary>
    public static TapeFetch Fetch(DateTimeOffset receivedAt) => new()
    {
        Source = TapeSourceCatalog.Premium,
        Series = "premium-index",
        Url = BuiltInUrl,
        RequestedAt = receivedAt.AddMilliseconds(-300),
        ReceivedAt = receivedAt,
        HttpStatus = 200
    };

    /// <summary>
    /// One premium-index reading of BTCUSDT in the vendor's shape, stamped <paramref name="stamped"/> and received two
    /// seconds later unless <paramref name="received"/> says when.
    /// </summary>
    public static void Reading(TapeStore store, DateTimeOffset stamped, string funding, DateTimeOffset? received = null) =>
        store.Append(Fetch(received ?? stamped.AddSeconds(2)),
        [
            new TapeItem("BTCUSDT", stamped,
                $$"""{"symbol":"BTCUSDT","markPrice":"85000.00000000","lastFundingRate":"{{funding}}","time":{{stamped.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}}}""")
        ]);
}
