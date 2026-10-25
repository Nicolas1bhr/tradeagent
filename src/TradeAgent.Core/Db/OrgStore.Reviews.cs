using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE CUT RULES THE APP REVIEWS (<c>U-org-scorecards-b</c> b2; ORGANISATION § 8, "cut rules — app review events, not agent appeals"):
/// strings, as the chart's vocabulary is. Each raises a REVIEW EVENT and nothing else — no share, status or seat moves on one (ORG-001;
/// any retirement is a board act, R24).
/// </summary>
public static class ReviewRule
{
    /// <summary>A unit spent half its day envelope with nothing accepted in its subtree that day (§ 8): its head — the parent's — reviews it.</summary>
    public const string HalfEnvelope = "half-envelope";

    /// <summary>A position's scored forecasts under one manager version scored below the base rate (§ 8, "calibration below the base rate"): its decider reviews it.</summary>
    public const string Calibration = "calibration";

    public static readonly string[] All = [HalfEnvelope, Calibration];
}

/// <summary>
/// THE REVIEW THRESHOLDS — POLICY DATA the host hands the sweep, never constants inside it (<c>U-org-scorecards-b</c> b2; ORGANISATION
/// § 8's defaults, to calibrate): <see cref="EnvelopeSpent"/> of a unit's day envelope spent with nothing accepted; a mean skill below
/// <see cref="SkillBelow"/> over at least <see cref="MinScored"/> resolved, scored forecasts in one manager version.
/// </summary>
public sealed record ReviewThresholds(decimal EnvelopeSpent, int MinScored, double SkillBelow)
{
    /// <summary>Half the envelope; ten scored forecasts; worse than the base rate.</summary>
    public static ReviewThresholds Default { get; } = new(0.5m, 10, 0.0);
}

/// <summary>What one review sweep raised, and each reading it could not judge, in words (an unknown cost is said, never read as zero).</summary>
public sealed record ReviewSweepResult(int Raised, IReadOnlyList<string> Unjudged);

/// <summary>
/// A REVIEW ANSWER AS THE REVIEWER ASKS IT (<c>U-org-scorecards-b</c> b3): the review event it answers, its choice, rationale and options
/// (claims), the app records it rests on, and its forecasts (at least one; a subject left empty is the reviewed unit).
/// </summary>
public sealed record ReviewAnswerRequest(
    string Caller, string? Attempt, long EventId, string Choice, string Rationale, IReadOnlyList<string> Options,
    IReadOnlyList<OrgRef> Refs, IReadOnlyList<ForecastClaim> Forecasts);

/// <summary>What a review answer wrote: its <c>review-answer</c> decision (the event its first ref), the forecasts, and how many untaken wakes it closed.</summary>
public sealed record ReviewAnswered(long EventId, DecisionRow Decision, IReadOnlyList<ForecastRow> Forecasts, int WakesClosed);

