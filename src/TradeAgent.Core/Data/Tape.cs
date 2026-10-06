using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeAgent.Core.Data;

/// <summary>
/// WHAT A TAPE OBSERVATION COUNTS AS — computed by the app per observation from fields it recorded
/// itself, and never accepted from a caller (<c>docs/EDGE-FACTORY.md</c> § 4.1, R07 § 5).
///
/// <para>Four words, of which this build writes three: <see cref="Live"/>, <see cref="Pit"/> — since
/// <c>U-tape-archive</c>, for a file of a vendor's archive fetched late, whose vendor checksum verifies and which
/// the vendor's storage says was written no later than the first-seen time it declares — and <see cref="Arch"/>.
/// <see cref="Hind"/> is built with hindsight. All four are spelled here, and in the column's <c>CHECK</c>, so a
/// later unit adds a WRITER and never a table rebuild.</para>
/// </summary>
public static class TapeClass
{
    /// <summary>First received from the source's built-in origin within its cadence, its documented publication delay and 30 s of its source time.</summary>
    public const string Live = "O-LIVE";

    /// <summary>
    /// A vendor-checksummed file fetched late from the source's built-in origin: its published MD5 matches this build's
    /// and the vendor's storage dates it no later than its declared first-seen time (<c>U-tape-archive</c>).
    /// </summary>
    public const string Pit = "O-PIT";

    /// <summary>Anything else fetched after the fact — late, from another origin, or from a row a file added.</summary>
    public const string Arch = "O-ARCH";

    /// <summary>Built with hindsight. Never written by the tape's own collector.</summary>
    public const string Hind = "O-HIND";

    /// <summary>
    /// THE ORDER "NEVER UPGRADED" IS MEASURED IN: live above a checked print above archive above
    /// hindsight. A word this build does not know ranks lowest, so it can only ever pull a class down.
    /// </summary>
    static int Rank(string? c) => c switch { Live => 3, Pit => 2, Arch => 1, _ => 0 };

    /// <summary>The lower of two classes — the most a later revision of a datum may be.</summary>
    public static string Lower(string a, string b) => Rank(a) <= Rank(b) ? a : b;
}

/// <summary>
/// ONE ATTEMPT TO FETCH ONE SERIES OF ONE SOURCE, SUCCEEDED OR FAILED, as the collector observed it.
///
/// <para>It carries no ORIGIN: <c>TapeStore</c> computes that from <see cref="Url"/> with
/// <see cref="UrlOrigin"/>, so the address a row was fetched from and the address the evidence class
/// is decided by can never be two different claims.</para>
/// </summary>
public sealed record TapeFetch
{
    /// <summary>The catalogue row's id, e.g. <c>binance-um-premium</c>.</summary>
    public required string Source { get; init; }

    /// <summary>The series of that row this attempt asked for, e.g. <c>premium-index</c>.</summary>
    public required string Series { get; init; }

