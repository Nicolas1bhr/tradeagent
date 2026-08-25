using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

static class Sql
{
    public static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    public static decimal Dec(object? o) => o is null or DBNull ? 0m : decimal.Parse(Convert.ToString(o, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
    public static decimal? DecN(object? o) => o is null or DBNull ? null : Dec(o);
    public static string T(DateTimeOffset d) => d.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset Time(object? o) => DateTimeOffset.Parse((string)o!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    public static DateTimeOffset? TimeN(object? o) => o is null or DBNull ? null : Time(o);
    public static string? S(object? o) => o is null or DBNull ? null : (string)o;
}

/// <summary>
/// The write-ahead record of every mutating intent, and the only writer of order state.
///
/// Idempotency lives here, not in the caller: <see cref="TryCreate"/> is the single point where a
/// duplicate request_id is collapsed onto the existing record. If this returns created=false, the
/// gateway must not dispatch again — that is the whole duplicate-order defence.
/// </summary>
public sealed class ExecutionRequestStore(Database db)
{
    const string Cols = """
        request_id, agent_session_id, connector_id, account_id, instrument, intent, parameters,
        client_order_id, created_at, dispatched_at, execution_state, connector_order_id,
        filled_quantity, average_price, needs_reconciliation, last_reconciled_at, last_error, mode
        """;

    public (bool Created, ExecutionRequest Request) TryCreate(ExecutionRequest r)
    {
        var rows = db.Write(_ =>
        {
            using var c = db.Cmd($"""
                INSERT INTO execution_request({Cols}, updated_at)
                VALUES($rid,$sess,$conn,$acct,$inst,$intent,$params,$coid,$created,NULL,$state,NULL,'0',NULL,0,NULL,NULL,$mode,$upd)
                ON CONFLICT(request_id) DO NOTHING
                """,
                ("$rid", r.RequestId), ("$sess", r.AgentSessionId), ("$conn", r.ConnectorId), ("$acct", r.AccountId),
                ("$inst", r.Instrument), ("$intent", r.Intent.ToString()), ("$params", r.ParametersJson),
                ("$coid", r.ClientOrderId), ("$created", Sql.T(r.CreatedAt)), ("$state", r.State.ToString()),
                ("$mode", r.Mode.ToString()), ("$upd", Sql.T(DateTimeOffset.UtcNow)));
            return c.ExecuteNonQuery();
        });

        var stored = Get(r.RequestId) ?? throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT, "request vanished after insert");
        return (rows == 1, stored);
    }

    public ExecutionRequest? Get(string requestId) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM execution_request WHERE request_id=$r", ("$r", requestId));
        using var rd = c.ExecuteReader();
        return rd.Read() ? Map(rd) : null;
    });

