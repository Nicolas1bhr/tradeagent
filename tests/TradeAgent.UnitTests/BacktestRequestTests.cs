using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 5 — THE REQUEST, WHAT IT WILL NOT READ, AND WHAT THE OWNER'S REPORT SAYS ABOUT IT.
///
/// <para><b>The containment check is the one with teeth.</b> Without it the `backtest` op is a general
/// file-read primitive for anything the app's own process can open — the database, the IPC token, the
/// owner's inbox — because a parse refusal echoes the line it failed on. So the resolved path is
/// required to be inside a role home, AFTER normalisation, which is the only order in which
/// <c>../../state/tradeagent.db</c> is caught.</para>
///
/// <para><b>And section 8 of the owner's report.</b> Its "nothing has been backtested" line used to
/// disappear as soon as a DATASET existed, so an installation that had collected twelve months and
/// measured nothing with them read as though it had evidence. It goes when a RUN exists and at no
/// other time. A run is listed under "measured by TradeAgent" because the app computed every figure in
/// it from its own trace; an agent's own account of a backtest is a publication and stays on the other
/// side of that line — which is the mutant this class was watched against.</para>
/// </summary>
public class BacktestRequestTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset At = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const string Text = """
        instrument BTCUSDT
        size fixed 1
        exit when close < 97
        entry when close > 103
        """;

    /// <summary>Writes a program into one role's own folder and answers the path the agent would type.</summary>
    static string GivenProgram(string role = CouncilRoles.Operations, string text = Text, string name = "x.strategy")
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text);
        return Path.Combine("strategies", name);
    }

    /// <summary>A dataset the ledger really recorded, with a sawtooth that crosses 103 every ten minutes.</summary>
    static DatasetRecord GivenData(Database db, int bars = 120, string pair = "BTCUSDT")
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new System.Text.StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"{At.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, At, At.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, At, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, At, KlineTimeUnit.Microseconds, raw)]);

        return record with { Id = new DatasetStore(db).Record(record) };
    }

    /// <summary>
    /// A PROGRAM IS READ FROM INSIDE A ROLE'S FOLDER, AND THE ROLE IS THE FOLDER IT WAS IN.
    ///
    /// <para>The role is a MEASUREMENT — which home the file was actually under — and not a claim the
    /// caller made, because the pipe does not yet carry an authenticated role and a role an agent could
    /// name is a role it could name somebody else's.</para>
    /// </summary>
    [Fact]
    public async Task A_program_in_a_roles_own_folder_runs_and_the_run_is_recorded_against_that_role()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var set = GivenData(db);

        var ran = gw.Backtests.Run(new BacktestAsk(
            GivenProgram(CouncilRoles.Research, name: "research-only.strategy"), set.Id, Fees: 0.001m));

        Assert.Equal(CouncilRoles.Research, ran.Role);
        Assert.Equal(set.Id, ran.Result.Request.DatasetId);
        Assert.NotEmpty(ran.Result.Trades);

        var version = gw.Strategies.VersionById(ran.Program.StrategyId);
        Assert.NotNull(version);
        Assert.Equal(CouncilRoles.Research, version.Role);
        Assert.Null(version.Attempt);                       // the pipe carries no attested attempt yet

        var run = gw.Strategies.RunById(ran.Result.RunId);
        Assert.NotNull(run);
        Assert.Equal(ran.Program.StrategyId, run.VersionId);
        Assert.Equal(set.NormalisedSha256, run.DatasetSha256);
        Assert.Equal("fees=0.001;slippage=0;increment=1;capital=10000", run.ExecutionModel);
        Assert.Equal(ran.Result.Trace.Sha256, run.TraceSha256);
        Assert.Equal(ran.Result.Trades.Count, gw.Strategies.TradesOf(ran.Result.RunId).Count);

        // The same request again is the same run: one row, not two.
        gw.Backtests.Run(new BacktestAsk(
            Path.Combine("strategies", "research-only.strategy"), set.Id, Fees: 0.001m));
        Assert.Equal(1, gw.Strategies.RunCount);
    }

    /// <summary>
    /// A PATH OUTSIDE EVERY ROLE FOLDER IS REFUSED, ABSOLUTE OR CLIMBING OUT.
    ///
    /// <para>The database, the IPC token and the owner's inbox are all files this process can open, and
    /// a parse refusal echoes the line it failed on — so an op that read any path would hand their
    /// contents back. Each of these is refused before the file is touched.</para>
    /// </summary>
    [Theory]
    [InlineData("../../state/tradeagent.db")]
    [InlineData("../inbox/the-owners-file.pdf")]
    [InlineData("strategies/../../../ipc.token")]
    public async Task A_path_that_leaves_the_role_folder_is_refused_before_anything_is_read(string path)
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var set = GivenData(db);

        var refused = Assert.Throws<GatewayDeniedException>(
            () => gw.Backtests.Run(new BacktestAsk(path, set.Id)));

        log.WriteLine(refused.Message);
        Assert.Equal(ErrorCode.INVALID_REQUEST, refused.Code);
        Assert.Contains("outside your role folder", refused.Message);
    }

    /// <summary>The same rule for an absolute path, including one the app's own state lives at.</summary>
    [Fact]
    public async Task An_absolute_path_outside_the_role_folders_is_refused_even_when_the_file_is_there()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var set = GivenData(db);

        // A real, readable file, outside every role home.
        var elsewhere = Path.Combine(Paths.State, "not-a-strategy.txt");
        File.WriteAllText(elsewhere, Text);

        var refused = Assert.Throws<GatewayDeniedException>(
            () => gw.Backtests.Run(new BacktestAsk(elsewhere, set.Id)));
        Assert.Contains("outside your role folder", refused.Message);

        // And a sibling directory whose name merely starts with a role home's is not inside it.
        var sibling = Paths.RoleHome(CouncilRoles.Research) + "-elsewhere";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "x.strategy"), Text);
        Assert.Contains("outside your role folder", Assert.Throws<GatewayDeniedException>(
            () => gw.Backtests.Run(new BacktestAsk(Path.Combine(sibling, "x.strategy"), set.Id))).Message);
    }

    /// <summary>
    /// A TEXT THAT IS NOT A PROGRAM IS REFUSED AND THE REFUSAL NAMES THE LINE.
    ///
    /// <para>The program was written by a cheap model and the refusal is what it reads to fix it, so the
    /// line number is the whole value of the message. Nothing is recorded: a refusal has no canonical
    /// form and therefore no id to key a row by.</para>
    /// </summary>
    [Fact]
    public async Task A_text_that_is_not_a_program_is_refused_naming_the_line_and_nothing_is_recorded()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var set = GivenData(db);

        var refused = Assert.Throws<GatewayDeniedException>(() => gw.Backtests.Run(new BacktestAsk(
            GivenProgram(text: """
                instrument BTCUSDT
                size fixed 1
                indicator fastma = sma(close, 0)
                exit when close < 97
                entry when close > fastma
                """, name: "broken.strategy"), set.Id)));

        log.WriteLine(refused.Message);
        Assert.Contains("line 3", refused.Message);
        Assert.Empty(gw.Strategies.AllVersions());
        Assert.Equal(0, gw.Strategies.RunCount);
    }

    /// <summary>An execution model that cannot mean what it says is refused before any bar is read.</summary>
    [Fact]
    public async Task An_execution_model_that_cannot_mean_what_it_says_is_refused()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var set = GivenData(db);

        var refused = Assert.Throws<GatewayDeniedException>(() => gw.Backtests.Run(
            new BacktestAsk(GivenProgram(), set.Id, Fees: 0.1m)));

        Assert.Contains("basis points", refused.Message);
        Assert.Equal(0, gw.Strategies.RunCount);
    }

    /// <summary>
    /// ONE RUN AT A TIME PER ROLE, AND IT IS REFUSED RATHER THAN QUEUED.
    ///
    /// <para>The second request is made from INSIDE the first one — the runner's own clock hook is
    /// called while the guard is held — so this is the real reentrancy and not two threads hoping to
    /// collide. Refused rather than queued because a run is unbounded work on the caller's own
    /// connection and queueing would hold a pipe handler open behind it.</para>
    /// </summary>
    [Fact]
    public async Task A_second_run_for_the_same_role_while_one_is_in_flight_is_refused()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var set = GivenData(db);
        var ask = new BacktestAsk(GivenProgram(), set.Id);

        Backtests? runner = null;
        string? refusal = null;

        runner = new Backtests(gw, db, () =>
        {
            if (refusal is null)
                try { runner!.Run(ask); }
                catch (GatewayDeniedException ex) { refusal = ex.Message; }
            return At;
        });

        runner.Run(ask);

        Assert.NotNull(refusal);
        log.WriteLine(refusal);
        Assert.Contains("already running", refusal);
        Assert.Contains("One at a time per role", refusal);

        // And the guard is released afterwards: the next request runs.
        Assert.NotNull(runner.Run(ask).Result);
    }

    /// <summary>
    /// SECTION 8 LISTS A RUN AS MEASURED BY TRADEAGENT, AND THE GAP LINE GOES ONLY WHEN ONE EXISTS.
    ///
    /// <para>A dataset is not evidence. With twelve months collected and nothing run, the report still
    /// says nothing has been backtested — which is the state of the installation.</para>
    /// </summary>
    [Fact]
    public async Task Section_eight_says_nothing_has_been_backtested_until_a_run_exists()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var set = GivenData(db);

        var before = gw.Reports.Compose(DateTimeOffset.Now);
        Assert.Contains(before.Research.Missing, g => g.Why.Contains("nothing has been backtested"));
        Assert.Contains(before.Research.AppMetrics, m => m.Contains("BTCUSDT"));   // the dataset IS listed

        gw.Backtests.Run(new BacktestAsk(GivenProgram(), set.Id, Fees: 0.001m));

        var after = gw.Reports.Compose(DateTimeOffset.Now);
        var text = DailyReportText.Render(after);
        log.WriteLine(text[(text.IndexOf("8. Research evidence", StringComparison.Ordinal))..]);

        Assert.DoesNotContain(after.Research.Missing, g => g.Why.Contains("nothing has been backtested"));
        Assert.Contains(after.Research.AppMetrics, m => m.StartsWith("backtest ", StringComparison.Ordinal));
        Assert.Contains("measured by TradeAgent", text);
        Assert.Contains("backtest ", text);
    }

    /// <summary>
    /// AN AGENT'S OWN ACCOUNT OF A BACKTEST IS A CLAIM AND IS LISTED AS ONE.
    ///
    /// <para>The two lists are the ledger rule applied to research: what TradeAgent measured and what
    /// somebody said. A publication of the Research Director's — including one that reports a backtest
    /// in words — belongs in "claimed by an agent", and moving it up into the measured list is the
    /// mutant this test was watched against.</para>
    /// </summary>
    [Fact]
    public async Task A_publication_is_listed_as_claimed_and_never_among_the_figures_the_app_measured()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var set = GivenData(db);
        gw.Backtests.Run(new BacktestAsk(GivenProgram(), set.Id));

        new PublicationStore(db).Commit(new Publication
        {
            Id = Sha256Hex.Of("my strategy makes 40% a year"),
            Role = CouncilRoles.Research,
            Kind = PublicationKind.Report,
            Recipients = CouncilRoles.Operations,
            CreatedAt = DateTimeOffset.Now,
            Content = "my strategy makes 40% a year"
        }, DateTimeOffset.Now);

        var report = gw.Reports.Compose(DateTimeOffset.Now);

        // The load-bearing one first: what an agent said is not among what the app measured.
        Assert.DoesNotContain(report.Research.AppMetrics, m => m.Contains("Research Director published"));
        Assert.Contains(report.Research.AgentClaims, c => c.Contains("Research Director published"));
        Assert.Contains(report.Research.AppMetrics, m => m.StartsWith("backtest ", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE OP IS ON THE READ SIDE OF THE FENCE, AND IT IS IN THE TABLES THAT SAY SO.
    ///
    /// <para>Not in <c>Ops.Mutating</c> — that word on this channel means "sends something to a broker"
    /// — and in the drain table at zero, because it makes no connector call at all. And it is in the
    /// schema, with what a backtest cannot prove said where an agent reads the surface.</para>
    /// </summary>
    [Fact]
    public void The_backtest_op_is_not_mutating_is_in_the_schema_and_says_what_it_cannot_prove()
    {
        Assert.False(Ops.IsMutating(Ops.Backtest));
        Assert.DoesNotContain(Ops.Backtest, Ops.Mutating);

        var spec = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.Backtest);
        Assert.False(spec.Mutating);
        Assert.Contains("trade backtest", spec.Cli);
        Assert.Contains("INSIDE YOUR OWN ROLE FOLDER", spec.Description);
        Assert.Contains("strategy", spec.Args.Select(a => a.Name));
        Assert.Contains("dataset", spec.Args.Select(a => a.Name));

        // What a run is not, said on the surface rather than only in the op's own description.
        var described = Json.Write(GatewaySchema.Describe());
        Assert.Contains("no queue position", described);
        Assert.Contains("never a record of a trade", described);
    }
}
