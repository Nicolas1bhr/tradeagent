using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE ROLE'S OWN MEMORY, SNAPSHOTTED AS A REVISION AT THE END OF EVERY TURN — and refused when it
/// has outgrown the budget the app enforces.
///
/// <para><c>docs/COUNCIL.md</c>, "Memory in four tiers": tier (iii) is "the role's working files,
/// versioned in the workspace", with "size budgets on every file, enforced by the app at
/// publication: an invalid publication is rejected and the last valid plan and index stand". Until
/// this class existed <c>trading/PLAN.md</c> and <c>trading/JOURNAL.md</c> were written freely by
/// the agent — no cap, no revision, no record of what any earlier turn had planned. The relay's
/// <see cref="CouncilRelay.Valid"/> capped <c>out/</c> and nothing else.</para>
///
/// <para><b>Why the two files and not the whole folder.</b> These are the only two that cross a
/// fresh CLI session; the mission file says so and the agent is told to read them first. Everything
/// else in the role's home is working material the scanner records. A revision here is what makes
/// "what did this role plan, and on what evidence" answerable after the session that planned it is
/// gone.</para>
///
/// <para><b>Rejection restores; it never edits.</b> An over-cap or unreadable file is not published
/// and the LAST VALID revision is written back over it, so the agent's next turn opens a plan the
/// app is standing behind rather than one it has quietly refused. The turn is told in its next
/// <c>## Situation</c> — a restore the agent is not told about is the app editing its memory behind
/// its back, and the next turn would spend itself wondering where its work went.</para>
///
/// <para><b>Written by the app only.</b> Same rule as <c>material</c>, <c>ai_attempt</c> and the
/// relay's tables: no verb, no pipe op, no path from an agent. A role that could write its own
/// revisions could publish a plan it never held, or delete the record of one it did.</para>
/// </summary>
public sealed class WorkspaceRevisions(Database db, Func<string, string> homeOf, Func<DateTimeOffset>? now = null)
{
    /// <summary>
    /// THE PLAN'S BUDGET, in non-empty lines. Sixty is the size at which a plan is still a plan: it
    /// is read in full at the start of every turn, by a model paying for every token of it, and a
    /// plan nobody can afford to read is a plan nobody reads.
    /// </summary>
    public const int PlanLines = 60;

    /// <summary>
    /// THE JOURNAL'S BUDGET. Bigger, because a journal is a list and its value is in the entries —
    /// but bounded, because it is loaded every turn too. Older entries go to
    /// <see cref="ArchiveDir"/>, which nothing here reads and nothing caps.
    /// </summary>
    public const int JournalLines = 200;

    /// <summary>Where the agent moves what no longer fits. Tracked, so the record keeps it.</summary>
    public const string ArchiveDir = "trading/archive";

    /// <summary>The two files that cross a fresh session, and the budget each is held to.</summary>
    public static readonly (string Kind, string Path, int Lines)[] Files =
    [
        (PublicationKind.Plan, "trading/PLAN.md", PlanLines),
        (PublicationKind.Journal, "trading/JOURNAL.md", JournalLines)
    ];

    readonly PublicationStore _store = new(db);
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>What was refused and what was put back, so the app can say so once. Never throws out.</summary>
    public Action<string>? Rejected { get; set; }

    /// <summary>
    /// ONE FILE THE APP OWES THE WORKSPACE after the transaction: the last valid revision of a file
    /// whose current contents were refused. Returned rather than written inside the commit, because
    /// a rollback cannot un-write a file — and because the write-back is idempotent, so a crash
    /// between the commit and the copy simply has the next turn refuse and restore again.
    /// </summary>
    public sealed record Restore(string Path, string Content);

