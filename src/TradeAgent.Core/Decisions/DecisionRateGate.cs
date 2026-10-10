using System.Globalization;

namespace TradeAgent.Core.Decisions;

/// <summary>
/// THE HOSTS' RATE LIMITS, ENFORCED BEFORE A CALL IS MADE (<c>U-decision-card</c>) — per instrument, in memory, refusing
/// in words and never waiting.
///
/// <para><b>What it counts.</b> Every admission of the last rolling second, against the instrument's
/// <see cref="DecisionLimits.RequestsPerSecond"/>; and tokens against its <see cref="DecisionLimits.TokensPerSecond"/>, where
/// a call in flight is charged the most it could use — <see cref="DecisionInstrument.ReservedTokens"/>, the bound its
/// money is reserved at — because the host counts tokens with a tokenizer this app does not have
/// (<see cref="DecisionRequests.Refusal"/>), and a settled call is charged what its answer says it used, input and output,
/// for the second after it ended. A call with no usage keeps its bound: unknown is never zero. At TypeSafe's 100,000 tokens
/// a second that leaves ONE call in flight at a time — the app's conservative reading, not the host's figure.</para>
///
/// <para><b>Zero is TradeAgent's own bound, never "none".</b> A rate nobody documents — OpenRouter's route documents none —
/// or one <c>decision-models.json</c> sets to zero is not a host with no limit: it is a host whose limit TradeAgent does not
/// know, so the app applies its own, named as its own — <see cref="OwnCallsInFlight"/> call in flight and
/// <see cref="OwnRequestsPerSecond"/> a second — and no file lifts it (<see cref="DecisionInstruments.Read"/> refuses a row
/// that sets a rate its host does not document).</para>
///
/// <para><b>A host that says "not now" holds the instrument.</b> A 429 (too many requests), a 529 (overloaded) or a 402
/// (a credit limit) keeps every call to that instrument out until the instant its <c>Retry-After</c> named — seconds or a
/// date; an unreadable one counts as absent — or, with none, for TradeAgent's own back-off: <see cref="FirstBackOff"/>,
/// doubling with each such answer in a row up to <see cref="MaxBackOff"/>, the run ended by an answer. The failed call itself
/// stays FAILED and keeps its reservation as its cost: whether a 429 is billed is not documented.</para>
///
/// <para><b>Asked before the key is read or anything reserved</b> (<see cref="IDecisionModel"/> property 1): a call the
/// rate would refuse reads no key and writes no row. A call admitted here and then refused for its key, its budget or its
/// ledger is withdrawn — the host saw nothing. It REFUSES and never waits: a waiting call would hold the owner's press, and
/// the caller decides when to ask again, as the wire's "never a retry" already says.</para>
///
/// <para><b>In memory only.</b> A restart forgets every window and every hold; a second is shorter than a restart, and a
/// host's longer hold is the host's to repeat.</para>
///
/// <para>Process-wide (<see cref="Shared"/>) for the reason <c>LiveAttempts.Shared</c> is: the app rebuilds a wire when the
/// file changes an instrument, and the window belongs to the instrument, not to the object. A test brings its own.</para>
/// </summary>
public sealed class DecisionRateGate
{
    /// <summary>The process's gate — the one every decision model in the app is admitted through.</summary>
    public static DecisionRateGate Shared { get; } = new();

    /// <summary>The window every rate is counted over: any rolling second.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>TradeAgent's own bound where no rate is documented: one call in flight at a time. Not a vendor figure.</summary>
    public const int OwnCallsInFlight = 1;

    /// <summary>TradeAgent's own bound where no rate is documented: one call a second. Not a vendor figure.</summary>
    public const int OwnRequestsPerSecond = 1;

    /// <summary>TradeAgent's own back-off after a host's "not now" that named no time — not a vendor figure.</summary>
    public static readonly TimeSpan FirstBackOff = TimeSpan.FromSeconds(1);

    /// <summary>The longest that back-off grows to, doubling with each such answer in a row.</summary>
    public static readonly TimeSpan MaxBackOff = TimeSpan.FromSeconds(60);

    /// <summary>The statuses that hold an instrument: too many requests, overloaded, and a credit limit.</summary>
    public static bool Holds(int? status) => status is 429 or 529 or 402;

