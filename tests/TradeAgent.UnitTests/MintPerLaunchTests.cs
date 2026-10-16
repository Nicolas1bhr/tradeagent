using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EVERY LAUNCH CARRIES THE ID IT MINTED ITSELF, NEVER ANOTHER LAUNCH'S (<c>U-mint-per-launch</c>), and a launch whose
/// wakes another launch took is refused before it is charged.
///
/// <para>The meter held ONE minted slot: <c>Mint()</c> was <c>_minted ??= NewId()</c> and every <c>Begin</c> took it.
/// The owner's chat is admitted through the same meter, so a message typed while the loop composed its Situation took
/// the id the loop had already written into its prompt — the chat's row carried it, the loop's row got another, and the
/// loop's output named a launch that was the owner's chat (<c>docs/ORCHESTRATION.md</c> § 2.5).</para>
///
/// <para>No real model anywhere: the loop runs against a seam host over a real meter and a real database, and the
/// conversations are test doubles that start no process.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class MintPerLaunchTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();

    public void Dispose() => _db.Dispose();

    /// <summary>A meter that prices every launch (6.40 at the shipped list), so "nothing reserved" is a number.</summary>
    TurnMeter Meter() =>
        new(_db, () => 50m, runtimeId: () => "codex", model: () => "gpt-5.6-sol", share: _ => 1m,
            live: new LiveAttempts());

    int Rows()
    {
        using var c = _db.Cmd("SELECT COUNT(*) FROM ai_attempt");
        return Convert.ToInt32(c.ExecuteScalar());
    }

    /// <summary>
    /// (a) RED at base: the chat's row carried the loop's minted id. The owner types into the chair's chat while the
    /// loop is between its mint and its launch; each launch runs under the id it minted, and the loop's prompt, row and
    /// output name are one id.
    /// </summary>
    [Fact]
    public async Task The_owners_chat_admitted_between_a_mint_and_the_loops_launch_takes_its_own_id()
    {
        var meter = Meter();
        var chat = new Chat();
        using var attached = meter.Attach(chat, CouncilRoles.Operations);

        AiAdmission? typed = null;
        var host = new SeamHost(meter) { WhileComposing = () => typed = chat.Admit!("what are you doing?") };

        await new MissionLoop(host).TurnAsync();

        var minted = Assert.Single(host.Minted);
        Assert.NotNull(typed);
        Assert.True(typed!.Admitted, typed.Refusal);
        Assert.NotEqual(minted, typed.Id);

        var launched = Assert.Single(host.Launched);
        Assert.Equal(minted, launched.Id);
        Assert.Contains(minted, Assert.Single(host.Conv.Sent));
        Assert.NotNull(new AiAttemptStore(_db).Get(minted));
        Assert.Equal(2, Rows());
    }

    /// <summary>(a2) RED at base: <c>_minted ??=</c> handed both callers one id. Each mint is its own launch's.</summary>
    [Fact]
    public void Two_mints_before_either_launch_are_two_ids()
    {
        var meter = Meter();
        var first = meter.Mint(CouncilRoles.Research);
        var second = meter.Mint(CouncilRoles.Operations);
        Assert.NotEqual(first, second);

        Assert.Equal(first, meter.Begin("## Situation", role: CouncilRoles.Research, attemptId: first).Id);
        Assert.Equal(second, meter.Begin("## Situation", role: CouncilRoles.Operations, attemptId: second).Id);
        Assert.Equal(2, Rows());
    }

    /// <summary>
    /// (a3) RED at base: <c>OpenAttemptIdFor</c> fell back to the one minted slot, whoever minted it — the grant and the
    /// worker tools of a role with nothing open named another role's coming launch.
    /// </summary>
    [Fact]
    public void A_role_with_no_open_launch_is_never_handed_another_roles_minted_id()
    {
        var meter = Meter();
        var chairs = meter.Mint(CouncilRoles.Operations);

        Assert.Null(meter.OpenAttemptIdFor(CouncilRoles.Research));
        Assert.Equal(chairs, meter.OpenAttemptIdFor(CouncilRoles.Operations));

        // FORGOTTEN WHEN ITS TURN GIVES UP, so it outlives nothing.
        meter.Forget(chairs);
        Assert.Null(meter.OpenAttemptIdFor(CouncilRoles.Operations));
        Assert.False(meter.Begin("## Situation", role: CouncilRoles.Operations, attemptId: chairs).Admitted);
    }

    /// <summary>
    /// (a4) RED at base: <c>Begin</c> launched under whatever slot it found and ignored the caller's id. An id this
    /// meter never minted — another meter's, or a well-formed one nobody minted — and an id already launched under are
    /// refused in words: no row, nothing reserved, no wake consumed.
    /// </summary>
    [Fact]
    public void A_launch_under_an_id_this_meter_never_minted_is_refused()
    {
        var meter = Meter();
        // ANOTHER METER ON THE SAME DATABASE, opened first: one opened after the launch below would turn its row LOST.
        var anothers = Meter().Mint(CouncilRoles.Operations);
        var events = new MissionEventStore(_db);
        var at = DateTimeOffset.Now.AddSeconds(-1);
        events.Raise(MissionEventIds.Fill("EXEC-1"), MissionEventKind.Fill, at);

        var launched = meter.Mint(CouncilRoles.Operations);
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations, attemptId: launched).Admitted);

        foreach (var foreign in new[] { anothers, TurnMeter.IdMintedAt(at), launched })
        {
            var refused = meter.Begin("## Situation", [MissionEventIds.Fill("EXEC-1")], CouncilRoles.Operations,
                attemptId: foreign);
            Assert.False(refused.Admitted, $"a launch under '{foreign}' was admitted");
            Assert.Null(refused.Id);
            Assert.Contains(foreign, refused.Refusal);
        }

        Assert.Equal(1, Rows());
        Assert.Single(events.Due(DateTimeOffset.Now));
        Assert.Equal(meter.Today.NextTurnReservation, meter.Today.Reserved);
    }

    /// <summary>
    /// (c) THE GUARD: a wake answered by one launch buys no second paid turn, across a restart. The first loop launches
    /// once for its one wake; a new meter and a new loop over the same database find nothing due and start nothing.
    /// </summary>
    [Fact]
    public async Task No_due_wake_means_no_paid_turn()
    {
        var events = new MissionEventStore(_db);
        events.Raise(MissionEventIds.Fill("EXEC-1"), MissionEventKind.Fill, DateTimeOffset.Now.AddSeconds(-1));

        var before = new SeamHost(Meter()) { Events = events };
        await new MissionLoop(before).TurnAsync();
        Assert.Single(before.Conv.Sent);
        Assert.Equal(1, Rows());

        // ---- the restart: a new meter (which turns the open row LOST) and a new loop over the same home ----
        var after = new SeamHost(Meter()) { Events = new MissionEventStore(_db) };
        await new MissionLoop(after).TurnAsync();

        Assert.Empty(after.Conv.Sent);
        Assert.Empty(after.Launched);
        Assert.Equal(1, Rows());
    }

    /// <summary>
    /// THE SEAM HOST: an <see cref="IMissionHost"/> over a real meter, minting and launching exactly as the app's host
    /// does, with a hook between the mint and the launch where the owner's chat can be typed into.
    /// </summary>
    sealed class SeamHost(TurnMeter meter) : IMissionHost
    {
        public Recording Conv { get; } = new();
        public List<string> Minted { get; } = [];
        public List<AiAdmission> Launched { get; } = [];
        public Action? WhileComposing { get; init; }
        public MissionEventStore? Events { get; init; }

        public IAgentConversation? Conversation => Conv;
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, $"mint-home-{Guid.NewGuid():n}");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<MissionSituation> SituationAsync(CancellationToken ct)
        {
            WhileComposing?.Invoke();
            return Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });
        }

        public string? NextAttemptId(string role)
        {
            var id = meter.Mint(role);
            Minted.Add(id);
            return id;
        }

        public void ForgetAttemptId(string attemptId) => meter.Forget(attemptId);

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role, string? attemptId)
        {
            var admission = meter.Begin(prompt, wakes, role, attemptId: attemptId);
            if (admission.Admitted) Launched.Add(admission);
            return admission;
        }
    }

    /// <summary>The owner's chat as the meter sees it: a conversation it admits for, which starts no process.</summary>
    sealed class Chat : IAgentConversation, IAdmittedConversation
    {
        public Func<string, AiAdmission>? Admit { get; set; }
        public Func<string, bool>? RecordTyped { get; set; }

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];

        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<string>? Delta { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<AgentTurnEnded>? TurnEnded { add { } remove { } }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    /// <summary>The loop's conversation: records what it was sent and ends every turn at once.</summary>
    sealed class Recording : IAgentConversation
    {
        public List<string> Sent { get; } = [];

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];

        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<string>? Delta { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Run(message);
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Run(message);
        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;

        Task Run(string message)
        {
            Sent.Add(message);
            TurnEnded?.Invoke(new AgentTurnEnded(0, TimeSpan.FromSeconds(1), "", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }
}
