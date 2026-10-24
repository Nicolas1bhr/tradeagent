using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace TradeAgent.Core.Tls;

/// <summary>U-tls-denylist — the one validation every secure connection the app makes goes through.</summary>
public static class TlsPolicy
{
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
