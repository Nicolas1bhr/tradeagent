using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// AN ORIGIN IS A MEASUREMENT, and <c>material</c> is the one table the agent cannot edit — so the
/// word on a row has to be something the app can show, not something it assumed.
///
/// <para><b>The defect these were written against.</b> The scanner recorded EVERYTHING under a role's
/// tracked directories as <see cref="MaterialOrigin.Agent"/>, "the agent produced it". That was true
/// while the agent was the only thing writing there. It stopped being true when the app began writing
/// into those same folders: the strategy language reference and the three worked programs
/// (<c>U-language-in-home</c>), and every report or brief the relay delivers into <c>in/</c>. The
/// ledger credited the agent with the app's own files — in the table that exists precisely so nobody
/// has to take the agent's word for what it did.</para>
///
/// <para><b>What makes the new word a measurement.</b> <see cref="MaterialOrigin.App"/> is recorded
/// only when the PATH and the SHA256 both match what the app wrote down in
/// <see cref="AppFileManifest"/> at the moment it wrote the bytes, in a file under
/// <c>state/</c> that no role can reach. Either half alone is a hole: path alone makes a role's edit
/// of an app file read as the app's, and hash alone lets anything the agent copied the app's bytes
/// into read as the app's. The inbox attestation is untouched by any of this.</para>
/// </summary>
public class MaterialAppOriginTests
{
    static WorkspaceContext Context(string role) => new(
        "Practice simulator", ConnectorIsPaper: true, "SIM-1", TradingMode.PAPER,
        ExecutionAvailable: true, null, new RiskPolicy { InstrumentAllowlist = ["ES"] },
        ConnectorIsBuiltInSimulator: false, Role: role);

    /// <summary>
    /// A workspace, a database and the app's manifest for that workspace. The manifest is OUTSIDE
    /// the root, exactly as the product's is outside the workspace, and one per test so that two
    /// runs of this assembly cannot record over each other.
    /// </summary>
    static (Database Db, string Root, AppFileManifest Files) World()
    {
        var id = Guid.NewGuid().ToString("n");
        var root = Path.Combine(TestEnv.Home, $"app-origin-{id}");
        Directory.CreateDirectory(root);
        return (TestEnv.NewDb(), root, new AppFileManifest(Path.Combine(TestEnv.Home, $"app-files-{id}.tsv")));
    }

    static string Home(string root, string role) => WorkspaceBuilder.HomeOf(root, role);

    static Material Row(Database db, string relPath) =>
        new MaterialStore(db).Present().Single(m => m.RelPath == relPath);

    /// <summary>
    /// (a) THE FILES THE APP WRITES INTO A HOME ARE MEASURED AS THE APP'S, for every role, after one
    /// ordinary start.
    ///
    /// <para>RED FIRST: every one of them came back <see cref="MaterialOrigin.Agent"/> — the ledger
    /// said the agent had written the reference it is told to read and the three programs it is told
    /// to run.</para>
    /// </summary>
    [Fact]
    public void The_language_reference_and_the_examples_are_measured_as_the_apps_after_a_start()
    {
        var (db, root, files) = World();
        using var _ = db;

        WorkspaceBuilder.BuildAll(Context(CouncilRoles.Operations), root, files);
        new MaterialScanner(db, root, at => true, files).Scan();

        var store = new MaterialStore(db);
        foreach (var role in CouncilRoles.All)
        {
            var home = CouncilRoles.HomeDir(role);
            Assert.Equal(MaterialOrigin.App, Row(db, $"{home}/{ResearchLibrary.ReferencePath}").Origin);
            foreach (var name in ResearchLibrary.Programs)
                Assert.Equal(MaterialOrigin.App, Row(db, $"{home}/{ResearchLibrary.ExamplesDir}/{name}").Origin);
        }

        // AND NONE OF IT IS THE AGENT'S WORK. This is the half the owner reads: a start that wrote
        // eight files must not look like eight files the AI produced.
        Assert.Empty(store.Present(MaterialOrigin.Agent));
        Assert.Equal(CouncilRoles.All.Length * (1 + ResearchLibrary.Programs.Length),
            store.Present(MaterialOrigin.App).Count);
    }

