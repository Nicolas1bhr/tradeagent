using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- U-evidence-breadth-paper: an act names the pair it changes ----------------------------------------------------------

    /// <summary>
    /// AN ACT NAMES THE PAIR IT CHANGES, AND CHANGES THAT PAIR ALONE (<c>U-evidence-breadth-paper</c>; R25 D11): each pair's envelope is
    /// its own field, <c>paper_envelopes.&lt;PAIR&gt;</c>. Terms for another instrument under a pair's name are refused at the parse in
    /// words — and a body holding them is refused by the strict read — so no act can move one pair's authority while naming another.
    /// One seat narrowing ETHUSDT's envelope withdraws ETHUSDT's alone (every other pair's stands on); one seat widening it is refused,
    /// and two seats widen it; an act that grants a pair the founding policy does not name is a widening. RED before the unit: no
    /// field named a pair ("no field the board's acts change"). Watched red with the parse's same-pair check removed: the cross-pair
    /// act parsed.
    /// </summary>
    [Fact]
    public async Task An_act_names_the_pair_it_changes_and_changes_that_pair_alone()
    {
        var btc = For("BTCUSDT", Envelope);
        var eth = For("ETHUSDT", EnvelopeWith("max_quantity", "2.5"));

        // THE CROSS-PAIR ACT: BTCUSDT's field, ETHUSDT's terms — refused at the parse, whichever way it would move.
        var (crossed, why) = BoardIntent.Parse($$"""[{"field":"paper_envelopes.BTCUSDT","from":{{btc}},"to":{{eth}}}]""");
        log.WriteLine($"cross-pair act: {why}");
        Assert.Null(crossed);
        Assert.Contains("never another's", why, StringComparison.Ordinal);
        Assert.Null(BoardIntent.Parse($$"""[{"field":"paper_envelopes.BTCUSDT","from":null,"to":{{eth}}}]""").Intent);

        // AND THE BODY: terms under another pair's key are no body this build reads.
        var body = BoardPolicyBody.Canonical(BoardFounding.V1).Replace("\"BTCUSDT\":{", "\"DOGEUSDT\":{", StringComparison.Ordinal);
        var (unread, bodyWhy) = BoardPolicyBody.Read(body, BoardPolicyBody.Sha256(body));
        log.WriteLine($"mis-keyed body: {bodyWhy}");
        Assert.Null(unread);
        Assert.Contains("states terms for BTCUSDT", bodyWhy, StringComparison.Ordinal);

        // A PAIR THE FOUNDING POLICY DOES NOT NAME: granting it widens; removing it narrows.
        var doge = For("DOGEUSDT", Envelope);
        Assert.Equal(BoardDirectionKind.Widens, BoardDirection.Of("paper_envelopes.DOGEUSDT", "null", doge));
        Assert.Equal(BoardDirectionKind.Narrows, BoardDirection.Of("paper_envelopes.DOGEUSDT", doge, "null"));
        Assert.NotNull(BoardIntent.Parse($$"""[{"field":"paper_envelopes.DOGEUSDT","from":null,"to":{{doge}}}]""").Intent);

        // ONE SEAT NARROWS ETHUSDT'S: ETHUSDT's founding envelope withdrawn in the act's write, every other pair's standing on.
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        await using var gw = await PaperGateway(db);
        List<PaperEnvelopeRow> All() => [.. gw.Envelopes.All().Where(e => e.StandsAt(DateTimeOffset.UtcNow)).OrderBy(e => e.Symbol, StringComparer.Ordinal)];
        var before = All();
        Assert.Equal(EvidenceDefaults.Pairs.Order(StringComparer.Ordinal), before.Select(e => e.Symbol));
        var founded = For("ETHUSDT", BoardStore.ValueAt(BoardPolicyBody.Canonical(gw.Policy), "paper_envelopes.ETHUSDT"));
        var narrower = System.Text.Json.Nodes.JsonNode.Parse(founded)!.AsObject();
        narrower["max_notional"] = 1_000m;
        gw.BoardNarrow(Act("board-1", Change("paper_envelopes.ETHUSDT", Json(founded), Json(narrower.ToJsonString()))));
        var after = All();
        foreach (var e in after) log.WriteLine($"after the narrowing: {e.Symbol} {e.MaxNotional}");
        Assert.DoesNotContain(after, e => e.Symbol == "ETHUSDT");
        Assert.Equal(before.Where(e => e.Symbol != "ETHUSDT").Select(e => e.Id), after.Select(e => e.Id));
        await gw.RefreshHealthAsync();
        Assert.Equal(1_000m, Assert.Single(All(), e => e.Symbol == "ETHUSDT").MaxNotional);
        Assert.Equal(before.Where(e => e.Symbol != "ETHUSDT").Select(e => e.Id), All().Where(e => e.Symbol != "ETHUSDT").Select(e => e.Id));

        // ONE SEAT CANNOT WIDEN A PAIR BACK; TWO SEATS CAN.
        var widerAgain = narrower.ToJsonString();
        var refused = Assert.Throws<TradeAgentException>(() =>
            gw.BoardNarrow(Act("board-1", Change("paper_envelopes.ETHUSDT", Json(widerAgain), Json(founded)))));
        log.WriteLine($"one seat widening ETHUSDT: {refused.Message}");
        var proposed = gw.BoardPropose(Act("board-1", Change("paper_envelopes.ETHUSDT", Json(widerAgain), Json(founded))));
        Assert.Equal(BoardActEventKind.Confirmed,
            gw.BoardDecide(proposed.Act.Id, "board-2", null, true, proposed.Act.IntentSha256, "the pair has earned it back").Status);
        Assert.Equal(5_000m, gw.Policy.PaperEnvelopeFor("ETHUSDT")!.MaxNotional);
        Assert.Equal(0.1m, gw.Policy.PaperEnvelopeFor("BTCUSDT")!.MaxQuantity);
    }

    /// <summary>
    /// A VETO ON ONE PAIR NEVER SHORTENS ANOTHER PAIR'S LIFE (<c>U-evidence-breadth-paper</c>, the Codex review's P2): BTCUSDT's envelope of
    /// ten days is granted at day 0; on day nine two seats widen ETHUSDT's envelope and the owner vetoes that act; then two seats widen
    /// BTCUSDT's. BTCUSDT's widening stands, so the next pass grants its new terms their full ten days from now — day nineteen — and the
    /// veto of an act that never touched BTCUSDT renews or revokes nothing of BTCUSDT's. RED before the fix: any vetoed version in the
    /// range failed BTCUSDT closed, and its envelope ended on day ten. Watched red with the same-pair skip removed.
    /// </summary>
    [Fact]
    public async Task A_veto_on_one_pair_never_shortens_another_pairs_life()
    {
        using var db = TestEnv.NewDb();
        var (gw, clock, day0, _) = await TenDayEnvelope(db);
        await using var _gw = gw;
        clock.Now = day0.AddDays(9);

        var ethNow = BoardStore.ValueAt(BoardPolicyBody.Canonical(gw.Policy), "paper_envelopes.ETHUSDT");
        var ethWider = System.Text.Json.Nodes.JsonNode.Parse(ethNow)!.AsObject();
        ethWider["max_notional"] = 6_000m;
        var eth = gw.BoardPropose(Act("board-1", Change("paper_envelopes.ETHUSDT", Json(ethNow), Json(ethWider.ToJsonString()))));
        Assert.Equal(BoardActEventKind.Confirmed, gw.BoardDecide(eth.Act.Id, "board-2", null, true, eth.Act.IntentSha256, "ETH has room").Status);
        gw.VetoBoardAct(eth.Act.Id, gw.PreviewBoardVeto(eth.Act.Id));
        Assert.Equal(5_000m, gw.Policy.PaperEnvelopeFor("ETHUSDT")!.MaxNotional);

        var btc = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Terms(0.1m, 10)), Json(Terms(0.2m, 10)))));
        Assert.Equal(BoardActEventKind.Confirmed, gw.BoardDecide(btc.Act.Id, "board-2", null, true, btc.Act.IntentSha256, "BTC has room").Status);
        await gw.RefreshHealthAsync();
        foreach (var e in gw.Envelopes.All().Where(e => e.Symbol == "BTCUSDT"))
            log.WriteLine($"BTCUSDT {e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        var standing = Assert.Single(gw.Envelopes.All(), e => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT");
        Assert.Equal((0.2m, day0.AddDays(19)), (standing.MaxQuantity, standing.ExpiresAt));
    }
}
