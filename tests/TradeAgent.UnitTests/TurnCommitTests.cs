using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE UNIT'S PROPERTY (<c>docs/COUNCIL.md</c> rule 6, and the <c>U-turn-commit</c> line): <b>a turn
/// ends in ONE committed transition — the launch record closed, what the turn published, its memory
/// versioned, and what became of its wakes — so a crash either side of it leaves the turn in one
/// identifiable state and never in half of two.</b>
///
/// <para>Four writes used to happen in a row: <c>AiAttemptStore.End</c> part-way through the turn,
/// then the relay's publications, then the plan and journal, then the dispositions. Two windows sat
/// between them. A crash in the first leaves an attempt recorded as ENDED whose work is nowhere —
/// the record says a turn finished and produced nothing, which is not what happened. A crash in the
/// second leaves work published against a turn that never ended and wakes still owed an answer, so
/// the next turn is charged for them again.</para>
///
/// <para>Both runs below inject a real exception at a real boundary and then open a NEW
/// <see cref="Database"/> over the same file — a genuine restart, not the same object under another
/// name — and run the pass the app runs on start.</para>
/// </summary>
public class TurnCommitTests
{
    const string Report = "the 1-minute bars have a 40-minute gap on 2026-03-09";
    const string Plan = "find out whether the gap is in the archive or the download";
    const string Journal = "2026-09-08 downloaded March, counted the bars, found the gap";

