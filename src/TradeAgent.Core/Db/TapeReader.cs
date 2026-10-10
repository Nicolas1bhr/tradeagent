using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT ONE RANGE READ OF THE TAPE ASKS FOR (<c>U-tape-read</c>): one series of one source, one subject or every
/// subject, the vendor's source time between <see cref="From"/> and <see cref="To"/> (both inclusive), only what had
/// ARRIVED by <see cref="AsOf"/> (inclusive), only rows the tape wrote before row <see cref="Before"/> — newest first,
/// at most <see cref="Limit"/> rows and at most <see cref="MaxBytes"/> as the caller measures them.
/// </summary>
public sealed record TapeQuery
{
    public required string Source { get; init; }
    public required string Series { get; init; }

    /// <summary>One subject, or null for every subject of the series.</summary>
    public string? Subject { get; init; }

    /// <summary>The earliest source time served, inclusive, or null for no bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>The latest source time served, inclusive, or null for no bound.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Only rows that had arrived by this instant, inclusive — or null for everything that has.</summary>
    public DateTimeOffset? AsOf { get; init; }

    /// <summary>Only rows the tape wrote before this row id — the cursor a capped answer hands back — or null.</summary>
    public long? Before { get; init; }

    /// <summary>The most rows served, 1 to <see cref="TapeReader.MaxRows"/>.</summary>
    public int Limit { get; init; } = TapeReader.DefaultRows;

    /// <summary>The most bytes served, as the reader's cost function measures each row.</summary>
    public long MaxBytes { get; init; } = long.MaxValue;
}

/// <summary>
/// What a range read served: the rows, newest first; whether the tape holds more that the read matched; and which
/// bound stopped it — <see cref="TapeReader.CappedByLimit"/>, <see cref="TapeReader.CappedByBytes"/>, or null
/// because nothing did.
///
/// <para><see cref="Refusal"/> is the holdout's (<see cref="TapeHoldout"/>), in words, or null. It comes with NO rows
/// deliberately, as <c>BarWindow.Refusal</c> does: a caller that forgets to look at it is handed nothing, never a row
/// stamped inside a holdout window.</para>
/// </summary>
public sealed record TapeWindow(IReadOnlyList<TapeObservation> Rows, bool More, string? CappedBy, string? Refusal = null);

/// <summary>
/// ONE SERIES OF THE TAPE, as <c>data-list</c> names it: which of the owner's switches records it, its rows, the arrival
/// of its first and newest row in the order the tape wrote them (null while it holds none), what its newest attempt
/// got wrong (null when it delivered), and its subjects where they are symbols — null where they are digests or
/// record ids nobody can enumerate usefully.
/// </summary>
public sealed record TapeSeriesSummary(
    string Source, string Series, string Switch, long Rows, DateTimeOffset? FirstArrival, DateTimeOffset? LastArrival,
    string? LastError, IReadOnlyList<string>? Subjects);

/// <summary>
/// WHETHER THE TAPE IS RECORDING, off its own rows: the rows that arrived since UTC midnight, the attempts that failed in
/// the last hour, each source's newest delivery and current failure, and whether GDELT's daily cap stopped a file
/// labelled today.
/// </summary>
public sealed record TapeRecording(
    long RowsToday, int FailuresLastHour, IReadOnlyList<TapeSourceRecording> Sources, bool GdeltCapReachedToday);

