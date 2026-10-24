using System.Globalization;
using System.Text.RegularExpressions;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE THREE CURRENCIES OF A UNIT'S ENVELOPE (<c>U-org-verbs-e</c>; ORCHESTRATION § 2.4, "V1 carries AI spend, trials and verdicts";
/// ORGANISATION § 3): <see cref="Ai"/> is <c>envelope_share</c>, a fraction of the parent's AI envelope; <see cref="Trials"/> and
/// <see cref="Verdicts"/> are <c>trial_share</c> and <c>verdict_share</c>, fractions of a campaign's own trial and verdict budgets that
/// the launches a unit funds may spend (<see cref="CampaignStore.RegisterTrial"/>, <see cref="CampaignStore.ChargeVerdict"/>). NULL is
/// no own cap. Strings, as the chart's vocabulary is.
/// </summary>
public static class OrgCurrency
{
    public const string Ai = "ai", Trials = "trials", Verdicts = "verdicts";

    public static readonly string[] All = [Ai, Trials, Verdicts];

    public static bool IsKnown(string? currency) => currency is not null && Array.IndexOf(All, currency) >= 0;

    /// <summary>The <c>org_unit</c> column a currency's share is kept in — this build's own constant, never a caller's text.</summary>
    internal static string Column(string currency) => currency switch
    {
        Ai => "envelope_share",
        Trials => "trial_share",
        Verdicts => "verdict_share",
        _ => throw new TradeAgentException(ErrorCode.INVALID_REQUEST, $"'{currency}' is no currency of a unit's envelope.")
    };

    /// <summary>A unit's share in <paramref name="currency"/>, as its row holds it (null: no own cap).</summary>
    public static string? Of(OrgUnitRow unit, string currency) => currency switch
    {
        Ai => unit.EnvelopeShare,
        Trials => unit.TrialShare,
        Verdicts => unit.VerdictShare,
        _ => null
    };
}

/// <summary>
/// A TILT AS THE PARENT'S HEAD ASKS IT (ORCHESTRATION § 2.9; ORGANISATION § 8): the child unit, ONE currency, the new share, the
/// rationale and options (claims), the app records it rests on, and its forecasts (at least one; a subject left empty is the unit).
/// </summary>
public sealed record TiltRequest(
    string Caller, string? Attempt, string UnitId, string Currency, decimal Share, string Rationale, IReadOnlyList<string> Options,
    IReadOnlyList<OrgRef> Refs, IReadOnlyList<ForecastClaim> Forecasts);

/// <summary>What a tilt wrote: the unit as it now stands, the currency, the share before (null: none) and after, its <c>envelope-tilt</c> decision, the forecasts and the <c>tilted</c> event's id.</summary>
public sealed record Tilted(OrgUnitRow Unit, string Currency, string? From, string To, DecisionRow Decision, IReadOnlyList<ForecastRow> Forecasts,
    long EventId);

