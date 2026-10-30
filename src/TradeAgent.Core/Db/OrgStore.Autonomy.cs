using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>
/// A POSITION'S REQUEST FOR ITS NEXT WAKE (<c>U-org-autonomy</c> item 1; R25 D5): the caller the broker proved, the launch that asks,
/// and how long from now it asks to be woken.
/// </summary>
public sealed record WakeRequest(string Caller, string? Attempt, TimeSpan After);

/// <summary>
/// WHAT A WAKE REQUEST WROTE: the one <c>self</c> wake, when it is due, and the bounds it was held to — the caller's level, the floor
/// between its wakes and its ceiling, both the security board's policy in force.
/// </summary>
public sealed record WakeAsked(string EventId, DateTimeOffset DueAt, string Level, TimeSpan Floor, TimeSpan Ceiling);

public sealed partial class OrgStore
{
    /// <summary>
    /// THE CHIEF AND EVERY HEAD ASK FOR THEIR NEXT WAKE (<c>U-org-autonomy</c> item 1; R25 D5; ORCHESTRATION § 2.5: "a floor and never a
    /// heartbeat"): ONE <c>self</c> wake for the caller, due <see cref="WakeRequest.After"/> from now — the broker-seat equivalent of a
    /// cell's <c>next.json</c>, which a broker seat cannot write. Bounded by the security board's policy in force (<paramref name="policy"/>,
    /// read by the caller from the gateway's policy, never a constant here): the wake is due no sooner than the caller's level's FLOOR
    /// after its last woken turn began (<see cref="RewakeFloors.Floor"/>), and no later than its CEILING after it
    /// (<see cref="RewakeFloors.Ceilings"/>) — the ceiling wake that would wake it anyway; a level whose policy holds no ceiling is woken
    /// by no clock, so it asks for nothing. ONE PENDING REQUEST PER POSITION: a second is refused while the first is untaken. And the AI
    /// day: <paramref name="noRoom"/> is the host's reason one more turn of the caller's seat does not fit its funding chain today — the
    /// envelopes up to the plan's day-total — or null where it fits; a reason refuses. Refused in words, nothing written: no active
    /// position on the chart; a request for nothing or for the past; before the floor; past the ceiling; a pending request; no room.
    /// It places no order, moves no money and reaches no operator authority: a wake is a turn the admission still meters.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public WakeAsked RequestWake(WakeRequest r, RewakeFloors policy, string? noRoom, DateTimeOffset now) => db.Write(_ =>
    {
        var position = Roster().FirstOrDefault(p => p.Id == r.Caller && !p.IsLegacy)
                       ?? throw Forbidden($"'{r.Caller}' is no active position beyond the legacy pair: a wake is asked by the position whose launch asks it.");
        if (r.After <= TimeSpan.Zero)
            throw Refused("a wake is asked for a time after now: 'after_minutes' is a whole number of minutes above zero.");

        var level = position.Level;
        var floor = policy.Floor(level);
        var ceiling = policy.Ceilings.TryGetValue(level, out var c) && c > TimeSpan.Zero ? c : TimeSpan.Zero;
        if (ceiling <= TimeSpan.Zero)
            throw Refused($"the security board's policy holds no ceiling for the {level} level, so no clock wakes {r.Caller} and it asks for "
                          + "none: work addressed to it still wakes it.");

        // ANCHORED AT THE LAST WOKEN TURN (the floor is BETWEEN wakes): a position never woken is anchored now.
        var anchor = new AiAttemptStore(db).LastWoken(r.Caller)?.StartedAt ?? now;
        if (anchor > now) anchor = now;
        var due = now + r.After;
        var soonest = anchor + floor;
        var latest = anchor + ceiling;
        if (due < soonest)
            throw Refused($"the soonest {r.Caller} may be woken by its own request is {Sql.T(soonest)}: the security board's floor between "
                          + $"wakes at the {level} level is {Minutes(floor)} after its last woken turn began ({Sql.T(anchor)}). Ask for at least "
                          + $"{Minutes(soonest - now)} from now, or let work addressed to you wake you.");
        if (due > latest)
            throw Refused($"your ceiling wakes {r.Caller} by {Sql.T(latest)} — the security board's ceiling at the {level} level is "
                          + $"{Minutes(ceiling)} after its last woken turn began — so a request past it asks for nothing. Ask for at most "
                          + $"{Minutes(latest - now)} from now.");

        var events = new MissionEventStore(db);
        if (events.Pending(MissionEventKind.Self, r.Caller) is { Count: > 0 } pending)
            throw Refused($"{r.Caller} has asked for a wake already, due {Sql.T(pending.Min(e => e.DueAt))}: one request at a time, and it "
                          + "stands until it is taken.");

        if (noRoom is not null)
            throw Refused($"one more turn of {r.Caller}'s seat does not fit its AI day: {noRoom}");

        var id = MissionEventIds.Self(r.Attempt is { Length: > 0 } a ? "asked:" + a : "asked:" + now.UtcDateTime.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
        if (!events.RaiseDue(id, MissionEventKind.Self, now, due, role: r.Caller))
            throw Refused($"this launch of {r.Caller} has asked for its wake already: one request a launch.");
        return new WakeAsked(id, due, level, floor, ceiling);
    });

    /// <summary>A span in whole minutes, as the policy states its floors.</summary>
    static string Minutes(TimeSpan span) =>
        $"{Math.Ceiling(Math.Max(0, span.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} minutes";
}