    /// <summary>The URL asked for, as asked. The origin and the window are read off it.</summary>
    public required string Url { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>When the answer — or the failure — was in hand. Every observation of this fetch arrived then.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The HTTP status, or null when nothing was answered at all (a timeout, a refused socket).</summary>
    public int? HttpStatus { get; init; }

    /// <summary>The SHA-256 of the body this build computed. NEVER a vendor's: there is none.</summary>
    public string? BodySha256 { get; init; }

    /// <summary>Why this attempt delivered nothing, in words, or null for one that delivered.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// ONE ITEM OF AN ANSWER, before the store has decided what it is.
///
/// <para>No natural key, no revision, no hash and no class: the store computes all four, so none of
/// them can be a caller's claim. The natural key is <c>subject|source time in milliseconds</c> — the
/// vendor's own time field for each series (<c>time</c>, <c>timestamp</c>, <c>fundingTime</c>) — and the
/// payload is made canonical by the store whatever spacing or key order it arrives in.</para>
/// </summary>
public sealed record TapeItem(string Subject, DateTimeOffset SourceTime, string Payload);

/// <summary>
/// ONE STORED OBSERVATION: what the vendor said about one subject at one source time, which reading
/// of it this is, when it arrived, which fetch brought it, what it counts as — and whether the screen,
/// run at this read, quarantines it (<see cref="TapeScreen"/>).
///
/// <para><see cref="Payload"/> is the item as recorded, or NULL where this read withheld it: a quarantined
/// item read through <c>TapeStore.AsOf</c>. <see cref="Quarantine"/> says why, by rule and screen version,
/// and never with the text.</para>
/// </summary>
public sealed record TapeObservation(
    long Id, string Source, string Series, string Subject, DateTimeOffset SourceTime,
    DateTimeOffset ReceivedAt, long FetchId, string NaturalKey, int Revision, string PayloadSha256,
    string? Payload, string EvidenceClass, TapeQuarantine? Quarantine);

/// <summary>One stored attempt, with the origin the store computed from its URL.</summary>
public sealed record TapeFetchRecord(
    long Id, string Source, string Series, string Url, string? Origin, DateTimeOffset RequestedAt,
    DateTimeOffset ReceivedAt, int? HttpStatus, int Items, string? BodySha256, string? Note);

/// <summary>
/// ONE FILE OF A VENDOR'S CHECKSUMMED ARCHIVE, AS THE RECORDER READ IT (<c>U-tape-archive</c>): the facts
/// <c>TapeStore.AppendArchive</c> writes into the file's own record and classes every row of it from.
///
/// <para>It carries no class, no kept count and no kept size: the store computes all three from what it writes.
/// It carries no arrival instant either — that is the fetch's <see cref="TapeFetch.ReceivedAt"/>, the record's
/// own <c>received_at</c> — so the same file read twice is the same record, and the second reading writes
/// nothing.</para>
/// </summary>
public sealed record TapeArchiveBatch
{
    /// <summary>The series of the file's record, e.g. <c>gkg-batch</c>.</summary>
    public required string RecordSeries { get; init; }

    /// <summary>The subject of the file's record, e.g. <c>gdelt</c>.</summary>
    public required string RecordSubject { get; init; }

    /// <summary>The series of the file's kept rows, e.g. <c>gkg-items</c>.</summary>
    public required string ItemsSeries { get; init; }

    /// <summary>The vendor's first-seen time for the file — the source time of its record and of every row.</summary>
    public required DateTimeOffset Label { get; init; }

    /// <summary>The bytes of the file, all of which the hashes below cover.</summary>
    public required long Bytes { get; init; }

    /// <summary>The MD5 the vendor published for the file, hex.</summary>
    public required string PublishedMd5 { get; init; }

    /// <summary>This build's MD5 of the bytes it read, hex.</summary>
    public required string ComputedMd5 { get; init; }

    /// <summary>This build's SHA-256 of the bytes it read, hex.</summary>
    public required string Sha256 { get; init; }

    /// <summary>When the vendor's storage says the file was last written, or null where it did not say.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>The rows the file held, kept or not.</summary>
    public required int Rows { get; init; }

    /// <summary>The name and version of the filter that chose the kept rows, e.g. <c>gkg-crypto-v1</c>.</summary>
    public required string Filter { get; init; }
}

/// <summary>
/// WHAT ONE <c>Append</c> DID. <see cref="Items"/> is what the answer carried; <see cref="Stored"/> is
/// what was new — a first reading or a revision, <see cref="Revised"/> of them revisions — and
/// <see cref="Unchanged"/> is the re-readings that matched the latest revision and wrote nothing.
/// </summary>
public sealed record TapeAppend(long FetchId, int Items, int Stored, int Revised, int Unchanged);

/// <summary>
/// ONE SPELLING OF A JSON VALUE, SO THAT "THE SAME PAYLOAD" MEANS THE SAME BYTES.
///
/// <para>Objects with their keys in ordinal order, no whitespace, arrays in the order served, and —
/// the reason this is not <c>JsonSerializer</c> — every value exactly as the vendor wrote it.
/// <b>A decimal string stays the string it was</b>: Binance serves <c>"0.00010000"</c>, and a build
/// that parsed it into a number and wrote it back would store <c>0.0001</c>, a value the vendor never
/// published, and would call a re-reading that differs only in a trailing zero a revision. A JSON
/// number is written back as its own raw text for the same reason.</para>
///
/// <para>An object that names the same key twice is refused rather than spelled: which of the two a
/// later reader would see depends on the reader, and a payload whose meaning depends on who reads it
/// is not one this ledger can say it holds.</para>
/// </summary>
public static class TapeJson
{
    /// <summary>The canonical spelling of <paramref name="json"/>. Throws <see cref="JsonException"/> for anything that is not one JSON value.</summary>
    public static string Canonical(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
            Write(w, doc.RootElement);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The SHA-256 of a payload's UTF-8 bytes, lower-case hex. Computed by this build, of the canonical text.</summary>
    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static void Write(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var properties = e.EnumerateObject().ToList();
                foreach (var p in properties)
                    if (!seen.Add(p.Name))
                        throw new JsonException($"the object names '{p.Name}' twice");

                w.WriteStartObject();
                foreach (var p in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    Write(w, p.Value);
                }
                w.WriteEndObject();
                break;

            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) Write(w, item);
                w.WriteEndArray();
                break;

            case JsonValueKind.String:
                w.WriteStringValue(e.GetString());
                break;

            case JsonValueKind.Number:
                // THE NUMBER AS SERVED, never re-formatted: `1.50` stays `1.50`.
                w.WriteRawValue(e.GetRawText());
                break;

            case JsonValueKind.True:
                w.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                w.WriteBooleanValue(false);
                break;

            default:
                w.WriteNullValue();
                break;
        }
    }
}
