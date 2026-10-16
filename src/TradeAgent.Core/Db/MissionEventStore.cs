using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>
/// The reasons the AI is allowed to be woken. Strings rather than an enum because the row is
/// read by a human as often as by this code, and an unknown kind arriving from a newer build must
/// read as itself rather than as whichever enum member happens to be zero.
/// </summary>
public static class MissionEventKind
{
    /// <summary>The owner typed something while the AI was working. See <see cref="MissionOwnerMessage"/>.</summary>
    public const string Owner = "owner";

    /// <summary>
    /// A scan pass recorded material that ARRIVED in the owner's <c>inbox/</c>
    /// (<see cref="ScanResult.Arrived"/>). Never a role's own write and never the app's own file, so
    /// the words a turn is told — "new material arrived in <c>../inbox</c>" — are true by construction.
    /// </summary>
    public const string Inbox = "inbox";

    /// <summary>
    /// VALIDATED DATA ARRIVED — an ACCEPTED dataset the AI has never been told about.
    ///
    /// <para><c>docs/COUNCIL.md</c>:89-91 lists "validated data arrival" among the persisted events a
    /// role may be woken for, and until this unit nothing raised one: the owner could collect twelve
    /// months of history and the research process would find out at its next scheduled look, or not at
    /// all. Written only by <c>MarketDataService</c>, which is the app's own collector and has no verb
    /// and no pipe op behind it, and raised for Research — the role whose work it is.</para>
    ///
    /// <para>VALIDATED is the operative word. A REJECTED dataset raises nothing, and neither does a
    /// re-verification: <see cref="DatasetStore.Checked"/> re-hashes a row on every read and finding it
    /// unchanged is not news. The id is the dataset's own SHA-256 — the ENTITY
    /// (<c>docs/COUNCIL.md</c>:64) — so a rebuild that reproduces the same bytes raises an id the table
    /// already holds and buys nobody a turn.</para>
    /// </summary>
    public const string Data = "data";

    /// <summary>An execution reached the fill ledger.</summary>
    public const string Fill = "fill";

    /// <summary>A request reached a terminal state.</summary>
    public const string Order = "order";

    /// <summary>The local day turned over, so the spending allowance is a new one.</summary>
    public const string Renewal = "renewal";

    /// <summary>The AI asked to be woken later, in <c>next.json</c>.</summary>
    public const string Self = "self";

    /// <summary>
    /// Nothing happened and the owner still wants it to look. The heartbeat, and a setting — the
    /// fastest it comes round: it slows while only looks wake a role (<c>MissionLoop.NextLook</c>).
    /// </summary>
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

    /// <summary>
    /// THE REFEREE ANSWERED. Written only by <see cref="PublicationStore.Commit"/>, in the same
    /// transaction as the promotion and the note that carries it, and keyed by the PROMOTION — the
    /// entity — so a repeated delivery of one judgement buys no second turn
    /// (<c>docs/COUNCIL.md</c>:64, "deduplicate boundary events by entity and revision, so repeated
    /// proposals cannot manufacture senior spend").
    /// </summary>
    public const string Verdict = PublicationKind.Verdict;

    /// <summary>
    /// A CONSEQUENTIAL BOUNDARY OPENED, and this role owes it an assessment.
    ///
    /// <para>Written only by <see cref="CouncilBoundaries.Open"/>, in the same transaction as the
    /// boundary row, and keyed by the boundary — whose own id is <c>kind:entity:revision</c>. That is
    /// <c>docs/COUNCIL.md</c>:64 twice over: the boundary is deduplicated by entity and revision, and the
    /// wake is deduplicated by the boundary, so a re-proposed promotion raises an id the table already
    /// holds and buys neither director a turn.</para>
    ///
    /// <para>TWO rows per boundary, one per role: one row consumed by whichever director reached it
    /// first would silently deny the other the turn the protocol requires it to take. (New material is
    /// ONE row, the chair's — <c>AppHost.InboxWake</c>; each role's Situation lists it since that role's
    /// own last turn.) "Two assessments are two turns" (:63) is a count of what the owner
    /// pays for, and these are the two.</para>
    /// </summary>
    public const string Boundary = "boundary";

