using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TradeAgent.Core.Tls;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-tls-denylist, item 1 — THE DENYLIST IS SHIPPED DATA, AND A BAD ONE IS REFUSED IN WORDS, NEVER READ AS EMPTY.
/// (d) a missing or malformed file is refused naming the file and the repair; (f) the shipped file holds the eight keys with their sources.
/// </summary>
public class TlsDenylistTests
{
    /// <summary>Go CL 851047's <c>distrustedSPKIHashes</c>, the eight keys of Mozilla bug 2083449.</summary>
    static readonly string[] GoCl851047 =
    [
        "ebed6475c2c2d80fcb199f3fe8dd6e6b3ce60b77eb15d4e42c3dec0c6f5b5105",
        "49dd353dc2b919a3770645117d0e60c16115e7d1bc259eefe734b00ea434fe5f",
        "e0254c7fcdc265e2f0f8a428dabb75ab4ae039b4872d62f5ee57f0877d5a8a41",
        "5c0660a2b164910d8ff589c18e14552e9e0ec237dcefe2cb736d41a4811f81a0",
        "43bffa19d6ef651451f6841e019931e96f6f2eaeff28ab4b6f1ce4576de2717b",
        "63ecefee431d49523bc1136c4e72d0206a477086caf606cd5cf025686c109cf8",
        "ec4ffa1ad0ac88081314b7aaffef6a91f97cd57291bb5ba686c5415f5eb4f6b6",
        "92c063d67e8f14db71d288e11266ffdaf69cbf115f7bab14f3bc2f68d25e0e30",
    ];

    const string Hash = "0000000000000000000000000000000000000000000000000000000000000001";

    static string Entry(string subject = "Test CA", string spki = Hash, string purpose = "tls", string source = "a test",
        string date = "2026-10-17", string extra = "") =>
        $$"""{ "subject": "{{subject}}", "spkiSha256": "{{spki}}", "purpose": "{{purpose}}", "source": "{{source}}", "date": "{{date}}"{{extra}} }""";

    static string File(params string[] entries) =>
        $$"""{ "note": "n", "fetched": "2026-10-24", "entries": [ {{string.Join(", ", entries)}} ] }""";

    [Fact]
    public void A_missing_or_malformed_denylist_is_refused_in_words()
    {
        // MISSING: never an empty list.
        var dir = Directory.CreateTempSubdirectory("ta-tls-").FullName;
        try
        {
            var missing = Path.Combine(dir, TlsDenylist.FileName);
            var refused = Assert.Throws<TlsDenylistUnreadableException>(() => TlsDenylist.Load(missing));
            Assert.Contains(TlsDenylist.FileName, refused.Message);
            Assert.Contains("missing", refused.Message);
            Assert.Contains("Reinstall TradeAgent", refused.Message);
            Assert.Contains("refuses every secure connection", refused.Message);

            System.IO.File.WriteAllText(missing, "{ not json");
            refused = Assert.Throws<TlsDenylistUnreadableException>(() => TlsDenylist.Load(missing));
            Assert.Contains(TlsDenylist.FileName, refused.Message);
            Assert.Contains("not valid JSON", refused.Message);
            Assert.Contains("Reinstall TradeAgent", refused.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }

        // MALFORMED, each way: refused, and the words say what is wrong.
        (string text, string why)[] malformed =
        [
            ("", "not valid JSON"),
            ("null", "not a JSON object"),
            ("[]", "not a JSON object"),
            ("""{ "note": "n", "fetched": "2026-10-24" }""", "'entries'"),
            (File(), "lists no certificate authority"),
            ("""{ "note": "n", "fetched": "2026-10-24", "entries": [], "extra": 1 }""", "'extra'"),
            (File(Entry(spki: "ABC")), "not a SHA-256"),
            (File(Entry(spki: Hash.ToUpperInvariant().Replace('0', 'A'))), "not a SHA-256"),
            (File(Entry(subject: "")), "'subject'"),
            (File(Entry(source: "")), "'source'"),
            (File(Entry(purpose: "email")), "'purpose'"),
            (File(Entry(date: "17 Oct 2026")), "'date'"),
            (File(Entry(extra: """, "trusted": true""")), "'trusted'"),
            (File(Entry(), Entry(subject: "Other")), "twice"),
            ("""{ "note": "n", "fetched": "2026-10-24", "entries": [ 3 ] }""", "not a JSON object"),
            ("""{ "note": "n", "fetched": "2026-10-24", "entries": [ { "subject": 1, "spkiSha256": "x", "purpose": "tls", "source": "s", "date": "2026-10-17" } ] }""", "'subject'"),
        ];
        foreach (var (text, why) in malformed)
        {
            var refused = Assert.Throws<TlsDenylistUnreadableException>(() => TlsDenylist.Parse(text));
            Assert.True(refused.Message.Contains(why, StringComparison.Ordinal), $"for {text}: {refused.Message}");
            Assert.Contains(TlsDenylist.FileName, refused.Message);
            Assert.Contains("Reinstall TradeAgent", refused.Message);
        }

        // And a well-formed one reads.
        var list = TlsDenylist.Parse(File(Entry()));
        Assert.Equal("Test CA", Assert.Single(list.Entries).Subject);
        Assert.Equal("Test CA", list.Find(Hash)?.Subject);
        Assert.Null(list.Find(GoCl851047[0]));
    }

    [Fact]
    public void The_shipped_list_holds_the_eight_entries_with_their_sources()
    {
        var list = TlsDenylist.Load(TlsDenylist.ShippedPath);

        Assert.Equal(8, list.Entries.Count);
        Assert.All(list.Entries, e =>
        {
            Assert.Matches(new Regex("^[0-9a-f]{64}$"), e.SpkiSha256);
            Assert.StartsWith("EM ", e.Subject);
            Assert.Contains("Go CL 851047", e.Source);
            Assert.Contains("Mozilla bug 2083449", e.Source);
            Assert.Equal("2026-10-17", e.Date);
        });
        Assert.Equal(GoCl851047.Order(StringComparer.Ordinal), list.Entries.Select(e => e.SpkiSha256).Order(StringComparer.Ordinal));
        Assert.Equal(6, list.Entries.Count(e => e.Purpose == "tls"));
        Assert.Equal(2, list.Entries.Count(e => e.Purpose == "code-signing"));
    }

    /// <summary>
    /// THE HASH IS OF THE KEY, AS THE POLICY COMPUTES IT: the real EM DV TLS CA - G3B certificate (crt.sh, SHA-256 42C4ACCD…1493, the one
    /// Mozilla bug 2083449 comment 0 names) hashes to the shipped entry with that name — not to its certificate fingerprint.
    /// </summary>
    [Fact]
    public void The_shipped_hash_is_what_the_policy_computes_for_the_real_certificate()
    {
        using var cert = X509Certificate2.CreateFromPem(EmDvTlsCaG3B);
        Assert.Equal("42C4ACCD12C4F59597925E1A08A7A90102608543809308D50EC427E118BA1493", cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));

        var list = TlsDenylist.Load(TlsDenylist.ShippedPath);
        var entry = list.Find(TlsPolicy.SpkiSha256(cert));
        Assert.NotNull(entry);
        Assert.Equal("EM DV TLS CA - G3B", entry.Subject);
    }

