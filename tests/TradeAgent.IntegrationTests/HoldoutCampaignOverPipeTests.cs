using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A CAMPAIGN IS ABOUT MARKET TIME FOR EVERY PAIR (<c>U-holdout-campaign</c>; seat A's decision of 2026-10-09): research over
/// its development months is charged to it through any dataset of any pair, and a second press over months already held
/// shares the judgements taken over them.
///
/// <para><b>What was wrong before this.</b> A run charged the campaign over its OWN dataset alone (<c>Backtests.Run</c>'s
/// <c>OpenForDataset(ask.Dataset)</c>), while every Download press records a version of the pair with no cutoff. The
/// survey's probe (<c>s-holdout-campaign</c>, RED at <c>ea88e72b</c>) ran research over A's development minutes through B
/// (Q1.3) and through C (Q1.4) past A's spent budget, charged to no campaign; and a second press on B over A's window
/// opened campaign 2 with no judgement spent, so one version was judged twice over one held hour (S.5-S.9).</para>
///
/// <para>The ledger every test reads: A is BTCUSDT 1m v1, 120 minutes from 2026-08-01 00:00Z, cut at 01:00 by the owner's own
/// press (<c>TradingGateway.SetHoldout</c>, which opens campaign 1), so its development months are [00:00, 01:00) and its
/// window [01:00, 02:00). Other datasets are recorded by each test with no cutoff, as a second Download press records
/// one.</para>
/// </summary>
public class HoldoutCampaignOverPipeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A's cutoff, minute 60.</summary>
    static readonly DateTimeOffset Cutoff = Start.AddMinutes(60);

    /// <summary>The last minute of A's development months: a run that ends here closes its last bar at the cutoff.</summary>
    static readonly DateTimeOffset LastDev = Cutoff.AddMinutes(-1);

    /// <summary>The close of A's last bar, minute 120: where its window ends.</summary>
    static readonly DateTimeOffset WindowEnd = Start.AddMinutes(120);

    /// <summary>The wire by hand, as <c>HoldoutLaterOverPipeTests</c> speaks it: the machine's key on every frame, the grant on the hello.</summary>
    sealed class Raw : IAsyncDisposable
    {
        NamedPipeClientStream _pipe = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<Raw> ConnectAsync(string pipeName)
        {
            var c = new Raw { _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._pipe.ConnectAsync(5000);
            c._r = new StreamReader(c._pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> SendAsync(IpcRequest req)
        {
            req.Token ??= IpcToken.Ensure();
            await _w.WriteLineAsync(Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            return Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        public ValueTask DisposeAsync()
        {
            _r.Dispose();
            _w.Dispose();
            return _pipe.DisposeAsync();
        }
    }

    /// <summary>A gateway serving the pipe, A, and campaign 1 the owner's press opened over it.</summary>
    sealed record Rig(TradingGateway Gw, Database Db, GatewayPipeServer Server, AgentGrants Grants, string Pipe,
        DatasetRecord A, CampaignRow Campaign) : IAsyncDisposable
    {
        public async Task<Raw> Dial(string role)
        {
            var client = await Raw.ConnectAsync(Pipe);
            var grant = Grants.Issue(role, "attempt-holdout-campaign").Token;
            var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
            Assert.True(hello.Ok, Json.Write(hello.Error));
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Db.Dispose();
        }
    }

    static async Task<Rig> Ready(int trials, int verdicts = 3)
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = trials;
            s.CampaignExplorationBudget = trials;
            s.CampaignVerdictBudget = verdicts;
        });
        var pipe = "ta-hc-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        // A: THE OWNER'S FIRST PRESS — the cutoff and its campaign in one transaction.
        var a = Recorded(db, "BTCUSDT", "v1", Start, 120);
        var (done, campaign) = gw.SetHoldout(a.Id, Cutoff, EvaluationClass.Research);
        Assert.True(done.Ok, done.Why);
        Assert.NotNull(campaign);

        return new Rig(gw, db, server, grants, pipe, gw.Datasets.ById(a.Id)!, gw.Campaigns.ById(campaign!.Id)!);
    }

    /// <summary>A dataset the ledger recorded, hashes and all, with no cutoff: <paramref name="bars"/> 1m bars from <paramref name="from"/>, closes cycling 96 → 105.</summary>
    static DatasetRecord Recorded(Database db, string pair, string version, DateTimeOffset from, int bars)
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + (i + 600) % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{from.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, version, 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, from, from.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, from, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, from, KlineTimeUnit.Microseconds, raw)]));
        return store.ById(id)!;
    }

    /// <summary>
    /// A PROGRAM IN THE RESEARCH DIRECTOR'S OWN FOLDER, its version frozen at <see cref="Start"/> — before any window
    /// begins, so the scoring policy's forward-evidence clause can be met. The freshness only varies the text and so the
    /// version id; the shape is the judgeable one <c>VerdictOverPipeTests</c> uses, over bars cycling 96 → 105.
    /// </summary>
    static (string File, string Version) Written(Database db, string freshness, string instrument = "BTCUSDT")
    {
        var source = $"instrument {instrument}\nsize fixed 1\ntimeframe 1m\ndata_freshness {freshness}\n"
            + "max_decision_age 30s\nexit when close > 103\nentry when close < 97\n";
        var parsed = StrategyParser.Parse(source).Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            Start, CouncilRoles.Research, "attempt-holdout-campaign"));

        var dir = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(dir);
        var name = $"holdout-campaign-{Guid.NewGuid():n}.strategy";
        File.WriteAllText(Path.Combine(dir, name), source);
        return ("strategies/" + name, parsed.StrategyId);
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    static Task<IpcResponse> Backtest(Raw client, string file, DatasetRecord set, DateTimeOffset from, DateTimeOffset to) =>
        client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "holdout-campaign", RequestId = "hc-bt-" + Guid.NewGuid().ToString("n")[..12],
            Args = Args(("strategy", file), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)),
                ("from", Iso(from)), ("to", Iso(to)), ("increment", "1"))
        });

    /// <summary>What a reply said, for the log: "REFUSED code: message" or "SERVED run … bars …".</summary>
    static string Said(IpcResponse r)
    {
        if (!r.Ok) return $"REFUSED {r.Error?.Code}: {r.Error?.Message}";

        var d = Data(r);
        if (d.TryGetProperty("metrics", out var metrics))
            return $"SERVED run {d.GetProperty("run_id").GetString()![..12]} over dataset {d.GetProperty("dataset_id")}: "
                + $"metrics.bars={metrics.GetProperty("bars")}";
        return "SERVED " + d;
    }

    static string RunId(IpcResponse r) => Data(r).GetProperty("run_id").GetString()!;

    /// <summary>The words a refusal through another dataset must carry: the campaign, its months, and the dataset read.</summary>
    static void NamesTheCampaignItsMonthsAndTheDatasetRead(IpcResponse reply, CampaignRow campaign, DatasetRecord held,
        DatasetRecord read, int budget)
    {
        Assert.False(reply.Ok, $"research over campaign {campaign.Id}'s months through dataset {read.Id} was served past its "
            + $"spent budget: {Said(reply)}");
        Assert.Equal(nameof(ErrorCode.CAMPAIGN_BUDGET_REACHED), reply.Error!.Code);
        var why = reply.Error.Message;
        Assert.StartsWith($"campaign {campaign.Id} has registered all {budget} of its research trials, so this run is "
            + "refused before it is made.", why, StringComparison.Ordinal);
        Assert.Contains($"This run reads dataset {read.Id} ({read.Pair} 1m {read.Version})", why, StringComparison.Ordinal);
        Assert.Contains($"the development months of campaign {campaign.Id}: the bars of dataset {held.Id} "
            + $"({held.Pair} 1m {held.Version}) from {Start:u} up to its cutoff at {Cutoff:u}", why, StringComparison.Ordinal);
        Assert.Contains("charged to it through any dataset of any pair", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// (a) RESEARCH OVER A CAMPAIGN'S MONTHS THROUGH ANOTHER DATASET IS CHARGED TO IT — the probe's Q1.3 and Q1.4 over the
    /// pipe, asserting the protection. With trials left, a run over B (BTCUSDT v2, A's minutes) and one over C (ETHUSDT, the
    /// same minutes) inside A's development months are each charged to campaign 1, once — asking the same run again is the
    /// same trial. Spent, the next of each is refused before it is made with <c>CAMPAIGN_BUDGET_REACHED</c> naming campaign
    /// 1, its months and the dataset asked for, and nothing runs. RED on the base, where both were served and charged to no
    /// campaign.
    /// </summary>
    [Fact]
    public async Task Research_over_a_campaigns_months_through_another_dataset_is_charged_to_it()
    {
        await using var rig = await Ready(trials: 2);
        var c = Recorded(rig.Db, "ETHUSDT", "v1", Start, 120);
        var b = Recorded(rig.Db, "BTCUSDT", "v2", Start, 120);
        var campaign = rig.Campaign;
        await using var research = await rig.Dial(CouncilRoles.Research);

        // BUDGET LEFT: B, then the same run again, then C — each charged to campaign 1, once.
        var p2 = Written(rig.Db, "3m");
        var q13 = await Backtest(research, p2.File, b, Start, LastDev);
        var afterB = rig.Gw.Campaigns.TrialsCharged(campaign.Id);
        log.WriteLine($"Q1.3 backtest P2 over B [{Iso(Start)}, {Iso(LastDev)}]: {Said(q13)}; campaign {campaign.Id} trials charged 0 -> {afterB} of 2");
        Assert.True(q13.Ok, Json.Write(q13.Error));
        Assert.True(afterB == 1, $"a run over dataset {b.Id} inside campaign {campaign.Id}'s development months was charged "
            + $"to it {afterB} time(s), not once: {Said(q13)}");
        var trial = Assert.Single(rig.Gw.Campaigns.Trials(campaign.Id));
        Assert.Equal(RunId(q13), trial.RunId);
        Assert.Equal(p2.Version, trial.VersionId);
        Assert.Equal(EvaluationClass.Research, trial.Kind);
        Assert.True(trial.Charged);

        var again = await Backtest(research, p2.File, b, Start, LastDev);
        log.WriteLine($"Q1.3 again, the same run: {Said(again)}; trials charged {rig.Gw.Campaigns.TrialsCharged(campaign.Id)}");
        Assert.True(again.Ok, Json.Write(again.Error));
        Assert.Equal(RunId(q13), RunId(again));
        Assert.Equal(1, rig.Gw.Campaigns.TrialsCharged(campaign.Id));

        var p3 = Written(rig.Db, "4m", "ETHUSDT");
        var q14 = await Backtest(research, p3.File, c, Start, LastDev);
        log.WriteLine($"Q1.4 backtest P3 (ETHUSDT) over C, the same minutes: {Said(q14)}; trials charged {rig.Gw.Campaigns.TrialsCharged(campaign.Id)} of 2");
        Assert.True(q14.Ok, Json.Write(q14.Error));
        Assert.Equal(2, rig.Gw.Campaigns.TrialsCharged(campaign.Id));
        Assert.Equal(2, rig.Gw.Campaigns.Trials(campaign.Id).Count);

        // SPENT: through B and through C alike, refused before anything runs, in words naming the campaign, its months and
        // the dataset asked for.
        var runsBefore = rig.Gw.Strategies.Runs().Count;
        var p4 = Written(rig.Db, "5m");
        var spentB = await Backtest(research, p4.File, b, Start, LastDev);
        log.WriteLine($"Q1.3 spent, backtest P4 over B: {Said(spentB)}");
        NamesTheCampaignItsMonthsAndTheDatasetRead(spentB, campaign, rig.A, b, 2);

        var p5 = Written(rig.Db, "6m", "ETHUSDT");
        var spentC = await Backtest(research, p5.File, c, Start, LastDev);
        log.WriteLine($"Q1.4 spent, backtest P5 (ETHUSDT) over C: {Said(spentC)}");
        NamesTheCampaignItsMonthsAndTheDatasetRead(spentC, campaign, rig.A, c, 2);

        Assert.Equal(runsBefore, rig.Gw.Strategies.Runs().Count);
        Assert.Equal(2, rig.Gw.Campaigns.TrialsCharged(campaign.Id));
        Assert.Null(rig.Gw.Campaigns.OpenForDataset(b.Id));
        Assert.Null(rig.Gw.Campaigns.OpenForDataset(c.Id));
    }

    /// <summary>
    /// (b) A RUN OUTSIDE EVERY CAMPAIGN'S MONTHS CHARGES NONE — and is served with campaign 1's budget spent. B is BTCUSDT
    /// v2 from an hour before A's first bar to an hour after its window: a run over B after A's window closes, and one
    /// whose last bar closes as A's first opens, read none of A's development months. A FIXTURE run inside them — over F,
    /// ETHUSDT pressed as fixture bars — is registered under its own campaign uncharged and under no other. And a campaign
    /// over fixture bars, or over a REJECTED dataset, is charged through its own dataset only: the run before A's first
    /// bar reads F's and R's development months and is charged to neither.
    /// </summary>
    [Fact]
    public async Task A_run_outside_every_campaigns_months_charges_none()
    {
        await using var rig = await Ready(trials: 1);
        var campaign = rig.Campaign;
        await using var research = await rig.Dial(CouncilRoles.Research);

        // CAMPAIGN 1 SPENT, through its own dataset.
        var p1 = Written(rig.Db, "2m");
        var own = await Backtest(research, p1.File, rig.A, Start, LastDev);
        Assert.True(own.Ok, Json.Write(own.Error));
        Assert.Equal(1, rig.Gw.Campaigns.TrialsCharged(campaign.Id));

        // F: fixture bars over [-60, 120), held at 01:50 as FIXTURE — its months [-60, 01:50), its window [01:50, 02:00).
        var f = Recorded(rig.Db, "ETHUSDT", "v1", Start.AddMinutes(-60), 180);
        var (fixtureHeld, fixture) = rig.Gw.SetHoldout(f.Id, Start.AddMinutes(110), EvaluationClass.Fixture);
        Assert.True(fixtureHeld.Ok, fixtureHeld.Why);
        // R: research bars over [-60, 120), held at 01:55 and then REJECTED — its months [-60, 01:55).
        var r = Recorded(rig.Db, "SOLUSDT", "v1", Start.AddMinutes(-60), 180);
        var (rejectedHeld, rejected) = rig.Gw.SetHoldout(r.Id, Start.AddMinutes(115), EvaluationClass.Research);
        Assert.True(rejectedHeld.Ok, rejectedHeld.Why);
        rig.Gw.Datasets.Reject(r.Id, "a file this ledger measured changed on disk (a test's own rejection)");
        // B: BTCUSDT v2 from an hour before A's first bar to an hour after its window.
        var b = Recorded(rig.Db, "BTCUSDT", "v2", Start.AddMinutes(-60), 240);

        var p2 = Written(rig.Db, "3m");
        foreach (var (what, from, to) in new[]
                 {
                     ("after A's window closes", WindowEnd, WindowEnd.AddMinutes(59)),
                     ("ending as A's first bar opens", Start.AddMinutes(-60), Start.AddMinutes(-1))
                 })
        {
            var ran = await Backtest(research, p2.File, b, from, to);
            log.WriteLine($"backtest P2 over B {what} [{Iso(from)}, {Iso(to)}]: {Said(ran)}; trials: campaign {campaign.Id}="
                + $"{rig.Gw.Campaigns.TrialsCharged(campaign.Id)}, fixture campaign {fixture!.Id}={rig.Gw.Campaigns.Trials(fixture.Id).Count} rows, "
                + $"rejected campaign {rejected!.Id}={rig.Gw.Campaigns.Trials(rejected.Id).Count} rows");
            Assert.True(ran.Ok, Json.Write(ran.Error));
        }

        // THE FIXTURE RUN, inside A's development months.
        var p3 = Written(rig.Db, "4m", "ETHUSDT");
        var plumbing = await Backtest(research, p3.File, f, Start, LastDev);
        log.WriteLine($"fixture backtest P3 over F [{Iso(Start)}, {Iso(LastDev)}]: {Said(plumbing)}");
        Assert.True(plumbing.Ok, Json.Write(plumbing.Error));

        Assert.Equal(1, rig.Gw.Campaigns.TrialsCharged(campaign.Id));
        Assert.Single(rig.Gw.Campaigns.Trials(campaign.Id));
        var uncharged = Assert.Single(rig.Gw.Campaigns.Trials(fixture!.Id));
        Assert.Equal(RunId(plumbing), uncharged.RunId);
        Assert.Equal(EvaluationClass.Fixture, uncharged.Kind);
        Assert.False(uncharged.Charged);
        Assert.Equal(0, rig.Gw.Campaigns.TrialsCharged(fixture.Id));
        Assert.Empty(rig.Gw.Campaigns.Trials(rejected!.Id));
    }

    static Task<IpcResponse> Verdict(Raw client, string version, DatasetRecord set) => client.SendAsync(new IpcRequest
    {
        Op = Ops.Verdict, Session = "holdout-campaign", RequestId = "hc-v-" + Guid.NewGuid().ToString("n")[..12],
        Args = Args(("version", version), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
    });

    /// <summary>
    /// (d) A SECOND CAMPAIGN OVER HELD MONTHS COUNTS THE JUDGEMENTS ALREADY TAKEN — the probe's S.1-S.9 over the pipe,
    /// asserting the protection. P1 is researched over A and judged (campaign 1, 1 of 2). B, a second download of A's
    /// minutes, is held back by the owner's second press at the same date: campaign 2 opens with 1 of its 2 judgements
    /// already spent, and the press's note — <c>CampaignStore.PressNote</c>, the sentence the card prints — names campaign
    /// 1 and the judgement it took. P1 researched over B and judged there answers <c>verdicts_spent</c> 2 of 2, and both
    /// campaigns now count both judgements; P2's verdict over B is refused before anything is computed, naming campaign 1
    /// and OTHER months. TOLD and COUNTED: the press itself is never refused. RED on the base, where campaign 2's count
    /// read its own lineage alone — 0 at the press, "1 of 2" after its verdict, and P2 judged as well.
    /// </summary>
    [Fact]
    public async Task A_second_campaign_over_held_months_counts_the_judgements_already_taken()
    {
        await using var rig = await Ready(trials: 20, verdicts: 2);
        var c1 = rig.Campaign;
        await using var research = await rig.Dial(CouncilRoles.Research);

        // S.1-S.2 — RESEARCH OVER A, AND ITS JUDGEMENT.
        var p1 = Written(rig.Db, "2m");
        var s1 = await Backtest(research, p1.File, rig.A, Start, LastDev);
        log.WriteLine($"S.1 backtest P1 over A's development minutes: {Said(s1)}");
        Assert.True(s1.Ok, Json.Write(s1.Error));
        var s2 = await Verdict(research, p1.Version, rig.A);
        log.WriteLine($"S.2 verdict P1 --dataset A: {(s2.Ok ? Data(s2).ToString() : Said(s2))}");
        Assert.True(s2.Ok, Json.Write(s2.Error));
        Assert.Equal(1, Data(s2).GetProperty("verdicts_spent").GetInt32());

        // S.3-S.5 — A SECOND DOWNLOAD OF THE SAME MINUTES, HELD BACK BY THE OWNER'S SECOND PRESS AT THE SAME DATE.
        var b = Recorded(rig.Db, "BTCUSDT", "v2", Start, 120);
        var (pressed, c2) = rig.Gw.SetHoldout(b.Id, Cutoff, EvaluationClass.Research);
        Assert.True(pressed.Ok, $"a second press over held months was refused, and it is told and counted, never refused: {pressed.Why}");
        Assert.NotNull(c2);
        Assert.NotEqual(c1.Id, c2!.Id);
        var spentAtPress = rig.Gw.Campaigns.JudgementsSpent(c2.Id);
        var note = CampaignStore.PressNote(c2, spentAtPress, rig.Gw.Campaigns.OverTheSameMonths(c2.Id));
        log.WriteLine($"S.5 the owner's second press, on B at {Cutoff:u}: campaign {c2.Id}, judgements spent {spentAtPress} of "
            + $"{c2.VerdictBudget}; the press's note: \"{note}\"");
        Assert.True(spentAtPress == 1, $"campaign {c2.Id}, opened over months campaign {c1.Id} already judged once, starts "
            + $"with {spentAtPress} of its {c2.VerdictBudget} judgements spent");
        Assert.Contains($"The same months are already held back by campaign {c1.Id} (BTCUSDT 1m v1), which has taken "
            + $"1 judgement over them, so 1 of campaign {c2.Id}'s 2 final judgements is already spent", note, StringComparison.Ordinal);
        Assert.Contains("hold back OTHER months", note, StringComparison.Ordinal);

        // S.6-S.7 — RESEARCH OVER B, CHARGED TO BOTH CAMPAIGNS, AND THE JUDGEMENT OVER B.
        var s6 = await Backtest(research, p1.File, b, Start, LastDev);
        log.WriteLine($"S.6 backtest P1 over B's development minutes: {Said(s6)}; trials campaign {c1.Id}="
            + $"{rig.Gw.Campaigns.TrialsCharged(c1.Id)} campaign {c2.Id}={rig.Gw.Campaigns.TrialsCharged(c2.Id)}");
        Assert.True(s6.Ok, Json.Write(s6.Error));
        var s7 = await Verdict(research, p1.Version, b);
        log.WriteLine($"S.7 verdict P1 --dataset B: {(s7.Ok ? Data(s7).ToString() : Said(s7))}");
        Assert.True(s7.Ok, Json.Write(s7.Error));
        Assert.Equal(c2.Id, Data(s7).GetProperty("campaign").GetInt64());
        Assert.Equal(2, Data(s7).GetProperty("verdicts_spent").GetInt32());
        Assert.Equal(2, Data(s7).GetProperty("verdicts_budget").GetInt32());

        // S.8-S.9 — ONE COUNT FOR ONE HELD HOUR, WHICHEVER CAMPAIGN IS ASKED.
        log.WriteLine($"S.9 judgements spent: campaign {c1.Id} = {rig.Gw.Campaigns.JudgementsSpent(c1.Id)}; campaign {c2.Id} = "
            + $"{rig.Gw.Campaigns.JudgementsSpent(c2.Id)}");
        Assert.Equal(2, rig.Gw.Campaigns.JudgementsSpent(c1.Id));
        Assert.Equal(2, rig.Gw.Campaigns.JudgementsSpent(c2.Id));

        // AND THE NEXT JUDGEMENT OVER THOSE MONTHS IS REFUSED BEFORE ANYTHING IS COMPUTED, naming campaign 1 and OTHER months.
        var p2 = Written(rig.Db, "3m");
        var researched = await Backtest(research, p2.File, b, Start, LastDev);
        Assert.True(researched.Ok, Json.Write(researched.Error));
        var refused = await Verdict(research, p2.Version, b);
        log.WriteLine($"verdict P2 --dataset B: {(refused.Ok ? Data(refused).ToString() : Said(refused))}");
        Assert.True(refused.Ok, Json.Write(refused.Error));
        Assert.Equal(JsonValueKind.Null, Data(refused).GetProperty("verdict").ValueKind);
        var why = Data(refused).GetProperty("why").GetString()!;
        Assert.Contains($"campaign {c2.Id} has spent all 2 of its final judgements", why, StringComparison.Ordinal);
        Assert.Contains("BEFORE anything is computed", why, StringComparison.Ordinal);
        Assert.Contains($"campaign {c1.Id} over dataset {rig.A.Id} (BTCUSDT 1m v1) has taken 1", why, StringComparison.Ordinal);
        Assert.Contains("a holdout over OTHER months", why, StringComparison.Ordinal);
        Assert.Single(rig.Gw.Campaigns.Verdicts(c2.Id));
        Assert.Equal(2, rig.Gw.Campaigns.JudgementsSpent(c2.Id));
    }

    /// <summary>
    /// (e) THE OTHER BUTTON AT THE SAME DATE IS REFUSED WHILE A CAMPAIGN JUDGES — both directions, through the owner's own
    /// press (<c>TradingGateway.SetHoldout</c>). A, held as real history under campaign 1, pressed "These are fixture bars"
    /// at its own date; F, held as fixture bars under its own campaign, pressed "Hold these bars back" at its own date: each
    /// is refused in the owner's words naming the campaign, the class it judges under and the fresh download, and the
    /// ledger is unchanged — both cutoffs, both classes, both campaigns, the count of campaigns and every held window. The
    /// same instant and the same class stays the Ok no-op. RED on the base, where both presses answered Ok and rewrote the
    /// class.
    /// </summary>
    [Fact]
    public async Task The_other_button_at_the_same_date_is_refused_while_a_campaign_judges()
    {
        await using var rig = await Ready(trials: 3);
        var f = Recorded(rig.Db, "ETHUSDT", "v1", Start, 120);
        var (fixtureHeld, fixture) = rig.Gw.SetHoldout(f.Id, Cutoff, EvaluationClass.Fixture);
        Assert.True(fixtureHeld.Ok, fixtureHeld.Why);

        (DateTimeOffset? A, string AClass, DateTimeOffset? F, string FClass, long? OpenA, long? OpenF, int Campaigns, string Windows) Ledger()
        {
            var a = rig.Gw.Datasets.ById(rig.A.Id)!;
            var held = rig.Gw.Datasets.ById(f.Id)!;
            return (a.HoldoutFrom, a.EvaluationClass, held.HoldoutFrom, held.EvaluationClass,
                rig.Gw.Campaigns.OpenForDataset(a.Id)?.Id, rig.Gw.Campaigns.OpenForDataset(held.Id)?.Id,
                rig.Gw.Campaigns.All().Count,
                string.Join(" | ", TapeHoldout.WindowsOf(rig.Gw.Datasets.All()).Select(w => $"{w.DatasetId} [{w.From:u}, {w.Until:u})")));
        }

        var before = Ledger();
        foreach (var (set, campaign, asked, judgedAs) in new[]
                 {
                     (rig.A, rig.Campaign, EvaluationClass.Fixture, "real history"),
                     (f, fixture!, EvaluationClass.Research, "fixture bars")
                 })
        {
            var (done, opened) = rig.Gw.SetHoldout(set.Id, Cutoff, asked);
            log.WriteLine($"dataset {set.Id}, held as {judgedAs} under campaign {campaign.Id}, pressed {asked} at {Cutoff:u}: "
                + $"Ok={done.Ok} Why=\"{done.Why}\"");

            Assert.False(done.Ok, $"the press rewrote dataset {set.Id}'s class to {asked} while campaign {campaign.Id} judges it as {judgedAs}");
            Assert.Null(opened);
            Assert.Null(done.Cutoff);
            Assert.StartsWith("Nothing was changed.", done.Why, StringComparison.Ordinal);
            Assert.Contains($"Campaign {campaign.Id} judges strategies on these bars as {judgedAs}", done.Why, StringComparison.Ordinal);
            Assert.Contains("download a fresh copy of the history and hold months back on that", done.Why, StringComparison.Ordinal);
            Assert.Equal(before, Ledger());
        }

        // THE SAME INSTANT AND THE SAME CLASS STAYS THE Ok NO-OP, handing back the campaign already open.
        var (again, same) = rig.Gw.SetHoldout(rig.A.Id, Cutoff, EvaluationClass.Research);
        Assert.True(again.Ok, again.Why);
        Assert.Equal(rig.Campaign.Id, same!.Id);
        var (fixtureAgain, sameFixture) = rig.Gw.SetHoldout(f.Id, Cutoff, EvaluationClass.Fixture);
        Assert.True(fixtureAgain.Ok, fixtureAgain.Why);
        Assert.Equal(fixture!.Id, sameFixture!.Id);
        Assert.Equal(before, Ledger());
    }
}
