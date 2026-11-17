namespace TradeAgent.Core.Db;

/// <summary>
/// One AI turn as the floor shows it: who took it, when it opened, and — once it has — when and how it ended.
/// </summary>
/// <param name="Id">The launch's id (<c>ai_attempt.id</c>).</param>
/// <param name="Role">Whose turn: a position id, a board seat id (<c>board-N</c>) or an app principal.</param>
/// <param name="State"><c>LAUNCHED</c>, <c>ENDED</c> or <c>LOST</c>.</param>
/// <param name="Cost">What the turn cost, or null while it is open or where nothing priced it. Unknown is never zero.</param>
public sealed record FloorTurn(string Id, string Role, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string State, int? ExitCode,
    decimal? Cost, string? Mandate, string? Model);

/// <summary>The kinds of thing the floor shows happening. Each is one row some ledger wrote; none is inferred.</summary>
public static class FloorEventKind
{
    /// <summary>A turn opened. Actor: whose. Attempt: the launch. Mandate: the work order it runs for, if any. Detail: the model asked for.</summary>
    public const string TurnStart = "turn-start";

    /// <summary>A turn ended. Ok: it ended and exited 0. Took: how long it ran. Amount: its cost, null where unknown. Detail: <c>ENDED</c> or <c>LOST</c>.</summary>
    public const string TurnEnd = "turn-end";

    /// <summary>A tool call answered. Detail: the tool. Text: its argument. Ok: served. Ref: the refusal, if refused. Took: how long it ran.</summary>
    public const string Tool = "tool";

    /// <summary>
    /// A mandate event: Detail is the event's own kind (<c>issued</c>, <c>delivered</c>, <c>accepted</c>, <c>rejected</c>, <c>retry</c>,
    /// <c>escalated</c>, <c>declined</c>, <c>cancelled</c>, <c>expired</c>, <c>rescoped</c>, <c>proposed</c>). Actor: who wrote the event.
    /// Target: the other party to the mandate (the holder when the decider acts, the decider when the holder does). Text: for
    /// <c>issued</c> the objective, for <c>delivered</c> the report, else the event's reason. Ref: the mandate's holder, whoever the actor is.
    /// </summary>
    public const string Mandate = "mandate";

    /// <summary>A recorded decision. Detail: its kind. Text: the choice.</summary>
    public const string Decision = "decision";

    /// <summary>An escalation. Target: who it went to, or <see cref="FloorEvent.Owner"/>. Text: the reason.</summary>
    public const string Escalation = "escalation";

    /// <summary>Words from one position to a peer. Target: the peer. Text: the words.</summary>
    public const string Lateral = "lateral";

    /// <summary>A change to the chart (<c>org_event</c>). Detail: its kind (chartered, hired, activated, retired, …). Actor: who decided. Unit and Target (the position) as recorded. Text: the app's own detail line.</summary>
    public const string Chart = "chart";

    /// <summary>A security-board act's event. Actor: the seat (or <c>app</c> / <c>owner</c>). Detail: the event's kind (proposed, applied, confirmed, refused, expired, stale, vetoed). Ref: the act. Text: the act's own kind (narrow / propose).</summary>
    public const string Board = "board";

    /// <summary>A backtest run was recorded. Actor: the role that asked. Detail: its outcome. Count: its trades. Ref: the run.</summary>
    public const string Backtest = "backtest";

    /// <summary>A final judgement was asked of the referee. Actor: the role that asked, where the row says. Ref: the version.</summary>
    public const string VerdictAsked = "verdict-asked";

    /// <summary>The referee gave a verdict. Actor: <see cref="FloorEvent.Referee"/>. Detail: the verdict as recorded. Text: its reason. Ref: the version.</summary>
    public const string Verdict = "verdict";

    /// <summary>A forward paper run started. Actor: <see cref="FloorEvent.App"/>. Detail: the symbol. Ref: the deployment.</summary>
    public const string PaperRun = "paper-run";

