using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1 — THE HOLDOUT CUTOFF, AND THE ONE DIRECTION IT MAY NOT BE MOVED.
///
/// <para><b>What was wrong before this.</b> <c>dataset</c> had two states, ACCEPTED and REJECTED, and
/// no class and no cutoff — so nothing in this product held anything back. `docs/COUNCIL.md`:131 asks
/// the referee to protect "holdout data the research process cannot reach", and :212 says why it is
/// this unit's job rather than a later one: contaminated evidence is one of the four things that
/// cannot be recovered afterwards, because "a leaked holdout cannot become unseen".</para>
///
/// <para><b>Where COUNCIL is silent, this build chooses</b> — and `docs/CONTRACTS.md` says so as a
/// choice. A holdout is a TIME CUTOFF on a dataset rather than a second dataset: a cutoff cannot be
/// asked for by id, and there is only one file to keep hashed. The cutoff is INCLUSIVE.</para>
///
/// <para><b>The mutant this class exists to catch</b> is the cutoff taken from a request field — a
/// caller marking its own holdout, which is a defendant writing the indictment. The wire half of that
/// is `HoldoutOverPipeTests`; this half is that the store has exactly one writer and that it refuses
/// to move a cutoff back.</para>
/// </summary>
public class HoldoutLedgerTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A dataset row to hang a cutoff on. No bars are read here — the columns are the subject — so the
    /// normalised path names a file that does not exist and nothing calls <c>Checked</c>.
    /// </summary>
    static DatasetRecord Given(Database db, string pair = "BTCUSDT")
    {
        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 12, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), "aa11", 1000,
            At.AddDays(-300), At, 0, [], false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []);

        return record with { Id = new DatasetStore(db).Record(record) };
    }

    /// <summary>
    /// THE TWO COLUMNS ARRIVE AT SCHEMA 14, AND AN OLDER ROW READS AS A DATASET WITH NO HOLDOUT.
    ///
    /// <para>That reading is the only safe one for an additive migration: a row written before this
    /// unit was collected by an installation that held nothing back, and inventing a cutoff for it
    /// would make bars the research process has already read look private. The class reads
    /// <c>research</c> for the same reason — a fixture is something the owner declares, and defaulting
    /// to it would make every existing dataset free to run over.</para>
    /// </summary>
    [Fact]
    public void The_holdout_columns_arrive_at_schema_fourteen_and_an_older_row_holds_nothing_back()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= 14,
            $"the holdout, the campaign and the trial need schema 14 or later; "
            + $"this build says {Versions.DatabaseSchemaVersion}");

        using var db = TestEnv.NewDb();
        var set = new DatasetStore(db).ById(Given(db).Id)!;

        Assert.Null(set.HoldoutFrom);
        Assert.Equal(EvaluationClass.Research, set.EvaluationClass);
    }

    /// <summary>The owner's press, read back: the instant in UTC and the class beside it.</summary>
    [Fact]
    public void The_owners_cutoff_is_recorded_in_utc_and_read_back_off_the_row()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var set = Given(db);

        // Deliberately NOT already UTC: the owner's window hands over whatever the machine's offset
        // is, and a cutoff stored in local time would mean a different set of bars on every machine.
        var local = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.FromHours(2));
        var done = store.SetHoldout(set.Id, local, EvaluationClass.Research);

        Assert.True(done.Ok, done.Why);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), store.ById(set.Id)!.HoldoutFrom);
        Assert.Equal(EvaluationClass.Research, store.ById(set.Id)!.EvaluationClass);
    }

    /// <summary>
    /// THE REFUSAL THAT CANNOT BE A WARNING. Bars between the old cutoff and an earlier new one have
    /// already been served; rewriting the column would record them as evidence nobody had seen.
    /// </summary>
    [Fact]
    public void A_cutoff_cannot_be_moved_earlier_and_the_refusal_says_why()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var set = Given(db);
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(store.SetHoldout(set.Id, cutoff, EvaluationClass.Research).Ok);

        var back = store.SetHoldout(set.Id, cutoff.AddDays(-30), EvaluationClass.Research);

        Assert.False(back.Ok, "a cutoff was moved back, and the bars in between had already been served");
        Assert.Contains("EARLIER", back.Why, StringComparison.Ordinal);
        Assert.Contains("already been served to the research process", back.Why, StringComparison.Ordinal);
        Assert.Equal(cutoff, store.ById(set.Id)!.HoldoutFrom);
    }

    /// <summary>
    /// The other direction, which is the whole reason this is not "the cutoff is immutable". Moving it
    /// later withholds bars nothing has read yet, so it leaks nothing and the owner may do it.
    /// </summary>
    [Fact]
    public void A_cutoff_may_be_moved_later_because_that_withholds_bars_nothing_has_read()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var set = Given(db);
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(store.SetHoldout(set.Id, cutoff, EvaluationClass.Research).Ok);
        Assert.True(store.SetHoldout(set.Id, cutoff, EvaluationClass.Research).Ok, "the same instant again");

        var later = store.SetHoldout(set.Id, cutoff.AddDays(30), EvaluationClass.Research);

        Assert.True(later.Ok, later.Why);
        Assert.Equal(cutoff.AddDays(30), store.ById(set.Id)!.HoldoutFrom);
    }

    /// <summary>
    /// A class this build does not know is refused rather than written. The column decides whether a
    /// run over these bars is CHARGED, so a word nobody can read must not become the reading that
    /// charges nothing.
    /// </summary>
    [Fact]
    public void An_evaluation_class_this_build_does_not_know_is_refused()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var set = Given(db);

        var done = store.SetHoldout(set.Id, At, "free");

        Assert.False(done.Ok);
        Assert.Contains("is not an evaluation class this build knows", done.Why, StringComparison.Ordinal);
        Assert.Null(store.ById(set.Id)!.HoldoutFrom);
    }

    /// <summary>A dataset this installation does not have is a sentence, not an exception.</summary>
    [Fact]
    public void A_holdout_on_a_dataset_that_is_not_there_is_refused_in_words()
    {
        using var db = TestEnv.NewDb();

        var done = new DatasetStore(db).SetHoldout(4242, At, EvaluationClass.Fixture);

        Assert.False(done.Ok);
        Assert.Contains("there is no dataset 4242", done.Why, StringComparison.Ordinal);
    }
}
