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

    readonly PublicationStore _store;
    readonly MissionEventStore _events;
    readonly AiAttemptStore _attempts;
    readonly Func<string, string> _homeOf;
    readonly Func<DateTimeOffset> _now;

    public CouncilRelay(Database db, Func<string, string> homeOf, Func<DateTimeOffset>? now = null)
    {
        _store = new PublicationStore(db);
        _events = new MissionEventStore(db);
        _attempts = new AiAttemptStore(db);
        _homeOf = homeOf;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// A SEAM FOR THE PROPERTY TEST, AND FOR NOTHING ELSE. Called at each of the three boundaries
    /// with its name; the test throws from it to simulate the app dying exactly there. Null in the
    /// product, where the three boundaries are just three consecutive statements.
    /// </summary>
    internal Action<string>? Boundary { get; set; }

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
    /// ONE FULL RECONCILIATION OF DISK AGAINST THE TABLES. Run on start and after every turn.
    ///
    /// <paramref name="attempt"/> IS NO LONGER WHAT ATTRIBUTES A FILE — the id in the file's name is
    /// — but it is still what the fence needs: it says which launch this pass belongs to, so a file
    /// naming a DIFFERENT launch that is still open can be told apart from this turn's own output.
    /// A pass on start names none, and by then the meter has already turned every open row LOST.
    /// </summary>
    public void Run(string? role = null, string? attempt = null)
    {
        foreach (var r in role is { Length: > 0 } one ? [one] : CouncilRoles.All)
            Publish(r, r == role ? attempt : null);

        Deliver();
    }

    /// <summary>
    /// Publishes everything valid in one role's <c>out/</c> that is not published already. "Already"
    /// is decided by the primary key inside the transaction rather than by a read here: a
    /// read-then-write would be a race, and the hash is the answer.
    /// </summary>
    internal void Publish(string role, string? attempt)
    {
        var kind = KindFor(role);
        var outDir = Path.Combine(_homeOf(role), WorkspaceBuilder.OutDir);

        string[] files;
        try
        {
            if (!Directory.Exists(outDir)) return;
            // Top level only, which is what keeps `out/quarantine/` out of the pass: a file the
            // fence has already refused must not be read again on the next turn, for ever.
            files = [.. Directory.EnumerateFiles(outDir, Pattern(kind)).OrderBy(f => f, StringComparer.Ordinal)
                .Take(PerPass)];
        }
        catch (Exception) { return; }

        foreach (var file in files)
        {
            // ---- the fence, BEFORE the file is read -------------------------------------------
            // The name has to name a launch this app recorded, of THIS role. Everything else goes
            // to quarantine unread: a file the app cannot attribute is not this role's work as far
            // as anything downstream can show, and publishing it would put a real artifact id on a
            // provenance nobody can check.
            var named = AttemptIn(Path.GetFileName(file), kind);
            if (!Attributable(named, role, attempt))
            {
                Quarantine(file, outDir, role, named);
                continue;
            }

            string content;
            try { content = File.ReadAllText(file); }
            catch (Exception) { continue; }         // being written now; the next pass takes it

            if (!Valid(content, kind))
            {
                Rejected?.Invoke($"{CouncilRoles.Title(role)}: {Path.GetFileName(file)} is longer than "
                                 + $"{Limit(kind)} lines, so it was not published. The last one it "
                                 + "published still stands.");
                continue;
            }

            var p = new Publication
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
            };

            // ---- boundary 1: the file exists and nothing in the tables knows about it ------------
            Boundary?.Invoke("file");

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
                try { _events.Delegated(named!, p.Id); }
                catch (Exception) { /* a disposition is a record; losing one must not stop the relay */ }

            // ---- boundary 2: the artifact, its delivery and the task are committed; no copy yet --
            Boundary?.Invoke("transaction");
        }
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
    /// </list>
    /// </summary>
    bool Attributable(string? id, string role, string? passAttempt)
    {
        if (id is not { Length: > 0 }) return false;

        AiAttempt? a;
        try { a = _attempts.Get(id); }
        catch (Exception) { return false; }           // an unreadable ledger cannot attribute anything

        if (a is null || CouncilRoles.Or(a.Role) != role) return false;
        return a.State != AiAttemptState.LAUNCHED || id == passAttempt;
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
    /// WHO IT GOES TO. Both directions are one hop today. It is here rather than in the mission file
    /// because a role must not be able to choose its own audience: that is the recipient scoping
    /// round 4 asked for, and it is the app's decision.
    /// </summary>
    public static string[] RecipientsOf(string role) =>
        role == CouncilRoles.Research ? [CouncilRoles.Operations] : [CouncilRoles.Research];

    /// <summary>The file names each kind is read from — exactly what the mission file asks for.</summary>
    public static string Pattern(string kind) =>
        kind == PublicationKind.Report ? "report-*.md" : "agenda-*.md";

    public static int Limit(string kind) =>
        kind == PublicationKind.Report ? ReportLines : AgendaLines;

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
