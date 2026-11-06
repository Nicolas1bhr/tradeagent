using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// <c>U-turn-reservation</c> — A TURN BILLED TO THE OWNER'S PLAN RESERVES WHAT IT CAN PLAUSIBLY COST; A PAID API KEY'S KEEPS THE WHOLE BOUND
/// (R25 D13, decided by the orchestrator on the owner's behalf; BUDGET-001). Found by <c>U-loop-e2e</c>: at the owner's 2/8 one turn reserved
/// 2.56 while a 0.25 team of a 0.25 division of the founding 40 a day holds 2.50, so no team turn ever fit. A plan turn now reserves 2 × the
/// 99th percentile of its seat class + runtime + model's last 20 measured turns (their tokens at today's rate), never below one typical turn
/// (200,000 in / 4,000 out), never above the bound — two typical turns before five are measured. A turn that costs more is charged in full and
/// its unit refuses every launch until it is back inside its envelope. Every figure is computed at the owner's own rate 1/4 per million.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class TurnReservationTests : IDisposable
{
    readonly List<Database> _dbs = [];
    readonly string _records = Path.Combine(TestEnv.Home, $"reservation-{Guid.NewGuid():n}.jsonl");
    DateTimeOffset _at = TestEnv.LocalNoon();

    /// <summary>The owner's own two numbers per million tokens, so every reservation has one right answer.</summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    /// <summary>The whole turn bound at <see cref="Rate"/>: the shipped 1,200,000 in at 1 plus 20,000 out at 4.</summary>
    const decimal Bound = 1.28m;

    /// <summary>One typical turn at <see cref="Rate"/>: 200,000 in at 1 plus 4,000 out at 4.</summary>
    const decimal Typical = 0.216m;

    const string Runtime = "codex", Model = "gpt-5.6-sol";

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        foreach (var db in _dbs) db.Dispose();
    }

    Database NewDb()
    {
        var db = TestEnv.NewDb();
        _dbs.Add(db);
        return db;
    }

    TurnMeter Meter(Database db, decimal money = 1000m, decimal plan = 1000m, decimal research = 1m) =>
        new(db, () => money, now: () => _at, recordPath: _records, owner: () => Rate,
            share: role => role == CouncilRoles.Research ? research : 1m,
            seat: role => SeatResolver.Of(role, Runtime, Model, TurnAllowance.Default), live: new LiveAttempts(),
            declaredSeat: _ => SeatClasses.Cli, planCap: () => plan);

    /// <summary>
    /// One MEASURED turn in the ledger: ENDED with its runtime's own token figures on <paramref name="seatClass"/>, <paramref name="runtime"/>
    /// and <paramref name="model"/>. Its stored cost is deliberately not what its tokens cost today — the history is priced at today's rate.
    /// </summary>
    static void Measured(Database db, long input, long output, string seatClass = SeatClasses.Cli, string runtime = Runtime,
        string model = Model, DateTimeOffset? at = null)
    {
        var store = new AiAttemptStore(db);
        var id = $"measured-{Guid.NewGuid():n}";
        var when = at ?? TestEnv.LocalNoon().AddDays(-1);
        store.Begin(new AiAttempt
        {
            Id = id, StartedAt = when, Runtime = runtime, RequestedModel = model, SeatClass = seatClass, ReservedCost = Bound,
            BillingClass = BillingClasses.Plan
        });
        store.End(id, 0, when.AddMinutes(1), input, 0, 0, output, 0, null, 0.0001m, null, null);
    }

    /// <summary>One launch of <paramref name="role"/> billed to <paramref name="billing"/>; the open entry released so it may launch again.</summary>
    static AiAdmission Launch(TurnMeter meter, string role, string billing)
    {
        using var seat = meter.Attach(new BillingClassesTests.Billed(billing), role);
        var admission = meter.Begin("## Situation", role: role);
        if (admission.Id is { } id) meter.Release(role, id);
        return admission;
    }

    /// <summary>What a plan launch of the chair's Operations role reserves on a fresh ledger holding <paramref name="history"/>.</summary>
    decimal PlanReservationAfter(params (long In, long Out)[] history)
    {
        var db = NewDb();
        foreach (var (input, output) in history) Measured(db, input, output);
        var admission = Launch(Meter(db), CouncilRoles.Operations, BillingClasses.Plan);
        Assert.True(admission.Admitted, admission.Refusal);
        return new AiAttemptStore(db).Get(admission.Id!)!.ReservedCost;
    }

    // ---- guard 1: a plan turn reserves what it can plausibly cost, never the old bound -------------------------------------------

    /// <summary>
    /// A PLAN TURN WITH NO HISTORY RESERVES TWO TYPICAL TURNS, NOT THE BOUND — and four measured turns are still no history. The bound is
    /// what a key turn reserves (guard 2); here it would hold 1.28 for a turn of the owner's plan that has never been measured to cost it.
    /// </summary>
    [Fact]
    public void A_plan_turn_with_no_history_reserves_two_typical_turns_not_the_whole_bound()
    {
        Assert.Equal(2 * Typical, PlanReservationAfter());
        Assert.Equal(2 * Typical, PlanReservationAfter((300_000, 5_000), (300_000, 5_000), (300_000, 5_000), (300_000, 5_000)));
    }

    /// <summary>
    /// AFTER FIVE MEASURED TURNS A PLAN TURN RESERVES TWICE THE DEAREST OF THEM, at today's rate (their stored cost is 0.0001 each): the
    /// dearest of 100k/1k, 150k/2k, 300k/5k, 250k/2k, 200k/3k is 300k/5k = 0.32, so 0.64. Turns of another model, another seat class or
    /// another runtime, a LOST turn and a turn that reported no usage — each far dearer — are not its history.
    /// </summary>
    [Fact]
    public void After_five_measured_turns_a_plan_turn_reserves_twice_the_dearest_of_its_own_seat_and_model_at_todays_rate()
    {
        var db = NewDb();
        foreach (var (input, output) in new (long, long)[] { (100_000, 1_000), (150_000, 2_000), (300_000, 5_000), (250_000, 2_000), (200_000, 3_000) })
            Measured(db, input, output);
        Measured(db, 1_000_000, 20_000, model: "gpt-6-luna");
        Measured(db, 1_000_000, 20_000, seatClass: SeatClasses.Broker);
        Measured(db, 1_000_000, 20_000, runtime: "opencode");
        var store = new AiAttemptStore(db);
        store.Begin(new AiAttempt
        {
            Id = "lost", StartedAt = _at.AddHours(-2), Runtime = Runtime, RequestedModel = Model, SeatClass = SeatClasses.Cli, ReservedCost = 9m
        });
        store.LoseOpen(_at.AddHours(-1));
        store.Begin(new AiAttempt
        {
            Id = "no-usage", StartedAt = _at.AddHours(-1), Runtime = Runtime, RequestedModel = Model, SeatClass = SeatClasses.Cli, ReservedCost = 9m
        });
        store.End("no-usage", 1, _at.AddMinutes(-30), null, null, null, null, null, null, null, null, null);

        var admission = Launch(Meter(db), CouncilRoles.Operations, BillingClasses.Plan);
        Assert.True(admission.Admitted, admission.Refusal);
        Assert.Equal(0.64m, store.Get(admission.Id!)!.ReservedCost);
    }

    /// <summary>
    /// NEVER BELOW ONE TYPICAL TURN, NEVER ABOVE THE BOUND. Five cheap turns (10k/100 = 0.0104 each) reserve the floor, 0.216; five turns
    /// one of which read 1,000,000 in and 30,000 out (1.12, so 2.24) reserve the bound, 1.28 — the estimate is capped where the bound is lower.
    /// </summary>
    [Fact]
    public void A_plan_reservation_is_never_below_one_typical_turn_nor_above_the_bound()
    {
        Assert.Equal(Typical, PlanReservationAfter(Enumerable.Repeat((10_000L, 100L), 5).ToArray()));
        Assert.Equal(Bound, PlanReservationAfter((1_000_000, 30_000), (10_000, 100), (10_000, 100), (10_000, 100), (10_000, 100)));
    }

    // ---- guard 2: a paid API key's turn keeps the whole reservation, the day-total gate first ------------------------------------

    /// <summary>
    /// A KEY TURN — REAL MONEY — KEEPS THE WHOLE BOUND, AND SO DOES A TURN NOBODY ESTABLISHED THE PAYER OF, whatever its seat's history: five
    /// cheap measured turns that would size a plan turn at 0.216 leave both at 1.28. And the day-total gate stays first: with a money cap of
    /// 1.00 the key turn is refused by the day — it would cost up to 1.28 — though a plan-sized reservation would have fit.
    /// </summary>
    [Fact]
    public void A_key_turn_and_an_unknown_one_keep_the_whole_bound_whatever_the_seats_history_and_the_day_gate_comes_first()
    {
        var db = NewDb();
        for (var i = 0; i < 5; i++) Measured(db, 10_000, 100);
        var meter = Meter(db);
        var store = new AiAttemptStore(db);

        var key = Launch(meter, CouncilRoles.Research, BillingClasses.Key);
        var unknown = Launch(meter, CouncilRoles.Operations, BillingClasses.Unknown);
        Assert.Equal((Bound, Bound), (store.Get(key.Id!)!.ReservedCost, store.Get(unknown.Id!)!.ReservedCost));

        var capped = Launch(Meter(NewDb(), money: 1m), CouncilRoles.Research, BillingClasses.Key);
        Assert.False(capped.Admitted);
        Assert.Equal(AiAdmission.Day, capped.Ceiling);
        Assert.Equal(Labels.DailySpendingLimitReached, capped.Refusal);
    }

    // ---- guard 3: a turn that costs more than it reserved is charged in full and blocks its unit ---------------------------------

    /// <summary>
    /// A PLAN TURN THAT COSTS MORE THAN IT RESERVED IS CHARGED IN FULL, AND ITS UNIT TAKES NO NEW TURN UNTIL IT IS BACK INSIDE ITS ENVELOPE.
    /// The Research division holding 0.5 of a 4.00 plan day — 2.00. Its first turn reserves two typical turns (0.432) and reports 1,200,000
    /// in and 325,000 out: 2.50 at the owner's rate. The unit's next launch is refused by the division in words naming the 0.50 it is past
    /// its envelope, nothing written, though the day (4.00) has room; the row is charged 2.50, not 0.432; the next day it is admitted again.
    /// </summary>
    [Fact]
    public void A_plan_turn_that_overruns_its_reservation_is_charged_in_full_and_blocks_its_unit_until_it_is_back_inside()
    {
        var db = NewDb();
        var meter = Meter(db, plan: 4m, research: 0.5m);
        var store = new AiAttemptStore(db);
        const string role = CouncilRoles.Research;

        AiAdmission first;
        using (meter.Attach(new BillingClassesTests.Billed(BillingClasses.Plan), role))
        {
            first = meter.Begin("## Situation", role: role, heldForTheLoop: false);
            Assert.True(first.Admitted, first.Refusal);
            meter.Record(new AgentTurnEnded(0, TimeSpan.FromMinutes(1), "", _at.AddMinutes(1))
            {
                Usage = new TurnUsage(1_200_000, 0, 0, 325_000, 0, null)
            }, role);
        }

        var blocked = Launch(meter, role, BillingClasses.Plan);
        Assert.False(blocked.Admitted);
        Assert.Equal(OrgIds.Research, blocked.Ceiling);
        Assert.Equal(Labels.UnitEnvelopeOverrun(OrgEnvelopes.NameOf(OrgIds.Research), 2m, 0.5m, plan: true), blocked.Refusal);
        var charged = store.Get(first.Id!)!;
        Assert.Equal((AiAttemptState.ENDED, 2 * Typical, 2.5m), (charged.State, charged.ReservedCost, charged.Cost));
        Assert.Single(store.Between(_at.AddDays(-1), _at.AddDays(1)), a => a.Role == role);

        _at = _at.AddDays(1);
        Assert.True(Launch(meter, role, BillingClasses.Plan).Admitted);
    }
}