    readonly Lock _lock = new();
    readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);

    /// <summary>One instrument's calls of the last second and those still in flight, and its hold.</summary>
    internal sealed class Lane
    {
        public readonly List<Call> Calls = [];
        public DateTimeOffset? HeldUntil;
        public string? HeldWhy;
        public int InARow;
    }

    internal sealed class Call(DateTimeOffset admittedAt, long bound)
    {
        public DateTimeOffset AdmittedAt { get; } = admittedAt;
        public long Bound { get; } = bound;
        public DateTimeOffset? EndedAt { get; set; }
        public long Tokens { get; set; } = bound;
        public bool Flying => EndedAt is null;
        public bool Done { get; set; }
    }

    /// <summary>What <see cref="Admit"/> answered: a ticket to send on, or the refusal in words. Exactly one.</summary>
    public sealed record Admission(Ticket? Ticket, string? Refusal);

    /// <summary>
    /// MAY ONE CALL TO <paramref name="instrument"/> GO AT <paramref name="now"/>? Admitted, it is counted from this
    /// instant at its bound and the caller holds the ticket until it settles or withdraws it; refused, nothing is counted
    /// and the words say which limit, whose it is, and when the next call can go.
    /// </summary>
    public Admission Admit(DecisionInstrument instrument, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        lock (_lock)
        {
            if (!_lanes.TryGetValue(instrument.Id, out var lane)) _lanes[instrument.Id] = lane = new Lane();
            lane.Calls.RemoveAll(c => !c.Flying && c.EndedAt <= now - Window && c.AdmittedAt <= now - Window);

            // HELD, BY THE HOST'S OWN WORD OR BY THE BACK-OFF AFTER IT.
            if (lane.HeldUntil is { } until && until > now)
                return new(null, $"{lane.HeldWhy} Nothing goes to {instrument.DisplayName} before {When(until, now)}; this call was "
                                 + "not sent, and nothing was reserved or charged.");

            var limits = instrument.Limits;
            var bound = instrument.ReservedTokens;

            // ADMISSIONS IN ANY ROLLING SECOND.
            var perSecond = limits.RequestsPerSecond > 0 ? limits.RequestsPerSecond : OwnRequestsPerSecond;
            var recent = lane.Calls.Where(c => c.AdmittedAt > now - Window).OrderBy(c => c.AdmittedAt).ToList();
            if (recent.Count >= perSecond)
            {
                var next = recent[recent.Count - perSecond].AdmittedAt + Window;
                return new(null, limits.RequestsPerSecond > 0
                    ? $"{instrument.DisplayName} takes at most {perSecond:N0} requests a second ({Whose(instrument, Rate.Requests)}), and "
                      + $"{recent.Count:N0} went in the last second, so this call was not sent; nothing was reserved or charged. "
                      + $"The next can go at {When(next, now)}."
                    : $"{OwnBound(instrument, Rate.Requests)} One went in the last second, so this call was not sent; nothing was "
                      + $"reserved or charged. The next can go at {When(next, now)}.");
            }

            if (limits.TokensPerSecond > 0)
            {
                // TOKENS IN ANY ROLLING SECOND, a call in flight at its bound.
                if (bound > limits.TokensPerSecond)
                    return new(null, $"{instrument.DisplayName} takes at most {limits.TokensPerSecond:N0} tokens a second "
                                     + $"({Whose(instrument, Rate.Tokens)}), and one call may use up to {bound:N0}, so no call can be sent "
                                     + "to it; nothing was reserved or charged.");

                var counted = lane.Calls.Where(c => c.Flying || c.EndedAt > now - Window).ToList();
                var used = counted.Sum(c => c.Tokens);
                if (used + bound > limits.TokensPerSecond)
                    return new(null, $"{instrument.DisplayName} takes at most {limits.TokensPerSecond:N0} tokens a second "
                                     + $"({Whose(instrument, Rate.Tokens)}). TradeAgent counts a call in flight at the most it could "
                                     + $"use — {bound:N0} tokens — until its answer says what it used, and {used:N0} are counted in the "
                                     + "last second, so this call was not sent; nothing was reserved or charged. The next can go "
                                     + $"{NextForTokens(counted, limits.TokensPerSecond - bound, now)}.");
            }
            else if (lane.Calls.Count(c => c.Flying) >= OwnCallsInFlight)
            {
                return new(null, $"{OwnBound(instrument, Rate.Tokens)} A call is in flight, so this one was not sent; nothing was "
                                 + "reserved or charged. The next can go once it is answered.");
            }

            var call = new Call(now, bound);
            lane.Calls.Add(call);
            return new(new Ticket(this, lane, call, instrument.DisplayName), null);
        }
    }

    /// <summary>When enough of the second's tokens will have left it for one more call: never sooner than the answers.</summary>
    static string NextForTokens(List<Call> counted, long room, DateTimeOffset now)
    {
        if (counted.Any(c => c.Flying)) return "once the call in flight is answered";
        var left = counted.Sum(c => c.Tokens);
        foreach (var c in counted.OrderBy(c => c.EndedAt))
        {
            left -= c.Tokens;
            if (left <= room) return $"at {When(c.EndedAt!.Value + Window, now)}";
        }
        return $"at {When(now, now)}";
    }

    enum Rate { Tokens, Requests }

    /// <summary>
    /// WHOSE A LIMIT IS: the host's, read on a named day from a named page, when it is this build's own figure; the file's,
    /// not dated, when <c>decision-models.json</c> moved it — the file must date and source a price and need not a limit.
    /// </summary>
    static string Whose(DecisionInstrument instrument, Rate rate)
    {
        var shipped = DecisionInstruments.BuiltInLimits(instrument.Id);
        var moved = shipped is null
                    || (rate == Rate.Tokens ? shipped.TokensPerSecond != instrument.Limits.TokensPerSecond
                                            : shipped.RequestsPerSecond != instrument.Limits.RequestsPerSecond);
        return moved
            ? "the limit decision-models.json sets, not dated"
            : $"the host's documented limit, read {instrument.RatesReadOn} from {instrument.RatesSource}";
    }

    /// <summary>The sentence that names TradeAgent's own bound as TradeAgent's, and why it applies.</summary>
    static string OwnBound(DecisionInstrument instrument, Rate rate)
    {
        var shipped = DecisionInstruments.BuiltInLimits(instrument.Id);
        var documented = shipped is not null && (rate == Rate.Tokens ? shipped.TokensPerSecond : shipped.RequestsPerSecond) > 0;
        var which = rate == Rate.Tokens ? "tokens-a-second" : "requests-a-second";
        var why = documented
            ? $"decision-models.json sets no {which} rate for {instrument.DisplayName}"
            : $"No {which} rate is documented for {instrument.DisplayName}";
        return $"{why}, so TradeAgent applies its own bound — not a vendor's figure: {OwnCallsInFlight} call in flight at a time "
               + $"and {OwnRequestsPerSecond} a second.";
    }

    /// <summary>
    /// AN INSTANT AS THE OWNER READS IT: their wall clock, rounded UP to the whole second — "not before" — with the day
    /// when it is more than twelve hours off.
    /// </summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var over = local.Ticks % TimeSpan.TicksPerSecond;
        if (over != 0) local = local.AddTicks(TimeSpan.TicksPerSecond - over);
        return local.ToString(at - now < TimeSpan.FromHours(12) ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>What the host's status means, in a few words.</summary>
    static string Meaning(int status) => status switch
    {
        429 => "too many requests",
        529 => "overloaded",
        402 => "payment required: a credit limit",
        _ => "an error"
    };

    /// <summary>
    /// ONE ADMITTED CALL. The caller settles it once the call has gone — or may have — or withdraws it when it was never
    /// sent; whichever comes first counts and the other does nothing.
    /// </summary>
    public sealed class Ticket
    {
        readonly DecisionRateGate _gate;
        readonly Lane _lane;
        readonly Call _call;
        readonly string _name;

        internal Ticket(DecisionRateGate gate, Lane lane, Call call, string name)
        {
            _gate = gate;
            _lane = lane;
            _call = call;
            _name = name;
        }

        /// <summary>
        /// The call was refused after the gate — for its key, its budget or its ledger — and NOT SENT: it is taken out as
        /// though it never came, because the host saw nothing.
        /// </summary>
        public void Withdraw()
        {
            lock (_gate._lock)
            {
                if (_call.Done) return;
                _call.Done = true;
                _lane.Calls.Remove(_call);
            }
        }

        /// <summary>
        /// The call went, or may have, and ended at <paramref name="endedAt"/>: charged <paramref name="tokens"/> — its input
        /// and output as the answer reported them — or, when null, the bound it flew at. A 429, 529 or 402 holds the
        /// instrument until <paramref name="retryAfter"/>, or for the back-off when that is null; a 2xx ends a run of them.
        /// </summary>
        public void Settle(DateTimeOffset endedAt, long? tokens, int? status, DateTimeOffset? retryAfter)
        {
            lock (_gate._lock)
            {
                if (_call.Done) return;
                _call.Done = true;
                _call.EndedAt = endedAt;
                _call.Tokens = tokens is { } used and >= 0 ? used : _call.Bound;

                if (status is >= 200 and <= 299) _lane.InARow = 0;
                if (!Holds(status)) return;

                _lane.InARow++;
                var code = status!.Value;
                DateTimeOffset until;
                string why;
                if (retryAfter is { } asked)
                {
                    until = asked;
                    why = $"{_name} answered {code} ({Meaning(code)}) at {When(endedAt, endedAt)} and asked to be left until "
                          + $"{When(asked, endedAt)}.";
                }
                else
                {
                    var wait = TimeSpan.FromTicks(Math.Min(MaxBackOff.Ticks,
                        FirstBackOff.Ticks * (1L << Math.Min(_lane.InARow - 1, 30))));
                    until = endedAt + wait;
                    why = $"{_name} answered {code} ({Meaning(code)}) at {When(endedAt, endedAt)} without saying how long to wait, "
                          + $"so TradeAgent waits {wait.TotalSeconds:0} s — its own back-off, doubling with each such answer in a row "
                          + $"up to {MaxBackOff.TotalSeconds:0} s, not the host's figure.";
                }

                if (_lane.HeldUntil is not { } held || until > held)
                {
                    _lane.HeldUntil = until;
                    _lane.HeldWhy = why;
                }
            }
        }
    }
}
