using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
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
/// THE APP'S LINKS (<c>U-research-ledger</c>, item 3): a <c>backtest</c> or a <c>verdict</c> asked under a research-ledger
/// entry of the caller's own role is linked by the app to the record it answered with — the run, the promotion — in that
/// record's own write, or alone when a verdict already recorded is answered. Nothing an agent sends writes a link, and a
/// request refused, stopped or not judged links nothing.
///
/// <para>The fixture is <c>VerdictOverPipeTests</c>' in shape: 120 one-minute BTCUSDT bars cycling 96 → 105, the owner's
/// holdout from minute 60 with its campaign, and every research run ending a minute before the cutoff. Every request
/// below goes through the pipe's own handler — over the wire, through the harness's door, through the real CLI — so these
/// tests compile against a build without the link and were watched red against it.</para>
/// </summary>
public class ResearchLedgerLinkTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minutes into the fixture dataset at which the owner's holdout begins.</summary>
    const int HoldoutAtBar = 60;

    /// <summary>Buys the dip and sells the rip, with the three execution bounds: it is judged, and comes out ahead.</summary>
    static string Judged(string freshness) =>
        $"instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness {freshness}\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    /// <summary>The same bounds, buying the rip and selling the dip: judged, and refused.</summary>
    static string Losing(string freshness) =>
        $"instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness {freshness}\nmax_decision_age 30s\n"
        + "exit when close < 97\nentry when close > 103\n";

    // ---- the rig -----------------------------------------------------------------------------------------------------

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

    sealed record World(TradingGateway Gw, Database Db, DatasetRecord Set, CampaignRow Campaign, AgentGrants Grants,
        string Pipe, GatewayPipeServer Server) : IAsyncDisposable
    {
        public async Task<Raw> Dial(string role, string attempt)
        {
            var client = await Raw.ConnectAsync(Pipe);
            var hello = await client.SendAsync(new IpcRequest
            {
                Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = Grants.Issue(role, attempt).Token
            });
            Assert.True(hello.Ok, Json.Write(hello.Error));
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Db.Dispose();
        }
    }

    static async Task<World> Given(int trials = 20, int verdicts = 5)
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = trials;
            s.CampaignVerdictBudget = verdicts;
        });

        const int bars = 120;
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");
        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var id = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Bar0, Bar0.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]));
        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(HoldoutAtBar), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);

        var pipe = "ta-link-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();
        return new World(gw, db, gw.Datasets.ById(id)!, campaign!, grants, pipe, server);
    }

    /// <summary>
    /// A program in a role's own folder, its version frozen at <see cref="Bar0"/> so the holdout window post-dates the
    /// freeze — the arrangement <c>VerdictOverPipeTests</c> uses so a profitable version can be promoted.
    /// </summary>
    static (string Path, string Version) GivenProgram(World w, string role, string name, string source)
    {
        var parsed = StrategyParser.Parse(source).Program!;
        new StrategyStore(w.Db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, parsed.WarmUpBars, Bar0, role, "attempt-freeze"));
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), source);
        return ("strategies/" + name, parsed.StrategyId);
    }

    static Dictionary<string, JsonElement> Args(params (string Key, object? Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs)
            if (value is not null) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    static IpcRequest Backtest(World w, string path, long? entry) => new()
    {
        Op = Ops.Backtest, Session = "research", RequestId = "bt-" + Guid.NewGuid().ToString("n")[..12],
        Args = Args(("strategy", path), ("dataset", Id(w.Set.Id)), ("from", Iso(Bar0)),
            ("to", Iso(Bar0.AddMinutes(HoldoutAtBar - 1))), ("increment", "1"), ("entry", entry))
    };

    static IpcRequest Verdict(string version, long? entry) => new()
    {
        Op = Ops.Verdict, Session = "research", RequestId = "v-" + Guid.NewGuid().ToString("n")[..12],
        Args = Args(("version", version), ("entry", entry))
    };

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static string RunOf(IpcResponse r)
    {
        Assert.True(r.Ok, Json.Write(r.Error));
        return Data(r).GetProperty("run_id").GetString()!;
    }

    /// <summary>Every link of an entry as <c>entry|revision|kind|record|attempt</c>, oldest first.</summary>
    static List<string> Links(World w, long? entry = null) => w.Db.Read(_ =>
    {
        using var c = w.Db.Cmd("""
            SELECT entry_id, revision, record_kind, record_id, attempt FROM ledger_link
             WHERE $e IS NULL OR entry_id = $e ORDER BY id
            """, ("$e", entry));
        using var rd = c.ExecuteReader();
        var all = new List<string>();
        while (rd.Read())
            all.Add($"{rd.GetInt64(0)}|{rd.GetInt64(1)}|{rd.GetString(2)}|{rd.GetString(3)}|{(rd.IsDBNull(4) ? "NULL" : rd.GetString(4))}");
        return all;
    });

    static int Runs(World w) => w.Gw.Strategies.Runs().Count(r => r.Role != Referee.RunRole);

    static long Entry(World w, string role, string attempt = "attempt-entry") =>
        w.Gw.Ledger.Add(role, attempt, LedgerKind.Hypothesis, $"a {role} hypothesis", LedgerMark.Hypothesis, null, null, null).Id;

    // ---- (c) a run asked under an entry is linked by the app ----------------------------------------------------------

    /// <summary>
    /// (c) A RUN ASKED UNDER AN ENTRY IS LINKED BY THE APP — over the pipe, through the API harness's <c>trade</c> tool
    /// and through the real CLI (<c>Harness.RunTradeAsync</c>): one link per run, naming the run by its id, the revision
    /// the entry stood at when it was asked and the attempt that asked; the same run asked again is one run and still one
    /// link; and <c>ledger-show</c> names it, with the version it ran derived at read. RED before item 3: the entry was not
    /// an argument anything read, and no link was written.
    /// </summary>
    [Fact]
    public async Task A_run_asked_under_an_entry_is_linked_by_the_app()
    {
        await using var w = await Given();
        var entry = Entry(w, CouncilRoles.Research);
        w.Gw.Ledger.Revise(CouncilRoles.Research, "attempt-entry", entry, "only below 97", LedgerMark.Hypothesis,
            "narrowed before the run", 0.5m, null);

        // THE PIPE.
        await using var wire = await w.Dial(CouncilRoles.Research, "attempt-c1");
        var (a, versionA) = GivenProgram(w, CouncilRoles.Research, "a.strategy", Judged("2m"));
        var run = RunOf(await wire.SendAsync(Backtest(w, a, entry)));
        Assert.Equal([$"{entry}|2|run|{run}|attempt-c1"], Links(w));

        // ASKED AGAIN, AFTER ANOTHER REVISION: one run, one link — the first, with the revision it was asked under.
        w.Gw.Ledger.Revise(CouncilRoles.Research, "attempt-entry", entry, "only below 96", LedgerMark.Hypothesis,
            "narrowed again", null, null);
        var runs = Runs(w);
        Assert.Equal(run, RunOf(await wire.SendAsync(Backtest(w, a, entry))));
        Assert.Equal(runs, Runs(w));
        Assert.Equal([$"{entry}|2|run|{run}|attempt-c1"], Links(w));

        // THE API HARNESS'S `trade` TOOL, UNDER ITS OWN ATTEMPT.
        var (b, _) = GivenProgram(w, CouncilRoles.Research, "b.strategy", Judged("3m"));
        var tools = new GrantedWorkerTools(CouncilRoles.Research, () => Paths.RoleHome(CouncilRoles.Research),
            () => "attempt-harness", () => w.Server);
        var answer = await tools.InvokeAsync(new ToolRequest("t1", GrantedWorkerTools.Trade, JsonSerializer.Serialize(new
        {
            op = Ops.Backtest, strategy = b, dataset = w.Set.Id, from = Iso(Bar0), to = Iso(Bar0.AddMinutes(HoldoutAtBar - 1)),
            increment = 1, entry
        })));
        Assert.True(answer.Served, answer.Content);
        var runB = JsonDocument.Parse(answer.Content).RootElement.GetProperty("run_id").GetString();
        Assert.Equal([$"{entry}|2|run|{run}|attempt-c1", $"{entry}|3|run|{runB}|attempt-harness"], Links(w));

        // WHAT A READ SAYS OF IT: the link by kind and id, and the version its run ran, derived now.
        var shown = Data(await wire.SendAsync(new IpcRequest
        {
            Op = Ops.LedgerShow, Session = "research", Args = Args(("entry", entry))
        }));
        Assert.Equal(2, shown.GetProperty("link_count").GetInt32());
        Assert.Equal([("run", runB), ("run", run)], shown.GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("kind").GetString(), l.GetProperty("id").GetString())));
        Assert.Contains(versionA, shown.GetProperty("derived").GetProperty("versions").EnumerateArray().Select(v => v.GetString()));

        // THE REAL CLI, ON THE DEFAULT PIPE, AS THE ASSEMBLY'S OWN LAUNCH OF THE OPERATIONS DIRECTOR.
        var chair = Entry(w, CouncilRoles.Operations);
        var (c, _) = GivenProgram(w, CouncilRoles.Operations, "c.strategy", Judged("4m"));
        await using var cliServer = new GatewayPipeServer(w.Gw, IpcToken.Ensure());
        cliServer.Start();
        var ran = await Build.RunTradeAsync("backtest", "--strategy", c, "--dataset", Id(w.Set.Id), "--from", Iso(Bar0),
            "--to", Iso(Bar0.AddMinutes(HoldoutAtBar - 1)), "--increment", "1", "--entry", Id(chair), "--json");
        Assert.True(ran.Code == 0, ran.Err + ran.Out);
        string runC;
        using (var doc = JsonDocument.Parse(ran.Out)) runC = doc.RootElement.GetProperty("data").GetProperty("run_id").GetString()!;
        Assert.Equal([$"{chair}|1|run|{runC}|{TestEnv.Chair!.AttemptId}"], Links(w, chair));
        log.WriteLine(string.Join("\n", Links(w)));
    }

    // ---- (d) a verdict asked under an entry links its promotion -------------------------------------------------------

    /// <summary>
    /// (d) A VERDICT ASKED UNDER AN ENTRY LINKS ITS PROMOTION: one computed and promoted, one computed and REFUSED — a
    /// refusal is a verdict and is linked like any other — and one already judged, asked first under no entry and then
    /// under one, linked alone with nothing run and nothing charged, and still one link when asked a third time. RED before
    /// item 3: no link.
    /// </summary>
    [Fact]
    public async Task A_verdict_asked_under_an_entry_links_its_promotion()
    {
        await using var w = await Given();
        var entry = Entry(w, CouncilRoles.Research);
        await using var wire = await w.Dial(CouncilRoles.Research, "attempt-d1");

        var (good, promoted) = GivenProgram(w, CouncilRoles.Research, "good.strategy", Judged("2m"));
        var (bad, refused) = GivenProgram(w, CouncilRoles.Research, "bad.strategy", Losing("2m"));
        var (late, judged) = GivenProgram(w, CouncilRoles.Research, "late.strategy", Judged("3m"));
        foreach (var path in new[] { good, bad, late }) RunOf(await wire.SendAsync(Backtest(w, path, null)));

        // COMPUTED, AND PROMOTED.
        var first = await wire.SendAsync(Verdict(promoted, entry));
        Assert.True(first.Ok, Json.Write(first.Error));
        Assert.Equal(PromotionVerdict.Promoted, Data(first).GetProperty("verdict").GetString());
        var p1 = Assert.Single(w.Gw.Promotions.For(promoted)).Id;
        Assert.Equal([$"{entry}|1|promotion|{p1}|attempt-d1"], Links(w));

        // COMPUTED, AND REFUSED: linked like any other.
        var second = await wire.SendAsync(Verdict(refused, entry));
        Assert.True(second.Ok, Json.Write(second.Error));
        Assert.Equal(PromotionVerdict.Refused, Data(second).GetProperty("verdict").GetString());
        var p2 = Assert.Single(w.Gw.Promotions.For(refused)).Id;
        Assert.Equal($"{entry}|1|promotion|{p2}|attempt-d1", Links(w)[^1]);

        // ALREADY JUDGED: asked under no entry, it links nothing; asked under one, the recorded answer is linked alone.
        Assert.True((await wire.SendAsync(Verdict(judged, null))).Ok);
        Assert.Equal(2, Links(w).Count);
        var spent = w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id);
        var referee = w.Gw.Strategies.Runs().Count(r => r.Role == Referee.RunRole);
        var p3 = Assert.Single(w.Gw.Promotions.For(judged)).Id;
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var again = await wire.SendAsync(Verdict(judged, entry));
            Assert.True(again.Ok, Json.Write(again.Error));
            Assert.Equal([$"{entry}|1|promotion|{p1}|attempt-d1", $"{entry}|1|promotion|{p2}|attempt-d1",
                $"{entry}|1|promotion|{p3}|attempt-d1"], Links(w));
        }
        Assert.Equal(spent, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));
        Assert.Equal(referee, w.Gw.Strategies.Runs().Count(r => r.Role == Referee.RunRole));
        log.WriteLine(string.Join("\n", Links(w)));
    }

    // ---- (e) a refused request links nothing --------------------------------------------------------------------------

    /// <summary>
    /// (e) A REFUSED REQUEST LINKS NOTHING. An entry that is not there, or is the other director's, is refused on a run and
    /// on a verdict before anything runs or is charged — no run, no trial, <c>JudgementsSpent</c> unchanged; a run the app
    /// stops and a verdict the app stops (<c>U-verdict-stopped</c>, on <c>main</c>) record nothing and link nothing; a run
    /// refused <c>CAMPAIGN_BUDGET_REACHED</c> once the trials are spent links nothing; and a verdict the referee declines
    /// for want of a judgement to charge links nothing. RED before item 3: the unknown entry was not refused.
    /// </summary>
    [Fact]
    public async Task A_refused_request_links_nothing()
    {
        await using var w = await Given(trials: 2, verdicts: 1);
        var entry = Entry(w, CouncilRoles.Research);
        var theirs = Entry(w, CouncilRoles.Operations);
        await using var wire = await w.Dial(CouncilRoles.Research, "attempt-e1");
        var (a, versionA) = GivenProgram(w, CouncilRoles.Research, "a.strategy", Judged("2m"));
        var (b, versionB) = GivenProgram(w, CouncilRoles.Research, "b.strategy", Judged("3m"));
        var (c, _) = GivenProgram(w, CouncilRoles.Research, "c.strategy", Judged("4m"));

        // AN ENTRY THAT IS NOT THERE, AND THE OTHER DIRECTOR'S: refused, nothing run, nothing charged.
        foreach (var (under, words) in new[] { (999_999L, "there is no entry 999999"), (theirs, "is the Operations Director's") })
        {
            var refusedRun = await wire.SendAsync(Backtest(w, a, under));
            Assert.False(refusedRun.Ok, "answered: " + Json.Write(refusedRun.Data));
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), refusedRun.Error!.Code);
            Assert.Contains(words, refusedRun.Error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, Runs(w));
        Assert.Equal(0, w.Gw.Campaigns.TrialsCharged(w.Campaign.Id));

        // A RUN THE APP STOPS, through the harness's door with the turn's token cancelled: nothing recorded, nothing linked.
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            var stopped = await w.Server.CallAsync(Backtest(w, a, entry), CouncilRoles.Research, "attempt-e1", stop.Token);
            Assert.Equal(nameof(ErrorCode.IPC_UNAVAILABLE), stopped.Error?.Code);
        }
        Assert.Equal(0, Runs(w));
        Assert.Empty(Links(w));

        // TWO TRIALS SPENT, AND THE THIRD REFUSED: linked twice, then nothing.
        var runA = RunOf(await wire.SendAsync(Backtest(w, a, entry)));
        var runB = RunOf(await wire.SendAsync(Backtest(w, b, entry)));
        var spentRun = await wire.SendAsync(Backtest(w, c, entry));
        Assert.Equal(nameof(ErrorCode.CAMPAIGN_BUDGET_REACHED), spentRun.Error?.Code);
        Assert.Equal(2, Runs(w));
        Assert.Equal([$"{entry}|1|run|{runA}|attempt-e1", $"{entry}|1|run|{runB}|attempt-e1"], Links(w));

        // A VERDICT UNDER AN ENTRY THAT IS NOT THERE, OR IS THE OTHER DIRECTOR'S: refused before the charge.
        foreach (var under in new[] { 999_999L, theirs })
        {
            var refusedVerdict = await wire.SendAsync(Verdict(versionA, under));
            Assert.False(refusedVerdict.Ok, "answered: " + Json.Write(refusedVerdict.Data));
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), refusedVerdict.Error!.Code);
        }
        Assert.Equal(0, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));
        Assert.Empty(w.Gw.Promotions.For(versionA));

        // A VERDICT THE APP STOPS: refused IPC_UNAVAILABLE, nothing recorded, nothing linked.
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            var stopped = await w.Server.CallAsync(Verdict(versionA, entry), CouncilRoles.Research, "attempt-e1", stop.Token);
            Assert.Equal(nameof(ErrorCode.IPC_UNAVAILABLE), stopped.Error?.Code);
        }
        Assert.Empty(w.Gw.Promotions.For(versionA));
        Assert.Equal(2, Links(w).Count);

        // THE ONE JUDGEMENT, SPENT AND LINKED; THE NEXT VERSION HAS NONE TO CHARGE, AND LINKS NOTHING.
        Assert.True((await wire.SendAsync(Verdict(versionA, entry))).Ok);
        Assert.Equal(3, Links(w).Count);
        Assert.Equal(1, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));
        var unjudged = await wire.SendAsync(Verdict(versionB, entry));
        log.WriteLine($"no judgement to charge: {Json.Write(unjudged.Error ?? (object?)unjudged.Data)}");
        Assert.Empty(w.Gw.Promotions.For(versionB));
        Assert.Equal(1, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));
        Assert.Equal(3, Links(w).Count);
    }
}
