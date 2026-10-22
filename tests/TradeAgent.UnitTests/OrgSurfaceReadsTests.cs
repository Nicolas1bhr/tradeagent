using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE READS THE ORGANISATION-FIRST PAGES ARE BUILT FROM (<c>U-org-surface-a</c> item 1; R24 § 4.7): each across the whole chart in one query,
/// over a store the test seeded. No real model, no real seat — launches are rows the test wrote through <see cref="AiAttemptStore.Begin"/>.
/// </summary>
public class OrgSurfaceReadsTests
{
    const string Head = "pos-5ac0ffee0001", Member = "pos-5ac0ffee0002", Other = "pos-5bc0ffee0001";
    const string Team = "team-5ac0ffee", Elsewhere = "team-5bc0ffee";
    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void Seat(Database db, string id, string unit) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$at", Stamp));

    static void HeadAUnit(Database db, string position, string unit, string parent)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", unit), ("$p", parent), ("$at", Stamp));
        Seat(db, position, unit);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", unit));
    }

    /// <summary>The chart: <see cref="Head"/> heads <see cref="Team"/> under Research with <see cref="Member"/>; <see cref="Other"/> heads another team.</summary>
    static void Chart(Database db)
    {
        HeadAUnit(db, Head, Team, OrgIds.Research);
        Seat(db, Member, Team);
        HeadAUnit(db, Other, Elsewhere, OrgIds.Research);
    }

    static string Launch(Database db, string role, DateTimeOffset at, string seatClass = SeatClasses.Harness, string billing = BillingClasses.Key,
        string? mandate = null, string? unit = Team, decimal reserved = 0.10m)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var unitIds = unit is null ? [] : new[] { unit, OrgIds.Research, OrgIds.Root };
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = at, Role = role, ReservedCost = reserved, SeatClass = seatClass, BillingClass = billing, MandateId = mandate,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null, Funding = unitIds
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static void Priced(Database db, string id, decimal cost) =>
        Assert.True(new AiAttemptStore(db).End(id, 0, DateTimeOffset.UtcNow, 10, 0, 0, 10, 0, "model-x", cost, null, null));

    static void Unreported(Database db, string id) =>
        Assert.True(new AiAttemptStore(db).End(id, 0, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null));

    static void Mandate(Database db, string id, string decider, string holder, params string[] events)
    {
        Exec(db, """
            INSERT INTO org_mandate(id, unit_id, decider, holder, kind, objective, checks, latitude, deadline, allocation_ai, retry_limit, created_at)
            VALUES($id,$u,$d,$h,'research','find something','[]','exact',$by,'1.00',3,$at)
            """, ("$id", id), ("$u", Team), ("$d", decider), ("$h", holder), ("$by", "2026-12-01T00:00:00.0000000Z"), ("$at", Stamp));
        foreach (var kind in events)
            Exec(db, "INSERT INTO org_mandate_event(mandate_id, at, kind, actor, reason) VALUES($m,$at,$k,$a,$r)",
                ("$m", id), ("$at", Stamp), ("$k", kind), ("$a", decider), ("$r", kind == "proposed" ? "I found a thing" : null));
    }

    // ---- SeatAssuranceOf, RecordedSeats ---------------------------------------------------------------------------

    /// <summary>
    /// A POSITION'S RECORDED SEAT IS ITS LATEST LAUNCH'S, from the ledger: the assurance, the seat class and the billing class the app
    /// wrote — null for a position that never launched, and the whole chart in one read.
    /// RED at base: no <c>SeatAssuranceOf</c> or <c>RecordedSeats</c> existed.
    /// </summary>
    [Fact]
    public void SeatAssuranceOf_reads_the_latest_recorded_seat_and_RecordedSeats_reads_the_chart_at_once()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var t = DateTimeOffset.UtcNow.AddHours(-3);
        Launch(db, Head, t, SeatClasses.Cli, BillingClasses.Plan);
        Launch(db, Head, t.AddHours(1), SeatClasses.Harness, BillingClasses.Key);
        Launch(db, Member, t, SeatClasses.Broker, BillingClasses.Unknown);

        Assert.Equal(SeatAssurance.Brokered, org.SeatAssuranceOf(Head));
        Assert.Equal(SeatAssurance.Mediated, org.SeatAssuranceOf(Member));
        Assert.Null(org.SeatAssuranceOf(Other));

        var seats = org.RecordedSeats();
        Assert.Equal(SeatClasses.Harness, seats[Head].SeatClass);
        Assert.Equal(BillingClasses.Key, seats[Head].BillingClass);
        Assert.Equal(SeatAssurance.Brokered, seats[Head].Assurance);
        Assert.Equal(BillingClasses.Unknown, seats[Member].BillingClass);
        Assert.False(seats.ContainsKey(Other));
    }

    // ---- MandatesOpen --------------------------------------------------------------------------------------------

    /// <summary>
    /// EVERY OPEN MANDATE ON THE CHART IN ONE READ, WITH ITS STATUS AND ITS MEASURED SPEND: the closed ones are not in it, a proposal (a claim)
    /// is not a status, and a resolved launch with no cost is counted as unknown and a lost one as charged at its reservation — neither
    /// added as a zero.
    /// RED at base: no <c>MandatesOpen</c> existed.
    /// </summary>
    [Fact]
    public void MandatesOpen_reads_every_open_mandate_with_status_and_spend_and_never_adds_an_unknown_cost_as_zero()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Mandate(db, "man-000000000001", Head, Member, "issued");
        Mandate(db, "man-000000000002", Head, Member, "issued", "delivered", "proposed");
        Mandate(db, "man-000000000003", Head, Member, "issued", "delivered", "accepted");
        Mandate(db, "man-000000000004", Other, Head, "issued", "cancelled");
        Mandate(db, "man-000000000005", Other, Head, "issued", "escalated");

        var now = DateTimeOffset.UtcNow;
        Priced(db, Launch(db, Member, now.AddMinutes(-5), mandate: "man-000000000001"), 0.25m);
        Unreported(db, Launch(db, Member, now.AddMinutes(-3), mandate: "man-000000000001", reserved: 0.10m));
        // THE GATE REFUSES A LAUNCH AFTER A RESOLVED ONE WITH NO COST: the open one and the unknown-cost one are launched first.
        var unknown = Launch(db, Member, now.AddMinutes(-4), mandate: "man-000000000001", reserved: 0m);
        Launch(db, Member, now.AddMinutes(-2), mandate: "man-000000000001");
        Unreported(db, unknown);

        var open = new OrgStore(db).MandatesOpen();

        Assert.Equal(new[] { "man-000000000005", "man-000000000002", "man-000000000001" }, open.Select(o => o.Mandate.Id));
        Assert.Equal("delivered", open.Single(o => o.Mandate.Id == "man-000000000002").Status);
        Assert.Equal("escalated", open.Single(o => o.Mandate.Id == "man-000000000005").Status);
        var one = open.Single(o => o.Mandate.Id == "man-000000000001");
        Assert.Equal("issued", one.Status);
        Assert.Equal(4, one.Spend.Launches);
        Assert.Equal(0.25m + 0.10m, one.Spend.Spent);
        Assert.Equal(0.10m, one.Spend.Reserved);
        Assert.Equal(1, one.Spend.Unknown);
        Assert.Equal(1, one.ChargedAtReservation);
        // THE SAME FIGURES THE PER-MANDATE READ GIVES.
        Assert.Equal(new OrgStore(db).SpendOf("man-000000000001"), one.Spend);
    }

    // ---- DecisionsSince -------------------------------------------------------------------------------------------

    /// <summary>
    /// THE DECISIONS SINCE A TIME, ACROSS POSITIONS, NEWEST FIRST AND BOUNDED, each with its rationale (the position's claim) and the forecasts
    /// on it with the app's resolution — one snapshot.
    /// RED at base: no <c>DecisionsSince</c> existed.
    /// </summary>
    [Fact]
    public void DecisionsSince_reads_across_positions_newest_first_with_rationale_forecasts_and_resolutions()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var t = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var old = org.Decide(Head, null, "routine", ["a", "b"], "a", "an old reason", [], [], t.AddDays(-5));
        var first = org.Decide(Head, null, "routine", ["a", "b"], "b", "the first reason", [], [], t);
        var second = org.Decide(Other, null, "routine", ["x"], "x", "the second reason", [], [], t.AddHours(1));
        const string forecast = """
            INSERT INTO org_forecast(id, decision_id, forecaster, vocabulary, predicate, subject, target, resolve_by, probability, scored, unscored_why, at)
            VALUES($id, $d, $p, 1, 'verdict-class', 'v', 'promoted', $at, $prob, 1, NULL, $at)
            """;
        Exec(db, forecast, ("$id", "fc-000000000001"), ("$d", first.Id), ("$p", Head), ("$at", Stamp), ("$prob", "0.7"));
        Exec(db, forecast, ("$id", "fc-000000000002"), ("$d", first.Id), ("$p", Head), ("$at", Stamp), ("$prob", "0.2"));
        Exec(db, "INSERT INTO org_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at) VALUES('fc-000000000001','yes','version','v','it did',$at)",
            ("$at", Stamp));

        var seen = org.DecisionsSince(t, 10);

        Assert.Empty(seen.Refused);
        Assert.Equal([second.Id, first.Id], seen.Rows.Select(d => d.Decision.Id));
        Assert.DoesNotContain(seen.Rows, d => d.Decision.Id == old.Id);
        Assert.Equal("the second reason", seen.Rows[0].Rationale);
        Assert.Empty(seen.Rows[0].Forecasts);
        Assert.Equal("the first reason", seen.Rows[1].Rationale);
        Assert.Equal(["fc-000000000001", "fc-000000000002"], seen.Rows[1].Forecasts.Select(f => f.Forecast.Id));
        Assert.Equal(0.7m, seen.Rows[1].Forecasts[0].Forecast.Probability);
        Assert.Equal("yes", seen.Rows[1].Forecasts[0].Resolution?.Outcome);
        Assert.Null(seen.Rows[1].Forecasts[1].Resolution);
        // BOUNDED, NEWEST FIRST.
        Assert.Equal([second.Id], org.DecisionsSince(t.AddDays(-9), 1).Rows.Select(d => d.Decision.Id));
        Assert.Empty(org.DecisionsSince(t.AddDays(1), 10).Rows);
    }

    // ---- UnitTotalsByClass ---------------------------------------------------------------------------------------

    /// <summary>
    /// A UNIT'S DAY SPLIT BY THE CLASS THAT PAID, EXACT AND IN ONE QUERY: the three classes of a unit add up to its whole day (an unknown row is
    /// in its own class, not counted in the other two as a ceiling counts it), and a turn with no cost is counted as unpriced and adds nothing
    /// to the spent figure — never a zero price.
    /// RED at base: no <c>UnitTotalsByClass</c> existed.
    /// </summary>
    [Fact]
    public void UnitTotalsByClass_splits_each_units_day_exactly_and_names_a_turn_with_no_cost()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var attempts = new AiAttemptStore(db);
        var (from, to) = OwnerDay.Window(DateTimeOffset.UtcNow);
        var at = from.AddMinutes(30);
        Priced(db, Launch(db, Head, at, SeatClasses.Cli, BillingClasses.Plan), 0.40m);
        Priced(db, Launch(db, Head, at.AddMinutes(1), SeatClasses.Harness, BillingClasses.Key), 0.30m);
        Priced(db, Launch(db, Member, at.AddMinutes(2), SeatClasses.Harness, BillingClasses.Key), 0.20m);
        Priced(db, Launch(db, Member, at.AddMinutes(3), SeatClasses.Cli, BillingClasses.Unknown), 0.05m);
        Unreported(db, Launch(db, Member, at.AddMinutes(4), SeatClasses.Cli, BillingClasses.Plan, reserved: 0m));
        Launch(db, Other, at.AddMinutes(5), SeatClasses.Harness, BillingClasses.Key, unit: Elsewhere);
        Priced(db, Launch(db, Head, from.AddDays(-1), SeatClasses.Harness, BillingClasses.Key), 9m);   // yesterday: outside the window

        var rows = attempts.UnitTotalsByClass(from, to);

        AiAttemptTotals Of(string unit, string cls) => rows.Single(r => r.UnitId == unit && r.BillingClass == cls).Totals;
        Assert.Equal(0.40m, Of(Team, BillingClasses.Plan).Spent);
        Assert.Equal(2, Of(Team, BillingClasses.Plan).Turns);
        Assert.Equal(1, Of(Team, BillingClasses.Plan).Unpriced);
        Assert.Equal(0.50m, Of(Team, BillingClasses.Key).Spent);
        Assert.Equal(0.05m, Of(Team, BillingClasses.Unknown).Spent);
        Assert.Equal(1, Of(Elsewhere, BillingClasses.Key).Open);
        Assert.Equal(0.10m, Of(Elsewhere, BillingClasses.Key).Reserved);
        // THE THREE CLASSES ADD UP TO THE UNIT'S WHOLE DAY, as the existing unit read gives it.
        foreach (var unit in new[] { Team, OrgIds.Research, OrgIds.Root, Elsewhere })
        {
            var whole = attempts.UnitTotalsBetween(from, to, unit);
            var mine = rows.Where(r => r.UnitId == unit).Select(r => r.Totals).ToList();
            Assert.Equal(whole.Spent, mine.Sum(m => m.Spent), 6);
            Assert.Equal(whole.Reserved, mine.Sum(m => m.Reserved), 6);
            Assert.Equal(whole.Turns, mine.Sum(m => m.Turns));
            Assert.Equal(whole.Unpriced, mine.Sum(m => m.Unpriced));
            Assert.Equal(whole.Unreported, mine.Sum(m => m.Unreported));
        }
        // A UNIT WITH NO ROW IN A CLASS HAS NO ROW: the page says "none", it does not invent a zero.
        Assert.DoesNotContain(rows, r => r.UnitId == Elsewhere && r.BillingClass == BillingClasses.Plan);
    }

    // ---- EscalationsToOwner --------------------------------------------------------------------------------------

    /// <summary>
    /// THE ESCALATIONS ADDRESSED TO THE OWNER (<c>to_position IS NULL</c>), newest first — and not one addressed to a position.
    /// RED at base: no <c>EscalationsToOwner</c> existed.
    /// </summary>
    [Fact]
    public void EscalationsToOwner_reads_the_owners_records_and_no_other()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Exec(db, "INSERT INTO org_escalation(id, sender, unit_id, to_position, reason, skipped, wake_id, day, at) VALUES('esc-000000000001',$p,$u,NULL,'to the owner','[]',NULL,'2026-10-11',$at)",
            ("$p", Member), ("$u", Team), ("$at", Stamp));
        Exec(db, "INSERT INTO org_escalation(id, sender, unit_id, to_position, reason, skipped, wake_id, day, at) VALUES('esc-000000000002',$p,$u,$q,'to a head','[]','w-1','2026-10-11',$at)",
            ("$p", Member), ("$u", Team), ("$q", Head), ("$at", Stamp));
        Exec(db, "INSERT INTO org_escalation(id, sender, unit_id, to_position, reason, skipped, wake_id, day, at) VALUES('esc-000000000003',$p,$u,NULL,'to the owner, later','[]',NULL,'2026-10-12',$at)",
            ("$p", Other), ("$u", Elsewhere), ("$at", "2026-10-12T12:00:00.0000000Z"));

        var owner = new OrgStore(db).EscalationsToOwner();

        Assert.Empty(owner.Refused);
        Assert.Equal(["esc-000000000003", "esc-000000000001"], owner.Rows.Select(e => e.Id));
        Assert.All(owner.Rows, e => Assert.Null(e.Recipient));
    }

    // ---- the watcher ----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE ORGANISATION-FIRST READS NEVER NAME THE WATCHER (ORGANISATION § 9; <c>U-org-surface-a</c>): the source of the new reads, and of
    /// every page and sentence builder this unit adds, holds neither "watcher" nor "oversight" — the line is on its own page and nowhere else.
    /// Pins an absence: watched red with a planted word.
    /// </summary>
    [Fact]
    public void The_org_reads_never_name_the_watcher()
    {
        var root = RepoRoot();
        string[] files =
        [
            "src/TradeAgent.Core/Db/OrgStore.Surface.cs",
        ];
        foreach (var file in files)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{file} is missing");
            var text = File.ReadAllText(path);
            foreach (var word in new[] { "watcher", "oversight" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
        // AND THE NEW SQL IN THE LEDGER STORE reads no file of the line.
        var attempts = File.ReadAllText(Path.Combine(root, "src", "TradeAgent.Core", "Db", "AiAttemptStore.cs"));
        var byClass = attempts[attempts.IndexOf("UnitTotalsByClass(", StringComparison.Ordinal)..];
        byClass = byClass[..byClass.IndexOf("});", StringComparison.Ordinal)];
        Assert.DoesNotContain("oversight", byClass, StringComparison.OrdinalIgnoreCase);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root is not above the test binaries");
    }
}
