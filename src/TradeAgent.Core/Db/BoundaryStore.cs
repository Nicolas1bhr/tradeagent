using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE BOUNDARIES THE APP FIXES, AND THE ONLY ONES THE STRONGEST MODEL IS SPENT AT.
///
/// <para><c>docs/COUNCIL.md</c>:59: "promotion of a strategy, a change of allocation, the launch of a
/// research campaign, the retirement of a team, the post-mortem after a loss-budget event". Two of
/// those exist as code today; this build opens the FIRST and reserves the shape of the fourth
/// (:222, "a retirement-candidate EVENT: evidence frozen, two sealed assessments, one bounded
/// challenge, code applies the disposition"). A kind arriving from a newer build reads as itself,
/// for the reason <see cref="MissionEventKind"/> is strings.</para>
/// </summary>
public static class BoundaryKind
{
    /// <summary>One version, judged under one campaign. The entity is the version, the revision the campaign.</summary>
    public const string Promotion = "promotion";

    /// <summary>
    /// A TEAM PUT UP FOR RETIREMENT. <c>docs/COUNCIL.md</c>:222 asks for exactly this shape and says why
    /// it is not a new permission: "retirement executes an already-authorised lifecycle policy — it is
    /// not a new agent-reachable permission operation".
    ///
    /// <para><b>Nothing in this build opens one.</b> The constant is here because the table's <c>kind</c>
    /// column is what makes the shape reusable, and a second table for retirement would be two records
    /// of one protocol. There is no evolution, no team and no allocator yet — see the unit's report.</para>
    /// </summary>
    public const string Retirement = "retirement";
}

/// <summary>What a director may hand the app about a boundary. Both are publications, and the app decides which.</summary>
public static class BoundarySubmissionKind
{
    /// <summary>
    /// ONE DIRECTOR'S READING OF THE EVIDENCE, WRITTEN BEFORE IT HAS SEEN THE OTHER'S. Committed on
    /// arrival and WITHHELD from the peer until both exist — see <see cref="CouncilBoundaries.Release"/>.
    /// </summary>
    public const string Assessment = "assessment";

    /// <summary>
    /// THE ONE BOUNDED CHALLENGE, from either director, after both assessments have been released.
    /// Bounded twice over: at most one per boundary, and at most <c>CouncilRelay.ReportLines</c> lines.
    /// </summary>
    public const string Challenge = "challenge";
}

/// <summary>
/// WHAT A BOUNDARY CAN COME OUT AS. A closed vocabulary on the row, for the reason
/// <c>PromotionReason</c> is one: a disposition a query filters on must not be free text.
/// </summary>
public static class BoundaryDisposition
{
    /// <summary>The policy's answer is yes: this version is eligible and nothing has to hold it back.</summary>
    public const string Deploy = "deploy";

    /// <summary>The policy's answer is no. Not a refusal by a director — no director can answer at all.</summary>
    public const string Hold = "hold";
}

/// <summary>
/// WHO APPLIED A DISPOSITION. There is exactly one possible value in this build and that is the point of
/// the column: <c>docs/COUNCIL.md</c>:62 asks that "code applies the promotion and allocation policy so
/// neither director can veto an eligible deployment forever", and <see cref="CouncilBoundaries"/> exposes
/// no method that takes a disposition from a caller. A later build that added one would have to write a
/// second value here, where the owner's report would print it.
/// </summary>
public static class BoundaryAuthor
{
    public const string Policy = "policy";
}