    /// <summary>
    /// A CONSEQUENTIAL BOUNDARY'S DEADLINE IS NEAR AND THIS DIRECTOR HAS NOT ASSESSED IT (<c>U-reconcile-packet</c>).
    ///
    /// <para>Raised by the loop's own schedule, never by an agent: ONE row per boundary and director, keyed
    /// <see cref="MissionEventIds.Deadline"/>, due a quarter of the boundary's window before its deadline, and closed
    /// without a turn (<see cref="MissionEventStore.WithdrawDeadline"/>) once the director has assessed it or the
    /// boundary is settled. The boundary's opening wake is the first call; this is the one reminder, because a director
    /// that missed it would otherwise leave the decision to the app's default, and <c>next.json</c> cannot reach a
    /// deadline more than half an hour away.</para>
    /// </summary>
    public const string Deadline = "deadline";

    /// <summary>
    /// THE TAPE STORED SOMETHING NEW THAT RESEARCH OWNS (<c>U-reconcile-packet</c>): an exchange announcement, or a
    /// reading of a series an active deployment of one of its versions declares as a feature input — and only one
    /// whose source time is outside every holdout window the role's own reads are refused.
    ///
    /// <para>Raised by the app through the collector's delegate, never by an agent, and COALESCED: at most one untaken
    /// row per role (<see cref="MissionEventStore.RaiseCoalesced"/>), so a busy series buys at most one turn per turn
    /// taken. Its payload is <see cref="TapeWakePayload"/> — source, series, subject and count — and never an item's
    /// text: a tape item is untrusted data, and the turn reads it with the tape's own read, under the holdout. The holdout
    /// is asked again when the wake is admitted and when its packet is rendered (<see cref="TapeWakeRow"/>).</para>
    /// </summary>
    public const string Tape = "tape";

    /// <summary>
    /// A POSITION'S OUTPUT THE APP HELD, AND NEVER A WAKE (<c>U-org-rights</c>). A position after the legacy pair has no
    /// recipient until <c>U-org-assignments</c> gives it a mandate's issuer, so what it stages in <c>out/</c> passes the
    /// relay's fence, is recorded as the app's own copy and published to nobody — and this row is that disposition. It
    /// is written CONSUMED, by no launch (<see cref="MissionEventStore.Hold"/>), so it is never due, buys nobody a turn and
    /// makes no launch look woken; the writer's next Situation reads it (<c>CouncilRelay.HeldNotices</c>).
    /// </summary>
    public const string Output = "output";
}

/// <summary>
/// WHAT A <see cref="MissionEventKind.Tape"/> WAKE SAYS, AND ALL IT SAYS: which source and series, which subject when the
/// new items are all of one (null when they span several), and how many — never an item's text, its URL or a figure.
/// </summary>
public sealed record TapeWakePayload(string Source, string Series, string? Subject, int Count);