    /// <summary>A fill was recorded. Actor: <see cref="FloorEvent.Venue"/>. Detail: the side. Text: the symbol. Amount: the price. Quantity: the size.</summary>
    public const string Fill = "fill";

    /// <summary>The owner's words reached the queue. Actor: <see cref="FloorEvent.Owner"/>. Target: the role they are for.</summary>
    public const string OwnerWords = "owner-words";
}

/// <summary>
/// ONE THING THAT HAPPENED, as the floor shows it: when, what kind, who did it and to whom. A reading of a ledger row and nothing more —
/// <see cref="Text"/> is whatever a model or the owner wrote and is shown quoted; every other field is the app's own.
/// </summary>
public sealed record FloorEvent(DateTimeOffset At, string Kind, string Actor)
{
    /// <summary>The owner, as an actor or a target.</summary>
    public const string Owner = "owner";
    /// <summary>The app itself, where a row records it as the one that acted.</summary>
    public const string App = "app";
    /// <summary>The referee: the app's code that judges a strategy.</summary>
    public const string Referee = "referee";
    /// <summary>The venue an order went to.</summary>
    public const string Venue = "venue";

    public string? Target { get; init; }
    public string? Unit { get; init; }
    public string? Mandate { get; init; }
    public string? Attempt { get; init; }
    public string? Text { get; init; }
    public string? Detail { get; init; }
    public string? Ref { get; init; }
    public bool? Ok { get; init; }
    public TimeSpan? Took { get; init; }
    public decimal? Amount { get; init; }
    public decimal? Quantity { get; init; }
    public int? Count { get; init; }
}

/// <summary>
/// WHAT THE FLOOR READS (<c>U-ui-alive</c>): the turns open in a window and what happened in it, in order, from the ledgers as they stand.
/// Reads only — it writes nothing and no agent reaches it; it is the owner's own picture, in his process.
/// </summary>
public sealed class FloorStore(Database db)
{
    /// <summary>
    /// How far before a window's start a turn that is still in it may have opened: the scan is bounded by the <c>started_at</c> index, and
    /// a turn older than this is not a turn the floor shows.
    /// </summary>
    static readonly TimeSpan Lookback = TimeSpan.FromDays(2);

    /// <summary>Every turn open at some moment of [<paramref name="from"/>, <paramref name="to"/>], oldest first.</summary>
    public IReadOnlyList<FloorTurn> Turns(DateTimeOffset from, DateTimeOffset to) => db.Read(_ =>
    {
        var turns = new List<FloorTurn>();
        using var c = db.Cmd("""
            SELECT id, role, started_at, ended_at, state, exit_code, cost, mandate_id, COALESCE(NULLIF(effective_model, ''), requested_model)
              FROM ai_attempt
             WHERE started_at >= $back AND started_at <= $to AND (ended_at IS NULL OR ended_at >= $from)
               AND role IS NOT NULL AND role <> ''
             ORDER BY started_at, rowid
            """, ("$back", Sql.T(from - Lookback)), ("$from", Sql.T(from)), ("$to", Sql.T(to)));
        using var r = c.ExecuteReader();
        while (r.Read())
            turns.Add(new FloorTurn(r.GetString(0), r.GetString(1), Sql.Time(r.GetString(2)), Sql.TimeN(r.GetValue(3)), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt32(5), Sql.DecN(r.GetValue(6)), Sql.S(r.GetValue(7)), Sql.S(r.GetValue(8))));
        return turns;
    });

