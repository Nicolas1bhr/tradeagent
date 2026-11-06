using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-EVIDENCE-CAMPAIGN ITEM 3 — THE RESEARCH MONTHS OF A DATASET, AS A FILE IN THE CALLER'S OWN FOLDER (<c>data-export</c>).
///
/// <para>Never a held-back bar (the reader's own refusal, with nothing written), bounded in bars, in files and in what the ledger says the
/// position was written, written into the caller's home through the no-follow writer so a link planted at the name leads nowhere, and recorded as a read of that position in the
/// observed-deliveries ledger.</para>
/// </summary>
public class DataExportTests
{
    static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Cutoff = Start.AddMinutes(100);

    sealed record Rig(TradingGateway Gw, Database Db, GatewayPipeServer Server, DatasetRecord Set) : IAsyncDisposable
    {
        public Task<IpcResponse> ExportAsync(params (string Key, string Value)[] pairs)
        {
            var args = new Dictionary<string, JsonElement>();
            foreach (var (key, value) in pairs) args[key] = JsonSerializer.SerializeToElement(value);
            return Server.CallAsync(new IpcRequest { Op = Ops.DataExport, Session = "agent-export", Args = args },
                CouncilRoles.Research, "attempt-export");
        }

        public string Home => Paths.RoleHome(CouncilRoles.Research);

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>A gateway, the research director's folder, and a dataset of <paramref name="bars"/> minutes held back from <see cref="Cutoff"/>.</summary>
    static async Task<Rig> Ready(int bars = 120)
    {
        var (gw, _, db) = await TestEnv.Ready();
        var set = Recorded(db, TestEnv.NewPair(), bars);
        Assert.True(gw.Datasets.SetHoldout(set.Id, Cutoff, EvaluationClass.Research).Ok);
        // A CLEAN EXPORT FOLDER: the director's home is the process's, and every test here counts the files in it.
        TestEnv.DeleteQuietly(Path.Combine(Paths.RoleHome(CouncilRoles.Research), DataExports.Folder));
        Directory.CreateDirectory(Paths.RoleHome(CouncilRoles.Research));
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-export-" + Guid.NewGuid().ToString("n")[..12]);
        return new Rig(gw, db, server, gw.Datasets.ById(set.Id)!);
    }

