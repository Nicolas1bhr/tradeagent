using System.Globalization;
using System.Text.Json;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT A POSITION'S LATEST LAUNCH RAN ON, as the ledger wrote it (<c>U-org-surface-a</c>): the seat class and its assurance, and the
/// billing class — all composed by the app at admission, never an agent's word. Null where the ledger holds no such word; a word this
/// build does not know is <see cref="Assurance"/> null, refused rather than guessed.
/// </summary>
public sealed record RecordedSeat(string Role, string? SeatClass, SeatAssurance? Assurance, string BillingClass, DateTimeOffset StartedAt);

/// <summary>One open mandate with its status and measured spend, read across the chart in one query.</summary>
/// <param name="Status">The kind of its latest event that is not a claim, or null where it has no event (damage).</param>
/// <param name="ChargedAtReservation">
/// Resolved launches charged their RESERVATION because nothing reported their cost (a lost or unreported turn): their money is in
/// <c>Spend.Spent</c>, and this is what stops that figure reading as a bill. Never zero for an unknown cost.
/// </param>
public sealed record OpenMandate(MandateRow Mandate, string? Status, MandateSpend Spend, int ChargedAtReservation);

/// <summary>A forecast with the app's resolution of it, or null while it is open.</summary>
public sealed record RecentForecast(ForecastRow Forecast, ResolutionRow? Resolution);

/// <summary>
/// One decision with its rationale — the position's OWN words, a claim — and the forecasts made on it. The rationale is the
/// decision's ledger entry as first written; a later revision is the author's second thought, not what the decision rested on.
/// </summary>
public sealed record RecentDecision(DecisionRow Decision, string? Rationale, IReadOnlyList<RecentForecast> Forecasts);

/// <summary>One unit's day in one billing class (<see cref="AiAttemptStore.UnitTotalsByClass"/>).</summary>
public sealed record UnitClassTotals(string UnitId, string BillingClass, AiAttemptTotals Totals);

/// <summary>
/// THE READS THE ORGANISATION-FIRST PAGES ARE BUILT FROM (<c>U-org-surface-a</c>; R24 § 4.7): the chart's seats, the open mandates, the
/// recent decisions and the escalations addressed to the owner — each ONE query (or one snapshot of a few) across the whole chart, so a
/// five-second refresh asks the store once per fact and not once per position. <b>Reads only</b>: nothing here writes, and none of it
/// reads a file the organisation cannot read — these are the organisation's own records, shown to its owner.
/// </summary>
public sealed partial class OrgStore
{
    /// <summary>
    /// THE ASSURANCE OF <paramref name="position"/>'S RECORDED SEAT (<see cref="RecordedAssurance"/>, public): what the ledger wrote
    /// for its latest launch, or null — never launched, or a word this build does not know.
    /// </summary>
    public SeatAssurance? SeatAssuranceOf(string position) => db.Read(_ => RecordedAssurance(position));

