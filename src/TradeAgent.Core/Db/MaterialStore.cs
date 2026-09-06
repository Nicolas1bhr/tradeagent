using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// The material ledger: what the account owner handed the agent, what the agent produced, and what
/// anybody claims was done with either.
///
/// The split between <c>material</c> and <c>material_note</c> is the whole design. <c>material</c>
/// rows come from a directory listing and a hash — the agent cannot write one and cannot edit one.
/// <c>material_note</c> rows are the agent's own account of itself. Keeping them apart is what makes
/// the record worth reading in three weeks: the observations stand whether or not the agent was
/// diligent, honest, or even still running.
///
/// Nothing is ever deleted. A file that disappears gets <c>removed_at</c>; a file that is replaced
/// leaves its old row behind and gains a new one. A ledger that forgets is a ledger you cannot use
/// to answer "where did this come from".
/// </summary>
public sealed class MaterialStore(Database db)
{
    const string Cols = """
        id, rel_path, origin, sha256, size_bytes, modified_at, first_seen_at, last_seen_at, removed_at, runnable
        """;

    /// <summary>
    /// Record that a file was seen. Identity is (path, size, mtime) — the tuple a scan can read
    /// without opening the file — so a second sighting of an unchanged file only moves
    /// <c>last_seen_at</c>, and a changed file becomes a new row rather than overwriting the old one.
    ///
    /// <b>A row stamped <c>removed_at</c> is never un-removed</b> (REVIEW 2026-09-05b finding 6).
    /// Only a LIVE row can be matched and touched. A sighting at a tuple this ledger has already
    /// watched disappear is a NEW VERSION and gets its own row, with no hash, so the next pass reads
    /// the bytes that are actually there. The old row keeps its <c>removed_at</c> and its own
    /// measurement, which is the whole point: the ledger never states a hash for bytes it did not
    /// hash, and it never quietly re-attaches an old one to new content of a convenient length.
    ///
    /// The same file genuinely coming back — restored from a backup, byte for byte — also gets a new
    /// row, and the next pass hashes it to the same value. That is not a loss. The ledger saw it go
    /// and saw something arrive; saying so, and re-measuring, is the honest record of both events.
    /// </summary>
    public (bool Added, long Id) Observe(string relPath, MaterialOrigin origin, long size,
        DateTimeOffset modifiedAt, bool runnable, DateTimeOffset now)
    {
        return db.Write(_ =>
        {
            using var find = db.Cmd(
                "SELECT id FROM material WHERE rel_path=$p AND size_bytes=$s AND modified_at=$m AND removed_at IS NULL",
                ("$p", relPath), ("$s", size), ("$m", Sql.T(modifiedAt)));
            var existing = find.ExecuteScalar();

            if (existing is not null and not DBNull)
            {
                // Seen before, still here, unchanged. Nothing to do but move last_seen_at.
                var known = Convert.ToInt64(existing);
                using var upd = db.Cmd("UPDATE material SET last_seen_at=$now WHERE id=$i",
                    ("$now", Sql.T(now)), ("$i", known));
                upd.ExecuteNonQuery();
                return (false, known);
            }

            // Nothing live at this tuple. If the ledger has removed rows for it, this is the next
            // version of that tuple; the unique index carries the version so both rows can stand.
            using var prior = db.Cmd(
                "SELECT COALESCE(MAX(version), -1) FROM material WHERE rel_path=$p AND size_bytes=$s AND modified_at=$m",
                ("$p", relPath), ("$s", size), ("$m", Sql.T(modifiedAt)));
            var version = Convert.ToInt64(prior.ExecuteScalar()) + 1;

            using var ins = db.Cmd("""
                INSERT INTO material(rel_path, origin, sha256, size_bytes, modified_at, first_seen_at, last_seen_at, removed_at, runnable, version)
                VALUES($p,$o,NULL,$s,$m,$now,$now,NULL,$r,$v) RETURNING id
                """,
                ("$p", relPath), ("$o", origin.ToString()), ("$s", size), ("$m", Sql.T(modifiedAt)),
                ("$now", Sql.T(now)), ("$r", runnable ? 1 : 0), ("$v", version));
            return (true, Convert.ToInt64(ins.ExecuteScalar()));
        });
    }

