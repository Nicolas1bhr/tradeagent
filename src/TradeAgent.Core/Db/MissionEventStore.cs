using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// The seven reasons the AI is allowed to be woken. Strings rather than an enum because the row is
/// read by a human as often as by this code, and an unknown kind arriving from a newer build must
/// read as itself rather than as whichever enum member happens to be zero.
/// </summary>
public static class MissionEventKind
{
    /// <summary>The owner typed something while the AI was working. See <see cref="MissionOwnerMessage"/>.</summary>
    public const string Owner = "owner";

    /// <summary>A scan pass recorded material that was not there before.</summary>
    public const string Inbox = "inbox";

    /// <summary>An execution reached the fill ledger.</summary>
    public const string Fill = "fill";

    /// <summary>A request reached a terminal state.</summary>
    public const string Order = "order";

    /// <summary>The local day turned over, so the spending allowance is a new one.</summary>
    public const string Renewal = "renewal";

    /// <summary>The AI asked to be woken later, in <c>next.json</c>.</summary>
    public const string Self = "self";

    /// <summary>Nothing happened and the owner still wants it to look. The heartbeat, and a setting.</summary>
    public const string Review = "review";
}

/// <summary>What became of a wake, once the turn that took it has ended.</summary>
public static class MissionEventDisposition
{
    /// <summary>The turn that consumed it ended with a reply.</summary>
    public const string Answered = "answered";

    /// <summary>The turn that consumed it failed. An owner's message is re-raised once; see the store.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// The payload of an <see cref="MissionEventKind.Owner"/> event: what the owner typed, when it was
/// received, and — on the one retry a failed turn gets — which event this one replaces and what
/// went wrong with it.
///
/// The text is HERE and nowhere else once an event exists for it. A copy kept in memory beside the
/// row is the defect this replaces: the queue was a list on <c>AgentSession</c>, so a restart lost
/// both the words and the receipt the owner had already been shown.
/// </summary>
public sealed record MissionOwnerMessage(
    string Text, DateTimeOffset ReceivedAt, string? RetryOf = null, string? Failure = null);

/// <summary>
/// ONE REASON THE AI MAY TAKE A TURN, as the app wrote it down.
///
/// <see cref="Id"/> is deterministic per source, so raising the same fact twice is a no-op rather
/// than a second paid turn — see <see cref="MissionEventIds"/>.
/// </summary>
public sealed record MissionEvent
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this becomes eligible. Equal to <see cref="CreatedAt"/> for anything already true.</summary>
    public DateTimeOffset DueAt { get; init; }

    /// <summary>Small JSON, or null. Never large: this table is a queue, not a store of documents.</summary>
    public string? Payload { get; init; }

    public DateTimeOffset? ConsumedAt { get; init; }

    /// <summary>The <c>ai_attempt</c> id of the launch this wake was given to.</summary>
    public string? ConsumedBy { get; init; }

    public string? Disposition { get; init; }

    public bool Consumed => ConsumedAt is not null;
}

/// <summary>
/// THE IDS, WHICH ARE THE DEDUPLICATION.
///
/// Every one of these is a function of the FACT rather than of the moment it was noticed, which is
/// what makes a replay free: a reconnect serves every execution again, a restart re-reads the same
/// scan, a bridge repeats an order transition. Each of those raises an id the table already holds
/// and buys nothing. An id minted from a clock or a Guid would have made every one of them a turn.
/// </summary>
public static class MissionEventIds
{
    public static string Owner(long sequence) =>
        $"{MissionEventKind.Owner}:{sequence.ToString(CultureInfo.InvariantCulture)}";

    public static string Inbox(DateTimeOffset scanAt) =>
        $"{MissionEventKind.Inbox}:{scanAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}";

    public static string Fill(string executionId) => $"{MissionEventKind.Fill}:{executionId}";

    public static string Order(string requestId, string state) => $"{MissionEventKind.Order}:{requestId}:{state}";

    /// <summary>The LOCAL day, because the allowance the renewal is about is measured on the owner's wall clock.</summary>
    public static string Renewal(DateTimeOffset localMidnight) =>
        $"{MissionEventKind.Renewal}:{localMidnight.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    public static string Self(string attemptId) => $"{MissionEventKind.Self}:{attemptId}";