    /// <summary>
    /// EVERY POSITION'S LATEST LAUNCH, by position id, in ONE query: its seat class, assurance and billing class as the ledger recorded
    /// them. A position that never launched is not in it. The chart page reads this once and not once per position.
    /// </summary>
    public IReadOnlyDictionary<string, RecordedSeat> RecordedSeats() => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT COALESCE(a.role, $chair), a.seat_class, a.assurance, a.billing_class, a.started_at
              FROM ai_attempt a
             WHERE a.rowid = (SELECT b.rowid FROM ai_attempt b WHERE COALESCE(b.role, $chair) = COALESCE(a.role, $chair)
                               ORDER BY b.started_at DESC, b.rowid DESC LIMIT 1)
            """, ("$chair", CouncilRoles.Default));
        using var r = c.ExecuteReader();
        var seats = new Dictionary<string, RecordedSeat>(StringComparer.Ordinal);
        while (r.Read())
        {
            var name = Sql.S(r.GetValue(2));
            SeatAssurance? assurance = null;
            foreach (var a in Enum.GetValues<SeatAssurance>())
                if (SeatClasses.Name(a) == name) assurance = a;
            seats[r.GetString(0)] = new RecordedSeat(r.GetString(0), Sql.S(r.GetValue(1)), assurance, BillingClasses.Of(Sql.S(r.GetValue(3))),
                Sql.Time(r.GetValue(4)));
        }
        return (IReadOnlyDictionary<string, RecordedSeat>)seats;
    });

    /// <summary>
    /// EVERY OPEN MANDATE ON THE CHART, newest first, each with its latest status and its measured spend — ONE query. Open is what
    /// <see cref="IsOpen"/> says: no closing event has been written. A mandate's spend is the launch ledger's, as <see cref="SpendOf"/>
    /// reads it; a resolved launch with no cost is counted apart in <see cref="MandateSpend.Unknown"/> and never added as zero.
    /// </summary>
    public IReadOnlyList<OpenMandate> MandatesOpen() => db.Read(_ =>
    {
        var closing = MandateEventKind.Closing.Select((_, i) => $"$c{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        var cols = string.Join(", ", MandateCols.Split(", ").Select(x => "m." + x));
        using var c = db.Cmd($"""
            SELECT {cols}, {LatestStatus},
                   COALESCE(s.spent, 0), COALESCE(s.reserved, 0), COALESCE(s.unknown, 0), COALESCE(s.launches, 0), COALESCE(s.charged, 0)
              FROM org_mandate m
              LEFT JOIN (SELECT mandate_id,
                                SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NOT NULL THEN CAST(cost AS REAL) ELSE 0 END) AS spent,
                                SUM(CASE WHEN state='LAUNCHED' THEN CAST(reserved_cost AS REAL) ELSE 0 END) AS reserved,
                                SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NULL THEN 1 ELSE 0 END) AS unknown,
                                COUNT(*) AS launches,
                                SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NOT NULL AND unpriced_reason IS NOT NULL THEN 1 ELSE 0 END) AS charged
                           FROM ai_attempt WHERE mandate_id IS NOT NULL GROUP BY mandate_id) s ON s.mandate_id = m.id
             WHERE COALESCE({LatestStatus}, '') NOT IN ({string.Join(",", closing)})
             ORDER BY m.created_at DESC, m.id DESC
            """, [.. closing.Select((n, i) => (n, (object?)MandateEventKind.Closing[i]))]);
        using var r = c.ExecuteReader();
        var rows = new List<OpenMandate>();
        while (r.Read())
            rows.Add(new OpenMandate(ReadMandate(r), Sql.S(r.GetValue(15)),
                new MandateSpend((decimal)r.GetDouble(16), (decimal)r.GetDouble(17), Convert.ToInt32(r.GetValue(18), CultureInfo.InvariantCulture),
                    Convert.ToInt32(r.GetValue(19), CultureInfo.InvariantCulture)),
                Convert.ToInt32(r.GetValue(20), CultureInfo.InvariantCulture)));
        return (IReadOnlyList<OpenMandate>)rows;
    });

    /// <summary>
    /// THE DECISIONS RECORDED AT OR AFTER <paramref name="at"/>, ACROSS EVERY POSITION, newest first and at most
    /// <paramref name="limit"/>, each with its rationale (the position's claim) and the forecasts on it with the app's resolutions —
    /// one snapshot. A decision whose stored claims cannot be read is refused by its id, never thrown through the list.
    /// </summary>
    public OrgList<RecentDecision> DecisionsSince(DateTimeOffset at, int limit) => db.Read(_ =>
    {
        var cols = string.Join(", ", DecisionCols.Split(", ").Select(x => "d." + x));
        using var c = db.Cmd($"""
            SELECT {cols}, (SELECT lr.text FROM ledger_revision lr WHERE lr.entry_id = d.ledger_entry_id AND lr.revision = 1)
              FROM org_decision d WHERE d.at >= $at ORDER BY d.at DESC, d.rowid DESC LIMIT $limit
            """, ("$at", Sql.T(at)), ("$limit", Math.Max(0, limit)));
        using var r = c.ExecuteReader();
        var decisions = new List<(DecisionRow Row, string? Rationale)>();
        var refused = new List<string>();
        while (r.Read())
        {
            var id = r.GetString(0);
            try { decisions.Add((ReadDecision(r), Sql.S(r.GetValue(9)))); }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or FormatException)
            {
                refused.Add($"decision {id} cannot be read as TradeAgent writes one: it is not shown.");
            }
        }
        r.Close();

        var forecasts = new Dictionary<string, List<RecentForecast>>(StringComparer.Ordinal);
        if (decisions.Count > 0)
        {
            var names = decisions.Select((_, i) => $"$d{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
            var fcols = string.Join(", ", ForecastCols.Split(", ").Select(x => "f." + x));
            using var f = db.Cmd($"""
                SELECT {fcols}, x.outcome, x.record_kind, x.record_id, x.why, x.at
                  FROM org_forecast f LEFT JOIN org_forecast_resolution x ON x.forecast_id = f.id
                 WHERE f.decision_id IN ({string.Join(",", names)}) ORDER BY f.at, f.rowid
                """, [.. names.Select((n, i) => (n, (object?)decisions[i].Row.Id))]);
            using var fr = f.ExecuteReader();
            while (fr.Read())
            {
                var fid = fr.GetString(0);
                if (Probability(fr) is not { } probability) { refused.Add(Unreadable(fid)); continue; }
                var resolution = fr.IsDBNull(14)
                    ? null
                    : new ResolutionRow(fid, fr.GetString(14), fr.GetString(15), fr.GetString(16), fr.GetString(17), Sql.Time(fr.GetValue(18)));
                var forecast = ReadForecast(fr, probability);
                if (!forecasts.TryGetValue(forecast.DecisionId!, out var list)) forecasts[forecast.DecisionId!] = list = [];
                list.Add(new RecentForecast(forecast, resolution));
            }
        }

        return new OrgList<RecentDecision>(
            [.. decisions.Select(d => new RecentDecision(d.Row, d.Rationale,
                forecasts.TryGetValue(d.Row.Id, out var list) ? list : []))],
            refused);
    });

    /// <summary>
    /// THE ESCALATIONS ADDRESSED TO THE OWNER (<c>to_position IS NULL</c>: a record with no wake), newest first. Read only — its page is
    /// <c>U-org-surface-b</c>'s. A row whose skipped heads cannot be read is refused by its id.
    /// </summary>
    public OrgList<EscalationRow> EscalationsToOwner() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {EscalationCols} FROM org_escalation WHERE to_position IS NULL ORDER BY at DESC, rowid DESC");
        using var r = c.ExecuteReader();
        var rows = new List<EscalationRow>();
        var refused = new List<string>();
        while (r.Read())
        {
            var id = r.GetString(0);
            try { rows.Add(ReadEscalation(r)); }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or FormatException)
            {
                refused.Add($"escalation {id} cannot be read as TradeAgent writes one: it is not shown.");
            }
        }
        return new OrgList<EscalationRow>(rows, refused);
    });
}