/// <summary>
/// WHAT A <see cref="MissionEventKind.Tape"/> ROW HOLDS: what the wake says (<see cref="TapeWakePayload"/>'s four fields,
/// which read it back) and, for the app alone, each counted item's subject and source time — never its text — so the
/// holdout is asked again when the wake is admitted and when its packet is rendered (<c>U-reconcile-packet</c>, review
/// 1): a window declared after the raise takes the items it holds out of the count, and a wake it holds entirely is
/// closed without a turn (<see cref="MissionEventStore.WithdrawTape"/>). Never rendered.
/// </summary>
public sealed record TapeWakeRow(string Source, string Series, string? Subject, int Count, IReadOnlyList<TapeArrived> Items);

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
    ///
    /// <para>Also what a scheduled look reads when the app replaced it with a sooner one
    /// (<see cref="MissionEventStore.BringLookForward"/>): two looks carry no words at all, so the
    /// match is exact by construction, and the sooner look's id is the detail.</para>
    /// </summary>
    public const string Superseded = "superseded";

    /// <summary>
    /// A SCHEDULED LOOK THE APP CLOSED BECAUSE THE ROLE IT WAS FOR HELD NO OPEN WORK
    /// (<see cref="MissionEventStore.WithdrawLooks"/>, <c>U-reconcile-wakes</c>). Consumed by no launch, so nothing can
    /// serve it, and the detail says why. Only two of the app's own wakes are ever closed this way: its payload-less look
    /// — a look is the app paying for the clock, and a role that owns nothing open has nothing for the clock to find — and
    /// a deadline wake whose director has assessed the boundary or whose boundary is settled
    /// (<see cref="MissionEventStore.WithdrawDeadline"/>, <c>U-reconcile-packet</c>).
    /// </summary>
    public const string Withdrawn = "withdrawn";

    /// <summary>
    /// AN OUTPUT PUBLISHED TO NOBODY (<see cref="MissionEventKind.Output"/>, <c>U-org-rights</c>): a position after the
    /// legacy pair staged it, the relay's fence attributed it to that position's launch, and it was recorded and handed to
    /// no one, because no recipient exists for it until <c>U-org-assignments</c>. The detail says so. An outcome the app
    /// reached without a turn, like <see cref="Withdrawn"/>; never a claim about a turn.
    /// </summary>
    public const string Held = "held";
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

    /// <summary>
    /// The position this wake is for, with a row that names none reading as the chair's — and a row that names one
    /// reading as exactly that one, never folded into the chair (<c>U-org-principals</c>).
    /// </summary>
    public string For => Role ?? CouncilRoles.Default;

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

    /// <summary>
    /// THE DATA WAKE, KEYED BY THE DATASET'S OWN SHA-256 AND NEVER BY THE PRESS THAT PRODUCED IT.
    ///
    /// <para>The normalised file's hash IS the dataset's identity — it is what the ledger re-reads to
    /// decide whether the row still stands — so a rebuild of the same raw files raises the id that is
    /// already there, which is what makes "the same evidence, delivered twice" free. Keyed by the
    /// collection attempt, an owner pressing rebuild four times would have bought four paid turns to
    /// be told the same thing (<c>docs/COUNCIL.md</c>:64).</para>
    /// </summary>
    public static string Data(string normalisedSha256) => $"{MissionEventKind.Data}:{normalisedSha256}";

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

    /// <summary>
    /// THE HOLD OF ONE PUBLICATION (<see cref="MissionEventKind.Output"/>): keyed by the publication, whose id already
    /// hashes its role, kind and text, so a re-run of the relay over the same file holds it once.
    /// </summary>
    public static string Held(string publicationId) => $"{MissionEventKind.Output}:{publicationId}";

    /// <summary>
    /// THE VERDICT WAKE, KEYED BY THE PROMOTION AND NOT BY THE NOTE THAT CARRIES IT.
    ///
    /// <para>The promotion is the ENTITY (<c>docs/COUNCIL.md</c>:64): its id is already the hash of the
    /// evidence the verdict rested on, so a second delivery of the same judgement — a retry, a restart,
    /// a later build re-reading the ledger — raises an id the table already holds and costs nobody a
    /// turn. Keying it by the publication would have been one step further from the fact.</para>
    /// </summary>
    public static string Verdict(string promotionId) => $"{MissionEventKind.Verdict}:{promotionId}";

    /// <summary>
    /// THE PAPER-ALLOCATION WAKE, KEYED BY THE ALLOCATION AND NOT BY THE SWEEP THAT WROTE IT.
    ///
    /// <para>The allocation is the ENTITY (<c>docs/COUNCIL.md</c>:64), and its id is already the hash
    /// of the version, the ceiling, the platform, the account and the grant it was written under — so
    /// the app's policy running again on the next tick, after a restart, or straight after a verdict
    /// raises an id the table already holds and costs nobody a turn. Keyed by the sweep, an
    /// installation left running would have bought a paid turn every five seconds to be told the same
    /// thing.</para>
    /// </summary>
    public static string PaperAllocation(string allocationId) =>
        $"{PublicationKind.Note}:paper-allocation:{allocationId}";

    /// <summary>
    /// THE FORWARD-RUN WAKE, KEYED BY THE DEPLOYMENT AND THE OCCASION — a UTC day that closed over it,
    /// or the run's end.
    ///
    /// <para>The deployment is the ENTITY and the occasion is what there is to say about it, so a run
    /// that spans a week buys one turn a day and one at the end, and a pass that re-reads the same
    /// week raises ids the table already holds. Keyed by the deployment alone it would say the same
    /// thing once and never again; keyed by the sweep it would say it every five seconds.</para>
    /// </summary>
    public static string PaperRun(string deploymentId, string occasion) =>
        $"{PublicationKind.Note}:paper-run:{deploymentId}:{occasion}";

    /// <summary>The <paramref name="day"/> a forward run's figures are for, in UTC. See <see cref="PaperRun"/>.</summary>
    public static string DayOf(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// THE BOUNDARY WAKE, KEYED BY THE BOUNDARY — whose own id is already the entity and the revision
    /// (<see cref="BoundaryIds.Of"/>). Wrapped per role by <see cref="ForRole"/>, so the pair is still a
    /// function of the fact and a repeat of either is still free.
    /// </summary>
    public static string Boundary(string boundaryId) => $"{MissionEventKind.Boundary}:{boundaryId}";

    /// <summary>
    /// THE DEADLINE WAKE, KEYED BY THE BOUNDARY AND THE DIRECTOR — <c>deadline:&lt;boundary&gt;:&lt;role&gt;</c> — so each
    /// director is reminded once per boundary, and a schedule that runs every pass raises an id the table already holds.
    /// </summary>
    public static string Deadline(string boundaryId, string role) => $"{MissionEventKind.Deadline}:{boundaryId}:{role}";

    /// <summary>
    /// THE TAPE WAKE, KEYED BY THE FETCH THAT STORED WHAT IT IS ABOUT (<c>TapeAppend.FetchId</c>). Coalesced on top of
    /// that by the store, so a second fetch while one is untaken raises nothing at all.
    /// </summary>
    public static string Tape(long fetchId) =>
        $"{MissionEventKind.Tape}:{fetchId.ToString(CultureInfo.InvariantCulture)}";
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
    ///
    /// <para><b>THE OWNER'S WORDS FIRST, then the oldest</b> (pass 3, item 3). A turn is handed at most
    /// <paramref name="limit"/> wakes, and a backlog older than a message — fills recorded over a downtime, or the
    /// retry of words a lost turn took, written at its recovery — would otherwise push the owner's words out of the
    /// very turn that has to answer them first.</para>
    /// </summary>
    public List<MissionEvent> DueFor(string role, DateTimeOffset now, int limit = BatchLimit) =>
        db.Read(_ =>
        {
            using var c = db.Cmd(
                $"SELECT {Cols} FROM mission_event WHERE consumed_at IS NULL AND due_at <= $now "
                + "AND COALESCE(role,$chair) = $role ORDER BY (kind = $owner) DESC, due_at, rowid LIMIT $limit",
                ("$now", Sql.T(now)), ("$role", role), ("$chair", CouncilRoles.Default),
                ("$owner", MissionEventKind.Owner), ("$limit", limit));
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

    /// <summary>
    /// RAISES <paramref name="e"/> ONLY WHEN ITS ROLE HAS NO UNTAKEN WAKE OF ITS KIND, and answers whether it went in
    /// (<c>U-reconcile-packet</c>). One statement, inside the write lock, so two collectors' loops storing at the same
    /// instant cannot both find none and both raise: this is what makes a busy tape one paid turn per turn taken.
    /// </summary>
    public bool RaiseCoalesced(MissionEvent e) => db.Write(_ =>
    {
        using var c = db.Cmd($"""
            INSERT INTO mission_event({Cols})
            SELECT $id,$kind,$created,$due,$payload,NULL,NULL,NULL,$role,NULL
             WHERE NOT EXISTS (SELECT 1 FROM mission_event
                                WHERE kind=$kind AND consumed_at IS NULL AND COALESCE(role,$chair) = $for)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", e.Id), ("$kind", e.Kind), ("$created", Sql.T(e.CreatedAt)), ("$due", Sql.T(e.DueAt)),
            ("$payload", e.Payload), ("$role", e.Role), ("$chair", CouncilRoles.Default), ("$for", e.For));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// CLOSES ONE UNTAKEN <see cref="MissionEventKind.Deadline"/> WAKE WITHOUT A TURN, consumed by no launch and
    /// <see cref="MissionEventDisposition.Withdrawn"/> with <paramref name="why"/> — the director has assessed the
    /// boundary, or it is settled. The guard is the statement's: only an untaken deadline wake is ever closed here.
    /// </summary>
    public bool WithdrawDeadline(string id, DateTimeOffset at, string why) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE mission_event SET consumed_at=$at, disposition=$withdrawn, disposition_detail=$why
             WHERE id=$id AND kind=$deadline AND consumed_at IS NULL
            """,
            ("$at", Sql.T(at)), ("$withdrawn", MissionEventDisposition.Withdrawn), ("$why", why),
            ("$id", id), ("$deadline", MissionEventKind.Deadline));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// CLOSES ONE UNTAKEN <see cref="MissionEventKind.Tape"/> WAKE WITHOUT A TURN, consumed by no launch and
    /// <see cref="MissionEventDisposition.Withdrawn"/> with <paramref name="why"/> — a holdout window now holds every item
    /// it counted, or the windows could not be read (<c>U-reconcile-packet</c>, review 1). The guard is the statement's:
    /// only an untaken tape wake is ever closed here.
    /// </summary>
    public bool WithdrawTape(string id, DateTimeOffset at, string why) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE mission_event SET consumed_at=$at, disposition=$withdrawn, disposition_detail=$why
             WHERE id=$id AND kind=$tape AND consumed_at IS NULL
            """,
            ("$at", Sql.T(at)), ("$withdrawn", MissionEventDisposition.Withdrawn), ("$why", why),
            ("$id", id), ("$tape", MissionEventKind.Tape));
        return c.ExecuteNonQuery() == 1;
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
    /// BRINGS ONE ROLE'S PENDING SCHEDULED LOOK FORWARD TO <paramref name="dueAt"/>, or does nothing
    /// and answers false (<c>U-quiet-review</c>).
    ///
    /// <para>The loop raises a role's next look further out while only looks wake it, and a real
    /// event brings it back. A look's id is its due minute (<see cref="MissionEventIds.Review"/>), so
    /// it is not re-dated in place: the sooner look is raised under its own <paramref name="id"/>, and
    /// the later one is closed in the SAME transaction — consumed by no launch, and
    /// <see cref="MissionEventDisposition.Superseded"/> by the sooner one — so its row still says what
    /// became of it and nothing can serve it again. Both land or neither does.</para>
    ///
    /// <para><b>The guard is the statement's, not only the caller's.</b> Only an unconsumed
    /// <see cref="MissionEventKind.Review"/> with NO payload, for <paramref name="role"/>, due later
    /// than <paramref name="dueAt"/>, is ever closed. The owner's words, a delivery, a fill, an order,
    /// the renewal, a role's own request, a boundary, the owner's own press — a review WITH a payload
    /// — and the other role's look all keep their timing, whatever a caller asks.</para>
    /// </summary>
    /// <returns>True when a later look was replaced; false when there was none, or when the sooner
    /// look's id is already taken — and then nothing was written.</returns>
    public bool BringLookForward(string role, string id, DateTimeOffset at, DateTimeOffset dueAt) =>
        db.Write(_ =>
        {
            var later = new List<string>();
            using (var c = db.Cmd("""
                SELECT id FROM mission_event
                 WHERE kind=$review AND payload IS NULL AND consumed_at IS NULL
                   AND COALESCE(role,$chair) = $role AND due_at > $due
                """,
                ("$review", MissionEventKind.Review), ("$chair", CouncilRoles.Default), ("$role", role),
                ("$due", Sql.T(dueAt))))
            using (var r = c.ExecuteReader())
                while (r.Read()) later.Add(r.GetString(0));

            if (later.Count == 0) return false;
            if (!Raise(new MissionEvent
                {
                    Id = id, Kind = MissionEventKind.Review, CreatedAt = at, DueAt = dueAt, Role = role
                }))
                return false;

            foreach (var old in later)
            {
                using var u = db.Cmd("""
                    UPDATE mission_event SET consumed_at=$at, disposition=$superseded, disposition_detail=$by
                     WHERE id=$id AND kind=$review AND payload IS NULL AND consumed_at IS NULL
                    """,
                    ("$at", Sql.T(at)), ("$superseded", MissionEventDisposition.Superseded), ("$by", id),
                    ("$id", old), ("$review", MissionEventKind.Review));
                u.ExecuteNonQuery();
            }
            return true;
        });

    /// <summary>
    /// CLOSES <paramref name="role"/>'S PENDING SCHEDULED LOOKS, consumed by no launch and
    /// <see cref="MissionEventDisposition.Withdrawn"/> with <paramref name="why"/>, and answers how many
    /// (<c>U-reconcile-wakes</c>). The loop calls it for a role that holds no open objective, so a look raised while
    /// it had work is not paid for after the work is done.
    ///
    /// <para><b>The guard is the statement's</b>, as <see cref="BringLookForward"/>'s is: only an unconsumed
    /// <see cref="MissionEventKind.Review"/> with NO payload, for this role. The owner's words, a delivery, a fill, an
    /// order, the renewal, a role's own request, a boundary, the owner's own press — a review WITH a payload — and the
    /// other role's look are never touched, whatever a caller asks.</para>
    /// </summary>
    public int WithdrawLooks(string role, DateTimeOffset at, string why) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE mission_event SET consumed_at=$at, disposition=$withdrawn, disposition_detail=$why
             WHERE kind=$review AND payload IS NULL AND consumed_at IS NULL
               AND COALESCE(role,$chair) = $role
            """,
            ("$at", Sql.T(at)), ("$withdrawn", MissionEventDisposition.Withdrawn), ("$why", why),
            ("$review", MissionEventKind.Review), ("$chair", CouncilRoles.Default), ("$role", role));
        return c.ExecuteNonQuery();
    });

    /// <summary>
    /// THE OWNER'S MESSAGES TO <paramref name="role"/> THAT A TURN CAN STILL CLOSE, oldest first (review 1, finding
    /// 2): every one no turn has taken — a due row is the next turn's, whatever standing reason it carries — and every
    /// one a launch took that has no outcome yet while that launch is still LAUNCHED (its turn's commit settles it)
    /// or a restart declared it LOST (the loop re-raises its words, <see cref="LostOwnerWords"/>). A message taken by
    /// a launch that ENDED without an outcome, or by none the ledger holds, is not here: no turn can close it, and the
    /// loop settles it <c>failed</c> and re-raises its words once (<see cref="UnansweredOwnerWords"/>), so the retry is.
    /// </summary>
    public List<MissionEvent> OwnerWordsOpen(string role) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {Cols} FROM mission_event e
             WHERE e.kind = $owner AND COALESCE(e.role, $chair) = $role
               AND (e.consumed_at IS NULL
                    OR ((e.disposition IS NULL OR e.disposition = $blocked)
                        AND EXISTS (SELECT 1 FROM ai_attempt a WHERE a.id = e.consumed_by
                                     AND a.state IN ('LAUNCHED', 'LOST'))))
             ORDER BY e.created_at, e.rowid
            """,
            ("$owner", MissionEventKind.Owner), ("$chair", CouncilRoles.Default), ("$role", role),
            ("$blocked", MissionEventDisposition.Blocked));
        using var r = c.ExecuteReader();
        var list = new List<MissionEvent>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    /// <summary>
    /// THE OWNER'S MESSAGES A LOST TURN TOOK: consumed by a launch a restart declared LOST
    /// (<see cref="AiAttemptStore.LoseOpen"/>), with no outcome written. The loop settles each one
    /// <see cref="MissionEventDisposition.Failed"/> — the turn that took it ran and was lost — and re-raises its words
    /// once, as it does a failed turn's (<see cref="SettleLost"/>).
    /// </summary>
    public List<MissionEvent> LostOwnerWords() => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {Cols} FROM mission_event e
             WHERE e.kind = $owner AND e.consumed_at IS NOT NULL
               AND (e.disposition IS NULL OR e.disposition = $blocked)
               AND EXISTS (SELECT 1 FROM ai_attempt a WHERE a.id = e.consumed_by AND a.state = 'LOST')
             ORDER BY e.created_at, e.rowid
            """, ("$owner", MissionEventKind.Owner), ("$blocked", MissionEventDisposition.Blocked));
        using var r = c.ExecuteReader();
        var list = new List<MissionEvent>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    /// <summary>
    /// SETTLES ONE MESSAGE A LOST TURN TOOK <see cref="MissionEventDisposition.Failed"/>, with
    /// <paramref name="detail"/>, and answers whether THIS call did it. Only a row <see cref="LostOwnerWords"/> would
    /// still return is written, so two passes racing over it settle it once and only the winner re-raises the words.
    /// It is a turn's outcome, not the app's: the launch ran and was lost, which is what <c>failed</c> says.
    /// </summary>
    public bool SettleLost(string id, string detail) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE mission_event SET disposition = $failed, disposition_detail = $detail
             WHERE id = $id AND kind = $owner AND consumed_at IS NOT NULL
               AND (disposition IS NULL OR disposition = $blocked)
               AND EXISTS (SELECT 1 FROM ai_attempt a WHERE a.id = mission_event.consumed_by AND a.state = 'LOST')
            """,
            ("$failed", MissionEventDisposition.Failed), ("$detail", detail), ("$id", id),
            ("$owner", MissionEventKind.Owner), ("$blocked", MissionEventDisposition.Blocked));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// THE OWNER'S MESSAGES A FINISHED TURN LEFT WITHOUT AN OUTCOME (<c>U-owner-words-delegated</c>): consumed by a
    /// launch the ledger holds as ENDED — whose one committed transition (its close, its relay pass, its dispositions)
    /// has already landed — or by a launch with no row at all, a turn whose record could not be written, and settled
    /// by nothing. No turn can close one any more, so the loop settles it <see cref="MissionEventDisposition.Failed"/>
    /// and re-raises its words once, as it does a lost turn's (<see cref="SettleUnanswered"/>). Read off the app's own
    /// two ledgers; nothing an agent writes puts a message here or takes one out.
    /// </summary>
    public List<MissionEvent> UnansweredOwnerWords() => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {Cols} FROM mission_event e
             WHERE {Unanswered("e")}
             ORDER BY e.created_at, e.rowid
            """, ("$owner", MissionEventKind.Owner), ("$blocked", MissionEventDisposition.Blocked));
        using var r = c.ExecuteReader();
        var list = new List<MissionEvent>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    /// <summary>
    /// SETTLES ONE MESSAGE <see cref="UnansweredOwnerWords"/> WOULD STILL RETURN <see cref="MissionEventDisposition.Failed"/>,
    /// with <paramref name="detail"/>, and answers whether THIS call did it — so two passes racing over it settle it
    /// once and only the winner re-raises the words. A turn's outcome, not the app's: the launch took the words and
    /// ended without saying what became of them, which is what <c>failed</c> says.
    /// </summary>
    public bool SettleUnanswered(string id, string detail) => db.Write(_ =>
    {
        using var c = db.Cmd($"""
            UPDATE mission_event SET disposition = $failed, disposition_detail = $detail
             WHERE id = $id AND {Unanswered("mission_event")}
            """,
            ("$failed", MissionEventDisposition.Failed), ("$detail", detail), ("$id", id),
            ("$owner", MissionEventKind.Owner), ("$blocked", MissionEventDisposition.Blocked));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>The one predicate both statements above share, over the row aliased <paramref name="e"/>.</summary>
    static string Unanswered(string e) => $"""
        {e}.kind = $owner AND {e}.consumed_at IS NOT NULL
               AND ({e}.disposition IS NULL OR {e}.disposition = $blocked)
               AND (EXISTS (SELECT 1 FROM ai_attempt a WHERE a.id = {e}.consumed_by AND a.state = 'ENDED')
                    OR NOT EXISTS (SELECT 1 FROM ai_attempt a WHERE a.id = {e}.consumed_by))
        """;

    /// <summary>
    /// RUNS <paramref name="body"/> AS ONE COMMIT: every write it makes through this store lands together or not at all,
    /// and an exception out of it rolls them all back (<see cref="Database.Write{T}"/>). The lost owner's words are
    /// settled and their retry raised — its sequence number allocated — inside one of these (pass 3, item 1), so a crash
    /// between the two cannot leave a message settled with no row carrying its words, and no other writer can take the
    /// sequence number between its allocation and its insert.
    /// </summary>
    public T InOneCommit<T>(Func<T> body) => db.Write(_ => body());

    /// <summary>
    /// EVERY WAKE OF ONE KIND FOR ONE ROLE THAT NO TURN HAS TAKEN YET, due or not, oldest first — what that role is
    /// still owed: an owner's message nobody has answered (a blocked one included, which is still owed its turn), a
    /// verdict nobody has read. A row a turn took is that turn's: if the turn was lost, a look would not see the row
    /// either, so it opens nothing here.
    /// </summary>
    public List<MissionEvent> Pending(string kind, string role) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM mission_event WHERE kind=$k AND COALESCE(role,$chair) = $role "
            + "AND consumed_at IS NULL ORDER BY created_at, rowid",
            ("$k", kind), ("$chair", CouncilRoles.Default), ("$role", role));
        using var r = c.ExecuteReader();
        var list = new List<MissionEvent>();
        while (r.Read()) list.Add(Read(r));
        return list;
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
    /// GIVES BACK THE WAKES A LAUNCH TOOK AND THE ACTIVATION GATE THEN REFUSED (review of <c>U-thinking-seats-a</c>, item 1): each
    /// row <paramref name="consumedBy"/> consumed and no turn has settled is due again, as though that launch had been refused at
    /// admission. Called only inside the launch's own committed transition, beside the close that records its row as refused with
    /// the refusal's kind — the record that it took them and why it gave them back. A wake another launch took, or one a turn
    /// settled, is never touched. <b>A delegated one is given back too</b> (fix pass 2, item 6): a refused launch's output is held,
    /// never published, so a link saying its brief carried the owner's words would be false — it is cleared and the words are owed
    /// again. Returns how many rows were given back.
    /// </summary>
    public int Release(IReadOnlyList<string> ids, string consumedBy) => db.Write(_ =>
    {
        var written = 0;
        foreach (var id in ids)
        {
            using var c = db.Cmd("""
                UPDATE mission_event SET consumed_at=NULL, consumed_by=NULL,
                       disposition = CASE WHEN disposition=$delegated THEN NULL ELSE disposition END,
                       disposition_detail = CASE WHEN disposition=$delegated THEN NULL ELSE disposition_detail END
                 WHERE id=$id AND consumed_by=$by AND (disposition IS NULL OR disposition IN ($blocked, $delegated))
                """, ("$id", id), ("$by", consumedBy), ("$blocked", MissionEventDisposition.Blocked),
                ("$delegated", MissionEventDisposition.Delegated));
            written += c.ExecuteNonQuery();
        }
        return written;
    });

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
    /// <para><b>A MEASUREMENT OF A FINISHED TURN, AND ONLY OVER NO OUTCOME</b>
    /// (<c>U-owner-words-delegated</c>). The attempt is the id an agent-written FILE NAME carries, so
    /// the statement itself refuses everything but the one case the link measures: the launch is in
    /// <c>ai_attempt</c> and ENDED — never LOST (its words are the loop's to re-raise,
    /// <see cref="LostOwnerWords"/>), never a launch the ledger does not hold — and the publication is
    /// a brief by that launch's own role that the ledger ATTRIBUTES to that launch (<c>publication.attempt</c>). A
    /// publication's id is the hash of its role, kind and text, never of its launch, so a finished turn whose agenda has
    /// the same bytes as one another turn published first names a row that is the other turn's; the link is refused,
    /// and the words fall to the once-only hand-on (<see cref="UnansweredOwnerWords"/>) — the fail-safe outcome, never a
    /// measurement that is false. It writes only where the message has no outcome yet (NULL,
    /// or <see cref="MissionEventDisposition.Blocked"/>, the standing reason a message waited before a
    /// turn took it), so <c>answered</c>, <c>failed</c> and a lost turn's settlement are never
    /// overwritten. Whatever a caller names, an agent's file cannot settle, hide or rewrite the
    /// owner's message's outcome.</para>
    ///
    /// <para>The FIRST publication of a turn keeps the link, so re-running the relay over the same
    /// files writes nothing the second time.</para>
    /// </summary>
    public int Delegated(string attemptId, string publicationId) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE mission_event SET disposition=$d, disposition_detail=$pub
             WHERE kind=$owner AND consumed_by=$attempt
               AND (disposition IS NULL OR disposition = $blocked)
               AND EXISTS (SELECT 1 FROM ai_attempt a JOIN publication p ON p.id = $pub
                            WHERE a.id = $attempt AND a.state = 'ENDED'
                              AND p.attempt = a.id
                              AND p.kind = $brief AND p.role = COALESCE(a.role, $chair))
            """,
            ("$d", MissionEventDisposition.Delegated), ("$pub", publicationId),
            ("$owner", MissionEventKind.Owner), ("$attempt", attemptId),
            ("$blocked", MissionEventDisposition.Blocked), ("$brief", MissionEventKind.Brief),
            ("$chair", CouncilRoles.Default));
        return c.ExecuteNonQuery();
    });

    /// <summary>One event by id, or null.</summary>
    public MissionEvent? Get(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM mission_event WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>
    /// RECORDS THAT THE APP HELD ONE PUBLICATION — published to nobody — as a row born CONSUMED by no launch, with
    /// <see cref="MissionEventDisposition.Held"/> and why (<c>U-org-rights</c>). Never due, never a wake, and never
    /// <c>consumed_by</c> a launch, so <see cref="AiAttemptStore.LastWoken"/> does not mistake the writer's turn for one a
    /// wake bought. Idempotent by <see cref="MissionEventIds.Held"/>; runs inside whatever transaction is open on this
    /// thread, which is how a turn's hold shares the commit that closes its launch.
    /// </summary>
    public bool Hold(string publicationId, string role, DateTimeOffset at, string why) => db.Write(_ =>
    {
        using var c = db.Cmd($"""
            INSERT INTO mission_event({Cols})
            VALUES($id,$kind,$at,$at,$payload,$at,NULL,$held,$role,$why)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", MissionEventIds.Held(publicationId)), ("$kind", MissionEventKind.Output), ("$at", Sql.T(at)),
            ("$payload", Json.Write(new MissionTask(publicationId, MissionEventKind.Output, role))),
            ("$held", MissionEventDisposition.Held), ("$role", role), ("$why", why));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// <paramref name="role"/>'S HELD OUTPUTS SINCE <paramref name="since"/> (all of them where null), newest first, at
    /// most <paramref name="limit"/>: what its next Situation says the app did with what it staged.
    /// </summary>
    public List<MissionEvent> HeldSince(string role, DateTimeOffset? since, int limit) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {Cols} FROM mission_event
             WHERE kind=$kind AND role=$role AND disposition=$held AND ($since IS NULL OR created_at > $since)
             ORDER BY created_at DESC, rowid DESC LIMIT $limit
            """,
            ("$kind", MissionEventKind.Output), ("$role", role), ("$held", MissionEventDisposition.Held),
            ("$since", since is { } t ? Sql.T(t) : null), ("$limit", limit));
        using var r = c.ExecuteReader();
        var list = new List<MissionEvent>();
        while (r.Read()) list.Add(Read(r));
        return list;
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
