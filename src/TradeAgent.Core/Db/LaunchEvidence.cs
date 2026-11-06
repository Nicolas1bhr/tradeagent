using System.Text.RegularExpressions;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT A LAUNCH'S ROW RECORDS OF ITS SEAT AND ITS VENDOR (<c>U-chief-wake-once</c>, rung 52), composed by the app from what it measured —
/// the verified surface's tool names and the runtime stream's own error lines — never from an agent's words. Measurements only.
/// </summary>
public static partial class LaunchEvidence
{
    /// <summary>The most characters of a vendor's words one row keeps; the rest is named, never silently cut.</summary>
    public const int VendorSaidChars = 2000;

    /// <summary>What a credential-shaped run is replaced with.</summary>
    public const string Withheld = "[withheld]";

    /// <summary>The offered tools' names, sorted and distinct, one per line — or null for none offered or no verified surface.</summary>
    public static string? OfferedNames(IEnumerable<string>? names)
    {
        if (names is null) return null;
        var kept = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return kept.Count == 0 ? null : string.Join('\n', kept);
    }

    /// <summary>
    /// THE VENDOR'S LINES AS ONE BOUNDED TEXT, or null for none: each line kept in order, every run shaped like a credential — a value after
    /// a scheme or a credential's name, an <c>sk</c>/<c>rk</c>/<c>pk</c> key, a JWT, any unbroken run of 32 or more key characters — replaced by
    /// <see cref="Withheld"/>, and the whole cut at <see cref="VendorSaidChars"/>, saying so. A row never holds a credential.
    /// </summary>
    public static string? VendorSaid(IReadOnlyList<string>? lines)
    {
        if (lines is null) return null;
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => Scrub(l.Trim())).ToList();
        if (kept.Count == 0) return null;
        var said = string.Join('\n', kept);
        if (said.Length <= VendorSaidChars) return said;
        var more = $" … (cut here: {said.Length} characters in all)";
        return said[..(VendorSaidChars - more.Length)] + more;
    }

    static string Scrub(string line)
    {
        string Keep(Match m) => m.Groups[1].Value + Withheld;
        return Key().Replace(Named().Replace(Basic().Replace(Scheme().Replace(line, Keep), Keep), Keep), Withheld);
    }

    /// <summary>
    /// THE VALUE AFTER A WORD THAT NAMES A CREDENTIAL — however short or dotted (the Codex reviews: a pasted key echoed in an error line is
    /// not always long): "api key", "key", "token", "password", "passwd", "pwd", "secret", "credential", "authorization", with any prefix
    /// (<c>access_token</c>, <c>client_secret</c>, <c>sessionToken</c>; R29 F-C1), then an optional "provided", "given", "supplied", "used",
    /// "value", "was" or "is", then a ":" or "=", which may come after the quote that closes a JSON key's name. An opening quote may follow
    /// it, and a space and an opening quote alone will do. A quoted value runs to the same quote unescaped, spaces included; any other to the next
    /// space, quote or separator, whatever it starts with but the scheme <see cref="Scheme"/> has already withheld. A word with no separator
    /// after it ("API key is invalid", <c>"code":"invalid_api_key"</c>) is the vendor's diagnosis and is kept.
    /// </summary>
    [GeneratedRegex(@"(?i)(\b[a-z0-9_]*(?:api[ _-]?key|key|token|password|passwd|pwd|secret|credential|authorization)\b(?>(?:\s+(?:provided|given|supplied|used|value|was|is))?(?:\\?[""'`]?\s*[:=]\s*(\\?[""'`])?|\s+(\\?[""'`]))))(?!\[withheld\]|[a-z]+\s+\[withheld\])(?(2)(?:(?!(?-i:\2))(?:\\.|.))+|(?(3)(?:(?!(?-i:\3))(?:\\.|.))+|[^\s""',;\\]+))")]
    private static partial Regex Named();

    /// <summary>
    /// THE VALUE AFTER AN AUTHORIZATION SCHEME, whatever the scheme (<c>Bearer</c>, <c>Basic</c>, <c>Token</c>): the header's value, and a
    /// bearer value anywhere. The scheme's word is kept, because it says what was refused and nothing of the secret.
    /// </summary>
    [GeneratedRegex(@"(?i)(\bbearer\s+|\b[a-z0-9_]*authorization\\?[""'`]?\s*[:=]\s*\\?[""'`]?[a-z][a-z0-9_-]*\s+)[^\s""',;\\]+")]
    private static partial Regex Scheme();

    /// <summary>
    /// A <c>Basic</c> VALUE WITH NO HEADER NAME BEFORE IT, in the shape base64 takes. A plain word, lower case or capitalised, is kept: "basic
    /// authentication failed" and "Basic Plan" are the vendor's diagnosis, and <see cref="Scheme"/> has already taken any value inside an
    /// Authorization header. A real value of more than a few characters is almost never all one case.
    /// </summary>
    [GeneratedRegex(@"\b((?i:basic)\s+)(?![A-Z]?[a-z]+\b)[A-Za-z0-9+/]{4,}={0,2}")]
    private static partial Regex Basic();

    [GeneratedRegex(@"\b(?:sk|rk|pk)[-_][A-Za-z0-9_\-]{8,}|\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*|[A-Za-z0-9_\-+/=]{32,}")]
    private static partial Regex Key();
}
