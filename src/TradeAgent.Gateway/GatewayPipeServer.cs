using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

/// <summary>
/// The only door into the gateway from the agent's side of the fence.
///
/// Deliberately narrow: read operations and order operations, nothing else. Operator authority —
/// mode changes, the kill switch, live activation, approvals — is NOT reachable here, so an agent
/// that decides it would like more permission has nowhere to ask.
/// </summary>
public sealed class GatewayPipeServer(TradingGateway gateway, string token, string? pipeName = null)
    : IAsyncDisposable, IGatewayCalls
{
    /// <summary>
    /// The launch grants this gateway will recognise. The process-wide register by default, because
    /// the half that mints them is the app and the half that checks them is this class.
    /// </summary>
    public Security.AgentGrants Grants { get; init; } = Security.AgentGrants.Shared;

    /// <summary>
    /// WHAT A PEER MUST BE TO PRESENT A GRANT, or null to check nothing.
    ///
    /// The app configures it on every platform, because the kernel answers on every platform:
    /// <see cref="Security.PeerImage.ClientPath"/> names the program holding the connection on
    /// Windows, macOS and Linux, and where the kernel will not say, a caller presenting a grant is
    /// refused rather than waved through. Null is for a gateway built without the app — a test's —
    /// and the Doctor's containment row says in the owner's words whether the installation recorded
    /// what to check against.
    /// </summary>
    public Security.PeerRule? Peer { get; init; }

    /// <summary>
    /// How the image of the peer is read. Overridable so the RULE can be tested against an answer
    /// that is awkward to arrange for real; the product never sets it, and <c>LaunchGrantTests</c>
    /// asks the real kernel as well.
    /// </summary>
    public Func<NamedPipeServerStream, string?> PeerImagePath { get; init; } = Security.PeerImage.ClientPath;

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
    readonly System.Collections.Concurrent.ConcurrentDictionary<NamedPipeServerStream, Connected> _live = new();

    /// <summary>
    /// WHAT ONE OPEN CONNECTION PROVED, AND WHETHER IT IS INSIDE A CALL RIGHT NOW.
    ///
    /// <para>Both halves exist for the same ending. When a launch grant stops being live, the
    /// connections it authenticated must close — otherwise revoking it changes a list and nothing
    /// else, while the revoked caller keeps trading down a socket it opened earlier (REVIEW
    /// 2026-09-16 finding 5). To close them, the server has to know which connection is holding
    /// which token.</para>
    ///
    /// <para>And it must not close one MID-CALL: <see cref="Security.AgentGrants.Grace"/> is for a
    /// frame the gateway is already inside, whose order may already be at the broker, and cutting
    /// the socket there would report a failure for an order that placed. So an ending on a busy
    /// connection is recorded, the reply is delivered, and the connection closes on its way out.</para>
    ///
    /// <para>Under a lock rather than three volatile fields, because "set ended, and close it if
    /// nobody is inside" and "leave the call, and close if it ended while I was in there" are each
    /// one decision over two fields. Read with volatile fields, an ending that lands between the two
    /// reads is an ending nobody acts on, and the socket stays open for ever.</para>
    /// </summary>
    sealed class Connected
    {
        readonly Lock _gate = new();
        string? _grant;
        bool _inCall, _ended;

        /// <summary>
        /// The launch grant's token this connection proved at hello, or null for a caller that
        /// presented none — authenticated, roleless, and nothing to end.
        /// </summary>
        public string? Grant
        {
            get { lock (_gate) return _grant; }
            set { lock (_gate) _grant = value; }
        }

        /// <summary>A frame is being handled from now until <see cref="LeaveCall"/>.</summary>
        public void EnterCall() { lock (_gate) _inCall = true; }

        /// <summary>Done with the frame; true if the grant ended while it ran, so this connection closes.</summary>
        public bool LeaveCall() { lock (_gate) { _inCall = false; return _ended; } }

        /// <summary>
        /// The grant this connection proved has ended. True if the CALLER should close the pipe now;
        /// false means a call is in flight and the handler will close it after the reply.
        /// </summary>
        public bool End() { lock (_gate) { _ended = true; return !_inCall; } }
    }

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
    /// How many CONNECTION handlers are still running. Exposed so a test can assert the premise it
    /// claims to have created — that the agent has gone and its handler is finished — rather than
    /// assuming it from a sleep.
    /// </summary>
    public int LiveHandlerCount => _handlers.Count;

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
    /// <see cref="HandlerOverhead"/> for the handler's own work and
    /// <see cref="SettleAfterCancelTimeout"/> for the write-back. At shipped values:
    /// 5 × 50 + 1 + 5 = 256 s, and disposal's ceiling 5 + that + 5 = 266 s.
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
    /// THE TRADE IS DELIBERATE: at the shipped values the app may take up to 256 s here — 266 s over
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
    /// THREE TERMS, AND THE MIDDLE ONE IS WHAT MAKES THIS BOUND THE HANDLER. A row is arithmetic
    /// over the connector's deadlines, so the table bounds the CALLS; <see cref="HandlerOverhead"/>
    /// is what the handler costs on top of them — the frame, the parse, the request rows, the reply.
    /// It used to be absent, and the only thing standing in for it was
    /// <see cref="SettleAfterCancelTimeout"/>, which is a different quantity and is `init`-settable
    /// to zero: at zero the drain equalled the longest row exactly and any handler overhead at all
    /// was outside it (verifier round-11 L-2, measured — `cancel-all` at 917 ms against a 900 ms
    /// row). The two are now separate, so configuring the write-back window cannot configure away
    /// the bound.
    ///
    /// THE INVARIANT A CALLER CANNOT BREAK: whatever anybody sets, the drain is never shorter than
    /// the composite chain above PLUS that overhead. Asserted, rather than left to this paragraph.
    ///
    /// WHAT THIS IS NOT: the whole of disposal. `DisposeAsync` also waits up to 5 s for the accept
    /// loop before this, and up to <see cref="SettleAfterCancelTimeout"/> after it, so the ceiling
    /// on closing is 5 + this + 5 rather than this. Stated because the trade below quotes a number
    /// an operator will experience.
    /// </summary>
    TimeSpan DerivedDrainTimeout => HandlerPaths.Max(p => p.Path) + HandlerOverhead + SettleAfterCancelTimeout;

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
    /// and two terms are added once, on top of the maximum: <see cref="HandlerOverhead"/>, what a
    /// handler costs beyond its connector calls, and <see cref="SettleAfterCancelTimeout"/>, the
    /// write-back margin. Neither is part of any handler's own connector path, and a ROW IS THE
    /// CONNECTOR CHAIN AND NOT THE HANDLER.
    /// </summary>
    public IReadOnlyList<HandlerPath> HandlerPaths =>
    [
        new(Core.Ops.Status, ReadPath, "one account read"),
        new(Core.Ops.Schema, ReadPath, "the same status the 'status' handler builds, described"),
        new(Core.Ops.Accounts, ReadPath, "one account read"),
        new(Core.Ops.Account, ReadPath, "one account read"),
        new(Core.Ops.Instruments, ReadPath, "one instrument read"),
        new(Core.Ops.Quote, ReadPath, "one quote read"),
        new(Core.Ops.Positions, ReadPath, "the account, then the positions"),
        new(Core.Ops.Position, ReadPath, "the account, then the positions"),
        new(Core.Ops.Orders, ReadPath, "the account, then the orders"),
        new(Core.Ops.Order, ReadPath, "the account, then the orders"),
        new(Core.Ops.Executions, ReadPath, "the account, then the executions"),
        new(Core.Ops.Pnl, PnlHandlerPath, "the account, then the positions, then the instruments — the fills come from the ledger"),

        // NO CONNECTOR CALL AT ALL, and they are in the table anyway. A row with a zero path
        // contributes nothing to the maximum, which is the correct arithmetic — but a handler that
        // is ABSENT is one nobody will notice growing a call, and that is exactly how `schema` came
        // to make a connector-backed `StatusAsync` call from outside the derivation (Codex round-10
        // F3). Being in the table is what makes a handler covered.
        new(Core.Ops.Connectors, TimeSpan.Zero, "the connector's own id and capabilities, in process"),
        new(Core.Ops.MaterialList, TimeSpan.Zero, "the workspace ledger, in process"),
        new(Core.Ops.MaterialNote, TimeSpan.Zero, "the workspace ledger, in process"),
        new(Core.Ops.DataList, TimeSpan.Zero, "the dataset ledger and the hashes of the files it names, on disk"),
        new(Core.Ops.DataBars, TimeSpan.Zero, "the same hashes, then one normalised file read, on disk"),
        new(Core.Ops.DataTape, TimeSpan.Zero, "the tape's own file, opened read-only, one snapshot, on disk"),
        new(Core.Ops.VenueList, TimeSpan.Zero, "the venue catalogue this installation has recorded, in process"),
        new(Core.Ops.Report, TimeSpan.Zero, "the day's own tables and one file read, in process"),
        new(Core.Ops.Backtest, TimeSpan.Zero, "one program file read, then the dataset's own hashes and a stream of its bars, in process"),
        new(Core.Ops.RunTrades, TimeSpan.Zero, "the strategy ledger's run row and its trades, in process"),
        new(Core.Ops.Verdict, TimeSpan.Zero, "the campaign and this role's own runs of the version, then the referee's holdout run over the same bars, in process"),
        new(Core.Ops.DeploymentList, TimeSpan.Zero, "the deployment ledger and its operations, in process"),
        new(Core.Ops.LedgerAdd, TimeSpan.Zero, "the research ledger: one entry and its first revision, in process"),
        new(Core.Ops.LedgerRevise, TimeSpan.Zero, "the research ledger: one revision of the caller's own entry, in process"),
        new(Core.Ops.LedgerList, TimeSpan.Zero, "the research ledger's entries, newest first, in process"),
        new(Core.Ops.LedgerShow, TimeSpan.Zero, "the research ledger: one entry, its revisions, its links and the ids derived from them, in process"),

        new(Core.Ops.Buy, OrdinaryHandlerPath, "a cold placement: account -> positions -> quote -> instruments -> place"),
        new(Core.Ops.Sell, OrdinaryHandlerPath, "a cold placement: account -> positions -> quote -> instruments -> place"),
        new(Core.Ops.Modify, ModifyHandlerPath, "one orders read that both resolves the target and takes it as it stands, then a cold placement's own chain and the modify"),

        new(Core.Ops.Cancel, RiskReducingReadPath, "resolve the target, then cancel — both inside the one budget"),
        new(Core.Ops.CancelAll, RiskReducingReadPath, "the orders read and every leg — all inside the one budget"),
        new(Core.Ops.Close, RiskReducingHandlerPath, "all of it inside the budget, plus ONE ordinary placement for a connector that ignores the close intent"),
        new(Core.Ops.CloseAll, CloseAllHandlerPath, "all of it inside the budget, plus ONE WAVE of placements, serialised, for a connector that ignores the close intent"),

        // ENDING A DEPLOYMENT IS A CLOSE-ALL'S SHAPE, and it is budgeted as one: a cancel per working
        // order this run placed and then one close through the same path `close` takes. `CloseAll`'s
        // row is the conservative bound over both, and being IN this table is what makes the handler
        // covered — see the note above the zero-path rows.
        new(Core.Ops.DeploymentStop, CloseAllHandlerPath, "a cancel per working order of the run, then ONE close through the same path 'close' takes"),
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
    /// `pnl`: the account, the positions, and then the instruments — three in series.
    ///
    /// The FILLS cost nothing here: they come out of the ledger, which is the point of having one.
    /// The two reads that remain are the ones a figure about money cannot be honest without — the
    /// platform's own open positions with its own average price, and the contract size the value of
    /// a point is derived from. Both are allowed to FAIL without failing the handler; the report
    /// names what it could not include, which is why this row is a bound and not a requirement.
    /// </summary>
    TimeSpan PnlHandlerPath => 3 * gateway.Connector.WorstCaseOperationPath;

    /// <summary>
    /// `modify`: ONE orders read — which both resolves the target reference and takes the target as
    /// it stands — and then every call a cold placement makes, the account, positions, the quote and
    /// the instruments, before the modification itself. Six in series.
    ///
    /// It was four, from the days when a modification went to the wire on an authorization and
    /// nothing else. `TradingGateway.ModifyAsync` now risk-checks the order as it WILL STAND, which
    /// is the same arithmetic on the same numbers a placement is checked against (REVIEW 2026-09-05,
    /// Codex F2), so it issues a placement's chain on top of its own read. A row that is short is a
    /// shutdown drain that abandons a modification mid-flight, so it grows with the handler — and
    /// this is now the longest row in the table, which is why the drain moved with it.
    /// </summary>
    TimeSpan ModifyHandlerPath => 6 * gateway.Connector.WorstCaseOperationPath;

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
    /// Plus exactly one ordinary call, and it is RETAINED rather than left over. `close` and
    /// `close-all` are implemented as a PLACE of an offsetting order, and that placement used to be
    /// excluded from the emergency deadline outright — so the last call of a close was served the
    /// full ordinary bound while everything in front of it was served the budget. It is not excluded
    /// any more: <see cref="PlaceOrderCommand.Intent"/> carries the fact that a close REDUCES risk,
    /// both shipped connectors read it, and on either of them the whole handler now fits inside the
    /// budget.
    ///
    /// THE TERM STAYS BECAUSE THE INTENT IS AN OBLIGATION AND NOT A GUARANTEE. It is a field on a
    /// command, like the transport ledger's two calls: a third-party `ITradingConnector` that ignores
    /// it is safe and slow, and serves that placement the ordinary bound. A drain is a bound over
    /// whatever connector this gateway was handed, and one that is too long only costs shutdown
    /// seconds while one that is too short abandons an order that reached the broker.
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
    /// A `close` ends in a `Place` of an offsetting order, which a connector that does not read
    /// <see cref="PlaceOrderCommand.Intent"/> serves the ordinary bound (see
    /// <see cref="RiskReducingHandlerPath"/> for why that possibility is what this term covers).
    /// `close-all` has <see cref="MaxLegsInFlight"/> of those in the air at once, and
    /// `TradingGateway._dispatchGate` is a MUTEX held across the dispatch — so the wave's placements
    /// do not overlap, they queue, and one wave costs L ordinary calls end to end rather than one.
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
    /// and then all five of `PlaceAsync`): `close` is RISK-REDUCING, so all seven share one
    /// <see cref="RiskReducingHandlerPath"/> budget between them on a connector that reads
    /// <see cref="PlaceOrderCommand.Intent"/>, and six of the seven on one that does not. Counting
    /// its calls at the ordinary rate would over-cover it by minutes. The sweeps are excluded for the
    /// same reason and one more: their legs are issued concurrently, so their call COUNT is not their
    /// serial depth at all.
    ///
    /// A test counts the cold placement's calls over the real pipe and asserts this number covers
    /// what it counted, so a handler that grows a sixth call fails there instead of silently
    /// shortening this bound (§9.9).
    ///
    /// THE PRICE, STATED, AND IT WENT UP: at the shipped ATAS values (`WorstCaseOperationPath` 50 s)
    /// the drain is 5 × 50 + 1 + 5 = 256 s, and disposal's ceiling is 5 + that + 5 = 266 s. Round 8
    /// put that figure at 155 s and it was too short by two calls; the extra second is
    /// <see cref="HandlerOverhead"/>, added in round 12. It is paid ONLY while a request is
    /// genuinely in flight — an idle handler is freed when its pipe closes, before this wait — and
    /// the alternative is an order that reached the broker and is recorded DISPATCHING for ever. It
    /// remains a product decision rather than an arithmetic one, and it is the manager's to take.
    /// </summary>
    public const int SerialConnectorCallsPerHandler = 5;

    /// <summary>
    /// WHAT A HANDLER COSTS ON TOP OF ITS CONNECTOR CALLS, and the term that makes the drain bound
    /// the HANDLER rather than the handler's connector chain.
    ///
    /// Every row in <see cref="HandlerPaths"/> is arithmetic over the connector's own deadlines, so
    /// the table bounds the CALLS. A handler is more than its calls: it reads a frame off the pipe
    /// and parses it, writes its request record, settles it, and writes a reply — none of which any
    /// connector deadline describes. The only margin covering that was
    /// <see cref="SettleAfterCancelTimeout"/>, which is a DIFFERENT quantity (how long a handler gets
    /// AFTER cancellation to write down what it already knows), is added once, and is `init`-settable
    /// to zero — at which point the drain equalled the longest row exactly and every millisecond of
    /// handler was outside it. Measured at W = 300 ms and E = 900 ms: `cancel-all` cost 917 ms
    /// against a 900 ms row (verifier round-11 L-2).
    ///
    /// A CONSTANT AND NOT A DERIVATION, and that is deliberate rather than the usual defect: this
    /// work is a pipe read, a JSON parse and two or three SQLite writes on a local file. It does not
    /// scale with anything a connector reports, so deriving it from a connector deadline would be
    /// the fiction. One second is a judgement — three orders of magnitude over the 17 ms measured on
    /// an idle Mac, room for a loaded box, and a rounding error against a drain of 256 s. It is
    /// asserted against every handler this suite drives, so a handler that grows real overhead fails
    /// there rather than by shortening a shutdown.
    /// </summary>
    public static readonly TimeSpan HandlerOverhead = TimeSpan.FromSeconds(1);

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
    ///
    /// A CALLER MAY ONLY LENGTHEN IT. See <see cref="WriteBackAfterCancel"/>, which is what disposal
    /// actually waits.
    /// </summary>
    public TimeSpan SettleAfterCancelTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// WHAT DISPOSAL ACTUALLY WAITS AFTER CANCELLING, and it has a floor because a bound a caller can
    /// configure to nothing is not a bound.
    ///
    /// <see cref="SettleAfterCancelTimeout"/> is `init`-settable, and at ZERO step 5 waits for
    /// nothing: disposal cancels, times out immediately, reports the row as unsettled and returns —
    /// while the handler is one continuation away from writing UNKNOWN. AppHost then closes the
    /// gateway (:275) and the database (:276), so the settle lands in a store that is being taken
    /// away and the row stays `DISPATCHING`, unflagged, until the NEXT start's sweep notices.
    /// Measured at that setting on 2026-09-05: `Expected: UNKNOWN / Actual: DISPATCHING` at the
    /// instant disposal returned. It is the same shape as verifier round-11 L-2, which found the
    /// drain itself resting on this settable number, and the same answer: separate the promise from
    /// the setting.
    ///
    /// THE FLOOR IS <see cref="HandlerOverhead"/> BECAUSE IT IS ALREADY THE NAME FOR THIS WORK — a
    /// pipe read, a JSON parse and two or three local SQLite writes, not scaling with anything a
    /// connector reports. Writing an outcome down after cancellation is a subset of it. Lengthening
    /// is still a caller's to ask for; shortening it below what the write-back costs is asking for an
    /// order to be abandoned at shutdown, which is not.
    ///
    /// The DRAIN is deliberately left reading the raw setting: it is a different quantity — how long
    /// a handler gets to FINISH — and the test that configures the margin away to prove the drain
    /// does not depend on it must go on proving exactly that.
    /// </summary>
    TimeSpan WriteBackAfterCancel =>
        SettleAfterCancelTimeout > HandlerOverhead ? SettleAfterCancelTimeout : HandlerOverhead;

    Task? _loop;
    volatile bool _disposed;

    public string PipeName => _pipe;

    /// <summary>
    /// Opens the door, and subscribes to the register's endings while it is open.
    ///
    /// <para>Not in the constructor, because <see cref="Grants"/> is an <c>init</c> property and is
    /// not set yet when the constructor runs — a subscription there would listen to
    /// <see cref="Security.AgentGrants.Shared"/> whatever register this server was given. Here is
    /// also where it belongs: the subscription exists to close PIPE connections, and a server that
    /// was never started has none. <see cref="DisposeAsync"/> unsubscribes.</para>
    /// </summary>
    public void Start()
    {
        if (_loop is not null) return;
        Grants.Ended += OnGrantEnded;
        _loop = Task.Run(() => AcceptLoop(_accept.Token));
    }

    /// <summary>
    /// A LAUNCH GRANT HAS ENDED, SO THE CONNECTIONS IT AUTHENTICATED CLOSE.
    ///
    /// <para>This is the half of finding 5 that re-verifying the grant per frame does not cover. A
    /// revoked or expired token is caught on the next frame either way — but a turn that ends
    /// DISPOSES its grant, and the expiry is then pulled in to now plus the 60 s grace rather than
    /// deleted, so for that minute the register still calls the grant live. Without this the ended
    /// turn keeps placing orders down the connection it already holds for as long as the grace runs,
    /// which is precisely the minute nobody is supervising it.</para>
    ///
    /// <para>A connection with a call in flight is not cut: it is marked, answers the frame it is
    /// inside, and closes on the way out. That is what the grace is for, and it is all it is for.</para>
    /// </summary>
    void OnGrantEnded(string token)
    {
        foreach (var (pipe, state) in _live)
        {
            if (!Security.AgentGrants.SameToken(state.Grant, token)) continue;
            if (!state.End()) continue;            // in a call: the handler closes it after the reply
            try { pipe.Dispose(); } catch (Exception) { /* already gone */ }
        }
    }

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
                var state = new Connected();
                _live[s] = state;
                // The HANDLER token, not the accept token: a connection already taken is served to
                // the end even though the door has closed.
                var handler = Task.Run(() => Serve(s, state, _cts.Token), _cts.Token);
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

    async Task Serve(NamedPipeServerStream pipe, Connected state, CancellationToken ct)
    {
        var authenticated = false;
        // WHO THIS CONNECTION PROVED IT IS, settled at hello and never re-read off a later frame. A
        // role carried per-request would be a role the caller asserts; this one is the grant the app
        // minted for one process, looked up in the app's own register.
        //
        // WHETHER THAT IS STILL TRUE is a different question, and it is asked again on every frame
        // below. What the connection proved does not change; what it is worth does.
        Security.AgentGrant? grant = null;
        try
        {
            await using var _ = pipe;
            var buffer = new FrameBuffer();

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await ReadFrame(pipe, buffer, ct);
                if (line is null) break;
                if (line.Length == 0) continue;

                IpcRequest? req;
                try { req = Json.Read<IpcRequest>(line); }
                catch (Exception)
                {
                    // A FRAME THAT NEVER SAID WHICH PROTOCOL IT SPEAKS IS ANSWERED ON THE PROTOCOL,
                    // not as bad JSON (Codex F13). `v` is now required on the type, so such a frame
                    // arrives here rather than at the version check below — and "frame is not valid
                    // JSON" would send whoever owns that peer hunting a syntax error in a document
                    // that has none. Same code and same engineering line as a version this build
                    // does not speak, because it is the same fault: the two ends have not agreed
                    // what the frame is, and every other field is read on the strength of that.
                    var unversioned = NamesNoVersion(line);
                    if (unversioned)
                        gateway.Log.Engineering("Ipc", "protocol_rejected", "warn",
                            metadataJson: Json.Write(new { said = "absent", speaks = Versions.ProtocolVersion }));

                    var unreadable = unversioned
                        ? IpcResponse.Fail("", ErrorCode.INCOMPATIBLE_PROTOCOL,
                            $"this frame does not say which protocol version it speaks; TradeAgent speaks {Versions.ProtocolVersion}. " +
                            "Send 'v' on every frame: a version this build assumed on your behalf would be a version " +
                            "neither end agreed, and every other field is read on the strength of that agreement.")
                        : IpcResponse.Fail("", ErrorCode.INVALID_REQUEST, "frame is not valid JSON");
                    if (!await Send(pipe, unreadable, "", null, null)) return;
                    continue;
                }
                if (req is null)
                {
                    if (!await Send(pipe, IpcResponse.Fail("", ErrorCode.INVALID_REQUEST, "empty frame"), "", null, null)) return;
                    continue;
                }

                if (req.Op == Core.Ops.Hello)
                {
                    // THE PROTOCOL IS SETTLED BEFORE THE CREDENTIAL IS READ, and the order is the
                    // whole of it (Codex F8).
                    //
                    // The version used to be REPORTED — the reply carried `compatible: false` and
                    // the connection was authenticated anyway — so a peer built against a protocol
                    // this build does not speak went straight on to trade over it, on the strength
                    // of a field nothing was obliged to read. That is the one direction a version
                    // number must never fail in: the bridge half of this program refuses a
                    // mismatched hello outright rather than half-trusting it, and this half quietly
                    // did the opposite on the channel that places orders.
                    //
                    // Before the token, because a token is a credential and a credential is a value
                    // read out of a frame whose shape both ends have agreed. With the checks the
                    // other way round, a peer one version out is told its permission is wrong,
                    // which sends whoever owns it hunting a secret problem that does not exist.
                    //
                    // The connection is left open rather than dropped: a peer that guessed the
                    // version can say hello again with the right one, and until it does the channel
                    // is exactly as unauthenticated as it was before the frame arrived.
                    if (req.V != Versions.ProtocolVersion)
                    {
                        gateway.Log.Engineering("Ipc", "protocol_rejected", "warn",
                            session: req.Session, requestId: req.RequestId ?? req.Id,
                            metadataJson: Json.Write(new { said = req.V, speaks = Versions.ProtocolVersion }));
                        if (!await Send(pipe, IpcResponse.Fail(req.Id, ErrorCode.INCOMPATIBLE_PROTOCOL,
                            $"this frame says protocol version {req.V}; TradeAgent speaks {Versions.ProtocolVersion}. " +
                            "One of the two halves is out of date — update it rather than working around this."),
                            req.Op, req.Session, req.RequestId)) return;
                        continue;
                    }

                    if (!Security.IpcToken.Matches(req.Token, token))
                    {
                        gateway.Log.Engineering("Ipc", "auth_rejected", "warn");
                        await Send(pipe, IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, "token rejected"), req.Op, req.Session, req.RequestId);
                        return; // one chance per connection
                    }
                    // WHO IS CALLING, and it is settled here or not at all.
                    //
                    // A frame with no grant is authenticated and roleless: it may read, and every
                    // rule that asks which role is calling answers "none". A frame WITH a grant is
                    // making the strong claim, so it gets the strong checks — the register must know
                    // the token, the turn must not be over, and the program holding the handle must
                    // be TradeAgent's own trade command, as the kernel names it on every platform; a
                    // kernel that will not say is a refusal.
                    if (GrantRefusal(pipe, req, out grant) is { } grantRefusal)
                    {
                        gateway.Log.Engineering("Ipc", "grant_rejected", "warn", session: req.Session,
                            requestId: req.RequestId ?? req.Id);
                        await Send(pipe, grantRefusal, req.Op, req.Session, req.RequestId);
                        return; // one chance per connection, exactly as a wrong token gets
                    }

                    // Refused BEFORE the flag flips: a connection that asked for the operator's
                    // name never becomes usable under it, rather than being refused per-op after.
                    if (ReservedSessionRefusal(req) is { } helloRefusal)
                    {
                        if (!await Send(pipe, helloRefusal, req.Op, req.Session, req.RequestId)) return;
                        continue;
                    }

                    authenticated = true;
                    // WHICH TOKEN THIS SOCKET IS HOLDING, so that the end of that grant can reach it.
                    // Recorded after every refusal above, so a connection that failed the checks is
                    // never registered as holding anything.
                    state.Grant = grant?.Token;
                    if (!await Send(pipe, IpcResponse.Success(req.Id, new
                    {
                        protocol_version = Versions.ProtocolVersion,
                        app_version = Versions.App,
                        // Kept on the wire, and now always true by construction: a hello that
                        // reaches this line agreed the version above. It used to be the ONLY
                        // report of a mismatch, which made compatibility something the peer was
                        // trusted to check about itself.
                        compatible = true,
                        role = grant?.Role,
                        attempt = grant?.AttemptId
                    }), req.Op, req.Session, req.RequestId)) return;
                    continue;
                }

                if (!authenticated)
                {
                    await Send(pipe, IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, "say hello with a valid token first"), req.Op, req.Session, req.RequestId);
                    return;
                }

                // THE GRANT IS RE-VERIFIED HERE, ON EVERY FRAME, and this is the whole of finding 5.
                //
                // It used to be verified once, inside the hello arm, and the role it proved was then
                // carried into every later frame on this connection — so an expired, revoked or
                // turn-ended grant kept placing orders for as long as the caller held the socket,
                // while a NEW connection presenting the same grant was refused. Revocation that does
                // not reach a live connection is not revocation.
                //
                // Against the token this connection PROVED, never against `req.Grant`: a token read
                // off a later frame is one the caller asserts, and re-reading it here would let a
                // peer swap in some other live grant per frame and be whoever it liked.
                //
                // Before the reserved-session check and before the role gate, because it is the
                // question those two are answered ON BEHALF OF. A read is refused as well as a
                // place: a fresh connection presenting this grant is refused outright rather than
                // served as a roleless reader, and the same ending cannot mean less on a socket that
                // happens to be open already.
                if (grant is not null && EndedGrantRefusal(req, grant) is { } gone)
                {
                    gateway.Log.Engineering("Ipc", "grant_rejected", "warn", session: req.Session,
                        requestId: req.RequestId ?? req.Id);
                    await Send(pipe, gone, req.Op, req.Session, req.RequestId ?? req.Id);
                    return; // one chance per connection, exactly as hello gives
                }

                if (ReservedSessionRefusal(req) is { } refusal)
                {
                    if (!await Send(pipe, refusal, req.Op, req.Session, req.RequestId ?? req.Id)) return;
                    continue;
                }

                // INSIDE THE CALL from here to the reply. An ending that arrives now is recorded and
                // acted on below rather than cutting the frame in half: this is the one thing
                // AgentGrants.Grace is for.
                state.EnterCall();
                IpcResponse answer;
                var endedMidCall = false;
                try { answer = await Handle(req, grant?.Role, grant?.AttemptId, ct); }
                finally { endedMidCall = state.LeaveCall(); }

                if (!await Send(pipe, answer, req.Op, req.Session, req.RequestId ?? req.Id)) return;

                // The grant ended while that call was in flight. The reply is delivered — the agent
                // learns what happened to the order it already sent — and then the connection goes.
                if (endedMidCall) return;
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

    /// <summary>
    /// One newline-delimited frame, with the cap counted IN BYTES ON THE WIRE.
    ///
    /// It used to read CHARACTERS through a <c>StreamReader</c> and compare
    /// <c>StringBuilder.Length</c> — UTF-16 chars AFTER decoding — with <see cref="MaxFrameBytes"/>.
    /// Every non-ASCII character in a legal JSON string is two or three bytes in and one char out,
    /// so a frame of ordinary CJK text was accepted at 2.6x the cap the contract states: measured
    /// at 2,700,096 bytes against 1,048,576, read whole into the server's memory and answered
    /// (finding 10, probe P9). It is the one bound this server puts on what a peer may make it hold,
    /// and <c>ReadFrame</c> runs BEFORE the hello check — so the peer that spends it need not have
    /// authenticated, or hold a token at all.
    ///
    /// So the bytes are counted as bytes and decoded afterwards, which is the only way the number
    /// can mean what it says. Counting UTF-8 lengths per decoded char would be a second encoder
    /// alongside the real one, wrong for surrogate pairs and wrong again for invalid input the
    /// decoder replaces; the wire is the authority on how many bytes arrived.
    ///
    /// Buffered, because the alternative is a syscall per byte. The buffer belongs to the caller —
    /// one per connection — so a frame that spans reads keeps whatever the previous read had left
    /// over, which a fresh local buffer would silently discard.
    ///
    /// Over the cap throws, which the connection handler treats as the peer going away: it is
    /// dropped without a reply, exactly as a peer that stops reading is. Answering would mean
    /// finding the end of a frame that is by definition unbounded first.
    /// </summary>
    static async Task<string?> ReadFrame(Stream stream, FrameBuffer buffer, CancellationToken ct)
    {
        using var frame = new MemoryStream(capacity: 512);
        while (true)
        {
            if (!buffer.HasBytes)
            {
                buffer.Count = await stream.ReadAsync(buffer.Bytes, ct);
                buffer.Position = 0;
                if (buffer.Count == 0)
                    return frame.Length > 0 ? Decode(frame) : null;      // the peer hung up mid-frame
            }

            var b = buffer.Bytes[buffer.Position++];
            if (b == (byte)'\n') return Decode(frame);
            frame.WriteByte(b);
            if (frame.Length > MaxFrameBytes)
                throw new IOException($"frame exceeds the maximum size of {MaxFrameBytes} bytes");
        }

        static string Decode(MemoryStream m) =>
            Utf8.GetString(m.GetBuffer(), 0, (int)m.Length).TrimEnd('\r');
    }

    /// <summary>
    /// The read buffer for ONE connection, and it is a class because the leftovers matter.
    ///
    /// A read off the pipe returns whatever has arrived, which is routinely more than one frame or
    /// part of the next one. Holding that between calls is what makes the frames after the first
    /// arrive at all.
    /// </summary>
    sealed class FrameBuffer
    {
        public readonly byte[] Bytes = new byte[PipeBuffer];
        public int Count;
        public int Position;
        public bool HasBytes => Position < Count;
    }

    /// <summary>No BOM, and never throwing: a peer's malformed bytes are its own frame's problem.</summary>
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

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
        var session = req.Session?.Trim();

        // AND THE PAPER DEPLOYMENT'S OWN NAME, on the same tripwire and with the same standing. A
        // deployment identity is minted in process by `AgentContext.Deployment` and `ForAgent` cannot
        // build one, so this refusal is not what makes the claim true either — but an agent naming
        // itself `deployment:<something>` is probing for the one identity in this product that places
        // orders with nobody asking, and a probe nobody can see afterwards is not evidence.
        var reserved =
            string.Equals(session, AgentContext.OperatorSessionId, StringComparison.OrdinalIgnoreCase)
                ? AgentContext.OperatorSessionId
            : session is { Length: > 0 }
              && session.StartsWith(AgentContext.DeploymentSessionPrefix, StringComparison.OrdinalIgnoreCase)
                ? AgentContext.DeploymentSessionPrefix
                : null;

        if (reserved is null) return null;

        gateway.Log.Engineering("Ipc", "operator_session_refused", "warn",
            session: req.Session, requestId: req.RequestId ?? req.Id,
            metadataJson: Json.Write(new { op = req.Op, reserved }));
        return IpcResponse.Fail(req.Id, ErrorCode.INVALID_REQUEST,
            $"'{reserved}' is a reserved session name and is not available on this channel");
    }

    /// <summary>
    /// Null when this hello may be served, and the refusal otherwise. <paramref name="grant"/> is the
    /// launch this connection is, or null for a roleless one.
    ///
    /// THE ABSENT CASE IS NOT A REFUSAL, and that is the one judgement in here worth stating. A
    /// connection with no grant is exactly as authenticated as it was before this unit and exactly as
    /// unable to trade as a caller with no role — the owner's own <c>trade status</c> on the machine
    /// still answers, and nothing that moves money does. Refusing it outright would buy no safety
    /// (the roleless caller can already do nothing dangerous) and would cost the one diagnostic route
    /// into a gateway that is misbehaving.
    ///
    /// The image is checked only for a connection that PRESENTS a grant, and only where a rule was
    /// configured — which the app does on every platform. The kernel answers on all three; where it
    /// will not, the verdict is "could not be identified" and the grant is refused: the kernel's
    /// silence is a refusal, never a pass.
    /// </summary>
    IpcResponse? GrantRefusal(NamedPipeServerStream pipe, IpcRequest req, out Security.AgentGrant? grant)
    {
        grant = null;
        var verdict = Grants.Verify(req.Grant);

        if (verdict.State is Security.GrantState.Absent) return null;

        if (verdict.State is not Security.GrantState.Valid)
            return IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, verdict.Reason);

        if (Peer is { } rule)
        {
            var image = PeerImagePath(pipe);
            if (Security.PeerImage.Verdict(image, Security.PeerImage.HashOf(image), rule) is { } why)
                return IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED,
                    $"this connection presented a launch grant, and {why}");
        }

        grant = verdict.Grant;
        return null;
    }

    /// <summary>
    /// IS THE GRANT THIS CONNECTION PROVED STILL LIVE? Asked on every frame; null means carry on.
    ///
    /// <para>The same register, the same verdict and the same words a FRESH connection presenting
    /// this token would get, because it is the same question — the only difference is that this
    /// caller asked it a while ago and got a yes. A grant is worth what it is worth now.</para>
    ///
    /// <para>The peer-image rule is NOT re-run here, and that is a choice with a reason: the process
    /// on the other end of an accepted pipe cannot change, so the rule's answer cannot either, while
    /// re-running it would hash the trade command's image off disk on every frame — a file read and
    /// a SHA-256 in the path of every order, to re-answer a question whose subject is fixed. It is
    /// settled once, at hello, where the connection is admitted.</para>
    /// </summary>
    IpcResponse? EndedGrantRefusal(IpcRequest req, Security.AgentGrant grant)
    {
        var verdict = Grants.Verify(grant.Token);
        return verdict.State is Security.GrantState.Valid
            ? null
            : IpcResponse.Fail(req.Id, ErrorCode.IPC_UNAUTHENTICATED, verdict.Reason);
    }

    /// <summary>
    /// ONE OPERATION FROM AN IN-PROCESS WORKER, through exactly the handler the pipe uses.
    ///
    /// <para>The app-owned harness has no child process, so it presents no machine token and no launch
    /// grant: its identity is the one the composition root assigns it, which is the same pair a
    /// verified grant would have yielded. What it must not have is a second dispatcher — see
    /// <see cref="IGatewayCalls"/> — so this method adds exactly one thing to <see cref="Handle"/>, the
    /// reserved-session tripwire the read loop applies to every frame, and then hands over.</para>
    ///
    /// <para>It works whether or not <see cref="Start"/> was ever called: the pipe is one transport
    /// into this handler and an in-process worker is another.</para>
    /// </summary>
    public Task<IpcResponse> CallAsync(IpcRequest req, string? role, string? attemptId,
        CancellationToken ct = default) =>
        ReservedSessionRefusal(req) is { } refusal
            ? Task.FromResult(refusal)
            : Handle(req, role, attemptId, ct);

    async Task<IpcResponse> Handle(IpcRequest req, string? role, string? attemptId, CancellationToken ct)
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

        var ctx = AgentContext.ForAgent(req.Session, role, attemptId);

        // THE ROLE DECIDES WHETHER MONEY MOVES, and this is the only line that reads it.
        //
        // Before it, every authenticated caller was the same caller: one machine token, no way to
        // tell the Operations Director's launch from the Research Director's, so a Research turn
        // asking to buy was served. The doctrine gives Research no order permission at all, and a
        // caller that presented no grant has proved no role and gets none either — which is what
        // "the machine token alone is no longer served as the chair" means in code.
        //
        // Refused here rather than inside TradingGateway because it is a fact about the CALLER, not
        // about the order: the gateway's modes, limits, approvals and kill switch are unchanged and
        // still apply to everything that gets past this.
        if (Core.Ops.IsMutating(req.Op) && !ctx.MayPlaceOrders)
        {
            var who = ctx.Role is { Length: > 0 } named
                ? $"the {CouncilRoles.Title(named)}"
                : "a caller that presented no launch grant";
            gateway.Log.Activity($"AI order refused: {who} may not place, change or cancel orders", "warn");
            return IpcResponse.Fail(req.Id, ErrorCode.ROLE_MAY_NOT_TRADE,
                $"'{req.Op}' moves money, and {who} may not. Only the " +
                $"{CouncilRoles.Title(CouncilRoles.Operations)}'s own launch may, and it proves which launch " +
                "it is with the grant TradeAgent put in its environment.");
        }

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
        //
        // ON THE WALL CLOCK, deliberately, where the owner's presses are on the platform's
        // (U-fix-press-budget): the shutdown drain is derived from this budget bounding the whole
        // risk-reducing part of a handler, store included — see CloseAllHandlerPath — and refunding
        // the store's time here would let a sweep's last wave start later than the drain allows for.
        using var riskReducing = IsRiskReducing(req.Op)
            ? RiskReducingScope.Begin(gateway.Connector.EmergencyBudget)
            : null;

        try
        {
            object? data = req.Op switch
            {
                Core.Ops.Status      => ForCaller(await gateway.StatusAsync(ct), ctx),
                Core.Ops.Schema      => GatewaySchema.Describe(ForCaller(await gateway.StatusAsync(ct), ctx)),
                Core.Ops.Connectors  => new[] { new { id = gateway.Connector.Id, name = gateway.Connector.DisplayName, capabilities = gateway.Connector.Capabilities } },
                Core.Ops.Accounts    => await gateway.AccountsAsync(ct),
                Core.Ops.Account     => await gateway.AccountAsync(ct),
                Core.Ops.Instruments => await gateway.InstrumentsAsync(ct),
                Core.Ops.Quote       => await gateway.QuoteAsync(Require(req, "symbol"), ct),
                Core.Ops.Positions   => await gateway.PositionsAsync(ct),
                Core.Ops.Position    => (await gateway.PositionsAsync(ct)).FirstOrDefault(p => p.Symbol == Require(req, "symbol")),
                Core.Ops.Orders      => await gateway.OrdersAsync(NamedFlag(req, "all", whenAbsent: false), ct),
                Core.Ops.Order       => await FindOrder(ctx, Require(req, "id"), ct),
                Core.Ops.Executions  => await gateway.ExecutionsAsync(ct),
                Core.Ops.Pnl         => await PnlFor(req, ct),
                Core.Ops.MaterialList => MaterialList(req),
                Core.Ops.MaterialNote => MaterialNote(ctx, req),
                Core.Ops.DataList     => DataList(ctx),
                Core.Ops.DataBars     => DataBars(ctx, req),
                Core.Ops.DataTape     => DataTape(ctx, req),
                Core.Ops.VenueList    => VenueList(),
                Core.Ops.Report       => ReportFor(req),
                Core.Ops.Backtest     => BacktestFor(ctx, req, ct),
                Core.Ops.RunTrades    => RunTrades(ctx, req),
                Core.Ops.Verdict      => VerdictFor(ctx, req, ct),
                Core.Ops.DeploymentList => DeploymentList(),
                Core.Ops.LedgerAdd    => LedgerAdd(ctx, req),
                Core.Ops.LedgerRevise => LedgerRevise(ctx, req),
                Core.Ops.LedgerList   => LedgerList(req),
                Core.Ops.LedgerShow   => LedgerShow(req),

                Core.Ops.Buy or Core.Ops.Sell => await gateway.PlaceAsync(ctx, rid, ParsePlace(req), ct),
                Core.Ops.Modify   => await gateway.ModifyAsync(ctx, rid, Require(req, "id"), req.Dec("quantity"), req.Dec("limit"), req.Dec("stop"), ct),
                Core.Ops.Cancel   => await gateway.CancelAsync(ctx, rid, Require(req, "id"), ct),
                Core.Ops.CancelAll=> await CancelAll(ctx, rid, ct),
                Core.Ops.Close    => await gateway.CloseAsync(ctx, rid, Require(req, "symbol"), ct),
                Core.Ops.CloseAll => await CloseAll(ctx, rid, ct),
                Core.Ops.DeploymentStop => await DeploymentStop(ctx, req, ct),

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

    /// <summary>
    /// THE STATUS THIS CALLER IS SERVED IS COMPUTED WITH THIS CALLER'S AUTHORITY.
    ///
    /// <c>TradingGateway.StatusAsync</c> answers the authorization question as
    /// <c>AgentContext.Operator</c>, which is right for the Dashboard — the owner at the keyboard is
    /// who it is describing — and wrong for everyone on this pipe. The kill-switch arm of that chain
    /// is <c>AiTradingStopped &amp;&amp; !ctx.IsOperator</c>, so it was SKIPPED for the agent's own
    /// status: with the switch down, `status` said `execution_available: true` and gave no reason,
    /// while every order from that same session came back AI_TRADING_STOPPED (finding 7). `schema`
    /// embeds the same object, so it said it twice.
    ///
    /// Two fields, recomputed here rather than in the gateway, because the gateway serves both
    /// audiences from one method and only the pipe server knows whose question it is holding. The
    /// answer is fresher than the one it replaces, not staler: the account read inside StatusAsync
    /// happens between the two.
    ///
    /// The gates themselves are untouched — this is what the caller is TOLD, and the point is that
    /// it now matches what the caller is ALLOWED, which is decided in one place as before.
    /// </summary>
    GatewayStatus ForCaller(GatewayStatus status, AgentContext ctx)
    {
        var available = gateway.TryAuthorizeExecution(ctx, out var reason);
        return status with { ExecutionAvailable = available, ExecutionBlockedReason = reason };
    }

    /// <summary>
    /// One order, by request id or by broker order id — and the request records this caller may see
    /// are its OWN.
    ///
    /// <c>GetRequest</c> had no restriction at all, so <c>trade order op-close-&lt;nonce&gt;-ES</c>
    /// handed the agent the OPERATOR's press record: the session that wrote it, the parameters, the
    /// state, the broker order id and the owner-facing sentence "you pressed Close all positions at
    /// 10:15" (UNVERIFIED 6, measured over the pipe). Operator authority is deliberately absent from
    /// this channel; the operator's record of exercising it was on it anyway.
    ///
    /// A REFUSAL THAT LOOKS DIFFERENT FROM A MISS IS AN EXISTENCE ORACLE, so there is no refusal:
    /// the record is simply not this caller's, the lookup falls through, and the answer is the same
    /// "nothing by that name" an id nobody ever minted gets. The ids are not guessable, and this
    /// keeps them uninformative even if one were.
    ///
    /// The BROKER's book is untouched by this and deliberately so. Every order on the account,
    /// including the ones a press placed, is already visible through <c>orders --all</c>; what it
    /// carries is the platform's own view, not TradeAgent's record of who asked for it and why.
    /// </summary>
    async Task<object?> FindOrder(AgentContext ctx, string id, CancellationToken ct)
    {
        if (gateway.GetRequest(id) is { } r && MayRead(ctx, r)) return r;
        var orders = await gateway.OrdersAsync(true, ct);
        return orders.FirstOrDefault(o => o.ConnectorOrderId == id || o.ClientOrderId == id);
    }

    /// <summary>
    /// Whether this caller wrote this record. Two clauses, and they answer different questions.
    ///
    /// The OPERATOR's rows are never on this channel, whatever they are called: the session that
    /// wrote them is <see cref="AgentContext.OperatorSessionId"/>, which the pipe refuses as a name
    /// before a hello can adopt it, so no caller here can ever match it.
    ///
    /// A GATEWAY-MINTED id resolves only for the session whose sweep minted it. The agent is handed
    /// these ids in its own <c>cancel-all</c> and <c>close-all</c> replies and must be able to ask
    /// about them, which is why the answer is not "no <c>op-</c> id, ever" — but they are the one
    /// family of ids an agent did not choose and therefore the one family it could name without
    /// having been told. An id the agent chose is left alone: it is unique per record, it cannot
    /// begin <c>op-</c>, and restricting those by session as well would take away the ability to
    /// look up work from before a restart, which changes the session name and nothing else.
    /// </summary>
    static bool MayRead(AgentContext ctx, ExecutionRequest r)
    {
        if (string.Equals(r.AgentSessionId, AgentContext.OperatorSessionId, StringComparison.OrdinalIgnoreCase))
            return false;

        // AND A PAPER DEPLOYMENT'S ROWS ARE NEVER ON THIS CHANNEL EITHER, for the operator's reason
        // exactly: the session that wrote them is minted in process and the pipe refuses the name
        // before a hello can adopt it, so no caller here can match it. `trade deployment list` is
        // where a role reads what is deployed, and it carries no request ids.
        if (r.AgentSessionId is { Length: > 0 } wrote
            && wrote.StartsWith(AgentContext.DeploymentSessionPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return !r.RequestId.StartsWith(MintedIdPrefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(r.AgentSessionId, ctx.SessionId, StringComparison.Ordinal);
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
        // THE COMPOSITE IS WRITTEN BEFORE ANY EFFECT, AND A REPLAY SENDS NOTHING (Codex C2).
        //
        // The nonce used to be minted here, fresh, per CALL — so an agent whose reply was lost and
        // who re-sent the same request id got a whole new sweep over whatever was on the book by
        // then, including orders it had placed since. `BeginCompositeAsync` claims the id, stores
        // the plan and the nonce, and hands back the stored ones on a second call; the legs are then
        // named the same and every leg's own record refuses to dispatch twice.
        //
        // THE BOOK READ IS THE DELEGATE, NOT A LINE ABOVE THIS ONE, and the difference is the whole
        // of the offline case. Read first, ask second, and the lost reply this request id exists to
        // return cannot be returned at the one moment it is wanted: the platform is unreachable —
        // exactly as unreachable as it was when the reply went missing — so the read throws and the
        // agent is told TRADING_CONNECTION_MISSING about a sweep that is finished and answered in
        // the database. Handed over as a delegate, the lookup happens first and the capture never
        // runs on a replay, so the stored answer comes back with nothing asked of the platform. The
        // verb/session binding in `ReplayOf` is then the one gate a replay has to pass.
        //
        // AND THE ID IS OWNED FOR AS LONG AS THIS HANDLER RUNS IT. A second call on the same id while
        // this one is still in flight is not a crash to resume from: without the lease it re-ran the
        // plan, read the legs in the DISPATCHING state they were in at that instant, and wrote that
        // transient answer down first (Codex F18). Disposed however this ends, including a throw.
        var composite = await gateway.BeginCompositeAsync(ctx, rid, Core.Ops.CancelAll,
            async token => (await gateway.OrdersAsync(false, token)).Select(o => o.ConnectorOrderId).ToList(),
            () => FreshSweepNonce("cancelall"), ct);
        using var owner = composite.Owner;
        if (composite.StoredResultJson is { } answered) return Json.Read<JsonElement>(answered);
        var nonce = composite.Nonce;
        // AWAITED RATHER THAN HANDED OVER, and that is not a style choice. `RunLegs` takes the WIDER
        // contract — a leg that may produce no record at all, because `close-all` has that case and
        // `cancel-all` does not — and `Task<T>` is invariant, so passing `CancelAsync`'s
        // `Task<ExecutionRequest>` straight into a `Task<ExecutionRequest?>` parameter is a
        // nullability mismatch the compiler reports (CS8619) and nothing at runtime would catch.
        // Awaiting converts the VALUE instead of the task, which is the widening C# does allow: a
        // cancel leg always has a record, and a helper willing to accept none is satisfied by that.
        // The plan comes off the COMPOSITE, not off the book that was just read: a replay must act on
        // what the original call captured and on nothing that arrived after it.
        var legs = await RunLegs(composite.Targets, "cancelall", nonce,
            async (legId, target) => await gateway.CancelAsync(ctx, legId, target, ct));

        var results = legs.Where(l => l.Record is not null).Select(l => l.Record!).ToList();

        // COUNTED FROM THE WORD, NOT FROM THE BARE STATE, and the two can differ. `confirmed` is
        // CANCELLED *plus* a transport that is not `NothingWritten` — and since a leg the connector
        // proved it never sent is now settled CANCELLED rather than stranded UNKNOWN, reading the
        // state alone would report a cancellation that landed for an order the broker never heard
        // about. On the one command a person reaches for when they want everything to stop, that is
        // the worst possible lie, which is what this count was rewritten for in the first place.
        var landed = legs.Count(l => l.Outcome is LegOutcome.Confirmed);
        return Answer(rid, new
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
            not_cancelled = legs.Where(l => l.Record is not null && l.Outcome is not LegOutcome.Confirmed)
                .Select(l => new
                {
                    request_id = l.Record!.RequestId, order = l.Record.ConnectorOrderId,
                    state = l.Record.State.ToString(), outcome = Word(l.Outcome)
                }),
            not_sent = legs.Count(l => l.Outcome == LegOutcome.NotSent),
            outcomes = legs.Select(l => l.Describe()),
            requests = results
        });
    }

    /// <summary>
    /// Records what this composite request id answered, and answers it.
    ///
    /// Written AFTER the effects and only once: the reply the caller lost is still the reply this id
    /// has, and a later run may not rewrite it. Round-tripped through JSON so that the value stored
    /// and the value returned are the same bytes — a replay that handed back a differently shaped
    /// object would be a second answer wearing the first one's request id.
    /// </summary>
    JsonElement Answer(string rid, object data)
    {
        var json = Json.Write(data);
        gateway.CompleteComposite(rid, json);
        return Json.Read<JsonElement>(json);
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
    /// AND EVERY ARM CONSULTS THE TRANSPORT, INCLUDING (1). Round 10 shipped that half only for the
    /// unresolved states (Codex round-10 PRIOR R9-F4, PARTIAL). <see cref="TransportOutcome.NothingWritten"/>
    /// is a PROOF that this leg's frame never left the process, and a definite record state can have
    /// been produced by something other than this leg — the connector's event stream updates request
    /// records — so it is the one report allowed to overrule the record. See <see cref="TheAnswer"/>.
    ///
    /// NO CATCH-ALL. Every <see cref="ExecutionState"/> is named, and a new one must fail to compile
    /// or fail a test rather than quietly becoming the most dangerous word in the set — the same
    /// reason <c>Describe()</c> lost its own default arm (verifier round-9 F-3).
    /// </summary>
    static LegOutcome Classify(ExecutionState? state, TransportOutcome? transport) => state switch
    {
        ExecutionState.CANCELLED or ExecutionState.FILLED => TheAnswer(LegOutcome.Confirmed, transport),

        ExecutionState.REJECTED => TheAnswer(LegOutcome.Rejected, transport),

        ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED
            or ExecutionState.PARTIALLY_FILLED or ExecutionState.CANCEL_PENDING =>
            TheAnswer(LegOutcome.StillWorking, transport),

        // NOTHING OF THIS LEG WAS EVER DISPATCHED. `TradingGateway` writes the record before it
        // touches a connector and moves it to DISPATCHING immediately before the mutating call, so a
        // record that never got past these is proof that no mutating step of this leg was started.
        // An empty transport report is the truth about it and not an absence of one.
        null or ExecutionState.CREATED or ExecutionState.AWAITING_APPROVAL => BeforeTheWire(transport),

        // A MUTATING STEP OF THIS LEG WAS DISPATCHED, and these three states are the pipe server's
        // OWN evidence of it: DISPATCHING is written immediately before the connector call, and
        // UNKNOWN and RECONCILING are reachable only through it. See <see cref="Dispatched"/>.
        ExecutionState.DISPATCHING or ExecutionState.UNKNOWN or ExecutionState.RECONCILING =>
            Dispatched(transport),

        _ => throw new InvalidOperationException($"no leg outcome for execution state '{state}'")
    };

    /// <summary>
    /// A RECORD IN A STATE ONLY A BROKER'S ANSWER CAN PRODUCE IS THAT ANSWER — UNLESS THE CONNECTOR
    /// PROVED THIS LEG'S FRAME NEVER LEFT THE PROCESS.
    ///
    /// Round 10 gave the unresolved states a transport result and left these three arms reading the
    /// record alone (Codex round-10 PRIOR R9-F4, PARTIAL). But `confirmed`, `rejected` and
    /// `sent-still-working` are each a claim that this leg reached the broker, and a record can be in
    /// a definite state for a reason that has nothing to do with this leg: the connector's event
    /// stream updates request records, so a sweep leg can find one already settled by something else.
    /// "The record says CANCELLED" and "this leg cancelled it" are different sentences, and
    /// <see cref="TransportOutcome.NothingWritten"/> is the only report strong enough to tell them
    /// apart — it is a PROOF, not an inference from a failure.
    ///
    /// Everything else defers to the record, INCLUDING a null transport: an idempotent replay dispatches
    /// nothing and arrives here with a settled record and no transport of its own, and it is not
    /// `not-sent`.
    /// </summary>
    static LegOutcome TheAnswer(LegOutcome answer, TransportOutcome? transport) => transport switch
    {
        TransportOutcome.NothingWritten => LegOutcome.NotSent,
        null or TransportOutcome.PossiblyWritten or TransportOutcome.ReplyReceived => answer,
        _ => throw new InvalidOperationException($"no leg outcome for transport result '{transport}'")
    };

    /// <summary>
    /// NO MUTATING STEP OF THIS LEG WAS EVER STARTED, so an empty transport report is a FACT about
    /// it rather than a missing one, and <c>not-sent</c> is the honest word.
    ///
    /// The three shapes that arrive here are all real and all measured: a target resolution that
    /// failed before <c>TryCreate</c> (no record at all), a `close` leg parked as AWAITING_APPROVAL
    /// waiting on a person, and a `close-all` symbol with nothing left to close. None of them starts
    /// a mutating connector call — counted at the connector by the round-11 verifier, and two of the
    /// three leave the broker's own book unmoved. Flagging them would be verifier round-9's defect
    /// arrived at from the other side: <c>sent-not-confirmed</c> sets `needs_reconciliation`, which
    /// pauses ALL further execution, including the retry the message itself advises.
    ///
    /// A connector that can prove more still overrules the record in the usual direction, and an
    /// explicit report of a frame that went out still wins — a leg cannot reach a pre-dispatch state
    /// AND have written a byte, but the arm defers to the report rather than assuming it cannot.
    /// </summary>
    static LegOutcome BeforeTheWire(TransportOutcome? transport) => transport switch
    {
        null or TransportOutcome.NothingWritten => LegOutcome.NotSent,
        TransportOutcome.PossiblyWritten or TransportOutcome.ReplyReceived => LegOutcome.NotConfirmed,
        _ => throw new InvalidOperationException($"no leg outcome for transport result '{transport}'")
    };

    /// <summary>
    /// A MUTATING STEP WAS DISPATCHED AND NOTHING CAME BACK TO SAY WHERE IT GOT TO — so the word is
    /// the fail-closed one, on the pipe server's own knowledge rather than on the connector's.
    ///
    /// `not-sent` is the one word in the set that is an ASSURANCE, and until round 12 it depended on
    /// every connector calling <see cref="ConnectorSdk.TransportLedger.Attempt"/>. Both connectors in
    /// this tree do; `ITradingConnector` — what a third party implements — did not say so. Measured
    /// by the round-11 verifier with a connector written to the public interface that really cancels
    /// at the broker and never touches the ledger: <c>not-sent</c>, <c>attempted: 0</c>, for an order
    /// that had been cancelled (verifier round-11 F-2).
    ///
    /// The obligation is now STATED on the interface — but a contract a third party can still get
    /// wrong is not a guarantee, and this arm is the guarantee. `TradingGateway` writes DISPATCHING
    /// immediately before the connector's mutating call, and UNKNOWN and RECONCILING are reachable
    /// only through it, so a record in one of those states is this component's own proof that a
    /// mutating step of THIS leg was dispatched. That proof has the same standing as a broker's
    /// answer and is overruled by the same one report: <see cref="TransportOutcome.NothingWritten"/>,
    /// which is a connector PROVING the frame never left the process.
    ///
    /// <c>DISPATCHING</c> and <c>RECONCILING</c> reach <c>sent-not-confirmed</c> from here, and that
    /// is deliberate: the word is about the WIRE, and a frame that may have landed is exactly what it
    /// means. A record in one of them is not necessarily `UNKNOWN + needs_reconciliation`, so the leg
    /// carries the connector's own <c>transport</c> beside the word rather than the pipe server
    /// editing a row it does not own. (The shape this used to name — a mutation cancelled by disposal
    /// left `DISPATCHING` and unflagged — was `TradingGateway`'s half and is closed: every dispatch
    /// path catches a cancellation and settles UNKNOWN before disposal can take the store away.
    /// `A_cancelled_handler_settles_before_disposal_returns_even_with_no_write_back_margin` is the
    /// measurement. `DISPATCHING` is still reachable here — a handler that outlasts the drain and
    /// never unwinds leaves one, and that is the `handlers_did_not_finish` case.)
    /// </summary>
    static LegOutcome Dispatched(TransportOutcome? transport) => TheAnswer(LegOutcome.NotConfirmed, transport);

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

        public object Describe() => new LegAnswer(
            RequestId, Target, Word(Outcome), Record?.State.ToString(),
            Transport?.ToString(), Error ?? Record?.LastError);
    }

    /// <summary>
    /// One leg, as the agent reads it.
    ///
    /// WHY THIS IS A NAMED TYPE AND NOT AN ANONYMOUS ONE: <c>transport</c> has to be emitted even
    /// when it is null, and `Json.Options` ignores nulls by default. The field is THE EVIDENCE FOR
    /// THE CLAIM BESIDE IT — a leg refused before the wire is `not-sent` while
    /// `TradingGateway.SettleUnknown` still writes UNKNOWN on its row, and that row is the gateway's
    /// and not this unit's to change — and it was being dropped from the answer in exactly the case
    /// where the reader most needs it: an absent field is the shape of a leg whose word rests on
    /// this component's own knowledge rather than on a connector's report, and a reader could not
    /// tell that from a leg whose connector reported nothing at all (verifier round-11 F-2).
    ///
    /// <c>state</c> keeps the old behaviour deliberately. Its absence is a FACT with its own
    /// meaning — no record was ever written for this leg — and one the suite reads as such.
    /// </summary>
    /// <param name="Transport">
    /// Where this leg's frame got to, as the CONNECTOR reported it, or null when it reported nothing.
    /// Null does not mean nothing was sent: see <see cref="Dispatched"/>.
    /// </param>
    sealed record LegAnswer(
        [property: JsonPropertyName("request_id")] string RequestId,
        [property: JsonPropertyName("order")] string Order,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("transport"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        string? Transport,
        [property: JsonPropertyName("error")] string? Error);

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
        // See CancelAll: the composite is persisted before any effect, a replayed id returns the
        // answer it already gave rather than closing whatever is open now, and the position read is
        // inside the delegate so that answer is still available with the platform unreachable.
        // The owner lease is CancelAll's too: a duplicate waits for this run's answer instead of
        // resuming a sweep that never stopped.
        var composite = await gateway.BeginCompositeAsync(ctx, rid, Core.Ops.CloseAll,
            async token => (await gateway.PositionsAsync(token)).Where(p => p.Quantity != 0).Select(p => p.Symbol).ToList(),
            () => FreshSweepNonce("closeall"), ct);
        using var owner = composite.Owner;
        if (composite.StoredResultJson is { } answered) return Json.Read<JsonElement>(answered);

        var legs = await RunLegs(composite.Targets, "closeall", composite.Nonce,
            (legId, symbol) => gateway.CloseAsync(ctx, legId, symbol, ct));

        // Null from the gateway means it found nothing to close for that symbol. Not a failure, and
        // not a closure either — counting it as one is exactly the overstatement bdf9a24 removed.
        var nothingToDo = legs.Where(l => l.NoTargetFound).Select(l => l.Target).ToList();
        var results = legs.Where(l => l.Record is not null).Select(l => l.Record!).ToList();

        // COUNTED FROM THE WORD AS WELL AS THE RECORD, which `cancelled` has done since U2c1c and
        // this had not (REVIEW 2026-09-05, finding 9). A leg the connector PROVED it never sent is
        // settled CANCELLED rather than stranded UNKNOWN, so the record alone reported a closing
        // order the platform had cancelled — for a position that is still open and that nothing was
        // ever sent about.
        //
        // AND NOT FROM THE WORD ALONE, because the two sweeps are not symmetrical. `confirmed` is
        // "this leg's own intent is done", and `Classify` reads that off a CANCELLED *or* FILLED
        // record: for a cancel leg both mean the order is not working any more, but a CLOSE leg's
        // intent is a filled offsetting order, and a closing order that was itself cancelled has
        // flattened nothing. Requiring both is the reading that cannot over-claim in either
        // direction — the transport has to agree, and the position has to have actually closed.
        bool Landed(Leg l) => l.Outcome is LegOutcome.Confirmed && l.Record?.State is ExecutionState.FILLED;

        var landed = legs.Count(Landed);
        return Answer(rid, new
        {
            closed = landed,
            nothing_to_do = legs.Count == 0,
            // Same rule as `cancel-all`, and it CHANGES this number: a symbol with nothing to close
            // was being counted as attempted. It is already reported, by name, in
            // `nothing_to_close` — counting it here as well is the same over-claim bdf9a24 removed
            // from `cancelled`, one field over.
            attempted = legs.Count(l => l.Attempted),
            nothing_to_close = nothingToDo,
            // THE SAME SHAPE `not_cancelled` HAS, word included. It carried a state and nothing
            // else, so the one entry an agent most needs to read — a leg that never reached the
            // wire — arrived as `state: CANCELLED`, which reads as the platform having cancelled
            // the closing order. The word is what says which of those two it was, and the reader
            // should not have to cross-reference `outcomes` by request id to find it.
            not_closed = legs.Where(l => l.Record is not null && !Landed(l))
                .Select(l => new
                {
                    request_id = l.Record!.RequestId, instrument = l.Record.Instrument,
                    state = l.Record.State.ToString(), outcome = Word(l.Outcome)
                }),
            not_sent = legs.Count(l => l.Outcome == LegOutcome.NotSent),
            outcomes = legs.Select(l => l.Describe()),
            requests = results
        });
    }

    /// <summary>
    /// What the trading made or lost, and everything the figure does not know.
    ///
    /// THE SHAPE OF THE ANSWER IS THE CONTRACT: a field that is null is an UNKNOWN. `net` is null
    /// whenever a fill in the window carries no fee, because a net figure computed as though an
    /// unreported fee were zero is wrong in the owner's favour every time — and the loss budget this
    /// ruler is being built for would be enforced against it. `incomplete` is where every such gap is
    /// named in words a person can act on. An empty `incomplete` is a claim, and it is only made when
    /// nothing was missing.
    /// </summary>
    async Task<object> PnlFor(IpcRequest req, CancellationToken ct)
    {
        var all = NamedFlag(req, "all", whenAbsent: false);
        var sinceArg = req.Args is not null && req.Args.ContainsKey("since") ? req.Str("since") : null;

        if (all && sinceArg is not null)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'all' and 'since' ask for two different periods — send one of them");

        DateTimeOffset? since;
        string window;
        if (all) { since = null; window = "all"; }
        else if (sinceArg is not null)
        {
            // PRESENT AND UNREADABLE IS A REFUSAL, never a fall-through to today — the same rule as
            // a price. A window that quietly became a different window is a different answer.
            if (!DateTimeOffset.TryParse(sinceArg, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                    $"'since' is not a date this build can read: {sinceArg}. Send an ISO-8601 instant or date, " +
                    "for example 2026-09-06 or 2026-09-06T13:00:00Z. Reading it as today would answer a " +
                    "different question from the one you asked.");
            since = parsed;
            window = "since";
        }
        else { since = TradingGateway.StartOfDay(DateTimeOffset.UtcNow); window = "today"; }

        var r = await gateway.PnlAsync(since, window, ct);
        return new PnlReply(
            r.Window, r.Since, r.AsOf,
            "days are UTC calendar days. Every figure here is the OPERATING PAIR's — the account in " +
            "'account' on the platform in 'connector', and no other account's and no other " +
            "platform's fills are in it. A null figure is an UNKNOWN and never a zero: 'net' is " +
            "withheld when any fill in the period has no fee, and 'unrealized' when an open " +
            "position has no price or no contract size. 'incomplete' names every gap, " +
            "'unattributed_fills' among them.",
            r.Realized, r.Fees, r.FeesUnknownFills, r.Net, r.Unrealized,
            r.MaxDrawdown, r.DrawdownIncludesUnrealized, r.Fills, r.FirstFillAt, r.CoverageFrom,
            [.. r.BySymbol.Select(s => new PnlReplySymbol(s.Symbol, s.Realized, s.Fees, s.FeesUnknownFills,
                s.Unrealized, s.OpenQuantity, s.LastPrice, s.LastPriceAt, s.Multiplier, s.MultiplierKnown, s.Fills))],
            [.. r.ByDay.Select(d => new PnlReplyDay(d.Day, d.Realized, d.Fees, d.FeesUnknownFills, d.Fills))],
            r.Incomplete)
        {
            Connector = r.Connector,
            Account = r.Account,
            UnattributedFills = r.UnattributedFills
        };
    }

    /// <summary>
    /// A NULL FIELD HAS TO ARRIVE AS <c>null</c>, WHICH IS WHY THIS IS A DECLARED TYPE.
    ///
    /// <c>Json.Options</c> carries <c>DefaultIgnoreCondition = WhenWritingNull</c>, so an anonymous
    /// object's null field is not serialized at all — and an ABSENT <c>net</c> is worse than a wrong
    /// one: it reads as a field this build does not have, so a reader that asks for it gets a missing
    /// key rather than the answer "TradeAgent cannot compute this". Measured over the real pipe
    /// before this changed: a report with two unpriced fees came back with no <c>net</c> and no
    /// <c>fees</c> key at all. <see cref="JsonIgnoreCondition.Never"/> on each nullable money field is
    /// what overrides that default; the global option is left alone because every other payload on
    /// this channel wants it.
    /// </summary>
    sealed record PnlReply(
        string Window,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? Since,
        DateTimeOffset AsOf,
        string Note,
        decimal Realized,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Fees,
        int FeesUnknownFills,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Net,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Unrealized,
        decimal MaxDrawdown,
        bool DrawdownIncludesUnrealized,
        int Fills,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FirstFillAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CoverageFrom,
        IReadOnlyList<PnlReplySymbol> BySymbol,
        IReadOnlyList<PnlReplyDay> ByDay,
        IReadOnlyList<string> Incomplete)
    {
        /// <summary>
        /// WHOSE MONEY THIS IS — the platform and the account every figure above was computed over
        /// (<c>U-scope-identity</c>). The agent plans against this figure and the loss budget refuses
        /// on it, and until this they were a sum over every fill in the database whatever account or
        /// platform put it there (REVIEW 2026-09-16, finding 2).
        /// </summary>
        public string Connector { get; init; } = "";

        public string Account { get; init; } = "";

        /// <summary>
        /// This account's fills that name no platform — written before schema 22, counted here and
        /// in NONE of the figures above. `incomplete` says the same thing in words.
        /// </summary>
        public int UnattributedFills { get; init; }
    }

    /// <inheritdoc cref="PnlReply"/>
    sealed record PnlReplySymbol(
        string Symbol,
        decimal Realized,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Fees,
        int FeesUnknownFills,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Unrealized,
        decimal OpenQuantity,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? LastPrice,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastPriceAt,
        decimal Multiplier,
        bool MultiplierKnown,
        int Fills);

    /// <inheritdoc cref="PnlReply"/>
    sealed record PnlReplyDay(
        string Day,
        decimal Realized,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Fees,
        int FeesUnknownFills,
        int Fills);

    /// <summary>
    /// THE OWNER'S DAILY REPORT, AS THE OWNER READS IT.
    ///
    /// <para>A READ, and only a read. The document is composed by the app from tables it wrote; there
    /// is no op here that writes, rewrites or deletes one, which is the same asymmetry
    /// <c>material</c> and <c>data-list</c> make and for a sharper reason: this is the record the AI's
    /// own work is judged by, and an agent that could edit it could report a day it did not have.</para>
    ///
    /// <para>NO CONNECTOR CALL, which is why this handler's row in the table above is
    /// <see cref="TimeSpan.Zero"/>. The price is real and is in the answer rather than hidden: open
    /// positions are not valued in a report written from the ledger alone, and <c>missing</c> names
    /// that where the figure would have stood. <c>pnl</c> is the op that asks the platform.</para>
    /// </summary>
    object ReportFor(IpcRequest req)
    {
        var dayArg = req.Args is not null && req.Args.ContainsKey("day") ? req.Str("day") : null;
        DateTimeOffset at;

        if (dayArg is null) at = DateTimeOffset.Now;
        else
        {
            // PRESENT AND UNREADABLE IS A REFUSAL, the same rule `pnl --since` follows: a day that
            // quietly became today is a different day's report from the one that was asked for.
            if (!DateTime.TryParseExact(dayArg, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                    $"'day' is not a date this build can read: {dayArg}. Send a local calendar day as " +
                    "yyyy-MM-dd, for example 2026-09-08. Reading it as today would answer a different " +
                    "question from the one you asked.");
            at = new DateTimeOffset(parsed.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(parsed));
        }

        var r = gateway.Reports.Compose(at);
        var draft = DailyReportText.Draft(r);

        return new ReportReply(
            r.Identity.Day, r.Identity.SnapshotAt, r.Identity.Timezone, r.Identity.From, r.Identity.To,
            r.Identity.AppVersion, r.Identity.SchemaVersion,
            "a null figure is an UNKNOWN and never a zero. 'net' is withheld whenever any fill in the "
            + "day has no fee; 'unrealized' and 'exposure' are absent because this report asks your "
            + "platform nothing — use 'pnl' for those. 'missing' names every gap in the owner's own "
            + "words, and 'text' is the document they read, verbatim. Nothing here is inferred and no "
            + "AI turn produced it. You cannot write it: there is no operation that does.",
            new ReportReplyMission(r.Mission.State, r.Mission.Reason, r.Mission.NextEligibleWake),
            new ReportReplyReadiness(r.Readiness.Connector, r.Readiness.ConnectorIsPaper,
                r.Readiness.Account, r.Readiness.Mode, r.Readiness.ExecutionAvailable,
                r.Readiness.ExecutionBlockedReason),
            new ReportReplyPerformance(r.Performance.Realized, r.Performance.Fees,
                r.Performance.FeesUnknownFills, r.Performance.Net, r.Performance.Unrealized,
                r.Performance.Exposure, r.Performance.Drawdown, r.Performance.Fills,
                r.Performance.LossBudgetDay, r.Performance.Currency),
            new ReportReplyExecution(r.Execution.OrdersToday, r.Execution.FillsToday,
                r.Execution.RejectionsToday, r.Execution.OpenRequests, r.Execution.Unreconciled,
                [.. r.Execution.Unknown.Select(u => new ReportReplyUnknown(u.RequestId, u.Instrument,
                    u.Since, (int)u.Age.TotalSeconds))]),
            new ReportReplySpending(r.Spending.Spent, r.Spending.Reserved, r.Spending.Cap,
                r.Spending.Turns, r.Spending.UnpricedTurns, r.Spending.Currency, r.Spending.Runtime,
                r.Spending.Basis,
                [.. r.Spending.Roles.Select(x => new ReportReplyRole(x.Role, x.Model, x.Turns, x.Spent,
                    x.Reserved, x.Cap, x.Estimated))]),
            [.. r.Decisions.OwnerMessages.Select(m => new ReportReplyMessage(m.Text, m.ReceivedAt,
                m.Disposition, m.DispositionDetail, m.DueBy, m.Overdue))],
            [.. r.AllGaps.Select(g => new ReportReplyGap(g.Field, g.Why))],
            draft.Text, draft.Lines, draft.Rejected);
    }

    /// <summary>
    /// A NULL FIELD HAS TO ARRIVE AS <c>null</c>, WHICH IS WHY THIS IS A DECLARED TYPE — the reason
    /// <see cref="PnlReply"/> is one, and it matters more here: this document's whole subject is what
    /// the app could not measure. <c>Json.Options</c> drops a null field of an anonymous object
    /// entirely, so an absent <c>net</c> would read as a field this build does not have rather than as
    /// "TradeAgent cannot compute this", and the one figure a profitability claim rests on would
    /// disappear exactly when it was withheld on purpose.
    /// </summary>
    sealed record ReportReply(
        string Day,
        DateTimeOffset SnapshotAt,
        string Timezone,
        DateTimeOffset From,
        DateTimeOffset To,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AppVersion,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SchemaVersion,
        string Note,
        ReportReplyMission Mission,
        ReportReplyReadiness Readiness,
        ReportReplyPerformance Performance,
        ReportReplyExecution Execution,
        ReportReplySpending Spending,
        IReadOnlyList<ReportReplyMessage> OwnerMessages,
        IReadOnlyList<ReportReplyGap> Missing,
        string Text,
        int Lines,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Rejected);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplyMission(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? NextEligibleWake);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplyReadiness(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Connector,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? ConnectorIsPaper,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Account,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Mode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? ExecutionAvailable,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExecutionBlockedReason);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplyPerformance(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Realized,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Fees,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? FeesUnknownFills,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Net,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Unrealized,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Exposure,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Drawdown,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Fills,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? LossBudgetDay,
        string Currency);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplyExecution(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? OrdersToday,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? FillsToday,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RejectionsToday,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? OpenRequests,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Unreconciled,
        IReadOnlyList<ReportReplyUnknown> Unknown);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplyUnknown(string RequestId, string Instrument, DateTimeOffset Since, int AgeSeconds);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplySpending(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Spent,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Reserved,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Cap,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Turns,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? UnpricedTurns,
        string Currency,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Runtime,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Basis,
        IReadOnlyList<ReportReplyRole> Roles);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplyRole(
        string Role,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Model,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Turns,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Spent,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Reserved,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Cap,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Estimated);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplyMessage(
        string Text,
        DateTimeOffset ReceivedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Disposition,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DispositionDetail,
        DateTimeOffset DueBy,
        bool Overdue);

    /// <inheritdoc cref="ReportReply"/>
    sealed record ReportReplyGap(string Field, string Why);

    /// <summary>
    /// A BACKTEST THE APP RUNS AND RECORDS — a READ as far as trading is concerned.
    ///
    /// <para>No order, no mode check beyond what every read here does, no connector call, nothing that
    /// grants or removes authority. <paramref name="ctx"/> is the whole of who is asking — the role and
    /// the attempt off the launch grant — and the run is read from that role's folder and recorded under
    /// it. The app reads the program from inside the caller's own role folder,
    /// parses it (a refusal names the line), declares the execution model from what was asked for,
    /// streams the dataset's own hashed bars, computes the metrics from its own trace and writes the
    /// version and the run. What comes back is the run's id and those metrics.</para>
    ///
    /// <para><paramref name="ct"/> is the server's lifetime, and it is passed INTO the run: a shutdown
    /// halts it at the bar it has reached — a defined outcome — instead of holding the drain open for
    /// a window nobody is waiting for any more. A run the app stopped is not recorded.</para>
    /// </summary>
    object BacktestFor(AgentContext ctx, IpcRequest req, CancellationToken ct)
    {
        var ran = gateway.Backtests.Run(ctx, new BacktestAsk(
            Require(req, "strategy"),
            DatasetId(req),
            BarInstant(req, "from"),
            BarInstant(req, "to"),
            req.Dec("fees"),
            req.Dec("slippage"),
            req.Dec("increment"),
            req.Dec("capital"),
            // DECLARED BY THE SUBMITTER, never worked out by the app. It grants nothing: see
            // `BacktestAsk.Parent`.
            req.Str("parent"),
            // THE LEDGER ENTRY IT IS ASKED UNDER, checked by the runner before anything runs (U-research-ledger).
            LedgerId(req, "entry")), ct);

        var result = ran.Result;
        var m = result.Metrics;

        return new BacktestReply(
            result.RunId, result.VersionId, ran.Role, ran.Program.Instrument, ran.Program.WarmUpBars,
            result.Request.DatasetId, ran.Dataset.Pair, ran.Dataset.Interval, ran.Dataset.Version,
            result.Request.DatasetSha256, result.Request.From, result.Request.To,
            result.Request.Model.Canonical, ran.IncrementSource, ran.FrictionSource, result.Outcome.ToString(),
            result.FaultReason,
            "This is a measurement over BARS, and bars establish no actual fill, no queue position and "
            + "no intrabar ordering: what a run says is a reason to test something and never a record "
            + "of a trade. Fills are modelled at the next bar's open plus slippage and a fee is charged "
            + "on every fill — each the number you declared or, where you declared none, TradeAgent's "
            + "venue cost model's for this dataset's venue — a bar that touched both the stop and the "
            + "target counts as the stop, and a size is rounded DOWN to the increment. Read the nulls "
            + "and 'missing': a null is an UNKNOWN and never a zero. 'net_pnl' covers CLOSED trades only "
            + "— a position still open at the last bar is in the equity the drawdown is measured on, not "
            + "in it. "
            // U-run-trace: the answer lists the first twenty and counts the rest, and says where the rest are.
            + $"'trades' lists the first {Backtests.TradesShown} closed trades and 'trade_count' counts every one: "
            + "'trade run trades --run <run_id>' serves them all, in pages, to every role, and runs nothing again. "
            // U-paper-friction: an undeclared fee is no longer zero, so the answer no longer says that
            // leaving it out runs frictionless. It says what the run WAS charged, and who chose it.
            + (result.Request.Model.Frictionful
                ? "This run's fee and slippage are in 'execution_model', and who chose each in 'friction_source'."
                : "THIS RUN WAS CHARGED NO FEE AND NO SLIPPAGE — 'friction_source' says why — so it is an "
                  + "upper bound on a frictionless market and not a figure about any venue's costs.")
            // AND WHAT THESE BARS ARE. A run over midpoint-derived candles that reported like a run
            // over traded bars would be handing back a figure the caller cannot read correctly —
            // `docs/COUNCIL.md`:164-172, never trade evidence. The same sentence `data-list`,
            // `data-bars` and the owner's report carry, from the one place it is written.
            + (ran.Dataset.MidpointNote is { } midpoint ? " " + midpoint : "")
            // AND WHAT A FEATURE IS, where the program reads one (U-language-v2a): read off the tape as it had arrived
            // by each bar's close, absent rather than guessed, and research-only today.
            + (result.Features.Count == 0
                ? ""
                : " Every feature in 'features' was read from the market-context tape as it had arrived by the close of "
                  + "each bar the program was asked on; a bar where one had no value decided nothing, and 'bars_with_no_value' "
                  + "counts them. Bars before a feature's clean-history start were evaluated, and their values may stand "
                  + "on archive readings rather than first-hand ones; where 'clean_history_bounded' is set, a holdout "
                  + "window before the run kept the search for that start from reading further back, so it is the start "
                  + "since that window's close, not necessarily the input's own. Every tape source is research-only "
                  + "today, so no capital may stand on a version that reads one."),
            new BacktestReplyMetrics(
                m.Bars, m.ExposureBars, m.Signals, m.Fills, m.NoTrades, m.Trades, m.Wins, m.WinRate,
                m.GrossPnl, m.Fees, m.NetPnl, m.MaxDrawdown, m.FinalEquity, m.MissingMinutes, m.Gaps,
                m.Faults, m.PositionOpenAtEnd),
            [.. m.Missing.Select(g => new BacktestReplyGap(g.Field, g.Why))],
            result.Trades.Count,
            [.. result.Trades.Take(Backtests.TradesShown).Select(t => new BacktestReplyTrade(
                t.Ordinal, t.EntryBar, t.EntryPrice, t.ExitBar, t.ExitPrice, t.Quantity,
                t.Reason.ToString(), t.Fees, t.Pnl))],
            result.Trace.Sha256,
            [.. result.Features.Select(f => new BacktestReplyFeature(
                f.Name, f.Id, f.AsOf, f.CleanHistoryStart, f.NoCleanHistory, f.CleanHistoryBounded, f.BarsBefore,
                f.BarsAbsent, f.WorstClass))]);
    }

    /// <summary>
    /// A DATASET'S LEDGER ID, WHICH IS A WHOLE NUMBER AND NOT A PRICE. Present and unreadable is a
    /// refusal, the rule every other numeric field on this wire follows.
    /// </summary>
    static long DatasetId(IpcRequest req)
    {
        var raw = req.Dec("dataset")
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'dataset' is required: the ledger id of the data to run over. 'trade data list' has them.");

        if (raw != decimal.Truncate(raw) || raw <= 0m)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'dataset' is a ledger id — a whole number above 0 — and '{raw}' is not one. "
                + "'trade data list' has them.");

        return (long)raw;
    }

    /// <inheritdoc cref="BacktestFor"/>
    sealed record BacktestReply(
        string RunId, string VersionId, string Role, string Instrument, int WarmUpBars,
        long DatasetId, string Pair, string Interval, string DatasetVersion, string DatasetSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? From,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? To,
        string ExecutionModel,
        // WHO CHOSE THE INCREMENT. `execution_model` says WHAT was used; this says whether the number
        // was yours or was read out of the venue catalogue, and from which row. Never dropped when
        // null: a run recorded before this build had the field is not a run that declared its own.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? IncrementSource,
        // AND WHO CHOSE THE FEE AND THE SLIPPAGE, number by number: yours, or TradeAgent's venue cost
        // model for the dataset's venue, named by id and sha (U-paper-friction). Kept when null for
        // the reason the increment's is.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FrictionSource,
        string Outcome,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FaultReason,
        string Note, BacktestReplyMetrics Metrics, IReadOnlyList<BacktestReplyGap> Missing,
        int TradeCount, IReadOnlyList<BacktestReplyTrade> Trades, string TraceSha256,
        // WHAT EACH FEATURE THE PROGRAM READS CAME TO — the trace's `Feature` lines, under its hash — or empty for a
        // program that reads none (U-language-v2a).
        IReadOnlyList<BacktestReplyFeature> Features);

    /// <inheritdoc cref="BacktestReplyMetrics"/>
    sealed record BacktestReplyFeature(
        string Name, string Id, DateTimeOffset AsOf,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CleanHistoryStart,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NoCleanHistory,
        // WHY THE START IS NOT NECESSARILY THE INPUT'S OWN, where a holdout window before the run bounded the search for
        // it (U-tape-holdout's FeatureCleanStart.Bounded), or null when it is.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CleanHistoryBounded,
        long BarsBeforeCleanHistory, long BarsWithNoValue,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? WorstClass);

    /// <summary>
    /// DECLARED TYPES AND NEVER AN ANONYMOUS OBJECT, for the reason <see cref="PnlReply"/> is one:
    /// <c>Json.Options</c> drops a null field, and an absent <c>win_rate</c> would read as a field this
    /// build does not have rather than as a figure no trade was closed to compute.
    /// </summary>
    sealed record BacktestReplyMetrics(
        long Bars, long ExposureBars, long Signals, int Fills, int NoTrades, int Trades, int Wins,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? WinRate,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? GrossPnl,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Fees,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? NetPnl,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? MaxDrawdown,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? FinalEquity,
        long MissingMinutes, int Gaps, int Faults, bool PositionOpenAtEnd);

    /// <inheritdoc cref="BacktestReplyMetrics"/>
    sealed record BacktestReplyGap(string Field, string Why);

    /// <inheritdoc cref="BacktestReplyMetrics"/>
    sealed record BacktestReplyTrade(
        int Ordinal, DateTimeOffset EntryBar, decimal EntryPrice, DateTimeOffset ExitBar,
        decimal ExitPrice, decimal Quantity, string Reason, decimal Fees, decimal Pnl);

    /// <summary>
    /// THE MOST ONE <c>run-trades</c> ANSWER'S TRADES TAKE ON THE WIRE: 256 KiB, each trade measured as it is written
    /// (<see cref="RunTradeBytes"/>). A page of <see cref="StrategyStore.MaxTradeRows"/> trades of the observed run's shape
    /// — hourly BTCUSDT, an RSI mean reversion, the venue model's fee and slippage — measures under it
    /// (<c>RunTradesTests</c>), so the row limit is what stops an ordinary page, and this stops one whose figures carry
    /// longer decimals than any of those: never a page silently.
    /// </summary>
    public const long MaxRunTradesReplyBytes = 256L * 1024;

    /// <summary>
    /// EVERY CLOSED TRADE OF A RECORDED RUN, IN BOUNDED PAGES, FOR EVERY ROLE (<c>U-run-trace</c>) — the arrow from a run's
    /// figures back to the trades they were computed from, which the observed run's Research Director asked for and could
    /// not have: a <c>backtest</c> answer lists its first <see cref="Backtests.TradesShown"/>, and after it nothing did.
    ///
    /// <para><b>Any caller, any research run.</b> A role reads the other director's runs as readily as its own — reviewing
    /// them is the case that asked — and a connection that proved no role reads them too: reading a run grants nothing,
    /// and nothing here is run again, so nothing is charged.</para>
    ///
    /// <para><b>Nothing held back crosses, and the reader decides it.</b> The caller's own pipe audience goes into
    /// <see cref="StrategyStore.ReadTrades"/> with the dataset ledger (<see cref="TapeHoldout.Pipe"/>), and the reader
    /// refuses inside — <c>HOLDOUT_WITHHELD</c>, with no row and no trade — the referee's holdout run, and a run whose bars
    /// or feature reads a holdout window reaches now. A name that matches no one run is <c>INVALID_REQUEST</c>, naming what
    /// was asked.</para>
    ///
    /// <para><b>Bounded, and never silently.</b> At most <see cref="StrategyStore.MaxTradeRows"/> trades — a larger limit is
    /// refused in words, never clamped — and <see cref="MaxRunTradesReplyBytes"/> of them; an answer either bound stopped
    /// says which, and hands back the ordinal that continues it exactly.</para>
    /// </summary>
    object RunTrades(AgentContext ctx, IpcRequest req)
    {
        var run = (req.Str("run") ?? "").Trim();
        if (run.Length == 0)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'run' is required: the run's id as 'backtest' answered it, or its first "
                + $"{StrategyStore.ShortestRunId} characters or more, as 'trade report' prints it.");

        var after = RunTradesAfter(req);
        var limit = RunTradesLimit(req);

        // THE HOLDOUT IS THE CALLER'S OWN PIPE AUDIENCE WITH THE DATASET LEDGER, decided inside the reader at this read: the
        // referee's holdout run, and a run whose bars or feature reads a holdout window reaches now, come back refused with
        // no row and no trade, whoever asks.
        var page = gateway.Strategies.ReadTrades(run, TapeHoldout.Pipe(ctx.Role, gateway.Datasets), after ?? -1, limit,
            MaxRunTradesReplyBytes, RunTradeBytes);

        if (page.Refusal is { } why)
            throw new GatewayDeniedException(page.Withheld ? ErrorCode.HOLDOUT_WITHHELD : ErrorCode.INVALID_REQUEST, why);

        var r = page.Run!;
        var trades = page.Trades.Select(TradeRow).ToList();
        return new RunTradesReply(
            r.Id, r.VersionId, r.Role, r.Attempt, r.CreatedAt, r.DatasetId, r.DatasetSha256, r.WindowFrom, r.WindowTo,
            r.ExecutionModel, r.IncrementSource, r.FrictionSource, r.Outcome, r.FaultReason,
            new RunTradesFigures(r.Bars, r.Intents, r.Fills, r.Trades, r.Wins, r.ExposureBars, r.MissingMinutes, r.Faults,
                r.GrossPnl, r.Fees, r.NetPnl, r.MaxDrawdown),
            r.TraceSha256, page.TradeCount, after, limit, trades.Count, page.More, page.CappedBy,
            page.More ? trades[^1].Ordinal : null, RunTradesNote, trades);
    }

    /// <summary>
    /// WHAT ONE TRADE TAKES ON THE WIRE, written exactly as <c>run-trades</c> writes it: the cost
    /// <see cref="StrategyStore.ReadTrades"/> bounds a page by.
    /// </summary>
    public static long RunTradeBytes(StrategyTradeRow trade) => Encoding.UTF8.GetByteCount(Json.Write(TradeRow(trade)));

    /// <summary>A recorded trade in the shape a <c>backtest</c> answer lists one, so the two answers read alike.</summary>
    static BacktestReplyTrade TradeRow(StrategyTradeRow t) =>
        new(t.Ordinal, t.EntryBar, t.EntryPrice, t.ExitBar, t.ExitPrice, t.Quantity, t.ExitReason, t.Fees, t.Pnl);

    /// <summary>
    /// What the answer says about itself, once, in words: what a row is and what none is, what the figures and the hash
    /// are, the bounds, and how to continue.
    /// </summary>
    static readonly string RunTradesNote =
        "TRADES — every closed trade TradeAgent recorded from its own run, in ordinal order from 0, beside the run's row "
        + "as recorded. A row is a trade the run CLOSED: a position still open at the run's last bar is not one — it is in "
        + "the equity the drawdown was measured on — and no row is a record of a fill: bars establish no fill, no queue "
        + "position and no intrabar ordering, so a run is a reason to test something and never a record of a trade. "
        + "'entry_bar' and 'exit_bar' are the minutes the entry and the exit filled on; 'pnl' is GROSS — (exit_price − "
        + "entry_price) × quantity — and 'fees' is what both of its fills cost, so its net is pnl − fees; 'reason' is why it "
        + "closed: Rule (a declared exit), Stop, Target, SessionExit or MaxHoldBars. 'figures' are the run's own, as it "
        + "recorded them and named as a 'backtest' answer names them; 'trade_count' counts every closed trade it recorded; "
        + "'trace_sha256' is the hash of its bar-by-bar trace, which is not kept. At most "
        + $"{StrategyStore.MaxTradeRows.ToString("N0", CultureInfo.InvariantCulture)} trades and "
        + $"{MaxRunTradesReplyBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes of them a call: when 'more' is true "
        + "the answer stopped there — 'capped_by' says which bound — and asking again with 'after' set to 'next_after' "
        + "continues exactly where it stopped. Nothing was run again and nothing was charged, and nothing on this channel "
        + "edits or deletes a run or a trade.";

    /// <summary>
    /// HOW MANY TRADES ONE ANSWER MAY HOLD: absent is <see cref="StrategyStore.MaxTradeRows"/>; present, a whole number from
    /// 1 to it, and anything else is REFUSED in words — never clamped, because an answer to a different limit is a
    /// different answer from the one asked for.
    /// </summary>
    static int RunTradesLimit(IpcRequest req)
    {
        if (req.Args is null || !req.Args.ContainsKey("limit")) return StrategyStore.MaxTradeRows;

        var raw = req.Dec("limit")!.Value;
        if (raw != decimal.Truncate(raw) || raw < 1m || raw > StrategyStore.MaxTradeRows)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'limit' is how many trades one answer may hold — a whole number from 1 to "
                + $"{StrategyStore.MaxTradeRows.ToString(CultureInfo.InvariantCulture)} — and "
                + $"'{raw.ToString(CultureInfo.InvariantCulture)}' is not one. Ask for fewer and continue with 'after', which "
                + "every answer that stopped early hands back as 'next_after'.");

        return (int)raw;
    }

    /// <summary>
    /// THE ORDINAL A PAGE STARTS AFTER, or null for the first trade: a whole number from 0 — the 'next_after' of an answer
    /// that stopped early — or a refusal.
    /// </summary>
    static long? RunTradesAfter(IpcRequest req)
    {
        if (req.Args is null || !req.Args.ContainsKey("after")) return null;

        var raw = req.Dec("after")!.Value;
        if (raw != decimal.Truncate(raw) || raw < 0m || raw > int.MaxValue)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'after' is a trade's ordinal — a whole number from 0, the 'next_after' of an answer that stopped early — "
                + $"and '{raw.ToString(CultureInfo.InvariantCulture)}' is not one. Leave it out to start at the first trade.");

        return (long)raw;
    }

    /// <inheritdoc cref="RunTrades"/>
    sealed record RunTradesReply(
        string RunId, string VersionId,
        // NEVER DROPPED WHEN NULL: a run no role asked for, a window with an open side and a run that faulted on nothing
        // are answers, and an absent key would read as a build that has no such field.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Role,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Attempt,
        DateTimeOffset CreatedAt,
        long DatasetId, string DatasetSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? From,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? To,
        string ExecutionModel,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? IncrementSource,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FrictionSource,
        string Outcome,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FaultReason,
        RunTradesFigures Figures,
        string TraceSha256, int TradeCount,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? After,
        int Limit, int Count, bool More,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CappedBy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? NextAfter,
        string Note,
        IReadOnlyList<BacktestReplyTrade> Trades);

    /// <summary>
    /// THE RUN'S FIGURES AS IT RECORDED THEM, under the names a <c>backtest</c> answer's <c>metrics</c> gives them. A money
    /// figure is null for an UNKNOWN and never a zero, and it is never dropped.
    /// </summary>
    sealed record RunTradesFigures(
        long Bars, long Signals, int Fills, int Trades, int Wins, long ExposureBars, long MissingMinutes, long Faults,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? GrossPnl,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Fees,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? NetPnl,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? MaxDrawdown);

    /// <summary>Roles with a verdict in flight right now. See <see cref="VerdictFor"/>.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _judging = new(StringComparer.Ordinal);

    /// <summary>
    /// THE VERDICT REQUEST: THE AGENT ASKS, THE APP JUDGES, AND WHAT COMES BACK IS WORDS.
    ///
    /// <para><c>docs/PRINCIPLES.md</c> § Evidence — "Models may propose candidates for evaluation;
    /// software decides admission and issues the verdict within the existing evidence budget." This is
    /// the asking half, and every line of it is the deciding half's: the caller names a version and at
    /// most a dataset, and nothing else about the judgement is a parameter of the frame.</para>
    ///
    /// <para><b>Admission, and each refusal says which clause it is.</b> A KNOWN COUNCIL ROLE, by
    /// <see cref="Backtests.RoleOf"/> and the same refusal a backtest gets — a roleless connection is
    /// authenticated and is nobody, and the owner at the keyboard is not a council role. An OPEN
    /// CAMPAIGN for that dataset, because a campaign is what counts the attempts made against a
    /// holdout and there is nothing to bound a verdict without one; a dataset with no cutoff has none,
    /// and the refusal names the owner's own press rather than inventing a campaign. A COMPLETED RUN
    /// OF THIS VERSION BY THIS ROLE OVER THAT DATASET, with its trial registered: a version nobody
    /// measured buys no verdict, and a LOSING measurement buys one, because the clause is that the
    /// hypothesis was tested and not that it went well.</para>
    ///
    /// <para><b>IDEMPOTENT ON (version, campaign), and that is a holdout protection rather than a
    /// convenience.</b> A promotion already recorded is answered as it stands and NOTHING runs — so a
    /// caller that asks, waits for the collector to add a month and asks again is told the same thing
    /// it was told before. Re-running would let one charge buy an unbounded series of peeks at a
    /// growing holdout, each answer saying a little more about months the research process was never
    /// shown. <c>Referee.Verdict</c> is idempotent at the CHARGE already; this is idempotent at the
    /// RUN.</para>
    ///
    /// <para><b>One at a time per role, refused rather than queued</b>, exactly as a backtest is: the
    /// judgement reads a whole holdout in process, and a caller that fires three is told so rather than
    /// left holding a connection.</para>
    ///
    /// <para><b>A verdict the app stops is not answered</b> (<c>U-verdict-stopped</c>). <paramref name="ct"/> is
    /// never the asker's: on the pipe it is the server's, cancelled only when the server is disposed, and on the
    /// harness's door it is the turn's — the owner's Pause, the Chat page's Stop and every quit. The referee says
    /// <c>Stopped</c> and writes nothing, and the caller is refused <c>IPC_UNAVAILABLE</c> with no figure
    /// (<see cref="Backtests.VerdictStopped"/>). A judgement it charged before the stop stays the version's, as
    /// after a crash: no promotion was recorded, so the next ask under this campaign runs the referee again, and
    /// <c>ChargeVerdict</c> answers a charged pair Ok before the budget — judged, with nothing more spent.</para>
    ///
    /// <para><b>What does NOT cross.</b> No metric, no trace hash, no run id, no bar and no figure of
    /// any kind. <c>text</c> is <c>RefereeFeedback.Text</c> — which reads the promotion row
    /// alone, whose reason column is a closed vocabulary that cannot hold a number — and is not built
    /// here. <c>verdict</c> is copied through as the referee's OWN string rather than mapped, so a
    /// value the referee learns to write later arrives here untranslated. The execution model is not a
    /// parameter: it is the judge's default, because a submitter that chose the friction its evidence
    /// was scored under would be choosing the standard.</para>
    /// </summary>
    object VerdictFor(AgentContext ctx, IpcRequest req, CancellationToken ct)
    {
        // THE ROLE FIRST, and it is `Backtests`' own answer rather than a second reading of the same
        // question: one refusal, one rule, and a caller that cannot record a run cannot ask for a
        // verdict on one either.
        var role = Backtests.RoleOf(ctx);

        // THE ENTRY IT IS ASKED UNDER, CHECKED BEFORE ANYTHING IS CHARGED OR ANSWERED (U-research-ledger): in the ledger and
        // this role's own, or refused with nothing charged. Nothing is written here; the link is the verdict's own write,
        // or the one write beside an answer already recorded.
        var under = LedgerEntry(ctx, role, req);

        var version = Require(req, "version");
        var completed = gateway.Strategies.CompletedRunsOf(version, role);

        // THE DATASET IS NAMED OR IT IS THE ONE OBVIOUS ANSWER, and never a guess between two. Each
        // dataset with a cutoff has its own campaign and its own budget, so picking for the caller
        // would be choosing which of the owner's holdouts to spend.
        var dataset = req.Args is not null && req.Args.ContainsKey("dataset")
            ? DatasetId(req)
            : OnlyDataset(completed, version, role);

        var campaign = gateway.Campaigns.OpenForDataset(dataset)
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"dataset {dataset} has no open campaign, so there is no verdict to ask for. A campaign "
                + "is opened by TradeAgent when the account owner sets a holdout cutoff on a dataset in "
                + "TradeAgent's own window — it is what counts the attempts made against those months "
                + "and what bounds the judgements spent on them. There is no operation here that opens, "
                + "renews or re-budgets one.");

        if (!completed.Any(r => r.DatasetId == dataset && Registered(campaign.Id, version, r.Id)))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"{CouncilRoles.Title(role)} has never completed a run of version {Short(version)} over "
                + $"dataset {dataset}, so there is nothing to have judged. TradeAgent judges a program "
                + "its author already measured on the months it was allowed to see: run "
                + "'trade backtest --strategy <your program> --dataset " + dataset.ToString(CultureInfo.InvariantCulture)
                + " --to <before the cutoff>' first. A run that LOST is admission enough — the clause is "
                + "that the hypothesis was tested, not that it worked.");

        // ALREADY JUDGED: answered as it stands, and nothing runs. See the summary. "As it stands" is the
        // STANDING and not only the row: a recorded verdict whose evidence no longer holds is answered
        // with the record unchanged and `why` saying it was WITHDRAWN, when and why — read back bare, a
        // withdrawn promotion was a "promoted" with nothing beside it to say it no longer was.
        if (gateway.Promotions.For(version).FirstOrDefault(p => p.CampaignId == campaign.Id) is { } judged)
        {
            // ANSWERED AS IT STANDS, AND LINKED ALONE when it was asked under an entry: the app answered this request with
            // that record. Nothing runs and nothing is charged; asked again, it is the same one link. A REQUEST THE APP
            // STOPPED LINKS NOTHING: the stop is asked before the link and refused as the referee's stop is below — no
            // record was produced for it, so no link may say the app answered it.
            if (under is not null)
            {
                if (ct.IsCancellationRequested)
                    throw new GatewayDeniedException(ErrorCode.IPC_UNAVAILABLE, Backtests.VerdictStopped);
                gateway.Links.Link(under, LedgerRecord.Promotion, judged.Id, ctx.AttemptId);
            }
            return Answered(campaign, version, judged, Withdrawn(version, campaign.Id, judged));
        }

        if (!_judging.TryAdd(role, 0))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"a verdict for {CouncilRoles.Title(role)} is already being computed. One at a time per "
                + "role: wait for that one to answer, then ask for this one.");

        try
        {
            // THE APP'S OWN REFEREE, UNCHANGED AND UNPARAMETERISED BEYOND THE TWO IDS. The charge, the
            // holdout feed, the scoring policy, the promotion row and the delivery are all its own.
            // AND THE LINK, WHEN IT WAS ASKED UNDER AN ENTRY, IN THE PROMOTION'S OWN WRITE (U-research-ledger) — through the
            // referee's one callback, so a verdict not recorded links nothing and a `refused` one is linked like any other.
            var verdict = gateway.Referee.Verdict(version, campaign.Id, stop: ct,
                recorded: under is null ? null : promotion => gateway.Links.Link(under, LedgerRecord.Promotion, promotion.Id, ctx.AttemptId));

            // A VERDICT THE APP STOPPED IS NOT ANSWERED AS ONE (U-verdict-stopped): refused IPC_UNAVAILABLE, as
            // `Backtests.Run` refuses a run it stopped, and BEFORE the paper sweep below, because a stop writes
            // nothing. Read off the referee's flag, never off its words. See the summary.
            if (verdict.Stopped)
                throw new GatewayDeniedException(ErrorCode.IPC_UNAVAILABLE, Backtests.VerdictStopped);

            // AND THE APP'S OWN PAPER POLICY, IMMEDIATELY AFTER THE VERDICT RATHER THAN AT THE NEXT
            // TICK. It is the same sweep the mission loop runs on its periodic seam, and it writes
            // nothing this caller asked for and nothing this caller can see: no figure comes back, the
            // reply below is unchanged, and a version that has just been judged paper-eligible is in
            // the owner's envelope by the time the submitter reads the answer rather than up to a tick
            // later. It never throws (see `TradingGateway.AllocatePaperDue`), so a sweep that could not
            // run cannot turn a delivered verdict into a refusal.
            gateway.AllocatePaperDue();

            return Answered(campaign, version, verdict.Promotion, verdict.Ok ? null : verdict.Why);
        }
        finally
        {
            _judging.TryRemove(role, out _);
        }
    }

    /// <summary>
    /// WHY THIS RECORDED VERDICT NO LONGER STANDS, in words — or null, because it does
    /// (<c>U-evidence-identity</c>).
    ///
    /// <para>The standing is <c>Promotions.Standing</c>'s, asked of this campaign's verdict, and its reason
    /// is carried whole. When what moved is the evaluation semantics, the reply also says WHEN: the
    /// first instant this installation evaluated under semantics other than the ones the verdict was
    /// taken under (<c>EvaluationSemantics.WithdrawnAt</c>), and says so when no such instant was
    /// recorded rather than putting another date in its place. Then what can be done about it, which
    /// today is nothing through this op: asking again answers this same record and charges nothing,
    /// because re-judging a withdrawn verdict is a later unit's (<c>U-rejudge</c>), and a version the new
    /// manifest re-identified is a new program with an id of its own.</para>
    /// </summary>
    string? Withdrawn(string version, long campaignId, PromotionRow judged)
    {
        var standing = gateway.Promotions.Standing(version, campaignId);
        if (standing.State != PromotionState.Invalidated || standing.Promotion?.Id != judged.Id) return null;

        var manifest = gateway.Strategies.VersionById(version)?.Manifest;
        var evaluatorMoved = !string.Equals(
            judged.EvaluatorVersion, Core.Strategy.Referee.EvaluatorVersion, StringComparison.Ordinal);
        var manifestMoved = manifest is not null
            && !string.Equals(manifest, Core.Strategy.StrategyVersions.Manifest, StringComparison.Ordinal);

        var when = "";
        if ((evaluatorMoved || manifestMoved) && manifest is not null)
            when = gateway.EvaluationSemanticsWithdrawnAt(judged.EvaluatorVersion, manifest, judged.At) is { } at
                ? $" on {at:u}"
                : ", on a date this installation did not record,";

        return $"WITHDRAWN{when} because {standing.Why} The 'verdict', 'reason' and 'text' in this answer are "
            + "the record of what TradeAgent's referee answered, and a record is not rewritten; the verdict no "
            + "longer stands, so nothing may trade on it — no allocation authorises an order under it, no paper "
            + "run starts, and one already going is ended. Re-judging a withdrawn verdict is not available yet: "
            + "asking again answers this same record, charges nothing and runs nothing."
            + (manifestMoved
                ? " Under this build's manifest the same program text is a different version with its own id: "
                  + "backtest it again and ask about that id, which is a fresh verdict charged like any other."
                : "");
    }

    /// <summary>
    /// Whether this run is a registered trial anywhere in the campaign's renewal lineage. The lineage
    /// and not the campaign, because a renewal carries the parent's holdout: a run charged before the
    /// renewal is still a measurement of these months.
    /// </summary>
    bool Registered(long campaignId, string version, string runId) =>
        gateway.Campaigns.Lineage(campaignId).Any(c => gateway.Campaigns.Registered(c, version, runId));

    /// <summary>The first twelve of a content hash, which is how every refusal in this product names one.</summary>
    static string Short(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>
    /// THE ONE DATASET THIS ROLE HAS A COMPLETED RUN OF THIS VERSION OVER, or a refusal asking for one.
    /// Zero and several are both refused, and the refusal says which of the two it is: "you measured
    /// nothing" and "say which" are different mistakes with different repairs.
    /// </summary>
    static long OnlyDataset(IReadOnlyList<StrategyRunRow> completed, string version, string role)
    {
        var over = completed.Select(r => r.DatasetId).Distinct().ToList();
        if (over.Count == 1) return over[0];

        throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
            over.Count == 0
                ? $"'dataset' is required here: {CouncilRoles.Title(role)} has completed no run of "
                  + $"version {Short(version)} at all, so TradeAgent has no measurement of it to read a "
                  + "dataset off. 'trade data list' has the ledger ids."
                : $"'dataset' is required here: {CouncilRoles.Title(role)} has completed runs of version "
                  + $"{Short(version)} over {over.Count} datasets ("
                  + string.Join(", ", over.Select(d => d.ToString(CultureInfo.InvariantCulture)))
                  + "), and each holdout has its own campaign and its own budget — TradeAgent will not "
                  + "choose which of the owner's held-back months to spend a judgement on.");
    }

    /// <summary>
    /// THE WHOLE OF WHAT CROSSES, BUILT IN ONE PLACE. <paramref name="promotion"/> null with
    /// <paramref name="why"/> set is a referee that could not judge at all — an answer, not an error,
    /// because the budget and the words are what the caller needs and a refusal is not a fault.
    ///
    /// <para><c>verdicts_spent</c> is every judgement over the campaign's held months — its renewal lineage's and every
    /// other campaign's over the same months (<c>CampaignStore.JudgementsSpent</c>, <c>U-holdout-campaign</c>) — the count
    /// the budget is charged against, so the number a caller reads is the number that will refuse it.</para>
    /// </summary>
    VerdictReply Answered(CampaignRow campaign, string version, PromotionRow? promotion, string? why) =>
        new(version, campaign.Id,
            promotion?.Verdict, promotion?.Reason,
            promotion is null ? null : Core.Strategy.RefereeFeedback.Text(promotion), why,
            gateway.Campaigns.JudgementsSpent(campaign.Id), campaign.VerdictBudget);

    /// <summary>
    /// <inheritdoc cref="VerdictFor"/>
    ///
    /// <para>DECLARED TYPE AND NEVER AN ANONYMOUS OBJECT, for the reason <see cref="BacktestReplyMetrics"/>
    /// is one — and here it is also the shape a test asserts the whole of, so a figure cannot be added
    /// under a new name without that assertion going red.</para>
    /// </summary>
    sealed record VerdictReply(
        string Version,
        long Campaign,
        // NEVER DROPPED WHEN NULL. "The referee could not judge" is a different answer from "this build
        // has no such field", and a caller that read the absence as the latter would retry forever.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Verdict,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Why,
        int VerdictsSpent,
        int VerdictsBudget);

    /// <summary>
    /// WHAT MARKET DATA THIS INSTALLATION HOLDS, AND WHERE EVERY BYTE OF IT CAME FROM.
    ///
    /// A read, and only a read. The rows are written by the app's own collector; nothing on this
    /// pipe collects, normalises, rejects or deletes one. Each dataset is re-verified as it is
    /// listed — every raw archive file and the normalised file re-hashed against what the ledger
    /// recorded — so a dataset whose bytes have changed under it is reported REJECTED here rather
    /// than being described as though it were still the thing that was measured.
    /// </summary>
    object DataList(AgentContext ctx)
    {
        var sets = gateway.Datasets.All().Select(gateway.Datasets.Checked).ToList();

        return new DataListReply(
            sets.Count,
            "These are bars, and bars are hypothesis evidence: they establish no fill, no queue position "
            + "and no intrabar ordering, so a result computed over them is a reason to test something, "
            + "never a record of a trade. 'state' REJECTED means a file this ledger measured has changed "
            + "on disk since; those bars are not served. 'months_present' against 'months_attempted' is "
            + "the real coverage, 'gaps' counts minutes with no bar INSIDE the covered period and nothing "
            + "was filled in, and 'incomplete' counts bars excluded because they had not closed when the "
            + "archive was read. "
            // THE EVERY-DATASET RULE (U-bar-holdout), stated where the cutoff is named — this note once said a
            // window was refused only over its own dataset, and that the forward bars were exempt — and the
            // cutoff that does not move under a campaign (U-holdout-later).
            + "'holdout_from' is the instant from which the account owner is HOLDING THESE BARS BACK, or "
            + "null: every bar at or after it is private evaluation evidence, and the dataset's HOLDOUT "
            + "WINDOW runs from it to the close of its last bar. THE SAME MONTHS ARE HELD BACK ON EVERY "
            + "PAIR'S BARS AND ON THE TAPE: 'data-bars' and 'backtest' refuse any read whose bars reach ANY "
            + "dataset's holdout window — through that dataset, a newer version of the same pair, another "
            + "pair or the forward bars alike — and 'data-tape' refuses any read reaching one, whatever its "
            + "source or symbol; for you, for the other director and for a caller that proved no role, with "
            + "no exception and no quiet truncation. The cutoff is told to you rather than hidden so that "
            + "you need not find it one refusal at a time; nothing on this channel can set, clear or move "
            + "it, and the account owner cannot move it either — never earlier, and never later while a "
            + "campaign judges strategies from it, which is from the moment it is set. "
            + "'evaluation_class' is 'research' for real collected history and "
            + "'fixture' for bars that exist to prove the machinery works and are never evidence. "
            // AND THE FORWARD SERIES, NAMED HERE AND KEPT APART FROM THE DATASETS. A caller that could
            // not tell the two lists apart would be one bar count away from asking for a verdict over
            // minutes nobody checksummed.
            + "'forward' is a DIFFERENT KIND OF THING and is listed separately: bars TradeAgent "
            + "collected itself, minute by minute, while it was running. They carry NO vendor "
            + "checksum — none is published for a live window and none could be — so they are NOT "
            + "evaluation evidence and no verdict is ever taken over them; and they are held back over "
            + "every dataset's holdout window exactly as the archive's bars are, because a Download taken "
            + "after TradeAgent collected them freezes the same minutes. Read them with 'data-bars "
            + "--source forward'. "
            // AND THE TAPE, NAMED HERE AND KEPT APART FROM BOTH (U-tape-read): readings of the market's context, not
            // bars, with three times each and a class — a third kind of thing in a third list.
            + "'tape' is a THIRD kind of thing, in its own list: the market's context as TradeAgent recorded it "
            + "arriving. Read a series of it with 'data-tape'.",
            [.. sets.Select(Describe)],
            [.. gateway.Forward.All().Select(Describe)],
            TapeList());
    }

    /// <summary>
    /// THE TAPE'S SERIES, FOR <c>data-list</c> (<c>U-tape-read</c>): every series with the switch that records it, its
    /// rows, the arrival of its first and newest row, what its newest attempt got wrong, its symbols where its subjects
    /// are symbols and what its keys are where they are not, and the credit its source's terms require. Null when this
    /// gateway has no tape open. A tape that cannot be read just now says so in its note rather than taking the
    /// datasets' list down with it.
    /// </summary>
    DataListReplyTape? TapeList()
    {
        if (gateway.Tape is not { } tape) return null;
        try
        {
            return new DataListReplyTape(TapeListNote, [.. tape.Series().Select(s => new DataListReplyTapeSeries(
                s.Source, s.Series, s.Switch, s.Rows, s.FirstArrival, s.LastArrival, s.LastError, s.Subjects,
                SubjectKey(tape, s.Source, s.Series), CitationOf(tape, s.Source)))]);
        }
        catch (Exception ex)
        {
            return new DataListReplyTape(
                TapeListNote + " THE TAPE COULD NOT BE READ just now, so no series is listed: "
                + ex.Message.ReplaceLineEndings(" "), []);
        }
    }

    /// <summary>What the tape's list says about itself, in the brief's words first.</summary>
    static readonly string TapeListNote =
        "TAPE — recorded by TradeAgent as it arrived; O-LIVE rows only are first-hand; not evaluation evidence. "
        + "These are the market's context series — Binance USDⓈ-M premium index and funding, open interest and the "
        + "long/short and taker ratios for six pairs, Hyperliquid's perpetual contexts for the same six coins (open "
        + "interest, funding, premium, prices and the day's volume, subjects BTC to DOGE by Hyperliquid's own names, every "
        + "five minutes), OKX's announcements for EU users, GDELT's news items about crypto — "
        + "each with the owner's switch that records it ('switch'), its 'rows', the arrival of its first and newest row in "
        + "the order the tape wrote them, and what its newest attempt got wrong ('last_error', null when it delivered). "
        + "'subjects' lists a series' symbols; a series keyed by digests or record ids lists none and 'subject_key' says "
        + "what its keys are — leave 'subject' out of 'data-tape' to read every subject. 'citation' is the credit the "
        + "source's terms require wherever its rows are used or shown. Read a series with 'data-tape'. The tape is as deep "
        + "as TradeAgent has been running with a switch on; nothing on this channel records, edits or deletes a row.";

    /// <summary>One forward series on the wire. See <see cref="ForwardBars"/>.</summary>
    static DataListReplyForward Describe(ForwardSeries series) => new(
        series.Source, series.Symbol, series.Interval, series.Bars, series.FirstBar, series.LastBar,
        series.Gaps, series.BarsMissing, series.LastReceivedAt, series.LastError,
        // THE SENTENCE IS ON THE ROW AND NOT ONLY IN THE NOTE ABOVE, for the reason a bar's
        // `quality` is on every bar: a caller that reasons over one of these series has to be able
        // to read what it is from the series itself.
        series.Note);

    /// <summary>
    /// The bars themselves, for one pair, between two instants.
    ///
    /// Refuses rather than truncates when the window holds more than the cap, and says what the cap
    /// is: an answer quietly cut short is a different window from the one that was asked for, and
    /// nothing in the reply would say so.
    /// </summary>
    object DataBars(AgentContext ctx, IpcRequest req)
    {
        var pair = Require(req, "pair").ToUpperInvariant();
        if (!BinanceArchive.IsPair(pair))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{pair}' is not a pair this build holds data for. Use 'trade data list' to see what there is.");

        var from = BarInstant(req, "from");
        var to = BarInstant(req, "to");
        if (from is { } lo && to is { } hi && lo > hi)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'from' ({lo:O}) is after 'to' ({hi:O}), which is a window with nothing in it.");

        // THE SOURCE IS A WORD AND IT IS CHECKED, not defaulted: 'forward' and the archive are
        // different evidence with different claims attached, and a misspelling that quietly served
        // the archive would answer a question nobody asked.
        if (req.Args is not null && req.Args.ContainsKey("source"))
        {
            var word = (req.Str("source") ?? "").Trim().ToLowerInvariant();
            if (word.Length > 0 && word != "archive")
                return word == ForwardBars.SourceWord
                    ? ForwardBars_(ctx, pair, from, to)
                    : throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                        $"'{word}' is not a source this build serves bars from. It has 'archive' — the "
                        + "frozen, checksummed history the account owner collected — and "
                        + $"'{ForwardBars.SourceWord}', the closed minutes TradeAgent collected itself "
                        + "while it was running. Leaving --source out means the archive.");
        }

        var newest = gateway.Datasets.Newest(pair)
            ?? throw new GatewayDeniedException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"TradeAgent holds no {pair} data. The account owner collects it in TradeAgent, on the "
                + "Settings page under Market data; there is no command here that does it.");

        var set = gateway.Datasets.Checked(newest);
        if (set.State == DatasetState.REJECTED)
            throw new GatewayDeniedException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"The {pair} dataset is REJECTED and its bars are not served: {set.RejectedReason}. "
                + "The account owner collects the months again in TradeAgent.");

        BarWindow window;
        try
        {
            // THE AUDIENCE IS THE CALLER'S OWN AND IT IS A PIPE CALLER, whatever role it proved: the
            // reader takes the holdout decision itself, so this op cannot serve a held-back bar by
            // forgetting a check, and a null role is not treated as "not research" — it is treated as a
            // caller that proved nothing, which is refused exactly as both directors are. It travels
            // with the dataset ledger (U-bar-holdout): the newest dataset of a pair holding no cutoff
            // still serves no bar inside ANOTHER dataset's holdout window, of this pair or any other.
            window = DatasetReader.Read(set, TapeHoldout.Pipe(ctx.Role, gateway.Datasets), from, to);
        }
        catch (IOException ex)
        {
            throw new GatewayDeniedException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"The {pair} dataset file could not be read: {ex.Message}");
        }

        if (window.Refusal is { } withheld)
            throw new GatewayDeniedException(ErrorCode.HOLDOUT_WITHHELD, withheld);

        if (window.OverCap)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"That window holds more than {DatasetReader.MaxBars} bars, which is the most one call may "
                + $"have. Ask for a shorter period with --from and --to; the dataset covers "
                + $"{set.FirstBar:yyyy-MM-dd HH:mm} to {set.LastBar:yyyy-MM-dd HH:mm} UTC and "
                + $"{DatasetReader.MaxBars} one-minute bars is about {DatasetReader.MaxBars / 1440} days.");

        return new DataBarsReply(
            set.Pair, set.Interval, set.Version, set.Source, set.Id,
            "Closed bars in UTC, ascending, nothing filled in. They are hypothesis evidence and establish "
            + "no fill, no queue position and no intrabar ordering. A minute that is missing is missing: "
            + "'gaps_in_dataset' counts them across the whole dataset and 'trade data list' lists where "
            + "they are. EVERY BAR CARRIES A 'quality': 'traded' means the source published a volume "
            + "for it, and 'midpoint_derived' means it published none — that bar's volume of 0 is not a "
            + "measurement and the bar is never trade evidence."
            + (set.MidpointNote is { } midpoint ? " " + midpoint : ""),
            from, to, window.Bars.Count, set.Gaps, set.Incomplete,
            // THE WINDOW'S OWN COUNT AND THE DATASET'S, because a caller asking for a week of a year
            // is being told about the week it asked for and about the evidence it is a week of.
            window.Bars.Count(b => b.Quality == BarQuality.MidpointDerived), set.MidpointBars,
            set.MidpointNote,
            [.. window.Bars.Select(b => new DataBarsReplyBar(
                b.OpenTime, b.Open, b.High, b.Low, b.Close, b.Volume, b.Quality))]);
    }

    /// <summary>
    /// THE FORWARD BARS — the closed minutes TradeAgent collected itself while it was running.
    ///
    /// <para><b>Held back over every holdout window, as the archive's bars are</b> (<c>U-bar-holdout</c>).
    /// "Every forward bar post-dates every freeze" was a premise, and a Download taken after the
    /// collector ran breaks it: the archive's newest months are minutes the collector already holds, and
    /// a minute TradeAgent collected says what that minute did as surely as the archive's bar for it. So
    /// the read is <c>ForwardBarStore.Window</c>, which takes the caller's holdout with the dataset
    /// ledger and REFUSES a window whose market span reaches any dataset's holdout window —
    /// <c>HOLDOUT_WITHHELD</c>, in the words naming the dataset, its cutoff and the window, never
    /// clipped. <c>ForwardBarStore.Since</c> takes no holdout and is never called from here.</para>
    ///
    /// <para><b>And they are not evaluation evidence.</b> There is no vendor checksum for a live
    /// window and none is claimed, so the reply says so in the same words <c>data-list</c> uses.
    /// Outside every holdout window they are served to any role alike; the further refusal is on the
    /// EVIDENCE side, and it is that no verdict is taken over these bars at all.</para>
    ///
    /// <para>Bounded exactly as the archive read is bounded: at most <c>DatasetReader.MaxBars</c>,
    /// and a window holding more is REFUSED naming the cap rather than truncated. An answer quietly
    /// cut short is a different window from the one that was asked for.</para>
    /// </summary>
    object ForwardBars_(AgentContext ctx, string pair, DateTimeOffset? from, DateTimeOffset? to)
    {
        var series = gateway.Forward.Series(pair);
        if (series.Bars == 0)
            throw new GatewayDeniedException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"TradeAgent holds no forward bars for {pair}"
                + (series.LastError is { Length: > 0 } why ? $": the last look failed — {why}." : ".")
                + " They are collected only while TradeAgent is running, and the account owner "
                + "switches that on in TradeAgent's own window under Market data; there is no command "
                + "here that does it.");

        // THE CALLER'S HOLDOUT, WITH THE LEDGER, IS ASKED INSIDE THE READ (U-bar-holdout), before a row
        // is read, whatever role it proved; and one past the cap is the last row the read asks the
        // database for — the reading `DatasetReader.Read` takes, so the two doors refuse alike.
        var window = gateway.Forward.Window(TapeHoldout.Pipe(ctx.Role, gateway.Datasets), pair, from, to);

        if (window.Refusal is { } withheld)
            throw new GatewayDeniedException(ErrorCode.HOLDOUT_WITHHELD, withheld);

        var bars = window.Bars;
        if (window.OverCap)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"That window holds more than {DatasetReader.MaxBars} forward bars, which is the most "
                + "one call may have. Ask for a shorter period with --from and --to; the series covers "
                + $"{series.FirstBar:yyyy-MM-dd HH:mm} to {series.LastBar:yyyy-MM-dd HH:mm} UTC and "
                + $"{DatasetReader.MaxBars} one-minute bars is about {DatasetReader.MaxBars / 1440} days.");

        return new DataForwardBarsReply(
            series.Source, pair, series.Interval,
            ForwardBars.Evidence
            + ". Closed bars in UTC, ascending, nothing filled in: a minute that is missing is missing, "
            + "'gaps_in_series' counts the runs of them and 'bars_missing' the minutes, and 'data-list' "
            + "says where they are. The FIRST reading of a minute is the one stored — a later answer "
            + "that disagreed did not overwrite it — and every bar carries the instant it was received, "
            + "which is after its own close. 'last_bar_age_seconds' is how stale this series is right "
            + "now; a strategy's `data_freshness` is checked against the bar its decision was computed "
            + "from, at dispatch, and is not something this read can satisfy on its behalf.",
            from, to, bars.Count, series.Bars, series.Gaps, series.BarsMissing,
            series.FirstBar, series.LastBar, series.LastReceivedAt,
            gateway.Forward.Freshness(pair, gateway.UtcNow) is { } age ? (long)age.TotalSeconds : null,
            [.. bars.Select(b => new DataForwardBar(
                b.OpenTime, b.Open, b.High, b.Low, b.Close, b.Volume, b.CloseTime, b.ReceivedAt))]);
    }

    /// <summary>
    /// THE MOST ONE <c>data-tape</c> ANSWER'S ROWS TAKE ON THE WIRE: 4 MiB, each row measured as it is written. A tape
    /// payload reaches 64 KB, so for GDELT's items and OKX's announcements it is this and not the row limit that stops a
    /// read; five thousand market rows of a few hundred bytes each fit under it.
    /// </summary>
    public const long MaxTapeReplyBytes = 4L * 1024 * 1024;

    /// <summary>
    /// THE TAPE, FOR EVERY ROLE, BOUNDED AND READ-ONLY (<c>U-tape-read</c>).
    ///
    /// <para><b>Every role, and a caller that proved none — and none of them inside a holdout window</b>
    /// (<c>U-tape-holdout</c>). The tape is research context and no verdict is taken over it, but every dataset holding
    /// a cutoff holds the same market time on the tape: the reader is handed the caller's own pipe audience with the
    /// dataset ledger (<see cref="TapeHoldout.Pipe"/>) and refuses, inside, a window of source time reaching any
    /// dataset's held-back months — <c>HOLDOUT_WITHHELD</c>, in words naming the dataset, its cutoff and the window, never
    /// cut short. A quarantined item is served to every audience with its rule and without its payload — the reader
    /// withholds it in the one place <c>TapeStore.AsOf</c> does.</para>
    ///
    /// <para><b>Bounded, and never silently.</b> At most <see cref="TapeReader.MaxRows"/> rows — a larger limit is
    /// refused in words, never clamped — and at most <see cref="MaxTapeReplyBytes"/> of rows. An answer either bound
    /// stopped says which, and hands back the row id that continues it exactly.</para>
    ///
    /// <para><b>Validated in words.</b> A source the tape does not know, a series the source does not record, a subject
    /// the series holds no row of, an unreadable instant and a malformed limit are each refused naming what there is,
    /// rather than answered with an empty list that would read as "nothing happened".</para>
    /// </summary>
    object DataTape(AgentContext ctx, IpcRequest req)
    {
        var tape = gateway.Tape
            ?? throw new GatewayDeniedException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                "TradeAgent has no market-context tape open, so there is nothing to read. It is opened when "
                + "TradeAgent starts, and the account owner's activity log says why it could not be; there is no "
                + "command here that opens, starts or writes it.");

        var sources = tape.Sources();
        var source = (req.Str("source") ?? "").Trim();
        if (source.Length == 0)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'source' is required: which of the tape's sources to read — {string.Join(", ", sources)}. "
                + "'trade data list' names every series with its rows and arrivals.");
        if (!sources.Contains(source, StringComparer.Ordinal))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{source}' is not a source the tape records. It records {string.Join(", ", sources)}; "
                + "'trade data list' names every series with its rows and arrivals.");

        var known = tape.SeriesOf(source);
        string series;
        if (req.Args is not null && req.Args.ContainsKey("series"))
        {
            series = (req.Str("series") ?? "").Trim();
            if (!known.Contains(series, StringComparer.Ordinal))
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                    $"'{series}' is not a series of '{source}'. "
                    + (known.Count == 0 ? "It holds no series yet." : $"It records {string.Join(", ", known)}."));
        }
        else if (known.Count == 1) series = known[0];
        else
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                known.Count == 0
                    ? $"'{source}' holds no series yet: nothing of it has been recorded."
                    : $"'{source}' records {known.Count} series — {string.Join(", ", known)} — so 'series' must name one.");

        // THE SUBJECT IS OPTIONAL, and it is checked when it is given: an announcement or a news item is keyed by a
        // digest or a record id nobody can guess, so leaving it out reads every subject; a subject the series holds no
        // row of is refused naming what it does hold, never answered with an empty list.
        string? subject = null;
        if (req.Args is not null && req.Args.ContainsKey("subject"))
        {
            subject = (req.Str("subject") ?? "").Trim();
            if (!TapeStore.IsSubject(subject))
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                    $"'{subject}' is not a subject the tape can hold: letters, digits, '.', '_' and '-' only, at most 40. "
                    + "Leave 'subject' out to read every subject, newest arrival first.");
            if (!tape.Holds(source, series, subject))
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                    $"the tape holds no row of '{subject}' in {source} {series}. {SubjectsOf(tape, source, series)} "
                    + "Leave 'subject' out to read every subject, newest arrival first.");
        }

        var from = BarInstant(req, "from");
        var to = BarInstant(req, "to");
        if (from is { } lo && to is { } hi && lo > hi)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'from' ({lo:O}) is after 'to' ({hi:O}), which is a window with nothing in it.");
        var asOf = BarInstant(req, "as_of");
        var limit = TapeLimit(req);
        var before = TapeCursor(req);

        // THE HOLDOUT IS THE CALLER'S OWN PIPE AUDIENCE WITH THE DATASET LEDGER, read inside the reader at this read: a
        // window reaching any dataset's held-back months is refused there, whoever asks, before the tape is opened.
        var window = tape.Window(TapeHoldout.Pipe(ctx.Role, gateway.Datasets), new TapeQuery
        {
            Source = source,
            Series = series,
            Subject = subject,
            From = from,
            To = to,
            AsOf = asOf,
            Before = before,
            Limit = limit,
            MaxBytes = MaxTapeReplyBytes
        }, o => Encoding.UTF8.GetByteCount(Json.Write(TapeRow(o))));

        if (window.Refusal is { } withheld)
            throw new GatewayDeniedException(ErrorCode.HOLDOUT_WITHHELD, withheld);

        var rows = window.Rows.Select(TapeRow).ToList();
        return new DataTapeReply(
            source, series, subject, from, to, asOf, limit, rows.Count, window.More, window.CappedBy,
            window.More ? rows[^1].Id : null,
            // THE CREDIT TRAVELS WITH THE ROWS. GDELT's terms ask every use of its data to cite the project and link to
            // its site, so an answer holding GDELT's rows carries the row's own citation, in the row's own words.
            CitationOf(tape, source),
            TapeNote, rows);
    }

    /// <summary>
    /// What the answer says about itself, once, in words: what a row is, what each class means and what none of them
    /// is, what a quarantine withholds, the order, the bounds, and how to continue.
    /// </summary>
    static readonly string TapeNote =
        "TAPE — recorded by TradeAgent as it arrived. Each row is one reading of what the source said about one "
        + "subject at one source time: 'source_time' is the vendor's own time — for Hyperliquid's contexts, which carry "
        + "none, the Date its answer was sent with — 'received_at' the instant it arrived "
        + "here, and 'revision' which reading of that datum it is — a re-reading that differed is a new revision beside "
        + "the first, and nothing is ever overwritten. 'evidence_class' is computed by TradeAgent from what it recorded: "
        + "O-LIVE rows only are first-hand, received on time from the source's own address; O-PIT is a vendor's "
        + "checksummed archive file fetched late whose storage dates it no later than its first-seen time; O-ARCH is "
        + "anything else fetched after the fact. None of it is evaluation evidence — no verdict is ever taken over the "
        + "tape — and it is research context, not a record of a trade. A row with a 'quarantine' was flagged by "
        + "TradeAgent's screen as text addressed to an automated reader: it is served to every caller WITHOUT its "
        + "payload, and the rule says why. 'payload' is the vendor's item as recorded, canonical JSON text, and "
        + "'payload_sha256' is TradeAgent's hash of that text. Rows come newest arrival first; 'from' and 'to' bound the "
        + "source time and 'as_of' the arrival, all inclusive. At most "
        + $"{TapeReader.MaxRows.ToString("N0", CultureInfo.InvariantCulture)} rows and "
        + $"{MaxTapeReplyBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes of rows a call: when 'more' is true "
        + "the answer stopped there — 'capped_by' says which bound — and asking again with 'before' set to "
        + "'next_before' continues exactly where it stopped. Nothing on this channel writes the tape.";

    static DataTapeRow TapeRow(TapeObservation o) => new(
        o.Id, o.Subject, o.SourceTime, o.ReceivedAt, o.Revision, o.EvidenceClass, o.FetchId, o.PayloadSha256,
        o.Payload, o.Quarantine);

    /// <summary>
    /// THE CREDIT A SOURCE'S TERMS ASK FOR WHEREVER ITS DATA IS SHOWN, or null for a source whose terms ask none — the
    /// catalogue row's own <c>Citation</c>, so the words are the row's and not a second copy of them.
    /// </summary>
    static string? CitationOf(TapeReader tape, string source) =>
        tape.Row(source)?.Citation is { Length: > 0 } citation ? citation : null;

    /// <summary>
    /// How a series' subjects are keyed, in words, for a refusal: a symbol series names what it holds; an announcement
    /// or a news series says what its keys are made of, because nobody can guess one.
    /// </summary>
    static string SubjectsOf(TapeReader tape, string source, string series)
    {
        if (SubjectKey(tape, source, series) is { } keyed) return $"Its subjects are {keyed}.";
        var held = tape.Subjects(source, series);
        return held.Count == 0 ? "It holds no row of any subject yet." : $"It holds {string.Join(", ", held)}.";
    }

    /// <summary>
    /// WHAT A SERIES' SUBJECTS ARE MADE OF when they are not symbols, or null for a series of symbols: the first 32 hex
    /// of the SHA-256 of an announcement's <c>url</c>, GDELT's GKGRECORDID for a news item, <c>gdelt</c> for the record
    /// of each file.
    /// </summary>
    static string? SubjectKey(TapeReader tape, string source, string series) =>
        tape.Row(source)?.Parser switch
        {
            TapeSourceCatalog.AnnouncementParser =>
                "the first 32 hex characters of the SHA-256 of each announcement's url, one per announcement",
            TapeSourceCatalog.GkgParser when series == GdeltGkg.BatchSeries =>
                $"'{GdeltGkg.BatchSubject}', one record per GDELT file read",
            TapeSourceCatalog.GkgParser => "GDELT's GKGRECORDID, one per news item",
            _ => null
        };

    /// <summary>
    /// HOW MANY ROWS ONE ANSWER MAY HOLD: absent is <see cref="TapeReader.DefaultRows"/>; present, a whole number from
    /// 1 to <see cref="TapeReader.MaxRows"/>, and anything else is REFUSED in words — never clamped, because an answer
    /// to a different limit is a different answer from the one asked for.
    /// </summary>
    static int TapeLimit(IpcRequest req)
    {
        if (req.Args is null || !req.Args.ContainsKey("limit")) return TapeReader.DefaultRows;

        var raw = req.Dec("limit")!.Value;
        if (raw != decimal.Truncate(raw) || raw < 1m || raw > TapeReader.MaxRows)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'limit' is how many rows one answer may hold — a whole number from 1 to "
                + $"{TapeReader.MaxRows.ToString(CultureInfo.InvariantCulture)} — and '{raw.ToString(CultureInfo.InvariantCulture)}' "
                + "is not one. Ask for fewer and continue with 'before', which every answer that stopped early hands back "
                + "as 'next_before'.");

        return (int)raw;
    }

    /// <summary>The row id a capped answer handed back, or null: a whole number above 0, or a refusal.</summary>
    static long? TapeCursor(IpcRequest req)
    {
        if (req.Args is null || !req.Args.ContainsKey("before")) return null;

        var raw = req.Dec("before")!.Value;
        if (raw != decimal.Truncate(raw) || raw < 1m || raw > long.MaxValue)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'before' is a row id — a whole number above 0, the 'next_before' of an answer that stopped early — and "
                + $"'{raw.ToString(CultureInfo.InvariantCulture)}' is not one.");

        return (long)raw;
    }

    /// <inheritdoc cref="DataTape"/>
    sealed record DataTapeReply(
        string Source, string Series,
        // NEVER DROPPED WHEN NULL: "every subject" and "this build has no such field" are different answers, and so
        // are an unbounded window and a field nobody sent.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Subject,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? From,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? To,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? AsOf,
        int Limit, int Count, bool More,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CappedBy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? NextBefore,
        // NEVER DROPPED WHEN NULL: an answer holding GDELT's rows carries GDELT's credit, and "this source asks for
        // none" must read differently from a build that forgot to say.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Citation,
        string Note,
        IReadOnlyList<DataTapeRow> Rows);

    /// <summary>
    /// ONE ROW OF THE TAPE ON THE WIRE. <c>payload</c> and <c>quarantine</c> are never dropped when null: a withheld
    /// payload is a fact about this row, and an absent key would read as a build that has no such field.
    /// </summary>
    sealed record DataTapeRow(
        long Id, string Subject, DateTimeOffset SourceTime, DateTimeOffset ReceivedAt, int Revision,
        string EvidenceClass, long FetchId, string PayloadSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Payload,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TapeQuarantine? Quarantine);

    /// <summary>
    /// A date on a data request. Absent is null; PRESENT AND UNREADABLE IS A REFUSAL, the same rule
    /// `pnl --since` follows and for the same reason — a window that quietly became a different
    /// window is a different answer.
    /// </summary>
    static DateTimeOffset? BarInstant(IpcRequest req, string key)
    {
        if (req.Args is null || !req.Args.ContainsKey(key)) return null;
        var raw = req.Str(key);

        if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{key}' is not a date this build can read: {raw}. Send an ISO-8601 instant or date, for "
                + "example 2026-08-01 or 2026-08-01T13:00:00Z. Reading it as anything else would answer a "
                + "different question from the one you asked.");

        return parsed;
    }

    static DataListReplyItem Describe(DatasetRecord set) => new(
        // THE VENUE AND THE INSTRUMENT ARE THE ROW'S OWN, read off the record the collector wrote and
        // never derived from the pair or from anything the caller asked for: a dataset is the evidence
        // a strategy is judged on, and a caller that could rename where its own evidence came from
        // could bind a run to an instrument it was not measured on.
        set.VenueId, set.InstrumentSymbol,
        set.Id, set.Source, set.Pair, set.Interval, set.Version, set.State.ToString(), set.RejectedReason,
        set.MonthsAttempted, set.MonthsPresent, set.MonthsNotPublished,
        set.NormalisedSha256, set.Bars, set.FirstBar, set.LastBar,
        set.Gaps, [.. set.GapRuns.Select(g => new DataListReplyGap(g.From, g.To, g.Minutes))],
        set.GapRunsTruncated, set.Duplicates, set.Incomplete, set.Unreadable, set.AcceptedAt,
        set.HoldoutFrom, set.EvaluationClass,
        // WHAT THE SOURCE DECLARED AND WHAT THE NORMALISER COUNTED, on the row rather than inferred
        // from the bars: the target it was asked for, the depth that arrived, whether the source can
        // publish a candle with no volume at all, and how many of these bars are one.
        set.CoverageTargetDays, set.CoverageActualDays, set.SourceCarriesVolume, set.MidpointBars,
        set.MidpointNote,
        [.. set.Files.Select(f => new DataListReplyFile(f.Month, f.Url, f.PublishedSha256,
            f.ComputedSha256, f.Bytes, f.DownloadedAt, f.Unit.ToString()))]);

    /// <summary>
    /// DECLARED TYPES, FOR THE REASON <see cref="PnlReply"/> IS ONE. <c>Json.Options</c> drops a null
    /// field from an anonymous object, and an absent <c>rejected_reason</c> reads as a field this
    /// build does not have rather than as "there is no reason because nothing is wrong".
    /// </summary>
    sealed record DataListReply(int Count, string Note, IReadOnlyList<DataListReplyItem> Datasets,
        IReadOnlyList<DataListReplyForward> Forward,
        // NEVER DROPPED WHEN NULL: "this installation has no tape open" and "this build has no tape" are different
        // answers, and the first is one the account owner's activity log explains.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DataListReplyTape? Tape);

    /// <summary>
    /// THE TAPE'S LIST, IN ITS OWN SHAPE, for the reason <see cref="DataListReplyForward"/> is its own: a tape series has
    /// no bars, no version and no holdout, and a reply that reused either shape with those fields empty would let a
    /// caller read readings of the market's context as though they were bars.
    /// </summary>
    sealed record DataListReplyTape(string Note, IReadOnlyList<DataListReplyTapeSeries> Series);

    /// <summary>One series of the tape. See <see cref="TapeList"/>.</summary>
    sealed record DataListReplyTapeSeries(
        string Source, string Series, string Switch, long Rows,
        // NEVER DROPPED WHEN NULL: "nothing has arrived yet", "nothing is failing", "these keys are symbols" and "this
        // source asks no credit" are answers, and an absent key would read as a build without the field.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FirstArrival,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastArrival,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastError,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Subjects,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SubjectKey,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Citation);

    /// <summary>
    /// ONE FORWARD SERIES, IN ITS OWN LIST. It is deliberately NOT a <see cref="DataListReplyItem"/>
    /// with some fields null: a dataset has a version, a normalised hash, months attempted and
    /// present, a holdout and an evaluation class, and a forward series has none of those and never
    /// will. Merged, the reply would be one shape whose meaning depended on which half of its fields
    /// were filled in — and the one fact that must never be lost is which of the two a caller is
    /// looking at.
    /// </summary>
    sealed record DataListReplyForward(
        string Source, string Symbol, string Interval, int Bars,
        // NEVER DROPPED WHEN NULL: "this series has no bars yet" and "this build has no such field"
        // are different answers, and the first is the ordinary state of a collector that has just
        // started or has been failing all morning.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FirstBar,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastBar,
        int Gaps, int BarsMissing,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastReceivedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastError,
        string Note);

    /// <inheritdoc cref="DataListReply"/>
    sealed record DataListReplyItem(
        // NEVER DROPPED WHEN NULL, for the reason `rejected_reason` is not: "this dataset records no
        // venue" and "this build has no such field" are different answers, and only the first one is
        // something a caller can do anything about.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? VenueId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InstrumentSymbol,
        long Id, string Source, string Pair, string Interval, string Version, string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RejectedReason,
        int MonthsAttempted, int MonthsPresent, IReadOnlyList<string> MonthsNotPublished,
        string NormalisedSha256, int Bars,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FirstBar,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastBar,
        int Gaps, IReadOnlyList<DataListReplyGap> GapRuns, bool GapRunsTruncated,
        int Duplicates, int Incomplete, int Unreadable, DateTimeOffset AcceptedAt,
        // NEVER DROPPED WHEN NULL, for the reason `rejected_reason` is not: an absent key reads as a
        // field this build does not have, and "TradeAgent holds nothing back on this dataset" is a
        // different answer from "this build cannot hold anything back".
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? HoldoutFrom,
        string EvaluationClass,
        int CoverageTargetDays, int CoverageActualDays, bool SourceCarriesVolume, int MidpointBars,
        // NEVER DROPPED WHEN NULL: "these bars are all traded" and "this build cannot tell you" are
        // different answers.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MidpointVolumeNote,
        IReadOnlyList<DataListReplyFile> Files);

    /// <inheritdoc cref="DataListReply"/>
    sealed record DataListReplyGap(DateTimeOffset From, DateTimeOffset To, int Minutes);

    /// <inheritdoc cref="DataListReply"/>
    sealed record DataListReplyFile(
        string Month, string Url, string PublishedSha256, string ComputedSha256, long Bytes,
        DateTimeOffset DownloadedAt, string Unit);

    /// <inheritdoc cref="DataListReply"/>
    sealed record DataBarsReply(
        string Pair, string Interval, string Version, string Source, long DatasetId, string Note,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? From,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? To,
        int Count, int GapsInDataset, int IncompleteExcluded,
        int MidpointBarsInWindow, int MidpointBarsInDataset,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MidpointVolumeNote,
        IReadOnlyList<DataBarsReplyBar> Bars);

    /// <summary>
    /// THE FORWARD WINDOW, AND IT IS ITS OWN SHAPE for the reason <see cref="DataListReplyForward"/>
    /// is: there is no version, no dataset id, no normalised hash and no midpoint count here, and
    /// there never will be. A reply that reused <see cref="DataBarsReply"/> with those fields empty
    /// would let a caller read forward minutes as though they were a frozen dataset.
    /// </summary>
    sealed record DataForwardBarsReply(
        string Source, string Pair, string Interval, string Note,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? From,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? To,
        int Count, int BarsInSeries, int GapsInSeries, int BarsMissing,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FirstBar,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastBar,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastReceivedAt,
        // NEVER DROPPED WHEN NULL: "this series has no bars to be stale" and "this build cannot tell
        // you how stale it is" are different answers to a question about acting on it.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? LastBarAgeSeconds,
        IReadOnlyList<DataForwardBar> Bars);

    /// <summary>
    /// ONE FORWARD BAR. <c>received_at</c> is on every bar rather than only on the series, because it
    /// is what makes the closure claim checkable by the caller: this bar's close time precedes the
    /// moment it was read, and a reader can see that rather than trust it.
    /// </summary>
    sealed record DataForwardBar(
        DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume,
        DateTimeOffset CloseTime, DateTimeOffset ReceivedAt);

    /// <inheritdoc cref="DataListReply"/>
    sealed record DataBarsReplyBar(
        DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume,
        // ON EVERY BAR, NEVER ONLY IN THE NOTE. A caller that reads the note and then reasons over a
        // window has to be able to tell WHICH bars it is about, and a volume of 0 cannot say so.
        string Quality);

    /// <summary>
    /// WHAT INSTRUMENTS THIS INSTALLATION KNOWS OF, AND WHO SAID SO.
    ///
    /// A read, and only a read. The rows come from the catalogue TradeAgent ships and from the
    /// account owner's <c>venues.json</c>, overlaid by the app's own instrument checks (the served read,
    /// <c>U-venue-verify</c>); nothing on this pipe adds, edits, verifies or removes one, or asks for a
    /// check.
    /// An increment is what a size is rounded DOWN to, so an agent that could write its own would be
    /// choosing how much it trades and having the record agree with it.
    ///
    /// An UNVERIFIED row is served, and it is served as unverified — never left out and never quietly
    /// promoted. A caller that needs a number nobody has checked can say so and declare its own.
    /// </summary>
    /// <summary>
    /// WHAT IS BEING RUN FORWARD ON PAPER, for every role, as a READ.
    ///
    /// <para>It carries no request id and no order: the ids in <c>deployment_op</c> are
    /// <c>execution_request</c> ids and those rows belong to a session the pipe refuses as a name, so
    /// a caller that could read them would be reading the app's own orders. What crosses is the run,
    /// where it has got to, and how many of its operations this app cannot yet account for — which is
    /// the fact an agent planning anything needs, and it is the app telling it something rather than
    /// the other way about.</para>
    ///
    /// <para>There is deliberately no <c>deployment start</c>. A deployment is written by the app's
    /// own policy inside the grant the account owner pressed once, like an envelope and like an
    /// allocation; an agent that wanted one has nowhere to ask.</para>
    /// </summary>
    object DeploymentList() => new DeploymentListReply(
        "A paper deployment is TradeAgent running a frozen strategy version forward on a practice "
        + "account, inside the paper envelope the account owner granted once. It carries NO capital "
        + "and NO live authority, its fills are simulated, and nothing it produces is execution "
        + "evidence. You cannot start one, widen one, point it somewhere else or move its cursor; "
        + "'deployment-stop' ends one, which only ever removes exposure.",
        [.. gateway.DeploymentReadings().Select(d => new DeploymentListReplyRow(
            d.Id, d.VersionId, d.Symbol, d.ConnectorId, d.AccountId, d.Mode, d.State, d.StartedAt,
            d.CursorOpenTime, d.SuspendedReason, d.EndedAt, d.EndReason, d.Operations, d.Unresolved,
            d.Line))]);

    /// <summary>
    /// ENDS ONE PAPER DEPLOYMENT. Cancels what it has working, closes what the RUN holds — its own book,
    /// never another run's or the owner's (<c>U-paper-books</c>) — records the reason, and leaves the
    /// allocation and the grant exactly where they were.
    ///
    /// <para>It is reachable from here, when starting one is not, because it only ever REMOVES
    /// exposure. A caller that has to have a role that may place orders (it IS in
    /// <c>Ops.Mutating</c>) can already send a <c>close</c>; this is that, plus the record saying the
    /// run is over.</para>
    ///
    /// <para><paramref name="req"/>'s <c>id</c> is the deployment, and an id this installation does
    /// not hold is a refusal rather than a no-op: "there is no such run" and "the run is now over"
    /// prescribe different next steps.</para>
    /// </summary>
    async Task<object> DeploymentStop(AgentContext ctx, IpcRequest req, CancellationToken ct)
    {
        var id = Require(req, "id");
        var who = ctx.Role is { Length: > 0 } role ? CouncilRoles.Title(role) : ctx.SessionId;

        if (gateway.Deployments.ById(id) is null)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"there is no deployment {Short(id)} on this installation. 'deployment-list' says "
                + "what there is.");

        var ended = await gateway.EndPaperDeploymentAsync(id, $"stopped by {who}", ct)
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"deployment {Short(id)} could not be read back after being ended");

        var reading = gateway.DeploymentReadings().FirstOrDefault(d => d.Id == ended.Id);
        return new DeploymentListReplyRow(
            ended.Id, ended.VersionId, ended.Symbol, ended.ConnectorId, ended.AccountId, ended.Mode,
            ended.State, ended.StartedAt, ended.CursorOpenTime, ended.SuspendedReason, ended.EndedAt,
            ended.EndReason, reading?.Operations ?? 0, reading?.Unresolved ?? 0,
            reading?.Line ?? ended.State);
    }

    /// <summary>What <c>deployment-list</c> answers with. The sentence is part of the reply, not a doc comment.</summary>
    sealed record DeploymentListReply(string WhatThisIs, IReadOnlyList<DeploymentListReplyRow> Deployments);

    /// <inheritdoc cref="DeploymentListReply"/>
    sealed record DeploymentListReplyRow(
        string Id, string VersionId, string Symbol, string ConnectorId, string AccountId, string Mode,
        string State, DateTimeOffset StartedAt, DateTimeOffset? CursorOpenTime, string? SuspendedReason,
        DateTimeOffset? EndedAt, string? EndReason, int Operations, int Unresolved, string Says);

    object VenueList()
    {
        var venues = gateway.Venues.Venues();
        var instruments = gateway.Venues.Instruments();

        return new VenueListReply(
            venues.Count,
            instruments.Count,
            "What TradeAgent knows about the instruments it can be asked about, and nothing more. "
            + "'verified' false means NOTHING has confirmed that row against the venue's own instrument "
            + "definition — the numbers are what this build shipped, and a backtest that does not "
            + "declare its own increment is REFUSED over an unverified or unknown instrument rather "
            + "than run on a guess. 'verified' true with a 'checked_at' means TradeAgent itself read the "
            + "venue's own published instrument definition, from the venue's own address, within the last "
            + "seven days: these are the venue's numbers, 'check_url' says where they were read, and a "
            + "shipped number that disagrees is kept beside them as 'catalogue_tick_size' or "
            + "'catalogue_quantity_increment'. 'says' is the one line that states whether a row is "
            + "verified and why not. 'quantity_increment' is what a size is rounded DOWN to and "
            + "'tick_size' is the price grid. There is NO fee and NO minimum notional in the catalogue "
            + "itself: fees are declared per backtest and are part of that run's identity, and a fee read "
            + "out of a table nobody measured would read as a measurement; a checked row's 'min_quantity' "
            + "and 'min_notional' are the venue's published minimums, recorded and NOT applied. "
            + "'calendar_kind' is 'continuous' for a venue that never closes; 'sessioned' means it closes "
            + "and TradeAgent does not hold the table that says when, so it is a refusal to guess and not "
            + "a calendar. You cannot write any of this, and nothing here asks for a check: TradeAgent "
            + "checks the account owner's market-data pair itself.",
            gateway.Venues.Unreadable,
            [.. venues.Select(v => new VenueListReplyVenue(
                v.Id, v.DisplayName, v.CalendarKind, v.Source, v.RecordedAt, v.Verified,
                [.. instruments.Where(i => i.VenueId == v.Id).Select(i => new VenueListReplyInstrument(
                    i.Symbol, i.TickSize, i.QuantityIncrement, i.Source, i.RecordedAt, i.Verified)
                {
                    // THE SERVED ROW'S CHECK, WHEN ONE STANDS (U-venue-verify): where and when the venue's
                    // numbers were read, the minimums it published, and a shipped number that disagrees.
                    CheckedAt = i.Check?.ReceivedAt,
                    CheckUrl = i.Check?.Url,
                    MinQuantity = i.Check?.MinQuantity,
                    MinNotional = i.Check?.MinNotional,
                    CatalogueTickSize = i.CatalogueTickSize,
                    CatalogueQuantityIncrement = i.CatalogueQuantityIncrement,
                    Says = gateway.Venues.Verification(v.Id, i.Symbol).Says
                })]))]);
    }

    /// <summary>
    /// DECLARED TYPES, for the reason <see cref="DataListReply"/> is one: <c>Json.Options</c> drops a
    /// null field, and an absent <c>unreadable</c> must read as "the catalogue is fine" rather than as
    /// a field this build does not have.
    /// </summary>
    sealed record VenueListReply(
        int VenueCount, int InstrumentCount, string Note,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Unreadable,
        IReadOnlyList<VenueListReplyVenue> Venues);

    /// <inheritdoc cref="VenueListReply"/>
    sealed record VenueListReplyVenue(
        string Id, string DisplayName, string CalendarKind, string Source,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? RecordedAt,
        bool Verified, IReadOnlyList<VenueListReplyInstrument> Instruments);

    /// <inheritdoc cref="VenueListReply"/>
    sealed record VenueListReplyInstrument(
        string Symbol, decimal TickSize, decimal QuantityIncrement, string Source,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? RecordedAt,
        bool Verified)
    {
        /// <summary>When the check being served was answered, or absent because none is (<c>U-venue-verify</c>).</summary>
        public DateTimeOffset? CheckedAt { get; init; }

        /// <summary>The address that check read, or absent.</summary>
        public string? CheckUrl { get; init; }

        /// <summary>The venue's published minimum quantity, from the check. Recorded, not applied.</summary>
        public decimal? MinQuantity { get; init; }

        /// <summary>The venue's published minimum order value, from the check. Recorded, not applied.</summary>
        public decimal? MinNotional { get; init; }

        /// <summary>TradeAgent's shipped tick where it disagrees with the venue's, or absent.</summary>
        public decimal? CatalogueTickSize { get; init; }

        /// <summary>TradeAgent's shipped step where it disagrees with the venue's, or absent.</summary>
        public decimal? CatalogueQuantityIncrement { get; init; }

        /// <summary>Whether this row is verified right now and why not, in the Market data card's own words.</summary>
        public string Says { get; init; } = "";
    }

    /// <summary>What TradeAgent observed on disk, plus the notes already recorded against it.</summary>
    object MaterialList(IpcRequest req)
    {
        MaterialOrigin? origin = req.Str("origin")?.ToLowerInvariant() switch
        {
            "inbox" => MaterialOrigin.Inbox,
            // A separate word, not a synonym for `inbox`: these are the rows TradeAgent could not
            // attribute to the account owner because it could not show that no agent process was
            // running when the file appeared. Asking for `inbox` and getting them back would be the
            // ledger conceding the very claim it declines to make.
            "inbox-unattested" => MaterialOrigin.InboxUnattested,
            "agent" => MaterialOrigin.Agent,
            // Files TradeAgent itself wrote into the role's home. A separate word for the same
            // reason `inbox-unattested` is one: folding them into `agent` would have the ledger
            // credit the AI with the app's own reference, examples and deliveries.
            "app" => MaterialOrigin.App,
            null or "" or "all" => null,
            var other => throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"origin '{other}' is not one of: inbox, inbox-unattested, agent, app, all")
        };

        var items = gateway.Materials.Present(origin);
        return new
        {
            count = items.Count,
            note = "sha is the first 12 characters of sha256, and is what the material commands accept. A shorter prefix works only while it names exactly one file; one that names two is refused rather than guessed at. "
                + "origin 'app' reads: written by TradeAgent — the strategy language reference, the worked programs, and anything delivered into in/. It is measured by path and hash, it is never counted as your work, and editing one of those files makes the new version 'agent', which is yours.",
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
        // Through the same reader as `tif`, so the rule is one rule. It was `Enum.TryParse`, which
        // refuses a misspelling and ACCEPTS A NUMBER: `kind: "1"` was a `used` note nobody wrote the
        // word for, and `kind: "99"` an undefined value stored in the ledger as itself.
        var kind = NamedValue<MaterialNoteKind>(req, "kind") ?? MaterialNoteKind.Note;

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

    // ---- the research ledger (U-research-ledger) -------------------------------------------------------------------

    /// <summary>
    /// ONE ENTRY IN THE RESEARCH LEDGER, WRITTEN AS ITS AUTHOR'S CLAIM, under the role and the attempt the launch grant
    /// proved — never a name the frame carries.
    ///
    /// <para>What it can write is what <see cref="ResearchLedger.Add"/> takes and nothing else: an argument the op does not
    /// declare is refused first (<see cref="OnlyDeclared"/>), so a <c>run</c>, a <c>link</c> or a <c>promotion</c> sent
    /// beside the claim is never quietly dropped while the claim is written as though it had been read. The mark is one of
    /// the three and never "measured"; a confidence left out is recorded as unknown.</para>
    /// </summary>
    object LedgerAdd(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req);
        var author = LedgerAuthor(ctx);
        var entry = gateway.Ledger.Add(author, ctx.AttemptId, Require(req, "kind"), Require(req, "text"),
            Require(req, "mark"), Confidence(req), LedgerId(req, "about"), req.Str("source"));
        return Written(entry, gateway.Ledger.Latest(entry.Id)!);
    }

    /// <summary>
    /// THE NEXT REVISION OF AN ENTRY THE CALLER'S OWN ROLE WROTE. Another role's is refused by the store, in words naming
    /// the way to disagree — an entry of one's own about it. The revisions before it are never touched.
    /// </summary>
    object LedgerRevise(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req);
        var author = LedgerAuthor(ctx);
        var entry = LedgerId(req, "entry")
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'entry' is required: the id of the entry to revise, as 'ledger-add' or 'ledger-list' answered it.");
        var revision = gateway.Ledger.Revise(author, ctx.AttemptId, entry, Require(req, "text"), Require(req, "mark"),
            Require(req, "why"), Confidence(req), req.Str("status"));
        return Written(gateway.Ledger.Entry(entry)!, revision);
    }

    /// <summary>
    /// EVERY ROLE'S ENTRIES, NEWEST FIRST, BOUNDED TWICE AND NEVER SILENTLY: at most the limit asked for and
    /// <see cref="ResearchLedger.MaxReadBytes"/> of them as the wire writes them, an entry never split — the first is served
    /// whatever it costs — and an answer either bound stopped says which, with the <c>before</c> that continues it.
    /// </summary>
    object LedgerList(IpcRequest req)
    {
        OnlyDeclared(req);
        var limit = LedgerLimit(req);
        var author = req.Str("author");
        var kind = req.Str("kind");
        var status = req.Str("status");
        var before = LedgerId(req, "before");

        var rows = gateway.Ledger.List(author, kind, status, before, limit);
        var entries = new List<LedgerListReplyEntry>();
        long spent = 0;
        string? cappedBy = null;
        foreach (var row in rows)
        {
            if (entries.Count == limit) { cappedBy = TapeReader.CappedByLimit; break; }
            var shaped = ListEntry(row);
            var cost = Encoding.UTF8.GetByteCount(Json.Write(shaped));
            if (entries.Count > 0 && spent + cost > ResearchLedger.MaxReadBytes) { cappedBy = TapeReader.CappedByBytes; break; }
            entries.Add(shaped);
            spent += cost;
        }

        var more = cappedBy is not null;
        return new LedgerListReply(author, kind, status, before, limit, entries.Count, more, cappedBy,
            more ? entries[^1].Entry : null, LedgerListNote, entries);
    }

    /// <summary>
    /// ONE ENTRY IN FULL: its revisions newest first from before <c>before</c> — bounded by
    /// <see cref="ResearchLedger.MaxRevisions"/> and by what is left of <see cref="ResearchLedger.MaxReadBytes"/> once the
    /// rest of the answer is written, a revision never split — its links, the entries about it, and the promotion and
    /// deployment ids of the versions its linked runs ran, read now and never stored. Ids, never a figure.
    /// </summary>
    object LedgerShow(IpcRequest req)
    {
        OnlyDeclared(req);
        var entry = LedgerId(req, "entry")
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'entry' is required: the id of the entry to show, as 'ledger-add' or 'ledger-list' answered it.");
        var before = LedgerId(req, "before");
        if (before is > int.MaxValue)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'before' is a revision number, and no entry has {before.Value.ToString(CultureInfo.InvariantCulture)} revisions.");

        var shown = gateway.Ledger.Show(entry, (int?)before)
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"there is no entry {entry.ToString(CultureInfo.InvariantCulture)} in the research ledger — 'ledger-list' names them.");

        var e = shown.Entry;
        var links = shown.Links.Select(l => new LedgerReplyLink(l.RecordKind, l.RecordId, l.Revision, l.Attempt, l.At)).ToList();
        var about = shown.About.Select(a => new LedgerReplyAbout(a.Id, a.Kind, a.Author, a.Revision, a.Mark, a.Status, a.CreatedAt)).ToList();
        var derived = new LedgerReplyDerived(shown.Versions, shown.Promotions, shown.Deployments);

        // THE REST OF THE ANSWER FIRST, so the revisions are bounded by what is left of the budget.
        var frame = new LedgerShowReply(e.Id, e.Kind, e.Author, e.About, e.Source, e.CreatedAt, e.Attempt,
            shown.RevisionCount, before, 0, false, null, null, shown.LinkCount, links, shown.AboutCount, about, derived,
            LedgerShowNote, []);
        long spent = Encoding.UTF8.GetByteCount(Json.Write(frame));

        var revisions = new List<LedgerReplyRevision>();
        string? cappedBy = null;
        foreach (var r in shown.Revisions)
        {
            if (revisions.Count == ResearchLedger.MaxRevisions) { cappedBy = TapeReader.CappedByLimit; break; }
            var shaped = Revision(r);
            var cost = Encoding.UTF8.GetByteCount(Json.Write(shaped));
            if (revisions.Count > 0 && spent + cost > ResearchLedger.MaxReadBytes) { cappedBy = TapeReader.CappedByBytes; break; }
            revisions.Add(shaped);
            spent += cost;
        }

        var more = cappedBy is not null;
        return frame with
        {
            Count = revisions.Count, More = more, CappedBy = cappedBy, NextBefore = more ? revisions[^1].Revision : null,
            Revisions = revisions
        };
    }

    /// <summary>
    /// EVERY ARGUMENT THE OP DOES NOT DECLARE IS REFUSED, and nothing is written or read.
    ///
    /// <para><see cref="GatewaySchema.ArgSpec"/> is read nowhere but the schema, so every other handler ignores a name it
    /// does not read. For the ledger that would be the defect itself: a <c>run</c> or a <c>link</c> sent beside a claim and
    /// silently dropped is a link the agent believes it wrote and no table holds — and a link is the app's to write. So the
    /// four ledger ops refuse what they do not take, in words naming what they do take and where a link comes from.</para>
    /// </summary>
    static void OnlyDeclared(IpcRequest req)
    {
        if (req.Args is not { Count: > 0 } args) return;
        var spec = GatewaySchema.Ops().Single(o => o.Op == req.Op);
        var undeclared = args.Keys.Where(k => spec.Args.All(a => a.Name != k)).Order(StringComparer.Ordinal).ToList();
        if (undeclared.Count == 0) return;

        throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
            $"{string.Join(", ", undeclared.Select(k => $"'{k}'"))} {(undeclared.Count == 1 ? "is not an argument" : "are not arguments")} "
            + $"of '{req.Op}', which takes {string.Join(", ", spec.Args.Select(a => a.Name))} — TradeAgent refuses an argument "
            + "rather than act as though it had read it. A link between an entry and a run or a verdict is TradeAgent's "
            + "alone to write: ask for the run with 'backtest' or the verdict with 'verdict' under the entry ('entry'), and "
            + "TradeAgent links the record it answered with. Nothing was written.");
    }

    /// <summary>
    /// THE ROLE AN ENTRY IS WRITTEN UNDER: the council role the caller's launch grant proved, or a refusal — the rule
    /// <see cref="Backtests.RoleOf"/> applies to a run (<see cref="CouncilRoles.IsKnown"/>), in the ledger's own words. A
    /// connection that proved no role is nobody, and an in-process operator is not a council role.
    /// </summary>
    static string LedgerAuthor(AgentContext ctx) =>
        CouncilRoles.IsKnown(ctx.Role)
            ? ctx.Role!
            : throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "an entry is written under the role whose launch wrote it, and this connection presented no launch grant — "
                + "so there is no role to write it under. TradeAgent puts the grant in the environment of the process it "
                + "starts; a caller holding only the machine token is authenticated and is nobody. Nothing was written.");

    /// <summary>
    /// THE LEDGER ENTRY A RUN OR A VERDICT IS ASKED UNDER, AS CHECKED, or null because the frame names none. The entry must be
    /// in the ledger and <paramref name="role"/>'s own; a refusal says so and nothing has been run or charged.
    /// </summary>
    LedgerAsk? LedgerEntry(AgentContext ctx, string role, IpcRequest req) =>
        LedgerId(req, "entry") is { } entry ? gateway.Ledger.Ask(entry, role) : null;

    /// <summary>
    /// A CONFIDENCE AS STATED, OR AN UNKNOWN. Absent, JSON <c>null</c> or the word <c>unknown</c> — what a read prints for
    /// one — is not stated, and that is recorded as unknown and never refused; anything else must be a number the strict
    /// reader takes, and the store refuses one outside 0 to 1.
    /// </summary>
    static decimal? Confidence(IpcRequest req)
    {
        if (req.Args is null || !req.Args.TryGetValue("confidence", out var raw)) return null;
        if (raw.ValueKind == JsonValueKind.Null) return null;
        if (raw.ValueKind == JsonValueKind.String && string.Equals(raw.GetString()?.Trim(), Unknown, StringComparison.OrdinalIgnoreCase))
            return null;
        return req.Dec("confidence");
    }

    /// <summary>What a read says for a confidence its author did not state.</summary>
    const string Unknown = "unknown";

    /// <summary>A confidence as a read says it: the author's own number as stated, or <see cref="Unknown"/>.</summary>
    static string Said(decimal? confidence) =>
        confidence is { } p ? p.ToString(CultureInfo.InvariantCulture) : Unknown;

    /// <summary>
    /// AN ENTRY'S ID OR A CURSOR, a whole number above 0, or null because the frame did not name it. Present and unreadable
    /// is refused — the rule every other number on this wire follows.
    /// </summary>
    static long? LedgerId(IpcRequest req, string key)
    {
        if (req.Args is null || !req.Args.ContainsKey(key)) return null;
        var raw = req.Dec(key)!.Value;
        if (raw != decimal.Truncate(raw) || raw < 1m || raw > long.MaxValue)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{key}' is an id — a whole number above 0 — and '{raw.ToString(CultureInfo.InvariantCulture)}' is not one. "
                + "'ledger-list' names the entries.");
        return (long)raw;
    }

    /// <summary>How many entries a list may hold: absent is the most; present, 1 to it, and anything else is refused.</summary>
    static int LedgerLimit(IpcRequest req)
    {
        if (req.Args is null || !req.Args.ContainsKey("limit")) return ResearchLedger.MaxEntries;
        var raw = req.Dec("limit")!.Value;
        if (raw != decimal.Truncate(raw) || raw < 1m || raw > ResearchLedger.MaxEntries)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'limit' is how many entries one answer may hold — a whole number from 1 to {ResearchLedger.MaxEntries} — "
                + $"and '{raw.ToString(CultureInfo.InvariantCulture)}' is not one. Ask for fewer and continue with 'before', "
                + "which every answer that stopped early hands back; TradeAgent does not clamp it for you.");
        return (int)raw;
    }

    static LedgerWriteReply Written(LedgerEntryRow entry, LedgerRevisionRow revision) =>
        new(entry.Id, revision.Revision, entry.Kind, entry.Author, revision.Attempt, entry.About, revision.Mark,
            Said(revision.Confidence), revision.Status, LedgerWrittenNote);

    static LedgerListReplyEntry ListEntry(LedgerListed row) =>
        new(row.Entry.Id, row.Entry.Kind, row.Entry.Author, row.Entry.About, row.Entry.Source, row.Entry.CreatedAt,
            row.Entry.Attempt, row.Latest.Revision, row.Latest.At, row.Latest.Text, row.Latest.Mark,
            Said(row.Latest.Confidence), row.Latest.Status, row.Latest.Why, row.Revisions, row.Links);

    static LedgerReplyRevision Revision(LedgerRevisionRow r) =>
        new(r.Revision, r.At, r.Attempt, r.Text, r.Mark, Said(r.Confidence), r.Status, r.Why);

    /// <summary>What a written entry or revision says about itself, once.</summary>
    const string LedgerWrittenNote =
        "Recorded as YOUR CLAIM, under your role and this attempt — never a measurement. A confidence you did not state "
        + "reads 'unknown'. Revisions are kept: 'ledger-revise' adds the next, and nothing edits or deletes one. Ask for a "
        + "run or a verdict under this entry ('entry') and TradeAgent links the record it answered with.";

    /// <summary>What a list says about itself, once.</summary>
    static readonly string LedgerListNote =
        "ENTRIES — every role's, newest first, each with its newest revision. An entry is its author's claim, assumption or "
        + "hypothesis and never a measurement; 'confidence' is the author's own number as stated, or 'unknown' where none "
        + "was stated. 'links' counts the records TradeAgent linked to it — runs and verdicts it answered requests asked "
        + $"under it with; 'ledger-show' names them. At most {ResearchLedger.MaxEntries} entries and "
        + $"{ResearchLedger.MaxReadBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes of them a call: when 'more' is "
        + "true the answer stopped there — 'capped_by' says which bound — and asking again with 'before' set to "
        + "'next_before' continues exactly.";

    /// <summary>What a show says about itself, once.</summary>
    static readonly string LedgerShowNote =
        "ONE ENTRY — its author's claim, never a measurement — with its revisions newest first, each as its author said it "
        + "then. A LINK is TradeAgent's: it says TradeAgent answered a request asked under this entry, at that revision, for "
        + "that attempt, with that record — a 'run' or a 'promotion', by id — and not that the record supports the entry or "
        + "that every record bearing on it is linked. 'derived' is read now from the linked runs and never stored: the "
        + "versions they ran, and those versions' promotion and deployment ids. Ids only, never a figure. At most "
        + $"{ResearchLedger.MaxRevisions} revisions and {ResearchLedger.MaxReadBytes.ToString("N0", CultureInfo.InvariantCulture)} "
        + "bytes a call: when 'more' is true, asking again with 'before' set to 'next_before' continues the revisions "
        + $"exactly; 'links' and 'about_it' are the newest {ResearchLedger.MaxLinks} of each, beside how many there are.";

    /// <summary>
    /// <inheritdoc cref="LedgerAdd"/>
    ///
    /// <para>DECLARED TYPES AND NEVER AN ANONYMOUS OBJECT, so a test asserts the whole of what crosses and a field cannot
    /// arrive under a new name unseen. A null is kept, never dropped: an entry about nothing says so.</para>
    /// </summary>
    sealed record LedgerWriteReply(
        long Entry, int Revision, string Kind, string Author,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Attempt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? About,
        string Mark, string Confidence, string Status, string Note);

    /// <inheritdoc cref="LedgerList"/>
    sealed record LedgerListReply(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Author,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Before,
        int Limit, int Count, bool More,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CappedBy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? NextBefore,
        string Note, IReadOnlyList<LedgerListReplyEntry> Entries);

    /// <inheritdoc cref="LedgerList"/>
    sealed record LedgerListReplyEntry(
        long Entry, string Kind, string Author,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? About,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Source,
        DateTimeOffset CreatedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Attempt,
        int Revision, DateTimeOffset At, string Text, string Mark, string Confidence, string Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Why,
        int Revisions, int Links);

    /// <inheritdoc cref="LedgerShow"/>
    sealed record LedgerShowReply(
        long Entry, string Kind, string Author,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? About,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Source,
        DateTimeOffset CreatedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Attempt,
        int RevisionCount,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Before,
        int Count, bool More,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CappedBy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? NextBefore,
        int LinkCount, IReadOnlyList<LedgerReplyLink> Links,
        int AboutCount, IReadOnlyList<LedgerReplyAbout> AboutIt,
        LedgerReplyDerived Derived, string Note, IReadOnlyList<LedgerReplyRevision> Revisions);

    /// <inheritdoc cref="LedgerShow"/>
    sealed record LedgerReplyRevision(
        int Revision, DateTimeOffset At,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Attempt,
        string Text, string Mark, string Confidence, string Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Why);

    /// <inheritdoc cref="LedgerShow"/>
    sealed record LedgerReplyLink(
        string Kind, string Id, int Revision,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Attempt,
        DateTimeOffset At);

    /// <inheritdoc cref="LedgerShow"/>
    sealed record LedgerReplyAbout(long Entry, string Kind, string Author, int Revision, string Mark, string Status,
        DateTimeOffset CreatedAt);

    /// <inheritdoc cref="LedgerShow"/>
    sealed record LedgerReplyDerived(IReadOnlyList<string> Versions, IReadOnlyList<string> Promotions,
        IReadOnlyList<string> Deployments);

    /// <summary>
    /// Whether a frame this build could not read is one that never named its version. Asked only on
    /// the failure path, so the cost is paid by frames that are already being refused.
    ///
    /// It re-parses rather than trusting the deserializer's message, because the message is a
    /// framework string and a refusal reason is a contract. A document that is not valid JSON at all
    /// answers false: it is not a versionless frame, it is not a frame.
    /// </summary>
    static bool NamesNoVersion(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && !doc.RootElement.TryGetProperty("v", out _);
        }
        catch (JsonException) { return false; }
    }

    static string Require(IpcRequest r, string key) =>
        r.Str(key) ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{key}' is required");

    /// <summary>
    /// AN ENUMERATED FIELD ACCEPTS EXACTLY ITS NAMES, OR NOTHING. Absent keeps the caller's default;
    /// present-and-unrecognised is a refusal, never a substitution.
    ///
    /// It replaces <c>Enum.TryParse(…, out var t) ? t : Default</c>, which failed open in two
    /// separate ways at once (Codex F8). A MISSPELLING fell through to the default, so
    /// <c>tif: "ImmediateOrCancle"</c> — one transposition — became a resting Day order instead of
    /// one that cancels if it cannot fill immediately. And a NUMBER succeeded: TryParse accepts the
    /// underlying integer whether or not the enum defines it, so <c>tif: "999"</c> was carried to
    /// the connector as <c>(TimeInForce)999</c>, a value no backend can map and none is obliged to
    /// refuse. Both measured over the real pipe: `999` reached the connector as `999 (999)`.
    ///
    /// Names only, therefore, matched case-insensitively against <c>Enum.GetNames</c> — never
    /// <c>TryParse</c>, and never trimmed, because "a value with a stray character in it" and "a
    /// value the caller meant" are not distinguishable from here.
    /// </summary>
    static T? NamedValue<T>(IpcRequest r, string key) where T : struct, Enum
    {
        // ABSENT IS THE ONLY THING THAT KEEPS THE DEFAULT (Codex F6). This asked whether the VALUE
        // was empty, so the one shape that is neither a name nor an absence — the field present and
        // blank — fell through the refusal and took the default with it: `tif: ""` was a resting Day
        // order and `tif: null` reads the same way through Str. Measured over the real pipe before
        // this changed: `tif='' -> ok=True · connector saw: Day`.
        if (r.Args is null || !r.Args.ContainsKey(key)) return null;
        var raw = r.Str(key);
        foreach (var name in Enum.GetNames<T>())
            if (string.Equals(name, raw, StringComparison.OrdinalIgnoreCase)) return Enum.Parse<T>(name);

        throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
            $"'{key}' must be one of: {string.Join(", ", Enum.GetNames<T>())} — '{raw}' is none of them. " +
            "TradeAgent does not fall back to a default here: a value it cannot name would become an order " +
            "you did not ask for.");
    }

    /// <summary>
    /// The same rule for the one boolean the frame carries. <c>Str(key) is "true"</c> read every
    /// other spelling as false, so <c>all: "yes"</c> silently meant "working orders only" — the
    /// opposite of the question — and nothing said so.
    /// </summary>
    static bool NamedFlag(IpcRequest r, string key, bool whenAbsent)
    {
        // Same rule and the same hole as NamedValue above: `all: ""` asked a question and was
        // answered with the default rather than refused.
        if (r.Args is null || !r.Args.ContainsKey(key)) return whenAbsent;
        var raw = r.Str(key);
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
            $"'{key}' must be true or false — '{raw}' is neither, and reading it as either would answer " +
            "a different question from the one you asked.");
    }

    /// <summary>
    /// A field this protocol does not carry, refused rather than ignored.
    ///
    /// <c>side</c> and <c>type</c> LOOK like fields of an order — every trading API has them — and
    /// this one derives both: the side is the op (<c>buy</c> / <c>sell</c>) and the type is read off
    /// which prices are present. A frame naming either was accepted and discarded, so
    /// <c>{"op":"buy","side":"sell"}</c> bought and <c>{"op":"buy","type":"Limit"}</c> with no limit
    /// price sent a MARKET order. That is the same failure as an unrecognised enum value, arriving
    /// by the other door, and it deserves the same answer.
    /// </summary>
    static void RefuseDerivedField(IpcRequest r, string key, string where)
    {
        if (r.Args is null || !r.Args.ContainsKey(key)) return;
        throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
            $"'{key}' is not a field of this protocol, so naming it would be ignored rather than obeyed. " +
            $"It comes from {where}. Send the frame without it.");
    }

    static PlaceIntent ParsePlace(IpcRequest r)
    {
        RefuseDerivedField(r, "side", "the operation itself: 'buy' or 'sell'");
        RefuseDerivedField(r, "type", "which prices you send: neither = Market, 'limit' = Limit, 'stop' = Stop, both = StopLimit");

        var symbol = Require(r, "symbol");
        var qty = r.Dec("quantity") ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "'quantity' is required");
        var limit = r.Dec("limit");
        var stop = r.Dec("stop");
        var type = limit is not null && stop is not null ? OrderType.StopLimit
            : limit is not null ? OrderType.Limit
            : stop is not null ? OrderType.Stop
            : OrderType.Market;
        var tif = NamedValue<TimeInForce>(r, "tif") ?? TimeInForce.Day;
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

        // 0. Stop listening for endings. The register outlives this server — AgentGrants.Shared is
        //    process-wide — so a subscription left behind would keep every disposed server, and the
        //    gateway and database behind it, alive for the life of the process. A no-op if Start
        //    was never called.
        Grants.Ended -= OnGrantEnded;

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
        //    It is skipped when there is nothing to wait for; the REPORT below is not, because the
        //    report is about REQUESTS.
        //
        //    BOUNDED BY `WriteBackAfterCancel` AND NOT BY THE RAW SETTING. At a settle margin of zero
        //    this waited for nothing at all, so a handler that unwinds correctly and settles UNKNOWN
        //    a continuation later was reported unsettled and left DISPATCHING when disposal returned
        //    — the very state steps 3 to 5 exist to prevent, produced by configuring the margin away.
        if (handlers.Length > 0)
        {
            try { await Task.WhenAll(handlers).WaitAsync(WriteBackAfterCancel); }
            catch (Exception) { /* the report below is what matters */ }
        }

        // 6. AND THE REPORT, UNCONDITIONALLY — there is no `if` in front of it, and that is the
        //    whole of this step rather than an accident of where it sits.
        //
        //    COUNTED AFTER THE UNWIND, and counted on the STATE rather than on the symptom. It
        //    counted handler TASKS still running, which is not what this line is about. A connector
        //    that HONOURS its cancellation token unwinds the instant disposal cancels it, so the
        //    handler finishes — while `TradingGateway.ModifyAsync` catches only
        //    `ConnectorRejectedException` and `ConnectorTransportException` and lets a cancellation
        //    or a timeout escape, leaving the row DISPATCHING and unflagged. `ReconcileAsync` scans
        //    `NeedingReconciliation()` alone, so nothing will ever settle it, and the only trace an
        //    operator gets said nothing at all (verifier round-9 F-2, measured: `DISPATCHING rows =
        //    1`, `handlers_did_not_finish = (not logged)`).
        //
        //    ROUND 10 FIXED THE COUNT AND LEFT THE GUARD, and the guard is the same defect one level
        //    up. `_handlers` holds per-CONNECTION tasks, each of which removes itself on completion,
        //    read AFTER step 2 disposed every connection — so `handlers.Length` is a fact about
        //    whether an AGENT WAS STILL ATTACHED, and the promise not to return silently was
        //    conditioned on it. In the most ordinary shutdown there is — the agent CLI exits, the
        //    operator then closes the app — it is zero, and disposal returned in 3 ms with an
        //    unsettled DISPATCHING row and nothing logged (verifier round-11 F-1, measured with two
        //    probes differing in that one thing).
        //
        //    The query is about REQUESTS, so it runs whatever became of the connections that made
        //    them. Both facts are reported and either one fires it; the requests are NAMED, because
        //    "something was abandoned" is not something anybody can act on. Settling the row belongs
        //    to whoever owns the request — routed to U2c-1; refusing to return silently belongs here.
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
                    settle_timeout_ms = (int)WriteBackAfterCancel.TotalMilliseconds
                }));

        _cts.Dispose();
        _accept.Dispose();
    }
}
