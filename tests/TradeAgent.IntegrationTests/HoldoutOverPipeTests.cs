using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
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
        string evaluationClass = EvaluationClass.Research)
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
        if (holdout)
        {
            var done = store.SetHoldout(id, Start.AddMinutes(HoldoutAtBar), evaluationClass);
            Assert.True(done.Ok, done.Why);
        }

        return store.ById(id)!;
    }

    /// <summary>A program in a role's own folder, so the `backtest` op has something to read.</summary>
    static string GivenProgram(string role = CouncilRoles.Research, string name = "holdout.strategy")
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name),
            "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n");
        return "strategies/" + name;
    }

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
}
