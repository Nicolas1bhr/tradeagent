using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;

namespace TradeAgent.AtasBridge;

/// <summary>
/// The bridge's own half of the protocol, running inside ATAS.
///
/// It dials out to TradeAgent rather than listening, so its presence is something TradeAgent can
/// observe (a connection plus a heartbeat) instead of something the user has to confirm. It
/// reconnects for as long as it is loaded, because ATAS restarting is normal, not exceptional.
/// </summary>
public sealed class BridgeServer(IAtasAdapter adapter, string? pipeName = null) : IAsyncDisposable
{
    readonly string _pipe = pipeName ?? Paths.BridgePipeName;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _send = new(1, 1);
    NamedPipeClientStream? _client;
    StreamWriter? _writer;
    Task? _loop;

    public bool Connected { get; private set; }
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public async Task RunAsync(CancellationToken ct)
    {
        Subscribe();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _client.ConnectAsync(ct);
                _writer = new StreamWriter(_client, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
                Connected = true;

                await SendRaw(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello, data = adapter.Describe() }, ct);
                using var heartbeat = StartHeartbeat(ct);

                var reader = new StreamReader(_client, new UTF8Encoding(false), false, 8192, leaveOpen: true);
                string? line;
                while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
                    await HandleFrame(line, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* TradeAgent closed or is not running yet; keep trying */ }
            finally
            {
                Connected = false;
                _writer = null;
                _client?.Dispose();
                _client = null;
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(ReconnectDelay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    CancellationTokenSource StartHeartbeat(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && Connected)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval, token);
                    await SendRaw(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Heartbeat }, token);
                }
                catch (Exception) { return; }
            }
        }, token);
        return cts;
    }

    async Task HandleFrame(string line, CancellationToken ct)
    {
        BridgeFrame? f;
        try { f = Json.Read<BridgeFrame>(line); }
        catch (JsonException) { return; }
        if (f?.Op is null || f.Id is null) return;

        try
        {
            var data = Invoke(f);
            await SendRaw(new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = true, data }, ct);
        }
        catch (AtasRejectedException ex)
        {
            // Definite refusal. The 'rejected' flag is what stops the gateway from reconciling
            // something the broker already declined.
            await SendRaw(new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = false, rejected = true, error = ex.Message }, ct);
        }
        catch (Exception ex)
        {
            // Anything else is indefinite as far as TradeAgent is concerned.
            await SendRaw(new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = false, rejected = false, error = ex.Message }, ct);
        }
    }

    object? Invoke(BridgeFrame f)
    {
        var d = f.Data;
        string Str(string key) => d.HasValue && d.Value.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        bool Bool(string key) => d.HasValue && d.Value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        DateTimeOffset? Since() =>
            d.HasValue && d.Value.TryGetProperty("since", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetDateTimeOffset() : null;

        return f.Op switch
        {
            BridgeOps.Accounts => adapter.GetAccounts(),
            BridgeOps.Instruments => adapter.GetInstruments(),
            BridgeOps.Quote => adapter.GetQuote(Str("symbol")),
            BridgeOps.Positions => adapter.GetPositions(Str("account_id")),
            BridgeOps.Orders => adapter.GetOrders(Str("account_id"), Bool("include_inactive"), Since()),
            BridgeOps.Executions => adapter.GetExecutions(Str("account_id"), Since()),
            BridgeOps.Place => adapter.Place(Deserialize<PlaceOrderCommand>(d)),
            BridgeOps.Modify => adapter.Modify(Deserialize<ModifyOrderCommand>(d)),
            BridgeOps.Cancel => Nothing(() => adapter.Cancel(Str("connector_order_id"))),
            BridgeOps.CancelAll => adapter.CancelAll(Str("account_id")),
            BridgeOps.Close => adapter.ClosePosition(Str("account_id"), Str("symbol"), Str("client_order_id")),
            _ => throw new InvalidOperationException($"unknown bridge operation '{f.Op}'")
        };
    }

    static object? Nothing(Action a) { a(); return null; }

    static T Deserialize<T>(JsonElement? d) =>
        d.HasValue
            ? d.Value.Deserialize<T>(Json.Options) ?? throw new InvalidOperationException("unreadable payload")
            : throw new InvalidOperationException("missing payload");

    void Subscribe()
    {
        adapter.ConnectionChanged += c => Push(BridgeEvents.Connection, new { connected = c });
        adapter.QuoteChanged += q => Push(BridgeEvents.Quote, q);
        adapter.OrderChanged += o => Push(BridgeEvents.Order, o);
        adapter.ExecutionReceived += e => Push(BridgeEvents.Execution, e);
        adapter.PositionChanged += p => Push(BridgeEvents.Position, p);
        adapter.AccountChanged += a => Push(BridgeEvents.Account, a);
    }

    void Push(string name, object payload)
    {
        if (!Connected) return;
        _ = SendRaw(new { v = Versions.BridgeProtocolVersion, @event = name, data = payload }, _cts.Token);
    }

    async Task SendRaw(object frame, CancellationToken ct)
    {
        var w = _writer;
        if (w is null) return;
        await _send.WaitAsync(ct);
        try { await w.WriteLineAsync(Json.Write(frame)); }
        catch (Exception) { Connected = false; }
        finally { _send.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) { try { await _loop; } catch (Exception) { } }
        _client?.Dispose();
        _cts.Dispose();
        _send.Dispose();
    }
}
