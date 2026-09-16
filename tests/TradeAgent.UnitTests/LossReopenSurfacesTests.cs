using TradeAgent.AgentRuntime;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHEN A CLOSURE LIFTS IS TOLD, AND A REOPEN IS ITS OWN NEWS.
///
/// <para>A closure used to need no sentence about its end: the answer was midnight, and every
/// surface said so in the same six words. Since <c>U-reopen-1</c> it is a decision with conditions —
/// at least a day, a flat book, nothing unaccounted for, a clock that has not moved — so the honest
/// answer on a screen is either the EARLIEST instant or the thing standing in the way, and a reopen
/// is an event with a record rather than a key going out of scope.</para>
///
/// <para>Every one of these reads a ROW. <c>loss_reopens_at</c> is computed from the immutable breach
/// record; <c>loss_reopened_at</c> is the receipt's own instant and is ABSENT whenever anything is
/// still closed. That second rule is the one a test has to be built to catch: an account that
/// reopened on one day and closed again on the next has not reopened, and a status saying otherwise
/// beside a refusal is the disagreement this whole family of records exists to prevent.</para>
/// </summary>
public class LossReopenSurfacesTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;

        // A COHERENT CLOCK: the monotone half moves with the wall half, which is what a machine
        // nobody has touched does. The gateway holds a closure against BOTH since U-review-med, and
        // a fake that moved only the wall would be simulating a clock somebody set forward —
        // LossClockMonotoneTests is where that one is measured.
        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    static async Task<(TradingGateway Gw, TradeAgent.Connectors.Fake.FakeConnector Conn,
        TradeAgent.Core.Db.Database Db, TestClock Clock)> Closed()
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await TestEnv.Ready(
            s => s.Risk.MaxDailyLoss = 1_000m, new GatewayOptions { Clock = clock });

        await gw.PlaceAsync(new AgentContext("a"), "surf-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        Assert.NotEmpty((await gw.LossWatchAsync()).Closed);

        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);
        return (gw, conn, db, clock);
    }

    /// <summary>
    /// THE AGENT IS TOLD THE EARLIEST IT LIFTS, AND THE OWNER'S REPORT SAYS THE SAME (item 6).
    /// </summary>
    [Fact]
    public async Task A_closed_scope_carries_the_earliest_it_reopens_on_the_status_and_in_the_report()
    {
        var (gw, _, db, _) = await Closed();
        using var _1 = db;
        await using var _2 = gw;

        var status = await gw.StatusAsync();
        var json = Json.Write(status);
        log.WriteLine($"loss_reopens_at       : {status.LossReopensAt?.ToString("O") ?? "absent"}");
        log.WriteLine($"loss_reopened_at      : {status.LossReopenedAt?.ToString("O") ?? "absent"}");

        Assert.NotNull(status.LossDayClosedAt);
        Assert.Equal(Noon.AddSeconds(40).AddHours(24), status.LossReopensAt);
        Assert.Null(status.LossReopenHeld);
        Assert.Null(status.LossReopenedAt);
        Assert.Contains("loss_reopens_at", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_reopened_at", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_reopen_held", json, StringComparison.Ordinal);

        var line = (await gw.LossTodayAsync()).Line();
        log.WriteLine($"line                  : {line}");
        Assert.Contains("at the earliest 2026-03-11 12:00 UTC", line!, StringComparison.Ordinal);

        var text = DailyReportText.Render(gw.Reports.Compose(Noon.AddMinutes(1)));
        var section = text[text.IndexOf("4. Capital and performance", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("5. Execution health", StringComparison.Ordinal)];
        log.WriteLine(section);
        Assert.Contains("reopens: by code, at the earliest", section, StringComparison.Ordinal);
        Assert.DoesNotContain("reopened:", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// WHAT IS HOLDING IT IS NAMED, AND THEN NO INSTANT IS SHOWN (item 6, and items 3 and 4's half of
    /// it). A time that has already passed, printed beside a closure that is not going to lift, is
    /// worse than no time at all: the owner waits for it and then reads the software as broken.
    /// </summary>
    [Fact]
    public async Task A_closure_held_by_a_moved_clock_shows_no_instant_and_says_what_is_wrong()
    {
        var (gw, conn, db, clock) = await Closed();
        using var _1 = db;
        await using var _2 = gw;

        // The hours pass with something open — opened at the PLATFORM, because a closed account
        // refuses everything this gateway could send — so the mark rises and nothing reopens. Then
        // the clock is pulled back behind its own high-water mark.
        conn.Broker.Accept(new PlaceOrderCommand("by-hand", conn.Broker.AccountId, "NQ", OrderSide.Buy,
            OrderType.Market, 1m, null, null, TimeInForce.Day, "opened by hand"), FillBehaviour.FillImmediately);
        clock.MoveTo(new DateTimeOffset(2026, 3, 12, 16, 0, 0, TimeSpan.Zero));
        await gw.LossWatchAsync();
        clock.MoveTo(new DateTimeOffset(2026, 3, 11, 14, 0, 0, TimeSpan.Zero));
        await gw.LossWatchAsync();

        var status = await gw.StatusAsync();
        var json = Json.Write(status);
        log.WriteLine($"loss_reopens_at       : {status.LossReopensAt?.ToString("O") ?? "absent"}");
        log.WriteLine($"loss_reopen_held      : {status.LossReopenHeld}");

        Assert.Null(status.LossReopensAt);
        Assert.NotNull(status.LossReopenHeld);
        Assert.Contains("clock", status.LossReopenHeld, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_reopens_at", json, StringComparison.Ordinal);
        Assert.Contains("loss_reopen_held", json, StringComparison.Ordinal);

        var line = (await gw.LossTodayAsync()).Line();
        Assert.Contains("It will not reopen yet:", line!, StringComparison.Ordinal);

        var text = DailyReportText.Render(gw.Reports.Compose(Noon));
        Assert.Contains("reopens: not yet — waiting on:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REOPEN IS SAID, IN THE RECEIPT'S OWN WORDS — AND UNSAID THE MOMENT ANYTHING CLOSES AGAIN
    /// (item 6, and the mutant).
    ///
    /// <para><c>loss_reopened_at</c> is not "a receipt exists". An account reopened on Wednesday and
    /// closed again on Thursday is CLOSED, and a field derived from the presence of a row — or from a
    /// day key — would say it had reopened while every order was being refused. The second half of
    /// this test is the whole guard: the same receipt is on disk throughout, and the answer changes
    /// because the closure did.</para>
    /// </summary>
    [Fact]
    public async Task A_reopen_is_reported_in_the_receipts_own_words_and_goes_silent_when_it_closes_again()
    {
        var (gw, conn, db, clock) = await Closed();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        var breach = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24)) + TimeSpan.FromMinutes(1));
        Assert.Single((await gw.LossWatchAsync()).Reopened);

        var status = await gw.StatusAsync();
        var json = Json.Write(status);
        log.WriteLine($"loss_reopened_at      : {status.LossReopenedAt?.ToString("O") ?? "absent"}");

        Assert.Null(status.LossDayClosedAt);
        Assert.Null(status.LossReopensAt);
        Assert.Equal(clock.GetUtcNow(), status.LossReopenedAt);
        Assert.Contains("loss_reopened_at", json, StringComparison.Ordinal);

        var line = (await gw.LossTodayAsync()).Line();
        log.WriteLine($"line                  : {line}");
        Assert.Contains("TradeAgent reopened your account to new risk at", line!, StringComparison.Ordinal);

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        var section = text[text.IndexOf("4. Capital and performance", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("5. Execution health", StringComparison.Ordinal)];
        log.WriteLine(section);
        Assert.Contains("reopened:", section, StringComparison.Ordinal);
        Assert.Contains("the closure ran the 24 hours it had to", section, StringComparison.Ordinal);

        // AND NOW IT CLOSES AGAIN — a second, genuine episode on a later day. The receipt from the
        // first one is still on disk and must stop being the answer. The order goes out first, while
        // the budget is still wide; the budget is then narrowed to what the ledger already carries.
        await gw.PlaceAsync(new AgentContext("a"), "second-open", TestEnv.Buy("ES"));
        gw.Update(s => s.Risk.MaxDailyLoss = 1_000m);
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        Assert.NotEmpty((await gw.LossWatchAsync()).Closed);

        var again = await gw.StatusAsync();
        log.WriteLine($"after the second close: closed={again.LossDayClosedAt?.ToString("O") ?? "absent"} "
                      + $"reopened={again.LossReopenedAt?.ToString("O") ?? "absent"}");

        Assert.NotNull(again.LossDayClosedAt);
        Assert.Null(again.LossReopenedAt);
        Assert.DoesNotContain("loss_reopened_at", Json.Write(again), StringComparison.Ordinal);
        Assert.NotNull(db.GetKv(LossReopen.KeyFor(conn.Id, breach)));
    }

    /// <summary>
    /// THE DOCUMENTS AND THE SCREEN, held by name — the lines no test off a running app can reach.
    /// The same pattern as <c>LossDayClosedSurfacesTests</c>, and here because the promise that
    /// changed is the one an owner plans around: "it lifts at midnight" was in the guide, in the
    /// schema the agent reads, in its workspace instructions and in the error's own next step.
    /// </summary>
    [Fact]
    public void The_screen_the_guide_the_contract_and_the_agents_instructions_all_say_what_reopens_it()
    {
        var repo = Repo();

        var screen = File.ReadAllText(Path.Combine(repo, "src", "TradeAgent.App", "DashboardView.cs"));
        Assert.Contains("_host.Gateway.ReopenReading()", screen, StringComparison.Ordinal);

        var guide = File.ReadAllText(Path.Combine(repo, "docs", "USER-GUIDE.md"));
        Assert.Contains("TradeAgent reopens it itself, and only once it has looked", guide, StringComparison.Ordinal);

        var contracts = File.ReadAllText(Path.Combine(repo, "docs", "CONTRACTS.md"));
        Assert.Contains("loss_reopen:{connector}:{account}:{utcDay}", contracts, StringComparison.Ordinal);
        Assert.Contains("LossMinClosure", contracts, StringComparison.Ordinal);
        Assert.Contains("loss_clock_high_water", contracts, StringComparison.Ordinal);
        Assert.Contains("APPROVAL_PREDATES_LOSS_BREACH", contracts, StringComparison.Ordinal);

        var schema = Json.Write(GatewaySchema.Describe());
        Assert.Contains("loss_reopens_at", schema, StringComparison.Ordinal);
        Assert.Contains("loss_reopened_at", schema, StringComparison.Ordinal);
        Assert.Contains("loss_reopen_held", schema, StringComparison.Ordinal);
        // RE-PINNED BY U-reopen-2, item 3: the schema used to say "AT LEAST 24 HOURS", and 24 hours
        // stopped being a constant when the closure length became the account owner's number. The
        // sentence it has to carry now is the one that replaced it and says strictly more — that the
        // length is the SNAPSHOT on the record rather than the setting in force when the agent asks.
        Assert.Contains("AT LEAST the closure length that applied WHEN THE BREACH WAS CONFIRMED",
            schema, StringComparison.Ordinal);
        Assert.Contains("loss_closure_rule", schema, StringComparison.Ordinal);

        // And the refusal's own next step, which used to send the owner away to wait for midnight.
        var reached = Errors.Get(ErrorCode.LOSS_BUDGET_REACHED, "x");
        Assert.Contains("reopens the account itself", reached.Repair, StringComparison.Ordinal);
        Assert.DoesNotContain("midnight UTC", reached.Repair, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "tradeagent-tests", Guid.NewGuid().ToString("n"));
        var home = WorkspaceBuilder.Build(new WorkspaceContext(
            ConnectorName: "Simulator (built in)", ConnectorIsPaper: true, AccountId: "SIM-001",
            Mode: TradingMode.PAPER, ExecutionAvailable: true, ExecutionBlockedReason: null,
            Risk: new RiskPolicy { MaxDailyLoss = 500m, InstrumentAllowlist = ["ES"] }), root);
        var agents = File.ReadAllText(Path.Combine(home, "AGENTS.md"));
        Assert.Contains("loss_reopens_at", agents, StringComparison.Ordinal);
        // RE-PINNED with the schema above, and for the same reason.
        Assert.Contains("closure length the account owner had set when the breach was",
            agents, StringComparison.Ordinal);
        Assert.Contains("24 hours out of the", agents, StringComparison.Ordinal);
        Directory.Delete(root, true);
    }

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
