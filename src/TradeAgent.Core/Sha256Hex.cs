using System.Security.Cryptography;
using System.Text;

namespace TradeAgent.Core;

/// <summary>
/// SHA-256, LOWER-CASE HEX, IN ONE PLACE.
///
/// <para>There were seven copies of this across five assemblies, in two spellings that happen to
/// agree — <c>Convert.ToHexStringLower(...)</c> and <c>Convert.ToHexString(...).ToLowerInvariant()</c>
/// — and every one of them is load-bearing: a publication's id, a launch record's input hash, a
/// dataset's provenance, the hash the material ledger computes for itself, the key a resumable
/// download is bound to. Two of those are compared against figures written by an earlier build, so
/// a copy that drifted in case or in encoding would not fail loudly; it would make two records of
/// the same bytes disagree.</para>
///
/// <para><b>UTF-8 for text, and no BOM.</b> Stated here rather than at seven call sites, because the
/// encoding is the half of "the hash of this string" that a reader cannot see.</para>
///
/// <para>Nothing here is a security decision — these are content identities and provenance, not
/// passwords, and the app's secrets go through <c>TradeAgent.Security</c>.</para>
/// </summary>
public static class Sha256Hex
{
    /// <summary>The hash of one string's UTF-8 bytes, as 64 lower-case hex characters.</summary>
    public static string Of(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The hash of everything left in a stream, read from where it is.</summary>
    public static string Of(Stream stream) =>
        Convert.ToHexStringLower(SHA256.HashData(stream));

    /// <summary>The same, without holding a thread while a large file is read.</summary>
    public static async Task<string> OfAsync(Stream stream, CancellationToken ct = default) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));

    /// <summary>
    /// The hash of a file's contents, or null when it cannot be opened. Null rather than an
    /// exception because every caller of this overload is recording provenance rather than gating on
    /// it, and a file that is being written now is read again on the next pass.
    /// </summary>
    public static string? OfFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Of(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