    public void SetHash(long id, string sha256) => db.Write(_ =>
    {
        using var c = db.Cmd("UPDATE material SET sha256=$h WHERE id=$i AND sha256 IS NULL", ("$h", sha256), ("$i", id));
        return c.ExecuteNonQuery();
    });

    /// <summary>
    /// Rows in these origins that a scan did not find, stamped gone. Never deleted.
    ///
    /// A COLLECTION of origins, because one walked directory can produce more than one word for the
    /// same place: the inbox yields <see cref="MaterialOrigin.Inbox"/> or
    /// <see cref="MaterialOrigin.InboxUnattested"/> depending on what the pass could attest, and a
    /// sweep that named only the first would stamp every unattested row gone on the strength of a
    /// walk that had just seen the file.
    /// </summary>
    public int MarkMissing(IReadOnlyCollection<MaterialOrigin> origins, IReadOnlyCollection<long> stillPresent, DateTimeOffset now)
    {
        if (origins.Count == 0) return 0;
        var keep = stillPresent.Count == 0 ? "" : $" AND id NOT IN ({string.Join(',', stillPresent)})";
        // The enum's own names, never anything a caller typed, so this cannot be a hole.
        var names = string.Join(',', origins.Select(o => $"'{o}'"));
        return db.Write(_ =>
        {
            using var c = db.Cmd(
                $"UPDATE material SET removed_at=$now WHERE origin IN ({names}) AND removed_at IS NULL{keep}",
                ("$now", Sql.T(now)));
            return c.ExecuteNonQuery();
        });
    }

    /// <summary>Everything on disk right now, newest sighting first.</summary>
    public IReadOnlyList<Material> Present(MaterialOrigin? origin = null) => db.Read(_ =>
    {
        var where = origin is null ? "" : " AND origin=$o";
        using var c = db.Cmd($"SELECT {Cols} FROM material WHERE removed_at IS NULL{where} ORDER BY first_seen_at DESC, id DESC",
            ("$o", origin?.ToString()));
        return Read(c);
    });