    public ExecutionRequest? GetByClientOrderId(string clientOrderId) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM execution_request WHERE client_order_id=$c", ("$c", clientOrderId));
        using var rd = c.ExecuteReader();
        return rd.Read() ? Map(rd) : null;
    });

    public List<ExecutionRequest> Query(string where = "1=1", params (string, object?)[] ps) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM execution_request WHERE {where} ORDER BY created_at", ps);
        using var rd = c.ExecuteReader();
        var list = new List<ExecutionRequest>();
        while (rd.Read()) list.Add(Map(rd));
        return list;
    });

    public List<ExecutionRequest> NeedingReconciliation() => Query("needs_reconciliation=1");

    public List<ExecutionRequest> Open() =>
        Query("execution_state IN ('DISPATCHING','ACKNOWLEDGED','WORKING','PARTIALLY_FILLED','CANCEL_PENDING','UNKNOWN','RECONCILING')");

    /// <summary>
    /// Guarded state change. Refuses illegal transitions (see <see cref="OrderStateMachine"/>) and
    /// refuses to move a record whose stored state is not what the caller believed — that CAS check
    /// is what stops two racing dispatchers from both thinking they own the order.
    /// </summary>
    public ExecutionRequest Transition(string requestId, ExecutionState expectedFrom, ExecutionState to,
        string? connectorOrderId = null, decimal? filled = null, decimal? avgPrice = null,
        bool? needsReconciliation = null, string? error = null, bool markReconciled = false)
    {
        OrderStateMachine.Require(expectedFrom, to);
        var updated = db.Write(_ =>
        {
            using var c = db.Cmd("""
                UPDATE execution_request SET
                  execution_state=$to,
                  connector_order_id=COALESCE($coid, connector_order_id),
                  filled_quantity=COALESCE($fill, filled_quantity),
                  average_price=COALESCE($avg, average_price),
                  needs_reconciliation=COALESCE($nr, needs_reconciliation),
                  last_error=COALESCE($err, last_error),
                  last_reconciled_at=CASE WHEN $mr=1 THEN $now ELSE last_reconciled_at END,
                  dispatched_at=CASE WHEN $to='DISPATCHING' THEN $now ELSE dispatched_at END,
                  updated_at=$now
                WHERE request_id=$rid AND execution_state=$from
                """,
                ("$to", to.ToString()), ("$from", expectedFrom.ToString()), ("$rid", requestId),
                ("$coid", connectorOrderId), ("$fill", filled is null ? null : Sql.D(filled.Value)),
                ("$avg", avgPrice is null ? null : Sql.D(avgPrice.Value)),
                ("$nr", needsReconciliation is null ? null : (needsReconciliation.Value ? 1 : 0)),
                ("$err", error), ("$mr", markReconciled ? 1 : 0), ("$now", Sql.T(DateTimeOffset.UtcNow)));
            return c.ExecuteNonQuery();
        });

        if (updated != 1)
        {
            var actual = Get(requestId);
            throw new TradeAgentException(ErrorCode.ILLEGAL_STATE_TRANSITION,
                $"expected {requestId} in {expectedFrom} but found {actual?.State.ToString() ?? "missing"}");
        }
        return Get(requestId)!;
    }

    /// <summary>
    /// Flags a request for reconciliation WITHOUT changing its state. Used when a dispatch failed
    /// indefinitely but the event stream had already recorded an outcome: we do not overwrite what
    /// the stream saw, but we still refuse to trust it until the reconciler has confirmed it.
    /// </summary>
    public ExecutionRequest MarkNeedsReconciliation(string requestId, string? error = null)
    {
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                UPDATE execution_request
                SET needs_reconciliation=1, last_error=COALESCE($err, last_error), updated_at=$now
                WHERE request_id=$rid
                """, ("$rid", requestId), ("$err", error), ("$now", Sql.T(DateTimeOffset.UtcNow)));
            return c.ExecuteNonQuery();
        });
        return Get(requestId) ?? throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT, "request vanished");
    }

    /// <summary>
    /// Marks a request confirmed without changing its state. Used when the event stream already
    /// moved a record to the very state reconciliation was about to write: the fact is settled, so
    /// the flag must clear rather than the request staying paused forever.
    /// </summary>
    public ExecutionRequest ClearReconciliation(string requestId)
    {
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                UPDATE execution_request
                SET needs_reconciliation=0, last_reconciled_at=$now, updated_at=$now
                WHERE request_id=$rid
                """, ("$rid", requestId), ("$now", Sql.T(DateTimeOffset.UtcNow)));
            return c.ExecuteNonQuery();
        });
        return Get(requestId) ?? throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT, "request vanished");
    }

    static ExecutionRequest Map(SqliteDataReader r) => new()
    {
        RequestId = r.GetString(0),
        AgentSessionId = Sql.S(r.GetValue(1)),
        ConnectorId = r.GetString(2),
        AccountId = r.GetString(3),
        Instrument = r.GetString(4),
        Intent = Enum.Parse<RequestIntent>(r.GetString(5)),
        ParametersJson = r.GetString(6),
        ClientOrderId = r.GetString(7),
        CreatedAt = Sql.Time(r.GetValue(8)),
        DispatchedAt = Sql.TimeN(r.GetValue(9)),
        State = Enum.Parse<ExecutionState>(r.GetString(10)),
        ConnectorOrderId = Sql.S(r.GetValue(11)),
        FilledQuantity = Sql.Dec(r.GetValue(12)),
        AveragePrice = Sql.DecN(r.GetValue(13)),
        NeedsReconciliation = r.GetInt32(14) == 1,
        LastReconciledAt = Sql.TimeN(r.GetValue(15)),
        LastError = Sql.S(r.GetValue(16)),
        Mode = Enum.Parse<TradingMode>(r.GetString(17)),
    };
}

