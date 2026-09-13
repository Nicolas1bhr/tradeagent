using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE APP CARRYING WORK BETWEEN THE TWO ROLES, AND THE ONE PLACE THAT MAY.
///
/// <para>A role writes a file into its own <c>out/</c>. Nothing else it can do reaches the other
/// role: it has no command that publishes, no command that delivers, and no command that creates a
/// task. This class reads the file, validates it, publishes it as an immutable artifact, commits the
/// delivery and the recipient's one paid task in a single transaction, and only then copies the text
/// into the recipient's <c>in/</c>.</para>
///
/// <para><b>The property, from <c>docs/COUNCIL.md</c>:</b> a crash at each handoff boundary recovers
/// to exactly one committed report-to-Operations task, neither lost nor doubled. Three boundaries
/// exist — after the file is written, after the transaction, after the copy — and the recovery at
/// each is the same three lines of code, run again:</para>
/// <list type="bullet">
///   <item>after the file: nothing is in the tables, so the next pass reads the file and publishes.</item>
///   <item>after the transaction: the publication, the delivery and the task are there and the file
///     is not, so the next pass re-copies from <see cref="Publication.Content"/> — the app's own
///     copy, because the agent's <c>out/</c> file may be gone by then.</item>
///   <item>after the copy: the delivery is still <c>committed</c>, the file is already there, and
///     the next pass writes it again (identical bytes) and marks it delivered.</item>
/// </list>
///
/// <para>What makes all three converge on ONE is that the artifact's identity is a hash of its
/// content (<see cref="Publication.IdOf"/>), so republishing is a no-op the primary key absorbs, and
/// the task's id is keyed by that publication. An identity taken from the attempt would make each of
/// those recoveries a SECOND publication, a second file and a second paid turn.</para>
///
/// <para><b>Validation is the app's and the limit is real.</b> A report longer than
/// <see cref="ReportLines"/> lines, or an agenda longer than <see cref="AgendaLines"/>, is rejected:
/// nothing is published, and the last valid publication stands. That is <c>docs/COUNCIL.md</c>'s
/// "an invalid publication is rejected and the last valid plan and index stand" — the app's size
/// budget, enforced at publication rather than asked for in a prompt.</para>
///
/// <para><b>A FILE IS ATTRIBUTED BY THE ATTEMPT ITS NAME CARRIES, NEVER BY WHICHEVER TURN RAN THE
/// PASS.</b> The Situation names the launch id; the agent writes <c>out/report-&lt;attempt&gt;.md</c>;
/// this class reads the id back out of the name and publishes under it. Attribution by "whoever ran
/// the relay" — what this used to do — names the wrong launch whenever the turn that wrote the file
/// was killed before its own pass: the report is published later, under the NEXT turn's id, or under
/// none at all. Provenance that can name the wrong launch is provenance nobody can use, and round 4
/// of <c>docs/COUNCIL.md</c> lists "which attempt produced an artifact" among the things that cannot
/// be recovered afterwards.</para>
///
/// <para><b>The fence.</b> A file whose name carries no id this app can find in <c>ai_attempt</c>,
/// or one naming another role's launch, is MOVED to <c>out/quarantine/</c> and never published. That
/// is what stops a stale process — one the app believes is gone — from dropping a file the relay
/// then publishes as the work of a live turn. Quarantine keeps the bytes: the file is still on disk,
/// still in the role's own folder, and <see cref="TradeAgent.Core.MaterialScanner"/> records it.</para>
/// </summary>
public sealed class CouncilRelay
{
    /// <summary>A report is at most this many lines. The context-etiquette budget, enforced.</summary>
    public const int ReportLines = 20;

    /// <summary>An agenda is at most this many lines — a brief, in the build doctrine's sense.</summary>
    public const int AgendaLines = 40;

    /// <summary>
    /// The most publications one pass will take from one role's <c>out/</c>. A role that filled its
    /// folder with a thousand files would otherwise buy a thousand paid turns for the other one.
    /// </summary>
    public const int PerPass = 10;

    /// <summary>
    /// WHERE A FILE THE FENCE REFUSED GOES. Under the role's own <c>out/</c>, so nothing leaves the
    /// folder it was written in and nothing is deleted — the bytes are evidence, and an app that
    /// destroyed what it refused would be the one party able to erase the record of the refusal.
    /// </summary>
    public const string QuarantineDir = "quarantine";

