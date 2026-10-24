using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace TradeAgent.Core.Tls;

/// <summary>What <see cref="TlsPolicy.Validate"/> decided: allowed, or refused in words — and, for a denylisted chain, which entry.</summary>
public sealed record TlsVerdict(bool Allowed, string? Refusal, TlsDenyEntry? Denied)
{
    internal static readonly TlsVerdict Allow = new(true, null, null);
    internal static TlsVerdict Refuse(string refusal, TlsDenyEntry? denied = null) => new(false, refusal, denied);
}

/// <summary>
/// A SECURE CONNECTION THIS APP REFUSED, IN WORDS: its chain runs through a distrusted certificate authority, or the list it is checked
/// against could not be read. A definite TLS refusal — an <see cref="AuthenticationException"/>, the type a failed handshake already has.
/// It is never an order outcome: nothing turns it into <c>AtasRejectedException</c> or a guessed order state, and no connection was made.
/// </summary>
public sealed class TlsRefusedException(string message, Exception? inner = null) : AuthenticationException(message, inner)
{
    /// <summary>
    /// The refusal inside <paramref name="ex"/>, or null: <c>HttpClient</c> wraps a handshake's exception ("The SSL connection could not be
    /// established, see inner exception"), so a caller that shows a failure in words looks here first.
    /// </summary>
    public static TlsRefusedException? In(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is TlsRefusedException refused) return refused;
        return null;
    }
}

/// <summary>
/// U-tls-denylist — THE ONE VALIDATION EVERY SECURE CONNECTION THE APP MAKES GOES THROUGH (CRED-001: exchange keys, sign-ins and the app's
/// downloads travel over it). <see cref="TlsClients"/> installs <see cref="Callback"/> on every client it builds, and no other client exists.
///
/// <para><b>Two conditions, both required.</b> The platform's own validation must pass — <c>errors == None</c>, never bypassed, never
/// overridden by anything here — AND no certificate in the chain the platform built may have a public key on the <see cref="TlsDenylist"/>.
/// The second exists because the trust store cannot close it: the compromised eMudhra intermediates are cross-signed under a root that stays
/// trusted (Mozilla bug 2083449), and Go closed it the same way (CL 851047).</para>
/// </summary>
public static class TlsPolicy
{
    /// <summary>
    /// The policy, pure: <paramref name="errors"/> is the platform's result for <paramref name="chain"/>. Allowed only when the platform found
    /// nothing wrong and no element of the chain (the leaf included) has a denylisted key; anything else is refused in words.
    /// </summary>
    public static TlsVerdict Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, TlsDenylist list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (errors != SslPolicyErrors.None)
            return TlsVerdict.Refuse($"TradeAgent refused a secure connection: the system did not accept the server's certificate ({errors}).");
        if (certificate is null || chain is null)
            return TlsVerdict.Refuse("TradeAgent refused a secure connection: there was no certificate chain to check.");

        if (Denied(certificate, list) is { } leaf) return Refuse(leaf);
        foreach (var element in chain.ChainElements)
            if (Denied(element.Certificate, list) is { } entry) return Refuse(entry);
        return TlsVerdict.Allow;
    }

    static TlsDenyEntry? Denied(X509Certificate certificate, TlsDenylist list) => list.Find(SpkiSha256(certificate));

    static TlsVerdict Refuse(TlsDenyEntry entry) => TlsVerdict.Refuse(
        $"TradeAgent refused a secure connection: the server's certificate chain runs through {entry.Subject}, a certificate authority "
        + $"whose private key is compromised (reported {entry.Date}; {entry.Source}). The connection was not made.", entry);

    /// <summary>
    /// THE CALLBACK EVERY CLIENT CARRIES. A platform error is handed back as the platform's own refusal (<c>false</c>, its own message) and
    /// the list is not even read. With the platform satisfied, a denylisted chain — or a list that cannot be read — throws
    /// <see cref="TlsRefusedException"/>, so the words reach whoever shows the failure; otherwise <c>true</c>. It never returns true while
    /// <c>errors != None</c>.
    /// </summary>
    public static RemoteCertificateValidationCallback Callback(Func<TlsDenylist> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return (_, certificate, chain, errors) =>
        {
            if (errors != SslPolicyErrors.None) return false;

            TlsDenylist read;
            try { read = list(); }
            catch (TlsDenylistUnreadableException unreadable) { throw new TlsRefusedException(unreadable.Message, unreadable); }

            var verdict = Validate(certificate, chain, errors, read);
            return verdict.Allowed ? true : throw new TlsRefusedException(verdict.Refusal!);
        };
    }

    /// <summary>SHA-256 of a certificate's DER SubjectPublicKeyInfo, exactly as it is encoded in the certificate, lower-case hex.</summary>
    /// <remarks>
    /// Read out of the certificate's own bytes rather than re-encoded from <c>PublicKey</c>, so the hash is of the bytes the sources hashed
    /// (Go hashes <c>RawSubjectPublicKeyInfo</c>). Certificate ::= SEQUENCE { tbsCertificate SEQUENCE { [0] version OPTIONAL, serialNumber,
    /// signature, issuer, validity, subject, subjectPublicKeyInfo, … }, … }.
    /// </remarks>
    public static string SpkiSha256(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var cert = new AsnReader(certificate.GetRawCertData(), AsnEncodingRules.DER).ReadSequence();
        var tbs = cert.ReadSequence();
        if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) tbs.ReadEncodedValue();
        for (var i = 0; i < 5; i++) tbs.ReadEncodedValue(); // serialNumber, signature, issuer, validity, subject
        return Sha256Hex.Of(tbs.ReadEncodedValue().Span);
    }
}
