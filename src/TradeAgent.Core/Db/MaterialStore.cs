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
    /// </summary>
    public (bool Added, long Id) Observe(string relPath, MaterialOrigin origin, long size,
        DateTimeOffset modifiedAt, bool runnable, DateTimeOffset now)
    {
        return db.Write(_ =>
        {
            using var find = db.Cmd(
                "SELECT id FROM material WHERE rel_path=$p AND size_bytes=$s AND modified_at=$m",
                ("$p", relPath), ("$s", size), ("$m", Sql.T(modifiedAt)));
            var existing = find.ExecuteScalar();

            if (existing is not null and not DBNull)
            {
                // Seen before and unchanged. Move last_seen_at, and un-remove it if it had gone away
                // and come back — the same bytes at the same path really is the same thing returning.
                var known = Convert.ToInt64(existing);
                using var upd = db.Cmd("UPDATE material SET last_seen_at=$now, removed_at=NULL WHERE id=$i",
                    ("$now", Sql.T(now)), ("$i", known));
                upd.ExecuteNonQuery();
                return (false, known);
            }

            using var ins = db.Cmd("""
                INSERT INTO material(rel_path, origin, sha256, size_bytes, modified_at, first_seen_at, last_seen_at, removed_at, runnable)
                VALUES($p,$o,NULL,$s,$m,$now,$now,NULL,$r) RETURNING id
                """,
                ("$p", relPath), ("$o", origin.ToString()), ("$s", size), ("$m", Sql.T(modifiedAt)),
                ("$now", Sql.T(now)), ("$r", runnable ? 1 : 0));
            return (true, Convert.ToInt64(ins.ExecuteScalar()));
        });
    }

    public void SetHash(long id, string sha256) => db.Write(_ =>
    {
        using var c = db.Cmd("UPDATE material SET sha256=$h WHERE id=$i AND sha256 IS NULL", ("$h", sha256), ("$i", id));
        return c.ExecuteNonQuery();
    });

    /// <summary>Rows in this origin that a scan did not find, stamped gone. Never deleted.</summary>
    public int MarkMissing(MaterialOrigin origin, IReadOnlyCollection<long> stillPresent, DateTimeOffset now)
    {
        var keep = stillPresent.Count == 0 ? "" : $" AND id NOT IN ({string.Join(',', stillPresent)})";
        return db.Write(_ =>
        {
            using var c = db.Cmd(
                $"UPDATE material SET removed_at=$now WHERE origin=$o AND removed_at IS NULL{keep}",
                ("$now", Sql.T(now)), ("$o", origin.ToString()));
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

    public Material? ByShaPrefix(string prefix) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM material WHERE sha256 LIKE $p ORDER BY id DESC LIMIT 1", ("$p", prefix + "%"));
        return Read(c).FirstOrDefault();
    });

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
