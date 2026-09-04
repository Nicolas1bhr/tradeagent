using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
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
public sealed class AtasConnector(string? pipeName = null, TimeSpan? rpcTimeout = null, BridgeCredential? credential = null)
    : ITradingConnector, IConnectorStatusDetail
{
    readonly string _pipe = pipeName ?? Paths.BridgePipeName;
    readonly TimeSpan _timeout = rpcTimeout ?? TimeSpan.FromSeconds(10);
    readonly BridgeCredential? _fixedCredential = credential;
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

    BridgeCredential? _credential;
    volatile bool _authenticated;

    /// <summary>
    /// THIS CONNECTION HAS BEEN REFUSED, AND THE DECISION IS THE CONNECTION'S RATHER THAN THE
    /// FRAME'S.
    ///
    /// Guarding one frame type at a time is how this kept reopening. The hello refusal was added,
    /// then events were let through, so events were gated; then a HEARTBEAT — which carries a whole
    /// <c>BridgeHello</c> — set the capabilities instead, one op to the left; and a second hello on
    /// the same connection cleared the refusal outright. Each fix was correct about its instance and
    /// left the class alone: <b>a peer this connector has refused is still allowed to speak.</b>
    ///
    /// So it is decided ONCE, at the top of <see cref="Dispatch"/>, for every frame there is and
    /// every frame anyone adds later. Nothing clears it but a new connection — <see cref="Drop"/>
    /// resets it — because the fault is the peer's identity, not the frame that revealed it.
    /// </summary>
    volatile bool _refused;

    /// <summary>
    /// A COMPATIBLE HELLO HAS BEEN ACCEPTED ON THIS CONNECTION — which is a different fact from
    /// "this peer proved itself". Authentication says the thing on the pipe holds the secret; it
    /// says nothing about what it speaks, and before a hello arrives nothing has been established at
    /// all. Events and heartbeat refreshes both reach outside this class, so both wait for this.
    /// </summary>
    volatile bool _compatible;
    DateTimeOffset _peerArrived = DateTimeOffset.MaxValue;
    UnauthenticatedBridge? _unauthenticated;
    string? _peerImage;

    /// <summary>
    /// WHICH OF THE TWO REFUSALS WAS OBSERVED MORE RECENTLY, and it exists because BOTH are now
    /// permanent. Round 7b made a protocol refusal last until a compatible hello repairs it; a
    /// credential refusal already lasted until a peer proved itself. Neither supersedes the other,
    /// and the row returned the protocol one first — the OLDER of the two — so the sequence an
    /// operator walks reads wrong at the moment they need it: refused on protocol, told to reinstall,
    /// the new DLL fails AUTHENTICATION, and the row still says "reinstall the add-on".
    ///
    /// A counter and not a clock: two observations in the same tick must still order, and nothing
    /// here needs to know how long ago anything was — only which came last. Zero means "not set",
    /// which is why <see cref="NoteIncompatible"/> and <see cref="NoteUnauthenticated"/> are the only
    /// way either marker moves. Assigning the field directly is how this class of bug returns.
    /// </summary>
    long _observed;
    long _incompatibleAt;
    long _unauthenticatedAt;

    void NoteIncompatible(IncompatibleBridge? peer)
    {
        _incompatible = peer;
        _incompatibleAt = peer is null ? 0 : Interlocked.Increment(ref _observed);
    }

    void NoteUnauthenticated(UnauthenticatedBridge? peer)
    {
        _unauthenticated = peer;
        _unauthenticatedAt = peer is null ? 0 : Interlocked.Increment(ref _observed);
    }

    public string Id => "atas";
    public string DisplayName => "ATAS";
    public BridgeHello? Bridge => _hello;

    /// <summary>
    /// Set when a bridge dialled in speaking a protocol version this build does not, and null
    /// otherwise. Display only — see <see cref="IncompatibleBridge"/>. It is deliberately a separate
    /// property from <see cref="Bridge"/> so that no capability can ever be read off it by accident.
    /// </summary>
    public IncompatibleBridge? Incompatible => _incompatible;

    /// <summary>
    /// Set when the peer on the bridge pipe has not proved it holds this installation's bridge
    /// secret, and null once it has. Display only, exactly like <see cref="Incompatible"/> — nothing
    /// derives a capability from it, and nothing here is what refuses the peer.
    ///
    /// THREE DIFFERENT PEERS END UP HERE AND THEY ARE NOT THE SAME NEWS.
    ///   - One presented a proof and it was wrong. Refused in <see cref="Answer"/>: the connection is
    ///     dropped and nothing it claimed is kept. In practice this is a stale or mismatched
    ///     <c>bridge.auth</c> — two installations, or a copied profile.
    ///   - One said hello having never presented a proof at all. Refused in <see cref="Dispatch"/>:
    ///     <c>_hello</c> stays null, so <see cref="Capabilities"/> keeps reporting nothing supported
    ///     and the gateway cannot trade on a single thing that peer claimed. That refusal turns on
    ///     <c>_authenticated</c> at the instant the hello lands, NOT on <see cref="AuthGrace"/>.
    ///   - One connected and has said nothing whatever — no challenge, no hello — for longer than
    ///     <see cref="AuthGrace"/>. That is the only reading below that is derived from a clock, and
    ///     it is the only one that refuses nothing: there is nothing yet to refuse.
    /// </summary>
    public UnauthenticatedBridge? Unauthenticated =>
        // An explicit refusal always wins; the derived reading below exists only for a peer that has
        // given us nothing to name it by. It is deliberately blind to a peer already explained by
        // _incompatible or already refused into _unauthenticated, so the two readings can never both
        // be live and disagree about what is wrong.
        _unauthenticated ?? (_incompatible is null && _hello is null && !_authenticated
                             && DateTimeOffset.UtcNow - _peerArrived > AuthGrace
            ? UnauthenticatedBridge.Silent
            : null);

    /// <summary>
    /// The full path of the program on the other end of the bridge pipe, as Windows reports it —
    /// not as the peer describes itself. Null off Windows, and null when Windows would not say.
    /// DIAGNOSTIC ONLY: this connector does not refuse a client on it. See
    /// <see cref="BridgePipeAuth"/>.
    /// </summary>
    public string? PeerImage => _peerImage;

    /// <summary>One line explaining a FAILED trading connection, or null when there is nothing to
    /// add. Read by the gateway for the health detail the dashboard shows.</summary>
    /// <summary>
    /// THE ROW DESCRIBES THE PEER THAT IS THERE NOW. Both refusals are permanent, so when both are
    /// set the newer observation is the one an operator can act on; the older stays RECORDED and
    /// readable through <see cref="Incompatible"/> and <see cref="Unauthenticated"/>, it simply
    /// stops outranking a later one. The derived Silent reading has no stamp and needs none: the
    /// <see cref="Unauthenticated"/> getter only produces it while <c>_incompatible</c> is null, so
    /// it can never be the loser of this comparison.
    /// </summary>
    public string? StatusDetail
    {
        get
        {
            var incompatible = _incompatible;
            var unauthenticated = Unauthenticated;
            if (incompatible is null) return unauthenticated?.ToString();
            if (unauthenticated is null) return incompatible.ToString();
            return _unauthenticatedAt > _incompatibleAt ? unauthenticated.ToString() : incompatible.ToString();
        }
    }

    /// <summary>
    /// How often the read loop looks up from a peer that is saying nothing. A third of the heartbeat
    /// timeout, so a peer that has gone quiet is dropped within about a third of it past the deadline,
    /// and floored so a test that sets a very short timeout does not spin.
    /// </summary>
    TimeSpan IdlePoll
    {
        get
        {
            var third = TimeSpan.FromMilliseconds(HeartbeatTimeout.TotalMilliseconds / 3);
            return third < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : third;
        }
    }

    /// <summary>
    /// Nothing has been heard from this peer inside the heartbeat timeout. Measured from the LATER of
    /// its arrival and its last heartbeat, so a peer still completing its handshake is given the same
    /// window as one that has been talking — and a peer that never says anything at all is dropped
    /// once that window is spent rather than held for ever.
    /// </summary>
    bool PeerHasGoneQuiet()
    {
        var arrived = _peerArrived == DateTimeOffset.MaxValue ? DateTimeOffset.MinValue : _peerArrived;
        var lastHeard = _lastHeartbeat > arrived ? _lastHeartbeat : arrived;
        return DateTimeOffset.UtcNow - lastHeard > HeartbeatTimeout;
    }

    /// <summary>
    /// Keeps an abandoned read's failure from surfacing as an unobserved task exception. It will fault
    /// as soon as the stream is disposed, which is the point — that is how the read is aborted.
    /// </summary>
    static void Observe(Task task) =>
        task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    /// <summary>Missing for longer than this and we treat the bridge as gone.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a peer may sit on the pipe saying nothing at all before the status row says so.
    ///
    /// IT GOVERNS A SENTENCE ON A SCREEN AND NOTHING ELSE. No refusal depends on it, and none may:
    /// the refusal of an unproved hello is decided by whether the proof arrived, which is a fact
    /// this end already holds by the time the hello is read, not by how long anything took.
    /// </summary>
    public TimeSpan AuthGrace { get; set; } = TimeSpan.FromSeconds(3);

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
            // Whether anything ever reached us this time round. It decides whether the loop pauses,
            // and the pause is the point: see the comment at the bottom of the loop.
            var accepted = false;
            try
            {
                // Republished on every iteration so the record always names the program that is
                // holding the pipe RIGHT NOW, and so the secret exists before anything can dial in.
                _credential = _fixedCredential ?? BridgePipeAuth.EnsureForServer();

                _pipeStream = CreateServer();
                await _pipeStream.WaitForConnectionAsync(ct);
                accepted = true;
                _peerArrived = DateTimeOffset.UtcNow;
                _peerImage = BridgePipeAuth.ClientImagePath(_pipeStream);

                var reader = new StreamReader(_pipeStream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
                _writer = new StreamWriter(_pipeStream, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

                // A PEER THAT STOPS TALKING IS DROPPED, BECAUSE IT HOLDS THE ONLY INSTANCE THERE IS.
                //
                // This used to sit inside ReadLineAsync for ever. The heartbeat timeout was consulted
                // only for what the row DISPLAYS, and the pipe is created with
                // maxNumberOfServerInstances = 1 which the loop below recreates only after this one
                // ends — so a bridge whose ATAS froze, or a peer that opened the pipe and stalled
                // before its first newline, kept every later bridge off the machine while the row
                // reported nothing worse than a stale connection. Same class as the refused peer
                // round 7 dropped, reached from the other side: a peer nobody can hear must not be
                // able to hold the trading path shut.
                //
                // THE PENDING READ IS NEVER RESTARTED AND NEVER CANCELLED. Abandoning a read to start
                // another would put two reads on one StreamReader and split a frame between them;
                // cancelling one mid-flight can consume bytes that are then lost. So the same task is
                // carried across polls, and staleness ends the loop — the finally below disposes the
                // stream, which is what actually aborts the read.
                // AND THE QUESTION IS ASKED ON EVERY TURN, WHICHEVER SIDE OF THE RACE WON.
                //
                // Round 8 asked it only when the delay beat the read. Those are two different
                // questions — "has this peer gone quiet" and "did nothing arrive in the last poll" —
                // and the gap between them is a hole the width of the whole guard: a peer that
                // completes ANY line more often than IdlePoll never lets the poll win, so it was
                // never asked, and it held the only server instance there is while this connector's
                // own health reported DEGRADED about it. A frame that is neither a hello nor a
                // heartbeat is not evidence of liveness under this class's own definition —
                // _lastHeartbeat is written by those two and by nothing else — so it must not be
                // able to buy a peer another window.
                //
                // AFTER THE DISPATCH, NOT BEFORE IT. The frame in hand may BE the heartbeat, and
                // Dispatch is what records it; asking first would drop a healthy bridge whose beat
                // arrived one tick late.
                Task<string?>? pending = null;
                while (!ct.IsCancellationRequested)
                {
                    pending ??= reader.ReadLineAsync(ct).AsTask();
                    var settled = await Task.WhenAny(pending, Task.Delay(IdlePoll, ct));
                    if (settled == pending)
                    {
                        var line = await pending;
                        pending = null;
                        if (line is null) break;
                        if (!await Dispatch(line)) break; // a peer we have refused gets no second frame
                    }

                    if (PeerHasGoneQuiet())
                    {
                        if (pending is not null) Observe(pending);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* the bridge died or ATAS closed; fall through and wait for it again */ }
            finally
            {
                Drop("the ATAS bridge disconnected");
                _pipeStream?.Dispose();
                _pipeStream = null;
            }

            // The pause used to be unconditional, which left the pipe NAME unowned for a whole
            // second after every disconnection — and whoever creates that name first owns it, since
            // there is exactly one server instance. A second is long enough to be polled for. After
            // a real session the name is retaken immediately; the pause is kept only for the case
            // where nothing connected, which is the only way this loop could spin.
            //
            // This narrows the window. It does not close it: TradeAgent starting and stopping still
            // leaves the name free, and a peer that got there first is what the authentication in
            // BridgePipeAuth is for.
            if (!accepted && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// The bridge pipe, locked to this user account on Windows.
    ///
    /// Parity with <c>GatewayPipeServer.CreateServer</c>, and it buys one concrete thing: the
    /// DEFAULT security descriptor for a named pipe grants read access to Everyone, so without this
    /// any other account on the machine could open this pipe and read the order flow, the account
    /// identifiers and the quotes off it. It does not decide who gets to CREATE the name — nothing
    /// in a pipe's descriptor can, because the descriptor only exists once the pipe does.
    /// </summary>
    NamedPipeServerStream CreateServer()
    {
        if (OperatingSystem.IsWindows())
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(id.User!, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(id.User!, PipeAccessRights.CreateNewInstance, System.Security.AccessControl.AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(_pipe, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, Buffer, Buffer, security);
        }
        return new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, Buffer, Buffer);
    }

    /// <summary>
    /// The pipe's buffer, and it was 0 until it was measured.
    ///
    /// A Windows named pipe created with no buffer completes a write only when the far end reads it,
    /// however small the frame — so every response and every heartbeat the bridge sends was coupled
    /// to this process reading promptly, with no slack at all. That is the same property that froze
    /// the bridge in the deadlock recorded on 2026-09-01: it is not only an adversary who can stop
    /// reading, a stalled reader does it too. 8 KiB is a hint to the kernel, not a contract, and it
    /// changes nothing about the protocol — a frame that fits simply no longer waits for a reader.
    /// The bridge's own write deadline stays the backstop for a peer that never reads at all.
    /// </summary>
    const int Buffer = 8192;

    void Drop(string why)
    {
        var was = _connected;
        // An incompatible bridge never set _connected, so 'was' alone would not fire — and the
        // dashboard would go on displaying "bridge 9.9.9 speaks protocol 2" about a bridge that is
        // no longer on the pipe. Clearing the reason without re-announcing the row leaves the model
        // and the screen disagreeing, which on a status display is the whole of the bug.
        var wasExplained = _incompatible is not null || Unauthenticated is not null;
        _connected = false;
        _hello = null;

        // A MISMATCH IS NOT CLEARED HERE EITHER, AND IT IS THE SAME ARGUMENT AS THE ONE BELOW.
        //
        // This used to clear it unconditionally, on the reading that a wrong version is a fact about
        // the peer and leaves with the peer. Round 7 kept it across the disconnection our own refusal
        // caused — necessary, because the refusal is what CAUSES that disconnection, so clearing it
        // there erased the reason microseconds after writing it. That left an edge: the NEXT Drop for
        // any other cause — an unrelated peer arriving and going, a silent hang-up — still wiped it,
        // so the row went blank with nothing repaired and nothing having replaced the bridge. The
        // operator is told to reinstall the add-on and the instruction disappears while they do it.
        //
        // So it is kept outright, exactly as a credential refusal is kept below and for the reason
        // stated there: it is not a fact that leaves with the peer, it is "the thing that holds this
        // pipe speaks a protocol this build cannot", and the event that ends it is a COMPATIBLE
        // HELLO — the accepted-hello branch clears it — not a disconnection. A peer proving itself
        // is what resolves the credential refusal; a peer speaking the right protocol is what
        // resolves this one. Neither is resolved by hanging up.
        //
        // The silent-peer reading is the one that DOES leave with the peer: it is derived from
        // _peerArrived, reset below, so a pipe with nobody on it stops claiming anybody is there.

        // A REFUSAL IS NOT CLEARED HERE, AND THAT IS THE DIFFERENCE BETWEEN THE TWO.
        //
        // A version mismatch is a fact about the peer, so it leaves with the peer. A refusal is a
        // fact about THIS INSTALLATION'S CREDENTIALS, and it is repaired by repairing them, not by
        // the peer hanging up. Worse, the refusal is what CAUSES the disconnection — so clearing it
        // here erased the reason microseconds after setting it, and the dashboard was left showing
        // FAILED with nothing on it while the bridge redialled every two seconds. Answer() clears
        // this the moment a peer proves itself, which is the only event that actually ends it.
        //
        // The refusal of an unproved hello is kept for the same reason and it is the sharper case:
        // that refusal is itself what closes the connection, so clearing it here would erase the
        // reason microseconds after writing it and leave the dashboard reading FAILED with nothing
        // on it — while the thing on the pipe redials. It is not a fact about the peer that leaves
        // with the peer; it is "something that is not this build's bridge has the pipe", which is
        // still true after it hangs up, and which a proved peer ends by proving itself.
        //
        // The silent-peer reading is the one that does leave with the peer: it is derived from
        // _peerArrived, reset below, so a pipe with nobody on it stops claiming anybody is there.
        _authenticated = false;
        // Both are facts about THIS connection and end with it, exactly as _incompatible does: a
        // fresh peer on a fresh connection is heard out from the beginning, and a repaired bridge
        // that reconnects is accepted on its first hello.
        _refused = false;
        _compatible = false;
        _peerArrived = DateTimeOffset.MaxValue;
        _peerImage = null;
        _writer = null;
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new ConnectorTransportException(why));
        if (was || wasExplained) ConnectionChanged?.Invoke(HealthState.FAILED);
    }

    /// <summary>Handles one frame. False means this peer gets no further hearing.</summary>
    async Task<bool> Dispatch(string line)
    {
        // ONE DECISION FOR THE WHOLE CONNECTION, ABOVE EVERY FRAME TYPE. See _refused: guarding one
        // op at a time is how this reopened three times — the hello was refused, then events got
        // through, then a heartbeat carrying a hello, then a second hello. A peer whose protocol
        // this build cannot speak now gets no further hearing of ANY kind, including from a frame
        // type nobody has written yet.
        //
        // DISCARDED, NOT DISCONNECTED. Returning false breaks the read loop, which runs Drop, which
        // clears _incompatible — so the version number and the repair vanish from the row a moment
        // after being written. The frame is dropped instead: the peer may talk all it likes and
        // nothing it says is read, which is the whole of what "refused" has to mean here.
        if (_refused) return true;

        BridgeFrame? f;
        try { f = Json.Read<BridgeFrame>(line); } catch (Exception) { return true; }
        if (f is null) return true;

        // The authentication frames are handled before anything else so that a peer which is about
        // to be refused cannot have its capabilities or its events read first.
        if (f.Op == BridgePipeAuth.Challenge) return await Answer(f);
        if (f.Op == BridgePipeAuth.Refused)
        {
            // The far end refused US. It is the bridge's own words, so it is clipped like any other
            // untrusted string on its way to a label — but it is the only thing that turns
            // "connected, then silence" into a sentence somebody can act on.
            NoteUnauthenticated(new UnauthenticatedBridge(
                $"the ATAS bridge refused this copy of TradeAgent: {IncompatibleBridge.Clean(f.Error)}"));
            _connected = false;
            _hello = null;
            ConnectionChanged?.Invoke(HealthState.FAILED);
            return false;
        }

        // NOTHING BELOW THIS LINE IS SERVED TO A PEER THAT HAS NOT PROVED ITSELF. The two frames
        // below are the ones that reach outside this class — an event is raised at TradingGateway,
        // and a heartbeat carries a whole BridgeHello — so both are gated on the same fact the hello
        // is. Ignored rather than refused: unlike a wrong proof, a frame arriving before the
        // challenge has been answered is not evidence of anything, and dropping the connection for
        // it would add a disconnect path that buys nothing. Discarding the frame already leaves the
        // peer with no effect on this process at all, and the status row still names it.
        // AND A REFUSED PEER RAISES NOTHING. This asked only whether the peer had authenticated, so a
        // version-2 bridge — which authenticates perfectly well, it is this product's own older DLL —
        // went on raising order, execution, position, account, quote and connection events into the
        // application while this same connector reported FAILED and refused it every RPC. "Refused
        // outright" was true of what this process ASKS and false of what it BELIEVES. A peer whose
        // protocol this build does not speak is refused as a connection, both directions.
        if (f.Event is not null)
        {
            // AUTHENTICATION IS NOT COMPATIBILITY. Proving the secret says what the peer HAS; it says
            // nothing about what it speaks, and before a hello arrives nothing has been established.
            if (_authenticated && _compatible) HandleEvent(f);
            return true;
        }

        if (f.Op == BridgeOps.Hello)
        {
            var hello = f.Data.HasValue ? f.Data.Value.Deserialize<BridgeHello>(Json.Options) : null;
            if (hello is null) return true;
            if (!Versions.BridgeCompatible(hello.BridgeProtocolVersion))
            {
                // A mismatched bridge is refused outright rather than half-trusted: _hello stays
                // null, so Capabilities keeps reporting nothing supported and the gateway cannot
                // trade on anything this bridge claims. Its IDENTITY is kept separately, because
                // "FAILED" with no version number is not a repairable message — and keeping it in a
                // different field is what stops it being mistaken for a capability later.
                NoteIncompatible(new IncompatibleBridge(
                    hello.BridgeProtocolVersion, Versions.BridgeProtocolVersion,
                    IncompatibleBridge.Clean(hello.BridgeVersion), IncompatibleBridge.Clean(hello.AtasVersion)));
                _connected = false;
                _hello = null;
                _compatible = false;

                // AND THE CONNECTION IS REFUSED, NOT THE FRAME. What used to happen was that this
                // returned true and nothing else changed, so every later frame was still READ: a
                // heartbeat carrying a whole hello set the capabilities back, and a second hello
                // cleared the refusal outright. _refused ends that above, for every frame type at
                // once and for any frame type added later.
                _refused = true;
                ConnectionChanged?.Invoke(HealthState.FAILED);

                // AND IT IS DROPPED, NOT PARKED, WHICH ROUND 7 REVERSED.
                //
                // Round 6 kept the peer here so that Drop could not erase the version number and the
                // repair from the row. The pipe is created with maxNumberOfServerInstances = 1 (see
                // CreateServer) and AcceptLoop creates the next instance only after the inner read
                // loop ENDS — so a refused peer that holds the connection open and never speaks
                // again occupies the only slot there is. Measured: the operator reads "reinstall the
                // add-on", does it, and the fixed bridge's ConnectAsync TIMES OUT against a pipe held
                // by the peer it was sent to replace. Any process running as this user can hold the
                // trading path shut that way, and the row is telling the operator to do the one
                // thing that cannot work.
                //
                // Dropping is safe because Drop now keeps the identity across a disconnection WE
                // caused — see the argument there, which this file already made for an unproved
                // peer's refusal and which a mismatch we refused shares exactly.
                return false;
            }

            // AN UNPROVED HELLO IS REFUSED, AND THIS IS THE HALF THAT USED TO BE LEFT OPEN.
            //
            // Capabilities is derived from _hello; ConnectorCapabilities.ReconciliationProvable is
            // SupportsClientOrderId && SupportsOrderHistory; and TradingGateway consults exactly
            // that before it will permit LIVE_AUTONOMOUS. So keeping an unproved peer's hello let
            // anything holding this pipe assert both capabilities and unlock autonomous live
            // trading — operator authority reachable from a pipe, which is precisely what the
            // product forbids. Same treatment as the version mismatch above: _hello stays null so
            // nothing can be traded on what this peer claimed, and its identity is kept in a
            // different field, where it cannot be mistaken for a capability.
            //
            // DECIDED ON A FACT, NOT ON A CLOCK. AuthGrace is display-only and must stay that way.
            // The bridge authenticates BEFORE it says hello (BridgeServer.RunAsync sends the hello
            // only inside `if (await Authenticate(...))`), and this connector reads frames one at a
            // time, so by the time a hello is in hand the challenge has either been answered — and
            // _authenticated set, in this same loop — or was never sent at all. There is no window
            // in which a legitimate bridge's hello can arrive early and lose a race; a hello with
            // _authenticated false is not this build's bridge.
            //
            // AFTER the version check, deliberately. A peer that speaks an older protocol is named
            // by version, which is the true fault and the repairable one; routing it here instead
            // would send the next reader hunting a secret problem that does not exist. Nothing is
            // conceded by that order: both paths keep _hello null and leave Capabilities empty, so
            // claiming an old version buys an impostor nothing at all.
            if (!_authenticated)
            {
                var unproved = UnauthenticatedBridge.PresentedNoProof(hello.BridgeVersion, hello.AtasVersion);
                NoteUnauthenticated(unproved);
                _hello = null;
                _connected = false;
                // Told on the wire too, exactly as Answer() tells a peer whose proof was wrong. A
                // bridge of this build never reaches here, so this reaches only something that is
                // not one — but a refusal nobody can read is how a session gets spent.
                await SendFrame(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Refused, error = unproved.Reason });
                ConnectionChanged?.Invoke(HealthState.FAILED);
                return false;
            }

            _hello = hello;
            NoteIncompatible(null);
            _compatible = true;
            _connected = true;
            _lastHeartbeat = DateTimeOffset.UtcNow;
            ConnectionChanged?.Invoke(HealthState.READY);
            return true;
        }

        if (f.Op == BridgeOps.Heartbeat)
        {
            // THE HELLO REFUSAL IS WORTH NOTHING WITHOUT THIS ONE. A heartbeat carries a whole
            // BridgeHello — that is how a capability proved after the handshake reaches this end —
            // and the branch below assigns it to _hello. So an unproved peer that simply never sends
            // a hello could set SupportsClientOrderId and SupportsOrderHistory here instead, and
            // ReconciliationProvable with them: the same unlock, one frame to the left.
            // Same gate as the event branch, and for the same reason: this branch assigns to _hello,
            // so a peer that has not had a compatible hello accepted on this connection cannot reach
            // it. _refused is already handled above, for every frame at once.
            if (!_authenticated || !_compatible) return true;

            _lastHeartbeat = DateTimeOffset.UtcNow;

            // A heartbeat now carries the bridge's current answer, because capabilities are not
            // settled at the handshake: SupportsClientOrderId cannot be true until an order has
            // proved it, and the account is unknown until ATAS has a portfolio. Adopt the newer
            // answer — but only a whole, version-compatible one. A half-read frame must leave the
            // latched handshake alone rather than silently widen or narrow what the gateway
            // believes this platform is able to prove.
            if (!f.Data.HasValue) return true;
            try
            {
                var refreshed = f.Data.Value.Deserialize<BridgeHello>(Json.Options);
                if (refreshed is not null && Versions.BridgeCompatible(refreshed.BridgeProtocolVersion))
                    _hello = refreshed;
            }
            catch (JsonException) { /* keep whatever the handshake established */ }
            return true;
        }

        if (f.Id is not null && _pending.TryRemove(f.Id, out var tcs)) tcs.TrySetResult(f);
        return true;
    }

    /// <summary>
    /// Answers the bridge's authentication challenge, and refuses a peer that got it wrong.
    ///
    /// The proof is over the nonce the BRIDGE chose, which is what stops a recording of an earlier
    /// answer being replayed at it. The role string is in the message too, so the answer this end
    /// produces can never be reused as the question the other end asked.
    /// </summary>
    async Task<bool> Answer(BridgeFrame f)
    {
        var cred = _credential;
        var nonce = Field(f, "nonce");
        var proof = Field(f, "proof");

        if (cred is null || !BridgePipeAuth.IsSecret(cred.Secret))
        {
            // Our own credential is the broken one. Say that, rather than blaming the bridge for a
            // proof it produced correctly against a secret we cannot read.
            NoteUnauthenticated(new UnauthenticatedBridge(
                $"this copy of TradeAgent has no usable bridge secret to answer with " +
                $"({BridgePipeAuth.CredentialFile})"));
            _authenticated = false;
            return true;
        }

        if (!BridgePipeAuth.IsNonce(nonce) ||
            !BridgePipeAuth.ProofMatches(cred.Secret, BridgePipeAuth.BridgeRole, nonce!, proof))
        {
            // Not merely unproven — PROVEN WRONG. Nothing that answers a challenge with the wrong
            // proof is the bridge this installation published a secret for, so it is dropped rather
            // than tolerated. In practice this is a stale bridge.auth, not an attack, which is why
            // the reason names the file.
            var wrongProof = new UnauthenticatedBridge(
                "the peer on the bridge pipe could not prove it holds this installation's bridge " +
                $"secret ({BridgePipeAuth.CredentialFile})");
            NoteUnauthenticated(wrongProof);
            _authenticated = false;
            _connected = false;
            _hello = null;
            await SendFrame(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Refused, error = wrongProof.Reason });
            ConnectionChanged?.Invoke(HealthState.FAILED);
            return false;
        }

        _authenticated = true;
        NoteUnauthenticated(null);
        await SendFrame(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Response,
            data = new { proof = BridgePipeAuth.Proof(cred.Secret, BridgePipeAuth.ServerRole, nonce!) }
        });
        return true;
    }

    static string? Field(BridgeFrame f, string name) =>
        f.Data.HasValue && f.Data.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>One frame out, sharing the gate with <see cref="Rpc"/> so writes cannot interleave.</summary>
    async Task SendFrame(object frame)
    {
        var w = _writer;
        if (w is null) return;
        await _sendGate.WaitAsync(_cts.Token);
        try { await w.WriteLineAsync(Json.Write(frame)); }
        catch (Exception) { /* the peer went away mid-answer; the read loop reports it */ }
        finally { _sendGate.Release(); }
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

    /// <summary>
    /// MEASUREMENT ONLY, and it places a real order. Asks the bridge to submit through ATAS's
    /// ASYNCHRONOUS order call instead of the obsolete synchronous one, so that the completion point
    /// of that call can be timed — see <see cref="BridgeOps.PlaceViaAsyncOverload"/> for the question
    /// this exists to answer and why it is a separate op.
    ///
    /// DELIBERATELY NOT ON <c>ITradingConnector</c>, and that is the point of it being here rather
    /// than one line higher. TradingGateway is handed an <c>ITradingConnector</c>; the only placement
    /// on that interface is <see cref="PlaceOrderAsync"/> above, which sends
    /// <see cref="BridgeOps.Place"/>. So the measurement route is not merely unused by the gateway,
    /// it is not expressible through the type the gateway holds. The only caller is
    /// <c>tools/probe --place-test-order --via-async-overload</c>, which is not part of the product
    /// and is not in <c>TradeAgent.sln</c>.
    /// </summary>
    public Task<OrderInfo> PlaceOrderViaAsyncOverloadAsync(PlaceOrderCommand cmd, CancellationToken ct = default) =>
        Rpc<OrderInfo>(BridgeOps.PlaceViaAsyncOverload, cmd, ct);

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

/// <summary>
/// What the two ends of the bridge pipe know about each other. Written by whichever process owns
/// the pipe, read by the bridge inside ATAS.
/// </summary>
/// <param name="Secret">64 hex characters. The key both ends prove knowledge of.</param>
/// <param name="ServerImage">
/// The full path of the program that created the pipe, as it saw itself. The bridge compares this
/// with the path Windows reports for the process actually holding the pipe.
/// </param>
public sealed record BridgeCredential(string Secret, string? ServerImage);

/// <summary>What went wrong when the bridge declined to talk to the process holding the pipe.</summary>
public sealed record BridgeAuthFailure(string Reason, DateTimeOffset When)
{
    public override string ToString() => $"bridge authentication failed — {Reason}";
}

/// <summary>
/// A peer on the bridge pipe that has not proved it holds this installation's bridge secret.
/// DISPLAY ONLY, exactly like <see cref="IncompatibleBridge"/>: nothing derives a capability from it.
/// </summary>
public sealed record UnauthenticatedBridge(string Reason)
{
    /// <summary>
    /// A peer that took the pipe and then said nothing at all — no challenge, no hello. Display
    /// only and derived from a clock, which is why it is a different thing from
    /// <see cref="PresentedNoProof"/>: there is nothing here to refuse yet, only a silence to name.
    /// </summary>
    public static readonly UnauthenticatedBridge Silent = new(
        "a program is holding the far end of the bridge pipe and has neither proved itself nor said " +
        "hello. If ATAS is running with the TradeAgent strategy started, reinstall the add-on from " +
        "TradeAgent so the DLL in the ATAS Strategies folder is this one");

    /// <summary>
    /// A peer that said hello without ever proving it holds this installation's bridge secret. It is
    /// REFUSED, not merely named: nothing it claimed is kept, so it cannot unlock anything.
    ///
    /// THE SENTENCE HAS TO SEPARATE THIS FROM THE SILENCES, and that is most of why it is this long.
    /// A bridge DLL built without the ATAS reference (trap 12), a strategies folder ATAS is not
    /// watching (trap 7) and a chart strategy restored stopped (trap 24) all present identically —
    /// as nothing on the pipe at all — and each has already cost a session. This is the opposite
    /// shape: something IS on the pipe and it is answering wrongly. Anyone reading this line must be
    /// able to stop looking for the three that it is not.
    /// </summary>
    public static UnauthenticatedBridge PresentedNoProof(string? bridgeVersion, string? atasVersion) => new(
        $"a peer claiming to be bridge {IncompatibleBridge.Clean(bridgeVersion)} on ATAS " +
        $"{IncompatibleBridge.Clean(atasVersion)} said hello without ever presenting the shared " +
        "secret, so everything it claimed was discarded and it was disconnected. Something is " +
        "answering on this pipe, so this is not a bridge that failed to load, not the wrong ATAS " +
        "Strategies folder and not a strategy restored stopped — all three of those are silence. " +
        "The repair is to reinstall the add-on from TradeAgent; if this line survives that, another " +
        "program has taken the pipe name and TradeAgent will not trade through it");

    public override string ToString() => $"the ATAS bridge did not authenticate — {Reason}";
}

/// <summary>
/// Authentication for the pipe the ATAS bridge talks to — the pipe that places orders.
///
/// WHAT THIS DEFENDS AGAINST, AND WHAT IT DOES NOT. Read this before adding to it, and before
/// quoting it as a boundary anywhere.
///
/// THE ATTACK. There is exactly one server instance of the bridge pipe, so whichever process
/// creates that name first owns it, and the bridge inside ATAS connects to whatever is listening.
/// A process that wins the name receives the bridge's connection and can send it <c>place</c>
/// directly — around the mode, the kill switch, the approvals, the risk limits and the autonomy
/// gate, all of which live in TradingGateway, which would no longer be in the path. Nothing about
/// this requires beating TradeAgent to a boot: it requires one moment when TradeAgent is not
/// holding the name.
///
/// SO THE BRIDGE CHECKS WHO IT CONNECTED TO, before it will serve a single operation:
///
///   1. The peer must prove it holds the secret in <see cref="CredentialFile"/>, over a nonce the
///      BRIDGE chose. This is the only thing that stops another user account on the machine, or any
///      software that merely knows the pipe name — a pipe NAME is not access-controlled, so any
///      account can create it.
///
///   2. On Windows the peer's process is identified by asking the kernel, not by asking the peer:
///      <c>GetNamedPipeServerProcessId</c> then <c>QueryFullProcessImageName</c>. The image must be
///      the one recorded in the credential file, and it must not sit under the managed AI-runtime
///      folder no matter what the record says. This is the only rule that can catch a process
///      running as the SAME user, which the secret cannot.
///
/// WHAT IT DOES NOT DEFEND AGAINST, STATED PLAINLY. TradeAgent starts the AI runtime with
/// Process.Start as the same OS user as itself. That process can read this file, because the same
/// user can. It can write it too, and thereby name itself as the expected image. DPAPI would not
/// change that — CurrentUser protection unprotects for the same user — which is why this file is
/// not DPAPI-protected and why nothing here is described as a boundary against a same-user
/// adversary. Windows does not offer one; a same-user process can also inject into TradeAgent or
/// replace it on disk. What rule 2 buys is that the squat must be TARGETED: an AI runtime that opens
/// the pipe name and starts sending orders is refused by name, and to get past it a process has to
/// tamper with TradeAgent's own state directory and race TradeAgent's rewrite of it. tools/probe
/// demonstrates the residual weakness deliberately: it grants itself bridge authority by calling
/// <see cref="EnsureForServer"/>, and it is entitled to nothing that any other same-user process is
/// not.
///
/// HOW FAR EACH END GOES. BOTH ENDS ENFORCE, and they enforce different things.
///
///   - The BRIDGE refuses an unproved peer before Describe(): nothing reaches IAtasAdapter, so
///     nothing places an order. That is the authority half.
///   - The CONNECTOR refuses an unproved peer's HELLO: _hello stays null, so ConnectorCapabilities
///     reports nothing supported and ReconciliationProvable — the property TradingGateway consults
///     before permitting LIVE_AUTONOMOUS — cannot be made true by anything a peer asserts. That is
///     the permission half, and until protocol 2 it was open: a peer that presented no proof at all
///     was named on the status row and then served, so it could claim SupportsClientOrderId and
///     SupportsOrderHistory and unlock autonomous live trading from the pipe.
///
/// The compatibility that kept the second half open — an ATAS bridge older than this build sends a
/// hello with no proof — is gone, and gone by construction rather than by decision: such a bridge
/// speaks protocol 1, this build speaks 2, and it is refused by version with a message naming both.
/// </summary>
public static class BridgePipeAuth
{
    /// <summary>Bridge to pipe owner: "prove you are TradeAgent", and here is my own proof.</summary>
    public const string Challenge = "ta-auth";

    /// <summary>Pipe owner to bridge: the answer.</summary>
    public const string Response = "ta-auth-ok";

    /// <summary>Either end to the other: "I will not talk to you, and this is why."</summary>
    public const string Refused = "ta-auth-failed";

    /// <summary>Role labels, so neither end's proof can be replayed as the other's.</summary>
    public const string BridgeRole = "bridge", ServerRole = "tradeagent";

    const string Domain = "tradeagent-bridge-auth-v1";
    const int SecretChars = 64, NonceChars = 32;

    static readonly object Gate = new();

    public static string CredentialFile => Path.Combine(Paths.State, "bridge.auth");

    // ------------------------------------------------------------------ the credential on disk

    /// <summary>
    /// The credential for a process that is about to create the bridge pipe. The secret survives
    /// across runs — the bridge may be mid-reconnect — but the image path is rewritten every time,
    /// so the record always names the program holding the name right now.
    /// </summary>
    public static BridgeCredential EnsureForServer()
    {
        lock (Gate)
        {
            var existing = ReadFile()?.Secret;
            var secret = IsSecret(existing) ? existing! : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var cred = new BridgeCredential(secret, Environment.ProcessPath);
            WriteFile(cred);
            return cred;
        }
    }

    /// <summary>The credential as the bridge sees it, or null when TradeAgent has published none.</summary>
    public static BridgeCredential? ReadForClient() => ReadFile();

    static BridgeCredential? ReadFile()
    {
        // WriteFile replaces atomically, but a replace can still make one open fail outright; a
        // single retry covers it without turning a missing file into a spin.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!File.Exists(CredentialFile)) return null;
                var c = Json.Read<BridgeCredential>(File.ReadAllText(CredentialFile));
                return c is not null && IsSecret(c.Secret) ? c : null;
            }
            catch (JsonException) { return null; }
            catch (IOException) { /* retry once */ }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    static void WriteFile(BridgeCredential c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialFile)!);
        var tmp = $"{CredentialFile}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, Json.Write(c));
        Restrict(tmp);
        File.Move(tmp, CredentialFile, overwrite: true);
    }

    /// <summary>
    /// Owner-only where the filesystem has the concept. On Windows the ACL inherited from
    /// %LOCALAPPDATA% already denies other accounts, and this is a no-op.
    /// </summary>
    static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { /* a filesystem without modes is a diagnostics problem, not a crash */ }
    }

    // ------------------------------------------------------------------ the proofs

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsSecret(string? s) => Hex(s, SecretChars);
    public static bool IsNonce(string? s) => Hex(s, NonceChars);

    static bool Hex(string? s, int length) =>
        s is not null && s.Length == length && s.All(char.IsAsciiHexDigit);

    /// <summary>HMAC over the role and the nonce. The role is what keeps the two halves distinct.</summary>
    public static string Proof(string secret, string role, string nonce)
    {
        if (!IsSecret(secret)) throw new ArgumentException("not a bridge secret", nameof(secret));
        return Convert.ToHexStringLower(HMACSHA256.HashData(
            Convert.FromHexString(secret), Encoding.UTF8.GetBytes($"{Domain}|{role}|{nonce}")));
    }

    /// <summary>Constant-time, so a wrong proof cannot be walked towards a right one.</summary>
    public static bool ProofMatches(string? secret, string role, string nonce, string? presented)
    {
        if (!IsSecret(secret) || !IsNonce(nonce) || presented is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Proof(secret!, role, nonce)), Encoding.UTF8.GetBytes(presented));
    }

    // ------------------------------------------------------------------ who is on the other end

    /// <summary>
    /// Whether a program at <paramref name="actual"/> may own the bridge pipe: null if it may, and
    /// the reason it may not otherwise.
    ///
    /// Separated from the kernel call on purpose. The call only runs on Windows; this rule is what
    /// decides, and it can be tested anywhere.
    /// </summary>
    public static string? ImageVerdict(string? actual, string? expected, string? toolsDir)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return "Windows would not say which program is holding the bridge pipe, so the peer " +
                   "could not be identified at all";

        // Ahead of the recorded-path rule, and deliberately not derived from the record: an AI
        // runtime that had rewritten the record to name itself would satisfy that rule and still
        // fail this one. TradeAgent never runs from its own managed tools folder; only the runtimes
        // it installs do.
        if (!string.IsNullOrWhiteSpace(toolsDir) && Inside(actual!, toolsDir!))
            return $"the program holding the bridge pipe ({Show(actual)}) runs from the managed " +
                   "AI-runtime folder. No AI runtime may own this pipe";

        if (string.IsNullOrWhiteSpace(expected))
            return "TradeAgent did not record which program owns the bridge pipe, so the peer could " +
                   "not be checked";

        if (!Same(actual!, expected!))
            return $"the program holding the bridge pipe is {Show(actual)}, but TradeAgent recorded " +
                   $"{Show(expected)}";

        return null;
    }

    /// <summary>
    /// The image path of the process that owns the pipe this client is connected to, as the kernel
    /// reports it. Null off Windows and null when the query fails — both of which the caller must
    /// treat as "not identified", never as "fine".
    /// </summary>
    public static string? ServerImagePath(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ? ImagePathOf(pid) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The mirror image, for diagnostics only: who dialled in to a pipe we own.</summary>
    public static string? ClientImagePath(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? ImagePathOf(pid) : null;
        }
        catch (Exception) { return null; }
    }

    [SupportedOSPlatform("windows")]
    static string? ImagePathOf(uint pid)
    {
        // PROCESS_QUERY_LIMITED_INFORMATION, and QueryFullProcessImageName rather than
        // Process.MainModule: ATAS is a 32-bit process ("Program Files (x86)"), so the bridge runs
        // 32-bit and TradeAgent may not. Reading another process's module list across that boundary
        // fails; this call does not care.
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            var size = (uint)buf.Length;
            return QueryFullProcessImageName(h, 0, buf, ref size) && size > 0
                ? new string(buf, 0, (int)size)
                : null;
        }
        finally { CloseHandle(h); }
    }

    static string Norm(string p) => p.Trim().Replace('\\', '/').TrimEnd('/');

    static bool Same(string a, string b) =>
        string.Equals(Norm(a), Norm(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static bool Inside(string path, string dir)
    {
        var d = Norm(dir);
        if (d.Length == 0) return false;
        var p = Norm(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.StartsWith(d + "/", cmp);
    }

    /// <summary>A path on its way to a status line: one line, printable, and short.</summary>
    static string Show(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "'<unknown>'"
            : "'" + new string(path.Where(c => !char.IsControl(c)).Take(120).ToArray()).Trim() + "'";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
