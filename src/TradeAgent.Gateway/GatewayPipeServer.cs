using System.IO.Pipes;
using System.Text;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// The only door into the gateway from the agent's side of the fence.
///
/// Deliberately narrow: read operations and order operations, nothing else. Operator authority —
/// mode changes, the kill switch, live activation, approvals — is NOT reachable here, so an agent
/// that decides it would like more permission has nowhere to ask.
/// </summary>
public sealed class GatewayPipeServer(TradingGateway gateway, string token, string? pipeName = null) : IAsyncDisposable
{
    const int MaxFrameBytes = 1 << 20;

    /// <summary>
    /// The pipe's buffer, and it was 0 until this was measured.
    ///
    /// MEASURED, not arithmetic — but measured on the OTHER pipe. This is the same 8 KiB
    /// <see cref="Connectors.Atas.AtasConnector"/> was given in bbcd36e after the bridge froze on
    /// Windows on 2026-09-01, and it is here for the same reason: a Windows named pipe created with
    /// no buffer completes a write only when the far end reads it, however small the frame, so every
    /// reply this server sends was coupled to the agent reading promptly with no slack at all. It is
    /// not only a hostile agent that stops reading — a CLI process that is suspended, swapped out or
    /// stuck behind its own stdout does exactly the same thing.
    ///
    /// The number is a hint to the kernel, not a contract, and it changes nothing about the
    /// protocol: a frame that fits simply no longer waits for a reader. It is deliberately NOT sized
    /// to <see cref="MaxFrameBytes"/> — a reply near the frame cap should still be governed by
    /// <see cref="WriteTimeout"/> rather than by however much the kernel felt like absorbing.
    /// </summary>
    const int PipeBuffer = 8192;

    /// <summary>
    /// How much of a reply ONE deadline covers.
    ///
    /// ARITHMETIC, not measured, and the arithmetic is the whole point. <see cref="WriteTimeout"/>
    /// used to bound the WHOLE write, which is not a stalled-peer detector at all — it is a
    /// THROUGHPUT FLOOR of (reply size / timeout). A ~1 MiB reply against the shipped 10 s deadline
    /// demanded ~96 KiB/s of the agent forever, so a peer reading steadily at 79 KiB/s was dropped
    /// at 10.1 s and libelled in the log as having stopped reading. Measured by review of a0aa1a7.
    ///
    /// Chunking makes the deadline mean what it says: bytes accepted resets it, so the floor is this
    /// chunk per timeout — 8 KiB / 10 s ≈ 819 B/s at the shipped default — and a peer that is moving
    /// at all survives a reply of any size, while one that has genuinely stopped still fails the
    /// very first chunk that does not fit the buffer.
    /// </summary>
    const int WriteChunkBytes = 8192;

    /// <summary>
    /// How long ONE reply gets to reach the agent before that connection is declared dead.
    ///
    /// Same name, same default and same reasoning as <see cref="AtasBridge.BridgeServer.WriteTimeout"/>,
    /// because it is the same defect on the other pipe: cancellation cannot recall a write the kernel
    /// has already accepted, only closing the handle can. A reply that has not landed within this
    /// ends that ONE connection — the peer that stopped reading pays, nobody else does.
    ///
    /// Measured here on macOS on 2026-09-02, before the deadline existed: an authenticated peer that
    /// read one byte of a 960 KB material-list and then stopped left the handler parked in the write
    /// with 960,527 bytes still owed and the connection still open, and shutdown walked away from it
    /// rather than closing it.
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    readonly string _pipe = pipeName ?? Paths.PipeName;

    /// <summary>
    /// TWO TOKENS, AND THE SPLIT IS LOAD BEARING.
    ///
    /// <c>_accept</c> stops new connections being taken. <c>_cts</c> is what the HANDLERS hold, and
    /// it is cancelled only after they have been given their chance to finish.
    ///
    /// One token for both is what was here, and it made draining handlers impossible in principle:
    /// the handler's token reaches <c>TradingGateway.PlaceAsync</c> and, through it, the connector's
    /// own wait on the broker. Cancelling it first ABORTS AN ORDER THAT MAY ALREADY BE AT THE
    /// BROKER — the exact way an order ends up recorded DISPATCHING for ever, which is the fault
    /// this drain exists to prevent. Measured: with one token the in-flight place unwound in 15 ms
    /// and disposal "succeeded" without waiting for anything.
    /// </summary>
    readonly CancellationTokenSource _accept = new();
    readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Every connection currently being served. The handlers are fire-and-forget tasks, so without
    /// this <see cref="DisposeAsync"/> has no way to reach one: it awaited the ACCEPT loop, which is
    /// not where a stalled writer is parked, and the connection outlived the server that owned it.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<NamedPipeServerStream, byte> _live = new();

