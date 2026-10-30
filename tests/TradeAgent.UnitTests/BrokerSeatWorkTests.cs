using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT THE FIRST REAL CHIEF TURN SHOWED ABOUT THE BROKER SEAT (<c>U-broker-seat-fixes</c>; the M-org1 sitting of 2026-10-30, gpt-6-luna,
/// exit 0, ten door calls served): the chief said it could not read or update its plan and journal, and asked for no next wake. Every
/// file call here is the door's own surface (<see cref="BrokerDoor.ToolsFor"/>) and every verb the door's own
/// (<see cref="BrokerDoor.OrganisationVerbs"/>) under a grant issued here, through an in-process <see cref="GatewayPipeServer"/>. No real
/// model.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class BrokerSeatWorkTests : IDisposable
{
    readonly string _root = Path.Combine(TestEnv.Home, $"broker-work-{Guid.NewGuid():n}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    const string Chief = "pos-c41ef0000001", Other = "pos-c41ef0000002";

    static string PlanFile => WorkspaceRevisions.PathOf(PublicationKind.Plan);
    static string JournalFile => WorkspaceRevisions.PathOf(PublicationKind.Journal);

    static WorkspaceContext Ctx(string role, string place) => new(
        "Simulator (built in)", ConnectorIsPaper: true, null, TradingMode.PAPER, true, null, new RiskPolicy(),
        ConnectorIsBuiltInSimulator: true, Role: role, Kind: OrgPositionKind.Head, Place: place);

    /// <summary>The door's file surface for <paramref name="role"/>, its homes laid out as the app lays them: the workspace's own folders.</summary>
    static IWorkerTools Door(string workspace, string role, AgentGrants grants, out AgentGrant grant)
    {
        grant = grants.IssueHere(role, "attempt-" + Guid.NewGuid().ToString("n")[..12]);
        return BrokerDoor.ToolsFor(grant, r => Path.Combine(workspace, CouncilRoles.HomeDir(r).Replace('/', Path.DirectorySeparatorChar)),
            () => null, null);
    }

    static Task<ToolAnswer> Call(IWorkerTools tools, string name, object args) =>
        tools.InvokeAsync(new ToolRequest("call-" + Guid.NewGuid().ToString("n")[..8], name, JsonSerializer.Serialize(args)));

    // ---- item 1: a position keeps its own plan, journal and notes --------------------------------------------------------------

    /// <summary>
    /// (1a) AN ORG POSITION'S CANON NAMES ITS PLAN AND JOURNAL WHERE THE DOOR KEEPS THEM, AND THE TURN'S LAST LINE DOES TOO. The chief's
    /// first real turn reported it could not read or update <c>PLAN.md</c> / <c>JOURNAL.md</c> because <c>write_file</c> permits only
    /// <c>out/</c> or <c>trading/</c>: its canon never said where the two files are, and every turn's closing line named them bare. Each
    /// place, on the broker, the cell and the harness, is told the paths <see cref="WorkspaceRevisions"/> keeps — through slots its reach
    /// writes — and on the broker the door serves a write and a read of each.
    ///
    /// <para>RED at base: "Assert.Contains() Failure: Not found: `trading/PLAN.md`" — the org canon named neither file. The watched
    /// mutant (the closing line back to bare names) is quoted in the unit's report.</para>
    /// </summary>
    [Theory]
    [InlineData(OrgPlace.Chief, RuntimeClass.Broker)]
    [InlineData(OrgPlace.Head, RuntimeClass.Broker)]
    [InlineData(OrgPlace.Staff, RuntimeClass.Broker)]
    [InlineData(OrgPlace.Executor, RuntimeClass.Cell)]
    [InlineData(OrgPlace.Chief, RuntimeClass.Harness)]
    public async Task An_org_canon_names_its_plan_and_journal_where_the_door_keeps_them(string place, RuntimeClass runtime)
    {
        Assert.Equal("trading/PLAN.md", PlanFile);
        Assert.Equal("trading/JOURNAL.md", JournalFile);

        var reach = AgentReach.For(Chief, runtime);
        var rendered = Canon.Render(Ctx(Chief, place), reach);
        foreach (var file in new[] { PlanFile, JournalFile })
        {
            Assert.Contains($"`{file}`", rendered.Text, StringComparison.Ordinal);
            Assert.Contains(file, rendered.Writes);
            Assert.True(reach.WritesInto(file), $"the {runtime} reach does not write {file}");
            Assert.Contains($"`{file}`", MissionSituation.Continue, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("`PLAN.md`", MissionSituation.Continue, StringComparison.Ordinal);

        if (runtime != RuntimeClass.Broker) return;
        var workspace = Path.Combine(_root, $"ws-{place}");
        Directory.CreateDirectory(Path.Combine(workspace, "org", Chief, "trading"));
        var door = Door(workspace, Chief, new AgentGrants(), out var grant);
        using var _grant = grant;
        foreach (var file in rendered.Writes.Where(w => !w.EndsWith('/')).Where(w => w == PlanFile || w == JournalFile))
        {
            var wrote = await Call(door, GrantedWorkerTools.WriteFile, new { path = file, content = $"# {file}\nmine\n" });
            Assert.True(wrote.Served, $"the door refused the {place}'s own {file}: {wrote.Content}");
            var read = await Call(door, GrantedWorkerTools.ReadFile, new { path = file });
            Assert.True(read.Served, read.Content);
            Assert.Equal($"# {file}\nmine\n", read.Content);
        }
    }

    /// <summary>
    /// (1b) THROUGH THE DOOR A POSITION READS AND WRITES ITS OWN PLAN, JOURNAL AND NOTES, AND NOTHING OUTSIDE ITS HOME: another position's
    /// plan beside it, the app's state above the workspace and the seat home's codex folder are refused to read and to write, by path, by
    /// climb and as absolute paths; its own canon file is readable and not writable.
    ///
    /// <para>Holds at base (the door's write roots were already the position's own <c>out/</c> and <c>trading/</c>). The watched mutant —
    /// the door's home the workspace instead of the position's folder — is red here, quoted in the unit's report.</para>
    /// </summary>
    [Fact]
    public async Task Through_the_door_a_position_keeps_its_own_files_and_reaches_no_one_elses()
    {
        var workspace = Path.Combine(_root, "workspace");
        var own = Path.Combine(workspace, "org", Chief);
        var other = Path.Combine(workspace, "org", Other);
        foreach (var d in new[] { Path.Combine(own, "trading"), Path.Combine(other, "trading"), Path.Combine(_root, "seat-home", "codex") })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(own, Canon.CliFile), "# Your canon\n");
        File.WriteAllText(Path.Combine(other, "trading", "PLAN.md"), "the other position's plan\n");
        File.WriteAllText(Path.Combine(_root, "state.db"), "the app's state\n");
        File.WriteAllText(Path.Combine(_root, "seat-home", "codex", "auth.json"), "{\"fake\":true}\n");

        var door = Door(workspace, Chief, new AgentGrants(), out var grant);
        using var _grant = grant;

        var outside = new[]
        {
            $"org/{Other}/trading/PLAN.md", $"../{Other}/trading/PLAN.md", "../../../state.db", "../../../seat-home/codex/auth.json",
            Path.Combine(other, "trading", "PLAN.md"), Path.Combine(_root, "state.db"), Path.Combine(_root, "seat-home", "codex", "auth.json")
        };
        foreach (var path in outside)
        {
            var read = await Call(door, GrantedWorkerTools.ReadFile, new { path });
            Assert.False(read.Served, $"the door served a read of '{path}': {read.Content}");
            var wrote = await Call(door, GrantedWorkerTools.WriteFile, new { path, content = "overwritten\n" });
            Assert.False(wrote.Served, $"the door served a write of '{path}': {wrote.Content}");
        }
        foreach (var file in new[] { PlanFile, JournalFile, "trading/notes.md" })
        {
            var wrote = await Call(door, GrantedWorkerTools.WriteFile, new { path = file, content = $"{file} is mine\n" });
            Assert.True(wrote.Served, $"{file}: {wrote.Content}");
            var read = await Call(door, GrantedWorkerTools.ReadFile, new { path = file });
            Assert.True(read.Served, read.Content);
            Assert.Equal($"{file} is mine\n", read.Content);
        }
        Assert.True((await Call(door, GrantedWorkerTools.ReadFile, new { path = Canon.CliFile })).Served);

        var canon = await Call(door, GrantedWorkerTools.WriteFile, new { path = Canon.CliFile, content = "my own instructions\n" });
        Assert.False(canon.Served, "the door let a position rewrite its own canon");

        Assert.Equal("the other position's plan\n", File.ReadAllText(Path.Combine(other, "trading", "PLAN.md")));
        Assert.Equal("the app's state\n", File.ReadAllText(Path.Combine(_root, "state.db")));
        Assert.Equal("{\"fake\":true}\n", File.ReadAllText(Path.Combine(_root, "seat-home", "codex", "auth.json")));
        Assert.Equal("# Your canon\n", File.ReadAllText(Path.Combine(own, Canon.CliFile)));
        Assert.False(File.Exists(Path.Combine(workspace, "org", Other, "trading", "notes.md")));
    }

    // ---- item 3: the chief can pace itself ------------------------------------------------------------------------------------

    /// <summary>
    /// (3) A BROKER SEAT'S CANON TELLS IT HOW TO ASK FOR ITS NEXT WAKE, BY THE DOOR'S OWN NAME, AND THAT CALL IS SERVED. The first chief turn
    /// asked for no next wake (its kit predated canon v8's wake line) and so waited on its 24-hour ceiling. The chief's broker canon names
    /// <c>org_wake</c> — the door verb's own name, not a command line — the door lists that verb, and calling it under the chief's broker
    /// grant writes exactly one <c>self</c> wake, due when asked, inside the board's floor and ceiling for the chief's level.
    ///
    /// <para>A regression guard over <c>U-org-autonomy</c>'s verb (holds at base); the watched mutant is quoted in the unit's report.</para>
    /// </summary>
    [Fact]
    public async Task A_broker_chief_is_told_how_to_ask_for_its_next_wake_and_the_door_serves_it()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        var org = new OrgStore(db);
        var chief = org.AppointChief(new ChiefSeat("puppet", "scripted-1"), r => r is "codex" or "puppet", DateTimeOffset.UtcNow).Chief!;
        org.Activate(chief, _ => SeatClasses.Puppet, 3, DateTimeOffset.UtcNow, () => false);
        gw.AiEnvelopes = role => [.. new OrgStore(db).FundingLine(role).Select(u => new UnitDay(u.Id, BillingClasses.Plan, 10m, 0m, 0m, 0, 0, 0m))];
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());

        // THE CANON: the chief's own render on the broker reach names the door's verb.
        var name = BrokerDoor.VerbName(Ops.OrgWake);
        var reach = AgentReach.For(chief, RuntimeClass.Broker);
        Assert.Null(reach.WakeFile);
        var text = Canon.Render(Ctx(chief, OrgPlace.Chief), reach).Text;
        Assert.Contains($"**Waking:** ask for your next wake with `{name}(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TradeAgent wakes you, on an event or on its scheduled look; you cannot set a time", text, StringComparison.Ordinal);

        // THE DOOR: the verb is one it serves, and the chief's broker launch is served it.
        var verb = Assert.Single(BrokerDoor.OrganisationVerbs(() => server), v => v.Spec.Name == name);
        var attempt = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = attempt, StartedAt = DateTimeOffset.UtcNow, Role = chief, ReservedCost = 0.10m, SeatClass = SeatClasses.Broker,
            Assurance = SeatClasses.Name(SeatClasses.AssuranceOf(SeatClasses.Broker)!.Value)
        }).Admitted);
        var grants = new AgentGrants();
        using var grant = grants.IssueHere(chief, attempt);

        var before = DateTimeOffset.UtcNow;
        var answer = await verb.Run(grant, "{\"after_minutes\": 360}", default);
        Assert.True(answer.Served, answer.Content);
        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Self), e => e.Role == chief);
        Assert.InRange(wake.DueAt, before.AddMinutes(360), DateTimeOffset.UtcNow.AddMinutes(360));
        using var body = JsonDocument.Parse(answer.Content);
        Assert.True(body.RootElement.GetProperty("floor_minutes").GetInt32() <= 360);
        Assert.True(body.RootElement.GetProperty("ceiling_minutes").GetInt32() >= 360);
    }
}
