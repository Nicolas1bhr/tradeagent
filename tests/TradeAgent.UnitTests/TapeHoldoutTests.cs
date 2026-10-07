using System.Reflection;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TAPE'S HOLDOUT (<c>U-tape-holdout</c>; the orchestrator's ruling of 2026-10-07 12:02): every dataset holding a
/// cutoff holds a window of the tape — its cutoff to the close of its last bar — and no caller on the agent-facing pipe
/// reads a row stamped inside it, whatever the source, series or subject; the referee still does.
///
/// <para><b>What was wrong before this.</b> <c>U-tape-read</c> made the tape readable by every role, and a premium-index
/// row carries the mark price: once a holdout overlapped what the tape recorded, <c>trade data tape</c> handed any role
/// prices from inside the window the bars' holdout withholds. (a) and (d) were RED on <c>6ca2a54a</c>: rows served.</para>
/// </summary>
public class TapeHoldoutTests(ITestOutputHelper log)
{
    // THE HOLDOUT THE TESTS READ AGAINST: bars from July 1st, August held back, the last bar opening at 23:59 on the
    // 31st — so the window on the tape runs from the cutoff up to the close of that bar, midnight on September 1st.
    static readonly DateTimeOffset FirstBar = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Cutoff = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset LastBar = new(2026, 8, 31, 23, 59, 0, TimeSpan.Zero);
    static readonly DateTimeOffset End = LastBar.AddMinutes(1);

    /// <summary>Every audience a pipe caller can be: each director's launch, and a connection that proved no role.</summary>
    static readonly string?[] EveryCaller = [.. CouncilRoles.All, null];

