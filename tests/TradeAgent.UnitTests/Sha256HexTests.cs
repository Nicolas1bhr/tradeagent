using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ONE HELPER REPLACING SEVEN COPIES, AND THE PROOF THAT IT CHANGED NOTHING.
///
/// <para>The copies were in two spellings that happen to agree —
/// <c>Convert.ToHexStringLower(...)</c> and <c>Convert.ToHexString(...).ToLowerInvariant()</c> — and
/// several of the figures they produce are compared against values an EARLIER build wrote: a dataset
/// checksum on disk, a publication id already in the table, the key a half-finished download is
/// bound to. A refactor that drifted in case or encoding would not fail loudly; it would make two
/// records of the same bytes disagree, quietly, and only for installations that had upgraded.</para>
///
/// <para>So every assertion below computes the OLD expression, verbatim, beside the new helper.</para>
/// </summary>
public class Sha256HexTests
{
    const string Text = "role\nreport\nthe 1-minute bars have a 40-minute gap on 2026-03-09";

    static byte[] Bytes => "open,high,low,close\n1,2,3,4\n"u8.ToArray();

    // ---- the two old spellings, written out ----------------------------------------------------

    static string OldLower(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static string OldToLowerInvariant(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void The_helper_is_byte_identical_to_both_spellings_it_replaced()
    {
        Assert.Equal(OldLower(Text), Sha256Hex.Of(Text));
        Assert.Equal(OldToLowerInvariant(Text), Sha256Hex.Of(Text));

        // Empty and non-ASCII, because UTF-8 is the half of "the hash of this string" a reader
        // cannot see, and the copies never said which encoding they meant.
        Assert.Equal(OldLower(""), Sha256Hex.Of(""));
        Assert.Equal(OldLower("café — naïve"), Sha256Hex.Of("café — naïve"));
    }

    [Fact]
    public async Task The_stream_overloads_are_byte_identical_to_the_copies_they_replaced()
    {
        var file = Path.Combine(TestEnv.Home, $"sha-{Guid.NewGuid():n}.csv");
        await File.WriteAllBytesAsync(file, Bytes);

        string old;
        using (var fs = File.OpenRead(file)) old = Convert.ToHexStringLower(SHA256.HashData(fs));

        using (var fs = File.OpenRead(file)) Assert.Equal(old, Sha256Hex.Of(fs));
        await using (var fs = File.OpenRead(file)) Assert.Equal(old, await Sha256Hex.OfAsync(fs));
        Assert.Equal(old, Sha256Hex.OfFile(file));

        // The MaterialScanner's spelling, which was the other one.
        using (var fs = File.OpenRead(file))
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(), Sha256Hex.Of(fs));
    }

    /// <summary>
    /// EACH OLD CALL SITE, ASKED FOR ITS ANSWER AND CHECKED AGAINST THE EXPRESSION IT USED TO
    /// CONTAIN. The point of the sweep is that these five figures are unchanged, not that the helper
    /// is correct on its own.
    /// </summary>
    [Fact]
    public async Task Every_call_site_still_answers_exactly_what_it_answered_before()
    {
        var file = Path.Combine(TestEnv.Home, $"sha-site-{Guid.NewGuid():n}.csv");
        await File.WriteAllBytesAsync(file, Bytes);

        string ofFile;
        using (var fs = File.OpenRead(file)) ofFile = Convert.ToHexStringLower(SHA256.HashData(fs));

        // DatasetStore.Sha256 — a dataset's provenance, compared against a figure on disk.
        Assert.Equal(ofFile, DatasetStore.Sha256(file));

        // Downloader.Sha256Async — the archive checksum an installation checks after a download.
        Assert.Equal(ofFile, await Downloader.Sha256Async(file));

        // Publication.IdOf — an artifact's identity, and the primary key a restart relies on.
        Assert.Equal(
            OldToLowerInvariant($"{CouncilRoles.Research}\n{PublicationKind.Report}\n{Text}"),
            Publication.IdOf(CouncilRoles.Research, PublicationKind.Report, Text));

        // MaterialScanner — the hash the ledger computes for itself, over the same bytes.
        var (db, root) = (TestEnv.NewDb(), Path.Combine(TestEnv.Home, $"sha-ws-{Guid.NewGuid():n}"));
        using var _ = db;
        var dropped = Path.Combine(root, MaterialScanner.AgentDir, "data", "march.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(dropped)!);
        await File.WriteAllBytesAsync(dropped, Bytes);

        new MaterialScanner(db, root).Scan();
        Assert.Equal(ofFile,
            Assert.Single(new MaterialStore(db).Present(MaterialOrigin.Agent)).Sha256);
    }
}
