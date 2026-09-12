using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>Whether this dataset may still be read as what its row says it is.</summary>
public enum DatasetState
{
    /// <summary>Every raw file and the normalised file still hash to what the ledger recorded.</summary>
    ACCEPTED,

    /// <summary>Something the ledger measured has changed on disk. The bars are not served.</summary>
    REJECTED
}

/// <summary>One raw archive file, as measured on the day it was fetched.</summary>
public sealed record DatasetFile(
    string Month,
    string Url,
    string PublishedSha256,
    string ComputedSha256,
    long Bytes,
    DateTimeOffset DownloadedAt,
    KlineTimeUnit Unit,
    string Path);

/// <summary>
/// One normalised dataset and its whole provenance. Every field is the app's own measurement.
/// </summary>
public sealed record DatasetRecord(
    long Id,
    string Source,
    string Pair,
    string Interval,
    string Version,
    int MonthsAttempted,
    int MonthsPresent,
    IReadOnlyList<string> MonthsNotPublished,
    string NormalisedPath,
    string NormalisedSha256,
    int Bars,
    DateTimeOffset? FirstBar,
    DateTimeOffset? LastBar,
    int Gaps,
    IReadOnlyList<GapRun> GapRuns,
    bool GapRunsTruncated,
    int Duplicates,
    int Incomplete,
    int Unreadable,
    DateTimeOffset AcceptedAt,
    DatasetState State,
    string? RejectedReason,
    IReadOnlyList<DatasetFile> Files);

/// <summary>
/// THE DATASET LEDGER — provenance the AI cannot edit.
///
/// <para><b>Written by the app, read by everyone.</b> The gateway serves these rows over
/// <c>data-list</c> and the bars over <c>data-bars</c>, and there is no op and no verb on the other
/// side. That is the same split <see cref="MaterialStore"/> makes between what TradeAgent OBSERVED
/// and what somebody CLAIMED, applied to the evidence a strategy is going to be judged on: an agent
/// that could rewrite the coverage of its own data could report a clean twelve months over three
/// with the holes filled in.</para>
///
/// <para><b>Reproducible, and checked rather than asserted.</b> A row carries the SHA-256 of every
/// raw archive file it was built from and of the normalised file it produced. <see cref="Checked"/>
/// re-reads those hashes off the disk on every read; a file that no longer matches moves the row to
/// <see cref="DatasetState.REJECTED"/>, permanently, and the bars stop being served. It is never
/// re-normalised from the changed bytes — a dataset derived from a file nobody can identify has no
/// provenance, whatever the derivation produces.</para>
/// </summary>
public sealed class DatasetStore(Database db)
{
    /// <summary>Everything but the row id, in the order <see cref="Record"/> binds it.</summary>
    const string Written = """
        source, pair, interval, version, months_attempted, months_present, months_not_published,
        normalised_path, normalised_sha256, bars, first_bar, last_bar, gaps, gap_runs,
        gap_runs_truncated, duplicates, incomplete, unreadable, accepted_at, state, rejected_reason
        """;

    /// <summary>The same list with the id in front, in the order <see cref="ReadAll"/> reads it.</summary>
    const string Cols = "id, " + Written;

