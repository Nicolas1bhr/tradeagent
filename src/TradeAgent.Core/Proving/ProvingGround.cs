using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Proving;

/// <summary>
/// WHOSE HOME A STORE BELONGS TO (<c>U-proving-ground-a</c>; <c>docs/ORCHESTRATION.md</c> § 2.14, § 8 SIM-001): the
/// owner's real home, or a proving ground's shadow home, whose markets are synthetic by construction. Written into both
/// stores of a home when they are created (<see cref="HomeMark"/>) and compared on every open, so a proving ground's
/// records are never read as a real home's, and a real home's never feed a proving run.
/// </summary>
public enum HomeKind
{
    /// <summary>The owner's home — and every home written before this build, which carries no mark.</summary>
    Real,

    /// <summary>A proving ground's shadow home: its markets are synthetic, and no evidence path of a real home reads it.</summary>
    Proving
}

/// <summary>
/// THE MARK ON A STORE (<c>U-proving-ground-a</c> item 2): one key, <see cref="Key"/>, in a key/value table that already
/// exists — <c>meta</c> in the database, <c>tape_meta</c> in the tape — written first when the store is created and never
/// rewritten. No rung: a store that has none is a real home's, because every home before this build is one.
/// </summary>
public static class HomeMark
{
    /// <summary>The key both stores keep their kind under.</summary>
    public const string Key = "home_kind";

    /// <summary>The word a store carries for <paramref name="kind"/>.</summary>
    public static string Word(HomeKind kind) => kind == HomeKind.Proving ? "proving" : "real";

    /// <summary>The kind a store's word names, or null for a word this build does not know.</summary>
    public static HomeKind? Parse(string? word) => word switch
    {
        "real" => HomeKind.Real,
        "proving" => HomeKind.Proving,
        _ => null
    };

    /// <summary>
    /// WHY A STORE OF ONE KIND IS NOT OPENED AS THE OTHER — naming the file, what it is marked (or that it carries no
    /// mark, and so is a real home's), and what this open expected. Null when the two agree.
    /// </summary>
    /// <param name="what">The store, in the owner's words: "the database", "the market-context tape".</param>
    /// <param name="file">The file.</param>
    /// <param name="word">The mark the file carries, or null for none.</param>
    /// <param name="expected">The kind this open is for.</param>
    public static string? Refusal(string what, string file, string? word, HomeKind expected)
    {
        var found = word is null ? HomeKind.Real : Parse(word);
        if (found == expected) return null;

        var carries = word is null
            ? $"it carries no home mark, so it is a real home's ('{Word(HomeKind.Real)}') — every home written before this build is one"
            : found is null
                ? $"it is marked '{word}', which is not a kind of home this build knows ('{Word(HomeKind.Real)}' or '{Word(HomeKind.Proving)}')"
                : $"it is marked as a {Word(found.Value)} home's ('{word}')";
        var wants = expected == HomeKind.Proving
            ? $"this open is a proving ground's, which reads only a proving home's ('{Word(HomeKind.Proving)}')"
            : $"this open is a real home's ('{Word(HomeKind.Real)}'), which never reads a proving ground's synthetic records";
        return $"TradeAgent did not open {what} at {file}: {carries}, and {wants}. Nothing has been read from it or written to it.";
    }

    /// <summary>
    /// THE MARK A STORE FILE CARRIES, READ WITHOUT WRITING ANYTHING: the value under <see cref="Key"/> in
    /// <paramref name="table"/>, or null when the file, the table or the key is not there.
    /// </summary>
    public static string? Read(string file, string table)
    {
        if (!File.Exists(file)) return null;
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        c.Open();
        return Read(c, table);
    }

    /// <summary>The mark under <see cref="Key"/> in <paramref name="table"/> on an open connection, or null. Writes nothing.</summary>
    public static string? Read(SqliteConnection connection, string table)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t";
            exists.Parameters.AddWithValue("$t", table);
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return null;
        }

        using var c = connection.CreateCommand();
        // The table is one of this build's two constants, never input: `meta` or `tape_meta`.
        c.CommandText = table switch
        {
            "meta" => "SELECT value FROM meta WHERE key=$k",
            "tape_meta" => "SELECT value FROM tape_meta WHERE key=$k",
            _ => throw new ArgumentException($"'{table}' is not a table a home mark is kept in", nameof(table))
        };
        c.Parameters.AddWithValue("$k", Key);
        return c.ExecuteScalar() as string;
    }
}