/// <summary>
/// THE IDS, WHICH ARE THE DEDUPLICATION — the shape <see cref="MissionEventIds"/> already uses, applied
/// to <c>docs/COUNCIL.md</c>:64: "Deduplicate boundary events by entity and revision, so repeated
/// proposals cannot manufacture senior spend."
///
/// <para>A function of the FACT and of nothing else. An id carrying the attempt, the clock or a Guid
/// would make every restart, every retry and every re-proposal a second boundary — and a boundary costs
/// two turns of the strongest model the moment it opens, which is the spend the doctrine bounds.</para>
/// </summary>
public static class BoundaryIds
{
    public static string Of(string kind, string entity, long revision) =>
        $"{kind}:{entity}:{revision.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>ONE CONSEQUENTIAL BOUNDARY, as the app wrote it down.</summary>
public sealed record BoundaryRow(
    string Id, string Kind, string Entity, long Revision, DateTimeOffset OpenedAt,
    DateTimeOffset DeadlineAt, string DefaultDisposition, string Evidence,
    string? Disposition, DateTimeOffset? DisposedAt, string? DisposedBy)
{
    public bool IsOpen => Disposition is null;
}

/// <summary>Which boundary one publication answers, and as what.</summary>
public sealed record BoundarySubmissionRow(
    string BoundaryId, string Role, string Kind, string PublicationId, DateTimeOffset At);

/// <summary>Whether the boundary was newly opened by this call, and the row either way.</summary>
public sealed record BoundaryOpened(bool Fresh, BoundaryRow Row);

/// <summary>Whether a submission was taken, the boundary it answered, and the words for a refusal.</summary>
public sealed record BoundarySubmitted(bool Ok, string Why, string? BoundaryId = null);

/// <summary>
/// The payload of a <see cref="MissionEventKind.Boundary"/> wake: which boundary opened, over what, and
/// by when it will be decided without the director. Small, like every other payload in that queue.
/// </summary>
public sealed record BoundaryTask(string Boundary, string Kind, string Entity, DateTimeOffset DeadlineAt);

/// <summary>
/// THE CONSEQUENTIAL-BOUNDARY PROTOCOL, IN CODE — <c>docs/COUNCIL.md</c>:59-65, which until this unit had
/// no product code at all.
///
/// <para><b>Opening one is deduplicated and costs exactly two turns.</b> The id is
/// <c>kind:entity:revision</c>, so a repeated proposal writes nothing; a fresh one inserts one
/// <c>mission_event</c> per director in the SAME transaction as the boundary row, which is why a crash
/// cannot leave a boundary nobody was told about or a paid wake about a boundary that does not
/// exist.</para>
///
/// <para><b>The seal is the app's and never a director's discretion.</b> An assessment is committed as an
/// immutable publication the moment it arrives and its delivery to the peer is
/// <see cref="DeliveryState.Withheld"/>; <see cref="Release"/> — run by the relay's own delivery pass —
/// flips both together once both exist. Neither director can choose to wait, and neither can read the
/// other's first: the file is not on disk to be read.</para>
///
/// <para><b>Nothing here grants authority.</b> There is no method that takes a disposition, no pipe op
/// and no <c>trade</c> verb that reaches any of it, and the only writer of <c>boundary_event</c> is this
/// class. A director's whole reach is a file in its own <c>out/</c>, which the relay validates.</para>
/// </summary>
public sealed class CouncilBoundaries(Database db, Func<TimeSpan>? window = null)
{
    /// <summary>
    /// HOW LONG A BOUNDARY STAYS OPEN BEFORE THE POLICY ANSWERS IT. Not a setting, for the reason the
    /// day's renewal is not one: a deadline the owner could shorten under evidence already being
    /// gathered, or lengthen to keep an eligible deployment waiting, is exactly the veto
    /// <c>docs/COUNCIL.md</c>:62 forbids. It is injectable so a test can run a day in a millisecond.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    readonly PublicationStore _publications = new(db);
    readonly Func<TimeSpan> _window = window ?? (() => DefaultWindow);

    const string Cols =
        "id, kind, entity, revision, opened_at, deadline_at, default_disposition, evidence, "
        + "disposition, disposed_at, disposed_by";

    // ---- opening ----------------------------------------------------------------------------------

