using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>What a deployment is doing. Three words, and the only three this column may hold.</summary>
public static class DeploymentState
{
    /// <summary>The platform, the mode and the account are the deployment's own, and it may dispatch.</summary>
    public const string Active = "active";

    /// <summary>One of those three moved. Nothing is dispatched and NOTHING is retargeted.</summary>
    public const string Suspended = "suspended";

    /// <summary>Over, with a reason. No further operation is ever planned under it.</summary>
    public const string Ended = "ended";

    public static bool IsKnown(string? s) =>
        s is Active or Suspended or Ended;
}

/// <summary>
/// WHAT ONE OPERATION OF A DEPLOYMENT IS FOR. A closed vocabulary, because the kind is what a reader
/// uses to say whether an unresolved row could have ADDED exposure or only removed it.
/// </summary>
public static class DeploymentOpKind
{
    public const string Entry = "entry";
    public const string Exit = "exit";
    public const string Stop = "stop";
    public const string Target = "target";
    public const string Cancel = "cancel";
    public const string Flatten = "flatten";

    public static bool IsKnown(string? s) =>
        s is Entry or Exit or Stop or Target or Cancel or Flatten;
}

/// <summary>
/// THE FOUR STATES OF A WRITE-AHEAD OPERATION, and the order is the whole of the guarantee.
///
/// <para><c>planned</c> is written BEFORE anything is dispatched, <c>dispatched</c> the moment the
/// gateway has been asked, and <c>resolved</c>/<c>refused</c> only on a definite answer. A row that
/// is <c>dispatched</c> and whose <c>execution_request</c> says UNKNOWN is none of the three
/// terminal things: it stays unresolved, it blocks the cursor, and it is never re-sent.</para>
/// </summary>
public static class DeploymentOpState
{
    /// <summary>Written down. Nothing has been asked of the gateway yet.</summary>
    public const string Planned = "planned";

    /// <summary>The gateway has been asked. The wire may or may not have been touched.</summary>
    public const string Dispatched = "dispatched";

    /// <summary>A definite outcome came back and was recorded.</summary>
    public const string Resolved = "resolved";

    /// <summary>A gate refused it and nothing was sent. It is over, and it is not an outcome.</summary>
    public const string Refused = "refused";

    /// <summary>Whether this op is finished either way — what the cursor advances on.</summary>
    public static bool IsSettled(string? s) => s is Resolved or Refused;

    public static bool IsKnown(string? s) =>
        s is Planned or Dispatched or Resolved or Refused;
}

