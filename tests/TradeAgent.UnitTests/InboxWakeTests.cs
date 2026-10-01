using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A ROLE'S OWN WRITES NEVER WAKE IT. The inbox wake buys a paid turn, and it is owed for material
/// that ARRIVED — the owner's <c>inbox/</c> — never for what a role or the app wrote itself
/// (BUILD-STATUS.md, U-self-wake).
///
/// <para><b>The defect these were written against</b>, run on 2026-10-01 in the observed run: every
/// Operations turn rewrote <c>agent/trading/PLAN.md</c> and <c>JOURNAL.md</c>, the next pass recorded
/// both as new rows, and <see cref="AppHost.ScanMaterials"/> woke on any addition whatever its origin —
/// <c>inbox:&lt;instant&gt;</c> <c>{"added":2,"seen":20}</c> — so the next turn was told "new material
/// arrived in <c>../inbox</c>" and found nothing there. Eight turns in eight minutes.</para>
///
/// <para>Each pass below is the app's: the REAL scanner over a real workspace, then the wake
/// <see cref="AppHost.InboxWake"/> decides from what that pass found — the one
/// <see cref="AppHost.ScanMaterials"/> raises — written into the real queue. The ledger is asserted
/// beside every wake, because only the trigger narrows: every file is still recorded, under the word
/// it always had.</para>
/// </summary>
public class InboxWakeTests
{
    /// <summary>
    /// A workspace with the shared inbox and every role's <c>in/</c> and <c>out/</c>, a database, and
    /// the app's manifest for that workspace — outside the root, as the product's is, and one per test
    /// so that two runs of this assembly cannot record over each other.
    /// </summary>
    static (Database Db, string Root, AppFileManifest Files) World()
    {
        var id = Guid.NewGuid().ToString("n");
        var root = Path.Combine(TestEnv.Home, $"inbox-wake-{id}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        foreach (var role in CouncilRoles.All)
        {
            Directory.CreateDirectory(Path.Combine(WorkspaceBuilder.HomeOf(root, role), WorkspaceBuilder.InDir));
            Directory.CreateDirectory(Path.Combine(WorkspaceBuilder.HomeOf(root, role), WorkspaceBuilder.OutDir));
        }
        return (TestEnv.NewDb(), root, new AppFileManifest(Path.Combine(TestEnv.Home, $"app-files-{id}.tsv")));
    }

    static void Write(string root, string rel, string text)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>
    /// ONE MATERIAL PASS, AS THE APP RUNS IT: the scanner, then the inbox wake the app raises from
    /// that pass's result, if any, into the real queue. Returns both, so a test can say which pass
    /// raised what.
    /// </summary>
    static (ScanResult Result, (string Id, string Payload)? Wake) Pass(
        Database db, string root, AppFileManifest files, bool agentWasAlive = false)
    {
        var at = DateTimeOffset.UtcNow;
        var result = new MaterialScanner(db, root, _ => !agentWasAlive, files).Scan();
        var wake = AppHost.InboxWake(result, at);
        if (wake is { } w) new MissionEventStore(db).Raise(w.Id, MissionEventKind.Inbox, at, w.Payload);
        return (result, wake);
    }

    /// <summary>Every unconsumed wake in the queue, whenever it falls due.</summary>
    static List<MissionEvent> Queue(Database db) =>
        new MissionEventStore(db).Due(DateTimeOffset.UtcNow.AddDays(1));

    static Material Row(Database db, string rel) =>
        new MaterialStore(db).Present().Single(m => m.RelPath == rel);

    static readonly string Operations = CouncilRoles.HomeDir(CouncilRoles.Operations);
    static readonly string Research = CouncilRoles.HomeDir(CouncilRoles.Research);

    /// <summary>
    /// RED FIRST: A TURN THAT ONLY WROTE ITS OWN PLAN AND JOURNAL RAISES NO WAKE, and neither does the
    /// next one that rewrites them — while the ledger records every version, as the role's own.
    ///
    /// <para>On the base the first pass raised <c>inbox:…</c> <c>{"added":2,"seen":2}</c>: the
    /// observed run's loop, in one test. This is also the test the brief's mutant — the origin filter
    /// removed — turns red.</para>
    /// </summary>
    [Fact]
    public void A_roles_own_plan_and_journal_raise_no_inbox_wake()
    {
        var (db, root, files) = World();
        using var _ = db;
        var plan = $"{Operations}/trading/PLAN.md";
        var journal = $"{Operations}/trading/JOURNAL.md";

        // A turn writes its memory, as every Operations turn did in the observed run.
        Write(root, plan, "# Plan\n\nLook for a mean-reversion edge on ES.\n");
        Write(root, journal, "# Journal\n\n- Inbox and in/ held nothing new.\n");
        var first = Pass(db, root, files, agentWasAlive: true);

        Assert.Equal(2, first.Result.Added);
        Assert.Null(first.Wake);

        // The next turn rewrites both: new bytes, new rows, still the role's own.
        Write(root, plan, "# Plan\n\nMean reversion on ES failed its holdout; try the trend on NQ.\n");
        Write(root, journal, "# Journal\n\n- Inbox and in/ held nothing new.\n- Nor this time.\n");
        var second = Pass(db, root, files, agentWasAlive: true);

        Assert.Equal(2, second.Result.Added);
        Assert.Null(second.Wake);

        // The ledger is what it always was: both versions of both files, each the agent's word.
        var store = new MaterialStore(db);
        Assert.Equal([MaterialOrigin.Agent, MaterialOrigin.Agent], store.History(plan).Select(m => m.Origin));
        Assert.Equal([MaterialOrigin.Agent, MaterialOrigin.Agent], store.History(journal).Select(m => m.Origin));
        Assert.Empty(Queue(db));
    }

    /// <summary>
    /// A FILE THE OWNER DROPS IN <c>inbox/</c> STILL RAISES ONE WAKE — attested or not — and in a pass
    /// that also saw a role's own write, the wake counts only the file that arrived.
    ///
    /// <para>The wake half is GREEN on the base and kept as the guard on the other side: a fix that
    /// stopped waking for <see cref="MaterialOrigin.InboxUnattested"/> would leave every file the owner
    /// drops while a turn is running with no turn to read it. The count half was red on the base,
    /// which put the role's journal into the number.</para>
    /// </summary>
    [Fact]
    public void A_file_dropped_in_the_inbox_still_raises_one_wake()
    {
        var (db, root, files) = World();
        using var _ = db;

        // No agent alive anywhere in the window: the owner's, and attested.
        Write(root, "inbox/broker-statement.pdf", "the owner's document");
        var attested = Pass(db, root, files);

        Assert.Equal(MaterialOrigin.Inbox, Row(db, "inbox/broker-statement.pdf").Origin);
        Assert.Contains("\"added\":1,", Assert.NotNull(attested.Wake).Payload);

        // Dropped while a turn was running, beside that turn's own journal. The row is the weaker
        // word and the journal is the role's — the ledger as it always was — and still one wake.
        Write(root, "inbox/notes-for-the-ai.txt", "dropped mid-turn");
        Write(root, $"{Operations}/trading/JOURNAL.md", "# Journal\n\n- A turn's entry.\n");
        var unattested = Pass(db, root, files, agentWasAlive: true);

        Assert.Equal(MaterialOrigin.InboxUnattested, Row(db, "inbox/notes-for-the-ai.txt").Origin);
        Assert.Equal(MaterialOrigin.Agent, Row(db, $"{Operations}/trading/JOURNAL.md").Origin);
        Assert.Equal(2, unattested.Result.Added);
        Assert.Contains("\"added\":1,", Assert.NotNull(unattested.Wake).Payload);

        // And the engineering line ScanMaterials writes for the pass — BEFORE it raises the wake, so
        // a result that would not serialise would cost the wake — carries the count by origin.
        var line = Json.Write(unattested.Result);
        Assert.Contains("\"added_by\":{", line);
        Assert.Contains("\"arrived\":1", line);
    }

    /// <summary>
    /// RED FIRST: A RELAY DELIVERY WAKES ITS RECIPIENT THROUGH ITS OWN <c>task:</c> WAKE, AND THE PASS
    /// THAT RECORDS THE DELIVERED FILE RAISES NO INBOX WAKE.
    ///
    /// <para>The delivery's wake is written by <see cref="PublicationStore.Commit"/> in the same
    /// transaction as the publication and keyed by it. A second, inbox, wake for the same file is the
    /// recipient woken twice for one report and told to look in the wrong place for it. On the base
    /// the queue held both.</para>
    /// </summary>
    [Fact]
    public void A_relay_delivery_wakes_its_recipient_by_its_task_and_raises_no_inbox_wake()
    {
        var (db, root, files) = World();
        using var _ = db;

        new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = "turn-a",
            StartedAt = DateTimeOffset.UtcNow,
            Role = CouncilRoles.Research
        });
        var named = CouncilRelay.Pattern(CouncilRelay.KindFor(CouncilRoles.Research)).Replace("*", "turn-a");
        var report = $"{Research}/{WorkspaceBuilder.OutDir}/{named}";
        Write(root, report, string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line {i}: what I found")));

        new CouncilRelay(db, role => WorkspaceBuilder.HomeOf(root, role), appFiles: files)
            .Run(CouncilRoles.Research, "turn-a");
        var published = Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));

        var pass = Pass(db, root, files);

        // The ledger records both files exactly as before: the delivery as the app's, the report as
        // the role's own.
        Assert.Equal(2, pass.Result.Added);
        Assert.Equal(MaterialOrigin.App,
            Row(db, $"{Operations}/{WorkspaceBuilder.InDir}/{published.Id}.md").Origin);
        Assert.Equal(MaterialOrigin.Agent, Row(db, report).Origin);

        // ONE wake: the delivery's own, for its recipient.
        var wake = Assert.Single(Queue(db));
        Assert.Equal(MissionEventIds.ForRole(MissionEventIds.Task(published.Id), CouncilRoles.Operations), wake.Id);
        Assert.Equal(MissionEventKind.Report, wake.Kind);
        Assert.Equal(CouncilRoles.Operations, wake.For);
        Assert.Null(pass.Wake);
    }
}
