using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>Which of the two sources put this row in the ledger. See <see cref="FillStore"/>.</summary>
public enum FillSource
{
    /// <summary>The connector's own execution stream, as it happened.</summary>
    Event,

    /// <summary>A read of the platform's executions, at a (re)connect or on the interval.</summary>
    Pull
}

/// <summary>One execution as the ledger holds it. <c>Fee</c> null is UNKNOWN, never zero.</summary>
public sealed record Fill(
    string AccountId,
    string ExecutionId,
    DateTimeOffset At,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    string? ConnectorOrderId,
    string? ClientOrderId,
    string? RequestId,
    string? AgentSession,
    FillSource Source,
    decimal? Fee,
    DateTimeOffset RecordedAt)
{
    /// <summary>
    /// THE PLATFORM THIS EXECUTION HAPPENED ON — <c>connector</c>, schema 22.
    ///
    /// <para>An account id is unique only within a platform, and switching platforms builds a new
    /// gateway over the same database, so <c>account_id</c> alone never identified whose money this
    /// was. Every figure the loss budget is enforced on comes out of this table.</para>
    ///
    /// <para><b>NULL is a row written before schema 22</b> and it belongs to NO pair: it is counted
    /// and reported as an unattributed fill beside the figure rather than netted into it. There is
    /// no honest backfill — this build cannot know which connector wrote a row it did not stamp.</para>
    /// </summary>
    public string? ConnectorId { get; init; }
}

/// <summary>
/// THE FILL LEDGER. One row per execution, and it is the ruler everything else about money is
/// measured with: the loss budget, the AI's running cost, and one day several agents judged against
/// each other all read this table and nothing else.
///
/// <para><b>Written by the gateway only, never updated and never deleted.</b> There is no method
/// here that changes a row or removes one, and that absence is the design — the same split the
/// material ledger makes between what TradeAgent OBSERVED and what somebody CLAIMED. A P&amp;L
/// figure computed from rows the observed party can edit is not a measurement.</para>
///
/// <para><b>Identity is <c>(account_id, execution_id)</c>, and that is the whole of item 1.</b> The
/// same execution arrives twice by design: the connector raises it on its event stream AND serves it
/// again from <c>GetExecutionsAsync</c> at every reconnect and on the interval. Two sources are what
/// makes the ledger survive a dropped connection; two ROWS would make every number it produces
/// double. The insert is <c>ON CONFLICT DO NOTHING</c>, so whichever source arrives first owns the
/// row and says so in <c>source</c>, and the second is a no-op rather than a correction.</para>
///
/// <para>Decimals are stored as invariant text, exactly as <c>execution_request</c> stores them: the
/// quantity and the price a broker reported are not floating-point quantities and must come back out
/// as they went in.</para>
/// </summary>
public sealed class FillStore(Database db)
{
    const string Cols = """
        account_id, execution_id, at, symbol, side, quantity, price, connector_order_id,
        client_order_id, request_id, agent_session, source, fee, recorded_at, connector
        """;

