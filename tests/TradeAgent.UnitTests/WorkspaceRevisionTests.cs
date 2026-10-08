using System.Text;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// TIER (iii) OF <c>docs/COUNCIL.md</c>'s "Memory in four tiers": the role's working files,
/// versioned in the workspace, with "size budgets on every file, enforced by the app at
/// publication: an invalid publication is rejected and the last valid plan and index stand".
///
/// <para>Before this unit <c>trading/PLAN.md</c> and <c>trading/JOURNAL.md</c> were written freely
/// by the agent — no cap, no revision, no record of what any earlier turn had planned. The relay's
/// twenty-line budget covered <c>out/</c> and nothing else, so the two files that actually cross a
/// fresh session were the two the app was not standing behind.</para>
/// </summary>
public class WorkspaceRevisionTests
{
    /// <summary>One role's home over a real database file, and the two files that carry its memory.</summary>
    sealed class World : IDisposable
    {
        public World()
        {
            Root = Path.Combine(TestEnv.Home, $"revisions-{Guid.NewGuid():n}");
            Db = new Database(Path.Combine(Root, "state.db"));
            foreach (var role in CouncilRoles.All)
                Directory.CreateDirectory(Path.Combine(Home(role), "trading"));
        }

        public string Root { get; }
        public Database Db { get; }

        public string Home(string role) => WorkspaceBuilder.HomeOf(Root, role);

        public string PathOf(string role, string kind) =>
            Path.Combine(Home(role),
                WorkspaceRevisions.Files.Single(f => f.Kind == kind).Path
                    .Replace('/', Path.DirectorySeparatorChar));

        public void Write(string role, string kind, string text) =>
            File.WriteAllText(PathOf(role, kind), text);

        public string Read(string role, string kind) => File.ReadAllText(PathOf(role, kind));

        public WorkspaceRevisions Revisions() => new(Db, Home, () => At);

        public DateTimeOffset At { get; } = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        public List<Publication> Of(string role, string kind) =>
            [.. new PublicationStore(Db).By(role).Where(p => p.Kind == kind)];

        /// <summary>The role's <c>trading/archive/</c>, where a refused file is kept.</summary>
        public string Archive(string role) =>
            Path.Combine(Home(role), WorkspaceRevisions.ArchiveDir.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>The names in the archive, in ordinal order; none when it does not exist.</summary>
        public string[] Kept(string role) =>
            Directory.Exists(Archive(role))
                ? [.. Directory.GetFiles(Archive(role)).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)]
                : [];

        public byte[] KeptBytes(string role, string name) => File.ReadAllBytes(Path.Combine(Archive(role), name));

        public string ReadKept(string role, string name) => File.ReadAllText(Path.Combine(Archive(role), name));

        public void Dispose() => Db.Dispose();
    }

    static string Lines(int n, string what) =>
        string.Join("\n", Enumerable.Range(1, n).Select(i => $"{what} {i}"));

    // ---- the revision --------------------------------------------------------------------------

