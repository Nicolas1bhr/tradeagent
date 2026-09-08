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

        /// <summary>
        /// A NEW connection to the same file. This is what "restart" means here — and a restart
        /// turns every attempt still LAUNCHED into a LOST one, because that is what a meter opening
        /// this database does (<see cref="AiAttemptStore.LoseOpen"/>, called from the
        /// <c>TurnMeter</c> constructor before the app's first relay pass). Without it this world
        /// would be a restart that left the previous process's turns looking alive.
        /// </summary>
        public Database Open()
        {
            var db = new Database(DbFile);
            _open.Add(db);
            new AiAttemptStore(db).LoseOpen(DateTimeOffset.UtcNow);
            return db;
        }

        public CouncilRelay RelayOver(Database db) => new(db, Home);

        public void Write(string role, string file, string content) =>
            File.WriteAllText(Path.Combine(Home(role), WorkspaceBuilder.OutDir, file), content);

        /// <summary>
        /// A LAUNCH THIS APP RECORDED, and the name the turn's output has to carry. The relay
        /// attributes a file by the attempt id in its name and refuses one naming no launch, so a
        /// test that wants a publication has to have a row in <c>ai_attempt</c> first.
        /// </summary>
        public string Launched(Database db, string role, string id)
        {
            new AiAttemptStore(db).Begin(new AiAttempt
            {
                Id = id,
                StartedAt = DateTimeOffset.UtcNow,
                Role = role
            });
            return id;
        }

        /// <summary>The file name one role's output carries for one launch. The app's rule, not the role's.</summary>
        public static string Named(string role, string attempt) =>
            CouncilRelay.Pattern(CouncilRelay.KindFor(role)).Replace("*", attempt);

        /// <summary>Writes one turn's output under the name that turn is required to use.</summary>
        public void WriteFor(string role, string attempt, string content) =>
            Write(role, Named(role, attempt), content);

        /// <summary>What the fence moved aside, oldest name first.</summary>
        public string[] Quarantined(string role)
        {
            var dir = Path.Combine(Home(role), WorkspaceBuilder.OutDir, CouncilRelay.QuarantineDir);
            return Directory.Exists(dir)
                ? [.. Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
                : [];
        }

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
            world.Launched(dying, CouncilRoles.Research, "turn-a");
            world.WriteFor(CouncilRoles.Research, "turn-a", text);
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
        world.Launched(db, CouncilRoles.Research, "turn-a");
        world.WriteFor(CouncilRoles.Research, "turn-a", Report(5));

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

        world.Launched(db, CouncilRoles.Research, "turn-a");
        world.WriteFor(CouncilRoles.Research, "turn-a", Report(CouncilRelay.ReportLines));
        var relay = new CouncilRelay(db, world.Home) { Rejected = rejected.Add };
        relay.Run(CouncilRoles.Research, "turn-a");

        var good = Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));

        world.Launched(db, CouncilRoles.Research, "turn-b");
        world.WriteFor(CouncilRoles.Research, "turn-b", Report(CouncilRelay.ReportLines + 1));
        relay.Run(CouncilRoles.Research, "turn-b");

        Assert.Equal([good.Id], new PublicationStore(db).By(CouncilRoles.Research).Select(p => p.Id));
        Assert.Equal([$"{good.Id}.md"], world.Delivered(CouncilRoles.Operations));
        Assert.Single(rejected);
        Assert.Contains(World.Named(CouncilRoles.Research, "turn-b"), rejected[0]);
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
        world.Launched(db, CouncilRoles.Operations, "turn-a");
        world.WriteFor(CouncilRoles.Operations, "turn-a", agenda);

        world.RelayOver(db).Run(CouncilRoles.Operations, "turn-a");

        var p = Assert.Single(new PublicationStore(db).By(CouncilRoles.Operations));
        Assert.Equal(PublicationKind.Brief, p.Kind);
        Assert.Equal([CouncilRoles.Research], p.RecipientList);
        Assert.Equal(PublicationClass.Council, p.Classification);
        Assert.Equal($"{WorkspaceBuilder.OutDir}/{World.Named(CouncilRoles.Operations, "turn-a")}", p.Source);
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
        world.Launched(db, CouncilRoles.Research, "turn-a");
        world.WriteFor(CouncilRoles.Research, "turn-a", text);

        var relay = world.RelayOver(db);
        relay.Boundary = at => { if (at == "transaction") throw new IOException("killed after the transaction"); };
        Assert.Throws<IOException>(() => relay.Run(CouncilRoles.Research, "turn-a"));

        // The agent tidied up after itself. The app must still be able to deliver.
        File.Delete(Path.Combine(world.Home(CouncilRoles.Research), WorkspaceBuilder.OutDir,
            World.Named(CouncilRoles.Research, "turn-a")));
        world.RelayOver(db).Run();

        var p = Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Equal(text,
            File.ReadAllText(Path.Combine(world.Home(CouncilRoles.Operations),
                WorkspaceBuilder.InDir, $"{p.Id}.md")));
        Assert.Single(Tasks(db));
    }

    // ---- the fence: a file is the work of the launch its NAME carries -------------------------

    /// <summary>
    /// RED FIRST. A turn writes its report and is killed before its own relay pass runs. The pass
    /// that eventually publishes it is the NEXT turn's — and until this unit the publication was
    /// stamped with whichever launch happened to be running the pass, so the record said a turn
    /// that never wrote the file had produced it.
    ///
    /// <para>Provenance that can name the wrong launch is provenance nobody can use: round 4 of
    /// <c>docs/COUNCIL.md</c> lists "which attempt produced an artifact" among the four things that
    /// cannot be recovered afterwards. The work is real, so it is published — under the killed
    /// turn's own id, in the state the restart honestly left that turn in, which is LOST.</para>
    /// </summary>
    [Fact]
    public void A_killed_turns_report_is_published_under_its_own_attempt_not_the_next_turns()
    {
        using var world = new World();
        using var db = world.Open();
        var text = Report(9);

        // The turn that did the work, and was killed before anything published it.
        world.Launched(db, CouncilRoles.Research, "turn-killed");
        world.WriteFor(CouncilRoles.Research, "turn-killed", text);

        // THE RESTART. This is what a meter opening the database does, and it is what makes the
        // killed turn identifiable at all: nobody will ever report its usage, so it is LOST.
        new AiAttemptStore(db).LoseOpen(DateTimeOffset.UtcNow);

        // The next turn runs, and ITS pass is the one that finds the file.
        world.Launched(db, CouncilRoles.Research, "turn-next");
        world.RelayOver(db).Run(CouncilRoles.Research, "turn-next");

        var p = Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Equal(text, p.Content);
        Assert.Equal("turn-killed", p.Attempt);
        Assert.Equal(AiAttemptState.LOST, new AiAttemptStore(db).Get("turn-killed")!.State);
    }

    /// <summary>
    /// THE FENCE. A file naming no launch this app made is never published — it is moved aside, and
    /// the owner is told once.
    ///
    /// <para>Without it a process the app believes is gone can still drop a file into a role's
    /// <c>out/</c> and have the app publish it as that role's work, with a real artifact id and a
    /// delivery and a paid turn behind it. Moved rather than deleted: the bytes are evidence, they
    /// stay in the role's own folder, and the scanner records them where they now are.</para>
    /// </summary>
    [Theory]
    [InlineData("report-.md")]                       // names nothing at all
    [InlineData("report-1.md")]                      // names an id this app never launched
    [InlineData("report-turn-of-the-other-role.md")] // names the chair's launch, in Research's folder
    public void A_file_naming_no_launch_of_this_role_is_quarantined_and_never_published(string file)
    {
        using var world = new World();
        using var db = world.Open();
        var said = new List<string>();

        // FINISHED, not open: a launch that has ended is one the fence would otherwise let through
        // on its state alone, so what refuses this one is that it was made for the OTHER role.
        world.Launched(db, CouncilRoles.Operations, "turn-of-the-other-role");
        new AiAttemptStore(db).LoseOpen(DateTimeOffset.UtcNow);

        world.Write(CouncilRoles.Research, file, Report(4));

        var relay = new CouncilRelay(db, world.Home) { Quarantined = said.Add };
        relay.Run(CouncilRoles.Research, "turn-mine");

        Assert.Empty(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Empty(world.Delivered(CouncilRoles.Operations));
        Assert.Empty(Tasks(db));

        Assert.Equal([file], world.Quarantined(CouncilRoles.Research));
        Assert.False(File.Exists(Path.Combine(world.Home(CouncilRoles.Research),
            WorkspaceBuilder.OutDir, file)));
        Assert.Contains(file, Assert.Single(said));

        // AND IT IS NOT READ AGAIN. A refused file left where it was would be read, refused and
        // logged on every turn for the rest of the installation's life.
        said.Clear();
        relay.Run(CouncilRoles.Research, "turn-mine");
        Assert.Empty(said);
    }

    /// <summary>
    /// THE TURN IS TOLD THE NAME, AND THE NAME ROUND-TRIPS. The fence is only fair if the message
    /// the turn is charged to read says which id its output must carry — an agent that is not told
    /// cannot write a file this app will publish — and the name the Situation gives has to be
    /// exactly the one the relay reads back.
    /// </summary>
    [Theory]
    [InlineData(CouncilRoles.Research)]
    [InlineData(CouncilRoles.Operations)]
    public void The_situation_names_the_attempt_and_the_file_name_it_gives_round_trips(string role)
    {
        const string attempt = "turn-20260908120000000-abcdef";
        var name = MissionSituation.OutputName(role, attempt);

        var text = new MissionSituation { Role = role, Attempt = attempt }.Text();
        Assert.Contains($"This turn is attempt `{attempt}`", text);
        Assert.Contains($"{WorkspaceBuilder.OutDir}/{name}", text);

        Assert.Equal(attempt, CouncilRelay.AttemptIn(name, CouncilRelay.KindFor(role)));

        // And a Situation with no id names none, rather than inventing one nothing will recognise.
        Assert.DoesNotContain("This turn is attempt", new MissionSituation { Role = role }.Text());
    }

    /// <summary>
    /// THE STALE-PROCESS CASE, which is the one the pass's own launch id is still needed for. A row
    /// still LAUNCHED that is not the turn whose pass this is belongs to a process the app is not
    /// accounting for — its own restart turns every open row LOST before the first pass — so a file
    /// naming one arrived from somewhere the app cannot vouch for.
    /// </summary>
    [Fact]
    public void A_file_naming_a_launch_that_is_still_open_and_is_not_this_pass_is_quarantined()
    {
        using var world = new World();
        using var db = world.Open();

        world.Launched(db, CouncilRoles.Research, "turn-open");
        world.WriteFor(CouncilRoles.Research, "turn-open", Report(4));

        world.Launched(db, CouncilRoles.Research, "turn-mine");
        world.RelayOver(db).Run(CouncilRoles.Research, "turn-mine");

        Assert.Empty(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Equal([World.Named(CouncilRoles.Research, "turn-open")],
            world.Quarantined(CouncilRoles.Research));
    }
}
