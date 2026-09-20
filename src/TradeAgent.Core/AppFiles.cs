namespace TradeAgent.Core;

/// <summary>
/// WHAT THE APP ITSELF WROTE INTO A ROLE'S HOME, written down at the moment it wrote it: the path
/// relative to that home, and the sha256 of the bytes it put there. It is the only thing
/// <see cref="MaterialOrigin.App"/> is ever measured against.
///
/// <para><b>Why it exists.</b> <see cref="MaterialScanner"/> records everything under a role's
/// tracked directories as <see cref="MaterialOrigin.Agent"/> — "the agent produced it". That was
/// true while the only things in those directories were things the agent had made, and it stopped
/// being true when the app began writing there: the strategy language reference and the three worked
/// programs, and every report or brief the relay delivers into <c>in/</c>. A false origin in the one
/// table the agent cannot edit is a measurement error, not a formatting one — it credits the agent
/// with the app's own files.</para>
///
/// <para><b>Why a file under <see cref="Paths.State"/>.</b> An origin is a MEASUREMENT, so what it
/// is measured against must be something the observed party cannot author. This sits beside
/// <c>data/</c> and <c>reports/</c>, OUTSIDE the workspace a role is free in, and there is no verb
/// and no pipe op that writes here. A manifest a role could append to would be a role that can name
/// its own file as the app's, which is the exact claim this exists to refuse.</para>
///
/// <para><b>Relative to the home, never absolute.</b> <c>TRADEAGENT_HOME</c> can move an install —
/// a portable build on a stick is one of the reasons <see cref="Paths"/> reads it — and a manifest
/// keyed by yesterday's absolute path would quietly stop matching anything, which would read as "the
/// agent wrote all of this".</para>
///
/// <para><b>Failing to record is fail-closed.</b> An IO error leaves the entry unwritten, and a path
/// the manifest does not name is measured as the agent's: the weaker word, never the stronger one.
/// Same direction the inbox attestation fails in.</para>
///
/// <para><b>It grows with what the app writes</b> — one entry per path, so a few per role per build
/// plus one per delivered publication, and a re-record of the same bytes writes nothing. Nothing is
/// pruned: an entry is the record that the app once put those bytes there, and a manifest that
/// forgot would turn an untouched file into the agent's on the next pass that re-sighted it.</para>
/// </summary>
public sealed class AppFileManifest
{
    /// <summary>The install's one manifest. Under <see cref="Paths.State"/>, where no role reaches.</summary>
    public static AppFileManifest Shared { get; } = new(Path.Combine(Paths.State, "app-files.tsv"));

    public AppFileManifest(string manifestPath) => ManifestPath = manifestPath;

    /// <summary>The file the entries are kept in.</summary>
    public string ManifestPath { get; }

    readonly object _gate = new();

    /// <summary>
    /// The entries as last read or written, or null when they have to be read again. In memory
    /// because the scanner asks per new sighting and the app is the only writer — a second process
    /// reads this file never, and writes it never.
    /// </summary>
    Dictionary<string, string>? _entries;

    /// <summary>
    /// The key one entry is kept under: the path the scanner records, which is the home and the
    /// path inside it joined. Slashes, because that is the form <c>material.rel_path</c> carries.
    /// </summary>
    public static string Key(string home, string relPath) =>
        (home.Length == 0 ? relPath : $"{home}/{relPath}").Replace('\\', '/');

    /// <summary>Records that the app wrote this text at this path inside this home.</summary>
    public void Record(string home, string relPath, string text) =>
        Put(Key(home, relPath), Sha256Hex.Of(text));

    /// <summary>Records that the app wrote these bytes at this path inside this home.</summary>
    public void Record(string home, string relPath, ReadOnlySpan<byte> bytes) =>
        Put(Key(home, relPath), Sha256Hex.Of(bytes));

    /// <summary>
    /// Is this a path the app ever wrote? Asked FIRST by the scanner, because the answer is a
    /// dictionary lookup and the question after it costs a read of the file's bytes — and hashing
    /// everything on every pass is the budget this ledger is built around not spending.
    /// </summary>
    public bool Names(string relPath)
    {
        lock (_gate) return Load().ContainsKey(relPath);
    }

    /// <summary>
    /// Did the app write EXACTLY these bytes at this path? Both halves or it is not the app's: a
    /// path match alone would make a role's edit of an app file read as the app's own, and a hash
    /// match alone would let anything the agent copied the app's bytes into read as the app's.
    /// </summary>
    public bool Wrote(string relPath, string? sha256)
    {
        if (sha256 is null) return false;
        lock (_gate) return Load().TryGetValue(relPath, out var known) && known == sha256;
    }

    void Put(string key, string sha)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.TryGetValue(key, out var known) && known == sha) return;   // already on the record
            entries[key] = sha;
            Save(entries);
        }
    }

    Dictionary<string, string> Load()
    {
        if (_entries is not null) return _entries;

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(ManifestPath))
                foreach (var line in File.ReadLines(ManifestPath))
                {
                    // sha TAB path. A line this build cannot read is not an entry — it is skipped
                    // rather than guessed at, and a path it named then measures as the agent's.
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || tab == line.Length - 1) continue;
                    entries[line[(tab + 1)..]] = line[..tab];
                }
        }
        // NOT cached: a read that failed is not "the app has written nothing", and remembering it as
        // that would make every app file the agent's for the rest of the process's life.
        catch (IOException) { return entries; }
        catch (UnauthorizedAccessException) { return entries; }

        return _entries = entries;
    }

    /// <summary>
    /// Written whole and moved into place, so a pass that reads it while this runs sees the previous
    /// manifest or this one and never half of either.
    /// </summary>
    void Save(Dictionary<string, string> entries)
    {
        var tmp = ManifestPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            File.WriteAllLines(tmp, entries.Select(e => $"{e.Value}\t{e.Key}"));
            File.Move(tmp, ManifestPath, overwrite: true);
        }
        // What is on disk is what is recorded. Dropping the cache is what keeps the two the same:
        // the next question re-reads the file, and the entry that did not land is not claimed.
        catch (IOException) { _entries = null; }
        catch (UnauthorizedAccessException) { _entries = null; }
    }
}