    /// <summary>Rows still awaiting a hash, oldest first, so a big drop is worked through in order.</summary>
    public IReadOnlyList<Material> NeedingHash(int limit) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM material WHERE sha256 IS NULL AND removed_at IS NULL ORDER BY id LIMIT $n",
            ("$n", limit));
        return Read(c);
    });

    /// <summary>Every version ever seen at one path, including the ones that are gone.</summary>
    public IReadOnlyList<Material> History(string relPath) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM material WHERE rel_path=$p ORDER BY first_seen_at DESC, id DESC", ("$p", relPath));
        return Read(c);
    });

    /// <summary>The shortest prefix this will resolve. See <see cref="ByShaPrefix"/>.</summary>
    public const int MinShaPrefix = 4;

    /// <summary>
    /// The one file whose hash starts with <paramref name="prefix"/>, or null if there is none.
    ///
    /// <b>This is the agent's only way to name a row</b>, and every <c>material_note</c> it writes is
    /// attached through it — so a prefix that resolves to the WRONG row is the agent authoring a
    /// claim about a file it never touched. Two ways that used to be possible (Codex F17):
    ///
    /// The prefix went into <c>LIKE</c> unescaped, where <c>_</c> matches any single character and
    /// <c>%</c> matches anything at all: <c>"_"</c> matched every hashed row and <c>ORDER BY id DESC
    /// LIMIT 1</c> silently picked one. And a prefix short enough to match several DIFFERENT files
    /// was answered the same silent way, rather than refused.
    ///
    /// So: hexadecimal only — which is also what makes the <c>LIKE</c> pattern below wildcard-free,
    /// rather than escaping characters that now cannot arrive; at least
    /// <see cref="MinShaPrefix"/> characters, because a one- or two-character prefix is a coin toss
    /// that happens to be unique today; and a refusal, in words, when it matches more than one
    /// distinct hash. Several ROWS may share one hash — the same bytes at two paths, or a version
    /// that came back identical — and that is not ambiguous: they are the same file, and the newest
    /// row is the answer.
    /// </summary>
    /// <exception cref="TradeAgentException">
    /// INVALID_REQUEST when the prefix is not hex, is too short, or names more than one file.
    /// Refusing is the point: answering with an arbitrary one of them is the defect.
    /// </exception>
    public Material? ByShaPrefix(string prefix)
    {
        var p = (prefix ?? "").Trim().ToLowerInvariant();
        if (p.Length < MinShaPrefix)
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                $"a file's sha has to be given as at least {MinShaPrefix} characters — 'trade material list' prints the first 12");
        if (!p.All(char.IsAsciiHexDigit))
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                $"'{prefix}' is not a hash: a sha is hexadecimal, and only the characters 0-9 and a-f can appear in one");

        return db.Read(_ =>
        {
            // Two, not one: the second row is the whole question. Distinct, because rows sharing a
            // hash are one file and the ambiguity that matters is between different files.
            using var which = db.Cmd(
                "SELECT DISTINCT sha256 FROM material WHERE sha256 LIKE $p ORDER BY sha256 LIMIT 2", ("$p", p + "%"));
            var hashes = new List<string>();
            using (var rd = which.ExecuteReader())
                while (rd.Read()) hashes.Add(rd.GetString(0));

            if (hashes.Count == 0) return null;
            if (hashes.Count > 1)
                throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                    $"'{p}' is the start of more than one file's hash, so TradeAgent will not guess which you mean — " +
                    "give more characters of it");

            using var c = db.Cmd($"SELECT {Cols} FROM material WHERE sha256=$h ORDER BY id DESC LIMIT 1", ("$h", hashes[0]));
            return Read(c).FirstOrDefault();
        });
    }

    public long AddNote(string author, string? session, MaterialNoteKind kind, string? subjectSha,
        string? parentSha, string text, DateTimeOffset now) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            INSERT INTO material_note(at, author, session, kind, subject_sha, parent_sha, text)
            VALUES($at,$a,$s,$k,$sub,$par,$t) RETURNING id
            """,
            ("$at", Sql.T(now)), ("$a", author), ("$s", session), ("$k", kind.ToString()),
            ("$sub", subjectSha), ("$par", parentSha), ("$t", text));
        return Convert.ToInt64(c.ExecuteScalar());
    });

    public IReadOnlyList<MaterialNote> NotesFor(string sha256) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT id, at, author, session, kind, subject_sha, parent_sha, text FROM material_note
            WHERE subject_sha=$s OR parent_sha=$s ORDER BY id DESC
            """, ("$s", sha256));
        return ReadNotes(c);
    });

    public IReadOnlyList<MaterialNote> RecentNotes(int limit) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT id, at, author, session, kind, subject_sha, parent_sha, text FROM material_note
            ORDER BY id DESC LIMIT $n
            """, ("$n", limit));
        return ReadNotes(c);
    });

    static List<Material> Read(SqliteCommand c)
    {
        using var rd = c.ExecuteReader();
        var list = new List<Material>();
        while (rd.Read())
            list.Add(new Material(
                rd.GetInt64(0), rd.GetString(1),
                Enum.Parse<MaterialOrigin>(rd.GetString(2)),
                Sql.S(rd.GetValue(3)), rd.GetInt64(4),
                Sql.Time(rd.GetValue(5)), Sql.Time(rd.GetValue(6)), Sql.Time(rd.GetValue(7)),
                Sql.TimeN(rd.GetValue(8)), rd.GetInt64(9) != 0));
        return list;
    }

    static List<MaterialNote> ReadNotes(SqliteCommand c)
    {
        using var rd = c.ExecuteReader();
        var list = new List<MaterialNote>();
        while (rd.Read())
            list.Add(new MaterialNote(
                rd.GetInt64(0), Sql.Time(rd.GetValue(1)), rd.GetString(2), Sql.S(rd.GetValue(3)),
                Enum.Parse<MaterialNoteKind>(rd.GetString(4)),
                Sql.S(rd.GetValue(5)), Sql.S(rd.GetValue(6)), rd.GetString(7)));
        return list;
    }
}