/// <summary>
/// ONE PAPER DEPLOYMENT: the app's own record that a version is being RUN FORWARD, on one platform,
/// in one mode, on one account, in one instrument, under one allocation and one grant.
///
/// <para><b>Why it exists.</b> <c>manager-prompt.md</c> § 5 asks for "an explicit app-owned execution
/// identity with lineage back to the deployment and the research", and for "persisted progress and
/// unresolved operations so restart, replay, replacement or cancellation cannot duplicate exposure".
/// An allocation says what a version MAY do; nothing said that it is doing it, so a restart had
/// nothing to resume and a replacement had nothing to wait for.</para>
///
/// <para><b><see cref="Id"/> is a SHA-256 over the seven facts the deployment IS</b> — the version,
/// the allocation, the envelope, the platform, the account, the instrument and the instant it
/// started. <see cref="AllocationRow"/>'s shape and for <see cref="AllocationRow"/>'s reason: an id
/// minted from a counter would let one policy sweep write two records of one run, and a later reader
/// would have no way of saying which of them the orders belong to. The ORDER is part of the contract,
/// because an id is compared against rows written by earlier builds.</para>
///
/// <para><b>The seven identity columns are IMMUTABLE and there is no statement in this file that
/// updates one.</b> <see cref="Deployments.Suspend"/> is what happens when the platform, the mode or
/// the account move — never a rewrite of the columns that say which they were. A deployment that
/// could be retargeted is a deployment whose fills are attributable to nothing.</para>
///
/// <para><b><see cref="CursorOpenTime"/> is the LAST BAR WHOSE OPERATIONS ALL RESOLVED</b>, and NULL
/// at the start. It is not "the last bar seen": a bar carrying an operation this app cannot account
/// for is a bar the deployment has not finished, and advancing past it would be the software deciding
/// that an unresolved order did not happen.</para>
/// </summary>
public sealed record StrategyDeploymentRow(
    string Id,
    string VersionId,
    string AllocationId,
    string EnvelopeId,
    string ConnectorId,
    string AccountId,
    string Symbol,
    string Mode,
    string State,
    DateTimeOffset? CursorOpenTime,
    DateTimeOffset StartedAt,
    string? SuspendedReason,
    DateTimeOffset? EndedAt,
    string? EndReason)
{
    /// <summary>
    /// THE BOUND TUPLE, HASHED: seven facts, newline separated, in this order. The order and the
    /// spelling are part of the contract, exactly as <see cref="AllocationRow.IdOf"/>'s and
    /// <see cref="PaperEnvelopeRow.IdOf"/>'s are.
    /// </summary>
    public static string IdOf(string versionId, string allocationId, string envelopeId,
        string connectorId, string accountId, string symbol, DateTimeOffset startedAt) =>
        Sha256Hex.Of(string.Join('\n',
            versionId, allocationId, envelopeId, connectorId, accountId, symbol, Sql.T(startedAt)));

    /// <summary>The id these seven facts hash to, whatever <see cref="Id"/> currently holds.</summary>
    public string ComputedId =>
        IdOf(VersionId, AllocationId, EnvelopeId, ConnectorId, AccountId, Symbol, StartedAt);

    public bool IsActive => string.Equals(State, DeploymentState.Active, StringComparison.Ordinal);
    public bool IsSuspended => string.Equals(State, DeploymentState.Suspended, StringComparison.Ordinal);
    public bool IsEnded => string.Equals(State, DeploymentState.Ended, StringComparison.Ordinal);

    /// <summary>
    /// WHETHER THIS DEPLOYMENT IS POINTED AT THE PLACE THE GATEWAY IS OPERATING. All three, and none
    /// of them is a default: an account id is unique only within a platform, and the same platform
    /// and account are a different undertaking in LIVE than in PAPER — the reading
    /// <c>TradingGateway.FlattenForBreachAsync</c> takes of a loss closure, for the same reason.
    /// </summary>
    public bool PointedAt(string connectorId, string mode, string accountId) =>
        string.Equals(ConnectorId, connectorId, StringComparison.Ordinal)
        && string.Equals(Mode, mode, StringComparison.Ordinal)
        && string.Equals(AccountId, accountId, StringComparison.Ordinal);

    /// <summary>The first twelve of a content hash, which is how every surface in this product names one.</summary>
    public static string Short(string id) => id.Length <= 12 ? id : id[..12];
}

/// <summary>
/// ONE WRITE-AHEAD OPERATION OF ONE DEPLOYMENT — written BEFORE it is dispatched, and settled only on
/// a definite answer.
///
/// <para><b><see cref="RequestId"/> is the gateway's own request id</b>, and it is the primary key
/// here for the reason it is the primary key in <c>execution_request</c>: the two tables are joined
/// by it, so an operation and the order it became cannot come apart. It is built by
/// <see cref="Deployments.RequestIdFor"/> and is SENDABLE — it goes onto the broker order as
/// <c>TA-&lt;id&gt;</c> and safety rule 1 requires it back unchanged.</para>
/// </summary>
public sealed record DeploymentOpRow(
    string RequestId,
    string DeploymentId,
    DateTimeOffset BarOpenTime,
    string Kind,
    string IntentJson,
    string State,
    string? Answer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt)
{
    public bool IsSettled => DeploymentOpState.IsSettled(State);
    public bool IsPlanned => string.Equals(State, DeploymentOpState.Planned, StringComparison.Ordinal);
    public bool IsDispatched => string.Equals(State, DeploymentOpState.Dispatched, StringComparison.Ordinal);
}

/// <summary>What the ledger did with one proposed write, and why when it did nothing.</summary>
public sealed record DeploymentResult(bool Ok, string Why, StrategyDeploymentRow? Deployment);