    static DatasetRecord Recorded(Database db, string pair, int bars)
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");
        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
            text.Append(CultureInfo.InvariantCulture, $"{Start.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},100,101,99,100,1.00\n");
        File.WriteAllText(csv, text.ToString());

        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, [],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start.AddMinutes(bars - 1), 0, [], false, 0, 0, 0, Start,
            DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-03", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!, DatasetStore.Sha256(raw)!,
                new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]));
        return store.ById(id)!;
    }

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static IReadOnlyList<ToolCallRow> Exports(Database db) =>
        [.. new ToolCallStore(db).ForAttempt("attempt-export").Where(r => r.Tool == Ops.DataExport)];

    /// <summary>
    /// THE MONTHS BEFORE THE CUTOFF ARE WRITTEN, EXACTLY, INTO THE CALLER'S FOLDER, AND RECORDED AS ITS READ; A WINDOW REACHING THE CUTOFF
    /// IS REFUSED AS HELD BACK AND WRITES NOTHING.
    /// </summary>
    [Fact]
    public async Task The_research_months_are_written_and_recorded_and_a_window_reaching_the_holdout_writes_nothing()
    {
        await using var rig = await Ready();

        var held = await rig.ExportAsync(("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture)), ("to", Iso(Cutoff)));
        Assert.False(held.Ok);
        Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), held.Error!.Code);
        Assert.Contains("Nothing was written", held.Error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(rig.Home, DataExports.Folder))
                     && Directory.EnumerateFiles(Path.Combine(rig.Home, DataExports.Folder)).Any());
        Assert.Empty(Exports(rig.Db));

        var done = await rig.ExportAsync(("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture)),
            ("from", Iso(Start.AddMinutes(10))), ("to", Iso(Cutoff.AddMinutes(-1))));
        Assert.True(done.Ok, done.Error?.Message);
        var path = JsonSerializer.SerializeToElement(done.Data, Json.Options).GetProperty("path").GetString()!;
        var lines = File.ReadAllLines(Path.Combine(rig.Home, path));
        Assert.Equal(KlineNormaliser.HeaderWithQuality, lines[0]);
        Assert.Equal(90, lines.Length - 1);
        Assert.StartsWith(Iso(Start.AddMinutes(10)), lines[1], StringComparison.Ordinal);
        Assert.StartsWith(Iso(Cutoff.AddMinutes(-1)), lines[^1], StringComparison.Ordinal);

        var row = Assert.Single(Exports(rig.Db));
        Assert.Equal(CouncilRoles.Research, row.Role);
        Assert.Contains(path, row.Argument, StringComparison.Ordinal);
        Assert.Equal(new FileInfo(Path.Combine(rig.Home, path)).Length, row.Bytes);

        // A WINDOW ASKED IN THE SAME MINUTE BUT HOLDING OTHER BARS IS ANOTHER FILE (the Codex review's P2), and the first is untouched.
        var later = await rig.ExportAsync(("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture)),
            ("from", Iso(Start.AddMinutes(10).AddSeconds(30))), ("to", Iso(Cutoff.AddMinutes(-1))));
        Assert.True(later.Ok, later.Error?.Message);
        var second = JsonSerializer.SerializeToElement(later.Data, Json.Options).GetProperty("path").GetString()!;
        Assert.NotEqual(path, second);
        Assert.Equal(91, File.ReadAllLines(Path.Combine(rig.Home, path)).Length);
    }

    /// <summary>A FULL FOLDER IS REFUSED NAMING ITS EXPORTS AND WRITES NOTHING; THE SAME WINDOW AGAIN REWRITES ITS OWN FILE.</summary>
    [Fact]
    public async Task An_export_folder_is_bounded_in_files()
    {
        await using var rig = await Ready();
        var dataset = ("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture));

        for (var i = 0; i < DataExports.MaxFiles; i++)
            Assert.True((await rig.ExportAsync(dataset, ("from", Iso(Start.AddMinutes(i))), ("to", Iso(Cutoff.AddMinutes(-1))))).Ok);
        var full = await rig.ExportAsync(dataset, ("from", Iso(Start.AddMinutes(50))), ("to", Iso(Cutoff.AddMinutes(-1))));
        Assert.False(full.Ok);
        Assert.Contains($"already holds {DataExports.MaxFiles} exports", full.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(DataExports.MaxFiles, Exports(rig.Db).Count);
        Assert.Equal(DataExports.MaxFiles, Directory.EnumerateFiles(Path.Combine(rig.Home, DataExports.Folder)).Count());

        Assert.True((await rig.ExportAsync(dataset, ("from", Iso(Start)), ("to", Iso(Cutoff.AddMinutes(-1))))).Ok);
    }

    /// <summary>OVER <see cref="DataExports.MaxBars"/> BARS IS REFUSED NAMING THE BOUND, WITH NOTHING WRITTEN.</summary>
    [Fact]
    public async Task A_window_over_the_bar_cap_is_refused_naming_it()
    {
        await using var rig = await Ready();
        // AN UNCUT DATASET OF ANOTHER PAIR, read from after the held window's end so no holdout refuses it first.
        var uncut = Recorded(rig.Db, TestEnv.NewPair(), DataExports.MaxBars + 200);
        var over = await rig.ExportAsync(("dataset", uncut.Id.ToString(CultureInfo.InvariantCulture)),
            ("from", Iso(Start.AddMinutes(130))), ("to", Iso(Start.AddMinutes(DataExports.MaxBars + 150))));
        Assert.False(over.Ok);
        Assert.Contains($"more than {DataExports.MaxBars} bars", over.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(Exports(rig.Db));
    }

    /// <summary>
    /// A LINK PLANTED AT THE EXPORT'S NAME IS REPLACED, NEVER FOLLOWED: the file lands in the caller's folder and what the link pointed at
    /// is untouched — the gateway writes into a folder an executor's cell can write.
    /// </summary>
    [Fact]
    public async Task A_link_planted_at_the_name_is_never_followed()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var rig = await Ready();
        var to = Cutoff.AddMinutes(-1);
        var relative = DataExports.FileName(rig.Set, Start, to);
        Directory.CreateDirectory(Path.Combine(rig.Home, DataExports.Folder));
        var victim = Path.Combine(TestEnv.Home, $"victim-{Guid.NewGuid():n}.txt");
        File.WriteAllText(victim, "not the gateway's to write");
        File.CreateSymbolicLink(Path.Combine(rig.Home, relative), victim);

        var done = await rig.ExportAsync(("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture)), ("to", Iso(to)));
        Assert.True(done.Ok, done.Error?.Message);
        Assert.Equal("not the gateway's to write", File.ReadAllText(victim));
        Assert.Null(new FileInfo(Path.Combine(rig.Home, relative)).LinkTarget);
    }

    /// <summary>What the ledger says this position was already written, as an earlier export's row: the app's record, which a cell cannot touch.</summary>
    static void Charged(Rig rig, long bytes, string? role = null, DateTimeOffset? at = null, bool served = true) =>
        Assert.True(new ToolCallStore(rig.Db).Record(new ToolCallRow
        {
            At = at ?? DateTimeOffset.UtcNow,
            Attempt = "attempt-earlier",
            Role = role ?? CouncilRoles.Research,
            Tool = Ops.DataExport,
            Argument = "an earlier export",
            Bytes = bytes,
            Served = served,
            Refusal = served ? null : "refused"
        }) > 0);

    /// <summary>
    /// F-B3 (R29): THE FOLDER'S COUNT READS NAMES, AND A NAME IS THE CELL'S TO CHANGE. Code in a cell moves each export out of the name the
    /// count reads and asks again; what bounds the position is the ledger of what it was written, so the same asks stop at the bound.
    /// </summary>
    [Fact]
    public async Task A_rename_does_not_escape_the_bound_on_what_a_position_is_written()
    {
        await using var rig = await Ready();
        var dataset = ("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture));
        var window = ("to", Iso(Cutoff.AddMinutes(-1)));
        var folder = Path.Combine(rig.Home, DataExports.Folder);
        var kept = Path.Combine(folder, "kept");
        Directory.CreateDirectory(kept);

        Assert.True((await rig.ExportAsync(dataset, window)).Ok);
        var size = Exports(rig.Db).Single().Bytes;
        // ROOM FOR EXACTLY TWO MORE OF THIS SIZE: the bound is in megabytes and this dataset's files are in kilobytes.
        Charged(rig, DataExports.MaxBytesPerDay - (3 * size));

        var written = 1;
        IpcResponse last = null!;
        for (var i = 0; i < 10; i++)
        {
            foreach (var file in Directory.EnumerateFiles(folder, DataExports.Prefix + "*"))
                File.Move(file, Path.Combine(kept, $"renamed-{i}-{Path.GetFileName(file)}"));
            last = await rig.ExportAsync(dataset, window);
            if (last.Ok) written++;
        }

        Assert.Equal(3, written);
        Assert.False(last.Ok);
        Assert.Contains("the most one position is written", last.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(3, Exports(rig.Db).Count);
        Assert.Empty(Directory.EnumerateFiles(folder, DataExports.Prefix + "*"));
    }

    /// <summary>THE BOUND IS THIS POSITION'S OWN LAST DAY: another position's bytes, a day-old row and a refused ask are not what it was written.</summary>
    [Fact]
    public async Task The_bound_counts_only_what_this_position_was_written_in_the_last_day()
    {
        await using var rig = await Ready();
        var dataset = ("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture));
        var window = ("to", Iso(Cutoff.AddMinutes(-1)));

        Charged(rig, DataExports.MaxBytesPerDay, role: CouncilRoles.Operations);
        Charged(rig, DataExports.MaxBytesPerDay, at: DateTimeOffset.UtcNow - DataExports.BoundWindow - TimeSpan.FromMinutes(1));
        Charged(rig, DataExports.MaxBytesPerDay, served: false);
        var served = await rig.ExportAsync(dataset, window);
        Assert.True(served.Ok, served.Error?.Message);

        Charged(rig, DataExports.MaxBytesPerDay);
        var refused = await rig.ExportAsync(dataset, window);
        Assert.False(refused.Ok);
        Assert.Contains("Nothing was written", refused.Error!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE COUNT, THE WRITE AND THE RECORD UNDER ONE GATE: the record is what the next ask counts, so two asks at once must not both see the
    /// same room, each in a window of its own so no two share a file. <b>And a refusal lets go of the gate:</b> a lock in the method whose filter
    /// reads <c>is A or B</c> never does in a Release build (<see cref="GatewayPipeServer"/>'s <c>PlaceExport</c>), so the first refusal left
    /// every other ask here waiting for good. The wait is bounded so that shows as a failure and not as a suite that never ends.
    /// </summary>
    [Fact]
    public async Task Asks_at_the_same_moment_do_not_share_the_room_left_in_the_bound()
    {
        await using var rig = await Ready();
        var dataset = ("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture));
        var to = ("to", Iso(Cutoff.AddMinutes(-1)));

        Assert.True((await rig.ExportAsync(dataset, to)).Ok);
        var size = Exports(rig.Db).Single().Bytes;
        TestEnv.DeleteQuietly(Path.Combine(rig.Home, DataExports.Folder));
        // ROOM FOR ONE AND A HALF: every window below is at most one bar shorter per step, so one fits and no second does.
        Charged(rig, DataExports.MaxBytesPerDay - size - (size * 3 / 2));

        var answers = await Task.WhenAll(Enumerable.Range(0, 2 * DataExports.MaxFiles).Select(i =>
            Task.Run(() => rig.ExportAsync(dataset, ("from", Iso(Start.AddMinutes(i))), to)))).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(answers, a => a.Ok);
        Assert.True(new ToolCallStore(rig.Db).BytesServed(CouncilRoles.Research, Ops.DataExport, Start) <= DataExports.MaxBytesPerDay);
    }

    /// <summary>
    /// A WRITE THAT FAILED STAYS CHARGED AND SAYS SO: the charge comes first, because a file that stands is the cell's to rename, and a failed write
    /// may have reached the disk in part. A second row records that the file did not arrive.
    /// </summary>
    [Fact]
    public async Task A_write_that_failed_stays_charged_and_the_ledger_says_it_failed()
    {
        await using var rig = await Ready();
        var to = Cutoff.AddMinutes(-1);
        // A FOLDER AT THE EXPORT'S NAME: what the write's unlink refuses, on every platform.
        Directory.CreateDirectory(Path.Combine(rig.Home, DataExports.FileName(rig.Set, Start, to)));

        var refused = await rig.ExportAsync(("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture)), ("to", Iso(to)));

        Assert.False(refused.Ok);
        var rows = Exports(rig.Db);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Served);
        Assert.True(rows[0].Bytes > 0);
        Assert.False(rows[1].Served);
        Assert.Equal(rows[0].Bytes, new ToolCallStore(rig.Db).BytesServed(CouncilRoles.Research, Ops.DataExport, Start));
    }

    /// <summary>
    /// AN EXPORT THE LEDGER COULD NOT RECORD IS NOT WRITTEN: that row is the read this position is charged with and what the bound counts, so a
    /// file standing without one would be a read nobody can see and bytes the bound never counted.
    /// </summary>
    [Fact]
    public async Task An_export_the_ledger_could_not_record_leaves_no_file()
    {
        await using var rig = await Ready();
        rig.Db.Write(_ =>
        {
            using var c = rig.Db.Cmd("CREATE TEMP TRIGGER no_record BEFORE INSERT ON tool_call BEGIN SELECT RAISE(ABORT, 'the ledger refuses'); END");
            return c.ExecuteNonQuery();
        });

        var refused = await rig.ExportAsync(("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture)), ("to", Iso(Cutoff.AddMinutes(-1))));

        Assert.False(refused.Ok);
        Assert.Contains("Nothing was written", refused.Error!.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(rig.Home, DataExports.Folder)));
    }
}
