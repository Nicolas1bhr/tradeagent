using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE UNIT'S PROPERTY (<c>docs/COUNCIL.md</c>, the <c>U-council-thin</c> line): <b>a crash at each
/// handoff boundary recovers to exactly one committed report-to-Operations task, neither lost nor
/// doubled.</b>
///
/// <para>The handoff has three boundaries and each is a different failure. After the ROLE'S FILE is
/// written and before anything is committed, a crash must not lose the report. After the
/// TRANSACTION and before the copy, a crash must not leave the chair with a task pointing at a file
/// that is not there. After the COPY and before the delivery is marked, a crash must not produce a
/// second file or a second paid turn on the next pass.</para>
///
/// <para>All three converge because the artifact's identity is a hash of what is in it and the
/// task's id is keyed by that artifact. The mutant below takes that away — the task keyed by the
/// launch that produced it — and the recovery produces two.</para>
/// </summary>
public class CouncilRelayTests
{
    /// <summary>
    /// A world with two role folders and a database FILE, so that "a fresh host over the same
    /// database" is a genuinely new <see cref="Database"/> over the same bytes rather than the same
    /// object under another name.
    /// </summary>
    sealed class World : IDisposable
    {
        public World()
        {
            Root = Path.Combine(TestEnv.Home, $"relay-{Guid.NewGuid():n}");
            DbFile = Path.Combine(Root, "state.db");
            foreach (var role in CouncilRoles.All)
            {
                Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.InDir));
                Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.OutDir));
            }
        }

        public string Root { get; }
        public string DbFile { get; }
        readonly List<Database> _open = [];

        public string Home(string role) => WorkspaceBuilder.HomeOf(Root, role);

        /// <summary>A NEW connection to the same file. This is what "restart" means here.</summary>
        public Database Open()
        {
            var db = new Database(DbFile);
            _open.Add(db);
            return db;
        }

        public CouncilRelay RelayOver(Database db) => new(db, Home);

        public void Write(string role, string file, string content) =>
            File.WriteAllText(Path.Combine(Home(role), WorkspaceBuilder.OutDir, file), content);

        public string[] Delivered(string role) =>
            [.. Directory.EnumerateFiles(Path.Combine(Home(role), WorkspaceBuilder.InDir), "*.md")
                .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

        public void Dispose() { foreach (var db in _open) db.Dispose(); }
    }

    static string Report(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => $"line {i}: what I found"));

    static List<MissionEvent> Tasks(Database db) =>
        [.. new MissionEventStore(db).OfKind(MissionEventKind.Report)];

    // ---- the property ------------------------------------------------------------------------

    /// <summary>
    /// RED FIRST, THREE TIMES OVER: one run per boundary, each killing the relay exactly there and
    /// then starting a fresh host over the same database.
    ///
    /// It was red before this unit for the simplest possible reason — there was no relay at all, so
    /// a report written by one role reached the other zero times out of three. What the test pins is
    /// not that the relay exists but that its recovery is EXACTLY ONE at every boundary: one
    /// publication, one file in the chair's <c>in/</c>, one <c>report</c> wake, and a delivery that
    /// says delivered only because the file is really there.
    /// </summary>
    [Theory]
    [InlineData("file")]
    [InlineData("transaction")]
    [InlineData("copy")]
    public void A_crash_at_each_boundary_recovers_to_exactly_one_task_and_one_file(string boundary)
    {
        using var world = new World();
        var text = Report(12);

        // ---- the host that dies ------------------------------------------------------------
        using (var dying = world.Open())
        {
            world.Write(CouncilRoles.Research, "report-1.md", text);
            var relay = world.RelayOver(dying);
            relay.Boundary = at => { if (at == boundary) throw new IOException($"killed after the {at}"); };

            Assert.Throws<IOException>(() => relay.Run(CouncilRoles.Research, "turn-a"));
        }

        // ---- the fresh host over the same database -----------------------------------------
        using var restarted = world.Open();
        world.RelayOver(restarted).Run();

        var tasks = Tasks(restarted);
        Assert.Single(tasks);
        Assert.Equal(CouncilRoles.Operations, tasks[0].For);

        var store = new PublicationStore(restarted);
        var published = store.By(CouncilRoles.Research);
        Assert.Single(published);
        Assert.Equal(MissionEventIds.Task(published[0].Id), tasks[0].Id);
        Assert.Equal(text, published[0].Content);

        Assert.Equal([$"{published[0].Id}.md"], world.Delivered(CouncilRoles.Operations));
        Assert.Equal(text,
            File.ReadAllText(Path.Combine(world.Home(CouncilRoles.Operations),
                WorkspaceBuilder.InDir, $"{published[0].Id}.md")));

        var delivery = Assert.Single(store.To(CouncilRoles.Operations));
        Assert.Equal(DeliveryState.Delivered, delivery.State);

        // AND STILL ONE after the pass everything runs anyway — the loop relays after every turn.
        world.RelayOver(restarted).Run(CouncilRoles.Research, "turn-b");
        Assert.Single(Tasks(restarted));
        Assert.Single(store.By(CouncilRoles.Research));
        Assert.Single(world.Delivered(CouncilRoles.Operations));
    }

    /// <summary>
    /// THE MUTANT'S TEST, STATED AS THE PLAIN CASE: the relay running twice over the same file — a
    /// restart, or simply the next turn — publishes once and costs one paid turn.
    ///
    /// Key the task by the launch that produced it instead of by the artifact and this is where it
    /// shows: the second pass has a different attempt (or none at all), so it raises a second
    /// <c>report</c> wake, and the chair is charged for reading the same report again, for ever.
    /// </summary>
    [Fact]
    public void Publishing_the_same_report_twice_is_one_publication_one_file_and_one_task()
    {
        using var world = new World();
        using var db = world.Open();
        world.Write(CouncilRoles.Research, "report-1.md", Report(5));

        world.RelayOver(db).Run(CouncilRoles.Research, "turn-a");
        world.RelayOver(db).Run(CouncilRoles.Research, "turn-b");
        world.RelayOver(db).Run();

        Assert.Single(Tasks(db));
        Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Single(world.Delivered(CouncilRoles.Operations));
    }

    /// <summary>
    /// THE SIZE BUDGET IS THE APP'S AND IT IS REAL. A report over twenty lines is not published, and
    /// the last valid one stands — <c>docs/COUNCIL.md</c>, "an invalid publication is rejected and
    /// the last valid plan and index stand". Asked for in the mission file, enforced here.
    /// </summary>
    [Fact]
    public void A_report_over_twenty_lines_is_rejected_and_the_last_valid_one_stands()
    {
        using var world = new World();
        using var db = world.Open();
        var rejected = new List<string>();

        world.Write(CouncilRoles.Research, "report-1.md", Report(CouncilRelay.ReportLines));
        var relay = new CouncilRelay(db, world.Home) { Rejected = rejected.Add };
        relay.Run(CouncilRoles.Research, "turn-a");

        var good = Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));

        world.Write(CouncilRoles.Research, "report-2.md", Report(CouncilRelay.ReportLines + 1));
        relay.Run(CouncilRoles.Research, "turn-b");

        Assert.Equal([good.Id], new PublicationStore(db).By(CouncilRoles.Research).Select(p => p.Id));
        Assert.Equal([$"{good.Id}.md"], world.Delivered(CouncilRoles.Operations));
        Assert.Single(rejected);
        Assert.Contains("report-2.md", rejected[0]);
    }

    /// <summary>
    /// THE REVERSE PATH. The chair's agenda goes down to Research as a <c>brief</c>, at most forty
    /// lines, by the same three steps — and a role does not choose its own audience: the recipient
    /// is a function of the role, decided by the app.
    /// </summary>
    [Fact]
    public void The_chairs_agenda_is_published_as_a_brief_and_delivered_to_research()
    {
        using var world = new World();
        using var db = world.Open();
        var agenda = Report(35);
        world.Write(CouncilRoles.Operations, "agenda-1.md", agenda);

        world.RelayOver(db).Run(CouncilRoles.Operations, "turn-a");

        var p = Assert.Single(new PublicationStore(db).By(CouncilRoles.Operations));
        Assert.Equal(PublicationKind.Brief, p.Kind);
        Assert.Equal([CouncilRoles.Research], p.RecipientList);
        Assert.Equal(PublicationClass.Council, p.Classification);
        Assert.Equal($"{WorkspaceBuilder.OutDir}/agenda-1.md", p.Source);
        Assert.Equal("turn-a", p.Attempt);

        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Brief));
        Assert.Equal(CouncilRoles.Research, wake.For);
        Assert.Equal(MissionEventIds.ForRole(MissionEventIds.Task(p.Id), CouncilRoles.Research), wake.Id);

        Assert.Equal([$"{p.Id}.md"], world.Delivered(CouncilRoles.Research));
        Assert.Empty(world.Delivered(CouncilRoles.Operations));
    }

    /// <summary>
    /// THE COPY IS RE-MADE FROM THE APP'S OWN RECORD, not from the publishing role's file. That file
    /// belongs to the agent: it may be rewritten, moved or deleted between the commit and the copy,
    /// and a delivery that can only be re-made from it is a delivery a crash loses.
    /// </summary>
    [Fact]
    public void A_delivery_whose_file_was_deleted_is_re_copied_from_the_publication()
    {
        using var world = new World();
        using var db = world.Open();
        var text = Report(6);
        world.Write(CouncilRoles.Research, "report-1.md", text);

        var relay = world.RelayOver(db);
        relay.Boundary = at => { if (at == "transaction") throw new IOException("killed after the transaction"); };
        Assert.Throws<IOException>(() => relay.Run(CouncilRoles.Research, "turn-a"));

        // The agent tidied up after itself. The app must still be able to deliver.
        File.Delete(Path.Combine(world.Home(CouncilRoles.Research), WorkspaceBuilder.OutDir, "report-1.md"));
        world.RelayOver(db).Run();

        var p = Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Equal(text,
            File.ReadAllText(Path.Combine(world.Home(CouncilRoles.Operations),
                WorkspaceBuilder.InDir, $"{p.Id}.md")));
        Assert.Single(Tasks(db));
    }
}
