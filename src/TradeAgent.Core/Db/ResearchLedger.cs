using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT AN ENTRY IS ABOUT, in the agent's own words — and the vocabulary is the store's, checked here when it writes,
/// never a CHECK: a later unit adds a kind (a decision record, <c>U-org-assignments</c>) by changing this list and
/// nothing on disk (the reading rung 28 gives).
/// </summary>
public static class LedgerKind
{
    public const string Hypothesis = "hypothesis", Experiment = "experiment", Finding = "finding", Kill = "kill",
        Lesson = "lesson";

    public static readonly string[] All = [Hypothesis, Experiment, Finding, Kill, Lesson];

    public static bool IsKnown(string? s) => s is not null && Array.IndexOf(All, s) >= 0;
}

/// <summary>
/// HOW AN ENTRY IS HELD: the author's claim, assumption or hypothesis — VISION § 6.7's three, and the only three. There is
/// no fourth and no word for "measured": a measurement is the app's own record, which the app links to an entry
/// (<see cref="LedgerLinks"/>), and nothing becomes measured because an agent wrote it down. Refused here in words, and by
/// a CHECK on the column too, so no writer on the app's connection can store a row that says otherwise.
/// </summary>
public static class LedgerMark
{
    public const string Claim = "claim", Assumption = "assumption", Hypothesis = "hypothesis";

    public static readonly string[] All = [Claim, Assumption, Hypothesis];

    public static bool IsKnown(string? s) => s is not null && Array.IndexOf(All, s) >= 0;
}

/// <summary>
/// WHERE THE AUTHOR HAS PUT AN ENTRY: its own bookkeeping, and it gates nothing — an entry dropped is still read, linked
/// and revisable, and an open one asks nothing of anybody. Checked by the store, never a CHECK, for the reason
/// <see cref="LedgerKind"/> gives.
/// </summary>
public static class LedgerStatus
{
    public const string Open = "open", Held = "held", Dropped = "dropped";

    public static readonly string[] All = [Open, Held, Dropped];

    public static bool IsKnown(string? s) => s is not null && Array.IndexOf(All, s) >= 0;
}

/// <summary>
/// THE APP RECORDS A LINK CAN NAME: a research run the app recorded for a <c>backtest</c> asked under the entry, and a
/// promotion the referee recorded for a <c>verdict</c> asked under it. Nothing else — not <c>activity</c>, which is rotated,
/// and not a tape row, which lives in another file.
/// </summary>
public static class LedgerRecord
{
    public const string Run = "run", Promotion = "promotion";

    public static readonly string[] All = [Run, Promotion];

    public static bool IsKnown(string? s) => s is not null && Array.IndexOf(All, s) >= 0;
}

/// <summary>
/// ONE ENTRY: what it is about and who wrote it, all of it fixed when it was added. <see cref="Author"/> is the council role
/// the launch grant proved, never a name the request carried; <see cref="About"/> is another entry this one is about, or
/// null; <see cref="Source"/> is the author's own words for where it came from.
/// </summary>
public sealed record LedgerEntryRow(
    long Id, string Kind, string Author, long? About, string? Source, DateTimeOffset CreatedAt, string? Attempt);

/// <summary>
/// ONE REVISION OF AN ENTRY: what its author said at <see cref="At"/>, and how it held it. <see cref="Confidence"/> is
/// null when the author did not state one on THIS revision — an UNKNOWN, never a zero and never the last revision's
/// carried forward. <see cref="Why"/> is null on the first revision and the author's reason on every later one.
/// </summary>
public sealed record LedgerRevisionRow(
    long EntryId, int Revision, DateTimeOffset At, string? Attempt, string Text, string Mark, decimal? Confidence,
    string Status, string? Why);

/// <summary>
/// ONE LINK THE APP WROTE: that it answered a request asked under <see cref="EntryId"/> at <see cref="Revision"/> with
/// the record <see cref="RecordKind"/> <see cref="RecordId"/>. A reference — the kind and the id — and never a field of
/// the record: the record stays the authority and is read where it lives.
/// </summary>
public sealed record LedgerLinkRow(
    long Id, long EntryId, int Revision, string RecordKind, string RecordId, string? Attempt, DateTimeOffset At);

/// <summary>An entry about another one, as a <c>show</c> names it: who said what kind of thing, and where it stands.</summary>
public sealed record LedgerAboutRow(long Id, string Kind, string Author, int Revision, string Mark, string Status,
    DateTimeOffset CreatedAt);

