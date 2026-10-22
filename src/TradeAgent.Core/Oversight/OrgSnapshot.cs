using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Oversight;

/// <summary>One unit of the chart as the snapshot read it.</summary>
public sealed record SnapUnit(string Id, string? ParentId, string Kind, string Status, DateTimeOffset CreatedAt);

/// <summary>One AI attempt in the horizon: its charged unit (the funding chain's nearest, else its position's), spend, mandate.</summary>
public sealed record SnapAttempt(string Id, DateTimeOffset StartedAt, string? Role, string State, decimal Spend, string? MandateId, string? UnitId);

/// <summary>One mandate: the unit it is in.</summary>
public sealed record SnapMandate(string Id, string UnitId, string Holder, string Decider, DateTimeOffset CreatedAt);

/// <summary>One mandate event, its evidence ids as written.</summary>
public sealed record SnapMandateEvent(long Id, string MandateId, DateTimeOffset At, string Kind, string Actor, IReadOnlyList<string> Evidence);

/// <summary>One sealed delivery: its refs as the holder wrote them (a CLAIM, cited, never copied).</summary>
public sealed record SnapDelivery(string Id, string MandateId, string Refs, DateTimeOffset At);

/// <summary>One check the APP ran on a delivery.</summary>
public sealed record SnapCheck(string DeliveryId, string Name, bool Passed);

/// <summary>One publication's id, author and time — never its content.</summary>
public sealed record SnapPublication(string Id, string Role, string Kind, DateTimeOffset At);

/// <summary>One wake: its kind, the role it is for, when it was raised, due and consumed, and its disposition word.</summary>
public sealed record SnapWake(string Id, string Kind, string? Role, DateTimeOffset CreatedAt, DateTimeOffset DueAt, DateTimeOffset? ConsumedAt, string? Disposition);

/// <summary>One tool call the app refused.</summary>
public sealed record SnapToolRefusal(long Id, DateTimeOffset At, string? Attempt, string? Role, string Tool);

/// <summary>
/// One refusal the pipe wrote to the engineering log. Pruned there in time, so read by cursor and copied into the finding. An
/// <c>op_refused</c> row (<see cref="RefusalMark"/>) also carries the op, code, role and attempt the pipe recorded — app-written, never the request's text.
/// </summary>
public sealed record SnapPipeRefusal(long Id, DateTimeOffset At, string Event, string? Session,
    string? Op = null, string? Code = null, string? Role = null, string? Attempt = null);

/// <summary>A claim revision in the research ledger with no ledger link at that revision.</summary>
public sealed record SnapBareClaim(long EntryId, int Revision, DateTimeOffset At, string Author, string? Attempt);

/// <summary>A refused promotion of a version.</summary>
public sealed record SnapRefusedPromotion(string Id, string VersionId, DateTimeOffset At);

/// <summary>A backtest run of a version.</summary>
public sealed record SnapRun(string Id, string VersionId, DateTimeOffset CreatedAt, string? Role);

/// <summary>
/// THE ORGANISATION, AS THE APP'S OWN ROWS SAY IT WAS, AT ONE INSTANT — read on a <c>Mode=ReadOnly</c> connection to the app's database in
/// one read transaction, so the sweep CANNOT write there: no activity line, no engineering line, no wake, no publication. Every figure here
/// is an app measurement; the only organisation words it reads are ids and closed vocabulary — never a report's or a delivery's text.
/// </summary>
public sealed class OrgSnapshot
{
    /// <summary>How far back the snapshot reads time-bounded rows. Anything older was swept by an earlier window.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(30);

    /// <summary>The cursor sources this snapshot reads by id, because their tables are pruned or unbounded.</summary>
    public const string ToolCallSource = "tool_call", PipeLogSource = "engineering_log";

    /// <summary>The pipe's refusal events this line reads, as <c>GatewayPipeServer</c> writes them to the engineering log.</summary>
    /// <see cref="RefusalMark.Event"/> is every refusal of rights or authority the pipe's one handler records (<c>U-org-watcher-b</c>).
    public static readonly string[] PipeRefusalEvents = ["grant_rejected", "auth_rejected", "protocol_rejected", "operator_session_refused", RefusalMark.Event];

    public DateTimeOffset Now { get; private init; }
    public IReadOnlyList<SnapUnit> Units { get; private init; } = [];
    public IReadOnlyDictionary<string, string> PositionUnit { get; private init; } = new Dictionary<string, string>();
    public IReadOnlyList<SnapAttempt> Attempts { get; private init; } = [];
    public IReadOnlyList<SnapMandate> Mandates { get; private init; } = [];
    public IReadOnlyList<SnapMandateEvent> MandateEvents { get; private init; } = [];
    public IReadOnlyList<SnapDelivery> Deliveries { get; private init; } = [];
    public IReadOnlyList<SnapCheck> Checks { get; private init; } = [];
    public IReadOnlyList<SnapPublication> Publications { get; private init; } = [];
    public IReadOnlyList<SnapWake> Wakes { get; private init; } = [];
    public IReadOnlyList<SnapToolRefusal> ToolRefusals { get; private init; } = [];
    public IReadOnlyList<SnapPipeRefusal> PipeRefusals { get; private init; } = [];
    public IReadOnlyList<SnapBareClaim> BareClaims { get; private init; } = [];
    public IReadOnlyList<SnapRefusedPromotion> RefusedPromotions { get; private init; } = [];
    public IReadOnlyList<SnapRun> Runs { get; private init; } = [];

