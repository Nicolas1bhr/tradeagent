using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 4 — the two read-only operations, as an agent DISCOVERS them.
///
/// Red first: the handlers existed and the schema did not name them, so <c>trade schema --json</c> —
/// the thing this product tells an agent to read instead of trusting a prompt — described a build
/// with no market data in it at all. A capability an agent cannot discover is a capability it does
/// not have, and the whole reason the schema is served at runtime is that a prompt drifts.
/// </summary>
public class DataOpsTests
{
    static GatewaySchema.OpSpec Op(string name) =>
        GatewaySchema.Ops().SingleOrDefault(o => o.Op == name)
        ?? throw new Xunit.Sdk.XunitException($"the schema does not name '{name}'");

    [Fact]
    public void The_schema_names_both_data_operations_and_neither_of_them_mutates()
    {
        var list = Op(Ops.DataList);
        var bars = Op(Ops.DataBars);

        Assert.False(list.Mutating);
        Assert.False(bars.Mutating);
        Assert.Equal("trade data list", list.Cli);
        Assert.Equal("trade data bars --pair P [--from D] [--to D]", bars.Cli);
        Assert.Contains(bars.Args, a => a.Name == "pair" && a.Required);
        Assert.Contains(bars.Args, a => a.Name == "from");
        Assert.Contains(bars.Args, a => a.Name == "to");
    }

    [Fact]
    public void The_schema_says_what_the_data_is_NOT()
    {
        var described = JsonSerializer.SerializeToElement(GatewaySchema.Describe(), Json.Options);

        var sentence = described.GetProperty("market_data").GetString()!;
        Assert.Contains("hypothesis evidence", sentence);
        Assert.Contains("no fill", sentence);
        Assert.Contains("queue position", sentence);

        // And the operations' own descriptions repeat it, because an agent that reads one op spec
        // has not read the preamble.
        Assert.Contains("hypothesis evidence", Op(Ops.DataBars).Description);
    }

    [Fact]
    public void There_is_no_operation_on_this_channel_that_writes_a_dataset()
    {
        var ops = GatewaySchema.Ops().Select(o => o.Op).ToList();

        Assert.DoesNotContain(ops, o => o is "data-collect" or "data-download" or "data-delete" or "data-reject");
        Assert.DoesNotContain(Ops.Mutating, o => o.StartsWith("data", StringComparison.Ordinal));
        Assert.Equal(2, ops.Count(o => o.StartsWith("data-", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_situation_says_what_history_there_is_or_that_there_is_none()
    {
        Assert.Contains("no dataset yet", MissionSituation.DataLine(null));
        Assert.Contains("no command you can run that does it", MissionSituation.DataLine(null));

        var set = new DatasetRecord(1, "binance-spot-monthly-klines", "BTCUSDT", "1m", "v1", 12, 12, [],
            "/x/v1.csv", new string('a', 64), 525_600,
            new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero),
            41, [], false, 0, 3, 0, DateTimeOffset.UtcNow, DatasetState.ACCEPTED, null, []);

        var line = MissionSituation.DataLine(set);
        Assert.Contains("Data: BTCUSDT 1m, 2025-09-01 \u2192 2026-08-31, 525,600 bars, 41 gaps", line);
        Assert.Contains("trade data list", line);
        Assert.Contains("hypothesis evidence", line);

        var rejected = MissionSituation.DataLine(set with { State = DatasetState.REJECTED, RejectedReason = "a raw file changed" });
        Assert.Contains("REJECTED", rejected);
        Assert.Contains("a raw file changed", rejected);
    }

    [Fact]
    public void The_turns_message_carries_the_data_line_where_the_AI_reads_the_rest_of_its_state()
    {
        var text = new MissionSituation { Mode = "PAPER", Data = "Data: no dataset yet." }.Text();

        Assert.Contains("- Data: no dataset yet.", text);
        Assert.True(text.IndexOf("- Data:", StringComparison.Ordinal) > text.IndexOf("- Positions:", StringComparison.Ordinal),
            "the data line belongs below the account state, not above the owner's own words");
    }

    [Fact]
    public void The_missions_data_folder_sentences_name_the_command_that_serves_the_data()
    {
        var mission = WorkspaceBuilder.Instructions(new WorkspaceContext(
            "Practice simulator", ConnectorIsPaper: true, "SIM-1", TradingMode.PAPER,
            ExecutionAvailable: true, null, new RiskPolicy { InstrumentAllowlist = ["ES"] },
            ConnectorIsBuiltInSimulator: true));

        Assert.Contains("trade data list", mission);
        Assert.Contains("trade data bars", mission);
        Assert.Contains("hypothesis evidence", mission);
    }

    [Fact]
    public async Task Both_data_operations_are_in_the_deadline_table_at_zero()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-data-" + Guid.NewGuid().ToString("n")[..12]);

        foreach (var op in new[] { Ops.DataList, Ops.DataBars })
        {
            var row = server.HandlerPaths.SingleOrDefault(h => h.Handler == op);
            Assert.NotNull(row.Handler);
            Assert.Equal(TimeSpan.Zero, row.Path);
        }
    }
}
