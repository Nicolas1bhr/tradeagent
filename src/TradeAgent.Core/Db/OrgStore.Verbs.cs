using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE BOUNDS THE STRUCTURAL VERBS ENFORCE (ORGANISATION § 3; VISION § 3; <c>U-org-verbs-c</c>): today's defaults — policy, none
/// permanent. The structural counts rise only by VISION § 3's earning rule; a fourth manager layer is refused by the EARNING TEST (it
/// needs a bounded experiment's record, and none exists), never by a depth constant.
/// </summary>
public static class OrgBounds
{
    /// <summary>Manager layers the chart carries without an experiment's record: the chief, a division head, a team head (R13 D2).</summary>
    public const int ManagerLayers = 3;

    /// <summary>At most this many open divisions.</summary>
    public const int MaxDivisions = 6;

    /// <summary>At most this many positions that have not ended, the legacy pair included (R14 § 3.1).</summary>
    public const int MaxPositions = 10;

    /// <summary>At most this many members (positions that do not head it) in one unit (R13 D3: one to five executors per head).</summary>
    public const int MaxMembers = 5;

    /// <summary>At most this many structural changes (<see cref="OrgEventKind.Structural"/>) by positions in a trailing week (R17 #14).</summary>
    public const int StructuralPerWeek = 2;

    /// <summary>A unit lives at least this long before it is closed or merged away, unless a kill forecast on its charter resolved (ORGANISATION § 3).</summary>
    public static readonly TimeSpan MinimumLife = TimeSpan.FromDays(14);

    /// <summary>How recent the work a charter or a hire cites must be to count as current (survey Q6).</summary>
    public static readonly TimeSpan EarningWindow = TimeSpan.FromDays(30);

    /// <summary>A funded, bounded experiment: at most this share of its parent's AI envelope …</summary>
    public const decimal ExperimentMaxShare = 0.25m;

    /// <summary>… and a collapse date at most this far ahead.</summary>
    public static readonly TimeSpan ExperimentHorizon = TimeSpan.FromDays(90);

    /// <summary>A tilt moves a unit's share in one currency by at most this much in <see cref="TiltWindow"/> (ORGANISATION § 8: 20 points a week).</summary>
    public const decimal TiltPerWeek = 0.20m;

    /// <summary>The trailing window <see cref="TiltPerWeek"/> is summed over.</summary>
    public static readonly TimeSpan TiltWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// THE EXPLORATION FLOOR (<c>U-org-scorecards-b</c> b1; ORGANISATION § 8): this fraction of every pot is split EQUALLY among its
    /// active units, new ones included — the organisation's protected allocation for work no measure can value yet. A write of an AI
    /// share that leaves an active child below <c>ExplorationFloor / n</c> of its parent's envelope is refused (<see cref="OrgStore.Tilt"/>,
    /// <see cref="OrgStore.Charter"/>).
    /// </summary>
    public const decimal ExplorationFloor = 0.25m;
}

/// <summary>
/// A CHARTER AS ITS HEAD ASKS IT: the kind of unit, its AI share of the parent's envelope, the rationale and the options considered
/// (claims), the app records it cites as evidence of current work, its forecasts (at least one <c>unit-admissible</c>, whose subject
/// left empty is the unit chartered), and — for a funded, bounded experiment instead of cited work — its collapse date.
/// </summary>
/// <para><paramref name="TrialShare"/> and <paramref name="VerdictShare"/> (<c>U-org-verbs-e</c>): the new unit's fractions of a campaign's
/// own trial and verdict budgets; null is no own cap.</para>
public sealed record CharterRequest(
    string Caller, string? Attempt, string Kind, decimal AiShare, string Rationale, IReadOnlyList<string> Options,
    IReadOnlyList<OrgRef> Refs, IReadOnlyList<ForecastClaim> Forecasts, DateTimeOffset? CollapseBy,
    decimal? TrialShare = null, decimal? VerdictShare = null);