/// <summary>
/// One source's recording: its switch, its newest attempt that delivered, its failures in the last hour, and what is
/// failing now with the instant of that failing attempt.
/// </summary>
public sealed record TapeSourceRecording(
    string Source, string Switch, DateTimeOffset? LastReceivedAt, int FailuresLastHour, string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>
/// ONE DAY OF THE TAPE, for the owner's report (<c>U-tape-read</c>; what it did not record, <c>U-tape-gaps</c>): the rows
/// that arrived in the day's elapsed part, the attempts and the failed ones, the newest failure in words, how many of the
/// rows are GDELT's — whose credit then travels with the line — and every catalogue source's share of the day with its
/// gaps.
/// </summary>
/// <param name="From">The day's first instant.</param>
/// <param name="To">The instant the day ends.</param>
/// <param name="Until">
/// Where the day's ELAPSED part ends: <paramref name="To"/> once the day is over, the report's instant while it is still
/// open, and <paramref name="From"/> for a day that had not begun.
/// </param>
public sealed record TapeDay(
    DateTimeOffset From, DateTimeOffset To, DateTimeOffset Until,
    long Rows, int Requests, int Failed, string? LastError, long GdeltRows,
    IReadOnlyList<TapeSourceDay> Sources)
{
    /// <summary>How much of the day had passed at the report's instant.</summary>
    public TimeSpan Elapsed => Until - From;

    /// <summary>Whether the day was still open at the report's instant.</summary>
    public bool Open => Until < To;

    /// <summary>Where the look back for each source's delivery before the day stopped: <see cref="TapeReader.GapLookBack"/> before it.</summary>
    public DateTimeOffset LookedBackTo => From - TapeReader.GapLookBack;

    /// <summary>Every source's gaps on this day, counted.</summary>
    public int Gaps => Sources.Sum(s => s.Gaps.Count);

    /// <summary>The day's longest gap, by the part of it on this day, and its source — or null on a day without one.</summary>
    public (string Source, TapeGap Gap)? Longest
    {
        get
        {
            (string Source, TapeGap Gap)? longest = null;
            foreach (var source in Sources)
                foreach (var gap in source.Gaps)
                    if (longest is not { } held || gap.Length > held.Gap.Length) longest = (source.Source, gap);
            return longest;
        }
    }
}

/// <summary>
/// ONE CATALOGUE SOURCE'S DAY (<c>U-tape-gaps</c>): its allowance, when its recording began, the part of the day it
/// recorded, its gaps, and its attempts in the day with the newest failure among them.
/// </summary>
/// <param name="Allowed">The longest stretch without a delivery that is not a gap: twice the source's cadence plus 30 s.</param>
/// <param name="Begun">Whether the source had delivered at all before the end of the day's elapsed part.</param>
/// <param name="Began">
/// When its recording began — its first delivery ever — or null when it has delivered nothing at the report's instant.
/// Before it nothing is a gap.
/// </param>
/// <param name="Recorded">The day's elapsed part, less what came before its recording began and less its gaps.</param>
/// <param name="Attempts">Its attempts that arrived in the day's elapsed part, delivered or not.</param>
/// <param name="Failed">Of them, the ones that delivered nothing.</param>
/// <param name="NewestFailure">The newest of those failures in words, or null.</param>
public sealed record TapeSourceDay(
    string Source, TimeSpan Allowed, bool Begun, DateTimeOffset? Began, TimeSpan Recorded, IReadOnlyList<TapeGap> Gaps,
    int Attempts, int Failed, string? NewestFailure);

/// <summary>
/// ONE STRETCH LONGER THAN A SOURCE'S ALLOWANCE WITH NO DELIVERY FROM IT (<c>U-tape-gaps</c>), as one day sees it: the
/// whole gap, from the delivery before it to the delivery after it, across midnights and restarts, and the part of it
/// on this day — named by what the tape holds inside that part.
/// </summary>
/// <param name="From">The delivery the gap began after, or null: there was none since <see cref="TapeDay.LookedBackTo"/>.</param>
/// <param name="To">The delivery that ended it, or null: none yet at the report's instant — the gap is still open.</param>
/// <param name="ClippedFrom">Where the gap enters this day.</param>
/// <param name="ClippedTo">Where it leaves the day's elapsed part.</param>
/// <param name="Attempts">
/// The source's attempts inside the part on this day. Every one of them failed — a delivery would have ended the gap — and
/// none at all means nothing was asked: TradeAgent was not running or the switch was off, and the tape cannot tell which.
/// </param>
/// <param name="NewestFailure">The newest of those attempts' failures in words, or null when nothing was asked.</param>
public sealed record TapeGap(
    DateTimeOffset? From, DateTimeOffset? To, DateTimeOffset ClippedFrom, DateTimeOffset ClippedTo, int Attempts,
    string? NewestFailure)
{
    /// <summary>The length of the part on this day.</summary>
    public TimeSpan Length => ClippedTo - ClippedFrom;
}

/// <summary>
/// THE TAPE, READ — AND ONLY READ (<c>U-tape-read</c>; <c>docs/EDGE-FACTORY.md</c> § 4.1): what the gateway serves to
/// every role over <c>data-tape</c>, <c>data-list</c>, <c>status</c> and the daily report.
///
/// <para><b>It cannot write, and that is enforced by SQLite rather than promised here.</b> Every read opens its own
/// connection to the tape's file in <see cref="SqliteOpenMode.ReadOnly"/> mode with <c>query_only</c> set, so a
/// statement that would change the file is refused by the database itself. It has no method that writes, and it holds
/// no <see cref="TapeStore"/>: the gateway, which holds this, has no path to <see cref="TapeStore.Append"/> at all. The
/// app's collectors are still the only writers, through the store's own connection.</para>
///
/// <para><b>Its own connection per read</b>, and that is the second reason it is not the store: the tape is in WAL
/// mode, so a reader never blocks the collectors' writes and a long read cannot hold the store's one lock while a
/// look's answer waits to be written. Each read sees one snapshot of the file.</para>
///
/// <para><b>A payload is withheld in one place.</b> Every row read here passes through <see cref="TapeStore.Obs"/>,
/// which screens it, and then <see cref="TapeStore.Served"/>, which withholds a quarantined payload from the audience —
/// the same two calls <see cref="TapeStore.AsOf"/> makes. There is no read here that returns a payload without both.</para>
///
/// <para><b>And a holdout window is refused in one place</b> (<c>U-tape-holdout</c>): <see cref="Window"/>, the one read
/// here that serves rows, cannot be called without a <see cref="TapeHoldout"/> and refuses a window reaching any
/// dataset's held-back months before the file is opened. The other reads serve counts, names and arrival instants —
/// never a value — and take none.</para>
///
/// <para><b>Refuse, never guess.</b> A tape whose layout version is not the one this build writes is refused when the
/// reader is made, naming both versions; the app makes one only after <see cref="TapeStore"/> has opened the file, so
/// that is a file something else changed.</para>
/// </summary>
public sealed class TapeReader
{
    /// <summary>The most rows one read serves. Named in every refusal of a larger limit.</summary>
    public const int MaxRows = 5_000;

    /// <summary>The rows a read serves when it names no limit.</summary>
    public const int DefaultRows = 1_000;

    /// <summary>The word for a read stopped by its row limit.</summary>
    public const string CappedByLimit = "limit";

    /// <summary>The word for a read stopped by its byte budget.</summary>
    public const string CappedByBytes = "bytes";

    /// <param name="file">The tape's file. It must exist and carry this build's layout.</param>
    /// <param name="catalogue">
    /// The rows the tape is recorded from — what makes a series known before its first row arrives, how a series'
    /// subjects are keyed, its cadence and its credit. Null is <see cref="TapeSourceCatalog.Shipped"/>; the app passes
    /// the catalogue its collectors read.
    /// </param>
    public TapeReader(string file, IReadOnlyList<TapeSourceEntry>? catalogue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        File = file;
        Catalogue = catalogue ?? TapeSourceCatalog.Shipped();

        if (!System.IO.File.Exists(file))
            throw new InvalidOperationException($"there is no market-context tape at {file} to read");

        using var c = Open();
        var have = LayoutVersion(c);
        if (have != TapeStore.Schema)
            throw new InvalidOperationException(
                $"the market-context tape at {file} carries layout version {(have is { } v ? v.ToString(CultureInfo.InvariantCulture) : "none")}, "
                + $"and this build reads version {TapeStore.Schema}. Nothing has been read from it; the tape is read only once "
                + "TradeAgent's own store has opened it at this build's layout.");
    }

    /// <summary>The file read.</summary>
    public string File { get; }

    /// <summary>The rows the tape is recorded from, as this reader was given them.</summary>
    public IReadOnlyList<TapeSourceEntry> Catalogue { get; }

    /// <summary>The catalogue row of <paramref name="source"/>, or null for a source no row names any more.</summary>
    public TapeSourceEntry? Row(string source) =>
        Catalogue.FirstOrDefault(r => string.Equals(r.Id, source, StringComparison.Ordinal));

    // ------------------------------------------------------------------------------ the range read

    /// <summary>
    /// THE ROWS OF ONE SERIES, NEWEST FIRST, BOUNDED — the agent-facing range read (<c>data-tape</c>).
    ///
    /// <para><b>Newest first is the order the tape wrote them</b> (row id, descending). Within one series that is the
    /// order they arrived: each source is written by one loop, look after look, and GDELT's two tasks take turns. A
    /// row's <c>id</c> is its place in that order, and <see cref="TapeQuery.Before"/> continues a capped read exactly —
    /// which no time can, because a GDELT file's rows share both its label and its arrival.</para>
    ///
    /// <para><b>Bounded twice and never silently.</b> At most <see cref="TapeQuery.Limit"/> rows, and the rows' cost as
    /// <paramref name="cost"/> measures them (the payload's UTF-8 bytes when it is null) at most
    /// <see cref="TapeQuery.MaxBytes"/> — a row is never split, so the first row is served whatever it costs, and a
    /// payload is at most 64 KB. When either bound stops the read and the tape holds another matching row, the answer
    /// says so (<see cref="TapeWindow.More"/>, <see cref="TapeWindow.CappedBy"/>).</para>
    ///
    /// <para><b>The holdout is required, and it is checked HERE, before the tape is opened</b> (<c>U-tape-holdout</c>): a
    /// read whose source-time window reaches any dataset's holdout window is answered with
    /// <see cref="TapeWindow.Refusal"/> and no row — never clipped, whatever the source, series or subject — unless
    /// <paramref name="holdout"/> is the referee's. Every row then passes through <see cref="TapeStore.Served"/>: a
    /// quarantined item is served with its quarantine and without its payload, to every audience.</para>
    ///
    /// <para>Two phases in one snapshot: the ids, off the tape's own index on (source, series, subject, source time),
    /// and then each row by id — so a read never carries a payload it does not serve, and an "as of" in the past costs
    /// one short lookup for every row that arrived after it.</para>
    /// </summary>
    public TapeWindow Window(TapeHoldout holdout, TapeQuery query, Func<TapeObservation, long>? cost = null)
    {
        ArgumentNullException.ThrowIfNull(holdout);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaxRows)
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit,
                $"a read of the tape serves 1 to {MaxRows} rows, and {query.Limit} were asked for");
        if (query.MaxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(query), query.MaxBytes, "a read of the tape needs a byte budget above 0");

        // THE HOLDOUT, BEFORE THE FILE IS OPENED: the asked window of SOURCE time against every dataset's window, read
        // from the ledger now. Refused whole, never cut short at the window.
        if (holdout.Refusal(query.From, query.To) is { } withheld) return new TapeWindow([], false, null, withheld);

        using var c = Open();
        // DEFERRED: a plain BEGIN, which takes no write lock — the default is BEGIN IMMEDIATE, and a read-only
        // connection refuses that, rightly. The snapshot starts at the first read and holds for both phases.
        using var snapshot = c.BeginTransaction(deferred: true);

        var where = new StringBuilder("source=$src AND series=$ser");
        var ps = new List<(string, object?)> { ("$src", query.Source), ("$ser", query.Series) };
        if (query.Subject is { } subject) { where.Append(" AND subject=$subj"); ps.Add(("$subj", subject)); }
        if (query.From is { } from) { where.Append(" AND source_time >= $from"); ps.Add(("$from", Sql.T(from))); }
        if (query.To is { } to) { where.Append(" AND source_time <= $to"); ps.Add(("$to", Sql.T(to))); }
        if (query.Before is { } before) { where.Append(" AND id < $before"); ps.Add(("$before", before)); }

        // ONE PAST THE LIMIT IS ENOUGH TO KNOW THERE IS MORE — when nothing else can turn a candidate away. With an
        // "as of", rows that arrived later are skipped one by one, so the ids are streamed instead.
        var limit = query.AsOf is null ? " LIMIT $n" : "";
        if (query.AsOf is null) ps.Add(("$n", query.Limit + 1));

        using var ids = Cmd(c, $"SELECT id FROM tape_obs INDEXED BY ix_tape_obs_asof WHERE {where} ORDER BY id DESC{limit}",
            [.. ps]);
        using var arrived = Cmd(c, "SELECT received_at <= $asof FROM tape_obs WHERE id=$id",
            ("$asof", query.AsOf is { } a ? Sql.T(a) : null), ("$id", 0L));
        using var row = Cmd(c, $"SELECT {TapeStore.ObsCols} FROM tape_obs WHERE id=$id", ("$id", 0L));

        var rows = new List<TapeObservation>();
        long spent = 0;

        using var r = ids.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);

            if (query.AsOf is not null)
            {
                arrived.Parameters["$id"].Value = id;
                if (Convert.ToInt64(arrived.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) continue;
            }

            if (rows.Count == query.Limit) return new TapeWindow(rows, true, CappedByLimit);

            row.Parameters["$id"].Value = id;
            TapeObservation o;
            using (var one = row.ExecuteReader())
            {
                if (!one.Read()) continue;   // unreachable: the tape deletes nothing, and this is one snapshot
                o = TapeStore.Served(holdout.Audience, TapeStore.Obs(one));
            }

            var size = cost?.Invoke(o) ?? PayloadBytes(o);
            if (rows.Count > 0 && spent + size > query.MaxBytes) return new TapeWindow(rows, true, CappedByBytes);

            rows.Add(o);
            spent += size;
        }

        return new TapeWindow(rows, false, null);
    }

    /// <summary>The UTF-8 bytes of a row's payload as served: nothing for one withheld.</summary>
    public static long PayloadBytes(TapeObservation o) =>
        o.Payload is null ? 0 : Encoding.UTF8.GetByteCount(o.Payload);

    // ------------------------------------------------------------------------------ what is there

    /// <summary>
    /// EVERY SOURCE THE TAPE KNOWS OF, in ordinal order: every catalogue row, and every source the tape holds an attempt
    /// of — a row <c>tape-sources.json</c> once added and no longer does is still on the tape and still read.
    /// </summary>
    public IReadOnlyList<string> Sources()
    {
        using var c = Open();
        var sources = new SortedSet<string>(Catalogue.Select(r => r.Id), StringComparer.Ordinal);
        foreach (var s in Distinct(c, "SELECT source FROM tape_fetch INDEXED BY ix_tape_fetch_series WHERE source > $after ORDER BY source LIMIT 1"))
            sources.Add(s);
        return [.. sources];
    }

    /// <summary>
    /// THE OBSERVATION SERIES OF ONE SOURCE, in ordinal order: the catalogue row's series, and every series the tape holds
    /// rows of under it — GDELT's per-file records (<c>gkg-batch</c>) among them. A series an attempt was filed under and
    /// no row was (<c>lastupdate</c>, say) is not one.
    /// </summary>
    public IReadOnlyList<string> SeriesOf(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var c = Open();
        var series = new SortedSet<string>(StringComparer.Ordinal);
        if (Row(source) is { } row)
            foreach (var s in row.Series) series.Add(s.Id);
        foreach (var s in Distinct(c,
                     "SELECT series FROM tape_obs INDEXED BY ix_tape_obs_asof WHERE source=$src AND series > $after ORDER BY series LIMIT 1",
                     ("$src", source)))
            series.Add(s);
        return [.. series];
    }

    /// <summary>Whether the tape holds any row of <paramref name="subject"/> in that series, at any time.</summary>
    public bool Holds(string source, string series, string subject)
    {
        using var c = Open();
        using var cmd = Cmd(c,
            "SELECT EXISTS(SELECT 1 FROM tape_obs INDEXED BY ix_tape_obs_asof WHERE source=$src AND series=$ser AND subject=$subj)",
            ("$src", source), ("$ser", series), ("$subj", subject));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// The subjects the tape holds rows of in that series, in ordinal order, at most <paramref name="max"/> — read off
    /// the index one subject at a time, so a series of a few symbols costs a few lookups whatever its size.
    /// </summary>
    public IReadOnlyList<string> Subjects(string source, string series, int max = 64)
    {
        using var c = Open();
        return [.. Distinct(c,
            "SELECT subject FROM tape_obs INDEXED BY ix_tape_obs_asof WHERE source=$src AND series=$ser AND subject > $after ORDER BY subject LIMIT 1",
            ("$src", source), ("$ser", series)).Take(max)];
    }

    // ------------------------------------------------------------------------------ what is recorded

    /// <summary>The owner's switch over the market rows, the announcements and every row <c>tape-sources.json</c> adds.</summary>
    public const string MarketContextSwitch = "Record market context";

    /// <summary>The owner's switch over GDELT's recorder.</summary>
    public const string GdeltNewsSwitch = "Record GDELT news";

    /// <summary>Which of the owner's two switches governs a source's recording.</summary>
    public static string SwitchOf(string source) =>
        string.Equals(source, GdeltGkg.Source, StringComparison.Ordinal) ? GdeltNewsSwitch : MarketContextSwitch;

    /// <summary>
    /// HOW FAR OUT OF ARRIVAL ORDER A ROW CAN BE WRITTEN — the one assumption the counts below stand on. Every writer of
    /// the tape takes its arrival instant moments before it takes the store's one lock, and a write holds that lock for
    /// milliseconds, so a row is never written after a row that arrived more than this much later than it. Ten minutes
    /// is that, many times over, and a machine clock stepped back by more than it is the case it does not cover: a day's
    /// counts across such a step can then be off by the rows written during it.
    /// </summary>
    public static readonly TimeSpan ArrivalSlack = TimeSpan.FromMinutes(10);

    /// <summary>
    /// EVERY SERIES OF THE TAPE, as <c>data-list</c> names it: every catalogue series, every series the tape holds rows
    /// of, and every series a source no row names any more was attempted under — with its rows, the arrival of its first
    /// and newest row in the order the tape wrote them, what its newest attempt got wrong, and its subjects where they
    /// are symbols. One snapshot.
    /// </summary>
    public IReadOnlyList<TapeSeriesSummary> Series()
    {
        using var c = Open();
        using var snapshot = c.BeginTransaction(deferred: true);

        var pairs = new SortedSet<(string Source, string Series)>(Comparer<(string, string)>.Create((a, b) =>
        {
            var bySource = string.CompareOrdinal(a.Item1, b.Item1);
            return bySource != 0 ? bySource : string.CompareOrdinal(a.Item2, b.Item2);
        }));
        foreach (var row in Catalogue)
            foreach (var s in row.Series) pairs.Add((row.Id, s.Id));
        foreach (var pair in DistinctPairs(c, "tape_obs", "ix_tape_obs_asof")) pairs.Add(pair);

        // A SOURCE NO ROW NAMES ANY MORE, attempted and never delivered, is still a series of the tape: its attempts are
        // what say why it holds nothing. A built-in source's attempt-only series (GDELT's `lastupdate`) are not.
        var attempts = Attempts(c);
        foreach (var a in attempts.Where(a => Row(a.Source) is null)) pairs.Add((a.Source, a.Series));

        using var count = Cmd(c,
            "SELECT COUNT(*), MIN(id), MAX(id) FROM tape_obs INDEXED BY ix_tape_obs_asof WHERE source=$src AND series=$ser",
            ("$src", ""), ("$ser", ""));
        using var arrival = Cmd(c, "SELECT received_at FROM tape_obs WHERE id=$id", ("$id", 0L));

        var summaries = new List<TapeSeriesSummary>();
        foreach (var (source, series) in pairs)
        {
            count.Parameters["$src"].Value = source;
            count.Parameters["$ser"].Value = series;
            long rows;
            long? first = null, newest = null;
            using (var r = count.ExecuteReader())
            {
                r.Read();
                rows = r.GetInt64(0);
                if (!r.IsDBNull(1)) { first = r.GetInt64(1); newest = r.GetInt64(2); }
            }

            DateTimeOffset? ArrivalOf(long? id)
            {
                if (id is not { } at) return null;
                arrival.Parameters["$id"].Value = at;
                return arrival.ExecuteScalar() is string text ? Sql.Time(text) : null;
            }

            var mine = attempts.Where(a => a.Source == source).ToList();
            var exact = mine.FirstOrDefault(a => a.Series == series);
            var lastError = exact is not null ? exact.NewestNote : Failing(mine)?.NewestNote;

            // A SERIES OF SYMBOLS LISTS THEM — Binance's market rows, Hyperliquid's contexts (U-tape-chain) and any source no
            // row names any more; a series keyed by digests or record ids lists none.
            var parser = Row(source)?.Parser;
            IReadOnlyList<string>? subjects = parser is null || TapeSourceCatalog.MarketParsers.Contains(parser, StringComparer.Ordinal)
                ? [.. Distinct(c,
                    "SELECT subject FROM tape_obs INDEXED BY ix_tape_obs_asof WHERE source=$src AND series=$ser AND subject > $after ORDER BY subject LIMIT 1",
                    ("$src", source), ("$ser", series)).Take(64)]
                : null;

            summaries.Add(new TapeSeriesSummary(source, series, SwitchOf(source), rows, ArrivalOf(first), ArrivalOf(newest),
                lastError, subjects));
        }

        return summaries;
    }

    /// <summary>
    /// WHETHER THE TAPE IS RECORDING, READ OFF ITS OWN ROWS at <paramref name="now"/>: per source, the newest attempt that
    /// delivered, the attempts that failed in the last hour and what is failing now; the rows that arrived since UTC
    /// midnight; and whether GDELT's daily cap stopped a file labelled today. Never off "when a loop last ran": a
    /// recorder looking happily at a vendor that answers nothing is exactly what this must not report as recording.
    ///
    /// <para>Cheap at any size of tape: a few seeks per series, a binary search for where the hour and the day begin in
    /// arrival order (<see cref="ArrivalSlack"/>), the last hour's attempts, and a count over the row-to-attempt index.</para>
    /// </summary>
    public TapeRecording Recording(DateTimeOffset now)
    {
        using var c = Open();
        using var snapshot = c.BeginTransaction(deferred: true);

        var hourAgo = now - TimeSpan.FromHours(1);
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = Cmd(c, """
                   SELECT source, COUNT(*) FROM tape_fetch
                   WHERE id >= $from AND received_at >= $hour AND note IS NOT NULL
                   GROUP BY source
                   """,
                   ("$from", FirstAtOrAfter(c, "tape_fetch", hourAgo - ArrivalSlack)), ("$hour", Sql.T(hourAgo))))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) failures[r.GetString(0)] = r.GetInt32(1);

        var dayLow = FirstAtOrAfter(c, "tape_fetch", today - ArrivalSlack);
        var dayHigh = FirstAtOrAfter(c, "tape_fetch", today + ArrivalSlack);
        long rowsToday;
        using (var surely = Cmd(c, "SELECT COUNT(*) FROM tape_obs INDEXED BY ix_tape_obs_fetch WHERE fetch_id >= $high",
                   ("$high", dayHigh)))
        using (var band = Cmd(c, """
                   SELECT COUNT(*) FROM tape_obs o JOIN tape_fetch f ON f.id = o.fetch_id
                   WHERE f.id >= $low AND f.id < $high AND f.received_at >= $today
                   """,
                   ("$low", dayLow), ("$high", dayHigh), ("$today", Sql.T(today))))
            rowsToday = Convert.ToInt64(surely.ExecuteScalar(), CultureInfo.InvariantCulture)
                        + Convert.ToInt64(band.ExecuteScalar(), CultureInfo.InvariantCulture);

        // THE DAY'S CAP, AS THE RECORDER WROTE IT DOWN: an attempt at a file labelled today whose note says its rows were
        // not stored because they would pass the cap. Off the record, so a restart does not forget it.
        var capped = false;
        using (var cmd = Cmd(c, """
                   SELECT url FROM tape_fetch INDEXED BY ix_tape_fetch_series
                   WHERE source=$g AND id >= $low AND substr(note, 1, length($prefix)) = $prefix
                   """,
                   ("$g", GdeltGkg.Source), ("$low", dayLow), ("$prefix", GdeltGkg.CapNotePrefix)))
        using (var r = cmd.ExecuteReader())
            while (!capped && r.Read())
                capped = GdeltGkg.LabelOf(r.GetString(0)) is { } label && label.UtcDateTime.Date == today.UtcDateTime;

        var sources = Attempts(c)
            .GroupBy(a => a.Source, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var failing = Failing([.. g]);
                return new TapeSourceRecording(g.Key, SwitchOf(g.Key), g.Max(a => a.LastDelivered),
                    failures.GetValueOrDefault(g.Key), failing?.NewestNote, failing?.NewestAt);
            })
            .ToList();

        return new TapeRecording(rowsToday, failures.Values.Sum(), sources, capped);
    }

    /// <summary>
    /// HOW FAR BEFORE A DAY THE READER LOOKS FOR A SOURCE'S DELIVERY BEFORE IT (<c>U-tape-gaps</c>): eight days, the
    /// background loop's seven days of owed reports and one more. A source that delivered nothing in them is "no delivery
    /// since before" the date it stopped at — its gap is still counted, and a seek through months of failures is not made.
    /// </summary>
    public static readonly TimeSpan GapLookBack = TimeSpan.FromDays(8);

    /// <summary>
    /// ONE LOCAL DAY OF THE TAPE, for the owner's report, AS IT STOOD AT <paramref name="asOf"/> — the report's instant.
    /// The day's ELAPSED part is [<paramref name="from"/>, <paramref name="to"/>) once the day is over, and ends at
    /// <paramref name="asOf"/> while it is still open. In that part: the rows that arrived, the attempts and the failed
    /// ones, GDELT's share of the rows, the newest failure in words — and every catalogue source's share of it, with its
    /// GAPS (<c>U-tape-gaps</c>).
    ///
    /// <para><b>A gap</b> is a stretch longer than twice the source's cadence plus 30 s with no delivery from it — an
    /// attempt whose note is empty — from its last delivery before to its first after, across midnights and restarts;
    /// one still open ends at <paramref name="asOf"/>. The day counts every gap that overlaps it, clipped to its elapsed
    /// part, so a night the app was down is a gap on both days it touches, and a source with no delivery that day is one
    /// gap of the whole day whenever the day is longer than its allowance — every built-in source's always is. Before a
    /// source's first delivery ever nothing is a gap: the day says when its recording began.</para>
    ///
    /// <para><b>Each gap is named by what the tape holds inside its part of the day</b>: the attempts there, every one of
    /// them failed, and the newest failure — or none, which is "nothing asked": TradeAgent was not running or the switch
    /// was off, and the tape cannot tell which, because it keeps no record of its own runs.</para>
    ///
    /// <para><b>Where the deliveries just outside the day come from.</b> The day's band of attempts in arrival order
    /// (<see cref="ArrivalSlack"/>) holds the ones a few minutes either side, and of those, per (source, series), the
    /// latest that arrived before the day and the earliest that arrived at or after its elapsed part's end are taken by
    /// their arrival instants — never by the order the tape wrote them, which follows arrival only to within the slack.
    /// The rest are found per (source, series) on the attempts' own index — the newest before the band that arrived no
    /// earlier than <see cref="TapeDay.LookedBackTo"/>, <see cref="GapLookBack"/> before the day, by its arrival instant,
    /// and the first after it — because within one series the tape writes attempts in the order they arrived: each
    /// source is written by one loop, and GDELT's tasks each by their own. A source's first delivery ever is found the
    /// same way, walking forward from its first attempt. Each of these walks passes over failed attempts only, so it is
    /// long only for a series that has never delivered, or has failed ever since the day — as the status read's walk back
    /// to a series' newest delivery is for one that has stopped.</para>
    /// </summary>
    public TapeDay Day(DateTimeOffset from, DateTimeOffset to, DateTimeOffset asOf)
    {
        if (to <= from) throw new ArgumentOutOfRangeException(nameof(to), to, "a day ends after it begins");
        var until = asOf <= from ? from : asOf < to ? asOf : to;

        using var c = Open();
        using var snapshot = c.BeginTransaction(deferred: true);

        var low = FirstAtOrAfter(c, "tape_fetch", from - ArrivalSlack);
        var high = FirstAtOrAfter(c, "tape_fetch", until + ArrivalSlack);

        // THE DAY'S BAND: the attempts of the elapsed part, and the deliveries just either side of it — per series the
        // latest that arrived before it and the earliest that arrived at or after its end, by their arrival instants, since
        // the order the tape wrote them follows arrival only to within ArrivalSlack.
        int requests = 0, failed = 0;
        string? lastError = null;
        var attempts = new Dictionary<string, List<(DateTimeOffset At, string? Note)>>(StringComparer.Ordinal);
        var edges = new Dictionary<(string Source, string Series), (DateTimeOffset? Before, DateTimeOffset? After)>();
        using (var cmd = Cmd(c, "SELECT source, series, received_at, note FROM tape_fetch WHERE id >= $low AND id < $high ORDER BY id",
                   ("$low", low), ("$high", high)))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var source = r.GetString(0);
                var at = Sql.Time(r.GetString(2));
                var note = r.IsDBNull(3) ? null : r.GetString(3);

                if (at < from || at >= until)
                {
                    if (note is not null) continue;
                    var key = (source, r.GetString(1));
                    var edge = edges.GetValueOrDefault(key);
                    edges[key] = at < from
                        ? (edge.Before is { } b && b >= at ? b : at, edge.After)
                        : (edge.Before, edge.After is { } a && a <= at ? a : at);
                    continue;
                }

                requests++;
                if (note is not null) { failed++; lastError = note; }
                if (!attempts.TryGetValue(source, out var mine)) attempts[source] = mine = [];
                mine.Add((at, note));
            }

        long rows, gdelt;
        using (var cmd = Cmd(c, """
                   SELECT COUNT(*), COALESCE(SUM(f.source = $g), 0) FROM tape_obs o JOIN tape_fetch f ON f.id = o.fetch_id
                   WHERE f.id >= $low AND f.id < $high AND f.received_at >= $from AND f.received_at < $until
                   """,
                   ("$g", GdeltGkg.Source), ("$low", low), ("$high", high), ("$from", Sql.T(from)), ("$until", Sql.T(until))))
        using (var r = cmd.ExecuteReader())
        {
            r.Read();
            rows = r.GetInt64(0);
            gdelt = r.GetInt64(1);
        }

        // PER (SOURCE, SERIES), ON THE ATTEMPTS' OWN INDEX: the first delivery ever, the newest before the band — one that
        // arrived no earlier than where the day says its look back stopped, TapeDay.LookedBackTo: the ids fence the walk
        // with ArrivalSlack to spare, and the arrival instant bounds what it returns — and the first after it. Rows below
        // `low` all arrived before the day; rows from `high` on all arrived after its elapsed part.
        var lookedBack = from - GapLookBack;
        var back = FirstAtOrAfter(c, "tape_fetch", lookedBack - ArrivalSlack);
        using var first = Cmd(c,
            "SELECT received_at FROM tape_fetch INDEXED BY ix_tape_fetch_series WHERE source=$src AND series=$ser AND note IS NULL ORDER BY id LIMIT 1",
            ("$src", ""), ("$ser", ""));
        using var before = Cmd(c, """
            SELECT received_at FROM tape_fetch INDEXED BY ix_tape_fetch_series
            WHERE source=$src AND series=$ser AND id >= $back AND id < $low AND received_at >= $since AND note IS NULL
            ORDER BY id DESC LIMIT 1
            """, ("$src", ""), ("$ser", ""), ("$back", back), ("$low", low), ("$since", Sql.T(lookedBack)));
        using var after = Cmd(c, """
            SELECT received_at FROM tape_fetch INDEXED BY ix_tape_fetch_series
            WHERE source=$src AND series=$ser AND id >= $high AND note IS NULL ORDER BY id LIMIT 1
            """, ("$src", ""), ("$ser", ""), ("$high", high));

        DateTimeOffset? Seek(SqliteCommand cmd, string source, string series)
        {
            cmd.Parameters["$src"].Value = source;
            cmd.Parameters["$ser"].Value = series;
            return cmd.ExecuteScalar() is string text ? Sql.Time(text) : null;
        }

        var pairs = DistinctPairs(c, "tape_fetch", "ix_tape_fetch_series");
        var sources = new List<TapeSourceDay>();
        foreach (var row in Catalogue)
        {
            if (row.Cadence <= TimeSpan.Zero) continue;
            var series = pairs.Where(p => p.Source == row.Id).Select(p => p.Series).ToList();
            var mine = attempts.GetValueOrDefault(row.Id) ?? [];
            var began = Earliest(series.Select(s => Seek(first, row.Id, s)));

            // THE DELIVERY BEFORE THE DAY, when the recording began before it: the band's, else the index's within the look
            // back. None there means the gap began before the look back stopped. And the first after the day's elapsed
            // part, which only a day already over can have.
            DateTimeOffset? previous = began < from
                ? Latest(series.Select(s => edges.GetValueOrDefault((row.Id, s)).Before ?? Seek(before, row.Id, s)))
                : null;
            DateTimeOffset? next = asOf > until && began < until
                ? Earliest(series.Select(s => edges.GetValueOrDefault((row.Id, s)).After ?? Seek(after, row.Id, s)))
                : null;

            sources.Add(SourceDay(row, from, until, asOf, mine, began, previous, next));
        }

        return new TapeDay(from, to, until, rows, requests, failed, lastError, gdelt, sources);
    }

    /// <summary>
    /// ONE SOURCE'S DAY, from its attempts in the elapsed part, its first delivery ever, and the deliveries just either
    /// side of the part (<see cref="Day"/>): walked from the delivery before — or from before the look back, when there
    /// was none in it — through the day's deliveries to the one after, or to <paramref name="asOf"/> while none has come.
    /// </summary>
    static TapeSourceDay SourceDay(TapeSourceEntry row, DateTimeOffset from, DateTimeOffset until, DateTimeOffset asOf,
        List<(DateTimeOffset At, string? Note)> attempts, DateTimeOffset? began, DateTimeOffset? previous, DateTimeOffset? next)
    {
        var allowed = 2 * row.Cadence + TapeSourceCatalog.LiveTolerance;
        var failures = attempts.Where(a => a.Note is not null).ToList();
        var newestFailure = failures.Count > 0 ? failures.MaxBy(a => a.At).Note : null;

        // NOT BEGUN BY THE END OF THE ELAPSED PART: nothing recorded, and nothing a gap.
        if (began is not { } start || start >= until)
            return new TapeSourceDay(row.Id, allowed, false, began, TimeSpan.Zero, [], attempts.Count, failures.Count, newestFailure);

        var gaps = new List<TapeGap>();
        void Gap(DateTimeOffset? a, DateTimeOffset? b)
        {
            var clippedFrom = a is { } x && x > from ? x : from;
            var clippedTo = b is { } y && y < until ? y : until;
            if (clippedTo <= clippedFrom) return;

            var inside = failures.Where(f => f.At > (a ?? DateTimeOffset.MinValue) && f.At < (b ?? DateTimeOffset.MaxValue)
                                             && f.At >= clippedFrom && f.At < clippedTo).ToList();
            gaps.Add(new TapeGap(a, b, clippedFrom, clippedTo, inside.Count, inside.Count > 0 ? inside.MaxBy(f => f.At).Note : null));
        }

        // A STRETCH STARTS AT THE DELIVERY BEFORE — null when there was none in the look back — or, when the recording began
        // on this day, at its first delivery, before which nothing is a gap.
        var stretchFrom = start < from ? previous : null;
        var stretching = start < from;
        foreach (var at in attempts.Where(a => a.Note is null).Select(a => a.At).Order())
        {
            if (stretching && (stretchFrom is not { } since || at - since > allowed)) Gap(stretchFrom, at);
            stretchFrom = at;
            stretching = true;
        }

        if (next is { } then)
        {
            if (stretchFrom is not { } since || then - since > allowed) Gap(stretchFrom, then);
        }
        else if (stretchFrom is not { } since || asOf - since > allowed) Gap(stretchFrom, null);

        var recorded = until - (start > from ? start : from) - gaps.Aggregate(TimeSpan.Zero, (sum, g) => sum + g.Length);
        return new TapeSourceDay(row.Id, allowed, true, began, recorded, gaps, attempts.Count, failures.Count, newestFailure);
    }

    static DateTimeOffset? Earliest(IEnumerable<DateTimeOffset?> times) => times.Where(t => t is not null).Min();

    static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> times) => times.Where(t => t is not null).Max();

    /// <summary>
    /// The newest attempt of every (source, series) the tape holds attempts of, and the newest of them that delivered —
    /// two seeks each on the attempts' own index.
    /// </summary>
    static List<TapeAttempts> Attempts(SqliteConnection c)
    {
        using var newest = Cmd(c,
            "SELECT id, received_at, note FROM tape_fetch INDEXED BY ix_tape_fetch_series WHERE source=$src AND series=$ser ORDER BY id DESC LIMIT 1",
            ("$src", ""), ("$ser", ""));
        using var delivered = Cmd(c,
            "SELECT received_at FROM tape_fetch INDEXED BY ix_tape_fetch_series WHERE source=$src AND series=$ser AND note IS NULL ORDER BY id DESC LIMIT 1",
            ("$src", ""), ("$ser", ""));

        var all = new List<TapeAttempts>();
        foreach (var (source, series) in DistinctPairs(c, "tape_fetch", "ix_tape_fetch_series"))
        {
            newest.Parameters["$src"].Value = delivered.Parameters["$src"].Value = source;
            newest.Parameters["$ser"].Value = delivered.Parameters["$ser"].Value = series;

            long id;
            DateTimeOffset at;
            string? note;
            using (var r = newest.ExecuteReader())
            {
                r.Read();
                id = r.GetInt64(0);
                at = Sql.Time(r.GetString(1));
                note = r.IsDBNull(2) ? null : r.GetString(2);
            }

            all.Add(new TapeAttempts(source, series, id, at, note,
                delivered.ExecuteScalar() is string lastDelivered ? Sql.Time(lastDelivered) : null));
        }

        return all;
    }

    /// <summary>One (source, series) of attempts: its newest attempt's id, arrival and note, and the arrival of its newest that delivered.</summary>
    sealed record TapeAttempts(string Source, string Series, long NewestId, DateTimeOffset NewestAt, string? NewestNote,
        DateTimeOffset? LastDelivered);

    /// <summary>
    /// WHAT IS FAILING NOW, or null: of the series whose newest attempt failed, the newest such attempt. A series that
    /// failed once and has delivered since is not failing.
    /// </summary>
    static TapeAttempts? Failing(IReadOnlyList<TapeAttempts> attempts) =>
        attempts.Where(a => a.NewestNote is not null).MaxBy(a => a.NewestId);

    /// <summary>
    /// WHERE <paramref name="t"/> BEGINS IN ARRIVAL ORDER: the first id of <paramref name="table"/> whose row arrived at or
    /// after <paramref name="t"/>, by binary search over the ids — a few dozen seeks at any size, where a scan for an
    /// unindexed arrival instant would read the whole table.
    ///
    /// <para>The ids are in arrival order only to within <see cref="ArrivalSlack"/>, so a caller asks for
    /// <c>t - slack</c> to get an id before which every row surely arrived before <c>t</c>, and for <c>t + slack</c> to
    /// get one from which every row surely arrived at or after it — and checks the rows between exactly.</para>
    /// </summary>
    static long FirstAtOrAfter(SqliteConnection c, string table, DateTimeOffset t)
    {
        long lo, hi;
        using (var bounds = Cmd(c, $"SELECT MIN(id), MAX(id) FROM {table}"))
        using (var r = bounds.ExecuteReader())
        {
            r.Read();
            if (r.IsDBNull(0)) return 1;
            lo = r.GetInt64(0);
            hi = r.GetInt64(1) + 1;
        }

        var target = Sql.T(t);
        using var probe = Cmd(c, $"SELECT id, received_at FROM {table} WHERE id >= $mid ORDER BY id LIMIT 1", ("$mid", 0L));
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            probe.Parameters["$mid"].Value = mid;
            using var r = probe.ExecuteReader();
            if (!r.Read()) { hi = mid; continue; }
            if (string.CompareOrdinal(r.GetString(1), target) >= 0) hi = mid;
            else lo = r.GetInt64(0) + 1;
        }

        return lo;
    }

    /// <summary>The distinct (source, series) of an index's first two columns, one seek each — <see cref="Distinct"/> for a pair.</summary>
    static List<(string Source, string Series)> DistinctPairs(SqliteConnection c, string table, string index)
    {
        var pairs = new List<(string, string)>();
        (string Source, string Series) after = ("", "");
        while (true)
        {
            using var cmd = Cmd(c,
                $"SELECT source, series FROM {table} INDEXED BY {index} WHERE (source, series) > ($src, $ser) ORDER BY source, series LIMIT 1",
                ("$src", after.Source), ("$ser", after.Series));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return pairs;
            after = (r.GetString(0), r.GetString(1));
            pairs.Add(after);
        }
    }

    // ------------------------------------------------------------------------------ plumbing

    /// <summary>
    /// THE DISTINCT VALUES OF AN INDEX'S NEXT COLUMN, one seek each: <paramref name="sql"/> answers the first value after
    /// <c>$after</c>, and this asks again from it until there is none. A loose index scan, because SQLite has none of its
    /// own without statistics, and a <c>DISTINCT</c> would read every entry of a series that grows by thousands a day.
    /// </summary>
    static IEnumerable<string> Distinct(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var after = "";
        while (true)
        {
            using var cmd = Cmd(c, sql, [.. ps, ("$after", after)]);
            if (cmd.ExecuteScalar() is not string next) yield break;
            yield return next;
            after = next;
        }
    }

    /// <summary>The layout version the file names, or null for a file that names none.</summary>
    static int? LayoutVersion(SqliteConnection c)
    {
        using (var exists = Cmd(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tape_meta'"))
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return null;

        using var cmd = Cmd(c, "SELECT value FROM tape_meta WHERE key='schema'");
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) is { } text
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>A connection that cannot write: opened read-only, and <c>query_only</c> on top of it.</summary>
    SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = File,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());

        try
        {
            c.Open();
            using var pragmas = Cmd(c, "PRAGMA busy_timeout=5000; PRAGMA query_only=1;");
            pragmas.ExecuteNonQuery();
            return c;
        }
        catch (Exception)
        {
            c.Dispose();
            throw;
        }
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }
}
