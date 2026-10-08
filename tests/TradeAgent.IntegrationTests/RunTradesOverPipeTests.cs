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
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// EVERY CLOSED TRADE OF A RECORDED RUN, OVER THE WIRE (<c>U-run-trace</c>): <c>trade run trades</c> serves the run's row
/// and its trades in bounded pages to every caller — the other director and a connection that proved no role included —
/// and serves nothing of the referee's holdout run, nor of a run a holdout window reaches now.
///
/// <para><b>What was wrong before this.</b> Attempt 3's Research Director, asked for an exit-cause attribution of two runs,
/// found "no retained per-trade rows or read-only command to retrieve a completed run's trace": every trade was kept, and
/// an agent saw the first 20 of a run once, in its own <c>backtest</c> answer. Trades 21 to 103 were served to no one.</para>
///
/// <para>The ledger: BTCUSDT 1m v1 from 2026-08-01 00:00Z, closes cycling 96 → 105, so the program below closes a trade
/// every ten minutes. These tests speak the wire by hand, as <c>HoldoutOverPipeTests</c> does: the interesting caller is
/// the one that does not do what the product's client does.</para>
/// </summary>
public class RunTradesOverPipeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The cutoff every held-back leg below sets: minute 60.</summary>
    static readonly DateTimeOffset Cutoff = Start.AddMinutes(60);

    const string Program = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// A PROFITABLE PROGRAM THAT DECLARES THE THREE EXECUTION BOUNDS — the one <c>HoldoutOverPipeTests</c>' verdict test
    /// judges, over the same bars.
    /// </summary>
    const string JudgeableText =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    /// <summary>The wire by hand: the machine's key on every frame, the grant on the hello.</summary>
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

    /// <summary>A gateway serving the pipe.</summary>
    sealed record Rig(TradingGateway Gw, Database Db, GatewayPipeServer Server, AgentGrants Grants, string Pipe) : IAsyncDisposable
    {
        public async Task<Raw> Dial(string? role)
        {
            var client = await Raw.ConnectAsync(Pipe);
            var grant = role is null ? null : Grants.Issue(role, "attempt-run-trace").Token;
            var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
            Assert.True(hello.Ok, Json.Write(hello.Error));
            return client;
        }

        /// <summary>The same frame through the other door into the same handler: the in-process worker surface.</summary>
        public Task<IpcResponse> CallAsync(string? role, IpcRequest req) => Server.CallAsync(req, role, "attempt-run-trace");

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Db.Dispose();
        }
    }

    static async Task<Rig> Ready()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = "ta-rt-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();
        return new Rig(gw, db, server, grants, pipe);
    }

    /// <summary>
    /// A DATASET THE LEDGER RECORDED, hashes and all, with no cutoff: <paramref name="bars"/> one-minute bars from
    /// <see cref="Start"/>, closes cycling 96 → 105.
    /// </summary>
    static DatasetRecord Recorded(Database db, string pair, string version, int bars)
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Start.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, version, 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]));
        return store.ById(id)!;
    }

    /// <summary>A program in a role's own folder, so the <c>backtest</c> op has something to read.</summary>
    static string GivenProgram(string role, string name = "run-trace.strategy", string text = Program)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text);
        return "strategies/" + name;
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static Dictionary<string, JsonElement> Args(params (string Key, object? Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs)
            if (value is not null) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    static IpcRequest Backtest(string program, DatasetRecord set, DateTimeOffset? from, DateTimeOffset? to) => new()
    {
        Op = Ops.Backtest, Session = "run-trace", RequestId = "run-trace-" + Guid.NewGuid().ToString("n")[..12],
        Args = Args(("strategy", program), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)),
            ("from", from is { } f ? Iso(f) : null), ("to", to is { } t ? Iso(t) : null), ("increment", "1"))
    };

    static IpcRequest Trades(string run, object? after = null, object? limit = null) => new()
    {
        Op = Ops.RunTrades, Session = "run-trace", Args = Args(("run", run), ("after", after), ("limit", limit))
    };

    /// <summary>A trade as the wire carries one, compared by value: ordinal, both minutes, both prices, size, reason, fees, pnl.</summary>
    static (int, DateTimeOffset, decimal, DateTimeOffset, decimal, decimal, string?, decimal, decimal) Trade(JsonElement t) => (
        t.GetProperty("ordinal").GetInt32(), t.GetProperty("entry_bar").GetDateTimeOffset(), t.GetProperty("entry_price").GetDecimal(),
        t.GetProperty("exit_bar").GetDateTimeOffset(), t.GetProperty("exit_price").GetDecimal(), t.GetProperty("quantity").GetDecimal(),
        t.GetProperty("reason").GetString(), t.GetProperty("fees").GetDecimal(), t.GetProperty("pnl").GetDecimal());

    /// <summary>The same trade as the ledger recorded it.</summary>
    static (int, DateTimeOffset, decimal, DateTimeOffset, decimal, decimal, string?, decimal, decimal) Trade(StrategyTradeRow t) =>
        (t.Ordinal, t.EntryBar, t.EntryPrice, t.ExitBar, t.ExitPrice, t.Quantity, t.ExitReason, t.Fees, t.Pnl);

    static string Who(string? role) => role is null ? "a caller that proved no role" : $"the {CouncilRoles.Title(role)}";

    /// <summary>
    /// (a) EVERY CLOSED TRADE OF A RESEARCH RUN IS SERVED IN PAGES, TO A ROLE THAT DID NOT RUN IT. The Research Director's
    /// run over 300 minutes closes 29 trades and its <c>backtest</c> answer lists 20 beside <c>trade_count</c>; the
    /// Operations Director reads all 29 over the pipe in pages of seven, each that stopped saying <c>limit</c> and handing
    /// back the ordinal that continues it — equal, trade for trade, to the ledger's own <c>TradesOf</c>, and the first 20
    /// equal to the answer's. A caller that proved no role reads them by the report's twelve characters in one page; the
    /// row is the run's as recorded; nothing is run or written; and the real CLI on the default pipe serves the same.
    /// RED on the base: <c>unknown operation 'run-trades'</c>.
    /// </summary>
    [Fact]
    public async Task Every_closed_trade_of_a_research_run_is_served_in_pages()
    {
        await using var rig = await Ready();
        var set = Recorded(rig.Db, "BTCUSDT", "v1", 300);

        await using var research = await rig.Dial(CouncilRoles.Research);
        var ran = await research.SendAsync(Backtest(GivenProgram(CouncilRoles.Research), set, null, null));
        Assert.True(ran.Ok, Json.Write(ran.Error));
        var answer = Data(ran);
        var runId = answer.GetProperty("run_id").GetString()!;
        var total = answer.GetProperty("trade_count").GetInt32();
        log.WriteLine($"backtest {runId[..12]}: trade_count {total}, {answer.GetProperty("trades").GetArrayLength()} listed");
        Assert.True(total > Backtests.TradesShown, $"the fixture closed only {total} trades");
        Assert.Equal(Backtests.TradesShown, answer.GetProperty("trades").GetArrayLength());

        var recorded = rig.Gw.Strategies.TradesOf(runId);
        Assert.Equal(total, recorded.Count);
        var row = rig.Gw.Strategies.RunById(runId)!;
        var runs = rig.Gw.Strategies.RunCount;

        // THE OTHER DIRECTOR, OVER THE PIPE, SEVEN AT A TIME.
        await using var operations = await rig.Dial(CouncilRoles.Operations);
        var served = new List<JsonElement>();
        long? after = null;
        var pages = 0;
        while (true)
        {
            var reply = await operations.SendAsync(Trades(runId, after, 7));
            Assert.True(reply.Ok, Json.Write(reply.Error));
            var page = Data(reply);
            pages++;

            Assert.Equal(runId, page.GetProperty("run_id").GetString());
            Assert.Equal(CouncilRoles.Research, page.GetProperty("role").GetString());
            Assert.Equal(total, page.GetProperty("trade_count").GetInt32());
            Assert.Equal(7, page.GetProperty("limit").GetInt32());
            if (after is { } a) Assert.Equal(a, page.GetProperty("after").GetInt64());
            else Assert.Equal(JsonValueKind.Null, page.GetProperty("after").ValueKind);

            var rows = page.GetProperty("trades").EnumerateArray().ToList();
            Assert.Equal(rows.Count, page.GetProperty("count").GetInt32());
            served.AddRange(rows);
            log.WriteLine($"page {pages}: {rows.Count} trades, more={page.GetProperty("more")}, capped_by={page.GetProperty("capped_by")}, "
                          + $"next_after={page.GetProperty("next_after")}");

            if (!page.GetProperty("more").GetBoolean())
            {
                Assert.Equal(JsonValueKind.Null, page.GetProperty("capped_by").ValueKind);
                Assert.Equal(JsonValueKind.Null, page.GetProperty("next_after").ValueKind);
                break;
            }

            Assert.Equal(7, rows.Count);
            Assert.Equal(TapeReader.CappedByLimit, page.GetProperty("capped_by").GetString());
            after = page.GetProperty("next_after").GetInt64();
            Assert.Equal(rows[^1].GetProperty("ordinal").GetInt64(), after);
        }

        Assert.Equal((total + 6) / 7, pages);
        Assert.Equal(recorded.Select(Trade), served.Select(Trade));
        Assert.Equal(Enumerable.Range(0, total), served.Select(t => t.GetProperty("ordinal").GetInt32()));

        // THE BACKTEST'S TWENTY ARE THE FIRST TWENTY.
        Assert.Equal(answer.GetProperty("trades").EnumerateArray().Select(Trade), served.Take(Backtests.TradesShown).Select(Trade));

        // A CALLER THAT PROVED NO ROLE, BY THE REPORT'S TWELVE CHARACTERS, IN ONE PAGE — over the pipe and in process.
        await using var nobody = await rig.Dial(null);
        foreach (var reply in new[] { await nobody.SendAsync(Trades(runId[..12])), await rig.CallAsync(null, Trades(runId[..12])) })
        {
            Assert.True(reply.Ok, Json.Write(reply.Error));
            var whole = Data(reply);
            Assert.Equal(total, whole.GetProperty("count").GetInt32());
            Assert.Equal(StrategyStore.MaxTradeRows, whole.GetProperty("limit").GetInt32());
            Assert.False(whole.GetProperty("more").GetBoolean());
            Assert.Equal(recorded.Select(Trade), whole.GetProperty("trades").EnumerateArray().Select(Trade));

            // THE ROW AS RECORDED.
            Assert.Equal(row.VersionId, whole.GetProperty("version_id").GetString());
            Assert.Equal("attempt-run-trace", whole.GetProperty("attempt").GetString());
            Assert.Equal(set.Id, whole.GetProperty("dataset_id").GetInt64());
            Assert.Equal(set.NormalisedSha256, whole.GetProperty("dataset_sha256").GetString());
            Assert.Equal(JsonValueKind.Null, whole.GetProperty("from").ValueKind);
            Assert.Equal(JsonValueKind.Null, whole.GetProperty("to").ValueKind);
            Assert.Equal(answer.GetProperty("execution_model").GetString(), whole.GetProperty("execution_model").GetString());
            Assert.Equal(answer.GetProperty("increment_source").GetString(), whole.GetProperty("increment_source").GetString());
            Assert.Equal(answer.GetProperty("friction_source").GetString(), whole.GetProperty("friction_source").GetString());
            Assert.Equal("COMPLETED", whole.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, whole.GetProperty("fault_reason").ValueKind);
            Assert.Equal(answer.GetProperty("trace_sha256").GetString(), whole.GetProperty("trace_sha256").GetString());
            var metrics = answer.GetProperty("metrics");
            var figures = whole.GetProperty("figures");
            foreach (var name in new[] { "bars", "signals", "fills", "trades", "wins", "exposure_bars", "missing_minutes", "faults" })
                Assert.Equal(metrics.GetProperty(name).GetInt64(), figures.GetProperty(name).GetInt64());
            foreach (var name in new[] { "gross_pnl", "fees", "net_pnl", "max_drawdown" })
                Assert.Equal(metrics.GetProperty(name).GetDecimal(), figures.GetProperty(name).GetDecimal());
            Assert.Contains("no row is a record of a fill", whole.GetProperty("note").GetString()!, StringComparison.Ordinal);
            Assert.Contains("'pnl' is GROSS", whole.GetProperty("note").GetString()!, StringComparison.Ordinal);
        }

        // NOTHING WAS RUN, CHARGED OR WRITTEN.
        Assert.Equal(runs, rig.Gw.Strategies.RunCount);

        // THE REAL CLI, ON THE DEFAULT PIPE, AS THE ASSEMBLY'S OWN LAUNCH: five, then the rest from where it stopped.
        await using var cliServer = new GatewayPipeServer(rig.Gw, IpcToken.Ensure());
        cliServer.Start();
        var first = await Build.RunTradeAsync("run", "trades", "--run", runId[..12], "--limit", "5", "--json");
        Assert.True(first.Code == 0, first.Err + first.Out);
        using (var doc = JsonDocument.Parse(first.Out))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(5, data.GetProperty("count").GetInt32());
            Assert.True(data.GetProperty("more").GetBoolean());
            Assert.Equal(4, data.GetProperty("next_after").GetInt64());
        }

        var rest = await Build.RunTradeAsync("run", "trades", runId, "--after", "4", "--json");
        Assert.True(rest.Code == 0, rest.Err + rest.Out);
        using (var doc = JsonDocument.Parse(rest.Out))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(total - 5, data.GetProperty("count").GetInt32());
            Assert.Equal(5, data.GetProperty("trades")[0].GetProperty("ordinal").GetInt32());
        }
    }

    /// <summary>
    /// THE MINUTES OF EVERY INSTANT IN <paramref name="text"/> — ISO or the refusals' <c>u</c> form — so a reply can be read
    /// for one at or after a cutoff wherever it sits.
    /// </summary>
    static IEnumerable<DateTimeOffset> Instants(string text) =>
        Regex.Matches(text, @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})?")
            .Select(m => DateTimeOffset.Parse(m.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    /// <summary>
    /// A VERDICT OVER <paramref name="set"/>: the judgeable version frozen before its window, the Research Director's run of
    /// it over the months it may see, and the verdict — which records the referee's run over the held-back months, its
    /// trades in them. Both runs, as the ledger holds them.
    /// </summary>
    async Task<(StrategyRunRow Held, StrategyRunRow Research, PromotionRow Promotion)> Judged(Rig rig, DatasetRecord set)
    {
        var cutoff = set.HoldoutFrom!.Value;
        var program = GivenProgram(CouncilRoles.Research, "judgeable.strategy", JudgeableText);
        var parsed = StrategyParser.Parse(JudgeableText).Program!;
        new StrategyStore(rig.Db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            Start, CouncilRoles.Research, "attempt-run-trace"));

        var researched = await rig.CallAsync(CouncilRoles.Research, Backtest(program, set, Start, cutoff.AddMinutes(-1)));
        Assert.True(researched.Ok, Json.Write(researched.Error));
        var verdict = await rig.CallAsync(CouncilRoles.Research, new IpcRequest
        {
            Op = Ops.Verdict, Session = "run-trace", RequestId = "run-trace-verdict",
            Args = Args(("version", parsed.StrategyId), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
        });
        Assert.True(verdict.Ok, Json.Write(verdict.Error));
        log.WriteLine($"verdict: {Data(verdict).GetProperty("verdict")}");

        var held = Assert.Single(rig.Gw.Strategies.Runs(), r => r.Role == Referee.RunRole);
        var trades = rig.Gw.Strategies.TradesOf(held.Id);
        Assert.NotEmpty(trades);
        Assert.All(trades, t => Assert.True(t.EntryBar >= cutoff && t.ExitBar >= cutoff, $"{t.EntryBar:u} is not held back"));
        var promotion = Assert.Single(rig.Gw.Promotions.For(parsed.StrategyId));
        Assert.Equal(held.Id, promotion.HoldoutRunId);
        return (held, rig.Gw.Strategies.RunById(Data(researched).GetProperty("run_id").GetString()!)!, promotion);
    }

    /// <summary>
    /// A REFUSAL OF THE REFEREE'S HOLDOUT RUN THAT CARRIES NOTHING OF IT: <c>HOLDOUT_WITHHELD</c>, no data, words naming it
    /// the referee's holdout run and <c>trade verdict</c> as what serves its verdict and reason class — and, anywhere in the
    /// reply, no instant at or after the cutoff, not its trace hash and not one of its money figures.
    /// </summary>
    void NothingOf(IpcResponse reply, StrategyRunRow held, string? role, string asked, string leg)
    {
        var text = Json.Write(reply);
        log.WriteLine($"{leg}: {(reply.Ok ? "SERVED " + text[..Math.Min(text.Length, 200)] : $"REFUSED {reply.Error?.Code}: {reply.Error?.Message}")}");

        Assert.False(reply.Ok, $"{leg}: the referee's holdout run was served");
        Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), reply.Error!.Code);
        Assert.Null(reply.Data);
        Assert.StartsWith($"run {asked} is the referee's holdout run of version {held.VersionId[..12]}", reply.Error.Message, StringComparison.Ordinal);
        Assert.Contains("'trade verdict --version", reply.Error.Message, StringComparison.Ordinal);
        Assert.Contains(role is null ? "a caller that proved no role" : CouncilRoles.Title(role), reply.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(held.TraceSha256, text, StringComparison.Ordinal);
        foreach (var figure in new[] { held.GrossPnl, held.Fees, held.NetPnl, held.MaxDrawdown })
            if (figure is { } f && f.ToString(CultureInfo.InvariantCulture) is { Length: >= 3 } spelled)
                Assert.DoesNotContain(spelled, text, StringComparison.Ordinal);
        foreach (var at in Instants(text))
            Assert.True(at < Cutoff, $"{leg}: the reply carries {at:u}, at or after the cutoff {Cutoff:u}");
        Assert.DoesNotContain("entry_bar", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// (b) THE REFEREE'S HOLDOUT RUN IS REFUSED WITH NOTHING OF IT. After a verdict over the held-back hour, its run is asked
    /// for by its whole id and by its first twelve characters, by the Research Director — whose program it judged — and by
    /// a caller that proved no role, over the pipe and in process: <c>HOLDOUT_WITHHELD</c> every time, and nothing of it
    /// anywhere in the reply (<see cref="NothingOf"/>). And EACH CLAUSE ALONE refuses: a row the referee's mark names that
    /// no promotion does, and a research row a promotion names — each over the months research may see, so nothing but the
    /// clause stands between it and the caller. RED with (a) removed from the reader.
    /// </summary>
    [Fact]
    public async Task The_referees_holdout_run_is_refused_with_nothing_of_it()
    {
        await using var rig = await Ready();
        var plain = Recorded(rig.Db, "BTCUSDT", "v1", 120);
        var (done, campaign) = rig.Gw.SetHoldout(plain.Id, Cutoff, EvaluationClass.Research);
        Assert.True(done.Ok, done.Why);
        Assert.NotNull(campaign);
        var set = rig.Gw.Datasets.ById(plain.Id)!;
        var (held, research, promotion) = await Judged(rig, set);
        log.WriteLine($"holdout run {held.Id[..12]}: {held.Trades} trades, net {held.NetPnl}; research run {research.Id[..12]}");

        foreach (var role in new[] { CouncilRoles.Research, null })
        {
            await using var client = await rig.Dial(role);
            foreach (var asked in new[] { held.Id, held.Id[..12] })
            {
                NothingOf(await client.SendAsync(Trades(asked)), held, role, asked, $"{asked[..12]} as {Who(role)} over the pipe");
                NothingOf(await rig.CallAsync(role, Trades(asked, 0, 1)), held, role, asked, $"{asked[..12]} as {Who(role)} in process");
            }
        }

        // EACH CLAUSE ALONE, over the months research may see — the research run's own window, served until now.
        var served = await rig.CallAsync(CouncilRoles.Operations, Trades(research.Id));
        Assert.True(served.Ok, Json.Write(served.Error));

        var store = new StrategyStore(rig.Db);
        var marked = research with { Id = new string('e', 12) + research.Id[12..], Role = Referee.RunRole };
        store.RecordRun(marked, [.. store.TradesOf(research.Id).Select(t => t with { RunId = marked.Id })]);
        rig.Gw.Promotions.Record(promotion with { HoldoutRunId = research.Id });

        foreach (var (row, clause) in new[] { (marked, "marked the referee's, named by no promotion"), (research, "a research row a promotion names") })
            foreach (var role in new[] { CouncilRoles.Operations, null })
                NothingOf(await rig.CallAsync(role, Trades(row.Id)), row, role, row.Id, $"{clause}, as {Who(role)}");
    }

    /// <summary>
    /// (c) A CUTOFF SET AFTER THE RUN WITHHOLDS IT — read from the ledger at each read, never as it stood when the run was
    /// made. The Research Director runs the program over all 120 minutes and over the first 30; the Operations Director
    /// reads both. Then the owner sets a cutoff at minute 60 — on the run's OWN dataset, or on ANOTHER dataset of another
    /// pair over the same minutes — and the whole run is refused with <c>HOLDOUT_WITHHELD</c> in the holdout's words, to
    /// every caller, never cut short; the run ending before the new window is still served, whole. RED with (b) removed
    /// from the reader, and with the dataset read once instead of at each read.
    /// </summary>
    [Theory]
    [InlineData("own")]
    [InlineData("another")]
    public async Task A_cutoff_set_after_the_run_withholds_it(string where)
    {
        await using var rig = await Ready();
        var set = Recorded(rig.Db, "BTCUSDT", "v1", 120);
        var other = Recorded(rig.Db, "ETHUSDT", "v1", 120);

        await using var research = await rig.Dial(CouncilRoles.Research);
        var program = GivenProgram(CouncilRoles.Research);
        var whole = Data(await research.SendAsync(Backtest(program, set, Start, Start.AddMinutes(119)))).GetProperty("run_id").GetString()!;
        var early = Data(await research.SendAsync(Backtest(program, set, Start, Start.AddMinutes(29)))).GetProperty("run_id").GetString()!;

        await using var operations = await rig.Dial(CouncilRoles.Operations);
        foreach (var run in new[] { whole, early })
        {
            var before = await operations.SendAsync(Trades(run));
            Assert.True(before.Ok, Json.Write(before.Error));
            Assert.Equal(rig.Gw.Strategies.TradesOf(run).Count, Data(before).GetProperty("count").GetInt32());
        }

        // THE OWNER'S PRESS, AFTER BOTH RUNS.
        var held = where == "own" ? set : other;
        Assert.True(rig.Gw.Datasets.SetHoldout(held.Id, Cutoff, EvaluationClass.Research).Ok);
        var expected = where == "own"
            ? $"dataset {set.Id} (BTCUSDT 1m v1) holds out every bar from {Cutoff:u} onwards"
            : Assert.Single(TapeHoldout.Pipe(null, rig.Gw.Datasets).Windows()).BarWords;

        await using var nobody = await rig.Dial(null);
        foreach (var (who, reply) in new[]
                 {
                     ("the Operations Director over the pipe", await operations.SendAsync(Trades(whole))),
                     ("a caller that proved no role over the pipe", await nobody.SendAsync(Trades(whole[..12]))),
                     ("the Research Director in process", await rig.CallAsync(CouncilRoles.Research, Trades(whole, 3, 2)))
                 })
        {
            log.WriteLine($"{where}, {who}: {(reply.Ok ? "SERVED " + Data(reply).GetProperty("count") : $"REFUSED {reply.Error?.Code}: {reply.Error?.Message}")}");
            Assert.False(reply.Ok, $"{who} was served a run whose window a cutoff set after it now reaches");
            Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), reply.Error!.Code);
            Assert.Null(reply.Data);
            Assert.StartsWith("the trades of run ", reply.Error.Message, StringComparison.Ordinal);
            Assert.Contains($"it ran over the bars from {Start:u} to {Start.AddMinutes(119):u} of dataset {set.Id} (BTCUSDT 1m v1)",
                reply.Error.Message, StringComparison.Ordinal);
            Assert.Contains(expected, reply.Error.Message, StringComparison.Ordinal);
            Assert.Contains("REFUSED rather than quietly cut short", reply.Error.Message, StringComparison.Ordinal);
        }

        // THE RUN THAT ENDS BEFORE THE NEW WINDOW IS STILL SERVED, WHOLE.
        var still = await nobody.SendAsync(Trades(early));
        Assert.True(still.Ok, Json.Write(still.Error));
        Assert.Equal(rig.Gw.Strategies.TradesOf(early).Select(Trade), Data(still).GetProperty("trades").EnumerateArray().Select(Trade));
    }

    /// <summary>
    /// (d) LIMITS AND IDS ARE REFUSED IN WORDS, NEVER CLAMPED. A limit of 0, of 1,001 and of 2.5, an ordinal of −1 and of
    /// 1.5, and no run at all are each <c>INVALID_REQUEST</c> naming what was wrong; so are an id that names no run — whole
    /// or as a start — a start two runs share, and eleven characters, each naming what was asked. None of them is answered
    /// with the nearest thing: no data comes back with any.
    /// </summary>
    [Fact]
    public async Task Limits_and_ids_are_refused_in_words_never_clamped()
    {
        await using var rig = await Ready();
        var set = Recorded(rig.Db, "BTCUSDT", "v1", 120);
        await using var research = await rig.Dial(CouncilRoles.Research);
        var run = Data(await research.SendAsync(Backtest(GivenProgram(CouncilRoles.Research), set, null, null))).GetProperty("run_id").GetString()!;

        // TWO RUNS WHOSE IDS SHARE THEIR FIRST THIRTEEN CHARACTERS, recorded as the app records any run.
        var store = new StrategyStore(rig.Db);
        var row = store.RunById(run)!;
        var twins = new[] { "abcdefabcdef1" + new string('0', 51), "abcdefabcdef1" + new string('1', 51) };
        foreach (var twin in twins) store.RecordRun(row with { Id = twin }, []);

        await using var operations = await rig.Dial(CouncilRoles.Operations);
        foreach (var (args, words) in new (IpcRequest, string)[]
                 {
                     (Trades(run, null, 0), "'limit' is how many trades one answer may hold — a whole number from 1 to 1000 — and '0' is not one"),
                     (Trades(run, null, 1001), "and '1001' is not one"),
                     (Trades(run, null, 2.5m), "and '2.5' is not one"),
                     (Trades(run, -1, null), "'after' is a trade's ordinal — a whole number from 0"),
                     (Trades(run, 1.5m, null), "and '1.5' is not one"),
                     (new IpcRequest { Op = Ops.RunTrades, Session = "run-trace" }, "'run' is required"),
                     (Trades(new string('9', 64)), $"there is no run '{new string('9', 64)}' in this installation's strategy ledger"),
                     (Trades("999999999999"), "there is no run '999999999999'"),
                     (Trades(twins[0][..13]), $"'{twins[0][..13]}' is the start of more than one run's id, so it names none of them"),
                     (Trades(twins[0][..12]), $"'{twins[0][..12]}' is the start of more than one run's id"),
                     (Trades(run[..11]), $"'{run[..11]}' is 11 character(s) long, and a run is named by its whole id or by the first 12")
                 })
        {
            var reply = await operations.SendAsync(args);
            log.WriteLine($"{Json.Write(args.Args)}: {(reply.Ok ? "SERVED" : $"REFUSED {reply.Error?.Code}: {reply.Error?.Message}")}");
            Assert.False(reply.Ok, $"{Json.Write(args.Args)} was answered");
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
            Assert.Contains(words, reply.Error.Message, StringComparison.Ordinal);
            Assert.Null(reply.Data);
        }

        // AND EACH TWIN IS SERVED BY ITS WHOLE ID, EMPTY, WHILE THE RUN ITSELF IS SERVED BY ITS FIRST TWELVE.
        foreach (var twin in twins)
            Assert.Equal(0, Data(await operations.SendAsync(Trades(twin))).GetProperty("trade_count").GetInt32());
        Assert.True((await operations.SendAsync(Trades(run[..12], null, 1000))).Ok);
    }
}
