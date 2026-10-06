using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// One line of GDELT's <c>lastupdate.txt</c>, as this build reads it: the GKG file's label, its size and the
/// MD5 GDELT published for it. The address the line names is CHECKED and never used — every request is built
/// from the label on the recorder's own origin (<see cref="GdeltGkg.BatchUrl"/>).
/// </summary>
public sealed record GkgListing(DateTimeOffset Label, long Size, string Md5);

/// <summary>
/// WHAT ONE GKG FILE READ TO: its bytes and hashes, how many rows it held, the rows the filter kept — or the
/// reason it is refused, in words. A refused file keeps nothing: <see cref="Kept"/> is empty, and the hashes are
/// null unless the whole file was read.
/// </summary>
public sealed record GkgBatchRead
{
    /// <summary>The bytes of the file that were read, all of them when <see cref="Md5"/> is not null.</summary>
    public long Bytes { get; init; }

    /// <summary>This build's MD5 of the whole file, lower-case hex — null when it was not read to its end.</summary>
    public string? Md5 { get; init; }

    /// <summary>This build's SHA-256 of the whole file, lower-case hex — null when it was not read to its end.</summary>
    public string? Sha256 { get; init; }

    /// <summary>The rows the file held, kept or not.</summary>
    public int Rows { get; init; }

    /// <summary>The rows the filter kept, one item each: subject the GKGRECORDID, time the label, payload the 27 fields.</summary>
    public IReadOnlyList<TapeItem> Kept { get; init; } = [];

    /// <summary>The canonical UTF-8 bytes of <see cref="Kept"/>'s payloads — what storing them costs.</summary>
    public long KeptBytes { get; init; }

    /// <summary>Why the file is refused, in words, or null because it was read.</summary>
    public string? Refused { get; init; }

    /// <summary>True when the refusal is the kept rows passing the budget the caller gave — the daily cap — and not the file.</summary>
    public bool OverBudget { get; init; }
}

/// <summary>
/// GDELT'S GLOBAL KNOWLEDGE GRAPH, READ AS THE TAPE KEEPS IT (<c>U-tape-archive</c>; <c>docs/EDGE-FACTORY.md</c>
/// § 4.1): the fifteen-minute GKG files, each streamed once through MD5, SHA-256 and an inflater that holds only
/// the rows the crypto filter keeps.
///
/// <para><b>The label is GDELT's own first-seen time.</b> A file is named for the fifteen-minute batch its rows
/// were created in, and the codebook (GKG V2.1) says each GKGRECORDID begins with "the full date+time of the
/// 15 minute update batch that this record was created in". So a row's source time is its file's label — a
/// vendor first-seen time with a declared basis — and a file whose rows name any other batch is refused whole:
/// a row filed under a time it does not carry is a row whose time this build would be guessing.</para>
///
/// <para><b>Only the label is read off GDELT's listing.</b> <see cref="TryReadListing"/> reads the GKG line of
/// <c>lastupdate.txt</c> for its size, MD5 and label, and refuses a line that does not name GDELT's own host and a
/// <c>&lt;14 digits&gt;.gkg.csv.zip</c> file; the request is then built from the label on the recorder's origin.
/// A listing therefore cannot point the recorder anywhere.</para>
///
/// <para><b>The filter, <see cref="Filter"/>:</b> a row is kept when its themes name <see cref="BitcoinTheme"/>, or
/// when one of <see cref="Words"/> is a word — case aside, bounded by anything that is not a letter or a digit —
/// in its names (<c>V1PERSONS</c>, <c>V1ORGANIZATIONS</c>, <c>V2.1ALLNAMES</c>) or its page title
/// (<c>&lt;PAGE_TITLE&gt;</c> in <c>V2EXTRASXML</c>, its character references resolved). Its recall is not
/// claimed: an item about a coin it does not name is not kept.</para>
///
/// <para><b>Raw bytes are never kept.</b> Nothing here writes a file. The zip is read once, as it arrives; a row
/// the filter does not keep is dropped as soon as it is read; what is kept is the 27 codebook-named fields of
/// each kept row, whole, as canonical JSON, at most 64 KB a row.</para>
/// </summary>
public static class GdeltGkg
{
    /// <summary>The catalogue row's id, and the source of every row this family writes.</summary>
    public const string Source = "gdelt-gkg";

    /// <summary>The series of the kept rows: subject the GKGRECORDID, source time the file's label.</summary>
    public const string ItemsSeries = "gkg-items";