    /// <summary>
    /// What happened after <paramref name="from"/> and up to <paramref name="to"/>, oldest first, at most <paramref name="limit"/> — the
    /// newest when there are more. <paramref name="tools"/> false leaves the tool calls out: there are a hundred of them to every other
    /// thing that happens, and a reader that wants the day's story in a few hundred rows would otherwise be handed its last half hour.
    /// </summary>
    public IReadOnlyList<FloorEvent> Events(DateTimeOffset from, DateTimeOffset to, int limit = 4000, bool tools = true)
    {
        if (limit <= 0 || to <= from) return [];
        return db.Read(_ =>
        {
            // ONE SMALL QUERY PER SOURCE, each the newest `limit` of its own rows (the newest `limit` overall can hold no more than that of
            // any one source), merged here: by time, then the source's place in this list, then the source's own order.
            var all = new List<(FloorEvent Event, int Source, int Seq)>();
            var f = Sql.T(from);
            var t = Sql.T(to);
            var back = Sql.T(from - Lookback);
            var source = 0;

            void Add(string sql, Func<Microsoft.Data.Sqlite.SqliteDataReader, FloorEvent?> map)
            {
                var rows = new List<FloorEvent>();
                using (var c = db.Cmd(sql, ("$f", f), ("$t", t), ("$back", back), ("$n", limit), ("$chair", CouncilRoles.Default)))
                using (var r = c.ExecuteReader())
                    while (r.Read())
                        if (map(r) is { } e) rows.Add(e);
                // Each query reads newest first; its own order, oldest first, is the reverse.
                rows.Reverse();
                for (var i = 0; i < rows.Count; i++) all.Add((rows[i], source, i));
                source++;
            }

            // 1. A turn opened.
            Add("""
                SELECT started_at, role, id, mandate_id, requested_model FROM ai_attempt
                 WHERE started_at > $f AND started_at <= $t AND role IS NOT NULL AND role <> ''
                 ORDER BY started_at DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.TurnStart, r.GetString(1))
                {
                    Attempt = r.GetString(2), Mandate = Sql.S(r.GetValue(3)), Detail = Sql.S(r.GetValue(4))
                });

            // 2. A turn ended.
            Add("""
                SELECT ended_at, role, id, mandate_id, state, exit_code, started_at, cost FROM ai_attempt
                 WHERE started_at >= $back AND ended_at > $f AND ended_at <= $t AND role IS NOT NULL AND role <> ''
                 ORDER BY ended_at DESC, rowid DESC LIMIT $n
                """,
                r =>
                {
                    var ended = Sql.Time(r.GetString(0));
                    var state = r.GetString(4);
                    return new FloorEvent(ended, FloorEventKind.TurnEnd, r.GetString(1))
                    {
                        Attempt = r.GetString(2), Mandate = Sql.S(r.GetValue(3)), Detail = state,
                        Ok = state == nameof(AiAttemptState.ENDED) && !r.IsDBNull(5) && r.GetInt64(5) == 0,
                        Took = ended - Sql.Time(r.GetString(6)), Amount = Sql.DecN(r.GetValue(7))
                    };
                });

            // 3. A tool call answered.
            if (!tools) source++;
            else Add("""
                SELECT at, role, attempt, tool, argument, served, refusal, duration_ms FROM tool_call
                 WHERE at > $f AND at <= $t
                 ORDER BY at DESC, id DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Tool, Sql.S(r.GetValue(1)) ?? FloorEvent.App)
                {
                    Attempt = Sql.S(r.GetValue(2)), Detail = r.GetString(3), Text = Sql.S(r.GetValue(4)), Ok = r.GetInt64(5) != 0,
                    Ref = Sql.S(r.GetValue(6)), Took = r.IsDBNull(7) ? null : TimeSpan.FromMilliseconds(r.GetInt64(7))
                });

            // 4. A mandate's event, with the other party and — for an issue or a delivery — what was asked or handed back.
            Add("""
                SELECT e.at, e.actor, m.holder, m.decider, m.unit_id, m.id, e.attempt_id, e.kind,
                       CASE e.kind
                         WHEN 'issued' THEN m.objective
                         WHEN 'delivered' THEN (SELECT d.report FROM org_delivery d
                                                 WHERE d.mandate_id = m.id AND d.at <= e.at
                                                 ORDER BY d.at DESC, d.rowid DESC LIMIT 1)
                         ELSE e.reason END
                  FROM org_mandate_event e JOIN org_mandate m ON m.id = e.mandate_id
                 WHERE e.at > $f AND e.at <= $t
                 ORDER BY e.at DESC, e.id DESC LIMIT $n
                """,
                r =>
                {
                    var actor = r.GetString(1);
                    var kind = r.GetString(7);
                    return new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Mandate, actor)
                    {
                        Target = actor == r.GetString(2) ? r.GetString(3) : r.GetString(2), Ref = r.GetString(2),
                        Unit = r.GetString(4), Mandate = r.GetString(5), Attempt = Sql.S(r.GetValue(6)), Detail = kind,
                        Text = Sql.S(r.GetValue(8)),
                        Ok = kind == MandateEventKind.Accepted ? true : kind == MandateEventKind.Rejected ? false : null
                    };
                });

            // 5. A recorded decision.
            Add("""
                SELECT at, position, attempt_id, kind, choice, id FROM org_decision
                 WHERE at > $f AND at <= $t
                 ORDER BY at DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Decision, r.GetString(1))
                {
                    Attempt = Sql.S(r.GetValue(2)), Detail = r.GetString(3), Text = r.GetString(4), Ref = r.GetString(5)
                });

            // 6. An escalation: to a position, or — where none is named — to the owner.
            Add("""
                SELECT at, sender, to_position, unit_id, mandate_id, attempt_id, reason, id FROM org_escalation
                 WHERE at > $f AND at <= $t
                 ORDER BY at DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Escalation, r.GetString(1))
                {
                    Target = Sql.S(r.GetValue(2)) ?? FloorEvent.Owner, Unit = r.GetString(3), Mandate = Sql.S(r.GetValue(4)),
                    Attempt = Sql.S(r.GetValue(5)), Text = r.GetString(6), Ref = r.GetString(7)
                });

