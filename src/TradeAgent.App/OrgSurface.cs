using System.Globalization;
using Avalonia.Controls;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE ORGANISATION-FIRST PAGES ARE HANDED (<c>U-org-surface-a</c>): the stores they read, as delegates, so the page the owner sees and
/// the page a test presses are the same control over different sources. The app builds it from <see cref="AppHost"/>'s App-internal accessors.
/// <b>Reads only</b>: no source here writes, and none reaches the agent-facing pipe — operator authority stays in-process and on its own pages.
/// </summary>
sealed record OrgSurfaceSources
{
    /// <summary>The organisation's ledger, or null while the database is not open.</summary>
    public required Func<OrgStore?> Org { get; init; }

    /// <summary>The launch ledger, or null while the database is not open.</summary>
    public required Func<AiAttemptStore?> Attempts { get; init; }

    /// <summary>Whether a cell proof is recorded for this machine and build.</summary>
    public Func<bool> ProofHeld { get; init; } = () => false;

    /// <summary>The currency the AI is charged in.</summary>
    public Func<string> Currency { get; init; } = () => "";

    /// <summary>The clock a deadline is judged against and the day is cut by.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The sources over the running app.</summary>
    public static OrgSurfaceSources For(AppHost host) => new()
    {
        Org = () => host.OrgLedger,
        Attempts = () => host.AttemptLedger,
        ProofHeld = () => host.CellProofHeld,
        Currency = () => host.SpendToday.Currency,
    };
}

/// <summary>
/// THE SENTENCES THE THREE PAGES SHARE. A claim — a rationale, a report, an objective, a probability — is always printed as someone's
/// words, in quotation marks, after who wrote them; a measurement, a status, a cost or a resolution is printed as the app's own and never
/// inside quotation marks. The two are never joined in one sentence.
/// </summary>
static class OrgText
{
    /// <summary>The most characters of one claim a card prints; the rest is named, never silently cut.</summary>
    public const int ClaimChars = 600;

    /// <summary>An instant as these pages print it: UTC to the minute.</summary>
    public static string When(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="who"/>'s words, quoted and attributed. The typographic quotation marks inside the text are turned into single ones, so
    /// no claim can close its own frame and go on sounding like the app; a text longer than <see cref="ClaimChars"/> is cut and says so.
    /// </summary>
    public static string Claim(string who, string text)
    {
        var said = text.Trim().Replace('“', '‘').Replace('”', '’');
        var more = said.Length > ClaimChars ? $" (shortened here: {said.Length.ToString("N0", CultureInfo.InvariantCulture)} characters in all)" : "";
        if (said.Length > ClaimChars) said = said[..ClaimChars].TrimEnd() + "…";
        return $"{Labels.ClaimBy(who)} “{said}”{more}";
    }

    /// <summary>A unit in the owner's words: the three seeded ones by name, any other by its kind and id.</summary>
    public static string UnitTitle(OrgUnitRow unit)
    {
        if (unit.IsRoot) return "The organisation";
        var named = OrgEnvelopes.NameOf(unit.Id);
        return named.StartsWith("unit ", StringComparison.Ordinal) ? $"{Capital(unit.Kind)} {unit.Id}" : Capital(named["the ".Length..]);
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The chart depth-first from the root, each unit with its depth — a loop is never followed twice.</summary>
    public static IReadOnlyList<(OrgUnitRow Unit, int Depth)> Tree(IReadOnlyList<OrgUnitRow> units)
    {
        var children = units.Where(u => u.ParentId is not null).ToLookup(u => u.ParentId!, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<(OrgUnitRow, int)>();
        void Walk(OrgUnitRow u, int depth)
        {
            if (!seen.Add(u.Id)) return;
            order.Add((u, depth));
            foreach (var child in children[u.Id]) Walk(child, depth + 1);
        }
        foreach (var root in units.Where(u => u.ParentId is null)) Walk(root, 0);
        foreach (var stray in units) Walk(stray, 0);   // a unit the root does not reach is still shown, never hidden
        return order;
    }

    /// <summary>Sets a text block's text only when it changed, so a refresh that moved nothing touches nothing.</summary>
    public static void Set(TextBlock t, string text)
    {
        if (t.Text != text) t.Text = text;
    }
}

/// <summary>
/// A KEYED LIST OF CARDS THAT IS UPDATED IN PLACE. Each row's card is made ONCE, the first time its key is seen, and kept: a later read
/// writes the row's words into the card's existing text blocks (<see cref="OrgText.Set"/>), and the panel's children are re-laid only when
/// the keys or their order changed. Nothing is rebuilt on the five-second refresh — the dashboard rule (CLAUDE.md "Conventions").
/// </summary>
sealed class LiveList<T>
{
    readonly Panel _host;
    readonly Func<T, string> _key;
    readonly Func<T, (Control Card, Action<T> Fill)> _make;
    readonly Control _empty;
    readonly Dictionary<string, (Control Card, Action<T> Fill)> _cards = new(StringComparer.Ordinal);
    string _order = "\u0000";

    public LiveList(Panel host, Control empty, Func<T, string> key, Func<T, (Control Card, Action<T> Fill)> make)
    {
        _host = host;
        _empty = empty;
        _key = key;
        _make = make;
    }

    /// <summary>The card made for <paramref name="key"/>, or null — a test holds it to show the same card is still there after a refresh.</summary>
    public Control? CardOf(string key) => _cards.TryGetValue(key, out var c) ? c.Card : null;

    public void Update(IReadOnlyList<T> rows)
    {
        var keys = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var key = _key(row);
            if (!_cards.TryGetValue(key, out var card)) _cards[key] = card = _make(row);
            card.Fill(row);
            keys.Add(key);
        }

        var order = string.Join('|', keys);
        if (order == _order) return;
        _order = order;
        foreach (var gone in _cards.Keys.Except(keys, StringComparer.Ordinal).ToList()) _cards.Remove(gone);

        _host.Children.Clear();
        if (rows.Count == 0) _host.Children.Add(_empty);
        foreach (var key in keys) _host.Children.Add(_cards[key].Card);
    }
}