    /// <summary>A dataset of <paramref name="pair"/> with the owner's cutoff on it — or none — read back off the ledger.</summary>
    static DatasetRecord Dataset(DatasetStore store, string pair, DateTimeOffset first, DateTimeOffset last,
        DateTimeOffset? cutoff, string evaluationClass = EvaluationClass.Research, int bars = 1000)
    {
        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 2, 2, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), "aa11", bars,
            bars == 0 ? null : first, bars == 0 ? null : last, 0, [], false, 0, 0, 0, last.AddDays(1),
            DatasetState.ACCEPTED, null, []);
        var id = store.Record(record);
        if (cutoff is { } at) Assert.True(store.SetHoldout(id, at, evaluationClass).Ok);
        return store.ById(id)!;
    }

    /// <summary>The BTCUSDT dataset whose August is held back.</summary>
    static DatasetRecord HeldBack(DatasetStore store) => Dataset(store, "BTCUSDT", FirstBar, LastBar, Cutoff);

    /// <summary>
    /// (f) EVERY DATASET WITH A CUTOFF HOLDS ITS WINDOW, OPEN CAMPAIGN OR NOT — whatever its state or class, because the
    /// bars' rule reads the dataset and so does the tape's. A dataset with no cutoff holds nothing; one whose cutoff is
    /// after its last bar holds no market time; and a cutoff set after the reader was made counts at its next read.
    /// </summary>
    [Fact]
    public void Every_dataset_with_a_cutoff_holds_its_window_open_campaign_or_not()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);

        var campaigned = HeldBack(store);
        var opened = new CampaignStore(db).Open("tape-holdout", campaigned, 10, 2, LastBar.AddDays(2));
        Assert.True(opened.Ok, opened.Why);

        var bare = Dataset(store, "ETHUSDT", FirstBar, new DateTimeOffset(2026, 8, 20, 23, 59, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        var rejected = Dataset(store, "BNBUSDT", FirstBar, new DateTimeOffset(2026, 9, 9, 23, 59, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));
        store.Reject(rejected.Id, "a file this ledger measured changed on disk");
        var fixture = Dataset(store, "XRPUSDT", FirstBar, new DateTimeOffset(2026, 7, 20, 23, 59, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), EvaluationClass.Fixture);
        var open = Dataset(store, "SOLUSDT", FirstBar, LastBar, null);
        Dataset(store, "DOGEUSDT", FirstBar, new DateTimeOffset(2026, 7, 31, 23, 59, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));                               // its cutoff is after its last bar's close
        Dataset(store, "LTCUSDT", FirstBar, LastBar, Cutoff, bars: 0);                               // it holds no bar at all

        Assert.NotNull(new CampaignStore(db).OpenForDataset(campaigned.Id));
        Assert.Null(new CampaignStore(db).OpenForDataset(bare.Id));

        var holdout = TapeHoldout.Pipe(null, store);
        var windows = holdout.Windows();
        foreach (var w in windows) log.WriteLine(w.Words);

        TapeHoldoutWindow Expected(DatasetRecord set) =>
            new(set.Id, set.Pair, set.Interval, set.Version, set.HoldoutFrom!.Value, set.LastBar!.Value.AddMinutes(1));

        Assert.Equal([Expected(fixture), Expected(campaigned), Expected(bare), Expected(rejected)], windows);

        // EACH ONE REFUSES A READ THAT REACHES IT, the one with no campaign as surely as the one with.
        foreach (var set in new[] { campaigned, bare, rejected, fixture })
        {
            var refusal = holdout.Refusal(set.HoldoutFrom, set.HoldoutFrom);
            Assert.NotNull(refusal);
            Assert.Contains($"dataset {set.Id} ({set.Pair} 1m v1) holds out every bar from {set.HoldoutFrom:u}", refusal, StringComparison.Ordinal);
        }

        // A CUTOFF SET A SECOND AGO COUNTS: the holdout was made above, and reads the ledger again at each read.
        Assert.DoesNotContain(holdout.Windows(), w => w.DatasetId == open.Id);
        Assert.True(store.SetHoldout(open.Id, LastBar, EvaluationClass.Research).Ok);
        Assert.Contains(holdout.Windows(), w => w.DatasetId == open.Id);
        var now = holdout.Refusal(LastBar, LastBar);
        Assert.NotNull(now);
        Assert.Contains($"dataset {open.Id} (SOLUSDT 1m v1) holds out every bar from {LastBar:u}", now, StringComparison.Ordinal);
    }

    /// <summary>
    /// (h) NO PUBLIC DOOR IN <c>TradeAgent.Core</c> HANDS OUT A TAPE HOLDOUT THAT READS INSIDE A WINDOW — a whitelist by
    /// NAME, as <c>HoldoutLedgerTests</c> holds the bars' doors: <see cref="TapeHoldout.Pipe"/> is the only public member
    /// of any exported type that produces one, it never may, and it cannot be made without the ledger. The referee's is
    /// <c>internal</c>, and so is the audience inside, so it is not a second door onto a <see cref="BarAudience"/>.
    /// </summary>
    [Fact]
    public void No_public_door_in_core_hands_out_a_tape_holdout_that_reads_inside_a_window()
    {
        var doors = new List<string>();
        foreach (var type in typeof(TapeHoldout).Assembly.GetExportedTypes())
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                         .Where(m => m.ReturnType == typeof(TapeHoldout)))
                doors.Add($"{type.Name}.{m.Name}()");

            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                         .Where(f => f.FieldType == typeof(TapeHoldout)))
                doors.Add($"{type.Name}.{f.Name}");
        }

        Assert.Equal([$"{nameof(TapeHoldout)}.{nameof(TapeHoldout.Pipe)}()"], doors.Order(StringComparer.Ordinal));
        Assert.Empty(typeof(TapeHoldout).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(TapeHoldout).GetProperty("Audience", BindingFlags.Public | BindingFlags.Instance));
        Assert.Throws<ArgumentNullException>(() => TapeHoldout.Pipe(CouncilRoles.Research, null!));

        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        Assert.All(EveryCaller, role => Assert.False(TapeHoldout.Pipe(role, store).MayReadHoldout));
    }
}
