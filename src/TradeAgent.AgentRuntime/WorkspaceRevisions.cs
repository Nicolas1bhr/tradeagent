using System.Globalization;
using System.Text;
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
/// <para><b>And it never destroys.</b> The write-back lands over the only copy of what the turn
/// wrote — a refused journal is the turn's newest entries — so that is KEPT first, byte for byte, as
/// a new file in <see cref="ArchiveDir"/>, and a file that could not be kept is not put back at all
/// (<c>U-memory-kept</c>; <c>docs/PRINCIPLES.md</c>: "explicit refusal and recoverable output over
/// silent truncation or destruction"). The copy is not a revision — no publication carries it —
/// nothing here reads it back, and it reaches the next turn only as a name in the notice.</para>
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

    /// <summary>
    /// Where the agent moves what no longer fits, and where the app keeps a refused file before it
    /// puts the last revision back. Tracked, so the record keeps it.
    /// </summary>
    public const string ArchiveDir = "trading/archive";

    /// <summary>
    /// HOW MANY NAMES ONE REFUSAL MAY TRY before it counts as not kept. Two refusals come to the same
    /// name only when they name the same launch or the same millisecond, so a second name is rare and
    /// a hundredth is a folder this class does not understand — and a copy it cannot place is a
    /// write-back it does not make.
    /// </summary>
    const int KeptNames = 100;

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
    /// whose current contents were refused — and are already kept in <see cref="ArchiveDir"/>.
    /// Returned rather than written inside the commit, because a rollback cannot un-write a file —
    /// and because the write-back is idempotent, so a crash between the commit and the write-back
    /// simply has the next turn refuse, keep and restore again: one more copy in the archive, and
    /// nothing lost.
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
    ///
    /// <para><b>A refused file with an earlier revision is kept before it is owed a write-back</b>
    /// (<see cref="Keep"/>): the copy is written here, inside the transaction — a rollback leaves an
    /// extra file in the archive, which destroys nothing — and a file whose copy is not on disk is
    /// returned with no <see cref="Restore"/>, so it stays exactly as the agent left it.</para>
    /// </summary>
    public List<Restore> Snapshot(string role, string? attempt)
    {
        var restores = new List<Restore>();

        foreach (var (kind, rel, cap) in Files)
        {
            var home = homeOf(role);
            var full = Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar));

            // A FILE THAT IS NOT THERE IS NOT A REFUSAL. A role that has not written a plan yet has
            // nothing to version and nothing to restore; only a file that EXISTS and cannot be read
            // is refused, because that is a file whose contents the app cannot stand behind.
            bool there;
            try { there = File.Exists(full); }
            catch (Exception) { there = false; }
            if (!there) continue;

            // THE BYTES, READ ONCE. What is judged, recorded and — if it is refused — kept is this one
            // read: a copy taken by a second read could be of a file that changed in between, and a
            // copy made by re-encoding the text would not be what the agent wrote.
            byte[]? bytes;
            try { bytes = File.ReadAllBytes(full); }
            catch (Exception) { bytes = null; }      // unreadable reads as refused, below
            var content = bytes is null ? null : Text(bytes);

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
            string notice;
            if (last is null)
                notice = $"`{rel}` {why}, so no revision of it was recorded. There is no earlier revision "
                         + "to put back, so what is on disk is whatever you last wrote.";
            else if (Keep(home, rel, attempt, bytes) is { } kept)
            {
                // KEPT, SO IT MAY BE PUT BACK — and only now: the write-back lands over the file the
                // copy was made from. The notice names the copy, because a copy the next turn cannot
                // find is a copy in name only.
                restores.Add(new Restore(full, last.Content));
                notice = $"`{rel}` {why}, so it was not recorded and revision {last.Revision} — the last one "
                         + $"the app accepted — has been put back in its place; what you wrote is kept at "
                         + $"`{kept}`. Move what still matters into the "
                         + $"{(kind == PublicationKind.Plan ? "plan" : "journal")} under {cap} lines.";
            }
            else
                // NOT KEPT, SO NOT PUT BACK. A write-back over the only copy of what the turn wrote is
                // the destruction this exists to refuse; the file stays the agent's, and it is told.
                // A file that could not be read has no bytes to keep, so it lands here too.
                notice = $"`{rel}` {why}, so it was not recorded. What you wrote could not be kept in "
                         + $"`{ArchiveDir}/`, so revision {last.Revision} — the last one the app accepted — "
                         + "was not put back: what is on disk is what you last wrote."
                         + (content is null ? "" : $" Bring it under {cap} lines yourself.");

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
    /// KEEPS WHAT THE AGENT WROTE before anything is put back over it: the bytes
    /// <see cref="Snapshot"/> read, exactly, as a NEW file
    /// <c>trading/archive/&lt;PLAN|JOURNAL&gt;-refused-&lt;launch or instant&gt;.md</c> — a name that
    /// is taken is skipped, never written over, because a copy a later refusal could replace is not
    /// kept.
    ///
    /// <para>Returns the copy's path inside the role's home, in the form the agent writes it, or
    /// null when no copy is on disk: nothing was read, the folder cannot be made (a FILE where it
    /// belongs), the write failed, or every name was taken. Null means no write-back.</para>
    /// </summary>
    string? Keep(string home, string rel, string? attempt, byte[]? bytes)
    {
        if (bytes is null) return null;     // a file that could not be read has nothing here to keep

        try
        {
            var dir = Path.Combine(home, ArchiveDir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            var stem = $"{Path.GetFileNameWithoutExtension(rel)}-refused-{Stamp(attempt)}";

            for (var n = 1; n <= KeptNames; n++)
            {
                var name = n == 1 ? $"{stem}.md" : $"{stem}-{n}.md";
                var path = Path.Combine(dir, name);

                FileStream file;
                try { file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (Exception) when (File.Exists(path) || Directory.Exists(path)) { continue; }

                try
                {
                    using (file)
                    {
                        file.Write(bytes);
                        file.Flush(flushToDisk: true);     // on disk before the write-back is owed
                    }
                }
                catch (Exception)
                {
                    // A PART-COPY IS NOT A COPY. Taken away rather than left for a later turn to read
                    // as what it wrote; the original is untouched, because nothing is put back.
                    try { File.Delete(path); }
                    catch (Exception) { /* a stray part-copy; the notice names no copy at all */ }
                    return null;
                }

                return $"{ArchiveDir}/{name}";
            }
        }
        catch (Exception) { /* no folder to keep it in: not kept, so not put back */ }

        return null;
    }

    /// <summary>
    /// WHAT A KEPT COPY IS NAMED FOR: the launch that wrote it — or, for a pass that names none or a
    /// launch id that is not safe inside a file name, the UTC instant to the millisecond, written
    /// with no colon, because Windows refuses one in a name.
    /// </summary>
    string Stamp(string? attempt) =>
        attempt is { Length: > 0 and <= 64 } && attempt.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? attempt
            : _now().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The text exactly as <see cref="File.ReadAllText(string)"/> reads it — UTF-8 unless a
    /// byte-order mark says otherwise — from bytes already read, so the bytes can be kept as they are.
    /// </summary>
    static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// WHAT THIS ROLE'S NEXT TURN HAS TO BE TOLD: one line per file the app refused — what it put
    /// back and where it kept what was written, or why it put nothing back.
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