            // 7. Words to a peer.
            Add("""
                SELECT at, sender, to_position, attempt_id, text, id FROM org_lateral
                 WHERE at > $f AND at <= $t
                 ORDER BY at DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Lateral, r.GetString(1))
                {
                    Target = r.GetString(2), Attempt = Sql.S(r.GetValue(3)), Text = r.GetString(4), Ref = r.GetString(5)
                });

            // 8. A change to the chart.
            Add("""
                SELECT at, decider, position_id, unit_id, kind, detail FROM org_event
                 WHERE at > $f AND at <= $t
                 ORDER BY at DESC, id DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Chart,
                    Sql.S(r.GetValue(1)) is { Length: > 0 } decider ? decider : FloorEvent.App)
                {
                    Target = Sql.S(r.GetValue(2)) is { Length: > 0 } position ? position : null, Unit = r.GetString(3), Detail = r.GetString(4), Text = r.GetString(5)
                });

            // 9. A security-board act's event.
            Add("""
                SELECT e.at, e.seat, e.attempt_id, e.kind, a.id, a.kind
                  FROM board_act_event e JOIN board_act a ON a.id = e.act_id
                 WHERE e.at > $f AND e.at <= $t
                 ORDER BY e.at DESC, e.id DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Board, r.GetString(1))
                {
                    Attempt = Sql.S(r.GetValue(2)), Detail = r.GetString(3), Ref = r.GetString(4), Text = r.GetString(5)
                });

            // 10. A backtest run some role asked for — never the referee's own holdout run.
            Add("""
                SELECT created_at, role, attempt, outcome, trades, id FROM strategy_run
                 WHERE created_at > $f AND created_at <= $t AND role IS NOT NULL AND role <> ''
                   AND id NOT IN (SELECT holdout_run_id FROM strategy_promotion WHERE holdout_run_id IS NOT NULL)
                 ORDER BY created_at DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Backtest, r.GetString(1))
                {
                    Attempt = Sql.S(r.GetValue(2)), Detail = r.GetString(3), Count = r.GetInt32(4), Ref = r.GetString(5)
                });

            // 11. A final judgement asked of the referee, by the role whose launch asked where the row names one.
            Add("""
                SELECT v.requested_at, a.role, v.attempt_id, v.version_id
                  FROM strategy_verdict v LEFT JOIN ai_attempt a ON a.id = v.attempt_id
                 WHERE v.requested_at > $f AND v.requested_at <= $t
                 ORDER BY v.requested_at DESC, v.rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.VerdictAsked,
                    Sql.S(r.GetValue(1)) is { Length: > 0 } role ? role : FloorEvent.App)
                {
                    Attempt = Sql.S(r.GetValue(2)), Ref = r.GetString(3)
                });

            // 12. The referee's verdict.
            Add("""
                SELECT at, verdict, reason, version_id FROM strategy_promotion
                 WHERE at > $f AND at <= $t
                 ORDER BY at DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Verdict, FloorEvent.Referee)
                {
                    Detail = r.GetString(1), Text = r.GetString(2), Ref = r.GetString(3)
                });

            // 13. A forward paper run started.
            Add("""
                SELECT started_at, symbol, id FROM strategy_deployment
                 WHERE started_at > $f AND started_at <= $t AND mode = 'PAPER'
                 ORDER BY started_at DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.PaperRun, FloorEvent.App)
                {
                    Detail = r.GetString(1), Ref = r.GetString(2)
                });

            // 14. A fill, at the moment the app recorded it.
            Add("""
                SELECT COALESCE(recorded_at, at) AS t, side, symbol, price, quantity, execution_id FROM fill
                 WHERE COALESCE(recorded_at, at) > $f AND COALESCE(recorded_at, at) <= $t
                 ORDER BY t DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.Fill, FloorEvent.Venue)
                {
                    Detail = r.GetString(1), Text = r.GetString(2), Amount = Sql.DecN(r.GetValue(3)), Quantity = Sql.DecN(r.GetValue(4)),
                    Ref = r.GetString(5)
                });

            // 15. The owner's words reached the queue — that they did and for whom, never what they say.
            Add($"""
                SELECT created_at, COALESCE(role, $chair) FROM mission_event
                 WHERE kind = '{MissionEventKind.Owner}' AND created_at > $f AND created_at <= $t
                 ORDER BY created_at DESC, rowid DESC LIMIT $n
                """,
                r => new FloorEvent(Sql.Time(r.GetString(0)), FloorEventKind.OwnerWords, FloorEvent.Owner) { Target = r.GetString(1) });

            var merged = all.OrderBy(x => x.Event.At).ThenBy(x => x.Source).ThenBy(x => x.Seq).Select(x => x.Event).ToList();
            return merged.Count > limit ? merged.GetRange(merged.Count - limit, limit) : merged;
        });
    }

    /// <summary>When each unit and each position of the chart first existed (their created_at), by id — so a replay can show the chart as it stood.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Founded() => db.Read(_ =>
    {
        var founded = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var c = db.Cmd("SELECT id, created_at FROM org_unit UNION ALL SELECT id, created_at FROM org_position");
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var at = Sql.Time(r.GetString(1));
            // Units and positions have different id shapes; were one ever shared, the earlier moment is when the id first existed.
            if (!founded.TryGetValue(r.GetString(0), out var seen) || at < seen) founded[r.GetString(0)] = at;
        }
        return (IReadOnlyDictionary<string, DateTimeOffset>)founded;
    });

    /// <summary>The first and the last moment anything the floor shows was recorded, or nulls in a home where nothing has happened.</summary>
    public (DateTimeOffset? First, DateTimeOffset? Last) Span() => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT MIN(lo), MAX(hi) FROM (
              SELECT MIN(started_at) AS lo, MAX(started_at) AS hi FROM ai_attempt
              UNION ALL SELECT MIN(at), MAX(at) FROM org_event
              UNION ALL SELECT MIN(at), MAX(at) FROM org_mandate_event
              UNION ALL SELECT MIN(at), MAX(at) FROM tool_call)
            """);
        using var r = c.ExecuteReader();
        return r.Read() ? (Sql.TimeN(r.GetValue(0)), Sql.TimeN(r.GetValue(1))) : ((DateTimeOffset?)null, (DateTimeOffset?)null);
    });
}
