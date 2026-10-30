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
/// <para>Never a held-back bar (the reader's own refusal, with nothing written), bounded in bars and in files, written into the caller's
/// home through the no-follow writer so a link planted at the name leads nowhere, and recorded as a read of that position in the
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
        var relative = DataExports.FileName(rig.Set, null, to);
        Directory.CreateDirectory(Path.Combine(rig.Home, DataExports.Folder));
        var victim = Path.Combine(TestEnv.Home, $"victim-{Guid.NewGuid():n}.txt");
        File.WriteAllText(victim, "not the gateway's to write");
        File.CreateSymbolicLink(Path.Combine(rig.Home, relative), victim);

        var done = await rig.ExportAsync(("dataset", rig.Set.Id.ToString(CultureInfo.InvariantCulture)), ("to", Iso(to)));
        Assert.True(done.Ok, done.Error?.Message);
        Assert.Equal("not the gateway's to write", File.ReadAllText(victim));
        Assert.Null(new FileInfo(Path.Combine(rig.Home, relative)).LinkTarget);
    }
}
