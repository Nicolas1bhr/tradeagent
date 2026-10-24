using System.Text;
using System.Text.Json.Nodes;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// (3) THE DRY RUN READS THE APP'S MODEL CATALOG, ON THE REAL CODEX (<c>U-cell-model-catalog</c>): the broker and cell classes' own argv,
/// gpt-5.5, the probe's own loopback mock and stub door and a throwaway home — no real model, no key, no account. With the shipped
/// catalog each class is offered exactly its expected set; with the SAME catalog whose one entry says <c>supports_search_tool: false</c>
/// no <c>tool_search</c> is offered and the seat is refused in words — which can only happen if codex read the catalog the argv names,
/// not its bundled metadata (search on) or the fallback a live turn on the owner's account would take. RED at base: the probe's argv
/// named no catalog, and the altered run still offered <c>tool_search</c>.
/// </summary>
[Trait("Category", "Cell")]
public class ModelCatalogProbeTests
{
    [PinnedCodexFact]
    public async Task The_dry_run_offers_what_the_apps_catalog_says_and_not_the_bundled_metadata()
    {
        var exe = CliCodeModeHostTests.PinnedCodex();
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var sha = codex.Install.ExecutableSha256!;
        var (shipped, refusal) = SeatProfile.ShippedCatalog(codex.Seat!);
        Assert.True(shipped is not null, refusal);

        var altered = JsonNode.Parse(shipped!)!;
        altered["models"]![0]!["supports_search_tool"] = false;
        var withoutSearch = Encoding.UTF8.GetBytes(altered.ToJsonString());

        foreach (var seatClass in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass })
        {
            var ask = new SurfaceAsk(codex, exe, sha, seatClass, BrokerSeat.Model);

            var offered = await CodexSurfaceProbe.Instance.OfferedAsync(ask, default);
            var verdict = BrokerSeat.SurfaceRefusal(BrokerSeat.Model, offered, codex.DisplayName, seatClass);
            Assert.True(verdict is null, $"{seatClass} on the shipped catalog: {verdict} (offered {string.Join(", ", offered)})");

            var off = await CodexSurfaceProbe.Instance.OfferedAsync(ask, withoutSearch, default);
            Assert.True(!off.Select(BrokerSeat.ToolName).Contains("tool_search"),
                $"{seatClass} on a catalog with supports_search_tool false still offered tool_search: codex did not read the catalog " +
                $"(offered {string.Join(", ", off)})");
            var refused = BrokerSeat.SurfaceRefusal(BrokerSeat.Model, off, codex.DisplayName, seatClass);
            Assert.True(refused is not null && refused.Contains("'tool_search'", StringComparison.Ordinal),
                $"{seatClass}: the altered catalog's surface was not refused naming tool_search: {refused}");
        }
    }
}
