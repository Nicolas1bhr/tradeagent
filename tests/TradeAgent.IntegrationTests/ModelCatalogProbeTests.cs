using System.Text;
using System.Text.Json.Nodes;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// (3) THE DRY RUN READS THE APP'S MODEL CATALOG, ON THE REAL CODEX (<c>U-cell-model-catalog</c>, option C): the broker and cell classes'
/// own argv, gpt-6-luna on the app's catalog (its bundled entry without <c>tool_mode</c>), the probe's own loopback mock and stub door and a throwaway home — no real model, no key, no account. With the shipped
/// catalog each class is offered exactly its expected set; with the SAME catalog whose one entry says <c>supports_search_tool: false</c>
/// no <c>tool_search</c> is offered and the seat is refused in words — which can only happen if codex read the catalog the argv names,
/// not its bundled metadata or the fallback a live turn on the owner's account would take. Each run's offered set is printed (the
/// re-measure of 2026-10-25). RED in main's shape (no catalog in the argv): gpt-6-luna's bundled metadata is <c>code_mode_only</c>,
/// so code mode's <c>exec</c> is offered and refused. EVERY MODEL OF THE VERIFIED CATALOG (<c>U-seat-models</c>): gpt-6.1-sol, the
/// fallback, is held to the same two checks on its own entry — its surface measured here, never assumed.
/// </summary>
[Trait("Category", "Cell")]
public class ModelCatalogProbeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [PinnedCodexFact]
    public async Task The_dry_run_offers_what_the_apps_catalog_says_and_not_the_bundled_metadata()
    {
        var exe = CliCodeModeHostTests.PinnedCodex();
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var sha = codex.Install.ExecutableSha256!;
        var (shipped, refusal) = SeatProfile.ShippedCatalog(codex.Seat!);
        Assert.True(shipped is not null, refusal);

        foreach (var model in BrokerSeat.Models)
        {
            var altered = JsonNode.Parse(shipped!)!;
            foreach (var entry in altered["models"]!.AsArray())
                if ((string?)entry!["slug"] == model) entry["supports_search_tool"] = false;
            var withoutSearch = Encoding.UTF8.GetBytes(altered.ToJsonString());

            foreach (var seatClass in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass })
            {
                var ask = new SurfaceAsk(codex, exe, sha, seatClass, model);

                var offered = await CodexSurfaceProbe.Instance.OfferedAsync(ask, default);
                output.WriteLine($"{seatClass} {model} shipped catalog :: {string.Join(" ", offered)}");
                Assert.DoesNotContain("exec", offered.Select(BrokerSeat.ToolName));
                // NO QUESTION NOBODY ANSWERS (U-broker-seat-fixes item 2): the deny set's key and the catalog's edit hold on the real codex.
                Assert.DoesNotContain("request_user_input", offered.Select(BrokerSeat.ToolName));
                Assert.DoesNotContain("request_user_input_async", offered.Select(BrokerSeat.ToolName));
                var verdict = BrokerSeat.SurfaceRefusal(model, offered, codex.DisplayName, seatClass);
                Assert.True(verdict is null, $"{seatClass} {model} on the shipped catalog: {verdict} (offered {string.Join(", ", offered)})");

                var off = await CodexSurfaceProbe.Instance.OfferedAsync(ask, withoutSearch, default);
                output.WriteLine($"{seatClass} {model} supports_search_tool false :: {string.Join(" ", off)}");
                Assert.True(!off.Select(BrokerSeat.ToolName).Contains("tool_search"),
                    $"{seatClass} {model} on a catalog with supports_search_tool false still offered tool_search: codex did not read the catalog " +
                    $"(offered {string.Join(", ", off)})");
                var refused = BrokerSeat.SurfaceRefusal(model, off, codex.DisplayName, seatClass);
                Assert.True(refused is not null && refused.Contains("'tool_search'", StringComparison.Ordinal),
                    $"{seatClass} {model}: the altered catalog's surface was not refused naming tool_search: {refused}");
            }
        }
    }

    /// <summary>
    /// (rider (b)) A DRY RUN ON FALLBACK METADATA REFUSES THE SEAT BEFORE ADMISSION, in words: a catalog that does not hold the pinned
    /// model makes codex take its fallback metadata and say so; the probe reads codex's own output and refuses, quoting it — whatever
    /// tools that run happened to be offered. RED at base: the probe never read codex's output and returned the fallback's tools.
    /// </summary>
    [PinnedCodexFact]
    public async Task A_dry_run_on_fallback_metadata_is_refused_in_words()
    {
        var exe = CliCodeModeHostTests.PinnedCodex();
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var (shipped, _) = SeatProfile.ShippedCatalog(codex.Seat!);
        var other = JsonNode.Parse(shipped!)!;
        var n = 0;
        foreach (var entry in other["models"]!.AsArray()) entry!["slug"] = $"tradeagent-not-a-catalog-model-{n++}";
        var withoutModel = Encoding.UTF8.GetBytes(other.ToJsonString());

        foreach (var (seatClass, model) in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass }.SelectMany(c => BrokerSeat.Models.Select(m => (c, m))))
        {
            var ask = new SurfaceAsk(codex, exe, codex.Install.ExecutableSha256!, seatClass, model);
            IReadOnlyList<string>? offered = null;
            var ex = await Record.ExceptionAsync(async () => offered = await CodexSurfaceProbe.Instance.OfferedAsync(ask, withoutModel, default));
            output.WriteLine($"{seatClass} {model} without the model :: {ex?.Message ?? string.Join(" ", offered ?? [])}");
            Assert.True(ex is not null && ex.Message.Contains("fallback metadata", StringComparison.Ordinal),
                $"{seatClass}: a dry run on fallback metadata was not refused (offered {string.Join(", ", offered ?? [])})");

            var verdict = await BrokerSeat.VerifySurfaceAsync(ask with { ExeSha256 = "fallback-" + seatClass + "-" + model },
                new CatalogProbe(withoutModel), default);
            Assert.True(verdict.Refusal is not null && verdict.Refusal.Contains("fallback metadata", StringComparison.Ordinal), verdict.Refusal);
        }
    }

    /// <summary>The real dry run on a given catalog: what the admission's surface check would call.</summary>
    sealed class CatalogProbe(byte[] catalog) : ISurfaceProbe
    {
        public Task<IReadOnlyList<string>> OfferedAsync(SurfaceAsk ask, CancellationToken ct) => CodexSurfaceProbe.Instance.OfferedAsync(ask, catalog, ct);
    }
}
