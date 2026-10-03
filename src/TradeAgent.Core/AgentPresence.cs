using System.Globalization;

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
/// at any point since this mark?" — and it answers it NO whenever it is unsure: a process still
/// running, one that was alive at any point after the mark was taken, or a mark this register cannot
/// place in its own order, all read as "an agent could have done this". A pass that cannot attest
/// does not lie and does not discard the sighting; it records
/// <see cref="MaterialOrigin.InboxUnattested"/>, which is a different, weaker and honest statement.
///
/// <b>Ordered by a sequence it issues, never by a clock</b> (U-inbox-order). Every agent window's
/// opening and closing, and every pass's start (<see cref="Mark"/>), takes the next number from one
/// counter under one lock, so the numbers ARE the order those events happened in. This used to
/// compare wall-clock readings — an exit stamped by <see cref="DateTimeOffset.UtcNow"/> against the
/// instant the last pass began — and a clock that stepped back between the two (an NTP correction, a
/// clock set by hand) left the exit earlier than a pass that began before it: the next pass heard
/// "nobody since" across a window an agent had written in, and recorded its file as the owner's. No
/// step, freeze or jump of the clock can reorder a counter. The wall time is still kept
/// (<see cref="LastAliveAt"/>), for a person to read and for nothing else.
///
/// <b>Why a shared instance rather than a static.</b> The lifetime being tracked is the whole
/// process's — an agent that ran an hour ago and exited still means this hour's inbox sightings are
/// unattested, and a runtime object replaced by <c>PrepareAsync</c> would forget that. It is an
/// instance so tests get their own and never race each other through shared state; production uses
/// <see cref="Shared"/>, and a fresh instance is what a restart is. Nothing here is reachable from
/// the agent-facing pipe, and there is no setter: the only ways to move the sequence are to actually
/// start a process and to actually begin a pass.
/// </summary>
public sealed class AgentPresence
{
    readonly object _gate = new();
    readonly Func<DateTimeOffset> _now;
    int _live;
    long _sequence;
    long? _lastAliveSequence;
    DateTimeOffset? _lastAliveAt;

    /// <param name="now">The wall clock <see cref="LastAliveAt"/> is read from. The machine's, always,
    /// in the product; a test passes one it can step. Nothing this register decides reads it.</param>
    public AgentPresence(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The process-wide instance every real agent process reports itself to.</summary>
    public static AgentPresence Shared { get; } = new();

    /// <summary>
    /// THIS REGISTER'S OWN NAME, carried by every mark it issues. A mark is a place in ONE register's
    /// order and means nothing to another: a restart starts a new register under a new name, so a mark
    /// the last process persisted — after which that process's agent may have gone on writing — is
    /// one this register cannot place, and the window that opened at it is unsure.
    /// </summary>
    public string Epoch { get; } = Guid.NewGuid().ToString("n");

    /// <summary>How many agent processes are running right now.</summary>
    public int Live { get { lock (_gate) return _live; } }

    /// <summary>
    /// The last moment an agent process was known to be alive, or null if none ever was — as the wall
    /// clock read it then. For a person reading the register; the order is the sequence.
    /// </summary>
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
            _lastAliveSequence = ++_sequence;
            _lastAliveAt = _now();
        }
        return new Window(this);
    }

    /// <summary>
    /// A PASS'S PLACE IN THIS REGISTER'S ORDER, taken as the pass begins and before it looks at
    /// anything. Every window that opened or closed before this call has a smaller number, and every
    /// one that opens or closes after it a larger one — whatever the clock says about either.
    /// </summary>
    public PresenceMark Mark()
    {
        lock (_gate) return new PresenceMark(Epoch, ++_sequence);
    }

    /// <summary>
    /// The mark before everything this register will ever see: the window that opens here is "since
    /// before anything happened" — in THIS register, which is the whole question only where nothing
    /// that could have written into the drop folder ever reported anywhere else.
    /// </summary>
    public PresenceMark Origin => new(Epoch, 0);

    /// <summary>
    /// True only when nothing was running at or after <paramref name="since"/> in this register's own
    /// order, nothing is running now, and <paramref name="since"/> is a mark this register issued.
    /// Unsure is false.
    /// </summary>
    public bool NoneSince(PresenceMark since)
    {
        lock (_gate)
            return _live == 0
                   && since.Epoch == Epoch
                   && (_lastAliveSequence is null || _lastAliveSequence < since.Sequence);
    }

    /// <summary>
    /// True only when nothing was running at or after <paramref name="since"/> by the wall clock, and
    /// nothing is running now. A person's question about the register; no pass asks it, because a
    /// clock that stepped back can make an exit read earlier than a pass that began before it.
    /// </summary>
    public bool NoneSince(DateTimeOffset since)
    {
        lock (_gate) return _live == 0 && (_lastAliveAt is null || _lastAliveAt < since);
    }

    /// <summary>
    /// THE REGISTER A QUESTION IS ASKED OF, when the question is a register's own
    /// <see cref="NoneSince(PresenceMark)"/> — that delegate's target IS the register, which is how a
    /// pass handed only the question (<see cref="MaterialScanner.Attests"/>) takes its place in the same
    /// order it asks about. Null for any other question: a fixed answer orders nothing.
    /// </summary>
    public static AgentPresence? Behind(Func<PresenceMark, bool>? question) =>
        question?.Target is AgentPresence register && question.Equals((Func<PresenceMark, bool>)register.NoneSince)
            ? register
            : null;

    sealed class Window(AgentPresence owner) : IDisposable
    {
        bool _closed;

        // The decrement and the next number move together, under one lock. An exit that lowered the
        // count without taking a number would let the very next scan attest a window the process it
        // had just closed was inside. Disposing twice must not lower it twice either.
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_closed) return;
                _closed = true;
                owner._live--;
                owner._lastAliveSequence = ++owner._sequence;
                owner._lastAliveAt = owner._now();
            }
        }
    }
}

/// <summary>
/// A PLACE IN ONE <see cref="AgentPresence"/> REGISTER'S ORDER: the register's name and the number it
/// issued. Persisted between passes as text (<see cref="ToString"/>), and read back by
/// <see cref="Parse"/>, which answers null for anything that is not one — so text nobody can place
/// is never mistaken for a place.
/// </summary>
public readonly record struct PresenceMark(string Epoch, long Sequence)
{
    /// <summary>
    /// A mark no register issued. A window that opens at it is unsure in every register: it stands
    /// wherever the order cannot be shown.
    /// </summary>
    public static PresenceMark Unordered { get; } = new("", 0);

    public override string ToString() => $"{Epoch}/{Sequence.ToString(CultureInfo.InvariantCulture)}";

    public static PresenceMark? Parse(string? text)
    {
        var at = text?.LastIndexOf('/') ?? -1;
        if (at <= 0) return null;
        return long.TryParse(text![(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? new PresenceMark(text[..at], n)
            : null;
    }
}
