using System.Text.RegularExpressions;
using TradeAgent.Core.Data;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 5 — the suite talks to a loopback <see cref="System.Net.HttpListener"/> and never to the
/// vendor.
///
/// Every other test in this unit is written against <see cref="FakeArchive"/>. What makes that a
/// property of the SUITE rather than of the tests that happen to exist today is this scan, and the
/// hazard it closes is a real one: <see cref="BinanceArchiveClient"/> takes its base URL as an
/// OPTIONAL argument defaulting to <c>data.binance.vision</c>, so a test that forgets to pass the
/// loopback address does not fail — it quietly downloads two megabytes from a public CDN on every CI
/// run, on three platforms, and passes.
///
/// The one real download this unit was allowed is quoted in the brief's report. It was made once, by
/// hand, to establish the URL pattern and the sidecar's format; it is evidence, and evidence is not
/// something a test suite re-fetches.
/// </summary>
public class SuiteReachesNoVendorTests
{
    /// <summary>The vendor's own host, spelled once, here, where the scan is looking for it.</summary>
    const string VendorHost = "data" + ".binance" + ".vision";

    /// <summary>Every C# source file in both test projects.</summary>
    public static IReadOnlyList<string> TestSources()
    {
        var root = RepoRoot();
        return
        [
            .. Directory.GetFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .OrderBy(f => f, StringComparer.Ordinal)
        ];
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    [Fact]
    public void No_test_names_the_vendors_host_and_none_takes_the_clients_default_base_url()
    {
        var offenders = new List<string>();

        foreach (var file in TestSources())
        {
            var name = Path.GetFileName(file);
            if (name == nameof(SuiteReachesNoVendorTests) + ".cs") continue;

            var text = File.ReadAllText(file);
            foreach (var (line, n) in text.Replace("\r\n", "\n").Split('\n').Select((l, i) => (l, i + 1)))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)
                    || code.StartsWith("*", StringComparison.Ordinal)) continue;

                if (code.Contains(VendorHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names the vendor's own host");

                // `new BinanceArchiveClient()` with nothing in the brackets takes the default, which
                // is the vendor. Every test has to say where it is pointing.
                if (Regex.IsMatch(code, @"new\s+BinanceArchiveClient\s*\(\s*\)"))
                    offenders.Add($"{name}:{n} constructs BinanceArchiveClient with no base URL, so it would use the vendor's");
            }
        }

        Assert.True(offenders.Count == 0,
            "these tests would reach the real archive:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The scan has to be looking at both test projects. A scan that quietly covers one of them is a
    /// rule that holds where nobody was going to break it.
    /// </summary>
    [Fact]
    public void The_scan_covers_both_test_projects_and_every_source_in_them()
    {
        var files = TestSources();

        Assert.Contains(files, f => f.Contains("TradeAgent.UnitTests", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Contains("TradeAgent.IntegrationTests", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Contains("TradeAgent.FaultTests", StringComparison.Ordinal));
        Assert.True(files.Count > 40, $"the scan found only {files.Count} test sources, which is not this suite");
    }

    /// <summary>The default is the vendor's, which is exactly why the scan above exists.</summary>
    [Fact]
    public void The_clients_default_really_is_the_vendor()
    {
        Assert.Equal(BinanceArchive.BaseUrl, new BinanceArchiveClient().BaseUrl);
        Assert.StartsWith("https://", BinanceArchive.BaseUrl);
    }
}
