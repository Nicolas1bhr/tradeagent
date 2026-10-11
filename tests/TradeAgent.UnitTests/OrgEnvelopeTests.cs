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
}
