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
public sealed class ExecutionRequestStore(Database db, TimeProvider? clock = null)
{
    /// <summary>
    /// THE SAME CLOCK THE GATEWAY AGES REQUESTS ON, BECAUSE BOTH ENDS OF A DURATION MUST COME FROM ONE.
    ///
    /// `created_at` arrives on the record the caller hands to TryCreate, but `dispatched_at` is
    /// written here, and TradingGateway subtracts it from its own clock to decide whether an order is
    /// old enough for absence to mean it never landed. When those were two different clocks the
    /// subtraction was only meaningful because both happened to be the system clock; substitute one
    /// and the age silently became nonsense. Defaults to the system clock, so a caller that does not
    /// care is unaffected.
    /// </summary>
    DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

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
                ("$mode", r.Mode.ToString()), ("$upd", Sql.T(Now)));
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

    /// <summary>
    /// How long a record may sit in DISPATCHING before it counts as unconfirmed work rather than as
    /// an order in flight. The connector gives every RPC a 10 s deadline
    /// (<c>AtasConnector</c>'s <c>rpcTimeout</c>), so a record still DISPATCHING well past that
    /// cannot be waiting for an answer — the call either returned or threw, and either way something
    /// should have written the outcome. The margin above the deadline is deliberate slack for a slow
    /// write or a descheduled continuation, not a guess at how long a broker takes.
    /// </summary>
    public static readonly TimeSpan DefaultDispatchStrandedAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Work the gateway must not trade over.
    ///
    /// THE FLAG ALONE IS NOT ENOUGH, and that is the whole reason this takes an argument. Every path
    /// that sets <c>needs_reconciliation</c> runs inside a catch block, so a process that dies
    /// between the write-ahead DISPATCHING row and the settle leaves a record that may be live at
    /// the broker with the flag still 0 — invisible to this query, and therefore to the gate that
    /// reads it. A DISPATCHING record older than a dispatch can take is, by definition, one where
    /// the wire may have been touched and nobody wrote down what happened.
    ///
    /// The caller passes an absolute instant rather than an age because THIS CLASS AND THE GATEWAY
    /// READ DIFFERENT CLOCKS. It is not true that the store owns none: <c>Transition</c> and
    /// <c>TryCreate</c> stamp <c>dispatched_at</c>, <c>created_at</c> and <c>updated_at</c> from
    /// <see cref="DateTimeOffset.UtcNow"/> directly, while the gateway reads a substitutable
    /// <c>TimeProvider</c> that a test can move. Passing the cutoff in keeps the comparison on ONE
    /// of those clocks — the caller's — instead of straddling both. Omit it and the query is exactly
    /// what it always was: the flag.
    /// </summary>
    public List<ExecutionRequest> NeedingReconciliation(DateTimeOffset? strandedDispatchBefore = null) =>
        strandedDispatchBefore is { } cutoff
            ? Query("needs_reconciliation=1 OR (execution_state='DISPATCHING' AND COALESCE(dispatched_at, created_at) <= $cut)",
                ("$cut", Sql.T(cutoff)))
            : Query("needs_reconciliation=1");

    /// <summary>
    /// Every record the wire may already have seen. Read at startup, where "still DISPATCHING" can
    /// only mean the process that was flying it is gone.
    /// </summary>
    public List<ExecutionRequest> Dispatching() => Query("execution_state='DISPATCHING'");

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
                ("$err", error), ("$mr", markReconciled ? 1 : 0), ("$now", Sql.T(Now)));
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
                """, ("$rid", requestId), ("$err", error), ("$now", Sql.T(Now)));
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
                """, ("$rid", requestId), ("$now", Sql.T(Now)));
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
/// <summary>
/// THE ROW THAT MAKES A MULTI-TARGET OPERATION IDEMPOTENT.
///
/// <see cref="ExecutionRequestStore"/> gives one mutation that guarantee already: the caller's
/// request id is the primary key, so a repeated `buy` finds its record and dispatches nothing. A
/// sweep had no equivalent. `cancel-all` and `close-all` decomposed the caller's id into per-order
/// legs named after a nonce minted FRESH on every call, so an agent that lost the reply and re-sent
/// the same request id got a whole new sweep, over whatever happened to be on the book by then
/// (Codex C2).
///
/// Two fields carry it. <c>nonce</c> is what the legs are named after and it is written BEFORE any
/// effect, so a second call with the same request id derives the SAME leg ids and every leg's own
/// record refuses to dispatch twice. <c>result</c> is what the first run answered, written after
/// the effects; a replay that finds one hands it straight back.
/// </summary>
public sealed class CompositeRequestStore(Database db, TimeProvider? clock = null)
{
    DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    const string Cols = "request_id, agent_session_id, op, nonce, plan, created_at, result, completed_at";

