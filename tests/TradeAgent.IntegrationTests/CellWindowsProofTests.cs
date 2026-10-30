using System.Runtime.Versioning;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE WINDOWS BENCHMARK WRITES THE WINDOWS PROOF, AND THE GATE READS IT (<c>U-cell-windows</c> item 3): the whole benchmark run through
/// the product's own path on real Windows — every row held, one proof row under <see cref="RouteA.WindowsProvider"/> bound to this
/// machine, build and boot — after which <see cref="CellProofStore.HoldsProof"/> is true (the activation gate's one host-wide fact) and
/// <see cref="RouteA.Admission"/> accepts a Windows cell launch. A row that did not hold is a FAIL naming it, never a skip on Windows.
/// </summary>
[Trait("Category", "Cell"), Trait("CellScope", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class CellWindowsProofTests
{
    readonly ITestOutputHelper _out;

    public CellWindowsProofTests(ITestOutputHelper output) => _out = output;

    [WindowsFact]
    public async Task Every_row_holds_the_proof_is_written_and_the_gate_then_admits_a_windows_cell()
    {
        using var db = TestEnv.NewDb();
        // THE BUILD BINDING, as an install records it: the SHA-256 of the trade command it deployed (state/trade.sha256). The benchmark
        // and the gate both read it, so a proof is bound to this build; without it no proof is written at all.
        Directory.CreateDirectory(Paths.State);
        File.WriteAllText(Path.Combine(Paths.State, "trade.sha256"), new string('a', 64));
        Assert.False(new CellProofStore(db).HoldsProof(), "a fresh home holds no proof");

        var codex = PinnedWindowsCodex();   // the pinned windows codex, or the row that needs it FAILS
        var result = await WindowsCellBenchmark.RunAsync(db, codex, Pin, Launcher());
        _out.WriteLine(result.Sentence);
        foreach (var r in result.Rows) _out.WriteLine($"  {(r.Held ? "held" : "FAIL")}{(r.Gap ? " (GAP)" : "")}  {r.Name}: {r.Seen}");

        Assert.Equal(CellBenchmark.WindowsRowNames, [.. result.Rows.Select(r => r.Name)]);
        var failed = result.Rows.Where(r => !r.Held).ToList();
        Assert.True(failed.Count == 0, "rows that did not hold: " + string.Join("; ", failed.Select(r => $"{r.Name} ({r.Seen})")));
        Assert.NotNull(result.ProofId);

        // THE GATE'S FACT IS NOW TRUE, and the launch of a Windows cell is admitted under the proof just written.
        Assert.True(new CellProofStore(db).HoldsProof(), "the gate sees no proof after the benchmark wrote one");
        var (proof, egress, refusal) = RouteA.AdmissionOn(new CellRoute(db), Pin, CellPlatform.Read(), RouteA.Egress);
        Assert.Null(refusal);
        Assert.NotNull(proof);
        Assert.Equal(RouteA.WindowsProvider, proof!.Provider);
        _out.WriteLine($"admission: proof {proof.Id} ({proof.Provider}), the cell would run egress {egress.Name}");
    }

    static string Pin => RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex").Install.ExecutableSha256!;

    /// <summary>TA_CELL_CODEX from CI's step, else the release archive this build pins, downloaded and held to the archive's and the program's SHA-256.</summary>
    static string PinnedWindowsCodex()
    {
        var plan = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex").Install;
        var path = Environment.GetEnvironmentVariable("TA_CELL_CODEX");
        if (string.IsNullOrEmpty(path))
        {
            var dir = Path.Combine(TestEnv.Root, "codex-" + plan.Version + "-windows");
            path = Path.Combine(dir, plan.ExecutableInArchive!.Replace('/', '\\'));
            if (!File.Exists(path))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var bytes = http.GetByteArrayAsync(plan.Url).GetAwaiter().GetResult();
                var got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                Assert.True(got == plan.Sha256, $"the pinned codex archive {plan.Url} hashes to {got}, not this build's pin {plan.Sha256}");
                Directory.CreateDirectory(dir);
                using var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                System.Formats.Tar.TarFile.ExtractToDirectory(gz, dir, overwriteFiles: true);
            }
        }
        Assert.True(File.Exists(path), $"the pinned windows codex (TA_CELL_CODEX or the pinned archive; got '{path}')");
        using var f = File.OpenRead(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant();
        Assert.True(hash == plan.ExecutableSha256, $"{path} hashes to {hash}, not this build's codex pin {plan.ExecutableSha256}");
        return path;
    }

    static IReadOnlyList<string> Launcher()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) host = Environment.ProcessPath!;
        return [host, Build.TradeCliDll];
    }
}
