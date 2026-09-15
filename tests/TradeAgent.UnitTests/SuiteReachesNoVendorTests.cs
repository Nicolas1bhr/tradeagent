using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.AgentRuntime;
using TradeAgent.Core.Data;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE SUITE TALKS TO A LOOPBACK <see cref="System.Net.HttpListener"/> AND NEVER TO A VENDOR.
///
/// <para>Every test that needs an outside service is written against <see cref="FakeArchive"/> or
/// <see cref="FakeProvider"/>. What makes that a property of the SUITE rather than of the tests that
/// happen to exist today is this scan, and it closes the same hazard twice over:</para>
///
/// <list type="bullet">
/// <item><b>The data archive</b> (<c>U-data-binance</c> item 5). <see cref="BinanceArchiveClient"/>
/// takes its base URL as an OPTIONAL argument defaulting to <c>data.binance.vision</c>, so a test that
/// forgets to pass the loopback address does not fail — it quietly downloads two megabytes from a public
/// CDN on every CI run, on three platforms, and passes.</item>
/// <item><b>The AI provider</b> (<c>U-api-worker</c> item 5). Worse in one way: the app-owned harness is
/// built from the SHIPPED manifest, whose <c>BaseUrl</c> is the real endpoint, so a test that builds an
/// <c>ApiAgentRuntime</c> and forgets to repoint it sends a request to a provider — with a fake key, so
/// it fails as a 401 rather than as "you are on the network", which is the worst way to find out.</item>
/// </list>
///
/// <para>The one real download the data unit was allowed is quoted in that unit's report. It was made
/// once, by hand, to establish the URL pattern and the sidecar's format; it is evidence, and evidence is
/// not something a test suite re-fetches. <b>The provider was never called at all</b> — no run of this
/// repository has ever sent a request to it — so the harness's request and response shapes are the
/// vendor's published contract rather than something measured, and the manifest says so.</para>
/// </summary>
// It reads runtimes.json (RuntimeCatalog.Require, for the shipped harness manifest), so it shares the
// collection with the tests that corrupt that file on purpose rather than racing them.
[Collection(VendorOverrideFiles.Name)]
public class SuiteReachesNoVendorTests
{
    /// <summary>The vendor's own host, spelled once, here, where the scan is looking for it.</summary>
    const string VendorHost = "data" + ".binance" + ".vision";

    /// <summary>
    /// The AI provider's own host, spelled the same way and for the same reason: this file is the one
    /// place in the test tree these two names may appear, so a scan for them cannot find itself.
    /// </summary>
    const string ProviderHost = "api" + ".openai" + ".com";

    /// <summary>
    /// THE SECOND CANDLE SOURCE'S VENDOR (<c>U-data-2</c> item 4), spelled the same way.
    ///
    /// <para>This repository records NO public-candles endpoint for it — <c>RESEARCH-REQUIRED.md</c>
    /// has only the SIGNED trading base — so the hazard is not a test that forgets to repoint a
    /// client: it is a test that INVENTS an endpoint out of the one host this repository does know and
    /// sends a request to a vendor nobody has agreed to reach. The scan refuses the name outright.</para>
    /// </summary>
    const string SecondSourceHost = "revolut" + ".com";

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

            // THE CODE ONLY, built as the per-line loop goes, because both checks below are about what a
            // test WOULD DO and a comment does nothing. Measured: the file-level check over the whole
            // text flagged HarnessCatalogTests for one sentence in a comment naming the type.
            var codeLines = new System.Text.StringBuilder();

