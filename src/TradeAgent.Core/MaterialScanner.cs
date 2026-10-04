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
/// Answers "was no agent process alive at any point since this mark?". Only when it says yes is a
/// file in the inbox recorded as <see cref="MaterialOrigin.Inbox"/> — the one origin that claims
/// something a directory listing cannot show. Defaults to the process-wide
/// <see cref="AgentPresence.Shared"/>; tests pass their own so nothing races through shared state. A
/// register's own <see cref="AgentPresence.NoneSince(PresenceMark)"/> is also the register each pass
/// takes its mark in (<see cref="AgentPresence.Behind"/>); any other answer is asked as it is.
/// </param>
/// <param name="now">The wall clock the rows and <c>material_scan_at</c> are stamped with, for people
/// to read. The machine's in the product; a test passes one it can step. No claim is decided by it.</param>
/// <param name="reads">The reads this pass makes of the disk (<see cref="WorkspaceReads"/>). The
/// machine's own in the product; a test passes one that refuses a folder or a file.</param>
public sealed class MaterialScanner(Database db, string? workspaceRoot = null,
    Func<PresenceMark, bool>? noAgentSince = null, AppFileManifest? appFiles = null,
    Func<DateTimeOffset>? now = null, WorkspaceReads? reads = null)
{
    readonly string _root = workspaceRoot ?? Paths.Workspace;
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    readonly WorkspaceReads _reads = reads ?? WorkspaceReads.Disk;
    readonly MaterialStore _store = new(db);
    readonly Func<PresenceMark, bool> _noAgentSince = noAgentSince ?? AgentPresence.Shared.NoneSince;

    /// <summary>
    /// WHAT THE APP WROTE INTO A ROLE'S HOME AND WHAT IT PUT IN IT, recorded by the app at the moment
    /// it wrote each file. The only thing <see cref="MaterialOrigin.App"/> is measured against.
    /// </summary>
    readonly AppFileManifest _appFiles = appFiles ?? AppFileManifest.Shared;

    /// <summary>
    /// THE REGISTER THIS PASS ATTESTS OVER, so that "the scanner and the agent are looking at the
    /// same one" is a thing a test can assert rather than a thing somebody read in a constructor.
    ///
    /// The barrier is worth nothing if the two halves drift apart: a chat session reporting into a
    /// register nobody reads is a running agent process this pass cannot see, and it would attest a
    /// window with an agent in it. Delegate identity is the check — same target, same method — and
    /// the target is what matters, because it is the register.
    /// </summary>
    public Func<PresenceMark, bool> Attests => _noAgentSince;

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
    /// When the previous complete pass began, by the wall clock — for a person reading the database.
    /// Nothing decides a claim by it: <see cref="LastMarkKey"/> is the window.
    /// </summary>
    const string LastScanKey = "material_scan_at";

    /// <summary>
    /// WHERE THE PREVIOUS COMPLETE PASS BEGAN, IN THE REGISTER'S ORDER — the mark it took
    /// (<see cref="AgentPresence.Mark"/>), as text. The window an <see cref="MaterialOrigin.Inbox"/>
    /// claim is attested over is [this, the sighting]. Kept in the database, beside
    /// <see cref="LastScanKey"/>, because a scanner is built fresh for every pass; and it carries the
    /// register's name, because a restart is a new register that cannot place an old mark.
    /// </summary>
    const string LastMarkKey = "material_scan_mark";

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
        var now = _now();
        int seen = 0, added = 0, removed = 0, skipped = 0;
        var addedBy = new Dictionary<MaterialOrigin, int>();
        var truncated = false;

        // EVERY FOLDER THIS PASS COULD NOT READ IN FULL, relative to the workspace: a tracked folder the
        // disk would not say is there, a folder it would not list, or the folder of a listed file it would
        // not describe (WorkspaceReads). Any at all and this pass did not look everywhere — see the window
        // below. Folders skipped ON PURPOSE are not here: they are `skipped`, skipped on every pass alike,
        // and nothing in them is ever recorded under any word.
        var unreadable = new HashSet<string>(StringComparer.Ordinal);

        // THIS PASS'S PLACE IN THE REGISTER'S ORDER, taken before it looks at anything — so an agent
        // that opens or closes a window while this walk runs is after it, inside the window the NEXT
        // pass measures, whatever the clock says. A question with no register behind it orders
        // nothing and takes no mark.
        var register = AgentPresence.Behind(_noAgentSince);
        var mark = register?.Mark();

        // The window every Inbox claim in this pass is measured over.
        var since = WindowOpenedAt(register);

        // ONE GROUP PER PLACE, and every role's home inside the second group. `present` and
        // MarkMissing are per GROUP, so the roles have to be walked together: marking missing after
        // one role's walk would delete the other role's rows on every pass.
        //
        // A GROUP CARRIES EVERY WORD ITS WALK CAN PRODUCE, which is why the role group names
        // App as well as Agent: MarkMissing sweeps by origin, and a group that named only Agent
        // would leave a deleted app file standing in the ledger forever.
        foreach (var (origins, paths) in new (MaterialOrigin[], (string Home, string Dir)[])[]
                 {
                     ([MaterialOrigin.Inbox, MaterialOrigin.InboxUnattested], [("", InboxDir)]),
                     ([MaterialOrigin.Agent, MaterialOrigin.App], [.. RolePaths()])
                 })
        {
            var present = new List<long>();
            var complete = true;
            var isInbox = origins[0] == MaterialOrigin.Inbox;

            // EVERY PLACE IN THIS GROUP COULD BE READ. A folder the disk would not list holds files this
            // pass never saw, and "I could not look" is not "it is gone" — the reason a pass that ran out
            // of budget marks nothing missing, reached from the other side.
            var read = true;

            foreach (var (home, dir) in paths)
            {
                var full = Path.Combine(_root, home, dir);
                switch (LookAtFolder(full))
                {
                    case Look.NotThere: continue;
                    case Look.CouldNotRead: read = false; unreadable.Add(Relative(full)); continue;
                }

                var (files, refused) = Walk(full, ref skipped, ct);
                foreach (var folder in refused) { read = false; unreadable.Add(Relative(folder)); }

                foreach (var file in files)
                {
                    if (seen >= FileLimit) { complete = false; truncated = true; break; }
                    ct.ThrowIfCancellationRequested();

                    // A FILE THE LISTING NAMED AND THE DISK WILL NOT DESCRIBE is a place this pass could
                    // not read, not a file that has gone: its row is not in `present`, so counting it gone
                    // would stamp it removed. One that really has gone since the listing is skipped.
                    var (look, size, modifiedUtc) = LookAtFile(file);
                    if (look == Look.NotThere) continue;
                    if (look == Look.CouldNotRead) { read = false; unreadable.Add(Relative(Path.GetDirectoryName(file)!)); continue; }

                    var rel = Relative(file);
                    // ONE QUESTION PER GROUP, ASKED PER SIGHTING and only for a row that is about to
                    // be written (see the Func overload of Observe).
                    //
                    // The inbox's is attestation: an agent that starts while this walk is running
                    // must not be attested away by a question asked before it existed, and asking
                    // later can only make the answer stricter.
                    //
                    // A ROLE'S HOME IS A DIFFERENT QUESTION, and it is measured rather than assumed:
                    // the app writes down every file IT puts in a home, and a file whose path and
                    // bytes both match that record is the app's. Anything else in there is the
                    // agent's, which is what this used to say about all of it — including the
                    // language reference the app writes and the briefs the relay delivers.
                    Func<MaterialOrigin> origin = isInbox
                        ? () => _noAgentSince(since) ? MaterialOrigin.Inbox : MaterialOrigin.InboxUnattested
                        : () => WeWroteIt(rel, file) ? MaterialOrigin.App : MaterialOrigin.Agent;

                    // THE WORD THE NEW ROW WENT IN WITH, kept as the store is answered so the pass
                    // can say what ARRIVED (ScanResult.Arrived) without asking the question twice.
                    // The store asks only for a row it is about to write, so this is set exactly
                    // when `isNew` is, and nothing about the row changes by being counted.
                    MaterialOrigin? written = null;
                    var (isNew, id) = _store.Observe(rel, () => (written = origin()).Value, size,
                        new DateTimeOffset(modifiedUtc, TimeSpan.Zero),
                        RunnableExts.Contains(new FileInfo(file).Extension), now);

                    present.Add(id);
                    seen++;
                    if (isNew)
                    {
                        added++;
                        if (written is { } word) addedBy[word] = addedBy.GetValueOrDefault(word) + 1;
                    }
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
            //
            // `read` is the same rule for a group with a place in it the disk would not read: the files
            // there were not seen, so none of them is gone (U-inbox-unreadable). Per GROUP, so the other
            // group — read in full — is still swept as it always was.
            if (complete && !truncated && read) removed += _store.MarkMissing(origins, present, now);
        }

        // THE WINDOW ADVANCES ONLY ON A PASS THAT WALKED THE WHOLE TREE, READ EVERY PLACE IN IT AND
        // GOT HERE. It carries the pass's START, so a file created while this walk was running is inside
        // the window the NEXT pass measures it over. A pass that ran out of budget, could not read a
        // place it walked, or threw before this line, leaves the older and wider window in place: it did
        // not look everywhere, so it cannot shorten the period the next pass has to account for.
        //
        // The unread place is the case that made this rule bite (U-inbox-unreadable): an agent alive in
        // the window writes into a folder of the drop folder, this pass cannot list that folder, and a
        // later pass that can — with no agent since this one — would attest the agent's file as the
        // owner's over a window this pass had shortened past it. A folder that stays unreadable holds
        // the window for as long as it does, and every file that arrives meanwhile gets the word that
        // wider window allows; the result names the folders (ScanResult.UnreadableFolders), and the app
        // says so on the activity log when that starts and when it clears (AppHost.UnreadableLine).
        //
        // Fail-closed, in the direction that costs a weaker word on a row rather than a claim nobody can
        // support. The mark and the wall time go in together, and a pass asked of no register leaves the
        // mark where it was.
        if (!truncated && unreadable.Count == 0)
            db.Write(_ =>
            {
                db.SetKv(LastScanKey, Sql.T(now));
                if (mark is { } taken) db.SetKv(LastMarkKey, taken.ToString());
                return 0;
            });

        var hashed = HashPending(ct);
        return new ScanResult(seen, added, hashed, removed, skipped, truncated)
        {
            AddedBy = addedBy,
            Unreadable = unreadable.Count,
            // The owner's drop folder first, because that is the place they look after; then the homes.
            UnreadableFolders =
            [
                .. unreadable
                    .OrderBy(f => IsInInbox(f) ? 0 : 1)
                    .ThenBy(f => f, StringComparer.Ordinal)
                    .Take(UnreadableNamed)
            ]
        };
    }

    /// <summary>How many of the folders a pass could not read its result names; the count is all of them.</summary>
    public const int UnreadableNamed = 20;

    /// <summary>Whether a path relative to the workspace is the owner's drop folder or inside it.</summary>
    public static bool IsInInbox(string relative) =>
        relative == InboxDir || relative.StartsWith(InboxDir + "/", StringComparison.Ordinal);

    /// <summary>
    /// WHERE THE WINDOW THIS PASS ATTESTS OVER OPENED, in the order of the register it asks — or a
    /// mark no register issued, wherever that order cannot be shown, so that every claim over the
    /// window is unsure.
    ///
    /// <para><b>The last complete pass's mark</b>, when there is one. A mark from another register — the
    /// process before a restart, whose agent may have gone on writing after its last pass — cannot be
    /// placed, and neither can text that is not a mark.</para>
    ///
    /// <para><b>A complete pass that left no mark</b> — a build that kept only the wall time, or a pass
    /// asked of no register — opened a window nobody can order against.</para>
    ///
    /// <para><b>No complete pass ever</b>: "since before anything happened". The register watched all
    /// of that only if the database's whole life is this process's — <see cref="Database.CreatedHere"/>
    /// — because then everything that could have written into the drop folder reported to it. A
    /// database an earlier process opened, whose every pass ran out of budget, is the case this
    /// answers unsure for: that process's agent may have written anything.</para>
    /// </summary>
    PresenceMark WindowOpenedAt(AgentPresence? register)
    {
        if (db.GetKv(LastMarkKey) is { } stored) return PresenceMark.Parse(stored) ?? PresenceMark.Unordered;
        if (db.GetKv(LastScanKey) is not null) return PresenceMark.Unordered;
        return register is not null && db.CreatedHere ? register.Origin : PresenceMark.Unordered;
    }

    /// <summary>
    /// DID THE APP PUT THIS FILE HERE? Both halves, and in this order.
    ///
    /// <para>The path is asked first because it is a dictionary lookup and the hash costs a read of
    /// the file — so the bytes are read only for the handful of paths the app has written down,
    /// never for the workspace at large. That is the budget this whole class is shaped by.</para>
    ///
    /// <para><b>Path alone would not be a measurement.</b> It would say "the app wrote something
    /// here once", and a role that rewrote the file would inherit the app's name for its own text.
    /// <b>Hash alone would be worse</b>: every file the app writes into a home is readable by the
    /// role, so the agent could copy one anywhere and be recorded as TradeAgent.</para>
    ///
    /// <para>The bytes are read here rather than taken from the row's later hash because the origin
    /// is written WITH the row. A file swapped between the walk and this read is measured as the
    /// agent's — the weaker word, which is the direction this must fail in.</para>
    /// </summary>
    bool WeWroteIt(string rel, string full) =>
        _appFiles.Names(rel) && _appFiles.Wrote(rel, Sha256Hex.OfFile(full));

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

    /// <summary>
    /// DOES THE OWNER'S DROP FOLDER HOLD A FILE NO PASS HAS RECORDED — one whose (path, size, mtime)
    /// has no live row, which is exactly a sighting the next pass would write a NEW row for? The
    /// mission loop asks this before every turn, and takes a pass first when the answer is yes.
    ///
    /// <para><b>Why the ledger and not a clock.</b> This used to be asked of the files' own times —
    /// "is either newer than the instant the last pass began?" — and those come from two different
    /// clocks. A filesystem stamps a file from its own: hosted ubuntu's ext4 from the kernel's coarse
    /// clock, which advances once a millisecond and ran up to 1.3 ms behind
    /// <see cref="DateTimeOffset.UtcNow"/> in run 37051859960, so a file that landed just after a pass
    /// began, and that the pass never saw, read as older than the pass. A move on one disk keeps a
    /// file's times whatever they are, so a statement downloaded yesterday and dragged in today read
    /// as older than every pass. Either way the loop heard "nothing new", launched the next turn
    /// first, and the file was recorded behind that turn as <see cref="MaterialOrigin.InboxUnattested"/>
    /// — for good, because a row is written once. The ledger has no clock in it: a file is recorded or
    /// it is not.</para>
    ///
    /// <para><b>The same walk, the same reads and the same identity as <see cref="Scan"/></b>: the same
    /// skipped directories, depth and budget, the same three reads of the disk and the same rule for
    /// what counts as unread (<see cref="LookAtFolder"/>, <see cref="Walk"/>, <see cref="LookAtFile"/>),
    /// the same relative spelling, and the tuple <see cref="MaterialStore.Observe"/> matches. A file the
    /// scanner never records — inside a package cache — is not news, or every turn would pay for a walk
    /// that cannot record it. A file past the budget, a folder the disk would not list or say is there,
    /// and a file it would not describe are all answered yes — exactly the places that make a pass
    /// incomplete: a spurious pass costs a walk, and a missed one costs the owner the word.</para>
    ///
    /// <para>It writes nothing and asks the presence register nothing. Whether a sighting is the
    /// owner's is still decided by the pass, at the sighting, exactly as before; this only decides
    /// whether the loop takes that pass before the turn or leaves the file for the pass behind it.</para>
    /// </summary>
    public bool InboxHoldsUnrecorded(CancellationToken ct = default)
    {
        var inbox = Path.Combine(_root, InboxDir);
        switch (LookAtFolder(inbox))
        {
            case Look.NotThere: return false;
            case Look.CouldNotRead: return true;
        }

        var skipped = 0;
        var (files, refused) = Walk(inbox, ref skipped, ct);
        if (refused.Count > 0 || files.Count >= FileLimit) return true;

        var recorded = _store.Live([MaterialOrigin.Inbox, MaterialOrigin.InboxUnattested]);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var (look, size, modifiedUtc) = LookAtFile(file);
            if (look == Look.NotThere) continue;
            if (look == Look.CouldNotRead) return true;

            var tuple = (Relative(file), size, Sql.T(new DateTimeOffset(modifiedUtc, TimeSpan.Zero)));
            if (!recorded.Contains(tuple)) return true;
        }
        return false;
    }

    /// <summary>What one read of the disk found at a path.</summary>
    enum Look
    {
        /// <summary>It is there, and the disk described it.</summary>
        There,
        /// <summary>The disk said nothing is there — an answer, and the only one that means absent.</summary>
        NotThere,
        /// <summary>The disk would not say: a refusal, a failing device, a share that went away.</summary>
        CouldNotRead
    }

    /// <summary>
    /// IS THIS TRACKED FOLDER THERE? Asked before a walk, and asked of the disk in a way that can refuse:
    /// <see cref="Directory.Exists"/> answers false for a folder whose parent the disk will not search,
    /// and a pass that took that for "there is no inbox" would stamp every file in it removed.
    /// </summary>
    Look LookAtFolder(string dir)
    {
        try { return _reads.FolderIsThere(dir) ? Look.There : Look.NotThere; }
        catch (IOException) { return Look.CouldNotRead; }
        catch (UnauthorizedAccessException) { return Look.CouldNotRead; }
    }

    /// <summary>
    /// ONE LISTED FILE'S SIZE AND WRITE TIME, or why there are none: it has gone since the listing, or
    /// the disk would not describe it — which <see cref="FileSystemInfo.Exists"/> cannot tell apart, so
    /// it is not asked.
    /// </summary>
    (Look Look, long Size, DateTime ModifiedUtc) LookAtFile(string file)
    {
        try
        {
            return _reads.Stat(file) is { } stat
                ? (Look.There, stat.Size, stat.WrittenUtc)
                : (Look.NotThere, 0, default);
        }
        catch (IOException) { return (Look.CouldNotRead, 0, default); }
        catch (UnauthorizedAccessException) { return (Look.CouldNotRead, 0, default); }
    }

    /// <summary>
    /// THE WALK OF ONE TRACKED FOLDER: every file in it this pass would record, and every folder in it
    /// the disk would not list — "what it could not read", returned beside what it could, because a
    /// walk that dropped the second half let a pass that never saw a folder count itself complete.
    /// </summary>
    (List<string> Files, List<string> Unreadable) Walk(string dir, ref int skipped, CancellationToken ct)
    {
        // Recursion is written out rather than using EnumerateFiles(SearchOption.AllDirectories)
        // because that overload cannot skip a subtree — it would walk every node_modules it found
        // and only then let us discard the results.
        var files = new List<string>();
        var unreadable = new List<string>();
        Collect(dir, 0, files, ref skipped, unreadable, ct);
        return (files, unreadable);
    }

    /// <param name="skipped">Folders left out ON PURPOSE — noise, dot-folders, past the depth limit —
    /// on every pass alike, so nothing in them is ever recorded and they hold nothing open.</param>
    /// <param name="unreadable">
    /// Folders the disk would not list, kept apart from the ones skipped on purpose: they hold files a
    /// pass did not see, so <see cref="Scan"/> is incomplete and <see cref="InboxHoldsUnrecorded"/>
    /// answers yes for them, and for the skipped ones neither.
    /// </param>
    void Collect(string dir, int depth, List<string> into, ref int skipped, List<string> unreadable, CancellationToken ct)
    {
        if (depth > DepthLimit) { skipped++; return; }
        ct.ThrowIfCancellationRequested();

        string[] entries, subs;
        try
        {
            (entries, subs) = _reads.List(dir);
        }
        catch (IOException) { unreadable.Add(dir); return; }
        catch (UnauthorizedAccessException) { unreadable.Add(dir); return; }

        into.AddRange(entries);
        // Stop collecting, not just stop consuming. Enumerating a hundred thousand paths into a list
        // and then discarding all but the first few thousand is the same amount of walking.
        if (into.Count >= FileLimit) return;

        foreach (var sub in subs)
        {
            var name = Path.GetFileName(sub);
            if (NoiseDirs.Contains(name) || name.StartsWith('.')) { skipped++; continue; }
            Collect(sub, depth + 1, into, ref skipped, unreadable, ct);
            if (into.Count >= FileLimit) return;
        }
    }

    string Relative(string full) =>
        Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
/// THE THREE READS A MATERIAL PASS MAKES OF THE DISK, and the only three: whether a tracked folder is
/// there, what one folder lists, and one listed file's size and write time.
///
/// <para><b>"Not there" is an answer; a refusal is not.</b> Each read says absent — false, null — only
/// when the disk said the path is not there, and THROWS for anything else: a permission refused, a
/// device that failed, a share or a link target that went away. The pass counts a throw as a place it
/// could not read (U-inbox-unreadable). The machine's convenient reads do not keep the two apart:
/// <see cref="Directory.Exists"/> and <see cref="FileSystemInfo.Exists"/> answer false for a path the
/// disk refused to describe — measured on macOS, .NET 10.0.400: in a folder that can be listed but not
/// searched, <c>Directory.GetFiles</c> names the file, <c>FileInfo.Exists</c> answers false and
/// <c>FileInfo.Length</c> throws <see cref="UnauthorizedAccessException"/> — so a file the pass found
/// and could not inspect read as a file that had gone.</para>
///
/// <para>The machine's own in the product (<see cref="Disk"/>). A test passes one that refuses a folder
/// or a file, because a permission bit is not a refusal every platform honours the same way. It is a
/// constructor argument of the app's own code: nothing the agent can write, and nothing on the pipe or
/// in <c>state/</c>, chooses it.</para>
/// </summary>
/// <param name="FolderIsThere">Whether this path is a folder: false only when the disk says nothing is
/// there (or something that is not a folder); throws when it will not say.</param>
/// <param name="List">The files and the folders directly inside one folder, as full paths. It has no
/// "absent" answer: a folder that cannot be listed — refused, gone since its parent named it, or a link
/// whose target cannot be reached — is a place the pass did not see, and the files under a share that
/// went away are not gone.</param>
/// <param name="Stat">One listed file's size and last write time (UTC), or null when the disk says the
/// file is no longer there; throws when it will not say.</param>
public sealed record WorkspaceReads(
    Func<string, bool> FolderIsThere,
    Func<string, (string[] Files, string[] Folders)> List,
    Func<string, (long Size, DateTime WrittenUtc)?> Stat)
{
    /// <summary>The machine's own disk.</summary>
    public static WorkspaceReads Disk { get; } = new(
        FolderOnDisk,
        dir => (Directory.GetFiles(dir), Directory.GetDirectories(dir)),
        FileOnDisk);

    static bool FolderOnDisk(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    static (long Size, DateTime WrittenUtc)? FileOnDisk(string path)
    {
        // Both from ONE look at the disk: the first property read fills the FileInfo's cache and the
        // second is served from it, so the size and the time describe the same moment of the file.
        var info = new FileInfo(path);
        try { return (info.Length, info.LastWriteTimeUtc); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
