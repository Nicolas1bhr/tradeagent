using System.Reflection;
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

    // ---- item 2: the rule, and the one door past it ----------------------------------------------

    /// <summary>
    /// THE RULE ITSELF, AND IT REFUSES BY DEFAULT: a window is served only when its END is PROVED to be
    /// before the cutoff.
    ///
    /// <para>An unbounded <c>to</c> is therefore refused, and that is the whole shape of the decision.
    /// Reading "no end" as "up to the cutoff" would be clipping with extra steps: the caller asked for
    /// every bar of the dataset, and an answer that silently stopped early is a different window from
    /// the one asked for with nothing in the reply to say so.</para>
    /// </summary>
    [Theory]
    // from, to, is it served
    [InlineData(0, 59, true)]        // ends the minute before the cutoff
    [InlineData(0, 60, false)]       // the cutoff bar itself is already private: INCLUSIVE
    [InlineData(0, 119, false)]
    [InlineData(60, 119, false)]
    [InlineData(0, -1, false)]       // -1 means no 'to' at all
    public void A_window_is_served_only_when_its_end_is_proved_to_be_before_the_cutoff(
        int fromMinute, int toMinute, bool served)
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var id = Given(db).Id;
        Assert.True(store.SetHoldout(id, At.AddMinutes(60), EvaluationClass.Research).Ok);
        var set = store.ById(id)!;

        var refusal = Holdout.Refusal(set, BarAudience.Pipe(CouncilRoles.Research),
            At.AddMinutes(fromMinute), toMinute < 0 ? null : At.AddMinutes(toMinute));

        Assert.Equal(served, refusal is null);
        if (!served) Assert.Contains("holds out every bar from", refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY PIPE AUDIENCE IS REFUSED EQUALLY, and a NULL role is not "not research" — it is a caller
    /// that proved nothing, which is the reading `U-containment` had to fix once already when it was
    /// being served as the chair.
    /// </summary>
    [Theory]
    [InlineData(CouncilRoles.Research)]
    [InlineData(CouncilRoles.Operations)]
    [InlineData(null)]
    [InlineData("some-role-a-newer-build-has")]
    public void No_pipe_audience_may_read_the_holdout_whatever_role_it_proved(string? role)
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var id = Given(db).Id;
        Assert.True(store.SetHoldout(id, At, EvaluationClass.Research).Ok);

        var audience = BarAudience.Pipe(role);

        Assert.False(audience.MayReadHoldout);
        Assert.NotNull(Holdout.Refusal(store.ById(id)!, audience, null, null));
    }

    /// <summary>
    /// A DATASET WITH NO CUTOFF SERVES EVERYTHING, which is what every installation that upgrades into
    /// this build has. The holdout is something the owner declares, never something a migration invents.
    /// </summary>
    [Fact]
    public void A_dataset_with_no_cutoff_serves_every_window()
    {
        using var db = TestEnv.NewDb();
        var set = new DatasetStore(db).ById(Given(db).Id)!;

        Assert.Null(Holdout.Refusal(set, BarAudience.Pipe(CouncilRoles.Research), null, null));
    }

    /// <summary>
    /// NO PUBLIC DOOR IN <c>TradeAgent.Core</c> HANDS OUT AN AUDIENCE THAT MAY READ A HOLDOUT — and this
    /// is a whitelist by NAME, so a door added later fails here rather than passing quietly.
    ///
    /// <para>This is the structural half of "the referee alone reads the holdout, in process". The wire
    /// sweep in <c>HoldoutOverPipeTests</c> proves that no op reachable today serves a held-back bar; it
    /// cannot prove that a future op could not. This can: the only audience that may read past a cutoff
    /// is <c>BarAudience.Referee</c>, which is <c>internal</c>, and the only public member of any
    /// exported Core type that produces a <see cref="BarAudience"/> at all is <c>Pipe</c>, which never
    /// may. The gateway, the pipe server, the CLI and this test assembly are all outside that boundary,
    /// so none of them can mint one — not by forgetting a check, but because the type is not there.</para>
    ///
    /// <para>Parameters are deliberately not part of the check: being HANDED an audience is how the
    /// readers work, and the thing that has to be scarce is the ability to MAKE one.</para>
    /// </summary>
    [Fact]
    public void No_public_door_in_core_hands_out_an_audience_that_may_read_the_holdout()
    {
        var doors = new List<string>();
        foreach (var type in typeof(BarAudience).Assembly.GetExportedTypes())
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                         .Where(m => m.ReturnType == typeof(BarAudience)))
                doors.Add($"{type.Name}.{m.Name}()");

            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                         .Where(f => f.FieldType == typeof(BarAudience)))
                doors.Add($"{type.Name}.{f.Name}");
        }

        // `Pipe` is the whole public surface, and the property getter its name implies is not a second
        // door: a property returning one would show up here as get_X and would have to be justified.
        Assert.Equal([$"{nameof(BarAudience)}.{nameof(BarAudience.Pipe)}()"], doors.Order(StringComparer.Ordinal));
        Assert.All(new[] { CouncilRoles.Research, CouncilRoles.Operations, null },
            role => Assert.False(BarAudience.Pipe(role).MayReadHoldout));
    }
}
