using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Platforms;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE WHOLE ORGANISATION LOOP IN ONE PUPPET LIFE (<c>U-loop-e2e</c>; R25 gap 11): every arrow of the MVP loop has its own test, and this
/// one walks them end to end through the real app wiring — <see cref="PuppetLife"/>'s host composition, loop, admission, gate, sessions,
/// the real <c>trade</c> with each launch's own grant, the pipe and the stores — over the app's own paper venue, with puppet seats only:
/// no model, no network. Every arrow is asserted by the app's own records (chart, events, wakes, attempts, runs, promotions, allocations,
/// deployments, publications), never by what a puppet printed it did. The seats play the classes R25 gives them: the chief, the head and
/// the executor on broker seats (mediated; the cell class runs a real codex in a Linux cell, which a puppet cannot be — CellLinuxTests),
/// the legacy pair on their unconfined CLI class, which an active chief keeps asleep.
///
/// <para><b>Not here: simulated fills.</b> The forward runner steps a deployment only on minutes that CLOSE after its start, on the host's
/// wall clock, and the host composes everything on that clock — so a fill would cost real minutes. <c>ForwardRunnerTests</c> drives the
/// runner, the paper venue and its fills on a clock of their own; the run note this life reads is the one its author's stop raises.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrganisationLoopTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    const int Bars = 120, HoldoutAtBar = 60;

    /// <summary>Buys the dip and sells the rip over bars cycling 96 → 105: profitable over the held-back months (RefereeVerdictTests).</summary>
    const string Winning =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\nexit when close > 103\nentry when close < 97\n";

    /// <summary>The same program the other way up: it loses.</summary>
    const string Losing =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\nexit when close < 97\nentry when close > 103\n";

    static string Word(object value) => JsonSerializer.Serialize(value).Replace("\"", "\\\"", StringComparison.Ordinal);

    static string Forecasts() => Word(new[]
    {
        new { predicate = ForecastVocabulary.UnitAdmissible, target = ForecastVocabulary.Admitted,
            by = DateTimeOffset.UtcNow.AddDays(45).ToString("O"), probability = 0.4m }
    });

    /// <summary>A puppet step writing a program into the seat's own home: one line per step string, as the puppet writes the rest.</summary>
    static string Write(string path, string program) => $"write {path} {program}";

    /// <summary>
    /// THE APP'S OWN PAPER VENUE over this start's forward-bar ledger — the binding <c>Connectors.Create</c> makes for <c>paper</c> — with its
    /// book in a file of this life's own (the product's default is one per home, and this assembly shares one home between its classes).
    /// </summary>
    static Func<Database, TradeAgent.ConnectorSdk.ITradingConnector> PaperVenue(string book) => db => new PaperConnector(new PaperConnectorOptions
    {
        Source = new ForwardBarSource(new ForwardBarStore(db)),
        BookFile = book,
        CatalogueNow = () => new VenueStore(db).Catalogue()
    });

    /// <summary>
    /// THE OWNER'S DATA AND HIS HOLDOUT, as the setup's EVIDENCE_HELD press leaves them (<c>U-evidence-campaign</c>) — but with bars this
    /// test writes instead of a download: a BTCUSDT dataset of <see cref="Bars"/> one-minute bars, and his cutoff set through the gateway's
    /// own <see cref="TradingGateway.SetHoldout"/> (the only writer), which opens the campaign verdicts are spent from.
    /// </summary>
    static (DatasetRecord Set, CampaignRow Campaign) OwnersData(TradingGateway gw)
    {
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");
        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < Bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());
        var id = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2026-08"],
            csv, DatasetStore.Sha256(csv)!, Bars, Bar0, Bar0.AddMinutes(Bars - 1), 0, [], false,
            0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]));
        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(HoldoutAtBar), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);
        return (gw.Datasets.ById(id)!, campaign!);
    }

    static List<JsonObject> TradeSteps(PuppetLife life, string role, int turn) =>
        [.. life.Log(role, turn).Where(s => (string?)s["step"] == "trade")];

    static string Said(JsonObject step) => $"{(string?)step["stdout"]}{(string?)step["stderr"]}";

    /// <summary>Every <c>trade</c> step of <paramref name="role"/>'s <paramref name="turn"/>-th turn answered 0, or the failure says which and why.</summary>
    static void Served(PuppetLife life, string role, int turn, int expected)
    {
        var steps = TradeSteps(life, role, turn);
        foreach (var s in steps)
            Assert.True((int?)s["exit"] == 0, $"{role} turn {turn}: 'trade {string.Join(' ', s["args"]!.AsArray().Select(a => (string?)a))}' was refused: {Said(s)}");
        Assert.True(steps.Count == expected, $"{role} turn {turn} ran {steps.Count} trade steps, not {expected}: {string.Join(" | ", life.Log(role, turn).Select(l => l.ToJsonString()))}");
    }

    static int Ended(PuppetLife life, string role) => life.AttemptsOf(role).Count(a => a.State == AiAttemptState.ENDED);

    /// <summary>
    /// THE LOOP, END TO END, AND ITS LOSING BRANCH. A fresh home on the founding board policy (paper) and the app's own paper venue; the
    /// owner's data and holdout; the owner appoints and activates the chief, lets the AI work on its own and wakes the chief once; the chief
    /// charters a division with a share, appoints its head — who activates itself inside the gate and is woken — and asks for its own next
    /// wake; the head charters a team, hires an executor (activated and woken the same way) and assigns it a mandate; the executor writes
    /// two programs in its home, backtests both through the door and delivers them; the head accepts, reproduces both runs and spends two
    /// verdicts: the winner is paper-eligible and allocated to paper by app policy, deployed on the loop's next tick; the loser is refused
    /// and allocated nothing. The notes reach the author; the service restarts mid-turn and loses nothing; the author lists its deployment
    /// and stops it, and the run note reaches it; the owner's words reach the chief; every turn's cost is on the pages' own reads.
    /// </summary>
    [Fact]
    public async Task The_organisation_loop_runs_end_to_end_through_a_restart_and_a_losing_verdict_allocates_nothing()
    {
        var started = DateTimeOffset.UtcNow;
        var book = Path.Combine(TestEnv.Home, $"loop-paper-{Guid.NewGuid():n}.db");
        await using var life = await PuppetLife.Begin("loop", seed: db => ThinkingSeatsTests.ProveCell(db), seatClass: SeatClasses.Broker,
            seatClasses: new Dictionary<string, string> { [CouncilRoles.Operations] = SeatClasses.Cli, [CouncilRoles.Research] = SeatClasses.Cli },
            settings: s =>
            {
                s.SelectedAccountId = PaperConnector.TheAccount;
                // THE OWNER'S RATE, a quarter of the puppet lives' 2/8: at 2/8 one turn at the founding allowance reserves 2.56, and a team
                // that is a quarter of an experiment division that is a quarter of the founding 40 a day holds 2.50 — no turn would fit.
                s.AiPriceInputPerMillion = 0.5m;
                s.AiPriceOutputPerMillion = 2m;
            },
            connector: PaperVenue(book));
        var db = life.Host.Db;
        var org = new OrgStore(db);
        var gw = life.Host.Gateway;

        // ---- 0. A FRESH HOME: the founding policy in force (paper, app-decided), its standing paper envelope granted by the app's own pass.
        var policy = new TradeAgent.Core.Board.BoardStore(db).InForce();
        Assert.Equal((TradingMode.PAPER, false, TradeAgent.Core.Board.BoardDecider.App), (policy.Policy.Mode, policy.Policy.LiveActivated, policy.Decider));
        await life.Host.TradingPassAsync();
        var (set, campaign) = OwnersData(gw);

        // ---- 1. THE OWNER APPOINTS THE CHIEF AND ACTIVATES IT, each his own in-process press: the chief seated on the runtime this host
        // runs (the puppet), then activated through the gate.
        var appointed = life.Host.AppointChief(new ChiefSeat(RuntimeManifest.PuppetId, "scripted-chief"));
        Assert.StartsWith("You appointed the chief", appointed, StringComparison.Ordinal);
        var chief = org.Chief()!;
        var activated = life.Host.ActivateChief(chief);
        Assert.StartsWith($"You activated the chief {chief}", activated, StringComparison.Ordinal);
        Assert.Equal(OrgPositionStatus.Active, org.Position(chief)!.Status);
        var chiefActivationWake = life.Host.Wakes!.OfKind(MissionEventKind.Activated).Any(w => w.Role == chief);

        var collapse = DateTimeOffset.UtcNow.AddDays(60).ToString("O");
        string[] chiefWork =
        [
            $"trade org charter division 0.25 \"one division to find a paper strategy\" --forecasts {Forecasts()} --collapse-by {collapse}",
            $"trade org hire {{out:\"unit\":\\s*\"([^\"]+)\"}} \"a head for the new division\" --head true --forecasts {Forecasts()}",
            "trade org wake 300",
            "usage 400 0 40", "final Chartered a division, appointed its head, and asked to look again in five hours."
        ];
        string[] idle = ["usage 50 0 5", "final Read it."];
        life.Script(chief, chiefActivationWake ? [idle, chiefWork, idle] : [chiefWork, idle]);

        // ---- 2. "LET THE AI WORK ON ITS OWN", THEN "WAKE THE CHIEF ONCE": his two presses, the second bound to what its first showed.
        life.Host.LetTheAiWorkOnItsOwn();
        if (chiefActivationWake)
            await life.RunUntil(() => Ended(life, chief) >= 1, "the chief's activation turn ended");
        var preview = life.Host.PreviewChiefWake();
        var woke = life.Host.WakeChiefOnce(preview);
        Assert.StartsWith($"You woke the chief {chief} once", woke, StringComparison.Ordinal);
        var chiefWorkTurn = chiefActivationWake ? 2 : 1;

        await life.RunUntil(() => Ended(life, chief) >= chiefWorkTurn, "the chief's woken turn ended");
        Served(life, chief, chiefWorkTurn, 3);
        var ownerWake = Assert.Single(life.Host.Wakes.OfKind(MissionEventKind.OwnerWake));
        Assert.Equal((chief, life.AttemptsOf(chief)[chiefWorkTurn - 1].Id), (ownerWake.Role, ownerWake.ConsumedBy));

        // THE CHART: one division under the root, with its share, chartered by the chief; its head hired by the chief and ACTIVE — the
        // activation the hire's own write decided — and one wake to the head and one to the chief.
        var division = Assert.Single(org.Units(), u => u.Kind == OrgUnitKind.Division && u.ParentId == OrgIds.Root && OrgEnvelopes.LegacyRoleOf(u.Id) is null);
        Assert.Equal("0.25", division.EnvelopeShare);
        var head = division.HeadPositionId!;
        Assert.Equal(OrgPositionStatus.Active, org.Position(head)!.Status);
        Assert.Contains(org.DecisionsOf(chief), d => d.Kind == OrgDecisionKind.Charter);
        Assert.Equal(chief, Decider(db, OrgEventKind.Activated, head));
        Assert.Equal(new[] { chief, head }.Order(StringComparer.Ordinal),
            life.Host.Wakes.OfKind(MissionEventKind.Activated).Where(w => w.Payload == head).Select(w => w.Role!).Order(StringComparer.Ordinal));

        // R25 D5: THE CHIEF'S OWN NEXT WAKE, one `self` wake five hours after it asked, in its woken turn, pending.
        var chiefTurn = life.AttemptsOf(chief)[chiefWorkTurn - 1];
        var nextLook = Assert.Single(life.Events(), e => e.Role == chief && e.Kind == MissionEventKind.Self);
        Assert.False(nextLook.Consumed);
        Assert.InRange(nextLook.DueAt, chiefTurn.StartedAt.AddMinutes(300), DateTimeOffset.UtcNow.AddMinutes(300));

        // ---- 3. THE HEAD, WOKEN BY ITS ACTIVATION: charters a team, hires an executor into it, assigns it a mandate — one turn, ids read
        // back from each verb's own answer. The executor's activation wakes it and its hirer; its mandate wakes it too.
        var cutoff = Bar0.AddMinutes(HoldoutAtBar);
        var due = DateTimeOffset.UtcNow.AddDays(7).ToString("O");
        string[] headHire =
        [
            $"trade org charter team 0.25 \"a team to author and test candidates\" --forecasts {Forecasts()} --collapse-by {collapse}",
            $"trade org hire {{out:\"unit\":\\s*\"([^\"]+)\"}} \"an executor to author candidates\" --forecasts {Forecasts()}",
            $"trade mandate assign {{out:\"position\":\\s*\"([^\"]+)\"}} \"author two BTCUSDT programs and backtest each on dataset {set.Id}\" "
            + $"--kind task --shape report --due {due} --allocation 5",
            "usage 300 0 30", "final Staffed a team and assigned the work."
        ];
        life.Script(head, headHire, idle);
        await life.RunUntil(() => Ended(life, head) >= 1, "the head's first turn ended");
        Served(life, head, 1, 3);
        var team = Assert.Single(org.Units(), u => u.ParentId == division.Id);
        var executor = Assert.Single(org.Positions(), p => p.UnitId == team.Id).Id;
        Assert.Equal(OrgPositionStatus.Active, org.Position(executor)!.Status);
        Assert.Equal(head, Decider(db, OrgEventKind.Activated, executor));
        var mandate = Assert.Single(org.MandatesOf(executor)).Id;

        // ---- 4. THE EXECUTOR: two programs in its own home, a backtest of each through the door, delivered with both versions as refs.
        var winner = StrategyParser.Parse(Winning).Program!.StrategyId;
        var loser = StrategyParser.Parse(Losing).Program!.StrategyId;
        var to = cutoff.AddMinutes(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        string Backtest(string file, string from = "") => $"trade backtest --strategy {file} --dataset {set.Id}{from} --to {to} --fees 0.001 --increment 1";
        // THE HEAD'S REPRODUCTION starts ten bars in: a run's id is its version, dataset, window and model, never its role, so the very same
        // ask would be answered with the executor's own run row — and a verdict is spent only on the spender's own completed run.
        var later = $" --from {Bar0.AddMinutes(10).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}";
        var refs = Word(new[] { new { kind = "version", id = winner }, new { kind = "version", id = loser } });
        life.Script(executor,
        [
            Write("trading/dip.strategy", Winning), Write("trading/rip.strategy", Losing),
            Backtest("trading/dip.strategy"), Backtest("trading/rip.strategy"),
            "trade mandate list",
            $"trade mandate deliver {{out:\"mandate\":\\s*\"([^\"]+)\"}} \"two candidates backtested on the research months: dip and rip\" --refs {refs}",
            "signal delivered", "await executor-go", "usage 900 0 90", "final Delivered two candidates."
        ]);
        await life.RunUntil(() => life.Reached("delivered"), "the executor delivered");
        Served(life, executor, 1, 4);
        var runs = gw.Strategies.CompletedRunsOf(winner, executor).Concat(gw.Strategies.CompletedRunsOf(loser, executor)).ToList();
        Assert.Equal(2, runs.Count);
        var executorTurn = Assert.Single(life.AttemptsOf(executor));
        Assert.All(runs, r => Assert.Equal((executor, executorTurn.Id), (r.Role, r.Attempt)));
        Assert.Equal(executor, gw.Strategies.VersionById(winner)!.Role);
        var delivery = org.Deliveries(mandate)[^1];
        Assert.All(org.Checks(delivery.Id), c => Assert.True(c.Passed, $"{c.Check}: {c.Reason}"));

        // ---- 5. THE HEAD ACCEPTS, REPRODUCES BOTH RUNS AND SPENDS TWO VERDICTS. The executor's later turns hang, so the restart below
        // lands in the middle of one.
        life.Script(executor, ["signal executor-hung", "hang"]);
        life.Script(head, headHire,
        [
            $"trade mandate accept {delivery.Id}",
            Write("trading/dip.strategy", Winning), Write("trading/rip.strategy", Losing),
            Backtest("trading/dip.strategy", later), Backtest("trading/rip.strategy", later),
            $"trade verdict --version {winner}", $"trade verdict --version {loser}",
            "usage 700 0 70", "final Accepted, reproduced, judged."
        ]);
        var headTurns = Ended(life, head);
        life.Release("executor-go");
        await life.RunUntil(() => Ended(life, head) > headTurns, "the head's turn on the delivery ended");
        var judged = Ended(life, head);
        Served(life, head, judged, 5);
        await life.RunUntil(() => life.Reached("executor-hung") && gw.Deployments.Open().Count > 0,
            "the executor's turn on its notes began and the loop deployed the winner");
        Assert.Equal(MandateEventKind.Accepted, org.MandateStatus(mandate));

        // THE WINNER: paper-eligible by the campaign's paper arm (frozen after the cutoff), allocated by the app's policy inside the
        // founding envelope, and deployed on the loop's tick. THE LOSER: refused on its figures, and allocated nothing.
        var promotions = new Promotions(db);
        Assert.Equal(PromotionVerdict.PaperEligible, Assert.Single(promotions.For(winner)).Verdict);
        Assert.Equal(PromotionVerdict.Refused, Assert.Single(promotions.For(loser)).Verdict);
        var allocation = Assert.Single(gw.Allocations.PaperStanding(DateTimeOffset.UtcNow)).Allocation;
        Assert.Equal((winner, PaperConnector.ConnectorId, PaperConnector.TheAccount), (allocation.VersionId, allocation.ConnectorId, allocation.AccountId));
        Assert.DoesNotContain(gw.Allocations.PaperStanding(DateTimeOffset.UtcNow), a => a.Allocation.VersionId == loser);
        var deployment = Assert.Single(gw.Deployments.Open());
        Assert.Equal((winner, allocation.Id, DeploymentState.Active), (deployment.VersionId, deployment.AllocationId, deployment.State));

        // THE NOTES REACH THE AUTHOR: the referee's verdict note and the allocator's note name the executor beside research, each with
        // its wake — taken by the executor's turn now hanging.
        var publications = new PublicationStore(db);
        Assert.Contains(publications.By(Referee.RunRole), p => p.RecipientList.Contains(executor));
        Assert.Contains(publications.By(TradingGateway.PaperAllocatorRole), p => p.RecipientList.Contains(executor) && p.Content.Contains("allocated to PAPER", StringComparison.Ordinal));
        var hung = life.AttemptsOf(executor)[^1];
        Assert.Equal(AiAttemptState.LAUNCHED, hung.State);
        var notesTaken = life.Events().Where(e => e.Role == executor && e.ConsumedBy == hung.Id).ToList();
        Assert.NotEmpty(notesTaken);

        // ---- 6. A SERVICE RESTART, MID-TURN. Nothing is lost: the attempts and their costs, the hung turn LOST at its reservation, the
        // chief's pending wake, the deployment — resumed by the new host's own pass.
        var before = life.Attempts().Where(a => a.Id != hung.Id).Select(a => (a.Id, a.State, a.Cost)).ToList();
        await life.RestartMidTurn();
        db = life.Host.Db;
        org = new OrgStore(db);
        gw = life.Host.Gateway;
        publications = new PublicationStore(db);
        await life.Host.TradingPassAsync();

        var lost = new AiAttemptStore(db).Get(hung.Id)!;
        Assert.Equal(AiAttemptState.LOST, lost.State);
        Assert.Equal(hung.ReservedCost, lost.Cost);
        Assert.Equal(before, life.Attempts().Where(a => a.Id != hung.Id).Select(a => (a.Id, a.State, a.Cost)).ToList());
        Assert.False(life.Host.Wakes!.Get(nextLook.Id)!.Consumed);
        var resumed = gw.Deployments.ById(deployment.Id)!;
        Assert.Equal(DeploymentState.Active, resumed.State);

        // ---- 7. R25 D9: THE AUTHOR LISTS ITS DEPLOYMENT AND STOPS IT, on the notes its lost turn took — raised again once — and the run
        // note then reaches it.
        life.Script(executor, ["trade deployment list", $"trade deployment stop {deployment.Id}", "signal executor-stopped", "await executor-read",
            "usage 300 0 30", "final Listed and stopped my paper run."]);
        await life.RunUntil(() => life.Reached("executor-stopped"), "the author's turn after the restart listed and stopped its run");
        var stopTurn = life.Turns(executor);
        Served(life, executor, stopTurn, 2);
        var stopping = life.AttemptsOf(executor)[^1];
        Assert.All(notesTaken, e => Assert.Equal(stopping.Id, life.Host.Wakes.Get(e.Id + ":after-lost")?.ConsumedBy));
        Assert.Contains(deployment.Id, Said(TradeSteps(life, executor, stopTurn)[0]), StringComparison.Ordinal);
        Assert.False(gw.Deployments.ById(deployment.Id)!.IsActive, "the author's stop left its run active");
        life.Script(executor, ["usage 100 0 10", "final Read the run note."]);
        life.Release("executor-read");
        var runNote = MissionEventIds.ForRole(MissionEventIds.PaperRun(deployment.Id, "end"), executor);
        await life.RunUntil(() => life.Host.Wakes.Get(runNote)?.ConsumedBy is { } by
                                  && new AiAttemptStore(db).Get(by)?.State == AiAttemptState.ENDED,
            "the run note reached its author and was read");
        Assert.Contains(publications.By(TradingGateway.PaperAllocatorRole), p => p.RecipientList.Contains(executor)
            && p.Content.Contains("Forward paper run", StringComparison.Ordinal));
        Assert.Contains("Forward paper run", life.Prompt(executor, life.Turns(executor)), StringComparison.Ordinal);

        // ---- 8. R25 D4: THE OWNER'S WORDS REACH THE CHIEF, as data, and its turn reads them.
        const string words = "Keep the division on paper and tell me what it learns.";
        life.Script(chief, ["usage 80 0 8", "final Understood."]);
        var chiefTurns = Ended(life, chief);
        Assert.True(life.Host.RecordOwnerMessage(words));
        await life.RunUntil(() => Ended(life, chief) > chiefTurns, "the chief's turn on the owner's words ended");
        Assert.Contains(words, life.Prompt(chief, life.Turns(chief)), StringComparison.Ordinal);
        var said = Assert.Single(life.Host.Wakes.OfKind(MissionEventKind.Owner), e => e.Payload?.Contains(words, StringComparison.Ordinal) == true);
        Assert.Equal(chief, said.Role);

        // ---- 9. THE COST OF EVERY TURN, RECORDED AND READABLE FROM THE STORES THE PAGES READ: each attempt priced (a lost one at its
        // reservation), and the Costs page's read has the spend under the root, the division and the team.
        var attempts = life.Attempts().Where(a => a.Role is not CouncilRoles.Operations and not CouncilRoles.Research).ToList();
        Assert.All(attempts, a => Assert.True(a.Cost > 0m, $"{a.Role}'s attempt {a.Id} ({a.State}) carries no cost: {a.UnpricedReason}"));
        var costs = CostsRead.Of(org, new AiAttemptStore(db), DateTimeOffset.UtcNow, "");
        foreach (var unit in new[] { OrgIds.Root, division.Id, team.Id })
            Assert.True(costs.Day.Where(d => d.UnitId == unit).Sum(d => d.Totals.Spent) > 0m, $"the Costs page reads no spend under {unit}");
        var funded = new AiAttemptStore(db).FundingOf(executorTurn.Id);
        Assert.Equal(new[] { team.Id, division.Id, OrgIds.Root }.Order(StringComparer.Ordinal), funded.Order(StringComparer.Ordinal));

        output.WriteLine($"loop: {life.Counts()} in {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s");
    }

    static string Decider(Database db, string kind, string position) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT decider FROM org_event WHERE kind = $k AND position_id = $p", ("$k", kind), ("$p", position));
        return (string)c.ExecuteScalar()!;
    });
}