public sealed partial class OrgStore
{
    /// <summary>
    /// THE APP'S REVIEW SWEEP (<c>U-org-scorecards-b</c> b2; ORGANISATION § 8), after the forecast resolver on the organisation's tick. Two
    /// rules, read from the app's own measurements (EVID-001), with <paramref name="thresholds"/> as policy:
    /// <list type="bullet">
    /// <item><see cref="ReviewRule.HalfEnvelope"/> — an active unit whose spend today, in a ceiling where it holds an envelope of its own,
    /// reached the threshold's share of it (<paramref name="dayOf"/>: the host meter's reading of that unit, as admission resolves it), with
    /// no mandate accepted today in its subtree — reviewed by the nearest active head ABOVE it (the head whose verbs reach its share). A
    /// spend under the threshold with a turn of unknown cost is not judged and is said (<see cref="ReviewSweepResult.Unjudged"/>).</item>
    /// <item><see cref="ReviewRule.Calibration"/> — a position whose resolved, scored forecasts under ONE manager version number at least the
    /// threshold and whose mean skill against the base rate is below it (<see cref="Scorecards.Of"/>, computed at read) — reviewed by its
    /// decider: the head of its unit, or for a head, the nearest active head above. Once per such count of forecasts.</item>
    /// </list>
    /// Each is ONE <c>org_event</c> of kind <see cref="OrgEventKind.Review"/> decided by <see cref="OrgDecider.App"/>, its detail the app's,
    /// and ONE <see cref="MissionEventKind.CutReview"/> wake to the reviewer, its payload the event's id, in one write; the wake's id
    /// (<see cref="MissionEventIds.CutReview"/>) is the rule, the subject and the period, so a second pass — a restart included — raises
    /// nothing. A unit or position with no active head above it (the organisation's own head is the owner's seat today, and no org rule
    /// wakes the owner) is not reviewed. NOTHING ELSE IS WRITTEN: no share, status or seat moves on a review (ORG-001; R24 — any
    /// structural act after it is the reviewer's own verb or a board act).
    /// </summary>
    public ReviewSweepResult ReviewSweep(DateTimeOffset now, ReviewThresholds thresholds, Func<string, IReadOnlyList<UnitDay>?>? dayOf)
    {
        var raised = 0;
        var unjudged = new List<string>();
        var (from, to) = OwnerDay.Window(now);
        var day = from.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var units = Units();

        if (dayOf is null)
            unjudged.Add("this host meters no AI envelope, so no unit's half-envelope review can be judged.");
        else
            foreach (var unit in units.Where(u => u.Status == OrgUnitStatus.Active && !u.IsRoot))
            {
                if (HeadAbove(unit.Id, null) is not { } reviewer) continue;
                IReadOnlyList<UnitDay>? reading;
                try { reading = dayOf(unit.Id); }
                catch (Exception ex) when (ex is TradeAgentException or InvalidOperationException) { reading = null; }
                if (reading is null)
                {
                    unjudged.Add($"{OrgEnvelopes.NameOf(unit.Id)}'s day could not be read from the meter, so its half-envelope review is not judged.");
                    continue;
                }
                UnitDay? crossed = null;
                foreach (var d in reading.Where(d => d.UnitId == unit.Id && d.Envelope is > 0m))
                {
                    if (d.Spent >= d.Envelope!.Value * thresholds.EnvelopeSpent) { crossed = d; break; }
                    if (d.Unpriced > 0)
                        unjudged.Add($"{OrgEnvelopes.NameOf(unit.Id)} spent {Sql.D(d.Spent)} of its {Sql.D(d.Envelope.Value)} today "
                                     + $"({BillingClasses.Words(d.Ceiling)}) with {d.Unpriced.ToString(CultureInfo.InvariantCulture)} turn(s) of "
                                     + "unknown cost, so whether it passed the review's share is not known: not judged, never guessed.");
                }
                if (crossed is not { } c || AcceptedInSubtree(unit.Id, from, to)) continue;
                raised += RaiseReview(ReviewRule.HalfEnvelope, unit.Id, day, unit.Id, null, reviewer, now,
                    $"rule={ReviewRule.HalfEnvelope} unit={unit.Id} period={day} ceiling={c.Ceiling} spent={Sql.D(c.Spent)} "
                    + $"envelope={Sql.D(c.Envelope!.Value)} share={Sql.D(thresholds.EnvelopeSpent)} accepted=0");
            }

        var cards = new Scorecards(db);
        foreach (var positionId in Forecasters(thresholds.MinScored))
        {
            if (Position(positionId) is not { } position || position.Status == OrgPositionStatus.Ended) continue;
            var unit = units.FirstOrDefault(u => u.Id == position.UnitId);
            if (unit is null) continue;
            var decider = unit.HeadPositionId is { } h && h != position.Id && Position(h) is { Status: OrgPositionStatus.Active }
                ? h : HeadAbove(unit.Id, position.Id);
            if (decider is null) continue;
            foreach (var v in cards.Of(position.Id).Versions)
            {
                if (v.Scored < thresholds.MinScored || v.Skill is not { } skill || skill >= thresholds.SkillBelow) continue;
                var bucket = v.Scored / thresholds.MinScored * thresholds.MinScored;
                var period = $"{VersionKey(v.Name)}@{bucket.ToString(CultureInfo.InvariantCulture)}";
                raised += RaiseReview(ReviewRule.Calibration, position.Id, period, unit.Id, position.Id, decider, now,
                    $"rule={ReviewRule.Calibration} position={position.Id} period={period} scored={v.Scored.ToString(CultureInfo.InvariantCulture)} "
                    + $"skill={skill.ToString("0.####", CultureInfo.InvariantCulture)} below={thresholds.SkillBelow.ToString(CultureInfo.InvariantCulture)} "
                    + $"version={v.Name}");
            }
        }
        return new ReviewSweepResult(raised, unjudged);
    }