    /// <summary>
    /// Every handler task currently running. Registering the PIPES was not enough: closing a pipe
    /// ends the handler's I/O, but a handler parked INSIDE the gateway — in the middle of a place,
    /// waiting on the broker — is not doing I/O at all, and disposal walked straight past it. It
    /// then outlived the server, the gateway AND the database, so the settle that would have moved
    /// its order out of DISPATCHING ran against a closed connection, or never ran. An order that
    /// reached the broker was left DISPATCHING for ever.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _handlers = new();

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for in-flight handlers once their pipes are shut.
    ///
    /// ARITHMETIC, not measured, and DERIVED FROM THE CONNECTOR'S WORST CASE rather than picked.
    /// Five seconds was picked, and it was shorter than the path it had to outlast, so a shutdown
    /// during an order still abandoned it: measured at the shipped values,
    /// <c>DisposeAsync returned after 5.01s … unfinished:1 … state=DISPATCHING</c>.
    ///
    /// The worst case for ONE order through <c>AtasConnector.Rpc</c>, at shipped values:
    ///
    ///     send gate wait      up to WriteTimeout      10 s
    ///   + the write itself    up to WriteTimeout      10 s
    ///   + waiting for ATAS    up to rpcTimeout        10 s
    ///   = 30 s, + 5 s for the settle and its write-back
    ///   = 35 s
    ///
    /// <c>AtasConnector.WorstCaseOrderPath</c> computes those first three from the live values and a
    /// test asserts this default still covers it, so changing a connector deadline breaks a test
    /// rather than silently reintroducing the abandoned order.
    ///
    /// THE TRADE IS DELIBERATE: the app may take up to 35 s to close, but ONLY while an order is
    /// actually in flight — an idle handler is freed the moment its pipe is closed, which happens
    /// before this wait. Waiting is the right side of that trade, because the alternative is an
    /// order that reached the broker and is recorded DISPATCHING for ever.
    /// </summary>
    public TimeSpan HandlerDrainTimeout { get; init; } = TimeSpan.FromSeconds(35);

    Task? _loop;
    volatile bool _disposed;

    public string PipeName => _pipe;

