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

    /// <summary>
    /// ANOTHER ROLE'S WORK ARRIVED, delivered by the app. The two kinds are the two directions —
    /// <c>report</c> up to the chair, <c>brief</c> down to Research — and both are written ONLY by
    /// <see cref="PublicationStore.Commit"/>, in the same transaction as the artifact they are
    /// about. A role cannot raise one, so neither can hand itself work or a paid turn.
    /// </summary>
    public const string Report = PublicationKind.Report;

    /// <inheritdoc cref="Report"/>
    public const string Brief = PublicationKind.Brief;
}

/// <summary>
/// WHAT BECAME OF A WAKE.
///
/// <para>The first two are the outcomes of a TURN and are written after one has run. The other three
/// are outcomes the APP reached without one, and that is the whole reason they exist: an owner's
/// message the software delegated, refused for want of allowance, or replaced by the same words sent
/// again is a message with a real answer, and paying for a turn to say so would be spending the
/// owner's money to tell them what the app already knew (<c>docs/COUNCIL.md</c> rule 10, and round
/// 4's "Owner text enters Operations' agenda first, with receipt, disposition and deadline").</para>
/// </summary>
public static class MissionEventDisposition
{
    /// <summary>The turn that consumed it ended with a reply.</summary>
    public const string Answered = "answered";

    /// <summary>The turn that consumed it failed. An owner's message is re-raised once; see the store.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// THE APP TURNED IT INTO WORK FOR ANOTHER ROLE. Written by <c>CouncilRelay</c> when the turn
    /// that consumed the message published a brief, with that publication's id as the detail.
    ///
    /// <para>It is the strongest thing this software can say and it is a MEASURED co-occurrence, not
    /// a reading of the text: the same attempt consumed this wake and produced that artifact. The
    /// report says exactly that and no more.</para>
    /// </summary>
    public const string Delegated = "delegated";

    /// <summary>
    /// NOTHING COULD TAKE IT YET, and the detail is why — today the day's spending ceiling. The row
    /// stays UNCONSUMED, so the message is still owed a turn; this is a standing reason, replaced by
    /// the real outcome the moment one runs.
    /// </summary>
    public const string Blocked = "blocked";

    /// <summary>
    /// THE SAME WORDS ARRIVED AGAIN BEFORE ANYBODY LOOKED, and the later event's id is the detail.
    /// Only ever written on an EXACT text match, because "the same subject" is a judgment and this
    /// software is not entitled to make it — a message quietly superseded by one that merely looked
    /// similar is a question the owner asked and nobody answered.
    /// </summary>
    public const string Superseded = "superseded";
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

    /// <summary>
    /// WHAT THE DISPOSITION POINTS AT, where it points at something: the publication a message was
    /// delegated into, the later event that superseded it, the reason nothing could take it.
    ///
    /// A column rather than a suffix on <see cref="Disposition"/> because the disposition is a closed
    /// vocabulary a query filters on and the detail is free text a person reads; one field carrying
    /// both would make every future filter a LIKE.
    /// </summary>
    public string? DispositionDetail { get; init; }

    /// <summary>
    /// WHICH COUNCIL ROLE THIS WAKE IS FOR. Null on every row written before the council existed,
    /// and read as the chair's — see <see cref="CouncilRoles.Or"/>. A wake belongs to exactly one
    /// role: a fact that concerns both (new material, the day's renewal) is TWO rows with two ids,
    /// because one row consumed by one role would silently deny the other the turn it was owed.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>The role this wake is for, with a row that names none reading as the chair's.</summary>
    public string For => CouncilRoles.Or(Role);

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

    /// <summary>
    /// THE ID FOR ONE ROLE'S COPY OF A FACT THAT WAKES MORE THAN ONE OF THEM — new material, the
    /// day's renewal, the scheduled look, a delay a role asked for.
    ///
    /// The chair's copy keeps the bare id. That is not cosmetic: every id already written is the
    /// chair's, and suffixing it would make the whole existing queue look like a set of facts
    /// nobody had answered. A second role's copy is the same id with its name on it, so the pair is
    /// still a function of the fact and a replay of either is still free.
    /// </summary>
    public static string ForRole(string id, string role) =>
        role == CouncilRoles.Default ? id : $"{id}#{role}";

    /// <summary>The task a publication delivered to a role. Uniquely keyed BY THE PUBLICATION.</summary>
    public static string Task(string publicationId) => $"task:{publicationId}";
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
    const string Cols = EventCols;

