using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PROPERTY <c>docs/COUNCIL.md</c> NAMES FOR THIS UNIT: every launched attempt has a durable
/// identity and a spending commitment, and killing it before its usage arrives — then restarting
/// across midnight — cannot make that allowance available again.
///
/// What it replaces was not a rounding error. The day's totals were two counters written when a turn
/// FINISHED, so a turn whose process was killed cost nothing at all: the vendor had done the work and
/// billed for it, and the app's figure never moved. An AI that is killed and restarted therefore got
/// its whole allowance back, every time, and the ceiling that is the only bound on an agent working
/// non-stop was a report on completed spending rather than a cap. Measured on 2026-09-07: 5.07 USD
/// against a 5 USD ceiling.
///
/// The child in the kill test is a REAL process that is really killed — a script that sleeps — rather
/// than a fake that pretends to be one, because "killed before its usage arrived" is a fact about a
/// process and a database row, and a stand-in for the process would exercise neither.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class AiAttemptLedgerTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"attempts-{Guid.NewGuid():n}.jsonl");

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        _db.Dispose();
    }

    /// <summary>
    /// The owner's own rate, so the reservation has one right answer: 1,200,000 input at 1.00 and
    /// 20,000 output at 4.00, per million, is 1.28.
    /// </summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    const decimal Reservation = (1_200_000m * 1m + 20_000m * 4m) / 1_000_000m;

    TurnMeter Meter(Func<DateTimeOffset> now) =>
        new(_db, () => 5m, session: () => "thread-1", runtimeId: () => "probe", now: now,
            recordPath: _records, owner: () => Rate, model: () => "gpt-5.6-sol");

    /// <summary>
    /// THE GUARD. A turn is launched, its row and its reservation are written first, the process is
    /// killed with nothing having reported its usage, and a fresh meter opens the same database — as
    /// a restarted app does. The reservation is charged, and the next local day does not hand it back.
    /// </summary>
    [Fact]
    public async Task A_turn_killed_before_its_usage_stays_charged_across_a_restart_and_midnight()
    {
        var now = new DateTimeOffset(2026, 9, 7, 21, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 7)));
        var meter = Meter(() => now);

        // BEFORE the process, which is the whole of the mechanism.
        var id = meter.Begin("## Situation\n\nContinue your mission.").Id;
        Assert.NotNull(id);

        var store = new AiAttemptStore(_db);
        var launched = store.Get(id!)!;
        Assert.Equal(AiAttemptState.LAUNCHED, launched.State);
        Assert.Equal(Reservation, launched.ReservedCost);
        Assert.Equal("gpt-5.6-sol", launched.RequestedModel);
        Assert.Equal(AiAttemptStore.OwnerBasis, launched.PricingBasis);
        Assert.Equal(Versions.GrantPolicyVersion.ToString(), launched.PolicyVersion);
        // The prompt is hashed, never stored: it carries the owner's own words.
        Assert.Equal(64, launched.InputHash!.Length);

        // A real turn, really killed. Nothing is metering this session, which is what an app dying
        // mid-turn looks like from the database's side: the row stays LAUNCHED for ever.
        var session = AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream, sleepSeconds: 30);
        var run = session.SendAsync("go");
        for (var i = 0; i < 200 && !session.Busy; i++) await Task.Delay(10);
        Assert.True(session.Busy);
        await session.CancelAsync();
        await run;

        Assert.Equal(AiAttemptState.LAUNCHED, store.Get(id)!.State);
        Assert.Equal(0m, meter.Today.Spent);
        Assert.Equal(Reservation, meter.Today.Reserved);

        // The restart. Opening the database is what settles a turn nobody will ever hear from again.
        var fresh = Meter(() => now);
        Assert.Equal(AiAttemptState.LOST, store.Get(id)!.State);
        Assert.Equal(Reservation, fresh.Today.Spent);
        Assert.Equal(0m, fresh.Today.Reserved);
        Assert.Equal(1, fresh.Today.Turns);

        // AND ACROSS MIDNIGHT. Tomorrow's allowance is a new one — that is what a daily ceiling is —
        // but the charge stays on the day it was incurred rather than being released by the rollover.
        now = now.AddDays(1);
        Assert.Equal(0m, fresh.Today.Spent);
        Assert.Equal(0, fresh.Today.Turns);

        var lost = store.Get(id)!;
        Assert.Equal(AiAttemptState.LOST, lost.State);
        Assert.Equal(Reservation, lost.Cost);
        Assert.NotNull(lost.UnpricedReason);
    }

    /// <summary>
    /// A turn that ENDS normally is charged what it actually used, not its reservation — the
    /// commitment is released by being replaced with a measurement, and by nothing else.
    /// </summary>
    [Fact]
    public void A_turn_that_ends_is_charged_what_it_used_rather_than_what_it_reserved()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(() => now);
        var id = meter.Begin("## Situation").Id!;

        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(9), "…", now)
        {
            Usage = new TurnUsage(17232, 12928, 0, 6, 0, null)
        });

        // THE CLOSE IS HELD FOR THE TURN'S OWN TRANSITION. A turn the loop opened ends in the same
        // Database.Write as what it published and what became of its wakes, so the row moves to
        // ENDED here rather than the moment the runtime reported its usage.
        Assert.Equal(AiAttemptState.LAUNCHED, new AiAttemptStore(_db).Get(id)!.State);
        Assert.True(meter.CommitStaged());

        var row = new AiAttemptStore(_db).Get(id)!;
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(Reservation, row.ReservedCost);
        Assert.Equal((17232m * 1m + 6m * 4m) / 1_000_000m, row.Cost);
        Assert.Equal(0, row.ExitCode);
        Assert.Equal(17232, row.InputTokens);
        Assert.Equal(row.Cost, meter.Today.Spent);
        Assert.Equal(0m, meter.Today.Reserved);
    }

    /// <summary>
    /// A row a restart has already declared LOST is not reopened by a late arrival. A process that
    /// came back to claim a cheaper number for a turn nobody was watching is the one thing this
    /// table exists to refuse.
    /// </summary>
    [Fact]
    public void A_lost_attempt_is_not_reopened_by_a_late_report()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(() => now);
        var id = meter.Begin("## Situation").Id!;

        var store = new AiAttemptStore(_db);
        store.LoseOpen(now);
        Assert.Equal(AiAttemptState.LOST, store.Get(id)!.State);

        Assert.False(store.End(id, 0, now, 1, 0, 0, 1, 0, "gpt-5.6-sol", 0.0001m, null, null));
        var row = store.Get(id)!;
        Assert.Equal(AiAttemptState.LOST, row.State);
        Assert.Equal(Reservation, row.Cost);
    }

    /// <summary>
    /// The owner's own typed turn is recorded too — every run of the CLI is charged to somebody —
    /// but it reserves nothing, because no admission gate ran for it. The loop's turns are the ones
    /// that commit ahead.
    /// </summary>
    [Fact]
    public void A_turn_nobody_opened_is_still_recorded_and_reserves_nothing()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(() => now);

        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(3), "…", now)
        {
            Usage = new TurnUsage(1000, 0, 0, 10, 0, null)
        });

        var (from, to) = (now.ToLocalTime().Date, now.ToLocalTime().Date.AddDays(1));
        var rows = new AiAttemptStore(_db).Between(
            new DateTimeOffset(from, TimeZoneInfo.Local.GetUtcOffset(from)),
            new DateTimeOffset(to, TimeZoneInfo.Local.GetUtcOffset(to)));

        var row = Assert.Single(rows);
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(0m, row.ReservedCost);
        Assert.Equal(1, meter.Today.Turns);
    }

    /// <summary>
    /// The migration itself: the launch ledger arrived at schema 6, this build is at or past it, the
    /// version on disk is the one this build writes, and an older database gains the table empty
    /// rather than inheriting a total whose per-turn detail nobody kept.
    ///
    /// It used to read <c>Assert.Equal(6, Versions.DatabaseSchemaVersion)</c>, which made every
    /// later ADDITIVE migration fail here for no reason of its own — <c>U-wakes</c> added
    /// <c>mission_event</c> at 7 and this went red without anything about the launch ledger having
    /// changed. The exact number is still pinned, by
    /// <c>CouncilRoleTests.The_role_columns_arrive_at_schema_nine_and_an_unnamed_row_is_the_chairs</c>,
    /// which is where the current version belongs: with the migration that last moved it. What is
    /// asserted here is what this class is about — 6 is the floor, because below it there is no
    /// table at all.
    /// </summary>
    [Fact]
    public void The_launch_ledger_is_schema_six_and_starts_empty()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= 6,
            $"the launch ledger needs schema 6 or later; this build says {Versions.DatabaseSchemaVersion}");

        using var version = _db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), version.ExecuteScalar() as string);

        using var c = _db.Cmd("SELECT COUNT(*) FROM ai_attempt");
        Assert.Equal(0, Convert.ToInt32(c.ExecuteScalar()));
    }
}
