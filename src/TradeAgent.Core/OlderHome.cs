using Microsoft.Data.Sqlite;

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
public sealed class OlderHomeException(string home, int homeSchema, string thisVersion)
    : Exception(OlderHome.Words(home, homeSchema, thisVersion))
{
    /// <summary>The home directory refused (the folder that holds <c>state\tradeagent.db</c>).</summary>
    public string Home { get; } = home;

    /// <summary>The schema its database was stamped at.</summary>
    public int HomeSchema { get; } = homeSchema;

    /// <summary>The version of TradeAgent refusing it.</summary>
    public string ThisVersion { get; } = thisVersion;

    /// <summary>The release that wrote it, as the words name it.</summary>
    public string WrittenBy => Versions.ReleaseThatWrote(HomeSchema) ?? $"schema {HomeSchema}";
}

/// <summary>The refusal's words and the owner's start-fresh press.</summary>
public static class OlderHome
{
    /// <summary>What the owner reads on the refusal screen: both versions, that nothing was changed, and the repair.</summary>
    public static string Words(string home, int homeSchema, string thisVersion) =>
        $"This is TradeAgent {thisVersion}. The folder it keeps your records in ({home}) was written by TradeAgent " +
        $"{Versions.ReleaseThatWrote(homeSchema) ?? $"schema {homeSchema}"} (its records are at schema {homeSchema}; this version opens " +
        $"schema {Versions.OldestHomeSchema} and later), and this version does not open it: it would have to guess what the old records " +
        "mean, and it does not guess. Nothing in that folder has been changed. You can start fresh: the old folder is kept, whole, " +
        "beside the new one, and nothing in it is deleted.";

    /// <summary>What the second press says it will do, in full.</summary>
    public const string ConfirmStartFresh =
        "Yes — keep the old folder aside and start fresh (you will set TradeAgent up again and sign in to the AI again)";

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
    /// wrote it and the moment — <c>TradeAgent-from-0.1.2-20261031-101500</c> — and makes an empty <paramref name="home"/> in its place.
    /// Returns the folder the old home now lives in.
    ///
    /// <para>Refuses, moving nothing, unless the home is STILL one this build refuses (read again here, read-only): this press can
    /// never move a home the current build opens. A rename Windows will not do — a file in it held open by another program — is said
    /// in words and leaves the home where it was; nothing is copied, so nothing is half-moved.</para>
    /// </summary>
    public static string SetAside(string home, DateTimeOffset at)
    {
        var full = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (RefusedSchema(full) is not { } schema)
            throw new InvalidOperationException($"The folder {full} is not a home from an older TradeAgent, so starting fresh did not move it.");

        SqliteConnection.ClearAllPools();
        var label = (Versions.ReleaseThatWrote(schema) ?? $"schema-{schema}").Replace(" or ", "-or-");
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
        return aside;
    }
}
