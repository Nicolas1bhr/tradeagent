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
    readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Every connection currently being served. The handlers are fire-and-forget tasks, so without
    /// this <see cref="DisposeAsync"/> has no way to reach one: it awaited the ACCEPT loop, which is
    /// not where a stalled writer is parked, and the connection outlived the server that owned it.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<NamedPipeServerStream, byte> _live = new();

    Task? _loop;
    volatile bool _disposed;

    public string PipeName => _pipe;

    public void Start() => _loop ??= Task.Run(() => AcceptLoop(_cts.Token));

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
                _ = Task.Run(() => Serve(s, ct), ct);
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
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await ReadFrame(reader, ct);
                if (line is null) break;
                if (line.Length == 0) continue;

                IpcRequest? req;
                try { req = Json.Read<IpcRequest>(line); }
                catch (Exception)
                {
                    if (!await Send(pipe, writer, IpcResponse.Fail("", ErrorCode.INVALID_REQUEST, "frame is not valid JSON"), "", null, null)) return;
                    continue;
                }
                if (req is null)
                {
                    if (!await Send(pipe, writer, IpcResponse.Fail("", ErrorCode.INVALID_REQUEST, "empty frame"), "", null, null)) return;
                    continue;
                }

                if (req.Op == Core.Ops.Hello)
                {
                    if (!Security.IpcToken.Matches(req.Token, token))
                    {
                        gateway.Log.Engineering("Ipc", "auth_rejected", "warn");
                        await Send(pipe, writer, IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, "token rejected"), req.Op, req.Session, req.RequestId);
                        return; // one chance per connection
                    }
                    authenticated = true;
                    if (!await Send(pipe, writer, IpcResponse.Success(req.Id, new
                    {
                        protocol_version = Versions.ProtocolVersion,
                        app_version = Versions.App,
                        compatible = req.V == Versions.ProtocolVersion
                    }), req.Op, req.Session, req.RequestId)) return;
                    continue;
                }

                if (!authenticated)
                {
                    await Send(pipe, writer, IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, "say hello with a valid token first"), req.Op, req.Session, req.RequestId);
                    return;
                }

                if (!await Send(pipe, writer, await Handle(req, ct), req.Op, req.Session, req.RequestId ?? req.Id)) return;
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
    async Task<bool> Send(NamedPipeServerStream pipe, StreamWriter w, IpcResponse r,
        string op, string? session, string? requestId)
    {
        var write = w.WriteLineAsync(Json.Write(r));
        try
        {
            await write.WaitAsync(WriteTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            Observe(write);
            // THE REQUEST ID IS THE POINT OF THIS RECORD, not decoration on it. The reply that was
            // dropped may be the only acknowledgement of an order that already reached the broker,
            // and this log line is then the sole surviving link between that order and the id the
            // agent must reuse to reconcile it. It used to be written with request_id NULL.
            gateway.Log.Engineering("Ipc", "peer_stopped_reading", "warn", session: session,
                requestId: requestId,
                metadataJson: Json.Write(new
                {
                    op,
                    request_id = requestId,
                    write_timeout_ms = (int)WriteTimeout.TotalMilliseconds
                }));
            try { pipe.Dispose(); } catch (Exception) { /* already gone */ }
            return false;
        }
    }

    static void Observe(Task t) => _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);

    async Task<IpcResponse> Handle(IpcRequest req, CancellationToken ct)
    {
        // The reserved session is refused rather than quietly downgraded. AgentContext.ForAgent
        // cannot return an operator context whatever this string says, so nothing here is load
        // bearing for safety — it is a tripwire. An agent asking for the operator's name is probing
        // for an escalation, and a probe nobody can see afterwards is not evidence.
        if (string.Equals(req.Session?.Trim(), AgentContext.OperatorSessionId, StringComparison.OrdinalIgnoreCase))
        {
            gateway.Log.Engineering("Ipc", "operator_session_refused", "warn",
                session: req.Session, requestId: req.RequestId ?? req.Id,
                metadataJson: Json.Write(new { op = req.Op }));
            return IpcResponse.Fail(req.Id, ErrorCode.INVALID_REQUEST,
                $"'{AgentContext.OperatorSessionId}' is a reserved session name and is not available on this channel");
        }

        var ctx = AgentContext.ForAgent(req.Session);
        var rid = req.RequestId ?? req.Id;
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
    /// Agent-initiated cancel-all still goes through per-order requests so each cancellation is a
    /// durable, reconcilable record rather than one opaque sweep.
    /// </summary>
    async Task<object> CancelAll(AgentContext ctx, string rid, CancellationToken ct)
    {
        var working = await gateway.OrdersAsync(false, ct);
        var results = new List<object>();
        var i = 0;
        foreach (var o in working)
            results.Add(await gateway.CancelAsync(ctx, $"{rid}-{i++}", o.ConnectorOrderId, ct));
        return new { cancelled = results.Count, requests = results };
    }

    async Task<object> CloseAll(AgentContext ctx, string rid, CancellationToken ct)
    {
        var positions = await gateway.PositionsAsync(ct);
        var results = new List<object?>();
        var i = 0;
        foreach (var p in positions.Where(p => p.Quantity != 0))
            results.Add(await gateway.CloseAsync(ctx, $"{rid}-{i++}", p.Symbol, ct));
        return new { closed = results.Count, requests = results };
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
        await _cts.CancelAsync();

        foreach (var connection in _live.Keys)
        {
            try { connection.Dispose(); } catch (Exception) { /* already gone */ }
            _live.TryRemove(connection, out _);
        }

        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* cancelled, faulted, or would not let go: either way we are done */ }
        }
        _cts.Dispose();
    }
}