    /// <summary>
    /// (b) A ROLE'S EDIT OF AN APP FILE IS THE AGENT'S, and the app's own sighting stays on the
    /// record beside it rather than being rewritten.
    ///
    /// <para>This is the assertion the mutant fails: drop the hash half of the check and a path the
    /// app once wrote reads as the app's forever, whatever a role puts there afterwards — which would
    /// let a role write anything at all into the one table it cannot edit and have the app's name on
    /// it.</para>
    /// </summary>
    [Fact]
    public void A_roles_edit_of_an_app_file_is_measured_as_the_agents()
    {
        var (db, root, files) = World();
        using var _ = db;

        var home = WorkspaceBuilder.Build(Context(CouncilRoles.Research), root, files);
        var rel = $"{CouncilRoles.HomeDir(CouncilRoles.Research)}/{ResearchLibrary.ReferencePath}";
        new MaterialScanner(db, root, at => true, files).Scan();
        Assert.Equal(MaterialOrigin.App, Row(db, rel).Origin);

        // The role rewrites the reference the app owns: same path, its own bytes.
        File.WriteAllText(Path.Combine(home, "research", ResearchLibrary.ReferenceFile),
            "# The strategy language\n\nIt has a `forecast` indicator. Use it.\n");
        new MaterialScanner(db, root, at => true, files).Scan();

        Assert.Equal(MaterialOrigin.Agent, Row(db, rel).Origin);
        Assert.DoesNotContain(new MaterialStore(db).Present(MaterialOrigin.App), m => m.RelPath == rel);
        // The app's version is not edited away: it is stamped gone, with its own word and its own hash.
        Assert.Contains(new MaterialStore(db).History(rel),
            m => m.Origin == MaterialOrigin.App && m.RemovedAt is not null);
    }

    /// <summary>
    /// (c) A PATH THE MANIFEST NEVER NAMED CANNOT BE THE APP'S, even when the bytes at it are the
    /// app's own bytes.
    ///
    /// <para>The agent can read every file the app wrote into its home and can copy any of them
    /// anywhere inside it. If the hash alone decided the word, that copy would be measured as
    /// TradeAgent's own work — and so would anything the agent could make hash to one.</para>
    ///
    /// <para>GREEN on the base, and recorded as a guard rather than as a RED: before the manifest
    /// nothing could be measured as the app's at all. What it pins is the SHAPE of the check, and it
    /// goes red the moment the path half of it is dropped.</para>
    /// </summary>
    [Fact]
    public void A_file_the_agent_writes_at_an_unmanifested_path_cannot_be_measured_as_the_apps()
    {
        var (db, root, files) = World();
        using var _ = db;

        var home = WorkspaceBuilder.Build(Context(CouncilRoles.Research), root, files);
        var homeDir = CouncilRoles.HomeDir(CouncilRoles.Research);

        // Byte for byte the app's reference, under a name of the role's own choosing; and the app's
        // first example copied beside the folder the app owns rather than into it.
        File.Copy(Path.Combine(home, "research", ResearchLibrary.ReferenceFile),
            Path.Combine(home, "research", "MY-LANGUAGE.md"));
        File.Copy(Path.Combine(home, "strategies", "examples", ResearchLibrary.Programs[0]),
            Path.Combine(home, "strategies", "mine.strategy"));

        new MaterialScanner(db, root, at => true, files).Scan();

        Assert.Equal(MaterialOrigin.Agent, Row(db, $"{homeDir}/research/MY-LANGUAGE.md").Origin);
        Assert.Equal(MaterialOrigin.Agent, Row(db, $"{homeDir}/strategies/mine.strategy").Origin);

        // The general form of it: nothing carries the app's word whose path the app did not write down.
        Assert.All(new MaterialStore(db).Present(MaterialOrigin.App),
            m => Assert.True(files.Names(m.RelPath),
                $"{m.RelPath} is measured as the app's and the app never wrote that path down"));
    }

