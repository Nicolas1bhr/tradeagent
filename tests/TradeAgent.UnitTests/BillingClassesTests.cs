using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// <c>U-billing-classes</c> — EVERY LAUNCH IS COUNTED AGAINST THE CEILING OF ITS OWN BILLING CLASS (ORCHESTRATION § 2.4, § 8 BUDGET-001;
/// ORGANISATION § 10; COUNCIL rule 4): the owner's plan capacity — a list-price equivalent, not money — or the real-money cap, decided
/// from the seat the app verified and recorded with the row; an unknown class counts against both; the envelopes nest in each.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class BillingClassesTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"billing-{Guid.NewGuid():n}.jsonl");
    readonly DateTimeOffset _at = TestEnv.LocalNoon();

    /// <summary>The owner's own two numbers per million tokens, so every reservation has one right answer.</summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    /// <summary>One turn's reservation at <see cref="Rate"/> over the shipped allowance: 1.2 M in at 1, 20 k out at 4.</summary>
    static readonly decimal Turn = TurnAllowance.Default.InputTokens * 1m / 1_000_000m + TurnAllowance.Default.OutputTokens * 4m / 1_000_000m;

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        _db.Dispose();
    }

    TurnMeter Meter(decimal money, decimal plan, Database? db = null) =>
        new(db ?? _db, () => money, now: () => _at, recordPath: _records, owner: () => Rate, share: _ => 1m,
            seat: role => SeatResolver.Of(role, "codex", "gpt-5.6-sol", TurnAllowance.Default), live: new LiveAttempts(),
            planCap: () => plan);

    List<AiAttempt> Rows() => new AiAttemptStore(_db).Between(_at.AddDays(-1), _at.AddDays(1));

    static void Exec(Database db, string sql, params (string, object?)[] args) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, args);
        c.ExecuteNonQuery();
        return 0;
    });

    // ---- (d) the sign-in names the class -------------------------------------------------------------------------------

    /// <summary>
    /// (d) THE SIGN-IN NAMES THE CLASS, AND AN UNRECOGNISED ONE IS UNKNOWN: the shipped codex seat's two patterns read the two measured
    /// texts as <c>plan</c> and <c>key</c>; anything else — a reworded status, both at once, nothing — is <c>unknown</c>. The CLI's own
    /// probe reads it off <c>login status</c> under the seat home; the harness is <c>key</c>; a seat with no seat profile <c>unknown</c>.
    /// </summary>
    [Fact]
    public async Task The_sign_in_names_the_class_and_an_unrecognised_one_is_unknown()
    {
        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        Assert.Equal(BillingClasses.Plan, codex.Seat!.BillingClassOf("Logged in using ChatGPT"));
        Assert.Equal(BillingClasses.Key, codex.Seat.BillingClassOf("Logged in using an API key - sk-FAKE-***-0000"));
        foreach (var other in new[] { "Logged in using a ChatGPT workspace token", "", "Signed in.",
                     "Logged in using ChatGPT\nLogged in using an API key - sk-FAKE" })
            Assert.Equal(BillingClasses.Unknown, codex.Seat.BillingClassOf(other));

        // THE PROBE ITSELF, over a script that answers `login status` as each text: the class comes off the status the CLI printed.
        foreach (var (said, expected) in new[]
                 {
                     ("Logged in using ChatGPT", BillingClasses.Plan),
                     ("Logged in using an API key - sk-FAKE-***-0000", BillingClasses.Key),
                     ("Logged in some new way", BillingClasses.Unknown)
                 })
        {
            var (manifest, exe, cwd) = SignInScript(said);
            var reading = await CliSeatProbe.Instance.ReadAsync(manifest, exe, cwd, CancellationToken.None);
            Assert.True(reading.SignedIn);
            Assert.Equal(expected, reading.BillingClass);
        }

        // THE VERDICT: no seat profile is unknown; the harness is an API key.
        Assert.Equal(BillingClasses.Unknown, SeatVerdict.None.BillingClass);
        Assert.Equal(BillingClasses.Key, ((ISeatLaunch)HarnessConversation()).CheckSeat().BillingClass);
    }

    /// <summary>The shipped codex manifest pointed at a script that prints <paramref name="said"/> for <c>login status</c>.</summary>
    static (RuntimeManifest Manifest, string Exe, string Cwd) SignInScript(string said)
    {
        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        var dir = Path.Combine(TestEnv.Home, "billing-seat", Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(dir);
        string exe;
        if (OperatingSystem.IsWindows())
        {
            exe = Path.Combine(dir, "codex.cmd");
            File.WriteAllText(exe, "@echo off\r\nif \"%1\"==\"login\" echo " + said + " 1>&2\r\nexit /b 0\r\n");
        }
        else
        {
            exe = Path.Combine(dir, "codex");
            File.WriteAllText(exe, "#!/bin/sh\n[ \"$1\" = \"login\" ] && echo '" + said + "' >&2\nexit 0\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        codex.Executable = exe;
        codex.Install.ExecutableSha256 = SeatProfile.Sha256Of(exe);
        codex.Seat!.Home = Path.Combine(dir, "seat-home");
        var cwd = Path.Combine(dir, "work");
        Directory.CreateDirectory(cwd);
        return (codex, exe, cwd);
    }

    static IAgentConversation HarnessConversation()
    {
        var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
        // NO VENDOR: this conversation is only asked its seat, and a request it never sends would go nowhere.
        manifest.BaseUrl = "http://127.0.0.1:9/";
        var home = Path.Combine(TestEnv.Home, $"billing-harness-{Guid.NewGuid():n}");
        return new ApiAgentRuntime(manifest, new TradeAgent.Security.HarnessKey()).OpenConversation(CouncilRoles.Research, () => home,
            () => new Dictionary<string, string>(), () => null);
    }

    // ---- (h) the class is recorded with the row ------------------------------------------------------------------------

    /// <summary>
    /// (h) THE CLASS IS RECORDED WITH THE ROW, FROM THE VERDICT ADMITTED: a seat read as the plan writes <c>plan</c>, one read as a key
    /// <c>key</c>, a seat with no seat profile <c>unknown</c> — and nothing changes it afterwards: an UPDATE is refused by trigger, a
    /// word outside the three by CHECK, and a row inserted without one is <c>unknown</c>.
    /// </summary>
    [Fact]
    public void The_class_is_recorded_with_the_row_from_the_verdict_admitted()
    {
        var meter = Meter(money: 100m, plan: 100m);
        using (meter.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
        using (meter.Attach(new Billed(BillingClasses.Key), CouncilRoles.Research))
        {
            Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);
            Assert.True(meter.Begin("## Situation", role: CouncilRoles.Research).Admitted);
        }
        var rows = Rows().ToDictionary(a => a.Role!);
        Assert.Equal(BillingClasses.Plan, rows[CouncilRoles.Operations].BillingClass);
        Assert.Equal(BillingClasses.Key, rows[CouncilRoles.Research].BillingClass);

        // A CONVERSATION THAT DECLARED NO SEAT: nobody established who pays, so unknown.
        var bare = Meter(money: 100m, plan: 100m, db: _db);
        Assert.True(bare.Begin("## Situation").Admitted);
        Assert.Equal(BillingClasses.Unknown, Rows().Single(a => a.Role is null).BillingClass);

        var id = rows[CouncilRoles.Operations].Id;
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(_db, "UPDATE ai_attempt SET billing_class='key' WHERE id=$id", ("$id", id)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(_db,
            "INSERT INTO ai_attempt(id, started_at, reserved_cost, state, billing_class) VALUES('a-word', $at, 0, 'LAUNCHED', 'credits')",
            ("$at", Sql.T(_at))));
        Exec(_db, "INSERT INTO ai_attempt(id, started_at, reserved_cost, state) VALUES('a-bare', $at, 0, 'LAUNCHED')", ("$at", Sql.T(_at)));
        Assert.Equal(BillingClasses.Unknown, new AiAttemptStore(_db).Get("a-bare")!.BillingClass);
        Exec(_db, "UPDATE ai_attempt SET exit_code=0 WHERE id=$id", ("$id", id));
        Assert.Equal(BillingClasses.Plan, new AiAttemptStore(_db).Get(id)!.BillingClass);
    }

    // ---- (a) (b) (c) admission per class ----------------------------------------------------------------------------------

    /// <summary>A row of <paramref name="billing"/> still open at <paramref name="reserved"/>: committed money of that class today.</summary>
    void Spent(string billing, decimal reserved, string? role = null, Database? db = null) =>
        new AiAttemptStore(db ?? _db).Begin(new AiAttempt
        {
            Id = $"spent-{Guid.NewGuid():n}", StartedAt = _at, ReservedCost = reserved, BillingClass = billing, Role = role
        });

    /// <summary>One launch of <paramref name="role"/> through the meter, its open entry released so the role may launch again.</summary>
    static AiAdmission Launch(TurnMeter meter, string role)
    {
        var admission = meter.Begin("## Situation", role: role);
        if (admission.Id is { } id) meter.Release(role, id);
        return admission;
    }

    /// <summary>
    /// (a) A PLAN TURN COUNTS AGAINST THE PLAN CEILING AND NEVER THE MONEY CAP. A money cap of nothing — no real money at all today —
    /// and a day of key spending far past it do not stop a seat signed in with the plan; its own ceiling does, at the turn that would
    /// pass it, in the plan's own words. The rows it wrote are <c>plan</c>.
    /// </summary>
    [Fact]
    public void A_plan_turn_counts_against_the_plan_ceiling_and_never_the_money_cap()
    {
        Spent(BillingClasses.Key, 50m);
        var meter = Meter(money: 0m, plan: 2 * Turn + 0.5m);
        using var seat = meter.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations);

        Assert.True(Launch(meter, CouncilRoles.Operations).Admitted);
        Assert.True(Launch(meter, CouncilRoles.Operations).Admitted);
        var refused = Launch(meter, CouncilRoles.Operations);
        Assert.False(refused.Admitted);
        Assert.Equal(AiAdmission.PlanDay, refused.Ceiling);
        Assert.Equal(Labels.DailyPlanCapacityReached, refused.Refusal);
        Assert.Equal(Turn - 0.5m, refused.Over);
        Assert.Equal(2, Rows().Count(a => a.BillingClass == BillingClasses.Plan));
    }

    /// <summary>
    /// (b) A KEY TURN COUNTS AGAINST THE MONEY CAP AND NEVER THE PLAN CEILING — the harness: a plan ceiling of nothing and a day of plan
    /// turns far past it do not stop it; the money cap does, in its own words. (Perception's call, key by construction, is held to the
    /// money cap in <c>DecisionPortTests</c>, whose row reads <c>key</c>.)
    /// </summary>
    [Fact]
    public void A_key_turn_counts_against_the_money_cap_and_never_the_plan_ceiling()
    {
        Spent(BillingClasses.Plan, 50m);
        var meter = Meter(money: 2 * Turn + 0.5m, plan: 0m);
        using var seat = meter.Attach(new Billed(BillingClasses.Key), CouncilRoles.Research);

        Assert.True(Launch(meter, CouncilRoles.Research).Admitted);
        Assert.True(Launch(meter, CouncilRoles.Research).Admitted);
        var refused = Launch(meter, CouncilRoles.Research);
        Assert.False(refused.Admitted);
        Assert.Equal(AiAdmission.Day, refused.Ceiling);
        Assert.Equal(Labels.DailySpendingLimitReached, refused.Refusal);
        Assert.Equal(2, Rows().Count(a => a.BillingClass == BillingClasses.Key));
    }

    /// <summary>
    /// (c) AN UNKNOWN CLASS COUNTS AGAINST BOTH CEILINGS. Its launch must fit both — refused by the plan's when only the money has room,
    /// by the money's when only the plan has — and once admitted its row is in both: the next plan turn and the next key turn each see it.
    /// A conversation that declared no seat is unknown the same way.
    /// </summary>
    [Fact]
    public void An_unknown_class_counts_against_both_ceilings()
    {
        var onlyMoney = Meter(money: 100m, plan: 0m);
        using (onlyMoney.Attach(new Billed(BillingClasses.Unknown), CouncilRoles.Operations))
            Assert.Equal(AiAdmission.PlanDay, Launch(onlyMoney, CouncilRoles.Operations).Ceiling);
        var onlyPlan = Meter(money: 0m, plan: 100m);
        using (onlyPlan.Attach(new Billed(BillingClasses.Unknown), CouncilRoles.Operations))
            Assert.Equal(AiAdmission.Day, Launch(onlyPlan, CouncilRoles.Operations).Ceiling);
        // NO SEAT DECLARED: unknown, and so refused by the plan ceiling it has no room in.
        Assert.Equal(AiAdmission.PlanDay, Launch(Meter(money: 100m, plan: 0m), CouncilRoles.Research).Ceiling);
        Assert.Empty(Rows());

        var both = Meter(money: 2 * Turn - 0.5m, plan: 2 * Turn - 0.5m);
        using (both.Attach(new Billed(BillingClasses.Unknown), CouncilRoles.Operations))
            Assert.True(Launch(both, CouncilRoles.Operations).Admitted);
        Assert.Equal(BillingClasses.Unknown, Assert.Single(Rows()).BillingClass);
        using (both.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Research))
            Assert.Equal(AiAdmission.PlanDay, Launch(both, CouncilRoles.Research).Ceiling);
        using (both.Attach(new Billed(BillingClasses.Key), CouncilRoles.Research))
            Assert.Equal(AiAdmission.Day, Launch(both, CouncilRoles.Research).Ceiling);
    }

    /// <summary>
    /// (a)(b) AFTER AN APP START A ROLE IS HELD TO ITS OWN CLASS'S CEILING FROM THE FIRST LOOK (fix pass 1). The loop picks a role
    /// (<c>affordable</c>) and waits for midnight (<c>CappedUntilMidnight</c>) on its <see cref="TurnMeter.TodayFor"/> before any launch
    /// is begun: read from process memory, a restarted app read every role as <c>unknown</c>, so with no real money left a plan seat was
    /// never picked — and never admitted, so never learned. Read from the ledger, a fresh meter over the same home holds the plan seat to
    /// the plan's capacity and the key seat to the money cap, each blind to the other's ceiling; a role declared on the harness is key with
    /// no row at all; a role with no row is still <c>unknown</c>, both ceilings.
    /// </summary>
    [Fact]
    public void After_an_app_start_a_role_is_held_to_its_own_classes_ceiling_from_the_first_look()
    {
        // THE EARLIER PROCESS: one plan launch, then one key launch.
        var earlier = Meter(money: 100m, plan: 100m);
        using (earlier.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
            Assert.True(Launch(earlier, CouncilRoles.Operations).Admitted);
        using (earlier.Attach(new Billed(BillingClasses.Key), CouncilRoles.Research))
            Assert.True(Launch(earlier, CouncilRoles.Research).Admitted);

        // THE APP STARTS AGAIN with no real money today and the plan's 40: no admission in this process yet.
        var noMoney = Meter(money: 0m, plan: 40m);
        using (noMoney.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
        {
            var first = noMoney.TodayFor(CouncilRoles.Operations);
            Assert.True(first.AdmitsAnotherTurn);
            Assert.Equal(BillingClasses.Plan, first.Class);
            Assert.True(Launch(noMoney, CouncilRoles.Operations).Admitted);
        }
        Assert.False(noMoney.TodayFor(CouncilRoles.Research).AdmitsAnotherTurn);

        // THE MIRROR: no plan capacity, real money left — the key seat is picked, the plan seat is not.
        var noPlan = Meter(money: 40m, plan: 0m);
        Assert.Equal(BillingClasses.Key, noPlan.TodayFor(CouncilRoles.Research).Class);
        Assert.True(noPlan.TodayFor(CouncilRoles.Research).AdmitsAnotherTurn);
        Assert.False(noPlan.TodayFor(CouncilRoles.Operations).AdmitsAnotherTurn);

        // NO ROW: a home where nobody has launched reads both; a role declared on the harness is key by code, with no row at all —
        // and a row of the seat a role left does not answer for the seat it is on now.
        using var fresh = TestEnv.NewDb();
        Assert.Equal(BillingClasses.Unknown, Meter(money: 40m, plan: 40m, db: fresh).ClassOf(CouncilRoles.Research));
        var harness = new TurnMeter(fresh, () => 40m, now: () => _at, recordPath: _records, owner: () => Rate, share: _ => 1m,
            seat: role => SeatResolver.Of(role, "codex", "gpt-5.6-sol", TurnAllowance.Default), live: new LiveAttempts(),
            declaredSeat: _ => SeatClasses.Harness, planCap: () => 0m);
        Assert.Equal(BillingClasses.Key, harness.ClassOf(CouncilRoles.Research));
        Assert.True(harness.TodayFor(CouncilRoles.Research).AdmitsAnotherTurn);
        var broker = new TurnMeter(_db, () => 40m, now: () => _at, recordPath: _records, owner: () => Rate, share: _ => 1m,
            seat: role => SeatResolver.Of(role, "codex", "gpt-5.6-sol", TurnAllowance.Default), live: new LiveAttempts(),
            declaredSeat: _ => SeatClasses.Broker, planCap: () => 40m);
        Assert.Equal(BillingClasses.Unknown, broker.ClassOf(CouncilRoles.Operations));
    }

    // ---- (e) envelopes nest in each class --------------------------------------------------------------------------------

    void Exec(string sql, params (string, object?)[] ps) => Exec(_db, sql, ps);

    /// <summary>
    /// (e) THE ENVELOPES NEST IN EACH CLASS, AND TWO SIBLINGS CANNOT BOTH TAKE A CLASS'S LAST ROOM. A subdivision of Research with 0.2 of
    /// each ceiling (2.00 of 10.00 plan, 2.00 of 10.00 money) — room for one turn of each class. Two sibling positions' plan rules, built
    /// on an empty ledger before either is admitted: the first is admitted and the second refused by the subdivision, in the plan's
    /// words; the second sibling's KEY launch is then admitted, because a plan row is in no money envelope — and the subdivision's money
    /// room is then gone too. An unknown launch sees both envelopes.
    /// </summary>
    [Fact]
    public void Envelopes_nest_in_each_class_and_two_siblings_cannot_both_take_a_classes_last_room()
    {
        Exec("""
            INSERT INTO org_unit(id, parent_id, kind, status, envelope_share, created_at)
            VALUES('sub-b', $research, 'subdivision', 'active', '0.2', '2026-10-11T12:00:00.0000000Z'),
                  ('team-b1', 'sub-b', 'team', 'active', NULL, '2026-10-11T12:00:00.0000000Z'),
                  ('team-b2', 'sub-b', 'team', 'active', NULL, '2026-10-11T12:00:00.0000000Z')
            """, ("$research", OrgIds.Research));
        Exec("""
            INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
            VALUES('pos-b1', 'team-b1', 'org/pos-b1', 'active', '2026-10-11T12:00:00.0000000Z'),
                  ('pos-b2', 'team-b2', 'org/pos-b2', 'active', '2026-10-11T12:00:00.0000000Z')
            """);
        var meter = Meter(money: 10m, plan: 10m);
        var store = new AiAttemptStore(_db);

        var first = meter.AdmissionRuleFor("pos-b1", BillingClasses.Plan);
        var second = meter.AdmissionRuleFor("pos-b2", BillingClasses.Plan);
        Assert.Equal(2m, first.PlanFunding!.Single(u => u.UnitId == "sub-b").Envelope);
        Assert.Equal(first.Funding!.Select(u => u.UnitId), first.PlanFunding!.Select(u => u.UnitId));
        Assert.True(store.Begin(Attempt("b1-plan", "pos-b1", first), admit: first).Admitted);
        var refused = store.Begin(Attempt("b2-plan", "pos-b2", second), admit: second);
        Assert.False(refused.Admitted);
        Assert.Equal("sub-b", refused.Ceiling);
        Assert.Contains("plan-capacity", refused.Refusal, StringComparison.Ordinal);
        Assert.Null(store.Get("b2-plan"));

        var key = meter.AdmissionRuleFor("pos-b2", BillingClasses.Key);
        Assert.True(store.Begin(Attempt("b2-key", "pos-b2", key), admit: key).Admitted);
        var unknown = meter.AdmissionRuleFor("pos-b1", BillingClasses.Unknown);
        var neither = store.Begin(Attempt("b1-unknown", "pos-b1", unknown), admit: unknown);
        Assert.Equal("sub-b", neither.Ceiling);
        Assert.DoesNotContain("plan-capacity", neither.Refusal, StringComparison.Ordinal);   // the money envelope refused first
    }

    AiAttempt Attempt(string id, string role, AiAdmissionRule rule) => new()
    {
        Id = id, StartedAt = _at, ReservedCost = rule.Reservation, Role = role, BillingClass = rule.BillingClass,
        Funding = [.. rule.Funding!.Select(u => u.UnitId)]
    };

    // ---- (g) the readings name their ceiling ------------------------------------------------------------------------------

    /// <summary>The words that name the plan ceiling to the AI, and the money one.</summary>
    const string PlanWords = "daily plan-capacity limit — your owner's ChatGPT plan counted at list price, not money";
    const string MoneyWords = "daily limit of real money";

    /// <summary>
    /// (g) THE SITUATION NAMES ITS SEAT'S CEILING, AND THE SAFETY PAGE SHOWS BOTH. A role before its first admitted launch is told both
    /// ceilings — the gate would ask both of a seat nobody has read; after a plan launch only the plan's, never as money; after a key
    /// launch only the money's. The card shows both figures, each in its own words. (The Safety page half: <c>SafetyPage</c> below.)
    /// </summary>
    [Fact]
    public void The_situation_names_its_seats_ceiling_and_the_safety_page_shows_both()
    {
        var meter = Meter(money: 10m, plan: 40m);
        var before = MissionSituation.SpendLine(meter.TodayFor(CouncilRoles.Operations))!;
        Assert.Contains(PlanWords, before, StringComparison.Ordinal);
        Assert.Contains(MoneyWords, before, StringComparison.Ordinal);

        using (meter.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
            Assert.True(Launch(meter, CouncilRoles.Operations).Admitted);
        var plan = MissionSituation.SpendLine(meter.TodayFor(CouncilRoles.Operations))!;
        Assert.Contains($"of a {Labels.Money(40m, "USD")} {PlanWords}", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(MoneyWords, plan, StringComparison.Ordinal);

        using (meter.Attach(new Billed(BillingClasses.Key), CouncilRoles.Research))
            Assert.True(Launch(meter, CouncilRoles.Research).Admitted);
        var key = MissionSituation.SpendLine(meter.TodayFor(CouncilRoles.Research))!;
        Assert.Contains($"of a {Labels.Money(10m, "USD")} {MoneyWords}", key, StringComparison.Ordinal);
        Assert.DoesNotContain("plan-capacity", key, StringComparison.Ordinal);

        // THE CARD: both figures, the plan's never called a cost — each turn still open, so committed rather than spent.
        var card = TradeAgent.App.DashboardPage.MissionCost(meter.Today);
        var committed = $"including {Labels.Money(Turn, "USD")} committed";
        Assert.Contains($"Cost today: {Labels.Money(0m, "USD")} of {Labels.Money(10m, "USD")} daily limit of real money", card,
            StringComparison.Ordinal);
        Assert.Contains($"Plan capacity used today: {Labels.Money(0m, "USD")} of {Labels.Money(40m, "USD")} daily plan-capacity limit — "
                        + "your ChatGPT plan counted at list price, not money", card, StringComparison.Ordinal);
        Assert.Equal(2, card.Split(committed).Length - 1);
    }

    // ---- (f) the setting ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (f) THE SETTING: the plan's capacity ships at the owner's 40 a day, beside the money cap's 5; the app's own meter holds plan turns to
    /// it and key turns to the money cap — a money cap of nothing leaves the plan's room whole, and the reverse — and a settings row
    /// nobody could read allows no plan turn at all.
    /// </summary>
    [Fact]
    public void The_app_holds_plan_turns_to_the_plan_setting_and_an_unreadable_row_to_nothing()
    {
        Assert.Equal((5m, 40m), (new TradeAgentSettings().AiDailyCostCap, new TradeAgentSettings().AiPlanDailyCapacity));
        Assert.Equal((0m, 0m), (TradeAgentSettings.Unreadable().AiDailyCostCap, TradeAgentSettings.Unreadable().AiPlanDailyCapacity));

        using var db = TestEnv.NewDb();
        var host = TradeAgent.App.AppHost.Composed(db, new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker()),
            new AgentPresence());
        host.Gateway.Update(s => { s.AiDailyCostCap = 0m; s.AiPlanDailyCapacity = 40m; });
        var meter = host.Meter!;
        Assert.Equal(40m, meter.TodayIn(BillingClasses.Plan).Cap);
        Assert.Equal(0m, meter.TodayIn(BillingClasses.Key).Cap);
        Assert.Equal(40m, meter.Today.PlanReading!.Cap);
        host.Gateway.Update(s => { s.AiDailyCostCap = 10m; s.AiPlanDailyCapacity = 0m; });
        Assert.Equal((0m, 10m), (meter.TodayIn(BillingClasses.Plan).Cap, meter.TodayIn(BillingClasses.Key).Cap));
    }

    /// <summary>
    /// (f) RAISING THE PLAN CEILING ASKS TWICE AND LOWERING SAVES IN ONE — the Safety page's own press over its own box: a raise names
    /// the figure as the plan's, at list price; an edit disarms a raise armed for another figure; a lower or unchanged figure saves at
    /// once. And the money box beside it keeps its own press.
    /// </summary>
    [Fact]
    public void Raising_the_plan_ceiling_asks_twice_and_lowering_saves_in_one()
    {
        var saved = new List<decimal>();
        var current = 40m;
        var box = TradeAgent.App.Ui.NumberField(current, 0m, 5m);
        var b = TradeAgent.App.SafetyPage.BuildSavePlanCapacity(box, () => current, () => "USD", () => saved.Add(box.Value ?? current));

        box.Value = 60m;
        Press(b);
        Assert.Empty(saved);
        Assert.Equal(Labels.RaisePlanCapacityArmed("60 USD"), b.Content);
        Assert.Contains("ChatGPT plan, counted at list price", (string)b.Content!, StringComparison.Ordinal);

        box.Value = 100m;                       // an edit disarms the raise that named 60
        Press(b);
        Assert.Empty(saved);
        Assert.Equal(Labels.RaisePlanCapacityArmed("100 USD"), b.Content);
        Press(b);
        Assert.Equal([100m], saved);

        current = 100m;
        box.Value = 10m;                        // lowering: one press
        Press(b);
        Assert.Equal([100m, 10m], saved);
        Assert.Equal(Labels.SavePlanCapacity, b.Content);
        current = 10m;
        Press(b);                               // unchanged: one press
        Assert.Equal([100m, 10m, 10m], saved);
    }

    static void Press(Avalonia.Controls.Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    /// <summary>
    /// (g), THE SAFETY PAGE'S HALF: today against each ceiling — the money in real money, the plan's at list price and named "not money" —
    /// the three sentences the plan's figure cannot see, and every seat's class in words.
    /// </summary>
    [Fact]
    public void The_safety_page_shows_both_ceilings_and_every_seats_class()
    {
        var meter = Meter(money: 10m, plan: 40m);
        using (meter.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
            Assert.True(Launch(meter, CouncilRoles.Operations).Admitted);
        var (money, plan) = TradeAgent.App.SafetyPage.TodayLines(meter.Today);
        Assert.Equal($"Today: {Labels.Money(0m, "USD")} of {Labels.Money(10m, "USD")} in real money.", money);
        Assert.Equal($"Today: {Labels.Money(Turn, "USD")} of {Labels.Money(40m, "USD")} of plan capacity, counted at list price — not money.",
            plan);

        var seats = TradeAgent.App.SafetyPage.SeatClassesLine(
            CouncilRoles.All.Select(r => (CouncilRoles.Title(r), meter.ClassOf(r))));
        Assert.Contains($"{CouncilRoles.Title(CouncilRoles.Operations)} — your ChatGPT plan's capacity", seats, StringComparison.Ordinal);
        Assert.Contains($"{CouncilRoles.Title(CouncilRoles.Research)} — not read yet, so its turns count against both limits", seats,
            StringComparison.Ordinal);
        Assert.Contains("perception — real money", seats, StringComparison.Ordinal);

        Assert.Contains("may stop the AI's turns sooner", Labels.PlanVendorLimit, StringComparison.Ordinal);
        Assert.Contains("does not count it here", Labels.PlanSharedWithYou, StringComparison.Ordinal);
        Assert.Contains("real money that TradeAgent cannot see", Labels.PlanBoughtCredits, StringComparison.Ordinal);
    }

    /// <summary>
    /// (g) EACH SAFETY-PAGE LINE SAYS "REACHED" ONLY FOR ITS OWN CEILING (fix pass 1): the plan's capacity used up to its 40 leaves the real
    /// money line untouched — nothing of the 10 is spent and it is not reached — and the mirror, the money cap used up, leaves the plan's.
    /// </summary>
    [Fact]
    public void Each_safety_page_line_says_reached_only_for_its_own_ceiling()
    {
        Spent(BillingClasses.Plan, 40m);
        var (money, plan) = TradeAgent.App.SafetyPage.TodayLines(Meter(money: 10m, plan: 40m).Today);
        Assert.Equal($"Today: {Labels.Money(0m, "USD")} of {Labels.Money(10m, "USD")} in real money.", money);
        Assert.Equal($"Today: {Labels.Money(40m, "USD")} of {Labels.Money(40m, "USD")} of plan capacity, counted at list price — not money"
                     + " — reached; the next turn of this kind waits for midnight.", plan);

        using var other = TestEnv.NewDb();
        Spent(BillingClasses.Key, 10m, db: other);
        (money, plan) = TradeAgent.App.SafetyPage.TodayLines(Meter(money: 10m, plan: 40m, db: other).Today);
        Assert.EndsWith(" in real money — reached; the next turn of this kind waits for midnight.", money, StringComparison.Ordinal);
        Assert.Equal($"Today: {Labels.Money(0m, "USD")} of {Labels.Money(40m, "USD")} of plan capacity, counted at list price — not money.", plan);
    }

    // ---- GUARD: AUTH-001 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// GUARD — NO PIPE OP, DOOR VERB OR <c>trade</c> VERB REACHES EITHER CEILING (AUTH-001; CLAUDE.md, operator authority in-process only):
    /// no op or verb is named for a ceiling, a capacity or a billing class and none takes an argument that would address one; no source of
    /// the gateway — where every pipe op and door verb is served — nor the app's door names either setting; and the class column is
    /// written by one statement, the ledger's own insert.
    /// </summary>
    [Fact]
    public void No_pipe_op_door_verb_or_trade_verb_reaches_either_ceiling()
    {
        string[] words = ["billing", "capacity", "ceiling", "costcap", "cost-cap", "dailycap", "daily-cap", "plancap", "plan-cap", "spend-limit"];
        foreach (var op in TradeAgent.Gateway.GatewaySchema.Ops())
            foreach (var word in words)
            {
                Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, op.Cli, StringComparison.OrdinalIgnoreCase);
                foreach (var arg in op.Args) Assert.DoesNotContain(word, arg.Name, StringComparison.OrdinalIgnoreCase);
            }

        var root = RepoRoot();
        string[] settings = [nameof(TradeAgentSettings.AiDailyCostCap), nameof(TradeAgentSettings.AiPlanDailyCapacity)];
        var reaching = Directory.GetFiles(Path.Combine(root, "src", "TradeAgent.Gateway"), "*.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "src", "TradeAgent.AgentRuntime", "BrokerDoor.cs"))
            .Where(f => settings.Any(name => File.ReadAllText(f).Contains(name, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(reaching.Count == 0, "an agent-facing source names a ceiling: " + string.Join(", ", reaching));

        // THE COLUMN IN CODE (comments aside): the schema's rung and the ledger's own store, and nothing else names it.
        var code = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadLines(f).Any(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                                   && l.Contains("billing_class", StringComparison.Ordinal)))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["AiAttemptStore.cs", "Database.cs"], code);
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    /// <summary>The rung's number, spelled once here as in the schema.</summary>
    const int Rung = 46;

    /// <summary>
    /// THE RUNG REOPENS AFTER A CRASH BETWEEN ITS STATEMENTS AND ITS STAMP: with the trigger not yet made, and with everything made but the
    /// stamp, the next open runs the rung again to the same schema — the trigger present, a row's class as it was, an update refused.
    /// A row from before the rung reads <c>unknown</c>.
    /// </summary>
    [Fact]
    public void The_billing_rung_arrives_at_its_number_and_a_crash_before_its_stamp_runs_it_again()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= Rung, $"the column arrives at rung {Rung}; this build says {Versions.DatabaseSchemaVersion}");
        var file = Path.Combine(TestEnv.Home, $"billing-rung-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
            new AiAttemptStore(db).Begin(new AiAttempt { Id = "a-rung", StartedAt = _at, ReservedCost = 1m, BillingClass = BillingClasses.Plan });

        foreach (var dropped in new[] { true, false })
        {
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
            {
                raw.Open();
                using var c = raw.CreateCommand();
                c.CommandText = (dropped ? "DROP TRIGGER ai_attempt_billing_fixed; " : "")
                                + $"UPDATE meta SET value='{Rung - 1}' WHERE key='schema_version';";
                c.ExecuteNonQuery();
            }
            using var rerun = new Database(file);
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
                rerun.Read(_ => { using var c = rerun.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
            Assert.Equal(1L, rerun.Read(_ =>
            {
                using var c = rerun.Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='ai_attempt_billing_fixed'");
                return (long)c.ExecuteScalar()!;
            }));
            Assert.Equal(BillingClasses.Plan, new AiAttemptStore(rerun).Get("a-rung")!.BillingClass);
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(rerun, "UPDATE ai_attempt SET billing_class='key' WHERE id='a-rung'"));
        }
    }

    /// <summary>A conversation whose seat the app read as billed to <paramref name="billing"/>; it never runs a turn here.</summary>
    internal sealed class Billed(string billing, string seatClass = SeatClasses.Cli) : IAgentConversation, ISeatLaunch
    {
        public SeatVerdict CheckSeat() => SeatVerdict.None with { SeatClass = seatClass, BillingClass = billing };

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];
        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<string>? Delta { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<AgentTurnEnded>? TurnEnded { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }
}
