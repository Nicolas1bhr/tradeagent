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
/// </summary>
public sealed record TapeWindow(IReadOnlyList<TapeObservation> Rows, bool More, string? CappedBy);

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
    /// <para><b>The audience is required</b> and every row passes through <see cref="TapeStore.Served"/>: a quarantined
    /// item is served with its quarantine and without its payload, to every audience.</para>
    ///
    /// <para>Two phases in one snapshot: the ids, off the tape's own index on (source, series, subject, source time),
    /// and then each row by id — so a read never carries a payload it does not serve, and an "as of" in the past costs
    /// one short lookup for every row that arrived after it.</para>
    /// </summary>
    public TapeWindow Window(BarAudience audience, TapeQuery query, Func<TapeObservation, long>? cost = null)
    {
        ArgumentNullException.ThrowIfNull(audience);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaxRows)
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit,
                $"a read of the tape serves 1 to {MaxRows} rows, and {query.Limit} were asked for");
        if (query.MaxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(query), query.MaxBytes, "a read of the tape needs a byte budget above 0");

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
                o = TapeStore.Served(audience, TapeStore.Obs(one));
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