    /// <summary>
    /// OPENS ONE BOUNDARY AND WAKES BOTH DIRECTORS, ONCE, IN ONE <see cref="Database.Write"/>.
    ///
    /// <para>Returns <c>Fresh = false</c> when the key was already there, and then nothing at all was
    /// written: no row, no wake, no turn. That is <c>docs/COUNCIL.md</c>:64 — "repeated proposals cannot
    /// manufacture senior spend" — and it holds across a restart because the id is a function of the
    /// fact rather than of the process that noticed it.</para>
    ///
    /// <para><paramref name="defaultDisposition"/> is written NOW and never afterwards. "A deadline with
    /// a predetermined default" is precommitment: a default computed when the clock expires is a default
    /// chosen once the outcome is known, which <c>docs/COUNCIL.md</c>:212 names as the thing that cannot
    /// be recovered if it is not recorded at the time.</para>
    /// </summary>
    public BoundaryOpened Open(string kind, string entity, long revision, string defaultDisposition,
        string evidence, DateTimeOffset at) => db.Write(_ =>
    {
        var id = BoundaryIds.Of(kind, entity, revision);
        var deadline = at + _window();

        using var c = db.Cmd($"""
            INSERT INTO boundary_event({Cols})
            VALUES($id,$kind,$entity,$rev,$at,$deadline,$default,$evidence,NULL,NULL,NULL)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", id), ("$kind", kind), ("$entity", entity), ("$rev", revision), ("$at", Sql.T(at)),
            ("$deadline", Sql.T(deadline)), ("$default", defaultDisposition), ("$evidence", evidence));
        var fresh = c.ExecuteNonQuery() == 1;

        // ONE PAID TURN EACH, AND ONLY ON THE FIRST OPENING. Both directors, because an assessment
        // neither was woken for is an assessment that happens whenever one of them next runs for some
        // other reason — and "two assessments are two turns" (:63) is a count of invocations the owner
        // pays for, which means they have to be invocations the app actually caused.
        if (fresh)
            foreach (var role in CouncilRoles.All)
            {
                using var e = db.Cmd($"""
                    INSERT INTO mission_event({MissionEventStore.EventCols})
                    VALUES($id,$kind,$at,$at,$payload,NULL,NULL,NULL,$role,NULL)
                    ON CONFLICT(id) DO NOTHING
                    """,
                    ("$id", MissionEventIds.ForRole(MissionEventIds.Boundary(id), role)),
                    ("$kind", MissionEventKind.Boundary), ("$at", Sql.T(at)),
                    ("$payload", Json.Write(new BoundaryTask(id, kind, entity, deadline))),
                    ("$role", role));
                e.ExecuteNonQuery();
            }

        return new BoundaryOpened(fresh, ById(id)!);
    });

    // ---- the two sealed assessments -----------------------------------------------------------------

    /// <summary>
    /// TAKES ONE DIRECTOR'S ASSESSMENT, SEALED — or refuses it in words, having written nothing.
    ///
    /// <para>The boundary is the APP'S choice and not the director's: the oldest one still open that this
    /// role has not yet assessed. A role that named its own boundary could answer the easy one and leave
    /// the deadline to run out on the other, and <see cref="CouncilRelay"/> already decides what a role's
    /// output IS and who it goes to for the same reason.</para>
    ///
    /// <para><b>A second assessment from the same director over the same boundary is refused.</b> The
    /// primary key of <c>boundary_submission</c> says so in SQL; this method says it in words first, so
    /// the owner reads a sentence rather than a constraint violation. Refused means refused: nothing
    /// published, no delivery, no turn bought.</para>
    /// </summary>
    public BoundarySubmitted Assess(Publication p, DateTimeOffset at) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(p);

        var open = OpenBoundaries();
        if (open.Count == 0)
            return new BoundarySubmitted(false,
                "there is no consequential boundary open, so there is nothing to assess. TradeAgent "
                + "opens one itself when something consequential happens and tells you in the same "
                + "turn; you cannot open one, and neither can the other director.");

        var mine = open.FirstOrDefault(b => Submission(b.Id, p.Role, BoundarySubmissionKind.Assessment) is null);
        if (mine is null)
            return new BoundarySubmitted(false,
                $"you have already assessed every boundary that is open ({open.Count} of them). An "
                + "assessment is written once and is sealed the moment TradeAgent has it: it cannot be "
                + "revised, and a second one is not published. The one bounded challenge, after both "
                + "assessments are delivered, is where a reading changes.");

        // SEALED: committed as an artifact, WITHHELD from the peer. Not "not delivered yet" — a state on
        // the row, so a restart in the middle cannot mistake it for a delivery that merely failed.
        _publications.CommitSealed(p, at);

        using var c = db.Cmd("""
            INSERT INTO boundary_submission(boundary_id, role, kind, publication_id, at)
            VALUES($b,$role,$kind,$pub,$at)
            ON CONFLICT(boundary_id, role, kind) DO NOTHING
            """,
            ("$b", mine.Id), ("$role", p.Role), ("$kind", BoundarySubmissionKind.Assessment),
            ("$pub", p.Id), ("$at", Sql.T(at)));
        c.ExecuteNonQuery();

        return new BoundarySubmitted(true, "", mine.Id);
    });

    /// <summary>
    /// RELEASES EVERY SEALED PAIR WHOSE SECOND HALF HAS ARRIVED, in one transaction, and answers how many
    /// publications were released.
    ///
    /// <para><b>Both together or neither.</b> The whole property of :61 — "both directors submit an
    /// assessment before either sees the other's" — is that the release is atomic: releasing the first as
    /// soon as the second is committed would still let one director's file land while the other's is
    /// being written. The flip is one UPDATE over both, inside one <see cref="Database.Write"/>.</para>
    ///
    /// <para>Run by <c>CouncilRelay.Deliver</c>, which is the pass that then copies the files. It is safe
    /// to run at any time and on any pass: a boundary with one assessment releases nothing.</para>
    /// </summary>
    public int Release(DateTimeOffset at) => db.Write(_ =>
    {
        var released = 0;

        foreach (var boundary in Ready())
        {
            using var c = db.Cmd("""
                UPDATE delivery SET state=$to, created_at=$at
                 WHERE state=$from AND publication_id IN (
                   SELECT publication_id FROM boundary_submission
                    WHERE boundary_id=$b AND kind=$kind)
                """,
                ("$to", DeliveryState.Committed), ("$at", Sql.T(at)), ("$from", DeliveryState.Withheld),
                ("$b", boundary), ("$kind", BoundarySubmissionKind.Assessment));
            released += c.ExecuteNonQuery();
        }

        return released;
    });

    /// <summary>
    /// Boundaries whose assessments are ALL in — one from every role — and at least one of whose
    /// deliveries is still withheld. Read inside the caller's transaction, so nothing can commit the
    /// second assessment between this and the flip.
    /// </summary>
    List<string> Ready() => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT s.boundary_id FROM boundary_submission s
             WHERE s.kind=$kind
             GROUP BY s.boundary_id
            HAVING COUNT(DISTINCT s.role) >= $roles
               AND EXISTS (SELECT 1 FROM delivery d
                            WHERE d.publication_id IN (
                              SELECT publication_id FROM boundary_submission
                               WHERE boundary_id=s.boundary_id AND kind=$kind)
                              AND d.state=$withheld)
            """,
            ("$kind", BoundarySubmissionKind.Assessment), ("$roles", CouncilRoles.All.Length),
            ("$withheld", DeliveryState.Withheld));

        var ids = new List<string>();
        using var r = c.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    });