/// <summary>
/// WHAT THE HOST THAT METERS LAUNCHES READS FOR A CHARTER'S SHARE: the seat whose one turn the new unit must be able to fund (the
/// caller's own, as recorded), what one turn of it reserves, and the parent unit's envelope in each ceiling the seat is held to — as
/// admission resolves it, nearest envelope at or above the parent. Null is a host that meters nothing: the charter is refused.
/// <para><paramref name="LegacyShares"/>: each legacy division's share of ITS parent, by unit id, as admission reads its NULL
/// <c>envelope_share</c> — the split on the Safety page (<see cref="OrgEnvelopes"/>). A legacy sibling the host did not read here
/// cannot be sized, and the charter is refused rather than counting it as nothing.</para>
/// </summary>
public sealed record CharterFunding(string Seat, decimal Reservation, IReadOnlyList<CeilingEnvelope> Parent,
    IReadOnlyDictionary<string, decimal>? LegacyShares = null);

/// <summary>An envelope in one ceiling (<see cref="BillingClasses.Key"/> or <see cref="BillingClasses.Plan"/>).</summary>
public sealed record CeilingEnvelope(string Ceiling, decimal Envelope);

/// <summary>
/// A HIRE AS ITS HEAD ASKS IT: the unit, whether the new position is to be that VACANT unit's head (an appointment — never a
/// replacement), the rationale and options (claims), the app records it cites as current work, and its forecasts (at least one; a
/// subject left empty is the unit).
/// </summary>
public sealed record HireRequest(
    string Caller, string? Attempt, string UnitId, bool AsHead, string Rationale, IReadOnlyList<string> Options,
    IReadOnlyList<OrgRef> Refs, IReadOnlyList<ForecastClaim> Forecasts);

/// <summary>What a hire wrote: the position (dormant), its <c>hire</c> decision, the forecasts on it and the <c>hired</c> event's id.</summary>
public sealed record Hired(OrgPositionRow Position, DecisionRow Decision, IReadOnlyList<ForecastRow> Forecasts, long EventId);

/// <summary>What a charter wrote: the unit, its <c>charter</c> decision, the forecasts on it and the <c>chartered</c> event's id.</summary>
public sealed record Chartered(OrgUnitRow Unit, DecisionRow Decision, IReadOnlyList<ForecastRow> Forecasts, long EventId);

/// <summary>
/// THE STRUCTURAL VERBS (<c>U-org-verbs-c</c>, O6 part c; ORCHESTRATION § 2.9; ORGANISATION §§ 2, 3, 5): a head charters a unit under
/// its own and hires a position into its subtree — each one write, each a MANDATORY decision record with its rationale in the ledger
/// (a claim), each inside the rights matrix (<see cref="OrgRights"/>), the chart's bounds (<see cref="OrgBounds"/>), the weekly
/// throttle and the earning test — or refused in words with nothing written. Ids are the app's; the event's detail is composed by the
/// app from ids and fixed words. Nothing here activates a position: a hire is DORMANT, so no active position beyond the legacy pair
/// appears and the legacy pair's unconfined launches keep turning (<see cref="PositionActivation"/>). No verb here reaches
/// <c>operations</c> or its division (ORDER-001, AUTH-001): the matrix answers that a board act.
/// </summary>
public sealed partial class OrgStore
{
    /// <summary>The prefix of a unit id the app mints.</summary>
    public const string UnitPrefix = "unit-";

    static string MintUnit() => Mint(UnitPrefix);

