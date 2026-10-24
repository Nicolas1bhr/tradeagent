using System.Text.RegularExpressions;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-tls-denylist, item 4 — THE GUARD: NO NETWORK CLIENT IN <c>src/</c> IS BUILT OUTSIDE <c>TlsClients</c>, AND NO OTHER CERTIFICATE
/// VALIDATOR EXISTS.
///
/// <para>Every secure connection the app makes must go through <c>TlsPolicy</c> (the platform's result None AND no denylisted key in the
/// chain); a client built anywhere else would carry the platform's default validation alone, and a validator written anywhere else could
/// return true while the platform reports errors. So both are refused by name everywhere in <c>src/</c> but <c>TradeAgent.Core/Tls/</c>,
/// whose policy is proven by <c>TlsPolicyTests</c> (a platform error is never overridden).</para>
///
/// <para><b>What it scans:</b> every <c>.cs</c> file under <c>src/</c> (outside bin and obj), comment lines excepted. <b>What it cannot
/// see:</b> a client built by reflection, or by a library the app does not reference today; no such library is referenced.</para>
/// </summary>
public class TlsClientGuardTests
{
    /// <summary>
    /// A client or TLS stream built directly — by its short or its fully qualified name — or target-typed into a variable, field,
    /// property or method of one of those types. Whitespace includes line breaks: <see cref="Scan"/> reads the whole file.
    /// </summary>
    static readonly Regex BuildsAClient = new(
        @"\bnew\s+(global::)?([A-Za-z_]\w*\s*\.\s*)*(HttpClient|SocketsHttpHandler|HttpClientHandler|HttpMessageInvoker|SslStream|ClientWebSocket|WebClient)\b"
        + @"|\b(HttpClient|SocketsHttpHandler|HttpClientHandler|HttpMessageInvoker|SslStream)\??\s+\w+\s*(\([^()]*\))?\s*=>?\s*new\s*\("
        + @"|\bWebRequest\s*\.\s*Create|\bQuicConnection\s*\.\s*ConnectAsync|\bIHttpClientFactory\b",
        RegexOptions.Compiled);

    /// <summary>A certificate validator of its own, or the platform's "accept anything".</summary>
    static readonly Regex ValidatesCertificates = new(
        @"RemoteCertificateValidationCallback|ServerCertificateCustomValidationCallback|DangerousAcceptAnyServerCertificateValidator"
        + @"|CertificateValidationCallback|ServicePointManager",
        RegexOptions.Compiled);

    static readonly string Policy = Path.Combine("TradeAgent.Core", "Tls") + Path.DirectorySeparatorChar;

    [Fact]
    public void No_TLS_client_in_src_is_built_outside_the_factory_and_no_other_validator_exists()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var hits = new List<string>();
        var policyFiles = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
            if (rel.StartsWith(Policy, StringComparison.Ordinal)) { policyFiles++; continue; }
            hits.AddRange(Scan(File.ReadAllText(file)).Select(h => $"{rel}:{h.Line}: {h.Text}"));
        }
        Assert.True(policyFiles >= 3, "the policy folder TradeAgent.Core/Tls/ must exist; the guard's exemption names it");
        Assert.True(hits.Count == 0,
            "a network client or certificate validator outside TradeAgent.Core/Tls/; build it with TlsClients so TlsPolicy checks it:\n"
            + string.Join("\n", hits));
    }

    /// <summary>The guard sees what it refuses: each spelling it exists for is matched, and the factory's own calls are not.</summary>
    [Fact]
    public void The_guard_recognises_every_spelling_it_refuses()
    {
        string[] refused =
        [
            "using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };",
            "static readonly HttpClient Http = new(TlsClients.Handler());",
            "var handler = new SocketsHttpHandler",
            "_http = new HttpClient(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)",
            "using var ssl = new SslStream(tcp.GetStream());",
            "var h = new HttpClientHandler();",
            "readonly HttpClient? _http = new(handler);",
        ];
        Assert.All(refused, l => Assert.Matches(BuildsAClient, l));

        // FULLY QUALIFIED, AND SPLIT ACROSS LINES (fix pass, the review's P2): the scan reads the file, not one line at a time.
        Assert.Single(Scan("using var ssl = new System.Net.Security.SslStream(tcp.GetStream());"));
        Assert.Single(Scan("var http = new global::System.Net.Http.HttpClient();"));
        Assert.Single(Scan("static readonly HttpClient Http =\n    new(TlsClients.Handler());"));
        Assert.Single(Scan("var handler = new\n    SocketsHttpHandler { AllowAutoRedirect = false };"));
        Assert.Single(Scan("static HttpClient Make(HttpMessageHandler h) =>\n    new(h);"));
        Assert.Equal(4, Assert.Single(Scan("// a comment\n/// another\nusing var ssl =\n  new SslStream(s);")).Line);
        Assert.Empty(Scan("// var h = new HttpClientHandler();\n * new SslStream(s)\nvar c = TlsClients.Client();"));
        Assert.Matches(ValidatesCertificates, "handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;");
        Assert.Matches(ValidatesCertificates, "h.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;");

        string[] allowed =
        [
            "static readonly HttpClient Http = TlsClients.Client(h => h.AllowAutoRedirect = false, Timeout.InfiniteTimeSpan);",
            "readonly HttpClient _http = TlsClients.Client(new Handing(transport ?? TlsClients.Handler()), timeout);",
            "var handler = TlsClients.Handler(h => h.AllowAutoRedirect = true);",
        ];
        Assert.All(allowed, l => Assert.DoesNotMatch(BuildsAClient, l));
    }

    /// <summary>
    /// What one file builds or validates outside the factory, with the line each hit starts on. It reads the WHOLE text, so a
    /// construction split across lines is seen (fix pass, the review's P2); comment lines are blanked first, kept as empty lines so
    /// the line numbers stay the file's.
    /// </summary>
    static List<(int Line, string Text)> Scan(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("//") || line.StartsWith("*")) lines[i] = "";
        }
        var code = string.Join('\n', lines);
        var hits = new SortedDictionary<int, string>();
        foreach (var rx in new[] { BuildsAClient, ValidatesCertificates })
            foreach (Match m in rx.Matches(code))
            {
                var at = 1 + code.AsSpan(0, m.Index).Count('\n');
                hits.TryAdd(at, Regex.Replace(m.Value, @"\s+", " ") + " … " + lines[at - 1].Trim());
            }
        return hits.Select(h => (h.Key, h.Value)).ToList();
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
