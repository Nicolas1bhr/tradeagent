using System.Globalization;
using System.Text;
using System.Text.Json;
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
/// U-referee-2 item 5, over the wire: WHAT AN AGENT ACTUALLY RECEIVES ABOUT A VERDICT.
///
/// <para>The unit tests hold the promotion record and the note. This holds what only the wire can
/// settle. <c>trade report</c> serves the owner's document to the agent VERBATIM — <c>text</c> is the
/// whole rendered report — so section 8 is the one surface where a figure computed over the held-back
/// months could reach the research process without any op being wrong. It names the verdict and the
/// holdout run, and it values neither.</para>
/// </summary>
public class VerdictOverPipeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const int HoldoutAtBar = 60;

    const string ProgramText =
        "instrument BTCUSDT\nsize fixed 1\nexit when close > 103\nentry when close < 97\n";

    static string NewPipe() => "ta-verdict-" + Guid.NewGuid().ToString("n")[..12];

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