/// <summary>One entry as a list names it: the entry, its newest revision, and how many revisions and links it has.</summary>
public sealed record LedgerListed(LedgerEntryRow Entry, LedgerRevisionRow Latest, int Revisions, int Links);

/// <summary>
/// ONE ENTRY AS <c>show</c> READS IT, in one snapshot: the entry; its revisions newest first, from the one before the
/// asked revision down, at most <see cref="ResearchLedger.MaxRevisions"/> + 1 so a reader can tell there are more; its
/// links and the entries about it, newest first, at most <see cref="ResearchLedger.MaxLinks"/> and
/// <see cref="ResearchLedger.MaxAbout"/> with their totals; and, DERIVED AT READ and never stored, the promotion and
/// deployment ids of the versions its linked runs were runs of.
/// </summary>
public sealed record LedgerShown(
    LedgerEntryRow Entry, int RevisionCount, IReadOnlyList<LedgerRevisionRow> Revisions,
    int LinkCount, IReadOnlyList<LedgerLinkRow> Links, int AboutCount, IReadOnlyList<LedgerAboutRow> About,
    IReadOnlyList<string> Versions, IReadOnlyList<string> Promotions, IReadOnlyList<string> Deployments);

/// <summary>
/// THE ENTRY A REQUEST WAS ASKED UNDER, AS THE APP CHECKED IT — and the only thing a link can be written with.
///
/// <para>Minted by <see cref="ResearchLedger.Ask"/> alone (the constructor is this assembly's), after the entry was found
/// and found to be the asking role's own, and before anything was run or charged. It carries the revision the entry stood
/// at when the request was asked: the belief a measurement was asked under, which a revision made while it ran does not
/// rewrite. Holding one writes nothing; <see cref="LedgerLinks.Link"/> writes the link, inside the record's own
/// write.</para>
/// </summary>
public sealed class LedgerAsk
{
    internal LedgerAsk(long entry, int revision, string role)
    {
        Entry = entry;
        Revision = revision;
        Role = role;
    }

    public long Entry { get; }

    public int Revision { get; }

    public string Role { get; }
}

/// <summary>
/// THE RESEARCH LEDGER — EACH ROLE'S OWN BELIEFS, AS ITS OWN VERSIONED CLAIMS (<c>U-research-ledger</c>; VISION § 6.7, the
/// believed world; <c>docs/CONTRACTS.md</c> "The research ledger").
///
/// <para><b>Claim beside measurement, the rule <c>material</c> and <c>material_note</c> keep.</b> An entry is what its
/// author believes — a hypothesis, an experiment, a finding, a kill and its reason, a lesson — marked claim, assumption or
/// hypothesis, with the author's own statement of how sure it is or an unknown. What the app measured stays in its own
/// tables, and the only thing that joins the two is <c>ledger_link</c>, which the app writes and nothing an agent sends can
/// (<see cref="LedgerLinks"/>). An entry holds a REFERENCE to an app record and never a copy of one of its fields.</para>
///
/// <para><b>Two writes, and both only insert.</b> <see cref="Add"/> writes an entry and its first revision;
/// <see cref="Revise"/> writes the next revision of an entry its caller wrote. Nothing updates or deletes a
/// <c>ledger_</c> row, so the ledger answers what a role believed when, and why it changed its mind — the record is never
/// rewritten into what it believes now.</para>
///
/// <para><b>What the app writes and what the author does.</b> The author writes the kind, the text, the mark, the
/// confidence, the status, what the entry is about, the source and the reason for a revision. The app writes the ids, the
/// revision numbers, the author — the council role the launch grant proved, never an app principal and never a name in
/// the request — the attempt and the instants.</para>
///
/// <para><b>Confidence and status are fields, never gates</b> (PRINCIPLES, "Autonomy is the default inside authority").
/// Any hypothesis may be held at any confidence or none; nothing here or anywhere reads either to decide anything. A
/// confidence the author did not state is NULL on that revision — recorded as unknown, never refused, never defaulted
/// and never carried forward from the revision before.</para>
/// </summary>
public sealed class ResearchLedger(Database db, Func<DateTimeOffset>? now = null)
{
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The most characters an entry's text holds. Longer is refused, never cut.</summary>
    public const int MaxTextChars = 2_000;

