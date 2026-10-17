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

    /// <summary>The longest any step waits: a hang is a red, never a stuck suite.</summary>
    static readonly TimeSpan T = TimeSpan.FromSeconds(20);

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

        await loop.TurnAsync().WaitAsync(T);                                         // the chair's, refused by the ledger
        Assert.Empty(host.Conv(Ops).Sent);

        host.Refusals[Ops] = new TurnRefusal(RefusalKind.HostOrLaunch, "stepped over for this test");
        await loop.TurnAsync().WaitAsync(T);                                         // the Research Director's
        var research = Assert.Single(host.Conv(Research).Sent);
        Assert.DoesNotContain("buy nothing today", research);

        host.Refusals.Remove(Ops);
        await loop.TurnAsync().WaitAsync(T);                                         // the chair's again
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

        await loop.TurnAsync().WaitAsync(T);
        Assert.Empty(host.Conv(Research).Sent);

        chair.Hold.SetResult();
        await first.WaitAsync(T);
        await loop.TurnAsync().WaitAsync(T);
        Assert.Single(host.Conv(Research).Sent);

        // ---- and the dispatcher, offered three lanes, starts ONE ---------------------------------------------------
        Due(events, Ops, TimeSpan.FromMinutes(1));
        Due(events, Research, TimeSpan.FromMinutes(1));
        chair.Rearm();
        chair.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tick = await loop.TickAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await chair.Inside.Task.WaitAsync(TimeSpan.FromSeconds(20));
        _ = Assert.Single(tick.Lanes);
        Assert.Empty((await loop.TickAsync().WaitAsync(T)).Lanes);
        Assert.Single(host.Conv(Research).Sent);
        chair.Hold.SetResult();
        await Task.WhenAll(tick.Lanes).WaitAsync(T);
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
        await loop.TurnAsync().WaitAsync(T);
        Assert.Empty(host.Conv(Research).Sent);
        chair.Hold.SetResult();
        await first.WaitAsync(T);

        // ---- the hold: the chair's runtime is not known, so its pool is every pool -----------------------------------
        host.Runtimes[Ops] = null;
        Due(events, Ops, TimeSpan.FromMinutes(1));
        chair.Limit = new VendorLimit("You've hit your usage limit.", DateTimeOffset.UtcNow.AddHours(1));
        chair.Hold = null;
        host.Refusals[Research] = new TurnRefusal(RefusalKind.HostOrLaunch, "stepped over while the chair turns");
        await loop.TurnAsync().WaitAsync(T);
        Assert.Equal(2, chair.Sent.Count);
        host.Refusals.Remove(Research);

        await loop.TurnAsync().WaitAsync(T);
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

    /// <summary>
    /// THE APP'S LANE PROFILE IS READ FROM THE SEAT'S ASSURANCE (<c>U-thinking-seats-a</c>, the rulings Q2 and Q5): only a cell is a
    /// confined lane; a broker-only, harness, puppet, CLI or unknown seat is unconfined; and even a cell counts for the pass.
    /// </summary>
    [Fact]
    public void The_apps_lane_is_confined_only_on_a_cell_and_every_lane_counts_for_the_pass()
    {
        foreach (var seat in new[] { SeatClasses.Cli, SeatClasses.Broker, SeatClasses.Harness, SeatClasses.Puppet, "unknown", null })
            Assert.Equal(LaneProfile.Unconfined, AppHost.LaneOfSeat(seat));
        var cell = AppHost.LaneOfSeat(SeatClasses.Cell);
        Assert.True(cell.IsConfined);
        Assert.True(cell.Counted);
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

    // ---- item 5: dispatch up to N ---------------------------------------------------------------------------------

    const string Third = "pos-third";

    /// <summary>A host with the legacy pair and a third position, every one of them on this host's confined profile.</summary>
    LaneHost Confined(MissionEventStore events, LaneSettings lanes)
    {
        var host = new LaneHost { Events = events, Lanes = lanes };
        host.Extra.Add(new ChartPosition(Third, OrgPositionKind.Member, 2, OrgLevel.Executor));
        foreach (var role in new[] { Ops, Research, Third }) host.Profiles[role] = Cell;
        return host;
    }

    static TaskCompletionSource Held(Conversation c) =>
        (c.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>
    /// (c) RED at base (one loop task, every turn inline): TWO POSITIONS TURN AT ONCE ON TWO CONFINED LANES. One tick starts
    /// both, each in its own lane, both inside their runs at the same moment, and the card names both.
    /// </summary>
    [Fact]
    public async Task Two_positions_turn_at_once_on_two_confined_lanes()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = Confined(events, new LaneSettings(2, 2, 2));
        var release = new[] { Held(host.Conv(Ops)), Held(host.Conv(Research)) };
        var loop = new MissionLoop(host, new MissionOptions());

        var tick = await loop.TickAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Task.WhenAll(host.Conv(Ops).Inside.Task, host.Conv(Research).Inside.Task).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, tick.Lanes.Count);
        Assert.Equal([Ops, Research], loop.Status.Roles);
        foreach (var r in release) r.SetResult();
        await Task.WhenAll(tick.Lanes).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(host.Conv(Ops).Sent);
        Assert.Single(host.Conv(Research).Sent);
    }

    /// <summary>
    /// (c2) GUARD: ONE LANE BY DEFAULT — A SECOND DUE POSITION WAITS. The default policy, even with both seats confined: one tick
    /// starts the chair; a second tick while it turns starts nothing; once it has ended the Research Director turns.
    /// </summary>
    [Fact]
    public async Task One_lane_by_default_a_second_due_position_waits()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = Confined(events, LaneSettings.Default);
        var chair = Held(host.Conv(Ops));
        var loop = new MissionLoop(host, new MissionOptions());

        var first = await loop.TickAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await host.Conv(Ops).Inside.Task.WaitAsync(TimeSpan.FromSeconds(20));
        _ = Assert.Single(first.Lanes);
        Assert.Empty((await loop.TickAsync().WaitAsync(T)).Lanes);
        Assert.Empty(host.Conv(Research).Sent);

        chair.SetResult();
        await Task.WhenAll(first.Lanes).WaitAsync(T);
        var second = await loop.TickAsync().WaitAsync(T);
        await Task.WhenAll(second.Lanes).WaitAsync(T);
        Assert.Single(host.Conv(Research).Sent);
    }

    /// <summary>
    /// (c4) GUARD: ONE POSITION NEVER HOLDS TWO LEASES. The chair turns in one lane of three; a new wake for it arrives while it
    /// turns; neither another tick nor a turn asked of the loop directly starts it a second time.
    /// </summary>
    [Fact]
    public async Task One_position_never_holds_two_leases()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        var host = Confined(events, new LaneSettings(3, 3, 3));
        var chair = Held(host.Conv(Ops));
        var loop = new MissionLoop(host, new MissionOptions());

        var tick = await loop.TickAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await host.Conv(Ops).Inside.Task.WaitAsync(TimeSpan.FromSeconds(20));
        events.Raise("owner:1", MissionEventKind.Owner, DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Empty((await loop.TickAsync().WaitAsync(T)).Lanes);
        await loop.TurnAsync().WaitAsync(T);
        Assert.Single(host.Conv(Ops).Sent);

        chair.SetResult();
        await Task.WhenAll(tick.Lanes).WaitAsync(T);
    }

    /// <summary>
    /// (d) RED at base (the sweeps at the top of every turn): THE SWEEPS RUN ONCE PER TICK WITH TWO LANES IN FLIGHT. One tick
    /// starts two lanes; the paper allocation and the paper deployments — beside the boundary deadlines, in the one sweep that
    /// holds all three — have run once, and still once after both lanes end.
    /// </summary>
    [Fact]
    public async Task Sweeps_run_once_per_tick_with_two_lanes_in_flight()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = Confined(events, new LaneSettings(2, 2, 2));
        var release = new[] { Held(host.Conv(Ops)), Held(host.Conv(Research)) };
        var loop = new MissionLoop(host, new MissionOptions());

        var tick = await loop.TickAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Task.WhenAll(host.Conv(Ops).Inside.Task, host.Conv(Research).Inside.Task).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal((1, 1), (host.Allocations, host.Deployments));

        foreach (var r in release) r.SetResult();
        await Task.WhenAll(tick.Lanes).WaitAsync(T);
        Assert.Equal(2, tick.Lanes.Count);
        Assert.Equal((1, 1), (host.Allocations, host.Deployments));
    }

    /// <summary>
    /// (e2) GUARD on <c>U-grant-tree</c>: AN ORDER-CAPABLE OPERATIONS TURN RUNS ALONE AND THE OTHERS KEEP THEIR WAKES. The chair's
    /// turn holds order permission (nothing else is live when it is issued); while it runs, a tick offered three lanes starts
    /// no other lane, and the Research Director's and the third position's wakes stay due and unconsumed. Once it ends, both turn.
    /// </summary>
    [Fact]
    public async Task An_order_capable_operations_turn_runs_alone_and_the_others_keep_their_wakes()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        var host = Confined(events, new LaneSettings(3, 3, 3));
        host.Grants = new TradeAgent.Security.AgentGrants();
        var chair = Held(host.Conv(Ops));
        var loop = new MissionLoop(host, new MissionOptions());

        var first = await loop.TickAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await host.Conv(Ops).Inside.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(host.Conv(Ops).Withheld);

        Due(events, Research, TimeSpan.FromMinutes(1));
        events.Raise(MissionEventIds.ForRole("look:third", Third), MissionEventKind.Review, DateTimeOffset.UtcNow.AddMinutes(-1),
            role: Third);
        var during = await loop.TickAsync().WaitAsync(T);
        Assert.Empty(during.Lanes);
        Assert.Empty(host.Conv(Research).Sent);
        Assert.Empty(host.Conv(Third).Sent);
        Assert.Contains(events.Due(DateTimeOffset.UtcNow), e => e.Role == Research);
        Assert.Contains(events.Due(DateTimeOffset.UtcNow), e => e.Role == Third);

        chair.SetResult();
        await Task.WhenAll(first.Lanes).WaitAsync(T);
        var after = await loop.TickAsync().WaitAsync(T);
        await Task.WhenAll(after.Lanes).WaitAsync(T);
        Assert.Single(host.Conv(Research).Sent);
        Assert.Single(host.Conv(Third).Sent);
    }

    /// <summary>
    /// (h) GUARD: NO DUE WAKE MEANS NO PAID TURN, WITH THREE LANES AND ACROSS A RESTART. Three positions with one wake each turn
    /// once; the next tick starts nothing; a new loop over the same queue starts nothing either.
    /// </summary>
    [Fact]
    public async Task No_due_wake_means_no_paid_turn_with_three_lanes()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));
        events.Raise(MissionEventIds.ForRole("look:third", Third), MissionEventKind.Review, DateTimeOffset.UtcNow.AddMinutes(-1),
            role: Third);
        var host = Confined(events, new LaneSettings(3, 3, 3));
        var loop = new MissionLoop(host, new MissionOptions());

        var tick = await loop.TickAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Task.WhenAll(tick.Lanes).WaitAsync(T);
        Assert.Equal(3, tick.Lanes.Count);
        Assert.Empty((await loop.TickAsync().WaitAsync(T)).Lanes);

        var restarted = new MissionLoop(host, new MissionOptions());
        Assert.Empty((await restarted.TickAsync().WaitAsync(T)).Lanes);
        foreach (var role in new[] { Ops, Research, Third }) Assert.Single(host.Conv(role).Sent);
    }

    // ---- item 6: the drain, and outputs sealed first --------------------------------------------------------------

    static readonly LaneProfile Sealed = new(LaneAssurance.Confined, "test-cell", InboxUnwritable: true);

    /// <summary>
    /// (f) RED at base (busy lanes refuse every pass, launches continue): AN OWED PASS DRAINS THE LANES AND RUNS BEFORE THE NEXT
    /// LAUNCH. Two counted lanes turn; the inbox changes and a third position falls due; the tick starts nothing new; once both
    /// lanes end the pass runs, and only then does the third position launch.
    /// </summary>
    [Fact]
    public async Task An_owed_pass_drains_the_lanes_and_runs_before_the_next_launch()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = Confined(events, new LaneSettings(3, 3, 3));
        var release = new[] { Held(host.Conv(Ops)), Held(host.Conv(Research)) };
        var loop = new MissionLoop(host, new MissionOptions());

        var first = await loop.TickAsync().WaitAsync(T);
        await Task.WhenAll(host.Conv(Ops).Inside.Task, host.Conv(Research).Inside.Task).WaitAsync(T);

        host.InboxChanged = true;
        events.Raise(MissionEventIds.ForRole("look:third", Third), MissionEventKind.Review, DateTimeOffset.UtcNow.AddMinutes(-1),
            role: Third);
        var owed = await loop.TickAsync().WaitAsync(T);
        Assert.Empty(owed.Lanes);
        Assert.Empty(host.Conv(Third).Sent);
        Assert.DoesNotContain("scan", host.Log);

        foreach (var r in release) r.SetResult();
        await Task.WhenAll(first.Lanes).WaitAsync(T);
        var after = await loop.TickAsync().WaitAsync(T);
        await Task.WhenAll(after.Lanes).WaitAsync(T);

        Assert.Single(host.Conv(Third).Sent);
        var log = host.Log.ToList();
        Assert.True(log.IndexOf("scan") >= 0 && log.IndexOf("scan") < log.IndexOf($"send:{Third}"),
            $"the third position launched before the owed pass ran: {string.Join(", ", log)}");
    }

    /// <summary>
    /// (f2) GUARD (today's <c>No_scan_runs_while_a_role_is_launching</c>, with two lanes): NO PASS WHILE AN UNCONFINED LANE TURNS
    /// — nor while a confined lane that can write the inbox does. The app's pass is refused while both turn and while the
    /// unconfined one turns alone, and runs once both have ended.
    /// </summary>
    [Fact]
    public async Task No_pass_while_an_unconfined_lane_turns()
    {
        var events = new MissionEventStore(_db);
        Due(events, Ops, TimeSpan.FromMinutes(3));
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = Confined(events, new LaneSettings(2, 2, 2));
        host.Profiles[Ops] = LaneProfile.Unconfined;
        var chair = Held(host.Conv(Ops));
        var director = Held(host.Conv(Research));
        var loop = new MissionLoop(host, new MissionOptions());

        var tick = await loop.TickAsync().WaitAsync(T);
        await Task.WhenAll(host.Conv(Ops).Inside.Task, host.Conv(Research).Inside.Task).WaitAsync(T);
        Assert.Null(loop.TryPass(_ => "ran"));
        Assert.Null(loop.TryPass(() => "ran"));

        // THE CONFINED, COUNTED LANE ENDS FIRST: the unconfined one alone still holds every pass.
        director.SetResult();
        await tick.Lanes[1].WaitAsync(T);
        Assert.Null(loop.TryPass(_ => "ran"));

        chair.SetResult();
        await Task.WhenAll(tick.Lanes).WaitAsync(T);
        Assert.Equal("ran", loop.TryPass(_ => "ran"));
    }

    /// <summary>
    /// (f3) RED at base (any turning role blocks every pass): A CONFINED LANE THAT CANNOT WRITE THE INBOX HOLDS NO PASS, AND ITS
    /// HOME WAITS FOR ITS SEAL. The Research Director turns in a cell whose profile proves the inbox unwritable: the app's pass
    /// runs beside it, records the owner's file, and does not read the director's home — nor marks what it holds missing; once
    /// the lane has committed, the next pass records the director's file. The old pass, which cannot be told what to skip, is
    /// refused beside it.
    /// </summary>
    [Fact]
    public async Task A_confined_lane_that_cannot_write_the_inbox_holds_no_pass_and_its_home_waits_for_its_seal()
    {
        var root = Path.Combine(TestEnv.Home, $"lanes-ws-{Guid.NewGuid():n}");
        var home = Path.Combine(root, CouncilRoles.HomeDir(Research), CouncilRoles.OutDir);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        File.WriteAllText(Path.Combine(home, "report.md"), "half written");
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "brief.md"), "the owner's");
        var scanner = new MaterialScanner(_db, root, noAgentSince: _ => true);

        var events = new MissionEventStore(_db);
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = Confined(events, new LaneSettings(2, 2, 2));
        host.Profiles[Research] = Sealed;
        var director = Held(host.Conv(Research));
        var loop = new MissionLoop(host, new MissionOptions());

        var tick = await loop.TickAsync().WaitAsync(T);
        await host.Conv(Research).Inside.Task.WaitAsync(T);

        IReadOnlySet<string>? skipped = null;
        var beside = loop.TryPass(unsealed => scanner.Scan(default, skipped = unsealed));
        Assert.NotNull(beside);
        Assert.Equal([Research], skipped!.ToArray());
        var rows = new MaterialStore(_db).Present().Select(m => m.RelPath).ToList();
        Assert.Contains($"{MaterialScanner.InboxDir}/brief.md", rows);
        Assert.DoesNotContain(rows, p => p.EndsWith("report.md", StringComparison.Ordinal));
        Assert.Null(loop.TryPass(() => "ran"));

        director.SetResult();
        await Task.WhenAll(tick.Lanes).WaitAsync(T);
        Assert.NotNull(loop.TryPass(unsealed => scanner.Scan(default, unsealed)));
        Assert.Contains(new MaterialStore(_db).Present(), m => m.RelPath.EndsWith("report.md", StringComparison.Ordinal));
    }

    // ---- review of the first pass ---------------------------------------------------------------------------------

    /// <summary>
    /// (review 1) RED before the fix: A TICK WHILE A POSITION TURNS RAISES IT NO LOOK. The chair's look, raised half an hour ahead,
    /// is consumed by its turn; a tick while that turn runs used to raise its next look at the base interval — and its turn's
    /// end then kept that row, so the quiet-review backoff (twice the last interval) never applied. Its next look is the backed-
    /// off one, as a turn run alone gets.
    /// </summary>
    [Fact]
    public async Task A_tick_while_a_position_turns_raises_it_no_look_of_its_own()
    {
        var clock = DateTimeOffset.UtcNow;
        var events = new MissionEventStore(_db);
        var host = Confined(events, new LaneSettings(2, 2, 2));
        var options = new MissionOptions();
        var loop = new MissionLoop(host, options, now: () => clock);

        Assert.Empty((await loop.TickAsync().WaitAsync(T)).Lanes);           // the first sweep raises each look, 30 min out
        clock += options.ReviewEvery + TimeSpan.FromMinutes(1);
        var chair = Held(host.Conv(Ops));
        var tick = await loop.TickAsync().WaitAsync(T);
        await host.Conv(Ops).Inside.Task.WaitAsync(T);

        await loop.TickAsync().WaitAsync(T);                                  // a tick while the chair turns
        chair.SetResult();
        await Task.WhenAll(tick.Lanes).WaitAsync(T);

        var look = Assert.Single(events.OfKind(MissionEventKind.Review),
            e => e.Role == Ops && e.ConsumedBy is null && e.Disposition is null);
        Assert.Equal(clock + options.ReviewEvery + options.ReviewEvery, look.DueAt);
    }

    /// <summary>
    /// (review 2) RED before the fix: A PASS BESIDE AN UNSEALED LANE HASHES NOTHING IN ITS HOME. A file of the director's home was
    /// recorded, unhashed, before its lane started; the pass beside the lane leaves its hash unread; the pass after the seal reads it.
    /// </summary>
    [Fact]
    public async Task A_pass_beside_an_unsealed_lane_hashes_nothing_in_its_home()
    {
        var root = Path.Combine(TestEnv.Home, $"lanes-ws-{Guid.NewGuid():n}");
        var home = Path.Combine(root, CouncilRoles.HomeDir(Research), CouncilRoles.OutDir);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "report.md"), "first draft");
        new MaterialScanner(_db, root, noAgentSince: _ => true) { HashesPerPass = 0 }.Scan();
        Material Report() => Assert.Single(new MaterialStore(_db).Present(), m => m.RelPath.EndsWith("report.md", StringComparison.Ordinal));
        Assert.Null(Report().Sha256);

        var events = new MissionEventStore(_db);
        Due(events, Research, TimeSpan.FromMinutes(2));
        var host = Confined(events, new LaneSettings(2, 2, 2));
        host.Profiles[Research] = Sealed;
        var director = Held(host.Conv(Research));
        var loop = new MissionLoop(host, new MissionOptions());
        var tick = await loop.TickAsync().WaitAsync(T);
        await host.Conv(Research).Inside.Task.WaitAsync(T);

        var scanner = new MaterialScanner(_db, root, noAgentSince: _ => true);
        Assert.NotNull(loop.TryPass(unsealed => scanner.Scan(default, unsealed)));
        Assert.Null(Report().Sha256);

        director.SetResult();
        await Task.WhenAll(tick.Lanes).WaitAsync(T);
        Assert.NotNull(loop.TryPass(unsealed => scanner.Scan(default, unsealed)));
        Assert.NotNull(Report().Sha256);
    }

    /// <summary>
    /// (review 4) RED before the fix: A CONFIRMATION COMPLETES ONLY THE FIGURES IT NAMED. A raise to two lanes is armed, the box is
    /// changed to eight, and the armed press saves nothing — it asks again, naming eight.
    /// </summary>
    [Fact]
    public void Editing_a_lane_box_disarms_a_raise_armed_for_another_figure()
    {
        var saved = new List<int>();
        var lanes = Ui.NumberField(1m, 1m);
        var perPool = Ui.NumberField(1m, 1m);
        var perCell = Ui.NumberField(1m, 1m);
        (int, int, int) Pending() => ((int)(lanes.Value ?? 1), (int)(perPool.Value ?? 1), (int)(perCell.Value ?? 1));
        var b = SafetyPage.BuildSaveLanes([lanes, perPool, perCell], () => (1, 1, 1), Pending, () => saved.Add(Pending().Item1));

        lanes.Value = 2m;
        Press(b);
        Assert.Equal(Labels.RaiseLanesArmed(2, 1, 1), b.Content);

        lanes.Value = 8m;
        Press(b);
        Assert.Empty(saved);
        Assert.Equal(Labels.RaiseLanesArmed(8, 1, 1), b.Content);
        Press(b);
        Assert.Equal([8], saved);
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

        /// <summary>Positions after the legacy pair, each active and refused by nobody.</summary>
        public List<ChartPosition> Extra { get; } = [];
        public IReadOnlyList<ChartPosition> Positions() => [.. ChartPosition.LegacyPair, .. Extra];

        /// <summary>Set: every conversation claims its launch's grant in this register (<c>U-grant-tree</c>).</summary>
        public TradeAgent.Security.AgentGrants? Grants { get; set; }

        public int Allocations;
        public int Deployments;
        public void AllocatePaperDue(DateTimeOffset now) => Interlocked.Increment(ref Allocations);
        public void StartPaperDeploymentsDue(DateTimeOffset now) => Interlocked.Increment(ref Deployments);
        public string? RuntimeFor(string role) => Runtimes.GetValueOrDefault(role);

        /// <summary>The admission for (role, wakes), or null to admit it.</summary>
        public Func<string, IReadOnlyList<string>, AiAdmission?>? Admit { get; set; }

        public IAgentConversation? Conversation => Conv(Ops);
        public IAgentConversation? ConversationFor(string role) => Conv(role);
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, $"lanes-home-{Guid.NewGuid():n}");
        /// <summary>Set: the inbox holds something no pass has recorded, until the next pass runs.</summary>
        public bool InboxChanged { get; set; }
        public bool InboxChangedSinceLastPass => InboxChanged;

        /// <summary>What happened, in order: "scan" for each pass, "send:&lt;role&gt;" for each launch.</summary>
        public List<string> Log { get; } = [];
        public void Note(string what) { lock (Log) Log.Add(what); }

        /// <summary>The positions each loop pass was told were running unsealed.</summary>
        public List<string[]> ScannedBeside { get; } = [];

        public Task ScanAsync(CancellationToken ct) => ScanAsync(new HashSet<string>(), ct);

        public Task<bool> ScanAsync(IReadOnlySet<string> unsealed, CancellationToken ct)
        {
            Note("scan");
            lock (ScannedBeside) ScannedBeside.Add([.. unsealed.Order(StringComparer.Ordinal)]);
            InboxChanged = false;
            return Task.FromResult(true);
        }

        public Conversation Conv(string role)
        {
            lock (_conversations)
            {
                if (!_conversations.TryGetValue(role, out var c)) _conversations[role] = c = new Conversation(role, () => Grants) { OnSend = Note };
                return c;
            }
        }

        public TurnRefusal? ActivationRefusal(string role) =>
            Refusals.TryGetValue(role, out var refused) ? refused
            : CouncilRoles.IsKnown(role) || Extra.Any(p => p.Id == role) ? null
            : new TurnRefusal(RefusalKind.Position, "not on this host's chart");

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role, string? attemptId)
        {
            // ADMITTED WITH NO LEDGER: the loop consumes the wakes itself, as it does for a host built without one.
            return Admit?.Invoke(role, wakes) ?? AiAdmission.NoLedger;
        }
    }

    /// <summary>
    /// One position's conversation: records what it was sent and ends every turn at once — or when the test releases it. With a
    /// grant register it is also the launch gate the app's sessions are (<c>U-grant-tree</c>): it looks with
    /// <see cref="TradeAgent.Security.AgentGrants.HeldBy"/>, claims its grant before admission and ends it with its turn.
    /// </summary>
    internal sealed class Conversation(string role, Func<TradeAgent.Security.AgentGrants?> grants) : IAgentConversation, ILaunchGate
    {
        TradeAgent.Security.AgentGrant? _claim;

        /// <summary>Why the last claimed launch holds no order permission, or null when it held what its role has.</summary>
        public string? Withheld { get; private set; }

        public LaunchBar? Barred() =>
            grants()?.HeldBy(role) is { } held ? new LaunchBar(held, Lasting: false) : null;

        public LaunchBar? Claim(out string? ordersWithheld)
        {
            ordersWithheld = null;
            if (grants() is not { } register) return null;
            try { _claim = register.Issue(role, ""); }
            catch (TradeAgentException ex) when (ex.Code == ErrorCode.LAUNCH_HELD) { return new LaunchBar(ex.Message, Lasting: false); }
            ordersWithheld = Withheld = _claim.OrdersWithheld;
            return null;
        }

        public void Unclaim()
        {
            if (Interlocked.Exchange(ref _claim, null) is { } claim) grants()?.Revoke(claim.Token);
        }

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

        public Action<string>? OnSend { get; init; }

        public async Task SendMissionAsync(string message, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add(message);
            OnSend?.Invoke($"send:{role}");
            Inside.TrySetResult();
            var grant = Interlocked.Exchange(ref _claim, null);
            try { if (Hold is { } hold) await hold.Task.WaitAsync(ct); }
            finally { grant?.Dispose(); }
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