    readonly Database _db;
    readonly PublicationStore _store;
    readonly CouncilBoundaries _boundaries;
    readonly MissionEventStore _events;
    readonly AiAttemptStore _attempts;
    readonly WorkspaceRevisions _revisions;
    readonly Func<string, string> _homeOf;
    readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// THE LAUNCHES THIS PROCESS IS STILL FLYING, which the fence needs to tell a turn that is
    /// writing its report RIGHT NOW from a file dropped by a process the app is not accounting for.
    /// Both are rows still LAUNCHED that are not this pass's own; only this says which is which.
    /// </summary>
    readonly LiveAttempts _live;

    public CouncilRelay(Database db, Func<string, string> homeOf, Func<DateTimeOffset>? now = null,
        LiveAttempts? live = null, CouncilBoundaries? boundaries = null)
    {
        _db = db;
        _live = live ?? LiveAttempts.Shared;
        _store = new PublicationStore(db);
        _boundaries = boundaries ?? new CouncilBoundaries(db);
        _events = new MissionEventStore(db);
        _attempts = new AiAttemptStore(db);
        _revisions = new WorkspaceRevisions(db, homeOf, now);
        _homeOf = homeOf;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// The role's own memory, versioned at the end of its turn. Composed here rather than beside
    /// this class because it is the same pass, the same transaction and the same size-budget rule —
    /// <c>docs/COUNCIL.md</c>'s "an invalid publication is rejected and the last valid plan stands"
    /// is one sentence covering both halves.
    /// </summary>
    public WorkspaceRevisions Revisions => _revisions;

    /// <summary>
    /// A SEAM FOR THE PROPERTY TESTS, AND FOR NOTHING ELSE. Called at each boundary with its name;
    /// a test throws from it to simulate the app dying exactly there. Null in the product, where the
    /// boundaries are just consecutive statements.
    ///
    /// The names are <c>file</c> and <c>transaction</c> inside a publication, and
    /// <see cref="BeforeCommit"/> and <see cref="AfterCommit"/> either side of the turn's own
    /// commit.
    /// </summary>
    internal Action<string>? Boundary { get; set; }

    /// <summary>The instant before the turn's transaction opens. Nothing of the turn is committed.</summary>
    internal const string BeforeCommit = "before-commit";

    /// <summary>The instant after it commits, and before anything is copied onto disk.</summary>
    internal const string AfterCommit = "after-commit";

    /// <summary>
    /// The instant before one staged file is read off disk, with NO transaction open. A test holds
    /// the pass here to show that the other role's launch record is not waiting behind this read.
    /// </summary>
    internal const string Reading = "read";

    /// <summary>
    /// THE TURN'S ONE COMMITTED TRANSITION — <c>docs/COUNCIL.md</c> rule 6, "one committed
    /// transition"; the <c>U-turn-commit</c> line's property.
    ///
    /// <para>Four things happen to the database at the end of a turn: the launch record is CLOSED
    /// (<paramref name="endAttempt"/>), what the turn staged in <c>out/</c> is PUBLISHED, its two
    /// memory files are SNAPSHOTTED, and the wakes it consumed get their DISPOSITIONS
    /// (<paramref name="dispositions"/>). They used to be three separate commits in a row, with two
    /// windows a crash could land in: an attempt recorded as ENDED whose work is nowhere, or work
    /// published against an attempt that never ended and whose wakes are still owed an answer. Here
    /// they are one <see cref="Database.Write"/>, so the turn either happened or did not.</para>
    ///
    /// <para><b>What a crash before the commit leaves is a LAUNCHED row</b>, which the next meter to
    /// open the database turns LOST with its reservation as its cost — and the reconcile pass then
    /// publishes that attempt's staged files under it, because a file's provenance is the launch its
    /// NAME carries. Nothing is lost and nothing is doubled: the publication's id is the hash of its
    /// content, so re-publishing is a row the primary key already holds.</para>
    ///
    /// <para><b>The disk work is outside the transaction and after it</b>, for the reason the relay
    /// has always had: a rollback cannot un-write a file. The copy into the recipient's <c>in/</c>
    /// and the restore of a refused plan are both idempotent and both re-made on the next pass.</para>
    /// </summary>
    public void CommitTurn(string role, string? attempt, Action endAttempt, Action dispositions)
    {
        // READ FIRST, OUTSIDE THE TRANSACTION. What this turn staged in `out/` is on disk and the
        // database has one lock: reading the files inside the commit held that lock across every
        // byte of them, and the other role could not open its own launch record until this pass had
        // finished reading. See Read.
        var pass = Read(role, attempt);

        Boundary?.Invoke(BeforeCommit);

        var restores = _db.Write(_ =>
        {
            endAttempt();
            Land(pass);
            var refused = _revisions.Snapshot(role, attempt);
            dispositions();
            return refused;
        });

        Boundary?.Invoke(AfterCommit);

        Apply(pass);
        Deliver();
        WorkspaceRevisions.Apply(restores);
    }

    /// <summary>What a rejected file was, so the app can say so once. Never throws out of the relay.</summary>
    public Action<string>? Rejected { get; set; }

    /// <summary>
    /// WHAT THE FENCE REFUSED, so the owner reads one line saying a file naming no launch of theirs
    /// turned up in a role's <c>out/</c>. Separate from <see cref="Rejected"/> because the two are
    /// different events: a rejected file is one the role wrote too long, and a quarantined one is a
    /// file the app cannot attribute to any turn it launched. Never throws out of the relay.
    /// </summary>
    public Action<string>? Quarantined { get; set; }

    /// <summary>
    /// ONE ROLE'S OWN PASS: what that turn left in its <c>out/</c>, published, and its memory
    /// versioned. Run after every turn, by the turn that just ended.
    ///
    /// <para><b>ONE ROLE, AND ONLY THAT ROLE'S FOLDER.</b> It used to walk every role when it was
    /// given none, which was harmless while one turn ran at a time and is not now: a pass that walks
    /// the OTHER role's <c>out/</c> while that role is mid-turn reads a file being written for a
    /// launch that is still open, and the fence — which cannot tell that from a stale process's
    /// drop — moves it to <c>quarantine/</c>. The turn's own report, taken away from it by the other
    /// role's bookkeeping. The whole-council pass is <see cref="Reconcile"/>, on start.</para>
    ///
    /// <paramref name="attempt"/> IS NOT WHAT ATTRIBUTES A FILE — the id in the file's name is — but
    /// it is still what the fence needs: it says which launch this pass belongs to, so a file naming
    /// a DIFFERENT launch that is still open can be told apart from this turn's own output.
    /// </summary>
    public void Run(string role, string? attempt = null)
    {
        Publish(role, attempt);

        // THE ROLE'S OWN MEMORY, RECONCILED WITH THE REST. An unchanged file raises no new revision,
        // because the publication's id is the hash of what is in it.
        var restores = _revisions.Snapshot(role, attempt);

        Deliver();
        WorkspaceRevisions.Apply(restores);
    }

    /// <summary>
    /// ONE FULL RECONCILIATION OF DISK AGAINST THE TABLES, ACROSS EVERY ROLE — the start-up pass,
    /// and the only one that looks in a folder that is not the turning role's.
    ///
    /// <para>It names no launch, and on start it needs none: nothing in this process is flying, the
    /// meter that opened the database has already turned every row it found open LOST, and a LOST
    /// attempt's staged file is exactly the one this pass exists to publish.</para>
    ///
    /// <para><b>And if it is ever called while a turn IS running, it leaves that turn's file where
    /// it is</b> rather than quarantining it — see <see cref="Verdict"/>. That is the difference
    /// between a fence that protects the record and one that takes a live turn's work away because
    /// of when a pass happened to run.</para>
    ///
    /// <para>The role's own memory is versioned here too: a plan the app has neither recorded nor
    /// refused is one nobody can afterwards say the role held, and the next turn reads it first.</para>
    /// </summary>
    public void Reconcile()
    {
        var restores = new List<WorkspaceRevisions.Restore>();

        foreach (var role in CouncilRoles.All)
        {
            Publish(role, attempt: null);
            restores.AddRange(_revisions.Snapshot(role, attempt: null));
        }

        Deliver();
        WorkspaceRevisions.Apply(restores);
    }

    /// <summary>
    /// Publishes everything valid in one role's <c>out/</c> that is not published already. "Already"
    /// is decided by the primary key inside the transaction rather than by a read here: a
    /// read-then-write would be a race, and the hash is the answer.
    /// </summary>
    internal void Publish(string role, string? attempt)
    {
        var pass = Read(role, attempt);
        Land(pass);
        Apply(pass);
    }

    /// <summary>
    /// WHAT ONE PASS FOUND ON DISK AND WHAT IT DECIDED, before anything is written down. The three
    /// lists are the three outcomes: publish it, move it aside, or say it was too long.
    /// </summary>
    internal sealed class Pass
    {
        public List<(Publication Publication, string Kind)> Publishing { get; } = [];
        public List<(string File, string OutDir, string Role, string? Named)> Quarantining { get; } = [];
        public List<string> Rejecting { get; } = [];
    }

    /// <summary>
    /// EVERYTHING THE DISK CAN SAY, READ BEFORE ANY TRANSACTION IS OPEN.
    ///
    /// <para><b>Why the reads are here and not in <see cref="Land"/>.</b> There is ONE lock over the
    /// database and it is held for the whole of a <see cref="Database.Write"/>. Reading a role's
    /// <c>out/</c> inside the turn's commit therefore held that lock across an unbounded amount of
    /// disk I/O — up to <see cref="PerPass"/> files of whatever size the agent wrote — and the other
    /// role could not so much as open its launch record while it went on. Two roles' turns may
    /// overlap now, so that is one role's bookkeeping standing on the other role's turn.</para>
    ///
    /// <para>The ledger lookups the fence makes are still reads, and they are still short; what is
    /// unbounded is the file contents, and those are what moved out.</para>
    /// </summary>
    internal Pass Read(string role, string? attempt)
    {
        var pass = new Pass();
        var outDir = Path.Combine(_homeOf(role), WorkspaceBuilder.OutDir);

        List<(string File, string Kind)> files;
        try
        {
            if (!Directory.Exists(outDir)) return pass;
            // Top level only, which is what keeps `out/quarantine/` out of the pass: a file the
            // fence has already refused must not be read again on the next turn, for ever.
            //
            // EVERY KIND THIS ROLE MAY PUBLISH, and the cap is over the whole pass rather than per
            // pattern: three patterns at ten files each would be thirty paid turns bought by a role
            // that filled its folder, which is exactly what PerPass exists to stop.
            files = [.. KindsFor(role)
                .SelectMany(k => Directory.EnumerateFiles(outDir, Pattern(k)).Select(f => (File: f, Kind: k)))
                .OrderBy(x => x.File, StringComparer.Ordinal)
                .Take(PerPass)];
        }
        catch (Exception) { return pass; }

        foreach (var (file, kind) in files)
        {
            // ---- the fence, BEFORE the file is read -------------------------------------------
            // The name has to name a launch this app recorded, of THIS role. Everything else goes
            // to quarantine unread: a file the app cannot attribute is not this role's work as far
            // as anything downstream can show, and publishing it would put a real artifact id on a
            // provenance nobody can check.
            var named = AttemptIn(Path.GetFileName(file), kind);
            switch (Fence(named, role, attempt))
            {
                case Verdict.Quarantine:
                    pass.Quarantining.Add((file, outDir, role, named));
                    continue;

                // A TURN THAT IS WRITING IT RIGHT NOW. Not this pass's work to publish and not a
                // stranger's file to move: the role that owns it will publish it at its own commit.
                case Verdict.Leave:
                    continue;
            }

            // ---- boundary: the file is about to be read, and no transaction is open -------------
            Boundary?.Invoke(Reading);

            string content;
            try { content = File.ReadAllText(file); }
            catch (Exception) { continue; }         // being written now; the next pass takes it

            if (!Valid(content, kind))
            {
                pass.Rejecting.Add($"{CouncilRoles.Title(role)}: {Path.GetFileName(file)} is longer than "
                                   + $"{Limit(kind)} lines, so it was not published. The last one it "
                                   + "published still stands.");
                continue;
            }

            pass.Publishing.Add((new Publication
            {
                Id = Publication.IdOf(role, kind, content),
                Role = role,
                Attempt = named,
                Kind = kind,
                Recipients = string.Join(",", RecipientsOf(role)),
                Classification = PublicationClass.Council,
                CreatedAt = _now(),
                Source = $"{WorkspaceBuilder.OutDir}/{Path.GetFileName(file)}",
                Content = content
            }, kind));
        }

        return pass;
    }

    /// <summary>
    /// THE SQL HALF, and nothing else: whatever transaction is open on this thread is the one these
    /// land in, which is how a turn's publications share the commit that closes its launch.
    /// </summary>
    internal void Land(Pass pass)
    {
        foreach (var (p, kind) in pass.Publishing)
        {
            // ---- boundary 1: the file exists and nothing in the tables knows about it ------------
            Boundary?.Invoke("file");

            // THE THREE WAYS AN ARTIFACT LANDS, AND THE APP PICKS. A report or an agenda is committed
            // and handed straight over. An ASSESSMENT is committed and SEALED — no delivery, no wake —
            // until the other director's exists (docs/COUNCIL.md:61). A CHALLENGE is taken only once
            // per boundary and only after both assessments are delivered. A refusal writes nothing at
            // all and buys nobody a turn, and is said out loud by Apply below.
            switch (kind)
            {
                case PublicationKind.Assessment:
                {
                    var taken = _boundaries.Assess(p, _now());
                    if (!taken.Ok)
                        pass.Rejecting.Add($"{CouncilRoles.Title(p.Role)}: the assessment in "
                                           + $"{p.Source} was not published — {taken.Why}");
                    continue;
                }

                case PublicationKind.Challenge:
                {
                    var taken = _boundaries.Challenge(p, _now());
                    if (!taken.Ok)
                        pass.Rejecting.Add($"{CouncilRoles.Title(p.Role)}: the challenge in "
                                           + $"{p.Source} was not published — {taken.Why}");
                    continue;
                }
            }

            _store.Commit(p, _now());

            // WHAT THE OWNER ASKED FOR, TURNED INTO WORK FOR SOMEBODY ELSE. The chair's agenda is
            // the only kind that carries the owner's words downward, and the link recorded here is a
            // MEASUREMENT and nothing more: this attempt consumed those messages and this attempt
            // produced this artifact. Nothing reads either text — deciding that a brief is ABOUT a
            // message is a judgment, and a disposition resting on one is not evidence.
            //
            // The launch it names is the one the FILE names, not the one that ran this pass: the
            // messages were consumed by the turn that wrote the agenda, and a pass reconciling that
            // turn's leftovers after a restart must not move the link onto a later launch.
            //
            // Idempotent, like everything else in this pass: the first publication of a turn keeps
            // the link and a re-run writes nothing.
            if (kind == PublicationKind.Brief)
                try { _events.Delegated(p.Attempt!, p.Id); }
                catch (Exception) { /* a disposition is a record; losing one must not stop the relay */ }

            // ---- boundary 2: the artifact, its delivery and the task are committed; no copy yet --
            Boundary?.Invoke("transaction");
        }
    }

    /// <summary>
    /// THE DISK HALF THAT COMES AFTER THE COMMIT: files moved aside, and the two sentences the app
    /// says out loud. Outside the transaction for the reason every other write to disk is — a
    /// rollback cannot un-move a file — and because the app's own log is the same database.
    /// </summary>
    internal void Apply(Pass pass)
    {
        foreach (var (file, outDir, role, named) in pass.Quarantining) Quarantine(file, outDir, role, named);

        foreach (var why in pass.Rejecting)
            try { Rejected?.Invoke(why); }
            catch (Exception) { /* the app's own logging; never the relay's problem */ }
    }

    /// <summary>
    /// THE ATTEMPT ID A FILE NAME CARRIES, or null when it carries none. <c>report-&lt;id&gt;.md</c>
    /// and <c>agenda-&lt;id&gt;.md</c>, which is exactly what <see cref="Pattern"/> matched and what
    /// the mission file asks the agent for.
    /// </summary>
    public static string? AttemptIn(string fileName, string kind)
    {
        var prefix = Pattern(kind).Replace("*.md", "");
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(".md", StringComparison.Ordinal)) return null;

        var id = fileName[prefix.Length..^".md".Length];
        return id.Length == 0 ? null : id;
    }

