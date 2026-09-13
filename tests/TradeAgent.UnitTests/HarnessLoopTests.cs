using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE MISSION LOOP DRIVING THE APP-OWNED HARNESS, END TO END, THROUGH THE LOOP'S OWN PATH.
///
/// <para><b>What was missing.</b> <c>U-api-worker</c> proved every piece of the harness by driving
/// <see cref="ApiConversation"/> directly — a turn, the summed usage, the bounds, the tool grants —
/// and its own report says so: "no test drives <c>MissionLoop</c> over a harness conversation". Each
/// piece being right is not the same claim as the loop's own path being right, and the path is where
/// the money is: the admission inside <c>TurnMeter.Begin</c>'s transaction, the ONE committed
/// transition at the end (<c>docs/COUNCIL.md</c> rule 6), the relay publishing
/// <c>out/report-&lt;attempt&gt;.md</c> under the launch its NAME carries, and the Situation the turn
/// is given. Nothing below calls the conversation: every turn here starts at
/// <see cref="MissionLoop.TurnAsync"/> and reaches the provider fake through
/// <see cref="IMissionHost.ConversationFor"/>, which is the one line that decides whether a role on
/// the harness runs on it at all.</para>
///
/// <para><b>And the cut turn.</b> A turn the app stops on its own bound has done real work, so it
/// ends like any other: ENDED with the reason in <c>ai_attempt.context</c>, its staged files
/// published under that attempt on the SAME commit, and the next Situation told it was cut and by
/// which bound — a role that is not told spends its next turn working out where its report went.</para>
///
/// <para>Every request goes to <see cref="FakeProvider"/> on loopback. No key is real.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class HarnessLoopTests(ITestOutputHelper log) : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _root = Path.Combine(TestEnv.Home, $"harness-loop-{Guid.NewGuid():n}");

    /// <summary>Not a key. The fake reads the header back; nothing real is ever sent anywhere.</summary>
    const string Pretend = "not-a-real-credential";

    const string Brief = "Find out whether the March gap is in the archive or the download.";
    const string Report = "The 1-minute bars have a 40-minute gap on 2026-03-09.\nIt is in the archive.";

    /// <summary>The heartbeat off, so every turn below has a named cause the test put there.</summary>
    static readonly MissionOptions NoHeartbeat = new() { ReviewEvery = TimeSpan.Zero };

    /// <summary>gpt-5.6-luna on this runtime's own page: input 0.20, cached 0.020, WRITES 0.25, output 1.20.</summary>
    const decimal Input = 0.20m, CacheWrite = 0.25m, Output = 1.20m;

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    // ---- the world -------------------------------------------------------------------------------

    /// <summary>
    /// THE APP, COMPOSED THE WAY <c>AppHost</c> COMPOSES IT for a role on the harness: the real
    /// <see cref="TurnMeter"/> behind the admission, the real <see cref="CouncilRelay"/> behind the
    /// committed transition, the real <see cref="GrantedWorkerTools"/> behind the tools, and a real
    /// <see cref="ApiConversation"/> pointed at the loopback fake.
    ///
    /// <para>The chair's conversation is a STAND-IN that reaches no provider, and that is deliberate:
    /// it is what the mutant hits. Turn <c>_host.ConversationFor(role)</c> back into
    /// <c>_host.Conversation</c> in <see cref="MissionLoop.TurnAsync"/> — the pre-council behaviour,
    /// and exactly "ConversationFor returning the CLI conversation for a role on the harness" — and
    /// the Research turn is sent to this stand-in, the fake provider sees no request at all, and
    /// every assertion about <c>tool_call</c> and the publication fails.</para>
    /// </summary>
    sealed class Harness : IMissionHost, IDisposable
    {
        readonly ApiAgentRuntime _runtime;
        readonly CouncilRelay _relay;
        readonly IDisposable _metering;
        readonly Database _db;

        public Harness(Database db, string root, FakeProvider provider, TurnAllowance allowance,
            string? key = Pretend, decimal cap = 50m)
        {
            _db = db;
            Root = root;
            Events = new MissionEventStore(db);
            Ledger = new ToolCallStore(db);

            foreach (var role in CouncilRoles.All)
            {
                Directory.CreateDirectory(Path.Combine(HomeFor(role), WorkspaceBuilder.InDir));
                Directory.CreateDirectory(Path.Combine(HomeFor(role), WorkspaceBuilder.OutDir));
                Directory.CreateDirectory(Path.Combine(HomeFor(role), "trading"));
                Directory.CreateDirectory(Path.Combine(HomeFor(role), ".tradeagent"));
            }

            // THE SHIPPED MANIFEST, POINTED AT THE LOOPBACK FAKE. Everything else is as it ships, so
            // the model, the endpoint's shape and the max-output parameter's name are the product's.
            var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
            manifest.BaseUrl = provider.BaseUrl;

            // THE ROLE'S OWN RUNTIME AND MODEL, which is the pair a price is looked up by: Research is
            // on the harness and the chair is not, exactly as `AppHost.RuntimeForRole` resolves it.
            Meter = new TurnMeter(db, cap: () => cap,
                runtimeId: () => "codex", recordPath: Path.Combine(root, "turns.jsonl"),
                model: () => null, allowance: () => allowance, share: _ => 1m,
                roleModel: role => role == CouncilRoles.Research ? manifest.DefaultModel : null,
                roleRuntime: role => role == CouncilRoles.Research ? ApiAgentRuntime.RuntimeId : "codex");

            _runtime = new ApiAgentRuntime(manifest, () => key, tools: ToolsFor,
                attemptId: role => Meter.OpenAttemptIdFor(role),
                allowance: () => allowance, requestTimeout: TimeSpan.FromSeconds(10));

            Research = _runtime.OpenConversation(CouncilRoles.Research,
                workspace: () => HomeFor(CouncilRoles.Research),
                environment: () => new Dictionary<string, string>(),
                model: () => null);

            // METERED ON THIS LINE AND NOWHERE ELSE, as `AppHost.ConversationFor` does it: the
            // admission gate and the held close both arrive with it.
            _metering = Meter.Attach(Research, CouncilRoles.Research);

            _relay = new CouncilRelay(db, HomeFor);
        }

        public string Root { get; }
        public MissionEventStore? Events { get; }
        public ToolCallStore Ledger { get; }
        public TurnMeter Meter { get; }
        public IAgentConversation Research { get; }

        /// <summary>The relay itself, for the boundary seam the committed transition is read at.</summary>
        public CouncilRelay Handoff => _relay;

        /// <summary>The chair's, which is NOT the harness. See the type's summary.</summary>
        public StandIn Chair { get; } = new();

        /// <summary>Every launch this host opened, in order, as (role, the prompt that was sent).</summary>
        public List<(string Role, string Prompt)> Opened { get; } = [];

        public IAgentConversation? Conversation => Chair;

        public IAgentConversation? ConversationFor(string role) =>
            role == CouncilRoles.Research ? Research : Chair;

        public string AgentHome => HomeFor(CouncilRoles.Default);
        public string HomeFor(string role) => WorkspaceBuilder.HomeOf(Root, role);

        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public AiSpendToday Spend => Meter.Today;
        public AiSpendToday SpendFor(string role) => Meter.TodayFor(role);

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            SituationAsync(CouncilRoles.Default, ct);

        /// <summary>
        /// The Situation the app writes. <see cref="MissionSituation.Guidance"/> NAMES THE FILE the
        /// canned turn reads, so the read the fake asks for is one the app's own message asked for.
        /// </summary>
        public Task<MissionSituation> SituationAsync(string role, CancellationToken ct) =>
            Task.FromResult(new MissionSituation
            {
                Role = role,
                Mode = nameof(TradingMode.PAPER),
                Spend = SpendFor(role),
                Guidance = $"Start from `{WorkspaceBuilder.InDir}/brief.md`."
            });

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes) =>
            BeginTurn(prompt, wakes, CouncilRoles.Default);

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role)
        {
            Opened.Add((role, prompt));
            return Meter.Begin(prompt, wakes, role);
        }

        public string? NextAttemptId() => Meter.Mint();

        public void Relay(string role, string? attempt) => _relay.Run(role, attempt);

        /// <summary>The one committed transition, as <c>AppHost</c> wires it.</summary>
        public void CommitTurn(string role, string? attempt, Action dispositions) =>
            _relay.CommitTurn(role, attempt, () => Meter.CommitStaged(role), dispositions);

        public MissionDelivery? Delivered(string publicationId)
        {
            var p = new PublicationStore(_db).Get(publicationId);
            return p is null ? null : new MissionDelivery(p.Id, p.Kind, p.Role, p.Content);
        }

        IWorkerTools ToolsFor(string role) =>
            new GrantedWorkerTools(role, home: () => HomeFor(role),
                attempt: () => Meter.OpenAttemptIdFor(role), gateway: null, ledger: Ledger);

        public void Dispose()
        {
            _metering.Dispose();
            _runtime.Dispose();
        }
    }

    /// <summary>
    /// A CONVERSATION THAT REACHES NOTHING. It is the chair's in every test here, so a turn that was
    /// sent to the wrong conversation is visible as a request the provider never received — and as a
    /// message this object kept.
    /// </summary>
    sealed class StandIn : IAgentConversation
    {
        public List<string> Sent { get; } = [];
        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];

        public event Action<ChatTurn>? TurnAdded;
        public event Action<string>? Delta;
        public event Action? StateChanged;
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Run(message);
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Run(message);

        Task Run(string message)
        {
            Sent.Add(message);
            var turn = new ChatTurn(ChatRole.Ai, "the stand-in did nothing.", DateTimeOffset.UtcNow);
            TurnAdded?.Invoke(turn);
            StateChanged?.Invoke();
            Delta?.Invoke("");
            TurnEnded?.Invoke(new AgentTurnEnded(0, TimeSpan.FromMilliseconds(1), "", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> TakeTyped() => [];
        public Task StopAsync() => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;
        public void Queue(string message) { }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    void WakeResearch(Harness host, string id = "1")
    {
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        host.Events!.Raise(MissionEventIds.ForRole($"{MissionEventKind.Review}:{id}", CouncilRoles.Research),
            MissionEventKind.Review, earlier, role: CouncilRoles.Research);
    }

    AiAttempt? Row(string id) => new AiAttemptStore(_db).Get(id);

    List<Publication> Reports() =>
        [.. new PublicationStore(_db).By(CouncilRoles.Research)
            .Where(p => p.Kind == PublicationKind.Report)];

    /// <summary>The tool message the app put back into the conversation, off the wire.</summary>
    static string ToolResult(string requestBody)
    {
        using var body = JsonDocument.Parse(requestBody);
        var messages = body.RootElement.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];
        Assert.Equal("tool", last.GetProperty("role").GetString());
        return last.GetProperty("content").GetString() ?? "";
    }

    // ---- item 1: a loop turn on the harness, end to end -------------------------------------------

    /// <summary>
    /// ONE RESEARCH TURN, FROM <see cref="MissionLoop.TurnAsync"/> TO THE PUBLICATION, and nothing in
    /// between called by hand.
    ///
    /// <para>The loop admits the turn, launches it through <see cref="IMissionHost.ConversationFor"/>,
    /// the harness sends the Situation the app wrote, the canned turn reads the file that message
    /// named and writes its report under the launch id that message gave it, and the turn ends with
    /// the usage SUMMED over all three responses. Then the one committed transition: the launch closed
    /// at the exact price of the model on each response, the report published under that attempt, and
    /// the chair's paid task raised — read at both boundaries, because "one transition" is a claim
    /// about what is true either side of it.</para>
    /// </summary>
    [Fact]
    public async Task A_research_turn_on_the_harness_runs_through_the_loop_and_lands_in_one_transition()
    {
        using var provider = new FakeProvider();
        using var host = new Harness(_db, _root, provider, TurnAllowance.Default);

        File.WriteAllText(
            Path.Combine(host.HomeFor(CouncilRoles.Research), WorkspaceBuilder.InDir, "brief.md"), Brief);

        // THE ID THE LOOP WILL LAUNCH UNDER, taken from the meter's own mint — which is idempotent
        // until `Begin` takes it, so this is the same id `NextAttemptId` hands the turn and the name
        // the canned `write_file` has to use. Minting is not recording: nothing is written here.
        var attempt = host.Meter.Mint();

        provider
            .Answer(FakeProvider.ToolCall("read_file", new { path = $"{WorkspaceBuilder.InDir}/brief.md" },
                "c1", input: 1_000, output: 20))
            .Answer(FakeProvider.ToolCall("write_file",
                new { path = $"{WorkspaceBuilder.OutDir}/report-{attempt}.md", content = Report },
                "c2", input: 2_000, output: 30))
            .Answer(FakeProvider.Message("Written to out/.", input: 3_000, output: 40));

        // EITHER SIDE OF THE TRANSITION, read from the database rather than assumed.
        var seen = new List<string>();
        host.Handoff.Boundary = at =>
        {
            if (at is CouncilRelay.BeforeCommit or CouncilRelay.AfterCommit)
                seen.Add($"{at}: {Row(attempt)?.State.ToString() ?? "no row"}, {Reports().Count} published");
        };

        WakeResearch(host);
        await new MissionLoop(host, NoHeartbeat).TurnAsync();

        // ---- the loop's own path, and nothing else --------------------------------------------
        // THE REQUEST COUNT FIRST, because it is the mutant's own symptom: a Research turn sent to
        // the chair's conversation reaches no provider at all, and 0 of 3 says so before anything
        // downstream has a chance to fail for a second reason.
        Assert.Equal(3, provider.Requests.Count);
        Assert.Empty(host.Chair.Sent);
        Assert.Equal([CouncilRoles.Research], host.Opened.Select(o => o.Role));

        // The message the model was sent is the one the app composed: the role, the launch id it must
        // name its output after, and the file to start from.
        var prompt = host.Opened[0].Prompt;
        using (var first = JsonDocument.Parse(provider.Requests[0]))
            Assert.Equal(prompt,
                first.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Contains($"You are the {CouncilRoles.Title(CouncilRoles.Research)}", prompt);
        Assert.Contains($"This turn is attempt `{attempt}`", prompt);
        Assert.Contains($"{WorkspaceBuilder.OutDir}/report-{attempt}.md", prompt);

        // THE FILE'S TEXT ENTERED THE NEXT REQUEST, which is what "the app served the read" means.
        Assert.Equal(Brief, ToolResult(provider.Requests[1]));
        Assert.Contains("staged", ToolResult(provider.Requests[2]));

        // ---- the launch record --------------------------------------------------------------
        var row = Row(attempt);
        Assert.NotNull(row);
        Assert.Equal(AiAttemptState.ENDED, row!.State);
        Assert.Equal(CouncilRoles.Research, row.Role);
        Assert.Equal(ApiAgentRuntime.RuntimeId, row.Runtime);
        Assert.Equal(ApiConversation.Completed, row.ExitCode);

        // RESERVED BEFORE THE FIRST REQUEST, at the role's own runtime and model: the allowance's
        // input at the DEARER of the input and cache-write rates, plus its output at the output rate.
        Assert.Equal((TurnAllowance.Default.InputTokens * CacheWrite
                      + TurnAllowance.Default.OutputTokens * Output) / 1_000_000m, row.ReservedCost);

        // SUMMED OVER EVERY RESPONSE, and charged at the model each of them named.
        Assert.Equal(6_000, row.InputTokens);
        Assert.Equal(90, row.OutputTokens);
        Assert.Equal("gpt-5.6-luna", row.EffectiveModel);
        Assert.Equal((6_000m * Input + 90m * Output) / 1_000_000m, row.Cost);

        // ---- the tool calls, under that attempt ----------------------------------------------
        var calls = host.Ledger.ForAttempt(attempt);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(CouncilRoles.Research, c.Role));
        Assert.All(calls, c => Assert.True(c.Served, c.Refusal));
        Assert.Equal($"read_file {WorkspaceBuilder.InDir}/brief.md", calls[0].Argument);
        Assert.StartsWith($"write_file {WorkspaceBuilder.OutDir}/report-{attempt}.md (", calls[1].Argument);

        // ---- what the transition published ---------------------------------------------------
        var published = Assert.Single(Reports());
        Assert.Equal(Report, published.Content);
        Assert.Equal(attempt, published.Attempt);

        // AND THE CHAIR'S ONE PAID TASK, keyed by the publication.
        var task = Assert.Single(new MissionEventStore(_db).OfKind(PublicationKind.Report));
        Assert.Equal(MissionEventIds.ForRole(MissionEventIds.Task(published.Id), CouncilRoles.Operations),
            task.Id);
        Assert.Equal(CouncilRoles.Operations, task.For);
        Assert.True(File.Exists(Path.Combine(host.HomeFor(CouncilRoles.Operations),
            WorkspaceBuilder.InDir, $"{published.Id}.md")));

        // ---- one commit, and one state either side of it -------------------------------------
        log.WriteLine(string.Join("\n", seen));
        Assert.Equal(
            [$"{CouncilRelay.BeforeCommit}: {AiAttemptState.LAUNCHED}, 0 published",
             $"{CouncilRelay.AfterCommit}: {AiAttemptState.ENDED}, 1 published"],
            seen);
    }

    // ---- item 2: a turn cut mid-loop ends like any other ------------------------------------------

    /// <summary>
    /// A TURN THE APP STOPPED ON ITS OWN BOUND IS COMMITTED EXACTLY LIKE ONE THAT FINISHED, AND THE
    /// NEXT TURN IS TOLD.
    ///
    /// <para>Three claims, and the third is the one that was missing. The launch ends ENDED with
    /// <c>CONTEXT_BUDGET_EXCEEDED</c> in <c>ai_attempt.context</c> — bounded is not broken, and rule 4
    /// keeps enforcement apart from billing. What the turn had already staged is kept and published
    /// under that attempt, on the SAME commit as the close, because a report written by a turn that
    /// then ran out of context is still the role's work and the owner has already paid for it. And the
    /// next Situation says the turn was cut and which bound cut it: a role that reads a half-written
    /// plan with no explanation spends its next turn — at the owner's expense — working out where its
    /// report went, or writes it again.</para>
    ///
    /// <para>The mutant is the guard whose POSITION is the property: <c>MissionLoop.Commit</c>
    /// returning early for a turn that failed. A cut turn has exit code 1, so the transition never
    /// runs, and the launch is left open with its work nowhere.</para>
    /// </summary>
    [Fact]
    public async Task A_turn_cut_by_its_own_bound_commits_like_any_other_and_the_next_one_is_told()
    {
        using var provider = new FakeProvider();
        // 300 INPUT TOKENS, reached EXACTLY: the write lands, the read takes the turn to the bound,
        // and the third request is never sent — reaching a bound exactly is already too late.
        using var host = new Harness(_db, _root, provider, TurnAllowance.From(300, 20_000));

        File.WriteAllText(
            Path.Combine(host.HomeFor(CouncilRoles.Research), WorkspaceBuilder.InDir, "brief.md"), Brief);

        var attempt = host.Meter.Mint();
        provider
            .Answer(FakeProvider.ToolCall("write_file",
                new { path = $"{WorkspaceBuilder.OutDir}/report-{attempt}.md", content = Report },
                "c1", input: 100, output: 5))
            .Answer(FakeProvider.ToolCall("read_file", new { path = $"{WorkspaceBuilder.InDir}/brief.md" },
                "c2", input: 200, output: 5));

        var seen = new List<string>();
        host.Handoff.Boundary = at =>
        {
            if (at is CouncilRelay.BeforeCommit or CouncilRelay.AfterCommit)
                seen.Add($"{at}: {Row(attempt)?.State.ToString() ?? "no row"}, {Reports().Count} published");
        };

        WakeResearch(host, "cut");
        var loop = new MissionLoop(host, NoHeartbeat);
        await loop.TurnAsync();

        Assert.Equal(2, provider.Requests.Count);

        // ---- ENDED, with the reason ----------------------------------------------------------
        var row = Row(attempt);
        Assert.NotNull(row);
        Assert.Equal(AiAttemptState.ENDED, row!.State);
        Assert.Equal(ApiConversation.BudgetExceeded, row.ExitCode);
        Assert.Equal(300, row.InputTokens);
        using (var context = JsonDocument.Parse(row.Context!))
            Assert.Equal(nameof(ErrorCode.CONTEXT_BUDGET_EXCEEDED),
                context.RootElement.GetProperty("ended").GetString());

        // ---- the staged file kept, and published under that attempt on the same commit -------
        Assert.True(File.Exists(Path.Combine(host.HomeFor(CouncilRoles.Research),
            WorkspaceBuilder.OutDir, $"report-{attempt}.md")));
        var published = Assert.Single(Reports());
        Assert.Equal(Report, published.Content);
        Assert.Equal(attempt, published.Attempt);
        Assert.Single(new MissionEventStore(_db).OfKind(PublicationKind.Report));

        log.WriteLine(string.Join("\n", seen));
        Assert.Equal(
            [$"{CouncilRelay.BeforeCommit}: {AiAttemptState.LAUNCHED}, 0 published",
             $"{CouncilRelay.AfterCommit}: {AiAttemptState.ENDED}, 1 published"],
            seen);

        // ---- and the next Situation says so, in the words the cut turn was given --------------
        provider.Answer(FakeProvider.Message("Understood; carrying on.", input: 10, output: 5));
        WakeResearch(host, "after-the-cut");
        await loop.TurnAsync();

        var next = Assert.Single(host.Opened.Skip(1)).Prompt;
        log.WriteLine(next);
        Assert.Contains("TradeAgent stopped your last turn", next);
        Assert.Contains("300 input tokens of the 300 one turn is allowed", next);
        Assert.Contains("is kept", next);

        // AND IT IS SAID ONCE. The turn that was told about it finished normally, so the turn after
        // that is not told again — a notice that outlived the thing it describes would have the role
        // reading every future turn as the aftermath of one cut one.
        provider.Answer(FakeProvider.Message("Still going.", input: 10, output: 5));
        WakeResearch(host, "and-after-that");
        await loop.TurnAsync();

        Assert.DoesNotContain("TradeAgent stopped your last turn", host.Opened[2].Prompt);
    }
}