    sealed class World : IDisposable
    {
        public World()
        {
            Root = Path.Combine(TestEnv.Home, $"commit-{Guid.NewGuid():n}");
            DbFile = Path.Combine(Root, "state.db");
            Records = Path.Combine(Root, "turns.jsonl");
            foreach (var role in CouncilRoles.All)
            {
                Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.InDir));
                Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.OutDir));
                Directory.CreateDirectory(Path.Combine(Home(role), "trading"));
            }
        }

        public string Root { get; }
        public string DbFile { get; }
        public string Records { get; }
        public DateTimeOffset At { get; } = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        readonly List<Database> _open = [];

        public string Home(string role) => WorkspaceBuilder.HomeOf(Root, role);

        /// <summary>
        /// A NEW connection to the same file, and the meter that opens over it — which is what turns
        /// every attempt still LAUNCHED into a LOST one. This is what "restart" means here.
        /// </summary>
        public (Database Db, TurnMeter Meter) Open()
        {
            var db = new Database(DbFile);
            _open.Add(db);
            return (db, new TurnMeter(db, () => 50m, runtimeId: () => "codex", now: () => At,
                recordPath: Records, owner: () => new OwnerPrice(1m, 4m), model: () => "a-model"));
        }

        public void Write(string role, string rel, string text) =>
            File.WriteAllText(Path.Combine(Home(role), rel.Replace('/', Path.DirectorySeparatorChar)), text);

        public string Read(string role, string rel) =>
            File.ReadAllText(Path.Combine(Home(role), rel.Replace('/', Path.DirectorySeparatorChar)));

        public CouncilRelay RelayOver(Database db) => new(db, Home, () => At);

        public void Dispose() { foreach (var db in _open) db.Dispose(); }
    }

    static AgentTurnEnded Ended(DateTimeOffset at) =>
        new(0, TimeSpan.FromSeconds(9), "…", at) { Usage = new TurnUsage(1000, 0, 0, 100, 0, null) };

    static List<Publication> Of(Database db, string kind) =>
        [.. new PublicationStore(db).By(CouncilRoles.Research).Where(p => p.Kind == kind)];

    // ---- the property --------------------------------------------------------------------------

    /// <summary>
    /// RED FIRST, TWICE OVER: one run per side of the commit.
    ///
    /// <para>Before this item the end of a turn was several commits and the boundary between them
    /// was a window. Killed before the publications, the attempt was already ENDED and its work was
    /// nowhere; killed after them, the wakes it consumed were still unsettled and the next turn paid
    /// for them again. What the theory pins is that neither half exists: the turn's state and its
    /// output move together, and the ONE thing a crash can leave behind is a launch nobody closed —
    /// which the next start turns LOST and reconciles, publishing that turn's files under it because
    /// a file's provenance is the launch its NAME carries.</para>
    /// </summary>
    [Theory]
    [InlineData(CouncilRelay.BeforeCommit)]
    [InlineData(CouncilRelay.AfterCommit)]
    public void A_crash_either_side_of_the_commit_leaves_one_revision_per_file_and_one_state(string boundary)
    {
        using var world = new World();
        string attempt;

        // ---- the host that dies ---------------------------------------------------------------
        {
            var (dying, meter) = world.Open();
            var wakes = new MissionEventStore(dying);
            wakes.Raise("review:1", MissionEventKind.Review, world.At, role: CouncilRoles.Research);

            attempt = meter.Mint();
            Assert.Equal(attempt, meter.Begin("## Situation", ["review:1"], CouncilRoles.Research));

            world.Write(CouncilRoles.Research, $"{WorkspaceBuilder.OutDir}/report-{attempt}.md", Report);
            world.Write(CouncilRoles.Research, "trading/PLAN.md", Plan);
            world.Write(CouncilRoles.Research, "trading/JOURNAL.md", Journal);

            meter.Record(Ended(world.At));           // the runtime reported; the close is held

            var relay = world.RelayOver(dying);
            relay.Boundary = at => { if (at == boundary) throw new IOException($"killed at the {at}"); };

            Assert.Throws<IOException>(() => relay.CommitTurn(CouncilRoles.Research, attempt,
                () => meter.CommitStaged(),
                () => wakes.Settle("review:1", MissionEventDisposition.Answered)));

            dying.Dispose();
        }

        // ---- the fresh host over the same database, and the pass it runs on start --------------
        var (restarted, _) = world.Open();
        world.RelayOver(restarted).Run();

        // EXACTLY ONE REVISION PER FILE. Not none — the work is real and the bytes are on disk —
        // and not two, because a publication's id is the hash of its content.
        var report = Assert.Single(Of(restarted, PublicationKind.Report));
        Assert.Equal(Report, report.Content);
        Assert.Equal(attempt, report.Attempt);
        Assert.Equal(Plan, Assert.Single(Of(restarted, PublicationKind.Plan)).Content);
        Assert.Equal(Journal, Assert.Single(Of(restarted, PublicationKind.Journal)).Content);

        // AND ONE FILE IN THE CHAIR'S FOLDER, AND ONE PAID TASK.
        Assert.Single(new PublicationStore(restarted).To(CouncilRoles.Operations));
        Assert.Single(new MissionEventStore(restarted).OfKind(MissionEventKind.Report));

        // ---- the turn is in ONE state, and the wake agrees with it -----------------------------
        // Killed before the commit: nothing of the transition landed, so the launch is the one thing
        // left over and the restart calls it what it is. Killed after it: everything landed. There
        // is no third reading in which the attempt ended and its wake is still owed an answer, or in
        // which the wake was answered by a turn the ledger never closed.
        var row = new AiAttemptStore(restarted).Get(attempt)!;
        var wake = new MissionEventStore(restarted).OfKind(MissionEventKind.Review)[0];

        if (boundary == CouncilRelay.BeforeCommit)
        {
            Assert.Equal(AiAttemptState.LOST, row.State);
            Assert.Equal(row.ReservedCost, row.Cost);       // the reservation stands; it never comes back
            Assert.Null(wake.Disposition);
        }
        else
        {
            Assert.Equal(AiAttemptState.ENDED, row.State);
            Assert.Equal(MissionEventDisposition.Answered, wake.Disposition);
        }

        // Whichever it was, the turn is never ENDED with its work unrecorded.
        Assert.False(row.State == AiAttemptState.ENDED && Of(restarted, PublicationKind.Report).Count == 0);
    }

    /// <summary>
    /// AND WHEN NOTHING CRASHES, the same four things land together and the copy follows. This is
    /// the plain case the theory above is the failure mode of.
    /// </summary>
    [Fact]
    public void A_turn_that_commits_ends_its_launch_publishes_its_work_and_settles_its_wake_at_once()
    {
        using var world = new World();
        var (db, meter) = world.Open();
        var wakes = new MissionEventStore(db);
        wakes.Raise("review:1", MissionEventKind.Review, world.At, role: CouncilRoles.Research);

        var attempt = meter.Mint();
        meter.Begin("## Situation", ["review:1"], CouncilRoles.Research);
        world.Write(CouncilRoles.Research, $"{WorkspaceBuilder.OutDir}/report-{attempt}.md", Report);
        world.Write(CouncilRoles.Research, "trading/PLAN.md", Plan);
        meter.Record(Ended(world.At));

        world.RelayOver(db).CommitTurn(CouncilRoles.Research, attempt,
            () => meter.CommitStaged(),
            () => wakes.Settle("review:1", MissionEventDisposition.Answered));

        var row = new AiAttemptStore(db).Get(attempt)!;
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(attempt, Assert.Single(Of(db, PublicationKind.Report)).Attempt);
        Assert.Equal(attempt, Assert.Single(Of(db, PublicationKind.Plan)).Attempt);
        Assert.Equal(MissionEventDisposition.Answered,
            new MissionEventStore(db).OfKind(MissionEventKind.Review)[0].Disposition);

        var delivered = Assert.Single(new PublicationStore(db).To(CouncilRoles.Operations));
        Assert.Equal(DeliveryState.Delivered, delivered.State);
        Assert.Equal(Report, File.ReadAllText(Path.Combine(world.Home(CouncilRoles.Operations),
            WorkspaceBuilder.InDir, $"{delivered.PublicationId}.md")));
    }

    /// <summary>
    /// THREE ARTIFACTS IN ONE TRANSACTION GET THREE REVISION NUMBERS. <c>NextRevision</c> is read
    /// inside the insert's own transaction, not by the caller before it opens — which is what the
    /// revision number has to be for it to mean anything.
    ///
    /// <para>A turn publishes up to three things at once: its report, its plan and its journal. A
    /// number read once before the transaction is a number every one of them is handed, and the
    /// role's history then holds three rows all calling themselves its first — with the restore
    /// ("the last valid plan stands") reading a MAX that three rows are tied on. The count is what
    /// is asserted, because it is the ordering the table is read by.</para>
    /// </summary>
    [Fact]
    public void Three_publications_committed_in_one_turn_are_numbered_one_two_and_three()
    {
        using var world = new World();
        var (db, meter) = world.Open();

        var attempt = meter.Mint();
        meter.Begin("## Situation", [], CouncilRoles.Research);
        world.Write(CouncilRoles.Research, $"{WorkspaceBuilder.OutDir}/report-{attempt}.md", Report);
        world.Write(CouncilRoles.Research, "trading/PLAN.md", Plan);
        world.Write(CouncilRoles.Research, "trading/JOURNAL.md", Journal);
        meter.Record(Ended(world.At));

        world.RelayOver(db).CommitTurn(CouncilRoles.Research, attempt,
            () => meter.CommitStaged(), () => { });

        Assert.Equal([1, 2, 3],
            new PublicationStore(db).By(CouncilRoles.Research).Select(p => p.Revision));
    }

    /// <summary>
    /// THE WHOLE TRANSITION ROLLS BACK TOGETHER. A failure raised while the transaction is open —
    /// here from the dispositions, the last step — must leave nothing of the turn behind, not the
    /// publications that had already been inserted and not the closed launch record. That is the
    /// difference between one transaction and four that happen to be adjacent.
    /// </summary>
    [Fact]
    public void A_failure_inside_the_transaction_undoes_every_part_of_the_turn()
    {
        using var world = new World();
        var (db, meter) = world.Open();

        var attempt = meter.Mint();
        meter.Begin("## Situation", [], CouncilRoles.Research);
        world.Write(CouncilRoles.Research, $"{WorkspaceBuilder.OutDir}/report-{attempt}.md", Report);
        world.Write(CouncilRoles.Research, "trading/PLAN.md", Plan);
        meter.Record(Ended(world.At));

        Assert.Throws<InvalidOperationException>(() =>
            world.RelayOver(db).CommitTurn(CouncilRoles.Research, attempt,
                () => meter.CommitStaged(),
                () => throw new InvalidOperationException("the queue could not be written")));

        Assert.Equal(AiAttemptState.LAUNCHED, new AiAttemptStore(db).Get(attempt)!.State);
        Assert.Empty(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Empty(new PublicationStore(db).To(CouncilRoles.Operations));
    }

    /// <summary>
    /// A REFUSED PLAN IS PUT BACK OUTSIDE THE TRANSACTION, and only after it commits. A rollback
    /// cannot un-write a file, so a restore made inside the commit would survive a failure that took
    /// the revision it was restoring from with it.
    /// </summary>
    [Fact]
    public void A_plan_restored_by_the_commit_is_written_after_it_and_not_before()
    {
        using var world = new World();
        var (db, meter) = world.Open();
        var good = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"plan line {i}"));
        var tooLong = string.Join("\n",
            Enumerable.Range(1, WorkspaceRevisions.PlanLines + 1).Select(i => $"plan line {i}"));

        world.Write(CouncilRoles.Research, "trading/PLAN.md", good);
        world.RelayOver(db).Run(CouncilRoles.Research);

        var attempt = meter.Mint();
        meter.Begin("## Situation", [], CouncilRoles.Research);
        world.Write(CouncilRoles.Research, "trading/PLAN.md", tooLong);
        meter.Record(Ended(world.At));

        var relay = world.RelayOver(db);
        relay.Boundary = at =>
        {
            // At the instant the transaction has landed, the file is still the agent's own.
            if (at == CouncilRelay.AfterCommit)
                Assert.Equal(tooLong, world.Read(CouncilRoles.Research, "trading/PLAN.md"));
        };
        relay.CommitTurn(CouncilRoles.Research, attempt, () => meter.CommitStaged(), () => { });

        Assert.Equal(good, world.Read(CouncilRoles.Research, "trading/PLAN.md"));
        Assert.Single(Of(db, PublicationKind.Plan));
    }
}
