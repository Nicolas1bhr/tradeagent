using System.Globalization;
using System.Text.Json;

namespace TradeAgent.Core.Db;

/// <summary>
/// One read by position (<c>U-org-packets-b</c>): the rows the app could read, and — in words, naming the row and never its text —
/// each one it could not. A malformed row is refused here and never thrown through the list, so one row nothing should have written
/// stops no other record from reaching the packet.
/// </summary>
public sealed record OrgList<T>(IReadOnlyList<T> Rows, IReadOnlyList<string> Refused)
{
    public static OrgList<T> Empty { get; } = new([], []);
}

/// <summary>A position's own forecast and the app's resolution of it.</summary>
public sealed record ResolvedForecast(ForecastRow Forecast, ResolutionRow Resolution);

/// <summary>A decider's open mandate with a sealed delivery to judge: the row, its latest delivery and the app's checks on it.</summary>
public sealed record DeliveryToJudge(MandateRow Mandate, DeliveryRow Delivery, IReadOnlyList<CheckRow> Checks);

/// <summary>
/// THE READS A POSITION'S PACKET IS BUILT FROM (<c>U-org-packets-b</c>; ORCHESTRATION §§ 2.6, 2.10, 2.11): each one by position —
/// the position's own forecasts, resolutions and escalations, notes TO it — and never another's. "Since the last packet" is read off
/// <c>ai_attempt_input</c>: a record with a lineage row on an attempt OF THIS POSITION was shown to a launch whose outcome committed,
/// and a record with none was not — a launch lost, or refused by the gate at its last check, wrote none, so its records come again.
/// Reads only; nothing here writes.
/// </summary>
public sealed partial class OrgStore
{
    /// <summary>
    /// THE NOTES SENT TO <paramref name="position"/> THAT NO COMMITTED LAUNCH OF IT WAS SHOWN, oldest first — never one it sent, never
    /// one addressed to another position. A note whose refs cannot be read is refused by its id.
    /// </summary>
    public OrgList<LateralRow> LateralsUnshown(string position) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {LateralCols} FROM org_lateral l
             WHERE l.to_position = $p AND l.sender <> $p
               AND NOT EXISTS (SELECT 1 FROM ai_attempt_input i JOIN ai_attempt a ON a.id = i.attempt_id
                                WHERE i.kind = $kind AND i.ref_id = l.id AND a.role = $p)
             ORDER BY l.at, l.rowid
            """, ("$p", position), ("$kind", PacketInputKind.Lateral));
        using var r = c.ExecuteReader();
        var rows = new List<LateralRow>();
        var refused = new List<string>();
        while (r.Read())
        {
            var id = r.GetString(0);
            try
            {
                rows.Add(new LateralRow(id, r.GetString(1), Sql.S(r.GetValue(2)), r.GetString(3), r.GetString(4),
                    ReadRefs(r.GetString(5)), Sql.Time(r.GetValue(6))));
            }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or FormatException)
            {
                refused.Add($"note {id} cannot be read as TradeAgent writes a note: it is not shown, and `trade lateral list` lists it.");
            }
        }
        return new OrgList<LateralRow>(rows, refused);
    });

    /// <summary>
    /// <paramref name="position"/>'S OWN OPEN FORECASTS DUE BY <paramref name="by"/>, soonest first — never another forecaster's. A row
    /// whose probability is not the one shape the writer stores is refused by its id (<c>U-forecast-nul</c>'s rule), never parsed.
    /// </summary>
    public OrgList<ForecastRow> ForecastsDue(string position, DateTimeOffset by) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {ForecastCols} FROM org_forecast f
             WHERE f.forecaster = $p AND f.resolve_by <= $by
               AND NOT EXISTS (SELECT 1 FROM org_forecast_resolution r WHERE r.forecast_id = f.id)
             ORDER BY f.resolve_by, f.rowid
            """, ("$p", position), ("$by", Sql.T(by)));
        return Forecasts(c);
    });

    /// <summary>
    /// <paramref name="position"/>'S OWN FORECASTS THE APP HAS RESOLVED AND NO COMMITTED LAUNCH OF IT WAS SHOWN, in the order they
    /// resolved — the outcome and the record it rests on are the app's; the forecast is the position's own.
    /// </summary>
    public OrgList<ResolvedForecast> ResolutionsUnshown(string position) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {string.Join(", ", ForecastCols.Split(", ").Select(col => "f." + col))},
                   r.outcome, r.record_kind, r.record_id, r.why, r.at
              FROM org_forecast f JOIN org_forecast_resolution r ON r.forecast_id = f.id
             WHERE f.forecaster = $p
               AND NOT EXISTS (SELECT 1 FROM ai_attempt_input i JOIN ai_attempt a ON a.id = i.attempt_id
                                WHERE i.kind = $kind AND i.ref_id = f.id AND a.role = $p)
             ORDER BY r.at, r.rowid
            """, ("$p", position), ("$kind", PacketInputKind.Resolution));
        var rows = new List<ResolvedForecast>();
        var refused = new List<string>();
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                if (Probability(r) is { } probability)
                    rows.Add(new ResolvedForecast(ReadForecast(r, probability), new ResolutionRow(r.GetString(0), r.GetString(14),
                        r.GetString(15), r.GetString(16), r.GetString(17), Sql.Time(r.GetValue(18)))));
                else refused.Add(Unreadable(r.GetString(0)));
            }
        return new OrgList<ResolvedForecast>(rows, refused);
    });

    /// <summary>The forecasts a command reads, each refused by id when its probability is not the writer's shape.</summary>
    static OrgList<ForecastRow> Forecasts(Microsoft.Data.Sqlite.SqliteCommand c)
    {
        using var r = c.ExecuteReader();
        var rows = new List<ForecastRow>();
        var refused = new List<string>();
        while (r.Read())
        {
            if (Probability(r) is { } probability) rows.Add(ReadForecast(r, probability));
            else refused.Add(Unreadable(r.GetString(0)));
        }
        return new OrgList<ForecastRow>(rows, refused);
    }

    /// <summary>A forecast row's probability, read as TEXT and parsed only when it is the one shape the writer stores; else null.</summary>
    static decimal? Probability(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        r.GetValue(10) is string text && ForecastVocabulary.IsProbabilityText(text)
        && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var probability)
            ? probability : null;

    static string Unreadable(string forecastId) =>
        $"forecast {forecastId} holds a probability that is not a decimal text from 0 to 1 (written past the app's writer): it is not "
        + "shown, and never guessed.";

    /// <summary>
    /// THE OPEN ESCALATIONS <paramref name="position"/> RAISED — or, with <paramref name="received"/>, the ones addressed to it — newest
    /// first, never another position's. Open: on no mandate, or on a mandate no closing event (accepted, cancelled, expired) has ended.
    /// A row whose skipped heads cannot be read is refused by its id.
    /// </summary>
    public OrgList<EscalationRow> EscalationsOpen(string position, bool received) => db.Read(_ =>
    {
        // EVERY CLOSING EVENT, from the one list (a re-scope closes too, U-org-verbs-a).
        var closing = MandateEventKind.Closing.Select((_, i) => $"$c{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        using var c = db.Cmd($"""
            SELECT {EscalationCols} FROM org_escalation x
             WHERE {(received ? "x.to_position" : "x.sender")} = $p
               AND (x.mandate_id IS NULL OR NOT EXISTS (SELECT 1 FROM org_mandate_event e
                                                        WHERE e.mandate_id = x.mandate_id AND e.kind IN ({string.Join(",", closing)})))
             ORDER BY x.at DESC, x.rowid DESC
            """, [("$p", (object?)position), .. closing.Select((n, i) => (n, (object?)MandateEventKind.Closing[i]))]);
        using var r = c.ExecuteReader();
        var rows = new List<EscalationRow>();
        var refused = new List<string>();
        while (r.Read())
        {
            var id = r.GetString(0);
            try { rows.Add(ReadEscalation(r)); }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or FormatException)
            {
                refused.Add($"escalation {id} cannot be read as TradeAgent writes one: it is not shown, and `trade escalation list` lists it.");
            }
        }
        return new OrgList<EscalationRow>(rows, refused);
    });

    /// <summary>
    /// THE DELIVERIES <paramref name="decider"/> HAS TO JUDGE, oldest mandate first: each open mandate it decides whose latest event is
    /// a delivery, with that delivery and the app's checks on it. A delivery whose stored claims cannot be read is refused by its
    /// mandate's id.
    /// </summary>
    public OrgList<DeliveryToJudge> DeliveriesToJudgeOf(string decider) => db.Read(_ =>
    {
        var rows = new List<DeliveryToJudge>();
        var refused = new List<string>();
        foreach (var id in DeliveriesToJudge(decider))
        {
            try
            {
                if (Mandate(id) is { } m && Deliveries(id).LastOrDefault() is { } d)
                    rows.Add(new DeliveryToJudge(m, d, Checks(d.Id)));
            }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or FormatException)
            {
                refused.Add($"the delivery on mandate {id} cannot be read as TradeAgent seals one: it is not shown, and "
                            + $"`trade mandate show {id}` reads it.");
            }
        }
        return new OrgList<DeliveryToJudge>(rows, refused);
    });
}
