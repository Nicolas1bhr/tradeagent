using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- the fixtures: a seat made active, a version a test writes, a gateway on the simulator ----------------------------------

    /// <summary>Makes <paramref name="seats"/> ACTIVE through the test host's seam — the only writer of <c>active</c> in this build.</summary>
    static void Activate(Database db, params string[] seats)
    {
        var store = new BoardStore(db);
        foreach (var seat in seats) store.SetSeatStateUnderTest(seat, BoardSeatStatus.Active, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A VERSION THE TEST WRITES as a board act's, with raw SQL — the precondition a test needs (an allowlist and a quantity the
    /// simulator's instruments fit), never a path the product has: the founding version's body with <paramref name="change"/> applied.
    /// </summary>
    static int SeedVersion(Database db, Func<BoardPolicy, BoardPolicy> change)
    {
        var inForce = new BoardReader(db).InForce();
        var body = BoardPolicyBody.Canonical(change(inForce.Policy));
        var act = $"bact-seed{inForce.Version + 1}";
        Exec(db, "INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at) "
                 + "VALUES($id, 'narrow', 'board-3', NULL, '[]', $sha, $v, 'the test seeds this version', '[]', NULL, NULL, $at)",
            ("$id", act), ("$sha", Sha256Hex.Of("[]")), ("$v", inForce.Version), ("$at", Stamp));
        Exec(db, "INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, supersedes) "
                 + "VALUES($next, 'board', 'act', $act, $at, 'PAPER', 0, $body, $sha, $v)",
            ("$next", inForce.Version + 1), ("$act", act), ("$at", Stamp), ("$body", body), ("$sha", Sha256Hex.Of(body)), ("$v", inForce.Version));
        return inForce.Version + 1;
    }

    /// <summary>The founding policy with the simulator's instrument allowed and a quantity of ten.</summary>
    static BoardPolicy Tradable(BoardPolicy p)
    {
        var risk = p.Risk;
        risk.MaxOrderQuantity = 10m;
        risk.InstrumentAllowlist = ["ES", "BTCUSDT"];
        return p with { Risk = risk };
    }

    static async Task<(TradingGateway Gw, FakeConnector Conn)> Gateway(Database db)
    {
        var conn = new FakeConnector(new FakeBroker { IsSimulated = true });
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s => s.SelectedAccountId = conn.Broker.AccountId);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn);
    }

    static BoardActRequest Act(string seat, string intent, string rationale = "the seat's own reasons, a claim",
        IReadOnlyList<OrgRef>? refs = null, IReadOnlyList<ForecastClaim>? forecasts = null) =>
        new(seat, null, intent, rationale, refs ?? [], forecasts ?? []);

    static string Change(string field, object from, object to) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["field"] = field, ["from"] = from, ["to"] = to } });

    static string Counts(Database db) => Scalar(db,
        "SELECT (SELECT COUNT(*) FROM board_policy) || '/' || (SELECT COUNT(*) FROM board_act) || '/' || (SELECT COUNT(*) FROM board_act_event)"
        + " || '/' || (SELECT COUNT(*) FROM board_forecast)")!;

    // ---- item 3: one seat narrows, at once -----------------------------------------------------------------------------------

    /// <summary>
    /// A NARROWING BY ONE SEAT IS IN FORCE FOR THE NEXT ORDER: an active seat lowers the quantity limit from 10 to 2, and the version it
    /// writes — decided by <c>board</c> on the basis <c>act</c>, superseding the one in force, its rationale on the act and its seat the
    /// one decider — is the policy the gateway holds the very next order to: an order of 3 that passed a moment before is refused. A
    /// RAISE by one seat is refused in words, nothing written. RED before <c>Narrow</c>. Watched red with the narrowing check skipped.
    /// </summary>
    [Fact]
    public async Task A_narrowing_by_one_seat_is_in_force_for_the_next_order()
    {
        using var db = TestEnv.NewDb();
        var seeded = SeedVersion(db, Tradable);
        Activate(db, "board-1");
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        Assert.Equal(10m, gw.Policy.Risk.MaxOrderQuantity);
        await gw.PlaceAsync(new AgentContext("a"), "narrow-before", TestEnv.Buy("ES", 3m));

        var result = gw.BoardNarrow(Act("board-1", Change("risk.max_order_quantity", 10, 2), "three at once is more than this book should carry"));
        log.WriteLine(result.Words);
        Assert.Equal((BoardActEventKind.Applied, seeded + 1), (result.Status, result.PolicyVersion));
        Assert.Equal(2m, gw.Policy.Risk.MaxOrderQuantity);
        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "narrow-after", TestEnv.Buy("ES", 3m)));
        log.WriteLine($"{refused.Code}: {refused.Message}");
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, refused.Code);

        var reader = new BoardReader(db);
        var inForce = reader.InForce();
        Assert.False(inForce.CouldNotBeRead, inForce.Why);
        Assert.Equal((seeded + 1, BoardDecider.Board, BoardBasis.Act), (inForce.Version, inForce.Decider, inForce.Basis));
        Assert.Contains(inForce.Fields, f => f.Name == "risk.max_order_quantity" && f.Value == "2" && f.SetByVersion == seeded + 1);
        var last = reader.History()[^1];
        Assert.Equal("three at once is more than this book should carry", last.Rationale);
        Assert.Equal(["board-1"], last.Deciders);
        var act = Assert.Single(reader.Acts(), a => a.Act.Id == result.Act.Id);
        Assert.Equal((BoardActKind.Narrow, seeded, BoardActEventKind.Applied), (act.Act.Kind, act.Act.PolicyVersion, act.Status));
        Assert.Equal(seeded + 1, Assert.Single(act.Events).PolicyVersion);
        Assert.Equal(result.Act.Id, Scalar(db, "SELECT act_id FROM board_policy WHERE version=$v", ("$v", seeded + 1)));

        // A RAISE BY ONE SEAT: refused, in words, and nothing written.
        var before = Counts(db);
        var raise = Assert.Throws<TradeAgentException>(() =>
            gw.BoardNarrow(Act("board-1", Change("risk.max_order_quantity", 2, 5))));
        log.WriteLine(raise.Message);
        Assert.Contains("widens", raise.Message, StringComparison.Ordinal);
        Assert.Equal(before, Counts(db));
        Assert.Equal(2m, gw.Policy.Risk.MaxOrderQuantity);
    }

    /// <summary>
    /// A NARROWING AGAINST A VALUE NO LONGER IN FORCE IS REFUSED, and so is every act whose maker or matter is not what it must be — a
    /// dormant seat, a position, refs that cite nothing, a forecast outside the vocabulary — each in words, with nothing written. RED
    /// before <c>Narrow</c>.
    /// </summary>
    [Fact]
    public void A_narrowing_against_a_value_no_longer_in_force_is_refused()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var store = new BoardStore(db);
        var first = store.Narrow(Act("board-1", Change("plan_day_total", 40, 30)), DateTimeOffset.UtcNow);
        Assert.Equal(2, first.PolicyVersion);
        var before = Counts(db);

        foreach (var (request, says) in new[]
                 {
                     (Act("board-2", Change("plan_day_total", 40, 20)), "holds 30, not 40"),
                     (Act("board-3", Change("plan_day_total", 30, 20)), "dormant, not active"),
                     (Act(CouncilRoles.Research, Change("plan_day_total", 30, 20)), "no seat of the security board"),
                     (Act("board-2", Change("plan_day_total", 30, 20), refs: [new OrgRef(OrgRefKind.Version, "ver-nowhere")]), "not in the app's ledgers"),
                     (Act("board-2", Change("plan_day_total", 30, 20),
                         forecasts: [new ForecastClaim("price-above", OrgIds.Research, "yes", DateTimeOffset.UtcNow.AddDays(1), 0.5m)]), "no predicate"),
                     (Act("board-2", Change("plan_day_total", 30, 20), rationale: " "), "own words"),
                     (Act("board-2", "[{\"field\":\"live_activated\",\"from\":false,\"to\":true}]"), "real money")
                 })
        {
            var refused = Assert.Throws<TradeAgentException>(() => store.Narrow(request, DateTimeOffset.UtcNow));
            log.WriteLine(refused.Message);
            Assert.Contains(says, refused.Message, StringComparison.Ordinal);
            Assert.EndsWith("Nothing was written.", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(before, Counts(db));
        Assert.Equal(30m, new BoardReader(db).InForce().Policy.PlanDayTotal);
    }

    /// <summary>
    /// A BOARD ACT'S FORECAST IS THE APP'S TO RESOLVE: written with the act, scored, and resolved by the organisation's own sweep from the
    /// app's measurements into the board's resolution table — once, however often the sweep runs.
    /// </summary>
    [Fact]
    public void A_board_forecast_is_resolved_by_the_apps_sweep()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1");
        var now = DateTimeOffset.UtcNow;
        var claim = new ForecastClaim(ForecastVocabulary.UnitAdmissible, OrgIds.Research, ForecastVocabulary.NoneAdmitted, now.AddDays(1), 0.8m);
        var act = new BoardStore(db).Narrow(Act("board-1", Change("plan_day_total", 40, 35), forecasts: [claim]), now);
        var forecast = Assert.Single(new BoardStore(db).Forecasts(act.Act.Id));
        Assert.True(forecast.Scored);
        Assert.Equal("board-1", forecast.Forecaster);

        Assert.Equal(0, new ForecastResolver(db).Sweep(now.AddHours(1)).Resolved);
        Assert.Equal(1, new ForecastResolver(db).Sweep(now.AddDays(2)).Resolved);
        Assert.Equal(0, new ForecastResolver(db).Sweep(now.AddDays(3)).Resolved);
        var resolution = new BoardStore(db).Resolution(forecast.Id)!;
        Assert.Equal(ForecastOutcome.Yes, resolution.Outcome);
        Assert.Single(new BoardReader(db).Act(act.Act.Id)!.Resolutions);
    }
}
