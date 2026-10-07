using System.Globalization;
using System.Reflection;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
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
    static readonly DateTimeOffset Inside = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    const string Premium = TapeSourceCatalog.Premium;
    const string PremiumSeries = "premium-index";

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

    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    static string Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    static TapeFetch Fetch(string source, string series, DateTimeOffset receivedAt) => new()
    {
        Source = source,
        Series = series,
        Url = $"http://127.0.0.1:9/{source}/{series}",
        RequestedAt = receivedAt.AddMilliseconds(-300),
        ReceivedAt = receivedAt,
        HttpStatus = 200
    };

    /// <summary>A premium-index reading as Binance serves it — the mark price is the point.</summary>
    static TapeItem PremiumPoint(DateTimeOffset at, string symbol = "BTCUSDT", string mark = "85495.82186232") => new(symbol, at,
        $$"""{"symbol":"{{symbol}}","markPrice":"{{mark}}","indexPrice":"85534.78173913","lastFundingRate":"0.00001937","time":{{Ms(at)}}}""");

    static TapeItem NewsItem(DateTimeOffset label, int n) => new($"{label:yyyyMMddHHmmss}-{n}", label,
        $$"""{"GKGRECORDID":"{{label:yyyyMMddHHmmss}}-{{n}}","V2.1DATE":"{{label:yyyyMMddHHmmss}}","title":"Bitcoin in the held-back month"}""");

    /// <summary>The premium index's stamps: two before the window, four inside it (its first and last minute among them), two after.</summary>
    static readonly DateTimeOffset[] Stamps =
        [Cutoff.AddMinutes(-2), Cutoff.AddMinutes(-1), Cutoff, Inside, Inside.AddMinutes(1), LastBar, End, End.AddMinutes(1)];

    /// <summary>The tape the tests read: BTCUSDT's and ETHUSDT's premium index at every stamp, each arriving a second late, and a GDELT item inside the window.</summary>
    static void Record(TapeStore store)
    {
        foreach (var at in Stamps)
            store.Append(Fetch(Premium, PremiumSeries, at.AddSeconds(1)), [PremiumPoint(at), PremiumPoint(at, "ETHUSDT", "3100.5")]);
        store.Append(Fetch(GdeltGkg.Source, GdeltGkg.ItemsSeries, Inside.AddMinutes(1)), [NewsItem(Inside, 0)]);
    }

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    /// <summary>A gateway whose tape holds <see cref="Record"/>'s rows and whose ledger holds <see cref="HeldBack"/>.</summary>
    sealed record Rig(TradingGateway Gw, Database Db, TapeStore Store, GatewayPipeServer Server, DatasetRecord Set) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Store.Dispose();
            Db.Dispose();
        }

        /// <summary><c>data-tape</c> as <paramref name="role"/> asks it — through the same handler the pipe reaches.</summary>
        public Task<IpcResponse> TapeAsync(string? role, Dictionary<string, JsonElement> args) =>
            Server.CallAsync(new IpcRequest { Op = Ops.DataTape, Session = "agent-tape", Args = args }, role, "attempt-tape-holdout");
    }

    static async Task<Rig> Ready()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var store = new TapeStore(NewFile());
        Record(store);
        gw.Tape = new TapeReader(store.File);
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-tape-holdout-" + Guid.NewGuid().ToString("n")[..12]);
        return new Rig(gw, db, store, server, HeldBack(new DatasetStore(db)));
    }

    static List<DateTimeOffset> SourceTimes(IpcResponse reply)
    {
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var data = JsonSerializer.SerializeToElement(reply.Data, Json.Options);
        return [.. data.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("source_time").GetDateTimeOffset())];
    }

    string Said(string? role, Dictionary<string, JsonElement> args, IpcResponse reply)
    {
        var line = $"{role ?? "no role"} {string.Join(" ", args.Select(a => $"{a.Key}={a.Value}"))}: "
                   + (reply.Ok ? $"served {SourceTimes(reply).Count} rows" : $"{reply.Error!.Code}: {reply.Error.Message}");
        log.WriteLine(line);
        return line;
    }

    /// <summary>
    /// (a) A TAPE READ REACHING A HOLDOUT WINDOW IS REFUSED FOR EVERY ROLE — and for a caller that proved none, through
    /// the handler the pipe reaches. The dataset is BTCUSDT's; the read is refused whatever it asks: BTCUSDT's premium
    /// index from inside the window with no end, ANOTHER symbol's row at one instant inside it, a GDELT news item, and the
    /// agent's default call with no window at all. Refused with <c>HOLDOUT_WITHHELD</c> naming the dataset, its cutoff and
    /// the window — and nothing of a row in the answer. RED on <c>6ca2a54a</c>: the first ask served eight rows to the
    /// Operations Director.
    /// </summary>
    [Fact]
    public async Task A_tape_read_reaching_a_holdout_window_is_refused_for_every_role()
    {
        await using var rig = await Ready();

        foreach (var role in EveryCaller)
            foreach (var args in new[]
                     {
                         Args(("source", Premium), ("series", PremiumSeries), ("from", Iso(Inside))),
                         Args(("source", Premium), ("series", PremiumSeries), ("subject", "ETHUSDT"), ("from", Iso(Inside)), ("to", Iso(Inside))),
                         Args(("source", GdeltGkg.Source), ("series", GdeltGkg.ItemsSeries), ("from", Iso(Inside.AddMinutes(-5))), ("to", Iso(Inside.AddMinutes(5)))),
                         Args(("source", Premium), ("series", PremiumSeries))
                     })
            {
                var reply = await rig.TapeAsync(role, args);
                Said(role, args, reply);

                Assert.False(reply.Ok, $"a tape read reaching dataset {rig.Set.Id}'s holdout window was served to {role ?? "a caller with no role"}");
                Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), reply.Error!.Code);
                Assert.Contains($"dataset {rig.Set.Id} (BTCUSDT 1m v1) holds out every bar from {Cutoff:u}", reply.Error.Message, StringComparison.Ordinal);
                Assert.Contains($"up to the close of its last bar at {End:u}", reply.Error.Message, StringComparison.Ordinal);
                Assert.Contains("REFUSED rather than quietly cut short", reply.Error.Message, StringComparison.Ordinal);
                Assert.Contains(role is null ? "a caller that proved no role" : CouncilRoles.Title(role), reply.Error.Message, StringComparison.Ordinal);

                var wire = Json.Write(reply);
                Assert.DoesNotContain("markPrice", wire, StringComparison.Ordinal);
                Assert.DoesNotContain("held-back month", wire, StringComparison.Ordinal);
            }
    }

    /// <summary>
    /// (b) A TAPE READ ENDING BEFORE EVERY CUTOFF IS SERVED — in full, every row stamped before it — and so is one that
    /// lies between two windows; the cutoff's own instant is already held, and a read reaching the second window is
    /// refused naming that one. Every role and a caller with none.
    /// </summary>
    [Fact]
    public async Task A_tape_read_ending_before_every_cutoff_is_served()
    {
        await using var rig = await Ready();
        var later = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var second = Dataset(new DatasetStore(rig.Db), "ETHUSDT", FirstBar, later.AddDays(10), later);

        foreach (var role in EveryCaller)
        {
            var before = Args(("source", Premium), ("series", PremiumSeries), ("from", Iso(Cutoff.AddMinutes(-2))), ("to", Iso(Cutoff.AddMilliseconds(-1))));
            var served = await rig.TapeAsync(role, before);
            Said(role, before, served);
            Assert.Equal([Cutoff.AddMinutes(-1), Cutoff.AddMinutes(-1), Cutoff.AddMinutes(-2), Cutoff.AddMinutes(-2)], SourceTimes(served));

            var between = Args(("source", Premium), ("series", PremiumSeries), ("from", Iso(End)), ("to", Iso(later.AddMilliseconds(-1))));
            var gap = await rig.TapeAsync(role, between);
            Said(role, between, gap);
            Assert.Equal([End.AddMinutes(1), End.AddMinutes(1), End, End], SourceTimes(gap));

            var atCutoff = Args(("source", Premium), ("series", PremiumSeries), ("from", Iso(Cutoff.AddMinutes(-2))), ("to", Iso(Cutoff)));
            var held = await rig.TapeAsync(role, atCutoff);
            Said(role, atCutoff, held);
            Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), held.Error?.Code);

            var reaching = Args(("source", Premium), ("series", PremiumSeries), ("from", Iso(End)), ("to", Iso(later)));
            var refused = await rig.TapeAsync(role, reaching);
            Said(role, reaching, refused);
            Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), refused.Error?.Code);
            Assert.Contains($"dataset {second.Id} (ETHUSDT 1m v1)", refused.Error!.Message, StringComparison.Ordinal);
            Assert.DoesNotContain($"dataset {rig.Set.Id} ", refused.Error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// (c) AN UNBOUNDED READ THAT REACHES A HOLDOUT IS REFUSED, NOT CLIPPED: no bound at all is the whole tape, an absent
    /// end reaches every window after its start and an absent start every window before its end — each refused with no
    /// row, though rows outside the window match it. An open end that starts at the window's close reaches nothing and is
    /// served whole, and so is an open start that ends before the cutoff.
    /// </summary>
    [Fact]
    public void An_unbounded_read_that_reaches_a_holdout_is_refused_not_clipped()
    {
        using var db = TestEnv.NewDb();
        var datasets = new DatasetStore(db);
        HeldBack(datasets);
        using var store = new TapeStore(NewFile());
        Record(store);
        var reader = new TapeReader(store.File);
        var research = TapeHoldout.Pipe(CouncilRoles.Research, datasets);

        TapeWindow Read(DateTimeOffset? from, DateTimeOffset? to) =>
            reader.Window(research, new TapeQuery { Source = Premium, Series = PremiumSeries, From = from, To = to });

        foreach (var (from, to, asked) in new (DateTimeOffset?, DateTimeOffset?, string)[]
                 {
                     (null, null, "names neither a start nor an end, so it is the whole tape"),
                     (Cutoff.AddMinutes(-2), null, $"starts at {Cutoff.AddMinutes(-2):u} and names no end"),
                     (null, Cutoff, $"ends at {Cutoff:u} and names no start"),
                     (null, End.AddMinutes(1), $"ends at {End.AddMinutes(1):u} and names no start"),
                     (LastBar, null, $"starts at {LastBar:u} and names no end")
                 })
        {
            var refused = Read(from, to);
            log.WriteLine(refused.Refusal);
            Assert.Empty(refused.Rows);
            Assert.False(refused.More);
            Assert.Null(refused.CappedBy);
            Assert.Contains(asked, refused.Refusal, StringComparison.Ordinal);
            Assert.Contains($"'to' earlier than {Cutoff:u}, or 'from' at or after {End:u}", refused.Refusal, StringComparison.Ordinal);
        }

        var after = Read(End, null);
        Assert.Null(after.Refusal);
        Assert.Equal([End.AddMinutes(1), End.AddMinutes(1), End, End], after.Rows.Select(r => r.SourceTime));

        var before = Read(null, Cutoff.AddTicks(-1));
        Assert.Null(before.Refusal);
        Assert.Equal([Cutoff.AddMinutes(-1), Cutoff.AddMinutes(-1), Cutoff.AddMinutes(-2), Cutoff.AddMinutes(-2)], before.Rows.Select(r => r.SourceTime));
    }

    /// <summary>
    /// (d) AN AS-OF READ THAT WOULD SERVE A ROW INSIDE A WINDOW IS REFUSED — not answered with the newest reading before
    /// the window, which would be another answer. What had arrived before the window opened is served; so is what
    /// arrived after it closed; and ARRIVAL IS NOT THE TEST: a revision that arrived while the window's market time ran,
    /// of a reading stamped before it, is served. RED on <c>6ca2a54a</c>: the store served the Operations Director the
    /// reading stamped 12:01 on 15 August.
    /// </summary>
    [Fact]
    public void An_as_of_read_that_would_serve_a_row_inside_a_window_is_refused()
    {
        using var db = TestEnv.NewDb();
        var datasets = new DatasetStore(db);
        var set = HeldBack(datasets);
        using var store = new TapeStore(NewFile());
        Record(store);
        var stamped = Cutoff.AddMinutes(-1);
        store.Append(Fetch(Premium, PremiumSeries, stamped.AddSeconds(1)), [PremiumPoint(stamped, "SOLUSDT", "150.1")]);
        store.Append(Fetch(Premium, PremiumSeries, Inside), [PremiumPoint(stamped, "SOLUSDT", "150.2")]);

        foreach (var role in EveryCaller)
        {
            var holdout = TapeHoldout.Pipe(role, datasets);

            var inside = store.AsOf(holdout, Premium, PremiumSeries, "BTCUSDT", Inside.AddMinutes(5));
            log.WriteLine($"{role ?? "no role"}: {inside.Refusal}");
            Assert.Null(inside.Row);
            Assert.Contains($"dataset {set.Id} (BTCUSDT 1m v1) holds out every bar from {Cutoff:u}", inside.Refusal, StringComparison.Ordinal);
            Assert.Contains($"that had arrived by {Inside.AddMinutes(5):u} is stamped inside it", inside.Refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("85495", inside.Refusal, StringComparison.Ordinal);

            var before = store.AsOf(holdout, Premium, PremiumSeries, "BTCUSDT", Cutoff.AddSeconds(-30));
            Assert.Null(before.Refusal);
            Assert.Equal(Cutoff.AddMinutes(-1), before.Row!.SourceTime);

            var after = store.AsOf(holdout, Premium, PremiumSeries, "BTCUSDT", End.AddMinutes(5));
            Assert.Null(after.Refusal);
            Assert.Equal(End.AddMinutes(1), after.Row!.SourceTime);

            var late = store.AsOf(holdout, Premium, PremiumSeries, "SOLUSDT", Inside.AddHours(1));
            Assert.Null(late.Refusal);
            Assert.Equal((stamped, 2, Inside), (late.Row!.SourceTime, late.Row.Revision, late.Row.ReceivedAt));

            Assert.Equal(new TapeAsOf(null, null), store.AsOf(holdout, Premium, PremiumSeries, "BTCUSDT", FirstBar));
        }
    }

    /// <summary>
    /// (e) THE REFEREE READS THE TAPE INSIDE ITS HOLDOUT, in process, through the one pass-through — which this assembly
    /// can reach only by reflection, because it is <c>internal</c> to <c>TradeAgent.Core</c>: exactly the boundary that
    /// keeps the gateway, the pipe server and the CLI from it. The same reads, made as a director, are refused.
    /// </summary>
    [Fact]
    public void The_referee_reads_the_tape_inside_its_holdout()
    {
        using var db = TestEnv.NewDb();
        var datasets = new DatasetStore(db);
        HeldBack(datasets);
        using var store = new TapeStore(NewFile());
        Record(store);
        var reader = new TapeReader(store.File);

        var referee = (TapeHoldout)typeof(TapeHoldout).GetProperty("Referee", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.True(referee.MayReadHoldout);
        Assert.Equal("the referee", referee.Who);
        Assert.Empty(referee.Windows());

        var window = new TapeQuery { Source = Premium, Series = PremiumSeries, From = Cutoff, To = LastBar };
        var refused = reader.Window(TapeHoldout.Pipe(CouncilRoles.Research, datasets), window);
        Assert.NotNull(refused.Refusal);
        Assert.Empty(refused.Rows);

        var judged = reader.Window(referee, window);
        Assert.Null(judged.Refusal);
        Assert.Equal([LastBar, LastBar, Inside.AddMinutes(1), Inside.AddMinutes(1), Inside, Inside, Cutoff, Cutoff],
            judged.Rows.Select(r => r.SourceTime));
        Assert.All(judged.Rows, r => Assert.Contains("markPrice", r.Payload, StringComparison.Ordinal));

        var asOf = store.AsOf(referee, Premium, PremiumSeries, "BTCUSDT", Inside.AddMinutes(5));
        Assert.Null(asOf.Refusal);
        Assert.Equal(Inside.AddMinutes(1), asOf.Row!.SourceTime);
        Assert.NotNull(store.AsOf(TapeHoldout.Pipe(null, datasets), Premium, PremiumSeries, "BTCUSDT", Inside.AddMinutes(5)).Refusal);
    }

    /// <summary>
    /// (g) NO TAPE READER CAN BE CALLED WITHOUT ITS HOLDOUT: every public read of <see cref="TapeReader"/> and
    /// <see cref="TapeStore"/> that serves a row — anything carrying a <see cref="TapeObservation"/> — takes a
    /// <see cref="TapeHoldout"/>, as <c>HoldoutLedgerTests</c> holds the bars' doors. The two that do not are named here,
    /// so one added later fails: <c>Revisions</c> and <c>ObservationsOf</c>, the recorders' in-process bookkeeping on the
    /// store, which the gateway never holds (it holds a reader). Every other read serves counts, names and instants. A
    /// caller that hands no holdout is refused before anything is read.
    /// </summary>
    [Fact]
    public void No_tape_reader_can_be_called_without_its_holdout()
    {
        var reads = new List<string>();
        var unguarded = new List<string>();
        foreach (var type in new[] { typeof(TapeReader), typeof(TapeStore) })
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => Carries(m.ReturnType, [])))
            {
                reads.Add($"{type.Name}.{m.Name}");
                if (!m.GetParameters().Any(p => p.ParameterType == typeof(TapeHoldout))) unguarded.Add($"{type.Name}.{m.Name}");
            }

        log.WriteLine("reads that serve a row: " + string.Join(", ", reads.Order(StringComparer.Ordinal)));
        Assert.Contains("TapeReader.Window", reads);
        Assert.Contains("TapeStore.AsOf", reads);
        Assert.Equal(["TapeStore.ObservationsOf", "TapeStore.Revisions"], unguarded.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(TapeStore), typeof(GatewayPipeServer).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(f => f.FieldType));

        using var db = TestEnv.NewDb();
        using var store = new TapeStore(NewFile());
        Record(store);
        var reader = new TapeReader(store.File);
        Assert.Throws<ArgumentNullException>(() => reader.Window(null!, new TapeQuery { Source = Premium, Series = PremiumSeries }));
        Assert.Throws<ArgumentNullException>(() => store.AsOf(null!, Premium, PremiumSeries, "BTCUSDT", End));
    }

    /// <summary>Whether a type carries a tape row: the row itself, a collection or nullable of one, or a record of this product holding one.</summary>
    static bool Carries(Type t, HashSet<Type> seen)
    {
        if (!seen.Add(t)) return false;
        if (t == typeof(TapeObservation)) return true;
        if (Nullable.GetUnderlyingType(t) is { } inner) return Carries(inner, seen);
        if (t.IsArray) return Carries(t.GetElementType()!, seen);
        if (t.IsGenericType && t.GetGenericArguments().Any(a => Carries(a, seen))) return true;
        return t.Namespace?.StartsWith("TradeAgent", StringComparison.Ordinal) == true
               && t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => Carries(p.PropertyType, seen));
    }

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
