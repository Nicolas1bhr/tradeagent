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

        public void Dispose() => Db.Dispose();
    }

    static string Lines(int n, string what) =>
        string.Join("\n", Enumerable.Range(1, n).Select(i => $"{what} {i}"));

    // ---- the revision --------------------------------------------------------------------------

    /// <summary>
    /// THE EXACT SCHEMA PIN, which lives with the migration that last moved it. Schema 11 adds no
    /// column — a revision of a role's plan IS an artifact, and a second table would put two answers
    /// to "what did this role publish" in two places — it adds the index the restore reads, and the
    /// row on disk must equal what this build writes or an upgrade did not run.
    /// </summary>
    [Fact]
    public void The_plan_and_journal_revisions_arrive_at_schema_eleven()
    {
        using var db = TestEnv.NewDb();
        Assert.Equal(11, Versions.DatabaseSchemaVersion);
        Assert.Equal("11", db.Read(_ =>
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
    /// THE FIRST TURN'S FAILURE. A role whose very first plan is over the cap has no earlier
    /// revision to restore, and inventing one would be the app writing a plan the role never held.
    /// It is refused, nothing is written back, and the notice says exactly that.
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
        Assert.Contains("no earlier revision",
            Assert.Single(WorkspaceRevisions.Notices(world.Db, CouncilRoles.Research)));
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