    /// <summary>What a pass may do with one file it found. See <see cref="Fence"/>.</summary>
    internal enum Verdict
    {
        /// <summary>The name carries a launch of this role the app can stand behind.</summary>
        Publish,

        /// <summary>Somebody else's turn is writing it right now. Not this pass's to touch.</summary>
        Leave,

        /// <summary>Nothing here can say whose work it is, so it is moved aside and said out loud.</summary>
        Quarantine
    }

    /// <summary>
    /// MAY THIS FILE BE PUBLISHED AT ALL, and under the launch its name carries?
    ///
    /// <list type="bullet">
    ///   <item>The name must carry an id this app has a row for, launched for THIS role. A file
    ///     naming nothing, naming a launch that does not exist, or naming the other role's launch is
    ///     a file whose provenance the app cannot state.</item>
    ///   <item>ENDED and LOST both publish. A LOST attempt's file is the case the unit exists for:
    ///     the work is real and its provenance is that killed turn's, not the next one's.</item>
    ///   <item>A row still LAUNCHED publishes only when it is THIS pass's own launch. Any other open
    ///     row belongs to a process the app is not accounting for — its own restart turns every open
    ///     row LOST before the first pass — so a file naming one arrived from somewhere the app
    ///     cannot vouch for, and that is the stale-process case the fence exists to stop.</item>
    ///   <item><b>UNLESS THIS PROCESS IS FLYING IT.</b> Two roles' turns may overlap, so an open row
    ///     that is not this pass's own is no longer proof of a stale process: it is ordinarily the
    ///     other role, mid-turn, writing the file this pass just found. That one is LEFT where it
    ///     is — its own role's commit publishes it — because moving it to <c>quarantine/</c> would
    ///     take a live turn's report away from it on the strength of when a pass happened to run.</item>
    /// </list>
    /// </summary>
    Verdict Fence(string? id, string role, string? passAttempt)
    {
        if (id is not { Length: > 0 }) return Verdict.Quarantine;

        AiAttempt? a;
        try { a = _attempts.Get(id); }
        catch (Exception) { return Verdict.Quarantine; }   // an unreadable ledger attributes nothing

        if (a is null || CouncilRoles.Or(a.Role) != role) return Verdict.Quarantine;
        if (a.State != AiAttemptState.LAUNCHED || id == passAttempt) return Verdict.Publish;
        return _live.Holds(id) ? Verdict.Leave : Verdict.Quarantine;
    }

