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
                    // The heartbeat carries the current Describe(), not just a pulse.
                    //
                    // Two of those fields are answered at runtime and only become true *after* the
                    // handshake: SupportsClientOrderId turns true once a placed order has been seen
                    // coming back out of ATAS carrying our client id, and AccountId/IsSimulated stay
                    // unknown until ATAS has a portfolio. Sent once at Hello and never again, that
                    // proof arrived after the only moment anyone ever read it — so the gateway went
                    // on refusing autonomous live trading for the whole life of the connection, and
                    // the staged trial (practice, then ask-me-first, then automatic) had no way to
                    // reach its last step short of restarting ATAS.
                    //
                    // Re-sending it every beat is also self-correcting in a way a change-triggered
                    // frame is not: a lost update is repaired by the next beat instead of leaving
                    // the two ends permanently disagreeing, which is the same class of bug as the
                    // one being fixed here. Describe() is a bool, a cached assembly identity and a
                    // type test, so the cost of asking again is not worth a change-detection latch.
                    //
                    // Describe() reaches into ATAS's own Portfolio and Connector properties, and
                    // this loop's catch is `return` — so letting it throw here would end the
                    // heartbeat, and TradeAgent would declare the bridge dead fifteen seconds later.
                    // A capability read must never be able to cost the liveness signal, so a failed
                    // read degrades to the pulse this frame used to be: capabilities simply do not
                    // refresh this beat, which is the old behaviour and fails closed.
                    object? caps = null;
                    try { caps = adapter.Describe(); } catch (Exception) { /* pulse without it */ }
                    await SendRaw(caps is null
                        ? new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Heartbeat }
                        : (object)new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Heartbeat, data = caps }, token);
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
        catch (Exception ex) when (Refusal(ex) is not null)
        {
            // Definite refusal. The 'rejected' flag is what stops the gateway from reconciling
            // something the broker already declined. The message is taken off the REFUSAL, not off
            // the wrapper: an AggregateException's own message is "One or more errors occurred.",
            // which tells an operator nothing about why the broker said no.
            var refusal = Refusal(ex)!;
            await SendRaw(new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = false, rejected = true, error = refusal.Message }, ct);
        }
        catch (Exception ex)
        {
            // Anything else is indefinite as far as TradeAgent is concerned.
            await SendRaw(new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = false, rejected = false, error = ex.Message }, ct);
        }
    }

    /// <summary>
    /// The definite refusal inside <paramref name="ex"/>, or null if there is not exactly one.
    ///
    /// A plain <c>catch (AtasRejectedException)</c> was right about the shape the adapter throws
    /// today and wrong about the shape a task-based call path produces. Anything that waits with
    /// <c>.Wait()</c> or <c>.Result</c> — ours or a future caller's — delivers the refusal wrapped in
    /// an <see cref="AggregateException"/>, and the bare catch would miss it: the broker's definite
    /// "no" would cross the wire as <c>rejected=false</c>, the gateway would record UNKNOWN and go
    /// reconciling an order that was never accepted. <see cref="AtasCall"/> unwraps this at source,
    /// which is the right place; this is the wire refusing to depend on that being true everywhere.
    ///
    /// SINGLE-FAULT ONLY, AND THAT IS THE LINE. A task carrying several failures is ambiguous by
    /// definition — one of them being a refusal does not make the whole outcome a refusal, and the
    /// others may be exactly the timeout or disconnect that means an order is still live. Rule 3
    /// reserves 'rejected' for a definite broker refusal and nothing else, so a multi-fault
    /// AggregateException falls through to the indefinite path and gets reconciled. Recursing keeps
    /// that true through nesting: every layer has to be single-fault or the answer is null.
    /// </summary>
    public static AtasRejectedException? Refusal(Exception ex) => ex switch
    {
        AtasRejectedException r => r,
        AggregateException { InnerExceptions.Count: 1 } a => Refusal(a.InnerExceptions[0]),
        _ => null
    };

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
