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
    readonly string _pipe = pipeName ?? Paths.PipeName;
    readonly CancellationTokenSource _cts = new();
    Task? _loop;

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
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }
        return new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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
                catch (Exception) { await Send(writer, IpcResponse.Fail("", ErrorCode.INVALID_REQUEST, "frame is not valid JSON")); continue; }
                if (req is null) { await Send(writer, IpcResponse.Fail("", ErrorCode.INVALID_REQUEST, "empty frame")); continue; }

                if (req.Op == Core.Ops.Hello)
                {
                    if (!Security.IpcToken.Matches(req.Token, token))
                    {
                        gateway.Log.Engineering("Ipc", "auth_rejected", "warn");
                        await Send(writer, IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, "token rejected"));
                        return; // one chance per connection
                    }
                    authenticated = true;
                    await Send(writer, IpcResponse.Success(req.Id, new
                    {
                        protocol_version = Versions.ProtocolVersion,
                        app_version = Versions.App,
                        compatible = req.V == Versions.ProtocolVersion
                    }));
                    continue;
                }

                if (!authenticated)
                {
                    await Send(writer, IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, "say hello with a valid token first"));
                    return;
                }

                await Send(writer, await Handle(req, ct));
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

    static Task Send(StreamWriter w, IpcResponse r) => w.WriteLineAsync(Json.Write(r));

    async Task<IpcResponse> Handle(IpcRequest req, CancellationToken ct)
    {
        var ctx = new AgentContext(string.IsNullOrWhiteSpace(req.Session) ? "agent" : req.Session!);
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

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) { try { await _loop; } catch (Exception) { } }
        _cts.Dispose();
    }
}