    /// <summary>
    /// CHARTERS A UNIT UNDER THE CALLER'S OWN (ORGANISATION § 3): the <c>org_unit</c> row — the kind asked, the head VACANT, status
    /// active, the AI share as its envelope — the <c>charter</c> decision through the mandatory-kind path (rationale in the ledger), its
    /// forecasts (at least one <c>unit-admissible</c>), and one <c>chartered</c> event decided by the caller, in ONE write. Refused in
    /// words, nothing written: the rights matrix; a closed parent; a kind the parent cannot hold; a fourth manager layer without an
    /// experiment's record; a seventh division; a share that is no fraction, would take the siblings past the whole, or cannot fund one
    /// turn of the seat (<see cref="Unfundable"/>) — or no meter to read it from; a third structural change in seven days; the earning
    /// test.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public Chartered Charter(CharterRequest r, CharterFunding? funding, DateTimeOffset now) => db.Write(_ =>
    {
        if (IsAppPrincipal(r.Caller) || Position(r.Caller) is not { Status: OrgPositionStatus.Active })
            throw Forbidden($"'{r.Caller}' is no active position: a unit is chartered by the head whose launch asks it.");
        var parent = UnitOf(r.Caller) ?? throw Forbidden($"'{r.Caller}' belongs to no unit, so there is no unit to charter under.");
        var line = Ancestors(parent.Id);

        // THE RIGHTS MATRIX FIRST: who may charter under this unit, read off the chart.
        var right = OrgRights.Answer(OrgOperation.Charter, OrgRights.ReservedOf(OrgOperation.Charter, parent, line),
            OrgRights.RelationOf(OrgOperation.Charter, r.Caller, parent, line));
        if (OrgRights.Refusal(right, $"chartering a unit under {OrgEnvelopes.NameOf(parent.Id)}") is { } notYours)
            throw Forbidden(notYours);

        if (parent.Status == OrgUnitStatus.Closed)
            throw Refused($"{OrgEnvelopes.NameOf(parent.Id)} is closed: nothing is chartered under a closed unit.");
        if (KindRefusal(r.Kind, parent) is { } badKind) throw Refused(badKind);

        // A FOURTH MANAGER LAYER IS THE EARNING TEST'S, NEVER A DEPTH CONSTANT'S (VISION § 3): the new unit's head would be a manager
        // at layer depth(parent) + 2 — the root's head is the first.
        var layer = line.Count + 2;
        if (layer > OrgBounds.ManagerLayers)
            throw Refused($"a {r.Kind} under {OrgEnvelopes.NameOf(parent.Id)} would add manager layer {layer.ToString(CultureInfo.InvariantCulture)}, "
                          + $"and a layer past the {OrgBounds.ManagerLayers.ToString(CultureInfo.InvariantCulture)} the chart carries is earned "
                          + "only by a bounded experiment's record showing it pays (VISION § 3: the proving ground, then a paired run) — the "
                          + "earning test — and this build holds no such record.");
        if (r.Kind == OrgUnitKind.Division
            && Units().Count(u => u.Kind == OrgUnitKind.Division && u.Status != OrgUnitStatus.Closed) >= OrgBounds.MaxDivisions)
            throw Refused($"the organisation has {OrgBounds.MaxDivisions.ToString(CultureInfo.InvariantCulture)} open divisions, the most it carries.");

        // THE SHARE: a fraction, the siblings' with it at most the whole, and enough to fund one turn of the seat (R17 #1).
        if (r.AiShare is <= 0m or > 1m)
            throw Refused($"{r.AiShare.ToString(CultureInfo.InvariantCulture)} is no AI share: a fraction of the parent's envelope, above 0 and at most 1.");
        var id = MintUnit();
        if (funding is null)
            throw Refused("this host meters no AI envelope, so whether the share can fund one turn of a seat cannot be checked: a charter "
                          + "whose envelope nobody could size is refused, never guessed.");

        // EVERY SIBLING COUNTS (the Codex review of U-org-verbs-c, P2). A NULL share on a legacy division is "that division's position's
        // share of the day — the split on the Safety page" (OrgStore.cs, OrgEnvelopes; resolved in OrgEnvelopes.Resolve), a fraction of
        // this parent as real as a written one, read as admission reads it — and one the host did not read is refused, never taken as 0.
        // NULL on any other unit "is no separate cap": it holds no fraction of the parent to split, and spends inside the parent's room.
        var siblings = 0m;
        var legacy = false;
        foreach (var u in Units().Where(u => u.ParentId == parent.Id && u.Status != OrgUnitStatus.Closed))
        {
            if (u.EnvelopeShare is not null) siblings += ShareOf(u);
            else if (OrgEnvelopes.LegacyRoleOf(u.Id) is not null)
            {
                if (funding.LegacyShares?.TryGetValue(u.Id, out var split) != true || split is < 0m or > 1m)
                    throw Refused($"{OrgEnvelopes.NameOf(u.Id)}'s share of {OrgEnvelopes.NameOf(parent.Id)} is the split on the Safety page, "
                                  + "and this host did not read it: the siblings' shares cannot be summed, so the charter is refused, never guessed.");
                siblings += split;
                legacy = true;
            }
        }
        if (siblings + r.AiShare > 1m)
            throw Refused($"the units under {OrgEnvelopes.NameOf(parent.Id)} already hold {siblings.ToString(CultureInfo.InvariantCulture)} of its "
                          + (legacy ? "envelope (the legacy divisions' at the split on the Safety page)" : "envelope")
                          + $", and {r.AiShare.ToString(CultureInfo.InvariantCulture)} more would split more than the whole.");
        foreach (var parentEnvelope in funding.Parent)
            if (Unfundable(funding.Seat, id, r.AiShare * parentEnvelope.Envelope, funding.Reservation) is { } never)
                throw Refused($"{never} ({BillingClasses.Words(parentEnvelope.Ceiling)})");
        // THE EXPLORATION FLOOR (U-org-scorecards-b b1): the new unit counted among its parent's active units.
        if (FloorRefusal(parent.Id, null, r.AiShare) is { } belowFloor) throw Refused(belowFloor);

        // ITS TRIAL AND VERDICT SHARES, WHEN ASKED (U-org-verbs-e): fractions of a campaign's own budgets, the siblings' with each at
        // most the whole, each worth at least one of every open campaign's — the checks a tilt makes (OrgStore.Tilt).
        foreach (var (currency, asked) in new[] { (OrgCurrency.Trials, r.TrialShare), (OrgCurrency.Verdicts, r.VerdictShare) })
        {
            if (asked is not { } evidence) continue;
            if (evidence is < 0m or > 1m)
                throw Refused($"{evidence.ToString(CultureInfo.InvariantCulture)} is no {currency} share: a fraction, at least 0 and at most 1.");
            if (SiblingsRefusal(currency, parent.Id, null, evidence) is { } past) throw Refused(past);
            if (WorthRefusal(currency, evidence) is { } worthless) throw Refused(worthless);
        }

        if (ThrottleRefusal(now) is { } throttled) throw Refused(throttled);

        // THE EARNING TEST: current work, cited and checked — or a funded, bounded experiment with its collapse date.
        string route;
        if (r.CollapseBy is { } collapse)
        {
            if (r.AiShare > OrgBounds.ExperimentMaxShare)
                throw Refused($"a bounded experiment takes at most {OrgBounds.ExperimentMaxShare.ToString(CultureInfo.InvariantCulture)} of the "
                              + $"parent's envelope, and {r.AiShare.ToString(CultureInfo.InvariantCulture)} was asked (the earning test).");
            if (collapse <= now || collapse > now + OrgBounds.ExperimentHorizon)
                throw Refused($"a bounded experiment names the date it collapses unless its forecast holds — after now and within "
                              + $"{OrgBounds.ExperimentHorizon.TotalDays.ToString(CultureInfo.InvariantCulture)} days; {collapse:u} is not (the earning test).");
            route = $"experiment collapse_by={Sql.T(collapse)}";
        }
        else
        {
            var subtree = SubtreeOf(r.Caller).Select(u => u.Id).ToList();
            var current = r.Refs.Where(x => Earns(x, subtree, now)).ToList();
            if (current.Count == 0)
                throw Refused(EarningWords("a charter", subtree));
            route = "evidence " + string.Join(",", current.Select(x => $"{x.Kind}:{x.Id}"));
        }

        if (r.Forecasts.Count > DecisionLimits.MaxForecasts)
            throw Refused($"a charter carries at most {DecisionLimits.MaxForecasts.ToString(CultureInfo.InvariantCulture)} forecasts.");
        if (!r.Forecasts.Any(f => f.Predicate == ForecastVocabulary.UnitAdmissible && (f.Subject is null || f.Subject == id)))
            throw Refused($"a charter carries at least one '{ForecastVocabulary.UnitAdmissible}' forecast on the unit it charters (its subject "
                          + "left empty): whether a version run under its funding is promoted or made paper-eligible by a date (ORGANISATION § 5).");

        // THE WRITE: the decision first (the unit names it), then the unit, its forecasts and its event.
        var choice = $"charter a {r.Kind} under {parent.Id} with AI share {r.AiShare.ToString(CultureInfo.InvariantCulture)}";
        var decision = WriteDecision(r.Caller, r.Attempt, OrgDecisionKind.Charter, r.Options.Count > 0 ? r.Options : [choice], choice,
            r.Rationale, r.Refs, now);
        using (var c = db.Cmd("""
            INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, envelope_share, charter_decision_id, created_at, closed_at,
                                 trial_share, verdict_share)
            VALUES($id, $parent, $kind, NULL, $status, $share, $decision, $at, NULL, $trials, $verdicts)
            """, ("$id", id), ("$parent", parent.Id), ("$kind", r.Kind), ("$status", OrgUnitStatus.Active),
            ("$share", Sql.D(r.AiShare)), ("$decision", decision.Id), ("$at", Sql.T(now)),
            ("$trials", r.TrialShare is { } t ? Sql.D(t) : null), ("$verdicts", r.VerdictShare is { } v ? Sql.D(v) : null)))
            c.ExecuteNonQuery();
        var forecasts = r.Forecasts.Select(f => AddForecast(r.Caller, r.Attempt, decision.Id, null, f, now, subjectByDefault: id)).ToList();
        var eventId = Event(now, id, null, OrgEventKind.Chartered, r.Caller,
            $"unit={id} kind={r.Kind} parent={parent.Id} share={Sql.D(r.AiShare)} decision={decision.Id} {route}");
        return new Chartered(Units().Single(u => u.Id == id), decision, forecasts, eventId);
    });

    /// <summary>
    /// HIRES A POSITION INTO A UNIT (ORGANISATION § 2 :107 — only the head of the unit's PARENT hires; survey Q3): the app mints the id,
    /// writes the <c>org_position</c> row DORMANT — home <c>org/&lt;id&gt;</c>, no seat (<c>U-org-seats</c> binds one) — and, for an
    /// appointment, makes it the VACANT unit's head; the <c>hire</c> decision through the mandatory-kind path (rationale in the ledger),
    /// its forecasts, and one <c>hired</c> event decided by the caller — in ONE write. DORMANT, so nothing activates (survey Q2): an
    /// active position beyond the legacy pair would refuse the legacy pair's unconfined launches (<see cref="PositionActivation"/>), and
    /// activation is not an organisation verb's. Refused in words, nothing written: the rights matrix; a head's REPLACEMENT (a different
    /// operation); a unit that is not there or is closed; more than <see cref="OrgBounds.MaxPositions"/> positions; more than
    /// <see cref="OrgBounds.MaxMembers"/> members in the unit; the earning test (current work cited, or the unit's first position, or a
    /// unit chartered as a bounded experiment before its collapse date); no forecast.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public Hired Hire(HireRequest r, DateTimeOffset now) => db.Write(_ =>
    {
        if (IsAppPrincipal(r.Caller) || Position(r.Caller) is not { Status: OrgPositionStatus.Active })
            throw Forbidden($"'{r.Caller}' is no active position: a position is hired by the head whose launch asks it.");
        var unit = Units().FirstOrDefault(u => u.Id == r.UnitId) ?? throw Refused($"'{r.UnitId}' is no unit on the chart.");
        var line = Ancestors(unit.Id);

        // AN APPOINTMENT FILLS A VACANT HEAD; A FILLED ONE IS A REPLACEMENT — a different operation, no verb's in this build.
        if (r.AsHead && unit.HeadPositionId is { } sitting)
        {
            var replace = OrgRights.Answer(OrgOperation.Replace, OrgRights.ReservedOf(OrgOperation.Replace, unit, line, sitting),
                OrgRights.RelationOf(OrgOperation.Replace, r.Caller, unit, line));
            throw Forbidden($"{OrgEnvelopes.NameOf(unit.Id)} is headed by {sitting}: replacing a head is a different operation from an "
                            + "appointment, and "
                            + (OrgRights.Refusal(replace, "it") is { } words ? words : "no verb performs it in this build.")
                            + " A hire with its head flag only fills a vacant head.");
        }
        var op = r.AsHead ? OrgOperation.Appoint : OrgOperation.Hire;
        var right = OrgRights.Answer(op, OrgRights.ReservedOf(op, unit, line), OrgRights.RelationOf(op, r.Caller, unit, line));
        var what = r.AsHead ? $"appointing the head of {OrgEnvelopes.NameOf(unit.Id)}" : $"hiring into {OrgEnvelopes.NameOf(unit.Id)}";
        if (OrgRights.Refusal(right, what) is { } notYours) throw Forbidden(notYours);

        if (unit.Status == OrgUnitStatus.Closed)
            throw Refused($"{OrgEnvelopes.NameOf(unit.Id)} is closed: nobody is hired into a closed unit.");
        var open = Positions().Where(p => p.Status != OrgPositionStatus.Ended).ToList();
        if (open.Count >= OrgBounds.MaxPositions)
            throw Refused($"the organisation holds {open.Count.ToString(CultureInfo.InvariantCulture)} positions that have not ended, the most "
                          + $"it carries ({OrgBounds.MaxPositions.ToString(CultureInfo.InvariantCulture)}); a dormant one counts until it ends.");
        var inUnit = open.Where(p => p.UnitId == unit.Id).ToList();
        var members = inUnit.Count(p => p.Id != unit.HeadPositionId);
        if (!r.AsHead && members >= OrgBounds.MaxMembers)
            throw Refused($"{OrgEnvelopes.NameOf(unit.Id)} has {members.ToString(CultureInfo.InvariantCulture)} members, the most one head "
                          + $"leads ({OrgBounds.MaxMembers.ToString(CultureInfo.InvariantCulture)}).");

        // THE EARNING TEST: a unit's first position needs no more than its charter did; a unit chartered as a bounded experiment hires
        // inside it until its collapse date; anything else cites current work of the caller's subtree.
        string route;
        var subtree = SubtreeOf(r.Caller).Select(u => u.Id).ToList();
        var current = r.Refs.Where(x => Earns(x, subtree, now)).ToList();
        if (inUnit.Count == 0) route = "first";
        else if (ExperimentUntil(unit.Id) is { } collapse && collapse > now) route = $"experiment collapse_by={Sql.T(collapse)}";
        else if (current.Count > 0) route = "evidence " + string.Join(",", current.Select(x => $"{x.Kind}:{x.Id}"));
        else throw Refused(EarningWords($"a hire into {OrgEnvelopes.NameOf(unit.Id)}, which has positions already,", subtree));

        if (r.Forecasts.Count is 0 or > DecisionLimits.MaxForecasts)
            throw Refused($"a hire carries 1 to {DecisionLimits.MaxForecasts.ToString(CultureInfo.InvariantCulture)} forecasts — such as "
                          + $"'{ForecastVocabulary.UnitAdmissible}' on the unit, its subject left empty: a structural act is a decision with a forecast.");

        var id = OrgIds.MintPosition();
        if (OrgIds.PositionIdRefusal(id) is { } badId) throw Refused(badId);
        var choice = $"hire {id} into {unit.Id} as {(r.AsHead ? "its head" : "a member")}";
        var decision = WriteDecision(r.Caller, r.Attempt, OrgDecisionKind.Hire, r.Options.Count > 0 ? r.Options : [choice], choice,
            r.Rationale, r.Refs, now);
        var home = CouncilRoles.HomeDir(id);
        using (var c = db.Cmd("""
            INSERT INTO org_position(id, unit_id, home_dir, status, created_at, ended_at) VALUES($id, $unit, $home, $status, $at, NULL)
            """, ("$id", id), ("$unit", unit.Id), ("$home", home), ("$status", OrgPositionStatus.Dormant), ("$at", Sql.T(now))))
            c.ExecuteNonQuery();
        if (r.AsHead)
        {
            using var h = db.Cmd("UPDATE org_unit SET head_position_id = $id WHERE id = $unit AND head_position_id IS NULL",
                ("$id", id), ("$unit", unit.Id));
            if (h.ExecuteNonQuery() != 1)
                throw Refused($"{OrgEnvelopes.NameOf(unit.Id)}'s head was filled while this hire was written: an appointment fills a vacant head only.");
        }
        var forecasts = r.Forecasts.Select(f => AddForecast(r.Caller, r.Attempt, decision.Id, null, f, now, subjectByDefault: unit.Id)).ToList();
        var eventId = Event(now, unit.Id, id, OrgEventKind.Hired, r.Caller,
            $"position={id} unit={unit.Id} as={(r.AsHead ? "head" : "member")} status={OrgPositionStatus.Dormant} home={home} "
            + $"decision={decision.Id} {route}");
        return new Hired(Position(id)!, decision, forecasts, eventId);
    });

    /// <summary>
    /// THE COLLAPSE DATE OF A UNIT CHARTERED AS A BOUNDED EXPERIMENT, read from its <c>chartered</c> event — whose detail the app
    /// composed from ids and fixed words (<see cref="Charter"/>) — or null: chartered on cited work, seeded, or a detail this build did
    /// not write, which is never read on a guess.
    /// </summary>
    DateTimeOffset? ExperimentUntil(string unitId)
    {
        const string marker = " experiment collapse_by=";
        using var c = db.Cmd("SELECT detail FROM org_event WHERE unit_id = $u AND kind = $k AND position_id IS NULL ORDER BY id LIMIT 1",
            ("$u", unitId), ("$k", OrgEventKind.Chartered));
        if (c.ExecuteScalar() is not string detail) return null;
        var at = detail.IndexOf(marker, StringComparison.Ordinal);
        return at >= 0 && DateTimeOffset.TryParse(detail[(at + marker.Length)..], CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var collapse)
            ? collapse : null;
    }

    /// <summary>Why a unit of <paramref name="kind"/> cannot hang from <paramref name="parent"/>, or null: a division under the root, a subdivision under a division, a team under either.</summary>
    static string? KindRefusal(string kind, OrgUnitRow parent) => kind switch
    {
        OrgUnitKind.Division when parent.Kind == OrgUnitKind.Root => null,
        OrgUnitKind.Subdivision when parent.Kind == OrgUnitKind.Division => null,
        OrgUnitKind.Team when parent.Kind is OrgUnitKind.Division or OrgUnitKind.Subdivision => null,
        OrgUnitKind.Division or OrgUnitKind.Subdivision or OrgUnitKind.Team =>
            $"a {kind} does not hang from a {parent.Kind}: a division hangs from the organisation, a subdivision from a division, and a "
            + "team from a division or a subdivision.",
        _ => $"'{kind}' is no kind of unit a head charters: {OrgUnitKind.Division} | {OrgUnitKind.Subdivision} | {OrgUnitKind.Team}."
    };

    /// <summary>A stored share, as admission reads it; a share that is not a fraction is damage, refused.</summary>
    static decimal ShareOf(OrgUnitRow u) =>
        decimal.TryParse(u.EnvelopeShare, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var share) && share is >= 0m and <= 1m
            ? share
            : throw Damaged($"unit {u.Id}'s envelope share '{u.EnvelopeShare}' is not a fraction between 0 and 1");

    /// <summary>
    /// THE WEEKLY THROTTLE (ORGANISATION § 3; survey Q4): the structural changes positions decided in the seven days before
    /// <paramref name="now"/>, across the organisation — the app's and the owner's never counted — or null while under the bound.
    /// </summary>
    string? ThrottleRefusal(DateTimeOffset now)
    {
        var kinds = OrgEventKind.Structural.Select((_, i) => $"$k{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        using var c = db.Cmd($"""
            SELECT COUNT(*) FROM org_event
             WHERE kind IN ({string.Join(",", kinds)}) AND decider NOT IN ($app, $owner) AND at > $since
            """, [("$app", (object?)OrgDecider.App), ("$owner", OrgDecider.Owner), ("$since", Sql.T(now.AddDays(-7))),
                .. kinds.Select((k, i) => (k, (object?)OrgEventKind.Structural[i]))]);
        var made = Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        return made < OrgBounds.StructuralPerWeek ? null
            : $"the organisation has made {made.ToString(CultureInfo.InvariantCulture)} structural changes in the last seven days, the most "
              + $"it makes in a week ({OrgBounds.StructuralPerWeek.ToString(CultureInfo.InvariantCulture)}: a charter, a close, a merge).";
    }

    /// <summary>
    /// WHETHER <paramref name="r"/> IS CURRENT WORK OF THE SUBTREE (the earning test, survey Q6): a run whose launch the subtree funded
    /// (its RECORDED lineage), a promotion of a version such a run ran, or a delivery on a mandate of a unit in the subtree — recorded
    /// inside <see cref="OrgBounds.EarningWindow"/>. Kind, recency and bearing are checked, not existence alone; a run is read for its
    /// version, its attempt and its time, never a figure (EVID-001).
    /// </summary>
    bool Earns(OrgRef r, IReadOnlyList<string> subtree, DateTimeOffset now)
    {
        if (subtree.Count == 0) return false;
        var units = subtree.Select((_, i) => $"$u{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        var inSubtree = $"IN ({string.Join(",", units)})";
        var sql = r.Kind switch
        {
            OrgRefKind.Run => $"""
                SELECT EXISTS (SELECT 1 FROM strategy_run r WHERE r.id = $id AND r.created_at >= $since
                   AND EXISTS (SELECT 1 FROM ai_attempt_funding f WHERE f.attempt_id = r.attempt AND f.unit_id {inSubtree}))
                """,
            OrgRefKind.Promotion => $"""
                SELECT EXISTS (SELECT 1 FROM strategy_promotion p WHERE p.id = $id AND p.at >= $since
                   AND EXISTS (SELECT 1 FROM strategy_run r JOIN ai_attempt_funding f ON f.attempt_id = r.attempt
                                WHERE r.version_id = p.version_id AND f.unit_id {inSubtree}))
                """,
            OrgRefKind.Delivery => $"""
                SELECT EXISTS (SELECT 1 FROM org_delivery d JOIN org_mandate m ON m.id = d.mandate_id
                                WHERE d.id = $id AND d.at >= $since AND m.unit_id {inSubtree})
                """,
            _ => null
        };
        if (sql is null) return false;
        using var c = db.Cmd(sql, [("$id", (object?)r.Id), ("$since", Sql.T(now - OrgBounds.EarningWindow)),
            .. units.Select((u, i) => (u, (object?)subtree[i]))]);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    static string EarningWords(string act, IReadOnlyList<string> subtree) =>
        $"{act} must earn its place (VISION § 3; the earning test): cite current work of your subtree "
        + $"({(subtree.Count == 0 ? "you head none" : string.Join(", ", subtree))}) — a run its launches funded, a promotion of a version "
        + $"such a run ran, or a delivery on one of its mandates, recorded in the last {OrgBounds.EarningWindow.TotalDays.ToString(CultureInfo.InvariantCulture)} "
        + "days — or charter it as a funded, bounded experiment with a collapse date. Nothing cited is current work of yours.";

    /// <summary>ONE <c>org_event</c>, inside the caller's write; its detail is the app's, from ids and fixed words. Answers its id.</summary>
    long Event(DateTimeOffset at, string unitId, string? positionId, string kind, string decider, string detail)
    {
        using var c = db.Cmd("""
            INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES($at, $unit, $position, $kind, $decider, $detail)
            RETURNING id
            """, ("$at", Sql.T(at)), ("$unit", unitId), ("$position", positionId), ("$kind", kind), ("$decider", decider), ("$detail", detail));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
