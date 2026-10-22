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
    void Spent(string billing, decimal reserved, string? role = null) =>
        new AiAttemptStore(_db).Begin(new AiAttempt
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
