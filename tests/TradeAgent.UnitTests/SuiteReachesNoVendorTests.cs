using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.AgentRuntime;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
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

    /// <summary>
    /// THE FORWARD HOST (<c>U-forward-bars</c>), spelled the same way and for the same reason as the
    /// archive's — and it is a DIFFERENT name, which is exactly the hazard. <c>data-api.binance.vision</c>
    /// does not contain <c>data.binance.vision</c>, so the scan above would not have found a test that
    /// named it, and <see cref="ForwardBarCollector"/>'s base URL defaults to the catalogue row, which
    /// is the vendor: a test that forgets to point it at loopback would poll a public endpoint every
    /// tick, on three platforms, and pass.
    /// </summary>
    const string ForwardHost = "data-api" + ".binance" + ".vision";

    /// <summary>
    /// THE TAPE'S HOST (<c>U-tape-store</c>), spelled the same way — Binance's USDⓈ-M futures host, a
    /// third Binance name that neither scan above contains. <see cref="TapeCollector"/>'s base URL
    /// defaults to each catalogue row's, which is the vendor: a test that forgot to point it at loopback
    /// would ask thirty-one public questions a minute, on three platforms, and pass.
    /// </summary>
    const string TapeHost = "fapi" + ".binance" + ".com";

    /// <summary>
    /// OKX'S NAME (<c>U-tape-events</c>), spelled the same way: the announcement row ships pointing at OKX's
    /// EU domain, and the collector's own rule above is what keeps a test from asking it. A test that named
    /// any OKX host would be one edit away from a request to it, so the name is refused outright.
    /// </summary>
    const string OkxHost = "okx" + ".com";

    /// <summary>
    /// BYBIT'S NAME, as the start of every host it runs — <c>bybit.com</c>, <c>bybit.eu</c> and the rest.
    /// Its announcements were measured beside OKX's and DROPPED: no terms basis could be re-read on the day —
    /// its EU General Terms, read on 2026-10-04 as forbidding automatic means to access or monitor its platform,
    /// could not be read at all on 2026-10-06 (<c>docs/RESEARCH-REQUIRED.md</c>, C5d). No row reaches it, and no test may.
    /// </summary>
    const string BybitHost = "bybit" + ".";

    /// <summary>
    /// GDELT'S NAME (<c>U-tape-archive</c>), spelled the same way: the archive row ships pointing at GDELT's data host,
    /// and <see cref="GdeltRecorder"/>'s base URL defaults to it. A test that named any GDELT host would be one edit away
    /// from a request to it — a GKG file is megabytes — so the name is refused outright.
    /// </summary>
    const string GdeltHost = "gdeltproject" + ".org";

    /// <summary>
    /// HYPERLIQUID'S NAME (<c>U-tape-chain</c>), spelled the same way: the positioning row ships pointing at Hyperliquid's API
    /// host and its terms at Hyperliquid's app, and the collector's own rule above is what keeps a test from asking it — by
    /// POST, every five minutes. A test that named any Hyperliquid host would be one edit away from a request to it, so the
    /// name is refused outright.
    /// </summary>
    const string HyperliquidHost = "hyperliquid" + ".xyz";

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

                if (code.Contains(ForwardHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names the forward collector's vendor host");

                if (code.Contains(TapeHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names the tape collector's vendor host");

                if (code.Contains(OkxHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names an OKX host, which the tape's announcement row reaches");

                if (code.Contains(BybitHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names a Bybit host, a source dropped on its terms that nothing here may reach");

                if (code.Contains(GdeltHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names a GDELT host, which the tape's archive recorder reaches");

                if (code.Contains(HyperliquidHost, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{name}:{n} names a Hyperliquid host, which the tape's positioning row reaches");

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

            // AND THE FORWARD COLLECTOR, WHICH IS THE SAME TRAP A THIRD TIME. Its `baseUrl` argument
            // is optional and defaults to the catalogue row — the vendor's market-data host — so a
            // test that CONSTRUCTS one without naming a base URL would poll a public endpoint on
            // every tick and pass. A file-level rule, like the harness above: a test source that
            // builds one has to point it somewhere in the same file. The lookahead lets static
            // access through, because `ForwardBarCollector.MaxBackoff` reaches no host at all.
            if (Regex.IsMatch(body, @"\bForwardBarCollector\b(?!\s*\.)")
                && !body.Contains("baseUrl:", StringComparison.Ordinal))
                offenders.Add($"{name} builds a ForwardBarCollector and never names a baseUrl, so it "
                              + "would poll the vendor's market-data host every tick");

            // AND THE TAPE COLLECTOR, THE SAME TRAP A FOURTH TIME: its `baseUrl` is optional and null
            // means every row's own host. The same file-level rule, the same lookahead for static access.
            if (Regex.IsMatch(body, @"\bTapeCollector\b(?!\s*\.)")
                && !body.Contains("baseUrl:", StringComparison.Ordinal))
                offenders.Add($"{name} builds a TapeCollector and never names a baseUrl, so it would ask "
                              + "the vendor's futures host every look");

            // AND GDELT'S RECORDER, THE SAME TRAP A SIXTH TIME (U-tape-archive): its `baseUrl` is optional and null
            // means GDELT's own data host — megabytes a look. The same file-level rule, the same lookahead.
            if (Regex.IsMatch(body, @"\bGdeltRecorder\b(?!\s*\.)")
                && !body.Contains("baseUrl:", StringComparison.Ordinal))
                offenders.Add($"{name} builds a GdeltRecorder and never names a baseUrl, so it would ask GDELT's data "
                              + "host for its files");

            // AND THE INSTRUMENT VERIFIER, THE SAME TRAP A FIFTH TIME (U-venue-verify): its built-in
            // definition address defaults to the one compiled into this build — the vendor's market-data
            // host — so a test that builds one without naming `builtInDefinition` would ask the vendor for an
            // instrument definition on every run and pass. The same file-level rule and the same lookahead.
            if (Regex.IsMatch(body, @"\bInstrumentVerifier\b(?!\s*\.)")
                && !body.Contains("builtInDefinition:", StringComparison.Ordinal))
                offenders.Add($"{name} builds an InstrumentVerifier and never names a builtInDefinition, so it "
                              + "would ask the vendor's market-data host for an instrument definition");
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
    /// AND SO IS THE FORWARD ROW'S, which is why the collector's own rule above exists. The row is
    /// DATA and ships pointing at the vendor — that is the product working — so the claim the suite
    /// has to keep is that no TEST takes it.
    /// </summary>
    [Fact]
    public void The_forward_sources_shipped_base_url_really_is_the_vendor()
    {
        var entry = Assert.Single(CandleSourceCatalog.BuiltIn(), s => s.Id == ForwardBars.Source);

        Assert.Contains(ForwardHost, entry.BaseUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", entry.BaseUrl);
    }

    /// <summary>
    /// AND EVERY BUILT-IN TAPE ROW SHIPS POINTING AT THE VENDOR, which is the product working — and the
    /// reason the tape collector's own rule above exists. Asserted through the host spelled in this file.
    /// </summary>
    [Fact]
    public void The_tape_sources_shipped_base_url_really_is_the_vendor()
    {
        var rows = TapeSourceCatalog.BuiltIn();

        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.Contains(TapeHost, r.BaseUrl, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("https://", r.BaseUrl);
        });
    }

    /// <summary>
    /// AND THE ANNOUNCEMENT ROW SHIPS POINTING AT OKX (<c>U-tape-events</c>), asserted through the name spelled
    /// in this file — while no shipped row, announcement or market, names Bybit anywhere: its address, its
    /// documentation or its terms. That is the drop, pinned.
    /// </summary>
    [Fact]
    public void The_announcement_source_ships_pointing_at_okx_and_no_shipped_row_names_bybit()
    {
        var row = Assert.Single(TapeSourceCatalog.Announcements());
        Assert.Contains(OkxHost, row.BaseUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", row.BaseUrl);
        Assert.Contains(OkxHost, row.TermsUrl, StringComparison.OrdinalIgnoreCase);

        Assert.All(TapeSourceCatalog.Shipped(), r =>
        {
            Assert.DoesNotContain(BybitHost, r.BaseUrl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(BybitHost, r.DocUrl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(BybitHost, r.TermsUrl, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// AND THE ARCHIVE ROW SHIPS POINTING AT GDELT (<c>U-tape-archive</c>), its terms and codebook too, and the recorder's
    /// default root is GDELT's own — asserted through the name spelled in this file, which is why the recorder's own rule
    /// above exists. Building one here asks nothing: only <c>Start</c> or a look does.
    /// </summary>
    [Fact]
    public async Task The_archive_source_and_the_recorders_default_really_are_gdelt()
    {
        var row = Assert.Single(TapeSourceCatalog.Archives());
        Assert.Contains(GdeltHost, row.BaseUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", row.BaseUrl);
        Assert.Contains(GdeltHost, row.TermsUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(GdeltHost, row.DocUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(GdeltHost, row.Citation, StringComparison.OrdinalIgnoreCase);

        using var store = new TapeStore(Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db"));
        await using var recorder = new GdeltRecorder(store);
        Assert.Equal(row.BaseUrl, recorder.Root);
        Assert.Empty(store.Fetches());
    }

    /// <summary>
    /// AND THE POSITIONING ROW SHIPS POINTING AT HYPERLIQUID (<c>U-tape-chain</c>), its terms too — asserted through the name
    /// spelled in this file, which is why the tape collector's own rule above exists for this row as well.
    /// </summary>
    [Fact]
    public void The_positioning_source_ships_pointing_at_hyperliquid()
    {
        var row = Assert.Single(TapeSourceCatalog.Positioning());
        Assert.Contains(HyperliquidHost, row.BaseUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", row.BaseUrl);
        Assert.Contains(HyperliquidHost, row.TermsUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", row.TermsUrl);
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