    const string EmDvTlsCaG3B = """
        -----BEGIN CERTIFICATE-----
        MIIDaDCCAu6gAwIBAgIQAKSu5njyM2K0wORgt3LyFDAKBggqhkjOPQQDAzBWMQsw
        CQYDVQQGEwJJTjElMCMGA1UECgwcZU11ZGhyYSBUZWNobm9sb2dpZXMgTGltaXRl
        ZDEgMB4GA1UEAwwXZW1TaWduIFJvb3QgVExTIENBIC0gRzMwHhcNMjUwNDMwMTU1
        NDA4WhcNNDAwNDI4MTgzMDAwWjA/MQswCQYDVQQGEwJOTDETMBEGA1UECgwKZU11
        ZGhyYSBCVjEbMBkGA1UEAwwSRU0gRFYgVExTIENBIC0gRzNCMHYwEAYHKoZIzj0C
        AQYFK4EEACIDYgAE557ZwEqXc8RzXlERHahawr1eZKs3S4DhLLwgOOweTw47gS/J
        hZ4eZ6+wTi+LyX90hbplqpEe6KTkW2LVzAPVa+Z4s8OMtNyTfKJGDiOEU8iSk10p
        1E+R4VYGtjzO2rRoo4IBljCCAZIwHwYDVR0jBBgwFoAU4TqrIXzx401UrtRyt/2L
        WUIgkFQwHQYDVR0OBBYEFOIeL+ccGTkOVUtiwSAkVzUkiiUUMB0GA1UdJQQWMBQG
        CCsGAQUFBwMBBggrBgEFBQcDAjBdBgNVHSAEVjBUMDkGCysGAQQBg44hAQABMCow
        KAYIKwYBBQUHAgEWHGh0dHA6Ly9yZXBvc2l0b3J5LmVtc2lnbi5jb20wDQYLKwYB
        BAGDjiEBAmQwCAYGZ4EMAQIBMHUGCCsGAQUFBwEBBGkwZzAkBggrBgEFBQcwAYYY
        aHR0cDovL29jc3AtYi5lbXNpZ24uY29tMD8GCCsGAQUFBzAChjNodHRwOi8vcmVw
        b3NpdG9yeS5lbXNpZ24uY29tL2NlcnRzL0VNRFZUTFNDQUczQi5wN2MwNwYDVR0f
        BDAwLjAsoCqgKIYmaHR0cDovL2NybC5lbXNpZ24uY29tLz9yb290dGxzY2FnMy5j
        cmwwDgYDVR0PAQH/BAQDAgEGMBIGA1UdEwEB/wQIMAYBAf8CAQAwCgYIKoZIzj0E
        AwMDaAAwZQIwLmsGXi6oFMJ2klkcEMXMug6gvxQWZWkewiDWVzQyl2L6z6DuQfPO
        Ibh1grvdULDCAjEAxZvLlxUavG69J6wnSjC8XBbqNIQsJ6CE2QdNOe84Ptxb6VG8
        gt2TlfJbHii+KNvK
        -----END CERTIFICATE-----
        """;
}
