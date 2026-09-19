using TradeAgent.Core.Data;

namespace TradeAgent.Connectors.Paper;

/// <summary>One closed bar and the instrument it is of. What a bar source hands over.</summary>
public sealed record ClosedBar(string Symbol, KlineBar Bar);

/// <summary>
/// WHERE THE PAPER CONNECTOR'S PRICES COME FROM, and the only thing it is allowed to read them from.
///
/// <para>The connector simulates FILLS; it does not fetch prices, and this interface is what keeps
/// those two jobs apart. A connector that could reach out for a candle itself would be a connector
/// that can reach a venue, which is the one property <see cref="PaperConnector"/> exists to make
/// impossible by construction rather than by care.</para>
///
/// <para><see cref="SinceAsync"/> is EXCLUSIVE of <paramref name="openTimeExclusive"/> and must come
/// back in ascending open-time order. It is a HINT about what the caller has already seen and never
/// a guarantee it can rely on: a source is free to serve a bar again — a ledger restarted from an
/// older watermark does exactly that — and the connector is built so that re-serving a bar writes no
/// second fill. That guard is the fills table's key, not this argument.</para>
///
/// <para><see cref="BarClosed"/> is how a live source says a new bar has closed without being asked.
/// It carries the bar so a subscriber that only wants to know "something moved" pays nothing, and
/// the connector still settles from <see cref="SinceAsync"/> rather than from the event payload:
/// an event can be missed and a query cannot.</para>
/// </summary>
public interface IPaperBarSource
{
    Task<IReadOnlyList<KlineBar>> SinceAsync(string symbol, DateTimeOffset openTimeExclusive, CancellationToken ct = default);

    event Action<ClosedBar>? BarClosed;
}

/// <summary>
/// A bar source held in memory: the harness's, and the app's own stand-in until the forward-bars
/// ledger implements <see cref="IPaperBarSource"/> over what it has collected.
///
/// <para><b>Empty is the honest default and it is not a failure.</b> A paper connector with no bars
/// quotes nothing and fills nothing, and says so in <see cref="PaperConnector.StatusDetail"/> — which
/// is a different thing from a connector that pretends to a price it does not have.</para>
/// </summary>
public sealed class MemoryBarSource : IPaperBarSource
{
    readonly Lock _gate = new();
    readonly Dictionary<string, List<KlineBar>> _bars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Serve every bar held, whatever the caller says it has already seen — a ledger whose own
    /// watermark is behind the connector's, which is what a restart can really produce. It exists so
    /// a test can put the connector's idempotency under the thing it is a defence against; the
    /// connector must write no second fill either way.
    /// </summary>
    public bool ServeFromTheStart { get; init; }

    public event Action<ClosedBar>? BarClosed;

    /// <summary>Appends a closed bar and announces it, in that order: an announcement nobody can query is noise.</summary>
    public void Add(string symbol, KlineBar bar)
    {
        lock (_gate)
        {
            if (!_bars.TryGetValue(symbol, out var list)) _bars[symbol] = list = [];
            list.Add(bar);
            list.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        }
        BarClosed?.Invoke(new ClosedBar(symbol, bar));
    }

    public Task<IReadOnlyList<KlineBar>> SinceAsync(string symbol, DateTimeOffset openTimeExclusive, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_bars.TryGetValue(symbol, out var list)) return Task.FromResult<IReadOnlyList<KlineBar>>([]);
            IReadOnlyList<KlineBar> answer = ServeFromTheStart
                ? [.. list]
                : [.. list.Where(b => b.OpenTime > openTimeExclusive)];
            return Task.FromResult(answer);
        }
    }
}