    /// <summary>The most characters a revision's reason, or an entry's source, holds. Longer is refused, never cut.</summary>
    public const int MaxNoteChars = 1_000;

    /// <summary>The most entries one list answers. More is refused, never clamped.</summary>
    public const int MaxEntries = 100;

    /// <summary>The most revisions one show answers; the rest are reached with its <c>before</c>.</summary>
    public const int MaxRevisions = 100;

    /// <summary>The most links one show names, newest first, beside how many there are.</summary>
    public const int MaxLinks = 100;

    /// <summary>The most entries about one entry a show names, newest first, beside how many there are.</summary>
    public const int MaxAbout = 100;

    /// <summary>The most bytes one read answers, entries or revisions as the reader measures them.</summary>
    public const long MaxReadBytes = 64 * 1024;

    const string EntryCols = "id, kind, author, about, source, created_at, attempt";
    const string RevisionCols = "entry_id, revision, at, attempt, text, mark, confidence, status, why";

    /// <summary>
    /// ADDS ONE ENTRY AND ITS FIRST REVISION, in one write, and answers the entry as written. Its status is
    /// <see cref="LedgerStatus.Open"/>; its confidence is what the author stated, or null — an unknown.
    /// </summary>
    /// <exception cref="TradeAgentException">
    /// INVALID_REQUEST, with nothing written, for an author that is not a council role, a kind or a mark outside the
    /// vocabulary, a confidence outside 0 to 1, an <paramref name="about"/> naming no entry, or a text or source that is
    /// empty or too long.
    /// </exception>
    public LedgerEntryRow Add(string author, string? attempt, string kind, string text, string mark,
        decimal? confidence, long? about, string? source)
    {
        Author(author);
        Kind(kind);
        Mark(mark);
        Confidence(confidence);
        var said = Text(text, "text", MaxTextChars);
        var from = source is null ? null : Text(source, "source", MaxNoteChars);

        return db.Write(_ =>
        {
            var at = _now();
            if (about is { } other && Entry(other) is null)
                throw Refused($"entry {Id(other)} is not in the research ledger, so an entry cannot be about it. "
                    + "'about' names an entry that is already there — yours or the other director's — by the id "
                    + "'ledger-add' or 'ledger-list' answered. Nothing was written.");

            long id;
            using (var c = db.Cmd($"""
                INSERT INTO ledger_entry(kind, author, about, source, created_at, attempt)
                VALUES($kind, $author, $about, $source, $at, $attempt);
                SELECT last_insert_rowid();
                """,
                ("$kind", kind), ("$author", author), ("$about", about), ("$source", from), ("$at", Sql.T(at)),
                ("$attempt", attempt)))
                id = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);

            InsertRevision(id, 1, at, attempt, said, mark, confidence, LedgerStatus.Open, null);
            return Entry(id)!;
        });
    }

    /// <summary>
    /// WRITES THE NEXT REVISION OF AN ENTRY ITS CALLER WROTE, and answers it. The number is the last one + 1, read inside
    /// the write. An unstated confidence is null on THIS revision; an unstated status is the last revision's.
    /// </summary>
    /// <exception cref="TradeAgentException">
    /// INVALID_REQUEST, with nothing written, for an entry that is not there or is another role's, an author that is not a
    /// council role, a mark or status outside the vocabulary, a confidence outside 0 to 1, or a text or reason that is
    /// empty or too long.
    /// </exception>
    public LedgerRevisionRow Revise(string author, string? attempt, long entry, string text, string mark, string why,
        decimal? confidence, string? status)
    {
        Author(author);
        Mark(mark);
        Confidence(confidence);
        if (status is not null) Status(status);
        var said = Text(text, "text", MaxTextChars);
        var because = Text(why, "why", MaxNoteChars);

        return db.Write(_ =>
        {
            var row = Entry(entry) ?? throw NotThere(entry);
            if (!string.Equals(row.Author, author, StringComparison.Ordinal))
                throw Refused($"entry {Id(entry)} is the {CouncilRoles.Title(row.Author)}'s, and an entry is revised only "
                    + "by the role that wrote it: what it believed, and when, stays its own. To disagree with it, add an "
                    + $"entry of your own about it — 'ledger-add' with 'about' {Id(entry)}. Nothing was written.");

            var last = Latest(entry)!;
            InsertRevision(entry, last.Revision + 1, _now(), attempt, said, mark, confidence, status ?? last.Status, because);
            return Revisions(entry)[0];
        });
    }

    /// <summary>
    /// THE ENTRY CHECK A REQUEST ASKED UNDER ONE GOES THROUGH, before anything is run or charged: the entry is there, and it
    /// is <paramref name="role"/>'s own. Answers what a link is written with — the entry and the revision it stands at now.
    /// A READ: nothing is written, and nothing is linked until the record the request produced is.
    /// </summary>
    /// <exception cref="TradeAgentException">INVALID_REQUEST for an entry that is not there or is another role's.</exception>
    public LedgerAsk Ask(long entry, string role) => db.Read(_ =>
    {
        var row = Entry(entry) ?? throw NotThere(entry);
        if (!string.Equals(row.Author, role, StringComparison.Ordinal))
            throw Refused($"entry {Id(entry)} is the {CouncilRoles.Title(row.Author)}'s, and TradeAgent links a request "
                + "only to an entry of the role that asks it: a link says this role asked this under its own belief. Add "
                + $"one of your own — 'ledger-add' with 'about' {Id(entry)} if it answers theirs — and ask under that. "
                + "Nothing was run and nothing was charged.");
        return new LedgerAsk(row.Id, Latest(entry)!.Revision, role);
    });

    /// <summary>One entry by its id, or null.</summary>
    public LedgerEntryRow? Entry(long id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {EntryCols} FROM ledger_entry WHERE id=$id", ("$id", id));
        using var rd = c.ExecuteReader();
        return rd.Read() ? EntryOf(rd, 0) : null;
    });

    /// <summary>An entry's newest revision, or null when there is no such entry.</summary>
    public LedgerRevisionRow? Latest(long entry) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {RevisionCols} FROM ledger_revision WHERE entry_id=$e ORDER BY revision DESC LIMIT 1", ("$e", entry));
        using var rd = c.ExecuteReader();
        return rd.Read() ? RevisionOf(rd, 0) : null;
    });

    /// <summary>Every revision of an entry, newest first.</summary>
    public IReadOnlyList<LedgerRevisionRow> Revisions(long entry) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {RevisionCols} FROM ledger_revision WHERE entry_id=$e ORDER BY revision DESC", ("$e", entry));
        using var rd = c.ExecuteReader();
        var list = new List<LedgerRevisionRow>();
        while (rd.Read()) list.Add(RevisionOf(rd, 0));
        return list;
    });

    /// <summary>
    /// ENTRIES NEWEST FIRST — every role's — with each one's newest revision, filtered by author, kind and newest status,
    /// from the entry before <paramref name="before"/> down, at most <paramref name="limit"/> + 1 of them so the reader can
    /// say there are more. A read in one snapshot.
    /// </summary>
    /// <exception cref="TradeAgentException">
    /// INVALID_REQUEST for a filter outside its vocabulary: an author that is not a council role, a kind or a status
    /// that is not one — refused rather than answered with nothing, which would read as "there are none".
    /// </exception>
    public IReadOnlyList<LedgerListed> List(string? author, string? kind, string? status, long? before, int limit)
    {
        if (limit is < 1 or > MaxEntries)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"a list serves 1 to {MaxEntries} entries");
        if (author is not null && !CouncilRoles.IsKnown(author))
            throw Refused($"'{author}' is not a council role, so no entry is written under it: 'author' is "
                + $"{string.Join(" or ", CouncilRoles.All)}.");
        if (kind is not null) Kind(kind);
        if (status is not null) Status(status);

        return db.Read(_ =>
        {
            using var c = db.Cmd($"""
                SELECT e.id, e.kind, e.author, e.about, e.source, e.created_at, e.attempt,
                       r.entry_id, r.revision, r.at, r.attempt, r.text, r.mark, r.confidence, r.status, r.why,
                       (SELECT COUNT(*) FROM ledger_revision WHERE entry_id = e.id),
                       (SELECT COUNT(*) FROM ledger_link WHERE entry_id = e.id)
                  FROM ledger_entry e
                  JOIN ledger_revision r
                    ON r.entry_id = e.id
                   AND r.revision = (SELECT MAX(revision) FROM ledger_revision WHERE entry_id = e.id)
                 WHERE ($author IS NULL OR e.author = $author)
                   AND ($kind IS NULL OR e.kind = $kind)
                   AND ($status IS NULL OR r.status = $status)
                   AND ($before IS NULL OR e.id < $before)
                 ORDER BY e.id DESC
                 LIMIT $n
                """,
                ("$author", author), ("$kind", kind), ("$status", status), ("$before", before), ("$n", limit + 1));
            using var rd = c.ExecuteReader();
            var list = new List<LedgerListed>();
            while (rd.Read())
                list.Add(new LedgerListed(EntryOf(rd, 0), RevisionOf(rd, 7), rd.GetInt32(16), rd.GetInt32(17)));
            return list;
        });
    }

    /// <summary>
    /// ONE ENTRY, ITS REVISIONS FROM BEFORE <paramref name="before"/> DOWN, ITS LINKS AND WHAT IS ABOUT IT — or null when there
    /// is no such entry. See <see cref="LedgerShown"/>. A read in one snapshot.
    /// </summary>
    public LedgerShown? Show(long entry, int? before) => db.Read(_ =>
    {
        if (Entry(entry) is not { } row) return null;

        var revisions = new List<LedgerRevisionRow>();
        using (var c = db.Cmd($"""
            SELECT {RevisionCols} FROM ledger_revision
             WHERE entry_id=$e AND ($before IS NULL OR revision < $before)
             ORDER BY revision DESC LIMIT $n
            """, ("$e", entry), ("$before", before), ("$n", MaxRevisions + 1)))
        using (var rd = c.ExecuteReader())
            while (rd.Read()) revisions.Add(RevisionOf(rd, 0));

        var links = new List<LedgerLinkRow>();
        using (var c = db.Cmd("""
            SELECT id, entry_id, revision, record_kind, record_id, attempt, at FROM ledger_link
             WHERE entry_id=$e ORDER BY id DESC LIMIT $n
            """, ("$e", entry), ("$n", MaxLinks)))
        using (var rd = c.ExecuteReader())
            while (rd.Read())
                links.Add(new LedgerLinkRow(rd.GetInt64(0), rd.GetInt64(1), rd.GetInt32(2), rd.GetString(3),
                    rd.GetString(4), Sql.S(rd.GetValue(5)), Sql.Time(rd.GetValue(6))));

        var about = new List<LedgerAboutRow>();
        using (var c = db.Cmd("""
            SELECT e.id, e.kind, e.author, r.revision, r.mark, r.status, e.created_at
              FROM ledger_entry e
              JOIN ledger_revision r
                ON r.entry_id = e.id
               AND r.revision = (SELECT MAX(revision) FROM ledger_revision WHERE entry_id = e.id)
             WHERE e.about = $e ORDER BY e.id DESC LIMIT $n
            """, ("$e", entry), ("$n", MaxAbout)))
        using (var rd = c.ExecuteReader())
            while (rd.Read())
                about.Add(new LedgerAboutRow(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3),
                    rd.GetString(4), rd.GetString(5), Sql.Time(rd.GetValue(6))));

        // DERIVED AT READ, NEVER STORED: the versions the linked runs were runs of, and what the app has recorded about
        // those versions since — their promotions and their deployments, by id. A field of an app record is read where
        // it lives, at the moment it is asked for, and nothing of it is written here.
        var versions = Strings("""
            SELECT DISTINCT r.version_id FROM ledger_link l JOIN strategy_run r ON r.id = l.record_id
             WHERE l.entry_id = $e AND l.record_kind = 'run' ORDER BY r.version_id LIMIT $n
            """, entry);
        var promotions = Strings("""
            SELECT DISTINCT p.id FROM ledger_link l
              JOIN strategy_run r ON r.id = l.record_id
              JOIN strategy_promotion p ON p.version_id = r.version_id
             WHERE l.entry_id = $e AND l.record_kind = 'run' ORDER BY p.id LIMIT $n
            """, entry);
        var deployments = Strings("""
            SELECT DISTINCT d.id FROM ledger_link l
              JOIN strategy_run r ON r.id = l.record_id
              JOIN strategy_deployment d ON d.version_id = r.version_id
             WHERE l.entry_id = $e AND l.record_kind = 'run' ORDER BY d.id LIMIT $n
            """, entry);

        return new LedgerShown(row, Count("SELECT COUNT(*) FROM ledger_revision WHERE entry_id=$e", entry), revisions,
            Count("SELECT COUNT(*) FROM ledger_link WHERE entry_id=$e", entry), links,
            Count("SELECT COUNT(*) FROM ledger_entry WHERE about=$e", entry), about, versions, promotions, deployments);
    });

    // ---- the writes' one row ------------------------------------------------------------------------------------------

    void InsertRevision(long entry, int revision, DateTimeOffset at, string? attempt, string text, string mark,
        decimal? confidence, string status, string? why)
    {
        using var c = db.Cmd("""
            INSERT INTO ledger_revision(entry_id, revision, at, attempt, text, mark, confidence, status, why)
            VALUES($e, $rev, $at, $attempt, $text, $mark, $conf, $status, $why)
            """,
            ("$e", entry), ("$rev", revision), ("$at", Sql.T(at)), ("$attempt", attempt), ("$text", text),
            ("$mark", mark), ("$conf", confidence is { } p ? Sql.D(p) : null), ("$status", status), ("$why", why));
        c.ExecuteNonQuery();
    }

    // ---- the checks, each a refusal in words --------------------------------------------------------------------------

    /// <summary>
    /// THE AUTHOR IS A COUNCIL ROLE, the one the launch grant proved — never an app principal (the referee, the allocator,
    /// perception), never nobody. The pipe asks the grant first; this is the store's own floor under it, and the column's
    /// reference to a position is the database's.
    /// </summary>
    static void Author(string author)
    {
        if (!CouncilRoles.IsKnown(author))
            throw Refused($"'{author}' is not a council role, and an entry is written under the role whose launch wrote "
                + "it: an app principal never writes one, and a caller that proved no role cannot. Nothing was written.");
    }

    static void Kind(string kind)
    {
        if (!LedgerKind.IsKnown(kind))
            throw Refused($"'{kind}' is not a kind of entry: an entry is a {string.Join(", ", LedgerKind.All[..^1])} or "
                + $"{LedgerKind.All[^1]}. Nothing was written.");
    }

    static void Mark(string mark)
    {
        if (!LedgerMark.IsKnown(mark))
            throw Refused($"'{mark}' is not a mark: an entry is marked claim, assumption or hypothesis, and nothing you "
                + "write in the ledger is a measurement. What TradeAgent measured is its own record — a run, a verdict — "
                + "and TradeAgent links it to an entry when you ask for it under that entry; your entry stays your claim "
                + "beside it. Nothing was written.");
    }

    static void Status(string status)
    {
        if (!LedgerStatus.IsKnown(status))
            throw Refused($"'{status}' is not a status: an entry is open, held or dropped — your own bookkeeping, which "
                + "decides nothing. Nothing was written.");
    }

    /// <summary>
    /// A CONFIDENCE IS A NUMBER FROM 0 TO 1 OR IT IS NOT STATED. Unstated is null, recorded as unknown and never refused;
    /// stated outside the range is refused, never clamped — a 1.7 clamped to 1 would be a certainty nobody stated.
    /// </summary>
    static void Confidence(decimal? confidence)
    {
        if (confidence is { } p && p is < 0m or > 1m)
            throw Refused($"{p.ToString(CultureInfo.InvariantCulture)} is not a confidence: it is a number from 0 to 1 — "
                + "how sure you are that the entry is true — and left out when you cannot say, which is recorded as "
                + "unknown. Nothing was written.");
    }

    /// <summary>A text, a reason or a source: present, and at most so many characters — refused beyond, never cut.</summary>
    static string Text(string? text, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw Refused($"'{name}' is required and cannot be empty. Nothing was written.");
        var chars = text.EnumerateRunes().Count();
        if (chars > max)
            throw Refused($"'{name}' is {chars.ToString("N0", CultureInfo.InvariantCulture)} characters and holds at most "
                + $"{max.ToString("N0", CultureInfo.InvariantCulture)}: TradeAgent refuses a longer one rather than cut "
                + "it, because a cut claim says something its author did not. Say it shorter, or put the detail in a file "
                + "and name it in 'source'. Nothing was written.");
        return text;
    }

    static TradeAgentException NotThere(long entry) =>
        Refused($"there is no entry {Id(entry)} in the research ledger — 'ledger-list' names them. Nothing was run, "
            + "charged or written.");

    static TradeAgentException Refused(string why) => new(ErrorCode.INVALID_REQUEST, why);

    static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    // ---- reading rows -------------------------------------------------------------------------------------------------

    static LedgerEntryRow EntryOf(SqliteDataReader rd, int at) => new(
        rd.GetInt64(at), rd.GetString(at + 1), rd.GetString(at + 2), rd.IsDBNull(at + 3) ? null : rd.GetInt64(at + 3),
        Sql.S(rd.GetValue(at + 4)), Sql.Time(rd.GetValue(at + 5)), Sql.S(rd.GetValue(at + 6)));

    static LedgerRevisionRow RevisionOf(SqliteDataReader rd, int at) => new(
        rd.GetInt64(at), rd.GetInt32(at + 1), Sql.Time(rd.GetValue(at + 2)), Sql.S(rd.GetValue(at + 3)),
        rd.GetString(at + 4), rd.GetString(at + 5), Sql.DecN(rd.GetValue(at + 6)), rd.GetString(at + 7),
        Sql.S(rd.GetValue(at + 8)));

    int Count(string sql, long entry)
    {
        using var c = db.Cmd(sql, ("$e", entry));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    List<string> Strings(string sql, long entry)
    {
        using var c = db.Cmd(sql, ("$e", entry), ("$n", MaxLinks));
        using var rd = c.ExecuteReader();
        var list = new List<string>();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }
}

