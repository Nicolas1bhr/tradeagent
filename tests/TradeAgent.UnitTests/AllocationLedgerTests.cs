using System.Globalization;
using System.Reflection;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEMS 1 AND 2 — THE ALLOCATION RECORD, AND THE STANDING IT MAY ONLY BE WRITTEN AGAINST.
///
/// <para><b>What was wrong before this.</b> No line of <c>src</c> allocated capital at all: the only
/// thing this product called an allocation was the AI budget's split between council roles, and
/// nothing on the order path read <c>Promotions.Standing</c>. A promoted version and a quantity the
/// gateway would allow had nothing binding them, so <c>docs/COUNCIL.md</c>:14-15 — "every order passes
/// the code-enforced capability, freshness, reconciliation, capital and loss gates" — named a capital
/// gate this build did not have.</para>
///
/// <para><b>The two mutants this class exists to catch.</b> The id minted from the CLOCK, so one
/// allocation decision becomes two rows and no later reader can say which one the gateway was
/// enforcing. And the eligibility check written as "a promotion row exists" instead of
/// <c>Standing.IsPromoted</c>, which lets a version whose evidence TradeAgent has withdrawn go on
/// being allocated the owner's money.</para>
/// </summary>
public class AllocationLedgerTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    const string ProgramText = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// EVERYTHING AN ALLOCATION POINTS AT, really in the database: the held-back dataset, the campaign,
    /// the version, the holdout run and the promotion itself. The table's foreign keys mean a test
    /// cannot invent any of them, which is the point — an allocation of capital to nothing is the row
    /// this design exists to make unwritable.
    /// </summary>
    sealed record Allocated(Database Db, DatasetRecord Set, string VersionId, PromotionRow Promotion,
        string UnjudgedVersionId);

    static Allocated Given(string verdict = PromotionVerdict.Promoted, string reason = PromotionReason.Met)
    {
        var db = TestEnv.NewDb();
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"allocation-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));

        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open("BTCUSDT 1m v1", set, 10, 3, At);
        Assert.True(campaign.Ok, campaign.Why);

        var program = StrategyParser.Parse(ProgramText).Program!;
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars,
            Cutoff.AddDays(-1), null, null));

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var request = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null);
        var runId = request.RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        var promotion = new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id, campaign.Campaign.ScoringPolicySha256,
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical,
            Referee.EvaluatorVersion, runId, verdict, reason, At));

        // A SECOND VERSION NOBODY HAS ASKED ABOUT, really in the table. "No verdict has been recorded"
        // is a different state from "the answer was no", and a test that faked the version id would be
        // caught by the foreign key instead of by the eligibility check it is about.
        var unjudged = StrategyParser.Parse(
            ProgramText.Replace("103", "107", StringComparison.Ordinal)).Program!;
        strategies.RecordVersion(new StrategyVersionRow(
            unjudged.StrategyId, unjudged.Source, unjudged.Canonical, unjudged.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, unjudged.WarmUpBars,
            Cutoff.AddDays(-1), null, null));

        return new Allocated(db, set, program.StrategyId, promotion, unjudged.StrategyId);
    }

    /// <summary>The seven facts of one allocation, as the owner's card would have declared them.</summary>
    static AllocationRow RowFor(Allocated a, decimal quantity = 1m, decimal? notional = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, DateTimeOffset? at = null) =>
        new("", a.VersionId, a.Promotion.Id, AllocationPolicy.V1, quantity, notional, "USD",
            from ?? At, to, "the owner allocated capital to this version", at ?? At);

    // ---- item 1: the record -------------------------------------------------------------------

    /// <summary>
    /// AN ALLOCATION IS THE HASH OF THE SEVEN FACTS IT BINDS, AND OF NOTHING ELSE.
    ///
    /// <para>Each of the seven is varied on its own and each one moves the id, which is what "bound to"
    /// means: capital allocated under a different ceiling, a different promotion or a different policy
    /// is a different allocation and cannot occupy the row of the one before it.</para>
    ///
    /// <para>RED before this unit: <c>SQLite Error 1: 'no such table: strategy_allocation'</c>.</para>
    /// </summary>
    [Fact]
    public void An_allocation_is_addressed_by_the_seven_facts_it_binds()
    {
        var a = Given();
        using var _1 = a.Db;

        var recorded = new Allocations(a.Db).Record(RowFor(a));
        Assert.True(recorded.Ok, recorded.Why);
        var written = recorded.Allocation!;

        Assert.Equal(64, written.Id.Length);
        Assert.Equal(AllocationRow.IdOf(a.VersionId, a.Promotion.Id, AllocationPolicy.V1, 1m, null, "USD", At),
            written.Id);

        var baseline = written.Id;
        var varied = new[]
        {
            AllocationRow.IdOf("another-version", a.Promotion.Id, AllocationPolicy.V1, 1m, null, "USD", At),
            AllocationRow.IdOf(a.VersionId, "another-promotion", AllocationPolicy.V1, 1m, null, "USD", At),
            AllocationRow.IdOf(a.VersionId, a.Promotion.Id, "allocation-policy-v2", 1m, null, "USD", At),
            AllocationRow.IdOf(a.VersionId, a.Promotion.Id, AllocationPolicy.V1, 2m, null, "USD", At),
            AllocationRow.IdOf(a.VersionId, a.Promotion.Id, AllocationPolicy.V1, 1m, 50_000m, "USD", At),
            AllocationRow.IdOf(a.VersionId, a.Promotion.Id, AllocationPolicy.V1, 1m, null, "EUR", At),
            AllocationRow.IdOf(a.VersionId, a.Promotion.Id, AllocationPolicy.V1, 1m, null, "USD", At.AddDays(1))
        };

        Assert.Equal(7, varied.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(baseline, varied);
    }

    /// <summary>
    /// THE MUTANT: THE CLOCK HASHED INTO THE ID. The same allocation recorded twice, at two instants,
    /// is ONE row and the first account of it stands — which is what lets the owner's card be pressed
    /// twice, or the app be crashed between the write and the confirmation, without the gateway
    /// finding two ceilings for one version and having to choose.
    ///
    /// <para>With <c>at</c> in <c>IdOf</c> the second write lands beside the first:
    /// <c>Assert.Single() Failure: The collection contained 2 items</c>.</para>
    /// </summary>
    [Fact]
    public void The_same_allocation_recorded_twice_is_one_row_and_the_first_account_stands()
    {
        var a = Given();
        using var _1 = a.Db;
        var allocations = new Allocations(a.Db);

        var first = allocations.Record(RowFor(a, at: At)).Allocation!;
        var second = allocations.Record(RowFor(a, at: At.AddHours(3))).Allocation!;

        // The row count first, because it is the whole claim: one decision, one row.
        Assert.Single(allocations.For(a.VersionId));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(At, second.At);
    }

    /// <summary>
    /// THE TABLE ITSELF CANNOT HOLD AN ALLOCATION OF CAPITAL TO NOTHING. The store refuses a version
    /// nobody submitted a step earlier (see the standing tests below), so this reaches past it and
    /// writes the row directly: the foreign keys are the schema's own guarantee and are what makes an
    /// allocation naming no version unwritable rather than merely unwritten.
    /// </summary>
    [Fact]
    public void The_table_refuses_an_allocation_naming_a_version_that_does_not_exist()
    {
        var a = Given();
        using var _1 = a.Db;

        var boom = Assert.ThrowsAny<Exception>(() => a.Db.Write(_ =>
        {
            using var c = a.Db.Cmd("""
                INSERT INTO strategy_allocation(id, version_id, promotion_id, policy_version,
                  max_quantity, max_notional, currency, effective_from, effective_to, reason, at)
                VALUES('an-id','a-version-nobody-submitted',$prom,'p','1',NULL,'USD','x',NULL,'r','y')
                """, ("$prom", a.Promotion.Id));
            return c.ExecuteNonQuery();
        }));

        Assert.Contains("FOREIGN KEY", boom.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE LEDGER EXPOSES ONE WRITE AND IT ONLY INSERTS. No update, no delete, no <c>Withdraw</c> —
    /// not "no pipe op", no METHOD, so there is nothing for a later caller to reach for.
    ///
    /// <para>A ceiling is what the gateway refuses orders against. A limit its subject could edit is
    /// not a limit, and a capital decision the app could quietly restate once the outcome was known is
    /// not a record (<c>docs/COUNCIL.md</c>:210-212). Lowering or withdrawing an allocation is a fresh
    /// row from a later instant, which <see cref="Allocations.StandingFor"/> answers with.</para>
    /// </summary>
    [Fact]
    public void The_allocation_ledger_exposes_one_write_and_it_only_inserts()
    {
        var writes = typeof(Allocations)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            // `Standing` and `StandingFor` are READS — which row is in force, and where the promotion
            // under it stands, both computed from rows nobody edited.
            .Where(n => n is not ("ById" or "For" or "All" or "Standing" or "StandingFor"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Record"], writes);
        Assert.DoesNotContain(typeof(Allocations).GetMethods(), m =>
            m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Withdraw", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// THE TABLE IS AT SCHEMA 20. A FLOOR rather than an equality, so an additive migration above this
    /// rung does not have to edit a test about it.
    /// </summary>
    [Fact]
    public void The_allocation_table_is_schema_twenty()
    {
        var a = Given();
        using var _1 = a.Db;

        Assert.True(Versions.DatabaseSchemaVersion >= 20,
            "the allocation record needs schema 20 or later; this build says "
            + Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(
            Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            a.Db.Read(_ =>
            {
                using var c = a.Db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return (string)c.ExecuteScalar()!;
            }));
    }

    /// <summary>
    /// A LATER ALLOCATION SUPERSEDES AN EARLIER ONE, which is how a ceiling is lowered in a ledger with
    /// no update. Both rows stay on the table; the newest one in force is the answer.
    /// </summary>
    [Fact]
    public void The_newest_allocation_in_force_is_the_one_that_stands()
    {
        var a = Given();
        using var _1 = a.Db;
        var allocations = new Allocations(a.Db);

        Assert.True(allocations.Record(RowFor(a, quantity: 5m, from: At)).Ok);
        Assert.True(allocations.Record(RowFor(a, quantity: 1m, from: At.AddDays(1))).Ok);

        Assert.Equal(5m, allocations.StandingFor(a.VersionId, At.AddHours(1))!.Allocation.MaxQuantity);
        Assert.Equal(1m, allocations.StandingFor(a.VersionId, At.AddDays(2))!.Allocation.MaxQuantity);
        Assert.Equal(2, allocations.For(a.VersionId).Count);

        // Before it takes effect, and after it has expired, nothing stands.
        Assert.Null(allocations.StandingFor(a.VersionId, At.AddDays(-1)));
        Assert.True(allocations.Record(RowFor(a, quantity: 2m, from: At.AddDays(3), to: At.AddDays(4))).Ok);
        Assert.Equal(1m, allocations.StandingFor(a.VersionId, At.AddDays(5))!.Allocation.MaxQuantity);
    }

    // ---- item 2: only against a version that stands promoted ----------------------------------

    /// <summary>
    /// THE MUTANT: THE ELIGIBILITY WRITTEN AS "A PROMOTION ROW EXISTS".
    ///
    /// <para>The version here WAS promoted and the row is still on the table. Its holdout dataset has
    /// since been rejected, so <c>Promotions.Standing</c> reads <c>invalidated</c> —
    /// <c>docs/COUNCIL.md</c>:35, "a changed assumption invalidates the evidence that rested on it" —
    /// and the owner's money must not be put behind evidence TradeAgent has withdrawn.</para>
    ///
    /// <para>With the check written as <c>_promotions.ById(allocation.PromotionId) is not null</c> the
    /// allocation is recorded and stands: <c>Assert.False() Failure / Expected: False / Actual: True</c>,
    /// and the row is there for the gateway to authorise orders against.</para>
    /// </summary>
    [Fact]
    public void An_invalidated_promotion_cannot_be_allocated_capital()
    {
        var a = Given();
        using var _1 = a.Db;
        var allocations = new Allocations(a.Db);

        new DatasetStore(a.Db).Reject(a.Set.Id, "a raw archive file changed under it");
        Assert.Equal(PromotionState.Invalidated, new Promotions(a.Db).Standing(a.VersionId).State);

        var refused = allocations.Record(RowFor(a));

        Assert.False(refused.Ok, refused.Why);
        Assert.Null(refused.Allocation);
        Assert.Contains("does not stand promoted", refused.Why, StringComparison.Ordinal);
        Assert.Empty(allocations.For(a.VersionId));
        Assert.Null(allocations.StandingFor(a.VersionId, At));
    }

    /// <summary>
    /// AN UNJUDGED VERSION CANNOT BE ALLOCATED CAPITAL — the version is really in the table, and
    /// TradeAgent's referee has simply never been asked about it. <c>docs/COUNCIL.md</c>:32-33: only a
    /// promoted version executes, so only a promoted version is given anything to execute with.
    ///
    /// <para>RED before this unit: the store wrote the row and it stood —
    /// <c>Assert.False() Failure / Expected: False / Actual: True</c>, with
    /// <c>Assert.Empty() Failure: Collection was not empty</c> behind it.</para>
    /// </summary>
    [Fact]
    public void An_unjudged_version_cannot_be_allocated_capital()
    {
        var a = Given();
        using var _1 = a.Db;
        var allocations = new Allocations(a.Db);

        var unjudged = allocations.Record(RowFor(a) with { VersionId = a.UnjudgedVersionId });

        Assert.False(unjudged.Ok, unjudged.Why);
        Assert.Contains("no verdict has been recorded", unjudged.Why, StringComparison.Ordinal);
        Assert.Empty(allocations.For(a.UnjudgedVersionId));
        Assert.Null(allocations.StandingFor(a.UnjudgedVersionId, At));
    }

    /// <summary>
    /// AND A VERSION THE REFEREE SAID NO TO. "Nobody has asked" and "the answer was no" are different
    /// facts and the refusal says which; neither of them is a promotion.
    /// </summary>
    [Fact]
    public void A_refused_version_cannot_be_allocated_capital()
    {
        var a = Given(PromotionVerdict.Refused, PromotionReason.NotProfitable);
        using var _1 = a.Db;

        var refused = new Allocations(a.Db).Record(RowFor(a));

        Assert.False(refused.Ok, refused.Why);
        Assert.Contains("refused", refused.Why, StringComparison.Ordinal);
        Assert.Empty(new Allocations(a.Db).For(a.VersionId));
    }

    /// <summary>
    /// AN ALLOCATION MUST NAME THE PROMOTION THE VERSION ACTUALLY STANDS ON. The id binds the two, so
    /// an allocation pointing at some other verdict would carry a provenance that is not the reason it
    /// was allowed.
    /// </summary>
    [Fact]
    public void An_allocation_must_name_the_promotion_the_version_stands_on()
    {
        var a = Given();
        using var _1 = a.Db;

        var mismatched = new Allocations(a.Db).Record(RowFor(a) with { PromotionId = new string('f', 64) });

        Assert.False(mismatched.Ok);
        Assert.Contains("is not the one", mismatched.Why, StringComparison.Ordinal);
        Assert.Empty(new Allocations(a.Db).For(a.VersionId));
    }
}
