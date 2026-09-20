using TradeAgent.Connectors.Paper;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Platforms;

/// <summary>
/// THE FORWARD-BAR LEDGER, SERVED TO THE PAPER CONNECTOR AS PRICES.
///
/// <para><b>What it closes.</b> <see cref="ConnectorChoice.PaperBars"/> said "null until the
/// forward-bars ledger implements <see cref="IPaperBarSource"/>", and until this adapter existed
/// <c>Connectors.Create</c> handed the paper connector an EMPTY <see cref="MemoryBarSource"/>: a
/// connector that quotes nothing and fills nothing, whatever the collector had stored. This is the
/// one line between the minutes this installation collected and the fills it simulates.</para>
///
/// <para><b>It reads and it converts, and it does nothing else.</b> No fetch, no interpolation, no
/// carry-forward: the rows are served exactly as <see cref="ForwardBarStore.Since"/> answers them,
/// which is ascending by open time, exclusive of the caller's watermark, and with every gap still a
/// gap. A source that filled one would be inventing the price a fill is simulated at.</para>
///
/// <para><b><see cref="Announce"/> is how a collector says a minute closed</b>, and the bar it raises
/// is read back out of the ledger rather than taken from the caller's word — the ledger is what the
/// connector will settle from a moment later (<see cref="IPaperBarSource.SinceAsync"/>), so an
/// announcement of a row that is not there is no announcement at all. A subscriber that throws is
/// the announcer's problem and not this ledger's: the exception propagates to the caller that
/// raised it, which is where <c>ForwardBarCollector</c> already catches and records one.</para>
/// </summary>
public sealed class ForwardBarSource(ForwardBarStore store, string source = ForwardBars.Source)
    : IPaperBarSource
{
    /// <summary>Raised when <see cref="Announce"/> is told a minute closed and the ledger holds it.</summary>
    public event Action<ClosedBar>? BarClosed;

    /// <summary>The series this source serves. One row of <c>forward_bar</c>'s <c>source</c> column.</summary>
    public string Source => source;

    public Task<IReadOnlyList<KlineBar>> SinceAsync(
        string symbol, DateTimeOffset openTimeExclusive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<KlineBar> bars =
            [.. store.Since(symbol, openTimeExclusive, source: source).Select(Kline)];
        return Task.FromResult(bars);
    }

    /// <summary>
    /// ONE CLOSED MINUTE, ANNOUNCED. Wired to <c>ForwardBarCollector.BarClosed</c> by the host that
    /// owns both. A minute the ledger does not hold raises nothing.
    /// </summary>
    public void Announce(string symbol, DateTimeOffset openTime)
    {
        if (BarClosed is not { } subscribers) return;
        if (store.Bar(source, symbol, openTime) is not { } bar) return;
        subscribers(new ClosedBar(symbol, Kline(bar)));
    }

    static KlineBar Kline(ForwardBar b) =>
        new(b.OpenTime, b.Open, b.High, b.Low, b.Close, b.Volume);
}
