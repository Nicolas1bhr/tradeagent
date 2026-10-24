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
    /// <summary>A client or TLS stream built directly, or target-typed into a variable or field of one of those types.</summary>
    static readonly Regex BuildsAClient = new(
        @"\bnew\s+(System\.Net\.Http\.)?(HttpClient|SocketsHttpHandler|HttpClientHandler|HttpMessageInvoker|SslStream|ClientWebSocket|WebClient)\b"
        + @"|\b(HttpClient|SocketsHttpHandler|HttpClientHandler|HttpMessageInvoker|SslStream)\??\s+\w+\s*=\s*new\s*\("
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
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//") || line.StartsWith("*")) continue;
                if (BuildsAClient.IsMatch(line) || ValidatesCertificates.IsMatch(line)) hits.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
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

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
