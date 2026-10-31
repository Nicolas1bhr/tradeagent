using System.Globalization;
using System.Text.RegularExpressions;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHY AN OCCUPANT WAS RETIRED (<c>U-worker-replace</c>; ORGANISATION § 8, "Triggers: an app rule (a stall, repeated failed checks,
/// calibration below the base rate, an audit finding) or the owner"): strings, as the chart's vocabulary is. The four rules are the app's,
/// each read from its own measurements; <see cref="Owner"/> is his press. No other reason exists, and no agent can name one.
/// </summary>
public static class OccupantRule
{
    /// <summary>The occupant's last launches on mandates it holds moved none of them, and it still holds one.</summary>
    public const string Stall = "stall";

    /// <summary>The occupant's last deliveries each failed at least one of TradeAgent's checks.</summary>
    public const string FailedChecks = "failed-checks";

    /// <summary>The occupant's own resolved, scored forecasts scored below the base rate.</summary>
    public const string Calibration = "calibration";

    /// <summary>The public audit line found a severe defect in a delivery the occupant sealed.</summary>
    public const string Audit = "audit-finding";

    /// <summary>The owner's two-press "Replace the occupant", in-process only.</summary>
    public const string Owner = "owner";

    public static readonly string[] All = [Stall, FailedChecks, Calibration, Audit, Owner];
}

/// <summary>
/// THE RETIREMENT THRESHOLDS — POLICY DATA the host hands the sweep, never constants inside it (<c>U-worker-replace</c>; the review sweep's
/// precedent, <see cref="ReviewThresholds"/>): <see cref="StallLaunches"/> consecutive resolved launches on held mandates that moved none;
/// <see cref="FailedDeliveries"/> consecutive deliveries each failing a check; a mean skill below <see cref="SkillBelow"/> over at least
/// <see cref="MinScored"/> resolved, scored forecasts — every count read over the CURRENT occupant's record only.
/// </summary>
public sealed record OccupantThresholds(int StallLaunches, int FailedDeliveries, int MinScored, double SkillBelow)
{
    /// <summary>Six launches; three deliveries; ten scored forecasts, worse than the base rate (the review sweep's calibration bar).</summary>
    public static OccupantThresholds Default { get; } = new(6, 3, 10, 0.0);
}

/// <summary>
/// ONE AUDIT FINDING AS THE OCCUPANT RULE READS IT: the finding's id, the delivery it is about, its severity and when it was found — app
/// measurements only, mapped by the host from the audit line's own file, which this namespace never opens.
/// </summary>
public sealed record OccupantFinding(string Id, string DeliveryId, string Severity, DateTimeOffset At);

/// <summary>
/// WHO SITS IN A POSITION NOW (<c>U-worker-replace</c>): its <see cref="Ordinal"/> — 1 for the occupant the position was created with, one
/// more for each <c>occupant-replaced</c> event — and, for a successor, that event (<see cref="EventId"/>, <see cref="Since"/>), who decided
/// it and on which rule, and <see cref="Through"/>: the last <c>ai_attempt</c> row committed before it, so the launches of this occupant
/// are exactly the position's rows after it (0 for the first occupant: every launch is its own).
/// </summary>
public sealed record Occupancy(string Position, int Ordinal, long? EventId, DateTimeOffset? Since, long Through, string? Decider, string? Rule)
{
    /// <summary>Whether the position's first occupant still sits.</summary>
    public bool First => EventId is null;
}

/// <summary>What one replacement wrote: the position, the occupant retired and the one seated, the event, the fence, the wake, who and why.</summary>
public sealed record OccupantReplaced(
    string Position, int Retired, int Seated, long EventId, long FenceEventId, string WakeId, string Decider, string Rule, string Measurement);

/// <summary>What one occupant sweep replaced, and each reading it could not judge, in words (never guessed).</summary>
public sealed record OccupantSweepResult(IReadOnlyList<OccupantReplaced> Replaced, IReadOnlyList<string> Unjudged);

/// <summary>
/// PRESS ONE OF THE OWNER'S "REPLACE THE OCCUPANT": the position, the occupant it would retire, the sentence the armed press shows, or why
/// it cannot be pressed (<see cref="Refusal"/>). Press two is handed this and refuses when the press would now do anything else.
/// </summary>
public sealed record OccupantPreview(string? Position, int? Retires, string Words, string? Refusal)
{
    /// <summary>What moved between this preview and <paramref name="now"/>, in words, or null when press two would do what this showed.</summary>
    public string? WhatMoved(OccupantPreview now) =>
        now.Position != Position ? $"the position is now {now.Position ?? "none"}, not {Position ?? "none"}"
        : now.Retires != Retires ? $"{Position}'s occupant is now number {now.Retires}, not {Retires}: it was replaced since you looked"
        : null;
}

/// <summary>
/// A FRESH OCCUPANT TAKES OVER A SEAT FROM THE POSITION'S MEMORY (<c>U-worker-replace</c>; VISION § 1 test 1, "replace every occupant … the
/// knowledge lives in the institution … and not in transcripts"; ORGANISATION §§ 6, 8; PRINCIPLES "Useful autonomy needs code, tools and
/// durable memory"). An app rule or the owner retires the OCCUPANT — the sessions a seat runs — and never the position: its id, unit, seat,
/// home, plan, journal, mandates, decisions, ledger, spend, reservations, unresolved launches and its unit's trial and verdict counts all
/// stay exactly as they were. The replacement writes three rows and nothing else: one <c>occupant-replaced</c> event, one position-wide
/// <c>fenced</c> event (the predecessor's running turn ends and none of its launches acts again) and one <see cref="MissionEventKind.Occupant"/>
/// wake. The next launch starts a fresh session (the loop's <c>replaced</c> rotation) and its packet opens with the takeover block, built
/// from the position's records (<see cref="PositionPacket"/>), never the predecessor's conversation.
///
/// <para><b>Authority (AUTH-001, ORG-001).</b> Only two writers: the app's sweep, on its own measurements, and the owner's in-process press.
/// No pipe op, door verb, <c>trade</c> verb, worker tool or board act reaches either (a guard reads their sources); a manager that wants its
/// subordinate replaced proposes it to the owner (R17 #14). An agent can neither retire itself nor another occupant.</para>
/// </summary>
public sealed partial class OrgStore
{
    static readonly Regex RuleOf = new(@"(?:^| )rule=(?<rule>[a-z-]+)(?: |$)", RegexOptions.CultureInvariant);

    /// <summary>
    /// WHO SITS IN <paramref name="position"/> NOW, from its <c>occupant-replaced</c> events: the first occupant (ordinal 1, through 0) where
    /// it has none. A detail this build did not write is damage, refused, never read on a guess. Reads only.
    /// </summary>
    public Occupancy OccupancyOf(string position) => db.Read(_ =>
    {
        int count;
        using (var n = db.Cmd("SELECT COUNT(*) FROM org_event WHERE position_id = $p AND kind = $k",
                   ("$p", position), ("$k", OrgEventKind.OccupantReplaced)))
            count = Convert.ToInt32(n.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count == 0) return new Occupancy(position, 1, null, null, 0, null, null);
        using var c = db.Cmd("SELECT id, at, decider, detail FROM org_event WHERE position_id = $p AND kind = $k ORDER BY id DESC LIMIT 1",
            ("$p", position), ("$k", OrgEventKind.OccupantReplaced));
        using var r = c.ExecuteReader();
        r.Read();
        var id = r.GetInt64(0);
        var detail = r.GetString(3);
        var (why, through) = FenceMark(id, detail);
        var rule = RuleOf.Match(why) is { Success: true } m ? m.Groups["rule"].Value : null;
        if (rule is null || Array.IndexOf(OccupantRule.All, rule) < 0)
            throw Damaged($"occupant event {id.ToString(CultureInfo.InvariantCulture)} names no rule this build wrote, so who sits in {position} cannot be read");
        return new Occupancy(position, count + 1, id, Sql.Time(r.GetValue(1)), through, r.GetString(2), rule);
    });

    /// <summary>
    /// THE OCCUPANT WHOSE PACKET STILL OWES ITS TAKEOVER BLOCK, or null: a successor (never a position's first occupant) none of whose launches
    /// has yet ENDED — so a first launch that was lost or refused leaves the block standing for the next, and a restart changes nothing.
    /// </summary>
    public Occupancy? TakeoverOwed(string position) => db.Read(_ =>
    {
        var sits = OccupancyOf(position);
        if (sits.First) return null;
        using var c = db.Cmd("SELECT EXISTS (SELECT 1 FROM ai_attempt WHERE role = $p AND rowid > $through AND state = 'ENDED')",
            ("$p", position), ("$through", sits.Through));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) == 0 ? sits : null;
    });

    /// <summary>
    /// The launches of <paramref name="position"/>'s PREDECESSORS still unresolved — recorded before the current occupant's mark and neither
    /// ended nor lost: fenced, so they act no more, and their reservations stand until each resolves.
    /// </summary>
    public int UnresolvedBefore(Occupancy sits) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM ai_attempt WHERE role = $p AND rowid <= $through AND state = 'LAUNCHED'",
            ("$p", sits.Position), ("$through", sits.Through));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>PRESS ONE: what "Replace the occupant" of <paramref name="position"/> would do now, or why it cannot. Reads only.</summary>
    public OccupantPreview PreviewReplaceOccupant(string position) => db.Read(_ => ReplaceNow(position));

    /// <summary>
    /// PRESS TWO, THE OWNER'S, handed what press one showed (<paramref name="asShown"/>, required): in ONE write the press is decided again — a
    /// refusal now, or a press that would now retire another occupant than <paramref name="asShown"/> said, is refused in words with nothing
    /// written — then the replacement (<see cref="Replace"/>), decided by <see cref="OrgDecider.Owner"/> on <see cref="OccupantRule.Owner"/>.
    /// IN-PROCESS ONLY (AUTH-001): no pipe op, door verb, <c>trade</c> verb, worker tool or board act reaches it.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public OccupantReplaced ReplaceOccupant(OccupantPreview asShown, DateTimeOffset now) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(asShown);
        if (asShown.Position is not { } position) throw Refused(asShown.Refusal ?? "no position was shown, so no occupant is replaced.");
        var press = ReplaceNow(position);
        if (press.Refusal is { } refused) throw Refused(refused);
        if (asShown.WhatMoved(press) is { } moved) throw Refused($"No occupant was replaced: {moved}.");
        return Replace(position, press.Retires!.Value, OrgDecider.Owner, OccupantRule.Owner, "press=owner", now)
               ?? throw Refused($"{position}'s occupant changed inside the press, so no occupant was replaced.");
    });

    /// <summary>The owner's press as it stands now, read inside the caller's read or write.</summary>
    OccupantPreview ReplaceNow(string position)
    {
        OccupantPreview Refuse(string why) => new(null, null, why, why);
        if (CouncilRoles.IsKnown(position))
            return Refuse($"'{position}' is one of the council's two seats: its conversation is restarted by \"New conversation\" on the Chat "
                          + "page, not by replacing an occupant.");
        if (Board.BoardSeats.IsBoardId(position) || Position(position) is not { } row)
            return Refuse($"The chart holds no position '{position}', so there is no occupant to replace.");
        if (row.Status != OrgPositionStatus.Active)
            return Refuse($"{position} is {row.Status}: only an active position has an occupant at work to replace.");
        var sits = OccupancyOf(position);
        if (new MissionEventStore(db).HasUnconsumed(MissionEventKind.Occupant, position))
            return Refuse($"{position}'s occupant {sits.Ordinal.ToString(CultureInfo.InvariantCulture)} has not taken its first turn yet: it is "
                          + "replaced again only once it has worked.");
        var next = (sits.Ordinal + 1).ToString(CultureInfo.InvariantCulture);
        return new OccupantPreview(position, sits.Ordinal,
            $"Replace {position}'s occupant {sits.Ordinal.ToString(CultureInfo.InvariantCulture)} with occupant {next}: its running turn ends, "
            + "and the next one starts in a fresh session from the position's plan, journal, mandates, decisions and ledger — never the old "
            + "conversation. Nothing else changes: the seat, the open mandates, the spend, the reservations and the trial counts carry over. "
            + "One turn of the new occupant is woken now, on its seat.",
            null);
    }

    /// <summary>
    /// THE APP'S OCCUPANT SWEEP (<c>U-worker-replace</c>; ORGANISATION § 8), after the review sweep on the organisation's tick. For every
    /// ACTIVE position beyond the legacy pair, the four rules over the CURRENT occupant's record only — its launches after the last
    /// replacement's mark, its forecasts and deliveries written by those launches (or, unlaunched, after it) — so a successor is never
    /// retired for its predecessor's record:
    /// <list type="bullet">
    /// <item><see cref="OccupantRule.Audit"/> — a severe audit finding on a delivery the occupant sealed (<paramref name="findings"/>: null
    /// where the host relays none, said in <see cref="OccupantSweepResult.Unjudged"/>).</item>
    /// <item><see cref="OccupantRule.FailedChecks"/> — its last <see cref="OccupantThresholds.FailedDeliveries"/> deliveries each failed a
    /// check of TradeAgent's.</item>
    /// <item><see cref="OccupantRule.Calibration"/> — at least <see cref="OccupantThresholds.MinScored"/> of its forecasts resolved and scored,
    /// with a mean skill against the base rate below <see cref="OccupantThresholds.SkillBelow"/> (<see cref="Scorecards"/>, at read).</item>
    /// <item><see cref="OccupantRule.Stall"/> — it holds an open mandate, and its last <see cref="OccupantThresholds.StallLaunches"/> resolved
    /// launches all served a mandate and none wrote an event on any mandate.</item>
    /// </list>
    /// The first rule met replaces the occupant (<see cref="Replace"/>, decided by <see cref="OrgDecider.App"/>); one replacement per position
    /// per sweep, and a second pass — a restart included — finds the new occupant's record empty and replaces nothing.
    /// </summary>
    public OccupantSweepResult OccupantSweep(DateTimeOffset now, OccupantThresholds thresholds, Func<IReadOnlyList<OccupantFinding>>? findings)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        var replaced = new List<OccupantReplaced>();
        var unjudged = new List<string>();
        IReadOnlyList<OccupantFinding>? found = null;
        if (findings is null) unjudged.Add("this host relays no audit finding, so no occupant is judged on one.");
        else
        {
            try { found = findings(); }
            catch (Exception ex) when (ex is TradeAgentException or InvalidOperationException or IOException)
            {
                unjudged.Add($"the audit line's findings could not be read ({ex.Message.ReplaceLineEndings(" ")}), so no occupant is judged on one.");
            }
        }
        var cards = new Scorecards(db);
        foreach (var position in Positions().Where(p => p.Status == OrgPositionStatus.Active && !CouncilRoles.IsKnown(p.Id)))
        {
            var sits = OccupancyOf(position.Id);
            if (Judge(position.Id, sits, thresholds, found, cards) is not { } fired) continue;
            if (Replace(position.Id, sits.Ordinal, OrgDecider.App, fired.Rule, fired.Measurement, now) is { } done) replaced.Add(done);
        }
        return new OccupantSweepResult(replaced, unjudged);
    }

    /// <summary>The first rule <paramref name="sits"/>' own record meets, with the measurement it was met on, or null.</summary>
    (string Rule, string Measurement)? Judge(string position, Occupancy sits, OccupantThresholds t, IReadOnlyList<OccupantFinding>? findings,
        Scorecards cards) => db.Read(_ =>
    {
        var since = sits.Since is { } s ? Sql.T(s) : "";
        (string, object?)[] mine = [("$p", position), ("$through", sits.Through), ("$since", since)];

        // AUDIT: a severe finding on a delivery of this occupant's, the finding's id and the delivery's the measurement.
        if (findings is { Count: > 0 })
        {
            var deliveries = DeliveryIds(position, mine);
            if (findings.Where(f => f.Severity == Audit.AuditSeverity.Severe && deliveries.Contains(f.DeliveryId))
                    .OrderBy(f => f.At).ThenBy(f => f.Id, StringComparer.Ordinal).FirstOrDefault() is { } finding)
                return ((string, string)?)(OccupantRule.Audit, $"finding={finding.Id} delivery={finding.DeliveryId} severity={finding.Severity}");
        }

        // FAILED CHECKS: its last N deliveries, each with a failed check.
        if (t.FailedDeliveries > 0)
        {
            using var c = db.Cmd($"""
                SELECT d.id, EXISTS (SELECT 1 FROM org_check k WHERE k.delivery_id = d.id AND k.passed = 0)
                  FROM org_delivery d JOIN org_mandate m ON m.id = d.mandate_id
                 WHERE m.holder = $p AND {OfOccupant("d")}
                 ORDER BY d.at DESC, d.rowid DESC LIMIT $n
                """, [.. mine, ("$n", t.FailedDeliveries)]);
            using var r = c.ExecuteReader();
            var last = new List<(string Id, bool Failed)>();
            while (r.Read()) last.Add((r.GetString(0), r.GetInt64(1) != 0));
            if (last.Count == t.FailedDeliveries && last.All(d => d.Failed))
                return (OccupantRule.FailedChecks,
                    $"deliveries={last.Count.ToString(CultureInfo.InvariantCulture)} each_failed_a_check last={last[0].Id}");
        }

        // CALIBRATION: its own scored forecasts, against the base rate, computed at read.
        if (t.MinScored > 0)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            using (var c = db.Cmd($"SELECT f.id FROM org_forecast f WHERE f.forecaster = $p AND {OfOccupant("f")}", mine))
            using (var r = c.ExecuteReader())
                while (r.Read()) ids.Add(r.GetString(0));
            if (ids.Count >= t.MinScored)
            {
                var scores = cards.Of(position).Scores.Where(x => ids.Contains(x.ForecastId)).ToList();
                if (scores.Count >= t.MinScored && scores.Average(x => x.Skill) is var skill && skill < t.SkillBelow)
                    return (OccupantRule.Calibration,
                        $"scored={scores.Count.ToString(CultureInfo.InvariantCulture)} skill={skill.ToString("0.####", CultureInfo.InvariantCulture)} "
                        + $"below={t.SkillBelow.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        // STALL: an open mandate held, and its last N resolved launches all served a mandate and moved none.
        if (t.StallLaunches > 0 && MandatesOwedBy(position).Count is var owed and > 0)
        {
            using var c = db.Cmd("""
                SELECT a.id, a.mandate_id,
                       EXISTS (SELECT 1 FROM org_mandate_event e WHERE e.attempt_id = a.id)
                       OR EXISTS (SELECT 1 FROM org_delivery d WHERE d.attempt_id = a.id)
                  FROM ai_attempt a
                 WHERE a.role = $p AND a.rowid > $through AND a.state IN ('ENDED', 'LOST')
                 ORDER BY a.rowid DESC LIMIT $n
                """, [.. mine, ("$n", t.StallLaunches)]);
            using var r = c.ExecuteReader();
            var last = new List<(string Id, bool Served, bool Moved)>();
            while (r.Read()) last.Add((r.GetString(0), !r.IsDBNull(1), r.GetInt64(2) != 0));
            if (last.Count == t.StallLaunches && last.All(a => a.Served && !a.Moved))
                return (OccupantRule.Stall,
                    $"launches={last.Count.ToString(CultureInfo.InvariantCulture)} moved_no_mandate open_held={owed.ToString(CultureInfo.InvariantCulture)} "
                    + $"last={last[0].Id}");
        }
        return null;
    });

    /// <summary>
    /// A ROW OF THE CURRENT OCCUPANT'S, in SQL over alias <paramref name="a"/> with an <c>attempt_id</c> and an <c>at</c>: written by one of its
    /// launches (an <c>ai_attempt</c> row after the mark), or — written by no launch — after the replacement (any time, for the first occupant).
    /// </summary>
    static string OfOccupant(string a) =>
        $"(({a}.attempt_id IS NOT NULL AND EXISTS (SELECT 1 FROM ai_attempt oa WHERE oa.id = {a}.attempt_id AND oa.role = $p AND oa.rowid > $through)) "
        + $"OR ({a}.attempt_id IS NULL AND {a}.at > $since))";

    /// <summary>The deliveries the current occupant sealed on mandates the position holds.</summary>
    HashSet<string> DeliveryIds(string position, (string, object?)[] mine)
    {
        using var c = db.Cmd($"""
            SELECT d.id FROM org_delivery d JOIN org_mandate m ON m.id = d.mandate_id WHERE m.holder = $p AND {OfOccupant("d")}
            """, mine);
        using var r = c.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    /// <summary>
    /// ONE REPLACEMENT, IN ONE WRITE, or null when <paramref name="position"/>'s occupant is no longer <paramref name="retires"/> (another
    /// writer replaced it first): the <c>occupant-replaced</c> event — its detail the app's, ending with the commit mark of the last launch
    /// recorded before it — one position-wide <c>fenced</c> event, so the predecessor's running turn ends and none of its launches acts again,
    /// and one <see cref="MissionEventKind.Occupant"/> wake to the position, due now, its payload the event's id. NOTHING ELSE IS WRITTEN: no
    /// mandate is cancelled, moved or re-issued; no row of <c>ai_attempt</c>, its funding or its inputs; no campaign trial or verdict; no
    /// deployment operation; no seat, status or share. Restructuring resets nothing.
    /// </summary>
    OccupantReplaced? Replace(string position, int retires, string decider, string rule, string measurement, DateTimeOffset now) => db.Write(_ =>
    {
        if (OccupancyOf(position).Ordinal != retires) return null;
        if (Position(position) is not { Status: OrgPositionStatus.Active } row)
            throw Refused($"{position} is no active position, so no occupant is replaced.");
        var seated = retires + 1;
        using var c = db.Cmd("SELECT COALESCE(MAX(rowid), 0) FROM ai_attempt");
        var through = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        var why = $"position={position} occupant={seated.ToString(CultureInfo.InvariantCulture)} retired={retires.ToString(CultureInfo.InvariantCulture)} "
                  + $"rule={rule} {measurement}";
        var eventId = Event(now, row.UnitId, position, OrgEventKind.OccupantReplaced, decider, FenceDetail(why, through));
        var fence = FencePosition(row.UnitId, position, decider,
            $"occupant {retires.ToString(CultureInfo.InvariantCulture)} of {position} retired ({rule}), decided by {decider}", now);
        var wake = MissionEventIds.Occupant(position, eventId);
        if (!new MissionEventStore(db).Raise(wake, MissionEventKind.Occupant, now, payload: eventId.ToString(CultureInfo.InvariantCulture),
                role: position))
            throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT, $"the occupant wake {wake} appeared inside its own write");
        return new OccupantReplaced(position, retires, seated, eventId, fence, wake, decider, rule, measurement);
    });
}
