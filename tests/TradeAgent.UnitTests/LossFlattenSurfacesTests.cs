using TradeAgent.AgentRuntime;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PRODUCT STOPPED PROMISING "NOTHING IS CLOSED FOR YOU", SO EVERY SURFACE HAS TO SAY THE NEW
/// THING — and the new thing is not "your positions were closed" either, because that is a claim
/// about the platform that TradeAgent can only make when it has read the book back.
///
/// <para>Two states and no third. <c>flat</c> means the closes are FILLED and a fresh read says
/// nothing is open. <c>unresolved</c> means anything else: a leg refused at the wire, a close with no
/// confirmed outcome, an order that could not be cancelled, a position still showing. An owner or an
/// agent reading <c>unresolved</c> has to go and look, and that is exactly what it is for.</para>
///
/// <para>Every one of these reads the flatten's own RECORD, written once at the end of the run. A
/// surface deriving "flat" from the composite answering ok would be this app agreeing with itself
/// about the one fact it cannot decide.</para>
/// </summary>
public class LossFlattenSurfacesTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    /// <param name="refuseTheClose">
    /// The platform refuses the flatten's own close. Armed AFTER the position is opened, because a
    /// refusal that landed on the opening order would leave nothing to breach a budget with.
    /// </param>
    static async Task<(TradingGateway Gw, FakeConnector Conn, TradeAgent.Core.Db.Database Db)> Flattened(
        bool refuseTheClose = false)
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await TestEnv.Ready(
            s => s.Risk.MaxDailyLoss = 1_000m, new GatewayOptions { Clock = clock });

        await gw.PlaceAsync(new AgentContext("a"), "flat-open", TestEnv.Buy("ES"));
        if (refuseTheClose) conn.Faults.RejectNext = 1;
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// THE AGENT-FACING STATUS CARRIES WHAT WAS DONE, NOT ONLY THAT THE DAY IS SHUT (item 5).
    ///
    /// <para>An agent that reads "the day is closed" and nothing else will assume its positions are
    /// where it left them and plan the rest of its session around managing them. They are not there.
    /// <c>loss_flatten</c> is the field that says so, and it is ABSENT while nothing has been
    /// flattened — the convention every other figure on this status follows.</para>
    /// </summary>
    [Fact]
    public async Task The_status_says_the_positions_were_closed_and_whether_that_is_confirmed()
    {
        var (gw, conn, db) = await Flattened();
        using var _1 = db;
        await using var _2 = gw;

        var json = Json.Write(await gw.StatusAsync());
        log.WriteLine(json);
        Assert.Contains("\"loss_flatten\":\"flat\"", json, StringComparison.Ordinal);
        Assert.Equal(0m, conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m);
    }

    /// <summary>An open day has never been flattened, so the field is absent rather than empty.</summary>
    [Fact]
    public async Task An_open_day_carries_no_flatten_field_at_all()
    {
        var (gw, _, db) = await TestEnv.Ready(s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;
        await using var _2 = gw;

        var json = Json.Write(await gw.StatusAsync());
        Assert.DoesNotContain("loss_flatten", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A FLATTEN THE APP COULD NOT CONFIRM READS <c>unresolved</c>, AND THAT IS THE WHOLE POINT OF
    /// HAVING TWO WORDS (item 5, and the mutant this guards).
    ///
    /// <para>The close here has no confirmed outcome and the position is exactly where it was. Every
    /// surface has to say so: a status that said <c>flat</c> off the composite's own ok would tell an
    /// agent the book is empty while it is not, and the agent's next decision is made against a
    /// position it does not believe it has.</para>
    /// </summary>
    [Fact]
    public async Task A_flatten_that_could_not_be_confirmed_reads_unresolved_everywhere()
    {
        var (gw, conn, db) = await Flattened(refuseTheClose: true);
        using var _1 = db;
        await using var _2 = gw;

        var record = gw.FlattenToday(conn.Broker.AccountId);
        Assert.NotNull(record);
        log.WriteLine($"why                   : {record.Why}");
        Assert.False(record.Flat);

        var json = Json.Write(await gw.StatusAsync());
        log.WriteLine($"status                : {json}");
        Assert.Contains("\"loss_flatten\":\"unresolved\"", json, StringComparison.Ordinal);

        var line = (await gw.LossTodayAsync()).Line();
        log.WriteLine($"situation             : {line}");
        Assert.Contains("CANNOT CONFIRM", line!, StringComparison.Ordinal);

        var text = DailyReportText.Render(gw.Reports.Compose(Noon.AddMinutes(1)));
        Assert.Contains("CANNOT CONFIRM", text, StringComparison.Ordinal);
        Assert.Equal(1m, conn.Broker.Positions.First(p => p.Symbol == "ES").Quantity);
    }

    /// <summary>
    /// THE SITUATION AND SECTION 4 SAY IT IN WORDS, and neither of them says the old promise.
    ///
    /// <para>"NOTHING WAS CLOSED FOR YOU" was true of <c>U-flatten-1</c> and is now the opposite of
    /// what happens. A sentence left behind after the behaviour under it changed is worse than no
    /// sentence: it is the software telling the owner something it has just stopped doing.</para>
    /// </summary>
    [Fact]
    public async Task The_situation_and_the_report_say_the_positions_were_closed_and_never_the_old_promise()
    {
        var (gw, _, db) = await Flattened();
        using var _1 = db;
        await using var _2 = gw;

        var line = (await gw.LossTodayAsync()).Line();
        log.WriteLine($"situation             : {line}");
        Assert.NotNull(line);
        Assert.Contains("CLOSED YOUR OPEN POSITIONS", line, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING WAS CLOSED FOR YOU", line, StringComparison.Ordinal);

        var text = DailyReportText.Render(gw.Reports.Compose(Noon.AddMinutes(1)));
        var section = text[text.IndexOf("4. Capital and performance", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("5. Execution health", StringComparison.Ordinal)];
        log.WriteLine(section);
        Assert.Contains("CLOSED YOUR OPEN POSITIONS", section, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING WAS CLOSED FOR YOU", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DOCUMENTS AND THE SCREEN, held by name — the lines no test off a running app can reach.
    /// The old promise is asserted ABSENT from each of them, because a revert of this unit that left
    /// the code closing positions and the guide promising it would not is the failure that matters.
    /// </summary>
    [Fact]
    public void The_screen_the_guide_the_contract_the_schema_and_the_agents_instructions_all_say_it_closes()
    {
        var repo = Repo();

        var screen = File.ReadAllText(Path.Combine(repo, "src", "TradeAgent.App", "DashboardView.cs"));
        Assert.Contains("_lossFlattenNote,", screen, StringComparison.Ordinal);
        Assert.Contains("FlattenStateToday()", screen, StringComparison.Ordinal);

        var guide = File.ReadAllText(Path.Combine(repo, "docs", "USER-GUIDE.md"));
        Assert.Contains("TradeAgent closes your open positions", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was closed for you", guide, StringComparison.Ordinal);

        var contracts = File.ReadAllText(Path.Combine(repo, "docs", "CONTRACTS.md"));
        Assert.Contains("loss_flatten", contracts, StringComparison.Ordinal);
        Assert.Contains("op-budget-close-", contracts, StringComparison.Ordinal);
        Assert.Contains("ReductionOnlyOrThrow", contracts, StringComparison.Ordinal);

        var schema = Json.Write(GatewaySchema.Describe());
        Assert.Contains("loss_flatten", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING WAS CLOSED", schema, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "tradeagent-tests", Guid.NewGuid().ToString("n"));
        var home = WorkspaceBuilder.Build(new WorkspaceContext(
            ConnectorName: "Simulator (built in)", ConnectorIsPaper: true, AccountId: "SIM-001",
            Mode: TradingMode.PAPER, ExecutionAvailable: true, ExecutionBlockedReason: null,
            Risk: new RiskPolicy { MaxDailyLoss = 500m, InstrumentAllowlist = ["ES"] }), root);
        var agents = File.ReadAllText(Path.Combine(home, "AGENTS.md"));
        Assert.Contains("loss_flatten", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTHING WAS CLOSED FOR YOU", agents, StringComparison.Ordinal);
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