/// <summary>
/// THE APP'S LINKS — THE ONE WRITER OF <c>ledger_link</c>, and it only ever inserts (<c>U-research-ledger</c>).
///
/// <para><b>A link says the app answered a request asked under an entry with that record</b>, and nothing more: not that
/// the record supports the entry, refutes it, or is every record that bears on it. It is written by the record's own
/// path, inside the record's own write — <c>Backtests.Record</c> after the run's trials, and the referee's promotion write
/// through its callback — or alone when a verdict asked under an entry is answered by a promotion already recorded. A
/// request refused, stopped or not judged writes no record and so no link.</para>
///
/// <para><b>Its own type, apart from <see cref="ResearchLedger"/>, for the reason <c>material</c> is apart from
/// <c>material_note</c>:</b> the agent's claims have one writer and the app's references to its own records have another.
/// It is public because the backtest path is in the gateway's assembly and this one grants that assembly none of its
/// internals — the holdout audiences depend on that — so what holds it to the two record paths is that a link is written
/// only with a <see cref="LedgerAsk"/>, which the entry check alone mints, and a test that reads every call of it.</para>
///
/// <para><b>Idempotent on (entry, kind, record).</b> The same run asked for again under the same entry is one run — the
/// strategy ledger's first writer wins — and one link: the first, with the revision and the attempt it was asked
/// under.</para>
/// </summary>
public sealed class LedgerLinks(Database db, Func<DateTimeOffset>? now = null)
{
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// LINKS ONE RECORD TO THE ENTRY A REQUEST WAS ASKED UNDER, or leaves the link already there alone. Joins the
    /// transaction already open, so it lands with the record or not at all. Answers whether this call wrote it.
    /// </summary>
    public bool Link(LedgerAsk ask, string recordKind, string recordId, string? attempt)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (!LedgerRecord.IsKnown(recordKind))
            throw new ArgumentException($"'{recordKind}' is not a record a ledger link names", nameof(recordKind));
        if (string.IsNullOrWhiteSpace(recordId))
            throw new ArgumentException("a link names the record by its id", nameof(recordId));

        return db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO ledger_link(entry_id, revision, record_kind, record_id, attempt, at)
                VALUES($e, $rev, $kind, $id, $attempt, $at)
                ON CONFLICT(entry_id, record_kind, record_id) DO NOTHING
                """,
                ("$e", ask.Entry), ("$rev", ask.Revision), ("$kind", recordKind), ("$id", recordId),
                ("$attempt", attempt), ("$at", Sql.T(_now())));
            return c.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Every link of one entry, newest first.</summary>
    public IReadOnlyList<LedgerLinkRow> Of(long entry) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT id, entry_id, revision, record_kind, record_id, attempt, at FROM ledger_link
             WHERE entry_id=$e ORDER BY id DESC
            """, ("$e", entry));
        using var rd = c.ExecuteReader();
        var list = new List<LedgerLinkRow>();
        while (rd.Read())
            list.Add(new LedgerLinkRow(rd.GetInt64(0), rd.GetInt64(1), rd.GetInt32(2), rd.GetString(3), rd.GetString(4),
                Sql.S(rd.GetValue(5)), Sql.Time(rd.GetValue(6))));
        return list;
    });
}
