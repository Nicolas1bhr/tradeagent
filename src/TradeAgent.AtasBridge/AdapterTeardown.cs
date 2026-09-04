namespace TradeAgent.AtasBridge;

/// <summary>
/// WHEN THIS STRATEGY STOPS BEING ANYBODY'S BRIDGE — the flag, the teardown order, and the one lock
/// that makes those two agree with each other.
///
/// IT IS A SEPARATE CLASS BECAUSE <c>AtasStrategyAdapter.cs</c> IS <c>&lt;Compile Remove&gt;</c>d
/// EVERYWHERE EXCEPT A WINDOWS BOX WITH ATAS INSTALLED. Two defects were found in that file's
/// teardown, and neither could be given a failing test where it stood: nothing in the cross-platform
/// suite can compile it, let alone run it. The rule they are about needs no ATAS type at all — it is
/// about a boolean, a lock and a disposal — so it lives here, where a test can drive both
/// interleavings against a real <see cref="CoidWitness"/> on any machine. Same move, same reason, as
/// <see cref="AdapterTouchedOrders"/> and <see cref="ClientOrderIdProof"/>.
/// </summary>
public sealed class AdapterTeardown
{
    readonly object _gate = new();
    volatile bool _stopped;

    /// <summary>
    /// Whether this adapter has been taken down. Read for diagnostics only — a caller that wants to
    /// act on it must use <see cref="Record"/>, which is the whole point of this class.
    /// </summary>
    public bool Stopped => _stopped;

    /// <summary>The strategy is running again. The same instance may be started and stopped many
    /// times inside one ATAS process, and its witness takes the lease back on the next write.</summary>
    public void Started() => _stopped = false;

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
    /// in the same ATAS process, for no reason.
    /// </summary>
    /// F26 = R2: THE RELEASE IS IN A <c>finally</c>, because the steps before it call into ATAS while
    /// ATAS is taking the strategy down — the one moment the platform is most likely to answer with
    /// an exception. As two plain statements, an unsubscribe that threw skipped the release and the
    /// lease survived a TERMINAL path: an instance that will never write again, holding the witness
    /// against every bridge started afterwards until the process itself dies. The exception is not
    /// swallowed — a teardown that ate its own failures would be worse — it simply no longer decides
    /// whether the release happens.
    public void Stop(Action steps, Action releaseWitness)
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
                try { releaseWitness(); } catch (Exception) { /* teardown must finish */ }
            }
        }
    }
}
