using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE DAY IS TOLD CLOSED, HONESTLY, ON EVERY SURFACE — and "honestly" is doing all the work.
///
/// <para>Four things have to be said. Since WHEN: a closure without an instant is a mood. WHY: the
/// figure that closed it, against the limit it was measured against, in the account's currency. That
/// it lasts the WHOLE UTC DAY: an owner who thinks it will lift when the number recovers will sit and
/// wait for something that is not coming. And WHAT WAS DONE ABOUT THE POSITIONS — which until
/// <c>U-flatten-2</c> was "nothing, and say so plainly", and is now that TradeAgent closes what is
/// open. The fourth one is asserted here as the RULE the closure sentence states; whether it actually
/// happened is a different record and <see cref="LossFlattenSurfacesTests"/> holds it.</para>
///
/// <para>Every one of these reads the RECORD. Deriving any of it from today's loss is the defect the
/// unit exists to close, and it is the one a test has to be built to catch: the day closes on an
/// unrealised thousand, the loser is then closed at nine hundred and seventy-five realised, and a
/// surface computing from the figure would say the day had reopened while the gateway went on
/// refusing every order.</para>
/// </summary>
public class LossDayClosedSurfacesTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A day closed by the watch, and then made to look open again.
    ///
    /// <para>It used to be made to look open by closing the loser at a REALISED loss smaller than the
    /// budget. <c>U-flatten-2</c> closes the loser itself, at the price that closed the day, so the
    /// figure the ledger is left holding IS the budget and that route is gone. What replaces it is the
    /// harder version of the same attack: the owner WIDENS the budget afterwards, which makes every
    /// number on every surface say the day is fine while the gateway goes on refusing. No assertion
    /// below changed — this is the setup arriving at the same state by the route that still exists.</para>
    /// </summary>
    static async Task<(TradingGateway Gw, TradeAgent.Core.Db.Database Db, TestClock Clock)> ClosedDay(
        ITestOutputHelper log)
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await TestEnv.Ready(
            s => s.Risk.MaxDailyLoss = 1_000m, new GatewayOptions { Clock = clock });

        await gw.PlaceAsync(new AgentContext("a"), "surf-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        var closing = await gw.LossWatchAsync();
        Assert.NotEmpty(closing.Closed);

        // AND NOW THE BUDGET IS WIDENED, well past what the day has lost.
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);
        conn.Broker.PriceOffset = -19m;
        await gw.QuoteAsync("ES");

        var reading = await gw.LossTodayAsync();
        log.WriteLine($"figure now            : {reading.Loss} of {reading.DayBudget}, day reached {reading.DayReached}");
        Assert.False(reading.DayReached);     // under the budget again — and still closed

        return (gw, db, clock);
    }

    /// <summary>
    /// THE AGENT-FACING STATUS CARRIES SINCE WHEN, AND NOT A RECOMPUTED VERDICT (item 4).
    ///
    /// <para><c>loss_day_closed_at</c> is on the wire because the AI plans against it: an agent that
    /// believes the day is open spends its turns writing orders that will all be refused, reads the
    /// refusals as the software being broken, and says so to the owner. Absent means open, and absent
    /// is what an agent must be able to trust.</para>
    /// </summary>
    [Fact]
    public async Task The_status_says_since_when_the_day_has_been_closed_even_after_the_figure_recovers()
    {
        var (gw, db, _) = await ClosedDay(log);
        using var _1 = db;
        await using var _2 = gw;

        var status = await gw.StatusAsync();
        var json = Json.Write(status);
        log.WriteLine($"loss_day_closed_at    : {status.LossDayClosedAt?.ToString("O") ?? "absent"}");
        log.WriteLine($"loss_today            : {status.LossToday?.ToString() ?? "absent"}");

        Assert.NotNull(status.LossDayClosedAt);
        Assert.Equal(Noon.AddSeconds(40), status.LossDayClosedAt);
        Assert.Contains("loss_day_closed_at", json, StringComparison.Ordinal);

        // The figure beside it is the one the ledger holds now, and it is SMALLER than the budget.
        // Both are true, and that is exactly why the closure cannot be derived from the figure.
        Assert.NotNull(status.LossToday);
        Assert.True(status.LossToday < status.LossBudgetDay);
    }

    /// <summary>
    /// THE SITUATION BLOCK SAYS IT IN THE AI'S OWN WORDS, including what happens to its positions.
    /// </summary>
    [Fact]
    public async Task The_situation_line_says_since_when_why_and_what_happens_to_the_positions()
    {
        var (gw, db, _) = await ClosedDay(log);
        using var _1 = db;
        await using var _2 = gw;

        var loss = await gw.LossTodayAsync();
        var line = loss.Line();
        log.WriteLine($"line                  : {line}");

        Assert.NotNull(line);
        Assert.Contains("closed today to new risk at 12:00 UTC", line, StringComparison.Ordinal);
        // The rule it lasts by, re-pinned by U-reopen-1: "until the next UTC day" was true of a
        // record that expired with its key, and a breach at 23:58Z had two minutes of it.
        Assert.Contains("stays closed for at least 24 hours", line, StringComparison.Ordinal);
        Assert.Contains("CLOSES YOUR OPEN POSITIONS", line, StringComparison.Ordinal);

        // And it is in the Situation the mission actually renders, not only in the helper.
        var situation = new MissionSituation { LocalTime = Noon, Mode = "PAPER", Loss = loss }.Text();
        Assert.Contains("closed today to new risk", situation, StringComparison.Ordinal);
    }

    /// <summary>
    /// SECTION 4 OF THE OWNER'S REPORT SAYS IT, beside a figure that no longer looks like a breach.
    ///
    /// <para>The report is written from TradeAgent's own ledger and asks the platform nothing, which
    /// is exactly why this line has to come from the record: the section's own figure is the realised
    /// loss, and on the day this is about, that figure is under the budget.</para>
    /// </summary>
    [Fact]
    public async Task Section_four_of_the_owners_report_says_the_day_was_closed_and_what_that_does_to_positions()
    {
        var (gw, db, _) = await ClosedDay(log);
        using var _1 = db;
        await using var _2 = gw;

        var text = DailyReportText.Render(gw.Reports.Compose(Noon.AddMinutes(1)));
        var section = text[text.IndexOf("4. Capital and performance", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("5. Execution health", StringComparison.Ordinal)];
        log.WriteLine(section);

        Assert.Contains("closed to new risk", section, StringComparison.Ordinal);
        Assert.Contains("CLOSES YOUR OPEN POSITIONS", section, StringComparison.Ordinal);
        Assert.Contains("stays closed for at least 24 hours", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN OPEN DAY SAYS NOTHING AND SAYS IT BY BEING ABSENT — the convention every figure on this
    /// status follows. A field that was present-and-null on an open day would be one more thing an
    /// agent could read the wrong way round.
    /// </summary>
    [Fact]
    public async Task An_open_day_carries_neither_field_and_the_report_says_so_in_words()
    {
        var clock = new TestClock(Noon);
        var (gw, _, db) = await TestEnv.Ready(
            s => s.Risk.MaxDailyLoss = 1_000m, new GatewayOptions { Clock = clock });
        using var _1 = db;
        await using var _2 = gw;

        var json = Json.Write(await gw.StatusAsync());
        Assert.DoesNotContain("loss_day_closed_at", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_symbols_closed", json, StringComparison.Ordinal);

        var loss = await gw.LossTodayAsync();
        Assert.Null(loss.ClosedLine());

        var text = DailyReportText.Render(gw.Reports.Compose(Noon));
        Assert.Contains("closed to new risk: no — the day was open", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CLOSED SYMBOL IS NAMED, and it is a different sentence from a closed day: opens and adds on
    /// that instrument only, with the rest of the account untouched.
    /// </summary>
    [Fact]
    public async Task A_closed_symbol_is_named_on_the_status_and_in_the_report()
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await TestEnv.Ready(s =>
        {
            s.Risk.MaxLossPerTrade = 500m;
            s.Risk.MaxDailyLoss = 0m;
        }, new GatewayOptions { Clock = clock });
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "sym-open", TestEnv.Buy("ES", 2m));
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        var status = await gw.StatusAsync();
        log.WriteLine($"loss_symbols_closed   : {string.Join(", ", status.LossSymbolsClosed ?? [])}");
        Assert.Equal(new[] { "ES" }, status.LossSymbolsClosed);
        Assert.Null(status.LossDayClosedAt);
        Assert.Contains("loss_symbols_closed", Json.Write(status), StringComparison.Ordinal);

        var loss = await gw.LossTodayAsync();
        Assert.Contains("ES", loss.ClosedLine()!, StringComparison.Ordinal);

        var text = DailyReportText.Render(gw.Reports.Compose(Noon.AddMinutes(1)));
        Assert.Contains("positions closed to adds: ES", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DOCUMENTS AND THE SCREEN, held by name — the lines no test off a running app can reach,
    /// which catch a revert or a deletion rather than a rewrite. The same pattern as
    /// <c>LossBudgetCompositionTests</c>, and here for the same reason: the closure is the first
    /// thing in this product that stops the AI trading for a whole day, and an owner who is not told
    /// what it is and when it lifts reads it as a fault.
    /// </summary>
    [Fact]
    public void The_screen_the_guide_the_contract_and_the_agents_instructions_all_say_the_day_stays_closed()
    {
        var repo = Repo();

        var screen = File.ReadAllText(Path.Combine(repo, "src", "TradeAgent.App", "DashboardView.cs"));
        Assert.Contains("_lossClosedNote,", screen, StringComparison.Ordinal);
        Assert.Contains("_host.Gateway.ClosureToday()", screen, StringComparison.Ordinal);

        var guide = File.ReadAllText(Path.Combine(repo, "docs", "USER-GUIDE.md"));
        Assert.Contains("stays closed for at least 24 hours", guide, StringComparison.Ordinal);

        var contracts = File.ReadAllText(Path.Combine(repo, "docs", "CONTRACTS.md"));
        Assert.Contains("loss_day_closed_at", contracts, StringComparison.Ordinal);
        Assert.Contains("LossWatchInterval", contracts, StringComparison.Ordinal);
        Assert.Contains("LossBreachConfirmWithin", contracts, StringComparison.Ordinal);
        Assert.Contains("loss_breach:{account}:{utcDay}", contracts, StringComparison.Ordinal);

        // The schema the agent reads over the pipe, and the instructions it is given in its workspace.
        var schema = Json.Write(GatewaySchema.Describe());
        Assert.Contains("loss_day_closed_at", schema, StringComparison.Ordinal);
        Assert.Contains("loss_symbols_closed", schema, StringComparison.Ordinal);
        Assert.Contains("A CLOSURE DOES NOT END AT MIDNIGHT", schema, StringComparison.Ordinal);
        Assert.Contains("CLOSES what is open", schema, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "tradeagent-tests", Guid.NewGuid().ToString("n"));
        var home = WorkspaceBuilder.Build(new WorkspaceContext(
            ConnectorName: "Simulator (built in)", ConnectorIsPaper: true, AccountId: "SIM-001",
            Mode: TradingMode.PAPER, ExecutionAvailable: true, ExecutionBlockedReason: null,
            Risk: new RiskPolicy { MaxDailyLoss = 500m, InstrumentAllowlist = ["ES"] }), root);
        var agents = File.ReadAllText(Path.Combine(home, "AGENTS.md"));
        Assert.Contains("loss_day_closed_at", agents, StringComparison.Ordinal);
        Assert.Contains("closes your open positions for you", agents, StringComparison.Ordinal);
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
