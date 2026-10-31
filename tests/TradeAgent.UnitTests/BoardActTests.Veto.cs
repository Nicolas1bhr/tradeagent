using System.Text.Json.Nodes;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- item 9: the owner's veto narrows only -----------------------------------------------------------------------------------

    /// <summary>A version the test writes raw whose <paramref name="field"/> holds <paramref name="value"/> — the start each veto case needs.</summary>
    static void SeedField(Database db, string field, string value)
    {
        var inForce = new BoardReader(db).InForce();
        var root = JsonNode.Parse(BoardPolicyBody.Canonical(inForce.Policy))!.AsObject();
        var parts = field.Split('.');
        var parent = root;
        foreach (var part in parts[..^1]) parent = parent[part]!.AsObject();
        parent[parts[^1]] = JsonNode.Parse(value);
        var body = TapeJson.Canonical(root.ToJsonString());
        Assert.NotNull(BoardPolicyBody.Read(body, BoardPolicyBody.Sha256(body)).Policy);
        var act = $"bact-seed{inForce.Version + 1}";
        Exec(db, "INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at) "
                 + "VALUES($id, 'narrow', 'board-3', NULL, '[]', $sha, $v, 'the test seeds this version', '[]', NULL, NULL, $at)",
            ("$id", act), ("$sha", Sha256Hex.Of("[]")), ("$v", inForce.Version), ("$at", Stamp));
        Exec(db, "INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, supersedes) "
                 + "VALUES($next, 'board', 'act', $act, $at, $mode, 0, $body, $sha, $v)",
            ("$next", inForce.Version + 1), ("$act", act), ("$at", Stamp), ("$mode", BoardPolicyBody.Read(body, Sha256Hex.Of(body)).Policy!.Mode.ToString()),
            ("$body", body), ("$sha", Sha256Hex.Of(body)), ("$v", inForce.Version));
    }

    /// <summary>
    /// EVERY FIELD'S TRIPLE (before, wider, narrower-still): the act raises the field from <c>A</c> to <c>W</c>; a later act may narrow it
    /// on to <c>N</c>, narrower than <c>A</c> — or, for the allowlist, to a value neither narrower nor wider than <c>A</c>.
    /// </summary>
    static readonly (string Field, string A, string W, string N)[] Triples =
    [
        ("mode", "\"OBSERVE\"", "\"PAPER\"", "\"OBSERVE\""),
        ("risk.max_order_quantity", "2", "5", "1"),
        ("risk.max_notional_per_order", "1000", "0", "500"),
        ("risk.max_open_positions", "2", "4", "1"),
        ("risk.max_orders_per_minute", "6", "10", "3"),
        ("risk.instrument_allowlist", "[\"BTCUSDT\"]", "[\"BTCUSDT\",\"ETHUSDT\"]", "[\"ETHUSDT\"]"),
        ("risk.max_loss_per_trade", "100", "0", "50"),
        ("risk.max_daily_loss", "200", "400", "100"),
        ("risk.loss_min_closure_hours", "24", "12", "48"),
        ("risk.loss_strike_window_days", "7", "0", "14"),
        ("risk.valuation_loss_exit_minutes", "15", "30", "5"),
        ("plan_day_total", "40", "60", "20"),
        ("role_shares", "{\"research\":0.25}", "{\"research\":0.5}", "{\"research\":0.1}"),
        ("turn_allowance_input", "1000", "2000", "500"),
        ("turn_allowance_output", "1000", "2000", "500"),
        ("lanes", "2", "3", "1"),
        ("lanes_per_pool", "2", "3", "1"),
        ("lanes_per_cell_class", "2", "3", "1"),
        ("review_minutes", "30", "15", "0"),
        ("head_ceiling_hours", "24", "12", "0"),
        ("rewake_floor_minutes", "{\"team\":120}", "{\"team\":60}", "{\"team\":240}"),
        // EACH PAIR'S ENVELOPE ITS OWN FIELD (U-evidence-breadth-paper): the same triple for each founding pair.
        .. BoardFounding.PaperPairs.Select(p => ($"paper_envelopes.{p.Instrument}", For(p.Instrument, Envelope),
            For(p.Instrument, EnvelopeWith("max_quantity", "0.2")), For(p.Instrument, EnvelopeWith("max_quantity", "0.02"))))
    ];

    /// <summary>Every field's value in force, by path.</summary>
    static Dictionary<string, string> Values(Database db)
    {
        var body = BoardPolicyBody.Canonical(new BoardReader(db).InForce().Policy);
        return BoardDirection.Fields.ToDictionary(f => f.Path, f => BoardStore.ValueAt(body, f.Path));
    }

    /// <summary>
    /// A VETO NEVER WIDENS — on every field, with superseding acts between and without: a confirmed raise from <c>A</c> to <c>W</c>,
    /// vetoed at once, is set back to <c>A</c> in a version decided by the owner; vetoed after a later act narrowed the field on to
    /// <c>N</c>, it restores nothing wider than <c>N</c> — and where the two allow different things (two allowlists), what both allow.
    /// No field of the policy is wider after the veto than before it, and none is wider than the act's <c>from</c>. Watched red with
    /// <c>from</c> restored without the narrower-of.
    /// </summary>
    [Fact]
    public async Task A_veto_never_widens()
    {
        Assert.Equal(BoardDirection.Fields.Select(f => f.Path).Order(StringComparer.Ordinal), Triples.Select(t => t.Field).Order(StringComparer.Ordinal));
        foreach (var (field, a, w, n) in Triples)
            foreach (var superseded in new[] { false, true })
            {
                using var db = TestEnv.NewDb();
                SeedField(db, field, a);
                Activate(db, "board-1", "board-2", "board-3");
                var (gw, _) = await Gateway(db);
                await using var _gw = gw;

                var act = gw.BoardPropose(Act("board-1", Change(field, Json(a), Json(w))));
                Assert.Equal(BoardActEventKind.Confirmed, gw.BoardDecide(act.Act.Id, "board-2", null, true, act.Act.IntentSha256, "yes").Status);
                if (superseded && !BoardDirection.SameValue(w, n))
                    gw.BoardNarrow(Act("board-3", Change(field, Json(w), Json(n))));

                var before = Values(db);
                var preview = gw.PreviewBoardVeto(act.Act.Id);
                Assert.True(preview.Allowed, preview.Words);
                var vetoed = gw.VetoBoardAct(act.Act.Id, gw.PreviewBoardVeto(act.Act.Id));
                var after = Values(db);
                log.WriteLine($"{field} superseded={superseded}: {before[field]} -> {after[field]} ({vetoed.Words})");

                foreach (var f in BoardDirection.Fields)
                    Assert.NotEqual(BoardDirectionKind.Widens, BoardDirection.Of(f.Path, before[f.Path], after[f.Path]));
                Assert.NotEqual(BoardDirectionKind.Widens, BoardDirection.Of(field, a, after[field]));
                Assert.Equal(BoardActEventKind.Vetoed, new BoardStore(db).Status(act.Act.Id));
                if (!superseded)
                {
                    Assert.True(BoardDirection.SameValue(a, after[field]), $"{field}: {after[field]} is not {a}");
                    var inForce = new BoardReader(db).InForce();
                    Assert.Equal((vetoed.PolicyVersion, BoardDecider.Owner, BoardBasis.Veto), ((int?)inForce.Version, inForce.Decider, inForce.Basis));
                    Assert.Equal(act.Act.Id, Scalar(db, "SELECT act_id FROM board_policy WHERE version=$v", ("$v", inForce.Version)));
                    Assert.Equal([BoardDecider.Owner], new BoardReader(db).History()[^1].Deciders);
                }
            }
    }

    /// <summary>
    /// A VETO CANCELS AN OPEN PROPOSAL — closed <c>vetoed</c>, no version, no seat may decide it after — and the veto is refused, in
    /// words with nothing written, of an act that only narrowed (undoing it would widen), of one already vetoed and of one that expired
    /// having applied nothing. Press one says the same thing press two does.
    /// </summary>
    [Fact]
    public async Task A_veto_cancels_an_open_proposal()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;

        var open = gw.BoardPropose(Act("board-1", Raise()));
        var preview = gw.PreviewBoardVeto(open.Act.Id);
        Assert.True(preview.Allowed);
        Assert.Contains("closes the proposal", preview.Words, StringComparison.Ordinal);
        var vetoed = gw.VetoBoardAct(open.Act.Id, gw.PreviewBoardVeto(open.Act.Id));
        Assert.Equal((BoardActEventKind.Vetoed, (int?)null), (vetoed.Status, vetoed.PolicyVersion));
        Assert.Equal(BoardDecider.Owner, new BoardStore(db).Events(open.Act.Id)[^1].Seat);
        Assert.Equal(1, new BoardReader(db).InForce().Version);
        Assert.Throws<TradeAgentException>(() => gw.BoardDecide(open.Act.Id, "board-2", null, true, open.Act.IntentSha256, "yes"));

        var narrowed = gw.BoardNarrow(Act("board-1", Change("plan_day_total", 40, 30)));
        var expired = new BoardStore(db).Propose(Act("board-1", Raise(30m, 35m)), DateTimeOffset.UtcNow.AddDays(-2));
        new BoardStore(db).Expire(DateTimeOffset.UtcNow);
        var before = Counts(db);
        foreach (var (id, says) in new[] { (narrowed.Act.Id, "would WIDEN"), (open.Act.Id, "vetoed already"), (expired.Act.Id, "applied nothing") })
        {
            Assert.False(gw.PreviewBoardVeto(id).Allowed);
            var refused = Assert.Throws<TradeAgentException>(() => gw.VetoBoardAct(id, gw.PreviewBoardVeto(id)));
            log.WriteLine(refused.Message);
            Assert.Contains(says, refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(before, Counts(db));
        Assert.Equal(30m, gw.Policy.PlanDayTotal);
    }
}