    /// <summary>The highest id read from each cursor source — where the next sweep starts.</summary>
    public IReadOnlyDictionary<string, long> Cursors { get; private init; } = new Dictionary<string, long>();

    Dictionary<string, SnapUnit> _byId = [];

    /// <summary>The unit with this id, or null.</summary>
    public SnapUnit? Unit(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The unit a role or position belongs to; a legacy null role is the chair's; an unknown one is the root's.</summary>
    public string UnitOfRole(string? role) =>
        PositionUnit.GetValueOrDefault(role ?? CouncilRoles.Default) ?? OrgIds.Root;

    /// <summary>The unit's path from the root, e.g. <c>org/div-research/team-x</c>. A unit the chart does not hold is under the root.</summary>
    public string PathOf(string unitId)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var u = Unit(unitId); u is not null && seen.Add(u.Id); u = u.ParentId is null ? null : Unit(u.ParentId)) parts.Add(u.Id);
        if (parts.Count == 0) parts.Add(unitId == OrgIds.Root ? OrgIds.Root : $"{OrgIds.Root}/{unitId}");
        else if (parts[^1] != OrgIds.Root) parts.Add(OrgIds.Root);
        parts.Reverse();
        return string.Join('/', parts);
    }

    /// <summary>Whether <paramref name="unitId"/> is <paramref name="scopeUnit"/> or lies under it.</summary>
    public bool InSubtree(string unitId, string scopeUnit) =>
        scopeUnit == OrgIds.Root || ("/" + PathOf(unitId) + "/").Contains("/" + scopeUnit + "/", StringComparison.Ordinal);

    /// <summary>
    /// READS THE SNAPSHOT from <paramref name="databaseFile"/> on a read-only connection, in one read transaction. Tool-call and pipe refusals
    /// are read past <paramref name="cursors"/>.
    /// </summary>
    public static OrgSnapshot Read(string databaseFile, DateTimeOffset now, IReadOnlyDictionary<string, long?> cursors)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        conn.Open();
        using (var t = conn.CreateCommand())
        {
            t.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON;";
            t.ExecuteNonQuery();
        }
        using var tx = conn.BeginTransaction(deferred: true);
        var horizon = Sql.T(now - Horizon);
        var tc = cursors.GetValueOrDefault(ToolCallSource) ?? 0;
        var pl = cursors.GetValueOrDefault(PipeLogSource) ?? 0;

        var units = Rows(conn, "SELECT id, parent_id, kind, status, created_at FROM org_unit ORDER BY id", [],
            r => new SnapUnit(r.GetString(0), S(r, 1), r.GetString(2), r.GetString(3), Sql.Time(r.GetString(4))));
        var positions = Rows(conn, "SELECT id, unit_id FROM org_position", [], r => (r.GetString(0), r.GetString(1)))
            .ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);
        var snap = new OrgSnapshot
        {
            Now = now,
            Units = units,
            PositionUnit = positions,
            _byId = units.ToDictionary(u => u.Id, StringComparer.Ordinal),
            Attempts = Rows(conn, """
                SELECT a.id, a.started_at, a.role, a.state, a.cost, a.reserved_cost, a.mandate_id,
                       (SELECT f.unit_id FROM ai_attempt_funding f WHERE f.attempt_id = a.id AND f.depth = 0)
                  FROM ai_attempt a WHERE a.started_at >= $h ORDER BY a.started_at, a.id
                """, [("$h", horizon)],
                r => new SnapAttempt(r.GetString(0), Sql.Time(r.GetString(1)), S(r, 2), r.GetString(3),
                    r.GetString(3) == "LAUNCHED" ? Dec(S(r, 5)) : Dec(S(r, 4)), S(r, 6),
                    S(r, 7) ?? positions.GetValueOrDefault(S(r, 2) ?? CouncilRoles.Default))),
            Mandates = Rows(conn, "SELECT id, unit_id, holder, decider, created_at FROM org_mandate ORDER BY created_at, id", [],
                r => new SnapMandate(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Sql.Time(r.GetString(4)))),
            MandateEvents = Rows(conn, "SELECT id, mandate_id, at, kind, actor, evidence FROM org_mandate_event ORDER BY id", [],
                r => new SnapMandateEvent(r.GetInt64(0), r.GetString(1), Sql.Time(r.GetString(2)), r.GetString(3), r.GetString(4),
                    Ids(r.GetString(5)))),
            Deliveries = Rows(conn, "SELECT id, mandate_id, refs, at FROM org_delivery ORDER BY at, id", [],
                r => new SnapDelivery(r.GetString(0), r.GetString(1), r.GetString(2), Sql.Time(r.GetString(3)))),
            Checks = Rows(conn, "SELECT delivery_id, check_name, passed FROM org_check ORDER BY delivery_id, check_name", [],
                r => new SnapCheck(r.GetString(0), r.GetString(1), r.GetInt64(2) == 1)),
            Publications = Rows(conn, "SELECT id, role, kind, created_at FROM publication ORDER BY created_at, id", [],
                r => new SnapPublication(r.GetString(0), r.GetString(1), r.GetString(2), Sql.Time(r.GetString(3)))),
            Wakes = Rows(conn, """
                SELECT id, kind, role, created_at, due_at, consumed_at, disposition FROM mission_event
                 WHERE created_at >= $h OR consumed_at IS NULL ORDER BY created_at, id
                """, [("$h", horizon)],
                r => new SnapWake(r.GetString(0), r.GetString(1), S(r, 2), Sql.Time(r.GetString(3)), Sql.Time(r.GetString(4)),
                    S(r, 5) is { } c ? Sql.Time(c) : null, S(r, 6))),
            ToolRefusals = Rows(conn, "SELECT id, at, attempt, role, tool FROM tool_call WHERE served = 0 AND id > $c ORDER BY id",
                [("$c", tc)], r => new SnapToolRefusal(r.GetInt64(0), Sql.Time(r.GetString(1)), S(r, 2), S(r, 3), r.GetString(4))),
            PipeRefusals = Rows(conn, $"""
                SELECT id, at, event, session, metadata FROM engineering_log
                 WHERE component = 'Ipc' AND event IN ({string.Join(", ", PipeRefusalEvents.Select(e => $"'{e}'"))}) AND id > $c ORDER BY id
                """, [("$c", pl)], r => PipeRefusal(r.GetInt64(0), Sql.Time(r.GetString(1)), r.GetString(2), S(r, 3), S(r, 4))),
            BareClaims = Rows(conn, """
                SELECT r.entry_id, r.revision, r.at, e.author, r.attempt FROM ledger_revision r JOIN ledger_entry e ON e.id = r.entry_id
                 WHERE r.mark = 'claim' AND r.at >= $h
                   AND NOT EXISTS (SELECT 1 FROM ledger_link l WHERE l.entry_id = r.entry_id AND l.revision = r.revision)
                 ORDER BY r.entry_id, r.revision
                """, [("$h", horizon)],
                r => new SnapBareClaim(r.GetInt64(0), r.GetInt32(1), Sql.Time(r.GetString(2)), r.GetString(3), S(r, 4))),
            RefusedPromotions = Rows(conn, "SELECT id, version_id, at FROM strategy_promotion WHERE verdict = 'refused' ORDER BY at, id", [],
                r => new SnapRefusedPromotion(r.GetString(0), r.GetString(1), Sql.Time(r.GetString(2)))),
            Runs = Rows(conn, """
                SELECT id, version_id, created_at, role FROM strategy_run
                 WHERE version_id IN (SELECT version_id FROM strategy_promotion WHERE verdict = 'refused') ORDER BY created_at, id
                """, [], r => new SnapRun(r.GetString(0), r.GetString(1), Sql.Time(r.GetString(2)), S(r, 3))),
            Cursors = new Dictionary<string, long>
            {
                [ToolCallSource] = Max(conn, "SELECT MAX(id) FROM tool_call") ?? tc,
                [PipeLogSource] = Max(conn, "SELECT MAX(id) FROM engineering_log") ?? pl,
            },
        };
        tx.Commit();
        return snap;
    }

    /// <summary>
    /// One pipe refusal; an <c>op_refused</c> row's metadata read in CLOSED VOCABULARY only — the op as one of <see cref="Ops"/>' names or
    /// <c>unknown</c>, the code as an <see cref="ErrorCode"/> name or <c>unknown</c> — so nothing but an id or a known word reaches a finding.
    /// </summary>
    static SnapPipeRefusal PipeRefusal(long id, DateTimeOffset at, string @event, string? session, string? metadata)
    {
        if (@event != RefusalMark.Event) return new SnapPipeRefusal(id, at, @event, session);
        string? Field(JsonElement m, string key) =>
            m.ValueKind == JsonValueKind.Object && m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        JsonElement meta = default;
        try { if (metadata is not null) meta = JsonDocument.Parse(metadata).RootElement.Clone(); }
        catch (JsonException) { }
        var code = Field(meta, "code");
        return new SnapPipeRefusal(id, at, @event, session, RefusalMark.OpName(Field(meta, "op")),
            code is not null && Enum.TryParse<ErrorCode>(code, ignoreCase: false, out var known) && Enum.IsDefined(known) ? known.ToString() : "unknown",
            Field(meta, "role"), Field(meta, "attempt"));
    }

    static List<T> Rows<T>(SqliteConnection c, string sql, (string, object?)[] ps, Func<SqliteDataReader, T> map)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        var rows = new List<T>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(map(r));
        return rows;
    }

    static long? Max(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() is long l ? l : null;
    }

    static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);

    static decimal Dec(string? s) =>
        s is not null && decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    static IReadOnlyList<string> Ids(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