public sealed partial class OrgStore
{
    /// <summary>
    /// RE-SPLITS ONE CURRENCY OF A CHILD UNIT'S ENVELOPE (<c>org-tilt</c>; ORCHESTRATION § 2.9; ORGANISATION §§ 3, 8): the head of the
    /// unit's PARENT sets the child's share — its AI share (a fraction of the parent's envelope), or its trial or verdict share (a
    /// fraction of a campaign's own budget, which is read here and never written) — with an <c>envelope-tilt</c> decision, its
    /// forecasts and one <c>tilted</c> event, in ONE write. Re-splitting INSIDE the parent is the head's record; nothing here raises the
    /// parent's envelope or the root's, and the split of the organisation's AI day among its divisions is the security board's (R24
    /// § 4.1). Refused in words, nothing written: the rights matrix (only the parent's head; the root and <c>operations</c>' division a
    /// board act); a closed unit; an unknown currency; the AI share of a division under the organisation; a share that is no fraction
    /// or is the one it holds; a move past <see cref="OrgBounds.TiltPerWeek"/> in seven days, summed for that unit and currency; the
    /// active siblings' shares with it past the whole; an AI share that cannot fund one turn of the seat (<see cref="Unfundable"/>) or
    /// no meter to size it; a trial or verdict share worth less than one of an open campaign's; no forecast.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public Tilted Tilt(TiltRequest r, CharterFunding? funding, DateTimeOffset now, Func<string, decimal?>? reservationOf = null) => db.Write(_ =>
    {
        if (IsAppPrincipal(r.Caller) || Position(r.Caller) is not { Status: OrgPositionStatus.Active })
            throw Forbidden($"'{r.Caller}' is no active position: a share is tilted by the head whose launch asks it.");
        var unit = Units().FirstOrDefault(u => u.Id == r.UnitId) ?? throw Refused($"'{r.UnitId}' is no unit on the chart.");
        var line = Ancestors(unit.Id);

        // THE RIGHTS MATRIX FIRST: only the head of the unit's parent tilts it; the root and `operations`' division are board acts.
        var right = OrgRights.Answer(OrgOperation.Tilt, OrgRights.ReservedOf(OrgOperation.Tilt, unit, line),
            OrgRights.RelationOf(OrgOperation.Tilt, r.Caller, unit, line));
        if (OrgRights.Refusal(right, $"tilting {OrgEnvelopes.NameOf(unit.Id)}'s share") is { } notYours)
            throw Forbidden(notYours);

        if (unit.Status == OrgUnitStatus.Closed)
            throw Refused($"{OrgEnvelopes.NameOf(unit.Id)} is closed: a closed unit's envelope is not re-split.");
        if (!OrgCurrency.IsKnown(r.Currency))
            throw Refused($"'{r.Currency}' is no currency of a unit's envelope: {string.Join(" | ", OrgCurrency.All)} — one per tilt.");
        if (r.Currency == OrgCurrency.Ai && line.Count > 0 && line[0].IsRoot)
            throw Refused($"the split of the organisation's AI day among its divisions is the security board's policy (R24 § 4.1), never a "
                          + $"tilt: {OrgEnvelopes.NameOf(unit.Id)} hangs from the organisation itself. A tilt re-splits inside a division.");
        if (r.Share is < 0m or > 1m)
            throw Refused($"{r.Share.ToString(CultureInfo.InvariantCulture)} is no share: a fraction, at least 0 and at most 1.");

        var from = OrgCurrency.Of(unit, r.Currency);
        var before = from is null ? 0m : Share(unit.Id, r.Currency, from);
        if (from is not null && before == r.Share)
            throw Refused($"{OrgEnvelopes.NameOf(unit.Id)} already holds a {r.Currency} share of {from}: there is nothing to tilt.");

        // AT MOST TWENTY POINTS A WEEK (ORGANISATION § 8), every move of this unit's share in this currency counted, either way.
        var move = Math.Abs(r.Share - before);
        var moved = MovedThisWeek(unit.Id, r.Currency, now);
        if (moved + move > OrgBounds.TiltPerWeek)
            throw Refused($"{OrgEnvelopes.NameOf(unit.Id)}'s {r.Currency} share has moved {moved.ToString(CultureInfo.InvariantCulture)} in the "
                          + $"last seven days, and {move.ToString(CultureInfo.InvariantCulture)} more would move it past the "
                          + $"{OrgBounds.TiltPerWeek.ToString(CultureInfo.InvariantCulture)} a tilt moves a share in a week (ORGANISATION § 8).");

        if (SiblingsRefusal(r.Currency, unit.ParentId!, unit.Id, r.Share) is { } whole) throw Refused(whole);
        if (r.Currency == OrgCurrency.Ai)
        {
            if (funding is null)
                throw Refused("this host meters no AI envelope, so whether the share can fund one turn of a seat cannot be checked: a tilt "
                              + "whose envelope nobody could size is refused, never guessed.");
            foreach (var parentEnvelope in funding.Parent)
                if (Unfundable(funding.Seat, unit.Id, r.Share * parentEnvelope.Envelope, funding.Reservation) is { } never)
                    throw Refused($"{never} ({BillingClasses.Words(parentEnvelope.Ceiling)})");
            if (StrandedSeat(unit, r.Share, funding, reservationOf) is { } stranded) throw Refused(stranded);
        }
        else if (WorthRefusal(r.Currency, r.Share) is { } worthless) throw Refused(worthless);

        if (r.Forecasts.Count is 0 or > DecisionLimits.MaxForecasts)
            throw Refused($"a tilt carries 1 to {DecisionLimits.MaxForecasts.ToString(CultureInfo.InvariantCulture)} forecasts — such as "
                          + $"'{ForecastVocabulary.UnitAdmissible}' on the unit, its subject left empty: a tilt is a decision with a forecast.");

        // THE WRITE: the decision, the share, its forecasts and its event.
        var to = Sql.D(r.Share);
        var choice = $"tilt {unit.Id}'s {r.Currency} share from {from ?? "none"} to {to}";
        var decision = WriteDecision(r.Caller, r.Attempt, OrgDecisionKind.EnvelopeTilt, r.Options.Count > 0 ? r.Options : [choice], choice,
            r.Rationale, r.Refs, now);
        using (var c = db.Cmd($"UPDATE org_unit SET {OrgCurrency.Column(r.Currency)} = $share WHERE id = $id", ("$share", to), ("$id", unit.Id)))
            c.ExecuteNonQuery();
        var forecasts = r.Forecasts.Select(f => AddForecast(r.Caller, r.Attempt, decision.Id, null, f, now, subjectByDefault: unit.Id)).ToList();
        var eventId = Event(now, unit.Id, null, OrgEventKind.Tilted, r.Caller,
            $"unit={unit.Id} currency={r.Currency} from={from ?? "none"} to={to} move={Sql.D(move)} decision={decision.Id}");
        return new Tilted(Units().Single(u => u.Id == unit.Id), r.Currency, from, to, decision, forecasts, eventId);
    });

    /// <summary>
    /// WHY THE AI SHARE <paramref name="share"/> OF <paramref name="unit"/> WOULD STRAND A SEAT IN ITS SUBTREE, or null (the fix pass, the
    /// Codex review's P2; ORGANISATION § 3, "a split that leaves any seated position's reservation larger than its unit's whole envelope
    /// is refused"): in each ceiling the parent's envelope is read in, every unit of the subtree is resolved under the new share — a
    /// share of its own times the envelope above it, none being the room above it — and every position of it that is ACTIVE or SEATED
    /// must be able to start one turn: its reservation, as the host's meter prices it (<paramref name="reservationOf"/>), at most its
    /// unit's whole envelope (<see cref="Unfundable"/>). A seat nobody priced is refused, never guessed.
    /// </summary>
    string? StrandedSeat(OrgUnitRow unit, decimal share, CharterFunding funding, Func<string, decimal?>? reservationOf)
    {
        var subtree = Subtree(unit.Id);
        var inside = subtree.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var seated = Positions().Where(p => inside.Contains(p.UnitId) && p.Status != OrgPositionStatus.Ended
                                            && (p.Status == OrgPositionStatus.Active || p.SeatRuntime is not null)).ToList();
        if (seated.Count == 0) return null;
        if (reservationOf is null)
            return $"this host prices no seat, so whether the {seated.Count.ToString(CultureInfo.InvariantCulture)} seated position(s) under "
                   + $"{OrgEnvelopes.NameOf(unit.Id)} could still start a turn cannot be checked: a tilt that might strand one is refused, never guessed.";

        foreach (var parentEnvelope in funding.Parent)
        {
            var envelope = new Dictionary<string, decimal>(StringComparer.Ordinal) { [unit.Id] = share * parentEnvelope.Envelope };
            foreach (var u in subtree.Skip(1))
                envelope[u.Id] = u.EnvelopeShare is { } text ? Share(u.Id, OrgCurrency.Ai, text) * envelope[u.ParentId!] : envelope[u.ParentId!];
            foreach (var p in seated)
            {
                if (reservationOf(p.Id) is not { } reserves)
                    return $"the host could not price one turn of {p.Id}, seated under {OrgEnvelopes.NameOf(unit.Id)}: a tilt that might strand "
                           + "it is refused, never guessed.";
                if (Unfundable(p.Id, p.UnitId, envelope[p.UnitId], reserves) is { } never)
                    return $"{never} ({BillingClasses.Words(parentEnvelope.Ceiling)}) — the tilt would leave a seated position of "
                           + $"{OrgEnvelopes.NameOf(unit.Id)}'s unable to start one turn.";
            }
        }
        return null;
    }

    /// <summary>A stored share in <paramref name="currency"/>; one that is not a fraction is damage, refused.</summary>
    static decimal Share(string unitId, string currency, string text) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var share) && share is >= 0m and <= 1m
            ? share
            : throw Damaged($"unit {unitId}'s {currency} share '{text}' is not a fraction between 0 and 1");

    /// <summary>
    /// HOW FAR THIS UNIT'S SHARE IN <paramref name="currency"/> HAS MOVED in the seven days before <paramref name="now"/>: the sum of
    /// every <c>tilted</c> event's move, read from the detail the app composed (<see cref="Tilt"/>) — a detail this build did not write
    /// is damage, refused, never read on a guess.
    /// </summary>
    decimal MovedThisWeek(string unitId, string currency, DateTimeOffset now)
    {
        using var c = db.Cmd("SELECT detail FROM org_event WHERE unit_id = $u AND kind = $k AND at > $since",
            ("$u", unitId), ("$k", OrgEventKind.Tilted), ("$since", Sql.T(now - OrgBounds.TiltWindow)));
        using var r = c.ExecuteReader();
        var moved = 0m;
        while (r.Read())
        {
            var detail = r.GetString(0);
            var m = TiltDetail().Match(detail);
            if (!m.Success || !decimal.TryParse(m.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var by))
                throw Damaged($"a 'tilted' event of unit {unitId} reads '{detail}', which this build did not write");
            if (m.Groups[1].Value == currency) moved += by;
        }
        return moved;
    }

    [GeneratedRegex(@"^unit=\S+ currency=(\S+) from=\S+ to=\S+ move=([0-9.]+) decision=\S+$")]
    private static partial Regex TiltDetail();

    /// <summary>
    /// WHY <paramref name="share"/> IN <paramref name="currency"/> WOULD TAKE THE ACTIVE UNITS UNDER <paramref name="parentId"/> PAST THE
    /// WHOLE, or null. Every unit that is not closed counts but <paramref name="except"/> (the one being set); a NULL share holds no
    /// fraction to split. The AI share's legacy siblings — the divisions under the organisation — never reach here: their split is the
    /// board's, which no tilt makes and a charter reads from the host (<see cref="Charter"/>).
    /// </summary>
    string? SiblingsRefusal(string currency, string parentId, string? except, decimal share)
    {
        var held = Units().Where(u => u.ParentId == parentId && u.Id != except && u.Status != OrgUnitStatus.Closed)
            .Sum(u => OrgCurrency.Of(u, currency) is { } text ? Share(u.Id, currency, text) : 0m);
        return held + share <= 1m ? null
            : $"the units under {OrgEnvelopes.NameOf(parentId)} already hold {held.ToString(CultureInfo.InvariantCulture)} in {currency} shares, "
              + $"and {share.ToString(CultureInfo.InvariantCulture)} more would split more than the whole.";
    }

    /// <summary>
    /// WHY A TRIAL OR VERDICT SHARE IS WORTH LESS THAN ONE OF AN OPEN CAMPAIGN'S, or null: a share whose launches could never spend one
    /// is a refusal in disguise. Every open campaign is read — its budget fixed when it opened, and never written here (EVID-001).
    /// </summary>
    string? WorthRefusal(string currency, decimal share)
    {
        var trials = currency == OrgCurrency.Trials;
        foreach (var campaign in new CampaignStore(db).All().Where(c => c.IsOpen))
        {
            var budget = trials ? campaign.TrialBudget : campaign.VerdictBudget;
            if (share * budget >= 1m) continue;
            return $"a {currency} share of {share.ToString(CultureInfo.InvariantCulture)} is worth "
                   + $"{(share * budget).ToString("0.##", CultureInfo.InvariantCulture)} of campaign {campaign.Id.ToString(CultureInfo.InvariantCulture)}'s "
                   + $"{budget.ToString(CultureInfo.InvariantCulture)} {(trials ? "research trials" : "final judgements")} — less than one, so "
                   + "the unit's launches could never spend one. Give it at least one's worth, or none of its own (no share is no own cap).";
        }
        return null;
    }
}
