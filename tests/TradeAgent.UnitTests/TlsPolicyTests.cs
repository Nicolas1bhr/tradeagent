using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TradeAgent.Core.Tls;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-tls-denylist, item 2 — ONE VALIDATION POLICY: the platform's own result must be None, AND no certificate in the built chain may have
/// a denylisted SPKI. A synthetic root, intermediate and leaf (<see cref="CertificateRequest"/>), evaluated under a custom trust root so the
/// platform accepts the chain: (a) the intermediate on the list is refused in words; (b) a clean chain is allowed; (c) a platform error is
/// never overridden by the list.
/// </summary>
public sealed class TlsPolicyTests : IDisposable
{
    readonly X509Certificate2 _root, _intermediate, _leaf;
    readonly X509Chain _chain;

    public TlsPolicyTests()
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var intKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var rootReq = new CertificateRequest("CN=TradeAgent Test Root", rootKey, HashAlgorithmName.SHA256);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        _root = rootReq.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        var intReq = new CertificateRequest("CN=TradeAgent Test Intermediate", intKey, HashAlgorithmName.SHA256);
        intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intReq.PublicKey, false));
        intReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_root, true, false));
        using (var signed = intReq.Create(_root, now.AddDays(-1), now.AddDays(20), [1, 2, 3, 4]))
            _intermediate = signed.CopyWithPrivateKey(intKey);

        var leafReq = new CertificateRequest("CN=venue.test", leafKey, HashAlgorithmName.SHA256);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        leafReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_intermediate, true, false));
        _leaf = leafReq.Create(_intermediate, now.AddDays(-1), now.AddDays(10), [5, 6, 7, 8]);

        _chain = new X509Chain();
        _chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        _chain.ChainPolicy.CustomTrustStore.Add(_root);
        _chain.ChainPolicy.ExtraStore.Add(_intermediate);
        _chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(_chain.Build(_leaf), "the synthetic chain must be one the platform accepts");
        Assert.Equal(3, _chain.ChainElements.Count);
    }

    public void Dispose()
    {
        _chain.Dispose();
        _leaf.Dispose();
        _intermediate.Dispose();
        _root.Dispose();
    }

    static TlsDenylist ListOf(string subject, string spki) => TlsDenylist.Parse(
        $$"""{ "entries": [ { "subject": "{{subject}}", "spkiSha256": "{{spki}}", "purpose": "tls", "source": "a test source", "date": "2026-10-17" } ] }""");

    static readonly TlsDenylist Unrelated = ListOf("Unrelated CA", new string('a', 64));

    [Fact]
    public void A_chain_through_a_denylisted_intermediate_is_refused_even_when_the_platform_accepts_it()
    {
        // The policy's hash is of the key the platform sees: the same as .NET's own encoding of this synthetic intermediate's SPKI.
        var spki = TlsPolicy.SpkiSha256(_intermediate);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_intermediate.PublicKey.ExportSubjectPublicKeyInfo())), spki);
        var list = ListOf("TradeAgent Test Intermediate", spki);

        var verdict = TlsPolicy.Validate(_leaf, _chain, SslPolicyErrors.None, list);

        Assert.False(verdict.Allowed);
        Assert.Equal("TradeAgent Test Intermediate", verdict.Denied?.Subject);
        Assert.Contains("TradeAgent Test Intermediate", verdict.Refusal);
        Assert.Contains("a test source", verdict.Refusal);
        Assert.Contains("compromised", verdict.Refusal);

        // The callback every client carries refuses in the same words, as a TLS failure — never true, never a silent false.
        var callback = TlsPolicy.Callback(() => list);
        var refused = Assert.Throws<TlsRefusedException>(() => callback(this, _leaf, _chain, SslPolicyErrors.None));
        Assert.IsAssignableFrom<AuthenticationException>(refused);
        Assert.Equal(verdict.Refusal, refused.Message);
        Assert.Same(refused, TlsRefusedException.In(new HttpRequestException("outer", refused)));
    }

    [Fact]
    public void A_clean_chain_the_platform_accepts_is_allowed()
    {
        var verdict = TlsPolicy.Validate(_leaf, _chain, SslPolicyErrors.None, Unrelated);
        Assert.True(verdict.Allowed);
        Assert.Null(verdict.Refusal);
        Assert.Null(verdict.Denied);

        Assert.True(TlsPolicy.Callback(() => Unrelated)(this, _leaf, _chain, SslPolicyErrors.None));
    }

    [Fact]
    public void A_platform_error_is_never_overridden_by_the_list()
    {
        foreach (var errors in new[]
                 {
                     SslPolicyErrors.RemoteCertificateChainErrors, SslPolicyErrors.RemoteCertificateNameMismatch,
                     SslPolicyErrors.RemoteCertificateNotAvailable,
                     SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
                 })
        {
            // A clean list does not turn the platform's refusal into an allowance…
            var verdict = TlsPolicy.Validate(_leaf, _chain, errors, Unrelated);
            Assert.False(verdict.Allowed);
            Assert.Contains(errors.ToString(), verdict.Refusal);

            // …and the callback hands the platform's own refusal back unchanged: false, with no list consulted at all.
            var consulted = false;
            Assert.False(TlsPolicy.Callback(() => { consulted = true; return Unrelated; })(this, _leaf, _chain, errors));
            Assert.False(consulted);

            // Nor does an unreadable list, nor a denylisted chain.
            Assert.False(TlsPolicy.Callback(() => throw new TlsDenylistUnreadableException("x"))(this, _leaf, _chain, errors));
            Assert.False(TlsPolicy.Validate(_leaf, _chain, errors, ListOf("i", TlsPolicy.SpkiSha256(_intermediate))).Allowed);
        }

        // No chain or no certificate to check is never an allowance either.
        Assert.False(TlsPolicy.Validate(_leaf, null, SslPolicyErrors.None, Unrelated).Allowed);
        Assert.False(TlsPolicy.Validate(null, _chain, SslPolicyErrors.None, Unrelated).Allowed);
        Assert.Throws<TlsRefusedException>(() => TlsPolicy.Callback(() => Unrelated)(this, _leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void An_unreadable_denylist_refuses_every_connection_in_its_words()
    {
        var callback = TlsPolicy.Callback(() => TlsDenylist.Load(Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid(), TlsDenylist.FileName)));
        var refused = Assert.Throws<TlsRefusedException>(() => callback(this, _leaf, _chain, SslPolicyErrors.None));
        Assert.Contains(TlsDenylist.FileName, refused.Message);
        Assert.Contains("Reinstall TradeAgent", refused.Message);
        Assert.IsType<TlsDenylistUnreadableException>(refused.InnerException);
    }

    [Fact]
    public void The_factory_installs_the_policy_on_every_handler_and_refuses_a_second_validator()
    {
        using var handler = TlsClients.Handler(h => h.AllowAutoRedirect = false);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Same(TlsClients.ShippedCallback, handler.SslOptions.RemoteCertificateValidationCallback);

        // The shipped list is what the shipped callback reads: a chain the platform accepts and the list does not name is allowed.
        Assert.True(TlsClients.ShippedCallback(this, _leaf, _chain, SslPolicyErrors.None));

        var refused = Assert.Throws<InvalidOperationException>(() => TlsClients.Handler(h =>
            h.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true));
        Assert.Contains("one validation", refused.Message);

        using var client = TlsClients.Client(h => h.AllowAutoRedirect = false, TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }
}
