using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    /// <summary>
    /// AN ENVELOPE'S LIFE FOLLOWS EVERY ACT SINCE ITS LAST GRANT, NOT ONLY THE LAST ONE (<c>U-board-b2</c> item 8, from U-board-b1's re-review
    /// 2): an envelope of 0.05 granted for ten days; on day nine two seats widen it to 0.10, then one seat narrows it to 0.08. With a health
    /// pass between the two acts and without one, the 0.08 envelope the next pass grants ends on day NINETEEN — the widening's authorised
    /// life — and a narrowing alone still never lengthens one (b1's rule, <c>A_narrowing_never_lengthens_an_envelopes_life</c>). RED before
    /// the fix: with no pass between, it ended on day ten. Watched red with only the last act read.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_envelopes_life_follows_every_act_since_its_last_grant(bool passBetween)
    {
        using var db = TestEnv.NewDb();
        SeedField(db, "paper_envelope", EnvelopeWith("days", "10"));
        Activate(db, "board-1", "board-2");
        var clock = new Settable(DateTimeOffset.UtcNow);
        var inner = new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker
            { AccountId = BoardFounding.PaperAccount, IsSimulated = true }) { QuoteClock = clock };
        var conn = new RecordingConnector(inner, BoardFounding.PaperVenue);
        await using var gw = new TradeAgent.Gateway.TradingGateway(db, conn, new HealthRegistry(), new TradeAgent.Gateway.GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        bool Stands(PaperEnvelopeRow e) => e.StandsAt(clock.Now);
        var first = Assert.Single(gw.Envelopes.All(), Stands);
        var day0 = clock.Now;
        Assert.Equal(day0.AddDays(10), first.ExpiresAt);

        clock.Now = day0.AddDays(9);
        string With(decimal quantity)
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(EnvelopeWith("days", "10"))!.AsObject();
            o["max_quantity"] = quantity;
            return o.ToJsonString();
        }
        var widened = gw.BoardPropose(Act("board-1", Change("paper_envelope", Json(With(0.05m)), Json(With(0.10m)))));
        gw.BoardDecide(widened.Act.Id, "board-2", null, true, widened.Act.IntentSha256, "the book has room");
        if (passBetween)
        {
            await gw.RefreshHealthAsync();
            Assert.Equal((0.10m, day0.AddDays(19)), Assert.Single(gw.Envelopes.All(), Stands) is var w ? (w.MaxQuantity, w.ExpiresAt) : default);
        }
        gw.BoardNarrow(Act("board-1", Change("paper_envelope", Json(With(0.10m)), Json(With(0.08m)))));
        await gw.RefreshHealthAsync();

        var granted = Assert.Single(gw.Envelopes.All(), Stands);
        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        Assert.Equal((0.08m, day0.AddDays(19)), (granted.MaxQuantity, granted.ExpiresAt));
    }

    /// <summary>
    /// A VETOED WIDENING NEVER LENGTHENS AN ENVELOPE'S LIFE (AUTH-001; the re-review's P1-2): an envelope of 0.05 granted for ten days; on
    /// day nine two seats widen it to 0.10, the owner vetoes that widening (back to 0.05), then one seat narrows to 0.04. With no health pass
    /// between and with one after the widening, the 0.04 envelope the next pass grants ends on day TEN — the life granted before the vetoed
    /// widening — never day nineteen: revoked authority renews nothing, and where in doubt the envelope errs short. The third case (the
    /// re-review's): after the widening's pass, one seat narrows to 0.08 and a pass grants it — keeping the widening's life, day nineteen —
    /// THEN the owner vetoes the widening; the 0.08 envelope inherited that life from the vetoed widening, so the next narrowing still ends
    /// on day ten. RED before the fix: with no pass between, the vetoed widening still counted and it ended on day nineteen; RED on the
    /// fix's first cut for the third case (the inherited life was read from the 0.08 grant). Watched red with the veto unread.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_vetoed_widening_never_lengthens_an_envelopes_life(bool passAfterWidening, bool narrowedBeforeVeto)
    {
        using var db = TestEnv.NewDb();
        SeedField(db, "paper_envelope", EnvelopeWith("days", "10"));
        Activate(db, "board-1", "board-2");
        var clock = new Settable(DateTimeOffset.UtcNow);
        var inner = new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker
            { AccountId = BoardFounding.PaperAccount, IsSimulated = true }) { QuoteClock = clock };
        var conn = new RecordingConnector(inner, BoardFounding.PaperVenue);
        await using var gw = new TradeAgent.Gateway.TradingGateway(db, conn, new HealthRegistry(), new TradeAgent.Gateway.GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        bool Stands(PaperEnvelopeRow e) => e.StandsAt(clock.Now);
        var first = Assert.Single(gw.Envelopes.All(), Stands);
        var day0 = clock.Now;
        Assert.Equal(day0.AddDays(10), first.ExpiresAt);

        clock.Now = day0.AddDays(9);
        string With(decimal quantity)
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(EnvelopeWith("days", "10"))!.AsObject();
            o["max_quantity"] = quantity;
            return o.ToJsonString();
        }
        var widened = gw.BoardPropose(Act("board-1", Change("paper_envelope", Json(With(0.05m)), Json(With(0.10m)))));
        gw.BoardDecide(widened.Act.Id, "board-2", null, true, widened.Act.IntentSha256, "the book has room");
        if (passAfterWidening)
        {
            await gw.RefreshHealthAsync();
            Assert.Equal((0.10m, day0.AddDays(19)), Assert.Single(gw.Envelopes.All(), Stands) is var w ? (w.MaxQuantity, w.ExpiresAt) : default);
        }
        if (narrowedBeforeVeto)
        {
            clock.Now = clock.Now.AddMinutes(1);
            gw.BoardNarrow(Act("board-1", Change("paper_envelope", Json(With(0.10m)), Json(With(0.08m)))));
            await gw.RefreshHealthAsync();
            Assert.Equal((0.08m, day0.AddDays(19)), Assert.Single(gw.Envelopes.All(), Stands) is var n ? (n.MaxQuantity, n.ExpiresAt) : default);
        }
        gw.VetoBoardAct(widened.Act.Id);
        gw.BoardNarrow(Act("board-1", Change("paper_envelope", Json(With(0.05m)), Json(With(0.04m)))));
        await gw.RefreshHealthAsync();

        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        var granted = Assert.Single(gw.Envelopes.All(), Stands);
        Assert.Equal((0.04m, day0.AddDays(10)), (granted.MaxQuantity, granted.ExpiresAt));
    }
}
