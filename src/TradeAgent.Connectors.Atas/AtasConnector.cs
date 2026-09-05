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

    /// <summary>
    /// How long ONE frame gets to reach the bridge before the connection is declared dead.
    ///
    /// The mirror of <see cref="AtasBridge.BridgeServer.WriteTimeout"/>, which bbcd36e added to the
    /// bridge's end of this same pipe. This end was left without one, and the gap was worse here
    /// than there: <see cref="Rpc"/> has an RPC timeout, but it starts AFTER the write returns, so
    /// the deadline that is supposed to bound an order could not bound the part of it that hangs.
    ///
    /// And the write is taken under <c>_sendGate</c>, so one stuck frame does not stall one order —
    /// it stalls every frame behind it, a cancel and a cancel-all included. Measured on macOS on
    /// 2026-09-02 against a bridge that authenticated and then stopped reading: 1872 of 2000 calls
    /// never finished at all, against a 1 s RPC timeout.
    ///
    /// Cancellation cannot recall a write the kernel has accepted, so the deadline ends the
    /// connection by closing the handle; the accept loop then waits for the bridge to redial, which
    /// is what it does after any disconnection.
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long ONE frame gets in TOTAL, however steadily it is being taken.
    ///
    /// <see cref="WriteTimeout"/> is a progress budget: it is spent per chunk and reset by every
    /// chunk the peer accepts, which is exactly right for telling a slow peer from a stopped one
    /// and is NOT a bound on anything. A legal order near the 1 MiB frame cap is a thousand chunks,
    /// and a peer that accepts one just inside the budget each time keeps the write alive for a
    /// thousand times the budget. <see cref="WorstCaseOrderPath"/> claimed one WriteTimeout for the
    /// whole write, the shutdown drain was derived from that claim, and so the drain could expire on
    /// an order that was still legitimately in progress and abandon it DISPATCHING — the exact state
    /// cc7006e and 02aad9a exist to prevent (Codex F2: "non-composable deadline accounting").
    ///
    /// So there are two bounds and they answer different questions. The per-chunk budget answers
    /// "has this peer stopped?". This answers "is the total bounded at all?" — and it is deliberately
    /// GENEROUS, because being finite is the point and being fast is not. Thirty seconds against the
    /// 1 MiB frame cap is a floor of about 34 KiB/s, well under the 79 KiB/s reader de627e3 was
    /// written to stop dropping, so it does not reintroduce the throughput floor that correction
    /// removed. It is reached only by a peer that is both enormous and glacial.
    ///
    /// An emergency does not use this: its whole caller budget is <see cref="EmergencyDeadline"/>,
    /// which is shorter than this by a factor of fifteen.
    /// </summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an EMERGENCY operation takes in total before the caller is told where it stands —
    /// the send gate, the write and the reply, ONE bound over all three.
    ///
    /// It used to bound only the gate wait, and that was measured to be nowhere near enough. With
    /// the gate FREE — a frozen ATAS, the owner pressing stop, nothing else in flight, which is the
    /// most likely real shape of this — the emergency's ~100-byte frame lands in the socket buffer,
    /// the write returns Sent, and the caller then served the ORDINARY ten-second reply timeout:
    /// 10005 ms, the generic "ATAS did not answer" sentence with no instruction in it, and the dead
    /// connection left up so no reconnect ever started (round-4 verify, V2). Five times the wait
    /// the two seconds were chosen to prevent, on the path the feature exists for.
    ///
    /// A JUDGMENT, not a measurement, and the number is two seconds. Measured beforehand: at the
    /// shipped deadline an emergency cancel-all queued behind one stalled write took 9.76 s to come
    /// back, and for ten seconds the owner saw a screen that had not changed and had no idea whether
    /// anything had been cancelled. Ten seconds is a long time to be told nothing while trying to
    /// stop.
    ///
    /// So an emergency gets two seconds — wherever they are spent — and then the truth: the call
    /// fails as INDEFINITE and the reason says the operation is NOT confirmed and where to look.
    /// That is worse information than a confirmed cancellation and far better than silence, because
    /// it is the sentence that sends a person to their platform to look.
    ///
    /// WHAT HAPPENS TO THE CONNECTION IS A SEPARATE QUESTION, decided by the peer's LIVENESS and not
    /// by the caller's clock. A peer that has done nothing at all during the window is dropped, so
    /// the handle closes, any wedged write dies and the health loop redials. A peer that is moving —
    /// accepting our bytes, or sending us frames — is left alone, because it is our queue or its own
    /// slowness and disconnecting it helps nobody. That is round 4's busy/stalled distinction,
    /// applied after the wire as well as before it.
    ///
    /// Ordinary agent traffic keeps the full <see cref="WriteTimeout"/>. A quote that arrives late
    /// costs nothing, and an ordinary caller has no reason to tear down a connection that is merely
    /// busy.
    ///
    /// THE COST THAT REMAINS, and it is not fixed by any of this: an emergency against a bridge that
    /// is genuinely busy, or slow to answer, still waits these two seconds and then returns UNKNOWN.
    /// Its outcome is honestly unknown — the frame was queued, or sent and unanswered — and no
    /// amount of classification changes that. What it gets is a truthful answer in two seconds
    /// instead of a wrong one in ten, and a connection left up so the retry it is told to make has
    /// somewhere to go.
    /// </summary>
    public TimeSpan EmergencyDeadline { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether this operation REDUCES RISK. Classified by intent, not by who asked.
    ///
    /// The first version keyed on the operator's own button — <c>CancelAll</c> and <c>Close</c> — and
    /// so gave the fast path to exactly one caller. The agent's <c>cancel-all</c> is not one bridge
    /// op: the gateway sweeps it into per-order <c>Cancel</c> legs, which fell through to the full
    /// deadline. Measured: 9707 ms per agent leg against 2006 ms for the operator's button, and the
    /// legs run in sequence, so an agent cancelling N orders through a stalled bridge waited ~10N
    /// seconds. Same act, same urgency, ten times the wait, because of where it entered.
    ///
    /// So the question is what the frame DOES. Cancelling an order or closing a position can only
    /// ever reduce exposure, and is worth interrupting a stalled write for whoever sent it.
    /// <c>Place</c> and <c>Modify</c> can increase it and never get the short wait — an order that
    /// opens risk has no claim on an emergency path, and would be the obvious way to abuse one.
    ///
    /// KNOWN GAP, and it is a consequence of classifying here rather than upstream: the gateway
    /// implements <c>close</c> as a PLACE of an offsetting order (<c>TradingGateway.CloseAsync</c>
    /// calls <c>PlaceAsync</c>), so an agent <c>close-all</c> arrives as <c>BridgeOps.Place</c> and
    /// is indistinguishable at this layer from an order that opens a position. It therefore does NOT
    /// get the fast path. Closing that would mean carrying the intent down through
    /// <c>ITradingConnector</c>, which is not this unit's to change.
    /// </summary>
    static bool IsRiskReducing(string op) =>
        op is BridgeOps.Cancel or BridgeOps.CancelAll or BridgeOps.Close;

    /// <summary>
    /// Ops that can INCREASE exposure, and which therefore never inherit an emergency deadline from
    /// an ambient <see cref="RiskReducingScope"/> however they got here.
    ///
    /// The scope exists so the READS a risk-reducing operation has to do first — the orders list a
    /// sweep needs, the resolution of a client id, the position behind a close — stop being served
    /// the ordinary ten seconds while an emergency waits on them (Codex F11). It must not become a
    /// side door onto the fast path for the one thing round 4 deliberately kept off it: an order that
    /// OPENS exposure has no claim on an emergency deadline whatever it is nested inside.
    ///
    /// THE ANSWER FOR A CLOSE NOW COMES FROM THE COMMAND AND NOT FROM THIS LIST (F5, decided where it
    /// belongs). The gateway implements a close as a PLACE of an offsetting order, so this list — which
    /// can only see the op — had to call every close an opening order, and the placement an agent
    /// `close` is hurrying to make was served the ordinary bound while every read in front of it ran
    /// inside the emergency budget. <see cref="PlaceOrderCommand.Intent"/> carries the fact the op
    /// cannot, and <c>Rpc</c>'s <c>reducesRisk</c> is where it lands. This list still decides the
    /// question for every UNMARKED placement, which is the safe default.
    /// </summary>
    static bool OpensExposure(string op) =>
        op is BridgeOps.Place or BridgeOps.Modify or BridgeOps.PlaceViaAsyncOverload;

    /// <summary>Whether this op CHANGES anything at the broker, as opposed to asking it something.</summary>
    static bool Mutates(string op) =>
        op is BridgeOps.Place or BridgeOps.Modify or BridgeOps.PlaceViaAsyncOverload
           or BridgeOps.Cancel or BridgeOps.CancelAll or BridgeOps.Close;

    /// <summary>
    /// The owner-readable sentence for a risk-reducing operation that ran out of time, WITH THE
    /// RIGHT WORDS FOR WHAT WAS ACTUALLY ATTEMPTED.
    ///
    /// A prerequisite read inherits the emergency deadline — that is F11's point and it stands — and
    /// it was inheriting the wording with it: "'accounts' is NOT confirmed … check your positions and
    /// orders in ATAS" (verifier finding F-D). Both halves of that are wrong for a read. Nothing
    /// about an <c>accounts</c> or <c>positions</c> request needs CONFIRMING, because it never asked
    /// the broker to do anything; and sending the owner to hunt through ATAS for an order that was
    /// never placed is the opposite of the service these sentences exist to perform. The whole
    /// reason f518251 wrote them was that they are the sentence that sends a person to the right
    /// place — one that sends them to the wrong place is worse than a stack trace, because they will
    /// believe it.
    ///
    /// So a read says what happened and what did not: the bridge did not answer, and nothing was
    /// placed or cancelled. The deadline and the drop are identical; only the words differ.
    /// </summary>
    static string EmergencySentence(string op, string condition, string consequence) =>
        Mutates(op)
            // OUTCOME FIRST. It used to lead with the connection — "the bridge is busy; 'cancel' is
            // NOT confirmed…" — and after round 7's grace change that sentence is what EVERY
            // emergency reads at two seconds, including one against a bridge that is in fact dead
            // and will be dropped eight seconds later (verifier F-G). The person reading it is
            // trying to stop; what they need in the first clause is what happened to their order and
            // where to look, not a claim about a pipe.
            ? $"'{op}' is NOT confirmed — check your positions and orders in ATAS. " +
              $"The bridge is {condition}; {consequence}."
            : $"'{op}' could not be read, so the operation was not started. Nothing was placed or " +
              $"cancelled. The bridge is {condition}; {consequence}.";

    NamedPipeServerStream? _pipeStream;
    Stream? _out;

    /// <summary>
    /// <see cref="Environment.TickCount64"/> when the in-flight write last had bytes ACCEPTED.
    ///
    /// It is what lets an emergency caller tell "the bridge stopped reading" from "the bridge is
    /// busy", which are the same two seconds of waiting and completely different news.
    /// </summary>
    long _lastWriteProgressAt;

    /// <summary>
    /// <see cref="Environment.TickCount64"/> when the bridge last ANSWERED one of our requests.
    ///
    /// Not "last sent us a frame", and the distinction is the whole of it. A heartbeat proves a
    /// thread is running; <c>BridgeServer.StartHeartbeat</c> runs on its own <c>Task.Run</c>,
    /// independent of the frame read loop, so a freeze inside ATAS that wedges the loop leaves the
    /// heartbeat beating over a connection that consumes nothing. Measured on the first version of
    /// this rule: such a bridge was called BUSY and kept in 6 of 12 runs, the verdict decided by
    /// nothing but which side of the 5 s heartbeat interval the emergency landed on.
    ///
    /// An ANSWER cannot be produced that way. It exists only because the read loop took our frame,
    /// recognised it and replied — which is precisely the faculty an emergency needs and the one a
    /// freeze removes. So this is the clock the reply-timeout branch reads.
    /// </summary>
    long _lastAnswerAt;

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
    /// The longest one order can take before this connector gives up on it, at the current values.
    ///
    /// Three bounded waits in series inside <see cref="Rpc"/>: the send gate, the write, then the
    /// reply. The middle term is <see cref="FrameTimeout"/> and NOT <see cref="WriteTimeout"/> —
    /// that was the defect. WriteTimeout is spent per chunk and reset by every chunk accepted, so
    /// counting one of it for the whole write made this number a claim rather than a bound, and a
    /// near-1 MiB order could legally outlive it by three orders of magnitude while the drain
    /// derived from it expired and abandoned the order DISPATCHING. Published because <c>GatewayPipeServer.HandlerDrainTimeout</c> has to outlast it — a
    /// shutdown drain shorter than this abandons an order that is still legitimately in progress —
    /// and a number in one file derived by hand from constants in another is a claim with an expiry
    /// date. A test asserts the drain still covers this.
    /// </summary>
    public TimeSpan WorstCaseOrderPath => WriteTimeout + FrameTimeout + _timeout;

    /// <inheritdoc />
    public TimeSpan WorstCaseOperationPath => WorstCaseOrderPath;

    /// <inheritdoc />
    public TimeSpan EmergencyBudget => EmergencyDeadline;

    /// <summary>
    /// WHICH OF THE TWO REFUSALS WAS OBSERVED MORE RECENTLY, and it exists because BOTH are now
    /// permanent. Round 7b made a protocol refusal last until a compatible hello repairs it; a
    /// credential refusal already lasted until a peer proved itself. Neither supersedes the other,
    /// and the row returned the protocol one first — the OLDER of the two — so the sequence an
    /// operator walks reads wrong at the moment they need it: refused on protocol, told to reinstall,
    /// the new DLL fails AUTHENTICATION, and the row still says "press Reinstall the bridge".
    ///
    /// A counter and not a clock: two observations in the same tick must still order, and nothing
    /// here needs to know how long ago anything was — only which came last. Zero means "not set",
    /// which is why <see cref="NoteIncompatible"/> and <see cref="NoteUnauthenticated"/> are the only
    /// way either marker moves. Assigning the field directly is how this class of bug returns.
    /// </summary>
    long _observed;
    long _incompatibleAt;
    long _unauthenticatedAt;

    /// <summary>
    /// THE STAMP A DERIVED STATE CARRIES, AND WHY IT IS THE MOMENT THE CONNECTION BEGAN.
    ///
    /// The two refusals above are OBSERVED and stamped where they are written. The silent reading is
    /// DERIVED — nothing is written when a peer says nothing — so round 8 gave it no stamp and made
    /// the getter blind while an explicit marker was live, on the reasoning that two readings must
    /// never both be live and disagree. That is right about one connection and wrong across two: a
    /// refusal is kept by <see cref="Drop"/>, a DIFFERENT peer takes the pipe and says nothing, and
    /// the row goes on naming a bridge that has left.
    ///
    /// So a state derived for the CURRENT connection is stamped with the moment that connection
    /// began. It is newer than everything recorded before this peer arrived and older than everything
    /// recorded during it — which is exactly the ordering wanted at both ends: a new peer's silence
    /// outranks the last peer's refusal, and this peer's own refusal outranks its earlier silence.
    /// </summary>
    long _peerArrivedAt;

    /// <summary>
    /// WHEN THIS PEER PROVED ITSELF — the stamp on the third derived reading, and the one that was
    /// missing (F38). A peer that has authenticated and not yet said hello used to produce NO
    /// reading at all: the silence required <c>!_authenticated</c>, so the getter went quiet the
    /// instant the challenge was answered, and with nothing of its own to report the row fell back
    /// on whatever marker the PREVIOUS connection had left — naming a protocol mismatch belonging to
    /// a program that had already gone.
    ///
    /// It is newer than <see cref="_peerArrivedAt"/> by construction, so within one connection this
    /// reading supersedes that peer's own silence; and newer than everything from before this peer
    /// arrived, so across connections it supersedes the last peer's refusals. "No status" is not a
    /// state: a connection that is there always says something about itself.
    /// </summary>
    long _authenticatedAt;

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
    public UnauthenticatedBridge? Unauthenticated => UnauthenticatedNow().Peer;

    /// <summary>
    /// The unauthenticated reading and the stamp that decides whether it outranks a protocol
    /// refusal. An explicit refusal is an observation and a silence is derived from a clock, so the
    /// two are ordered the same way any two observations are — by which was made later — rather than
    /// by one being blind to the other.
    /// </summary>
    (UnauthenticatedBridge? Peer, long At) UnauthenticatedNow()
    {
        var silent = _hello is null && !_authenticated
                     && DateTimeOffset.UtcNow - _peerArrived > AuthGrace
            ? UnauthenticatedBridge.Silent
            : null;
        var refused = _unauthenticated;
        if (silent is null) return (refused, _unauthenticatedAt);
        if (refused is null) return (silent, _peerArrivedAt);
        return _unauthenticatedAt > _peerArrivedAt ? (refused, _unauthenticatedAt) : (silent, _peerArrivedAt);
    }

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
    /// A PEER THAT PROVED ITSELF AND HAS NOT SAID HELLO YET, or null. The third derived reading, and
    /// the one F38 was about — it is not an <see cref="UnauthenticatedBridge"/>, because this peer
    /// HAS authenticated and putting it there would make that property say the opposite of what it
    /// means to the two consumers that read it.
    ///
    /// No grace period gates it: authentication is an OBSERVATION, not a clock, and the moment it
    /// lands there is something true and current to say about this connection. That is what makes it
    /// beat a marker left by the peer before it without waiting for anything to time out.
    /// </summary>
    /// <summary>
    /// A PEER THAT HAS JUST ARRIVED AND IS INSIDE THE GRACE, or null. The FOURTH derived reading, and
    /// the hole the other three left.
    ///
    /// <see cref="AuthGrace"/> exists so that a peer part way through its handshake is not called
    /// unauthenticated for the second it takes — and for that second the connector said NOTHING about
    /// it. The silence reading requires the grace to have EXPIRED, <see cref="PendingHello"/> requires
    /// authentication to have landed, and neither had happened yet. With nothing of its own to report
    /// the row fell back on the marker the PREVIOUS connection left: the operator watches a peer they
    /// have just replaced dial in and reads "the bridge speaks protocol 2" about a program that is no
    /// longer on the pipe. The grace is display-only, and this is the display it was missing.
    ///
    /// Stamped <see cref="_peerArrivedAt"/> — the counter taken at accept, so it is newer than every
    /// marker recorded before this peer arrived and older than everything recorded during it. The
    /// three ways out are all replacements rather than expiries: the grace runs out and the silence
    /// reading takes over on the same stamp, the peer proves itself and
    /// <see cref="_authenticatedAt"/> outranks this, or the peer is refused and its own marker does.
    /// A hello ends it by ending the whole row.
    /// </summary>
    string? Connecting =>
        _peerArrived != DateTimeOffset.MaxValue && _hello is null && !_authenticated
        && DateTimeOffset.UtcNow - _peerArrived <= AuthGrace
            ? "connecting — waiting for the add-on to authenticate"
            : null;

    string? PendingHello =>
        _authenticated && _hello is null
            ? "the ATAS bridge is connected and has not said hello yet — it proved itself and has " +
              "not announced its version. If this line stays, the strategy on the chart is loaded " +
              "but is not the TradeAgent bridge, or it is a build that stops before its handshake; " +
              $"press {Labels.ReinstallBridge} on the Checks page"
            : null;

    /// <summary>
    /// THE ROW DESCRIBES THE PEER THAT IS THERE NOW, AND A PEER THAT IS THERE ALWAYS SAYS SOMETHING.
    ///
    /// Three readings can be live and each carries the stamp of the moment it was observed, so the
    /// newest wins and the older ones stay RECORDED and readable through <see cref="Incompatible"/>
    /// and <see cref="Unauthenticated"/> — they simply stop outranking a later one. Both refusals are
    /// permanent, which is why an order is needed at all.
    ///
    /// F38 IS THE THIRD READING BEING ADDED. With only two, a peer that had authenticated and not
    /// yet said hello produced nothing, and "nothing" let the previous connection's marker stand as
    /// the current row. The rule this states is: a current connection ALWAYS derives a status newer
    /// than any marker — authenticated-without-hello says so, silence past the grace says so, a
    /// refusal says so. "No status" is not one of the states.
    /// </summary>
    public string? StatusDetail
    {
        get
        {
            var incompatible = _incompatible;
            var (unauthenticated, unauthenticatedAt) = UnauthenticatedNow();

            string? winner = null;
            var at = long.MinValue;
            if (incompatible is not null) { winner = incompatible.ToString(); at = _incompatibleAt; }
            if (unauthenticated is not null && unauthenticatedAt > at)
            { winner = unauthenticated.ToString(); at = unauthenticatedAt; }
            // THE ARRIVAL ITSELF IS AN OBSERVATION. It is stamped at accept, so it outranks every
            // marker older than this connection and yields to anything this peer has since done.
            if (Connecting is { } dialling && _peerArrivedAt > at)
            { winner = dialling; at = _peerArrivedAt; }
            if (PendingHello is { } waiting && _authenticatedAt > at) winner = waiting;
            return winner;
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
    /// Keeps an abandoned read's or write's failure from surfacing as an unobserved task exception.
    /// It will fault as soon as the stream is disposed, which is the point — that is how the read is
    /// aborted, and how a write to a peer that stopped reading is torn off the pipe.
    ///
    /// One helper for both, because there were two identical ones: the read loop's abandoned frame
    /// read and the chunked write's abandoned chunk want exactly this and nothing else.
    /// </summary>
    static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

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
                // The stamp every state derived for THIS connection carries. See _peerArrivedAt.
                _peerArrivedAt = Interlocked.Increment(ref _observed);
                _peerImage = BridgePipeAuth.ClientImagePath(_pipeStream);

                var reader = new StreamReader(_pipeStream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
                _out = _pipeStream;

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
        // operator is told to reinstall the bridge and the instruction disappears while they do it.
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
        _authenticatedAt = 0;
        // Both are facts about THIS connection and end with it, exactly as _incompatible does: a
        // fresh peer on a fresh connection is heard out from the beginning, and a repaired bridge
        // that reconnects is accepted on its first hello.
        _refused = false;
        _compatible = false;
        _peerArrived = DateTimeOffset.MaxValue;
        _peerArrivedAt = 0;
        _peerImage = null;
        _out = null;
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

        if (f.Id is not null && _pending.TryRemove(f.Id, out var tcs))
        {
            // THE ONE PLACE LIVENESS IS RECORDED. Getting here means the read loop consumed a frame
            // of ours, matched it to an outstanding request and answered it. No other thread on the
            // far end can reach this line.
            PeerAnswered();
            if (_abandoned.TryRemove(f.Id, out _)) RecordLateAnswer(f);
            tcs.TrySetResult(f);
        }
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
        _authenticatedAt = Interlocked.Increment(ref _observed);
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
        var w = _out;
        if (w is null) return;
        try { _ = await WriteFrame(w, frame, _cts.Token, WriteTimeout, emergency: false, FrameTimeout); }
        catch (Exception) { /* the peer went away mid-answer; the read loop reports it */ }
    }

    /// <summary>
    /// What happened to one frame. Three outcomes, because two of them were being confused.
    /// </summary>
    enum SendOutcome
    {
        /// <summary>The frame reached the peer.</summary>
        Sent,

        /// <summary>
        /// The PEER did not read it in time. Its connection is dropped: this is a fact about the
        /// bridge, not about this caller.
        /// </summary>
        PeerStalled,

        /// <summary>
        /// WE were still queued behind our own traffic when the bound expired. A fact about this
        /// process under load and nothing at all about the peer, so only this caller fails and the
        /// connection is left alone.
        /// </summary>
        Busy,

        /// <summary>
        /// The frame did not finish inside <see cref="FrameTimeout"/> — or, for an emergency, inside
        /// what was left of <see cref="EmergencyDeadline"/>.
        ///
        /// Distinct from <see cref="PeerStalled"/> because it is a different accusation: the peer was
        /// taking bytes the whole time, it was simply never going to finish in a useful interval. It
        /// shares the CONSEQUENCE — the connection is dropped — for a reason that has nothing to do
        /// with blame: the frame is half-written into a StreamWriter every caller shares, so the
        /// write state is unknown and the next caller would interleave with it. That is the wedge
        /// 667b9a2 removed, and the only safe end for a half-written frame is the same end a timeout
        /// gives it.
        /// </summary>
        FrameIncomplete
    }

    /// <summary>
    /// Takes the send gate, then writes one frame under <see cref="WriteTimeout"/>.
    ///
    /// THE DEADLINE STARTS AFTER THE GATE, and that is a correction. It used to start before, which
    /// made it measure this process's own send queue as well as the peer's reading: enough
    /// concurrent RPCs and a perfectly healthy bridge was declared stalled and disconnected, because
    /// OUR backlog ran out ITS clock. The gate wait is still bounded — a caller must not queue for
    /// ever — but expiring there returns <see cref="SendOutcome.Busy"/> and touches nothing else.
    ///
    /// CANCELLATION WITH A WRITE IN FLIGHT IS A DROP, not a return. The write is on a StreamWriter
    /// shared by every caller, and a cancelled wait does not cancel the write — so releasing the
    /// gate would hand the next caller a writer with a half-written frame still going into it. That
    /// left the connector wedged with Connected still true and no reconnect, every later frame
    /// failing for ever. The write state is unknown, so the connection ends the same way a timeout
    /// ends it.
    ///
    /// The abandoned write is observed rather than dropped, so its inevitable fault does not surface
    /// later as an unobserved task exception.
    /// </summary>
    /// <param name="writeDeadlineAt">
    /// An ABSOLUTE <see cref="Environment.TickCount64"/> stamp the write must finish by, or null to
    /// derive one from <paramref name="frameBudget"/> once the gate is ours.
    ///
    /// The two forms are not a convenience, they are the two different promises this connector
    /// makes. An ORDINARY frame is bounded from the moment the gate is acquired, because queueing
    /// behind our own backlog is not that frame's fault — 667b9a2's correction, unchanged. An
    /// EMERGENCY is bounded from the moment the CALLER asked, because what it promises is a total:
    /// two seconds until somebody is told where they stand, however that time is divided between
    /// waiting for the gate and getting the bytes out.
    /// </param>
    async Task<SendOutcome> WriteFrame(Stream w, object frame, CancellationToken ct,
        TimeSpan gateWait, bool emergency, TimeSpan frameBudget, long? writeDeadlineAt = null,
        bool mutating = false)
    {
        var waitedFrom = Environment.TickCount64;

        // A CALLER WHO GAVE UP WHILE QUEUED FOR THE GATE IS THE FOURTH WAY OF NOT GETTING IT, and it
        // is as provable as the other three: the gate was never ours, the frame is not even built
        // yet, and no byte of it can exist. It used to fall through to the outer catch in `Rpc`,
        // which records the fail-closed answer for everything it cannot identify — so a cancelled
        // leg was reported as possibly at the broker and pulled a reconciliation and a trading pause
        // behind it (Codex round-10 F1).
        bool gate;
        try { gate = await _sendGate.WaitAsync(gateWait, ct); }
        catch (OperationCanceledException)
        {
            if (mutating) TransportLedger.Record(TransportOutcome.NothingWritten);
            throw;
        }

        if (!gate)
        {
            // EVERY EXIT FROM THE GATE WROTE NOTHING, and it is provable rather than assumed: the
            // gate was never ours, so not one byte of this frame reached the stream. `Busy` and a
            // gate-expiry `PeerStalled` are different facts about the far end and the SAME fact
            // about our frame.
            if (mutating) TransportLedger.Record(TransportOutcome.NothingWritten);
            if (!emergency) return SendOutcome.Busy;

            // AN EMERGENCY THAT GAVE UP ON THE GATE STILL HAS TO SAY WHICH THING WENT WRONG.
            //
            // The first version dropped unconditionally and told the owner the bridge was not
            // responding — which was a lie whenever the bridge was merely busy with our own
            // backlog. Reproduced: 1500 concurrent 900 KiB RPCs and one cancel-all returned in
            // 2.01 s having disconnected a bridge that was reading everything we sent it.
            //
            // So ask the writer that is holding the gate whether it got anywhere while we waited.
            // Bytes accepted in that window means the far end is reading and we are the queue;
            // nothing accepted means it has stopped, and the connection is worth ending to free
            // the gate and start the reconnect.
            if (Volatile.Read(ref _lastWriteProgressAt) > waitedFrom) return SendOutcome.Busy;

            DropStalledPeer();
            return SendOutcome.PeerStalled;
        }
        try
        {
            // ONE CLOCK FOR THE CALLER'S WAIT, and this line is where it was two.
            //
            // The budget used to be captured before the gate wait and then started AGAIN here, so an
            // emergency could spend nearly its whole deadline queueing and be handed a fresh one for
            // its write: the two-second end-to-end promise was false by construction. Measured on
            // the fixture Codex specified — hold the gate until just under the deadline, release it
            // into a pipe with no room — 3.40 s against a 2 s promise.
            //
            // An ordinary frame still starts its ceiling when the gate is ours (667b9a2: our own
            // backlog is not that frame's fault). An emergency's deadline is absolute and was set
            // when the caller asked.
            var deadlineAt = writeDeadlineAt
                ?? Environment.TickCount64 + (long)frameBudget.TotalMilliseconds;
            var bytes = Encoding.UTF8.GetBytes(Json.Write(frame) + "\n");
            var sent = 0;
            while (sent < bytes.Length)
            {
                var left = RiskReducingScope.LeftUntil(deadlineAt);
                if (left <= TimeSpan.Zero)
                {
                    // Out of total time with the frame half-written. Nothing can be recalled, so
                    // the connection ends the way a timeout ends it. From HERE on the gate is ours
                    // and bytes may already be with the kernel, so nothing below may claim to have
                    // written nothing — even on the very first chunk, where a partial flush is
                    // exactly what cannot be ruled out.
                    if (mutating) TransportLedger.Record(TransportOutcome.PossiblyWritten);
                    DropStalledPeer();
                    return SendOutcome.FrameIncomplete;
                }

                var n = Math.Min(WriteChunkBytes, bytes.Length - sent);
                var write = w.WriteAsync(bytes.AsMemory(sent, n), ct).AsTask();
                try
                {
                    // Whichever bound is nearer: the progress budget for THIS chunk, or what is left
                    // of the whole frame's.
                    await write.WaitAsync(left < WriteTimeout ? left : WriteTimeout, ct);
                }
                catch (TimeoutException)
                {
                    Observe(write);
                    if (mutating) TransportLedger.Record(TransportOutcome.PossiblyWritten);
                    DropStalledPeer();
                    // Which bound actually expired decides what we are entitled to say.
                    return left < WriteTimeout ? SendOutcome.FrameIncomplete : SendOutcome.PeerStalled;
                }
                catch (OperationCanceledException)
                {
                    Observe(write);
                    if (mutating) TransportLedger.Record(TransportOutcome.PossiblyWritten);
                    DropStalledPeer();
                    throw;
                }
                sent += n;
                Volatile.Write(ref _lastWriteProgressAt, Environment.TickCount64);
                // Deliberately NOT PeerIsAlive(). The kernel taking our bytes means the socket
                // buffer had room, not that anything read them — an 8 KiB buffer swallows a whole
                // emergency frame while the far end is a corpse. Buffer-level progress answers "can
                // we get bytes out" (the gate-expiry question) and nothing about whether the peer
                // is alive (the reply question).
            }
            return SendOutcome.Sent;
        }
        finally { _sendGate.Release(); }
    }

    /// <summary>
    /// How much of a frame one write deadline covers, and — the part that was costing more — HOW
    /// FINELY PROGRESS CAN BE SEEN.
    ///
    /// ARITHMETIC. Chunking is what makes <see cref="WriteTimeout"/> a stalled-peer detector rather
    /// than a throughput floor of (frame size / timeout), and it is also what makes progress
    /// OBSERVABLE — a single WriteLineAsync gives back one task that either finishes or does not,
    /// and an emergency caller cannot ask it whether the bridge is alive.
    ///
    /// But a chunk only reports progress when the WHOLE of it has been accepted, so the chunk size
    /// is the RESOLUTION of that signal, and anything moving slower than one chunk per
    /// <see cref="EmergencyDeadline"/> reads as not moving at all. Measured at 8 KiB on 2026-09-03
    /// (Codex F4): a peer accepting 1 KiB every 800 ms — 2 KiB taken off us inside the window, still
    /// reading when we hung up — was dropped and told the owner it had stopped responding. At
    /// 2.5 KiB/s it was correctly called busy; the boundary sat between them, at chunk ÷ deadline =
    /// 4 KiB/s.
    ///
    /// 1 KiB puts that boundary at 512 B/s. The boundary cannot be removed, only moved: a peer slow
    /// enough IS indistinguishable from a dead one inside two seconds, and round 4 took that trade
    /// knowingly. What it must not sit on is the speed of an ordinary struggling reader. The cost is
    /// eight times as many writes for a large frame — 1024 of them for a 1 MiB order, on a local
    /// pipe — against telling somebody their bridge is dead when it is not.
    /// </summary>
    const int WriteChunkBytes = 1024;

    /// <summary>
    /// Ends a connection whose peer has stopped reading. Disposing the handle is what actually kills
    /// the pending overlapped write; <see cref="Drop"/> alone only clears this side's state, and the
    /// accept loop would still have been parked on a socket nobody was draining.
    /// </summary>
    void DropStalledPeer()
    {
        Drop($"the ATAS bridge stopped reading; no frame landed within {WriteTimeout.TotalSeconds:0}s");
        try { _pipeStream?.Dispose(); } catch (Exception) { /* already gone */ }
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

    /// <summary>The bridge answered a request. Called only where a pending RPC is completed.</summary>
    void PeerAnswered() => Volatile.Write(ref _lastAnswerAt, Environment.TickCount64);

    /// <summary>Requests whose caller has stopped waiting but whose answer is still welcome.</summary>
    readonly ConcurrentDictionary<string, string> _abandoned = new();
    int _lateAnswers;

    /// <summary>
    /// Answers that arrived after their caller had given up on them.
    ///
    /// They are delivered to the pending request rather than discarded, which is what makes the
    /// deferred verdict honest: the connection is kept because the bridge answered, so the answer
    /// has to exist somewhere. WHETHER THE GATEWAY SETTLES A REQUEST ON ONE IS NOT DECIDED HERE —
    /// that is U2c-1's, and it is why this is exposed rather than consumed.
    /// </summary>
    public int LateAnswers => Volatile.Read(ref _lateAnswers);

    /// <summary>Raised when an answer arrives after its caller has given up. See <see cref="LateAnswers"/>.</summary>
    public event Action<BridgeFrame>? LateAnswerReceived;

    /// <summary>
    /// How many answers this connector is still willing to receive on behalf of a caller that has
    /// gone. It returns to zero: an entry is removed by the answer, by the race check, or by the
    /// grace expiring, and a number that only grows would be the leak Codex F3 named.
    /// </summary>
    public int AwaitingLateAnswer => _abandoned.Count;

    /// <summary>
    /// Requests this connector is still holding a slot for — the far end may still answer them.
    ///
    /// Exposed for the same reason <see cref="AwaitingLateAnswer"/> is: it must return to zero, and
    /// a number that only grows is a leak that nothing outside this class could see. `Drop` faults
    /// everything here, so it is bounded by the connection's life either way — but "bounded by a
    /// disconnect" is not the same promise as "released when the work ends".
    /// </summary>
    public int PendingRequests => _pending.Count;

    void RecordLateAnswer(BridgeFrame f)
    {
        Interlocked.Increment(ref _lateAnswers);
        LateAnswerReceived?.Invoke(f);
    }

    /// <summary>
    /// The caller has stopped waiting; the CONNECTION has not been judged yet, and is judged here.
    ///
    /// Waits out what is left of the ordinary RPC deadline. If the request is answered in that time,
    /// or anything else is, the bridge is serving and is left alone. If nothing at all comes back,
    /// it has answered nothing within the bound this system already calls "did not answer" — and
    /// that is when the handle is closed so the redial can start.
    ///
    /// THE COST, STATED: a bridge that wedges while heartbeating is now detected at the grace rather
    /// than at the emergency deadline — about ten seconds instead of about two. The caller is not
    /// delayed by it; only the teardown is.
    /// </summary>
    void JudgeTheConnectionWhenTheGraceRunsOut(string id, string op, long startedAt,
        TaskCompletionSource<BridgeFrame> caller) =>
        AwaitALateAnswer(id, op, startedAt, caller, judgeTheConnection: true);

    /// <summary>
    /// THE CALLER GAVE UP FOR ITS OWN REASONS, AND THAT IS EVIDENCE ABOUT NOTHING.
    ///
    /// Same bounded machinery as the reply timeout — the slot is released when the answer arrives or
    /// when the grace runs out, and an answer that does arrive is COUNTED — with the one difference
    /// this method exists to state: no verdict is passed on the connection. A reply timeout is a fact
    /// about the bridge; the app closing, or an operator pressing stop, is a fact about us, and
    /// tearing down a bridge that is serving perfectly well on that basis is the round-6 mistake in a
    /// new place.
    ///
    /// Before this, the exit did neither. The reply wait's catch is filtered
    /// <c>when (!ct.IsCancellationRequested)</c> so that a caller's own cancellation is not read as a
    /// timeout, and the filter also skipped the `_pending.TryRemove` every other exit performs:
    /// `_pending` grew by one per cancelled emergency (bounded only by the connection's life), and
    /// because the id never reached `_abandoned` a late answer for it was delivered to a
    /// `TaskCompletionSource` nobody awaited and counted in neither <see cref="LateAnswers"/> nor the
    /// event (verifier round-11 L-1, measured: 0 -> 1 -> 1 with `AwaitingLateAnswer` at 0).
    /// </summary>
    void ReleaseTheSlotAndStillCountALateAnswer(string id, string op, long startedAt,
        TaskCompletionSource<BridgeFrame> caller) =>
        AwaitALateAnswer(id, op, startedAt, caller, judgeTheConnection: false);

    void AwaitALateAnswer(string id, string op, long startedAt,
        TaskCompletionSource<BridgeFrame> caller, bool judgeTheConnection)
    {
        _abandoned[id] = op;

        // THE DOUBLE CHECK, and it closes a race Codex found (F3). The answer can land between the
        // caller's deadline expiring and this registration: `Dispatch` then removed the pending
        // entry and found nothing in `_abandoned` to count, so a late answer went unrecorded AND the
        // registration below leaked an id that nothing would ever remove. Both sides now attempt the
        // same `TryRemove`, so exactly one of them wins and the entry always goes.
        if (caller.Task.IsCompletedSuccessfully && _abandoned.TryRemove(id, out _))
        {
            RecordLateAnswer(caller.Task.Result);
            return;
        }

        var grace = Remaining(startedAt, _timeout);
        var answer = _pending.TryGetValue(id, out var tcs) ? tcs.Task : caller.Task;

        _ = Task.Run(async () =>
        {
            try { await answer.WaitAsync(grace, _cts.Token); return; }   // answered in time
            catch (TimeoutException) { }
            catch (Exception)
            {
                // DISPOSED, CANCELLED, OR THE CONNECTION WENT AWAY UNDER US — and this exit used to
                // remove nothing (Codex round-8 F2). `Drop` faults every pending request, so a
                // disconnect during the grace, and disposal, both land here, and the id stayed in
                // `_abandoned` for the life of the process. No answer is coming down a connection
                // that has gone, so there is nothing left to await and nothing to judge: the entry
                // goes, and the connection is not touched, because it has already been decided by
                // whoever ended it.
                _pending.TryRemove(id, out _);
                _abandoned.TryRemove(id, out _);
                return;
            }

            _pending.TryRemove(id, out _);
            _abandoned.TryRemove(id, out _);

            // Nothing was learned about the bridge, because nothing was asked of it: this caller
            // stopped waiting of its own accord. See ReleaseTheSlotAndStillCountALateAnswer.
            if (!judgeTheConnection) return;

            // Something else came back while we waited: the read loop is running and this one
            // operation was simply lost or slow. A connection that is serving is not torn down.
            if (PeerAnsweredSince(startedAt)) return;

            Drop($"the ATAS bridge answered nothing within {_timeout.TotalSeconds:0}s; " +
                 $"'{op}' is not confirmed and the bridge is not responding");
            try { _pipeStream?.Dispose(); } catch (Exception) { /* already gone */ }
        });
    }

    /// <summary>
    /// Has the bridge ANSWERED anything since <paramref name="since"/> (a <c>TickCount64</c> stamp)?
    ///
    /// The keep-signal is an answer, and it is deliberately neither of the two weaker things it
    /// could be. Not "any frame": a heartbeat comes from a thread a freeze does not touch, and
    /// keying on it made the verdict against a wedged bridge a coin flip on heartbeat phase — kept
    /// in 6 of 12 runs at the shipped 5 s interval. Not "bytes accepted" either, which was the first
    /// correction proposed and does not work: the emergency frame is about a hundred bytes and the
    /// socket buffer is eight kilobytes, so the kernel takes it whether or not anything ever reads
    /// it, and the clock moves identically for a wedged peer and a healthy one. Measured — with that
    /// rule the wedged peer was still kept at two of the twelve phases, and a bridge that WAS
    /// reading everything was dropped, because the write and the caller's start stamp can land on
    /// the same millisecond tick.
    ///
    /// An answer is the one signal that cannot be produced without the faculty an emergency needs.
    ///
    /// The trade, unchanged and still stated: a bridge that reads us but answers nothing at all for
    /// the whole window is dropped and redialled. That costs a reconnect, and a reconnect is the
    /// right remedy for a connection that is consuming requests and returning nothing — it is also
    /// the only thing that makes the retry this failure advises worth making.
    /// </summary>
    ///
    /// The comparison is <c>&gt;=</c>, not <c>&gt;</c>. <see cref="Environment.TickCount64"/> has
    /// millisecond resolution, so an answer that lands in the same tick as the caller's start stamp
    /// is a real answer that a strict comparison throws away — and throwing it away here means
    /// classifying a live bridge as dead and tearing the connection down (Codex C5). The direction
    /// of that error is what settles it: counting a same-tick answer as an answer can only keep a
    /// connection one tick longer than necessary; missing one disconnects a healthy platform.
    bool PeerAnsweredSince(long since) => Volatile.Read(ref _lastAnswerAt) >= since;

    /// <param name="reducesRisk">
    /// The CALLER's knowledge that this operation reduces risk, for the one op where the op itself
    /// cannot say: a close is an offsetting <c>place</c>. It only ever WIDENS urgency — it cannot make
    /// a risk-reducing op ordinary — so the worst a wrong one can do is give a placement the emergency
    /// deadline it would have inherited had it been a cancel.
    /// </param>
    async Task<BridgeFrame> Rpc(string op, object? args, CancellationToken ct, bool reducesRisk = false)
    {
        // WHERE THE FRAME GOT TO IS RECORDED WHERE IT IS KNOWN, and only for a MUTATION.
        //
        // The gateway maps every ConnectorTransportException to UNKNOWN, correctly — from up there
        // a refusal before the send gate and a half-written frame are the same exception. Down here
        // they are not, and the difference is the difference between `not-sent` and
        // `sent-not-confirmed`, which is the difference between an owner reading "nothing happened"
        // and an owner hunting through ATAS while all further execution is paused (verifier
        // round-9 F-1). Reads are not recorded: a leg resolves its target and then does the thing it
        // came to do, and only the second one can leave an order at a broker.
        var mutating = Mutates(op);

        // THE ATTEMPT IS MARKED BEFORE ANYTHING CAN GO WRONG, and that is what makes "nothing was
        // recorded" mean "no mutation was ever started" rather than "no site happened to write it
        // down". Every exit below that KNOWS says so and overrides this; an exit that does not know
        // — a caller's own cancellation during the reply wait was one, and the frame was already
        // whole on the far side (Codex round-10 F2) — leaves `PossiblyWritten`, which is the
        // fail-closed answer and this connector's honest one.
        if (mutating) TransportLedger.Attempt();

        if (!_connected || _out is null)
        {
            if (mutating) TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorTransportException("the ATAS bridge is not connected");
        }

        var id = Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<BridgeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        // THE CALLER'S CLOCK STARTS HERE, not at each phase. For an emergency the whole of what
        // follows — the gate, the write and the reply — has to fit inside one bound, because the
        // person waiting is not waiting for a phase.
        var startedAt = Environment.TickCount64;

        // WHAT THIS CALL CAN DO TO EXPOSURE, WHICH IS NOT ALWAYS WHAT ITS OP SAYS. `place` is the op
        // a close is made of, so the classification below asked the wrong question of it: the answer
        // is on the command (`PlaceOrderCommand.Intent`) and arrives here as `reducesRisk`. Nothing
        // else may narrow this — a cancel is risk-reducing whatever anybody passes.
        var opensExposure = OpensExposure(op) && !reducesRisk;
        var emergency = IsRiskReducing(op) || reducesRisk || (RiskReducingScope.IsActive && !opensExposure);

        // ONE DEADLINE FOR THE OPERATION, not one per RPC.
        //
        // A cancel-all is a read, then a resolution per order, then a leg per order, and each of
        // them used to start its own two seconds — so the bound was paid once per RPC and the
        // promise scaled with the size of the sweep. Measured by Codex: three replies delayed 1.9 s
        // each made an IPC cancel-all take about 5.7 s. When a scope carries a deadline, every RPC
        // inside it gets what is LEFT of that deadline; only a call with no operation around it
        // starts a fresh one. `place` and `modify` are excluded from the scope entirely, so this
        // cannot shorten them either.
        var deadlineAt = emergency
            ? (!opensExposure && RiskReducingScope.DeadlineAt is { } shared
                ? shared
                : startedAt + (long)EmergencyDeadline.TotalMilliseconds)
            : 0L;

        // A LEG WHOSE TURN NEVER CAME IS NOT EVIDENCE ABOUT THE PEER.
        //
        // With one deadline for the operation, a leg reached after it has expired has nothing left
        // to spend. Letting it queue for its one remaining millisecond and then judging the bridge
        // on whether anything moved in that millisecond is not a measurement — it dropped a bridge
        // that was reading throughout, which the shared-deadline test caught. So it fails here,
        // before the gate, saying exactly what happened: nothing was sent, and the connection is
        // untouched because this call learned nothing about it.
        if (emergency && deadlineAt <= Environment.TickCount64)
        {
            _pending.TryRemove(id, out _);
            // PROVABLE: the gate was never taken and no byte of this frame exists.
            if (mutating) TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorTransportException(Mutates(op)
                ? $"'{op}' is NOT confirmed — check your positions and orders in ATAS. It was not sent: " +
                  "the operation ran out of time before this leg's turn came."
                : $"'{op}' was not sent: the operation ran out of time before this read's turn came. " +
                  "Nothing was placed or cancelled.");
        }

        try
        {
            // One payload field ("data") in both directions; a request-only "args" field silently
            // dropped every argument when the bridge read the frame back as a BridgeFrame.
            var outcome = await WriteFrame(_out, new { v = Versions.BridgeProtocolVersion, id, op, data = args }, ct,
                emergency ? Left(deadlineAt) : WriteTimeout, emergency, FrameTimeout,
                emergency ? deadlineAt : null, mutating);
            if (outcome is not SendOutcome.Sent)
            {
                _pending.TryRemove(id, out _);
                // All of these are indefinite, and they have to stay that way: a frame that was
                // queued or half-written may or may not have reached ATAS. Safety rule 3 — only a
                // definite refusal from the broker is allowed to read as definite. They are
                // different SENTENCES, because they ask different things of whoever reads them.
                throw new ConnectorTransportException(outcome switch
                {
                    // Written for the owner, not for a log: these reach the screen during the
                    // seconds when someone is trying to stop and needs to know where they stand.
                    // They are different facts and they get different words — "not responding"
                    // sends a person to look at ATAS, "busy" tells them to wait and try again.
                    SendOutcome.PeerStalled when emergency =>
                        EmergencySentence(op, "not responding",
                            "The connection has been dropped and will be retried"),
                    SendOutcome.Busy when emergency =>
                        EmergencySentence(op, "busy", "The connection is still up — try again"),
                    SendOutcome.FrameIncomplete when emergency =>
                        EmergencySentence(op, "too slow",
                            "It was still being sent when the deadline passed, so the connection has been " +
                            "dropped and will be retried"),
                    SendOutcome.FrameIncomplete =>
                        $"'{op}' was still being sent to the ATAS bridge after {FrameTimeout.TotalSeconds:0}s " +
                        "and the connection was dropped; it is not known whether ATAS received it",
                    SendOutcome.PeerStalled =>
                        $"the ATAS bridge did not read '{op}' within {WriteTimeout.TotalSeconds:0}s",
                    _ =>
                        $"'{op}' waited {WriteTimeout.TotalSeconds:0}s to be sent and was not; the bridge connection is still up"
                });
            }
        }
        catch (ConnectorTransportException) { throw; }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            // NOTHING IS RECORDED HERE, and that is the fix rather than an omission. This catch
            // cannot identify what it caught, so it used to write the fail-closed answer over
            // whatever the site below had already PROVEN — a cancelled gate wait reports
            // `NothingWritten` and this line turned it into `PossiblyWritten`. The attempt marked at
            // the top of this method already supplies exactly the same fail-closed answer for an
            // exit nothing identified, and it does so without overwriting one that was identified.
            throw new ConnectorTransportException("could not reach the ATAS bridge", ex);
        }

        // WHAT IS LEFT OF THE CALLER'S BUDGET, not a fresh one. An emergency that spent 1.9 s
        // getting its frame out does not then get a further ten seconds to wait for the answer —
        // that arithmetic is what produced a 10005 ms "emergency" against an idle stalled bridge.
        var replyWait = emergency ? Left(deadlineAt) : _timeout;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(replyWait);
        try
        {
            var frame = await tcs.Task.WaitAsync(timeout.Token);
            // The round trip completed. Whatever the frame says — an acknowledgement, a definite
            // refusal, or a failure the bridge reports — it is an ANSWER, and the record the gateway
            // writes from it is what the word should be read off.
            if (mutating) TransportLedger.Record(TransportOutcome.ReplyReceived);
            if (frame.Ok == false)
                throw frame.Rejected
                    ? new ConnectorRejectedException(frame.Error ?? "rejected by ATAS")
                    : new ConnectorTransportException(frame.Error ?? "the ATAS bridge reported a failure");
            return frame;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out waiting for the answer. The frame WENT OUT, so this is the most indefinite
            // state there is and stays UNKNOWN either way.
            // The frame WENT OUT and no answer came back: the most indefinite state there is, on
            // both branches below.
            if (mutating) TransportLedger.Record(TransportOutcome.PossiblyWritten);

            if (!emergency)
            {
                _pending.TryRemove(id, out _);
                throw new ConnectorTransportException($"ATAS did not answer '{op}' within {replyWait.TotalSeconds:0}s");
            }

            // TWO BOUNDS, TWO MEANINGS, AND THIS IS WHERE THEY PART.
            //
            // EmergencyDeadline bounds what the CALLER waits, and nothing else. The owner has had
            // their answer in two seconds — not confirmed, check ATAS, UNKNOWN — which is the whole
            // point of f518251 and is unchanged.
            //
            // Whether the CONNECTION is dead is a different question on a different clock, and
            // judging it here was wrong. `BridgeServer` handles frames strictly sequentially
            // (`BridgeServer.cs:130`), so while the bridge is working on OUR emergency it cannot
            // read, match or answer anything else — the exact signal a liveness test needs is the
            // one it is unable to emit precisely BECAUSE it is busy with us. Measured by the round-6
            // verifier: a bridge that had our frame in hand and answered it at 2500 ms or 3500 ms
            // was disconnected at ~2000 ms and told the owner it was not responding. And this repo
            // names the legitimate cause in its own words — `BridgeProtocol.cs` records that the
            // obsolete synchronous ATAS call sites "cannot be given a deadline, so a block inside
            // one wedges the bridge's frame loop". A >2 s synchronous call is a state this unit
            // already expects; it must not cost a teardown.
            //
            // So the grace is the deadline this system ALREADY uses for "ATAS did not answer" —
            // `_timeout`, no new number — and the verdict is deferred to it. The pending request
            // stays registered, so an answer arriving late is delivered rather than dropped on the
            // floor.
            JudgeTheConnectionWhenTheGraceRunsOut(id, op, startedAt, tcs);

            // "Still up" is not a guess here, it is the state: nothing has been dropped, and at two
            // seconds nothing is yet known that would justify dropping it.
            throw new ConnectorTransportException(
                EmergencySentence(op, "busy", "The connection is still up — try again"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // THE CALLER STOPPED WAITING. Nothing is recorded about the transport here on purpose —
            // the attempt marked at the top of `Rpc` already reports `PossiblyWritten` for an exit
            // that cannot say more, and the frame really may have landed. What was missing is the
            // BOOKKEEPING: the slot, and the answer that may still arrive for it.
            ReleaseTheSlotAndStillCountALateAnswer(id, op, startedAt, tcs);
            throw;
        }
    }

    /// <summary>
    /// What is left of <paramref name="budget"/> since <paramref name="startedAt"/>, never negative
    /// and never zero — a zero would cancel before the already-arrived answer could be read.
    ///
    /// DELIBERATELY UNLIKE <see cref="Left"/>, which returns zero past its deadline. This one is a
    /// RELATIVE budget handed to a grace that is only just opening, so a caller that lands here with
    /// nothing left is one whose ordinary timeout was already spent — and giving it a moment to
    /// collect an answer that has arrived costs nothing anybody is waiting on. An absolute deadline
    /// is a promise to a person, and one more millisecond of it is a promise broken.
    /// </summary>
    static TimeSpan Remaining(long startedAt, TimeSpan budget)
    {
        var spent = TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
        var left = budget - spent;
        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    /// <summary>
    /// What is left until an ABSOLUTE deadline: <see cref="TimeSpan.Zero"/> once it has passed.
    ///
    /// It used to hand a caller past the deadline a fresh millisecond, on the same "never zero"
    /// reasoning <see cref="Remaining"/> still uses — and that reasoning does not transfer. A
    /// millisecond after the operation was promised to be over is not a budget, it is a race the
    /// gate or the reply can win AFTER the stated deadline (Codex round-8 F4). The arithmetic and
    /// the argument for zero now live on <see cref="RiskReducingScope.LeftUntil"/>, next to the
    /// deadline they are about, so the simulator's copy of the same subtraction cannot drift from
    /// this one.
    /// </summary>
    static TimeSpan Left(long deadlineAt) => RiskReducingScope.LeftUntil(deadlineAt);

    async Task<T> Rpc<T>(string op, object? args, CancellationToken ct, bool reducesRisk = false)
    {
        var f = await Rpc(op, args, ct, reducesRisk);
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

    /// <summary>
    /// A placement, on the emergency clock when it is CLOSING a position and on the ordinary one when
    /// it is not — the distinction <see cref="OpensExposure"/> could not make from the op alone.
    /// </summary>
    public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default) =>
        Rpc<OrderInfo>(BridgeOps.Place, cmd, ct, reducesRisk: cmd.Intent is OrderIntent.Close);

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
        $"hello. If ATAS is running with the TradeAgent strategy started, press {Labels.ReinstallBridge} " +
        "on the Checks page so the bridge in the ATAS Strategies folder is this one");

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
        $"The repair is to press {Labels.ReinstallBridge} on the Checks page; if this line survives " +
        "that, another program has taken the pipe name and TradeAgent will not trade through it");

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
