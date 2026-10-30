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
    /// THE VENDOR'S LINES AS ONE BOUNDED TEXT, or null for none: each line kept in order, every run shaped like a credential — a bearer
    /// value, an <c>sk-</c>/<c>rk-</c>/<c>pk-</c> key, a JWT, any unbroken run of 32 or more key characters — replaced by
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

    static string Scrub(string line) =>
        Key().Replace(Named().Replace(Bearer().Replace(line, m => m.Groups[1].Value + Withheld), m => m.Groups[1].Value + Withheld), Withheld);

    /// <summary>
    /// THE VALUE AFTER A WORD THAT NAMES A CREDENTIAL — however short or dotted (the Codex review: a pasted key echoed in an error line is
    /// not always long): "api key", "key", "token", "password", "secret", "credential", "authorization", then an optional ":" or "=",
    /// and the next run of non-space characters.
    /// </summary>
    [GeneratedRegex(@"(?i)(\b(?:api[ _-]?key|key|token|password|passwd|secret|credential|authorization)\b\s*[:=]?\s*)(?!\[withheld\]|bearer\b)[^\s""',;]+")]
    private static partial Regex Named();

    [GeneratedRegex(@"(?i)(bearer\s+)[^\s""',;]+")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(?:sk|rk|pk)-[A-Za-z0-9_\-]{8,}|\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*|[A-Za-z0-9_\-+/=]{32,}")]
    private static partial Regex Key();
}
