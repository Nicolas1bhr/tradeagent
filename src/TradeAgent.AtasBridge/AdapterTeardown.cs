namespace TradeAgent.AtasBridge;

/// <summary>
/// WHEN THIS STRATEGY STOPS BEING ANYBODY'S BRIDGE — the flag, the teardown order, the one lock that
/// makes those two agree with each other, and THE ONLY DOOR TO THE WITNESS.
///
/// IT IS A SEPARATE CLASS BECAUSE <c>AtasStrategyAdapter.cs</c> IS <c>&lt;Compile Remove&gt;</c>d
/// EVERYWHERE EXCEPT A WINDOWS BOX WITH ATAS INSTALLED. Two defects were found in that file's
/// teardown, and neither could be given a failing test where it stood: nothing in the cross-platform
/// suite can compile it, let alone run it. The rule they are about needs no ATAS type at all — it is
/// about a boolean, a lock and a disposal — so it lives here, where a test can drive both
/// interleavings against a real <see cref="CoidWitness"/> on any machine. Same move, same reason, as
/// <see cref="AdapterTouchedOrders"/> and <see cref="ClientOrderIdProof"/>.
///
/// ROUND 9: IT OWNS THE WITNESS, AND THAT IS THE FIX RATHER THAN A TIDY-UP.
///
/// Round 8 guarded the order-event fan and left the adapter holding its own <c>CoidWitness</c>
/// field, so the guard was a CONVENTION at four call sites and was applied at one of them. The
/// other three — <c>Place</c>'s write-ahead record, <c>Place</c>'s identification and
/// <c>ClosePosition</c>'s write-ahead record — run on the BridgeServer frame loop, which outlives
/// the teardown BY CONSTRUCTION: <c>DisposeAsync</c> waits five seconds for that loop and then gives
/// up, <c>StopBridge</c> catches its own timeout, and its own doc says the abandoned loop still
/// holds its pipe client until whatever wedged it returns. A <c>Place</c> still in flight therefore
/// reaches the witness AFTER the lease has been released, leases it again for a strategy ATAS has
/// already taken down, and holds it for the life of the ATAS process — PRIOR 21's own harm, through
/// a door the fix did not cover.
///
/// So the witness is not the adapter's any more. The adapter has no <c>CoidWitness</c> reference at
/// all (<c>grep -n "_witness" AtasStrategyAdapter.cs</c> finds nothing), every write is a method on
/// this class, and a fifth write site cannot be added without going through the same lock. Reads
/// are forwarded unguarded because reading takes no lease — <c>EnsureRecovered</c> is explicitly a
/// path that never does — and refusing a diagnostic to a stopped strategy would remove the very
/// sentences an operator needs while it is going down.
/// </summary>
public sealed class AdapterTeardown
{
    readonly object _gate = new();
    readonly CoidWitness _witness;
    volatile bool _stopped;

    /// <summary>The live bridge's witness. The adapter's field initialiser and nothing else.</summary>
    public AdapterTeardown() : this(new CoidWitness()) { }

    /// <summary>The testable shape: a witness on a path of the caller's choosing.</summary>
    public AdapterTeardown(CoidWitness witness) => _witness = witness;

    /// <summary>
    /// Whether this adapter has been taken down. Read for diagnostics only — a caller that wants to
    /// act on it must use <see cref="Record"/>, which is the whole point of this class.
    /// </summary>
    public bool Stopped => _stopped;

    /// <summary>The strategy is running again. The same instance may be started and stopped many
    /// times inside one ATAS process, and its witness takes the lease back on the next write.</summary>
    public void Started() => _stopped = false;

    // ---------------------------------------------------------------- the two writes

    /// <summary>
    /// THE WRITE-AHEAD CLAIM, GUARDED. Returns false when the record did not reach the disk OR when
    /// this strategy is being taken down; both mean the same thing to the caller — nothing was
    /// submitted — and <see cref="Stopped"/> says which, for the message.
    ///
    /// It is refused rather than raced. The alternative is what the frame loop did before: write
    /// after the release, re-lease the file for a strategy that no longer exists, and refuse every
    /// order the live bridge then tries to record.
    /// </summary>
    public bool Submitting(string clientOrderId, string? accountId, string? symbol, string? side,
                           decimal quantity, decimal? limitPrice)
    {
        var recorded = false;
        return Record(() => recorded = _witness.Submitting(clientOrderId, accountId, symbol, side,
                                                           quantity, limitPrice))
               && recorded;
    }

