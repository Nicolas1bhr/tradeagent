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
/// <param name="noAgentSince">
/// Answers "was no agent process alive at any point since this instant?". Only when it says yes is
/// a file in the inbox recorded as <see cref="MaterialOrigin.Inbox"/> — the one origin that claims
/// something a directory listing cannot show. Defaults to the process-wide
/// <see cref="AgentPresence.Shared"/>; tests pass their own so nothing races through shared state.
/// </param>
public sealed class MaterialScanner(Database db, string? workspaceRoot = null, Func<DateTimeOffset, bool>? noAgentSince = null)
{
    readonly string _root = workspaceRoot ?? Paths.Workspace;
    readonly MaterialStore _store = new(db);
    readonly Func<DateTimeOffset, bool> _noAgentSince = noAgentSince ?? AgentPresence.Shared.NoneSince;

    /// <summary>
    /// THE REGISTER THIS PASS ATTESTS OVER, so that "the scanner and the agent are looking at the
    /// same one" is a thing a test can assert rather than a thing somebody read in a constructor.
    ///
    /// The barrier is worth nothing if the two halves drift apart: a chat session reporting into a
    /// register nobody reads is a running agent process this pass cannot see, and it would attest a
    /// window with an agent in it. Delegate identity is the check — same target, same method — and
    /// the target is what matters, because it is the register.
    /// </summary>
    public Func<DateTimeOffset, bool> Attests => _noAgentSince;

    /// <summary>Where the account owner drops things. Everything under it is theirs, not the agent's.</summary>
    public const string InboxDir = "inbox";

    /// <summary>
    /// The CHAIR's tree, beside <see cref="InboxDir"/> rather than around it. A recorded path reads
    /// <c>agent/scripts/x.py</c>. It is the same string as
    /// <see cref="CouncilRoles.HomeDir"/> for Operations, and every OTHER role's home is walked
    /// under its own name — see <see cref="RolePaths"/>.
    /// </summary>
    public const string AgentDir = "agent";

    /// <summary>
    /// When the previous pass ran. The window an <see cref="MaterialOrigin.Inbox"/> claim is
    /// attested over is [this, the sighting]. Kept in the database rather than on the instance
    /// because a scanner is built fresh for every pass.
    /// </summary>
    const string LastScanKey = "material_scan_at";

    /// <summary>
    /// The agent's own directories that are worth remembering, relative to <see cref="AgentDir"/>.
    /// <c>logs/</c> and <c>scratch/</c> are deliberately absent: the agent is told scratch is
    /// disposable, and its logs churn every run. The rule that makes this legible to the agent is
    /// in AGENTS.md — anything it wants on the record goes in a tracked folder.
    /// </summary>
    public static readonly string[] TrackedAgentDirs = ["trading", "research", "strategies", "data", "scripts"];

    /// <summary>
    /// EVERY DIRECTORY THIS PASS WALKS INSIDE ONE ROLE'S HOME: the role's own tracked folders, plus
    /// the two the APP owns — <c>in/</c>, what was delivered to it, and <c>out/</c>, what it handed
    /// back and what the fence refused.
    ///
    /// <para>Those last two are the reason this array exists. A published report is the most
    /// consequential file a role writes and it was the one file nothing recorded: the relay's tables
    /// say an artifact was committed, and the ledger — the app's own measurement of what is on disk,
    /// which the agent cannot edit — said nothing at all. Same for a brief the role was handed. The
    /// two records answer different questions and the second is the one that survives a role
    /// deleting its own copy.</para>
    /// </summary>
    public static readonly string[] TrackedRoleDirs =
        [.. TrackedAgentDirs, CouncilRoles.InDir, CouncilRoles.OutDir];

    /// <summary>
    /// EVERY ROLE'S HOME AND EVERY DIRECTORY IN IT, as (home, directory) pairs relative to the
    /// workspace root.
    ///
    /// <para>This used to be the chair's home alone. The council gave the Research Director its own
    /// folder and nothing walked it, so everything that role read, wrote or was handed was outside
    /// the one record the agent cannot edit — and <c>docs/COUNCIL.md</c>'s "measured facts, the
    /// app's tables, read-only to agents" covered half a council.</para>
    /// </summary>
    public static IEnumerable<(string Home, string Dir)> RolePaths() =>
        from role in CouncilRoles.All
        from dir in TrackedRoleDirs
        select (CouncilRoles.HomeDir(role), dir);

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

