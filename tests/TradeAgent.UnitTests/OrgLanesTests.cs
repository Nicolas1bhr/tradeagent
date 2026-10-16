using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
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

    // ---- item 4: lane policy as data ------------------------------------------------------------------------------

    static readonly LaneProfile Cell = new(LaneAssurance.Confined, "test-cell", InboxUnwritable: false);

    /// <summary>
    /// (c3) RED at base (no code ceiling): A SECOND UNCONFINED LANE IS NEVER STARTED, WHATEVER THE SETTINGS FILE SAYS. The settings
    /// ask for three lanes, three per pool and three per cell class; the chair's unconfined turn is held inside its run, and the
    /// Research Director's due look is refused its lease — nothing is sent to it — until the chair's turn has ended.
    /// </summary>
    [Fact]
    public async Task A_second_unconfined_lane_is_never_dispatched_whatever_the_settings_file_says()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = new LaneHost { Events = events, Lanes = new LaneSettings(3, 3, 3) };
        var chair = host.Conv(Ops);
        chair.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = new MissionLoop(host, new MissionOptions());

        var first = Task.Run(() => loop.TurnAsync());
        await chair.Inside.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await loop.TurnAsync();
        Assert.Empty(host.Conv(Research).Sent);

        chair.Hold.SetResult();
        await first;
        await loop.TurnAsync();
        Assert.Single(host.Conv(Research).Sent);
    }

    /// <summary>
    /// (e) RED at base (no pool; holds keyed by runtime): A POOL AT ITS LIMIT ADMITS NO FURTHER LANE, AND A VENDOR HOLD HOLDS THE
    /// POOL. Two confined positions on one runtime, one turn per pool: the second is refused its lease while the first runs. Then
    /// a vendor limit reached by a launch whose runtime nobody could say — its pool unknown, so it collapses into every pool —
    /// holds the other position's launch on <c>codex</c> until the time the vendor named.
    /// </summary>
    [Fact]
    public async Task A_pool_at_its_limit_admits_no_further_lane_and_a_vendor_hold_holds_the_pool()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = new LaneHost { Events = events, Lanes = new LaneSettings(3, PerPool: 1, PerCellClass: 3) };
        host.Profiles[Ops] = Cell;
        host.Profiles[Research] = Cell;
        host.Runtimes[Ops] = "codex";
        host.Runtimes[Research] = "codex";
        var chair = host.Conv(Ops);
        chair.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = new MissionLoop(host, new MissionOptions());

        var first = Task.Run(() => loop.TurnAsync());
        await chair.Inside.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await loop.TurnAsync();
        Assert.Empty(host.Conv(Research).Sent);
        chair.Hold.SetResult();
        await first;

        // ---- the hold: the chair's runtime is not known, so its pool is every pool -----------------------------------
        host.Runtimes[Ops] = null;
        Due(events, Ops, TimeSpan.FromMinutes(1));
        chair.Limit = new VendorLimit("You've hit your usage limit.", DateTimeOffset.UtcNow.AddHours(1));
        chair.Hold = null;
        host.Refusals[Research] = new TurnRefusal(RefusalKind.HostOrLaunch, "stepped over while the chair turns");
        await loop.TurnAsync();
        Assert.Equal(2, chair.Sent.Count);
        host.Refusals.Remove(Research);

        await loop.TurnAsync();
        Assert.Empty(host.Conv(Research).Sent);
        Assert.Equal("codex", host.RuntimeFor(Research));
    }

    /// <summary>
    /// (i) GUARD: NO PIPE OP OR VERB NAMES A LANE OR A POOL, no op takes an argument that would set one, the packet carries no
    /// field for one, and the settings are written from the Safety page alone — in the shape of
    /// <c>OrgLedgerTests.No_pipe_op_writes_an_org_table</c>.
    /// </summary>
    [Fact]
    public void No_pipe_op_or_verb_names_a_lane_or_pool()
    {
        string[] words = ["lane", "pool", "quota", "concurren", "parallel"];
        foreach (var op in GatewaySchema.Ops())
            foreach (var word in words)
            {
                Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, op.Cli, StringComparison.OrdinalIgnoreCase);
                foreach (var arg in op.Args) Assert.DoesNotContain(word, arg.Name, StringComparison.OrdinalIgnoreCase);
            }

        foreach (var field in typeof(MissionSituation).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            foreach (var word in words)
                Assert.DoesNotContain(word, field.Name, StringComparison.OrdinalIgnoreCase);

        var root = RepoRoot();
        Assert.Equal(
            ["src/TradeAgent.App/DashboardView.cs"],
            Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f),
                    @"\.(AiLanes|AiLanesPerPool|AiLanesPerCellClass)\s*=[^=]"))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>RAISING THE LANE COUNT ASKS TWICE AND NAMES THE FIGURE; lowering it saves in one press.</summary>
    [Fact]
    public void Raising_the_lanes_asks_twice_and_lowering_saves_at_once()
    {
        var saved = 0;
        var current = (1, 1, 1);
        var pending = (3, 1, 1);
        var b = SafetyPage.BuildSaveLanes(() => current, () => pending, () => saved++);

        Press(b);
        Assert.Equal(0, saved);
        Assert.Equal(Labels.RaiseLanesArmed(3, 1, 1), b.Content);
        Press(b);
        Assert.Equal(1, saved);

        // A RAISE OF THE POOL'S LIMIT ALONE IS A RAISE: it asks twice too.
        current = (3, 1, 1);
        pending = (3, 2, 1);
        Press(b);
        Assert.Equal(1, saved);
        Press(b);
        Assert.Equal(2, saved);

        // Lowering saves at once.
        current = (3, 2, 1);
        pending = (2, 2, 1);
        Press(b);
        Assert.Equal(3, saved);
    }

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
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
        public LaneSettings Lanes { get; set; } = LaneSettings.Default;
        public Dictionary<string, LaneProfile> Profiles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> Runtimes { get; } = new(StringComparer.Ordinal);

        public LaneProfile LaneOf(string role) => Profiles.TryGetValue(role, out var p) ? p : LaneProfile.Unconfined;
        public string? RuntimeFor(string role) => Runtimes.GetValueOrDefault(role);

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

        /// <summary>Set: the turn waits inside its run until this completes.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>Completed once a turn is inside its run.</summary>
        public TaskCompletionSource Inside { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set: every turn ends refused for this vendor limit.</summary>
        public VendorLimit? Limit { get; set; }

        public async Task SendMissionAsync(string message, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add(message);
            Inside.TrySetResult();
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
            var limit = Limit;
            TurnEnded?.Invoke(new AgentTurnEnded(limit is null ? 0 : 1, TimeSpan.FromSeconds(1), "", DateTimeOffset.UtcNow)
                { Limit = limit });
        }

        /// <summary>A fresh <see cref="Inside"/>, for the next turn to complete.</summary>
        public void Rearm() => Inside = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
