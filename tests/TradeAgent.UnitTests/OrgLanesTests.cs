using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// LANES (<c>U-org-lanes</c>, O9; <c>docs/ORCHESTRATION.md</c> § 2.5): the loop's one tick split into its sweeps, its pick and
/// each picked position's run; up to N positions turning at once, one lease per position, within each quota pool's and cell
/// class's limit, and AT MOST ONE UNCONFINED lane whatever the settings file says; the sweeps once per tick; a material pass
/// owed drains the counted lanes and runs before the next launch.
///
/// <para>No real model anywhere: every test runs on a seam host over a real wake queue, and the conversations are test
/// doubles that start no process. A "confined" lane is this host's own profile — the only producer of one today, never
/// settings or <c>runtimes.json</c>.</para>
/// </summary>
public class OrgLanesTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();

    public void Dispose() => _db.Dispose();

    static readonly string Ops = CouncilRoles.Operations;
    static readonly string Research = CouncilRoles.Research;

    /// <summary>A review look of <paramref name="role"/>, due <paramref name="ago"/> ago.</summary>
    static void Due(MissionEventStore events, string role, TimeSpan ago)
    {
        var at = DateTimeOffset.UtcNow - ago;
        events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(at), role), MissionEventKind.Review, at, role: role);
    }

    // ---- item 3: one tick, many lanes — the split -----------------------------------------------------------------

    /// <summary>
    /// (g) RED at base (one list for every role): THE OWNER'S WORDS FROM A REFUSED TURN REACH ONLY THEIR POSITION. The chair's
    /// turn takes the owner's words off its conversation and is refused by the ledger; the Research Director turns next and
    /// is told nothing of them; the chair's next turn carries them.
    /// </summary>
    [Fact]
    public async Task Owner_words_from_a_refused_turn_reach_only_their_position()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));

        var refuseOnce = true;
        var host = new LaneHost { Events = events };
        host.Conv(Ops).Typed.Add("buy nothing today, just look");
        host.Admit = (role, _) =>
        {
            if (role != Ops || !refuseOnce) return null;
            refuseOnce = false;
            return AiAdmission.NoLedgerOpen;
        };
        var loop = new MissionLoop(host, new MissionOptions());

        await loop.TurnAsync();                                         // the chair's, refused by the ledger
        Assert.Empty(host.Conv(Ops).Sent);

        host.Refusals[Ops] = new TurnRefusal(RefusalKind.HostOrLaunch, "stepped over for this test");
        await loop.TurnAsync();                                         // the Research Director's
        var research = Assert.Single(host.Conv(Research).Sent);
        Assert.DoesNotContain("buy nothing today", research);

        host.Refusals.Remove(Ops);
        await loop.TurnAsync();                                         // the chair's again
        var chair = Assert.Single(host.Conv(Ops).Sent);
        Assert.Contains("buy nothing today, just look", chair);
    }

    /// <summary>
    /// OWED from <c>U-mint-per-launch</c>, RED at base: THE CARD SHOWS THE NEWEST REFUSAL OF THE POSITION IT DESCRIBES. The
    /// chair's launch is refused for its launch (kept as the reason its words wait), then its next launch is refused by the
    /// ledger: the card names the ledger's refusal, not the stale launch refusal before it.
    /// </summary>
    [Fact]
    public async Task The_card_shows_the_newest_refusal_of_the_position_it_describes()
    {
        var events = new MissionEventStore(_db);
        events.Raise("owner:1", MissionEventKind.Owner, DateTimeOffset.UtcNow.AddSeconds(-5));

        var looks = 0;
        var host = new LaneHost { Events = events };
        host.Admit = (_, _) => ++looks == 1
            ? AiAdmission.LaunchRefused("another launch had already taken this turn's wakes")
            : AiAdmission.LedgerRefused("the disk is full");

        MissionLoop? loop = null;
        var statuses = new List<MissionStatus>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop = new MissionLoop(host, new MissionOptions(), delay: (_, ct) =>
        {
            lock (statuses) statuses.Add(loop!.Status);
            if (looks < 2) return Task.CompletedTask;
            second.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        });

        loop.Start();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await loop.PauseAsync();

        var first = statuses[0];
        Assert.Contains("another launch had already taken", first.WaitingFor);
        var last = statuses[^1];
        Assert.Equal(MissionState.Waiting, last.State);
        Assert.True(last.WaitingFor?.Contains("the disk is full") == true,
            $"the card kept a stale launch refusal over the ledger's newer one: {last.WaitingFor}");
        Assert.DoesNotContain("another launch had already taken", last.WaitingFor);
    }

    // ---- the seam host --------------------------------------------------------------------------------------------

    /// <summary>
    /// THE SEAM HOST: one conversation per position, a real wake queue, an admission the test can refuse, and an activation
    /// seam the test can answer. It starts no process and runs no model.
    /// </summary>
    internal sealed class LaneHost : IMissionHost
    {
        readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

        public MissionEventStore? Events { get; init; }
        public Dictionary<string, TurnRefusal> Refusals { get; } = new(StringComparer.Ordinal);

        /// <summary>The admission for (role, wakes), or null to admit it.</summary>
        public Func<string, IReadOnlyList<string>, AiAdmission?>? Admit { get; set; }

        public IAgentConversation? Conversation => Conv(Ops);
        public IAgentConversation? ConversationFor(string role) => Conv(role);
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, $"lanes-home-{Guid.NewGuid():n}");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public Conversation Conv(string role)
        {
            lock (_conversations)
            {
                if (!_conversations.TryGetValue(role, out var c)) _conversations[role] = c = new Conversation();
                return c;
            }
        }

        public TurnRefusal? ActivationRefusal(string role) =>
            Refusals.TryGetValue(role, out var refused) ? refused
            : CouncilRoles.IsKnown(role) ? null
            : new TurnRefusal(RefusalKind.Position, "not on this host's chart");

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role, string? attemptId)
        {
            // ADMITTED WITH NO LEDGER: the loop consumes the wakes itself, as it does for a host built without one.
            return Admit?.Invoke(role, wakes) ?? AiAdmission.NoLedger;
        }
    }

    /// <summary>One position's conversation: records what it was sent and ends every turn at once.</summary>
    internal sealed class Conversation : IAgentConversation
    {
        public List<string> Sent { get; } = [];
        public List<string> Typed { get; } = [];

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];

        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<string>? Delta { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => SendMissionAsync(message, ct);

        public Task SendMissionAsync(string message, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add(message);
            TurnEnded?.Invoke(new AgentTurnEnded(0, TimeSpan.FromSeconds(1), "", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public void Queue(string message) => Typed.Add(message);

        public IReadOnlyList<string> TakeTyped()
        {
            var taken = Typed.ToList();
            Typed.Clear();
            return taken;
        }

        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }
}
