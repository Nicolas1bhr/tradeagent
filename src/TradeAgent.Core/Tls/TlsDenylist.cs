using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeAgent.Core.Tls;

/// <summary>One distrusted certificate authority: its name, its public key's hash, what it signs, where the entry comes from, and when.</summary>
/// <param name="Subject">The certificate's common name, as the sources spell it.</param>
/// <param name="SpkiSha256">SHA-256 of the DER SubjectPublicKeyInfo, 64 lower-case hex characters.</param>
/// <param name="Purpose"><c>tls</c> or <c>code-signing</c>.</param>
/// <param name="Source">The primary sources that name this key.</param>
/// <param name="Date">The day the compromise was reported, <c>yyyy-MM-dd</c>.</param>
public sealed record TlsDenyEntry(string Subject, string SpkiSha256, string Purpose, string Source, string Date);

/// <summary>
/// The list cannot be used: it is missing, unreadable or malformed. Its message names the file and the repair, in words; the caller
/// refuses every secure connection rather than make one without the list.
/// </summary>
public sealed class TlsDenylistUnreadableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// U-tls-denylist — THE CERTIFICATE AUTHORITIES NO SECURE CONNECTION MAY RUN THROUGH, AS SHIPPED DATA.
///
/// <para>The private keys of eight eMudhra intermediates are compromised (Mozilla bug 2083449). Sectigo cross-signed all eight under a root
/// that stays trusted, so a host's trust-store update leaves them valid; Go closed it with an SPKI denylist in <c>crypto/x509</c>
/// (CL 851047), and this is the same list for this app. A key is named by its SubjectPublicKeyInfo's hash, so the original and the
/// cross-signed certificate of one key are both caught.</para>
///
/// <para><b>Data, not code</b> (<c>CLAUDE.md</c>, vendor commands): <see cref="FileName"/> ships beside the binaries, so a later change to
/// the list is a data change. <b>Refuse, never guess:</b> a missing, unreadable or malformed file is never read as an empty list — it is
/// refused in words that name the file and the repair, and <see cref="TlsPolicy"/> then refuses every secure connection.</para>
/// </summary>
public sealed class TlsDenylist
{
    /// <summary>The shipped file's name, beside the binaries.</summary>
    public const string FileName = "tls-denylist.json";

    /// <summary>Where the shipped list is read from.</summary>
    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, FileName);

    readonly Dictionary<string, TlsDenyEntry> _bySpki;

    TlsDenylist(IReadOnlyList<TlsDenyEntry> entries)
    {
        Entries = entries;
        _bySpki = entries.ToDictionary(e => e.SpkiSha256, StringComparer.Ordinal);
    }

    /// <summary>Every entry, in the file's order.</summary>
    public IReadOnlyList<TlsDenyEntry> Entries { get; }

    /// <summary>The entry for one public key's hash (lower-case hex), or null.</summary>
    public TlsDenyEntry? Find(string spkiSha256) => _bySpki.GetValueOrDefault(spkiSha256);

    /// <summary>Reads the list at <paramref name="path"/>, or throws <see cref="TlsDenylistUnreadableException"/> in words.</summary>
    public static TlsDenylist Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Refused("it is missing", ex);
        }
        catch (Exception ex) { throw Refused("TradeAgent could not open it", ex); }
        return Parse(text);
    }

    /// <summary>Parses the list's text, or throws <see cref="TlsDenylistUnreadableException"/> in words.</summary>
    public static TlsDenylist Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw Refused("the text in it is not valid JSON", ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Refused("it is not a JSON object");
            foreach (var p in root.EnumerateObject())
                if (p.Name is not ("note" or "fetched" or "entries"))
                    throw Refused($"it has a field '{p.Name}' this build does not know");
            if (!root.TryGetProperty("entries", out var array) || array.ValueKind != JsonValueKind.Array)
                throw Refused("it has no 'entries' list");

            var entries = new List<TlsDenyEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var n = 0;
            foreach (var item in array.EnumerateArray())
            {
                n++;
                if (item.ValueKind != JsonValueKind.Object) throw Refused($"entry {n} is not a JSON object");
                foreach (var p in item.EnumerateObject())
                    if (p.Name is not ("subject" or "spkiSha256" or "purpose" or "source" or "date"))
                        throw Refused($"entry {n} has a field '{p.Name}' this build does not know");

                var subject = Text(item, "subject", n);
                var spki = Text(item, "spkiSha256", n);
                var purpose = Text(item, "purpose", n);
                var source = Text(item, "source", n);
                var date = Text(item, "date", n);

                if (!Hex64.IsMatch(spki))
                    throw Refused($"entry {n}'s 'spkiSha256' is not a SHA-256 in 64 lower-case hex characters");
                if (purpose is not ("tls" or "code-signing"))
                    throw Refused($"entry {n}'s 'purpose' is neither 'tls' nor 'code-signing'");
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Refused($"entry {n}'s 'date' is not a date written yyyy-MM-dd");
                if (!seen.Add(spki))
                    throw Refused($"it lists the key {spki} twice");

                entries.Add(new TlsDenyEntry(subject, spki, purpose, source, date));
            }
            if (entries.Count == 0) throw Refused("it lists no certificate authority");
            return new TlsDenylist(entries);
        }
    }

    static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    static string Text(JsonElement item, string name, int n) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : throw Refused($"entry {n} has no '{name}' written as text");

    /// <summary>
    /// THE REFUSAL, IN WORDS: the file, what is wrong with it, what TradeAgent does instead, and the repair. No path and no command — the
    /// file ships with the app, and putting it back is a reinstall.
    /// </summary>
    static TlsDenylistUnreadableException Refused(string why, Exception? inner = null) =>
        new($"{FileName} could not be read: {why}. Without its list of distrusted certificate authorities TradeAgent refuses every "
            + "secure connection rather than make one unchecked. Reinstall TradeAgent to put the file back.", inner);
}