    /// <summary>Rounded to the minute: two ticks scheduled inside one minute are one review.</summary>
    public static string Review(DateTimeOffset at) =>
        $"{MissionEventKind.Review}:{at.LocalDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// THE WAKE QUEUE: why the AI is running, written down before it runs and kept after it has.
///
/// <para><b>What it replaces.</b> <c>MissionLoop.AskedForDelay</c> returned <c>Zero</c> whenever the
/// AI had not written <c>next.json</c>, so the loop started another turn the instant one ended and
/// went on doing that for as long as the machine was on. The only thing that ever stopped it was the
/// day's cost ceiling, which means an agent with nothing to do spent the whole allowance finding
/// that out. <c>docs/COUNCIL.md</c> rule 7: justified idleness launches no inference.</para>
///
/// <para><b>Deduplicated by id, not by content.</b> Every id is a function of the fact — see
/// <see cref="MissionEventIds"/> — so <see cref="Raise"/> is idempotent and a replay of a day's
/// events launches nothing. That is the property the unit is built on, and it is why a raise
/// returns whether it was new rather than throwing on a repeat.</para>
///
/// <para><b>Consumed before the launch, in the launch's own transaction.</b>
/// <see cref="AiAttemptStore.Begin"/> takes the ids it is consuming and writes both in one commit,
/// so a kill between the two cannot hand the same wake to the next launch and be charged twice.</para>
///
/// <para><b>Written by the app only.</b> No verb, no pipe op, no path from the agent — the rule
/// <c>material</c> and <c>ai_attempt</c> already keep. An agent that could insert here could buy
/// itself turns; one that could delete here could erase what it was asked to do.</para>
/// </summary>
public sealed class MissionEventStore(Database db)
{
    const string Cols = "id, kind, created_at, due_at, payload, consumed_at, consumed_by, disposition";

    /// <summary>Events handed to one turn. A wake with more behind it leaves the rest for the next one.</summary>
    public const int BatchLimit = 50;

