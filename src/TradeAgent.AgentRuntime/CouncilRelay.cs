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

    readonly PublicationStore _store;
    readonly MissionEventStore _events;
    readonly Func<string, string> _homeOf;
    readonly Func<DateTimeOffset> _now;

    public CouncilRelay(Database db, Func<string, string> homeOf, Func<DateTimeOffset>? now = null)
    {
        _store = new PublicationStore(db);
        _events = new MissionEventStore(db);
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
    /// ONE FULL RECONCILIATION OF DISK AGAINST THE TABLES. Run on start and after every turn.
    ///
    /// <paramref name="attempt"/> is recorded as the provenance of anything published in this pass,
    /// which is only meaningful for the role that just turned; a pass on start names none, and the
    /// column is nullable for exactly that reason.
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
    void Publish(string role, string? attempt)
    {
        var kind = KindFor(role);
        var outDir = Path.Combine(_homeOf(role), WorkspaceBuilder.OutDir);

        string[] files;
        try
        {
            if (!Directory.Exists(outDir)) return;
            files = [.. Directory.EnumerateFiles(outDir, Pattern(kind)).OrderBy(f => f, StringComparer.Ordinal)
                .Take(PerPass)];
        }
        catch (Exception) { return; }

        foreach (var file in files)
        {
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
                Attempt = attempt,
                Revision = _store.NextRevision(role),
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
            // Idempotent, like everything else in this pass: the first publication of a turn keeps
            // the link and a re-run writes nothing.
            if (kind == PublicationKind.Brief && attempt is { Length: > 0 } launch)
                try { _events.Delegated(launch, p.Id); }
                catch (Exception) { /* a disposition is a record; losing one must not stop the relay */ }

            // ---- boundary 2: the artifact, its delivery and the task are committed; no copy yet --
            Boundary?.Invoke("transaction");
        }
    }

    /// <summary>
    /// Writes every committed delivery into its recipient's <c>in/</c> and only then marks it
    /// delivered. The content comes from the publication row, never from the publishing role's file:
    /// that file is the agent's and may be gone.
    /// </summary>
    void Deliver()
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
