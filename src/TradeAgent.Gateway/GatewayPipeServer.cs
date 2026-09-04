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
    /// The worst case for ONE CALL through <c>AtasConnector.Rpc</c>, at shipped values:
    ///
    ///     send gate wait      up to WriteTimeout      10 s
    ///   + the write itself    up to FrameTimeout      30 s
    ///   + waiting for ATAS    up to rpcTimeout        10 s
    ///   = 50 s  (`WorstCaseOperationPath`)
    ///
    /// A HANDLER IS NOT ONE CALL, so the drain multiplies that by the longest chain a handler issues
    /// in series — see <see cref="SerialConnectorCallsPerHandler"/>, which is five, and
    /// <see cref="RiskReducingHandlerPath"/>, which is the other shape — and adds
    /// <see cref="SettleAfterCancelTimeout"/> for the write-back. At shipped values: 5 × 50 + 5 =
    /// 255 s, and disposal's ceiling 5 + that + 5 = 265 s.
    ///
    /// THE MIDDLE TERM WAS WRONG UNTIL 2026-09-03 and this number with it. It counted one
    /// WriteTimeout for the whole write, but WriteTimeout is a per-chunk PROGRESS budget reset by
    /// every chunk the peer accepts — so a legal near-1 MiB order could stay in the write for a
    /// thousand times that while this drain, derived from the claim, expired and abandoned it
    /// DISPATCHING (Codex F2). `AtasConnector.FrameTimeout` is the real ceiling and the arithmetic
    /// above now uses it.
    ///
    /// <c>AtasConnector.WorstCaseOrderPath</c> computes those first three from the live values and a
    /// test asserts this default still covers it, so changing a connector deadline breaks a test
    /// rather than silently reintroducing the abandoned order.
    ///
    /// THE TRADE IS DELIBERATE: at the shipped values the app may take up to 255 s here — 265 s over
    /// the whole of disposal — but ONLY while a request is
    /// actually in flight — an idle handler is freed the moment its pipe is closed, which happens
    /// before this wait. Waiting is the right side of that trade, because the alternative is an
    /// order that reached the broker and is recorded DISPATCHING for ever.
    ///
    /// WHAT IT DOES NOT COVER, said plainly rather than left to be discovered: this is the bound for
    /// ONE handler. `TradingGateway._dispatchGate` is a mutex, so N placements in flight together
    /// queue on each other and cost N times a chain — and `DisposeAsync` waits for all of them under
    /// this one bound. That was true before this round and this round does not change it; it is
    /// named here because the number above is otherwise read as covering everything.
    /// </summary>
    public TimeSpan HandlerDrainTimeout
    {
        // AN EXPLICIT VALUE MAY ONLY LENGTHEN THIS, NEVER SHORTEN IT.
        //
        // It used to win outright, which put the whole derivation one constructor argument away from
        // meaningless: `new GatewayPipeServer(gw, tok, pipe) { HandlerDrainTimeout = 7.Seconds() }`
        // against a hundred-second worst path is the abandoned DISPATCHING order this drain exists to
        // prevent, reintroduced by the caller who was trying to configure it (Codex round-8 CHECK d).
        // A caller who names a LONGER value means it and gets it; one who names a shorter value is
        // asking for an order to be abandoned at shutdown, which is not theirs to ask for.
        get => _drain is { } d && d > DerivedDrainTimeout ? d : DerivedDrainTimeout;
        init => _drain = value;
    }

    readonly TimeSpan? _drain;

    /// <summary>
    /// The drain, DERIVED from the connector's live deadlines rather than written down.
    ///
    /// 55 s was a literal, correct for the shipped values and silently wrong for any others — and
    /// constructing a connector with different deadlines is a supported thing to do. Codex C3's
    /// arithmetic: an `AtasConnector` with a 60 s RPC timeout has a 100 s worst path against a 55 s
    /// drain, which is the abandoned-DISPATCHING order cc7006e and 02aad9a exist to prevent,
    /// reintroduced by a constructor argument.
    ///
    /// So it is read off the connector, and a test changes the deadlines and asserts the drain
    /// follows.
    ///
    /// THE TWO SHAPES ARE MAXED, NOT ADDED, because one handler is one shape or the other: it is
    /// risk-reducing or it is not. And the trailing term is <see cref="SettleAfterCancelTimeout"/>
    /// itself rather than a second literal five seconds — the two numbers always meant the same
    /// thing (time for a handler to write down what it knows) and writing it twice is how a derived
    /// number silently stops being derived, which is the class this unit has now fixed three times.
    ///
    /// THE INVARIANT A CALLER CANNOT BREAK: whatever anybody sets, the drain is never shorter than
    /// the composite chain above. The settle term is a margin on top of it; shortening that margin
    /// shortens a handler's write-back window, which is what <see cref="SettleAfterCancelTimeout"/>
    /// already means and already allows. Asserted, rather than left to this paragraph.
    ///
    /// WHAT THIS IS NOT: the whole of disposal. `DisposeAsync` also waits up to 5 s for the accept
    /// loop before this, and up to <see cref="SettleAfterCancelTimeout"/> after it, so the ceiling
    /// on closing is 5 + this + 5 rather than this. Stated because the trade below quotes a number
    /// an operator will experience.
    /// </summary>
    TimeSpan DerivedDrainTimeout => HandlerPaths.Max(p => p.Path) + SettleAfterCancelTimeout;

    /// <summary>
    /// EVERY HANDLER, WITH ITS OWN SERIAL DEPTH — and the drain is the maximum over this table.
    ///
    /// Three rounds have found the drain derived from ONE handler's shape and silently wrong for
    /// another: round 8 from a single connector call, round 9 from a three-call chain that was
    /// really five, round 10 from a risk-reducing handler with one trailing placement that really
    /// has <see cref="MaxLegsInFlight"/>. Enumerating every handler is the structural end of that
    /// class: a handler is covered because it is IN the table, not because somebody remembered it.
    ///
    /// The terms, and they are read off the live connector rather than written down:
    ///
    ///   W = <c>Connector.WorstCaseOperationPath</c>   one ordinary call, every bounded wait in it
    ///   E = <c>Connector.EmergencyBudget</c>          the WHOLE risk-reducing part of one operation
    ///   L = <see cref="MaxLegsInFlight"/>             how many legs of a sweep are in flight at once
    ///
    /// and <see cref="SettleAfterCancelTimeout"/> is added once, on top of the maximum, as the
    /// write-back margin — it is not part of any handler's own path.
    /// </summary>
    public IReadOnlyList<HandlerPath> HandlerPaths =>
    [
        new(Core.Ops.Status, ReadPath, "one account read"),
        new(Core.Ops.Accounts, ReadPath, "one account read"),
        new(Core.Ops.Account, ReadPath, "one account read"),
        new(Core.Ops.Instruments, ReadPath, "one instrument read"),
        new(Core.Ops.Quote, ReadPath, "one quote read"),
        new(Core.Ops.Positions, ReadPath, "the account, then the positions"),
        new(Core.Ops.Position, ReadPath, "the account, then the positions"),
        new(Core.Ops.Orders, ReadPath, "the account, then the orders"),
        new(Core.Ops.Order, ReadPath, "the account, then the orders"),
        new(Core.Ops.Executions, ReadPath, "the account, then the executions"),

        new(Core.Ops.Buy, OrdinaryHandlerPath, "a cold placement: account -> positions -> quote -> instruments -> place"),
        new(Core.Ops.Sell, OrdinaryHandlerPath, "a cold placement: account -> positions -> quote -> instruments -> place"),
        new(Core.Ops.Modify, ModifyHandlerPath, "the account, the orders to resolve the target, the account again, the modify"),

        new(Core.Ops.Cancel, RiskReducingReadPath, "resolve the target, then cancel — both inside the one budget"),
        new(Core.Ops.CancelAll, RiskReducingReadPath, "the orders read and every leg — all inside the one budget"),
        new(Core.Ops.Close, RiskReducingHandlerPath, "the prefix inside the budget, then ONE ordinary placement"),
        new(Core.Ops.CloseAll, CloseAllHandlerPath, "the prefix inside the budget, then ONE WAVE of placements, serialised"),
    ];

    /// <param name="Handler">The IPC op, so a handler and its row cannot drift apart by name.</param>
    /// <param name="Path">The longest this handler can take, from the live connector's own values.</param>
    /// <param name="Why">The chain that number is, in words, for whoever reads a failing assertion.</param>
    public readonly record struct HandlerPath(string Handler, TimeSpan Path, string Why);

    /// <summary>
    /// The deepest READ: an account resolution and then the read itself.
    ///
    /// <c>TradingGateway.RequireAccountId</c> issues <c>GetAccountsAsync</c> when no account has been
    /// selected, and `positions`, `orders` and `executions` all go through it — so a read is two
    /// calls in series on an installation that has not chosen an account, and one on a configured
    /// one. Two is what a bound is for.
    /// </summary>
    TimeSpan ReadPath => 2 * gateway.Connector.WorstCaseOperationPath;

    /// <summary>
    /// `modify`: the account, the orders read that resolves the target reference, the account again
    /// for the record, and the modification. Four in series on an installation with no account
    /// selected, two on a configured one — and never the five a cold placement issues, which is why
    /// this is its own row rather than sharing the placement's.
    /// </summary>
    TimeSpan ModifyHandlerPath => 4 * gateway.Connector.WorstCaseOperationPath;

    /// <summary>
    /// The worst an ORDINARY handler can cost: every call in its chain paying the full per-call
    /// bound, because nothing shortens any of them.
    /// </summary>
    TimeSpan OrdinaryHandlerPath =>
        SerialConnectorCallsPerHandler * gateway.Connector.WorstCaseOperationPath;

    /// <summary>
    /// The worst a RISK-REDUCING handler can cost, and it is a different shape rather than a longer
    /// chain — which is why taking only the ordinary term under-covered it.
    ///
    /// Round 8 gave the whole operation ONE deadline: the orders read, every target resolution and
    /// every leg share <c>EmergencyBudget</c>, and a leg whose turn arrives after it is reported
    /// NOT SENT instead of being issued. So the entire risk-reducing part of such a handler costs
    /// the budget ONCE however many calls it decomposes into.
    ///
    /// Plus exactly one ordinary call, and that one is not a rounding allowance. `close` and
    /// `close-all` are implemented as a PLACE of an offsetting order, and `Place` is excluded from
    /// the emergency deadline on purpose (an op that can open exposure has no claim on it) — so the
    /// last call of a close is served the full ordinary bound while everything in front of it was
    /// served the budget.
    ///
    /// It matters at values the suite actually uses: a fixture with a 30 s emergency budget over a
    /// 4 s connector needs 34 s, against 20 s from the ordinary term alone.
    /// </summary>
    TimeSpan RiskReducingHandlerPath =>
        gateway.Connector.EmergencyBudget + gateway.Connector.WorstCaseOperationPath;

    /// <summary>
    /// `cancel` and `cancel-all`, which end in no ordinary call at all: every RPC they issue is
    /// risk-reducing, so the whole handler is the one budget however many calls it decomposes into.
    /// </summary>
    TimeSpan RiskReducingReadPath => gateway.Connector.EmergencyBudget;

    /// <summary>
    /// `close-all`, and it is the row three rounds of this unit kept getting wrong.
    ///
    /// A `close` ends in a `Place` of an offsetting order, and `Place` is excluded from the emergency
    /// deadline on purpose. `close-all` has <see cref="MaxLegsInFlight"/> of those in the air at
    /// once, and `TradingGateway._dispatchGate` is a MUTEX held across the dispatch — so the wave's
    /// placements do not overlap, they queue, and one wave costs L ordinary calls end to end rather
    /// than one.
    ///
    /// Only ONE wave, and the reason is the deadline rather than the arithmetic: <c>RunLegs</c>
    /// checks the operation deadline before issuing each leg, so once the budget is gone every
    /// remaining leg is reported NOT SENT instead of being issued. Whatever the size of the book, at
    /// the instant the last wave is issued less than E has elapsed, and that wave costs at most
    /// L × W more.
    ///
    /// Codex round-9 F1 measured what the missing term costs: at `E = 30 s`, `W = 4 s`, `S = 5 s` and
    /// four positions the handler needs 51 s and the round-9 formula returned 39 s — twelve seconds
    /// of placements that disposal walks away from.
    /// </summary>
    TimeSpan CloseAllHandlerPath =>
        gateway.Connector.EmergencyBudget + MaxLegsInFlight * gateway.Connector.WorstCaseOperationPath;

    /// <summary>
    /// The longest chain of connector calls ONE ORDINARY handler issues in series, counted from the
    /// handlers rather than assumed — and the longest is a COLD <c>buy</c>/<c>sell</c>.
    ///
    /// Three was written down as "a prerequisite read, a target resolution, the mutation", which is
    /// the shape of a <c>modify</c> and is not the longest one. `TradingGateway.PlaceAsync` on a
    /// process that has not warmed its caches issues FIVE, every one of them awaited before the next
    /// (Codex round-8 CHECK d):
    ///
    ///   1. the account — `AccountAsync`, which is `GetAccountAsync` or `GetAccountsAsync`;
    ///   2. the open positions — the `MaxOpenPositions` check;
    ///   3. a quote — required for EVERY order, so a stale price cannot size one;
    ///   4. the instrument list — read once and cached, so only a cold process pays it;
    ///   5. the order itself.
    ///
    /// "Cold" is not a contrived state: it is every placement made before anything else has warmed
    /// the caches, which at shutdown is exactly the placement most likely to still be in flight.
    ///
    /// WHY NOT SEVEN, which is what a cold `close` issues (a `RequireAccountId` and a positions read,
    /// and then all five of `PlaceAsync`): `close` is RISK-REDUCING, so six of those seven share one
    /// <see cref="RiskReducingHandlerPath"/> budget between them and only the trailing `Place` is
    /// served the ordinary bound. Counting its calls at the ordinary rate would over-cover it by
    /// minutes. The sweeps are excluded for the same reason and one more: their legs are issued
    /// concurrently, so their call COUNT is not their serial depth at all.
    ///
    /// A test counts the cold placement's calls over the real pipe and asserts this number covers
    /// what it counted, so a handler that grows a sixth call fails there instead of silently
    /// shortening this bound (§9.9).
    ///
    /// THE PRICE, STATED, AND IT WENT UP: at the shipped ATAS values (`WorstCaseOperationPath` 50 s)
    /// the drain is 5 × 50 + 5 = 255 s, and disposal's ceiling is 5 + that + 5 = 265 s. Round 8 put
    /// that figure at 155 s and it was too short by two calls. It is paid ONLY while a request is
    /// genuinely in flight — an idle handler is freed when its pipe closes, before this wait — and
    /// the alternative is an order that reached the broker and is recorded DISPATCHING for ever. It
    /// remains a product decision rather than an arithmetic one, and it is the manager's to take.
    /// </summary>
    public const int SerialConnectorCallsPerHandler = 5;

    /// <summary>
    /// How long a handler gets AFTER its token is cancelled, to write down what it knows.
    ///
    /// Cancelling the token and returning was the second half of Codex F2. A handler over the drain
    /// bound is cancelled and then unwinds — through the catch-all that records an after-the-wire
    /// failure as UNKNOWN — and disposal used to walk away at exactly that moment, so the gateway
    /// and then the database closed under a request that was mid-write-back. A request that reached
    /// the broker and left no record is the state this whole drain exists to prevent; producing it
    /// at the last step by cancelling and not waiting is the same defect one line later.
    ///
    /// Short on purpose. This is not another chance to finish the operation — that chance was the
    /// drain above, and it is over. It is time to record an outcome that is already decided.
    /// </summary>
    public TimeSpan SettleAfterCancelTimeout { get; init; } = TimeSpan.FromSeconds(5);

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

        // A FRAME THAT NAMES NO REQUEST IS MALFORMED, and it is answered rather than fatal.
        //
        // `id` has a GUID default so it is never absent — but a client can send it explicitly null,
        // and then this fallback is null too. The two checks below dereference it, and they run
        // BEFORE the handler's try/catch, so such a frame took the whole connection down with a
        // NullReferenceException: every other request on that channel died with it and the agent
        // learned nothing about why (Codex C4). Answering inside the boundary would only turn it
        // into UNKNOWN_ERROR; the honest code is the one the rest of this method already uses.
        if (string.IsNullOrEmpty(rid))
            return IpcResponse.Fail(req.Id ?? "", ErrorCode.INVALID_REQUEST,
                "a request must carry an id: send 'request_id', or leave 'id' to its default rather than sending it null");

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
        using var riskReducing = IsRiskReducing(req.Op)
            ? RiskReducingScope.Begin(gateway.Connector.EmergencyBudget)
            : null;

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
        var nonce = FreshSweepNonce("cancelall");
        // AWAITED RATHER THAN HANDED OVER, and that is not a style choice. `RunLegs` takes the WIDER
        // contract — a leg that may produce no record at all, because `close-all` has that case and
        // `cancel-all` does not — and `Task<T>` is invariant, so passing `CancelAsync`'s
        // `Task<ExecutionRequest>` straight into a `Task<ExecutionRequest?>` parameter is a
        // nullability mismatch the compiler reports (CS8619) and nothing at runtime would catch.
        // Awaiting converts the VALUE instead of the task, which is the widening C# does allow: a
        // cancel leg always has a record, and a helper willing to accept none is satisfied by that.
        var legs = await RunLegs(working.Select(o => o.ConnectorOrderId), "cancelall", nonce,
            async (legId, target) => await gateway.CancelAsync(ctx, legId, target, ct));

        var results = legs.Where(l => l.Record is not null).Select(l => l.Record!).ToList();
        var landed = results.Count(r => r.State is ExecutionState.CANCELLED);
        return new
        {
            cancelled = landed,
            // THE ONE PLACE `nothing-to-do` IS A TRUE THING TO SAY, and it is about the OPERATION.
            // As a per-leg word it was a category error: a leg exists because there was something
            // for it to act on. A sweep that found no targets did nothing, and saying so is not the
            // same as saying it failed.
            nothing_to_do = legs.Count == 0,
            // COUNTED FROM THE OUTCOMES, so it cannot disagree with them. It was the number of legs
            // holding a RECORD, which counts a leg that wrote its record and never dispatched — the
            // one shape Codex found reporting `attempted` for something nothing was attempted on.
            attempted = legs.Count(l => l.Attempted),
            // Named rather than inferred: anything not cancelled is still out there, and the agent
            // has to be able to see which without diffing two lists.
            not_cancelled = results.Where(r => r.State is not ExecutionState.CANCELLED)
                .Select(r => new { request_id = r.RequestId, order = r.ConnectorOrderId, state = r.State.ToString() }),
            not_sent = legs.Count(l => l.Outcome == LegOutcome.NotSent),
            outcomes = legs.Select(l => l.Describe()),
            requests = results
        };
    }

    /// <summary>
    /// What happened to one leg of a sweep, from the point of view of somebody stopping — AND ONE
    /// WORD PER RECORD STATE, so the sentence and the record cannot say different things.
    ///
    /// There were three words for six situations, and two of them were lies. `RefuseCancel` makes a
    /// broker refuse a cancellation definitively; the gateway records REJECTED, which is as final as
    /// an answer gets, and the reply said <c>sent-not-confirmed</c> — sending the owner to reconcile
    /// something that needs no reconciling. And a target resolution that expired before
    /// <c>_requests.TryCreate</c> ran also said <c>sent-not-confirmed</c>, with <c>attempted=0</c>
    /// and NO RECORD AT ALL: a claim that a leg reached the wire when nothing had (Codex round-8 F1).
    ///
    /// The rule that replaces them: <see cref="Classify"/> reads the outcome OFF the record, so a
    /// word can only be produced by the state that means it. <c>sent-not-confirmed</c> now really
    /// does imply UNKNOWN and reconciliation, which is the guarantee Codex asked for and the whole
    /// reason the word exists.
    /// </summary>
    enum LegOutcome
    {
        /// <summary>The broker said this leg's own intent is done: CANCELLED, or FILLED.</summary>
        Confirmed,

        /// <summary>
        /// The broker DEFINITIVELY refused it — REJECTED. Nothing is working from this leg and there
        /// is nothing to reconcile. It is the only outcome allowed to be definite about a failure
        /// (safety rule 3 on <c>IAtasAdapter</c>), and it was being reported as an unknown.
        /// </summary>
        Rejected,

        /// <summary>
        /// It reached the broker, the broker answered, and the order this leg is about is STILL OUT
        /// THERE — WORKING, ACKNOWLEDGED, PARTIALLY_FILLED or CANCEL_PENDING. A `close-all` leg that
        /// rests rather than filling is the ordinary way to get here.
        ///
        /// It is a fifth word where the bounce named four, and it is needed by the bounce's own rule:
        /// without it a WORKING leg falls into <see cref="NotConfirmed"/>, which promises an UNKNOWN
        /// record and reconciliation for an order that is neither.
        /// </summary>
        StillWorking,

        /// <summary>It was sent and the outcome is not known. The gateway has recorded UNKNOWN for it.</summary>
        NotConfirmed,

        /// <summary>
        /// It never reached the wire: no record was written, or one was written and nothing was ever
        /// dispatched from it. Nothing needs reconciling, and the owner has been told which orders
        /// may still be working.
        /// </summary>
        NotSent,

    }

    /// <summary>
    /// WHERE THE FRAME GOT TO DECIDES THE WORD, AND THE RECORD SAYS WHAT THE ANSWER WAS.
    ///
    /// Round 9 read the word off the record alone, which is the right instinct — a word must be
    /// producible only by the thing that means it — applied to a source that cannot carry the
    /// distinction. <c>TradingGateway</c> maps EVERY <c>ConnectorTransportException</c> to UNKNOWN,
    /// correctly, because from up there a refusal before the send gate and a half-written frame are
    /// the same exception. So a leg the connector had PROVED it never sent came back
    /// <c>sent-not-confirmed</c> — an instruction to hunt through ATAS for an order that does not
    /// exist, carrying a flag that pauses all further execution including the retry the sentence
    /// advises (verifier round-9 F-1, measured through the real pipe).
    ///
    /// The two sources answer different questions and neither can answer the other's:
    ///
    ///   1. A record in a state only a BROKER'S ANSWER can produce — CANCELLED, FILLED, REJECTED,
    ///      WORKING, ACKNOWLEDGED, PARTIALLY_FILLED, CANCEL_PENDING — is itself proof the round trip
    ///      completed, and it says what the answer was. That is not the record deciding an ambiguous
    ///      case; it is the record being the ONLY thing that knows which answer came back. (An
    ///      idempotent replay of an earlier leg arrives here with no transport of its own, and this
    ///      is why it does not read as `not-sent`.)
    ///
    ///   2. Everything else — CREATED, AWAITING_APPROVAL, DISPATCHING, UNKNOWN, RECONCILING, or no
    ///      record at all — is a state the record cannot settle, and there the CONNECTOR's
    ///      <see cref="TransportOutcome"/> decides. No mutating call attempted, or one the connector
    ///      can show wrote nothing, is <c>not-sent</c>; anything else is <c>sent-not-confirmed</c>,
    ///      which is the fail-closed direction.
    ///
    /// NO CATCH-ALL. Every <see cref="ExecutionState"/> is named, and a new one must fail to compile
    /// or fail a test rather than quietly becoming the most dangerous word in the set — the same
    /// reason <c>Describe()</c> lost its own default arm (verifier round-9 F-3).
    /// </summary>
    static LegOutcome Classify(ExecutionState? state, TransportOutcome? transport) => state switch
    {
        ExecutionState.CANCELLED or ExecutionState.FILLED => LegOutcome.Confirmed,

        ExecutionState.REJECTED => LegOutcome.Rejected,

        ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED
            or ExecutionState.PARTIALLY_FILLED or ExecutionState.CANCEL_PENDING => LegOutcome.StillWorking,

        // Written but never dispatched, still mid-dispatch, or settled to an unknown: none of these
        // says where the frame got to, so none of them chooses the word.
        null or ExecutionState.CREATED or ExecutionState.AWAITING_APPROVAL
            or ExecutionState.DISPATCHING or ExecutionState.UNKNOWN or ExecutionState.RECONCILING =>
            transport switch
            {
                null or TransportOutcome.NothingWritten => LegOutcome.NotSent,
                TransportOutcome.PossiblyWritten or TransportOutcome.ReplyReceived => LegOutcome.NotConfirmed,
                _ => throw new InvalidOperationException($"no leg outcome for transport result '{transport}'")
            },

        _ => throw new InvalidOperationException($"no leg outcome for execution state '{state}'")
    };

    /// <summary>
    /// THE SEAM THE VOCABULARY IS TESTED THROUGH: one record state and one transport result in, one
    /// of the five words out. Public because the alternative is a membership test that can only
    /// reach the combinations some fixture happens to produce, which is how an unmapped arm survives.
    /// </summary>
    public static string LegWordFor(ExecutionState? state, TransportOutcome? transport) =>
        Word(Classify(state, transport));

    /// <summary>
    /// NO CATCH-ALL ARM. It used to end <c>_ => "not-sent"</c>, so a new outcome would have been
    /// reported as "nothing was even attempted" — the most dangerous of the words to be wrong about
    /// — silently and with no compiler complaint.
    /// </summary>
    static string Word(LegOutcome outcome) => outcome switch
    {
        // `confirmed`, not `sent-and-confirmed`: the word's content is the BROKER'S ANSWER, and
        // leading with a claim about the wire put it in the same shape as the two words that are
        // about the wire and made the set read as six variations on "sent" (Codex round-9 F3).
        LegOutcome.Confirmed => "confirmed",
        LegOutcome.Rejected => "rejected",
        LegOutcome.StillWorking => "sent-still-working",
        LegOutcome.NotConfirmed => "sent-not-confirmed",
        LegOutcome.NotSent => "not-sent",
        _ => throw new InvalidOperationException($"no word for leg outcome '{outcome}'")
    };

    /// <param name="NoTargetFound">
    /// The gateway found nothing for this leg to act on — a `close-all` symbol whose position had
    /// already gone. It is reported by NAME (`nothing_to_close`) rather than by a word of its own:
    /// the leg reached no wire, so its word is the one that means that.
    /// </param>
    sealed record Leg(string RequestId, string Target, LegOutcome Outcome, ExecutionRequest? Record,
        TransportOutcome? Transport, string? Error, bool NoTargetFound = false)
    {
        /// <summary>Whether this leg got as far as the wire, which is what <c>attempted</c> counts.</summary>
        public bool Attempted => Outcome is not LegOutcome.NotSent;

        public object Describe() => new
        {
            request_id = RequestId,
            order = Target,
            outcome = Word(Outcome),
            state = Record?.State.ToString(),
            // THE EVIDENCE, IN THE SAME OBJECT AS THE CLAIM. A leg refused before the wire is
            // `not-sent` while `TradingGateway.SettleUnknown` still writes UNKNOWN on its row — that
            // row is the gateway's and not this unit's to change, so the answer carries the
            // connector's own report of where the frame got to rather than leaving an owner to
            // reconcile two fields that disagree.
            transport = Transport?.ToString(),
            error = Error ?? Record?.LastError
        };
    }

    /// <summary>
    /// Issues every leg of a sweep under the operation's ONE deadline, and reports what became of
    /// each of them.
    ///
    /// Three things were wrong with the loop this replaces, and they were the same fault seen from
    /// three sides. It awaited each leg before starting the next, so the legs were serial when the
    /// only thing that has to be serial is the connector's own send gate. It had no try/catch, so a
    /// single failing leg abandoned every leg after it — SILENTLY, since the exception surfaced as
    /// one transport error for the whole sweep and named none of the orders left working. And every
    /// leg started its own two-second budget, so the promise scaled with the size of the book.
    ///
    /// Now: the legs are issued concurrently, each inheriting the ambient deadline, so the sweep
    /// costs one budget rather than one per order; a leg that fails is recorded rather than allowed
    /// to end the sweep; and a leg whose turn comes after the deadline is reported as NOT SENT
    /// rather than dropped. That last distinction is the one an owner needs: "this order may still
    /// be working and nothing was even attempted on it" is different news from "we tried and do not
    /// know".
    /// </summary>
    async Task<IReadOnlyList<Leg>> RunLegs(
        IEnumerable<string> targets, string intent, string nonce, Func<string, string, Task<ExecutionRequest?>> issue)
    {
        var legs = new List<Leg>();
        var i = 0;
        var pending = new List<(string Id, string Target, TransportRecord Transport, Task<ExecutionRequest?> Task)>();

        foreach (var target in targets)
        {
            var legId = DerivedId(nonce, intent, i++);

            // Checked before every leg, not once: the deadline can pass while earlier legs are in
            // flight, and a leg whose turn never comes must be REPORTED rather than dropped.
            if (RiskReducingScope.DeadlineAt is { } d && Environment.TickCount64 >= d)
            {
                legs.Add(new Leg(legId, target, LegOutcome.NotSent, null, TransportOutcome.NothingWritten,
                    "the operation ran out of time before this leg was issued; it was not sent"));
                continue;
            }

            // ONE TRANSPORT RECORD PER LEG, attached before the leg is started so it flows into the
            // leg's own execution context and nowhere else. The legs of a wave run concurrently and
            // each mutates the object THIS loop still holds; the handle is disposed immediately
            // because it only has to restore the ambient value for the next iteration.
            var transport = new TransportRecord();
            Task<ExecutionRequest?> leg;
            using (TransportLedger.Attach(transport)) leg = issue(legId, target);

            pending.Add((legId, target, transport, leg));
            if (pending.Count < MaxLegsInFlight) continue;
            await Collect(pending, legs);
            pending.Clear();
        }

        await Collect(pending, legs);
        return legs;
    }

    /// <summary>
    /// How many legs of a sweep are in flight at once.
    ///
    /// Concurrent, because awaiting each leg before starting the next made the sweep serial when the
    /// only thing that has to be serial is the connector's own send gate. BOUNDED, because that gate
    /// means unbounded fan-out buys nothing: past a handful the legs queue on it anyway, and a book
    /// of several hundred orders would put several hundred gateway dispatches in flight to achieve
    /// it. Four is a judgement, and it is the number that makes "issued in waves" true — which is
    /// also what makes a leg's turn able to arrive after the deadline, and therefore what makes
    /// NOT SENT a real outcome rather than a branch nothing reaches.
    /// </summary>
    public const int MaxLegsInFlight = 4;

    async Task Collect(
        List<(string Id, string Target, TransportRecord Transport, Task<ExecutionRequest?> Task)> pending,
        List<Leg> legs)
    {
        foreach (var (id, target, transport, task) in pending)
        {
            try
            {
                var record = await task;
                legs.Add(new Leg(id, target, Classify(record?.State, transport.Outcome), record,
                    transport.Outcome, null, NoTargetFound: record is null));
            }
            catch (Exception ex)
            {
                // A LEG THAT THREW IS NOT CLASSIFIED BY THE FACT THAT IT THREW, and since round 10 it
                // is not classified by its record alone either.
                //
                // Assuming NotConfirmed made two different lies: a broker's definite refusal
                // (REJECTED) read as an unknown, and a leg whose target resolution expired BEFORE
                // `TryCreate` — no record written, nothing sent, `attempted=0` — read as a leg that
                // reached the wire (Codex round-8 F1). Reading the RECORD instead fixed both and left
                // a third: `TradingGateway` settles every ambiguous connector failure as UNKNOWN, so a
                // leg the connector proved it never sent read `sent-not-confirmed` too (verifier
                // round-9 F-1). The connector's own report of where the frame got to is the evidence
                // that separates them, and it is what decides the word.
                var record = gateway.GetRequest(id);
                legs.Add(new Leg(id, target, Classify(record?.State, transport.Outcome), record,
                    transport.Outcome, ex.Message));
            }
        }
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
        var nonce = FreshSweepNonce("closeall");
        var legs = await RunLegs(positions.Where(p => p.Quantity != 0).Select(p => p.Symbol), "closeall", nonce,
            (legId, symbol) => gateway.CloseAsync(ctx, legId, symbol, ct));

        // Null from the gateway means it found nothing to close for that symbol. Not a failure, and
        // not a closure either — counting it as one is exactly the overstatement bdf9a24 removed.
        var nothingToDo = legs.Where(l => l.NoTargetFound).Select(l => l.Target).ToList();
        var results = legs.Where(l => l.Record is not null).Select(l => l.Record!).ToList();

        var landed = results.Count(r => r.State is ExecutionState.FILLED);
        return new
        {
            closed = landed,
            nothing_to_do = legs.Count == 0,
            // Same rule as `cancel-all`, and it CHANGES this number: a symbol with nothing to close
            // was being counted as attempted. It is already reported, by name, in
            // `nothing_to_close` — counting it here as well is the same over-claim bdf9a24 removed
            // from `cancelled`, one field over.
            attempted = legs.Count(l => l.Attempted),
            nothing_to_close = nothingToDo,
            not_closed = results.Where(r => r.State is not ExecutionState.FILLED)
                .Select(r => new { request_id = r.RequestId, instrument = r.Instrument, state = r.State.ToString() }),
            not_sent = legs.Count(l => l.Outcome == LegOutcome.NotSent),
            outcomes = legs.Select(l => l.Describe()),
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
        }

        // 4. Only now is it safe to cancel the handlers' token: anything still holding it has had
        //    its chance and is over the bound, and there is nothing left to settle in good order.
        await _cts.CancelAsync();

        // 5. AND THEN WAIT AGAIN, briefly, FOR THE UNWIND. Cancelling and returning was a way to
        //    produce the very state this drain prevents: a cancelled handler unwinds through the
        //    catch-all that records an after-the-wire failure as UNKNOWN, and disposal used to walk
        //    away at that exact moment — so AppHost closed the gateway and then the database under a
        //    request that was mid-write-back, leaving an order that may have reached the broker with
        //    no record at all. This is not another chance to finish the operation; that was step 3.
        //    It is time to write down an outcome that is already decided.
        if (handlers.Length > 0)
        {
            try { await Task.WhenAll(handlers).WaitAsync(SettleAfterCancelTimeout); }
            catch (Exception) { /* the count below is what matters */ }

            // COUNTED AFTER THE UNWIND, and counted on the STATE rather than on the symptom.
            //
            // It counted handler TASKS still running, which is not what this line is about. A
            // connector that HONOURS its cancellation token unwinds the instant disposal cancels it,
            // so the handler finishes — while `TradingGateway.ModifyAsync` catches only
            // `ConnectorRejectedException` and `ConnectorTransportException` and lets the
            // cancellation escape, leaving the row DISPATCHING and unflagged. `ReconcileAsync` scans
            // `NeedingReconciliation()` alone, so nothing will ever settle it, and the only trace an
            // operator gets said nothing at all (verifier round-9 F-2, measured: `DISPATCHING rows =
            // 1`, `handlers_did_not_finish = (not logged)`).
            //
            // So both are reported, and the REQUEST is the one that decides whether this fires. It
            // is named, because "something was abandoned" is not something anybody can act on.
            // Settling the row belongs to whoever owns the request — routed to U2c-1; refusing to
            // return silently belongs here.
            var unfinished = handlers.Count(h => !h.IsCompleted);
            var unsettled = gateway.Requests.Query("execution_state='DISPATCHING'");
            if (unfinished > 0 || unsettled.Count > 0)
                gateway.Log.Engineering("Ipc", "handlers_did_not_finish", "error",
                    metadataJson: Json.Write(new
                    {
                        unfinished,
                        of = handlers.Length,
                        unsettled = unsettled.Count,
                        requests = unsettled.Select(r => r.RequestId).ToArray(),
                        drain_timeout_ms = (int)HandlerDrainTimeout.TotalMilliseconds,
                        settle_timeout_ms = (int)SettleAfterCancelTimeout.TotalMilliseconds
                    }));
        }

        _cts.Dispose();
        _accept.Dispose();
    }
}
