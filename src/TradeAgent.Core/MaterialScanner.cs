using System.Diagnostics;
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
    readonly WorkspaceReads _reads = reads ?? WorkspaceReads.Under(workspaceRoot ?? Paths.Workspace);
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
    ///
    /// <para><b>EVERY POSITION'S HOME, from this scanner's own database</b> (<c>U-org-principals</c>): the legacy pair
    /// and every position the chart holds, whatever its status. Not only the active ones: the whole group is marked
    /// missing against what this pass saw (<see cref="MaterialStore.MarkMissing"/>), so a dormant or ended position's
    /// home left out of the walk would have its files stamped removed while they lie on the disk — an invented
    /// deletion. An id that is not a position id has no home and is not walked: never mapped to another one.</para>
    /// </summary>
    public IEnumerable<(string Home, string Dir)> RolePaths() =>
        from position in CouncilRoles.All.Concat(new OrgStore(db).Positions().Select(p => p.Id))
            .Distinct(StringComparer.Ordinal).Where(CouncilRoles.IsPositionId).ToList()
        from dir in TrackedRoleDirs
        select (CouncilRoles.HomeDir(position), dir);

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

    /// <summary>
    /// FILES PER PASS. A pass that reaches it has not looked everywhere: it marks nothing missing and leaves the inbox's window where it
    /// was, and — the walk taking folders in the same order every pass — the files past it are not recorded until the tree is smaller.
    ///
    /// <para><b>20,000, from 5,000</b> (<c>U-perf-scan</c>, R28 G2): past 5,000 every pass was one that ran out, so removals went
    /// unrecorded and the window froze. The limit was set when each sighting was its own durable commit; with a pass's sightings in a
    /// handful (<see cref="SightingsPerWrite"/>), a pass over 20,000 unchanged files measured 1.0 s on the build Mac (2026-11-13, other
    /// builds running), off the loop that runs the trading pass.</para>
    /// </summary>
    public int FileLimit { get; init; } = 20_000;

    /// <summary>
    /// SIGHTINGS PER COMMIT (<c>U-perf-scan</c>): a pass records what it saw this many at a time, so 5,000 files are ten durable
    /// commits rather than 5,000, and no one commit holds the database's write lock for long.
    /// </summary>
    public const int SightingsPerWrite = 500;

    /// <summary>Directory depth. Deep trees are almost always something unpacked, not something handed over.</summary>
    public int DepthLimit { get; init; } = 12;

    /// <summary>Files hashed per pass, so one 4 GB drop cannot stall the pass that follows it.</summary>
    public int HashesPerPass { get; init; } = 24;

    /// <summary>
    /// BYTES HASHED PER PASS (<c>U-cards-2</c>): the sum of the sizes of the files a pass reads to hash. A file that would carry the
    /// pass past it waits for the next pass, where it is read first — so a 4 GB drop is worked through, never abandoned — and
    /// <see cref="HashesPerPass"/> alone bounded the COUNT: 24 files of 4 GB each, or one sparse terabyte, held the pass for as long as
    /// the reads took.
    /// </summary>
    public long HashBytesPerPass { get; init; } = 2L << 30;

    /// <summary>
    /// WALL TIME HASHING MAY TAKE PER PASS (<c>U-cards-2</c>), across every file and inside one: a read that has not finished when it
    /// runs out is stopped between reads, its row left unhashed and said so (<see cref="ScanResult.HashDeferrals"/>), and tried again
    /// next pass. A file too large to finish inside it is retried each pass and never hashed — said each time, never skipped silently.
    /// A third of the app's 30-second pass at most: the pass runs on the loop that also runs the trading pass.
    /// </summary>
    public TimeSpan HashTimePerPass { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many of a pass's deferrals its result names in words; the count is all of them.</summary>
    public const int DeferralsNamed = 10;

    /// <summary>
    /// THE HASH OF ONE OPEN FILE, cancellable between reads: <see cref="Sha256Hex.Of(Stream, CancellationToken)"/> in the product. A test
    /// passes one whose read the operating system holds and that no cancellation reaches, which is what a stalled disk is.
    /// </summary>
    internal Func<Stream, CancellationToken, string> Hasher { get; init; } = Sha256Hex.Of;

    /// <summary>
    /// HOW MUCH LONGER THAN ITS BOUND A HASH READ IS WAITED FOR (<c>U-perf-scan</c>): the bound is checked between reads, so a read that
    /// is merely slow returns within one more block; past this it is a read the disk is holding.
    /// </summary>
    static readonly TimeSpan StallGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// THE BYTES OF A FILE THE APP NAMES, read at most to a bound: <see cref="Sha256Hex.OfAtMost"/> in the product. A test passes one the
    /// operating system holds (<c>U-material-pass-2</c>), as it does for <see cref="Hasher"/>.
    /// </summary>
    internal Func<Stream, long, CancellationToken, string?> Measurer { get; init; } = Sha256Hex.OfAtMost;

    /// <summary>
    /// EACH WORKSPACE'S READS THAT DID NOT COME BACK, by workspace root and kind, process-wide because the disk is: each read left to finish
    /// on its own (and its file closed when it does). A hash read since <c>U-perf-scan</c> holds the hashing of that workspace; a listing, the
    /// stat of a file or the app-origin measure since <c>U-material-pass-2</c> (<see cref="Bounded{T}"/>) holds its walk, and the walk alone
    /// — one file whose bytes never come back must not stop new drops being recorded (the opus review). While one waits, no pass makes
    /// another read of that kind there: it says why and lets go of its exclusion, so a stalled disk costs a waiting read of each kind, never
    /// one more per pass.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Stalled = new(StringComparer.Ordinal);

    string HashKey => _root;
    string WalkKey => _root + "|walk";

    /// <summary>Whether a read of this kind an earlier pass left behind is still waiting on the disk; a finished one is forgotten.</summary>
    bool StillHeld(string key)
    {
        if (!Stalled.TryGetValue(key, out var waiting)) return false;
        if (!waiting.IsCompleted) return true;
        Stalled.TryRemove(new KeyValuePair<string, Task>(key, waiting));
        return false;
    }

    /// <summary>
    /// A READ LEFT WAITING, KEPT BESIDE ANY OTHER STILL WAITING (the opus review): a pass and the loop's inbox question can each leave one,
    /// and the second must not replace the first, or the hold would end while a read is still stuck. Its fault, a cancellation included, is
    /// observed here, so a read that ends by throwing is never reported later as an error the owner sees.
    /// </summary>
    static void Hold(string key, Task waiting)
    {
        var observed = waiting.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Stalled.AddOrUpdate(key, observed, (_, before) => before.IsCompleted ? observed : Task.WhenAll(before, observed));
    }

    /// <summary>
    /// THE PROGRESS OF ONE BOUNDED READ OF THE WALK (<c>U-material-pass-2</c>): bumped after every read of the disk, and told when the pass
    /// has left it, so a worker the pass gave up on stops at its next read and hands nothing back.
    /// </summary>
    sealed class ReadWatch
    {
        long _reads;
        volatile bool _left;
        public long Reads => Interlocked.Read(ref _reads);
        public void Read() => Interlocked.Increment(ref _reads);
        public bool Left => _left;
        public void Leave() => _left = true;
    }

    /// <summary>
    /// THE WALK'S READS OF THE DISK, BOUNDED AS THE HASH READ IS (<c>U-material-pass-2</c>; U-perf-scan's card 1). A listing, the stat of a
    /// file and the app-origin measure are reads the operating system can hold, and no cancellation reaches one it holds: they ran on the
    /// pass's own thread, so a disk that stopped answering held the pass — and with it the loop's one exclusion, so no lane was leased —
    /// for as long as the disk did.
    ///
    /// <para>Now they run on a thread of their own (not the pool's: a read queued behind a busy pool would be taken for a held one, CI run
    /// 43350995349), and the pass waits only while they make progress: one read that has not come back within
    /// <see cref="WorkspaceReads.StallAfter"/> of the one before is left to finish on its own, the workspace is held
    /// (<see cref="Stalled"/>), and the answer is null — a place this pass did not look, so it marks nothing missing and moves no window.</para>
    ///
    /// <para><b>The worker only reads.</b> Every row is written by the pass's own thread, under its exclusion, from what the worker
    /// handed back: a worker left behind never writes, so nothing is recorded once the pass has let a lane launch.</para>
    /// </summary>
    T? Bounded<T>(Func<ReadWatch, T> read, CancellationToken ct) where T : class
    {
        var watch = new ReadWatch();
        var worker = Task.Factory.StartNew(() => read(watch), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // A WORKER THAT ENDS BY THROWING WHILE NOBODY WAITS (a quit's cancellation, read at its next look) is observed, never left to
        // surface later as an unobserved exception (the opus review).
        _ = worker.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        long before = 0;
        try
        {
            while (!worker.Wait(_reads.StallAfter, ct))
            {
                var now = watch.Reads;
                if (now == before)
                {
                    watch.Leave();
                    Hold(WalkKey, worker);
                    return null;
                }
                before = now;
            }
        }
        catch (OperationCanceledException)
        {
            // A QUIT IS NOT HELD EITHER: the wait ends at the pass's own cancellation, a worker still reading is left as a stalled one is.
            if (!worker.IsCompleted) { watch.Leave(); Hold(WalkKey, worker); }
            throw;
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            throw;
        }
        return worker.Result;
    }

    /// <summary>One listed file, as the walk described it: what a sighting is made of, read off the pass's thread.</summary>
    sealed record Described(string Full, string Rel, long Size, DateTime ModifiedUtc);

    /// <summary>
    /// WHAT ONE GROUP'S WALK READ OF THE DISK (<c>U-material-pass-2</c>), handed back by the worker: the files it described, in the walk's
    /// order, and what the old inline walk set as it went — so the pass decides from it exactly as it did.
    /// </summary>
    sealed class GroupRead
    {
        public List<Described> Files { get; } = [];
        public List<string> Unreadable { get; } = [];
        public int Seen;
        public int Skipped;
        public bool Complete = true;
        public bool Truncated;
        public bool Read = true;
        public string? CutIn;
    }

    /// <summary>
    /// ONE GROUP'S READS: each place's look, its walk, and the stat of each file it listed, up to the pass's file budget — on the worker,
    /// writing nothing. <paramref name="seen"/> is the pass's count before this group, as the budget spans the groups.
    /// </summary>
    GroupRead ReadGroup((string Home, string Dir)[] paths, bool isInbox, IReadOnlySet<string> unsealedHomes, int seen, ReadWatch watch,
        CancellationToken ct)
    {
        var g = new GroupRead { Seen = seen };
        foreach (var (home, dir) in paths)
        {
            if (watch.Left) return g;
            if (!isInbox && unsealedHomes.Contains(home)) { g.Read = false; continue; }
            var full = Path.Combine(_root, home, dir);
            var look = LookAtFolder(full);
            watch.Read();
            switch (look)
            {
                case Look.NotThere: continue;
                case Look.CouldNotRead: g.Read = false; g.Unreadable.Add(Relative(full)); continue;
            }

            var (files, refused, stopped) = Walk(full, ref g.Skipped, ct, watch);
            foreach (var folder in refused) { g.Read = false; g.Unreadable.Add(Relative(folder)); }

            // A WALK THAT STOPPED COLLECTING AT THE BUDGET WITH FOLDERS LEFT UNVISITED did not look everywhere, however many files it
            // brought back (U-perf-scan): one that stopped on exactly the budget used to read as complete — the loop below never met
            // a file past it — and stamped every file in the folders it never listed removed, and shortened the inbox's window.
            if (stopped) { g.Complete = false; g.Truncated = true; g.CutIn ??= Relative(full); }

            foreach (var file in files)
            {
                if (g.Seen >= FileLimit) { g.Complete = false; g.Truncated = true; g.CutIn ??= Relative(full); break; }
                ct.ThrowIfCancellationRequested();
                if (watch.Left) return g;

                // A FILE THE LISTING NAMED AND THE DISK WILL NOT DESCRIBE is a place this pass could
                // not read, not a file that has gone: its row is not in `present`, so counting it gone
                // would stamp it removed. One that really has gone since the listing is skipped.
                var (fileLook, size, modifiedUtc) = LookAtFile(file);
                watch.Read();
                if (fileLook == Look.NotThere) continue;
                if (fileLook == Look.CouldNotRead) { g.Read = false; g.Unreadable.Add(Relative(Path.GetDirectoryName(file)!)); continue; }

                g.Files.Add(new Described(file, Relative(file), size, modifiedUtc));
                g.Seen++;
            }

            if (!g.Complete) break;
        }
        return g;
    }

    public ScanResult Scan(CancellationToken ct = default, IReadOnlySet<string>? unsealed = null)
    {
        var now = _now();
        int seen = 0, added = 0, removed = 0, skipped = 0;
        var addedBy = new Dictionary<MaterialOrigin, int>();
        var truncated = false;

        // THE TRACKED FOLDER THE FILE BUDGET RAN OUT IN, relative to the workspace (U-material-pass-2): the owner is told where a pass
        // stopped looking (ScanResult.CutIn). And whether a read of the disk did not come back, this pass's or one an earlier pass
        // left waiting: then this pass looks nowhere more, marks nothing missing and moves no window (Bounded).
        string? cutIn = null;
        var walkStalled = StillHeld(WalkKey);

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

        // THE HOMES OF THE LANES TURNING BESIDE THIS PASS UNSEALED (U-org-lanes): a confined lane whose cell cannot write the
        // inbox does not stop a pass, and its home is not read until its turn has committed — what is half written there is
        // not yet what the lane delivered. A home skipped is a place this pass did not look, so its group marks nothing
        // missing; it is not unreadable, and it does not hold the inbox's window, which that lane cannot write into.
        var unsealedHomes = (unsealed ?? new HashSet<string>()).Select(CouncilRoles.HomeDir).ToHashSet(StringComparer.Ordinal);

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
            var isInbox = origins[0] == MaterialOrigin.Inbox;

            // THE GROUP'S SIGHTINGS, RECORDED A CHUNK AT A TIME (U-perf-scan, R28 G2): one commit per chunk, never one per file. Each
            // is recorded as Observe records it, with its origin asked only for a row about to be written, at the write — after the
            // file's bytes were read (WeWroteIt), never under the lock while they are.
            var pending = new List<(MaterialStore.Sighting Sighting, MaterialOrigin?[] Written)>();
            void Record()
            {
                if (pending.Count == 0) return;
                var recorded = _store.ObserveAll([.. pending.Select(p => p.Sighting)], now);
                for (var i = 0; i < recorded.Count; i++)
                {
                    var (isNew, id) = recorded[i];
                    present.Add(id);
                    if (!isNew) continue;
                    added++;
                    if (pending[i].Written[0] is { } word) addedBy[word] = addedBy.GetValueOrDefault(word) + 1;
                }
                pending.Clear();
            }

            // THE GROUP'S READS OF THE DISK, ON A WORKER AND BOUNDED (U-material-pass-2): every place's look, its walk and the stat of
            // each listed file. A read the disk holds past the bound ends the walk here — this group and every one after it are places
            // this pass did not look, and the workspace is held until that read comes back (Bounded).
            // ASKED AGAIN FOR EACH GROUP: the loop's inbox question may have left a read waiting since this pass began (the opus review).
            var looked = walkStalled || StillHeld(WalkKey) ? null
                : Bounded(watch => ReadGroup(paths, isInbox, unsealedHomes, seen, watch, ct), ct);
            if (looked is null) { walkStalled = true; break; }

            seen = looked.Seen;
            skipped += looked.Skipped;
            unreadable.UnionWith(looked.Unreadable);
            if (looked.Truncated) truncated = true;
            cutIn ??= looked.CutIn;
            var complete = looked.Complete;

            // EVERY PLACE IN THIS GROUP COULD BE READ. A folder the disk would not list holds files this
            // pass never saw, and "I could not look" is not "it is gone" — the reason a pass that ran out
            // of budget marks nothing missing, reached from the other side.
            var read = looked.Read;

            // A ROLE'S HOME IS A DIFFERENT QUESTION FROM THE INBOX'S, and it is measured rather than assumed:
            // the app writes down every file IT puts in a home, and a file whose path and
            // bytes both match that record is the app's. Anything else in there is the
            // agent's, which is what this used to say about all of it — including the
            // language reference the app writes and the briefs the relay delivers.
            //
            // AND IT IS MEASURED HERE, OUTSIDE THE DATABASE LOCK (U-exec-env-fix item 1): an executor's cell writes its whole
            // home, so the bytes at a name the app wrote may be a sparse terabyte, and a read inside Observe's write lock held
            // every reader and writer of the database for its length. Only for a sighting no live row has — the one Observe
            // would write — and bounded and cancellable (WeWroteIt); a word left unmeasured is the agent's. ON THE WORKER TOO
            // (U-material-pass-2): it is a read of the disk, and one the disk holds ends the walk as a held listing does.
            var measured = new MaterialOrigin?[looked.Files.Count];
            if (!isInbox)
            {
                var ask = Enumerable.Range(0, looked.Files.Count)
                    .Where(i => looked.Files[i] is var f && _appFiles.Names(f.Rel)
                                && !_store.IsLive(f.Rel, f.Size, new DateTimeOffset(f.ModifiedUtc, TimeSpan.Zero)))
                    .ToList();
                if (ask.Count > 0)
                {
                    var answers = Bounded(watch => ask.Select(i =>
                    {
                        if (watch.Left) return false;
                        var ours = WeWroteIt(looked.Files[i].Rel, looked.Files[i].Size, ct);
                        watch.Read();
                        return ours;
                    }).ToArray(), ct);
                    if (answers is null) { walkStalled = true; break; }
                    for (var k = 0; k < ask.Count; k++) measured[ask[k]] = answers[k] ? MaterialOrigin.App : MaterialOrigin.Agent;
                }
            }

            for (var i = 0; i < looked.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = looked.Files[i];
                // ONE QUESTION PER GROUP, ASKED PER SIGHTING and only for a row that is about to
                // be written (see the Func overload of Observe).
                //
                // The inbox's is attestation: an agent that starts while this walk is running
                // must not be attested away by a question asked before it existed, and asking
                // later can only make the answer stricter.
                var word = measured[i];
                Func<MaterialOrigin> origin = isInbox
                    ? () => _noAgentSince(since) ? MaterialOrigin.Inbox : MaterialOrigin.InboxUnattested
                    : () => word ?? MaterialOrigin.Agent;

                // THE WORD THE NEW ROW WENT IN WITH, kept as the store is answered so the pass
                // can say what ARRIVED (ScanResult.Arrived) without asking the question twice.
                // The store asks only for a row it is about to write, so this is set exactly
                // when `isNew` is, and nothing about the row changes by being counted.
                var written = new MaterialOrigin?[1];
                pending.Add((new MaterialStore.Sighting(file.Rel, () => (written[0] = origin()).Value, file.Size,
                    new DateTimeOffset(file.ModifiedUtc, TimeSpan.Zero), RunnableExts.Contains(Path.GetExtension(file.Full))), written));
                if (pending.Count >= SightingsPerWrite) Record();
            }

            // EVERY SIGHTING IS RECORDED BEFORE THE GROUP IS SWEPT: MarkMissing keeps only the ids in `present`.
            Record();

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
            //
            // A group whose walk did not come back (U-material-pass-2) never reaches here: it broke out above with nothing recorded.
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
        //
        // A READ THE DISK DID NOT RETURN (U-material-pass-2) is a place this pass did not look: the window stays where it was.
        if (!truncated && unreadable.Count == 0 && !walkStalled)
            db.Write(_ =>
            {
                db.SetKv(LastScanKey, Sql.T(now));
                if (mark is { } taken) db.SetKv(LastMarkKey, taken.ToString());
                return 0;
            });

        var (hashed, deferred, deferrals, stalled) = HashPending(ct, unsealedHomes);
        return new ScanResult(seen, added, hashed, removed, skipped, truncated)
        {
            HashDeferred = deferred,
            HashDeferrals = deferrals,
            HashStalled = stalled,
            WalkStalled = walkStalled,
            CutIn = truncated ? cutIn : null,
            FileLimit = FileLimit,
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
    ///
    /// <para><b>Read through <see cref="HomeFiles"/>, never by the path</b> (<c>U-execution-environment</c> fix pass 5, the fourth
    /// re-review's D2): it is only ever asked of a position's home, which an executor's cell writes, and a name the walk described as a
    /// file may be a link by now. Opened link-free and as a file with one name, a link — to a copy of the app's bytes outside the home,
    /// to <c>/dev/zero</c>, to a FIFO — is refused before a byte is read, and the name is the agent's.</para>
    ///
    /// <para><b>Bounded, cancellable, and never under the database lock</b> (<c>U-exec-env-fix</c> item 1, the Codex second pass): the
    /// cell can put a sparse terabyte at the app's name. A size past <see cref="AppFileManifest.MaxMeasuredBytes"/> — as the walk saw it or
    /// as the open file says — is not the app's and is not read; one the open file disagrees with the walk on is not the file the row will
    /// describe; and the read stops at the bound and at <paramref name="ct"/>. The caller asks it before <see cref="MaterialStore.Observe(string, Func{MaterialOrigin}, long, DateTimeOffset, bool, DateTimeOffset)"/>,
    /// never inside it.</para>
    /// </summary>
    bool WeWroteIt(string rel, long size, CancellationToken ct)
    {
        if (!_appFiles.Names(rel) || size > AppFileManifest.MaxMeasuredBytes) return false;
        try
        {
            using var fs = new FileStream(HomeFiles.OpenRead(_root, rel), FileAccess.Read);
            if (fs.Length != size) return false;
            return _appFiles.Wrote(rel, Measurer(fs, AppFileManifest.MaxMeasuredBytes, ct));
        }
        catch (IOException) { return false; }               // refused, gone or being written: not measured as the app's
        catch (UnauthorizedAccessException) { return false; }
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
    (int Hashed, int Deferred, IReadOnlyList<string> Deferrals, bool Stalled) HashPending(CancellationToken ct, IReadOnlySet<string> unsealedHomes)
    {
        var hashed = 0;

        // BOUNDED IN BYTES AND IN WALL TIME (U-cards-2): the batch comes smallest first (NeedingHash), so the small files of a drop are
        // not held behind its large one, a file no pass can finish never holds a batch's places against the files behind it, and the
        // large one is first in a pass once the smaller are done. Whatever this pass does not reach or finish is DEFERRED, counted and
        // said in words, and read again next pass.
        var clock = Stopwatch.StartNew();
        long spent = 0;
        var deferred = 0;
        var deferrals = new List<string>();
        void Defer(Material m, string why)
        {
            deferred++;
            if (deferrals.Count < DeferralsNamed)
                deferrals.Add($"{m.RelPath} ({m.SizeBytes:N0} bytes) was not hashed this pass: {why} It is tried again next pass.");
        }

        // A READ AN EARLIER PASS LEFT WAITING ON THE DISK (U-perf-scan) holds every read of this workspace until it has come back: a disk
        // that stopped answering would otherwise take one more waiting read, and one more thread, every pass.
        var held = StillHeld(HashKey);

        var batch = _store.NeedingHash(HashesPerPass);
        foreach (var m in batch)
        {
            ct.ThrowIfCancellationRequested();

            // NOR IS A FILE IN AN UNSEALED HOME OPENED TO BE HASHED (U-org-lanes, review 2): the lane writing it has not committed.
            if (unsealedHomes.Any(h => m.RelPath.StartsWith(h + "/", StringComparison.Ordinal))) continue;

            if (held)
            {
                Defer(m, "a read of a file here did not come back from the disk, and nothing here is read until it has.");
                continue;
            }

            var left = HashTimePerPass - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                Defer(m, $"this pass's {HashTimePerPass.TotalSeconds:0.#} s for hashing was spent.");
                continue;
            }
            if (spent > 0 && spent + m.SizeBytes > HashBytesPerPass)
            {
                Defer(m, $"this pass's {HashBytesPerPass:N0} bytes for hashing would be passed; a file read first in a pass is read whole.");
                continue;
            }

            var full = Path.Combine(_root, m.RelPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                // A POSITION'S HOME IS OPENED WITHOUT FOLLOWING A LINK AT ANY COMPONENT (U-execution-environment fix pass 4, the second
                // re-review's [P2] 4): an executor's cell writes its whole home, so a name the walk measured may be a link by now, and the
                // bytes hashed onto its row would be another file's. The owner's inbox, which no cell writes, is opened as it always was.
                var fs = IsInInbox(m.RelPath)
                    ? File.Open(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                    : new FileStream(HomeFiles.OpenRead(_root, m.RelPath, oneName: false), FileAccess.Read);
                CancellationTokenSource? bound = null;
                var leftReading = false;
                try
                {
                    if (!IsStill(fs, m)) continue;

                    // CANCELLABLE BETWEEN READS (U-exec-env-fix item 1): a cell can leave a sparse terabyte at any name in its home, and a
                    // pass that is asked to stop stops here too rather than at the end of the file. The same cut-off is the pass's wall time.
                    spent += m.SizeBytes;
                    bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bound.CancelAfter(left);
                    var (file, token) = (fs, bound.Token);

                    // AND WAITED FOR, NOT MADE, HERE (U-perf-scan; U-cards-2's card): a read the operating system holds is not between
                    // reads, and no cancellation reaches it. The pass waits as long as its bound and a moment more, then leaves the read to
                    // finish on its own — its file and its bound closed only once it has, never under it — and goes on.
                    // ON A THREAD OF ITS OWN, not the pool's: a read queued behind a busy pool would wait out the bound unstarted and be
                    // taken for one the disk holds (CI run 43350995349, ubuntu: a 20-byte file left unhashed beside the parallel suite).
                    var reading = Task.Factory.StartNew(() => Hasher(file, token), CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    // A QUIT IS NOT HELD EITHER: the wait ends at the pass's own cancellation, the read is left as a stalled one is, and
                    // the cancellation goes on up.
                    bool back;
                    try { back = reading.Wait(left + StallGrace, ct); }
                    catch (AggregateException) { back = true; }
                    catch (OperationCanceledException)
                    {
                        if (reading.IsCompleted) throw;
                        leftReading = true;
                        LeaveReading(reading, file, bound);
                        throw;
                    }
                    if (!back)
                    {
                        leftReading = true;
                        LeaveReading(reading, file, bound);
                        held = true;
                        Defer(m, $"a read of it did not come back from the disk within the {HashTimePerPass.TotalSeconds:0.#} s a pass has "
                                 + "for hashing; it is left to finish, and nothing here is read until it has.");
                        continue;
                    }

                    string digest;
                    try { digest = reading.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Defer(m, $"it did not finish within the {HashTimePerPass.TotalSeconds:0.#} s a pass has for hashing.");
                        continue;
                    }
                    if (!IsStill(fs, m)) continue;

                    _store.SetHash(m.Id, digest);
                    hashed++;
                }
                finally
                {
                    if (!leftReading) { fs.Dispose(); bound?.Dispose(); }
                }
            }
            catch (IOException) { }                 // still being written, or gone — the next pass retries
            catch (UnauthorizedAccessException) { }
        }

        // AND WHAT THE BATCH DID NOT REACH, when this pass deferred at all (Codex review): a pass held up by what it could not finish
        // leaves the rows behind its batch waiting, and those are said too. A pass that deferred nothing stays silent: HashesPerPass is
        // then only its pace, and the next pass takes them.
        if (deferred > 0)
        {
            var past = _store.CountNeedingHash() - (batch.Count - hashed);
            if (past > 0)
            {
                deferred += past;
                deferrals.Add($"{past:N0} more file{(past == 1 ? "" : "s")} waiting to be hashed {(past == 1 ? "was" : "were")} not reached this pass. They are tried again next pass, smallest first.");
            }
        }

        return (hashed, deferred, deferrals, held);
    }

    /// <summary>A read that did not come back, left to finish on its own: its file and its bound are closed once it has, never under it.</summary>
    void LeaveReading(Task reading, FileStream file, CancellationTokenSource bound) =>
        Hold(HashKey, reading.ContinueWith(_ => { file.Dispose(); bound.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default));

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
        // A READ OF THIS WORKSPACE THE DISK HAS NOT RETURNED (U-material-pass-2) answers yes without another: the loop asks this before
        // every tick, and a listing held here held the dispatcher — no lane was leased while the disk stalled. Yes costs one pass, which
        // the same hold turns into a pass that reads nothing and lets go at once.
        if (StillHeld(WalkKey)) return true;

        var recorded = _store.Live([MaterialOrigin.Inbox, MaterialOrigin.InboxUnattested]);
        var inbox = Path.Combine(_root, InboxDir);
        var answer = Bounded<object>(watch =>
        {
            var look = LookAtFolder(inbox);
            watch.Read();
            switch (look)
            {
                case Look.NotThere: return false;
                case Look.CouldNotRead: return true;
            }

            var skipped = 0;
            var (files, refused, _) = Walk(inbox, ref skipped, ct, watch);
            if (refused.Count > 0 || files.Count >= FileLimit) return true;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (watch.Left) return true;

                var (fileLook, size, modifiedUtc) = LookAtFile(file);
                watch.Read();
                if (fileLook == Look.NotThere) continue;
                if (fileLook == Look.CouldNotRead) return true;

                var tuple = (Relative(file), size, Sql.T(new DateTimeOffset(modifiedUtc, TimeSpan.Zero)));
                if (!recorded.Contains(tuple)) return true;
            }
            return false;
        }, ct);
        return answer is not bool known || known;
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
    /// <returns>And whether it STOPPED at the budget with a folder left unlisted (<c>U-perf-scan</c>): a walk that did saw part of the
    /// tree whatever its count.</returns>
    (List<string> Files, List<string> Unreadable, bool Stopped) Walk(string dir, ref int skipped, CancellationToken ct, ReadWatch watch)
    {
        // Recursion is written out rather than using EnumerateFiles(SearchOption.AllDirectories)
        // because that overload cannot skip a subtree — it would walk every node_modules it found
        // and only then let us discard the results.
        var files = new List<string>();
        var unreadable = new List<string>();
        var stopped = false;
        Collect(dir, 0, files, ref skipped, unreadable, ref stopped, ct, watch);
        return (files, unreadable, stopped);
    }

    /// <param name="skipped">Folders left out ON PURPOSE — noise, dot-folders, past the depth limit —
    /// on every pass alike, so nothing in them is ever recorded and they hold nothing open.</param>
    /// <param name="unreadable">
    /// Folders the disk would not list, kept apart from the ones skipped on purpose: they hold files a
    /// pass did not see, so <see cref="Scan"/> is incomplete and <see cref="InboxHoldsUnrecorded"/>
    /// answers yes for them, and for the skipped ones neither.
    /// </param>
    /// <param name="stopped">Set when this walk stops at the budget with a folder still to list.</param>
    void Collect(string dir, int depth, List<string> into, ref int skipped, List<string> unreadable, ref bool stopped, CancellationToken ct,
        ReadWatch watch)
    {
        if (depth > DepthLimit) { skipped++; return; }
        ct.ThrowIfCancellationRequested();
        if (watch.Left) return;   // the pass has gone on without this walk: what it lists now is handed to nobody

        string[] entries, subs;
        try
        {
            (entries, subs) = _reads.List(dir);
        }
        catch (IOException) { unreadable.Add(dir); watch.Read(); return; }
        catch (UnauthorizedAccessException) { unreadable.Add(dir); watch.Read(); return; }
        watch.Read();

        into.AddRange(entries);
        // Stop collecting, not just stop consuming. Enumerating a hundred thousand paths into a list
        // and then discarding all but the first few thousand is the same amount of walking.
        if (into.Count >= FileLimit) { stopped |= subs.Length > 0; return; }

        for (var i = 0; i < subs.Length; i++)
        {
            var name = Path.GetFileName(subs[i]);
            if (NoiseDirs.Contains(name) || name.StartsWith('.')) { skipped++; continue; }
            Collect(subs[i], depth + 1, into, ref skipped, unreadable, ref stopped, ct, watch);
            if (into.Count >= FileLimit) { stopped |= i < subs.Length - 1; return; }
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
    /// <summary>
    /// HOW LONG ONE OF THESE READS MAY GO WITHOUT COMING BACK before the pass takes it for one the disk is holding (<c>U-material-pass-2</c>):
    /// it is then left to finish on its own, and the pass goes on without that place. Ten seconds: one listing or one stat, where a pass
    /// over 20,000 unchanged files took about a second (U-perf-scan); a test's disk that never answers passes its own.
    /// </summary>
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The machine's own disk, every path read as the disk reads it — links followed. The owner's inbox only, in the product
    /// (<see cref="Under"/>); internal so that no product code can hand a pass a home read this way (fix pass 4: the app once did).
    /// </summary>
    internal static WorkspaceReads Disk { get; } = new(
        FolderOnDisk,
        dir => (Directory.GetFiles(dir), Directory.GetDirectories(dir)),
        FileOnDisk);

    /// <summary>
    /// THE MACHINE'S OWN DISK UNDER ONE WORKSPACE, as a pass reads it (<c>U-execution-environment</c> fix pass 4, the second re-review's
    /// [P2] 4): the owner's inbox as <see cref="Disk"/> reads it, and every position's home through <see cref="HomeFiles"/> — each
    /// component opened from the one above it without following a link, and each name examined where it is. A link in a home is neither
    /// walked into nor measured: an executor's cell writes its whole home, and a link it aimed at a seat's login would otherwise be
    /// recorded in <c>material</c> as the position's own file.
    /// </summary>
    public static WorkspaceReads Under(string root) => new(
        dir => InHome(root, dir) is { } rel ? HomeFiles.Describe(root, rel) is { Folder: true, Link: false } : FolderOnDisk(dir),
        dir => InHome(root, dir) is { } rel ? ListHome(root, rel, dir) : Disk.List(dir),
        file => InHome(root, file) is { } rel
            ? HomeFiles.Describe(root, rel) is { Folder: false, Link: false } e ? (e.Length, e.WrittenUtc) : null
            : FileOnDisk(file));

    /// <summary>One folder in a home, listed link-free: a link is neither a file nor a folder of the pass's.</summary>
    static (string[] Files, string[] Folders) ListHome(string root, string rel, string dir)
    {
        var seen = HomeFiles.List(root, rel);
        return ([.. seen.Where(e => !e.Folder && !e.Link).Select(e => Path.Combine(dir, e.Name))],
                [.. seen.Where(e => e.Folder && !e.Link).Select(e => Path.Combine(dir, e.Name))]);
    }

    /// <summary>The path relative to <paramref name="root"/> when it is in a position's home, or null for the owner's inbox.</summary>
    static string? InHome(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return MaterialScanner.IsInInbox(rel) ? null : rel;
    }

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