    /// <summary>Whether both assessments of this boundary have been released to their recipients.</summary>
    public bool AssessmentsDelivered(string boundaryId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT COUNT(*) FROM boundary_submission s
             WHERE s.boundary_id=$b AND s.kind=$kind
               AND NOT EXISTS (SELECT 1 FROM delivery d
                                WHERE d.publication_id=s.publication_id AND d.state=$withheld)
            """,
            ("$b", boundaryId), ("$kind", BoundarySubmissionKind.Assessment),
            ("$withheld", DeliveryState.Withheld));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture) >= CouncilRoles.All.Length;
    });

    // ---- the one bounded challenge ------------------------------------------------------------------

    /// <summary>
    /// TAKES THE ONE CHALLENGE A BOUNDARY GETS, from either director — or refuses it in words, having
    /// written nothing and bought nobody a turn.
    ///
    /// <para>Three conditions, and each of them is a sentence of :61. It comes AFTER both assessments are
    /// delivered, because a challenge written before the peer's reading is on disk is a second assessment
    /// wearing another name. There is exactly ONE per boundary and it may come from EITHER director, so
    /// the second is refused whoever writes it — the partial unique index says the same thing in SQL.
    /// And unlike an assessment it is delivered normally, which buys the peer the one turn it is worth:
    /// a challenge nobody is woken for is a challenge answered after the deadline.</para>
    /// </summary>
    public BoundarySubmitted Challenge(Publication p, DateTimeOffset at) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(p);

        var open = OpenBoundaries();
        var eligible = open.Where(b => AssessmentsDelivered(b.Id)).ToList();

        if (eligible.Count == 0)
            return new BoundarySubmitted(false,
                "there is no boundary you may challenge. A challenge comes after BOTH assessments have "
                + "been delivered — until then there is nothing of the other director's to challenge, "
                + "and a reading written before theirs arrives is a second assessment under another "
                + "name, which TradeAgent does not publish.");

        var free = eligible.FirstOrDefault(b => Challenged(b.Id) is null);
        if (free is null)
            return new BoundarySubmitted(false,
                "every boundary open to you has already been challenged once, and one bounded challenge "
                + "is what a boundary gets (`docs/COUNCIL.md`). It is one per BOUNDARY and not one per "
                + "director, so it makes no difference which of you wrote it. What settles it now is "
                + "TradeAgent applying the policy's default at the deadline.");

        _publications.Commit(p, at);

        using var c = db.Cmd("""
            INSERT INTO boundary_submission(boundary_id, role, kind, publication_id, at)
            VALUES($b,$role,$kind,$pub,$at)
            ON CONFLICT(boundary_id, role, kind) DO NOTHING
            """,
            ("$b", free.Id), ("$role", p.Role), ("$kind", BoundarySubmissionKind.Challenge),
            ("$pub", p.Id), ("$at", Sql.T(at)));
        c.ExecuteNonQuery();

        return new BoundarySubmitted(true, "", free.Id);
    });

    /// <summary>The challenge of this boundary, from whichever director wrote it, or null.</summary>
    public BoundarySubmissionRow? Challenged(string boundaryId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT boundary_id, role, kind, publication_id, at FROM boundary_submission
             WHERE boundary_id=$b AND kind=$kind LIMIT 1
            """, ("$b", boundaryId), ("$kind", BoundarySubmissionKind.Challenge));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadSubmission(r) : null;
    });

    // ---- the disposition, applied by code ------------------------------------------------------------

    /// <summary>
    /// WRITES THE POLICY'S DEFAULT ONTO EVERY BOUNDARY WHOSE TIME IS UP, and answers which ones it
    /// settled. <c>docs/COUNCIL.md</c>:62: "code applies the promotion and allocation policy so neither
    /// director can veto an eligible deployment forever".
    ///
    /// <para><b>Two triggers, and both are the app's clock or the app's own record.</b> The DEADLINE has
    /// passed; or the challenge window has CLOSED, which this build reads as "both assessments are
    /// delivered and the one challenge has been written", because at that point the protocol has had
    /// everything it is ever going to get and waiting out the clock buys nothing. Neither trigger is a
    /// director saying it is finished.</para>
    ///
    /// <para>The disposition written is the <c>default_disposition</c> recorded AT OPEN, never a value
    /// computed here: see <see cref="Open"/>. The comparison is <c>now &gt;= deadline</c>, and its
    /// direction is the guard — inverted, every boundary is disposed the instant it opens and the two
    /// assessments the owner paid for arrive after the decision.</para>
    /// </summary>
    public IReadOnlyList<BoundaryRow> ApplyDue(DateTimeOffset now) => db.Write(_ =>
    {
        var settled = new List<BoundaryRow>();

        foreach (var b in OpenBoundaries())
        {
            var expired = now >= b.DeadlineAt;
            var closed = Challenged(b.Id) is not null && AssessmentsDelivered(b.Id);
            if (!expired && !closed) continue;

            using var c = db.Cmd("""
                UPDATE boundary_event SET disposition=$d, disposed_at=$at, disposed_by=$by
                 WHERE id=$id AND disposition IS NULL
                """,
                ("$d", b.DefaultDisposition), ("$at", Sql.T(now)), ("$by", BoundaryAuthor.Policy),
                ("$id", b.Id));

            if (c.ExecuteNonQuery() == 1 && ById(b.Id) is { } row) settled.Add(row);
        }

        return (IReadOnlyList<BoundaryRow>)settled;
    });

    /// <summary>
    /// The earliest deadline still to come, or null when nothing is open. The loop sleeps until it, so a
    /// boundary is settled on the owner's clock rather than whenever an agent next happens to run.
    /// </summary>
    public DateTimeOffset? NextDeadline() => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT MIN(deadline_at) FROM boundary_event WHERE disposition IS NULL");
        var o = c.ExecuteScalar();
        return o is null or DBNull ? null : (DateTimeOffset?)Sql.Time(o);
    });

    // ---- reads ---------------------------------------------------------------------------------------

    public BoundaryRow? ById(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM boundary_event WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>Every boundary still open, oldest first. What the Situation shows and what the sweep walks.</summary>
    public IReadOnlyList<BoundaryRow> OpenBoundaries() => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM boundary_event WHERE disposition IS NULL ORDER BY opened_at, id");
        return ReadAll(c);
    });

    /// <summary>Every boundary this installation has opened, newest first. What the owner's report lists.</summary>
    public IReadOnlyList<BoundaryRow> All(int limit = 100) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM boundary_event ORDER BY opened_at DESC, id DESC LIMIT $n", ("$n", limit));
        return ReadAll(c);
    });

    /// <summary>Everything submitted about one boundary, oldest first.</summary>
    public IReadOnlyList<BoundarySubmissionRow> Submissions(string boundaryId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT boundary_id, role, kind, publication_id, at FROM boundary_submission
             WHERE boundary_id=$b ORDER BY at, role
            """, ("$b", boundaryId));

        var rows = new List<BoundarySubmissionRow>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add(ReadSubmission(r));
        return (IReadOnlyList<BoundarySubmissionRow>)rows;
    });

    /// <summary>One role's submission of one kind about one boundary, or null because it has made none.</summary>
    public BoundarySubmissionRow? Submission(string boundaryId, string role, string kind) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT boundary_id, role, kind, publication_id, at FROM boundary_submission
             WHERE boundary_id=$b AND role=$role AND kind=$kind
            """, ("$b", boundaryId), ("$role", role), ("$kind", kind));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadSubmission(r) : null;
    });

    /// <summary>
    /// WHAT THIS ROLE STILL OWES, boundary by boundary: the open ones, oldest first, each with whether
    /// this director has assessed it and whether the one challenge is still available. The Situation is
    /// built from this, which is why it is a read and not a rendering.
    /// </summary>
    public IReadOnlyList<BoundaryStanding> OpenFor(string role) =>
        [.. OpenBoundaries().Select(b => new BoundaryStanding(
            b,
            Submission(b.Id, role, BoundarySubmissionKind.Assessment) is not null,
            AssessmentsDelivered(b.Id),
            Challenged(b.Id) is not null))];

    static List<BoundaryRow> ReadAll(SqliteCommand c)
    {
        var rows = new List<BoundaryRow>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add(Read(r));
        return rows;
    }

    static BoundaryRow Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), Sql.Time(r.GetValue(4)),
        Sql.Time(r.GetValue(5)), r.GetString(6), r.GetString(7), Sql.S(r.GetValue(8)),
        Sql.TimeN(r.GetValue(9)), Sql.S(r.GetValue(10)));

    static BoundarySubmissionRow ReadSubmission(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Sql.Time(r.GetValue(4)));
}

