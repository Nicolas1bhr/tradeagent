using TradeAgent.AgentRuntime;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT THE THRESHOLDS DO, AND WHAT THEY DO NOT PROMISE — said in the daily report, in words, on
/// every day and whatever the budgets are set to.
///
/// <para>"Most it may lose in one day" reads as a bound on the loss. It is not one. It is the figure
/// at which TradeAgent intervenes, and what the intervention is left holding is whatever a market
/// order fills at after the market has gapped, across whatever spread it has to cross, less fees the
/// platform may never have reported. A report that printed the budget beside the day's figure and
/// said nothing else would be handing the owner a guarantee this product cannot give, and an owner
/// who believes a loss is bounded takes risks they would not otherwise take.</para>
///
/// <para>The sampling delay is the other half of it, and it is MEASURED rather than declared: the
/// gateway times its own reading of the open book on a real wall clock and the report prints what it
/// took. A constant compiled into this build would be a claim about somebody else's computer.</para>
/// </summary>
public class ValuationLossSurfacesTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    /// <summary>A feed that has stopped: every price the platform serves is a memory.</summary>
    static readonly TimeSpan Silent = TimeSpan.FromMinutes(10);

    /// <summary>
    /// THE ONE WALL CLOCK IN THIS FILE, AND IT IS THE SIMULATOR'S EMERGENCY BUDGET.
    ///
    /// <para>Everything these fixtures assert runs on <see cref="TestClock"/>, which is why they can
    /// spell four thresholds and an age in seconds. One thing does not: the data-loss exit does its
    /// cancel-then-settle-then-close inside <c>RiskReducingScope.Begin(Connector.EmergencyBudget)</c>,
    /// an absolute <see cref="Environment.TickCount64"/> deadline, and the simulator's shipped
    /// default is two seconds. Inside it are durable SQLite commits at <c>synchronous=FULL</c>.</para>
    ///
    /// <para><b>MEASURED, on draft PR #23 (runs 35504722157 and 35513092386), by a throwaway probe
    /// that timed each health pass of the exit fixture, two runs each:</b> the pass that fires the
    /// exit spent 29-33 ms on ubuntu-latest, 17-19 ms on macos-latest and <b>833-1258 ms on
    /// windows-latest</b> — of 2000. A windows job whose Unit suite runs 19 m 14 s against the usual
    /// ~40 s has nothing like that left, and the SAME probe with the budget cut to 1 ms reproduced
    /// the runner's red exactly on all three runners, in both runs: the valuation CANCEL press does not resolve, so
    /// <c>ExitLostValuationAsync</c> refuses to send a second press of its own kind
    /// (<c>valuation_cancel_press_already_open</c>, <c>valuation_exit_press_already_open</c> in the
    /// engineering log), no exit row is written, and the report's list prints its honest
    /// <c>none</c> — on which <c>Assert.Contains("VALUATION_LOST", text)</c> fails with
    /// <c>Sub-string not found</c>. The product is right at every step of that; the fixture was
    /// asking a question whose answer depended on the runner's disk.</para>
    ///
    /// <para>Twenty seconds is 16x the worst of those three and the number the sweep family settled
    /// on for the same reason (<c>SweepRequestIdTests.SweepBudget</c>). Nothing is loosened: no
    /// assertion below moves, and no test here is ABOUT the emergency budget.</para>
    /// </summary>
    static readonly TimeSpan Emergency = TimeSpan.FromSeconds(20);

    static async Task<(TradingGateway Gw, FakeConnector Conn, TradeAgent.Core.Db.Database Db, TestClock Clock)>
        Ready(decimal dailyBudget = 1_000m, decimal exitAfterMinutes = 15m)
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await TestEnv.Ready(s =>
        {
            s.Risk.MaxDailyLoss = dailyBudget;
            s.Risk.ValuationLossExitMinutes = exitAfterMinutes;
        }, new GatewayOptions { Clock = clock }, emergencyBudget: Emergency);
        return (gw, conn, db, clock);
    }

    /// <summary>
    /// HOW MANY HEALTH PASSES A FIXTURE HERE WILL DRIVE BEFORE IT CALLS THE PRODUCT WRONG.
    ///
    /// <para>A pass that throws is swallowed by <c>RefreshHealthAsync</c>'s own catch and a pass
    /// whose episode row will not commit is dropped by <c>WriteValuationEpisode</c> — by design, in
    /// both cases: "an episode the app cannot record is not an episode it may act on". Either one
    /// shifts every later step of a fixture that counts passes instead of watching for the record.
    /// MEASURED on the same probe: losing the FIRST unvaluable pass opens the episode 20 seconds
    /// late and the exit never fires inside four passes (<c>VALUATION_LOST in the report: False</c>
    /// on all three runners), and losing the pass that would fire the exit does the same; driving to
    /// the record instead took 4 passes normally and 5 with a pass lost, on every runner and at
    /// every lost index. Twelve is three times the four this fixture needs, and it is a bound rather
    /// than a wait: nothing here sleeps, the clock is the test's own.</para>
    /// </summary>
    const int Passes = 12;

    // ===================== PROBE (U-runner-reds-3, throwaway; reverted before the fix) ===========
    // A gateway exactly like Ready()'s but over a simulator whose EMERGENCY BUDGET this probe
    // chooses. The budget is a REAL wall clock (Environment.TickCount64, FakeConnector's default is
    // 2 s) and the data-loss exit runs its cancel-then-close inside one, so it is the only quantity
    // in this fixture the runner can spend.
    static async Task<(TradingGateway Gw, FakeConnector Conn, TradeAgent.Core.Db.Database Db, TestClock Clock)>
        ProbeReady(TimeSpan emergency, decimal exitAfterMinutes = 1m)
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker(), null) { EmergencyBudget = emergency };
        var gw = new TradingGateway(db, conn, new TradeAgent.Core.HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxDailyLoss = 1_000m;
            s.Risk.ValuationLossExitMinutes = exitAfterMinutes;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    /// <summary>The gateway's own account of what it did, in its own words.</summary>
    static List<string> Engineering(TradeAgent.Core.Db.Database db)
    {
        var rows = new List<string>();
        using var c = db.Cmd("SELECT event,severity FROM engineering_log WHERE component='Gateway' ORDER BY id");
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add($"{r.GetString(0)}/{r.GetString(1)}");
        return rows;
    }

    /// <summary>
    /// PROBE: the fixture's schedule, per step, against the 2 s emergency budget it runs inside —
    /// and the same fixture with that budget cut, to see whether a spent budget is what removes the
    /// exit line the runner's red says is missing.
    /// </summary>
    [Fact]
    public async Task Probe_valuation_exit_schedule()
    {
        var said = new List<string>();
        // (budget the exit's cancel-then-close runs inside, which pass — if any — the runner loses)
        foreach (var (budget, lose) in new[]
                 {
                     (TimeSpan.FromSeconds(2), -1), (TimeSpan.FromMilliseconds(1), -1),
                     (TimeSpan.FromSeconds(2), 0), (TimeSpan.FromSeconds(2), 1),
                     (TimeSpan.FromSeconds(2), 2), (TimeSpan.FromSeconds(2), 3)
                 })
        {
            var (gw, conn, db, clock) = await ProbeReady(budget);
            using var _1 = db;
            await using var _2 = gw;

            var wall = System.Diagnostics.Stopwatch.StartNew();
            await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
            var placed = wall.Elapsed.TotalMilliseconds;
            clock.Advance(Tick);
            await gw.RefreshHealthAsync();
            var first = wall.Elapsed.TotalMilliseconds;

            conn.Faults.QuoteAge = Silent;
            var steps = new List<double>();
            var episodes = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var at = wall.Elapsed.TotalMilliseconds;
                clock.Advance(Tick);
                // A LOST PASS, which is what a health refresh that throws amounts to: the catch at
                // the bottom of RefreshHealthAsync swallows it and the loss watch never runs.
                conn.Faults.Disconnected = i == lose;
                await gw.RefreshHealthAsync();
                conn.Faults.Disconnected = false;
                steps.Add(wall.Elapsed.TotalMilliseconds - at);
                var row = db.KvStartingWith("valuation_unavailable:").FirstOrDefault();
                episodes.Add(row.Value is null ? "(no episode)"
                    : $"since={Json.Read<System.Text.Json.JsonElement>(row.Value).GetProperty("since").GetString()}");
            }

            var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
            var exited = text.Contains("VALUATION_LOST", StringComparison.Ordinal);
            var line = text.Split('\n').First(l => l.Contains("closed because nothing could value it", StringComparison.Ordinal));

            var one =
                $"PROBE valuation budget={budget.TotalMilliseconds:0} ms lost-pass={lose} | place {placed:0.#} ms | first refresh "
                + $"{first - placed:0.#} ms | the four unvaluable refreshes {string.Join(", ", steps.Select(s => $"{s:0.#}"))} ms "
                + $"| total {wall.Elapsed.TotalMilliseconds:0.#} ms | episode after each: {string.Join(" ", episodes)} "
                + $"| VALUATION_LOST in the report: {exited} | line: {line.Trim()[..Math.Min(140, line.Trim().Length)]} "
                + $"| engineering: {string.Join(", ", Engineering(db))}";
            log.WriteLine(one);
            said.Add(one);
        }

        // A PROBE IS NOT A TEST. It fails on purpose so the measurement is printed by the runner's
        // own step log, which is the only place a draft PR's numbers can be read from.
        Assert.Fail(string.Join("\n", said));
    }

    /// <summary>
    /// PROBE: the proposed fixture — drive the health pass until the product has RECORDED the exit,
    /// bounded, instead of assuming the fourth pass is the one — under every lost pass in turn.
    /// </summary>
    [Fact]
    public async Task Probe_valuation_exit_driven_to_the_record()
    {
        var said = new List<string>();
        foreach (var lose in new[] { -1, 0, 1, 2, 3, 4 })
        {
            var (gw, conn, db, clock) = await ProbeReady(TimeSpan.FromSeconds(2));
            using var _1 = db;
            await using var _2 = gw;

            var wall = System.Diagnostics.Stopwatch.StartNew();
            await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
            clock.Advance(Tick);
            await gw.RefreshHealthAsync();

            conn.Faults.QuoteAge = Silent;
            var passes = 0;
            string text;
            do
            {
                clock.Advance(Tick);
                conn.Faults.Disconnected = passes == lose;
                await gw.RefreshHealthAsync();
                conn.Faults.Disconnected = false;
                passes++;
                text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
            }
            while (passes < 12 && !text.Contains("VALUATION_LOST", StringComparison.Ordinal));

            var one = $"PROBE valuation-driven lost-pass={lose} | passes {passes} | wall {wall.Elapsed.TotalMilliseconds:0.#} ms "
                       + $"| VALUATION_LOST in the report: {text.Contains("VALUATION_LOST", StringComparison.Ordinal)}";
            log.WriteLine(one);
            said.Add(one);
        }

        // A PROBE IS NOT A TEST. It fails on purpose so the measurement is printed by the runner's
        // own step log, which is the only place a draft PR's numbers can be read from.
        Assert.Fail(string.Join("\n", said));
    }
    // =================== end PROBE ==============================================================

    /// <summary>
    /// THE REPORT SAYS THE BUDGET IS A THRESHOLD AND NOT A MAXIMUM LOSS (item 3).
    ///
    /// <para>All four thresholds are named with the numbers actually in force — the tick the book is
    /// valued on, the window a second reading has to agree inside, the age at which a price is
    /// refused, and the bound past which a position nobody can value is closed — together with what
    /// they cannot bound: the gap between two readings, the spread the closing order crosses, the
    /// slippage on the way and the fees the platform did not report.</para>
    ///
    /// <para><b>The mutant this watches</b> is printing the line only for an installation whose
    /// budget is zero. That is the one reader with nothing to be misled about, and it withholds the
    /// sentence from every owner who has set a budget and is relying on it: a live budget then reads
    /// as a guarantee, which is exactly the defect.</para>
    /// </summary>
    [Fact]
    public async Task The_report_states_what_the_thresholds_do_and_that_they_are_not_a_maximum_loss()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        log.WriteLine($"MEASURED sampling: one reading of the open book took {gw.LastLossWatchTook?.TotalMilliseconds:0.###} ms "
                      + $"on this machine; the gap between two readings is up to {TimeSpan.FromSeconds(15)}");

        // A BUDGET IS SET, so the mutant's "only when it is zero" never prints and this fails first.
        Assert.Contains("what these limits are and are not", text, StringComparison.Ordinal);
        Assert.Contains("none of them is a maximum loss", text, StringComparison.Ordinal);
        log.WriteLine(text.Split('\n').First(l => l.Contains("what these limits are and are not", StringComparison.Ordinal)));

        // THE FOUR THRESHOLDS, with the numbers in force.
        Assert.Contains("values your open book every 15 seconds", text, StringComparison.Ordinal);
        Assert.Contains("agreeing reading within 1 minute", text, StringComparison.Ordinal);
        Assert.Contains("older than 30 seconds", text, StringComparison.Ordinal);
        Assert.Contains("unable to value at all for 15 minutes", text, StringComparison.Ordinal);

        // AND WHAT THEY CANNOT BOUND.
        Assert.Contains("gap straight through the figure", text, StringComparison.Ordinal);
        Assert.Contains("spread it crosses", text, StringComparison.Ordinal);
        Assert.Contains("slippage", text, StringComparison.Ordinal);
        Assert.Contains("fees your platform has not reported", text, StringComparison.Ordinal);
        Assert.Contains("can be larger than the budget", text, StringComparison.Ordinal);

        // AND THE SAMPLING DELAY, MEASURED ON THE MACHINE THIS IS RUNNING ON.
        Assert.NotNull(gw.LastLossWatchTook);
        Assert.Contains("last reading of your open book took", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND IT IS PRINTED ON AN INSTALLATION WITH NO BUDGET TOO — the other side of the mutant.
    ///
    /// <para>Printing it here as well is what makes the sentence a statement about the product rather
    /// than a footnote attached to one configuration. An owner about to set their first budget reads
    /// this report before they set it.</para>
    /// </summary>
    [Fact]
    public async Task The_disclosure_is_printed_whether_a_budget_is_set_or_not()
    {
        var (gw, _, db, clock) = await Ready(dailyBudget: 0m);
        using var _1 = db;
        await using var _2 = gw;

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        Assert.Contains("no daily loss budget is set", text, StringComparison.Ordinal);
        Assert.Contains("none of them is a maximum loss", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ZERO EXIT BOUND IS DISCLOSED AS THE WIDEST VALUE IT HAS, not omitted.
    ///
    /// <para>Switching the data-loss exit off is a decision with a consequence — a position nobody
    /// can value stays open and unbounded — and the report that lists the thresholds is the document
    /// that has to say which of them is not in force.</para>
    /// </summary>
    [Fact]
    public async Task A_switched_off_data_loss_exit_is_disclosed_rather_than_left_out()
    {
        var (gw, _, db, clock) = await Ready(exitAfterMinutes: 0m);
        using var _1 = db;
        await using var _2 = gw;

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        Assert.Contains("will NOT close a position it cannot value at all", text, StringComparison.Ordinal);
        Assert.DoesNotContain("unable to value at all for", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A POSITION NOBODY CAN VALUE IS ON THE STATUS AND IN SECTION FOUR, WITH ITS CLOCK.
    ///
    /// <para>An agent that reads <c>RISK_CHECK_UNAVAILABLE</c> has been told that its order was
    /// refused, not that a POSITION has been unmeasurable for minutes and is on its way to being
    /// closed by the app. The owner reading a report is in the same position: the money section's
    /// figure is silently missing whatever nobody could value.</para>
    /// </summary>
    [Fact]
    public async Task The_status_and_the_report_name_the_position_nobody_can_value_and_since_when()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
        conn.Faults.QuoteAge = Silent;

        // THE EPISODE HAS TO OPEN BEFORE ITS CLOCK CAN BE READ, and which pass opens it is the
        // product's answer rather than this fixture's arithmetic — see Passes. So the first tick is
        // watched for rather than counted, and the two ticks AFTER it are what the 40 seconds below
        // is made of, whichever pass the record turned out to be written on.
        var opened = 0;
        while (gw.Reports.Compose(clock.GetUtcNow()).Performance.ValuationLost.Count == 0)
        {
            Assert.True(opened < Passes,
                $"no episode was opened for an unvaluable ES in {opened} loss watches");
            clock.Advance(Tick);
            await gw.LossWatchAsync();
            opened++;
        }

        for (var i = 0; i < 2; i++)
        {
            clock.Advance(Tick);
            await gw.LossWatchAsync();
        }

        var json = Json.Write(await gw.StatusAsync());
        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        log.WriteLine(json);
        log.WriteLine(text.Split('\n').First(l => l.Contains("cannot value", StringComparison.Ordinal)));

        Assert.Contains("loss_valuation_lost", json, StringComparison.Ordinal);
        Assert.Contains("cannot work out what your open 2 ES is worth", json, StringComparison.Ordinal);
        Assert.Contains("positions TradeAgent cannot value", text, StringComparison.Ordinal);
        Assert.Contains("It has now been 40 seconds", text, StringComparison.Ordinal);

        // AND IT IS NOT A CLOSURE. Nothing here may read as a budget having been reached.
        Assert.DoesNotContain("loss_day_closed_at", json, StringComparison.Ordinal);
        Assert.Contains("no — the day was open to new positions", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND AN EXIT IS ITS OWN LINE, NEVER FOLDED INTO "positions closed for you".
    ///
    /// <para>That line is about a budget that was REACHED. A reader who found a data-loss exit under
    /// it would be told their money went when their prices did — the mutant item 2's fixture watches,
    /// arriving at the surface where the owner would actually read it.</para>
    /// </summary>
    [Fact]
    public async Task An_exit_is_reported_under_its_own_line_and_says_no_budget_was_reached()
    {
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();

        conn.Faults.QuoteAge = Silent;

        // DRIVEN TO THE PRODUCT'S OWN RECORD, NOT TO A PASS COUNT — see Passes. The exit is due on
        // the fourth pass and the fixture used to stop there, so a single pass the runner lost took
        // the exit line out of the report and left the list saying its honest `none`, on which the
        // assertions below fail with `Sub-string not found` rather than with anything about the
        // product. The health pass is idempotent and the clock is this test's, so asking again costs
        // nothing at all.
        var passes = 0;
        while (gw.Reports.Compose(clock.GetUtcNow()).Performance.ValuationExits.Count == 0)
        {
            Assert.True(passes < Passes,
                $"no data-loss exit was recorded in {passes} health passes, {passes * Tick.TotalSeconds:0} "
                + "seconds past a bound of one minute");
            clock.Advance(Tick);
            await gw.RefreshHealthAsync();
            passes++;
        }

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        var line = text.Split('\n').First(l => l.Contains("closed because nothing could value it", StringComparison.Ordinal));
        log.WriteLine(line);

        Assert.Contains("closed because nothing could value it", text, StringComparison.Ordinal);
        Assert.Contains("VALUATION_LOST", text, StringComparison.Ordinal);
        Assert.Contains("Your loss budget was NOT reached", text, StringComparison.Ordinal);
        Assert.DoesNotContain("positions closed for you", text, StringComparison.Ordinal);
        Assert.Contains("no — the day was open to new positions", text, StringComparison.Ordinal);
    }
}
