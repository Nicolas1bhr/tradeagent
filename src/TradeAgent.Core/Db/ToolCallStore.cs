namespace TradeAgent.Core.Db;

/// <summary>
/// ONE TOOL CALL THE APP-OWNED HARNESS WAS ASKED FOR, and what the app did about it.
///
/// <para><see cref="Argument"/> is a SUMMARY, never the payload: a path, an op, a count. The whole
/// point of a record the owner can read in three weeks is that it answers "what did this attempt reach
/// for" without becoming a second copy of the files and the owner's own words.</para>
/// </summary>
public sealed record ToolCallRow
{
    public long Id { get; init; }
    public required DateTimeOffset At { get; init; }

    /// <summary>The AI attempt this call belongs to, or null where nothing opened one.</summary>
    public string? Attempt { get; init; }

    /// <summary>The council role whose launch made the call. TradeAgent's answer, never the model's.</summary>
    public string? Role { get; init; }

    public required string Tool { get; init; }
    public string? Argument { get; init; }

    /// <summary>Bytes the call RETURNED — the retrieval quantity the app controls outright.</summary>
    public long Bytes { get; init; }

    public bool Served { get; init; }

    /// <summary>Why it was refused, present exactly when <see cref="Served"/> is false.</summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// THE OBSERVED-DELIVERIES LEDGER, AND THE APP IS THE ONLY THING THAT WRITES IT.
///
/// <para>Round 4 of <c>docs/COUNCIL.md</c>: "Record identities, input hashes, grants, policy versions
/// and observed deliveries; unrestricted CLI reads remain unobserved." A worker on a vendor CLI reads
/// whatever it likes and nothing outside that process knows; a worker on the harness reads through the
/// app, and this is where the app says so.</para>
///
/// <para>There is no verb and no pipe op that inserts, updates or deletes here, and there is no
/// method on this class that changes a row. It is the separation <c>material</c> keeps from
/// <c>material_note</c> — a measurement the observed party can rewrite is not a measurement — applied
/// to what a worker asked for rather than to what it produced.</para>
/// </summary>
public sealed class ToolCallStore(Database db)
{
    /// <summary>The most of an argument summary that is kept. A long one is cut, never a whole payload.</summary>
    public const int MaxArgumentChars = 300;

    /// <summary>
    /// Writes one row. Returns its id.
    ///
    /// <para><b>It never throws.</b> This is called from inside a turn, after the tool has already
    /// done its work, and a ledger that could break a turn would be a record that stops the product.
    /// A row that could not be written is a gap in the record, which the app says out loud on the
    /// report rather than paying for with a failed turn — and the alternative, refusing the tool
    /// because the ledger is down, would make an unwritable database into a worker that cannot
    /// read its own brief.</para>
    /// </summary>
    public long Record(ToolCallRow row)
    {
        try
        {
            return db.Write(_ =>
            {
                using var c = db.Cmd("""
                    INSERT INTO tool_call(at, attempt, role, tool, argument, bytes, served, refusal)
                    VALUES($at,$attempt,$role,$tool,$arg,$bytes,$served,$refusal) RETURNING id
                    """,
                    ("$at", Sql.T(row.At)), ("$attempt", row.Attempt), ("$role", row.Role),
                    ("$tool", row.Tool), ("$arg", Short(row.Argument)), ("$bytes", row.Bytes),
                    ("$served", row.Served ? 1 : 0), ("$refusal", Short(row.Refusal)));
                return Convert.ToInt64(c.ExecuteScalar());
            });
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Every call one attempt made, oldest first. For the record and for the tests.</summary>
    public IReadOnlyList<ToolCallRow> ForAttempt(string attempt) =>
        Query("SELECT id, at, attempt, role, tool, argument, bytes, served, refusal FROM tool_call "
              + "WHERE attempt=$a ORDER BY id", ("$a", attempt));

    /// <summary>Every call in a window, oldest first — the day the owner's report covers.</summary>
    public IReadOnlyList<ToolCallRow> Between(DateTimeOffset from, DateTimeOffset to) =>
        Query("SELECT id, at, attempt, role, tool, argument, bytes, served, refusal FROM tool_call "
              + "WHERE at>=$f AND at<$t ORDER BY id", ("$f", Sql.T(from)), ("$t", Sql.T(to)));

    IReadOnlyList<ToolCallRow> Query(string sql, params (string, object?)[] ps) => db.Read(_ =>
    {
        var rows = new List<ToolCallRow>();
        using var c = db.Cmd(sql, ps);
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new ToolCallRow
            {
                Id = r.GetInt64(0),
                At = Sql.Time(r.GetString(1)),
                Attempt = r.IsDBNull(2) ? null : r.GetString(2),
                Role = r.IsDBNull(3) ? null : r.GetString(3),
                Tool = r.GetString(4),
                Argument = r.IsDBNull(5) ? null : r.GetString(5),
                Bytes = r.GetInt64(6),
                Served = r.GetInt64(7) != 0,
                Refusal = r.IsDBNull(8) ? null : r.GetString(8)
            });
        return rows;
    });

    static string? Short(string? text) =>
        text is null ? null : text.Length <= MaxArgumentChars ? text : text[..MaxArgumentChars];
}
