using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A POSITION HIRED WHILE THE APP RUNS WORKS AT ONCE (<c>U-home-on-hire</c>; R25 D3 self-activation; the 72-hour unattended run): its
/// home — seat home, canon, guide, cell workspace — is built through the start-up pass's own builder before its first launch, not at
/// the next start; and a launch refused because a home is unbuilt or stale gives its wakes back instead of burning them.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class HomeOnHireTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    /// <summary>
    /// (3) THE PUPPET LIFE, RED FIRST: the app running, a division head's puppet charters a team and HIRES an executor into it through
    /// <c>trade org hire</c>; the hire activates itself in the hire's own write (the host's facts declare it a cell seat on a proven cell —
    /// the test's stand-in for a host this Mac is not); and the executor's FIRST wake launches a turn in its built home — the app's canon
    /// and guide at their names, its in/out folders and context file, the attempt recording the app's own canon — with no restart, the
    /// activation wake answered by that turn. On the base the home was built only by the next start's pass.
    /// </summary>
    [Fact]
    public async Task A_hire_activated_mid_run_launches_its_first_turn_in_its_built_home_without_a_restart()
    {
        const string head = "pos-5e55ff000001", division = "div-5e55ff00";
        await using var life = await PuppetLife.Begin("home-on-hire", seed: SeedDivision(division, head));
        var org = new OrgStore(life.Host.Db);

        // THE HOST'S FACTS FOR A HIRE: a cell seat on a proven cell (self-activation's gate). Everything else is the host's own reading.
        var real = life.Host.Gateway.SeatFactsOf!;
        life.Host.Gateway.SeatFactsOf = id => id == head || CouncilRoles.IsKnown(id) || real(id) is not { } f
            ? real(id)
            : f with { DeclaredClass = SeatClasses.Cell, ProvenCell = true };

        var forecasts = Word(new[] { new { predicate = ForecastVocabulary.UnitAdmissible, target = ForecastVocabulary.Admitted,
            by = DateTimeOffset.UtcNow.AddDays(45).ToString("O"), probability = 0.4m } });
        string[] charter = [$"trade org charter team 0.25 \"the funding skew family earns a team\" --forecasts {forecasts} --collapse-by "
                            + DateTimeOffset.UtcNow.AddDays(60).ToString("O"), "usage 100 0 10", "final Chartered."];
        life.Script(head, charter);
        life.Wake(head);
        await life.RunUntil(() => life.AttemptsOf(head).Any(a => a.State == AiAttemptState.ENDED), "the head chartered");
        var team = Assert.Single(org.Units(), u => u.ParentId == division).Id;

        life.Script(head, charter, [$"trade org hire {team} \"an executor for the skew\" --forecasts {forecasts}", "usage 100 0 10", "final Hired."]);
        life.Wake(head);
        await life.RunUntil(() => life.AttemptsOf(head).Count(a => a.State == AiAttemptState.ENDED) >= 2, "the head hired");
        var hired = life.Log(head, 2).Single(s => (string?)s["step"] == "trade");
        Assert.True((int?)hired["exit"] == 0, $"the hire was refused: {hired.ToJsonString()}");
        var hire = Assert.Single(org.Positions(), p => p.UnitId == team);
        Assert.True(hire.Status == OrgPositionStatus.Active, $"the hire did not activate itself: {hire.Status}; {hired.ToJsonString()}");

        // ITS FIRST WAKE — the activation's, raised in the hire's own write — launches a turn, the app never restarted.
        await life.RunUntil(() => life.AttemptsOf(hire.Id).Any(a => a.State == AiAttemptState.ENDED)
                                  || life.Events().Any(e => e.Role == hire.Id && e.Kind == MissionEventKind.Activated && e.ConsumedAt is not null),
            "the hire's first wake was taken");
        await life.RunUntil(() => life.AttemptsOf(hire.Id).Any(a => a.State == AiAttemptState.ENDED), "the hire's first turn ended");

        var home = life.Host.HomeFor(hire.Id);
        var attempt = life.AttemptsOf(hire.Id).First(a => a.State == AiAttemptState.ENDED);
        output.WriteLine($"hire {hire.Id}: home {home}; attempt {attempt.Id} exit={attempt.ExitCode}; turns {life.Turns(hire.Id)}; {life.Counts()}");
        Assert.True(life.Turns(hire.Id) == 1, $"the hire's puppet did not start in its home: {life.Turns(hire.Id)} turn(s). {life.State()}");
        foreach (var name in new[] { Canon.CliFile, Canon.GuideFile, Canon.HarnessFile, ".tradeagent/context.json" })
            Assert.True(File.Exists(Path.Combine(home, name)), $"the hire's home holds no {name}: {home}");
        foreach (var dir in new[] { WorkspaceBuilder.InDir, WorkspaceBuilder.OutDir })
            Assert.True(Directory.Exists(Path.Combine(home, dir)), $"the hire's home holds no {dir}/: {home}");
        Assert.Null(life.Host.HomeRefusalFor(hire.Id));
        using (var context = JsonDocument.Parse(attempt.Context ?? "{}"))
            Assert.True(context.RootElement.TryGetProperty("canon_app_own", out var own) && own.ValueKind == JsonValueKind.True,
                $"the hire's first turn did not run under the app's own canon: {attempt.Context}");
        Assert.True(attempt.ExitCode == 0, $"the hire's first turn failed: exit {attempt.ExitCode}. {life.State()}");
        var woke = Assert.Single(life.Events(), e => e.Role == hire.Id && e.Kind == MissionEventKind.Activated);
        Assert.Equal((attempt.Id, MissionEventDisposition.Answered), (woke.ConsumedBy, woke.Disposition));
    }

    /// <summary>
    /// (2b) THE HARD LINE BEHIND THE LOOK, ON BOTH RUNTIMES (U-runway-fixes' card: ApiConversation's and AgentSession's): a home the last
    /// start could not rebuild, met by a launch the look did not stop — the race the hard line exists for — is refused as the launch gate's
    /// typed refusal before any work (so the loop gives its wakes back and the meter charges nothing), in words naming the position; the
    /// CLI starts no program and the harness sends no request. RED at base: an untyped failure (the CLI's a CELL_UNAVAILABLE exception's
    /// words, the harness's a "first request withheld") — a lasting refusal that burned the launch's wakes, turn after turn.
    /// </summary>
    [Fact]
    public async Task A_launch_meeting_an_unbuilt_home_at_its_hard_line_is_refused_as_the_gates_typed_refusal_on_both_runtimes()
    {
        var root = Path.Combine(TestEnv.Home, "unbuilt-hard-line", Guid.NewGuid().ToString("n"));
        var ctx = new WorkspaceContext("Simulator (built in)", ConnectorIsPaper: true, "SIM-001", TradingMode.PAPER,
            ExecutionAvailable: true, null, new RiskPolicy { MaxDailyLoss = 500m, InstrumentAllowlist = ["ES"] });
        var files = new AppFileManifest(Path.Combine(root, "..", $"files-{Guid.NewGuid():n}.tsv"));
        var homes = WorkspaceBuilder.BuildAll(ctx, root, files);
        foreach (var role in new[] { CouncilRoles.Operations, CouncilRoles.Research })
        {
            var canon = Path.Combine(homes[role], Canon.CliFile);
            File.Delete(canon);
            Directory.CreateDirectory(canon);                                   // a folder where the app's canon belongs
        }
        WorkspaceBuilder.BuildAll(ctx, root, files);                            // the start that cannot rebuild either home

        // THE CLI: a session over a fake codex, sent its turn directly — as after a look that passed.
        var (codex, exe, dump) = SeatHomeTests.Seat("unbuilt-hard-line");
        var cli = await SeatHomeTests.Turn(SeatHomeTests.Session(codex, exe, homes[CouncilRoles.Operations], new SeatHomeTests.FakeProbe()));
        Assert.True(cli.GateRefused is { BeforeAnyWork: true, Refusal.Kind: RefusalKind.HostOrLaunch },
            $"the CLI's hard line was not the gate's typed refusal: gate {cli.GateRefused?.Refusal.Kind}; '{cli.Raw}'");
        Assert.Contains(CouncilRoles.Operations, cli.GateRefused!.Refusal.Words, StringComparison.Ordinal);
        Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));

        // THE HARNESS: a conversation over the loopback fake, sent its turn directly.
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("nothing to do.", input: 10, output: 2));
        var manifest = new RuntimeManifest
        {
            Id = ApiAgentRuntime.RuntimeId, DisplayName = "Loopback harness", BaseUrl = provider.BaseUrl,
            CompletionsPath = "chat/completions", DefaultModel = "loopback-model"
        };
        using var runtime = new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"), requestTimeout: TimeSpan.FromSeconds(10));
        var harness = runtime.OpenConversation(CouncilRoles.Research, () => homes[CouncilRoles.Research], () => new Dictionary<string, string>(), () => null);
        AgentTurnEnded? api = null;
        harness.TurnEnded += e => api = e;
        await harness.SendMissionAsync("## Situation\nnothing has happened.");
        Assert.True(api?.GateRefused is { BeforeAnyWork: true, Refusal.Kind: RefusalKind.HostOrLaunch },
            $"the harness's hard line was not the gate's typed refusal: gate {api?.GateRefused?.Refusal.Kind}; withheld '{api?.FirstRequestWithheld}'");
        Assert.Contains(CouncilRoles.Research, api!.GateRefused!.Refusal.Words, StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
    }

    /// <summary>A division under the root with its head, each active — as only a test may write the chart.</summary>
    static Action<Database> SeedDivision(string division, string head) => db => db.Write(_ =>
    {
        const string at = "2026-10-23T12:00:00.0000000Z";
        foreach (var line in new[]
                 {
                     $"INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES('{division}', '{OrgIds.Root}', 'division', NULL, 'active', '{at}')",
                     $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('{head}', '{division}', 'org/{head}', 'active', '{at}')",
                     $"UPDATE org_unit SET head_position_id='{head}' WHERE id='{division}'"
                 })
        {
            using var c = db.Cmd(line);
            c.ExecuteNonQuery();
        }
        return 0;
    });

    /// <summary>A JSON argument as a puppet's script carries it: one word, each quote escaped.</summary>
    static string Word(object value) => JsonSerializer.Serialize(value).Replace("\"", "\\\"", StringComparison.Ordinal);
}