    /// <summary>
    /// Writes one event, or does nothing because that id is already known. Returns true only when
    /// the row is new — the caller that wants to nudge a sleeping loop uses that, so a repeat does
    /// not wake anything.
    /// </summary>
    public bool Raise(MissionEvent e) => db.Write(_ =>
    {
        using var c = db.Cmd($"""
            INSERT INTO mission_event({Cols})
            VALUES($id,$kind,$created,$due,$payload,NULL,NULL,NULL)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", e.Id), ("$kind", e.Kind), ("$created", Sql.T(e.CreatedAt)),
            ("$due", Sql.T(e.DueAt)), ("$payload", e.Payload));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// Raises an event that is due the moment it is written. The ordinary case: something happened.
    /// </summary>
    public bool Raise(string id, string kind, DateTimeOffset at, string? payload = null) =>
        Raise(new MissionEvent { Id = id, Kind = kind, CreatedAt = at, DueAt = at, Payload = payload });

    /// <summary>
    /// Raises an event that becomes eligible later — a delay the AI asked for, the next review tick,
    /// tomorrow's renewal. <paramref name="at"/> is when it was decided and <paramref name="dueAt"/>
    /// is when it counts.
    /// </summary>
    public bool RaiseDue(string id, string kind, DateTimeOffset at, DateTimeOffset dueAt, string? payload = null) =>
        Raise(new MissionEvent { Id = id, Kind = kind, CreatedAt = at, DueAt = dueAt, Payload = payload });

    /// <summary>
    /// WRITES WHAT THE OWNER TYPED WHILE THE AI WAS WORKING, with the next sequence number, and
    /// answers whether the row went in.
    ///
    /// One method rather than a raise at the call site, because the payload is the whole of what
    /// makes this worth doing: the ROW is the only copy once it exists — <c>AgentSession.Queue</c>
    /// stops holding the message in memory the moment this answers true — so an event written
    /// without the owner's words in it is a message that has been silently thrown away, with the
    /// receipt already shown.
    /// </summary>
    public bool RecordOwnerMessage(string text, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Raise(MissionEventIds.Owner(NextOwnerSequence()), MissionEventKind.Owner, at,
            Json.Write(new MissionOwnerMessage(text, at)));
    }

    /// <summary>
    /// EVERY UNCONSUMED EVENT THAT IS DUE, oldest first. An empty answer is the loop's whole reason
    /// not to spend money.
    /// </summary>
    public List<MissionEvent> Due(DateTimeOffset now, int limit = BatchLimit) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM mission_event WHERE consumed_at IS NULL AND due_at <= $now "
            + "ORDER BY due_at, rowid LIMIT $limit",
            ("$now", Sql.T(now)), ("$limit", limit));
        using var r = c.ExecuteReader();
        var list = new List<MissionEvent>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    /// <summary>
    /// When the next unconsumed event becomes eligible, or null when the queue holds none at all.
    /// What the loop sleeps until, and what the card says it is waiting for.
    /// </summary>
    public DateTimeOffset? NextDueAt() => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT MIN(due_at) FROM mission_event WHERE consumed_at IS NULL");
        return Sql.TimeN(c.ExecuteScalar());
    });

    /// <summary>
    /// Whether a wake of this kind is already queued and untaken. What keeps the scheduled kinds —
    /// the review tick and the day's renewal — to exactly one pending row each: without it every
    /// early wake would leave another review behind it, and a burst of owner messages would buy a
    /// trickle of paid reviews over the following half hour.
    /// </summary>
    public bool HasUnconsumed(string kind) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT 1 FROM mission_event WHERE kind=$k AND consumed_at IS NULL LIMIT 1", ("$k", kind));
        return c.ExecuteScalar() is not null;
    });

    /// <summary>The kind of the earliest unconsumed event, for the line the card shows.</summary>
    public string? NextKind() => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT kind FROM mission_event WHERE consumed_at IS NULL ORDER BY due_at, rowid LIMIT 1");
        return c.ExecuteScalar() as string;
    });

    /// <summary>
    /// Marks events consumed on their own, for a caller with no attempt to open — a turn the
    /// ceiling refused, a host with no meter behind it. The launch path uses
    /// <see cref="AiAttemptStore.Begin"/> instead, which does both in one commit.
    /// </summary>
    public int Consume(IReadOnlyList<string> ids, string attemptId, DateTimeOffset at) =>
        db.Write(_ => MarkConsumed(db, ids, attemptId, at));

    /// <summary>
    /// The UPDATE itself, with no transaction of its own, so it can be committed together with the
    /// <c>ai_attempt</c> row. Only an unconsumed row is written: a wake already given to one launch
    /// is never re-assigned to another.
    /// </summary>
    internal static int MarkConsumed(Database db, IReadOnlyList<string> ids, string attemptId, DateTimeOffset at)
    {
        var written = 0;
        foreach (var id in ids)
        {
            using var c = db.Cmd("""
                UPDATE mission_event SET consumed_at=$at, consumed_by=$by
                 WHERE id=$id AND consumed_at IS NULL
                """, ("$at", Sql.T(at)), ("$by", attemptId), ("$id", id));
            written += c.ExecuteNonQuery();
        }
        return written;
    }

    /// <summary>
    /// Records what became of a wake. Only a CONSUMED row is written, because a disposition on an
    /// event nobody has taken would be a verdict on a turn that never happened.
    /// </summary>
    public bool Settle(string id, string disposition) => db.Write(_ =>
    {
        using var c = db.Cmd(
            "UPDATE mission_event SET disposition=$d WHERE id=$id AND consumed_at IS NOT NULL",
            ("$d", disposition), ("$id", id));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>One event by id, or null.</summary>
    public MissionEvent? Get(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM mission_event WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>Every event of a kind, oldest first. For the screens and for a test reading rows back.</summary>
    public List<MissionEvent> OfKind(string kind) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM mission_event WHERE kind=$k ORDER BY created_at, rowid",
            ("$k", kind));
        using var r = c.ExecuteReader();
        var list = new List<MissionEvent>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    /// <summary>
    /// The next sequence number for an owner's message. Taken from the ids already written rather
    /// than from a counter kept elsewhere, so the table is the only thing that has to survive.
    /// </summary>
    public long NextOwnerSequence() => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COALESCE(MAX(CAST(substr(id, 7) AS INTEGER)), 0) FROM mission_event WHERE kind=$k",
            ("$k", MissionEventKind.Owner));
        var o = c.ExecuteScalar();
        return (o is null or DBNull ? 0L : Convert.ToInt64(o, CultureInfo.InvariantCulture)) + 1;
    });

    static MissionEvent Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Kind = r.GetString(1),
        CreatedAt = Sql.Time(r.GetValue(2)),
        DueAt = Sql.Time(r.GetValue(3)),
        Payload = Sql.S(r.GetValue(4)),
        ConsumedAt = Sql.TimeN(r.GetValue(5)),
        ConsumedBy = Sql.S(r.GetValue(6)),
        Disposition = Sql.S(r.GetValue(7))
    };
}
