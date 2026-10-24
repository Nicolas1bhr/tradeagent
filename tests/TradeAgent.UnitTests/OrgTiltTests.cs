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
    static Database Chart(decimal siblingAi = 0.5m)
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
        Assert.True(new CampaignStore(db).Open("BTCUSDT 1m v1", store.ById(id)!, 10, 5, Now).Ok);
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

        var ai = org.Tilt(Ask(OrgCurrency.Ai, 0.35m), Metered, Now);
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
        if (scenario == "second-tilt") org.Tilt(Ask(OrgCurrency.Ai, 0.35m), Metered, Now);
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
            "second-tilt" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.5m), Metered, Now),
            "siblings" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.35m), Metered, Now),
            "unfundable" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.005m), Metered, Now),
            "unmetered" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m), null, Now),
            "worthless" => () => org.Tilt(Ask(OrgCurrency.Trials, 0.05m), null, Now),
            "member" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m, caller: Member), Metered, Now),
            "own-head" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m, caller: TeamHead), Metered, Now),
            "root" => () => org.Tilt(Ask(OrgCurrency.Trials, 0.3m, unit: OrgIds.Root), null, Now),
            "division-ai" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m, caller: Chief, unit: OrgIds.Research), Metered, Now),
            "currency" => () => org.Tilt(Ask("paper-slots", 0.3m), null, Now),
            "no-forecast" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.3m, forecasts: []), Metered, Now),
            "same" => () => org.Tilt(Ask(OrgCurrency.Ai, 0.2m), Metered, Now),
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
}
