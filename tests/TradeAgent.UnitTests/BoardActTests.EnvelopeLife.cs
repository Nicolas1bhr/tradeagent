using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    /// <summary>
    /// AN ENVELOPE'S LIFE FOLLOWS EVERY ACT SINCE ITS LAST GRANT, NOT ONLY THE LAST ONE (<c>U-board-b2</c> item 8, from U-board-b1's re-review
    /// 2): an envelope of 0.1 granted for ten days; on day nine two seats widen it to 0.2, then one seat narrows it to 0.16. With a health
    /// pass between the two acts and without one, the 0.16 envelope the next pass grants ends on day NINETEEN — the widening's authorised
    /// life — and a narrowing alone still never lengthens one (b1's rule, <c>A_narrowing_never_lengthens_an_envelopes_life</c>). RED before
    /// the fix: with no pass between, it ended on day ten. Watched red with only the last act read.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_envelopes_life_follows_every_act_since_its_last_grant(bool passBetween)
    {
        using var db = TestEnv.NewDb();
        SeedField(db, "paper_envelopes.BTCUSDT", EnvelopeWith("days", "10"));
        Activate(db, "board-1", "board-2");
        var clock = new Settable(DateTimeOffset.UtcNow);
        var inner = new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker
            { AccountId = BoardFounding.PaperAccount, IsSimulated = true }) { QuoteClock = clock };
        var conn = new RecordingConnector(inner, BoardFounding.PaperVenue);
        await using var gw = new TradeAgent.Gateway.TradingGateway(db, conn, new HealthRegistry(), new TradeAgent.Gateway.GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        bool Stands(PaperEnvelopeRow e) => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT";
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
        var widened = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(With(0.1m)), Json(With(0.2m)))));
        gw.BoardDecide(widened.Act.Id, "board-2", null, true, widened.Act.IntentSha256, "the book has room");
        if (passBetween)
        {
            await gw.RefreshHealthAsync();
            Assert.Equal((0.2m, day0.AddDays(19)), Assert.Single(gw.Envelopes.All(), Stands) is var w ? (w.MaxQuantity, w.ExpiresAt) : default);
        }
        gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(With(0.2m)), Json(With(0.16m)))));
        await gw.RefreshHealthAsync();

        var granted = Assert.Single(gw.Envelopes.All(), Stands);
        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        Assert.Equal((0.16m, day0.AddDays(19)), (granted.MaxQuantity, granted.ExpiresAt));
    }

    /// <summary>
    /// A VETOED WIDENING NEVER LENGTHENS AN ENVELOPE'S LIFE (AUTH-001; the re-review's P1-2): an envelope of 0.1 granted for ten days; on
    /// day nine two seats widen it to 0.2, the owner vetoes that widening (back to 0.1), then one seat narrows to 0.08. With no health pass
    /// between and with one after the widening, the 0.08 envelope the next pass grants ends on day TEN — the life granted before the vetoed
    /// widening — never day nineteen: revoked authority renews nothing, and where in doubt the envelope errs short. The third case (the
    /// re-review's): after the widening's pass, one seat narrows to 0.16 and a pass grants it — keeping the widening's life, day nineteen —
    /// THEN the owner vetoes the widening; the 0.16 envelope inherited that life from the vetoed widening, so the next narrowing still ends
    /// on day ten. RED before the fix: with no pass between, the vetoed widening still counted and it ended on day nineteen; RED on the
    /// fix's first cut for the third case (the inherited life was read from the 0.16 grant). Watched red with the veto unread.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_vetoed_widening_never_lengthens_an_envelopes_life(bool passAfterWidening, bool narrowedBeforeVeto)
    {
        using var db = TestEnv.NewDb();
        SeedField(db, "paper_envelopes.BTCUSDT", EnvelopeWith("days", "10"));
        Activate(db, "board-1", "board-2");
        var clock = new Settable(DateTimeOffset.UtcNow);
        var inner = new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker
            { AccountId = BoardFounding.PaperAccount, IsSimulated = true }) { QuoteClock = clock };
        var conn = new RecordingConnector(inner, BoardFounding.PaperVenue);
        await using var gw = new TradeAgent.Gateway.TradingGateway(db, conn, new HealthRegistry(), new TradeAgent.Gateway.GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        bool Stands(PaperEnvelopeRow e) => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT";
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
        var widened = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(With(0.1m)), Json(With(0.2m)))));
        gw.BoardDecide(widened.Act.Id, "board-2", null, true, widened.Act.IntentSha256, "the book has room");
        if (passAfterWidening)
        {
            await gw.RefreshHealthAsync();
            Assert.Equal((0.2m, day0.AddDays(19)), Assert.Single(gw.Envelopes.All(), Stands) is var w ? (w.MaxQuantity, w.ExpiresAt) : default);
        }
        if (narrowedBeforeVeto)
        {
            clock.Now = clock.Now.AddMinutes(1);
            gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(With(0.2m)), Json(With(0.16m)))));
            await gw.RefreshHealthAsync();
            Assert.Equal((0.16m, day0.AddDays(19)), Assert.Single(gw.Envelopes.All(), Stands) is var n ? (n.MaxQuantity, n.ExpiresAt) : default);
        }
        gw.VetoBoardAct(widened.Act.Id, gw.PreviewBoardVeto(widened.Act.Id));
        gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(With(0.1m)), Json(With(0.08m)))));
        await gw.RefreshHealthAsync();

        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        var granted = Assert.Single(gw.Envelopes.All(), Stands);
        Assert.Equal((0.08m, day0.AddDays(10)), (granted.MaxQuantity, granted.ExpiresAt));
    }

    /// <summary>A gateway on the founding paper venue with an envelope of 0.1 for ten days granted at day 0, and its clock.</summary>
    async Task<(TradeAgent.Gateway.TradingGateway Gw, Settable Clock, DateTimeOffset Day0, RecordingConnector Conn)> TenDayEnvelope(Database db)
    {
        SeedField(db, "paper_envelopes.BTCUSDT", EnvelopeWith("days", "10"));
        Activate(db, "board-1", "board-2");
        var clock = new Settable(DateTimeOffset.UtcNow);
        var inner = new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker
            { AccountId = BoardFounding.PaperAccount, IsSimulated = true }) { QuoteClock = clock };
        var conn = new RecordingConnector(inner, BoardFounding.PaperVenue);
        var gw = new TradeAgent.Gateway.TradingGateway(db, conn, new HealthRegistry(), new TradeAgent.Gateway.GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        var day0 = clock.Now;
        Assert.Equal(day0.AddDays(10), Assert.Single(gw.Envelopes.All(), e => e.StandsAt(day0) && e.Symbol == "BTCUSDT").ExpiresAt);
        return (gw, clock, day0, conn);
    }

    static string Terms(decimal quantity, int days)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(EnvelopeWith("days", days.ToString(System.Globalization.CultureInfo.InvariantCulture)))!.AsObject();
        o["max_quantity"] = quantity;
        return o.ToJsonString();
    }

    /// <summary>
    /// A VETO THAT RESTORES NOTHING STILL TAKES BACK THE LIFE IT REVOKED (the fix pass's re-review 2): 0.1 for ten days; on day nine two
    /// seats widen to 0.2, a pass grants it to day nineteen, one seat narrows to 0.08 and a pass grants that — keeping day nineteen — and
    /// THEN the owner vetoes the widening. 0.08 is already narrower than 0.1, so the veto writes no version and moves no terms; the
    /// standing envelope's life past day ten rested on the vetoed widening alone, so the veto withdraws it in its own write, and no pass
    /// grants anything past day ten. RED before: the 0.08 envelope stood to day nineteen.
    /// </summary>
    [Fact]
    public async Task A_veto_that_restores_no_terms_still_withdraws_the_life_it_revoked()
    {
        using var db = TestEnv.NewDb();
        var (gw, clock, day0, _) = await TenDayEnvelope(db);
        await using var _gw = gw;
        clock.Now = day0.AddDays(9);
        var widened = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Terms(0.1m, 10)), Json(Terms(0.2m, 10)))));
        gw.BoardDecide(widened.Act.Id, "board-2", null, true, widened.Act.IntentSha256, "the book has room");
        await gw.RefreshHealthAsync();
        clock.Now = clock.Now.AddMinutes(1);
        gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Terms(0.2m, 10)), Json(Terms(0.08m, 10)))));
        await gw.RefreshHealthAsync();
        Assert.Equal(day0.AddDays(19), Assert.Single(gw.Envelopes.All(), e => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT").ExpiresAt);

        var vetoed = gw.VetoBoardAct(widened.Act.Id, gw.PreviewBoardVeto(widened.Act.Id));
        Assert.Null(vetoed.PolicyVersion);
        await gw.RefreshHealthAsync();
        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        Assert.DoesNotContain(gw.Envelopes.All(), e => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT" && e.ExpiresAt > day0.AddDays(10));
        Assert.DoesNotContain(gw.Envelopes.All(), e => e.StandsAt(day0.AddDays(11)) && e.Symbol == "BTCUSDT");
    }

    /// <summary>
    /// A SURVIVING WIDENING NEVER CARRIES A VETOED ONE'S DURATION (the fix pass's re-review 2): 0.1 for ten days; on day nine two seats
    /// confirm A (0.1 → 0.2) and then B (ten days → twenty) with no pass between, and the next pass grants to day twenty-nine. The owner
    /// vetoes B; on day ten one seat narrows to 0.18. The replacement never stands past day nineteen — A's own ten days — and where the
    /// grant's life cannot be split between A and the vetoed B it errs short. RED before: the replacement stood to day twenty.
    /// </summary>
    [Fact]
    public async Task A_surviving_widening_never_carries_a_vetoed_ones_duration()
    {
        using var db = TestEnv.NewDb();
        var (gw, clock, day0, _) = await TenDayEnvelope(db);
        await using var _gw = gw;
        clock.Now = day0.AddDays(9);
        var a = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Terms(0.1m, 10)), Json(Terms(0.2m, 10)))));
        gw.BoardDecide(a.Act.Id, "board-2", null, true, a.Act.IntentSha256, "the book has room");
        var b = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Terms(0.2m, 10)), Json(Terms(0.2m, 20)))));
        gw.BoardDecide(b.Act.Id, "board-2", null, true, b.Act.IntentSha256, "a longer run");
        await gw.RefreshHealthAsync();
        Assert.Equal(day0.AddDays(29), Assert.Single(gw.Envelopes.All(), e => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT").ExpiresAt);

        gw.VetoBoardAct(b.Act.Id, gw.PreviewBoardVeto(b.Act.Id));
        clock.Now = day0.AddDays(10);
        gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Terms(0.2m, 10)), Json(Terms(0.18m, 10)))));
        await gw.RefreshHealthAsync();
        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        Assert.DoesNotContain(gw.Envelopes.All(), e => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT" && e.ExpiresAt > day0.AddDays(19));
    }

    /// <summary>
    /// A VETOED CHIEF ACT TAKES BACK NO ENVELOPE'S LIFE AND BLOCKS NO RENEWAL (U-board-b2 re-stacked on U-org-chief; the seam between them): a
    /// chief act writes no policy version and its confirmed event names the version IN FORCE it was decided under — here the widening's —
    /// so its veto must not read as a veto of that version. 0.1 for ten days; on day nine two seats widen to 0.2 (granted to day
    /// nineteen); two seats then appoint a chief and the owner vetoes it: the 0.2 envelope still stands to day nineteen. On day fifteen two
    /// seats widen to 0.24 and the next pass renews it to day twenty-five. The contrast, a vetoed POLICY act: the owner vetoes that widening
    /// and the envelope standing past day nineteen is withdrawn in the veto's write. Watched red with <see cref="BoardStore.VetoedVersion"/>
    /// also counting a vetoed act whose event names the version.
    /// </summary>
    [Fact]
    public async Task A_vetoed_chief_act_neither_withdraws_an_envelope_nor_blocks_its_renewal()
    {
        using var db = TestEnv.NewDb();
        var (gw, clock, day0, _) = await TenDayEnvelope(db);
        await using var _gw = gw;
        gw.KnownRuntime = runtime => runtime == "puppet";
        PaperEnvelopeRow Standing() => Assert.Single(gw.Envelopes.All(), e => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT");
        clock.Now = day0.AddDays(9);
        var widened = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Terms(0.1m, 10)), Json(Terms(0.2m, 10)))));
        var v = gw.BoardDecide(widened.Act.Id, "board-2", null, true, widened.Act.IntentSha256, "the book has room").PolicyVersion;
        await gw.RefreshHealthAsync();
        Assert.Equal((0.2m, day0.AddDays(19)), (Standing().MaxQuantity, Standing().ExpiresAt));

        clock.Now = clock.Now.AddMinutes(1);
        var chief = gw.BoardPropose(Act("board-1", """[{"field":"org.chief","from":null,"to":{"runtime":"puppet","model":"scripted-1"}}]"""));
        var seated = gw.BoardDecide(chief.Act.Id, "board-2", null, true, chief.Act.IntentSha256, "the root needs a head");
        Assert.Equal((BoardActEventKind.Confirmed, (int?)null), (seated.Status, seated.PolicyVersion));
        Assert.NotNull(new OrgStore(db).Chief());
        var vetoed = gw.VetoBoardAct(chief.Act.Id, gw.PreviewBoardVeto(chief.Act.Id));
        Assert.Equal((BoardActEventKind.Vetoed, (int?)null), (vetoed.Status, vetoed.PolicyVersion));
        Assert.Null(new OrgStore(db).Chief());
        Assert.False(new BoardStore(db).VetoedVersion(v!.Value), $"vetoing the chief act {chief.Act.Id} read as a veto of version {v}");
        await gw.RefreshHealthAsync();
        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        Assert.Equal((0.2m, day0.AddDays(19)), (Standing().MaxQuantity, Standing().ExpiresAt));

        clock.Now = day0.AddDays(15);
        var renewed = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Terms(0.2m, 10)), Json(Terms(0.24m, 10)))));
        gw.BoardDecide(renewed.Act.Id, "board-2", null, true, renewed.Act.IntentSha256, "more room");
        await gw.RefreshHealthAsync();
        Assert.Equal((0.24m, day0.AddDays(25)), (Standing().MaxQuantity, Standing().ExpiresAt));

        gw.VetoBoardAct(renewed.Act.Id, gw.PreviewBoardVeto(renewed.Act.Id));
        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        Assert.DoesNotContain(gw.Envelopes.All(), e => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT" && e.ExpiresAt > day0.AddDays(19));
    }
}
