using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;

namespace TradeAgent.Tests;

/// <summary>
/// A connector that COUNTS the mutating calls that reached it, and can HOLD one of them open.
///
/// Both halves exist for the same reason: "the gate refused" and "the gate refused in time" are
/// different claims, and only the wire can settle either. A refusal code proves nothing about
/// whether a frame went out — the fake broker's <c>ModifyOrderAsync</c> does not even write its
/// answer back into the book, so a modification that reached the platform leaves no trace there —
/// and a gate evaluated before an awaited read cannot be caught by any test that does not stop the
/// world INSIDE that read.
///
/// <see cref="Hold"/> is that stop: set it to a task, and the next call of the held kind parks until
/// it completes, with <see cref="Reached"/> already signalled. That is the barrier every
/// "authorized, then the owner pressed Stop" test is built out of.
/// </summary>
public sealed class RecordingConnector(FakeConnector inner) : ITradingConnector
{
    public FakeConnector Inner { get; } = inner;
    public FakeBroker Broker => Inner.Broker;
    public FaultProfile Faults => Inner.Faults;

    public int Places;
    public int Modifies;
    public int Cancels;
    public int Closes;
    public int Positions;

    /// <summary>Reads that reached the connector. Counted so that "zero connector calls" can be ASSERTED.</summary>
    public int Reads;

    /// <summary>Mutating calls of every kind that reached the connector.</summary>
    public int Mutations => Places + Modifies + Cancels + Closes;

    /// <summary>
    /// Everything that reached the connector, reads included.
    ///
    /// A refusal that claims to make no connector call cannot be settled by <see cref="Mutations"/>
    /// or by the broker's book: a frame refused three reads into the risk check places no order
    /// either, and looks identical. This is the number that tells the two apart.
    /// </summary>
    public int Calls => Reads + Mutations;

    /// <summary>
    /// Every placement, whole, in the order it arrived.
    ///
    /// <see cref="OrderInfo"/> does not carry the time-in-force, so the broker's book cannot say
    /// what a placement actually asked for — the command is the only place the value that left this
    /// process can be read back.
    /// </summary>
    public readonly List<PlaceOrderCommand> Placed = [];

    /// <summary>What the held call waits for. Null means nothing is held.</summary>
    public Task? Hold;

    /// <summary>Which call parks on <see cref="Hold"/>.</summary>
    public HeldCall Holds = HeldCall.Place;

    /// <summary>Completed the moment the held call has been entered, so a test knows it is inside.</summary>
    public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public enum HeldCall { Place, Positions, Modify }

    async Task Gate(HeldCall kind)
    {
        if (Holds != kind || Hold is null) return;
        Reached.TrySetResult();
        await Hold;
    }

    public string Id => Inner.Id;
    public string DisplayName => Inner.DisplayName;
    public ConnectorCapabilities Capabilities => Inner.Capabilities;
    public TimeSpan WorstCaseOperationPath => Inner.WorstCaseOperationPath;
    public TimeSpan EmergencyBudget => Inner.EmergencyBudget;
    public Task ConnectAsync(CancellationToken ct = default) => Inner.ConnectAsync(ct);
    /// <summary>Counts one read and hands the inner call straight back. <c>ConnectAsync</c> is not a read.</summary>
    T Read<T>(T call) { Interlocked.Increment(ref Reads); return call; }

    public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => Read(Inner.GetHealthAsync(ct));
    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Read(Inner.IsConnectedAsync(ct));
    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => Read(Inner.GetAccountsAsync(ct));
    public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => Read(Inner.GetAccountAsync(a, ct));
    public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => Read(Inner.GetInstrumentsAsync(ct));
    public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => Read(Inner.GetQuoteAsync(s, ct));

    public async Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Positions);
        await Gate(HeldCall.Positions);
        return await Inner.GetPositionsAsync(a, ct);
    }

    public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inactive, DateTimeOffset? since, CancellationToken ct = default) =>
        Read(Inner.GetOrdersAsync(a, inactive, since, ct));

    public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) =>
        Read(Inner.GetExecutionsAsync(a, since, ct));

    public async Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Places);
        lock (Placed) Placed.Add(cmd);
        await Gate(HeldCall.Place);
        return await Inner.PlaceOrderAsync(cmd, ct);
    }

    public async Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Modifies);
        await Gate(HeldCall.Modify);
        return await Inner.ModifyOrderAsync(c, ct);
    }

    public Task CancelOrderAsync(string id, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Cancels);
        return Inner.CancelOrderAsync(id, ct);
    }

    public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) =>
        Inner.CancelAllOrdersAsync(a, ct);

    public Task<OrderInfo?> ClosePositionAsync(string a, string s, string coid, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Closes);
        return Inner.ClosePositionAsync(a, s, coid, ct);
    }

    public event Action<HealthState>? ConnectionChanged { add => Inner.ConnectionChanged += value; remove => Inner.ConnectionChanged -= value; }
    public event Action<QuoteInfo>? QuoteChanged { add => Inner.QuoteChanged += value; remove => Inner.QuoteChanged -= value; }
    public event Action<OrderInfo>? OrderChanged { add => Inner.OrderChanged += value; remove => Inner.OrderChanged -= value; }
    public event Action<ExecutionInfo>? ExecutionReceived { add => Inner.ExecutionReceived += value; remove => Inner.ExecutionReceived -= value; }
    public event Action<PositionInfo>? PositionChanged { add => Inner.PositionChanged += value; remove => Inner.PositionChanged -= value; }
    public event Action<AccountInfo>? AccountChanged { add => Inner.AccountChanged += value; remove => Inner.AccountChanged -= value; }
    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
