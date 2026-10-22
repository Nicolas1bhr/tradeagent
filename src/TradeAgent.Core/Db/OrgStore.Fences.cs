namespace TradeAgent.Core.Db;

/// <summary>
/// WHY AN ATTEMPT MAY DO NOTHING MORE (<c>U-org-verbs-b</c>; ORCHESTRATION § 2.3 <i>Fences</i>): the authority-changing transition
/// that ended it — its mandate's own <c>cancelled</c> or <c>rescoped</c> event, or an <c>org_event</c> <c>fenced</c> naming its
/// position — written at or after the attempt started. A DB fact, read on every operation: restart-safe and atomic with the
/// transition that wrote it, never an in-memory counter a crash could lose between the commit and the bump (survey Q13).
/// </summary>
/// <param name="Attempt">The fenced launch.</param>
/// <param name="Position">The position the launch ran for.</param>
/// <param name="Cause">What fenced it: <c>cancelled</c>, <c>rescoped</c> or <c>fenced</c> (a position-wide fence).</param>
/// <param name="Mandate">The mandate whose event fenced it, or null for a position-wide fence.</param>
/// <param name="At">When the fencing transition was written.</param>
/// <param name="Detail">The transition's own reason or app-composed detail.</param>
public sealed record OrgFence(string Attempt, string Position, string Cause, string? Mandate, DateTimeOffset At, string? Detail)
{
    /// <summary>The fence in words, naming the transition and its cause — what a refusal, a cut turn and a held output say.</summary>
    public string Words => Mandate is { } m
        ? $"launch {Attempt} of {Position} is fenced: its mandate {m} was {Cause} at {At:O}{(Detail is { Length: > 0 } d ? $" ({d})" : "")}, "
          + "and an attempt under a stale authority does nothing more"
        : $"launch {Attempt} of {Position} is fenced: its position was fenced at {At:O}{(Detail is { Length: > 0 } d2 ? $" ({d2})" : "")}, "
          + "and an attempt under a stale authority does nothing more";
}

public sealed partial class OrgStore
{
    /// <summary>
    /// THE ONE EXEMPTION, AND IT IS A GUARD (<c>U-org-verbs-b</c>; AUTH-001, ORDER-001, UNKNOWN-001): a fence acts on the
    /// organisation's AI only — "anything about <c>operations</c> stays his presses" (ORCHESTRATION § 2.3) — so the legacy pair,
    /// a launch attributed to nobody (read as the chair's), and an app principal are NEVER fenced, whatever a row says. Asked on
    /// the caller's role BEFORE anything is read, so no fence read — and no failure of one — ever stands between the Operations
    /// Director and an order op.
    /// </summary>
    public static bool NeverFenced(string? role) => role is null || CouncilRoles.IsKnown(role) || IsAppPrincipal(role);

    /// <summary>
    /// WHETHER <paramref name="attemptId"/> IS FENCED, and by what (<c>U-org-verbs-b</c> item 1): its mandate's latest event
    /// (claims aside) is <c>cancelled</c> or <c>rescoped</c> at or after the attempt started, or an <c>org_event</c>
    /// <c>fenced</c> names its position at or after that. Null when neither holds, when no such launch is recorded, or when its
    /// role is one <see cref="NeverFenced"/> exempts. A read that THROWS is not an answer: the caller refuses the operation
    /// (refuse, never guess).
    /// </summary>
    public OrgFence? FencedSince(string attemptId) => db.Read(_ =>
    {
        string? role, mandate;
        DateTimeOffset started;
        using (var c = db.Cmd("SELECT role, mandate_id, started_at FROM ai_attempt WHERE id=$id", ("$id", attemptId)))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read()) return null;
            role = Sql.S(r.GetValue(0));
            mandate = Sql.S(r.GetValue(1));
            started = Sql.Time(r.GetValue(2));
        }
        if (NeverFenced(role)) return null;

        // THE MANDATE'S OWN TRANSITION: its latest event that is not a claim, when that closed it by its decider's act.
        if (mandate is not null)
        {
            using var c = db.Cmd("""
                SELECT kind, at, reason FROM org_mandate_event WHERE mandate_id=$m AND kind <> $claim ORDER BY id DESC LIMIT 1
                """, ("$m", mandate), ("$claim", MandateEventKind.Proposed));
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                var kind = r.GetString(0);
                var at = Sql.Time(r.GetValue(1));
                if (kind is MandateEventKind.Cancelled or MandateEventKind.Rescoped && at >= started)
                    return new OrgFence(attemptId, role!, kind, mandate, at, Sql.S(r.GetValue(2)));
            }
        }

        // A POSITION-WIDE FENCE (parts c–d write it: retire, close, merge, veto): the earliest one at or after the start.
        using (var c = db.Cmd("SELECT at, detail FROM org_event WHERE kind=$kind AND position_id=$p ORDER BY id",
                   ("$kind", OrgEventKind.Fenced), ("$p", role)))
        using (var r = c.ExecuteReader())
        {
            while (r.Read())
            {
                var at = Sql.Time(r.GetValue(0));
                if (at >= started) return new OrgFence(attemptId, role!, OrgEventKind.Fenced, null, at, Sql.S(r.GetValue(1)));
            }
        }
        return null;
    });

    /// <summary>
    /// THE LAUNCHES <paramref name="mandateId"/>'S OWN CANCELLATION OR RE-SCOPE FENCED (<c>U-org-verbs-b</c> item 5): those under it
    /// that had started and had not ended when the transition was written — the ones it took authority from mid-turn, oldest first.
    /// Empty while the mandate is open, or closed any other way. A launch that ended before the transition did nothing under a stale
    /// authority, and is not named.
    /// </summary>
    public IReadOnlyList<string> FencedBy(string mandateId) => db.Read(_ =>
    {
        DateTimeOffset at;
        using (var c = db.Cmd("SELECT kind, at FROM org_mandate_event WHERE mandate_id=$m AND kind <> $claim ORDER BY id DESC LIMIT 1",
                   ("$m", mandateId), ("$claim", MandateEventKind.Proposed)))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read() || r.GetString(0) is not (MandateEventKind.Cancelled or MandateEventKind.Rescoped)) return (IReadOnlyList<string>)[];
            at = Sql.Time(r.GetValue(1));
        }

        var fenced = new List<(DateTimeOffset Started, string Id)>();
        using (var c = db.Cmd("SELECT id, started_at, ended_at, role FROM ai_attempt WHERE mandate_id=$m", ("$m", mandateId)))
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                var started = Sql.Time(r.GetValue(1));
                var ended = Sql.TimeN(r.GetValue(2));
                if (NeverFenced(Sql.S(r.GetValue(3))) || started > at || ended < at) continue;
                fenced.Add((started, r.GetString(0)));
            }
        return (IReadOnlyList<string>)[.. fenced.OrderBy(f => f.Started).Select(f => f.Id)];
    });
}
