namespace TradeAgent.AtasBridge;

/// <summary>
/// WHEN THIS STRATEGY STOPS BEING ANYBODY'S BRIDGE — the three states, the teardown order, the one
/// lock that makes them agree with each other, and THE ONLY DOOR TO THE WITNESS.
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
/// ROUND 10: IT IS A THREE-STATE MACHINE, AND THAT IS WHAT ENDS THE CLASS.
///
/// Round 9 made this the only door and guarded it with a boolean. A boolean cannot express a
/// teardown that is HALF DONE, so <c>Started()</c> — a plain assignment, no lock — could clear it
/// while <c>Stop</c> was still running its steps, and the door was open again for a strategy whose
/// lease was about to be released (F35). Now there is ONE lock, THREE states and four transitions:
/// <c>Running → Stopping</c> at the top of <c>Stop</c>, <c>Stopping → Stopped</c> after the lease is
/// released, <c>Stopped → Running</c> on a start, and nothing else. Every state change and every
/// witness operation — the two writes, which are refused unless RUNNING, and the six reads, which
/// are not — happens inside that one lock. The class-closure argument is that sentence: there is no
/// transition outside the lock to disagree with a guard, and no witness call outside it either.
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
    /// <summary>
    /// THREE STATES, NOT A BOOLEAN, AND THE MIDDLE ONE IS THE FINDING (F35).
    ///
    /// A boolean says "stopped or not", so a teardown that is HALF DONE has to be one of the two —
    /// and it was recorded as "stopped", which is right for refusing writes and wrong for
    /// <see cref="Started"/>: a start arriving while <see cref="Stop"/> was still running cleared
    /// the flag with a plain assignment, put the adapter back into "running" mid-teardown, and let
    /// the abandoned frame loop write into a witness that was about to be released. STOPPING is a
    /// state a start is not legal from, which is a thing a boolean cannot say.
    /// </summary>
    public enum State
    {
        /// <summary>The strategy is live. The only state in which the witness may be written.</summary>
        Running,

        /// <summary>The teardown is under way. No write, and no start either.</summary>
        Stopping,

        /// <summary>The teardown has finished and the lease is released. A start is legal from here.</summary>
        Stopped
    }

    readonly object _gate = new();
    readonly CoidWitness _witness;
    State _state = State.Running;

    /// <summary>The live bridge's witness. The adapter's field initialiser and nothing else.</summary>
    public AdapterTeardown() : this(new CoidWitness()) { }

    /// <summary>The testable shape: a witness on a path of the caller's choosing.</summary>
    public AdapterTeardown(CoidWitness witness) => _witness = witness;

    /// <summary>
    /// Which of the three this adapter is in. Read for diagnostics and by tests; a caller that wants
    /// to ACT on it must go through <see cref="Record"/>, which reads the state and does the thing
    /// under one lock — that is the whole point of this class, and reading this property and then
    /// acting is precisely the two-step PRIOR 21 was about.
    /// </summary>
    public State Now { get { lock (_gate) return _state; } }

    /// <summary>
    /// Whether this adapter has been taken down. Kept as a boolean because that is what the message
    /// on a refused order asks — "is this strategy going away" — and STOPPING and STOPPED are the
    /// same answer to it.
    /// </summary>
    public bool Stopped { get { lock (_gate) return _state != State.Running; } }

    /// <summary>
    /// THE STRATEGY IS RUNNING AGAIN, IF IT IS ALLOWED TO BE. The same instance is started and
    /// stopped many times inside one ATAS process, and its witness takes the lease back on the next
    /// write — but only from <see cref="State.Stopped"/>. A start that arrives while the teardown is
    /// still running is refused and answers false, because the alternative is the door reopening
    /// underneath a teardown that is about to release the lease (F35).
    ///
    /// Refused rather than thrown: this is called from ATAS's own callback, and a teardown path that
    /// throws is worse than one that says no.
    /// </summary>
    public bool Started()
    {
        lock (_gate)
        {
            if (_state == State.Stopping) return false;
            _state = State.Running;
            return true;
        }
    }

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
    public string? Trouble => Read(w => w.Trouble);

    public string? Path => Read(w => w.Path);

    public string? LastWriteFailure => Read(w => w.LastWriteFailure);

    public string Token() => Read(w => w.Token());

    public CoidWitnessRecord? PriorSession(string clientOrderId) => Read(w => w.PriorSession(clientOrderId));

    public IReadOnlyList<string> PriorSessionIds(int max) => Read(w => w.PriorSessionIds(max));

    /// <summary>
    /// EVERY WITNESS OPERATION GOES THROUGH THE LOCK — this one and <see cref="Record"/> are the
    /// only two ways to reach <see cref="_witness"/>, and both take <see cref="_gate"/>. A read is
    /// not REFUSED by the state, and that is a deliberate departure from "refused unless Running",
    /// stated here rather than left implicit: <c>SupportsClientOrderId</c> is
    /// <c>proof &amp;&amp; Trouble is null</c> on the adapter, so a <c>Trouble</c> that answered null
    /// for a stopped strategy would report the capability as PROVEN over a witness nobody had asked,
    /// which is the one direction this file must never fail in. Refusing them the other way — a
    /// blank diagnostic — removes the sentences an operator needs exactly while the thing they are
    /// diagnosing is being taken down.
    ///
    /// The lock costs nothing that was not already being paid: every one of these enters
    /// <see cref="CoidWitness"/>'s own gate, which a write in flight already holds.
    /// </summary>
    T Read<T>(Func<CoidWitness, T> read)
    {
        lock (_gate) return read(_witness);
    }

    // ---------------------------------------------------------------- the guard itself

    /// <summary>
    /// A STOPPED STRATEGY RECORDS NOTHING. The order-event fan stays subscribed after teardown and
    /// reaches for the witness on every order in ATAS's book carrying a comment, so without this a
    /// stopped strategy took the lease back on the next event and held it for the life of the ATAS
    /// process, refusing every order the live bridge then tried to record.
    /// </summary>
    /// PRIOR 21: THE CHECK AND THE WRITE ARE ONE ACT, UNDER THE LOCK THE RELEASE TAKES. Reading the
    /// state and then writing left a window the width of the whole write: the fan reads it while the
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
            if (_state != State.Running) return false;
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
    /// ONE TEARDOWN, HOWEVER MANY CALLERS ASK FOR ONE. <c>Stop</c> was re-entrant by omission: two
    /// overlapping calls both ran their steps, and whichever finished FIRST published
    /// <see cref="State.Stopped"/> and released the lease while the other was still calling into
    /// ATAS. From that instant <see cref="Started"/> was legal again and a write could take the
    /// lease back — F35's harm, reached without a start racing anything, just by asking to stop
    /// twice. ATAS does exactly that: <c>OnStopping</c> and a dispose on the way down are two
    /// callers, and a strategy removed while the platform is closing gets both.
    ///
    /// So the entry is a COMPARE-AND-SET under the same lock every other transition takes: the
    /// caller that finds RUNNING owns the teardown, everyone else returns at once and writes
    /// nothing. STOPPED is published by that one owner, after the last step, and until then
    /// <see cref="Started"/> answers false to everybody. The compare and the set are one act for the
    /// same reason the guard and the write are: two callers that both READ running would both
    /// proceed, which is the bug with an extra step in it.
    public void Stop(Action steps)
    {
        // STOPPING FIRST, AND UNDER THE LOCK. Anything still holding a reference to this instance
        // stops acting through it before the steps below run — the fan runs on ATAS's thread and
        // does not wait — and taking the lock to say so is what makes the transition and the guard
        // one act: a Record already inside the lock finishes its write, and one that is waiting for
        // the lock finds STOPPING when it gets there. The steps themselves run OUTSIDE the lock,
        // because they call into ATAS and this lock is one the reads take.
        lock (_gate)
        {
            if (_state != State.Running) return;
            _state = State.Stopping;
        }

        try { steps(); }
        finally
        {
            lock (_gate)
            {
                try { _witness.Dispose(); } catch (Exception) { /* teardown must finish */ }
                // AND ONLY NOW IS IT STOPPED — after the lease is released, in the same critical
                // section, so there is no instant at which a start is legal and the witness is still
                // this stopped strategy's.
                _state = State.Stopped;
            }
        }
    }
}