    /// <summary>
    /// Writes one fill. Returns false when this execution was already in the ledger — which is the
    /// ordinary case for the second source, not an error.
    /// </summary>
    public bool Record(Fill f) => db.Write(_ =>
    {
        using var c = db.Cmd($"""
            INSERT INTO fill({Cols})
            VALUES($acct,$xid,$at,$sym,$side,$qty,$px,$coid,$cloid,$rid,$sess,$src,$fee,$rec,$conn)
            ON CONFLICT(account_id, execution_id) DO NOTHING
            """,
            ("$acct", f.AccountId), ("$xid", f.ExecutionId), ("$at", Sql.T(f.At)), ("$sym", f.Symbol),
            ("$side", f.Side), ("$qty", Sql.D(f.Quantity)), ("$px", Sql.D(f.Price)),
            ("$coid", f.ConnectorOrderId), ("$cloid", f.ClientOrderId), ("$rid", f.RequestId),
            ("$sess", f.AgentSession), ("$src", f.Source.ToString().ToLowerInvariant()),
            ("$fee", f.Fee is null ? null : Sql.D(f.Fee.Value)), ("$rec", Sql.T(f.RecordedAt)),
            ("$conn", f.ConnectorId is { Length: > 0 } c2 ? c2 : null));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>Every fill at or after <paramref name="since"/>, oldest first. Null is everything.</summary>
    public List<Fill> Since(DateTimeOffset? since = null) => db.Read(_ =>
    {
        using var c = since is null
            ? db.Cmd($"SELECT {Cols} FROM fill ORDER BY at, rowid")
            : db.Cmd($"SELECT {Cols} FROM fill WHERE at >= $s ORDER BY at, rowid", ("$s", Sql.T(since.Value)));
        using var r = c.ExecuteReader();
        var list = new List<Fill>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    /// <summary>
    /// THE OPERATING PAIR'S OWN FILLS, and how many rows of this account belong to no pair —
    /// <c>U-scope-identity</c>, REVIEW 2026-09-16 finding 2.
    ///
    /// <para>Every fill of <paramref name="accountId"/> stamped with <paramref name="connectorId"/>,
    /// oldest first, with <c>since</c> meaning the same thing it does in <see cref="Since"/>. A row
    /// of another account or another platform is not in it at all — it is not a smaller part of this
    /// figure, it is somebody else's.</para>
    ///
    /// <para><b>The whole ledger is still walked, and the WINDOW is what <c>since</c> selects.</b>
    /// Average cost is a running quantity: a position opened last week and closed today realises
    /// against last week's price, so the rows are filtered by SCOPE here and by time by the caller,
    /// never the other way round.</para>
    ///
    /// <para><c>Unattributed</c> counts this account's rows with no connector on them — every row
    /// written before schema 22. They are NEVER netted into the figure: attributing them to the
    /// platform that happens to be attached would be finding 2 with a migration in front of it.</para>
    /// </summary>
    public (List<Fill> Fills, int Unattributed) Scoped(string connectorId, string accountId,
        DateTimeOffset? since = null) => db.Read(_ =>
    {
        var list = new List<Fill>();
        using (var c = db.Cmd($"""
            SELECT {Cols} FROM fill WHERE account_id=$a AND connector=$c ORDER BY at, rowid
            """, ("$a", accountId), ("$c", connectorId)))
        {
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
        }

        using var u = since is null
            ? db.Cmd("SELECT COUNT(*) FROM fill WHERE account_id=$a AND connector IS NULL", ("$a", accountId))
            : db.Cmd("SELECT COUNT(*) FROM fill WHERE account_id=$a AND connector IS NULL AND at >= $s",
                ("$a", accountId), ("$s", Sql.T(since.Value)));
        return (list, Convert.ToInt32(u.ExecuteScalar()));
    });

    /// <summary>When the first fill this ledger holds happened. Null when it holds none.</summary>
    public DateTimeOffset? FirstAt() => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT MIN(at) FROM fill");
        return Sql.TimeN(c.ExecuteScalar());
    });

    /// <summary>When the newest fill this ledger holds happened. What a routine pull asks from.</summary>
    public DateTimeOffset? Newest() => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT MAX(at) FROM fill");
        return Sql.TimeN(c.ExecuteScalar());
    });

    public int Count() => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM fill");
        return Convert.ToInt32(c.ExecuteScalar());
    });

    static Fill Read(SqliteDataReader r) => new(
        AccountId: r.GetString(0),
        ExecutionId: r.GetString(1),
        At: Sql.Time(r.GetValue(2)),
        Symbol: r.GetString(3),
        Side: r.GetString(4),
        Quantity: Sql.Dec(r.GetValue(5)),
        Price: Sql.Dec(r.GetValue(6)),
        ConnectorOrderId: Sql.S(r.GetValue(7)),
        ClientOrderId: Sql.S(r.GetValue(8)),
        RequestId: Sql.S(r.GetValue(9)),
        AgentSession: Sql.S(r.GetValue(10)),
        Source: string.Equals(Sql.S(r.GetValue(11)), "pull", StringComparison.Ordinal) ? FillSource.Pull : FillSource.Event,
        Fee: Sql.DecN(r.GetValue(12)),
        RecordedAt: Sql.Time(r.GetValue(13)))
    {
        ConnectorId = Sql.S(r.GetValue(14))
    };
}