    /// <summary>
    /// MOVES ONE UNATTRIBUTABLE FILE OUT OF THE PASS AND SAYS SO ONCE. Moved rather than deleted or
    /// left where it is: deleting destroys the evidence, and leaving it would have the relay read
    /// and refuse the same file on every turn for the rest of the installation's life.
    ///
    /// A move that fails is not an error worth stopping for — the file stays, the next pass tries
    /// again, and nothing has been published either way.
    /// </summary>
    void Quarantine(string file, string outDir, string role, string? named)
    {
        var name = Path.GetFileName(file);
        try
        {
            var into = Path.Combine(outDir, QuarantineDir);
            Directory.CreateDirectory(into);
            File.Move(file, Path.Combine(into, name), overwrite: true);
        }
        catch (Exception) { return; }

        var why = named is { Length: > 0 }
            ? $"names the attempt {named}, which is not a turn TradeAgent has finished for it"
            : "names no attempt at all";

        try
        {
            Quarantined?.Invoke($"{CouncilRoles.Title(role)}: {name} {why}, so it was not published. "
                                + $"It has been moved to `{WorkspaceBuilder.OutDir}/{QuarantineDir}/`.");
        }
        catch (Exception) { /* the app's own logging; never the relay's problem */ }
    }

    /// <summary>
    /// Writes every committed delivery into its recipient's <c>in/</c> and only then marks it
    /// delivered. The content comes from the publication row, never from the publishing role's file:
    /// that file is the agent's and may be gone.
    /// </summary>
    internal void Deliver()
    {
        // BOTH SEALED ASSESSMENTS ARE RELEASED TOGETHER, AND HERE IS WHERE "TOGETHER" MEANS SOMETHING.
        // The flip from `withheld` to `committed` is one transaction over both rows, and the copy pass
        // below is what then puts both files on disk in the same pass. Releasing at the moment the
        // second assessment was committed would still leave one director's file landing while the
        // other's was being written; the release is the app's own pass, not the author's.
        //
        // Never throws out of the relay: a release that could not be made leaves both withheld, which
        // is the fail-safe direction — the seal holds, and the next pass tries again.
        try { _boundaries.Release(_now()); }
        catch (Exception) { /* the seal holds; the next pass releases */ }

        foreach (var d in _store.Undelivered())
        {
            var p = _store.Get(d.PublicationId);
            if (p is null) continue;                // cannot happen in one transaction; not a crash

            try
            {
                var into = Path.Combine(_homeOf(d.Recipient), WorkspaceBuilder.InDir);
                Directory.CreateDirectory(into);
                File.WriteAllText(Path.Combine(into, $"{p.Id}.md"), p.Content);
            }
            catch (Exception) { continue; }          // the next pass tries again; nothing is claimed

            // ---- boundary 3: the file is on disk and the row still says committed ---------------
            Boundary?.Invoke("copy");

            _store.MarkDelivered(d.PublicationId, d.Recipient, _now());
        }
    }

