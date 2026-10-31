using System.Text.Json;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- item 5: the envelope and the gateway follow the policy at once ----------------------------------------------------------

    /// <summary>A gateway on the app's own paper venue and account, the founding envelope granted by its first health pass.</summary>
    static async Task<TradingGateway> PaperGateway(Database db)
    {
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker { AccountId = BoardFounding.PaperAccount, IsSimulated = true }),
            BoardFounding.PaperVenue);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return gw;
    }

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>BTCUSDT's standing envelopes — the pair these acts change; every other pair's stands on, untouched (<c>U-evidence-breadth-paper</c>).</summary>
    static List<PaperEnvelopeRow> Standing(TradingGateway gw) =>
        [.. gw.Envelopes.All().Where(e => e.StandsAt(DateTimeOffset.UtcNow) && e.Symbol == "BTCUSDT")];

    /// <summary>
    /// A NARROWED ENVELOPE IS WITHDRAWN IN THE ACT'S WRITE: one seat lowers the standing envelope's ceiling, and the founding envelope
    /// is withdrawn by the act itself — before any pass runs — and the narrower terms are granted on the next pass, under the act's
    /// version, in the security board's words and never the owner's. An act that moves another field withdraws nothing and grants
    /// nothing; a refused act touches nothing; and once the owner withdraws the board's envelope, no later act that leaves the terms
    /// alone grants it again (his withdrawal is his, permanently — Q5). RED before item 5: the act changed the terms and the founding
    /// envelope stood on, with nothing granted under them.
    /// </summary>
    [Fact]
    public async Task A_narrowed_envelope_is_withdrawn_in_the_acts_write()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1");
        await using var gw = await PaperGateway(db);
        var founding = Assert.Single(Standing(gw));
        Assert.Equal("granted by the app under the security board's founding policy, version 1", founding.Reason);

        var refused = Assert.Throws<TradeAgentException>(() => gw.BoardNarrow(Act("board-1",
            Change("paper_envelopes.BTCUSDT", Json(EnvelopeWith("max_quantity", "0.08")), Json(EnvelopeWith("max_quantity", "0.02"))))));
        log.WriteLine(refused.Message);
        Assert.Equal(founding.Id, Assert.Single(Standing(gw)).Id);

        var narrowed = gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Envelope), Json(EnvelopeWith("max_quantity", "0.02")))));
        Assert.Equal(2, narrowed.PolicyVersion);
        Assert.Empty(Standing(gw));
        Assert.NotNull(gw.Envelopes.ById(founding.Id)!.WithdrawnAt);
        Assert.Equal(0.02m, gw.Policy.PaperEnvelopeFor("BTCUSDT")!.MaxQuantity);

        await gw.RefreshHealthAsync();
        var granted = Assert.Single(Standing(gw));
        Assert.Equal((0.02m, "granted by the app under the security board's act, version 2"), (granted.MaxQuantity, granted.Reason));
        var said = new LogStore(db).RecentActivity(200).Select(a => a.Text).Where(t => t.Contains("security board", StringComparison.Ordinal)).ToList();
        foreach (var line in said) log.WriteLine(line);
        Assert.Contains(said, t => t.Contains("version 2 changed the standing paper envelope's terms", StringComparison.Ordinal));
        Assert.Contains(said, t => t.StartsWith("The security board's version 2 lets TradeAgent run paper experiments", StringComparison.Ordinal));
        Assert.DoesNotContain(said, t => t.Contains("You ", StringComparison.Ordinal) || t.Contains("you granted", StringComparison.OrdinalIgnoreCase));

        // ANOTHER FIELD: nothing withdrawn, nothing granted.
        gw.BoardNarrow(Act("board-1", Change("plan_day_total", 40, 30)));
        await gw.RefreshHealthAsync();
        Assert.Equal(granted.Id, Assert.Single(Standing(gw)).Id);

        // THE OWNER'S WITHDRAWAL HOLDS through a later act that leaves the terms alone.
        Assert.True(gw.WithdrawPaperEnvelope(granted.Id).Ok);
        gw.BoardNarrow(Act("board-1", Change("plan_day_total", 30, 20)));
        await gw.RefreshHealthAsync();
        await gw.RefreshHealthAsync();
        Assert.Empty(Standing(gw));
        Assert.Equal(2, gw.Envelopes.All().Count(e => e.Symbol == "BTCUSDT"));
    }

    /// <summary>
    /// A CONFIRMED ENVELOPE WIDENING GRANTS UNDER THE ACT: a proposal to raise the envelope's ceiling changes nothing — the founding
    /// envelope stands — until a second seat confirms it; then the founding envelope is withdrawn in the confirmation's write and the
    /// wider terms are granted on the next pass, under the act's version, named as the security board's. Watched red with the grant
    /// kept to the founding policy alone.
    /// </summary>
    [Fact]
    public async Task A_confirmed_envelope_widening_grants_under_the_act()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        await using var gw = await PaperGateway(db);
        var founding = Assert.Single(Standing(gw));

        var proposed = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Envelope), Json(EnvelopeWith("max_quantity", "0.2")))));
        await gw.RefreshHealthAsync();
        Assert.Equal(founding.Id, Assert.Single(Standing(gw)).Id);

        var confirmed = gw.BoardDecide(proposed.Act.Id, "board-2", null, true, proposed.Act.IntentSha256, "the paper book has room");
        Assert.Equal((BoardActEventKind.Confirmed, 2), (confirmed.Status, confirmed.PolicyVersion));
        Assert.Empty(Standing(gw));
        Assert.Equal(0.2m, gw.Policy.PaperEnvelopeFor("BTCUSDT")!.MaxQuantity);

        await gw.RefreshHealthAsync();
        var granted = Assert.Single(Standing(gw));
        log.WriteLine($"{granted.MaxQuantity} — {granted.Reason}");
        Assert.Equal((0.2m, "granted by the app under the security board's act, version 2"), (granted.MaxQuantity, granted.Reason));
        Assert.Contains(new LogStore(db).RecentActivity(200).Select(a => a.Text),
            t => t.StartsWith("The security board's version 2 lets TradeAgent run paper experiments", StringComparison.Ordinal));
    }
}
