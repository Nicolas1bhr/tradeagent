using System.Security.Cryptography;
using TradeAgent.Core.Db;

namespace TradeAgent.Core;

/// <summary>
/// Walks the workspace and writes down what is there. The only writer of <c>material</c> rows.
///
/// Two things shape every limit below, and both are load-bearing:
///
/// <b>The ledger must not become the dump it exists to prevent.</b> One <c>npm install</c> inside
/// the workspace is forty thousand files. Tracking them would drown the twelve rows anybody
/// actually wants to read, so noise directories are skipped by name and <c>scratch/</c> — which the
/// agent is told is disposable — is not tracked at all.
///
/// <b>This runs on a modest laptop.</b> Hashing every file on every pass is out of the question, so
/// the pass compares the tuple it can read from a directory listing (path, size, mtime) and opens
/// only what changed, a bounded number per pass.
/// </summary>
public sealed class MaterialScanner(Database db, string? workspaceRoot = null)
{
    readonly string _root = workspaceRoot ?? Paths.Workspace;
    readonly MaterialStore _store = new(db);

    /// <summary>Where the account owner drops things. Everything under it is theirs, not the agent's.</summary>
    public const string InboxDir = "inbox";

    /// <summary>
    /// The agent's own directories that are worth remembering. <c>logs/</c> and <c>scratch/</c> are
    /// deliberately absent: the agent is told scratch is disposable, and its logs churn every run.
    /// The rule that makes this legible to the agent is in AGENTS.md — anything it wants on the
    /// record goes in a tracked folder.
    /// </summary>
    public static readonly string[] TrackedAgentDirs = ["trading", "research", "strategies", "data", "scripts"];

    /// <summary>Build output and package caches. Present by the tens of thousands or not at all.</summary>
    static readonly HashSet<string> NoiseDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "__pycache__", ".venv", "venv", "env",
        "bin", "obj", "target", "dist", "build", ".next", ".cache", ".pytest_cache",
        ".mypy_cache", ".ruff_cache", ".gradle", ".idea", ".vs", "packages"
    };

    static readonly HashSet<string> RunnableExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".com", ".scr", ".bat", ".cmd", ".ps1", ".psm1",
        ".vbs", ".js", ".jar", ".py", ".sh", ".pl", ".rb", ".reg", ".lnk", ".dll", ".wsf"
    };

    /// <summary>Files per pass. A drop larger than this is picked up over successive passes.</summary>
    public int FileLimit { get; init; } = 5_000;

    /// <summary>Directory depth. Deep trees are almost always something unpacked, not something handed over.</summary>
    public int DepthLimit { get; init; } = 12;

    /// <summary>Files hashed per pass, so one 4 GB drop cannot stall the pass that follows it.</summary>
    public int HashesPerPass { get; init; } = 24;

    public ScanResult Scan(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        int seen = 0, added = 0, removed = 0, skipped = 0;
        var truncated = false;

        foreach (var (origin, dirs) in new (MaterialOrigin, string[])[]
                 {
                     (MaterialOrigin.Inbox, [InboxDir]),
                     (MaterialOrigin.Agent, TrackedAgentDirs)
                 })
        {
            var present = new List<long>();
            var complete = true;

            foreach (var dir in dirs)
            {
                var full = Path.Combine(_root, dir);
                if (!Directory.Exists(full)) continue;

                foreach (var file in Walk(full, 0, ref skipped, ct))
                {
                    if (seen >= FileLimit) { complete = false; truncated = true; break; }
                    ct.ThrowIfCancellationRequested();

                    FileInfo info;
                    try { info = new FileInfo(file); if (!info.Exists) continue; }
                    catch (IOException) { skipped++; continue; }
                    catch (UnauthorizedAccessException) { skipped++; continue; }

                    var rel = Relative(file);
                    var (isNew, id) = _store.Observe(rel, origin, info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        RunnableExts.Contains(info.Extension), now);

                    present.Add(id);
                    seen++;
                    if (isNew) added++;
                }

                if (!complete) break;
            }

            // A pass that ran out of budget saw only part of the tree, and "I did not see it" is not
            // "it is gone". Marking the remainder removed there would invent a deletion — the exact
            // kind of false entry that makes a record untrustworthy.
            //
            // `complete` is the half that is PROVEN to bite: remove it and
            // A_scan_that_ran_out_of_budget_never_reports_a_file_as_removed fails. `!truncated` is
            // belt-and-braces for a case no test currently produces — a later origin whose walk
            // comes back empty after an earlier one ran out of budget — and removing it on its own
            // breaks nothing today. It stays because the cost is one boolean and the failure it
            // would cover is a silent false deletion. Do not read it as covered.
            if (complete && !truncated) removed += _store.MarkMissing(origin, present, now);
        }

        var hashed = HashPending(ct);
        return new ScanResult(seen, added, hashed, removed, skipped, truncated);
    }

    /// <summary>
    /// Fills in hashes for rows that do not have one yet. Separated from the walk so a slow disk
    /// delays the hashes and not the record that the file arrived — knowing a 4 GB installer landed
    /// at 14:02 is most of the value, and it should not wait on reading 4 GB.
    /// </summary>
    int HashPending(CancellationToken ct)
    {
        var hashed = 0;
        foreach (var m in _store.NeedingHash(HashesPerPass))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(_root, m.RelPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                using var fs = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _store.SetHash(m.Id, Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant());
                hashed++;
            }
            catch (IOException) { }                 // still being written, or gone — the next pass retries
            catch (UnauthorizedAccessException) { }
        }
        return hashed;
    }

    IEnumerable<string> Walk(string dir, int depth, ref int skipped, CancellationToken ct)
    {
        // Recursion is written out rather than using EnumerateFiles(SearchOption.AllDirectories)
        // because that overload cannot skip a subtree — it would walk every node_modules it found
        // and only then let us discard the results.
        var files = new List<string>();
        Collect(dir, depth, files, ref skipped, ct);
        return files;
    }

    void Collect(string dir, int depth, List<string> into, ref int skipped, CancellationToken ct)
    {
        if (depth > DepthLimit) { skipped++; return; }
        ct.ThrowIfCancellationRequested();

        string[] entries, subs;
        try
        {
            entries = Directory.GetFiles(dir);
            subs = Directory.GetDirectories(dir);
        }
        catch (IOException) { skipped++; return; }
        catch (UnauthorizedAccessException) { skipped++; return; }

        into.AddRange(entries);
        // Stop collecting, not just stop consuming. Enumerating a hundred thousand paths into a list
        // and then discarding all but the first few thousand is the same amount of walking.
        if (into.Count >= FileLimit) return;

        foreach (var sub in subs)
        {
            var name = Path.GetFileName(sub);
            if (NoiseDirs.Contains(name) || name.StartsWith('.')) { skipped++; continue; }
            Collect(sub, depth + 1, into, ref skipped, ct);
            if (into.Count >= FileLimit) return;
        }
    }

    string Relative(string full) =>
        Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/');
}
