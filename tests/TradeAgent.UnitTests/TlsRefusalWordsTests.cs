using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Tls;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-tls-denylist, fix pass (the review's P2) — A CONNECTION THE TLS POLICY REFUSED KEEPS ITS WORDS ON THE WAY TO THE OWNER. The refusal
/// names the distrusted authority and its source, or the missing list and the repair; <c>HttpClient</c> wraps it ("The SSL connection
/// could not be established, see inner exception"), and a path that shows the wrapper's message, or folds the failure into "could not be
/// read", loses exactly the part that says what happened. The downloader's paths here; the AI conversation's in <c>HarnessBudgetTests</c>
/// and the decision wire's in <c>DecisionPortTests</c>, beside their own rigs.
/// </summary>
public class TlsRefusalWordsTests
{
    /// <summary>The policy's words for a denylisted chain, as <c>TlsPolicy</c> writes them.</summary>
    internal const string Words =
        "TradeAgent refused a secure connection: the server's certificate chain runs through EM DV TLS CA - G3B, a certificate authority "
        + "whose private key is compromised (reported 2026-10-17; Go CL 851047). The connection was not made.";

    /// <summary>A transport whose every connection the TLS policy refused, wrapped the way <c>SocketsHttpHandler</c> wraps it.</summary>
    internal sealed class RefusingTransport : HttpMessageHandler
    {
        public int Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            throw new HttpRequestException("The SSL connection could not be established, see inner exception.", new TlsRefusedException(Words));
        }
    }

    const string Url = "https://example.invalid/file";

    [Fact]
    public async Task The_downloader_never_folds_a_tls_refusal_into_could_not_be_read()
    {
        using var http = new HttpClient(new RefusingTransport());

        var text = await Assert.ThrowsAsync<TlsRefusedException>(() => Downloader.TryGetStringAsync(http, Url));
        Assert.Contains("EM DV TLS CA - G3B", text.Message);

        var small = await Assert.ThrowsAsync<TlsRefusedException>(() => Downloader.TryGetSmallTextAsync(http, Url, 1024, TimeSpan.FromSeconds(5)));
        Assert.Contains("EM DV TLS CA - G3B", small.Message);

        var release = await Assert.ThrowsAsync<TlsRefusedException>(
            () => Downloader.ResolveGitHubAssetAsync(http, "owner/repo", ".*", api: "https://example.invalid"));
        Assert.Contains("EM DV TLS CA - G3B", release.Message);

        // A STATUS SAYS WHY THERE WAS NO ANSWER, in the refusal's words, never the wrapper's "see inner exception".
        var status = await Downloader.StatusAsync(http, Url, TimeSpan.FromSeconds(5));
        Assert.False(status.Answered);
        Assert.Contains("EM DV TLS CA - G3B", status.Because);
    }

    /// <summary>
    /// A REFUSED DOWNLOAD NEVER FALLS BACK TO THE PACKAGE (fix pass, the review's P1). An archive the TLS policy refused sent the
    /// install to npm through TradeAgent's private Node — a second road whose connections never meet the denylist, taken because the
    /// first one was refused for exactly that. Here the package road is open (a plan with an npm package, not pinned, Node provided)
    /// and the archive is refused: the install refuses in the policy's words, and no package install is started.
    /// </summary>
    [Fact]
    public async Task A_refused_download_never_falls_back_to_the_package()
    {
        var manifest = RuntimeCatalog.BuiltIn(HostPlatform.LinuxX64).Single(m => m.Id == "opencode");
        Assert.Equal(InstallKind.Download, manifest.Install.Kind);
        Assert.False(string.IsNullOrEmpty(manifest.Install.NpmPackage));
        Assert.False(manifest.Install.Pinned);
        manifest.Id = $"opencode-tls-{Guid.NewGuid():n}"[..24];
        manifest.Install.GitHubRepo = null;
        manifest.Install.Url = Url;

        var downloads = 0;
        var said = new Said();
        var runtime = new CliAgentRuntime(manifest)
        {
            NodeCanProvide = () => true,
            Download = (_, _, _, _, _) =>
            {
                downloads++;
                throw new HttpRequestException("The SSL connection could not be established, see inner exception.", new TlsRefusedException(Words));
            }
        };

        var refused = await Assert.ThrowsAsync<TradeAgentException>(() => runtime.InstallAsync(said));

        Assert.Equal(1, downloads);
        Assert.DoesNotContain(said.Lines, l => l.Contains("package", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(said.Lines, l => l.Contains("Node.js", StringComparison.Ordinal));
        Assert.Contains("EM DV TLS CA - G3B", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCode.AI_INSTALL_FAILED, refused.Info.Code);
    }

    /// <summary>Progress, kept in order as it is said — synchronously, unlike <see cref="Progress{T}"/>.</summary>
    sealed class Said : IProgress<string>
    {
        public List<string> Lines { get; } = [];
        public void Report(string value) => Lines.Add(value);
    }
}