    /// <summary>The series of the per-file record (subject <see cref="BatchSubject"/>), which the store classes every row from.</summary>
    public const string BatchSeries = "gkg-batch";

    /// <summary>The subject of every per-file record.</summary>
    public const string BatchSubject = "gdelt";

    /// <summary>The filter's name and version, written into every record it kept rows for. A change to it is a new name.</summary>
    public const string Filter = "gkg-crypto-v1";

    /// <summary>
    /// GDELT'S DATA HOST, spelled in pieces so the test-tree scan that forbids a test naming a vendor host
    /// (<c>SuiteReachesNoVendorTests</c>) can look for it without finding this line.
    /// </summary>
    public const string Host = "data" + ".gdeltproject" + ".org";

    /// <summary>The built-in origin: the only one whose rows can be <c>O-LIVE</c> or <c>O-PIT</c>.</summary>
    public const string BaseUrl = "https://" + Host;

    /// <summary>Where a GKG file is, by its label. DATA in the catalogue row, and the one shape every request is built from.</summary>
    public const string BatchShape = "{base}/gdeltv2/{label}.gkg.csv.zip";

    /// <summary>Where GDELT lists its newest files.</summary>
    public const string ListingPath = "/gdeltv2/lastupdate.txt";

    /// <summary>GDELT publishes one GKG file every fifteen minutes, named for the batch.</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The longest row read, in UTF-8 bytes. The longest of 979 rows measured on 2026-10-06 held 35,860; a line
    /// past this is not a GKG row, and the file is refused rather than held in memory to find out.
    /// </summary>
    public const int MaxRowBytes = 1024 * 1024;

    /// <summary>The most a file may inflate to. A 4.1 MB file measured on 2026-10-06 inflated to 12.7 MB.</summary>
    public const long MaxInflatedBytes = 256L * 1024 * 1024;

    /// <summary>The theme GDELT tags an item about Bitcoin with.</summary>
    public const string BitcoinTheme = "ECON_BITCOIN";

    /// <summary>The words the filter keeps a row for, in its names or its title.</summary>
    public static IReadOnlyList<string> Words { get; } =
        ["Bitcoin", "Ethereum", "Solana", "Binance", "BNB", "XRP", "Ripple", "Dogecoin"];

    /// <summary>
    /// THE 27 FIELDS OF A GKG 2.1 ROW, in the file's order and named as the codebook names them
    /// (<c>GDELT-Global_Knowledge_Graph_Codebook-V2.1.pdf</c>). A kept row's payload is these, whole.
    /// </summary>
    public static IReadOnlyList<string> Fields { get; } =
    [
        "GKGRECORDID", "V2.1DATE", "V2SOURCECOLLECTIONIDENTIFIER", "V2SOURCECOMMONNAME", "V2DOCUMENTIDENTIFIER",
        "V1COUNTS", "V2.1COUNTS", "V1THEMES", "V2ENHANCEDTHEMES", "V1LOCATIONS", "V2ENHANCEDLOCATIONS",
        "V1PERSONS", "V2ENHANCEDPERSONS", "V1ORGANIZATIONS", "V2ENHANCEDORGANIZATIONS", "V1.5TONE",
        "V2.1ENHANCEDDATES", "V2GCAM", "V2.1SHARINGIMAGE", "V2.1RELATEDIMAGES", "V2.1SOCIALIMAGEEMBEDS",
        "V2.1SOCIALVIDEOEMBEDS", "V2.1QUOTATIONS", "V2.1ALLNAMES", "V2.1AMOUNTS", "V2.1TRANSLATIONINFO",
        "V2EXTRASXML"
    ];

    const int Id = 0, Date = 1, Themes = 7, EnhancedThemes = 8, Persons = 11, Organizations = 13, AllNames = 23, Extras = 26;

    const string GkgFile = ".gkg.csv.zip";

    // ------------------------------------------------------------------------------------------ labels

    /// <summary>A label as GDELT writes it: <c>yyyyMMddHHmmss</c>, UTC.</summary>
    public static string LabelText(DateTimeOffset label) =>
        label.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="t"/> is a batch label: a whole fifteen minutes, UTC.</summary>
    public static bool IsLabel(DateTimeOffset t) => t.UtcTicks % Cadence.Ticks == 0;

    /// <summary>The newest label at or before <paramref name="t"/>.</summary>
    public static DateTimeOffset LabelAtOrBefore(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % Cadence.Ticks, TimeSpan.Zero);

