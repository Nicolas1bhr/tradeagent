using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHY AN ATTEMPT MAY DO NOTHING MORE (<c>U-org-verbs-b</c>; ORCHESTRATION § 2.3 <i>Fences</i>): the authority-changing transition
/// that ended it — its mandate's own <c>cancelled</c> or <c>rescoped</c> event, or an <c>org_event</c> <c>fenced</c> naming its
/// position — committed after the attempt's own row (commit order, <c>U-org-verbs-d</c>). A DB fact, read on every operation: restart-safe and atomic with the
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

/// <summary>
/// ONE LAUNCH A MANDATE'S CANCELLATION OR RE-SCOPE FENCED, AS THE LEDGER HAS IT (<c>U-org-verbs-b</c>): <paramref name="Ended"/> is
/// whether its row is closed — measured, never inferred from the fence: until it is, its turn is ending, not ended.
/// </summary>
public sealed record FencedLaunch(string Attempt, bool Ended);

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
    /// WHETHER <paramref name="attemptId"/> IS FENCED, and by what (<c>U-org-verbs-b</c> item 1), ORDERED BY COMMIT ORDER — never by
    /// stamps taken before the writes (<c>U-org-verbs-d</c> item 1): a transition stamped before a concurrent admission can commit after
    /// it, and the launch it admitted then ran under the authority the transition took away.
    /// <list type="bullet">
    /// <item><b>Its mandate's own transition</b> — the mandate's latest event (claims aside) is <c>cancelled</c> or <c>rescoped</c>.
    /// <see cref="AiAttemptStore.Begin"/> admits a launch under a mandate only while it is open, in the same write as the launch's row,
    /// and <c>mandate_id</c> is written in that insert and never updated — so a launch row under a closed mandate committed BEFORE the
    /// transition that closed it, whatever either stamp says.</item>
    /// <item><b>A position-wide fence</b> — an <c>org_event</c> <c>fenced</c> naming its position whose app-written mark covers the
    /// launch's ledger row: the fence records, in its own write, the last <c>ai_attempt</c> row committed before it
    /// (<see cref="FencePosition"/>), and that table's rowid only ever rises (nothing deletes, rebuilds or VACUUMs it).</item>
    /// </list>
    /// Null when neither holds, when no such launch is recorded, or when its role is one <see cref="NeverFenced"/> exempts. A fence row
    /// whose mark this build did not write is damage and THROWS, as does a read that fails: the caller refuses the operation (refuse,
    /// never guess).
    /// </summary>
    public OrgFence? FencedSince(string attemptId) => db.Read(_ =>
    {
        string? role, mandate;
        long row;
        using (var c = db.Cmd("SELECT rowid, role, mandate_id FROM ai_attempt WHERE id=$id", ("$id", attemptId)))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read()) return null;
            row = r.GetInt64(0);
            role = Sql.S(r.GetValue(1));
            mandate = Sql.S(r.GetValue(2));
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
                if (kind is MandateEventKind.Cancelled or MandateEventKind.Rescoped)
                    return new OrgFence(attemptId, role!, kind, mandate, Sql.Time(r.GetValue(1)), Sql.S(r.GetValue(2)));
            }
        }

        // A POSITION-WIDE FENCE (part d writes it: retire, close, merge, veto): the earliest one committed after the launch's row.
        using (var c = db.Cmd("SELECT id, at, detail FROM org_event WHERE kind=$kind AND position_id=$p ORDER BY id",
                   ("$kind", OrgEventKind.Fenced), ("$p", role)))
        using (var r = c.ExecuteReader())
        {
            while (r.Read())
            {
                var (why, through) = FenceMark(r.GetInt64(0), r.GetString(2));
                if (row <= through) return new OrgFence(attemptId, role!, OrgEventKind.Fenced, null, Sql.Time(r.GetValue(1)), why);
            }
        }
        return null;
    });

    /// <summary>
    /// THE LAUNCHES <paramref name="mandateId"/>'S OWN CANCELLATION OR RE-SCOPE FENCED (<c>U-org-verbs-b</c> item 5): those under it
    /// that had not ended when the transition was written — the ones it took authority from mid-turn, oldest first. Every launch row
    /// under the mandate committed before the transition (<see cref="FencedSince"/>: admission refuses a closed mandate in the row's own
    /// write), so none is left out for a start stamped after the transition's (<c>U-org-verbs-d</c> item 1: commit order, never stamps).
    /// Whether one had already ended is its own row's end stamp against the transition's — the end write carries no commit mark.
    /// Empty while the mandate is open, or closed any other way. A launch that ended before the transition did nothing under a stale
    /// authority, and is not named. Each says whether its row is closed yet (<see cref="FencedLaunch.Ended"/>).
    /// </summary>
    public IReadOnlyList<FencedLaunch> FencedBy(string mandateId) => db.Read(_ =>
    {
        DateTimeOffset at;
        using (var c = db.Cmd("SELECT kind, at FROM org_mandate_event WHERE mandate_id=$m AND kind <> $claim ORDER BY id DESC LIMIT 1",
                   ("$m", mandateId), ("$claim", MandateEventKind.Proposed)))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read() || r.GetString(0) is not (MandateEventKind.Cancelled or MandateEventKind.Rescoped)) return (IReadOnlyList<FencedLaunch>)[];
            at = Sql.Time(r.GetValue(1));
        }

        var fenced = new List<(long Row, FencedLaunch Launch)>();
        using (var c = db.Cmd("SELECT id, rowid, ended_at, role FROM ai_attempt WHERE mandate_id=$m", ("$m", mandateId)))
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                var row = r.GetInt64(1);
                var ended = Sql.TimeN(r.GetValue(2));
                if (NeverFenced(Sql.S(r.GetValue(3))) || ended < at) continue;
                fenced.Add((row, new FencedLaunch(r.GetString(0), ended is not null)));
            }
        return (IReadOnlyList<FencedLaunch>)[.. fenced.OrderBy(f => f.Row).Select(f => f.Launch)];
    });

    /// <summary>The words an app-written position-wide fence ends with: the last launch row committed before it.</summary>
    static readonly System.Text.RegularExpressions.Regex Mark = new(@"^(?<why>.*) \(launches recorded through #(?<through>[0-9]+)\)$",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A POSITION-WIDE FENCE'S DETAIL AS THE APP WRITES IT (<c>U-org-verbs-d</c> item 1): its reason, then the last <c>ai_attempt</c> row
    /// committed before it — the commit-order mark <see cref="FencedSince"/> reads.
    /// </summary>
    internal static string FenceDetail(string why, long through) =>
        $"{why} (launches recorded through #{through.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>A fence row's reason and mark; a detail this build did not write is damage, refused, never read on a guess.</summary>
    static (string Why, long Through) FenceMark(long eventId, string detail) =>
        Mark.Match(detail) is { Success: true } m
        && long.TryParse(m.Groups["through"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var through)
            ? (m.Groups["why"].Value, through)
            : throw Damaged($"fence {eventId.ToString(CultureInfo.InvariantCulture)} carries no commit mark this build wrote, so which launches it fenced cannot be read");

    /// <summary>
    /// ONE POSITION-WIDE FENCE, inside the caller's write (<c>U-org-verbs-d</c>): an <c>org_event</c> <c>fenced</c> naming
    /// <paramref name="positionId"/>, its detail the reason and the last <c>ai_attempt</c> row committed before it — read in this same
    /// write, so every launch committed before the fence is fenced by it and none committed after. Answers the event's id.
    /// </summary>
    long FencePosition(string unitId, string positionId, string decider, string why, DateTimeOffset now) => db.Write(_ =>
    {
        using var c = db.Cmd("SELECT COALESCE(MAX(rowid), 0) FROM ai_attempt");
        var through = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        return Event(now, unitId, positionId, OrgEventKind.Fenced, decider, FenceDetail(why, through));
    });
}
