using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// AN AI THAT WORKS NON-STOP ON SOMEBODY'S ACCOUNT WITH NO CEILING IS A BILL WITH NO CEILING.
///
/// The loop's whole purpose is not to stop, so the one thing allowed to stop it is the owner's own
/// number — and only when TradeAgent can actually say what has been spent. Three properties, and
/// they are separable:
///
/// <list type="number">
/// <item>Reaching the cap takes no more turns, until local midnight.</item>
/// <item>Reaching it is <c>&gt;=</c>, not <c>&gt;</c>: the owner's figure is a ceiling to stop at,
/// and a cap of zero is therefore reached by a day that has spent nothing.</item>
/// <item>An UNPRICED day does not stop, because stopping there would be stopping on a number
/// nobody measured. That is why the card has to say the limit is holding nothing back.</item>
/// </list>
/// </summary>
public class MissionCapTests
{
    /// <summary>A conversation that records what it was asked and never starts a process.</summary>
    sealed class Recording : IAgentConversation
    {
        readonly List<ChatTurn> _history = [];
        public List<string> Sent { get; } = [];

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => _history.ToArray();

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
            StateChanged?.Invoke();
            Delta?.Invoke("");
            var turn = new ChatTurn(ChatRole.Ai, "Did some work.", DateTimeOffset.UtcNow);
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
            TurnEnded?.Invoke(new AgentTurnEnded(0, TimeSpan.FromSeconds(1), "", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> TakeTyped() => [];
        public void Queue(string message) { }
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    sealed class Host(AiSpendToday spend) : IMissionHost
    {
        public Recording Conv { get; } = new();
        public List<AiSpendToday> Told { get; } = [];

        public IAgentConversation? Conversation => Conv;
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, "cap-agent-home");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public AiSpendToday Spend { get; set; } = spend;
        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });

        void IMissionHost.SpendCapReached(AiSpendToday spend) => Told.Add(spend);
    }

    static DateTimeOffset Midnight()
    {
        var tomorrow = DateTimeOffset.Now.LocalDateTime.Date.AddDays(1);
        return new DateTimeOffset(tomorrow, TimeZoneInfo.Local.GetUtcOffset(tomorrow));
    }

    static AiSpendToday Spent(decimal spent, decimal cap, int unpriced = 0) => new()
    {
        Metered = true, Spent = spent, Cap = cap, Currency = "USD",
        Turns = 3, UnpricedTurns = unpriced, ResumesAt = Midnight()
    };

    /// <summary>
    /// THE FINDING THIS ITEM EXISTS FOR. With the day's spending at the owner's ceiling, the loop
    /// went on composing turns and running the CLI — an account being charged past a number its
    /// owner had written down.
    /// </summary>
    [Fact]
    public async Task A_day_that_has_reached_the_cap_takes_no_further_turns()
    {
        var host = new Host(Spent(5m, 5m));
        var loop = new MissionLoop(host);

        var wait = await loop.TurnAsync();

        Assert.Empty(host.Conv.Sent);
        Assert.Equal(0, loop.Status.Turns);
        // Until local midnight, when today's totals stop being today's.
        Assert.True(wait > TimeSpan.Zero);
        Assert.Equal(Midnight(), DateTimeOffset.Now + wait, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Under the cap, nothing changes: the loop is not in the business of stopping.
    /// </summary>
    [Fact]
    public async Task A_day_under_the_cap_takes_its_turn_as_usual()
    {
        var host = new Host(Spent(4.99m, 5m));
        var loop = new MissionLoop(host);

        await loop.TurnAsync();

        Assert.Single(host.Conv.Sent);
        Assert.Equal(1, loop.Status.Turns);
        Assert.Empty(host.Told);
    }

    /// <summary>
    /// REACHING IT IS ENOUGH. The owner's number is a ceiling, not a threshold to be crossed, so the
    /// comparison is <c>&gt;=</c> — and that is what makes a cap of zero mean "no spending at all"
    /// rather than "unlimited", which is what an unreadable settings row falls back to.
    /// </summary>
    [Fact]
    public async Task A_cap_of_zero_stops_a_day_that_has_spent_nothing()
    {
        var host = new Host(Spent(0m, 0m));
        var loop = new MissionLoop(host);

        await loop.TurnAsync();

        Assert.Empty(host.Conv.Sent);
    }

    /// <summary>
    /// STOPPING ON AN UNMEASURED NUMBER IS NOT AN OPTION. A day whose turns could not be priced has
    /// a spend of zero because nothing was priced, not because nothing was spent, so the cap does not
    /// bind — and the card says the limit is not holding anything back rather than leaving the owner
    /// to work that out from an AI that never stops.
    /// </summary>
    [Fact]
    public async Task An_unpriced_day_is_not_stopped_by_a_cap_it_cannot_be_measured_against()
    {
        var host = new Host(Spent(0m, 5m, unpriced: 3));
        var loop = new MissionLoop(host);

        await loop.TurnAsync();

        Assert.Single(host.Conv.Sent);
    }

    /// <summary>
    /// A host with no meter behind it keeps turning. The permissive default is deliberate: the
    /// restrictive reading belongs where the number is measured, and a loop that stopped because
    /// nobody was counting would stop for ever.
    /// </summary>
    [Fact]
    public async Task A_loop_with_no_meter_behind_it_keeps_turning()
    {
        var host = new Host(AiSpendToday.NotMetered);
        var loop = new MissionLoop(host);

        await loop.TurnAsync();

        Assert.Single(host.Conv.Sent);
    }

    /// <summary>
    /// The owner is told ONCE. A line every five seconds for the rest of the day would bury the
    /// activity log under the one event it is meant to make findable.
    /// </summary>
    [Fact]
    public async Task The_owner_is_told_once_when_the_cap_is_first_reached()
    {
        var host = new Host(Spent(6m, 5m));
        var loop = new MissionLoop(host);

        await loop.TurnAsync();
        await loop.TurnAsync();
        await loop.TurnAsync();

        Assert.Single(host.Told);
        Assert.Equal(6m, host.Told[0].Spent);
        Assert.Equal(5m, host.Told[0].Cap);

        // A raised cap lets the work resume, and the next time it is reached is said again.
        host.Spend = Spent(6m, 20m);
        await loop.TurnAsync();
        Assert.Single(host.Conv.Sent);

        host.Spend = Spent(21m, 20m);
        await loop.TurnAsync();
        Assert.Equal(2, host.Told.Count);
    }
}