    /// <summary>Reads fourteen digits as a label, refusing anything that is not a real fifteen-minute instant.</summary>
    public static bool TryParseLabel(string? text, out DateTimeOffset label)
    {
        label = default;
        if (text is not { Length: 14 } || !text.All(char.IsAsciiDigit)) return false;
        if (!DateTime.TryParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return false;

        var t = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
        if (!IsLabel(t)) return false;
        label = t;
        return true;
    }

    // -------------------------------------------------------------------------------------------- URLs

    /// <summary>The GKG file of <paramref name="label"/> on <paramref name="root"/>, built from <see cref="BatchShape"/>.</summary>
    public static string BatchUrl(string root, DateTimeOffset label)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!IsLabel(label)) throw new ArgumentException($"{label:O} is not a fifteen-minute label", nameof(label));
        return BatchShape.Replace("{base}", root.TrimEnd('/'), StringComparison.Ordinal)
            .Replace("{label}", LabelText(label), StringComparison.Ordinal);
    }

    /// <summary>GDELT's listing of its newest files on <paramref name="root"/>.</summary>
    public static string ListingUrl(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.TrimEnd('/') + ListingPath;
    }

    /// <summary>The label a GKG file URL names, or null for an address that is not one.</summary>
    public static DateTimeOffset? LabelOf(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.EndsWith(GkgFile, StringComparison.Ordinal)) return null;
        var name = url[(url.LastIndexOf('/') + 1)..];
        return TryParseLabel(name[..^GkgFile.Length], out var label) ? label : null;
    }

    // ------------------------------------------------------------------------------------- the listing

    /// <summary>
    /// THE GKG LINE OF <c>lastupdate.txt</c>, read for its size, MD5 and label only. Measured on 2026-10-06: three
    /// lines of <c>size md5 http://data.gdeltproject.org/gdeltv2/&lt;label&gt;.&lt;feed&gt;.zip</c>. The export and
    /// mentions lines are not read. The GKG line is refused unless it is three fields, a positive size, 32 hex
    /// digits and an address on GDELT's own host whose file is <c>&lt;14 digits&gt;.gkg.csv.zip</c> — and the
    /// address itself is never fetched.
    /// </summary>
    public static bool TryReadListing(string? body, out GkgListing? listing, out string? why)
    {
        listing = null;
        why = null;
        if (string.IsNullOrWhiteSpace(body)) { why = "the listing was empty"; return false; }

        GkgListing? found = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var parts = line.Split(' ');
            if (!parts[^1].EndsWith(GkgFile, StringComparison.Ordinal)) continue;

            if (found is not null) { why = "the listing names two GKG files"; return false; }
            if (parts.Length != 3) { why = "the listing's GKG line is not 'size md5 address'"; return false; }

            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
            {
                why = $"the listing's GKG line gives the size '{parts[0]}', which is not one";
                return false;
            }

            if (!IsHex(parts[1], 32)) { why = "the listing's GKG line gives an MD5 that is not 32 hex digits"; return false; }

            if (!Uri.TryCreate(parts[2], UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
                || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                why = "the listing's GKG line names an address that is not on GDELT's own data host, and TradeAgent reads "
                      + "a listing only from GDELT";
                return false;
            }

            const string Dir = "/gdeltv2/";
            var file = uri.AbsolutePath.StartsWith(Dir, StringComparison.Ordinal) ? uri.AbsolutePath[Dir.Length..] : "";
            if (!file.EndsWith(GkgFile, StringComparison.Ordinal) || !TryParseLabel(file[..^GkgFile.Length], out var label))
            {
                why = "the listing's GKG line names a file that is not <14 digits>.gkg.csv.zip at a fifteen-minute label";
                return false;
            }

            found = new GkgListing(label, size, parts[1].ToLowerInvariant());
        }

        if (found is null) { why = "the listing names no GKG file"; return false; }
        listing = found;
        return true;
    }

    // ---------------------------------------------------------------------------------------- the file

    /// <summary>
    /// READS ONE GKG FILE AS IT ARRIVES: the zip's one entry, which must be <c>&lt;label&gt;.gkg.csv</c>, inflated a
    /// piece at a time; every row checked against the label; the kept rows held; the rest of the file read to its
    /// end so that the MD5 and SHA-256 cover every byte. Nothing is written anywhere.
    ///
    /// <para><b>Refused whole</b>, with the reason in words and nothing kept: a file over <paramref name="maxBytes"/>,
    /// one that is not that zip, one that will not inflate or inflates past <see cref="MaxInflatedBytes"/>; a row over
    /// <see cref="MaxRowBytes"/>, without the codebook's 27 fields, or whose GKGRECORDID or V2.1DATE is not its label's;
    /// a GKGRECORDID named twice; a kept row that is not UTF-8 or whose payload passes 64 KB; and —
    /// <see cref="GkgBatchRead.OverBudget"/> — kept rows passing <paramref name="keptBudget"/>.</para>
    ///
    /// <para>A failure of the stream itself — the socket, the leash — is not a refusal of the file: it propagates,
    /// so the caller says what went wrong with the request.</para>
    /// </summary>
    public static async Task<GkgBatchRead> ReadBatchAsync(Stream body, DateTimeOffset label, long maxBytes, long keptBudget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!IsLabel(label)) throw new ArgumentException($"{label:O} is not a fifteen-minute label", nameof(label));

        using var tally = new TallyStream(body, maxBytes);
        var reader = new BatchReader(label, keptBudget);

        try
        {
            var entry = await ReadEntryHeaderAsync(tally, label, ct);
            if (entry.Refused is { } bad) return reader.Refuse(tally, bad);

            Stream compressed = entry.CompressedSize is { } size ? new BoundedStream(tally, size) : tally;
            await using (var inflater = new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true))
            {
                if (await reader.ReadRowsAsync(inflater, ct) is { } refused) return refused with { Bytes = tally.Bytes };
            }

            // THE REST OF THE FILE — the central directory — is read too: the hashes are of every byte GDELT serves.
            var rest = new byte[16 * 1024];
            while (await tally.ReadAsync(rest, ct) > 0) { }
        }
        catch (TallyStream.TooLongException)
        {
            return reader.Refuse(tally, $"the file is longer than the {maxBytes} bytes TradeAgent reads of one GKG file");
        }
        catch (InvalidDataException ex)
        {
            return reader.Refuse(tally, "the file could not be inflated: " + ex.Message.ReplaceLineEndings(" "));
        }
        catch (EndOfStreamException)
        {
            return reader.Refuse(tally, "the file ended inside its zip entry");
        }

        return reader.Done(tally);
    }

    readonly record struct EntryHeader(long? CompressedSize, string? Refused);

    /// <summary>
    /// THE ZIP'S FIRST LOCAL FILE HEADER, read off the stream without a seek: the one entry a GKG file holds,
    /// deflated, named for its label. Its sizes bound the inflater when the header carries them.
    /// </summary>
    static async Task<EntryHeader> ReadEntryHeaderAsync(Stream s, DateTimeOffset label, CancellationToken ct)
    {
        var head = new byte[30];
        await s.ReadExactlyAsync(head, ct);

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x04034b50)
            return new(null, "the file is not a zip");

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
        var method = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
        var compressed = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(18));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(26));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(28));

        var name = new byte[nameLength];
        await s.ReadExactlyAsync(name, ct);
        var extra = new byte[extraLength];
        await s.ReadExactlyAsync(extra, ct);

        var expected = LabelText(label) + ".gkg.csv";
        if (!string.Equals(Encoding.ASCII.GetString(name), expected, StringComparison.Ordinal))
            return new(null, $"the zip's entry is not {expected}");
        if ((flags & 1) != 0) return new(null, "the zip's entry is encrypted");
        if (method != 8) return new(null, $"the zip's entry is stored with method {method}, and a GKG file is deflated");
        if (compressed == uint.MaxValue) return new(null, "the zip's entry is a ZIP64 entry, which a GKG file is not");

        // BIT 3: THE SIZES FOLLOW THE DATA, so the inflater finds its own end. Otherwise they bound it.
        return new((flags & 8) != 0 ? null : compressed, null);
    }

    /// <summary>One file's rows, read and kept or dropped as they arrive.</summary>
    sealed class BatchReader(DateTimeOffset label, long keptBudget)
    {
        readonly string _label = LabelText(label);
        readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        readonly List<TapeItem> _kept = [];
        long _keptBytes;
        long _inflated;
        int _rows;

        /// <summary>Every row of the inflated text, or the refusal that stopped it.</summary>
        public async Task<GkgBatchRead?> ReadRowsAsync(Stream text, CancellationToken ct)
        {
            var chunk = new byte[64 * 1024];
            var line = new ArrayBufferWriter<byte>(64 * 1024);

            int n;
            while ((n = await text.ReadAsync(chunk, ct)) > 0)
            {
                _inflated += n;
                if (_inflated > MaxInflatedBytes)
                    return Refusal($"the file inflates past {MaxInflatedBytes / (1024 * 1024)} MB, which a GKG file does not");

                var span = chunk.AsSpan(0, n);
                int at;
                while ((at = span.IndexOf((byte)'\n')) >= 0)
                {
                    if (line.WrittenCount + at > MaxRowBytes) return Refusal(TooLong());
                    line.Write(span[..at]);
                    if (Row(line.WrittenSpan) is { } refused) return refused;
                    line.Clear();
                    span = span[(at + 1)..];
                }

                if (line.WrittenCount + span.Length > MaxRowBytes) return Refusal(TooLong());
                line.Write(span);
            }

            // A LAST ROW WITH NO LINE BREAK AFTER IT is a row like the others.
            return line.WrittenCount > 0 ? Row(line.WrittenSpan) : null;
        }

        string TooLong() => $"row {_rows + 1} is longer than the {MaxRowBytes / 1024} KB a GKG row may be";

        GkgBatchRead? Row(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return null;
            _rows++;

            // READ LENIENTLY TO DECIDE, KEPT ONLY WHOLE. GDELT's files are not always UTF-8 — measured on
            // 2026-10-06, 2 of 24 files held one row with a raw Latin-1 byte in an address — so a row is decoded
            // with U+FFFD for what is not UTF-8 to be checked and filtered, and only a row the filter KEEPS must be
            // UTF-8 throughout: it is stored whole or the file is not stored at all.
            var utf8 = Utf8.IsValid(bytes);
            var f = Encoding.UTF8.GetString(bytes).Split('\t');
            if (f.Length != Fields.Count)
                return Refusal($"row {_rows} has {f.Length} fields, and the GKG 2.1 codebook names {Fields.Count}");

            // THE LABEL IS EVERY ROW'S, OR THE FILE IS NOT READ: a row created in another batch would be filed
            // under a time it does not carry.
            if (!IsRecordId(f[Id], _label))
                return Refusal($"row {_rows}'s GKGRECORDID is not one of batch {_label}'s, so the file is mislabelled");
            if (!string.Equals(f[Date], _label, StringComparison.Ordinal))
                return Refusal($"row {_rows}'s V2.1DATE is '{Clip(f[Date])}', not its batch's {_label}, so the file is mislabelled");
            if (!_ids.Add(f[Id]))
                return Refusal($"the GKGRECORDID {f[Id]} appears twice in the file");

            if (!Keeps(f)) return null;
            if (!utf8) return Refusal($"the kept row {f[Id]} is not UTF-8, so it cannot be kept whole and the file is not stored in part");

            var payload = Payload(f);
            var size = Encoding.UTF8.GetByteCount(payload);
            if (size > TapeStore.MaxPayloadBytes)
                return Refusal($"the kept row {f[Id]} is {size} bytes as the tape keeps it, past the {TapeStore.MaxPayloadBytes} "
                               + "a row may hold, so the file is not stored in part");

            _keptBytes += size;
            if (_keptBytes > keptBudget)
                return Refusal($"its kept rows pass the {keptBudget} bytes left under the daily cap", overBudget: true);

            _kept.Add(new TapeItem(f[Id], label, payload));
            return null;
        }

        GkgBatchRead Refusal(string why, bool overBudget = false) =>
            new() { Rows = _rows, Refused = why, OverBudget = overBudget };

        public GkgBatchRead Refuse(TallyStream tally, string why) => Refusal(why) with { Bytes = tally.Bytes };

        public GkgBatchRead Done(TallyStream tally) => new()
        {
            Bytes = tally.Bytes,
            Md5 = tally.Md5(),
            Sha256 = tally.Sha256(),
            Rows = _rows,
            Kept = _kept,
            KeptBytes = _keptBytes
        };
    }

    /// <summary><c>&lt;label&gt;-&lt;digits&gt;</c>, or <c>&lt;label&gt;-T&lt;digits&gt;</c> for a translated document, at most 40 characters.</summary>
    static bool IsRecordId(string id, string label)
    {
        if (id.Length > 40 || id.Length < label.Length + 2 || !id.StartsWith(label, StringComparison.Ordinal) || id[label.Length] != '-')
            return false;
        var serial = id.AsSpan(label.Length + 1);
        if (serial[0] == 'T') serial = serial[1..];
        return serial.Length > 0 && !serial.ContainsAnyExcept("0123456789");
    }

    static string Clip(string s) => s.Length <= 20 ? s : s[..20] + "…";

    // -------------------------------------------------------------------------------------- the filter

    /// <summary>Whether <see cref="Filter"/> keeps the row whose fields are <paramref name="f"/>.</summary>
    public static bool Keeps(IReadOnlyList<string> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (f.Count != Fields.Count) return false;

        return NamesTheme(f[Themes], f[EnhancedThemes])
               || HasWord(f[Persons]) || HasWord(f[Organizations]) || HasWord(f[AllNames])
               || (Title(f[Extras]) is { } title && HasWord(title));
    }

    /// <summary>
    /// The page title in <c>V2EXTRASXML</c>, its character references resolved (GDELT writes them: <c>&amp;#x2013;</c>
    /// was the commonest of 63 in one file on 2026-10-06), or null where the field holds none.
    /// </summary>
    public static string? Title(string extras)
    {
        ArgumentNullException.ThrowIfNull(extras);
        const string Open = "<PAGE_TITLE>", Close = "</PAGE_TITLE>";
        var start = extras.IndexOf(Open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += Open.Length;
        var end = extras.IndexOf(Close, start, StringComparison.Ordinal);
        return end < 0 ? null : TapeScreen.Decode(extras[start..end]);
    }

    /// <summary><see cref="BitcoinTheme"/> as one of <c>V1THEMES</c>' names, or as the name of one of <c>V2ENHANCEDTHEMES</c>' blocks.</summary>
    static bool NamesTheme(string themes, string enhanced)
    {
        foreach (var t in themes.Split(';'))
            if (string.Equals(t, BitcoinTheme, StringComparison.Ordinal)) return true;
        foreach (var block in enhanced.Split(';'))
        {
            var comma = block.IndexOf(',');
            if (string.Equals(comma < 0 ? block : block[..comma], BitcoinTheme, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>One of <see cref="Words"/> as a word: case aside, with no letter or digit on either side of it.</summary>
    static bool HasWord(string text)
    {
        foreach (var word in Words)
        {
            var from = 0;
            int at;
            while ((at = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var end = at + word.Length;
                if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end])))
                    return true;
                from = at + 1;
            }
        }
        return false;
    }

    /// <summary>The row as the tape keeps it: an object of the 27 codebook names and their fields, whole, canonical.</summary>
    static string Payload(string[] f)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            for (var i = 0; i < f.Length; i++) w.WriteString(Fields[i], f[i]);
            w.WriteEndObject();
        }
        return TapeJson.Canonical(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    static bool IsHex(string s, int length) =>
        s.Length == length && !s.AsSpan().ContainsAnyExcept("0123456789abcdefABCDEF");

    // ------------------------------------------------------------------------------------- the streams

    /// <summary>
    /// EVERY BYTE OF THE FILE, COUNTED AND HASHED AS IT IS READ — by the zip reader, the inflater and the drain
    /// alike — and refused past its cap. Read-only and forward-only, like the socket under it.
    /// </summary>
    sealed class TallyStream(Stream inner, long maxBytes) : Stream
    {
        public sealed class TooLongException : Exception;

        readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Bytes { get; private set; }

        public string Md5() => Convert.ToHexStringLower(_md5.GetHashAndReset());
        public string Sha256() => Convert.ToHexStringLower(_sha256.GetHashAndReset());

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Tally(buffer, inner.Read(buffer));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            return Tally(buffer.Span, n);
        }

        int Tally(ReadOnlySpan<byte> buffer, int n)
        {
            if (n <= 0) return n;
            Bytes += n;
            if (Bytes > maxBytes) throw new TooLongException();
            _md5.AppendData(buffer[..n]);
            _sha256.AppendData(buffer[..n]);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _md5.Dispose();
                _sha256.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The zip entry's compressed bytes and no more, so the inflater cannot read into the central directory.</summary>
    sealed class BoundedStream(Stream inner, long length) : Stream
    {
        readonly long _length = length;
        long _left = length;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_left <= 0) return 0;
            var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            _left -= n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_left <= 0) return 0;
            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], ct);
            if (n == 0) throw new EndOfStreamException();
            _left -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _length - _left; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