    /// <summary>
    /// ANSWERS A REVIEW WITH A RECORD (<c>U-org-scorecards-b</c> b3; ORGANISATION § 8, "a review event its head answers with a record"):
    /// the reviewer the app named — the role its <c>cut-review</c> wake was raised to — writes a <c>review-answer</c> decision, MANDATORY
    /// (so no <c>decision-record</c> writes one), whose FIRST ref is the review event (<see cref="OrgRefKind.Event"/>), with at least one
    /// forecast (a subject left empty is the reviewed unit), and its untaken wakes for that review are closed — in ONE write. Once per
    /// review. Refused in words, nothing written: not an active position; no such review; another reviewer's; answered already; no
    /// forecast. The answer is a record and nothing else: it moves no share, status or seat — a structural act after a review is the
    /// reviewer's own verb, a board act or the owner's veto (R24).
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public ReviewAnswered AnswerReview(ReviewAnswerRequest r, DateTimeOffset now) => db.Write(_ =>
    {
        if (Board.BoardSeats.IsBoardId(r.Caller)) throw Forbidden(Board.BoardSeats.NotAPosition(r.Caller));
        if (IsAppPrincipal(r.Caller) || Position(r.Caller) is not { Status: OrgPositionStatus.Active })
            throw Forbidden($"'{r.Caller}' is no active position: a review is answered by the position the app woke for it.");
        var id = r.EventId.ToString(CultureInfo.InvariantCulture);
        string unitId;
        using (var e = db.Cmd("SELECT unit_id FROM org_event WHERE id = $id AND kind = $k", ("$id", r.EventId), ("$k", OrgEventKind.Review)))
            unitId = e.ExecuteScalar() as string
                     ?? throw Refused($"event {id} is no review of the organisation's: a review answer names the 'review' event its wake carried.");
        string? reviewer;
        using (var w = db.Cmd("SELECT role FROM mission_event WHERE kind = $k AND payload = $id ORDER BY created_at, id LIMIT 1",
                   ("$k", MissionEventKind.CutReview), ("$id", id)))
            reviewer = w.ExecuteScalar() as string;
        if (reviewer != r.Caller)
            throw Forbidden($"review {id} is {reviewer ?? "nobody's"} to answer, not yours: the app woke its reviewer for it, and only that "
                            + "position answers it.");
        var answered = $"[{{\"kind\":\"{OrgRefKind.Event}\",\"id\":\"{id}\"}}%";
        using (var a = db.Cmd("SELECT id FROM org_decision WHERE kind = $k AND refs LIKE $first",
                   ("$k", OrgDecisionKind.ReviewAnswer), ("$first", answered)))
            if (a.ExecuteScalar() is string before)
                throw Refused($"review {id} is answered already, by decision {before}: a review is answered once — a later act is its own record.");
        if (r.Forecasts.Count is 0 or > DecisionLimits.MaxForecasts)
            throw Refused($"a review answer carries 1 to {DecisionLimits.MaxForecasts.ToString(CultureInfo.InvariantCulture)} forecasts — such "
                          + $"as '{ForecastVocabulary.UnitAdmissible}' on the reviewed unit, its subject left empty: an answer is a decision with a forecast.");

        IReadOnlyList<OrgRef> refs = [new OrgRef(OrgRefKind.Event, id), .. r.Refs.Where(x => !(x.Kind == OrgRefKind.Event && x.Id == id))];
        var decision = WriteDecision(r.Caller, r.Attempt, OrgDecisionKind.ReviewAnswer, r.Options.Count > 0 ? r.Options : [r.Choice], r.Choice,
            r.Rationale, refs, now);
        var forecasts = r.Forecasts.Select(f => AddForecast(r.Caller, r.Attempt, decision.Id, null, f, now, subjectByDefault: unitId)).ToList();
        var closed = new MissionEventStore(db).CloseReviewWakes(r.EventId, now, $"answered by decision {decision.Id}");
        return new ReviewAnswered(r.EventId, decision, forecasts, closed);
    });