    /// <summary>
    /// SNAPSHOTS ONE ROLE'S TWO MEMORY FILES. Called inside the turn's committed transition, so a
    /// revision and the launch that produced it land together or not at all.
    ///
    /// <para>Returns what still has to be written back to disk. A file that is missing is not a
    /// rejection — a role that has not written a plan yet has nothing to refuse — and a file whose
    /// content is unchanged raises no new revision, because the publication's id is the hash of what
    /// is in it and the primary key absorbs the second insert.</para>
    /// </summary>
    public List<Restore> Snapshot(string role, string? attempt)
    {
        var restores = new List<Restore>();

        foreach (var (kind, rel, cap) in Files)
        {
            var full = Path.Combine(homeOf(role), rel.Replace('/', Path.DirectorySeparatorChar));

            // A FILE THAT IS NOT THERE IS NOT A REFUSAL. A role that has not written a plan yet has
            // nothing to version and nothing to restore; only a file that EXISTS and cannot be read
            // is refused, because that is a file whose contents the app cannot stand behind.
            bool there;
            try { there = File.Exists(full); }
            catch (Exception) { there = false; }
            if (!there) continue;

            string? content;
            try { content = File.ReadAllText(full); }
            catch (Exception) { content = null; }    // unreadable reads as refused, below

            if (content is not null && Valid(content, cap))
            {
                _store.Record(new Publication
                {
                    Id = Publication.IdOf(role, kind, content),
                    Role = role,
                    Attempt = attempt,
                    Kind = kind,
                    // THE ROLE ITSELF, AND NOBODY ELSE. A plan is the role's own memory, not a
                    // handoff: recording the recipient as the author is what says out loud that
                    // this artifact was never scoped to anyone else.
                    Recipients = role,
                    Classification = PublicationClass.Private,
                    CreatedAt = _now(),
                    Source = rel,
                    Content = content
                }, _now());

                Clear(role, kind);
                continue;
            }

            // ---- refused ----------------------------------------------------------------------
            var why = content is null
                ? "could not be read"
                : $"is {NonEmpty(content)} lines and the limit is {cap}";

            var last = _store.Latest(role, kind);
            var notice = last is null
                ? $"`{rel}` {why}, so no revision of it was recorded. There is no earlier revision "
                  + "to put back, so what is on disk is whatever you last wrote."
                : $"`{rel}` {why}, so it was not recorded and revision {last.Revision} — the last one "
                  + $"the app accepted — has been put back in its place. Move what no longer fits to "
                  + $"`{ArchiveDir}/` and keep it under {cap} lines.";

            if (last is not null) restores.Add(new Restore(full, last.Content));

            Note(role, kind, notice);
            try { Rejected?.Invoke($"{CouncilRoles.Title(role)}: {notice}"); }
            catch (Exception) { /* the app's own logging; never this class's problem */ }
        }

        return restores;
    }

    /// <summary>
    /// Writes the restores back. Outside the transaction and after it: the database is the record,
    /// the file is a copy of it, and a copy that failed is re-made on the next turn.
    /// </summary>
    public static void Apply(IEnumerable<Restore> restores)
    {
        foreach (var r in restores)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(r.Path)!);
                File.WriteAllText(r.Path, r.Content);
            }
            catch (Exception) { /* the next turn refuses and restores again */ }
    }

    /// <summary>
    /// WHAT THIS ROLE'S NEXT TURN HAS TO BE TOLD: one line per file the app refused and restored.
    ///
    /// It is kept in the database rather than on this object because the turn that reads it is a
    /// different process's worth of work away, and because a notice the app forgot across a restart
    /// would be a plan silently replaced under an agent that never learned why.
    /// </summary>
    public static IReadOnlyList<string> Notices(Database db, string role)
    {
        var said = new List<string>();
        foreach (var (kind, _, _) in Files)
            try
            {
                if (db.GetKv(Key(role, kind)) is { Length: > 0 } text) said.Add(text);
            }
            catch (Exception) { /* a Situation is composed of what could be read */ }

        return said;
    }

    /// <summary>Counted on non-empty lines, so a trailing newline is never what refuses a file.</summary>
    public static bool Valid(string content, int cap) => NonEmpty(content) <= cap;

    static int NonEmpty(string content) =>
        content.Replace("\r\n", "\n").Split('\n').Count(l => l.Trim().Length > 0);

    static string Key(string role, string kind) => $"revision_rejected:{role}:{kind}";

    void Note(string role, string kind, string text)
    {
        try { db.SetKv(Key(role, kind), text); }
        catch (Exception) { /* the activity line above still says it happened */ }
    }

    void Clear(string role, string kind)
    {
        try { db.SetKv(Key(role, kind), ""); }
        catch (Exception) { /* a stale notice costs one line in one Situation */ }
    }
}