/// <summary>
/// THE PROVING GROUND'S PROCESS KIND (<c>U-proving-ground-a</c> item 1; <c>docs/ORCHESTRATION.md</c> § 2.14: "a separate
/// app process with its own home, pipe and registers, never inside the real one").
///
/// <para><b><see cref="ThisProcess"/> is internal and never serialized</b>, set only by a proving host's entry — the
/// <c>PuppetSeats.TestHost</c> pattern: nothing in the product sets it, and no file, setting or pipe op reaches it. It
/// decides the kind every store of this process expects by default (<see cref="ThisKind"/>); a store opened with an
/// explicit kind is that kind's, which is how a test exercises both directions in one process.</para>
///
/// <para><b>A proving process refuses a home that could be the real one</b> (<see cref="HomeRefusal"/>), checked at
/// <c>Paths</c>' first use: no <c>TRADEAGENT_HOME</c>, the default home, a home whose database does not carry the
/// proving mark, and no <c>TRADEAGENT_PIPE</c> or the default one — whose name is the real gateway's.</para>
///
/// <para><b>Not claimed</b> (<c>docs/ORCHESTRATION.md</c> § 5): SIM-001 against an unconfined process of the same user,
/// which can read either home's files whatever this says. What this makes hard is the app's own code paths.</para>
/// </summary>
public static class ProvingGround
{
    /// <summary>Whether this process is a proving ground. Set only by a proving host's entry; false in the product.</summary>
    internal static bool ThisProcess { get; set; }

    /// <summary>The kind every store of this process expects unless it is told otherwise.</summary>
    public static HomeKind ThisKind => ThisProcess ? HomeKind.Proving : HomeKind.Real;

    /// <summary>
    /// WHY A PROVING PROCESS MAY NOT RUN IN <paramref name="home"/> WITH <paramref name="pipe"/>, naming both — or null,
    /// because the home is a marked proving home other than the default and the pipe is its own.
    /// </summary>
    /// <param name="home">The process's <c>TRADEAGENT_HOME</c>, or null when it is not set.</param>
    /// <param name="defaultHome">The home a process with no <c>TRADEAGENT_HOME</c> would be: the real one.</param>
    /// <param name="pipe">The process's <c>TRADEAGENT_PIPE</c>, or null when it is not set.</param>
    public static string? HomeRefusal(string? home, string defaultHome, string? pipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultHome);
        var named = $"(home {(string.IsNullOrWhiteSpace(home) ? "not set" : home)}, pipe {(string.IsNullOrWhiteSpace(pipe) ? "not set" : pipe)})";

        if (string.IsNullOrWhiteSpace(home))
            return $"A proving ground runs only in a home of its own, and TRADEAGENT_HOME is not set, so this process would be "
                   + $"the real home at {defaultHome} {named}. It did not start.";

        if (SamePath(home, defaultHome))
            return $"A proving ground runs only in a home of its own, and {home} is the default home — the real one — "
                   + $"{named}. It did not start.";

        var database = Path.Combine(home, "state", "tradeagent.db");
        var word = HomeMark.Read(database, "meta");
        if (HomeMark.Parse(word) != HomeKind.Proving)
            return $"A proving ground runs only in a home marked as one, and {home} is not: "
                   + (word is null
                       ? File.Exists(database) ? $"its database at {database} carries no home mark, so it is a real home's" : $"it has no database at {database}"
                       : $"its database at {database} is marked '{word}'")
                   + $" {named}. It did not start.";

        if (string.IsNullOrWhiteSpace(pipe))
            return $"A proving ground speaks only on a pipe of its own, and TRADEAGENT_PIPE is not set, so this process would "
                   + $"open the real gateway's pipe, {Paths.DefaultPipeName} {named}. It did not start.";

        if (string.Equals(pipe.Trim(), Paths.DefaultPipeName, StringComparison.OrdinalIgnoreCase))
            return $"A proving ground speaks only on a pipe of its own, and {pipe} is the real gateway's {named}. It did not start.";

        return null;
    }

    /// <summary>
    /// Whether two paths name one directory, compared ignoring case and a trailing separator — so a spelling that
    /// differs only in case is refused as the same home, on the platforms whose file systems would treat it as one.
    /// </summary>
    static bool SamePath(string a, string b) =>
        string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Trim()));
}
