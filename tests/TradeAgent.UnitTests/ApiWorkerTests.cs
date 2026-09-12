using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1 — THE TURN THE APP RUNS ITSELF: the role's mission and the Situation in, the provider's
/// tool-calling loop executed by TradeAgent, and one <c>TurnEnded</c> out carrying the usage SUMMED
/// over every response.
///
/// <para><b>The property that matters for money.</b> A turn is not one request. On a vendor CLI the
/// app sees one process and one usage event, so summing and last-wins are the same number; here a turn
/// is as many requests as the model asks for, each reporting its own tokens, and a bill taken from the
/// LAST response is a three-request turn charged as one. That is not a rounding error — it is the
/// day's ceiling holding back a third of what it should.</para>
///
/// <para>Every turn below runs against <see cref="FakeProvider"/> on loopback. No key is real:
/// the harness only needs a non-empty string to send, and the fake asserts what arrived.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class ApiWorkerTests : IDisposable
{
    readonly string _home = Path.Combine(TestEnv.Home, $"harness-{Guid.NewGuid():n}");

    /// <summary>Not a key. Any non-empty string reaches the wire; the fake reads the header back.</summary>
    const string Pretend = "not-a-real-credential";

    public ApiWorkerTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { }
    }

    /// <summary>The shipped manifest, pointed at the loopback fake. Everything else is as it ships.</summary>
    RuntimeManifest Manifest(FakeProvider provider)
    {
        var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
        manifest.BaseUrl = provider.BaseUrl;
        return manifest;
    }

    ApiAgentRuntime Runtime(FakeProvider provider, string? key = Pretend,
        Func<string, IWorkerTools>? tools = null, TurnAllowance? allowance = null,
        TimeSpan? timeout = null) =>
        new(Manifest(provider), () => key, tools,
            allowance: () => allowance ?? TurnAllowance.Default,
            requestTimeout: timeout ?? TimeSpan.FromSeconds(10));

    IAgentConversation Research(ApiAgentRuntime runtime) =>
        runtime.OpenConversation(CouncilRoles.Research, () => _home,
            () => new Dictionary<string, string>(), () => null);

    // ---- the turn ---------------------------------------------------------------------------------

    /// <summary>
    /// ONE REQUEST, ONE ANSWER, AND THE USAGE THE PROVIDER REPORTED. The baseline: without this the
    /// summing test below could pass on a harness that never sent anything at all.
    /// </summary>
    [Fact]
    public async Task A_turn_sends_the_model_the_mission_and_the_situation_and_reports_what_it_used()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("I read the plan.", input: 1234, output: 56, cached: 34));

        File.WriteAllText(Path.Combine(_home, "AGENTS.md"), "You are the Research Director. Do research.");

        using var runtime = Runtime(provider);
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation\nnothing has happened.");

        Assert.NotNull(ended);
        Assert.Equal(ApiConversation.Completed, ended!.ExitCode);
        Assert.Equal(1234, ended.Usage!.InputTokens);
        Assert.Equal(56, ended.Usage.OutputTokens);
        // NESTED in prompt_tokens_details, which is where the provider puts it. A build that missed it
        // would charge all 1,234 at the full input rate instead of 1,200 of them.
        Assert.Equal(34, ended.Usage.CachedInputTokens);
        Assert.Equal("gpt-5.6-luna", ended.Usage.Model);

        var sent = Assert.Single(provider.Requests);
        using var body = JsonDocument.Parse(sent);
        Assert.Equal("gpt-5.6-luna", body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("Research Director", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Contains("nothing has happened", messages[1].GetProperty("content").GetString());
        // The provider's own max-output parameter, by the name the manifest gives it.
        Assert.Equal(TurnAllowance.Default.OutputTokens,
            body.RootElement.GetProperty("max_completion_tokens").GetInt64());

        Assert.Equal($"Bearer {Pretend}", Assert.Single(provider.Keys));
        Assert.Contains(conversation.History, t => t.Role == ChatRole.Ai && t.Text == "I read the plan.");
    }

    /// <summary>
    /// THE MUTANT'S TEST. Three requests in one turn, each reporting its own tokens, and the turn is
    /// charged the sum. Watch <c>total.Plus(reported)</c> become <c>reported</c> in
    /// <c>ApiConversation.RunRequestsAsync</c> and this goes red on the input count.
    /// </summary>
    [Fact]
    public async Task A_turn_of_several_requests_is_charged_what_every_response_together_reported()
    {
        using var provider = new FakeProvider();
        provider
            .Answer(FakeProvider.ToolCall("read_file", new { path = "in/brief.md" }, "c1", input: 1_000, output: 20))
            .Answer(FakeProvider.ToolCall("read_file", new { path = "in/brief.md" }, "c2", input: 2_000, output: 30))
            .Answer(FakeProvider.Message("Done.", input: 3_000, output: 40));

        using var runtime = Runtime(provider);
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation");

        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(6_000, ended!.Usage!.InputTokens);
        Assert.Equal(90, ended.Usage.OutputTokens);
        Assert.Equal(ApiConversation.Completed, ended.ExitCode);
    }

    /// <summary>
    /// DEFAULT DENY, AT THE SURFACE. A role with no configured tools offers none and refuses every
    /// call by name — and the turn CARRIES ON, because a worker that asked for something it may not
    /// have should learn that rather than have its turn die.
    /// </summary>
    [Fact]
    public async Task A_tool_nobody_granted_is_refused_in_words_and_the_turn_continues()
    {
        using var provider = new FakeProvider();
        provider
            .Answer(FakeProvider.ToolCall("run_shell", new { command = "rm -rf /" }))
            .Answer(FakeProvider.Message("Understood."));

        using var runtime = Runtime(provider);
        var conversation = Research(runtime);
        await conversation.SendMissionAsync("## Situation");

        Assert.Equal(2, provider.Requests.Count);

        // Nothing was offered, so the request carried no tool list at all.
        using var first = JsonDocument.Parse(provider.Requests[0]);
        Assert.False(first.RootElement.TryGetProperty("tools", out _));

        // The refusal went back as the tool's own answer, naming the tool.
        using var second = JsonDocument.Parse(provider.Requests[1]);
        var messages = second.RootElement.GetProperty("messages");
        var tool = messages[messages.GetArrayLength() - 1];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("call-1", tool.GetProperty("tool_call_id").GetString());
        Assert.Contains("not a tool this launch was granted", tool.GetProperty("content").GetString());

        Assert.Contains(conversation.History, t => t.Role == ChatRole.Tool && t.Text == "Refused run_shell");
    }

    /// <summary>
    /// USAGE THAT NEVER ARRIVES STAYS UNRESOLVED. Null rather than a zeroed <c>TurnUsage</c>, because
    /// the launch ledger charges a reservation for the first and nothing for the second — and a turn
    /// the provider has already been paid for must never read as free.
    /// </summary>
    [Fact]
    public async Task A_response_that_reports_no_usage_leaves_the_turn_unresolved_rather_than_zero()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.WithoutUsage("No idea what that cost."));

        using var runtime = Runtime(provider);
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation");

        Assert.Equal(ApiConversation.Completed, ended!.ExitCode);
        Assert.Null(ended.Usage);
    }

    /// <summary>
    /// A NEVER-ANSWERING ENDPOINT FAILS IN SECONDS. The failure that costs most here is the one that
    /// looks like thinking, so the timeout is named as a timeout and the turn ends.
    /// </summary>
    [Fact]
    public async Task An_endpoint_that_never_answers_ends_the_turn_within_its_timeout()
    {
        using var provider = new FakeProvider(answers: false);
        using var runtime = Runtime(provider, timeout: TimeSpan.FromSeconds(2));
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await conversation.SendMissionAsync("## Situation");
        clock.Stop();

        Assert.Equal(ApiConversation.ProviderFailed, ended!.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20),
            $"the turn took {clock.Elapsed.TotalSeconds:N1}s, so it did not fail on its own timeout: "
            + string.Join("\n  ", provider.Marks));
        Assert.Contains(conversation.History,
            t => t.Role == ChatRole.System && t.Text.Contains("did not answer within"));
    }

    /// <summary>
    /// NO KEY STARTS NOTHING — item 4's rule, asserted at the boundary that enforces it. The turn still
    /// ends, so the loop and the ledger account for it rather than losing a turn that vanished.
    /// </summary>
    [Fact]
    public async Task A_role_on_the_harness_with_no_key_sends_nothing_at_all()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("should never be asked for"));

        using var runtime = Runtime(provider, key: null);
        var conversation = Research(runtime);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendMissionAsync("## Situation");

        Assert.Empty(provider.Requests);
        Assert.Equal(ApiConversation.NotStarted, ended!.ExitCode);
        Assert.Null(ended.Usage);
        Assert.Contains(conversation.History,
            t => t.Role == ChatRole.System && t.Text == Labels.HarnessKeyNotHeld);
    }

    /// <summary>
    /// THE RUNTIME IS NOT A PROGRAM, and the members that describe one say so rather than pretending.
    /// A no-op that reported success would leave the owner waiting for an installer to finish.
    /// </summary>
    [Fact]
    public async Task The_harness_reports_itself_as_an_endpoint_and_refuses_to_store_a_key()
    {
        using var provider = new FakeProvider();
        using var runtime = Runtime(provider);

        var detection = await runtime.DetectAsync();
        Assert.True(detection.Installed);
        Assert.Equal(provider.BaseUrl.TrimEnd('/') + "/chat/completions", detection.Path);
        Assert.False(runtime.Capabilities.CanInstallItself);
        Assert.False(runtime.Capabilities.BrowserAuth);

        Assert.Equal(AuthState.Authenticated, await runtime.GetAuthenticationStateAsync());
        Assert.Equal(HealthState.READY, await runtime.GetHealthAsync());

        var refusal = await Assert.ThrowsAsync<TradeAgentException>(
            () => runtime.SignInWithApiKeyAsync("anything"));
        Assert.Contains("holds its key in memory only", refusal.Message);

        using var keyless = Runtime(provider, key: null);
        Assert.Equal(AuthState.NotAuthenticated, await keyless.GetAuthenticationStateAsync());
        Assert.Equal(HealthState.FAILED, await keyless.GetHealthAsync());
        var notStarted = await Assert.ThrowsAsync<TradeAgentException>(() => keyless.StartAsync());
        Assert.Equal(ErrorCode.AI_AUTH_REQUIRED, notStarted.Info.Code);
    }
}
