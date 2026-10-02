using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// EVIDENCE IS BOUND TO WHAT THE EVALUATOR MEANS, NOT TO THE APP'S RELEASE NUMBER — measured where it
/// costs something: a paper run that is running, or that is ended (<c>U-evidence-identity</c>).
///
/// <para><b>What was wrong.</b> <c>Promotions.Standing</c> withdrew a verdict whose
/// <c>interpreter_build</c> was not this build's, and that column reads <c>app=&lt;release&gt;;…</c>.
/// The app updates itself, so every release withdrew every verdict, and the reconcile pass ended every
/// paper run on the strength of it — a false positive on the money path, firing on exactly the event
/// that changes nothing a program means.</para>
///
/// <para><b>What withdraws standing now</b> is the evaluation semantics: the evaluator's version on the
/// promotion, and the language manifest on the version row. A manifest bump also re-identifies the
/// program — the same text hashes to another id — so the runner refuses to step a deployment whose
/// re-parsed id is not the version it was started on, whichever of the two passes reaches it first.</para>
///
/// <para>Everything is measured over <see cref="RecordingConnector"/> and the built-in simulator, in
/// practice mode. Nothing here reaches a venue and no real money is involved.</para>
/// </summary>
public class EvidenceIdentityTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset At = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>What <c>interpreter_build</c> read on rows an earlier release of this app wrote.</summary>
    const string EarlierRelease = "app=0.0.1;language=1";

    /// <summary>A manifest this build does not have: the indicator semantics one version back.</summary>
    const string EarlierManifest = "language=1;indicators=0;calendar=1";

    /// <summary>
    /// A PROGRAM THAT DECIDES ON EVERY BAR AND NEVER TRADES ON THESE: its entry needs a close above 1000
    /// and the bars below close at 100. The three execution bounds are declared because a run that
    /// steps a bar is a run that could dispatch, and the runner ends one that declares none.
    /// </summary>
    const string ProgramText =
        "instrument BTCUSDT\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\nsize fixed 1\n"
        + "exit when close < 90\nentry when close > 1000\n";

    /// <summary>A clock this suite owns and can MOVE, so the bars it writes have closed when it reads them.</summary>
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
        public override long GetTimestamp() => At.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>A gateway on a simulator both witnesses call a simulation, in practice mode.</summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready()
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker { AccountId = "SIM-001" }));
        var clock = new TestClock(At);
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments, "BTCUSDT"];
            s.Risk.MaxOrderQuantity = 100m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    /// <summary>
    /// A VERSION THAT REALLY CARRIES A PAPER-ELIGIBLE VERDICT IN THIS DATABASE — the dataset, the
    /// holdout, the campaign, the version, the holdout run and the promotion row — written the way the
    /// release named by <paramref name="release"/> would have written it.
    ///
    /// <para><paramref name="identifiedUnder"/> is the manifest the version's id is hashed under and
    /// <paramref name="manifestOnRow"/> the one its row carries. They differ only in the arrangement
    /// <see cref="Reidentified"/> explains; everywhere else both are this build's.</para>
    /// </summary>
    static string Judged(Database db, string release, string identifiedUnder, string manifestOnRow)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"evidence-identity-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open("BTCUSDT 1m evidence identity", set, 10, 3, At);
        Assert.True(campaign.Ok, campaign.Why);

        var program = StrategyParser.Parse(ProgramText).Program!;
        var versionId = Sha256Hex.Of($"{program.Canonical}\n{program.Parameters}\n{identifiedUnder}");

        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            versionId, program.Source, program.Canonical, manifestOnRow, release,
            ParseVerdict.Accepted, program.WarmUpBars, Cutoff.AddDays(-1), null, null)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var runId = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null).RunIdFor(versionId);
        strategies.RecordRun(new StrategyRunRow(
            runId, versionId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", versionId, campaign.Campaign!.Id, CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1),
            release, set.Id, set.NormalisedSha256, model.Canonical, Referee.EvaluatorVersion, runId,
            PromotionVerdict.PaperEligible, PromotionReason.MetOnHistory, At)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        return versionId;
    }

    /// <summary>
    /// THE OWNER'S GRANT, THE APP'S ALLOCATION INSIDE IT AND THE APP'S DEPLOYMENT OF IT — every one
    /// written by the call the product uses. Answers the deployment.
    /// </summary>
    static async Task<StrategyDeploymentRow> Deployed(TradingGateway gw)
    {
        var granted = await gw.GrantPaperEnvelopeAsync("BTCUSDT", 5m, null, At.AddDays(30), At);
        Assert.True(granted.Ok, granted.Why);
        Assert.Equal(1, gw.AllocatePaperDue(At));
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        return gw.Deployments.Open().Single();
    }

    /// <summary>
    /// THE STATE AN UPDATE TO A BUILD WITH ANOTHER MANIFEST LEAVES BEHIND, WITH A PAPER RUN IN IT.
    ///
    /// <para>The version is recorded as an earlier build identified it — the same text hashed under
    /// <see cref="EarlierManifest"/> — and is deployed while its row still carries THIS build's
    /// manifest, because nothing in this build will start a run on a version whose evidence it already
    /// withdraws. Then the earlier build's manifest is written into the one column an update leaves
    /// behind: from that statement on, the ledger is exactly what this build finds after replacing a
    /// build whose indicator semantics were one version back — the version's id, its row and its run,
    /// all of that build's, and a running deployment of it.</para>
    /// </summary>
    static async Task<(string Version, StrategyDeploymentRow Deployment)> Reidentified(
        TradingGateway gw, Database db)
    {
        var version = Judged(db, StrategyStore.InterpreterBuild, EarlierManifest, StrategyVersions.Manifest);
        var deployment = await Deployed(gw);

        var written = db.Write(_ =>
        {
            using var c = db.Cmd("UPDATE strategy_version SET manifest=$m WHERE id=$id",
                ("$m", EarlierManifest), ("$id", version));
            return c.ExecuteNonQuery();
        });
        Assert.Equal(1, written);

        return (version, deployment);
    }

    /// <summary>
    /// <paramref name="count"/> CLOSED FORWARD MINUTES AFTER THE RUN STARTED, stored the way the
    /// collector stores them, each closing at 100 — and the clock moved past the last close.
    /// </summary>
    static void Bars(Database db, TestClock clock, int count)
    {
        var store = new ForwardBarStore(db);
        for (var i = 1; i <= count; i++)
        {
            var open = At.AddMinutes(i);
            var close = open + ForwardBars.BarLength;
            var append = store.Append(new ForwardFetchAttempt
            {
                Source = ForwardBars.Source,
                Symbol = "BTCUSDT",
                Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                RequestedAt = close,
                ReceivedAt = close.AddSeconds(1),
                HttpStatus = 200
            }, [new ForwardBars.Kline(open, 100m, 100.5m, 99.5m, 100m, 10m, close)]);
            Assert.Equal(1, append.Stored);
        }

        clock.At = At.AddMinutes(count + 1).AddSeconds(2);
    }

    // ---- (a) a release that changed nothing a program means -------------------------------------

    /// <summary>
    /// (a) A RELEASE WITH UNCHANGED EVALUATION SEMANTICS KEEPS THE VERDICT STANDING AND THE DEPLOYMENT
    /// RUNNING.
    ///
    /// <para>The evidence is what an earlier release wrote: its own <c>app=</c> on the version row and on
    /// the promotion, and the same evaluator and manifest as this build — the ledger of every
    /// installation that has updated since its last verdict. The verdict stands, the app's own policy
    /// allocates and deploys it, and the two passes the app runs after every start, the reconcile and
    /// the runner, leave it running and stepping.</para>
    ///
    /// <para><b>RED on the base</b>, at the first assertion: the standing read <c>invalidated</c> with
    /// "this verdict was computed by interpreter build app=0.0.1;language=1". <b>Mutant (i)</b> — the
    /// <c>interpreter_build</c> compare put back in <c>Invalidation</c> — goes red in the same place.</para>
    /// </summary>
    [Fact]
    public async Task A_release_with_unchanged_evaluation_semantics_keeps_the_verdict_standing_and_the_deployment_running()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;
        var runner = new ForwardRuns(gw, db, () => clock.At);

        var version = Judged(db, EarlierRelease, StrategyVersions.Manifest, StrategyVersions.Manifest);

        var standing = gw.Promotions.Standing(version);
        log.WriteLine($"standing             : {standing.State} — {standing.Why}");
        Assert.Equal(PromotionState.PaperEligible, standing.State);
        Assert.Equal(EarlierRelease, standing.Promotion!.InterpreterBuild);

        var deployment = await Deployed(gw);
        Bars(db, clock, 3);

        await gw.ReconcilePaperDeploymentsAsync(clock.At);
        var afterReconcile = gw.Deployments.ById(deployment.Id)!;
        var states = await runner.AdvanceAsync();
        var afterRunner = gw.Deployments.ById(deployment.Id)!;

        var state = Assert.Single(states);
        log.WriteLine($"after the reconcile  : {afterReconcile.State} — {afterReconcile.EndReason ?? "-"}");
        log.WriteLine($"after the runner     : {afterRunner.State}, {state.BarsReplayed} bars stepped, "
                      + $"ended {state.Ended ?? "no"}");

        Assert.Equal(DeploymentState.Active, afterReconcile.State);
        Assert.Equal(DeploymentState.Active, afterRunner.State);
        Assert.Null(afterRunner.EndReason);
        Assert.Null(state.Ended);
        Assert.Equal(3, state.BarsReplayed);
        Assert.Equal(PromotionState.PaperEligible, gw.Promotions.Standing(version).State);
        await gw.DisposeAsync();
    }

    // ---- (d) a manifest bump --------------------------------------------------------------------

    /// <summary>
    /// (d) A MANIFEST BUMP WITHDRAWS STANDING AND ENDS THE PAPER RUN.
    ///
    /// <para>The version was identified under a manifest this build does not have, so the same text
    /// now means — and hashes to — another program. The verdict is withdrawn, naming the semantics it
    /// was taken under and the ones this build has, and the reconcile pass ends the run on it, as it
    /// ends any run whose verdict no longer stands.</para>
    ///
    /// <para><b>RED on the base</b>: <c>interpreter_build</c> was the only build fact compared and this
    /// row's is this build's, so the standing read <c>paper_eligible</c> and the run went on.
    /// <b>Mutant (iii)</b> — the manifest compare removed from <c>Invalidation</c> — reads exactly the
    /// same, which is the gap the release number used to cover by accident.</para>
    /// </summary>
    [Fact]
    public async Task A_manifest_bump_withdraws_standing_and_ends_the_paper_run()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;

        var (version, deployment) = await Reidentified(gw, db);

        var standing = gw.Promotions.Standing(version);
        log.WriteLine($"standing             : {standing.State} — {standing.Why}");
        Assert.Equal(PromotionState.Invalidated, standing.State);
        Assert.Contains(
            $"the evaluation semantics changed from evaluator {Referee.EvaluatorVersion} with manifest "
            + $"{EarlierManifest} to {EvaluationSemantics.Current}", standing.Why, StringComparison.Ordinal);

        await gw.ReconcilePaperDeploymentsAsync(clock.At);
        var ended = gw.Deployments.ById(deployment.Id)!;
        log.WriteLine($"deployment           : {ended.State} — {ended.EndReason ?? "-"}");

        Assert.Equal(DeploymentState.Ended, ended.State);
        Assert.Equal("the version's verdict no longer stands as promoted or paper-eligible", ended.EndReason);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE RUNNER WILL NOT STEP A VERSION WHOSE TEXT NOW READS AS ANOTHER PROGRAM, whichever pass
    /// reaches the run first.
    ///
    /// <para><c>ForwardRuns.Frozen</c> re-parses the recorded source on every pass, and before this unit
    /// took whatever came out. Under another manifest that is a DIFFERENT program — another id, other
    /// indicator arithmetic — stepped and dispatched under the deployment of the one that was judged.
    /// Here the runner reaches the run before any reconcile pass does, and ends it before a single bar
    /// is stepped, saying why.</para>
    ///
    /// <para><b>RED on the base</b>: the runner stepped all three bars and the run stayed active.</para>
    /// </summary>
    [Fact]
    public async Task A_run_whose_text_now_reads_as_another_program_is_ended_by_the_runner_before_it_steps()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;
        var runner = new ForwardRuns(gw, db, () => clock.At);

        var (_, deployment) = await Reidentified(gw, db);
        Bars(db, clock, 3);

        var states = await runner.AdvanceAsync();
        var ended = gw.Deployments.ById(deployment.Id)!;

        var state = Assert.Single(states);
        log.WriteLine($"runner               : {state.BarsReplayed} bars stepped, ended {state.Ended ?? "no"}");
        log.WriteLine($"deployment           : {ended.State} — {ended.EndReason ?? "-"}");

        Assert.Equal(0, state.BarsReplayed);
        Assert.NotNull(state.Ended);
        Assert.Contains("reads it as a different program with a different id", state.Ended, StringComparison.Ordinal);
        Assert.Equal(DeploymentState.Ended, ended.State);
        Assert.Equal(state.Ended, ended.EndReason);
        await gw.DisposeAsync();
    }
}