/// <summary>
/// THE DEPLOYMENT LEDGER: WHAT IS BEING RUN FORWARD, AND EVERY OPERATION IT HAS WRITTEN DOWN.
///
/// <para><b>State is written by <see cref="Start"/>, <see cref="Suspend"/>, <see cref="Resume"/> and
/// <see cref="End"/>, and by nothing else</b> — four methods, one <c>Database.Write</c> each, each
/// one a single statement whose <c>WHERE</c> names the state it is coming FROM. That is what makes
/// the transition write-once rather than a rule a caller keeps: two processes over one file (the app
/// and <c>tradeagent-gateway.exe</c>) cannot both end one deployment, and an in-process lock would
/// have settled neither. The identity columns appear in no <c>UPDATE</c> in this file at all.</para>
///
/// <para><b>There is no <c>trade</c> verb and no pipe op that starts, suspends or resumes one.</b> The
/// app's own policy is the only caller, on its own clock. An agent that wanted a deployment has
/// nowhere to ask, exactly as it has nowhere to ask for an envelope or an allocation. The ONE thing
/// reachable from the agent-facing side is ENDING one — which only ever removes exposure, the
/// reduction-only exception this product already makes for <c>close</c> and <c>cancel</c>.</para>
/// </summary>
public sealed class Deployments(Database db)
{
    const string Cols =
        "id, version_id, allocation_id, envelope_id, connector_id, account_id, symbol, mode, state, " +
        "cursor_open_time, started_at, suspended_reason, ended_at, end_reason";

    const string OpCols =
        "request_id, deployment_id, bar_open_time, kind, intent, state, answer, created_at, resolved_at";

    /// <summary>
    /// THE ID AN OPERATION LEAVES THE PROCESS UNDER: <c>dp-&lt;12 of the deployment&gt;-&lt;the bar's
    /// open, in whole minutes since the epoch&gt;-&lt;sequence&gt;</c>.
    ///
    /// <para><b>Every character is <c>[A-Za-z0-9-]</c> and the whole is far inside the 61 a request id
    /// may run to</b> (<c>TradingGateway.MaxRequestIdChars</c>), because it is carried onto the broker
    /// order as <c>TA-&lt;id&gt;</c> and safety rule 1 needs it back unchanged. Twelve hex characters
    /// of the deployment, a minute count that is eight digits until the year 2160, and a sequence: 28
    /// characters on a realistic row, and a test pins the budget rather than trusting the arithmetic.</para>
    ///
    /// <para><b>It is a FUNCTION of the deployment, the bar and the sequence and of nothing else</b> —
    /// no clock, no counter, no GUID. That is what makes a restart's re-plan of the same operation the
    /// SAME id, which <c>ExecutionRequestStore.TryCreate</c> then collapses onto the row that is
    /// already there rather than sending a second order.</para>
    /// </summary>
    public static string RequestIdFor(string deploymentId, DateTimeOffset barOpen, int seq) =>
        $"dp-{StrategyDeploymentRow.Short(deploymentId)}-"
        + (barOpen.ToUnixTimeSeconds() / 60).ToString(CultureInfo.InvariantCulture)
        + $"-{seq.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// STARTS ONE DEPLOYMENT, or leaves the row that is already there alone. Returns the row AS
    /// WRITTEN, with the id its seven facts hash to.
    ///
    /// <para>The invariants asked here are the ones that belong to the DATA: the seven identity facts
    /// are present, and the mode is a word this build knows. Whether the version's verdict stands,
    /// whether the envelope still stands and whether the envelope has room are questions about three
    /// other ledgers at one instant, and they are asked by the app's policy
    /// (<c>TradingGateway.StartPaperDeploymentsDue</c>) rather than restated here from a copy.</para>
    /// </summary>
    public DeploymentResult Start(StrategyDeploymentRow deployment) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(deployment);

        if (deployment.VersionId.Length == 0 || deployment.AllocationId.Length == 0
            || deployment.EnvelopeId.Length == 0 || deployment.ConnectorId.Length == 0
            || deployment.AccountId.Length == 0 || deployment.Symbol.Length == 0
            || deployment.Mode.Length == 0)
            return new DeploymentResult(false,
                "a deployment names the version, the allocation, the envelope, the platform, the "
                + "account, the instrument and the mode it runs under, and this one does not, so "
                + "nothing was written.", null);

