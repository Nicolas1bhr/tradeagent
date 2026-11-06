using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;

namespace TradeAgent.Core;

/// <summary>
/// A HOME WRITTEN BY A PUBLISHED RELEASE THIS BUILD DOES NOT OPEN (<c>U-windows-release</c>; CLAUDE.md "No backward compatibility
/// while we build"). Thrown by <c>Database</c>'s open after the read-only kind check and BEFORE anything is written to the file — not
/// even the journal mode — so the refused home is left exactly as it was found.
///
/// <para><b>Refuse, never guess; never destroy without the owner's choice.</b> The words name both versions and the repair, and the
/// repair is the owner's press in the app: <see cref="SetAside"/> moves the WHOLE home aside by one rename, kept beside the new one,
/// and nothing of his is deleted, rewritten or migrated. No terminal anywhere in it.</para>
/// </summary>
public sealed class OlderHomeException : Exception
{
    /// <summary>A home a published release wrote, stamped below <see cref="Versions.OldestHomeSchema"/>.</summary>
    public OlderHomeException(string home, int homeSchema, string thisVersion) : base(OlderHome.Words(home, homeSchema, thisVersion))
    {
        Home = home;
        HomeSchema = homeSchema;
        ThisVersion = thisVersion;
    }

    /// <summary>
    /// A home whose board was FOUNDED IN AN OLDER POLICY SHAPE (<c>U-founding-carry</c>): its schema this build climbs, its founding
    /// policy a body this build does not read — refused, untouched, in words naming both shapes.
    /// </summary>
    public OlderHomeException(string home, OlderPolicyShape policy, string thisVersion)
        : base(OlderHome.PolicyWords(home, policy, thisVersion))
    {
        Home = home;
        HomeSchema = policy.HomeSchema;
        ThisVersion = thisVersion;
        Policy = policy;
    }

    /// <summary>The home directory refused (the folder that holds <c>state\tradeagent.db</c>).</summary>
    public string Home { get; }

    /// <summary>The schema its database was stamped at.</summary>
    public int HomeSchema { get; }

    /// <summary>The version of TradeAgent refusing it.</summary>
    public string ThisVersion { get; }

    /// <summary>The older founding policy shape that is why, or null when the schema is.</summary>
    public OlderPolicyShape? Policy { get; }

    /// <summary>The release that wrote it, as the words name it.</summary>
    public string WrittenBy => Policy is not null
        ? "an earlier build of this building phase"
        : Versions.ReleaseThatWrote(HomeSchema) ?? $"schema {HomeSchema}";
}

/// <summary>
/// A HOME'S BOARD FOUNDED IN AN OLDER POLICY SHAPE (<c>U-founding-carry</c>), as read without writing anything: the schema it is
/// stamped at, the field paths only its founding body names (<see cref="Theirs"/> — <c>paper_envelope</c>, one envelope for one pair)
/// and the ones only this build's names (<see cref="Ours"/> — <c>paper_envelopes</c>, one per pair), and how many policy versions
/// after the founding one and how many board acts were recorded on it. Only field NAMES are compared; no value of the older body is
/// read, so nothing about it is guessed.
/// </summary>
public sealed record OlderPolicyShape(int HomeSchema, IReadOnlyList<string> Theirs, IReadOnlyList<string> Ours, int LaterVersions, int Acts)
{
    /// <summary>Whether the board decided anything on the older policy: a version after the founding one, or an act.</summary>
    public bool HasDecisions => LaterVersions > 0 || Acts > 0;
}

/// <summary>The refusal's words and the owner's start-fresh press.</summary>
public static class OlderHome
{
    /// <summary>What the owner reads on the refusal screen: both versions, that nothing was changed, and the repair.</summary>
    public static string Words(string home, int homeSchema, string thisVersion) =>
        $"This is TradeAgent {thisVersion}. The folder it keeps your records in ({home}) was written by TradeAgent " +
        $"{Versions.ReleaseThatWrote(homeSchema) ?? $"schema {homeSchema}"} (its records are at schema {homeSchema}; this version opens " +
        $"schema {Versions.OldestHomeSchema} and later), and this version does not open it: it would have to guess what the old records " +
        "mean, and it does not guess. None of your records or files in that folder has been changed. You can start fresh: the old folder is kept, whole, " +
        "beside the new one, and nothing in it is deleted.";

