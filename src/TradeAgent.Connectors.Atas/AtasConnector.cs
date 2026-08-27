using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

namespace TradeAgent.Connectors.Atas;

/// <summary>
/// The ATAS trading backend, seen from TradeAgent's side. It owns the pipe the in-ATAS bridge
/// connects to, and turns the bridge protocol into <see cref="ITradingConnector"/>.
///
/// Two properties matter for safety:
///   - a transport problem surfaces as <see cref="ConnectorTransportException"/> (indefinite), and only
///     an explicit rejection from the bridge becomes <see cref="ConnectorRejectedException"/> (definitive);
///   - capabilities come from the bridge's handshake, so a bridge that cannot serve order history
///     will not be granted autonomous live trading by the gateway.
/// </summary>
public sealed class AtasConnector(string? pipeName = null, TimeSpan? rpcTimeout = null) : ITradingConnector, IConnectorStatusDetail
{
    readonly string _pipe = pipeName ?? Paths.BridgePipeName;
    readonly TimeSpan _timeout = rpcTimeout ?? TimeSpan.FromSeconds(10);
    readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeFrame>> _pending = new();
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _sendGate = new(1, 1);

    NamedPipeServerStream? _pipeStream;
    StreamWriter? _writer;
    Task? _accept;
    volatile bool _connected;
    BridgeHello? _hello;
    IncompatibleBridge? _incompatible;
    DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;

    public string Id => "atas";
    public string DisplayName => "ATAS";
    public BridgeHello? Bridge => _hello;

    /// <summary>
    /// Set when a bridge dialled in speaking a protocol version this build does not, and null
    /// otherwise. Display only — see <see cref="IncompatibleBridge"/>. It is deliberately a separate
    /// property from <see cref="Bridge"/> so that no capability can ever be read off it by accident.
    /// </summary>
    public IncompatibleBridge? Incompatible => _incompatible;

    /// <summary>One line explaining a FAILED trading connection, or null when there is nothing to
    /// add. Read by the gateway for the health detail the dashboard shows.</summary>
    public string? StatusDetail => _incompatible?.ToString();

    /// <summary>Missing for longer than this and we treat the bridge as gone.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public ConnectorCapabilities Capabilities => _hello is null
        ? new ConnectorCapabilities(false, false, false, false, false, true)
        : new ConnectorCapabilities(_hello.IsSimulated, _hello.SupportsClientOrderId, _hello.SupportsOrderHistory,
            _hello.SupportsModify, _hello.SupportsClosePosition, true);

    public event Action<HealthState>? ConnectionChanged;
    public event Action<QuoteInfo>? QuoteChanged;
    public event Action<OrderInfo>? OrderChanged;
    public event Action<ExecutionInfo>? ExecutionReceived;
    public event Action<PositionInfo>? PositionChanged;
    public event Action<AccountInfo>? AccountChanged;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _accept ??= Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipeStream = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await _pipeStream.WaitForConnectionAsync(ct);