    /// <summary>
    /// Claims this request id for a composite, or hands back the one that already holds it.
    ///
    /// <c>Created == false</c> is the whole point: it means this id has been seen, and the caller
    /// must use the STORED nonce and plan rather than the ones it just built. The insert is the
    /// atomic step — two callers racing on one id cannot both be told they created it.
    /// </summary>
    public (bool Created, CompositeRequest Request) TryBegin(CompositeRequest r)
    {
        var rows = db.Write(_ =>
        {
            using var c = db.Cmd($"""
                INSERT INTO composite_request({Cols})
                VALUES($rid,$sess,$op,$nonce,$plan,$created,NULL,NULL)
                ON CONFLICT(request_id) DO NOTHING
                """,
                ("$rid", r.RequestId), ("$sess", r.AgentSessionId), ("$op", r.Op), ("$nonce", r.Nonce),
                ("$plan", r.PlanJson), ("$created", Sql.T(r.CreatedAt)));
            return c.ExecuteNonQuery();
        });

        var stored = Get(r.RequestId)
            ?? throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT, "composite vanished after insert");
        return (rows == 1, stored);
    }

    public CompositeRequest? Get(string requestId) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM composite_request WHERE request_id=$r", ("$r", requestId));
        using var rd = c.ExecuteReader();
        return rd.Read() ? Map(rd) : null;
    });

    /// <summary>
    /// Writes the answer, ONCE. A second run of the same composite may not overwrite what the first
    /// one told the caller — the reply the caller lost is still the reply this id has, and rewriting
    /// it would make a replay return something the original run never said.
    /// </summary>
    public CompositeRequest Complete(string requestId, string resultJson)
    {
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                UPDATE composite_request SET result=$res, completed_at=$now
                WHERE request_id=$rid AND result IS NULL
                """, ("$rid", requestId), ("$res", resultJson), ("$now", Sql.T(Now)));
            return c.ExecuteNonQuery();
        });
        return Get(requestId)
            ?? throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT, "composite vanished before it was completed");
    }

    static CompositeRequest Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        RequestId = r.GetString(0),
        AgentSessionId = Sql.S(r.GetValue(1)),
        Op = r.GetString(2),
        Nonce = r.GetString(3),
        PlanJson = r.GetString(4),
        CreatedAt = Sql.Time(r.GetValue(5)),
        ResultJson = Sql.S(r.GetValue(6)),
        CompletedAt = Sql.TimeN(r.GetValue(7))
    };
}

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

    /// <summary>
    /// <see cref="Engineering"/> for a caller that is already handling a failure and must not be
    /// stopped by a second one. Returns whether the row was written, so nothing has to guess.
    /// </summary>
    public bool TryEngineering(string component, string @event, string severity = "info",
        string? session = null, string? correlationId = null, string? requestId = null,
        string? metadataJson = null, Exception? ex = null)
    {
        try { Engineering(component, @event, severity, session, correlationId, requestId, metadataJson, ex); return true; }
        catch (Exception) { return false; }
    }

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