    /// <summary>
    /// The row's columns, in order, so the ONE other writer of this table — the relay's transaction
    /// in <see cref="PublicationStore.Commit"/>, which has to insert an event and a publication
    /// together — spells them the same way this class does rather than keeping a second copy that
    /// can drift.
    /// </summary>
    internal const string EventCols =
        "id, kind, created_at, due_at, payload, consumed_at, consumed_by, disposition, role, "
        + "disposition_detail";

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
            VALUES($id,$kind,$created,$due,$payload,NULL,NULL,NULL,$role,NULL)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", e.Id), ("$kind", e.Kind), ("$created", Sql.T(e.CreatedAt)),
            ("$due", Sql.T(e.DueAt)), ("$payload", e.Payload), ("$role", e.Role));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// Raises an event that is due the moment it is written. The ordinary case: something happened.
    /// </summary>
    public bool Raise(string id, string kind, DateTimeOffset at, string? payload = null,
        string? role = null) =>
        Raise(new MissionEvent
        {
            Id = id, Kind = kind, CreatedAt = at, DueAt = at, Payload = payload, Role = role
        });

    /// <summary>
    /// Raises an event that becomes eligible later — a delay the AI asked for, the next review tick,
    /// tomorrow's renewal. <paramref name="at"/> is when it was decided and <paramref name="dueAt"/>
    /// is when it counts.
    /// </summary>
    public bool RaiseDue(string id, string kind, DateTimeOffset at, DateTimeOffset dueAt,
        string? payload = null, string? role = null) =>
        Raise(new MissionEvent
        {
            Id = id, Kind = kind, CreatedAt = at, DueAt = dueAt, Payload = payload, Role = role
        });

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
        // THE CHAIR'S, ALWAYS. docs/COUNCIL.md round 4: "Owner text enters Operations' agenda first,
        // with receipt, disposition and deadline; workers receive scoped briefs." A message routed
        // to whichever role happened to be next would be a person waiting on the research queue.
        var id = MissionEventIds.Owner(NextOwnerSequence());
        if (!Raise(id, MissionEventKind.Owner, at, Json.Write(new MissionOwnerMessage(text, at)),
                CouncilRoles.Operations)) return false;

        SupersedeIdentical(text, id);
        return true;
    }

    /// <summary>
    /// MARKS EVERY EARLIER MESSAGE THAT SAID EXACTLY THIS, AND THAT NOBODY HAS TAKEN, SUPERSEDED BY
    /// <paramref name="by"/>.
    ///
    /// <para>The owner pressing send twice is the case, and without this each copy buys its own paid
    /// turn to answer the same question. EXACT text only: "the same subject" is a judgment about
    /// meaning, and a message dropped because it merely resembled another is a question the owner
    /// asked that nobody ever answers. A consumed message is left alone — a turn already has it.</para>
    ///
    /// <para>It never throws. The message itself is safely written by the time this runs, and losing
    /// a de-duplication costs a turn where losing the words costs the owner their question.</para>
    /// </summary>
    void SupersedeIdentical(string text, string by)
    {
        try
        {
            foreach (var e in OfKind(MissionEventKind.Owner))
            {
                if (e.Id == by || e.Consumed || e.Disposition is not null) continue;
                if (Json.Read<MissionOwnerMessage>(e.Payload ?? "")?.Text != text) continue;
                SettleWithoutTurn(e.Id, MissionEventDisposition.Superseded, by);
            }
        }
        catch (Exception) { /* a de-duplication that did not happen costs a turn, not a message */ }
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
    /// EVERY DUE UNCONSUMED EVENT FOR ONE ROLE. What the serial scheduler hands to one turn: a turn
    /// runs for exactly one role, so it must never be told about — or charged for — a wake that
    /// belongs to the other.
    ///
    /// A row with no role is the chair's, which is why the filter is written out rather than
    /// expressed as <c>role = $role</c>: every event in an upgraded database has NULL there.
    /// </summary>
    public List<MissionEvent> DueFor(string role, DateTimeOffset now, int limit = BatchLimit) =>
        db.Read(_ =>
        {
            using var c = db.Cmd(
                $"SELECT {Cols} FROM mission_event WHERE consumed_at IS NULL AND due_at <= $now "
                + "AND COALESCE(role,$chair) = $role ORDER BY due_at, rowid LIMIT $limit",
                ("$now", Sql.T(now)), ("$role", role), ("$chair", CouncilRoles.Default),
                ("$limit", limit));
            using var r = c.ExecuteReader();
            var list = new List<MissionEvent>();
            while (r.Read()) list.Add(Read(r));
            return list;
        });

    /// <summary>
    /// EVERY ROLE WITH A DUE UNCONSUMED WAKE, THE ONE THAT HAS WAITED LONGEST FIRST. The serial
    /// scheduler's whole decision: one loop, one process at a time, and the oldest reason to work
    /// wins. A tie goes to insertion order — the chair, on any fact that woke both.
    ///
    /// A LIST rather than the single winner, because the winner may not be affordable: a role that
    /// has spent its slice of the day is skipped and the next one runs. Returning only the first
    /// would let one role's exhausted allowance stop the whole council until midnight, which is the
    /// opposite of what a share is for.
    /// </summary>
    public List<string> RolesDue(DateTimeOffset now) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COALESCE(role,$chair) AS r FROM mission_event "
            + "WHERE consumed_at IS NULL AND due_at <= $now GROUP BY r ORDER BY MIN(due_at), MIN(rowid)",
            ("$now", Sql.T(now)), ("$chair", CouncilRoles.Default));
        using var r = c.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
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

    /// <summary>The same question for one role, so the chair's pending review does not stand in for
    /// Research's and leave a role that is never scheduled at all.</summary>
    public bool HasUnconsumed(string kind, string role) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT 1 FROM mission_event WHERE kind=$k AND consumed_at IS NULL "
            + "AND COALESCE(role,$chair) = $role LIMIT 1",
            ("$k", kind), ("$role", role), ("$chair", CouncilRoles.Default));
        return c.ExecuteScalar() is not null;
    });

    /// <summary>The kind of the earliest unconsumed event, for the line the card shows.</summary>
    public string? NextKind() => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT kind FROM mission_event WHERE consumed_at IS NULL ORDER BY due_at, rowid LIMIT 1");
        return c.ExecuteScalar() as string;
    });

    /// <summary>Whose the earliest unconsumed event is, so a waiting card can name the role.</summary>
    public string? NextRole() => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COALESCE(role,$chair) FROM mission_event WHERE consumed_at IS NULL "
            + "ORDER BY due_at, rowid LIMIT 1", ("$chair", CouncilRoles.Default));
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
    /// RECORDS WHAT A TURN MADE OF A WAKE. Only a CONSUMED row is written, because a disposition on
    /// an event nobody has taken would be a verdict on a turn that never happened.
    ///
    /// <para><b>It never overwrites <see cref="MissionEventDisposition.Delegated"/>.</b> The relay
    /// runs BEFORE this — the file a turn wrote is published at the end of that turn — so without the
    /// guard the ordinary <c>answered</c> written a moment later would erase the one fact the owner
    /// most wants from a message they sent: which brief it became. Both are true of such a turn and
    /// the stronger one is kept, which also makes a re-run of the relay a no-op.</para>
    /// </summary>
    public bool Settle(string id, string disposition, string? detail = null) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE mission_event SET disposition=$d, disposition_detail=$x
             WHERE id=$id AND consumed_at IS NOT NULL
               AND (disposition IS NULL OR disposition <> $delegated)
            """,
            ("$d", disposition), ("$x", detail), ("$id", id),
            ("$delegated", MissionEventDisposition.Delegated));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// RECORDS AN OUTCOME THE APP REACHED WITHOUT A TURN — the three dispositions that exist for
    /// exactly that: <see cref="MissionEventDisposition.Delegated"/>,
    /// <see cref="MissionEventDisposition.Blocked"/>, <see cref="MissionEventDisposition.Superseded"/>.
    ///
    /// <para><b>It refuses <c>answered</c> and <c>failed</c>.</b> Those are claims about a turn, and a
    /// claim about a turn that never ran is the one thing this table must never hold: the whole
    /// ledger's value is that a disposition is evidence rather than an assumption.</para>
    ///
    /// <para>It writes only where nothing has settled the row yet, so it is idempotent and cannot
    /// erase a real outcome. The row is left CONSUMED or UNCONSUMED exactly as it was: a blocked
    /// message is still owed a turn, and this is the standing reason it has not had one.</para>
    /// </summary>
    public bool SettleWithoutTurn(string id, string disposition, string? detail = null)
    {
        if (disposition is MissionEventDisposition.Answered or MissionEventDisposition.Failed)
            throw new ArgumentException(
                $"'{disposition}' is what a TURN made of a wake and cannot be recorded without one",
                nameof(disposition));

        return db.Write(_ =>
        {
            using var c = db.Cmd("""
                UPDATE mission_event SET disposition=$d, disposition_detail=$x
                 WHERE id=$id AND disposition IS NULL
                """,
                ("$d", disposition), ("$x", detail), ("$id", id));
            return c.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>
    /// RECORDS THAT THE APP TURNED THE OWNER'S WORDS INTO WORK FOR ANOTHER ROLE, for every owner
    /// message the given launch consumed. Returns how many rows were written.
    ///
    /// <para>The link is a MEASUREMENT and nothing more: this attempt consumed those wakes and this
    /// attempt produced that publication. Nothing here reads the message or the brief, because
    /// deciding that a brief is ABOUT a message is a judgment, and a disposition that rests on one is
    /// not evidence. The report prints the pair and lets the owner draw the conclusion.</para>
    ///
    /// <para>The FIRST publication of a turn keeps the link, so re-running the relay over the same
    /// files writes nothing the second time.</para>
    /// </summary>
    public int Delegated(string attemptId, string publicationId) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE mission_event SET disposition=$d, disposition_detail=$pub
             WHERE kind=$owner AND consumed_by=$attempt
               AND (disposition IS NULL OR disposition <> $d)
            """,
            ("$d", MissionEventDisposition.Delegated), ("$pub", publicationId),
            ("$owner", MissionEventKind.Owner), ("$attempt", attemptId));
        return c.ExecuteNonQuery();
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
        Disposition = Sql.S(r.GetValue(7)),
        Role = Sql.S(r.GetValue(8)),
        DispositionDetail = Sql.S(r.GetValue(9))
    };
}