    /// <summary>
    /// WHAT EACH ROLE PUBLISHES. The chair sends an agenda down; Research reports up. It is a
    /// function of the role rather than of the file name, so a role cannot change what its output IS
    /// by naming a file differently.
    /// </summary>
    public static string KindFor(string role) =>
        role == CouncilRoles.Research ? PublicationKind.Report : PublicationKind.Brief;

    /// <summary>
    /// EVERY KIND ONE ROLE MAY PUBLISH. Its own handoff, plus the two the consequential boundary adds —
    /// and both directors write both of those, which is the point: an assessment is one per director per
    /// boundary and the one challenge may come from either.
    ///
    /// <para>A function of the role and never of the file name, like <see cref="KindFor"/>: a role does
    /// not get to turn its agenda into an assessment by renaming the file, and WHICH boundary an
    /// assessment answers is the app's decision too (<see cref="CouncilBoundaries.Assess"/>).</para>
    /// </summary>
    public static string[] KindsFor(string role) =>
        [KindFor(role), PublicationKind.Assessment, PublicationKind.Challenge];

    /// <summary>
    /// WHO IT GOES TO. Both directions are one hop today. It is here rather than in the mission file
    /// because a role must not be able to choose its own audience: that is the recipient scoping
    /// round 4 asked for, and it is the app's decision.
    /// </summary>
    public static string[] RecipientsOf(string role) =>
        role == CouncilRoles.Research ? [CouncilRoles.Operations] : [CouncilRoles.Research];

