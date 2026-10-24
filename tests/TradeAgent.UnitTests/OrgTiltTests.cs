using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A HEAD TILTS ITS CHILDREN'S SHARES (<c>U-org-verbs-e</c> item 5; ORCHESTRATION § 2.9; ORGANISATION §§ 3, 8): one currency per call —
/// AI, trials or verdicts — each an <c>envelope-tilt</c> decision with a forecast and a <c>tilted</c> event, at most twenty points a
/// week, inside the parent's whole, fundable, worth at least one of an open campaign's — or refused in words with nothing written. A
/// charter carries trial and verdict shares too, and <c>org-show</c> shows all three. Store-level, but for <c>org-show</c>.
/// </summary>
public class OrgTiltTests
{
    const string Head = "pos-71170000a001", Member = "pos-71170000a002", TeamHead = "pos-71170000a003", Chief = "pos-71170000a004";

    const string Div = "div-71170000", Team = "team-71170001", Sibling = "team-71170002";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    static readonly CharterFunding Metered = new(SeatClasses.Harness, 0.10m, [new(BillingClasses.Key, 10m)]);

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static long Count(Database db, string table) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table}");
        return (long)c.ExecuteScalar()!;
    });

    static void Unit(Database db, string id, string parent, string kind, string? head, string? ai = null, string? trials = null)
    {
        Exec(db, """
            INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, envelope_share, created_at, trial_share)
            VALUES($t,$p,$k,NULL,'active',$ai,$at,$tr)
            """, ("$t", id), ("$p", parent), ("$k", kind), ("$ai", ai), ("$at", Stamp), ("$tr", trials));
        if (head is null) return;
        Seat(db, head, id);
        Exec(db, "UPDATE org_unit SET head_position_id=$h WHERE id=$t", ("$h", head), ("$t", id));
    }

    static void Seat(Database db, string id, string unit) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$at", Stamp));

    /// <summary>
    /// <see cref="Head"/> heads <see cref="Div"/> under the organisation, with a member; under it <see cref="Team"/> (AI 0.2, headed by
    /// <see cref="TeamHead"/>) and <see cref="Sibling"/> (AI 0.5, trials 0.6); one open campaign of 10 trials and 5 judgements.
    /// </summary>
    static Database Chart(decimal siblingAi = 0.5m, int verdicts = 5)
    {
        var db = TestEnv.NewDb();
        Unit(db, Div, OrgIds.Root, OrgUnitKind.Division, Head);
        Seat(db, Member, Div);
        Unit(db, Team, Div, OrgUnitKind.Team, TeamHead, ai: "0.2");
        Unit(db, Sibling, Div, OrgUnitKind.Team, null, ai: siblingAi.ToString(CultureInfo.InvariantCulture), trials: "0.6");
        var store = new DatasetStore(db);
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), "aa11", 1000,
            cutoff.AddDays(-300), cutoff.AddDays(60), 0, [], false, 0, 0, 0, Now, DatasetState.ACCEPTED, null, []));
        Assert.True(store.SetHoldout(id, cutoff, EvaluationClass.Research).Ok);
        Assert.True(new CampaignStore(db).Open("BTCUSDT 1m v1", store.ById(id)!, 10, verdicts, Now).Ok);
        return db;
    }

    static ForecastClaim Admissible(string? subject = null) =>
        new(ForecastVocabulary.UnitAdmissible, subject, ForecastVocabulary.Admitted, Now.AddDays(45), 0.4m);

    static TiltRequest Ask(string currency, decimal share, string caller = Head, string unit = Team, IReadOnlyList<ForecastClaim>? forecasts = null) =>
        new(caller, null, unit, currency, share, "the skew family earns more of the division's room", ["hold", "tilt"], [],
            forecasts ?? [Admissible()]);

    static string? ShareOf(Database db, string unit, string currency) =>
        OrgCurrency.Of(new OrgStore(db).Units().Single(u => u.Id == unit), currency);

    static long[] Written(Database db) =>
        [Count(db, "org_event"), Count(db, "org_decision"), Count(db, "org_forecast"), Count(db, "ledger_entry")];

    /// <summary>
    /// THE DIVISION HEAD TILTS ITS TEAM IN EACH CURRENCY, ONE PER CALL: the AI share by 0.15 (0.2 → 0.35), and a trial and a verdict
    /// share set from none — each an <c>envelope-tilt</c> decision, its forecast on the unit, and one <c>tilted</c> event naming the move.
    /// RED at base: no tilt.
    /// </summary>
    [Fact]
    public void A_division_head_tilts_its_teams_share_in_each_currency()
    {
        using var db = Chart();
        var org = new OrgStore(db);

        var ai = org.Tilt(Ask(OrgCurrency.Ai, 0.35m), Metered, Now, Seats);
        Assert.Equal(("0.2", "0.35"), (ai.From, ai.To));
        Assert.Equal("0.35", ShareOf(db, Team, OrgCurrency.Ai));
        Assert.Equal(OrgDecisionKind.EnvelopeTilt, ai.Decision.Kind);
        Assert.Equal(Team, Assert.Single(ai.Forecasts).Subject);

        var trials = org.Tilt(Ask(OrgCurrency.Trials, 0.2m), null, Now);
        Assert.Null(trials.From);
        Assert.Equal("0.2", ShareOf(db, Team, OrgCurrency.Trials));
        Assert.Equal("0.2", org.Tilt(Ask(OrgCurrency.Verdicts, 0.2m), null, Now).To);
        Assert.Equal("0.2", ShareOf(db, Team, OrgCurrency.Verdicts));

        var events = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT detail FROM org_event WHERE kind=$k AND unit_id=$u ORDER BY id", ("$k", OrgEventKind.Tilted), ("$u", Team));
            using var r = c.ExecuteReader();
            var all = new List<string>();
            while (r.Read()) all.Add(r.GetString(0));
            return all;
        });
        Assert.Equal(3, events.Count);
        Assert.StartsWith($"unit={Team} currency=ai from=0.2 to=0.35 move=0.15 decision=", events[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// EACH REFUSAL, ONE CASE EACH, NOTHING WRITTEN: a second 0.15 tilt that week; a split leaving the siblings past the whole; an AI
    /// share that cannot fund one turn of the seat; no meter to size it; a trial share worth less than one trial; a member, the team's
    /// own head, and the organisation's head-less root (a board act); a division's AI share under the organisation (the board's split);
    /// an unknown currency; no forecast; the share it already holds.
    /// </summary>
    [Theory]
    [InlineData("second-tilt", "moved 0.15 in the last seven days")]
    [InlineData("siblings", "would split more than the whole")]
    [InlineData("unfundable", "more than the whole")]
    [InlineData("unmetered", "meters no AI envelope")]
    [InlineData("worthless", "less than one")]
    [InlineData("member", "only the parent unit's head")]
    [InlineData("own-head", "only the parent unit's head")]
    [InlineData("root", OrgRightAnswer.BoardAct)]
    [InlineData("division-ai", "security board's policy")]
    [InlineData("currency", "no currency")]
    [InlineData("no-forecast", "1 to")]
    [InlineData("same", "nothing to tilt")]
    public void Each_tilt_refusal_writes_nothing(string scenario, string words)
    {
        using var db = Chart(siblingAi: scenario == "siblings" ? 0.7m : 0.5m);
        var org = new OrgStore(db);
        if (scenario == "second-tilt") org.Tilt(Ask(OrgCurrency.Ai, 0.35m), Metered, Now, Seats);
        if (scenario == "division-ai")
        {
            // THE CHIEF HEADS THE ORGANISATION, so the matrix allows it: the refusal is the board's split, never a missing right.
            Seat(db, Chief, OrgIds.Root);
            Exec(db, "UPDATE org_unit SET head_position_id=$h WHERE id=$r", ("$h", Chief), ("$r", OrgIds.Root));
        }
        var before = Written(db);
        var shares = OrgCurrency.All.Select(c => ShareOf(db, Team, c)).ToList();

        Func<Tilted> act = scenario switch
        {
            "second-tilt" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.5m), Metered, Now, Seats),
            "siblings" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.35m), Metered, Now, Seats),
            "unfundable" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.005m), Metered, Now, Seats),
            "unmetered" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m), null, Now),
            "worthless" => () => org.Tilt(Ask(OrgCurrency.Trials, 0.05m), null, Now),
            "member" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m, caller: Member), Metered, Now, Seats),
            "own-head" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m, caller: TeamHead), Metered, Now, Seats),
            "root" => () => org.Tilt(Ask(OrgCurrency.Trials, 0.3m, unit: OrgIds.Root), null, Now),
            "division-ai" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m, caller: Chief, unit: OrgIds.Research), Metered, Now, Seats),
            "currency" => () => org.Tilt(Ask("paper-slots", 0.3m), null, Now),
            "no-forecast" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m, forecasts: []), Metered, Now, Seats),
            "same" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.2m), Metered, Now, Seats),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var refused = Assert.Throws<TradeAgentException>(() => act());
        Assert.Contains(words, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        Assert.Equal(shares, OrgCurrency.All.Select(c => ShareOf(db, Team, c)).ToList());
    }

    /// <summary>
    /// A CHARTER CARRIES TRIAL AND VERDICT SHARES TOO, checked as a tilt checks them: written when they fit, refused when the siblings'
    /// with it pass the whole. RED at base: the request carried no such share.
    /// </summary>
    [Fact]
    public void A_charter_carries_trial_and_verdict_shares()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        CharterRequest Charter(decimal trials) => new(Head, null, OrgUnitKind.Team, 0.1m, "a third family", ["a team"], [], [Admissible()],
            Now.AddDays(30), TrialShare: trials, VerdictShare: 0.4m);

        var made = org.Charter(Charter(0.3m), Metered, Now);
        Assert.Equal(("0.3", "0.4"), (made.Unit.TrialShare, made.Unit.VerdictShare));

        var past = Assert.Throws<TradeAgentException>(() => org.Charter(Charter(0.2m), Metered, Now));
        Assert.Contains("would split more than the whole", past.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ORG-SHOW SHOWS ALL THREE DIMENSIONS of every unit of the caller's chain: its AI, trial and verdict shares as the rows hold them
    /// (none: no own cap). RED at base: only the AI envelopes were shown.
    /// </summary>
    [Fact]
    public async Task Org_show_shows_all_three_dimensions()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        Unit(db, Div, OrgIds.Root, OrgUnitKind.Division, Head);
        Unit(db, Team, Div, OrgUnitKind.Team, TeamHead, ai: "0.2", trials: "0.3");
        Exec(db, "UPDATE org_unit SET verdict_share='0.25' WHERE id=$t", ("$t", Team));
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());

        var id = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = TeamHead, ReservedCost = 0.10m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.Name(SeatAssurance.Brokered)
        }).Admitted);
        var reply = await server.CallAsync(new IpcRequest { Op = Ops.OrgShow, Session = "tilt", RequestId = "tilt-show" }, TeamHead, id);
        Assert.True(reply.Ok, reply.Error?.Message);
        var chain = JsonDocument.Parse(Json.Write(reply.Data)).RootElement.GetProperty("chain").EnumerateArray().ToList();
        var team = chain[0].GetProperty("shares");
        Assert.Equal(("0.2", "0.3", "0.25"),
            (team.GetProperty("ai").GetString(), team.GetProperty("trials").GetString(), team.GetProperty("verdicts").GetString()));
        Assert.False(chain[1].GetProperty("shares").TryGetProperty("trials", out _), "a unit with no trial share of its own was shown one");
    }

    // ---- the fix pass --------------------------------------------------------------------------------------------

    /// <summary>
    /// One turn of each seat, as the host's meter prices it — the team's head 1.5, every other 0.1 — each held to the money cap, where
    /// the division's envelope is 10 (as <see cref="Metered"/>'s).
    /// </summary>
    static SeatTurn? Seats(string position) => new(position == TeamHead ? 1.5m : 0.1m, [new(BillingClasses.Key, 10m)]);

    /// <summary>The team's head bills the plan, reserving 0.3, where the division's envelope is <paramref name="plan"/>; every other seat as <see cref="Seats"/>.</summary>
    static Func<string, SeatTurn?> PlanTeamHead(decimal plan) =>
        position => position == TeamHead ? new SeatTurn(0.3m, [new(BillingClasses.Plan, plan)]) : Seats(position);

    /// <summary>
    /// FIX PASS (the Codex review's P2, its settling check): AN AI TILT THAT WOULD STRAND A SEATED CHILD IS REFUSED. The head's own turn
    /// reserves 0.1; the team's head, seated in the team, reserves 1.5; the team's envelope would fall from 2 (0.2 of 10) to 1 — inside
    /// the weekly bound — so its seat could not start one turn: refused, naming the seat, nothing written. RED before the fix: only the
    /// calling head's reservation was read, and the tilt was written.
    /// </summary>
    [Fact]
    public void An_ai_tilt_that_would_strand_a_seated_child_is_refused()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var before = Written(db);

        var refused = Assert.Throws<TradeAgentException>(() => org.Tilt(Ask(OrgCurrency.Ai, 0.1m), Metered, Now, Seats));
        Assert.Contains(TeamHead, refused.Message, StringComparison.Ordinal);
        Assert.Contains("more than the whole", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        Assert.Equal("0.2", ShareOf(db, Team, OrgCurrency.Ai));

        // A TILT THAT LEAVES IT ITS TURN IS WRITTEN: 0.2 → 0.35, an envelope of 3.5.
        Assert.Equal("0.35", org.Tilt(Ask(OrgCurrency.Ai, 0.35m), Metered, Now, Seats).To);
    }

    /// <summary>
    /// FIX PASS 2 (the Codex re-review's P2): EACH SEATED CHILD IS JUDGED IN ITS OWN CEILINGS, NOT THE CALLING HEAD'S. The head bills a key,
    /// its division's envelope 10 in the money cap; the team's head bills the plan and reserves 0.3, where the division's envelope is 2.
    /// 0.2 → 0.1 leaves the team 1 in the money cap but 0.2 in the plan's — less than the child's turn: refused, naming the seat and the
    /// plan, nothing written. RED before the fix: the child was judged against the head's 10, and the tilt was written.
    /// </summary>
    [Fact]
    public void A_tilt_that_strands_a_plan_billed_child_under_a_key_billed_head_is_refused()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var before = Written(db);

        var refused = Assert.Throws<TradeAgentException>(() => org.Tilt(Ask(OrgCurrency.Ai, 0.1m), Metered, Now, PlanTeamHead(plan: 2m)));
        Assert.Contains(TeamHead, refused.Message, StringComparison.Ordinal);
        Assert.Contains(BillingClasses.Words(BillingClasses.Plan), refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        Assert.Equal("0.2", ShareOf(db, Team, OrgCurrency.Ai));
    }

    /// <summary>
    /// ITS TWIN: A FUNDABLE CHILD IS NOT REFUSED BECAUSE OF THE HEAD'S SMALLER CEILING. The head bills a key, its division's envelope 2 in
    /// the money cap; the team's head bills the plan and reserves 0.3, where the division's envelope is 10. 0.2 → 0.1 leaves the team 1 in
    /// the plan's ceiling — the child's turn fits — and the head's own turn (0.1) fits the 0.2 the key ceiling leaves: written. RED
    /// before the fix: the child was judged against the head's 2, and a fundable tilt was refused.
    /// </summary>
    [Fact]
    public void A_fundable_plan_billed_child_is_not_refused_for_a_key_billed_heads_smaller_ceiling()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var smallKey = Metered with { Parent = [new(BillingClasses.Key, 2m)] };

        Assert.Equal("0.1", org.Tilt(Ask(OrgCurrency.Ai, 0.1m), smallKey, Now, PlanTeamHead(plan: 10m)).To);
        Assert.Equal("0.1", ShareOf(db, Team, OrgCurrency.Ai));
    }

    /// <summary>
    /// FIX PASS (seat B's ruling on the Codex review's P2, its settling check — budget 3, NULL start): THE FIRST DELEGATION OF A TRIAL OR
    /// VERDICT SHARE IS A DELEGATION, NOT A TILT. From none to 0.34 of a 3-judgement campaign is written past the weekly 0.20, as an
    /// <c>envelope-tilt</c> decision with its forecast; it is still bounded by the siblings' whole and by one judgement's worth; every
    /// later change is a tilt under the weekly bound, which the delegation does not count. RED before the fix: from none was a move
    /// from 0, so every share worth one of three judgements moved more than 0.20 and no first delegation could be made.
    /// </summary>
    [Fact]
    public void A_first_delegation_is_not_a_weekly_tilt_and_every_later_change_is()
    {
        using var db = Chart(verdicts: 3);
        var org = new OrgStore(db);

        var delegated = org.Tilt(Ask(OrgCurrency.Verdicts, 0.34m), null, Now);
        Assert.Equal(((string?)null, "0.34"), (delegated.From, delegated.To));
        Assert.Equal(OrgDecisionKind.EnvelopeTilt, delegated.Decision.Kind);
        Assert.Single(delegated.Forecasts);

        // STILL BOUNDED: one judgement's worth, and the siblings' whole (the sibling holds 0.6 of the trials).
        Assert.Contains("less than one", Assert.Throws<TradeAgentException>(() => org.Tilt(Ask(OrgCurrency.Verdicts, 0.2m, unit: Sibling), null, Now)).Message,
            StringComparison.Ordinal);
        Assert.Contains("would split more than the whole",
            Assert.Throws<TradeAgentException>(() => org.Tilt(Ask(OrgCurrency.Trials, 0.5m), null, Now)).Message, StringComparison.Ordinal);

        // EVERY LATER CHANGE IS A TILT: 0.33 more in the week is refused; 0.16 is not, the delegation uncounted.
        Assert.Contains("in the last seven days",
            Assert.Throws<TradeAgentException>(() => org.Tilt(Ask(OrgCurrency.Verdicts, 0.67m), null, Now)).Message, StringComparison.Ordinal);
        Assert.Equal("0.5", org.Tilt(Ask(OrgCurrency.Verdicts, 0.5m), null, Now).To);
    }
}