/// <summary>Two log layers: a plain-language history for the user, and structured events for support.</summary>
public sealed class LogStore(Database db)
{
    public void Activity(string text, string level = "info") => db.Write(_ =>
    {
        using var c = db.Cmd("INSERT INTO activity(at,level,text) VALUES($a,$l,$t)",
            ("$a", Sql.T(DateTimeOffset.UtcNow)), ("$l", level), ("$t", text));
        return c.ExecuteNonQuery();
    });

    public void Engineering(string component, string @event, string severity = "info",
        string? session = null, string? correlationId = null, string? requestId = null,
        string? metadataJson = null, Exception? ex = null) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            INSERT INTO engineering_log(at,component,event,severity,session,correlation_id,request_id,metadata,exception)
            VALUES($a,$c,$e,$s,$sess,$corr,$rid,$meta,$exc)
            """,
            ("$a", Sql.T(DateTimeOffset.UtcNow)), ("$c", component), ("$e", @event), ("$s", severity),
            ("$sess", session), ("$corr", correlationId), ("$rid", requestId), ("$meta", metadataJson),
            ("$exc", ex?.ToString()));
        return c.ExecuteNonQuery();
    });

    public void Health(ComponentHealth h) => db.Write(_ =>
    {
        using var c = db.Cmd("INSERT INTO health_event(at,component,state,detail) VALUES($a,$c,$s,$d)",
            ("$a", Sql.T(h.At)), ("$c", h.Component), ("$s", h.State.ToString()), ("$d", h.Detail));
        return c.ExecuteNonQuery();
    });

    public List<(DateTimeOffset At, string Level, string Text)> RecentActivity(int take = 200) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT at,level,text FROM activity ORDER BY id DESC LIMIT $n", ("$n", take));
        using var r = c.ExecuteReader();
        var list = new List<(DateTimeOffset, string, string)>();
        while (r.Read()) list.Add((Sql.Time(r.GetValue(0)), r.GetString(1), r.GetString(2)));
        list.Reverse();
        return list;
    });

    /// <summary>Keeps the two log tables bounded. A laptop must not fill its disk because the agent ran all week.</summary>
    public void Rotate(int keepActivity = 5_000, int keepEngineering = 20_000) => db.Write(_ =>
    {
        using var c = db.Cmd($"""
            DELETE FROM activity WHERE id <= (SELECT MAX(id)-{keepActivity} FROM activity);
            DELETE FROM engineering_log WHERE id <= (SELECT MAX(id)-{keepEngineering} FROM engineering_log);
            DELETE FROM health_event WHERE id <= (SELECT MAX(id)-{keepEngineering} FROM health_event);
            """);
        return c.ExecuteNonQuery();
    });
}

public sealed class OnboardingStore(Database db)
{
    public void Complete(OnboardingStep step, string? detail = null) => db.Write(_ =>
    {
        using var c = db.Cmd("INSERT INTO onboarding(step,completed_at,detail) VALUES($s,$a,$d) ON CONFLICT(step) DO UPDATE SET completed_at=$a, detail=$d",
            ("$s", step.ToString()), ("$a", Sql.T(DateTimeOffset.UtcNow)), ("$d", detail));
        return c.ExecuteNonQuery();
    });

    public void Clear(OnboardingStep step) => db.Write(_ =>
    {
        using var c = db.Cmd("DELETE FROM onboarding WHERE step=$s", ("$s", step.ToString()));
        return c.ExecuteNonQuery();
    });

    public HashSet<OnboardingStep> Completed() => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT step FROM onboarding");
        using var r = c.ExecuteReader();
        var set = new HashSet<OnboardingStep>();
        while (r.Read()) if (Enum.TryParse<OnboardingStep>(r.GetString(0), out var s)) set.Add(s);
        return set;
    });

    /// <summary>The first step not yet done. This is what makes onboarding resumable after any crash.</summary>
    public OnboardingStep Current()
    {
        var done = Completed();
        foreach (var s in OnboardingSteps.Order) if (!done.Contains(s)) return s;
        return OnboardingStep.SETUP_COMPLETE;
    }

    public bool IsComplete() => Completed().Contains(OnboardingStep.SETUP_COMPLETE);
}