    /// <summary>
    /// THE HALF WE DID NOT WRITE, GUARDED. Both places that record a broker id come here — the one
    /// inside <c>Place</c>, which runs on the frame loop, and the order-event fan, which stays
    /// subscribed after teardown.
    /// </summary>
    public bool Identified(string clientOrderId, string? brokerOrderId) =>
        Record(() => _witness.Identified(clientOrderId, brokerOrderId));

    // ---------------------------------------------------------------- reads, unguarded

    /// <summary>
    /// UNGUARDED ON PURPOSE, AND THE REASON IS THE LEASE. Ownership is a property of WRITING: a
    /// reader answers from the committed file and its own memory and never comes to the lock
    /// (<c>CoidWitness.EnsureRecovered</c>). So nothing below can take the witness back for a
    /// strategy that has stopped, and refusing them would blank the diagnostics an operator reads
    /// exactly while the thing they are diagnosing is being taken down.
    /// </summary>
    public string? Trouble => _witness.Trouble;

    public string? Path => _witness.Path;

    public string? LastWriteFailure => _witness.LastWriteFailure;

    public string Token() => _witness.Token();

    public CoidWitnessRecord? PriorSession(string clientOrderId) => _witness.PriorSession(clientOrderId);

    public IReadOnlyList<string> PriorSessionIds(int max) => _witness.PriorSessionIds(max);

    // ---------------------------------------------------------------- the guard itself

    /// <summary>
    /// A STOPPED STRATEGY RECORDS NOTHING. The order-event fan stays subscribed after teardown and
    /// reaches for the witness on every order in ATAS's book carrying a comment, so without this a
    /// stopped strategy took the lease back on the next event and held it for the life of the ATAS
    /// process, refusing every order the live bridge then tried to record.
    /// </summary>
    /// PRIOR 21: THE CHECK AND THE WRITE ARE ONE ACT, UNDER THE LOCK THE RELEASE TAKES. Reading the
    /// flag and then writing left a window the width of the whole write: the fan reads it while the
    /// strategy is still running, ATAS stops the strategy, <see cref="Stop"/> releases the lease, and
    /// the fan — already past its check — leases the file again for a strategy that no longer exists.
    /// A second read of the flag would not close it; only one of the two orders being CHOSEN does.
    ///
    /// The caller keeps everything expensive outside: this holds a lock that teardown waits on, and
    /// teardown runs on ATAS's own thread.
    public bool Record(Action write)
    {
        lock (_gate)
        {
            if (_stopped) return false;
            write();
            return true;
        }
    }

    /// <summary>
    /// Teardown, in order: the caller's own steps, then the witness stops being ours.
    ///
    /// The lease is held for the life of the owner, and a strategy ATAS has taken down is not the
    /// owner of anything — leaving it held would refuse the witness to a bridge started afterwards
    /// in the same ATAS process, for no reason. The instance stays usable: if this strategy is
    /// started again, its next write takes the lease back.
    /// </summary>
    /// F26 = R2: THE RELEASE IS IN A <c>finally</c>, because the steps before it call into ATAS while
    /// ATAS is taking the strategy down — the one moment the platform is most likely to answer with
    /// an exception. As two plain statements, an unsubscribe that threw skipped the release and the
    /// lease survived a TERMINAL path: an instance that will never write again, holding the witness
    /// against every bridge started afterwards until the process itself dies. The exception is not
    /// swallowed — a teardown that ate its own failures would be worse — it simply no longer decides
    /// whether the release happens.
    public void Stop(Action steps)
    {
        // FIRST, so that anything still holding a reference to this instance stops acting through it
        // before the teardown below has finished — the fan runs on ATAS's thread and does not wait.
        // Set outside the lock and BEFORE it, so a Record that is already inside cannot read false
        // after this point, whichever of the two reaches the lock first.
        _stopped = true;
        try { steps(); }
        finally
        {
            lock (_gate)
            {
                try { _witness.Dispose(); } catch (Exception) { /* teardown must finish */ }
            }
        }
    }
}