    /// <summary>
    /// What the owner reads when the board was founded in an older policy shape (<c>U-founding-carry</c>): both shapes in words, that
    /// nothing was changed, the board's decisions on the older policy when there are any, and the repair — with what it carries.
    /// </summary>
    public static string PolicyWords(string home, OlderPolicyShape policy, string thisVersion)
    {
        var words =
            $"This is TradeAgent {thisVersion}. The folder it keeps your records in ({home}) was set up by an earlier build of TradeAgent, " +
            $"and its board's founding policy is written in an older shape: it states {Fields(policy.Theirs)} — one paper envelope for one " +
            $"pair — where this version states {Fields(policy.Ours)} — one paper envelope for each pair under evidence. This version does not " +
            "open it: it would have to guess what the older policy means, and it does not guess. None of your records or files in that " +
            "folder has been changed.";
        if (policy.HasDecisions)
            words += $" The board also decided on that older policy ({Count(policy.LaterVersions, "later policy version")}, " +
                     $"{Count(policy.Acts, "board act")}); those decisions were made on terms this version does not read, so they are not " +
                     "carried either — they stay, whole, in the old folder.";
        return words + " You can start fresh: the old folder is kept, whole, beside the new one, and nothing in it is deleted. The " +
               "market-context tape (state/tape.db) is copied into the new folder, byte for byte, when this version reads its layout, so " +
               "what it observed stays as it was recorded.";

        static string Fields(IReadOnlyList<string> paths) => string.Join(" and ", paths.Select(p => $"'{p}'"));
        static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
    }

    /// <summary>
    /// The older founding policy shape of <paramref name="home"/>'s <c>state\tradeagent.db</c>, or null when it is not one (no
    /// database, no board yet, a founding body this build reads, or one that does not verify — corrupt is refused as unreadable by
    /// the board's own reader, never excused as older). Read-only: opened <c>Mode=ReadOnly</c>, nothing written.
    /// </summary>
    public static OlderPolicyShape? RefusedPolicy(string home)
    {
        var file = Path.Combine(home, "state", "tradeagent.db");
        if (!File.Exists(file)) return null;
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        conn.Open();
        return RefusedPolicy(conn);
    }

