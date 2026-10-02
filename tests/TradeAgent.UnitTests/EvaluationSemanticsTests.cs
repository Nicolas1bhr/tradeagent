using System.Globalization;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHEN A VERDICT WAS WITHDRAWN — the one fact a standing computed at read time cannot know, recorded the
/// only way it can be: the first instant this installation evaluated under each build's semantics
/// (<c>U-evidence-identity</c>).
///
/// <para>Nothing here decides a standing; <c>Promotions.Standing</c> compares the semantics and is
/// tested in <c>PromotionLedgerTests</c>. This is the date <c>trade verdict</c> puts on a withdrawal,
/// and the two things that would make it a wrong one: an instant that moves when a build opens the
/// ledger a second time, and a withdrawal dated by the LAST change instead of the first.</para>
/// </summary>
public class EvaluationSemanticsTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    static DateTimeOffset Read(string? text) =>
        DateTimeOffset.Parse(text!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// THE FIRST INSTANT IS RECORDED ONCE AND NEVER MOVES: a build that opens the ledger again — every
    /// start, every connector switch — writes nothing, so the date a withdrawal is given cannot drift to
    /// the latest restart.
    /// </summary>
    [Fact]
    public void The_first_instant_a_build_evaluates_under_its_semantics_is_recorded_once_and_never_moved()
    {
        using var db = TestEnv.NewDb();

        Assert.True(EvaluationSemantics.RecordInForce(db, T0));
        Assert.False(EvaluationSemantics.RecordInForce(db, T0.AddDays(3)));

        Assert.Equal(T0, Read(db.GetKv(EvaluationSemantics.SinceKeyPrefix + EvaluationSemantics.Current)));
        Assert.Equal(
            $"evaluator {Referee.EvaluatorVersion} with manifest {StrategyVersions.Manifest}",
            EvaluationSemantics.Current);
    }

    /// <summary>
    /// A VERDICT IS WITHDRAWN AT THE FIRST OTHER SEMANTICS RECORDED AFTER IT WAS TAKEN — the earliest,
    /// because across two bumps the first one is what withdrew it; never at a change recorded before the
    /// judgement, which that judgement already postdates; and with no date at all when none was
    /// recorded, rather than with one invented.
    /// </summary>
    [Fact]
    public void A_verdict_is_withdrawn_at_the_first_other_semantics_recorded_after_it_was_taken()
    {
        using var db = TestEnv.NewDb();
        const string manifest = "language=1;indicators=1;calendar=1";

        db.SetKv(EvaluationSemantics.SinceKeyPrefix + EvaluationSemantics.Of("backtest=1;metrics=1;scoring=1", manifest), Iso(T0));
        db.SetKv(EvaluationSemantics.SinceKeyPrefix + EvaluationSemantics.Of("backtest=2;metrics=1;scoring=1", manifest), Iso(T0.AddDays(10)));
        db.SetKv(EvaluationSemantics.SinceKeyPrefix + EvaluationSemantics.Of("backtest=3;metrics=1;scoring=1", manifest), Iso(T0.AddDays(20)));

        // Judged under the first, withdrawn by the second — not by the third, which changed nothing more.
        Assert.Equal(T0.AddDays(10),
            EvaluationSemantics.WithdrawnAt(db, "backtest=1;metrics=1;scoring=1", manifest, T0.AddDays(2)));

        // Judged under the second, withdrawn by the third.
        Assert.Equal(T0.AddDays(20),
            EvaluationSemantics.WithdrawnAt(db, "backtest=2;metrics=1;scoring=1", manifest, T0.AddDays(12)));

        // The manifest half is half of the pair: the same evaluator under another manifest is other semantics.
        Assert.Equal(T0,
            EvaluationSemantics.WithdrawnAt(db, "backtest=1;metrics=1;scoring=1", "language=1;indicators=0;calendar=1",
                T0.AddDays(-1)));

        // Taken after every recorded change: nothing recorded withdrew it, and no date is made up.
        Assert.Null(
            EvaluationSemantics.WithdrawnAt(db, "backtest=0;metrics=1;scoring=1", manifest, T0.AddDays(30)));
    }
}