                var reader = new StreamReader(_pipeStream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
                _writer = new StreamWriter(_pipeStream, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

                string? line;
                while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
                    Dispatch(line);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* the bridge died or ATAS closed; fall through and wait for it again */ }
            finally
            {
                Drop("the ATAS bridge disconnected");
                _pipeStream?.Dispose();
                _pipeStream = null;
            }
            if (!ct.IsCancellationRequested) { try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; } }
        }
    }

    void Drop(string why)
    {
        var was = _connected;
        // An incompatible bridge never set _connected, so 'was' alone would not fire — and the
        // dashboard would go on displaying "bridge 9.9.9 speaks protocol 2" about a bridge that is
        // no longer on the pipe. Clearing the reason without re-announcing the row leaves the model
        // and the screen disagreeing, which on a status display is the whole of the bug.
        var wasExplained = _incompatible is not null;
        _connected = false;
        _hello = null;
        // The bridge is gone; "wrong version" stops being the live explanation the moment there is
        // nothing on the pipe to be the wrong version.
        _incompatible = null;
        _writer = null;
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new ConnectorTransportException(why));
        if (was || wasExplained) ConnectionChanged?.Invoke(HealthState.FAILED);
    }

    void Dispatch(string line)
    {
        BridgeFrame? f;
        try { f = Json.Read<BridgeFrame>(line); } catch (Exception) { return; }
        if (f is null) return;

        if (f.Event is not null) { HandleEvent(f); return; }

        if (f.Op == BridgeOps.Hello)
        {
            var hello = f.Data.HasValue ? f.Data.Value.Deserialize<BridgeHello>(Json.Options) : null;
            if (hello is null) return;
            if (!Versions.BridgeCompatible(hello.BridgeProtocolVersion))
            {
                // A mismatched bridge is refused outright rather than half-trusted: _hello stays
                // null, so Capabilities keeps reporting nothing supported and the gateway cannot
                // trade on anything this bridge claims. Its IDENTITY is kept separately, because
                // "FAILED" with no version number is not a repairable message — and keeping it in a
                // different field is what stops it being mistaken for a capability later.
                _incompatible = new IncompatibleBridge(
                    hello.BridgeProtocolVersion, Versions.BridgeProtocolVersion,
                    IncompatibleBridge.Clean(hello.BridgeVersion), IncompatibleBridge.Clean(hello.AtasVersion));
                _connected = false;
                ConnectionChanged?.Invoke(HealthState.FAILED);
                return;
            }
            _hello = hello;
            _incompatible = null;
            _connected = true;
            _lastHeartbeat = DateTimeOffset.UtcNow;
            ConnectionChanged?.Invoke(HealthState.READY);
            return;
        }

        if (f.Op == BridgeOps.Heartbeat)
        {
            _lastHeartbeat = DateTimeOffset.UtcNow;

            // A heartbeat now carries the bridge's current answer, because capabilities are not
            // settled at the handshake: SupportsClientOrderId cannot be true until an order has
            // proved it, and the account is unknown until ATAS has a portfolio. Adopt the newer
            // answer — but only a whole, version-compatible one. A half-read frame must leave the
            // latched handshake alone rather than silently widen or narrow what the gateway
            // believes this platform is able to prove.
            if (!f.Data.HasValue) return;
            try
            {
                var refreshed = f.Data.Value.Deserialize<BridgeHello>(Json.Options);
                if (refreshed is not null && Versions.BridgeCompatible(refreshed.BridgeProtocolVersion))
                    _hello = refreshed;
            }
            catch (JsonException) { /* keep whatever the handshake established */ }
            return;
        }

        if (f.Id is not null && _pending.TryRemove(f.Id, out var tcs)) tcs.TrySetResult(f);
    }

    void HandleEvent(BridgeFrame f)
    {
        if (!f.Data.HasValue) return;
        var d = f.Data.Value;
        try
        {
            switch (f.Event)
            {
                case BridgeEvents.Quote: if (d.Deserialize<QuoteInfo>(Json.Options) is { } q) QuoteChanged?.Invoke(q); break;
                case BridgeEvents.Order: if (d.Deserialize<OrderInfo>(Json.Options) is { } o) OrderChanged?.Invoke(o); break;
                case BridgeEvents.Execution: if (d.Deserialize<ExecutionInfo>(Json.Options) is { } x) ExecutionReceived?.Invoke(x); break;
                case BridgeEvents.Position: if (d.Deserialize<PositionInfo>(Json.Options) is { } p) PositionChanged?.Invoke(p); break;
                case BridgeEvents.Account: if (d.Deserialize<AccountInfo>(Json.Options) is { } a) AccountChanged?.Invoke(a); break;
                case BridgeEvents.Connection:
                    var ok = d.TryGetProperty("connected", out var c) && c.GetBoolean();
                    ConnectionChanged?.Invoke(ok ? HealthState.READY : HealthState.DEGRADED);
                    break;
            }
        }
        catch (JsonException) { /* a malformed event must not take the connector down */ }
    }

    async Task<BridgeFrame> Rpc(string op, object? args, CancellationToken ct)
    {
        if (!_connected || _writer is null)
            throw new ConnectorTransportException("the ATAS bridge is not connected");

        var id = Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<BridgeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await _sendGate.WaitAsync(ct);
        try { // One payload field ("data") in both directions; a request-only "args" field silently
            // dropped every argument when the bridge read the frame back as a BridgeFrame.
            await _writer.WriteLineAsync(Json.Write(new { v = Versions.BridgeProtocolVersion, id, op, data = args })); }
        catch (Exception ex) { _pending.TryRemove(id, out _); throw new ConnectorTransportException("could not reach the ATAS bridge", ex); }
        finally { _sendGate.Release(); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            var frame = await tcs.Task.WaitAsync(timeout.Token);
            if (frame.Ok == false)
                throw frame.Rejected
                    ? new ConnectorRejectedException(frame.Error ?? "rejected by ATAS")
                    : new ConnectorTransportException(frame.Error ?? "the ATAS bridge reported a failure");
            return frame;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            // Timed out: we do not know whether ATAS acted. Indefinite by construction.
            throw new ConnectorTransportException($"ATAS did not answer '{op}' within {_timeout.TotalSeconds:0}s");
        }
    }

    async Task<T> Rpc<T>(string op, object? args, CancellationToken ct)
    {
        var f = await Rpc(op, args, ct);
        if (!f.Data.HasValue) throw new ConnectorTransportException($"ATAS returned no data for '{op}'");
        return f.Data.Value.Deserialize<T>(Json.Options)
               ?? throw new ConnectorTransportException($"ATAS returned unreadable data for '{op}'");
    }

    public Task<HealthState> GetHealthAsync(CancellationToken ct = default)
    {
        if (!_connected) return Task.FromResult(HealthState.FAILED);
        var stale = DateTimeOffset.UtcNow - _lastHeartbeat > HeartbeatTimeout;
        return Task.FromResult(stale ? HealthState.DEGRADED : HealthState.READY);
    }

    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(_connected);

    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) =>
        Rpc<IReadOnlyList<AccountInfo>>(BridgeOps.Accounts, null, ct);

    public async Task<AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default) =>
        (await GetAccountsAsync(ct)).FirstOrDefault(a => a.Id == accountId);

    public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) =>
        Rpc<IReadOnlyList<InstrumentInfo>>(BridgeOps.Instruments, null, ct);

    public async Task<QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default) =>
        await Rpc<QuoteInfo?>(BridgeOps.Quote, new { symbol }, ct);

    public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default) =>
        Rpc<IReadOnlyList<PositionInfo>>(BridgeOps.Positions, new { account_id = accountId }, ct);

    public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive, DateTimeOffset? since, CancellationToken ct = default) =>
        Rpc<IReadOnlyList<OrderInfo>>(BridgeOps.Orders, new { account_id = accountId, include_inactive = includeInactive, since }, ct);

    public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since, CancellationToken ct = default) =>
        Rpc<IReadOnlyList<ExecutionInfo>>(BridgeOps.Executions, new { account_id = accountId, since }, ct);

    public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default) =>
        Rpc<OrderInfo>(BridgeOps.Place, cmd, ct);

    public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default) =>
        Rpc<OrderInfo>(BridgeOps.Modify, cmd, ct);

    public Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default) =>
        Rpc(BridgeOps.Cancel, new { connector_order_id = connectorOrderId }, ct);

    public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default) =>
        Rpc<IReadOnlyList<string>>(BridgeOps.CancelAll, new { account_id = accountId }, ct);

    public Task<OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId, CancellationToken ct = default) =>
        Rpc<OrderInfo?>(BridgeOps.Close, new { account_id = accountId, symbol, client_order_id = clientOrderId }, ct);

    int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Idempotent: the gateway disposes the connector it was handed, and a caller that also owns
        // it may dispose it too. Throwing on the second call turns tidy shutdown into a crash.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync();
        Drop("shutting down");
        _pipeStream?.Dispose();
        _cts.Dispose();
        _sendGate.Dispose();
    }
}