/// <summary>
/// ONE OPEN BOUNDARY AS ONE DIRECTOR SEES IT: the row, whether this role has assessed it, whether both
/// assessments have been released, and whether the one challenge has been used.
///
/// <para>It is a READING and never a permission. What a director may do about a boundary is write a file
/// into its own <c>out/</c>; everything else — opening, releasing, disposing — is the app's.</para>
/// </summary>
public sealed record BoundaryStanding(
    BoundaryRow Row, bool Assessed, bool BothDelivered, bool Challenged)
{
    /// <summary>
    /// THE ONE LINE THIS DIRECTOR IS SHOWN. It names the boundary, what it is about, the deadline and the
    /// default that will be applied without them — because a deadline whose consequence is not stated is
    /// a date, not a deadline.
    /// </summary>
    public string Line() =>
        $"Boundary `{Row.Id}` ({Row.Kind} of {Row.Entity}): {Next()} TradeAgent decides this itself at "
        + $"{Row.DeadlineAt.LocalDateTime:yyyy-MM-dd HH:mm} and the answer it will write is "
        + $"`{Row.DefaultDisposition}`. Evidence: {Row.Evidence}.";

    string Next() =>
        !Assessed
            ? "write your assessment this turn — it is sealed until the other director has written "
              + "theirs, and you cannot revise it."
            : !BothDelivered
                ? "your assessment is sealed; the other director has not finished theirs yet."
                : Challenged
                    ? "both assessments are delivered and the one challenge has been made."
                    : "both assessments are delivered; one bounded challenge is still available, from "
                      + "either of you.";
}