    /// <summary>The next unused version name for this pair, e.g. <c>v3</c>.</summary>
    public string NextVersion(string pair, string interval) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM dataset WHERE pair=$p AND interval=$i",
            ("$p", pair), ("$i", interval));
        return "v" + (Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) + 1);
    });

    /// <summary>Writes one dataset and its raw files, in one transaction. Returns the new id.</summary>
    public long Record(DatasetRecord set) => db.Write(_ =>
    {
        using var insert = db.Cmd($"""
            INSERT INTO dataset({Written})
            VALUES($src,$pair,$int,$ver,$att,$pres,$notpub,$npath,$nsha,$bars,$first,$last,$gaps,
                   $runs,$trunc,$dup,$inc,$unread,$at,$state,$why);
            SELECT last_insert_rowid();
            """,
            ("$src", set.Source), ("$pair", set.Pair), ("$int", set.Interval), ("$ver", set.Version),
            ("$att", set.MonthsAttempted), ("$pres", set.MonthsPresent),
            ("$notpub", string.Join(',', set.MonthsNotPublished)),
            ("$npath", set.NormalisedPath), ("$nsha", set.NormalisedSha256), ("$bars", set.Bars),
            ("$first", set.FirstBar is { } f ? Sql.T(f) : null),
            ("$last", set.LastBar is { } l ? Sql.T(l) : null),
            ("$gaps", set.Gaps), ("$runs", EncodeRuns(set.GapRuns)),
            ("$trunc", set.GapRunsTruncated ? 1 : 0), ("$dup", set.Duplicates),
            ("$inc", set.Incomplete), ("$unread", set.Unreadable), ("$at", Sql.T(set.AcceptedAt)),
            ("$state", set.State.ToString()), ("$why", set.RejectedReason));

        var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);

        foreach (var raw in set.Files)
        {
            using var file = db.Cmd("""
                INSERT INTO dataset_file(dataset_id, month, url, published_sha256, computed_sha256,
                                         bytes, downloaded_at, unit, path)
                VALUES($id,$m,$u,$p,$c,$b,$d,$unit,$path)
                """,
                ("$id", id), ("$m", raw.Month), ("$u", raw.Url), ("$p", raw.PublishedSha256),
                ("$c", raw.ComputedSha256), ("$b", raw.Bytes), ("$d", Sql.T(raw.DownloadedAt)),
                ("$unit", raw.Unit.ToString()), ("$path", raw.Path));
            file.ExecuteNonQuery();
        }

        return id;
    });

    /// <summary>Every dataset this installation has, newest first. Not verified — see <see cref="Checked"/>.</summary>
    public IReadOnlyList<DatasetRecord> All() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM dataset ORDER BY id DESC");
        return ReadAll(c);
    });

    /// <summary>
    /// ONE DATASET BY ITS LEDGER ID, or null when this installation has no such row.
    ///
    /// <para><see cref="Newest"/> answers "the freshest BTCUSDT data", which is the right question
    /// for an agent asking to look at some bars and the wrong one for a run whose result is going to
    /// be attached to an id: a backtest run against "newest" is a result nobody can reproduce once a
    /// month is collected. Not verified — see <see cref="Checked"/>.</para>
    /// </summary>
    public DatasetRecord? ById(long id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM dataset WHERE id=$id", ("$id", id));
        return ReadAll(c).FirstOrDefault();
    });

    /// <summary>The newest dataset for a pair, or null when this installation has none.</summary>
    public DatasetRecord? Newest(string pair) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM dataset WHERE pair=$p ORDER BY id DESC LIMIT 1", ("$p", pair));
        return ReadAll(c).FirstOrDefault();
    });

    /// <summary>
    /// THE DATASET AS IT IS NOW, not as it was recorded.
    ///
    /// Every raw file and the normalised file are re-hashed and compared with the row. A single
    /// disagreement moves the row to <see cref="DatasetState.REJECTED"/> — written down, so the next
    /// read need not re-measure to reach the same verdict — and the returned record says so. A row
    /// already rejected stays rejected: an altered file that is put back is not the same evidence
    /// having been restored, it is a file somebody changed twice.
    /// </summary>
    public DatasetRecord Checked(DatasetRecord set)
    {
        if (set.State == DatasetState.REJECTED) return set;

        var reason = FirstMismatch(set);
        if (reason is null) return set;

        Reject(set.Id, reason);
        return set with { State = DatasetState.REJECTED, RejectedReason = reason };
    }

    /// <summary>
    /// What no longer hashes to what the ledger recorded, in words, or null when everything does.
    ///
    /// THE RAW FILES ARE CHECKED, not only the normalised one. The normalised file is what the bars
    /// are served from and the raw files are what makes it REPRODUCIBLE — checking only the output
    /// would leave a dataset whose inputs nobody can identify still reading as accepted, which is
    /// precisely the claim this ledger exists to be able to make.
    /// </summary>
    public static string? FirstMismatch(DatasetRecord set)
    {
        foreach (var f in set.Files)
        {
            var now = Sha256(f.Path);
            if (now is null)
                return $"the raw archive file for {f.Month} is no longer on disk at {f.Path}";
            if (!string.Equals(now, f.ComputedSha256, StringComparison.OrdinalIgnoreCase))
                return $"the raw archive file for {f.Month} no longer matches the hash recorded for it " +
                       $"({f.ComputedSha256} became {now})";
        }

        var normalised = Sha256(set.NormalisedPath);
        if (normalised is null)
            return $"the normalised dataset file is no longer on disk at {set.NormalisedPath}";
        if (!string.Equals(normalised, set.NormalisedSha256, StringComparison.OrdinalIgnoreCase))
            return $"the normalised dataset file no longer matches the hash recorded for it " +
                   $"({set.NormalisedSha256} became {normalised})";

        return null;
    }

    /// <summary>Marks a dataset rejected. There is no route back: see <see cref="Checked"/>.</summary>
    public void Reject(long id, string reason) => db.Write(_ =>
    {
        using var c = db.Cmd("UPDATE dataset SET state='REJECTED', rejected_reason=$w WHERE id=$id",
            ("$w", reason), ("$id", id));
        return c.ExecuteNonQuery();
    });

    public static string? Sha256(string file) => Sha256Hex.OfFile(file);

    List<DatasetRecord> ReadAll(SqliteCommand c)
    {
        var rows = new List<DatasetRecord>();
        using (var r = c.ExecuteReader())
            while (r.Read())
                rows.Add(new DatasetRecord(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetInt32(5), r.GetInt32(6),
                    r.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries),
                    r.GetString(8), r.GetString(9), r.GetInt32(10),
                    Sql.TimeN(r.IsDBNull(11) ? null : r.GetString(11)),
                    Sql.TimeN(r.IsDBNull(12) ? null : r.GetString(12)),
                    r.GetInt32(13), DecodeRuns(r.GetString(14)), r.GetInt32(15) != 0,
                    r.GetInt32(16), r.GetInt32(17), r.GetInt32(18), Sql.Time(r.GetString(19)),
                    Enum.TryParse<DatasetState>(r.GetString(20), out var s) ? s : DatasetState.REJECTED,
                    r.IsDBNull(21) ? null : r.GetString(21),
                    []));

        return [.. rows.Select(row => row with { Files = FilesOf(row.Id) })];
    }

    IReadOnlyList<DatasetFile> FilesOf(long id) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT month, url, published_sha256, computed_sha256, bytes, downloaded_at, unit, path
            FROM dataset_file WHERE dataset_id=$id ORDER BY month
            """, ("$id", id));

        var files = new List<DatasetFile>();
        using var r = c.ExecuteReader();
        while (r.Read())
            files.Add(new DatasetFile(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt64(4), Sql.Time(r.GetString(5)),
                Enum.TryParse<KlineTimeUnit>(r.GetString(6), out var u) ? u : KlineTimeUnit.Milliseconds,
                r.GetString(7)));
        return files;
    });

    /// <summary>Gap runs as one column: <c>from/to/minutes</c>, joined by <c>;</c>.</summary>
    static string EncodeRuns(IReadOnlyList<GapRun> runs) =>
        string.Join(';', runs.Select(g => $"{Sql.T(g.From)}/{Sql.T(g.To)}/{g.Minutes}"));

    static IReadOnlyList<GapRun> DecodeRuns(string text)
    {
        var runs = new List<GapRun>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split('/');
            if (f.Length == 3 && int.TryParse(f[2], CultureInfo.InvariantCulture, out var minutes))
                runs.Add(new GapRun(Sql.Time(f[0]), Sql.Time(f[1]), minutes));
        }
        return runs;
    }
}