        // The window every Inbox claim in this pass is measured over. On the very first pass there
        // is no previous one, and MinValue is the honest window: "since before anything happened".
        var since = Sql.TimeN(db.GetKv(LastScanKey)) ?? DateTimeOffset.MinValue;

        // ONE GROUP PER ORIGIN, and every role's home inside the agent group. `present` and
        // MarkMissing are per GROUP, so the roles have to be walked together: marking missing after
        // one role's walk would delete the other role's rows on every pass.
        foreach (var (origins, paths) in new (MaterialOrigin[], (string Home, string Dir)[])[]
                 {
                     ([MaterialOrigin.Inbox, MaterialOrigin.InboxUnattested], [("", InboxDir)]),
                     ([MaterialOrigin.Agent], [.. RolePaths()])
                 })
        {
            var present = new List<long>();
            var complete = true;

            foreach (var (home, dir) in paths)
            {
                var full = Path.Combine(_root, home, dir);
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
                    // Asked HERE, per sighting, rather than once for the pass: an agent that starts
                    // while this walk is running must not be attested away by a question asked
                    // before it existed. `origins[0]` is the attested word, `[^1]` the weaker one,
                    // and for the agent's own tree they are the same word.
                    var origin = _noAgentSince(since) ? origins[0] : origins[^1];
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
            if (complete && !truncated) removed += _store.MarkMissing(origins, present, now);
        }

        // THE WINDOW ADVANCES ONLY ON A PASS THAT WALKED THE WHOLE TREE AND GOT HERE. It carries
        // the pass's START, so a file created while this walk was running is inside the window the
        // NEXT pass measures it over. A pass that ran out of budget, or threw before this line,
        // leaves the older and wider window in place: it did not look everywhere, so it cannot
        // shorten the period the next pass has to account for. Fail-closed, in the direction that
        // costs a weaker word on a row rather than a claim nobody can support.
        if (!truncated) db.SetKv(LastScanKey, Sql.T(now));

        var hashed = HashPending(ct);
        return new ScanResult(seen, added, hashed, removed, skipped, truncated);
    }

    /// <summary>
    /// Fills in hashes for rows that do not have one yet. Separated from the walk so a slow disk
    /// delays the hashes and not the record that the file arrived — knowing a 4 GB installer landed
    /// at 14:02 is most of the value, and it should not wait on reading 4 GB.
    ///
    /// <b>The tuple the row is keyed on is re-read here, from the open handle, before and after the
    /// bytes</b> (Codex F19). The gap between the walk and this loop is unbounded — a 4 GB drop is
    /// hashed over several passes — and nothing checked that the file was still the one the row
    /// describes. An equal-length replacement with the mtime restored inside that gap had its hash
    /// written onto the earlier sighting's row, which is the same lie as finding 6 reached from the
    /// other side: a measurement filed against something that was not measured.
    ///
    /// From the HANDLE, not the path, because by then the path may name a different file entirely;
    /// and twice, because a swap during the read would otherwise pass the check before it happened.
    /// A row that fails either check is simply left unhashed: the next walk sees the new tuple and
    /// records it as the new version it is.
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
                if (!IsStill(fs, m)) continue;

                var digest = Sha256Hex.Of(fs);
                if (!IsStill(fs, m)) continue;

                _store.SetHash(m.Id, digest);
                hashed++;
            }
            catch (IOException) { }                 // still being written, or gone — the next pass retries
            catch (UnauthorizedAccessException) { }
        }
        return hashed;
    }

    /// <summary>Is the open file still the one this row was written about?</summary>
    static bool IsStill(FileStream fs, Material m) =>
        fs.Length == m.SizeBytes &&
        new DateTimeOffset(File.GetLastWriteTimeUtc(fs.SafeFileHandle), TimeSpan.Zero) == m.ModifiedAt;

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
