using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE VERDICT REQUEST AS AN AGENT ACTUALLY MEETS IT: over the pipe, with a launch grant, against a
/// real dataset whose last months the owner held back and a real campaign counting the attempts.
///
/// <para><b>What this closes.</b> `docs/PRINCIPLES.md` § Evidence: "Models may propose candidates for
/// evaluation; software decides admission and issues the verdict within the existing evidence budget."
/// Before this op the second half existed and the first did not — the referee was reachable only from
/// inside the app, so a research process could produce a candidate and had no way to have it judged.</para>
///
/// <para><b>And what it must not become.</b> Requesting an evaluation "does not grant access to the
/// holdout, alter the scorer or authorise live capital". So the tests below are mostly about what does
/// NOT cross: no metric, no trace hash, no bar, no second run over a holdout that has grown, and no
/// verdict at all for a caller that measured nothing or proved no role.</para>
/// </summary>
public class VerdictOverPipeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minutes into the fixture dataset at which the owner's holdout begins.</summary>
    const int HoldoutAtBar = 60;

    /// <summary>
    /// BUYS THE DIP AND SELLS THE RIP over bars that cycle 96 → 105, so the holdout half closes real
    /// trades and comes out ahead — which is what lets the PROMOTED path be exercised over the wire
    /// rather than only the refusals. Copied in shape from <c>RefereeVerdictTests</c>.
    ///
    /// <para>The three execution bounds are declared because <c>U-promote-bounds</c> refuses to judge a
    /// version that declares none of them, and every test here is about what happens after the referee
    /// agrees to judge. <paramref name="freshness"/> varies the TEXT and therefore the version id while
    /// changing nothing the backtest does, which is how the budget test below gets four distinct
    /// versions that all behave identically.</para>
    /// </summary>
    static string Program(string freshness = "2m") =>
        $"instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness {freshness}\n"
        + "max_decision_age 30s\nexit when close > 103\nentry when close < 97\n";

    /// <summary>
    /// The program <see cref="The_report_an_agent_reads_names_the_verdict_and_values_no_holdout_bar"/>
    /// judges. The three execution bounds are on it because `U-promote-bounds` refuses to JUDGE a
    /// version that declares none of them, and that test is about what the agent is told once a verdict
    /// exists. Identical in shape to <see cref="Program"/>'s default, and kept as its own constant
    /// because that test predates this class's fixture and names it.
    /// </summary>
    const string ProgramText =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    static string NewPipe() => "ta-verdict-" + Guid.NewGuid().ToString("n")[..12];

    sealed record World(
        TradingGateway Gw, Database Db, DatasetRecord Set, CampaignRow Campaign,
        AgentGrants Grants, string Pipe, Raw Client, GatewayPipeServer Server) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>The wire by hand, as <c>HoldoutOverPipeTests</c> speaks it: token on every frame.</summary>
    internal sealed class Raw : IAsyncDisposable
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

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>The bars this fixture writes to disk: closes cycling 96 → 105, open equal to close.</summary>
    static string Csv(int bars)
    {
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// A GATEWAY WITH REAL BARS, A REAL CUTOFF, A REAL CAMPAIGN AND A CONNECTED CALLER.
    ///
    /// <para><c>TradingGateway.SetHoldout</c> and not <c>DatasetStore.SetHoldout</c>, because the
    /// campaign is what bounds a verdict and only the gateway's press opens one.</para>
    /// </summary>
    static async Task<World> Given(
        string? role = CouncilRoles.Research, int verdicts = 3, int bars = 120, string attempt = "attempt-v1")
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = 20;
            s.CampaignVerdictBudget = verdicts;
        });

        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        File.WriteAllText(csv, Csv(bars));

        var id = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Bar0, Bar0.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]));

        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(HoldoutAtBar), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);

        var pipe = "ta-verdict-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var client = await Connect(pipe, grants, role, attempt);
        return new World(gw, db, gw.Datasets.ById(id)!, campaign!, grants, pipe, client, server);
    }

    static async Task<Raw> Connect(string pipe, AgentGrants grants, string? role, string attempt)
    {
        var client = await Raw.ConnectAsync(pipe);
        var grant = role is null ? null : grants.Issue(role, attempt).Token;
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
        Assert.True(hello.Ok, Json.Write(hello.Error));
        return client;
    }

    /// <summary>
    /// ONE ACCEPTED VERSION WITH ONE COMPLETED RESEARCH RUN BEHIND IT, which is the admission the
    /// verdict op asks for — and the id of that version.
    ///
    /// <para>The version row is written first, frozen at <see cref="Bar0"/>, so that the holdout window
    /// (which begins at bar 60) POST-DATES the freeze and the scoring policy's forward-evidence clause
    /// can be met. <c>StrategyStore.RecordVersion</c> is <c>ON CONFLICT DO NOTHING</c>, so the research
    /// run below re-records the same id and leaves that instant alone — the same arrangement
    /// <c>RefereeVerdictTests</c> uses, and the only way to freeze a version in the past without a
    /// clock the pipe does not have.</para>
    ///
    /// <para>The run itself goes over the WIRE, with a window that ends one bar before the cutoff, so it
    /// is a real research trial registered against the campaign by the app's own writer.</para>
    /// </summary>
    static async Task<string> GivenMeasuredVersion(
        World w, Raw? client = null, string freshness = "2m", string role = CouncilRoles.Research)
    {
        var source = Program(freshness);
        var parsed = StrategyParser.Parse(source).Program!;
        new StrategyStore(w.Db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            Bar0, role, "attempt-v1"));

        var name = $"v-{freshness}.strategy";
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), source);

        var ran = await (client ?? w.Client).SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "research", RequestId = $"verdict-fixture-{freshness}",
            // The increment is DECLARED: this fixture's dataset records no venue and TradeAgent will
            // not invent one (`VenueIncrementTests`).
            Args = Args(("strategy", "strategies/" + name), ("dataset", Id(w.Set.Id)),
                ("from", Iso(Bar0)), ("to", Iso(Bar0.AddMinutes(HoldoutAtBar - 1))), ("increment", "1"))
        });

        Assert.True(ran.Ok, Json.Write(ran.Error));
        Assert.Equal(parsed.StrategyId, Data(ran).GetProperty("version_id").GetString());
        return parsed.StrategyId;
    }

    static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static async Task<IpcResponse> AskVerdict(Raw client, string version, long? dataset = null, string rid = "v-1")
    {
        var args = dataset is { } set
            ? Args(("version", version), ("dataset", Id(set)))
            : Args(("version", version));

        return await client.SendAsync(new IpcRequest
        {
            Op = Ops.Verdict, Session = "research", RequestId = rid, Args = args
        });
    }

    // ---- what comes back, and what does not -------------------------------------------------------

    /// <summary>
    /// A COUNCIL ROLE ASKS FOR A VERDICT AND IS TOLD THE VERDICT AND THE REASON — IN WORDS, WITH NO
    /// FIGURE.
    ///
    /// <para>RED before the handler existed: the frame came back
    /// <c>unknown operation 'verdict'</c>.</para>
    ///
    /// <para><b>The no-figure assertion is the point of the unit</b> and it is made three ways, because
    /// one way alone is either loose or flaky. The reply's KEY SET is exactly the eight this op
    /// promises, so a metric cannot arrive under a new name. <c>text</c> is byte-identical to
    /// <see cref="RefereeFeedback.Text"/>, which reads the promotion row alone — that is the assertion
    /// the mutant fails. And the run's own figures, read back out of the database, are absent from the
    /// reply once the two content hashes are removed from it: the ids are 64 hex characters and a short
    /// decimal like <c>48</c> occurs inside them by chance, so the digits are looked for in the part of
    /// the answer that is not an identifier rather than in a way that could go red on a coincidence.</para>
    /// </summary>
    [Fact]
    public async Task A_council_role_asks_for_a_verdict_over_the_pipe_and_is_told_the_verdict_and_the_reason_and_no_figure()
    {
        await using var w = await Given();
        var version = await GivenMeasuredVersion(w);

        var reply = await AskVerdict(w.Client, version);
        log.WriteLine(Json.Write(reply.Error ?? (object)Data(reply), pretty: true));
        Assert.True(reply.Ok, Json.Write(reply.Error));

        var data = Data(reply);
        Assert.Equal(
            new[] { "campaign", "reason", "text", "verdict", "verdicts_budget", "verdicts_spent", "version", "why" },
            data.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(version, data.GetProperty("version").GetString());
        Assert.Equal(w.Campaign.Id, data.GetProperty("campaign").GetInt64());
        Assert.Equal(PromotionVerdict.Promoted, data.GetProperty("verdict").GetString());
        Assert.Equal(PromotionReason.Met, data.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("why").ValueKind);
        Assert.Equal(1, data.GetProperty("verdicts_spent").GetInt32());
        Assert.Equal(3, data.GetProperty("verdicts_budget").GetInt32());

        // THE PROMOTION ROW NO DEVELOPER WROTE: the observable result this unit exists for.
        var promotion = Assert.Single(w.Gw.Promotions.For(version));
        Assert.Equal(w.Campaign.Id, promotion.CampaignId);
        Assert.True(promotion.IsPromoted, promotion.Reason);

        var run = new StrategyStore(w.Db).RunById(promotion.HoldoutRunId)!;
        var raw = Json.Write(reply.Data);
        Assert.DoesNotContain(run.TraceSha256, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(promotion.HoldoutRunId, raw, StringComparison.Ordinal);

        Assert.Equal(RefereeFeedback.Text(promotion), data.GetProperty("text").GetString());
        Assert.Contains(version[..12], data.GetProperty("text").GetString()!, StringComparison.Ordinal);

        // AND THE RUN'S OWN FIGURES, READ BACK OUT OF THE DATABASE, ARE IN NONE OF THE WORDS. The two
        // content hashes and the campaign number are taken out first and the digits are matched on
        // their own boundaries: an id is 64 hex characters and a figure like 48 or 0 occurs inside one
        // by chance, so a plain substring search over the raw frame would go red on a coincidence
        // rather than on a leak.
        var words = string.Join(" ",
            new[] { data.GetProperty("verdict"), data.GetProperty("reason"), data.GetProperty("text"), data.GetProperty("why") }
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!))
            .Replace(version, " ", StringComparison.Ordinal)
            .Replace(promotion.Id, " ", StringComparison.Ordinal)
            .Replace($"campaign {promotion.CampaignId}", "campaign", StringComparison.Ordinal);

        foreach (var figure in new object?[]
                 {
                     run.NetPnl, run.GrossPnl, run.Fees, run.MaxDrawdown,
                     run.Bars, run.Trades, run.Wins, run.ExposureBars
                 })
        {
            var digits = Convert.ToString(figure, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(digits)) continue;
            Assert.DoesNotMatch($@"(?<![0-9.]){Regex.Escape(digits)}(?![0-9.])", words);
        }

        // The premise of that check: it really can find a figure when one is there.
        Assert.Matches($@"(?<![0-9.]){Regex.Escape(Convert.ToString(run.Trades, CultureInfo.InvariantCulture)!)}(?![0-9.])",
            words + " " + run.Trades.ToString(CultureInfo.InvariantCulture));
    }

    // ---- idempotence: a holdout that grew buys no second peek -------------------------------------

    /// <summary>
    /// ASKING TWICE IS ONE VERDICT, ONE CHARGE AND ONE RUN — EVEN AFTER THE HELD-BACK MONTHS HAVE GROWN.
    ///
    /// <para><b>Why the growth is the case that matters.</b> A verdict that re-ran on every ask would
    /// let a caller watch one program against an expanding holdout for the price of one charge: ask,
    /// wait for the collector, ask again. The answer would change, and each change would say something
    /// about months the research process was never shown. So a promotion already recorded for
    /// (version, campaign) is answered AS IT STANDS and nothing runs.</para>
    ///
    /// <para>The dataset is grown the way the collector grows one — more bars in the normalised file —
    /// and the ledger row is moved on to match with one statement, because the store has no public
    /// writer for it and a file whose hash no longer matches its row would be REJECTED, which would
    /// make the second ask fail for a reason that is not this one.</para>
    /// </summary>
    [Fact]
    public async Task Asking_twice_after_the_dataset_grew_is_one_verdict_one_charge_and_no_second_run()
    {
        await using var w = await Given();
        var version = await GivenMeasuredVersion(w);

        var first = await AskVerdict(w.Client, version, rid: "v-first");
        Assert.True(first.Ok, Json.Write(first.Error));
        var promotion = Assert.Single(w.Gw.Promotions.For(version));
        var runsAfterFirst = RefereeRuns(w);
        Assert.Equal(1, runsAfterFirst);
        Assert.Equal(1, Data(first).GetProperty("verdicts_spent").GetInt32());

        Grow(w, to: 180);
        Assert.Equal(180, w.Gw.Datasets.ById(w.Set.Id)!.Bars);
        Assert.Equal(DatasetState.ACCEPTED, w.Gw.Datasets.Checked(w.Gw.Datasets.ById(w.Set.Id)!).State);

        var second = await AskVerdict(w.Client, version, rid: "v-second");
        log.WriteLine(Json.Write(second.Error ?? (object)Data(second), pretty: true));
        Assert.True(second.Ok, Json.Write(second.Error));

        Assert.Equal(PromotionVerdict.Promoted, Data(second).GetProperty("verdict").GetString());
        Assert.Equal(RefereeFeedback.Text(promotion), Data(second).GetProperty("text").GetString());
        Assert.Equal(1, Data(second).GetProperty("verdicts_spent").GetInt32());

        var after = Assert.Single(w.Gw.Promotions.For(version));
        Assert.Equal(promotion.Id, after.Id);
        Assert.Equal(runsAfterFirst, RefereeRuns(w));
        Assert.Single(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
    }

    static int RefereeRuns(World w) =>
        new StrategyStore(w.Db).Runs(1000).Count(r => r.Role == Referee.RunRole);

    /// <summary>
    /// THE COLLECTOR, STOOD IN FOR: more bars in the normalised file and the ledger row moved on to
    /// match. There is no public store method that re-measures a dataset in place, and inventing one
    /// for a test would put a writer on the ledger that the product does not have.
    /// </summary>
    static void Grow(World w, int to)
    {
        var set = w.Gw.Datasets.ById(w.Set.Id)!;
        File.WriteAllText(set.NormalisedPath, Csv(to));
        w.Db.Write(_ =>
        {
            using var c = w.Db.Cmd(
                "UPDATE dataset SET bars=$b, normalised_sha256=$sha, last_bar=$last WHERE id=$id",
                ("$b", to), ("$sha", DatasetStore.Sha256(set.NormalisedPath)!),
                ("$last", Bar0.AddMinutes(to - 1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("$id", w.Set.Id));
            return c.ExecuteNonQuery();
        });
    }

    // ---- the budget is the bound ------------------------------------------------------------------

    /// <summary>
    /// THE CAMPAIGN'S VERDICT BUDGET REFUSES THE FOURTH VERSION IN WORDS, AND NOTHING IS RUN FOR IT.
    ///
    /// <para>Three is the shipped default and the number the owner's holdout is worth: every verdict
    /// tells the research process something about months it was never shown, so the supply is finite.
    /// The refusal is an ANSWER rather than an error — the caller is told the verdict is null, why, and
    /// what it has spent out of what it had — and no promotion row is written for the fourth version.</para>
    /// </summary>
    [Fact]
    public async Task The_campaigns_verdict_budget_refuses_the_next_version_in_words()
    {
        await using var w = await Given(verdicts: 3);

        foreach (var freshness in new[] { "2m", "3m", "4m" })
        {
            var judged = await GivenMeasuredVersion(w, freshness: freshness);
            var ok = await AskVerdict(w.Client, judged, rid: "v-" + freshness);
            Assert.True(ok.Ok, Json.Write(ok.Error));
            Assert.Equal(PromotionVerdict.Promoted, Data(ok).GetProperty("verdict").GetString());
        }

        var fourth = await GivenMeasuredVersion(w, freshness: "5m");
        var reply = await AskVerdict(w.Client, fourth, rid: "v-fourth");
        log.WriteLine(Json.Write(reply.Error ?? (object)Data(reply), pretty: true));

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var data = Data(reply);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("verdict").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("text").ValueKind);
        Assert.Equal(3, data.GetProperty("verdicts_spent").GetInt32());
        Assert.Equal(3, data.GetProperty("verdicts_budget").GetInt32());
        Assert.Contains("all 3", data.GetProperty("why").GetString()!, StringComparison.Ordinal);

        Assert.Empty(w.Gw.Promotions.For(fourth));
        Assert.Equal(3, w.Gw.Campaigns.Verdicts(w.Campaign.Id).Count);
        Assert.Equal(3, RefereeRuns(w));
    }

    // ---- admission -------------------------------------------------------------------------------

    /// <summary>
    /// A VERSION THIS ROLE NEVER RAN OVER THAT DATASET BUYS NO VERDICT, AND IS CHARGED NOTHING.
    ///
    /// <para>A version nobody measured is a program with no hypothesis behind it, and the campaign's
    /// scarcest budget is not the place to find out whether it runs at all. A LOSING measurement is
    /// admission enough — the clause is that a trial was registered, not that it went well — so the
    /// refusal cannot be read as the app protecting its own scoreboard.</para>
    ///
    /// <para>Nothing is charged, because admission is settled before the referee is called: the
    /// <c>strategy_verdict</c> table is still empty afterwards.</para>
    /// </summary>
    [Fact]
    public async Task A_version_this_role_never_ran_over_that_dataset_is_refused_in_words()
    {
        await using var w = await Given();

        // Accepted and frozen, and never run by anybody.
        var source = Program("7m");
        var parsed = StrategyParser.Parse(source).Program!;
        new StrategyStore(w.Db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            Bar0, CouncilRoles.Research, "attempt-v1"));

        var named = await AskVerdict(w.Client, parsed.StrategyId, dataset: w.Set.Id, rid: "v-unmeasured");
        log.WriteLine(Json.Write(named.Error ?? (object)Data(named)));
        Assert.False(named.Ok, "a version nobody measured bought a verdict");
        Assert.Contains("never completed a run", named.Error!.Message, StringComparison.Ordinal);

        // And with no dataset named at all: there is no ONE dataset to infer, so it asks for one.
        var guessed = await AskVerdict(w.Client, parsed.StrategyId, rid: "v-unmeasured-2");
        Assert.False(guessed.Ok, "the app guessed which holdout to spend");
        Assert.Contains("'dataset'", guessed.Error!.Message, StringComparison.Ordinal);

        Assert.Empty(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
        Assert.Empty(w.Gw.Promotions.For(parsed.StrategyId));
        Assert.Equal(0, RefereeRuns(w));
    }

    /// <summary>
    /// THE OPERATOR AND A CALLER THAT PROVED NO ROLE GET NO VERDICT — no charge, no run, no row.
    ///
    /// <para>The fixture is complete for the Research Director: the version is accepted, measured and
    /// would be promoted. What is refused here is the ASKER. A roleless connection is authenticated and
    /// is nobody (`U-containment`), and the owner at the keyboard is not a council role — a verdict
    /// recorded for either would put a judgement in a lineage that produced none of the evidence.</para>
    /// </summary>
    [Theory]
    [InlineData(CouncilRoles.Operations)]
    [InlineData(null)]
    public async Task A_roleless_or_operator_caller_gets_no_verdict(string? role)
    {
        await using var w = await Given();
        var version = await GivenMeasuredVersion(w);

        await using var other = await Connect(w.Pipe, w.Grants, role, "attempt-other");
        var reply = await AskVerdict(other, version, dataset: w.Set.Id, rid: "v-other");
        log.WriteLine($"{role ?? "(no role)"}: {Json.Write(reply.Error ?? (object)Data(reply))}");

        Assert.False(reply.Ok, $"'{role ?? "(no role)"}' was served a verdict");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error?.Code);

        // THE CLAUSE IS NAMED, so this cannot pass on a refusal that is about something else — which
        // is what it did on the base, where every frame came back "unknown operation 'verdict'".
        Assert.Contains(
            role is null ? "presented no launch grant" : "has never completed a run",
            reply.Error!.Message, StringComparison.Ordinal);

        Assert.Empty(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
        Assert.Empty(w.Gw.Promotions.For(version));
        Assert.Equal(0, RefereeRuns(w));
    }

    // ---- what the owner's report tells an agent about a verdict (U-referee-2 item 5) --------------

    /// <summary>
    /// U-referee-2 item 5, over the wire: WHAT AN AGENT ACTUALLY RECEIVES ABOUT A VERDICT.
    ///
    /// <para>The unit tests hold the promotion record and the note. This holds what only the wire can
    /// settle. <c>trade report</c> serves the owner's document to the agent VERBATIM — <c>text</c> is the
    /// whole rendered report — so section 8 is the one surface where a figure computed over the held-back
    /// months could reach the research process without any op being wrong. It names the verdict and the
    /// holdout run, and it values neither.</para>
    ///
    /// <para>It judges IN PROCESS (<c>gw.Referee.Verdict</c>) deliberately: this test is about the
    /// report, and it was written when nothing on the pipe could ask for a verdict at all. The tests
    /// above are the ones about the asking.</para>
    /// </summary>
    [Fact]
    public async Task The_report_an_agent_reads_names_the_verdict_and_values_no_holdout_bar()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var _2 = server;
        var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        await using var _3 = client;

        // 120 one-minute bars, the last 60 held back, a campaign over them, and one version frozen
        // before the cutoff so that forward evidence is satisfied and the verdict is a promotion.
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var rows = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < 120; i++)
        {
            var close = 96m + i % 10;
            rows.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, rows.ToString());

        var id = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, 120, Bar0, Bar0.AddMinutes(119), 0, [], false, 0, 0, 0,
            Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]));

        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(HoldoutAtBar), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);

        var program = StrategyParser.Parse(ProgramText).Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, Bar0,
            CouncilRoles.Research, "attempt-1"));

        var verdict = gw.Referee.Verdict(program.StrategyId, campaign!.Id);
        Assert.True(verdict.Promoted, verdict.Promotion?.Reason ?? verdict.Why);

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.Report, Session = "agent-1" });
        Assert.True(reply.Ok, Json.Write(reply.Error));

        var text = JsonSerializer.SerializeToElement(reply.Data, Json.Options)
            .GetProperty("text").GetString()!;
        log.WriteLine(text);

        // WHAT THE AGENT IS TOLD: the judgement, and that the run happened.
        Assert.Contains($"PROMOTED version {program.StrategyId[..12]}", text, StringComparison.Ordinal);
        Assert.Contains("holdout run", text, StringComparison.Ordinal);
        Assert.Contains("its figures are not printed in this report", text, StringComparison.Ordinal);

        // WHAT IT IS NOT TOLD: one number the app measured over the months it has never been served.
        var run = new StrategyStore(db).RunById(verdict.Promotion!.HoldoutRunId)!;
        Assert.True(run.NetPnl > 0m, "the fixture program is profitable, so a leak would have shown one");
        Assert.DoesNotContain($"net {run.NetPnl!.Value.ToString(CultureInfo.InvariantCulture)}", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"{run.Bars} bars", text, StringComparison.Ordinal);
        Assert.DoesNotContain(run.TraceSha256, text, StringComparison.Ordinal);

        // AND THE BARS THEMSELVES ARE STILL REFUSED ON THE SAME CONNECTION, verdict or no verdict.
        var bars = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars,
            Session = "agent-1",
            Args = new Dictionary<string, JsonElement>
            {
                ["pair"] = JsonSerializer.SerializeToElement("BTCUSDT"),
                ["from"] = JsonSerializer.SerializeToElement(Bar0.AddMinutes(HoldoutAtBar).ToString("O")),
                ["to"] = JsonSerializer.SerializeToElement(Bar0.AddMinutes(119).ToString("O"))
            }
        });

        Assert.False(bars.Ok);
        Assert.Equal(ErrorCode.HOLDOUT_WITHHELD.ToString(), bars.Error!.Code);
    }
}