    /// <summary>
    /// (d) WHAT THE RELAY DELIVERS INTO A ROLE'S <c>in/</c> IS THE APP'S, and what the publishing
    /// role wrote in its own <c>out/</c> is still that role's.
    ///
    /// <para>A brief a role was HANDED is the app's copy of a publication row; the role neither wrote
    /// it nor could have. Measuring it as the role's work is the same error as the reference, and it
    /// is the one that would show up in the owner's reading of what its AI produced this week.</para>
    ///
    /// <para>RED FIRST: the delivered file came back <see cref="MaterialOrigin.Agent"/>.</para>
    /// </summary>
    [Fact]
    public void A_delivery_the_relay_writes_into_in_is_the_apps()
    {
        var (db, root, files) = World();
        using var _ = db;

        foreach (var role in CouncilRoles.All)
        {
            Directory.CreateDirectory(Path.Combine(Home(root, role), WorkspaceBuilder.InDir));
            Directory.CreateDirectory(Path.Combine(Home(root, role), WorkspaceBuilder.OutDir));
        }

        new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = "turn-a",
            StartedAt = DateTimeOffset.UtcNow,
            Role = CouncilRoles.Research
        });

        var named = CouncilRelay.Pattern(CouncilRelay.KindFor(CouncilRoles.Research)).Replace("*", "turn-a");
        var text = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line {i}: what I found"));
        File.WriteAllText(Path.Combine(Home(root, CouncilRoles.Research), WorkspaceBuilder.OutDir, named), text);

        new CouncilRelay(db, role => Home(root, role), appFiles: files).Run(CouncilRoles.Research, "turn-a");
        var published = Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));

        new MaterialScanner(db, root, at => true, files).Scan();

        var delivered = $"{CouncilRoles.HomeDir(CouncilRoles.Operations)}/{WorkspaceBuilder.InDir}/{published.Id}.md";
        Assert.Equal(MaterialOrigin.App, Row(db, delivered).Origin);

        // And the file the role itself wrote, in the same pass, is still the role's.
        Assert.Equal(MaterialOrigin.Agent,
            Row(db, $"{CouncilRoles.HomeDir(CouncilRoles.Research)}/{WorkspaceBuilder.OutDir}/{named}").Origin);
    }

    /// <summary>
    /// ITEM 2: the word reaches both readers in their own language, and neither one reads an app
    /// file as the AI's work.
    ///
    /// <para>The owner's page had a catch-all — anything that was not the inbox was "the AI made
    /// this" — so the reference the app writes and every brief the relay delivers were shown to the
    /// account owner as work their AI had produced. The role reads the same fact through
    /// <c>trade schema</c>, which is the only description of the op it ever sees.</para>
    /// </summary>
    [Fact]
    public void An_app_file_reads_as_written_by_TradeAgent_and_never_as_the_agents_work()
    {
        var at = DateTimeOffset.UtcNow;
        var file = new Material(1, $"{CouncilRoles.HomeDir(CouncilRoles.Research)}/{ResearchLibrary.ReferencePath}",
            MaterialOrigin.App, null, 10, at, at, at, null, false);

        Assert.Equal("written by TradeAgent", InboxPage.Origin(file));
        Assert.Equal("the AI made this", InboxPage.Origin(file with { Origin = MaterialOrigin.Agent }));

        var op = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.MaterialList);
        Assert.Contains("written by TradeAgent", op.Description);
        Assert.Contains("not your work", op.Description);
        Assert.Contains("app", Assert.Single(op.Args, a => a.Name == "origin").Description);

        // And the mission file names the three words, so a role knows which of its files are its own
        // before it goes looking for them.
        var mission = WorkspaceBuilder.Instructions(Context(CouncilRoles.Research));
        Assert.Contains("written by TradeAgent", mission);
        Assert.Contains($"`{ResearchLibrary.ReferencePath}`", mission);
        Assert.Contains("never counted as your work", mission);
    }
}