        var row = deployment with
        {
            Id = deployment.ComputedId,
            State = DeploymentState.Active,
            SuspendedReason = null,
            EndedAt = null,
            EndReason = null
        };

        using var c = db.Cmd($"""
            INSERT INTO strategy_deployment({Cols})
            VALUES($id,$ver,$alloc,$env,$conn,$acct,$sym,$mode,$state,$cursor,$started,NULL,NULL,NULL)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", row.Id), ("$ver", row.VersionId), ("$alloc", row.AllocationId),
            ("$env", row.EnvelopeId), ("$conn", row.ConnectorId), ("$acct", row.AccountId),
            ("$sym", row.Symbol), ("$mode", row.Mode), ("$state", row.State),
            ("$cursor", row.CursorOpenTime is { } cur ? Sql.T(cur) : null),
            ("$started", Sql.T(row.StartedAt)));
        var created = c.ExecuteNonQuery() == 1;

        // THE SEVEN FACTS ARE THE ID, SO A RE-START OF AN ENDED RUN IS THE SAME ROW — and it must not
        // read as a fresh one. `ON CONFLICT DO NOTHING` left the ended row exactly as it was, which is
        // right; answering Ok would tell the policy it had started something. A genuine replacement is
        // a LATER instant and therefore a different id, which is the whole reason `started_at` is one
        // of the seven.
        if (!created && ById(row.Id) is { } existing && existing.IsEnded)
            return new DeploymentResult(false,
                $"a deployment of version {StrategyDeploymentRow.Short(existing.VersionId)} with exactly "
                + $"these facts ran and ended at {existing.EndedAt:u} ({existing.EndReason}), so nothing "
                + "was written. A replacement is a later instant and a different record.", existing);

        var written = ById(row.Id) ?? row;
        return new DeploymentResult(true,
            $"version {StrategyDeploymentRow.Short(written.VersionId)} is deployed forward on PAPER "
            + $"account {written.AccountId} at {written.ConnectorId}, in {written.Symbol}. No capital "
            + "and no live authority come with it.", written);
    });

    /// <summary>
    /// ONE STATE WRITE: <c>active</c> → <c>suspended</c>, with the reason. Nothing else moves, and the
    /// seven identity columns are not in the statement.
    ///
    /// <para><c>WHERE state='active'</c> is what makes it a transition rather than an assignment: a
    /// deployment that has already ended is not re-opened by a late sweep noticing the platform moved,
    /// and two processes racing to suspend the same row leave one instant on it, not two.</para>
    /// </summary>
    public bool Suspend(string id, string reason) => db.Write(_ =>
    {
        using var c = db.Cmd(
            "UPDATE strategy_deployment SET state=$to, suspended_reason=$why "
            + "WHERE id=$id AND state=$from",
            ("$to", DeploymentState.Suspended), ("$why", reason), ("$id", id),
            ("$from", DeploymentState.Active));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// ONE STATE WRITE: <c>suspended</c> → <c>active</c>, and the reason is cleared because it is no
    /// longer true. Refuses an ended row by the same <c>WHERE</c> the others use.
    /// </summary>
    public bool Resume(string id) => db.Write(_ =>
    {
        using var c = db.Cmd(
            "UPDATE strategy_deployment SET state=$to, suspended_reason=NULL "
            + "WHERE id=$id AND state=$from",
            ("$to", DeploymentState.Active), ("$id", id), ("$from", DeploymentState.Suspended));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// ONE STATE WRITE: anything that is not already ended → <c>ended</c>, with the instant and the
    /// reason.
    ///
    /// <para><c>WHERE state &lt;&gt; 'ended'</c> rather than a state name, because both an active and
    /// a suspended deployment can be ended and the fact that must not move is WHEN it ended and WHY —
    /// a second end would restate a decision already taken, which is the rule
    /// <see cref="Envelopes.Withdraw"/> keeps with <c>WHERE withdrawn_at IS NULL</c>.</para>
    ///
    /// <para><b>It does not flatten anything.</b> Cancelling the working orders and closing the
    /// position are wire work with gates in front of them, and they belong to the gateway
    /// (<c>TradingGateway.EndPaperDeploymentAsync</c>), which writes those legs as <c>flatten</c> ops
    /// BEFORE it calls this.</para>
    /// </summary>
    public bool End(string id, string reason, DateTimeOffset at) => db.Write(_ =>
    {
        using var c = db.Cmd(
            "UPDATE strategy_deployment SET state=$to, ended_at=$at, end_reason=$why "
            + "WHERE id=$id AND state<>$to",
            ("$to", DeploymentState.Ended), ("$at", Sql.T(at)), ("$why", reason), ("$id", id));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// WHERE THE DEPLOYMENT HAS GOT TO: the last bar every one of whose operations settled.
    ///
    /// <para>Not a state write and not one of the four — it is progress rather than authority — but it
    /// is still <c>WHERE</c>-guarded so it can only ever move FORWARD. A cursor that could go
    /// backwards would re-offer a bar whose operations are already on the wire.</para>
    /// </summary>
    public bool AdvanceCursor(string id, DateTimeOffset to) => db.Write(_ =>
    {
        using var c = db.Cmd(
            "UPDATE strategy_deployment SET cursor_open_time=$to "
            + "WHERE id=$id AND (cursor_open_time IS NULL OR cursor_open_time < $to)",
            ("$to", Sql.T(to)), ("$id", id));
        return c.ExecuteNonQuery() == 1;
    });

    public StrategyDeploymentRow? ById(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM strategy_deployment WHERE id=$id", ("$id", id));
        return Read(c).FirstOrDefault();
    });

    /// <summary>Every deployment this installation has recorded, newest first.</summary>
    public IReadOnlyList<StrategyDeploymentRow> All(int limit = 100) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_deployment ORDER BY started_at DESC, id DESC LIMIT $n",
            ("$n", limit));
        return (IReadOnlyList<StrategyDeploymentRow>)Read(c);
    });

    /// <summary>
    /// EVERY DEPLOYMENT THAT IS NOT OVER — active and suspended alike, newest first. What the policy
    /// counts against an envelope's <c>max_deployments</c>, and what the surfaces list.
    /// </summary>
    public IReadOnlyList<StrategyDeploymentRow> Open(int limit = 100) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_deployment WHERE state<>$ended "
            + "ORDER BY started_at DESC, id DESC LIMIT $n",
            ("$ended", DeploymentState.Ended), ("$n", limit));
        return (IReadOnlyList<StrategyDeploymentRow>)Read(c);
    });

    /// <summary>Every deployment ever written under one grant, newest first.</summary>
    public IReadOnlyList<StrategyDeploymentRow> ForEnvelope(string envelopeId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_deployment WHERE envelope_id=$e "
            + "ORDER BY started_at DESC, id DESC",
            ("$e", envelopeId));
        return (IReadOnlyList<StrategyDeploymentRow>)Read(c);
    });

    /// <summary>Every deployment ever written under one allocation, newest first.</summary>
    public IReadOnlyList<StrategyDeploymentRow> ForAllocation(string allocationId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_deployment WHERE allocation_id=$a "
            + "ORDER BY started_at DESC, id DESC",
            ("$a", allocationId));
        return (IReadOnlyList<StrategyDeploymentRow>)Read(c);
    });

    // ---------------------------------------------------------------- operations

    /// <summary>
    /// WRITES ONE OPERATION DOWN, <c>planned</c>, BEFORE ANYTHING IS DISPATCHED — and answers whether
    /// THIS call is the one that wrote it.
    ///
    /// <para><c>ON CONFLICT DO NOTHING</c> and the insert's own answer, never a read-then-write: a
    /// re-plan of the same bar after a restart raises the id that is already there, and false here
    /// means "that operation exists and is the one that counts". An earlier draft of this product
    /// learned the same lesson at <c>Database.AddKvOnce</c> and again at <c>forward_bar</c>.</para>
    /// </summary>
    public bool Plan(DeploymentOpRow op) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(op);

        using var c = db.Cmd($"""
            INSERT INTO deployment_op({OpCols})
            VALUES($rid,$dep,$bar,$kind,$intent,$state,NULL,$created,NULL)
            ON CONFLICT(request_id) DO NOTHING
            """,
            ("$rid", op.RequestId), ("$dep", op.DeploymentId), ("$bar", Sql.T(op.BarOpenTime)),
            ("$kind", op.Kind), ("$intent", op.IntentJson), ("$state", DeploymentOpState.Planned),
            ("$created", Sql.T(op.CreatedAt)));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// <c>planned</c> → <c>dispatched</c>. Written IMMEDIATELY BEFORE the gateway is asked, so that a
    /// process killed inside the call leaves a row saying the wire may have been touched.
    /// </summary>
    public bool MarkDispatched(string requestId) =>
        MoveOp(requestId, DeploymentOpState.Planned, DeploymentOpState.Dispatched, null, null);

    /// <summary>A definite outcome: <c>dispatched</c> → <c>resolved</c>, with what the record said.</summary>
    public bool Resolve(string requestId, string answer, DateTimeOffset at) =>
        MoveOp(requestId, DeploymentOpState.Dispatched, DeploymentOpState.Resolved, answer, at);

    /// <summary>
    /// A GATE SAID NO AND NOTHING WAS SENT: <c>planned</c> or <c>dispatched</c> → <c>refused</c>.
    ///
    /// <para>Both from-states, because the two ways an operation is refused look different from here:
    /// a gate that refused before the record existed leaves the op <c>planned</c>, and one that threw
    /// out of <c>PlaceAsync</c> after the op was marked leaves it <c>dispatched</c>. Neither is an
    /// outcome and both are over — which is the difference from UNKNOWN, where the answer is the one
    /// thing nobody has.</para>
    /// </summary>
    public bool Refuse(string requestId, string why, DateTimeOffset at) =>
        MoveOp(requestId, DeploymentOpState.Planned, DeploymentOpState.Refused, why, at)
        || MoveOp(requestId, DeploymentOpState.Dispatched, DeploymentOpState.Refused, why, at);

    bool MoveOp(string requestId, string from, string to, string? answer, DateTimeOffset? at) => db.Write(_ =>
    {
        using var c = db.Cmd(
            "UPDATE deployment_op SET state=$to, answer=COALESCE($answer, answer), "
            + "resolved_at=COALESCE($at, resolved_at) WHERE request_id=$rid AND state=$from",
            ("$to", to), ("$answer", answer), ("$at", at is { } when ? Sql.T(when) : null),
            ("$rid", requestId), ("$from", from));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>Every operation of one deployment, oldest bar first and then in the order written.</summary>
    public IReadOnlyList<DeploymentOpRow> OpsOf(string deploymentId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {OpCols} FROM deployment_op WHERE deployment_id=$d "
            + "ORDER BY bar_open_time ASC, created_at ASC, request_id ASC",
            ("$d", deploymentId));
        return (IReadOnlyList<DeploymentOpRow>)ReadOps(c);
    });

    /// <summary>One operation by the request id it was written under, or null.</summary>
    public DeploymentOpRow? OpById(string requestId) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {OpCols} FROM deployment_op WHERE request_id=$r", ("$r", requestId));
        return ReadOps(c).FirstOrDefault();
    });

    /// <summary>
    /// WHETHER THIS DEPLOYMENT IS ACCOUNTED FOR: every operation it ever wrote is settled.
    ///
    /// <para>What a replacement waits on. An unresolved operation is an order that may be live at the
    /// platform, and starting a second deployment over it is precisely the duplicated exposure this
    /// ledger exists to prevent.</para>
    /// </summary>
    public bool IsReconciled(string deploymentId) =>
        OpsOf(deploymentId).All(o => o.IsSettled);

    static List<StrategyDeploymentRow> Read(SqliteCommand c)
    {
        var rows = new List<StrategyDeploymentRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new StrategyDeploymentRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                Sql.TimeN(r.GetValue(9)), Sql.Time(r.GetValue(10)), Sql.S(r.GetValue(11)),
                Sql.TimeN(r.GetValue(12)), Sql.S(r.GetValue(13))));
        return rows;
    }

    static List<DeploymentOpRow> ReadOps(SqliteCommand c)
    {
        var rows = new List<DeploymentOpRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new DeploymentOpRow(
                r.GetString(0), r.GetString(1), Sql.Time(r.GetValue(2)), r.GetString(3),
                r.GetString(4), r.GetString(5), Sql.S(r.GetValue(6)), Sql.Time(r.GetValue(7)),
                Sql.TimeN(r.GetValue(8))));
        return rows;
    }
}
