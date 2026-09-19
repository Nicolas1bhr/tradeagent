using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
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
/// THE HOLDOUT AS AN AGENT ACTUALLY MEETS IT: over the pipe, on a dataset the ledger really recorded,
/// with the launch grant `U-containment` put on the connection.
///
/// <para>The unit tests hold the rule and the store. This holds the two things only the wire settles:
/// that NO op on this channel writes the cutoff, and that no op on this channel serves a bar at or
/// after it — for the Research Director, for the chair, and for a connection that proved no role at
/// all, which is the case that had already been read as the chair once before (`U-containment`).</para>
///
/// <para>These tests speak the wire by hand rather than through <c>PipeClient</c>, as
/// <c>LaunchGrantTests</c> and <c>BacktestOverPipeTests</c> do: the interesting caller is the one that
/// does NOT do what the product's client does.</para>
/// </summary>
public class HoldoutOverPipeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minutes into the dataset at which the holdout begins. Half way, so both halves exist.</summary>
    const int HoldoutAtBar = 60;

    static string NewPipe() => "ta-hold-" + Guid.NewGuid().ToString("n")[..12];

    static async Task<(TradingGateway Gw, Database Db, Raw Client, IAsyncDisposable Server)> Connected(
        string? role = CouncilRoles.Research, string attempt = "attempt-h1")
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var client = await Raw.ConnectAsync(pipe);
        var grant = role is null ? null : grants.Issue(role, attempt).Token;
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
        Assert.True(hello.Ok, Json.Write(hello.Error));

        return (gw, db, client, server);
    }

    /// <summary>The wire by hand: the machine token on every frame, and the grant on the hello.</summary>
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

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    /// <summary>An instant as the wire carries one, so a refusal quotes what was really asked for.</summary>
    static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    /// <summary>
    /// A REAL DATASET WITH A REAL HOLDOUT: <paramref name="bars"/> one-minute bars on disk, hashes and
    /// all, and the cutoff written by the store the owner's window calls.
    /// </summary>
    static DatasetRecord Given(Database db, string pair = "BTCUSDT", int bars = 120, bool holdout = true,
        string evaluationClass = EvaluationClass.Research, TradingGateway? campaignFor = null)
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

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]);

        var store = new DatasetStore(db);
        var id = store.Record(record);
        if (holdout && campaignFor is { } gw)
        {
            // THE GATEWAY'S OWN PRESS, which writes the cutoff and OPENS THE CAMPAIGN in one
            // transaction. Only the verdict test below needs a campaign; the sweeps above are about
            // the cutoff itself and are left on the store's writer so that nothing they prove depends
            // on a campaign existing.
            var (done, campaign) = gw.SetHoldout(id, Start.AddMinutes(HoldoutAtBar), evaluationClass);
            Assert.True(done.Ok, done.Why);
            Assert.NotNull(campaign);
        }
        else if (holdout)
        {
            var done = store.SetHoldout(id, Start.AddMinutes(HoldoutAtBar), evaluationClass);
            Assert.True(done.Ok, done.Why);
        }

        return store.ById(id)!;
    }

    /// <summary>A program in a role's own folder, so the `backtest` op has something to read.</summary>
    static string GivenProgram(string role = CouncilRoles.Research, string name = "holdout.strategy",
        string? text = null)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name),
            text ?? "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n");
        return "strategies/" + name;
    }

    /// <summary>
    /// A PROFITABLE PROGRAM THAT DECLARES THE THREE EXECUTION BOUNDS, which is what the referee will
    /// agree to judge at all (`U-promote-bounds`) and what comes out ahead over bars cycling 96 → 105.
    /// </summary>
    const string JudgeableText =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    /// <summary>Every op name this build has, off <see cref="Ops"/> itself rather than a list kept here.</summary>
    static IReadOnlyList<string> EveryOp() =>
        [.. typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(op => op != Ops.Hello)
            .Distinct(StringComparer.Ordinal)];

    // ---- item 1: no op on this channel writes the cutoff -----------------------------------------

    /// <summary>
    /// NO OP ON THIS CHANNEL SETS, CLEARS OR MOVES THE HOLDOUT — and every op is asked, off
    /// <see cref="Ops"/>'s own fields, so an op added later is asked too without this test being edited.
    ///
    /// <para>Every frame carries the fields a caller would use to try — <c>holdout_from</c>,
    /// <c>holdout</c>, <c>evaluation_class</c> and <c>class</c> — and the sweep runs TWICE, once naming
    /// an instant EARLIER than the owner's cutoff and once naming one LATER. Both directions, because
    /// only one of them is a direction the store itself refuses: a handler that quietly honoured the
    /// field would be caught by the later pass even though the earlier pass could not tell it from the
    /// store's own refusal. The three ops that actually reach the dataset ledger (<c>data-list</c>,
    /// <c>data-bars</c> and <c>backtest</c>) are given the arguments they need to SUCCEED while carrying
    /// them, so this is not a sweep of ops that all failed for unrelated reasons.</para>
    ///
    /// <para>The mutant: a handler that reads <c>holdout_from</c> off the request. The defendant writing
    /// the indictment — and the reason the cutoff has exactly one writer, in the owner's window.</para>
    /// </summary>
    [Fact]
    public async Task No_op_on_this_channel_sets_clears_or_moves_the_holdout()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var set = Given(db);
        var cutoff = set.HoldoutFrom!.Value;
        var dataset = set.Id.ToString(CultureInfo.InvariantCulture);
        var before = Iso(cutoff.AddMinutes(-1));
        var program = GivenProgram();

        var served = 0;
        var pass = 0;
        foreach (var asked in new[] { Iso(cutoff.AddMinutes(-30)), Iso(cutoff.AddMinutes(30)) })
        {
            pass++;
            foreach (var op in EveryOp())
            {
                var reply = await client.SendAsync(new IpcRequest
                {
                    Op = op, Session = "research", RequestId = $"holdout-write-{pass}-{op}",
                    Args = Args(
                        ("holdout_from", asked), ("holdout", asked), ("evaluation_class", EvaluationClass.Fixture),
                        ("class", EvaluationClass.Fixture), ("dataset", dataset), ("pair", set.Pair),
                        ("strategy", program), ("from", Iso(Start)),
                        ("to", before), ("symbol", "ES"), ("quantity", "1"), ("id", "nothing"))
                });

                if (reply.Ok) served++;
                log.WriteLine($"{op} asked {asked}: ok={reply.Ok} {reply.Error?.Code}");

                var now = gw.Datasets.ById(set.Id)!;
                Assert.Equal(cutoff, now.HoldoutFrom);
                Assert.Equal(EvaluationClass.Research, now.EvaluationClass);
            }
        }

        // Not a sweep of refusals: the reads that touch this ledger really did answer.
        Assert.True(served >= 6, $"only {served} ops were served at all, so the sweep proved little");
    }

    // ---- item 2: no op on this channel reads a holdout bar ----------------------------------------

    /// <summary>
    /// A WINDOW THAT REACHES THE CUTOFF IS REFUSED IN WORDS, FOR EVERY CALLER, AND NEVER CLIPPED.
    ///
    /// <para>Red first, over the real wire: a Research grant asking for the whole dataset was served all
    /// 120 bars, the last 60 of which are the months the owner held back — and so was a connection that
    /// proved no role at all, which is the reading `U-containment` had already had to fix once.</para>
    ///
    /// <para>Three callers and two shapes of window: no <c>to</c> at all, and a <c>to</c> past the
    /// cutoff. Both are refused with <c>HOLDOUT_WITHHELD</c> and a refusal that NAMES the cutoff, and
    /// neither comes back with bars: an answer quietly cut short at the cutoff would be a different
    /// window from the one asked for, and nothing in the reply would say so.</para>
    /// </summary>
    [Theory]
    [InlineData(CouncilRoles.Research)]
    [InlineData(CouncilRoles.Operations)]
    [InlineData(null)]
    public async Task A_window_that_reaches_the_cutoff_is_refused_in_words_for_every_caller(string? role)
    {
        var (gw, db, client, server) = await Connected(role);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var set = Given(db);
        var cutoff = set.HoldoutFrom!.Value;

        foreach (var args in new[]
                 {
                     Args(("pair", set.Pair)),
                     Args(("pair", set.Pair), ("to", Iso(cutoff))),
                     Args(("pair", set.Pair), ("from", Iso(Start)), ("to", Iso(Start.AddMinutes(119))))
                 })
        {
            var reply = await client.SendAsync(new IpcRequest { Op = Ops.DataBars, Session = "agent", Args = args });
            log.WriteLine(Json.Write(reply.Error ?? (object)Data(reply)));

            Assert.False(reply.Ok, "a caller on the agent pipe was served the bars the owner held back");
            Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), reply.Error?.Code);
            Assert.Contains("holds out every bar from", reply.Error!.Message, StringComparison.Ordinal);
            Assert.Contains(cutoff.ToString("u", CultureInfo.InvariantCulture), reply.Error!.Message, StringComparison.Ordinal);
            Assert.Contains("REFUSED rather than quietly cut short", reply.Error!.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE PAIRED POSITIVE, which is what makes the refusal a boundary rather than an outage: a window
    /// that ENDS before the cutoff is served in full, to the same caller, on the same dataset.
    /// </summary>
    [Fact]
    public async Task A_window_that_ends_before_the_cutoff_is_served_in_full()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var set = Given(db);
        var cutoff = set.HoldoutFrom!.Value;

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "research",
            Args = Args(("pair", set.Pair), ("from", Iso(Start)), ("to", Iso(cutoff.AddMinutes(-1))))
        });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var bars = Data(reply).GetProperty("bars");
        Assert.Equal(HoldoutAtBar, bars.GetArrayLength());
        foreach (var bar in bars.EnumerateArray())
            Assert.True(bar.GetProperty("open_time").GetDateTimeOffset() < cutoff,
                "a bar at or after the cutoff was in an answer that was served");
    }

    /// <summary>
    /// NOT ONE OP SERVES A BAR AT OR AFTER THE CUTOFF — every op this build has, asked with a window
    /// that covers the whole dataset, off <see cref="Ops"/>'s own fields so that an op added later is
    /// asked too. The one bar shape this product has carries <c>open_time</c>, so that is what is looked
    /// for, anywhere in the reply and at any depth.
    ///
    /// <para>What it proves and what it does not: it proves that nothing reachable on this channel today
    /// hands back a held-back bar, to a role or to a roleless caller, and it is the leg that a new op
    /// would fail. It does not prove a future op could not invent a different name for a bar; the other
    /// leg of that is structural and is in <c>HoldoutLedgerTests</c> — the audience that may read past a
    /// cutoff cannot be minted outside <c>TradeAgent.Core</c> at all.</para>
    /// </summary>
    [Theory]
    [InlineData(CouncilRoles.Research)]
    [InlineData(null)]
    public async Task Not_one_op_on_this_channel_serves_a_bar_at_or_after_the_cutoff(string? role)
    {
        var (gw, db, client, server) = await Connected(role);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var set = Given(db);
        var cutoff = set.HoldoutFrom!.Value;
        var program = GivenProgram(role ?? CouncilRoles.Research);

        foreach (var op in EveryOp())
        {
            var reply = await client.SendAsync(new IpcRequest
            {
                Op = op, Session = "agent", RequestId = $"holdout-read-{op}",
                Args = Args(("pair", set.Pair), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)),
                    ("strategy", program), ("from", Iso(Start)), ("to", Iso(Start.AddMinutes(1000))),
                    ("symbol", "ES"), ("quantity", "1"), ("id", "nothing"), ("all", "true"))
            });

            var served = Held(Data(reply), cutoff);
            Assert.True(served is null,
                $"'{op}' served a bar at {served:u}, which is at or after the holdout cutoff {cutoff:u}");
        }
    }

    /// <summary>The first bar at or after <paramref name="cutoff"/> anywhere in this reply, or null.</summary>
    static DateTimeOffset? Held(JsonElement e, DateTimeOffset cutoff)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals("open_time") && p.Value.TryGetDateTimeOffset(out var at) && at >= cutoff)
                        return at;
                    if (Held(p.Value, cutoff) is { } deeper) return deeper;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (Held(item, cutoff) is { } deeper) return deeper;
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// A BACKTEST WHOSE WINDOW REACHES THE CUTOFF IS REFUSED BEFORE IT RUNS, and the paired positive is
    /// a run over the months the AI is allowed to see. A run is the reading that MATTERS: `data-bars`
    /// hands over prices, and a backtest hands over what the prices did — the same leak, laundered
    /// through a metric.
    /// </summary>
    [Fact]
    public async Task A_backtest_whose_window_reaches_the_cutoff_is_refused_and_one_before_it_runs()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var set = Given(db);
        var cutoff = set.HoldoutFrom!.Value;
        var dataset = set.Id.ToString(CultureInfo.InvariantCulture);
        var program = GivenProgram();

        var refused = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "research", RequestId = "holdout-bt-1",
            // Declared for the reason the run below declares it, and so that what this measures is the
            // HOLDOUT refusal rather than the missing-increment one: the request has to be complete
            // before the cutoff is the thing standing in its way.
            Args = Args(("strategy", program), ("dataset", dataset), ("increment", "1"))
        });

        Assert.False(refused.Ok, "a backtest ran over the months the owner held back");
        Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), refused.Error?.Code);
        Assert.Contains("holds out every bar from", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(gw.Strategies.Runs());

        var ran = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "research", RequestId = "holdout-bt-2",
            // Declared, because this fixture's dataset records no venue and TradeAgent will not invent
            // an increment (`VenueIncrementTests`). 1 is what this run used before the catalogue.
            Args = Args(("strategy", program), ("dataset", dataset), ("to", Iso(cutoff.AddMinutes(-1))),
                ("increment", "1"))
        });

        Assert.True(ran.Ok, Json.Write(ran.Error));
        Assert.Equal(HoldoutAtBar, Data(ran).GetProperty("metrics").GetProperty("bars").GetInt64());
        Assert.Single(gw.Strategies.Runs());
    }

    /// <summary>
    /// `data-list` NAMES THE CUTOFF. The refusal is only half of an honest answer: an agent that cannot
    /// see where the boundary is would spend its budget discovering it one refused window at a time, and
    /// the boundary itself is not secret — the bars are.
    /// </summary>
    [Fact]
    public async Task Data_list_names_the_cutoff_and_the_evaluation_class()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var set = Given(db);
        var plain = Given(db, "ETHUSDT", holdout: false);

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.DataList, Session = "research" });
        Assert.True(reply.Ok, Json.Write(reply.Error));
        log.WriteLine(Json.Write(reply.Data));

        var rows = Data(reply).GetProperty("datasets").EnumerateArray().ToList();
        var held = rows.Single(r => r.GetProperty("id").GetInt64() == set.Id);
        var open = rows.Single(r => r.GetProperty("id").GetInt64() == plain.Id);

        Assert.Equal(set.HoldoutFrom, held.GetProperty("holdout_from").GetDateTimeOffset());
        Assert.Equal(EvaluationClass.Research, held.GetProperty("evaluation_class").GetString());

        // A dataset with no cutoff says so with a null rather than by leaving the field out: an absent
        // key reads as "this build has no such field", which is a different answer.
        Assert.Equal(JsonValueKind.Null, open.GetProperty("holdout_from").ValueKind);
        Assert.Contains("holdout_from", Data(reply).GetProperty("note").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OTHER TRANSPORT INTO THE SAME HANDLER SERVES NO HOLDOUT BAR EITHER.
    ///
    /// <para><c>U-api-worker</c> landed a second way in while this unit was being built: the app-owned
    /// harness has no child process, so it presents no machine token and no launch grant and reaches the
    /// gateway through <c>GatewayPipeServer.CallAsync</c> instead of the pipe. Its <c>data</c> tool
    /// carries <c>data-list</c> and <c>data-bars</c>, which is exactly the surface this unit refuses.</para>
    ///
    /// <para>The refusal needed no new code for it, and that is the point being asserted: the check is
    /// inside the READER, not beside the transport, so a second door onto the same handler inherits it.
    /// Every op is asked over that door too, with a window covering the whole dataset.</para>
    /// </summary>
    [Theory]
    [InlineData(CouncilRoles.Research)]
    [InlineData(null)]
    public async Task Not_one_op_reached_through_the_in_process_worker_surface_serves_a_holdout_bar(string? role)
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), NewPipe());

        var set = Given(db);
        var cutoff = set.HoldoutFrom!.Value;
        var program = GivenProgram(role ?? CouncilRoles.Research);

        foreach (var op in EveryOp())
        {
            var reply = await server.CallAsync(new IpcRequest
            {
                Op = op, Session = "worker", RequestId = $"holdout-worker-{op}",
                Args = Args(("pair", set.Pair), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)),
                    ("strategy", program), ("from", Iso(Start)), ("to", Iso(Start.AddMinutes(1000))),
                    ("symbol", "ES"), ("quantity", "1"), ("id", "nothing"))
            }, role, "attempt-worker");

            var served = Held(Data(reply), cutoff);
            Assert.True(served is null,
                $"'{op}' over the in-process worker surface served a bar at {served:u}, at or after {cutoff:u}");
        }
    }

    // ---- item 3: the verdict op reads the holdout and hands none of it back -----------------------

    /// <summary>
    /// A VERDICT IS THE ONE THING ON THIS CHANNEL THAT CAUSES THE HELD-BACK MONTHS TO BE READ — AND IT
    /// MOVES NO CUTOFF AND OPENS NO DOOR.
    ///
    /// <para>RED before <c>Ops.Verdict</c> had a handler: the frame came back <c>unknown operation
    /// 'verdict'</c>. It is here rather than only in <c>VerdictOverPipeTests</c> because this class is
    /// where the cutoff's invariants live: the op that asks the app to run over the holdout is exactly
    /// the op most likely to be the one that moves it, truncates it, or hands a bar back.</para>
    ///
    /// <para>Three things are checked AFTER a real verdict has run: the dataset's cutoff row and its
    /// evaluation class are untouched; not one bar at or after the cutoff is anywhere in the verdict's
    /// own reply, at any depth; and <c>data-bars</c> past the cutoff is still refused with
    /// <c>HOLDOUT_WITHHELD</c> to the very caller whose program was just judged on those months.</para>
    /// </summary>
    [Fact]
    public async Task A_verdict_moves_no_cutoff_and_leaves_the_held_back_bars_withheld()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var set = Given(db, campaignFor: gw);
        var cutoff = set.HoldoutFrom!.Value;
        var dataset = set.Id.ToString(CultureInfo.InvariantCulture);
        var program = GivenProgram(name: "judgeable.strategy", text: JudgeableText);

        // Frozen BEFORE the holdout window begins, so the scoring policy's forward-evidence clause can
        // be met. `RecordVersion` is ON CONFLICT DO NOTHING, so the research run below leaves it alone.
        var parsed = StrategyParser.Parse(JudgeableText).Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            Start, CouncilRoles.Research, "attempt-h1"));

        var researched = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "research", RequestId = "holdout-verdict-run",
            Args = Args(("strategy", program), ("dataset", dataset), ("from", Iso(Start)),
                ("to", Iso(cutoff.AddMinutes(-1))), ("increment", "1"))
        });
        Assert.True(researched.Ok, Json.Write(researched.Error));

        var verdict = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Verdict, Session = "research", RequestId = "holdout-verdict",
            Args = Args(("version", parsed.StrategyId), ("dataset", dataset))
        });
        log.WriteLine(Json.Write(verdict.Error ?? (object)Data(verdict)));
        Assert.True(verdict.Ok, Json.Write(verdict.Error));
        Assert.Equal("promoted", Data(verdict).GetProperty("verdict").GetString());

        // THE CUTOFF ROW DID NOT MOVE, and the class it was set under did not change either.
        var now = gw.Datasets.ById(set.Id)!;
        Assert.Equal(cutoff, now.HoldoutFrom);
        Assert.Equal(EvaluationClass.Research, now.EvaluationClass);

        // NOT ONE HELD-BACK BAR IS IN THE ANSWER, at any depth.
        Assert.Null(Held(Data(verdict), cutoff));

        // AND THE DOOR IS STILL SHUT for the caller whose program was just judged on those months.
        var bars = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "research", RequestId = "holdout-verdict-bars",
            Args = Args(("pair", set.Pair), ("from", Iso(Start)), ("to", Iso(Start.AddMinutes(119))))
        });

        Assert.False(bars.Ok, "the bars the owner held back were served after a verdict had read them");
        Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), bars.Error?.Code);
        Assert.Contains("holds out every bar from", bars.Error!.Message, StringComparison.Ordinal);
    }
}