            foreach (var (line, n) in text.Replace("\r\n", "\n").Split('\n').Select((l, i) => (l, i + 1)))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)
                    || code.StartsWith("*", StringComparison.Ordinal)) continue;
                codeLines.Append(code).Append('\n');

                if (code.Contains(VendorHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names the archive vendor's own host");

                if (code.Contains(ProviderHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names the AI provider's own host");

                if (code.Contains(SecondSourceHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names the second candle source's vendor host, which this "
                                  + "build has never reached and has no endpoint for");

                // `new BinanceArchiveClient()` with nothing in the brackets takes the default, which
                // is the vendor. Every test has to say where it is pointing.
                if (Regex.IsMatch(code, @"new\s+BinanceArchiveClient\s*\(\s*\)"))
                    offenders.Add($"{name}:{n} constructs BinanceArchiveClient with no base URL, so it would use the vendor's");
            }

            // AND THE HARNESS, WHICH CANNOT BE CAUGHT A LINE AT A TIME. An ApiAgentRuntime is built from
            // a manifest, and the shipped manifest's BaseUrl is the provider — so the rule is about the
            // FILE: a test source that uses the type has to repoint it somewhere in the same file.
            //
            // THE MATCH IS THE TYPE, NOT `new ApiAgentRuntime`, and that is the whole difference between
            // a scan and a scan that works. Measured here: with the repoint deliberately removed from
            // ApiWorkerTests, a check for `new ApiAgentRuntime` found nothing — that file builds one with
            // a TARGET-TYPED `new(...)`, so the constructor's name is nowhere in the source. The lookahead
            // lets static access through (`ApiAgentRuntime.RuntimeId` names no instance and reaches
            // nothing) and catches every way of naming the type itself.
            var body = codeLines.ToString();
            if (Regex.IsMatch(body, @"\bApiAgentRuntime\b(?!\s*\.)")
                && !body.Contains("BaseUrl =", StringComparison.Ordinal))
                offenders.Add($"{name} uses ApiAgentRuntime and never sets BaseUrl, so it would "
                              + "send a request to the AI provider");
        }

        Assert.True(offenders.Count == 0,
            "these tests would reach a real vendor:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// AND THE LOOPBACK LISTENER IS BUILT IN ONE PLACE (<c>U-loopback-listener-mac</c>). Four fixtures
    /// each chose their own port out of a random band and each retried <c>Start()</c> on the SAME
    /// <see cref="System.Net.HttpListener"/>, which a failed bind has already closed: the retry threw
    /// <c>ObjectDisposedException</c> out of the constructor, so a collision between two classes
    /// running in parallel killed a test in milliseconds while the product was green. Moving the four
    /// fixes the fixtures that exist today; this scan is what stops the fifth one being written.
    ///
    /// <para><see cref="Loopback.Start"/> is the only place in the test tree that may construct one —
    /// it borrows a port the OS says is free and retries from a fresh instance.</para>
    /// </summary>
    [Fact]
    public void No_test_builds_its_own_loopback_listener()
    {
        // BOTH SPELLINGS, and the second is the one that matters: every fixture this unit moved wrote
        // `readonly HttpListener _http = new();`, where the constructor's name is nowhere on the line.
        // Measured here — with the field initialiser put back in FakeArchive, a check for
        // `new HttpListener(` alone found nothing and this test passed. It is the same trap the
        // ApiAgentRuntime check above records, one file over.
        //
        // The type name is spelled in pieces so the scan cannot find itself.
        const string Type = "Http" + "Listener";
        var construction = $@"new\s+{Type}\s*\(|\b{Type}\b[^=;]*=\s*new\b";

        var offenders =
            (from file in TestSources()
             where Path.GetFileName(file) != nameof(Loopback) + ".cs"
                && Path.GetFileName(file) != nameof(SuiteReachesNoVendorTests) + ".cs"
             from pair in File.ReadAllText(file).Replace("\r\n", "\n").Split('\n')
                              .Select((l, i) => (Line: l, Number: i + 1))
             let code = pair.Line.TrimStart()
             where Regex.IsMatch(code, construction)
                && !code.StartsWith("//", StringComparison.Ordinal)
                && !code.StartsWith("*", StringComparison.Ordinal)
             select $"{Path.GetFileName(file)}:{pair.Number}").ToList();

        Assert.True(offenders.Count == 0,
            "these fixtures build their own HttpListener, and a port collision with a class running "
            + "beside them will throw ObjectDisposedException out of the constructor; take one from "
            + "Loopback.Start instead: " + string.Join(", ", offenders));
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

    /// <summary>
    /// AND THE SHIPPED HARNESS MANIFEST REALLY POINTS AT THE PROVIDER, which is why a test that builds
    /// one has to repoint it. Asserted through the host spelled in this file rather than against a
    /// literal, so the scan above and this claim cannot drift apart.
    /// </summary>
    [Fact]
    public void The_shipped_harness_manifest_really_points_at_the_provider()
    {
        var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);

        Assert.Contains(ProviderHost, manifest.Endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", manifest.Endpoint);
        // Unverified against the real provider by design: the brief forbids a real call, and nothing in
        // this repository has ever made one.
        Assert.False(manifest.Verified);
    }

    /// <summary>
    /// AND THE SECOND SOURCE SHIPS WITH NO ENDPOINT AT ALL, which is the strongest form of this rule:
    /// a client that forgot to point somewhere cannot reach a vendor, because the build holds no
    /// address for one. The row is served — as unverified — and it refuses to fetch, in words.
    /// </summary>
    [Fact]
    public void The_second_candle_source_ships_with_no_endpoint_and_says_why()
    {
        var entry = Assert.Single(CandleSourceCatalog.BuiltIn(),
            s => s.Id == CandleSourceCatalog.RevolutXCandles);

        Assert.Equal("", entry.BaseUrl);
        Assert.False(entry.Verified);
        Assert.Contains("records NO public-candles endpoint", entry.Source);
        Assert.DoesNotContain(SecondSourceHost, entry.UrlShape, StringComparison.OrdinalIgnoreCase);

        // AND ASKING IT FOR A PERIOD IS A REFUSAL IN WORDS, not a request to a relative path.
        var refused = Assert.Throws<TradeAgentException>(
            () => CandleSourceCatalog.Of(entry).Periods("BTC-USD", DateTimeOffset.UtcNow));
        Assert.Contains("has no endpoint to ask", refused.Message);
    }

    /// <summary>
    /// The provider fake is a loopback listener and says so in its own address, so a test pointed at it
    /// cannot accidentally be pointed somewhere else.
    /// </summary>
    [Fact]
    public void The_provider_fake_serves_loopback_only()
    {
        using var provider = new FakeProvider();

        Assert.StartsWith("http://127.0.0.1:", provider.BaseUrl);
        Assert.DoesNotContain(ProviderHost, provider.BaseUrl, StringComparison.OrdinalIgnoreCase);
    }
}
