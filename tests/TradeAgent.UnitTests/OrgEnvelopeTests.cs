using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// SPEND IS RESERVED AGAINST THE UNIT TREE (<c>U-org-envelopes</c>, <c>docs/ORCHESTRATION.md</c> § 2.4): a position's launch is
/// admitted only when, for every unit of its funding chain to the root, that unit's spent + unresolved reservations + this
/// reservation fit its envelope — read inside <see cref="AiAttemptStore.Begin"/>'s one transaction — and the chain it was
/// admitted under is recorded with the row and never moves when the chart does.
///
/// <para>The chart is written by SQL, as only a test may: no verb writes it yet (<c>U-org-verbs</c>). Every launch below reserves
/// the global allowance at the owner's own rate, 1.20 M in at 1.00 and 20 k out at 4.00 = 1.28.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgEnvelopeTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"envelopes-{Guid.NewGuid():n}.jsonl");
    readonly DateTimeOffset _now = DateTimeOffset.Now;

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        _db.Dispose();
    }

    const decimal Reservation = (1_200_000m * 1m + 20_000m * 4m) / 1_000_000m;   // 1.28

    /// <summary>A meter over this test's database. Each one built is a RESTART: it declares every open row LOST.</summary>
    TurnMeter Meter(decimal cap, Func<string, decimal>? share = null) =>
        new(_db, () => cap, runtimeId: () => "codex", now: () => _now, recordPath: _records,
            owner: () => new OwnerPrice(1m, 4m), model: () => "gpt-5.6-sol", share: share ?? (_ => 1m),
            live: new LiveAttempts());

    void Unit(string id, string parent, string? share = null, string kind = OrgUnitKind.Team) => Exec("""
        INSERT INTO org_unit(id, parent_id, kind, status, envelope_share, created_at)
        VALUES($id, $parent, $kind, 'active', $share, '2026-10-11T12:00:00.0000000Z')
        """, ("$id", id), ("$parent", parent), ("$kind", kind), ("$share", share));

    void Seat(string id, string unit) => Exec("""
        INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
        VALUES($id, $unit, $home, 'active', '2026-10-11T12:00:00.0000000Z')
        """, ("$id", id), ("$unit", unit), ("$home", $"org/{id}"));

    void Exec(string sql, params (string, object?)[] ps) => _db.Write(_ =>
    {
        using var c = _db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    long Count(string sql, params (string, object?)[] ps) => _db.Read(_ =>
    {
        using var c = _db.Cmd(sql, ps);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>The funding chain recorded with an attempt, nearest unit first.</summary>
    List<string> Chain(string attemptId) => _db.Read(_ =>
    {
        using var c = _db.Cmd("SELECT unit_id FROM ai_attempt_funding WHERE attempt_id=$id ORDER BY depth",
            ("$id", attemptId));
        using var r = c.ExecuteReader();
        var units = new List<string>();
        while (r.Read()) units.Add(r.GetString(0));
        return units;
    });

    /// <summary>
    /// A subdivision under Research with room for exactly one reservation (0.2 × 10.00 = 2.00), and a team under it per
    /// position. Neither team has an envelope of its own, so the subdivision's is the one that binds.
    /// </summary>
    void TwoSiblingTeams()
    {
        Unit("sub-1", OrgIds.Research, "0.2", OrgUnitKind.Subdivision);
        Unit("team-1", "sub-1");
        Unit("team-2", "sub-1");
        Seat("pos-1", "team-1");
        Seat("pos-2", "team-2");
    }

    // ---- (a) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) TWO SIBLING TEAMS CANNOT BOTH TAKE THE PARENT'S LAST ROOM. Two real threads, both through
    /// <see cref="TurnMeter.Begin"/>, both released together: the parent's totals are read in the transaction that writes
    /// the reservation, so the second sees the first. And the sequential half: two rules built on an EMPTY ledger, handed to
    /// the store one after the other — a rule that carried the parent's totals would admit both.
    /// </summary>
    [Fact]
    public void Two_sibling_teams_cannot_both_take_the_parents_last_room()
    {
        TwoSiblingTeams();
        var meter = Meter(10m);

        var admissions = new AiAdmission[2];
        using var go = new Barrier(2);
        void Admit(int i, string who)
        {
            go.SignalAndWait();
            admissions[i] = meter.Begin("## Situation", role: who);
        }

        var a = new Thread(() => Admit(0, "pos-1"));
        var b = new Thread(() => Admit(1, "pos-2"));
        a.Start();
        b.Start();
        Assert.True(a.Join(TimeSpan.FromSeconds(30)), "the first admission never finished");
        Assert.True(b.Join(TimeSpan.FromSeconds(30)), "the second admission never finished");

        Assert.Single(admissions, x => x.Admitted);
        var refused = Assert.Single(admissions, x => !x.Admitted);
        Assert.Equal("sub-1", refused.Ceiling);
        Assert.Equal(0.56m, refused.Over);   // 1.28 committed + 1.28 asked for, against 2.00
        Assert.Equal(1, Count("SELECT COUNT(*) FROM ai_attempt"));
        Assert.True(meter.Today.AdmitsAnotherTurn);   // the DAY has room; the parent does not
    }

    /// <summary>(a), the sequential half: rules built before either reservation, admitted one after the other.</summary>
    [Fact]
    public void Two_sibling_teams_cannot_both_take_the_parents_last_room_with_rules_built_on_an_empty_ledger()
    {
        TwoSiblingTeams();
        var meter = Meter(10m);
        var store = new AiAttemptStore(_db);

        var first = meter.AdmissionRuleFor("pos-1");
        var second = meter.AdmissionRuleFor("pos-2");

        Assert.True(store.Begin(Attempt("seq-1", "pos-1", first), admit: first).Admitted);
        var refused = store.Begin(Attempt("seq-2", "pos-2", second), admit: second);
        Assert.False(refused.Admitted);
        Assert.Equal("sub-1", refused.Ceiling);
        Assert.Null(store.Get("seq-2"));
    }

    AiAttempt Attempt(string id, string role, AiAdmissionRule rule) => new()
    {
        Id = id, StartedAt = _now, ReservedCost = Reservation, State = AiAttemptState.LAUNCHED, Role = role,
        Funding = [.. rule.Funding!.Select(u => u.UnitId)]
    };

    // ---- (b) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (b) A UNIT OVER ITS ENVELOPE IS REFUSED WHILE ITS SIBLING LAUNCHES. Day 20.00; Research keeps the whole day (the legacy
    /// share, 1); team-1 has 0.1 of it (2.00), team-2 0.5 (10.00). The second launch of team-1 is refused by team-1 by name,
    /// and team-2's launch beside it is admitted.
    /// </summary>
    [Fact]
    public void A_unit_over_its_envelope_is_refused_while_its_sibling_launches()
    {
        Unit("team-1", OrgIds.Research, "0.1");
        Unit("team-2", OrgIds.Research, "0.5");
        Seat("pos-1", "team-1");
        Seat("pos-2", "team-2");
        var meter = Meter(20m);

        Assert.True(meter.Begin("## Situation", role: "pos-1").Admitted);
        var refused = meter.Begin("## Situation", role: "pos-1");

        Assert.False(refused.Admitted);
        Assert.Null(refused.Id);
        Assert.Equal("team-1", refused.Ceiling);
        Assert.Equal(0.56m, refused.Over);   // 1.28 + 1.28 against 2.00
        Assert.Contains("team-1", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("2.00", refused.Refusal, StringComparison.Ordinal);   // its envelope
        Assert.Contains("0.72", refused.Refusal, StringComparison.Ordinal);   // its room

        Assert.True(meter.Begin("## Situation", role: "pos-2").Admitted);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM ai_attempt"));
    }

    // ---- (c) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) THE LEGACY PAIR ADMITS EXACTLY AS BEFORE — a GUARD. With no envelope set anywhere, each division's envelope is its
    /// position's legacy share of the day, and a launch with no role is the chair's, so a sequence of launches by the two
    /// legacy positions and by nobody is admitted, refused and measured over exactly where the pre-tree rule put it: the day
    /// first, then <c>spent + reserved + this ≤ cap × share</c> over the role's rows, a NULL role read as the chair's.
    /// </summary>
    [Theory]
    [InlineData("10", "0.5", "0.5")]
    [InlineData("10", "0.3", "0.9")]
    [InlineData("5", "1", "1")]
    [InlineData("6.4", "0.25", "0.75")]
    [InlineData("20", "0.6", "0.2")]
    public void The_legacy_pair_admits_exactly_as_before(string capText, string opsText, string resText)
    {
        var cap = decimal.Parse(capText, CultureInfo.InvariantCulture);
        var shares = new Dictionary<string, decimal>
        {
            [CouncilRoles.Operations] = decimal.Parse(opsText, CultureInfo.InvariantCulture),
            [CouncilRoles.Research] = decimal.Parse(resText, CultureInfo.InvariantCulture)
        };
        var meter = Meter(cap, role => shares[role]);

        decimal day = 0m;
        var role = new Dictionary<string, decimal> { [CouncilRoles.Operations] = 0m, [CouncilRoles.Research] = 0m };
        string?[] order =
        [
            CouncilRoles.Research, CouncilRoles.Operations, null, CouncilRoles.Research, CouncilRoles.Operations,
            CouncilRoles.Research, null, CouncilRoles.Operations, CouncilRoles.Research, CouncilRoles.Operations,
            CouncilRoles.Research, null, CouncilRoles.Operations, CouncilRoles.Research
        ];
        foreach (var who in order)
        {
            // THE PRE-TREE RULE, written out: the day, then the role's slice (a NULL role has none, and counts as the chair's).
            var charged = who ?? CouncilRoles.Default;
            var dayOver = day + Reservation - cap;
            var roleOver = who is null ? 0m : role[charged] + Reservation - cap * shares[charged];
            var admit = dayOver <= 0m && roleOver <= 0m;

            var got = meter.Begin("## Situation", role: who);
            Assert.True(admit == got.Admitted,
                $"{who ?? "(no role)"} at day {day}, role {role[charged]}: expected {(admit ? "admitted" : "refused")}");
            if (admit)
            {
                day += Reservation;
                role[charged] += Reservation;
            }
            else if (dayOver > 0m)
            {
                Assert.Equal(AiAdmission.Day, got.Ceiling);
                Assert.Equal(dayOver, got.Over);
            }
            else
            {
                Assert.NotEqual(AiAdmission.Day, got.Ceiling);
                Assert.Equal(roleOver, got.Over);
            }
        }
        Assert.Equal(day, meter.Today.Reserved);
    }

    // ---- (d) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) A LOST ATTEMPT KEEPS ITS RESERVATION IN EVERY ANCESTOR ACROSS A RESTART. A subdivision with 3.00 (0.15 of 20.00) and
    /// two teams under it; every launch is followed by a restart, which declares it LOST at its reservation. Two fit; the third
    /// is refused by the subdivision — the LOST rows are still its, and the root's.
    /// </summary>
    [Fact]
    public void A_lost_attempt_keeps_its_reservation_in_every_ancestor_across_a_restart()
    {
        Unit("sub-1", OrgIds.Research, "0.15", OrgUnitKind.Subdivision);
        Unit("team-1", "sub-1");
        Unit("team-2", "sub-1");
        Seat("pos-1", "team-1");
        Seat("pos-2", "team-2");

        var first = Meter(20m).Begin("## Situation", role: "pos-1");
        Assert.True(first.Admitted);
        var second = Meter(20m).Begin("## Situation", role: "pos-2");   // a restart: the first is LOST
        Assert.True(second.Admitted);

        var refused = Meter(20m).Begin("## Situation", role: "pos-1");   // and again: both are LOST
        Assert.False(refused.Admitted);
        Assert.Equal("sub-1", refused.Ceiling);
        Assert.Equal(0.84m, refused.Over);   // 1.28 + 1.28 lost + 1.28 asked for, against 3.00

        var store = new AiAttemptStore(_db);
        Assert.Equal(AiAttemptState.LOST, store.Get(first.Id!)!.State);
        Assert.Equal(AiAttemptState.LOST, store.Get(second.Id!)!.State);
        Assert.Equal(["team-1", "sub-1", OrgIds.Research, OrgIds.Root], Chain(first.Id!));
        Assert.Equal(["team-2", "sub-1", OrgIds.Research, OrgIds.Root], Chain(second.Id!));
    }

    // ---- (e) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (e) A SEAT WHOSE RESERVATION EXCEEDS ITS UNIT'S WHOLE ENVELOPE IS REFUSED BY NAME: a team with 0.1 of a 10.00 day has
    /// 1.00, and a turn reserves 1.28 — a split that cannot fund its own seat, refused in a sentence naming the seat, the
    /// reservation and the envelope, on an empty day.
    /// </summary>
    [Fact]
    public void A_seat_whose_reservation_exceeds_its_units_whole_envelope_is_refused_by_name()
    {
        Unit("team-1", OrgIds.Research, "0.1");
        Seat("pos-1", "team-1");
        var meter = Meter(10m);

        var refused = meter.Begin("## Situation", role: "pos-1");

        Assert.False(refused.Admitted);
        Assert.Equal("team-1", refused.Ceiling);
        Assert.Equal(0.28m, refused.Over);
        Assert.Contains("pos-1", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("1.28", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("1.00", refused.Refusal, StringComparison.Ordinal);
        Assert.Equal(OrgStore.Unfundable("pos-1", "team-1", 1.00m, Reservation), refused.Refusal);
        Assert.Null(OrgStore.Unfundable("pos-1", "team-1", Reservation, Reservation));   // a turn that fits exactly is fundable
        Assert.Equal(0, Count("SELECT COUNT(*) FROM ai_attempt"));
    }

    // ---- (g) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) NON-POSITION SPEND STILL COUNTS AGAINST THE DAILY CAP UNDER THE TREE. Perception's 7.00 is in the day's total and in
    /// no unit's: a team with 5.00 of a 10.00 day takes two launches (its subtree holds 2.56), and the third is refused by
    /// the DAY, not by the team — and perception's rows carry no funding chain.
    /// </summary>
    [Fact]
    public void Non_position_spend_still_counts_against_the_daily_cap_under_the_tree()
    {
        Unit("team-1", OrgIds.Research, "0.5");
        Seat("pos-1", "team-1");
        var meter = Meter(10m);
        var store = new AiAttemptStore(_db);
        store.Begin(new AiAttempt
        {
            Id = "decision-1", StartedAt = _now, ReservedCost = 7m, State = AiAttemptState.LAUNCHED,
            Role = AppPrincipals.Perception
        });

        var one = meter.Begin("## Situation", role: "pos-1");
        var two = meter.Begin("## Situation", role: "pos-1");
        Assert.True(one.Admitted);
        Assert.True(two.Admitted);
        Assert.Equal(["team-1", OrgIds.Research, OrgIds.Root], Chain(one.Id!));

        var refused = meter.Begin("## Situation", role: "pos-1");
        Assert.False(refused.Admitted);
        Assert.Equal(AiAdmission.Day, refused.Ceiling);
        Assert.Equal(0.84m, refused.Over);   // 7.00 + 2.56 + 1.28 against 10.00
        Assert.Empty(Chain("decision-1"));
    }

    // ---- (h) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (h) A POSITION WHOSE FUNDING CHAIN CANNOT BE READ IS REFUSED, never admitted on the day gate alone: a share that is not a
    /// fraction, and a line of parents that loops. Nothing is written.
    /// </summary>
    [Fact]
    public void A_position_whose_funding_chain_cannot_be_read_is_refused()
    {
        Unit("team-bad", OrgIds.Research, "half");
        Seat("pos-bad", "team-bad");
        Unit("loop-a", OrgIds.Research);
        Unit("loop-b", "loop-a");
        Exec("UPDATE org_unit SET parent_id='loop-b' WHERE id='loop-a'");
        Seat("pos-loop", "loop-b");
        var meter = Meter(10m);

        foreach (var who in new[] { "pos-bad", "pos-loop" })
        {
            var refused = meter.Begin("## Situation", role: who);
            Assert.False(refused.Admitted, $"{who} was admitted on a chain nobody could read");
            Assert.Null(refused.Id);
            Assert.Contains(who, refused.Refusal, StringComparison.Ordinal);
        }
        Assert.Equal(0, Count("SELECT COUNT(*) FROM ai_attempt"));
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Research).Admitted);   // the readable chain still runs
    }

    // ---- one cap per admission ---------------------------------------------------------------------------------------

    /// <summary>
    /// ONE ADMISSION READS THE DAY'S CAP ONCE, and sizes the unit envelopes and the rule's own cap from that one value. The owner
    /// lowers the cap from 10.00 to 2.00 between two reads: were the envelopes sized from the first and the rule's cap from the
    /// second, Research's 5.00 and the root's 10.00 would sit inside a 2.00 day — a child envelope larger than the cap it is a
    /// share of (BUDGET-001), and a launch admitted past the room its unit has against the cap the rule enforces.
    /// </summary>
    [Fact]
    public void One_admission_reads_the_days_cap_once_and_sizes_its_envelopes_and_its_rule_from_it()
    {
        Unit("team-1", OrgIds.Research);
        Seat("pos-1", "team-1");
        var reads = 0;
        decimal Lowered() => ++reads == 1 ? 10m : 2m;   // the owner lowers the cap the instant after the first read
        var meter = new TurnMeter(_db, Lowered, runtimeId: () => "codex", now: () => _now, recordPath: _records,
            owner: () => new OwnerPrice(1m, 4m), model: () => "gpt-5.6-sol", share: _ => 0.5m, live: new LiveAttempts());

        reads = 0;
        var rule = meter.AdmissionRuleFor("pos-1");
        Assert.All(rule.Funding!, u => Assert.True(u.Envelope is not { } e || e <= rule.Cap,
            $"{u.UnitId}'s envelope {u.Envelope} is larger than the cap {rule.Cap} this rule enforces"));
        Assert.Equal(rule.Cap, rule.Funding!.Single(u => u.UnitId == OrgIds.Root).Envelope);
        Assert.Equal(rule.Cap * 0.5m, rule.Funding!.Single(u => u.UnitId == OrgIds.Research).Envelope);
        Assert.Equal(1, reads);

        reads = 0;
        Assert.True(meter.Begin("## Situation", role: "pos-1").Admitted);   // admitted against 10.00, every number of it
        Assert.Equal(1, reads);
    }

    // ---- item 3: envelope resolution ---------------------------------------------------------------------------------

    /// <summary>
    /// ENVELOPES ARE FRACTIONS OF THE PARENT'S, the root's is the day's cap: a legacy division with no share reads its position's
    /// split, a share is that fraction of the nearest envelope above, a unit with none has no separate cap, and a share that is
    /// not a fraction between 0 and 1 is refused as damage.
    /// </summary>
    [Fact]
    public void An_envelope_is_a_share_of_its_parents_and_a_legacy_division_reads_its_split()
    {
        static OrgUnitRow U(string id, string? parent, string? share) => new(id, parent,
            parent is null ? OrgUnitKind.Root : OrgUnitKind.Team, null, OrgUnitStatus.Active, share, null, DateTimeOffset.UnixEpoch, null);
        decimal Split(string role) => role == CouncilRoles.Research ? 0.25m : 0.75m;

        var root = U(OrgIds.Root, null, null);
        var research = U(OrgIds.Research, OrgIds.Root, null);
        var sub = U("sub", OrgIds.Research, "0.5");
        var open = U("open", "sub", null);
        var team = U("team", "open", "0.1");

        Assert.Equal(
            [new FundedUnit("team", 0.2m), new FundedUnit("open", null), new FundedUnit("sub", 2m),
             new FundedUnit(OrgIds.Research, 4m), new FundedUnit(OrgIds.Root, 16m)],
            OrgEnvelopes.Resolve([team, open, sub, research, root], 16m, Split));
        Assert.Equal([new FundedUnit(OrgIds.Operations, 12m), new FundedUnit(OrgIds.Root, 16m)],
            OrgEnvelopes.Resolve([U(OrgIds.Operations, OrgIds.Root, null), root], 16m, Split));
        Assert.Equal([new FundedUnit(OrgIds.Research, 8m), new FundedUnit(OrgIds.Root, 16m)],
            OrgEnvelopes.Resolve([U(OrgIds.Research, OrgIds.Root, "0.5"), root], 16m, Split));   // a share set wins over the split

        foreach (var bad in new[] { "half", "1.5", "-0.1", "1e-1", "" })
            Assert.Throws<TradeAgentException>(() => OrgEnvelopes.Resolve([U("t", OrgIds.Root, bad), root], 16m, Split));
    }

    // ---- item 1: the chain, recorded --------------------------------------------------------------------------------

    /// <summary>
    /// A LAUNCH RECORDS THE UNITS THAT FUND IT, in its own commit: a third position's chain is its team, Research and the
    /// root; a launch with no role is the chair's; perception's call is funded by no unit.
    /// </summary>
    [Fact]
    public void A_launch_records_its_funding_chain_with_its_row()
    {
        Unit("team-1", OrgIds.Research);
        Seat("pos-1", "team-1");
        var meter = Meter(50m);

        var third = meter.Begin("## Situation", role: "pos-1");
        var nobody = meter.Begin("## Situation");
        var research = meter.Begin("## Situation", role: CouncilRoles.Research);

        var store = new AiAttemptStore(_db);
        Assert.Equal(["team-1", OrgIds.Research, OrgIds.Root], store.FundingOf(third.Id!));
        Assert.Equal([OrgIds.Operations, OrgIds.Root], store.FundingOf(nobody.Id!));
        Assert.Equal([OrgIds.Research, OrgIds.Root], store.FundingOf(research.Id!));
        Assert.Equal(["team-1", OrgIds.Research, OrgIds.Root], Chain(third.Id!));
    }

    /// <summary>
    /// RUNG 38: <c>ai_attempt_funding</c> arrives, and a home's existing rows are charged from their role as the ledger always
    /// read it — NULL the chair's, a position its unit's chain, an app principal and a name that is no position none. A CRASH
    /// BETWEEN THE RUNG'S STATEMENTS AND ITS STAMP reruns it over what is there: nothing written twice, and a chain already
    /// recorded is never re-derived from a chart that has moved since.
    /// </summary>
    [Fact]
    public void The_funding_chain_arrives_at_its_rung_and_fills_each_existing_row_from_its_role()
    {
        // A FLOOR, for the reason OrgLedgerTests gives: a later rung must not fail here for no reason of its own.
        Assert.True(Versions.DatabaseSchemaVersion >= FundingRung, $"the chain arrives at rung {FundingRung}; this build says {Versions.DatabaseSchemaVersion}");
        var file = Path.Combine(TestEnv.Home, $"funding-rung-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
        {
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ai_attempt_funding'"));
            Write(db, "INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES('team-1', 'div-research', 'team', 'active', '2026-10-11T12:00:00.0000000Z')");
            Write(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('pos-1', 'team-1', 'org/pos-1', 'active', '2026-10-11T12:00:00.0000000Z')");
            var store = new AiAttemptStore(db);
            foreach (var (id, role) in new (string, string?)[]
                     {
                         ("a-null", null), ("a-ops", CouncilRoles.Operations), ("a-res", CouncilRoles.Research),
                         ("a-pos", "pos-1"), ("a-perception", AppPrincipals.Perception), ("a-ghost", "nobody-here")
                     })
                store.Begin(new AiAttempt { Id = id, StartedAt = _now, ReservedCost = 1m, Role = role });
        }

        // A HOME FROM BEFORE THE RUNG: its rows, no funding table, stamped 37.
        using (var raw = Raw(file))
            RawExec(raw, $"DROP TABLE ai_attempt_funding; UPDATE meta SET value='{FundingRung - 1}' WHERE key='schema_version';");
        using (var raw = Raw(file))
            Assert.Equal(6L, RawScalar(raw, "SELECT COUNT(*) FROM ai_attempt"));

        void AssertFilled(Database db)
        {
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), Scalar(db, "SELECT value FROM meta WHERE key='schema_version'")?.ToString());
            var store = new AiAttemptStore(db);
            Assert.Equal([OrgIds.Operations, OrgIds.Root], store.FundingOf("a-null"));
            Assert.Equal([OrgIds.Operations, OrgIds.Root], store.FundingOf("a-ops"));
            Assert.Equal([OrgIds.Research, OrgIds.Root], store.FundingOf("a-res"));
            Assert.Equal(["team-1", OrgIds.Research, OrgIds.Root], store.FundingOf("a-pos"));
            Assert.Empty(store.FundingOf("a-perception"));
            Assert.Empty(store.FundingOf("a-ghost"));
            Assert.Equal(9L, Scalar(db, "SELECT COUNT(*) FROM ai_attempt_funding"));
        }

        using (var db = new Database(file)) AssertFilled(db);

        // THE CRASH: every row in place and the stamp one lower — and the chart moved meanwhile. Nothing recorded moves.
        using (var raw = Raw(file))
            RawExec(raw, $"UPDATE org_unit SET parent_id='div-operations' WHERE id='team-1'; UPDATE meta SET value='{FundingRung - 1}' WHERE key='schema_version';");
        using (var db = new Database(file)) AssertFilled(db);
    }

    const int FundingRung = 38;

    static object? Scalar(Database db, string sql) => db.Read(_ =>
    {
        using var c = db.Cmd(sql);
        var o = c.ExecuteScalar();
        return o is long or int ? Convert.ToInt64(o, CultureInfo.InvariantCulture) : o;
    });

    static void Write(Database db, string sql) => db.Write(_ =>
    {
        using var c = db.Cmd(sql);
        return c.ExecuteNonQuery();
    });

    static Microsoft.Data.Sqlite.SqliteConnection Raw(string file)
    {
        var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False");
        raw.Open();
        return raw;
    }

    static void RawExec(Microsoft.Data.Sqlite.SqliteConnection raw, string sql)
    {
        using var c = raw.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    static long RawScalar(Microsoft.Data.Sqlite.SqliteConnection raw, string sql)
    {
        using var c = raw.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    // ---- (j) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (j) A PAST CHARGE STAYS WITH THE CHAIN IT WAS ADMITTED UNDER (§ 2.4, "funding lineage is immutable"). A team under
    /// Research spends, then the chart moves it under Operations: its past charge stays Research's — Research's other team
    /// is still refused for it, and Operations' launch is not.
    /// </summary>
    [Fact]
    public void A_past_charge_stays_with_the_chain_it_was_admitted_under()
    {
        Unit("team-1", OrgIds.Research);
        Unit("team-2", OrgIds.Research);
        Seat("pos-1", "team-1");
        Seat("pos-2", "team-2");
        // Each division has 2.00 of a 4.00 day: room for one reservation each.
        var meter = Meter(4m, _ => 0.5m);

        var charge = meter.Begin("## Situation", role: "pos-1");
        Assert.True(charge.Admitted);
        Exec("UPDATE org_unit SET parent_id=$p WHERE id='team-1'", ("$p", OrgIds.Operations));

        Assert.Equal(["team-1", OrgIds.Research, OrgIds.Root], Chain(charge.Id!));
        var refused = meter.Begin("## Situation", role: "pos-2");
        Assert.False(refused.Admitted);
        Assert.Equal(OrgIds.Research, refused.Ceiling);
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);
    }
}