    /// <summary>The file names each kind is read from — exactly what the mission file asks for.</summary>
    public static string Pattern(string kind) => kind switch
    {
        PublicationKind.Report => "report-*.md",
        PublicationKind.Assessment => "assessment-*.md",
        PublicationKind.Challenge => "challenge-*.md",
        _ => "agenda-*.md"
    };

    /// <summary>
    /// HOW MANY LINES EACH KIND MAY BE. An assessment and a challenge are capped like a REPORT, which is
    /// the context-etiquette budget the doctrine states for anything handed upward or sideways — and the
    /// challenge is the one <c>docs/COUNCIL.md</c>:61 calls "bounded" in so many words. Dropping the cap
    /// is the mutant this unit watches: an unbounded challenge is a transcript handed to the peer at the
    /// owner's expense, which is the one thing context etiquette exists to stop.
    /// </summary>
    public static int Limit(string kind) => kind == PublicationKind.Brief ? AgendaLines : ReportLines;

    /// <summary>
    /// Whether this text may be published. Blank is not a publication, and neither is a file over
    /// the limit — counted on non-empty lines so a trailing newline is not what rejects a report.
    /// </summary>
    public static bool Valid(string content, string kind)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var lines = content.Replace("\r\n", "\n").Split('\n').Count(l => l.Trim().Length > 0);
        return lines <= Limit(kind);
    }
}
