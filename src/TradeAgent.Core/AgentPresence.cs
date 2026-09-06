namespace TradeAgent.Core;

/// <summary>
/// Whether an agent process has been alive, and when it last was. The one fact that lets the
/// material scanner say <see cref="MaterialOrigin.Inbox"/> and mean it.
///
/// <b>Why this exists.</b> `material` is the MEASUREMENT table — `CLAUDE.md`: "a record the observed
/// party can rewrite is not one". `Inbox` claims something the filesystem cannot show on its own:
/// that the ACCOUNT OWNER put the file there. Before REVIEW 2026-09-05b finding 5 that claim came
/// from the directory name alone, so any process that could write a file into the drop folder could
/// author a measurement row saying the owner had handed it over — no sandbox escape needed, one
/// relative path from the agent's own working directory. Moving the agent's tree out of the drop
/// folder's parent (<see cref="Paths.AgentHome"/>) makes that a deliberate climb rather than a
/// typo; this makes the claim provable instead of assumed.
///
/// <b>What it can and cannot say.</b> It answers exactly one question — "was no agent process alive
/// at any point since <paramref name="since"/>?" — and it answers it NO whenever it is unsure: a
/// process still running, or one that exited at the same instant the window opened, both read as
/// "an agent could have done this". A pass that cannot attest does not lie and does not discard the
/// sighting; it records <see cref="MaterialOrigin.InboxUnattested"/>, which is a different, weaker
/// and honest statement.
///
/// <b>Why a shared instance rather than a static.</b> The lifetime being tracked is the whole
/// process's — an agent that ran an hour ago and exited still means this hour's inbox sightings are
/// unattested, and a runtime object replaced by <c>PrepareAsync</c> would forget that. It is an
/// instance so tests get their own and never race each other through shared state; production uses
/// <see cref="Shared"/>. Nothing here is reachable from the agent-facing pipe, and there is no
/// setter: the only way to move it is to actually start a process.
/// </summary>
public sealed class AgentPresence
{
    readonly object _gate = new();
    int _live;
    DateTimeOffset? _lastAliveAt;

    /// <summary>The process-wide instance every real agent process reports itself to.</summary>
    public static AgentPresence Shared { get; } = new();

    /// <summary>How many agent processes are running right now.</summary>
    public int Live { get { lock (_gate) return _live; } }

    /// <summary>The last moment an agent process was known to be alive, or null if none ever was.</summary>
    public DateTimeOffset? LastAliveAt { get { lock (_gate) return _lastAliveAt; } }

    /// <summary>
    /// Marks an agent process alive until the returned handle is disposed. Wrapped around every
    /// process started in the agent's working directory, including the short per-message runs and
    /// the sign-in — over-inclusive on purpose, because a wrong "no agent ran" is the failure that
    /// matters and a wrong "an agent ran" only costs a weaker word on a row.
    /// </summary>
    public IDisposable Enter()
    {
        lock (_gate)
        {
            _live++;
            _lastAliveAt = DateTimeOffset.UtcNow;
        }
        return new Window(this);
    }

    /// <summary>
    /// True only when nothing was running at or after <paramref name="since"/>, and nothing is
    /// running now. Unsure is false.
    /// </summary>
    public bool NoneSince(DateTimeOffset since)
    {
        lock (_gate) return _live == 0 && (_lastAliveAt is null || _lastAliveAt < since);
    }

    sealed class Window(AgentPresence owner) : IDisposable
    {
        bool _closed;

        // The decrement and the timestamp move together, under one lock. An exit that lowered the
        // count without moving the clock would let the very next scan attest a window the process
        // it had just closed was inside. Disposing twice must not lower it twice either.
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_closed) return;
                _closed = true;
                owner._live--;
                owner._lastAliveAt = DateTimeOffset.UtcNow;
            }
        }
    }
}