    /// <summary>
    /// THE RULE ITSELF, ONE PLACE, on an open connection that has written nothing (<see cref="Database"/>'s open asks it before the
    /// journal mode): the FOUNDING version — version 1, decided by the app on the founding basis — read with its stored hash, and
    /// <see cref="BoardPolicyBody.OlderShape"/> asked whether it verifies as an older shape. Writes nothing.
    /// </summary>
    internal static OlderPolicyShape? RefusedPolicy(SqliteConnection conn)
    {
        if (!TableExists(conn, "board_policy")) return null;
        string body, sha;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT body, body_sha256 FROM board_policy WHERE version=1 AND decider='app' AND basis='founding'";
            using var r = c.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0) || r.IsDBNull(1)) return null;
            body = r.GetString(0);
            sha = r.GetString(1);
        }
        if (BoardPolicyBody.OlderShape(body, sha) is not { } shape) return null;
        return new OlderPolicyShape(Int(conn, "SELECT value FROM meta WHERE key='schema_version'", "meta"), shape.Theirs, shape.Ours,
            Int(conn, "SELECT COUNT(*) FROM board_policy WHERE version > 1", "board_policy"),
            Int(conn, "SELECT COUNT(*) FROM board_act", "board_act"));
    }

    static bool TableExists(SqliteConnection conn, string table)
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t";
        c.Parameters.AddWithValue("$t", table);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    static int Int(SqliteConnection conn, string sql, string table)
    {
        if (!TableExists(conn, table)) return 0;
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        return int.TryParse(Convert.ToString(c.ExecuteScalar(), CultureInfo.InvariantCulture), NumberStyles.None,
            CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>What the second press says it will do, in full.</summary>
    public const string ConfirmStartFresh =
        "Yes — keep the old folder aside and start fresh (you will set TradeAgent up again and sign in to the AI again)";

    /// <summary>What the second press says for a home refused for its older founding policy (<c>U-founding-carry</c>): the tape is carried.</summary>
    public const string ConfirmStartFreshCarryingTape =
        "Yes — keep the old folder aside and start fresh, carrying the market-context tape (you will set TradeAgent up again and sign in to the AI again)";

    /// <summary>
    /// The schema of the database in <paramref name="home"/>'s <c>state\tradeagent.db</c> when it is a home this build refuses, else
    /// null. Read-only: opened <c>Mode=ReadOnly</c>, nothing written. A missing database, no stamp, or a stamp at or above
    /// <see cref="Versions.OldestHomeSchema"/> is null — not an older home.
    /// </summary>
    public static int? RefusedSchema(string home)
    {
        var file = Path.Combine(home, "state", "tradeagent.db");
        if (!File.Exists(file)) return null;
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
        object? o;
        try { o = c.ExecuteScalar(); }
        catch (SqliteException) { return null; }
        return o is not null && int.TryParse(o.ToString(), out var s) ? Refused(s) : null;
    }

    /// <summary>The refusal rule itself, one place: a stamped schema below the floor. 0 (no stamp) is a new file, never refused.</summary>
    public static int? Refused(int schema) => schema is > 0 and < Versions.OldestHomeSchema ? schema : null;

    /// <summary>
    /// THE OWNER'S START-FRESH PRESS: moves <paramref name="home"/> aside WHOLE, by one rename, to a sibling named for the release that
    /// wrote it and the moment — <c>TradeAgent-from-0.1.2-20261031-101500</c>, or <c>TradeAgent-from-older-policy-…</c> for a home whose
    /// board was founded in an older policy shape — and makes an empty <paramref name="home"/> in its place. Returns the folder the old
    /// home now lives in.
    ///
    /// <para>Refuses, moving nothing, unless the home is STILL one this build refuses (read again here, read-only): this press can
    /// never move a home the current build opens. A rename Windows will not do — a file in it held open by another program — is said
    /// in words and leaves the home where it was; nothing is copied, so nothing is half-moved.</para>
    /// </summary>
    public static string SetAside(string home, DateTimeOffset at) => SetAside(home, at, out _);

    /// <summary>
    /// <see cref="SetAside(string, DateTimeOffset)"/>, and for a home refused for its older founding policy the market-context tape
    /// CARRIED into the new home (<see cref="CarryTape"/>): <paramref name="tape"/> says in words what happened to it, or is null when
    /// the home was refused for its schema (a published release's home, which predates the tape) or held no tape.
    /// </summary>
    public static string SetAside(string home, DateTimeOffset at, out string? tape)
    {
        tape = null;
        var full = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string label;
        var carry = false;
        if (RefusedSchema(full) is { } schema)
            label = (Versions.ReleaseThatWrote(schema) ?? $"schema-{schema}").Replace(" or ", "-or-");
        else if (RefusedPolicy(full) is not null)
        {
            label = "older-policy";
            carry = true;
        }
        else
            throw new InvalidOperationException($"The folder {full} is not a home from an older TradeAgent, so starting fresh did not move it.");

        SqliteConnection.ClearAllPools();
        var aside = $"{full}-from-{label}-{at.UtcDateTime:yyyyMMdd-HHmmss}";
        if (Directory.Exists(aside) || File.Exists(aside))
            throw new InvalidOperationException($"{aside} already exists, so the older home was not moved. Press Start fresh again in a second.");
        try
        {
            Directory.Move(full, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Windows would not move {full} aside — a program still has a file in it open ({ex.Message}). Nothing was moved or " +
                "deleted; close that program and press Start fresh again.", ex);
        }
        Directory.CreateDirectory(full);
        if (carry) tape = CarryTape(aside, full);
        return aside;
    }

    /// <summary>
    /// THE TAPE, CARRIED (<c>U-founding-carry</c>): the market-context tape is a point-in-time record nobody can backfill, and it holds
    /// nothing of the board's policy — what a vendor published, when it arrived, and which revision it is. So a start-fresh over a home
    /// refused for its older founding policy COPIES <c>state/tape.db</c> (and its <c>-wal</c>, which holds committed rows not yet
    /// checkpointed) from the home set aside into the new one, and the set-aside home keeps its own, untouched.
    ///
    /// <para><b>Only a tape this build reads as it is.</b> The copy is made beside its final name, a plain copy of the bytes, and its own
    /// ladder version read from the copy, read-only: only a tape at
    /// <see cref="TapeStore.Schema"/>, marked a real home's (or unmarked, which every real tape before the mark is), takes the tape's
    /// name in the new home. Anything else — another layout version, a proving ground's tape, a file that cannot be copied or read — is not carried: the copy is removed (it was ours; the original stays whole in the old folder) and the words say why.
    /// A decision or lens row naming an attempt names one recorded in the set-aside home; those ids carry a GUID, so nothing the new
    /// home records can ever take one of them.</para>
    /// </summary>
    internal static string CarryTape(string aside, string home)
    {
        var from = Path.Combine(aside, "state", "tape.db");
        if (!File.Exists(from)) return "The old folder held no market-context tape, so there was none to carry.";
        var state = Path.Combine(home, "state");
        Directory.CreateDirectory(state);
        var to = Path.Combine(state, "tape.db");
        var staged = to + ".carrying";
        var pairs = new List<(string From, string Staged, string To)> { (from, staged, to) };
        if (File.Exists(from + "-wal")) pairs.Add((from + "-wal", staged + "-wal", to + "-wal"));

        string? why;
        try
        {
            foreach (var (f, st, _) in pairs) File.Copy(f, st, overwrite: false);
            why = TapeRefusal(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            why = $"it could not be copied or read ({ex.Message})";
        }

        if (why is null)
        {
            // THE -WAL FIRST, THEN THE TAPE: a tape that takes its name stands with every row it committed.
            foreach (var (_, st, t) in Enumerable.Reverse(pairs)) File.Move(st, t);
            Remove(staged + "-shm");
            return $"The market-context tape was copied into the new folder byte for byte (layout version {TapeStore.Schema}); the old " +
                   "folder keeps its own.";
        }
        foreach (var (_, st, _) in pairs) Remove(st);
        Remove(staged + "-shm");
        return $"The market-context tape was not carried into the new folder: {why}. It is kept, whole, in {aside}.";

        static void Remove(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Why a staged copy of a tape is not one this build carries, or null. Read-only; the connection let go before return.</summary>
    static string? TapeRefusal(string file)
    {
        try
        {
            // IMMUTABLE WHEN CLOSED (HomeMark.Read's way): a read-only open of a WAL file with no -wal makes one beside it.
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = File.Exists(file + "-wal") ? file : Proving.HomeMark.ImmutableUri(file), Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            c.Open();
            if (!TableExists(c, "tape_meta")) return "it holds no tape layout";
            string? version;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM tape_meta WHERE key='schema'";
                version = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            if (version != TapeStore.Schema.ToString(CultureInfo.InvariantCulture))
                return $"its layout is version {(string.IsNullOrEmpty(version) ? "none" : version)} and this version reads {TapeStore.Schema} as it is";
            var word = Proving.HomeMark.Read(c, "tape_meta");
            if (Proving.HomeMark.Refusal("the market-context tape", file, word, Proving.HomeKind.Real) is not null)
                return $"it is marked '{word}', not a real home's";
            return null;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }
}
