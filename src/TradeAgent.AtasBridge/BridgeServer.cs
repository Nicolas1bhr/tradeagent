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
///
/// DIALLING OUT IS ALSO THE WEAKNESS, AND IT IS WHY THIS CLASS AUTHENTICATES ITS PEER. There is one
/// server instance of that pipe, so whichever process creates the name first owns it, and this
/// class connects to whatever is listening. Everything past <see cref="HandleFrame"/> reaches
/// <see cref="IAtasAdapter"/> — <c>place</c> included — with TradingGateway, and therefore the mode,
/// the kill switch, the approvals, the risk limits and the autonomy gate, entirely out of the path.
/// So nothing is served until the peer has proved who it is. What that proof does and does not buy
/// is written out in full on <see cref="BridgePipeAuth"/>; read it before trusting it.
/// </summary>
public sealed class BridgeServer(IAtasAdapter adapter, string? pipeName = null, BridgeCredential? credential = null)
    : IAsyncDisposable
{
    readonly string _pipe = pipeName ?? Paths.BridgePipeName;
    readonly BridgeCredential? _fixedCredential = credential;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _send = new(1, 1);
    NamedPipeClientStream? _client;
    StreamWriter? _writer;
    Task? _loop;
    volatile bool _authenticated;
    volatile bool _disposed;
    int _authFailures;

    public bool Connected { get; private set; }
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the peer gets to answer the authentication challenge. A deadline rather than a wait:
    /// "connected, then nothing" is the shape of three separate traps that have each cost a session,
    /// and an authentication failure must never be a fourth.
    /// </summary>
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long ONE frame gets to reach the peer before the connection is declared dead.
    ///
    /// The read side had a deadline from the beginning and the write side had none, which left the
    /// whole class open at the other end: a peer that accepts the connection and then simply stops
    /// reading parks this bridge inside a write that can never complete. Windows named pipes make
    /// that trivial — a server pipe created with no buffer blocks every write until somebody reads
    /// it — and a squatter chooses its own buffer size.
    ///
    /// Measured, not reasoned: on Windows the refusal frame and the squatter's next frame pended
    /// against each other forever, and the async chain from the dump was
    /// DisposeAsync -> RunAsync -> Authenticate -> Refuse -> SendRaw -> WriteAsyncInternal. So the
    /// bridge could be frozen by the very peer it was in the middle of refusing, and unloading the
    /// strategy from ATAS would have hung with it.
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Why the last refusal happened, or null if the peer has been accepted. Diagnostic.</summary>
    public BridgeAuthFailure? LastAuthFailure { get; private set; }

    /// <summary>How many peers have been refused since this bridge was loaded.</summary>
    public int AuthFailures => Volatile.Read(ref _authFailures);

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

                // ONE READER for the whole connection. Authenticate() reads the peer's answer off
                // it, and a second reader here would silently drop whatever the first one had
                // already buffered — which, with the peer answering immediately, is the hello's
                // reply and every frame after it.
                var reader = new StreamReader(_client, new UTF8Encoding(false), false, 8192, leaveOpen: true);

                // Before the hello, not after. An unproved peer learns nothing about this bridge:
                // not the ATAS version, not the account, not what it can prove about client order
                // ids. Connected stays false until this returns true, so Push() cannot leak an
                // event to it either.
                if (await Authenticate(reader, ct))
                {
                    _authenticated = true;
                    Connected = true;

                    await SendRaw(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello, data = adapter.Describe() }, ct);
                    using var heartbeat = StartHeartbeat(ct);

                    string? line;
                    while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
                        await HandleFrame(line, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* TradeAgent closed or is not running yet; keep trying */ }
            finally
            {
                Connected = false;
                _authenticated = false;
                _writer = null;
                _client?.Dispose();
                _client = null;
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(ReconnectDelay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Decides whether the process holding the bridge pipe gets to drive this adapter.
    ///
    /// Two questions, in this order, and a no to either ends the connection with a named reason
    /// rather than a silence:
    ///   1. WHO IS IT? Asked of Windows, not of the peer — the peer cannot answer this one falsely.
    ///   2. DOES IT HOLD THE SECRET? Over a nonce chosen here, so a recording of an earlier
    ///      exchange cannot be replayed back at this bridge.
    /// </summary>
    async Task<bool> Authenticate(StreamReader reader, CancellationToken ct)
    {
        // A credential that is absent and one that is unusable are the same news and must produce
        // the same sentence. Checking the shape here is not fussiness: Proof() throws on a malformed
        // secret, and an exception thrown out of this method lands in RunAsync's catch-all, where it
        // becomes a silent reconnect — an authentication failure that reports nothing at all, which
        // is the exact outcome this whole design exists to prevent.
        var cred = _fixedCredential ?? BridgePipeAuth.ReadForClient();
        if (cred is null || !BridgePipeAuth.IsSecret(cred.Secret))
            return await Refuse($"TradeAgent has published no usable bridge secret on this machine " +
                                $"({BridgePipeAuth.CredentialFile} is missing, unreadable or malformed). " +
                                "Start TradeAgent once", ct);

        // Windows only, and a failure to identify the peer is a refusal, not a shrug: "could not
        // check" is exactly the state an impersonator would engineer. Off Windows there is no ATAS
        // and no product, so the rule is skipped rather than faked.
        if (OperatingSystem.IsWindows() &&
            BridgePipeAuth.ImageVerdict(BridgePipeAuth.ServerImagePath(_client!), cred.ServerImage, Paths.Tools) is { } wrongPeer)
            return await Refuse(wrongPeer, ct);

        var nonce = BridgePipeAuth.NewNonce();
        await SendRaw(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Challenge,
            data = new { nonce, proof = BridgePipeAuth.Proof(cred.Secret, BridgePipeAuth.BridgeRole, nonce) }
        }, ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(AuthTimeout);
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(deadline.Token)) is not null)
            {
                BridgeFrame? f;
                try { f = Json.Read<BridgeFrame>(line); } catch (JsonException) { continue; }
                if (f is null) continue;

                if (f.Op == BridgePipeAuth.Refused)
                    return await Refuse($"the process holding the bridge pipe refused this bridge: {Clip(f.Error)}", ct);

                // Nothing else is even looked at before the answer arrives. An op sent ahead of it
                // is not served, not queued and not acknowledged.
                if (f.Op != BridgePipeAuth.Response) continue;

                var proof = f.Data.HasValue && f.Data.Value.TryGetProperty("proof", out var p) ? p.GetString() : null;
                if (!BridgePipeAuth.ProofMatches(cred.Secret, BridgePipeAuth.ServerRole, nonce, proof))
                    return await Refuse("the process holding the bridge pipe answered the challenge with the " +
                                        "wrong proof — it does not hold this installation's bridge secret", ct);

                LastAuthFailure = null;
                return true;
            }
            return await Refuse("the process holding the bridge pipe closed the connection without " +
                                "answering the authentication challenge", ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await Refuse($"the process holding the bridge pipe did not answer the authentication " +
                                $"challenge within {AuthTimeout.TotalSeconds:0}s", ct);
        }
    }

    async Task<bool> Refuse(string reason, CancellationToken ct)
    {
        LastAuthFailure = new BridgeAuthFailure(reason, DateTimeOffset.UtcNow);
        Interlocked.Increment(ref _authFailures);
        // Said out loud on the wire as well. A peer entitled to this pipe — a TradeAgent whose
        // bridge.auth has gone stale, most likely — needs this sentence on screen; a peer that is
        // not entitled learns only that it was refused, which it was about to find out anyway.
        try { await SendRaw(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Refused, error = reason }, ct); }
        catch (Exception) { /* refusing a peer that has already gone is still a refusal */ }
        return false;
    }

    /// <summary>
    /// Untrusted text from a peer we are in the middle of declining, on its way into a diagnostic.
    /// Same treatment as <see cref="IncompatibleBridge.Clean"/> and for the same reason, with more
    /// room because a refusal has to explain itself and a version string does not.
    /// </summary>
    static string Clip(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "no reason given";
        var kept = new string(raw.Where(c => !char.IsControl(c)).Take(200).ToArray()).Trim();
        return kept.Length == 0 ? "no reason given" : kept;
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
        // Belt and braces. RunAsync does not reach this loop until Authenticate() said yes, so this
        // can only fire if someone rearranges that — which is exactly when it needs to be here,
        // because the next line but eight hands 'place' to a live broker.
        if (!_authenticated) return;

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
            // MEASUREMENT ONLY, and it places a real order — see BridgeOps.PlaceViaAsyncOverload.
            // Deliberately a separate case rather than a flag read out of `d`: the payload is a
            // PlaceOrderCommand deserialised from the wire, and a submission path selected by a
            // field inside it would be one JSON property away from the ordinary place path. This
            // way the route is chosen by the op name, which the product never sends.
            BridgeOps.PlaceViaAsyncOverload => adapter.PlaceViaAsyncOverload(Deserialize<PlaceOrderCommand>(d)),
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

    /// <summary>
    /// An adapter event on its way to the peer, fire and forget.
    ///
    /// The disposed check is not defensive noise: nothing unsubscribes from the adapter, so ATAS can
    /// raise an event into this object after <see cref="DisposeAsync"/> has run, and reading
    /// <c>_cts.Token</c> then throws straight back into ATAS's own event raise.
    /// </summary>
    void Push(string name, object payload)
    {
        if (!Connected || _disposed) return;
        try { _ = SendRaw(new { v = Versions.BridgeProtocolVersion, @event = name, data = payload }, _cts.Token); }
        catch (ObjectDisposedException) { /* disposed between the check and the read */ }
    }

    /// <summary>
    /// One frame to the peer, with a deadline on it.
    ///
    /// Cancellation cannot reach a write that Windows has already accepted — only closing the handle
    /// can — so a frame that has not landed within <see cref="WriteTimeout"/> ends the connection by
    /// disposing the pipe, which fails the pending write and returns the read loop to
    /// <see cref="RunAsync"/> to reconnect. The abandoned task is observed rather than dropped: an
    /// unhandled fault inside ATAS is not ours to leave lying around.
    /// </summary>
    async Task SendRaw(object frame, CancellationToken ct)
    {
        var w = _writer;
        if (w is null) return;

        // The queue for the writer is bounded too. Without it, one stuck frame makes every later
        // caller wait behind it for as long as the peer feels like — the heartbeat included, which
        // is the signal TradeAgent uses to decide this bridge is alive.
        if (!await _send.WaitAsync(WriteTimeout, ct)) { DropConnection(); return; }
        try
        {
            var write = w.WriteLineAsync(Json.Write(frame));
            try
            {
                await write.WaitAsync(WriteTimeout, ct);
            }
            catch (TimeoutException)
            {
                Observe(write);
                DropConnection();
            }
        }
        catch (Exception) { Connected = false; }
        finally { _send.Release(); }
    }

    /// <summary>Ends the connection now. The pending overlapped write dies with the handle.</summary>
    void DropConnection()
    {
        Connected = false;
        _authenticated = false;
        try { _client?.Dispose(); } catch (Exception) { /* already gone */ }
    }

    static void Observe(Task t) => _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);

    /// <summary>
    /// Stops the bridge. THE PIPE IS CLOSED BEFORE THE LOOP IS WAITED ON, and the wait is bounded.
    ///
    /// The other order — cancel, await the loop, then close — is what was here, and it hangs
    /// forever against a peer that has stopped reading: the loop is parked in a write, and a token
    /// cannot cancel a write the kernel has already taken. This method runs when ATAS unloads the
    /// strategy, so "forever" would have been ATAS's problem as much as ours.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;      // idempotent: disposing twice is not an error, it is a no-op
        _disposed = true;
        await _cts.CancelAsync();
        try { _client?.Dispose(); } catch (Exception) { /* already gone */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* cancelled, faulted, or would not let go: either way we are done */ }
        }
        _cts.Dispose();
        _send.Dispose();
    }
}
