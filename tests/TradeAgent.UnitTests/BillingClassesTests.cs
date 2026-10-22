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

    TurnMeter Meter(decimal money, decimal plan, Database? db = null, Func<string?>? signIn = null) =>
        new(db ?? _db, () => money, now: () => _at, recordPath: _records, owner: () => Rate, share: _ => 1m,
            seat: role => SeatResolver.Of(role, "codex", "gpt-5.6-sol", TurnAllowance.Default), live: new LiveAttempts(),
            planCap: () => plan, signIn: signIn);

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

    // ---- U-billing-followups item 1: the class is re-read before exec ---------------------------------------------------------

    /// <summary>
    /// <c>U-billing-followups</c> item 1 (the P1 of U-billing-classes' Codex second pass): A SEAT WHOSE SIGN-IN TURNS TO A KEY AFTER ADMISSION
    /// STARTS NO PROCESS. The class is fixed on the row at admission, and the start re-verified only the profile, which by design leaves the
    /// sign-in out — so a seat read as the ChatGPT plan when the row went in, and signed in with a pasted key before its start, ran on the key
    /// under a plan reservation, outside the real-money cap. With the money cap at 0 the start now re-reads the class from the same reading
    /// the admission used and refuses in words naming both classes; the row closes, no process starts.
    /// </summary>
    [Fact]
    public async Task A_seat_whose_signin_turns_to_a_key_after_admission_starts_no_process() =>
        await SignInTurnsAfterAdmission(nameof(A_seat_whose_signin_turns_to_a_key_after_admission_starts_no_process),
            admittedAs: BillingClasses.Plan, startsAs: BillingClasses.Key, money: 0m, plan: 100m);

    /// <summary>Item 1's twin: KEY AT ADMISSION, THE PLAN BEFORE EXEC — a turn reserved against the money cap never runs on a plan whose
    /// capacity is spent.</summary>
    [Fact]
    public async Task A_seat_whose_signin_turns_to_the_plan_after_admission_starts_no_process() =>
        await SignInTurnsAfterAdmission(nameof(A_seat_whose_signin_turns_to_the_plan_after_admission_starts_no_process),
            admittedAs: BillingClasses.Key, startsAs: BillingClasses.Plan, money: 100m, plan: 0m);

    async Task SignInTurnsAfterAdmission(string name, string admittedAs, string startsAs, decimal money, decimal plan)
    {
        var (codex, exe, dump) = SeatHomeTests.Seat(name);
        var workspace = Path.GetDirectoryName(exe)!;
        // THE SIGN-IN, AS THE APP READS IT: the admitted class until the row is in, the other one from then on — between admission and exec.
        var probe = new SignInProbe(() => Rows().Count == 0 ? admittedAs : startsAs);
        var session = SeatHomeTests.Session(codex, exe, workspace, probe);
        var meter = Meter(money, plan);
        using var metering = meter.Attach(session, CouncilRoles.Operations);

        string? said = null;
        void On(AgentTurnEnded e) => said = e.Raw;
        session.TurnEnded += On;
        try { await session.SendAsync("what are you doing?"); }
        finally { session.TurnEnded -= On; }

        var row = Assert.Single(Rows());
        Assert.Equal(admittedAs, row.BillingClass);
        Assert.True(probe.Reads >= 2, $"the start did not read the sign-in again ({probe.Reads} reads)");
        Assert.DoesNotContain(SeatHomeTests.Argv(dump), line => line.StartsWith("exec", StringComparison.Ordinal));
        Assert.NotNull(said);
        Assert.Contains(BillingClasses.Words(admittedAs), said, StringComparison.Ordinal);
        Assert.Contains(BillingClasses.Words(startsAs), said, StringComparison.Ordinal);
        Assert.NotEqual(AiAttemptState.LAUNCHED, row.State);
    }

    /// <summary>The CLI's own report with a sign-in whose class <paramref name="billing"/> answers at each read.</summary>
    sealed class SignInProbe(Func<string> billing) : ISeatProbe
    {
        public int Reads;

        public Task<SeatReading> ReadAsync(RuntimeManifest manifest, string exe, string workingDirectory, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(new SeatReading(SeatHomeTests.FakeProbe.Allowed + "plugins   stable   false\n", "[]", true, billing()));
        }
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

    /// <summary>
    /// (a)(b) THE FIRST LOOK READS THE SIGN-IN THE SEAT HOLDS NOW (fix pass 2, the re-review's defect): the loop picks a role
    /// (<c>affordable</c>) and waits for midnight (<c>CappedUntilMidnight</c>) on <see cref="TurnMeter.TodayFor"/> before any launch is begun,
    /// so a class only an admission could teach was never taught to a role that look held back. (a) A fresh home — no row — with no real
    /// money and the plan's 40: the app's reading of a ChatGPT sign-in picks the plan role, and the launch is admitted. (b) A plan row, then
    /// the owner signs the seat in with a pasted key, with real money left and no plan capacity: the key seat is picked, not held to the
    /// plan's 0 by its old row — and the mirror. Each time the first look and the admission give one answer. An unmatched sign-in is
    /// <c>unknown</c>, both ceilings, as the gate holds it; no reading yet leaves the row's class, which the first reading replaces.
    /// </summary>
    [Fact]
    public void The_first_look_reads_the_sign_in_the_seat_holds_now_not_an_admission_it_never_got()
    {
        // (a) A FRESH HOME: no row, money 0, plan 40, the seat signed in with ChatGPT.
        using var fresh = TestEnv.NewDb();
        var home = Meter(money: 0m, plan: 40m, db: fresh, signIn: () => BillingClasses.Plan);
        using (home.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
        {
            var look = home.TodayFor(CouncilRoles.Operations);
            Assert.Equal(BillingClasses.Plan, look.Class);
            Assert.True(look.AdmitsAnotherTurn);
            Assert.True(Launch(home, CouncilRoles.Operations).Admitted);
        }

        // (b) ONE PLAN LAUNCH ADMITTED, then the owner signs the seat in with a pasted key: money 40, plan 0.
        var before = Meter(money: 100m, plan: 100m, signIn: () => BillingClasses.Plan);
        using (before.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
            Assert.True(Launch(before, CouncilRoles.Operations).Admitted);
        var signIn = BillingClasses.Key;
        var after = Meter(money: 40m, plan: 0m, signIn: () => signIn);
        using (after.Attach(new Billed(BillingClasses.Key), CouncilRoles.Operations))
        {
            var look = after.TodayFor(CouncilRoles.Operations);
            Assert.Equal(BillingClasses.Key, look.Class);
            Assert.True(look.AdmitsAnotherTurn);
            Assert.True(Launch(after, CouncilRoles.Operations).Admitted);
        }

        // THE MIRROR: the last row is now key; signed in with ChatGPT again, no real money left and the plan's 40 — picked and admitted.
        var back = Meter(money: 0m, plan: 40m, signIn: () => BillingClasses.Plan);
        using (back.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
        {
            Assert.True(back.TodayFor(CouncilRoles.Operations).AdmitsAnotherTurn);
            Assert.True(Launch(back, CouncilRoles.Operations).Admitted);
        }

        // AND THE LOOK HOLDS WHAT THE GATE HOLDS: the key sign-in with no real money is held back, and refused if launched anyway.
        signIn = BillingClasses.Key;
        var spent = Meter(money: 0m, plan: 40m, signIn: () => signIn);
        using (spent.Attach(new Billed(BillingClasses.Key), CouncilRoles.Operations))
        {
            Assert.False(spent.TodayFor(CouncilRoles.Operations).AdmitsAnotherTurn);
            Assert.False(Launch(spent, CouncilRoles.Operations).Admitted);
        }

        // A SIGN-IN NEITHER PATTERN NAMES is unknown — both ceilings, as the gate counts it; the harness stays key by code.
        var unmatched = Meter(money: 0m, plan: 40m, signIn: () => BillingClasses.Unknown);
        Assert.Equal(BillingClasses.Unknown, unmatched.ClassOf(CouncilRoles.Operations));
        Assert.False(unmatched.TodayFor(CouncilRoles.Operations).AdmitsAnotherTurn);
        var harness = new TurnMeter(fresh, () => 40m, now: () => _at, recordPath: _records, owner: () => Rate, share: _ => 1m,
            seat: role => SeatResolver.Of(role, "codex", "gpt-5.6-sol", TurnAllowance.Default), live: new LiveAttempts(),
            declaredSeat: _ => SeatClasses.Harness, planCap: () => 0m, signIn: () => BillingClasses.Plan);
        Assert.Equal(BillingClasses.Key, harness.ClassOf(CouncilRoles.Research));

        // NO READING YET (signed out, or the app's first seconds): the row's class, as fix pass 1 reads it; the reading replaces it.
        string? nothing = null;
        var early = Meter(money: 40m, plan: 0m, signIn: () => nothing);
        Assert.Equal(BillingClasses.Plan, early.ClassOf(CouncilRoles.Operations));
        Assert.False(early.TodayFor(CouncilRoles.Operations).AdmitsAnotherTurn);
        nothing = BillingClasses.Key;
        Assert.Equal(BillingClasses.Key, early.ClassOf(CouncilRoles.Operations));
        Assert.True(early.TodayFor(CouncilRoles.Operations).AdmitsAnotherTurn);
    }

    /// <summary>
    /// THE READING ITSELF (fix pass 2): what the meter is handed is the app's own reading of the seat home's sign-in — signed in, its
    /// class; signed out, unknown or not applicable, none — never a class guessed for a sign-in nobody read.
    /// </summary>
    [Fact]
    public void Only_a_signed_in_reading_settles_a_class()
    {
        Assert.Equal(BillingClasses.Plan, new SeatSignInReading(SeatSignInState.SignedIn, null, BillingClasses.Plan).SignedInClass);
        Assert.Equal(BillingClasses.Key, new SeatSignInReading(SeatSignInState.SignedIn, null, BillingClasses.Key).SignedInClass);
        Assert.Equal(BillingClasses.Unknown, new SeatSignInReading(SeatSignInState.SignedIn, null).SignedInClass);
        Assert.Equal(BillingClasses.Unknown, new SeatSignInReading(SeatSignInState.SignedIn, null, "credits").SignedInClass);
        foreach (var state in new[] { SeatSignInState.SignedOut, SeatSignInState.Unknown, SeatSignInState.NotApplicable })
            Assert.Null(new SeatSignInReading(state, "why", BillingClasses.Plan).SignedInClass);
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

    // ---- U-billing-followups items 3 and 4: the status's turns and the review's funding, in the app's own composition -------------

    /// <summary>One finished turn of <paramref name="role"/> billed to <paramref name="billing"/>, at noon today, on the CLI seat.</summary>
    static void Finished(Database db, string role, string billing)
    {
        var store = new AiAttemptStore(db);
        var at = TestEnv.LocalNoon();
        var id = $"done-{Guid.NewGuid():n}";
        store.Begin(new AiAttempt
        {
            Id = id, StartedAt = at, ReservedCost = 0.5m, BillingClass = billing, Role = role, SeatClass = SeatClasses.Cli,
            State = AiAttemptState.LAUNCHED
        });
        Assert.True(store.End(id, 0, at, 1000, 0, 0, 100, 0, "gpt-5.6-sol", 0.25m, null, null));
    }

    /// <summary>
    /// <c>U-billing-followups</c> item 3: <c>ai_turns_today</c> COUNTS EVERY TURN SINCE MIDNIGHT — plan, key and unknown. It read the money
    /// cap's reading, which since the billing classes counts key and unknown rows only, so a day of plan turns read as none.
    /// </summary>
    [Fact]
    public async Task The_status_counts_a_finished_plan_turn_among_the_days_turns()
    {
        using var db = TestEnv.NewDb();
        var host = TradeAgent.App.AppHost.Composed(db, new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker()),
            new AgentPresence());
        try
        {
            Finished(db, CouncilRoles.Operations, BillingClasses.Plan);
            Assert.Equal(1, (await host.Gateway.StatusAsync()).AiTurnsToday);
            Finished(db, CouncilRoles.Research, BillingClasses.Key);
            Finished(db, CouncilRoles.Research, BillingClasses.Unknown);
            Assert.Equal(3, (await host.Gateway.StatusAsync()).AiTurnsToday);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// <c>U-billing-followups</c> item 4: A REVIEW IS CALLED UNFUNDED ONLY WHEN THE CLASS IT WOULD BILL CANNOT FUND ONE TURN. With no real
    /// money today and the plan's 40, a council whose seats are signed in with the plan can still be reviewed — the report said it could
    /// not, reading both ceilings together. With neither ceiling funding a turn, it still says so.
    /// </summary>
    [Fact]
    public async Task A_review_billed_to_a_funded_plan_is_not_declared_unfunded_with_no_real_money()
    {
        using var db = TestEnv.NewDb();
        var host = TradeAgent.App.AppHost.Composed(db, new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker()),
            new AgentPresence());
        try
        {
            foreach (var role in CouncilRoles.All) Finished(db, role, BillingClasses.Plan);
            host.Gateway.Update(s => { s.AiDailyCostCap = 0m; s.AiPlanDailyCapacity = 40m; });
            Assert.All(CouncilRoles.All, role => Assert.Equal(BillingClasses.Plan, host.BillingClassOf(role)));
            Assert.Null(host.ReportInputs().ReviewNotFunded);

            // NEITHER CLASS CAN FUND ONE: unfunded, as before.
            host.Gateway.Update(s => { s.AiDailyCostCap = 0m; s.AiPlanDailyCapacity = 0m; });
            Assert.NotNull(host.ReportInputs().ReviewNotFunded);
        }
        finally { await host.Gateway.DisposeAsync(); }
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
        var box = TradeAgent.App.Ui.AmountField(current, AmountKind.Decimal);
        var b = TradeAgent.App.SafetyPage.BuildSavePlanCapacity(box, () => current, () => "USD", () => saved.Add(box.Value ?? current));

        box.Box.Text = "60";
        Press(b);
        Assert.Empty(saved);
        Assert.Equal(Labels.RaisePlanCapacityArmed("60 USD"), b.Content);
        Assert.Contains("ChatGPT plan, counted at list price", (string)b.Content!, StringComparison.Ordinal);

        box.Box.Text = "100";                       // an edit disarms the raise that named 60
        Press(b);
        Assert.Empty(saved);
        Assert.Equal(Labels.RaisePlanCapacityArmed("100 USD"), b.Content);
        Press(b);
        Assert.Equal([100m], saved);

        current = 100m;
        box.Box.Text = "10";                        // lowering: one press
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
    /// the two sentences the plan's figure cannot see, NO sentence about bought or extra credits (the owner's answer, 2026-10-22: "Leave it
    /// out"), and every seat's class in words.
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
        // The page says nothing about credits: no label carries the word, and the Safety page's own text has none (fix pass 3).
        var withCredits = typeof(Labels).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.GetValue(null) is string t
                && t.Contains("credit", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name).ToList();
        Assert.Empty(withCredits);
        Assert.DoesNotContain("credit",
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "TradeAgent.App", "DashboardView.cs")), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>U-billing-followups</c> item 2: THE PLAN'S TOTALS NAME THEIR UNREPORTED TURNS, as the money's do. A plan turn that never reported
    /// what it used — LOST at a restart, its reservation standing as its cost — is inside the plan capacity used; the report says how many
    /// of the day's plan turns are such reservations, so a worst case is never read as capacity measured.
    /// </summary>
    [Fact]
    public async Task The_daily_reports_plan_totals_name_a_lost_plan_turn_as_a_reservation_charged_without_reported_usage()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var meter = Meter(money: 10m, plan: 40m, db: db);
        using (meter.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
            Assert.True(Launch(meter, CouncilRoles.Operations).Admitted);
        Assert.Equal(1, new AiAttemptStore(db).LoseOpen(_at));

        var report = gw.Reports.Compose(_at, new TradeAgent.Gateway.DailyReportInputs
        {
            Spending = [meter.Today, .. CouncilRoles.All.Select(meter.TodayFor)]
        });
        Assert.Equal(1, report.Spending.PlanTurns);
        Assert.Equal(Turn, report.Spending.PlanSpent);

        var text = TradeAgent.Gateway.DailyReportText.Render(report);
        Assert.Contains("turns against plan capacity: 1, 1 charged at their reservation because no usage was ever reported", text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (g) THE DAILY REPORT COUNTS PLAN TURNS AND NEVER PRINTS PLAN FIGURES AS MONEY (fix pass 1): the real-money lines are the money cap's
    /// own (key and unknown rows), the plan's capacity has its own lines — its figures as capacity, never in the money's format — and the
    /// turns of each are counted; a role whose launches bill the plan is reported against the plan's ceiling, in its words.
    /// </summary>
    [Fact]
    public async Task The_daily_report_counts_plan_turns_and_never_prints_plan_figures_as_money()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var meter = Meter(money: 10m, plan: 40m, db: db);
        var store = new AiAttemptStore(db);
        using (meter.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
            for (var n = 0; n < 2; n++)
            {
                var id = Launch(meter, CouncilRoles.Operations).Id!;
                Assert.True(store.End(id, 0, _at, 1000, 0, 0, 100, 0, "gpt-5.6-sol", 1.5m, null, null));
            }
        // AND ONE STILL OPEN, committed.
        using (meter.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
            Assert.True(Launch(meter, CouncilRoles.Operations).Admitted);
        var report = gw.Reports.Compose(_at, new TradeAgent.Gateway.DailyReportInputs
        {
            Spending = [meter.Today, .. CouncilRoles.All.Select(meter.TodayFor)]
        });

        Assert.Equal(0, report.Spending.Turns);
        Assert.Equal(2, report.Spending.PlanTurns);
        Assert.Equal(10m, report.Spending.Cap);
        Assert.Equal(40m, report.Spending.PlanCap);
        Assert.Equal(3m, report.Spending.PlanSpent);
        Assert.Equal(Turn, report.Spending.PlanReserved);
        Assert.Equal(0m, report.Spending.Spent);
        var operations = report.Spending.Roles.Single(r => r.Role == CouncilRoles.Title(CouncilRoles.Operations));
        Assert.Equal(BillingClasses.Plan, operations.Class);
        Assert.Equal(40m, operations.Cap);

        var text = TradeAgent.Gateway.DailyReportText.Render(report);
        Assert.Contains($"daily ceiling of real money: {Labels.Money(10m, "USD")}", text, StringComparison.Ordinal);
        Assert.Contains("turns against plan capacity: 2", text, StringComparison.Ordinal);
        Assert.Contains($"plan capacity used: 3 of 40 a day, {Turn.ToString("0.####", CultureInfo.InvariantCulture)} committed — "
                        + "your ChatGPT plan's use counted at list price in USD, not money", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Labels.Money(40m, "USD"), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Labels.Money(3m, "USD"), text, StringComparison.Ordinal);
        Assert.Contains($"{CouncilRoles.Title(CouncilRoles.Operations)}: 3 of plan capacity used", text, StringComparison.Ordinal);
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