    public void Start() => _loop ??= Task.Run(() => AcceptLoop(_accept.Token));

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(ct);
                var s = server;
                server = null; // ownership moves to the handler
                _live[s] = 0;
                // The HANDLER token, not the accept token: a connection already taken is served to
                // the end even though the door has closed.
                var handler = Task.Run(() => Serve(s, _cts.Token), _cts.Token);
                _handlers[handler] = 0;
                _ = handler.ContinueWith(t => _handlers.TryRemove(t, out _), TaskScheduler.Default);
            }
            catch (OperationCanceledException) { server?.Dispose(); return; }
            catch (Exception ex)
            {
                server?.Dispose();
                gateway.Log.Engineering("Ipc", "accept_failed", "warn", ex: ex);
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    NamedPipeServerStream CreateServer()
    {
        // On Windows, lock the pipe to this user account as well as requiring the token, so another
        // account on the same machine cannot even open the handle.
        if (OperatingSystem.IsWindows())
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(id.User!, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(id.User!, PipeAccessRights.CreateNewInstance, System.Security.AccessControl.AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBuffer, PipeBuffer, security);
        }
        return new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBuffer, PipeBuffer);
    }

    async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var authenticated = false;
        try
        {
            await using var _ = pipe;
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await ReadFrame(reader, ct);
                if (line is null) break;
                if (line.Length == 0) continue;

                IpcRequest? req;
                try { req = Json.Read<IpcRequest>(line); }
                catch (Exception)
                {
                    if (!await Send(pipe, IpcResponse.Fail("", ErrorCode.INVALID_REQUEST, "frame is not valid JSON"), "", null, null)) return;
                    continue;
                }
                if (req is null)
                {
                    if (!await Send(pipe, IpcResponse.Fail("", ErrorCode.INVALID_REQUEST, "empty frame"), "", null, null)) return;
                    continue;
                }

                if (req.Op == Core.Ops.Hello)
                {
                    if (!Security.IpcToken.Matches(req.Token, token))
                    {
                        gateway.Log.Engineering("Ipc", "auth_rejected", "warn");
                        await Send(pipe, IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, "token rejected"), req.Op, req.Session, req.RequestId);
                        return; // one chance per connection
                    }
                    // Refused BEFORE the flag flips: a connection that asked for the operator's
                    // name never becomes usable under it, rather than being refused per-op after.
                    if (ReservedSessionRefusal(req) is { } helloRefusal)
                    {
                        if (!await Send(pipe, helloRefusal, req.Op, req.Session, req.RequestId)) return;
                        continue;
                    }

                    authenticated = true;
                    if (!await Send(pipe, IpcResponse.Success(req.Id, new
                    {
                        protocol_version = Versions.ProtocolVersion,
                        app_version = Versions.App,
                        compatible = req.V == Versions.ProtocolVersion
                    }), req.Op, req.Session, req.RequestId)) return;
                    continue;
                }

                if (!authenticated)
                {
                    await Send(pipe, IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, "say hello with a valid token first"), req.Op, req.Session, req.RequestId);
                    return;
                }

                if (ReservedSessionRefusal(req) is { } refusal)
                {
                    if (!await Send(pipe, refusal, req.Op, req.Session, req.RequestId ?? req.Id)) return;
                    continue;
                }

                if (!await Send(pipe, await Handle(req, ct), req.Op, req.Session, req.RequestId ?? req.Id)) return;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The agent went away. Normal.
        }
        catch (Exception ex)
        {
            gateway.Log.Engineering("Ipc", "connection_failed", "error", ex: ex);
        }
        finally
        {
            _live.TryRemove(pipe, out _);
        }
    }

    static async Task<string?> ReadFrame(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[1];
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(buf[0]);
            if (sb.Length > MaxFrameBytes) throw new IOException("frame exceeds the maximum size");
        }
    }

    /// <summary>
    /// One reply to one peer, with a deadline on it. False means this connection is finished.
    ///
    /// A peer that authenticates, asks for something large and then stops reading used to park the
    /// handler here forever: <c>WriteLineAsync</c> had no deadline and takes no cancellation token,
    /// and no token could have helped anyway — a write the kernel has already accepted cannot be
    /// recalled, only the handle can be closed. So the deadline closes the handle, which fails the
    /// pending write and ends that ONE connection. Other agents are on other handlers and other
    /// pipes and never notice; there is deliberately no lock here, because a lock shared across
    /// connections would turn one stalled peer into an outage for everybody.
    ///
    /// The abandoned write is observed rather than dropped, so its inevitable fault does not surface
    /// later as an unobserved task exception.
    /// </summary>
    /// <summary>
    /// One reply to one peer, written in chunks with a deadline on EACH — so the deadline measures
    /// PROGRESS, not elapsed time. False means this connection is finished.
    ///
    /// A peer that authenticates, asks for something large and then stops reading used to park the
    /// handler here forever: the write had no deadline and takes no cancellation token, and no token
    /// could have helped anyway — a write the kernel has already accepted cannot be recalled, only
    /// the handle can be closed. So the deadline closes the handle, ending that ONE connection.
    /// Other agents are on other handlers and other pipes and never notice; there is deliberately no
    /// lock here, because a lock shared across connections would turn one stalled peer into an
    /// outage for everybody.
    ///
    /// Writing the bytes straight to the pipe rather than through a StreamWriter is what makes the
    /// chunking real: a StreamWriter would hand the runtime the whole frame and give back one task
    /// to wait on, which is exactly the total-duration bound this replaced.
    /// </summary>
    async Task<bool> Send(NamedPipeServerStream pipe, IpcResponse r,
        string op, string? session, string? requestId)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Write(r) + "\n");
        var sent = 0;
        while (sent < bytes.Length)
        {
            var n = Math.Min(WriteChunkBytes, bytes.Length - sent);
            var write = pipe.WriteAsync(bytes.AsMemory(sent, n)).AsTask();
            try
            {
                await write.WaitAsync(WriteTimeout);
            }
            catch (TimeoutException)
            {
                Observe(write);
                // THE REQUEST ID IS THE POINT OF THIS RECORD, not decoration on it. The reply that
                // was dropped may be the only acknowledgement of an order that already reached the
                // broker, and this log line is then the sole surviving link between that order and
                // the id the agent must reuse to reconcile it. It used to be written request_id NULL.
                //
                // The byte counts say WHERE it stopped, which is the difference between "this peer
                // is gone" and "this peer is slow" — the distinction the old total-duration bound
                // could not make and got wrong.
                gateway.Log.Engineering("Ipc", "peer_stopped_reading", "warn", session: session,
                    requestId: requestId,
                    metadataJson: Json.Write(new
                    {
                        op,
                        request_id = requestId,
                        bytes_sent = sent,
                        bytes_total = bytes.Length,
                        write_timeout_ms = (int)WriteTimeout.TotalMilliseconds
                    }));
                try { pipe.Dispose(); } catch (Exception) { /* already gone */ }
                return false;
            }
            sent += n;
        }
        return true;
    }

    static void Observe(Task t) => _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);

    /// <summary>
    /// The reserved-session tripwire, for EVERY frame kind — <c>null</c> when the frame may proceed.
    ///
    /// The reserved session is refused rather than quietly downgraded. <c>AgentContext.ForAgent</c>
    /// cannot return an operator context whatever this string says, so nothing here is load bearing
    /// for safety — it is a tripwire. An agent asking for the operator's name is probing for an
    /// escalation, and a probe nobody can see afterwards is not evidence.
    ///
    /// It used to live inside <see cref="Handle"/>, which a HELLO frame never reaches: the read loop
    /// answers hello itself and continues. So the one frame kind an agent sends FIRST was the one
    /// kind the tripwire did not cover, and a valid-token hello carrying " operator " was answered
    /// with success (Codex F10 on d25dbb4). Being a method called from both places rather than a
    /// block inside one of them is the point — the next frame kind added to the loop has to walk
    /// past a named check to skip it.
    /// </summary>
    IpcResponse? ReservedSessionRefusal(IpcRequest req)
    {
        if (!string.Equals(req.Session?.Trim(), AgentContext.OperatorSessionId, StringComparison.OrdinalIgnoreCase))
            return null;

        gateway.Log.Engineering("Ipc", "operator_session_refused", "warn",
            session: req.Session, requestId: req.RequestId ?? req.Id,
            metadataJson: Json.Write(new { op = req.Op }));
        return IpcResponse.Fail(req.Id, ErrorCode.INVALID_REQUEST,
            $"'{AgentContext.OperatorSessionId}' is a reserved session name and is not available on this channel");
    }

    async Task<IpcResponse> Handle(IpcRequest req, CancellationToken ct)
    {
        // THE EFFECTIVE ID, COMPUTED BEFORE IT IS GUARDED — because the guard has to be on the value
        // that is USED, not on the field that may be absent.
        //
        // `request_id` is optional on the wire and `id` is not, so the id that actually reaches the
        // broker and keys the idempotency store is this fallback. Validating only `req.RequestId`
        // left every rule below bypassable by omitting one field, and both halves were measured on
        // d25dbb4 before this changed: a 200-character frame id containing '#', '/' and a space was
        // accepted and left this process as the 203-character ClientOrderId `TA-x#y/z w_qqq…`; and
        // `op-deadbeef-cancelall-0` in the frame id reached the broker AND became a live
        // idempotency key — the bdf9a24 collision (a sweep leg replaying an agent's PLACE record
        // and counting it as cancelled) restored one field over.
        var rid = req.RequestId ?? req.Id;

        // Two checks, and they are not the same check. The PREFIX keeps an agent's id from
        // colliding with one this gateway mints for a sweep leg. The CHARSET keeps whatever the
        // agent chose from reaching the broker as ClientOrderId ("TA-" + this) in a shape safety
        // rule 1 needs to round-trip and no one here can promise ATAS will accept.
        //
        // Applied to every op rather than only the mutating ones. `rid` is consumed only by the
        // mutating branches today, but the cost of guarding a read is one comparison and the cost
        // of scoping it is that the next op to start using `rid` inherits the hole silently.
        if (rid.StartsWith(MintedIdPrefix, StringComparison.OrdinalIgnoreCase))
            return IpcResponse.Fail(req.Id, ErrorCode.INVALID_REQUEST,
                $"a request id may not start with '{MintedIdPrefix}' — that prefix is how cancel-all and " +
                "close-all name the per-order requests they mint, and an id using it could collide with one");

        if (!IsConservativeId(rid))
            return IpcResponse.Fail(req.Id, ErrorCode.INVALID_REQUEST,
                $"a request id may use only letters, digits and '-', up to {MaxRequestIdChars} characters — it is " +
                $"carried onto the broker order as the client order id, which must fit {MaxClientOrderIdChars}, " +
                "and that has to be a shape the broker will give back");

        var ctx = AgentContext.ForAgent(req.Session);

        // EVERY READ THIS OPERATION HAS TO DO FIRST IS PART OF THE EMERGENCY, NOT A PRELUDE TO IT.
        //
        // The connector classifies urgency by the bridge op it is about to send, which is right for
        // the final frame and blind to everything that has to happen before it. A cancel-all reads
        // the working orders; cancelling by client id resolves the target by reading orders; a close
        // reads the position. Those are ordinary `orders` and `positions` RPCs, so at shipped
        // deadlines an emergency spent TEN SECONDS on a prerequisite read before the two-second
        // frame it was hurrying to send ever got a turn — and the test that claimed to measure the
        // agent's leg called the connector directly and skipped all of it (Codex F11).
        //
        // The intent is known here and needed several layers down, through interfaces this unit does
        // not own, so it travels on the execution context instead of through a signature. It only
        // ever WIDENS urgency, and Place/Modify are excluded at the far end, so the worst a stray
        // scope can do is make a read give up in two seconds and report UNKNOWN.
        using var riskReducing = IsRiskReducing(req.Op) ? RiskReducingScope.Begin() : null;

        try
        {
            object? data = req.Op switch
            {
                Core.Ops.Status      => await gateway.StatusAsync(ct),
                Core.Ops.Schema      => GatewaySchema.Describe(await gateway.StatusAsync(ct)),
                Core.Ops.Connectors  => new[] { new { id = gateway.Connector.Id, name = gateway.Connector.DisplayName, capabilities = gateway.Connector.Capabilities } },
                Core.Ops.Accounts    => await gateway.AccountsAsync(ct),
                Core.Ops.Account     => await gateway.AccountAsync(ct),
                Core.Ops.Instruments => await gateway.InstrumentsAsync(ct),
                Core.Ops.Quote       => await gateway.QuoteAsync(Require(req, "symbol"), ct),
                Core.Ops.Positions   => await gateway.PositionsAsync(ct),
                Core.Ops.Position    => (await gateway.PositionsAsync(ct)).FirstOrDefault(p => p.Symbol == Require(req, "symbol")),
                Core.Ops.Orders      => await gateway.OrdersAsync(req.Str("all") is "true", ct),
                Core.Ops.Order       => await FindOrder(Require(req, "id"), ct),
                Core.Ops.Executions  => await gateway.ExecutionsAsync(ct),
                Core.Ops.MaterialList => MaterialList(req),
                Core.Ops.MaterialNote => MaterialNote(ctx, req),

                Core.Ops.Buy or Core.Ops.Sell => await gateway.PlaceAsync(ctx, rid, ParsePlace(req), ct),
                Core.Ops.Modify   => await gateway.ModifyAsync(ctx, rid, Require(req, "id"), req.Dec("quantity"), req.Dec("limit"), req.Dec("stop"), ct),
                Core.Ops.Cancel   => await gateway.CancelAsync(ctx, rid, Require(req, "id"), ct),
                Core.Ops.CancelAll=> await CancelAll(ctx, rid, ct),
                Core.Ops.Close    => await gateway.CloseAsync(ctx, rid, Require(req, "symbol"), ct),
                Core.Ops.CloseAll => await CloseAll(ctx, rid, ct),

                _ => throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"unknown operation '{req.Op}'")
            };
            return IpcResponse.Success(req.Id, data);
        }
        catch (GatewayDeniedException ex)
        {
            // Visible in the user's own history: "why did it not trade?" should be answerable
            // without reading a log file.
            if (Core.Ops.IsMutating(req.Op))
                gateway.Log.Activity($"AI order refused: {ex.Info.UserMessage} ({ex.Message})", "warn");
            return IpcResponse.Fail(req.Id, ex.Info);
        }
        catch (TradeAgentException ex) { return IpcResponse.Fail(req.Id, ex.Info); }
        catch (ConnectorTransportException ex) { return IpcResponse.Fail(req.Id, ErrorCode.TRADING_CONNECTION_MISSING, ex.Message); }
        catch (Exception ex)
        {
            gateway.Log.Engineering("Ipc", "op_failed", "error", requestId: rid, ex: ex);
            return IpcResponse.Fail(req.Id, ErrorCode.UNKNOWN_ERROR, ex.Message);
        }
    }

    async Task<object?> FindOrder(string id, CancellationToken ct)
    {
        if (gateway.GetRequest(id) is { } r) return r;
        var orders = await gateway.OrdersAsync(true, ct);
        return orders.FirstOrDefault(o => o.ConnectorOrderId == id || o.ClientOrderId == id);
    }

    /// <summary>
    /// The prefix on every request id the GATEWAY mints, and the one prefix an agent may not use.
    ///
    /// It replaces a reserved separator (<c>#</c>), which solved collisions and created a worse
    /// problem: the id is carried into <c>ClientOrderId</c> as <c>TA-{id}</c> and SENT TO THE BROKER,
    /// and safety rule 1 requires that field to round-trip. Whether ATAS accepts <c>#</c> in a client
    /// order id is not knowable from here — it is settleable only on the box — so minting one was a
    /// bet on the one field the rule says must not be guessed at.
    /// </summary>
    const string MintedIdPrefix = "op-";

    /// <summary>
    /// The most characters a CLIENT ORDER ID may run to in total.
    ///
    /// 64 is a conservative guess and it is labelled as one. **ATAS's real limit is NOT VERIFIED**
    /// and cannot be from here — it is settleable only on the box, and it is on the open questions
    /// list with the charset. What is certain is that some limit exists and that safety rule 1
    /// needs this field to come back unchanged, so an unbounded id is a bet rather than a value.
    /// </summary>
    const int MaxClientOrderIdChars = 64;

    /// <summary>
    /// The most characters an incoming request id may run to, so the id built FROM it still fits.
    ///
    /// Derived, not typed: <c>TradingGateway.ClientOrderIdFor</c> prefixes <c>TA-</c>, so the budget
    /// is <see cref="MaxClientOrderIdChars"/> minus that prefix — 61 today. Bounding the request id
    /// and not the thing actually sent was the gap: a 64-character id was accepted and left the
    /// process as a 67-character client order id. Reading the prefix off the real function means a
    /// change there moves this instead of silently breaking it.
    /// </summary>
    static readonly int MaxRequestIdChars = MaxClientOrderIdChars - TradingGateway.ClientOrderIdFor("").Length;

    /// <summary>
    /// The only characters allowed in a request id, minted or agent-chosen: <c>[A-Za-z0-9-]</c>.
    ///
    /// Deliberately narrower than anything a broker is likely to refuse, because this string leaves
    /// the process. Every id in the suite already conformed, so this narrows what is ACCEPTED
    /// without changing what anything currently does.
    /// </summary>
    static bool IsConservativeId(string id) =>
        id.Length > 0 && id.Length <= MaxRequestIdChars && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Agent-initiated cancel-all still goes through per-order requests so each cancellation is a
    /// durable, reconcilable record rather than one opaque sweep.
    ///
    /// THE COUNT IS OF CANCELLATIONS THAT LANDED, not of attempts made. It was
    /// <c>cancelled = results.Count</c>, which reported every order it had tried — so a sweep that
    /// left an order WORKING, or came back UNKNOWN, still said <c>cancelled=1</c>. On the one command
    /// a person reaches for when they want everything to stop, that is the worst possible lie.
    /// </summary>
    async Task<object> CancelAll(AgentContext ctx, string rid, CancellationToken ct)
    {
        var working = await gateway.OrdersAsync(false, ct);
        var results = new List<ExecutionRequest>();
        var nonce = FreshSweepNonce("cancelall");
        var i = 0;
        foreach (var o in working)
            results.Add(await gateway.CancelAsync(ctx, DerivedId(nonce, "cancelall", i++), o.ConnectorOrderId, ct));

        var landed = results.Count(r => r.State is ExecutionState.CANCELLED);
        return new
        {
            cancelled = landed,
            attempted = results.Count,
            // Named rather than inferred: anything not cancelled is still out there, and the agent
            // has to be able to see which without diffing two lists.
            not_cancelled = results.Where(r => r.State is not ExecutionState.CANCELLED)
                .Select(r => new { request_id = r.RequestId, order = r.ConnectorOrderId, state = r.State.ToString() }),
            requests = results
        };
    }

    /// <summary>
    /// Which IPC operations can only ever REDUCE exposure, and so carry the emergency deadline down
    /// through everything they have to read first. `buy`, `sell` and `modify` are absent on purpose.
    /// </summary>
    static bool IsRiskReducing(string op) =>
        op is Core.Ops.Cancel or Core.Ops.CancelAll or Core.Ops.Close or Core.Ops.CloseAll;

    /// <summary>
    /// A per-item id for one leg of a sweep: <c>op-{nonce}-{intent}-{index}</c>.
    ///
    /// It does NOT embed the agent's own id any more. That is what keeps it inside
    /// <see cref="IsConservativeId"/> whatever the agent called its sweep, and the nonce plus the
    /// reserved prefix are what keep it from colliding with anything the agent can choose. The legs
    /// are returned in the reply, so the agent can still tie them back to its own request.
    /// </summary>
    static string DerivedId(string nonce, string intent, int index) =>
        $"{MintedIdPrefix}{nonce}-{intent}-{index}";

    /// <summary>
    /// A candidate nonce for one sweep. Hex, so it cannot leave the conservative charset.
    ///
    /// A WHOLE GUID, not the first eight characters of one. Eight hex is 32 bits, and the thing it
    /// has to stay clear of is not another live sweep — it is this installation's own DURABLE
    /// history, which only grows. At roughly 77,000 lifetime sweeps the birthday probability of
    /// landing on a nonce already in that history reaches about half (Codex F9), and a repeat is not
    /// a near miss: leg <c>op-{nonce}-cancelall-0</c> becomes an id the store already holds, so the
    /// leg REPLAYS an old record and the sweep counts a stale CANCELLED for an order still WORKING.
    ///
    /// 32 hex characters cost nothing here: <c>op-</c> + 32 + <c>-cancelall-</c> + index is 48, well
    /// inside the 61 the client-order-id budget allows, and a test asserts that rather than this
    /// comment claiming it.
    /// </summary>
    static string NewSweepNonce() => Guid.NewGuid().ToString("n");

    /// <summary>TEST SEAM: where a sweep's nonce comes from. The real one in production.</summary>
    public Func<string> SweepNonceSource { get; init; } = NewSweepNonce;

    /// <summary>How many nonces a sweep will try before it refuses to guess again.</summary>
    const int MaxNonceAttempts = 8;

    /// <summary>
    /// A nonce that is not already in the durable history it could collide WITH.
    ///
    /// Widening the nonce makes a collision vanishingly unlikely; this makes it HARMLESS, which is a
    /// different property and the one worth having on the money path. Asking costs one indexed read
    /// per sweep, and it is the only way the guarantee is testable at all — a probability is not
    /// something a test can observe, and 2^128 is not something it can wait for.
    ///
    /// Index 0 is enough to decide it: a sweep that minted anything minted leg 0, and a sweep that
    /// minted nothing left no record for this one to replay.
    /// </summary>
    string FreshSweepNonce(string intent)
    {
        for (var attempt = 1; ; attempt++)
        {
            var nonce = SweepNonceSource();
            if (gateway.Requests.Get(DerivedId(nonce, intent, 0)) is null) return nonce;

            // One in 2^128 that nobody can ever confirm happened is worse than one that is logged.
            gateway.Log.Engineering("Ipc", "sweep_nonce_collision", "warn",
                metadataJson: Json.Write(new { intent, attempt }));

            if (attempt >= MaxNonceAttempts)
                throw new GatewayDeniedException(ErrorCode.UNKNOWN_ERROR,
                    $"could not mint a sweep id for '{intent}' that is not already in this " +
                    "installation's history; nothing was cancelled or closed");
        }
    }

    /// <summary>Same two corrections as <see cref="CancelAll"/>: uncollidable ids, and a count of what landed.</summary>
    async Task<object> CloseAll(AgentContext ctx, string rid, CancellationToken ct)
    {
        var positions = await gateway.PositionsAsync(ct);
        var results = new List<ExecutionRequest>();
        var nothingToDo = new List<string>();
        var nonce = FreshSweepNonce("closeall");
        var i = 0;
        foreach (var p in positions.Where(p => p.Quantity != 0))
        {
            // Null means the gateway found nothing to close for that symbol. Not a failure, and
            // not a closure either — counting it as one is exactly the overstatement being removed.
            var r = await gateway.CloseAsync(ctx, DerivedId(nonce, "closeall", i++), p.Symbol, ct);
            if (r is null) nothingToDo.Add(p.Symbol); else results.Add(r);
        }

        var landed = results.Count(r => r.State is ExecutionState.FILLED);
        return new
        {
            closed = landed,
            attempted = results.Count + nothingToDo.Count,
            nothing_to_close = nothingToDo,
            not_closed = results.Where(r => r.State is not ExecutionState.FILLED)
                .Select(r => new { request_id = r.RequestId, instrument = r.Instrument, state = r.State.ToString() }),
            requests = results
        };
    }

    /// <summary>What TradeAgent observed on disk, plus the notes already recorded against it.</summary>
    object MaterialList(IpcRequest req)
    {
        MaterialOrigin? origin = req.Str("origin")?.ToLowerInvariant() switch
        {
            "inbox" => MaterialOrigin.Inbox,
            "agent" => MaterialOrigin.Agent,
            null or "" or "all" => null,
            var other => throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"origin '{other}' is not one of: inbox, agent, all")
        };

        var items = gateway.Materials.Present(origin);
        return new
        {
            count = items.Count,
            note = "sha is the first 12 characters of sha256, and is what the material commands accept",
            items = items.Select(m => new
            {
                path = m.RelPath,
                origin = m.Origin.ToString().ToLowerInvariant(),
                sha = m.ShortSha,
                sha256 = m.Sha256,
                size_bytes = m.SizeBytes,
                runnable = m.Runnable,
                first_seen = m.FirstSeenAt,
                modified = m.ModifiedAt
            }),
            recent_notes = gateway.Materials.RecentNotes(20).Select(n => new
            {
                at = n.At, author = n.Author, kind = n.Kind.ToString().ToLowerInvariant(),
                subject = n.SubjectSha?[..Math.Min(12, n.SubjectSha.Length)],
                parent = n.ParentSha?[..Math.Min(12, n.ParentSha.Length)],
                text = n.Text
            })
        };
    }

    /// <summary>
    /// Record what the agent says it did with a file.
    ///
    /// An unresolvable hash is refused rather than stored. A note pointing at nothing looks like a
    /// record and is not one, and the whole reason the ledger exists is that a record nobody can
    /// follow back to a file is how the workspace becomes a pile.
    /// </summary>
    object MaterialNote(AgentContext ctx, IpcRequest req)
    {
        var kindText = req.Str("kind") ?? "note";
        if (!Enum.TryParse<MaterialNoteKind>(kindText, true, out var kind))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"note kind '{kindText}' is not one of: {string.Join(", ", Enum.GetNames<MaterialNoteKind>()).ToLowerInvariant()}");

        var text = Require(req, "text");
        var subject = ResolveSha(req.Str("sha"), "sha");
        var parent = ResolveSha(req.Str("from"), "from");

        if (subject is null && kind != MaterialNoteKind.Note)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"a '{kind.ToString().ToLowerInvariant()}' note has to say which file it is about — pass its sha");
        if (kind == MaterialNoteKind.Derived && parent is null)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "a 'derived' note has to say what it was derived from — pass --from with the source sha");

        var id = gateway.Materials.AddNote("agent", ctx.SessionId, kind, subject, parent, text, DateTimeOffset.UtcNow);
        return new { recorded = true, id, kind = kind.ToString().ToLowerInvariant(), subject, parent };
    }

    string? ResolveSha(string? prefix, string argName)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        var found = gateway.Materials.ByShaPrefix(prefix.Trim().ToLowerInvariant())
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"no file in the ledger has a hash starting '{prefix}' — run 'trade material list' for what is there. " +
                "A file only gets a hash once TradeAgent has read it, which can lag a large drop by a pass.");
        return found.Sha256;
    }

    static string Require(IpcRequest r, string key) =>
        r.Str(key) ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{key}' is required");

    static PlaceIntent ParsePlace(IpcRequest r)
    {
        var symbol = Require(r, "symbol");
        var qty = r.Dec("quantity") ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "'quantity' is required");
        var limit = r.Dec("limit");
        var stop = r.Dec("stop");
        var type = limit is not null && stop is not null ? OrderType.StopLimit
            : limit is not null ? OrderType.Limit
            : stop is not null ? OrderType.Stop
            : OrderType.Market;
        var tif = Enum.TryParse<TimeInForce>(r.Str("tif"), true, out var t) ? t : TimeInForce.Day;
        var side = r.Op == Core.Ops.Sell ? OrderSide.Sell : OrderSide.Buy;
        return new PlaceIntent(symbol, side, type, qty, limit, stop, tif, r.Str("comment"));
    }

    /// <summary>
    /// Stops the server. EVERY LIVE CONNECTION IS CLOSED BEFORE ANYTHING IS WAITED ON, and the wait
    /// is bounded.
    ///
    /// What was here cancelled the token and awaited <c>_loop</c> — but <c>_loop</c> is the ACCEPT
    /// loop, and the per-connection handlers are untracked <c>Task.Run</c>s nobody holds. So this
    /// never hung; it did something quieter and worse. It returned promptly and LEFT THE CONNECTION
    /// OPEN, with a handler still parked in a write to a peer that had stopped reading. Measured on
    /// 2026-09-02: DisposeAsync returned in 21 ms and the abandoned connection still had the whole
    /// 960 KB reply to give.
    ///
    /// Cancelling the token cannot fix that on its own — the handler is inside a write, which takes
    /// no token — so the handles are closed here, which fails those writes and lets the handlers
    /// unwind.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;      // idempotent: disposing twice is not an error, it is a no-op
        _disposed = true;

        // 1. Stop taking new connections. Handlers already running are untouched by this.
        await _accept.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* cancelled, faulted, or would not let go: either way we are done */ }
        }

        // 2. Close the connections. This is what frees a handler parked in a write to a peer that
        //    stopped reading — cancellation cannot, because the write is already with the kernel.
        //    A handler that is inside the gateway rather than inside a write is not disturbed by it.
        foreach (var connection in _live.Keys)
        {
            try { connection.Dispose(); } catch (Exception) { /* already gone */ }
            _live.TryRemove(connection, out _);
        }

        // 3. THEN wait for the handlers, with their token still uncancelled. This is the step that
        //    lets a place already at the broker finish and settle. AppHost disposes server (:274),
        //    then gateway (:275), then the database (:276), so a settle that completes here
        //    completes while both are still open. Bounded, because a handler that will not finish
        //    must not be able to hold the app open.
        var handlers = _handlers.Keys.ToArray();
        if (handlers.Length > 0)
        {
            try { await Task.WhenAll(handlers).WaitAsync(HandlerDrainTimeout); }
            catch (Exception) { /* faulted or over the bound; the count below is what matters */ }

            var unfinished = handlers.Count(h => !h.IsCompleted);
            if (unfinished > 0)
                gateway.Log.Engineering("Ipc", "handlers_did_not_finish", "error",
                    metadataJson: Json.Write(new
                    {
                        unfinished,
                        of = handlers.Length,
                        drain_timeout_ms = (int)HandlerDrainTimeout.TotalMilliseconds
                    }));
        }

        // 4. Only now is it safe to cancel the handlers' token: anything still holding it has had
        //    its chance and is over the bound, and there is nothing left to settle in good order.
        await _cts.CancelAsync();
        _cts.Dispose();
        _accept.Dispose();
    }
}