    /// <summary>
    /// ONE REVIEW, IN ONE WRITE: nothing when its wake's id is already on the queue (a second pass, a restart); else the <c>review</c>
    /// event, decided by the app, and the <c>cut-review</c> wake to <paramref name="reviewer"/> carrying the event's id. Answers 1 or 0.
    /// </summary>
    int RaiseReview(string rule, string subject, string period, string unitId, string? positionId, string reviewer, DateTimeOffset now,
        string detail) => db.Write(_ =>
    {
        var wake = MissionEventIds.CutReview(rule, subject, period);
        using (var seen = db.Cmd("SELECT EXISTS (SELECT 1 FROM mission_event WHERE id = $id)", ("$id", wake)))
            if (Convert.ToInt64(seen.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return 0;
        var eventId = Event(now, unitId, positionId, OrgEventKind.Review, OrgDecider.App, $"{detail} reviewer={reviewer} wake={wake}");
        if (!new MissionEventStore(db).Raise(wake, MissionEventKind.CutReview, now, payload: eventId.ToString(CultureInfo.InvariantCulture),
                role: reviewer))
            throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT, $"the review wake {wake} appeared inside its own write");
        return 1;
    });

    /// <summary>The nearest ACTIVE head above <paramref name="unitId"/> that is not <paramref name="except"/>, or null (the root's head is the owner's seat).</summary>
    string? HeadAbove(string unitId, string? except) =>
        Ancestors(unitId).Select(u => u.HeadPositionId)
            .FirstOrDefault(h => h is not null && h != except && Position(h) is { Status: OrgPositionStatus.Active });

    /// <summary>Whether a mandate issued in, or held from, <paramref name="unitId"/>'s subtree was accepted in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    bool AcceptedInSubtree(string unitId, DateTimeOffset from, DateTimeOffset to)
    {
        var ids = Subtree(unitId).Select(u => u.Id).ToList();
        var names = ids.Select((_, i) => "$u" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        var inList = string.Join(", ", names);
        using var c = db.Cmd($"""
            SELECT EXISTS (SELECT 1 FROM org_mandate_event e JOIN org_mandate m ON m.id = e.mandate_id
                            LEFT JOIN org_position h ON h.id = m.holder
                            WHERE e.kind = $accepted AND e.at >= $from AND e.at < $to
                              AND (m.unit_id IN ({inList}) OR h.unit_id IN ({inList})))
            """, [("$accepted", (object?)MandateEventKind.Accepted), ("$from", Sql.T(from)), ("$to", Sql.T(to)),
                  .. names.Select((n, i) => (n, (object?)ids[i]))]);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>The positions with at least <paramref name="min"/> forecasts written scored and resolved yes or no — the only ones a calibration review can reach.</summary>
    IReadOnlyList<string> Forecasters(int min) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT f.forecaster FROM org_forecast f JOIN org_forecast_resolution r ON r.forecast_id = f.id
             WHERE f.scored = 1 AND r.outcome IN ($yes, $no) GROUP BY f.forecaster HAVING COUNT(*) >= $min ORDER BY f.forecaster
            """, ("$yes", ForecastOutcome.Yes), ("$no", ForecastOutcome.No), ("$min", Math.Max(1, min)));
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return (IReadOnlyList<string>)ids;
    });

    /// <summary>A manager version's name as a short key for a wake id: the first eight hex of its SHA-256.</summary>
    static string VersionKey(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
}
