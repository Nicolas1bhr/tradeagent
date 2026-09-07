using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CEILING IS APPLIED BEFORE A TURN RUNS, NOT AFTER IT.
///
/// <c>MissionCapTests</c> asserts what happens once the day's completed spending has reached the
/// owner's number. This file asserts the half that made 5.07 USD possible against a 5 USD ceiling on
/// 2026-09-07: the turn that carries the total past the number is always admitted by a comparison
/// made after the fact, because at the moment of the comparison the total does not include it yet.
///
/// The rule is <c>spent + reserved + this turn ≤ cap</c>, and the arithmetic below is real rather
/// than mocked — a real meter, over a real database, with the reservation priced by the real
/// catalogue at the owner's own rate, driving a real <see cref="MissionLoop"/> over the same
/// recording conversation <c>MissionCapTests</c> uses.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class TurnAllowanceTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"allowance-{Guid.NewGuid():n}.jsonl");

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        _db.Dispose();
    }

    /// <summary>
    /// The owner's rate, chosen so both numbers below are exact: 4.00 per million in and 20.00 out.
    /// Those happen to be <c>gpt-5.6-sol</c>'s shipped list price, which is the model this build
    /// asks codex for — the point is that the arithmetic has one right answer, not that a rate is
    /// being asserted about OpenAI.
    /// </summary>
    static readonly OwnerPrice Rate = new(4m, 20m);

    /// <summary>1,200,000 input at 4.00 and 20,000 output at 20.00, per million: 4.80 + 0.40.</summary>
    const decimal Reservation = 5.20m;

    /// <summary>300,000 input at 4.00 per million, and no output. Exactly 1.20 a turn.</summary>
    static AgentTurnEnded PricedAt120(DateTimeOffset at) =>
        new(0, TimeSpan.FromSeconds(5), "", at) { Usage = new TurnUsage(300_000, 0, 0, 0, 0, null) };

    TurnMeter Meter(Func<DateTimeOffset> now) =>
        new(_db, () => 5m, runtimeId: () => "codex", now: now, recordPath: _records,
            owner: () => Rate, model: () => "gpt-5.6-sol");

    /// <summary>A conversation and a host that record what the loop asked of them, and nothing else.</summary>
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

    sealed class Host(TurnMeter meter) : IMissionHost
    {
        public Recording Conv { get; } = new();
        public List<string> Opened { get; } = [];

        public IAgentConversation? Conversation => Conv;
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, "allowance-agent-home");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;
        public AiSpendToday Spend => meter.Today;

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER", Spend = meter.Today });

        public string? BeginTurn(string prompt, IReadOnlyList<string> wakes)
        {
            Opened.Add(prompt);
            return meter.Begin(prompt, wakes);
        }

        /// <summary>Meters this conversation the way the composition root does, so a turn that runs closes its row.</summary>
        public Host Metered() { meter.Attach(Conv); return this; }
    }

    /// <summary>
    /// THE GUARD. Three turns at 1.20 leave 3.60 of a 5.00 ceiling spent — under it by every reading
    /// — and the fourth turn is refused, because running it would commit 5.20 more. Under the old
    /// comparison the fourth ran, the day ended over the number, and nothing had been wrong at any
    /// single moment.
    /// </summary>
    [Fact]
    public async Task The_turn_that_would_pass_the_ceiling_is_refused_before_it_runs()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(() => now);

        for (var i = 0; i < 3; i++) meter.Record(PricedAt120(now));
        Assert.Equal(3.60m, meter.Today.Spent);
        Assert.False(meter.Today.CapReached);
        Assert.Equal(Reservation, meter.Today.NextTurnReservation);

        var host = new Host(meter);
        var loop = new MissionLoop(host);
        var wait = await loop.TurnAsync();

        Assert.Empty(host.Conv.Sent);
        Assert.Empty(host.Opened);
        Assert.Equal(3, meter.Today.Turns);
        // Until local midnight, when today's totals stop being today's.
        Assert.True(wait > TimeSpan.Zero);
    }

    /// <summary>
    /// And a day with room takes its turn, opens its record and commits its cost — so the refusal
    /// above is the ceiling working rather than the loop having stopped.
    /// </summary>
    [Fact]
    public async Task A_day_with_room_for_the_reservation_takes_its_turn_and_commits_it()
    {
        var now = DateTimeOffset.Now;
        var meter = new TurnMeter(_db, () => 50m, runtimeId: () => "codex", now: () => now,
            recordPath: _records, owner: () => Rate, model: () => "gpt-5.6-sol");

        for (var i = 0; i < 3; i++) meter.Record(PricedAt120(now));

        var host = new Host(meter).Metered();
        await new MissionLoop(host).TurnAsync();

        Assert.Single(host.Conv.Sent);
        Assert.Single(host.Opened);
        Assert.Equal(4, meter.Today.Turns);
    }

    /// <summary>
    /// The unresolved half of the sum. A turn that was launched and has not reported back is money
    /// asked for, so it counts against the ceiling exactly as spent money does — otherwise a killed
    /// turn would free its own allowance the moment it stopped being measurable.
    /// </summary>
    [Fact]
    public void A_committed_turn_that_has_not_reported_back_still_counts_against_the_ceiling()
    {
        var now = DateTimeOffset.Now;
        // A ceiling with room for two reservations, so the refusal below is the FIRST one being
        // counted rather than the ceiling having been too small all along.
        var meter = new TurnMeter(_db, () => 6m, runtimeId: () => "codex", now: () => now,
            recordPath: _records, owner: () => Rate, model: () => "gpt-5.6-sol");

        Assert.True(meter.Today.AdmitsAnotherTurn);
        meter.Begin("## Situation");

        var spend = meter.Today;
        Assert.Equal(0m, spend.Spent);
        Assert.Equal(Reservation, spend.Reserved);
        Assert.False(spend.CapReached);
        Assert.False(spend.AdmitsAnotherTurn);
    }

    /// <summary>
    /// THE READING MIDNIGHT DOES NOT REPAIR, and the one the shipped defaults produce: 5.00 a day
    /// against a 5.20 worst case on gpt-5.6-sol. No turn is ever admitted, so the app says which two
    /// numbers are in conflict and what to do about them, rather than sitting at "waiting until
    /// 00:00" for ever.
    /// </summary>
    [Fact]
    public void A_ceiling_smaller_than_one_turns_worst_case_says_so_rather_than_waiting_for_midnight()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(() => now);

        var spend = meter.Today;
        Assert.Equal(Reservation, spend.NextTurnReservation);
        Assert.True(spend.NextTurnReservation > spend.Cap);
        Assert.True(spend.CapCannotFundATurn);
        Assert.False(spend.AdmitsAnotherTurn);
        Assert.False(spend.CapReached);

        var card = DashboardPage.MissionCost(spend);
        Assert.Contains("more than the whole limit", card);
        Assert.Contains("Raise the limit", card);
        Assert.DoesNotContain("starts again at", card);

        var told = MissionSituation.SpendLine(spend)!;
        Assert.Contains("more than that whole limit", told);
        Assert.Contains("Midnight will not change it", told);
    }

    /// <summary>
    /// What the AI is told, in the same arithmetic the loop refuses on. An agent that believes it is
    /// in the middle of its last turn finishes what it is doing; one that knows the NEXT turn is
    /// refused writes down where it got to, which is the whole of what its files are for.
    /// </summary>
    [Fact]
    public void The_situation_tells_the_ai_the_limit_is_applied_before_a_turn()
    {
        // A reservation the ceiling could fund on its own, on a day that has already spent too much
        // for it — which is the ordinary way a day ends, and distinct from a ceiling that could
        // never fund one at all.
        var line = MissionSituation.SpendLine(new AiSpendToday
        {
            Metered = true, CanPrice = true, Spent = 3.6m, Reserved = 0m, NextTurnReservation = 2m,
            Cap = 5m, Currency = "USD", Turns = 3, ResumesAt = DateTimeOffset.Now.AddHours(3)
        });

        Assert.Contains("3.6 USD of a 5 USD daily limit", line);
        Assert.Contains("applied BEFORE a turn runs", line);
        Assert.Contains("refused rather than cut short", line);
    }

    /// <summary>
    /// An unpriced installation reserves nothing and is not stopped: the ceiling cannot be applied
    /// to a number nobody measured, and stopping there would stop the AI on a guess. The card and
    /// the Situation both say the limit is holding nothing back.
    /// </summary>
    [Fact]
    public void An_unpriced_installation_reserves_nothing_and_is_not_stopped()
    {
        var now = DateTimeOffset.Now;
        var meter = new TurnMeter(_db, () => 5m, runtimeId: () => "custom", now: () => now,
            recordPath: _records);

        Assert.Equal(0m, meter.Today.NextTurnReservation);
        Assert.True(meter.Today.AdmitsAnotherTurn);
        Assert.False(meter.Today.CanPrice);
    }

    /// <summary>The allowance is a setting, and zeroes in it are the shipped bound rather than none.</summary>
    [Fact]
    public void The_allowance_is_a_setting_and_a_zero_in_it_is_not_an_allowance_of_nothing()
    {
        var s = new TradeAgentSettings();
        Assert.Equal(1_200_000, s.AiTurnAllowanceInputTokens);
        Assert.Equal(20_000, s.AiTurnAllowanceOutputTokens);

        Assert.Equal(TurnAllowance.Default, TurnAllowance.From(0, 0));
        Assert.Equal(new TurnAllowance(500, 20_000), TurnAllowance.From(500, -1));
    }
}