    /// <summary>
    /// SCHEMA 11 IS THIS CLASS'S FLOOR, not the build's number. Schema 11 adds no column — a revision
    /// of a role's plan IS an artifact, and a second table would put two answers to "what did this role
    /// publish" in two places — it adds the index the restore reads.
    ///
    /// <para>It used to read <c>Assert.Equal(11, Versions.DatabaseSchemaVersion)</c>, which made every
    /// later ADDITIVE migration fail here for no reason of its own: <c>U-runner-3</c> added the
    /// strategy ledger at 12 and this went red without anything about a plan revision having changed.
    /// The same move <c>CouncilRoleTests</c>, <c>AiAttemptLedgerTests</c> and <c>OwnerDispositionTests</c>
    /// already made, for the same reason, and the comment on the last of those is the argument. The
    /// row on disk is still asserted to equal what this build writes, so an upgrade that did not run
    /// is still caught, and the index below is still the thing this test is about.</para>
    /// </summary>
    [Fact]
    public void The_plan_and_journal_revisions_arrive_at_schema_eleven()
    {
        using var db = TestEnv.NewDb();
        Assert.True(Versions.DatabaseSchemaVersion >= 11,
            $"the plan and journal revisions need schema 11 or later; this build says {Versions.DatabaseSchemaVersion}");
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), db.Read(_ =>
        {
            using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
            return c.ExecuteScalar() as string;
        }));
        Assert.Equal(1L, db.Read(_ =>
        {
            using var c = db.Cmd(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_publication_kind'");
            return (long)c.ExecuteScalar()!;
        }));
    }

    /// <summary>
    /// RED FIRST: nothing versioned either of these files, so this collection was empty.
    ///
    /// What the row says is the point. The recipient is the role ITSELF and the classification is
    /// private, which is round 4's recipient scoping applied to the one artifact that is nobody
    /// else's business; and no delivery and no wake come with it, because a role woken to read its
    /// own plan is the owner paying for the app to hand an agent its own memory back.
    /// </summary>
    [Fact]
    public void A_plan_inside_its_budget_becomes_a_private_revision_and_costs_nobody_a_turn()
    {
        using var world = new World();
        var text = Lines(WorkspaceRevisions.PlanLines, "plan line");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, text);

        Assert.Empty(world.Revisions().Snapshot(CouncilRoles.Research, "turn-a"));

        var p = Assert.Single(world.Of(CouncilRoles.Research, PublicationKind.Plan));
        Assert.Equal(text, p.Content);
        Assert.Equal(Publication.IdOf(CouncilRoles.Research, PublicationKind.Plan, text), p.Id);
        Assert.Equal([CouncilRoles.Research], p.RecipientList);
        Assert.Equal(PublicationClass.Private, p.Classification);
        Assert.Equal("turn-a", p.Attempt);
        Assert.Equal("trading/PLAN.md", p.Source);
        Assert.Equal(1, p.Revision);

        Assert.Empty(new PublicationStore(world.Db).To(CouncilRoles.Research));
        Assert.Empty(new MissionEventStore(world.Db).OfKind(PublicationKind.Plan));
        Assert.Empty(WorkspaceRevisions.Notices(world.Db, CouncilRoles.Research));
    }

    /// <summary>
    /// AN UNCHANGED FILE SPENDS NO REVISION. The id is the hash of what is in it, so the second
    /// turn's insert is the same row and the primary key refuses it — which is what stops a role
    /// that reads its plan and writes nothing from filling the table with copies of itself.
    /// </summary>
    [Fact]
    public void A_plan_that_did_not_change_raises_no_second_revision()
    {
        using var world = new World();
        world.Write(CouncilRoles.Research, PublicationKind.Plan, Lines(10, "plan line"));

        world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-b");

        var p = Assert.Single(world.Of(CouncilRoles.Research, PublicationKind.Plan));
        Assert.Equal("turn-a", p.Attempt);          // the turn that actually wrote those bytes
    }

    // ---- the budget ----------------------------------------------------------------------------

    /// <summary>
    /// RED FIRST: an eighty-line <c>PLAN.md</c> survived the turn untouched, because nothing capped
    /// it. Now it is refused, it raises no revision, and the last one the app accepted is written
    /// back over it.
    ///
    /// <para>The write-back is the half that matters. Refusing without restoring would leave the
    /// agent reading a plan the app has decided not to stand behind, and the record would say the
    /// role's plan is something that is not on its disk.</para>
    ///
    /// <para><b>And it destroys nothing</b> (<c>U-memory-kept</c>, RED FIRST): the write-back used
    /// to land over the only copy of what the turn wrote, so a refused plan was simply gone. What the
    /// agent wrote is now kept in <c>trading/archive/</c>, byte for byte, before the restore is
    /// handed back — "explicit refusal and recoverable output over silent truncation or
    /// destruction", <c>docs/PRINCIPLES.md</c>.</para>
    /// </summary>
    [Fact]
    public void An_over_cap_plan_is_refused_and_the_last_valid_revision_is_written_back()
    {
        using var world = new World();
        var said = new List<string>();
        var good = Lines(40, "plan line");

        world.Write(CouncilRoles.Research, PublicationKind.Plan, good);
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");

        var overCap = Lines(WorkspaceRevisions.PlanLines + 20, "plan line");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, overCap);

        var revisions = world.Revisions();
        revisions.Rejected = said.Add;
        WorkspaceRevisions.Apply(revisions.Snapshot(CouncilRoles.Research, "turn-b"));

        var p = Assert.Single(world.Of(CouncilRoles.Research, PublicationKind.Plan));
        Assert.Equal(good, p.Content);
        Assert.Equal(good, world.Read(CouncilRoles.Research, PublicationKind.Plan));

        // WHAT THE AGENT WROTE IS KEPT, exactly, under the launch that wrote it — and nothing else
        // is written there.
        Assert.Equal(["PLAN-refused-turn-b.md"], world.Kept(CouncilRoles.Research));
        Assert.Equal(overCap, world.ReadKept(CouncilRoles.Research, "PLAN-refused-turn-b.md"));
        Assert.Equal(Encoding.UTF8.GetBytes(overCap), world.KeptBytes(CouncilRoles.Research, "PLAN-refused-turn-b.md"));

        // AND THE TURN IS TOLD, in the words its next Situation will carry.
        var notice = Assert.Single(WorkspaceRevisions.Notices(world.Db, CouncilRoles.Research));
        Assert.Contains("trading/PLAN.md", notice);
        Assert.Contains($"revision {p.Revision}", notice);
        Assert.Contains(WorkspaceRevisions.ArchiveDir, notice);
        Assert.Contains("trading/PLAN.md", Assert.Single(said));

        // AND IT STOPS BEING TOLD once it has written a plan the app accepts.
        world.Write(CouncilRoles.Research, PublicationKind.Plan, Lines(41, "plan line"));
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-c");
        Assert.Empty(WorkspaceRevisions.Notices(world.Db, CouncilRoles.Research));
    }

    /// <summary>
    /// THE JOURNAL IS CAPPED TOO, at its own bigger number. It is loaded every turn like the plan,
    /// so an unbounded one is a bill that grows on its own; what no longer fits goes to
    /// <c>trading/archive/</c>, which nothing here reads and nothing caps.
    /// </summary>
    [Fact]
    public void A_journal_over_its_budget_is_refused_on_the_same_terms_as_the_plan()
    {
        using var world = new World();
        var good = Lines(WorkspaceRevisions.JournalLines, "2026-09-08 tried something");
        world.Write(CouncilRoles.Research, PublicationKind.Journal, good);
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");

        world.Write(CouncilRoles.Research, PublicationKind.Journal, Lines(300, "2026-09-09 and again"));
        WorkspaceRevisions.Apply(world.Revisions().Snapshot(CouncilRoles.Research, "turn-b"));

        var p = Assert.Single(world.Of(CouncilRoles.Research, PublicationKind.Journal));
        Assert.Equal(good, p.Content);
        Assert.Equal(good, world.Read(CouncilRoles.Research, PublicationKind.Journal));
    }

    /// <summary>
    /// RED FIRST: A REFUSED JOURNAL TOOK THE TURN'S NEW ENTRIES WITH IT. A journal is appended to, so
    /// the turn that takes it over its budget is the turn that wrote the newest entries in it — and
    /// the write-back of the last accepted journal landed over the only copy of them. The refusal is
    /// of the revision; the entries are kept in <c>trading/archive/</c> before anything is put back.
    ///
    /// <para>Written the way an agent on Windows writes it — a byte-order mark and CRLF — because the
    /// copy is of the BYTES the app read: a copy made by re-encoding the text would drop the mark,
    /// and a kept copy that is not what was written is not a kept copy.</para>
    /// </summary>
    [Fact]
    public void A_refused_journal_keeps_the_turns_new_entries()
    {
        using var world = new World();
        var accepted = Lines(WorkspaceRevisions.JournalLines - 2, "2026-09-08 tried something");
        world.Write(CouncilRoles.Research, PublicationKind.Journal, accepted);
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");

        string[] entries =
        [
            "2026-09-09 backtest of the opening-range rule on March: 41 trades, -0.8 R net",
            "2026-09-09 the 40-minute gap on 2026-03-09 is in the download, not the archive",
            "2026-09-09 next: download that day again and re-run before trusting any March figure"
        ];
        var wrote = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", accepted.Split('\n').Concat(entries))))
            .ToArray();
        File.WriteAllBytes(world.PathOf(CouncilRoles.Research, PublicationKind.Journal), wrote);

        WorkspaceRevisions.Apply(world.Revisions().Snapshot(CouncilRoles.Research, "turn-b"));

        // The journal the app stands behind is back on disk, and the turn's entries are not lost.
        Assert.Equal(accepted, world.Read(CouncilRoles.Research, PublicationKind.Journal));
        Assert.Equal(accepted, Assert.Single(world.Of(CouncilRoles.Research, PublicationKind.Journal)).Content);

        Assert.Equal(["JOURNAL-refused-turn-b.md"], world.Kept(CouncilRoles.Research));
        Assert.Equal(wrote, world.KeptBytes(CouncilRoles.Research, "JOURNAL-refused-turn-b.md"));
        var kept = world.ReadKept(CouncilRoles.Research, "JOURNAL-refused-turn-b.md");
        foreach (var entry in entries) Assert.Contains(entry, kept);
    }

    /// <summary>
    /// THE FIRST TURN'S FAILURE. A role whose very first plan is over the cap has no earlier
    /// revision to restore, and inventing one would be the app writing a plan the role never held.
    /// It is refused, nothing is written back, and the notice says exactly that.
    ///
    /// <para>Nothing is copied into the archive either: nothing is put back over the file, so the
    /// file on disk IS the copy, and this case is exactly what it was before <c>U-memory-kept</c>.</para>
    /// </summary>
    [Fact]
    public void An_over_cap_plan_with_no_earlier_revision_is_refused_and_nothing_is_written_back()
    {
        using var world = new World();
        var tooLong = Lines(WorkspaceRevisions.PlanLines + 1, "plan line");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, tooLong);

        var restores = world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");

        Assert.Empty(restores);
        Assert.Empty(world.Of(CouncilRoles.Research, PublicationKind.Plan));
        Assert.Equal(tooLong, world.Read(CouncilRoles.Research, PublicationKind.Plan));
        Assert.Empty(world.Kept(CouncilRoles.Research));
        Assert.Contains("no earlier revision",
            Assert.Single(WorkspaceRevisions.Notices(world.Db, CouncilRoles.Research)));
    }

    /// <summary>
    /// RED FIRST: NO COPY, NO RESTORE. The write-back lands over the only copy of what the turn
    /// wrote, so it may land only once that copy is on disk. Here it cannot be: a FILE sits where the
    /// archive folder belongs, which refuses the copy the same way on Windows, macOS and Linux. The
    /// over-cap plan then stays exactly as the agent left it — unrecorded, and nothing of the role's
    /// moved aside to make room — and the notice says it could not be kept, so the next turn knows
    /// the file it opens is its own and still over the limit.
    /// </summary>
    [Fact]
    public void No_restore_without_a_kept_copy()
    {
        using var world = new World();
        var good = Lines(40, "plan line");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, good);
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");

        var inTheWay = world.Archive(CouncilRoles.Research);
        File.WriteAllText(inTheWay, "a file, where the archive folder belongs");

        var overCap = Lines(WorkspaceRevisions.PlanLines + 20, "plan line");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, overCap);

        var said = new List<string>();
        var revisions = world.Revisions();
        revisions.Rejected = said.Add;
        var restores = revisions.Snapshot(CouncilRoles.Research, "turn-b");
        WorkspaceRevisions.Apply(restores);

        Assert.Empty(restores);
        Assert.Equal(overCap, world.Read(CouncilRoles.Research, PublicationKind.Plan));
        Assert.Equal(good, Assert.Single(world.Of(CouncilRoles.Research, PublicationKind.Plan)).Content);
        Assert.Equal("a file, where the archive folder belongs", File.ReadAllText(inTheWay));

        var notice = Assert.Single(WorkspaceRevisions.Notices(world.Db, CouncilRoles.Research));
        Assert.Contains("trading/PLAN.md", notice);
        Assert.Contains("could not be kept", notice);
        Assert.Contains("was not put back", notice);
        Assert.Contains("could not be kept", Assert.Single(said));
    }

    /// <summary>
    /// RED FIRST: A PLAN THE APP COULD NOT READ WAS WRITTEN OVER UNREAD. It is refused like an
    /// over-cap one, but no bytes were read, so there is nothing to keep — and by the rule above
    /// nothing is put back over it either. Held open with no sharing for the length of the pass,
    /// which refuses the read on Windows, macOS and Linux alike; let go before the write-back, which
    /// is when the old code destroyed it.
    /// </summary>
    [Fact]
    public void An_unreadable_plan_has_nothing_to_keep_and_is_not_put_back_over()
    {
        using var world = new World();
        world.Write(CouncilRoles.Research, PublicationKind.Plan, Lines(40, "plan line"));
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");

        var mine = Lines(10, "a plan the pass could not read");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, mine);

        List<WorkspaceRevisions.Restore> restores;
        using (new FileStream(world.PathOf(CouncilRoles.Research, PublicationKind.Plan),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            restores = world.Revisions().Snapshot(CouncilRoles.Research, "turn-b");
        WorkspaceRevisions.Apply(restores);

        Assert.Empty(restores);
        Assert.Equal(mine, world.Read(CouncilRoles.Research, PublicationKind.Plan));
        Assert.Empty(world.Kept(CouncilRoles.Research));

        var notice = Assert.Single(WorkspaceRevisions.Notices(world.Db, CouncilRoles.Research));
        Assert.Contains("could not be read", notice);
        Assert.Contains("was not put back", notice);
    }

    /// <summary>
    /// RED FIRST: A KEPT COPY IS NEVER WRITTEN OVER. Two refusals can come to the same name — the
    /// same launch refused twice, or two passes that name no launch inside one millisecond (this
    /// test's clock does not move at all) — and the second copy is a NEW file: the first keeps its
    /// name and every byte, because a copy that a later refusal can replace is not kept.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("turn-b")]
    public void A_second_refusal_never_overwrites_the_first_copy(string? attempt)
    {
        using var world = new World();
        world.Write(CouncilRoles.Research, PublicationKind.Plan, Lines(40, "plan line"));
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");

        var first = Lines(WorkspaceRevisions.PlanLines + 1, "first draft");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, first);
        WorkspaceRevisions.Apply(world.Revisions().Snapshot(CouncilRoles.Research, attempt));

        var second = Lines(WorkspaceRevisions.PlanLines + 2, "second draft");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, second);
        WorkspaceRevisions.Apply(world.Revisions().Snapshot(CouncilRoles.Research, attempt));

        // A pass that names no launch is named for the UTC instant, to the millisecond, in a form
        // every file system takes — no colon, which Windows refuses in a name.
        var named = $"PLAN-refused-{attempt ?? "20260908T120000000Z"}.md";
        var kept = world.Kept(CouncilRoles.Research);
        Assert.Equal(2, kept.Length);
        Assert.Contains(named, kept);
        Assert.Equal(first, world.ReadKept(CouncilRoles.Research, named));
        Assert.Equal(second, world.ReadKept(CouncilRoles.Research, Assert.Single(kept, k => k != named)));
    }

    /// <summary>
    /// RED FIRST: A TURN THAT TAKES BOTH FILES OVER THEIR BUDGETS LOSES NEITHER. Each is kept under
    /// its own name before either is put back, so the plan's copy and the journal's are two files.
    /// </summary>
    [Fact]
    public void A_plan_and_a_journal_refused_in_one_turn_are_kept_as_two_files()
    {
        using var world = new World();
        world.Write(CouncilRoles.Research, PublicationKind.Plan, Lines(40, "plan line"));
        world.Write(CouncilRoles.Research, PublicationKind.Journal, Lines(100, "2026-09-08 tried something"));
        world.Revisions().Snapshot(CouncilRoles.Research, "turn-a");

        var plan = Lines(WorkspaceRevisions.PlanLines + 1, "plan line");
        var journal = Lines(WorkspaceRevisions.JournalLines + 1, "2026-09-09 and again");
        world.Write(CouncilRoles.Research, PublicationKind.Plan, plan);
        world.Write(CouncilRoles.Research, PublicationKind.Journal, journal);

        var restores = world.Revisions().Snapshot(CouncilRoles.Research, "turn-b");
        Assert.Equal(2, restores.Count);
        WorkspaceRevisions.Apply(restores);

        Assert.Equal(["JOURNAL-refused-turn-b.md", "PLAN-refused-turn-b.md"], world.Kept(CouncilRoles.Research));
        Assert.Equal(plan, world.ReadKept(CouncilRoles.Research, "PLAN-refused-turn-b.md"));
        Assert.Equal(journal, world.ReadKept(CouncilRoles.Research, "JOURNAL-refused-turn-b.md"));
        Assert.Equal(Lines(40, "plan line"), world.Read(CouncilRoles.Research, PublicationKind.Plan));
        Assert.Equal(Lines(100, "2026-09-08 tried something"), world.Read(CouncilRoles.Research, PublicationKind.Journal));
    }

    /// <summary>
    /// AND THE NEXT TURN IS TOLD, in the message it is charged to read. Rendered above the state
    /// lines, because the last sentence of every one of these messages tells the turn to read
    /// `PLAN.md` first and a restored plan read without that warning looks like lost work.
    /// </summary>
    [Fact]
    public void The_next_situation_says_what_was_refused_and_put_back()
    {
        var text = new MissionSituation
        {
            Role = CouncilRoles.Research,
            Restored = ["`trading/PLAN.md` is 80 lines and the limit is 60, so it was not recorded."]
        }.Text();

        var notice = text.IndexOf("`trading/PLAN.md` is 80 lines", StringComparison.Ordinal);
        var state = text.IndexOf("- Trading mode:", StringComparison.Ordinal);

        Assert.True(notice >= 0, "the turn was not told its plan had been put back");
        Assert.True(notice < state, "the restore was rendered below the state lines");
        Assert.Contains("put the version before it back", text);
    }

    /// <summary>
    /// A FILE THAT IS NOT THERE IS NOT A FAILURE. A role that has not written a plan yet has nothing
    /// to refuse and nothing to restore, and a notice on every turn until it does would be the app
    /// nagging an agent about a file it is about to create.
    /// </summary>
    [Fact]
    public void A_role_that_has_written_nothing_yet_is_neither_versioned_nor_told_off()
    {
        using var world = new World();

        Assert.Empty(world.Revisions().Snapshot(CouncilRoles.Operations, "turn-a"));

        Assert.Empty(new PublicationStore(world.Db).By(CouncilRoles.Operations));
        Assert.Empty(WorkspaceRevisions.Notices(world.Db, CouncilRoles.Operations));
    }
}
