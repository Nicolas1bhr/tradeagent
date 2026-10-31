using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;

namespace TradeAgent.ProvingHost;

/// <summary>
/// THE FLOOR, RUN (<c>U-proving-ground-c</c> item 3): for each market's open campaign, in dataset order, every program of
/// <see cref="FloorGrammar"/> in its fixed order through the pipe's own <c>backtest</c> handler — <c>Backtests.Run</c> — under a
/// proving-only app principal: an in-process worker of this host, as the Research position, holding no launch. Its trials are
/// charged to the campaign as a seat's, and the campaign refuses past its budget. Then a verdict for the best research results,
/// net above zero at the venue's cost, best first, through the pipe's own <c>verdict</c> handler, until the campaign's verdict
/// budget refuses. No model; the order reads no truth and no result but the floor's own.
/// </summary>
static class Floor
{
    /// <summary>The principal the floor's runs are recorded under: the founding chart's research position.</summary>
    const string Role = CouncilRoles.Research;

    /// <summary>Returns true when the deadline stopped the floor.</summary>
    public static async Task<bool> Run(GatewayPipeServer server, Database db, DateTimeOffset deadline, List<string> log)
    {
        var datasets = new DatasetStore(db);
        var campaigns = new CampaignStore(db).All().Where(c => c.IsOpen).OrderBy(c => c.HoldoutDatasetId).ToList();
        var folder = Path.Combine(Paths.RoleHome(Role), "strategies");
        Directory.CreateDirectory(folder);

        foreach (var campaign in campaigns)
        {
            var set = datasets.ById(campaign.HoldoutDatasetId)!;
            // THE MARKET'S OWN MONTHS, NAMED AT BOTH ENDS: from its first bar — a window with no start reaches every earlier market's
            // holdout — to an hour and a minute before its cutoff.
            var to = campaign.HoldoutFrom.AddMinutes(-61);
            var programs = FloorGrammar.Programs(set.Pair, Readings(set.Pair, set.FirstBar ?? DateTimeOffset.MinValue, campaign.HoldoutFrom));
            var results = new List<(FloorProgram Program, string Version, decimal Net)>();

            foreach (var program in programs)
            {
                if (DateTimeOffset.UtcNow >= deadline) { log.Add("deadline reached in research"); return true; }
                var name = $"floor-{set.Pair}-{program.Ordinal:00}.strategy";
                await File.WriteAllTextAsync(Path.Combine(folder, name), program.Text);
                var answer = await server.CallAsync(Frame(Ops.Backtest, ("strategy", "strategies/" + name), ("dataset", set.Id),
                    ("from", (set.FirstBar ?? campaign.HoldoutFrom).ToString("O", CultureInfo.InvariantCulture)),
                    ("to", to.ToString("O", CultureInfo.InvariantCulture))), Role, null);
                if (!answer.Ok)
                {
                    if (answer.Error?.Code == nameof(ErrorCode.CAMPAIGN_BUDGET_REACHED))
                    {
                        log.Add($"{set.Pair}: the campaign refused trial {program.Ordinal} — its trial budget is spent");
                        break;
                    }
                    throw new InvalidOperationException($"the floor's backtest {name} was refused: {answer.Error?.Code} {answer.Error?.Message}");
                }
                var version = StrategyParser.Parse(program.Text).Program!.StrategyId;
                var net = NetOf(db, version, set.Id);
                results.Add((program, version, net));
                log.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{set.Pair} #{program.Ordinal} {program.Kind} {program.Side} p{program.Percentile} ({program.Threshold}) hold {program.Hold}: net {net}"));
            }

            foreach (var (program, version, net) in results.Where(r => r.Net > 0m).OrderByDescending(r => r.Net).ThenBy(r => r.Program.Ordinal))
            {
                if (DateTimeOffset.UtcNow >= deadline) { log.Add("deadline reached in judging"); return true; }
                var answer = await server.CallAsync(Frame(Ops.Verdict, ("version", version), ("dataset", set.Id)), Role, null);
                if (!answer.Ok)
                {
                    if (answer.Error?.Code == nameof(ErrorCode.CAMPAIGN_BUDGET_REACHED))
                    {
                        log.Add($"{set.Pair}: the campaign refused a verdict on #{program.Ordinal} — its verdict budget is spent");
                        break;
                    }
                    throw new InvalidOperationException($"the floor's verdict on #{program.Ordinal} was refused: {answer.Error?.Code} {answer.Error?.Message}");
                }
                // AN ANSWER WITH NO VERDICT IS THE REFEREE'S REFUSAL TO SPEND ONE: the campaign's verdict budget, in its own words.
                using var judged = JsonDocument.Parse(JsonSerializer.Serialize(answer.Data));
                if (!judged.RootElement.TryGetProperty("Verdict", out var verdict) || verdict.ValueKind != JsonValueKind.String)
                {
                    log.Add($"{set.Pair}: no verdict on #{program.Ordinal} — {(judged.RootElement.TryGetProperty("Why", out var w) ? w.GetString() : "no reason given")}");
                    break;
                }
                log.Add($"{set.Pair} #{program.Ordinal} judged: {verdict.GetString()}");
            }
        }
        return false;
    }

    /// <summary>The bare signal's values for <paramref name="subject"/> from <paramref name="from"/> up to the cutoff, in source-time order.</summary>
    static List<decimal> Readings(string subject, DateTimeOffset from, DateTimeOffset cutoff)
    {
        using var tape = new TapeStore(Paths.TapeFile, kind: HomeKind.Proving);
        return [.. tape.Fetches(ProvingSources.Synthetic, FloorGrammar.Series, 100_000).SelectMany(f => tape.ObservationsOf(f.Id))
            .Where(o => o.Subject == subject && o.SourceTime >= from && o.SourceTime < cutoff)
            .OrderBy(o => o.SourceTime)
            .Select(o => decimal.Parse(JsonDocument.Parse(o.Payload!).RootElement.GetProperty(ProvingSources.ValueField).GetString()!, CultureInfo.InvariantCulture))];
    }

    /// <summary>The net of the research position's newest completed run of <paramref name="version"/> over <paramref name="dataset"/>, as recorded.</summary>
    static decimal NetOf(Database db, string version, long dataset) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT net_pnl FROM strategy_run WHERE version_id=$v AND dataset_id=$d AND role=$r ORDER BY created_at DESC LIMIT 1",
            ("$v", version), ("$d", dataset), ("$r", Role));
        return c.ExecuteScalar() is string s ? decimal.Parse(s, CultureInfo.InvariantCulture) : 0m;
    });

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "proving-floor",
        RequestId = "floor-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };
}
