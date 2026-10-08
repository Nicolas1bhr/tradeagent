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
/// THE BARS' HOLDOUT OVER EVERY DATASET AND THE FORWARD DOOR (<c>U-bar-holdout</c>; seat A's decision of 2026-10-08): a
/// cutoff on any dataset holds every pair's bars back over its window — from every dataset and the forward bars, as the
/// tape's rows — and no caller on the agent-facing pipe reads one; the referee still does.
///
/// <para><b>What was wrong before this.</b> The bars checked THEIR OWN dataset's cutoff alone, while every Download press
/// records a new version of the pair with none. The survey's probe (<c>s-bar-holdout</c>, RED on <c>d73ecd59</c>) read A's
/// held-back hour through B — <c>data-bars</c> served it to the Research Director and to a caller that proved no role, and
/// <c>backtest --dataset B</c> answered metrics and trades over it — and through <c>data-bars --source forward</c>, on the
/// premise that every forward bar post-dates every freeze. (a) and (b) are that probe, asserting the protection.</para>
///
/// <para>The ledger every test reads: A is BTCUSDT 1m v1, 120 minutes from 2026-08-01 00:00Z, cut at 01:00 by the owner's
/// own press (<c>TradingGateway.SetHoldout</c>, which opens its campaign), so its window is [01:00, 02:00). B is BTCUSDT 1m
/// v2, recorded after it with NO cutoff, as a second Download press records one, over 180 minutes. C is ETHUSDT 1m v1 over
/// the same 180 minutes, no cutoff. The forward ledger holds BTCUSDT's same 180 minutes.</para>
/// </summary>
public class BarHoldoutOverPipeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A's cutoff, minute 60; its window runs to the close of its last bar, minute 120.</summary>
    static readonly DateTimeOffset Cutoff = Start.AddMinutes(60);

    static readonly DateTimeOffset WindowEnd = Start.AddMinutes(120);

    /// <summary>Every caller the pipe can carry: each director's launch, and a connection that proved no role.</summary>
    static readonly string?[] EveryCaller = [.. CouncilRoles.All, null];

    const string Program = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// A PROFITABLE PROGRAM THAT DECLARES THE THREE EXECUTION BOUNDS — the one <c>HoldoutOverPipeTests</c>' verdict test
    /// judges, over the same bars cycling 96 → 105.
    /// </summary>
    const string JudgeableText =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    /// <summary>The wire by hand, as <c>HoldoutOverPipeTests</c> speaks it: the machine's key on every frame, the grant on the hello.</summary>
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

    /// <summary>A gateway serving the pipe, and the ledger above.</summary>
    sealed record Rig(TradingGateway Gw, Database Db, GatewayPipeServer Server, AgentGrants Grants, string Pipe,
        DatasetRecord A, DatasetRecord B, DatasetRecord C) : IAsyncDisposable
    {
        public async Task<Raw> Dial(string? role)
        {
            var client = await Raw.ConnectAsync(Pipe);
            var grant = role is null ? null : Grants.Issue(role, "attempt-bar-holdout").Token;
            var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
            Assert.True(hello.Ok, Json.Write(hello.Error));
            return client;
        }

        /// <summary>The same frame through the other door into the same handler: the in-process worker surface.</summary>
        public Task<IpcResponse> CallAsync(string? role, IpcRequest req) => Server.CallAsync(req, role, "attempt-bar-holdout");

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Db.Dispose();
        }
    }

    static async Task<Rig> Ready()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = "ta-bh-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        // A: THE OWNER'S PRESS — the cutoff and its campaign in one transaction.
        var a = Recorded(db, "BTCUSDT", "v1", 120);
        var (done, campaign) = gw.SetHoldout(a.Id, Cutoff, EvaluationClass.Research);
        Assert.True(done.Ok, done.Why);
        Assert.NotNull(campaign);

        var b = Recorded(db, "BTCUSDT", "v2", 180);
        var c = Recorded(db, "ETHUSDT", "v1", 180);
        Forward(db, "BTCUSDT", 180);

        Assert.Equal(b.Id, gw.Datasets.Newest("BTCUSDT")!.Id);
        Assert.Null(b.HoldoutFrom);
        return new Rig(gw, db, server, grants, pipe, gw.Datasets.ById(a.Id)!, b, c);
    }

    /// <summary>
    /// A DATASET THE LEDGER RECORDED, hashes and all, with no cutoff: <paramref name="bars"/> bars of
    /// <paramref name="interval"/> from <see cref="Start"/>, closes cycling 96 → 105.
    /// </summary>
    static DatasetRecord Recorded(Database db, string pair, string version, int bars, string interval = "1m")
    {
        var step = KlineNormaliser.BarLength(interval);
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
                $"{(Start + i * step).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, interval, version, 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start + (bars - 1) * step, 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]));
        return store.ById(id)!;
    }

    /// <summary>Forward bars as the app's own collector writes them: through the store, closed, in one transaction.</summary>
    static void Forward(Database db, string pair, int bars)
    {
        var klines = new List<ForwardBars.Kline>();
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            klines.Add(new ForwardBars.Kline(Start.AddMinutes(i), close, close + 1m, close - 1m, close, 1.5m,
                Start.AddMinutes(i + 1).AddMilliseconds(-1)));
        }

        var readAt = Start.AddMinutes(bars + 1);
        new ForwardBarStore(db).Append(new ForwardFetchAttempt
        {
            Source = ForwardBars.Source, Symbol = pair,
            Url = $"http://127.0.0.1:0/api/v3/klines?symbol={pair}&interval=1m",
            RequestedAt = readAt.AddSeconds(-1), ReceivedAt = readAt, HttpStatus = 200, BodySha256 = new string('c', 64)
        }, klines);
    }

    /// <summary>A program in a role's own folder, so the <c>backtest</c> op has something to read.</summary>
    static string GivenProgram(string role, string name = "bar-holdout.strategy", string text = Program)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text);
        return "strategies/" + name;
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static Dictionary<string, JsonElement> Args(params (string Key, string? Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs)
            if (value is not null) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    static IpcRequest Bars(string pair, DateTimeOffset? from, DateTimeOffset? to, string? source = null) => new()
    {
        Op = Ops.DataBars, Session = "bar-holdout",
        Args = Args(("pair", pair), ("from", from is { } f ? Iso(f) : null), ("to", to is { } t ? Iso(t) : null), ("source", source))
    };

    static IpcRequest Backtest(string program, DatasetRecord set, DateTimeOffset from, DateTimeOffset to) => new()
    {
        Op = Ops.Backtest, Session = "bar-holdout", RequestId = "bar-holdout-" + Guid.NewGuid().ToString("n")[..12],
        Args = Args(("strategy", program), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)),
            ("from", Iso(from)), ("to", Iso(to)), ("increment", "1"))
    };

    static string Who(string? role) => role is null ? "a caller that proved no role" : $"the {CouncilRoles.Title(role)}";

    /// <summary>What a reply served, for the log: "REFUSED code: message…" or "SERVED n bars …".</summary>
    static string Said(IpcResponse r)
    {
        if (!r.Ok)
        {
            var message = r.Error?.Message ?? "";
            return $"REFUSED {r.Error?.Code}: {(message.Length > 220 ? message[..220] + "…" : message)}";
        }

        var d = Data(r);
        if (d.TryGetProperty("bars", out var bars) && bars.ValueKind == JsonValueKind.Array)
            return $"SERVED {bars.GetArrayLength()} bars"
                + (d.TryGetProperty("dataset_id", out var ds) ? $" from dataset {ds}" : "")
                + (bars.GetArrayLength() > 0
                    ? $", first open_time {bars[0].GetProperty("open_time")}, last open_time {bars[bars.GetArrayLength() - 1].GetProperty("open_time")}"
                    : "");
        if (d.TryGetProperty("metrics", out var m))
            return $"SERVED backtest over dataset {d.GetProperty("dataset_id")}: metrics.bars={m.GetProperty("bars")} "
                + $"trades={d.GetProperty("trade_count")}";
        return "SERVED " + d;
    }

    /// <summary>
    /// A REFUSAL IN A's WINDOW'S WORDS, or a leak recorded. Every refusal names A, its cutoff and its window's close, says it
    /// was refused rather than cut short, names the caller and the read, and carries no bar.
    /// </summary>
    void Withheld(Rig rig, List<string> leaks, string leg, IpcResponse reply, string? role, string read)
    {
        var line = $"{leg}: {Said(reply)}";
        log.WriteLine(line);
        if (reply.Ok)
        {
            leaks.Add(line);
            return;
        }

        var message = reply.Error!.Message;
        Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), reply.Error.Code);
        Assert.Contains($"dataset {rig.A.Id} (BTCUSDT 1m v1) holds out every bar from {Cutoff:u}", message, StringComparison.Ordinal);
        Assert.Contains($"up to the close of its last bar at {WindowEnd:u}", message, StringComparison.Ordinal);
        Assert.Contains(read, message, StringComparison.Ordinal);
        Assert.Contains("REFUSED rather than quietly cut short", message, StringComparison.Ordinal);
        Assert.Contains(role is null ? "a caller that proved no role" : CouncilRoles.Title(role), message, StringComparison.Ordinal);
        Assert.DoesNotContain("open_time", Json.Write(reply), StringComparison.Ordinal);
    }

    /// <summary>
    /// (a) A SECOND DATASET SERVES NO BAR INSIDE ANOTHER'S HOLDOUT WINDOW — the survey's probe, legs 1-4 and 6, asserting
    /// the protection: <c>data-bars</c> over A's window, for BTCUSDT (B, the newest, un-held) and for ETHUSDT (C, another
    /// pair), for each director and a caller with none, over the pipe and through the in-process worker surface; and
    /// <c>backtest --dataset B</c> over A's window for each director — all <c>HOLDOUT_WITHHELD</c> naming A's cutoff, and no
    /// run recorded. The control: <c>backtest --dataset A</c> is refused by A's own cutoff, as before. RED on the base.
    /// </summary>
    [Fact]
    public async Task A_second_dataset_serves_no_bar_inside_anothers_holdout_window()
    {
        await using var rig = await Ready();
        var leaks = new List<string>();
        var last = WindowEnd.AddMinutes(-1);

        foreach (var role in EveryCaller)
        {
            await using var client = await rig.Dial(role);
            foreach (var (pair, set) in new[] { ("BTCUSDT", rig.B), ("ETHUSDT", rig.C) })
            {
                var read = $"dataset {set.Id} ({pair} 1m {set.Version})";
                Withheld(rig, leaks, $"data-bars {pair} [{Iso(Cutoff)}, {Iso(last)}] as {Who(role)} over the pipe",
                    await client.SendAsync(Bars(pair, Cutoff, last)), role, read);
                Withheld(rig, leaks, $"data-bars {pair} [{Iso(Cutoff)}, {Iso(last)}] as {Who(role)} in process",
                    await rig.CallAsync(role, Bars(pair, Cutoff, last)), role, read);
            }
        }

        foreach (var role in CouncilRoles.All)
        {
            await using var client = await rig.Dial(role);
            var program = GivenProgram(role);

            Withheld(rig, leaks, $"backtest --dataset {rig.B.Id} (B) over A's window as {Who(role)}",
                await client.SendAsync(Backtest(program, rig.B, Cutoff, last)), role, $"dataset {rig.B.Id} (BTCUSDT 1m v2)");

            // THE CONTROL: A's own cutoff refuses the same window over A, exactly as it always did.
            var own = await client.SendAsync(Backtest(program, rig.A, Cutoff, last));
            log.WriteLine($"CONTROL backtest --dataset {rig.A.Id} (A) over its own window as {Who(role)}: {Said(own)}");
            Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), own.Error?.Code);
            Assert.Contains($"dataset {rig.A.Id} (BTCUSDT 1m v1) holds out every bar from {Cutoff:u}", own.Error!.Message, StringComparison.Ordinal);
        }

        log.WriteLine(leaks.Count == 0 ? "VERDICT: NO LEAK" : $"VERDICT: LEAK in {leaks.Count} legs");
        Assert.True(leaks.Count == 0, "a caller on the pipe read A's held-back bars through another dataset: " + string.Join(" | ", leaks));
        Assert.Empty(rig.Gw.Strategies.Runs());
    }

    /// <summary>
    /// (c) A READ WHOLLY OUTSIDE EVERY WINDOW IS SERVED FROM THE SECOND DATASET — before A's cutoff, and from its window's
    /// close on, whole: B's and C's bars, the forward bars and a backtest over B, to every caller; and the two minutes that
    /// bound the window are each refused, so the boundary is where A says it is.
    /// </summary>
    [Fact]
    public async Task A_read_wholly_outside_every_window_is_served_from_the_second_dataset()
    {
        await using var rig = await Ready();
        var before = (Start, Cutoff.AddMinutes(-1));
        var after = (WindowEnd, Start.AddMinutes(179));

        foreach (var role in EveryCaller)
        {
            await using var client = await rig.Dial(role);
            foreach (var (pair, source, set) in new (string, string?, DatasetRecord?)[] { ("BTCUSDT", null, rig.B), ("ETHUSDT", null, rig.C), ("BTCUSDT", "forward", null) })
                foreach (var (from, to) in new (DateTimeOffset, DateTimeOffset?)[] { before, after, (WindowEnd, null) })
                {
                    var reply = await client.SendAsync(Bars(pair, from, to, source));
                    log.WriteLine($"data-bars {pair} {source ?? "archive"} [{Iso(from)}, {(to is { } t ? Iso(t) : "no end")}] as {Who(role)}: {Said(reply)}");

                    Assert.True(reply.Ok, Json.Write(reply.Error));
                    var bars = Data(reply).GetProperty("bars").EnumerateArray().Select(b => b.GetProperty("open_time").GetDateTimeOffset()).ToList();
                    Assert.Equal(60, bars.Count);
                    Assert.Equal(from, bars[0]);
                    Assert.All(bars, at => Assert.True(at < Cutoff || at >= WindowEnd, $"{at:u} is inside A's window and was served"));
                    if (set is not null) Assert.Equal(set.Id, Data(reply).GetProperty("dataset_id").GetInt64());
                }

            // THE BOUNDARY, BOTH SIDES: the cutoff's own minute and the window's last minute are each held.
            Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), (await client.SendAsync(Bars("BTCUSDT", Start, Cutoff))).Error?.Code);
            Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), (await client.SendAsync(Bars("BTCUSDT", WindowEnd.AddMinutes(-1), Start.AddMinutes(179)))).Error?.Code);
            Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), (await client.SendAsync(Bars("BTCUSDT", Start, Cutoff, "forward"))).Error?.Code);
        }

        foreach (var role in CouncilRoles.All)
        {
            await using var client = await rig.Dial(role);
            var program = GivenProgram(role);
            foreach (var (from, to) in new[] { before, after })
            {
                var ran = await client.SendAsync(Backtest(program, rig.B, from, to));
                log.WriteLine($"backtest --dataset {rig.B.Id} (B) [{Iso(from)}, {Iso(to)}] as {Who(role)}: {Said(ran)}");
                Assert.True(ran.Ok, Json.Write(ran.Error));
                Assert.Equal(60, Data(ran).GetProperty("metrics").GetProperty("bars").GetInt64());
            }
        }
    }

    /// <summary>
    /// (d) A BAR WHOSE SPAN CROSSES INTO A WINDOW IS REFUSED: a bar is the market from its open to its close, so a 5m bar
    /// opening at 01:00 is inside a window that a 1m cutoff opens at 01:02. D is ETHUSDT 5m; A2 is BTCUSDT 1m cut at 01:02.
    /// Through <c>data-bars</c> (the feed's half is <c>BarHoldoutTests</c>); the 5m bar ending at 01:00 is served, and so
    /// is the 1m bar ending at 01:02.
    /// </summary>
    [Fact]
    public async Task A_bar_whose_span_crosses_into_a_window_is_refused()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-bh-" + Guid.NewGuid().ToString("n")[..12]);

        var cutoff = Start.AddMinutes(62);
        var a2 = Recorded(db, "BTCUSDT", "v1", 120);
        Assert.True(gw.Datasets.SetHoldout(a2.Id, cutoff, EvaluationClass.Research).Ok);
        var b2 = Recorded(db, "BTCUSDT", "v2", 180);
        Recorded(db, "ETHUSDT", "v1", 36, "5m");

        foreach (var role in EveryCaller)
        {
            async Task<IpcResponse> Ask(string pair, DateTimeOffset from, DateTimeOffset to)
            {
                var reply = await server.CallAsync(Bars(pair, from, to), role, "attempt-bar-holdout");
                log.WriteLine($"data-bars {pair} [{Iso(from)}, {Iso(to)}] as {Who(role)}: {Said(reply)}");
                return reply;
            }

            foreach (var refused in new[]
                     {
                         await Ask("ETHUSDT", Start, Start.AddMinutes(60)),                      // [01:00, 01:05) crosses 01:02
                         await Ask("ETHUSDT", Start.AddMinutes(115), Start.AddMinutes(175)),    // [01:55, 02:00) is inside
                         await Ask("BTCUSDT", Start, cutoff)                                     // the cutoff's own minute, through B2
                     })
            {
                Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), refused.Error?.Code);
                Assert.Contains($"dataset {a2.Id} (BTCUSDT 1m v1) holds out every bar from {cutoff:u}", refused.Error!.Message, StringComparison.Ordinal);
            }

            var fiveMinute = await Ask("ETHUSDT", Start, Start.AddMinutes(55));
            Assert.True(fiveMinute.Ok, Json.Write(fiveMinute.Error));
            Assert.Equal(12, Data(fiveMinute).GetProperty("bars").GetArrayLength());

            var afterwards = await Ask("ETHUSDT", Start.AddMinutes(120), Start.AddMinutes(175));
            Assert.True(afterwards.Ok, Json.Write(afterwards.Error));
            Assert.Equal(12, Data(afterwards).GetProperty("bars").GetArrayLength());

            var minute = await Ask("BTCUSDT", Start, cutoff.AddMinutes(-1));
            Assert.True(minute.Ok, Json.Write(minute.Error));
            Assert.Equal(62, Data(minute).GetProperty("bars").GetArrayLength());
        }
    }

    /// <summary>
    /// (e) THE REFEREE READS ITS CAMPAIGN'S DATASET WITH A SECOND DATASET PRESENT — the verdict over A, its held-back hour
    /// read in process from the cutoff on, while B holds the same minutes un-held: promoted, A's cutoff unmoved, and the
    /// door still shut afterwards through B.
    /// </summary>
    [Fact]
    public async Task The_referee_reads_its_campaign_dataset_with_a_second_dataset_present()
    {
        await using var rig = await Ready();
        await using var client = await rig.Dial(CouncilRoles.Research);
        var program = GivenProgram(CouncilRoles.Research, "judgeable.strategy", JudgeableText);

        // Frozen BEFORE the holdout window begins, so the scoring policy's forward-evidence clause can be met.
        var parsed = StrategyParser.Parse(JudgeableText).Program!;
        new StrategyStore(rig.Db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            Start, CouncilRoles.Research, "attempt-bar-holdout"));

        var researched = await client.SendAsync(Backtest(program, rig.A, Start, Cutoff.AddMinutes(-1)));
        Assert.True(researched.Ok, Json.Write(researched.Error));

        var verdict = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Verdict, Session = "bar-holdout", RequestId = "bar-holdout-verdict",
            Args = Args(("version", parsed.StrategyId), ("dataset", rig.A.Id.ToString(CultureInfo.InvariantCulture)))
        });
        log.WriteLine(Json.Write(verdict.Error ?? (object)Data(verdict)));
        Assert.True(verdict.Ok, Json.Write(verdict.Error));
        Assert.Equal("promoted", Data(verdict).GetProperty("verdict").GetString());
        Assert.Equal(Cutoff, rig.Gw.Datasets.ById(rig.A.Id)!.HoldoutFrom);

        var bars = await client.SendAsync(Bars("BTCUSDT", Start, WindowEnd.AddMinutes(-1)));
        log.WriteLine(Said(bars));
        Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), bars.Error?.Code);
        Assert.Contains($"dataset {rig.B.Id} (BTCUSDT 1m v2)", bars.Error!.Message, StringComparison.Ordinal);
    }
}
