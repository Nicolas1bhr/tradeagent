using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 3 — EVERY BOUNDARY COUNTED, AND COUNTED BEFORE THE REQUEST THAT WOULD PASS IT.
///
/// <para><b>What the harness adds that a CLI could not.</b> <c>CONTRACTS.md</c> states the limitation
/// this closes, in those words: "there is no provider-side ceiling… a turn whose input runs past
/// <c>AiTurnAllowanceInputTokens</c> is still run and still billed, and this app's only enforcement is
/// refusing the NEXT launch". On a CLI the allowance is an app-side commitment and the ceiling bites
/// between turns. Here the app composes every request, so the allowance bites INSIDE one — and the
/// place it has to bite is BEFORE the request, because a check taken after it has already paid for
/// the thing it was meant to prevent.</para>
///
/// <para><b>And nothing is thrown away when it bites.</b> A turn stopped on its bound did real work:
/// what it staged in <c>out/</c> and <c>trading/</c> stands, and the launch row says the turn was
/// BOUNDED rather than broken — a distinction an exit code cannot carry and rule 4 requires.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class HarnessBudgetTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"harness-turns-{Guid.NewGuid():n}.jsonl");
    readonly string _home = Path.Combine(TestEnv.Home, $"budget-{Guid.NewGuid():n}");

    /// <summary>Not a key. The fake reads the header back; nothing real is ever sent anywhere.</summary>
    const string Pretend = "not-a-real-credential";

    /// <summary>The owner's own two numbers, so a reservation has exactly one right answer.</summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    public HarnessBudgetTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, CouncilRoles.OutDir));
        Directory.CreateDirectory(Path.Combine(_home, "trading"));
    }

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
        _db.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { }
    }

    RuntimeManifest Manifest(FakeProvider provider)
    {
        var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
        manifest.BaseUrl = provider.BaseUrl;
        return manifest;
    }

    ApiAgentRuntime Runtime(FakeProvider provider, TurnAllowance allowance, string? key = Pretend,
        Func<string, IWorkerTools>? tools = null) =>
        new(Manifest(provider), () => key, tools, allowance: () => allowance,
            requestTimeout: TimeSpan.FromSeconds(10));

    IAgentConversation Research(ApiAgentRuntime runtime) =>
        runtime.OpenConversation(CouncilRoles.Research, () => _home,
            () => new Dictionary<string, string>(), () => null);

    TurnMeter Meter(decimal cap, TurnAllowance allowance) =>
        new(_db, () => cap, runtimeId: () => ApiAgentRuntime.RuntimeId, recordPath: _records,
            owner: () => Rate, model: () => "gpt-5.6-luna", allowance: () => allowance,
            share: _ => 1m);

    // ---- the bound, before each request -----------------------------------------------------------

    /// <summary>
    /// THE GUARD. Three responses of 100 input tokens each fill an allowance of 300; a fourth request
    /// is never sent, and the turn ends <c>CONTEXT_BUDGET_EXCEEDED</c>.
    ///
    /// The fake asks for a tool call every time and would go on for ever — running off the end of its
    /// queue repeats the last response deliberately — so the only thing that can stop this loop is the
    /// check. Move that check to AFTER the request and the fourth one is sent.
    /// </summary>
    [Fact]
    public async Task A_turn_makes_no_request_once_its_input_allowance_is_used()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 100, output: 5));

        using var runtime = Runtime(provider, TurnAllowance.From(300, 20_000));
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation");

        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(ApiConversation.BudgetExceeded, ended!.ExitCode);
        Assert.Equal(ApiConversation.EndedOverBudget, ended.Outcome);
        Assert.Equal(300, ended.Usage!.InputTokens);
        Assert.Contains(conversation.History,
            t => t.Role == ChatRole.System && t.Text.Contains("300 input tokens of the 300"));
    }

    /// <summary>The other half of the allowance, counted the same way and with its own sentence.</summary>
    [Fact]
    public async Task A_turn_makes_no_request_once_its_output_allowance_is_used()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 10, output: 50));

        using var runtime = Runtime(provider, TurnAllowance.From(1_000_000, 100));
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation");

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(ApiConversation.EndedOverBudget, ended!.Outcome);
        Assert.Contains(conversation.History,
            t => t.Role == ChatRole.System && t.Text.Contains("100 output tokens of the 100"));
    }

    /// <summary>
    /// THE APP CAPS OUTPUT WITH THE PROVIDER'S OWN PARAMETER, on every request and not only the first.
    /// It is the one bound the PROVIDER enforces, which is the difference between a cap and a
    /// commitment.
    /// </summary>
    [Fact]
    public async Task Every_request_carries_the_providers_own_max_output_parameter()
    {
        using var provider = new FakeProvider();
        provider
            .Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 10, output: 5))
            .Answer(FakeProvider.Message("done", input: 10, output: 5));

        using var runtime = Runtime(provider, TurnAllowance.From(1_000_000, 4_321));
        await Research(runtime).SendMissionAsync("## Situation");

        Assert.Equal(2, provider.Requests.Count);
        foreach (var sent in provider.Requests)
        {
            using var body = JsonDocument.Parse(sent);
            Assert.Equal(4_321, body.RootElement.GetProperty("max_completion_tokens").GetInt64());
        }
    }

    /// <summary>
    /// RETRIEVAL IS BOUNDED IN BYTES, which is the unit the app measures — never converted to tokens,
    /// for the reason <c>TurnContext</c> gives at length. It is the one quantity rule 4 says the app
    /// controls outright, so the bound is the app's own number rather than a share of the allowance.
    /// </summary>
    [Fact]
    public async Task A_turn_stops_once_its_tools_have_returned_all_one_turn_may_read()
    {
        var big = new string('y', GrantedWorkerTools.MaxBytesPerCall);
        Directory.CreateDirectory(Path.Combine(_home, CouncilRoles.InDir));
        File.WriteAllText(Path.Combine(_home, CouncilRoles.InDir, "big.txt"), big);

        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCall("read_file", new { path = "in/big.txt" }, input: 1, output: 1));

        using var runtime = Runtime(provider, TurnAllowance.From(100_000_000, 100_000_000),
            tools: role => new GrantedWorkerTools(role, () => _home, () => null));
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation");

        // 64 KiB a call against 512 KiB a turn: the ninth request is the one that is never sent.
        var expected = ApiConversation.MaxRetrievalBytesPerTurn / GrantedWorkerTools.MaxBytesPerCall;
        Assert.Equal(expected, provider.Requests.Count);
        Assert.Equal(ApiConversation.EndedOverBudget, ended!.Outcome);
        Assert.Contains(conversation.History,
            t => t.Role == ChatRole.System && t.Text.Contains("bytes, which is all one turn may read"));
    }

    /// <summary>
    /// STAGED FILES ARE KEPT, and the sentence the worker is left with says so. A turn that ran out of
    /// context still did work the owner paid for; deleting it would be the app throwing that away.
    /// </summary>
    [Fact]
    public async Task What_the_turn_staged_before_it_ran_out_of_context_is_still_there()
    {
        using var provider = new FakeProvider();
        provider
            .Answer(FakeProvider.ToolCall("write_file",
                new { path = $"{CouncilRoles.OutDir}/report.md", content = "# as far as I got" },
                input: 100, output: 5))
            .Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 300, output: 5));

        using var runtime = Runtime(provider, TurnAllowance.From(300, 20_000),
            tools: role => new GrantedWorkerTools(role, () => _home, () => null));
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation");

        Assert.Equal(ApiConversation.EndedOverBudget, ended!.Outcome);
        Assert.Equal("# as far as I got",
            File.ReadAllText(Path.Combine(_home, CouncilRoles.OutDir, "report.md")));
        Assert.Contains(conversation.History,
            t => t.Role == ChatRole.System && t.Text.Contains("out/ or trading/ is kept"));
    }

    // ---- the reservation, before the first request -------------------------------------------------

    /// <summary>
    /// THE RESERVATION IS COMMITTED BEFORE THE FIRST REQUEST, and a turn the ceiling refuses sends
    /// nothing at all. The meter's <c>Begin</c> is the gate; the harness only reaches the wire on the
    /// far side of it.
    /// </summary>
    [Fact]
    public async Task A_turn_the_days_ceiling_refuses_reaches_the_provider_not_at_all()
    {
        var allowance = TurnAllowance.From(1_200_000, 20_000);
        // 1.20 M at 1.00 plus 20 k at 4.00 is 1.28 a turn, against a ceiling of 0.50.
        var meter = Meter(0.50m, allowance);

        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("should never be asked for"));

        using var runtime = Runtime(provider, allowance);
        var conversation = Research(runtime);
        using var metering = meter.Attach(conversation, CouncilRoles.Research);

        await conversation.SendAsync("what is the plan?");

        Assert.Empty(provider.Requests);
        Assert.Contains(conversation.History,
            t => t.Role == ChatRole.System && t.Text == Labels.DailySpendingLimitReached);
        Assert.Equal(0, Rows(nameof(AiAttemptState.LAUNCHED)));
    }

    /// <summary>
    /// AND WHEN IT IS ADMITTED, THE ROW IS THERE WITH THE RESERVATION ON IT BEFORE THE TURN RUNS —
    /// the same commitment the CLI's launches make, on a runtime where the app is the one spending.
    /// </summary>
    [Fact]
    public async Task An_admitted_turn_commits_its_reservation_and_then_reports_what_it_used()
    {
        var allowance = TurnAllowance.From(1_200_000, 20_000);
        var meter = Meter(50m, allowance);

        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("done", input: 2_000, output: 100));

        using var runtime = Runtime(provider, allowance);
        var conversation = Research(runtime);
        using var metering = meter.Attach(conversation, CouncilRoles.Research);

        await conversation.SendAsync("what is the plan?");

        var row = Assert.Single(Attempts());
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(CouncilRoles.Research, row.Role);
        Assert.Equal(ApiAgentRuntime.RuntimeId, row.Runtime);
        Assert.Equal(1.28m, row.ReservedCost);
        Assert.Equal(2_000, row.InputTokens);
        Assert.Equal(100, row.OutputTokens);
        // Billed at the owner's own rate: 2,000 at 1.00 and 100 at 4.00 per million.
        Assert.Equal((2_000m * 1m + 100m * 4m) / 1_000_000m, row.Cost);
    }

    /// <summary>
    /// USAGE THAT NEVER ARRIVES IS UNRESOLVED, NEVER ZERO, AND THE RESERVATION STANDS AS THE COST.
    /// The rule <c>U-budget-reserve</c> put on every path rather than only the crash path — here on the
    /// harness, where a provider that answered without a usage object is an ordinary Tuesday.
    /// </summary>
    [Fact]
    public async Task A_turn_whose_usage_never_arrives_is_charged_its_reservation()
    {
        var allowance = TurnAllowance.From(1_200_000, 20_000);
        var meter = Meter(50m, allowance);

        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.WithoutUsage("no idea what that cost"));

        using var runtime = Runtime(provider, allowance);
        var conversation = Research(runtime);
        using var metering = meter.Attach(conversation, CouncilRoles.Research);

        await conversation.SendAsync("what is the plan?");

        var row = Assert.Single(Attempts());
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Equal(1.28m, row.Cost);
        Assert.Equal(AiAttemptStore.UnreportedReason, row.UnpricedReason);
        Assert.Equal(1, meter.TodayFor(CouncilRoles.Research).UnreportedTurns);
    }

    /// <summary>
    /// THE ROW SAYS THE TURN WAS BOUNDED RATHER THAN BROKEN. It lands in <c>ai_attempt.context</c>,
    /// which is already the app's own account of what it could see of a turn — a column per fact is how
    /// a table stops being readable.
    /// </summary>
    [Fact]
    public async Task The_launch_row_records_that_the_turn_ended_on_its_own_bound()
    {
        var allowance = TurnAllowance.From(300, 20_000);
        var meter = Meter(50m, allowance);

        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 100, output: 5));

        using var runtime = Runtime(provider, allowance);
        var conversation = Research(runtime);
        using var metering = meter.Attach(conversation, CouncilRoles.Research);

        await conversation.SendAsync("go on then");

        var row = Assert.Single(Attempts());
        Assert.Equal(ApiConversation.BudgetExceeded, row.ExitCode);
        Assert.NotNull(row.Context);
        using var context = JsonDocument.Parse(row.Context!);
        Assert.Equal(nameof(ErrorCode.CONTEXT_BUDGET_EXCEEDED),
            context.RootElement.GetProperty("ended").GetString());
        // And the app's own view of the turn is in the same record: three requests' worth of stream.
        Assert.True(context.RootElement.GetProperty("stream_bytes").GetInt32() > 0);
    }

    /// <summary>
    /// A TURN CANNOT RUN FOR EVER ON CHEAP REQUESTS. The token bounds stop a turn that is spending;
    /// this stops one that is not, and the stop is the app's rather than the provider's limits.
    /// </summary>
    [Fact]
    public async Task A_turn_that_never_stops_asking_for_tools_ends_at_the_request_limit()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 0, output: 0));

        using var runtime = Runtime(provider, TurnAllowance.From(100_000_000, 100_000_000));
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation");

        Assert.Equal(ApiConversation.MaxRequestsPerTurn, provider.Requests.Count);
        Assert.Equal(ApiConversation.EndedOverBudget, ended!.Outcome);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    int Rows(string state)
    {
        using var c = _db.Cmd("SELECT COUNT(*) FROM ai_attempt WHERE state=$s", ("$s", state));
        return Convert.ToInt32(c.ExecuteScalar());
    }

    IReadOnlyList<AiAttempt> Attempts()
    {
        var ids = new List<string>();
        using (var c = _db.Cmd("SELECT id FROM ai_attempt ORDER BY started_at, id"))
        using (var r = c.ExecuteReader())
            while (r.Read()) ids.Add(r.GetString(0));

        var store = new AiAttemptStore(_db);
        return [.. ids.Select(id => store.Get(id)!)];
    }
}
