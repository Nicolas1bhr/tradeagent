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
        world.RelayOver(restarted).Reconcile();

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
        world.RelayOver(db).Reconcile();

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
        world.RelayOver(db).Reconcile();

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
    /// ITEM 4, RED FIRST: THE PASS THAT WALKS EVERY ROLE LEAVES A LIVE TURN'S FILE WHERE IT IS.
    ///
    /// <para>The whole-council pass used to be what <c>Run</c> did when it was given no role, and it
    /// named no launch, so every open row looked like a stale process's. With two roles able to turn
    /// at once that is the ordinary case and not the exotic one: the Research Director is mid-turn,
    /// its report is in its own <c>out/</c> under its own open launch, and a pass run for any other
    /// reason moved it to <c>quarantine/</c>. The turn's work taken away from it by somebody else's
    /// bookkeeping, and the fence saying out loud that it was not a turn TradeAgent had finished —
    /// which was true and beside the point.</para>
    ///
    /// <para>The file is neither published nor moved. It is left, and the role's own commit
    /// publishes it a moment later, under its own launch.</para>
    /// </summary>
    [Fact]
    public void A_pass_over_every_role_leaves_a_live_turns_file_rather_than_quarantining_it()
    {
        using var world = new World();
        using var db = world.Open();
        var live = new LiveAttempts();
        var said = new List<string>();

        // The Research Director is mid-turn: its launch is open and THIS process is flying it.
        world.Launched(db, CouncilRoles.Research, "turn-b");
        live.Enter("turn-b");
        world.WriteFor(CouncilRoles.Research, "turn-b", Report(4));

        // The start-up reconciliation, which is the one pass that looks in every role's folder.
        new CouncilRelay(db, world.Home, live: live) { Quarantined = said.Add }.Reconcile();

        Assert.Empty(world.Quarantined(CouncilRoles.Research));
        Assert.Empty(said);
        Assert.Empty(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.True(File.Exists(Path.Combine(world.Home(CouncilRoles.Research),
            WorkspaceBuilder.OutDir, World.Named(CouncilRoles.Research, "turn-b"))));

        // AND THE ROLE'S OWN PASS PUBLISHES IT, under its own launch, a moment later.
        new CouncilRelay(db, world.Home, live: live).Run(CouncilRoles.Research, "turn-b");
        Assert.Equal("turn-b", Assert.Single(new PublicationStore(db).By(CouncilRoles.Research)).Attempt);
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

    // ---- the consequential boundary: two sealed assessments, one bounded challenge ---------------
    //
    // U-council-concurrent-2 items 2 and 3. They are HERE rather than beside the store's own tests
    // because the seal is only worth anything end to end: what :61 forbids is a FILE reaching the peer's
    // `in/` before it has written its own, and only the relay puts files there.

    /// <summary>A boundary for the relay's tests to answer, opened the way the referee opens one.</summary>
    static string Boundary(Database db, string entity = "version-a") =>
        new CouncilBoundaries(db).Open(BoundaryKind.Promotion, entity, 1, BoundaryDisposition.Deploy,
            "holdout run 9f3c under campaign 1", DateTimeOffset.UtcNow).Row.Id;

    static string AssessmentName(string attempt) =>
        CouncilRelay.Pattern(PublicationKind.Assessment).Replace("*", attempt);

    /// <summary>
    /// An assessment's text WITH the two declarations schema 21 requires of one — the recommendation and
    /// the forecast (<c>BoundaryDeclaration</c>). An assessment that declares neither is refused, and
    /// nothing in this class is about that refusal.
    /// </summary>
    static string Assessed(string text) =>
        $"{BoundaryDeclaration.RecommendationPrefix} {BoundaryDisposition.Deploy}\n"
        + $"{BoundaryDeclaration.BaselinePrefix} {PromotionState.Unjudged}\n{text}";

    static string ChallengeName(string attempt) =>
        CouncilRelay.Pattern(PublicationKind.Challenge).Replace("*", attempt);

    /// <summary>What the relay said it would not publish, in the app's own words.</summary>
    static CouncilRelay Relaying(World world, Database db, List<string> refusals)
    {
        var relay = world.RelayOver(db);
        relay.Rejected = why => refusals.Add(why);
        return relay;
    }

    /// <summary>
    /// RED FIRST: the first assessment reaches the second director's <c>in/</c> before they have written
    /// theirs.
    ///
    /// <para><c>docs/COUNCIL.md</c>:61 — "both directors submit an assessment BEFORE either sees the
    /// other's". Everything the pair is for is gone the moment one of them can read the other first: the
    /// second assessment is then a response, the two are not independent evidence, and whichever director
    /// happens to be slower is the only one whose reading is its own.</para>
    ///
    /// <para>The seal is asserted as the app HOLDING it, not as a race the second director happens to
    /// lose: the publication is committed, hashed and attributed the moment it arrives — it cannot be
    /// revised — and its DELIVERY is <c>withheld</c>. Then both are released by one pass and both files
    /// land together.</para>
    /// </summary>
    [Fact]
    public void An_assessment_is_committed_at_once_and_withheld_until_the_other_directors_exists()
    {
        using var world = new World();
        using var db = world.Open();
        Boundary(db);
        var refusals = new List<string>();

        // ---- the Research Director writes first ------------------------------------------------
        world.Launched(db, CouncilRoles.Research, "turn-r");
        world.Write(CouncilRoles.Research, AssessmentName("turn-r"), Assessed("I read the evidence as sufficient."));
        Relaying(world, db, refusals).Run(CouncilRoles.Research, "turn-r");

        var store = new PublicationStore(db);
        var mine = Assert.Single(store.By(CouncilRoles.Research));
        Assert.Equal(PublicationKind.Assessment, mine.Kind);

        // COMMITTED, AND NOT HANDED OVER. Nothing in the chair's folder, nothing it is paid to read.
        Assert.Empty(world.Delivered(CouncilRoles.Operations));
        Assert.Equal(DeliveryState.Withheld,
            Assert.Single(store.To(CouncilRoles.Operations)).State);
        Assert.Empty(new MissionEventStore(db).OfKind(PublicationKind.Assessment));
        Assert.Empty(refusals);

        // ---- and now the chair writes its own ---------------------------------------------------
        world.Launched(db, CouncilRoles.Operations, "turn-o");
        world.Write(CouncilRoles.Operations, AssessmentName("turn-o"), Assessed("I read it as thin."));
        Relaying(world, db, refusals).Run(CouncilRoles.Operations, "turn-o");

        // BOTH RELEASED TOGETHER, BY ONE PASS.
        Assert.Single(world.Delivered(CouncilRoles.Operations));
        Assert.Single(world.Delivered(CouncilRoles.Research));
        Assert.All(store.To(CouncilRoles.Operations).Concat(store.To(CouncilRoles.Research)),
            d => Assert.Equal(DeliveryState.Delivered, d.State));
        Assert.Empty(refusals);
    }

    /// <summary>
    /// A SECOND ASSESSMENT FROM THE SAME DIRECTOR OVER THE SAME BOUNDARY IS REFUSED, and refused means
    /// nothing published, nothing delivered and nobody charged a turn.
    ///
    /// <para>An assessment that could be revised is not sealed evidence of what that director thought
    /// before it saw the other's — it is a draft, and :212's precommitment is exactly the thing that
    /// cannot be recovered afterwards if it is not recorded at the time.</para>
    /// </summary>
    [Fact]
    public void A_second_assessment_from_one_director_over_one_boundary_is_refused_and_unpaid()
    {
        using var world = new World();
        using var db = world.Open();
        Boundary(db);
        var refusals = new List<string>();

        world.Launched(db, CouncilRoles.Research, "turn-r1");
        world.Write(CouncilRoles.Research, AssessmentName("turn-r1"), Assessed("my reading."));
        Relaying(world, db, refusals).Run(CouncilRoles.Research, "turn-r1");
        Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));

        world.Launched(db, CouncilRoles.Research, "turn-r2");
        world.Write(CouncilRoles.Research, AssessmentName("turn-r2"), Assessed("on reflection, the opposite."));
        Relaying(world, db, refusals).Run(CouncilRoles.Research, "turn-r2");

        Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Empty(world.Delivered(CouncilRoles.Operations));
        var said = Assert.Single(refusals);
        Assert.Contains("was not published", said, StringComparison.Ordinal);
        Assert.Contains("cannot be revised", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE BOUNDED CHALLENGE PER BOUNDARY, FROM EITHER DIRECTOR, AND THE SECOND IS REFUSED AND UNPAID.
    ///
    /// <para>:61 gives a boundary "one bounded challenge", and the count is per BOUNDARY and not per
    /// director: two challenges are a debate, and a debate at the strongest model is the standup the
    /// doctrine refuses to pay for. Unlike an assessment it is DELIVERED at once — it buys the peer the
    /// one turn it is worth, because a challenge nobody is woken for is a challenge answered after the
    /// deadline.</para>
    /// </summary>
    [Fact]
    public void One_challenge_per_boundary_is_delivered_and_the_second_is_refused_and_unpaid()
    {
        using var world = new World();
        using var db = world.Open();
        var boundary = Boundary(db);
        var refusals = new List<string>();
        var store = new PublicationStore(db);

        foreach (var (role, turn) in new[] { (CouncilRoles.Research, "a-r"), (CouncilRoles.Operations, "a-o") })
        {
            world.Launched(db, role, turn);
            world.Write(role, AssessmentName(turn), Assessed($"{role} assessed it."));
            Relaying(world, db, refusals).Run(role, turn);
        }

        // THE FIRST CHALLENGE, from the chair. Delivered, and it buys Research one turn.
        world.Launched(db, CouncilRoles.Operations, "c-o");
        world.Write(CouncilRoles.Operations, ChallengeName("c-o"), "the sample is one campaign wide.");
        Relaying(world, db, refusals).Run(CouncilRoles.Operations, "c-o");

        Assert.Single(new MissionEventStore(db).OfKind(PublicationKind.Challenge));
        Assert.NotNull(new CouncilBoundaries(db).Challenged(boundary));

        // THE SECOND, from the OTHER director. One per boundary, so it makes no difference who writes it.
        world.Launched(db, CouncilRoles.Research, "c-r");
        world.Write(CouncilRoles.Research, ChallengeName("c-r"), "and the fees were not declared.");
        Relaying(world, db, refusals).Run(CouncilRoles.Research, "c-r");

        Assert.Single(new MissionEventStore(db).OfKind(PublicationKind.Challenge));
        Assert.Single(store.By(CouncilRoles.Research));           // its assessment, and nothing else
        var said = Assert.Single(refusals);
        Assert.Contains("one bounded challenge is what a boundary gets", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CHALLENGE WRITTEN BEFORE BOTH ASSESSMENTS ARE DELIVERED IS NOT PUBLISHED. A reading written
    /// before the peer's has arrived is a second assessment under another name, and taking it would let a
    /// director spend the boundary's one challenge on something it was never for.
    /// </summary>
    [Fact]
    public void A_challenge_before_both_assessments_are_delivered_is_refused()
    {
        using var world = new World();
        using var db = world.Open();
        Boundary(db);
        var refusals = new List<string>();

        world.Launched(db, CouncilRoles.Research, "a-r");
        world.Write(CouncilRoles.Research, AssessmentName("a-r"), Assessed("my reading."));
        world.Write(CouncilRoles.Research, ChallengeName("a-r"), "and my challenge.");
        Relaying(world, db, refusals).Run(CouncilRoles.Research, "a-r");

        Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Empty(new MissionEventStore(db).OfKind(PublicationKind.Challenge));
        Assert.Contains("BOTH assessments have been delivered",
            Assert.Single(refusals), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CHALLENGE IS CAPPED LIKE A REPORT — this is the mutant. An unbounded challenge is a transcript
    /// handed to the peer at the owner's expense, which is the one thing context etiquette exists to stop
    /// (<c>docs/COUNCIL.md</c>, "a report of at most 20 lines out").
    /// </summary>
    [Fact]
    public void An_over_length_challenge_is_not_published()
    {
        using var world = new World();
        using var db = world.Open();
        Boundary(db);
        var refusals = new List<string>();

        foreach (var (role, turn) in new[] { (CouncilRoles.Research, "a-r"), (CouncilRoles.Operations, "a-o") })
        {
            world.Launched(db, role, turn);
            world.Write(role, AssessmentName(turn), Assessed($"{role} assessed it."));
            Relaying(world, db, refusals).Run(role, turn);
        }

        world.Launched(db, CouncilRoles.Operations, "c-o");
        world.Write(CouncilRoles.Operations, ChallengeName("c-o"), Report(CouncilRelay.ReportLines + 1));
        Relaying(world, db, refusals).Run(CouncilRoles.Operations, "c-o");

        Assert.Empty(new MissionEventStore(db).OfKind(PublicationKind.Challenge));
        Assert.Null(new CouncilBoundaries(db).Challenged(
            BoundaryIds.Of(BoundaryKind.Promotion, "version-a", 1)));
        Assert.Contains($"is longer than {CouncilRelay.ReportLines} lines",
            Assert.Single(refusals), StringComparison.Ordinal);
    }
}
