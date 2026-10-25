using System.Globalization;
using Avalonia.Controls;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// WHERE A POSITION WITH NO SEAT OF ITS OWN WOULD RUN, as the launch resolves it (<c>AppHost.SeatFor</c>): its runtime and model, or — when the
/// seat cannot be read or served — the reason, in the app's words. Never a default the page guessed.
/// </summary>
sealed record ResolvedSeat(string? Runtime, string? Model, string? Refused);

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

    /// <summary>The scorecards, computed at read (<c>U-org-scorecards-a</c>), or null while the database is not open or where none is read.</summary>
    public Func<Scorecards?> Scorecards { get; init; } = () => null;

    /// <summary>Whether a cell proof is recorded for this machine and build.</summary>
    public Func<bool> ProofHeld { get; init; } = () => false;

    /// <summary>The currency the AI is charged in.</summary>
    public Func<string> Currency { get; init; } = () => "";

    /// <summary>The clock a deadline is judged against and the day is cut by.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Where a position resolves to, the way its launch does (seat columns first, else the app's settings for the role), or null where there is
    /// nothing to ask — a test, a build with no host.
    /// </summary>
    public Func<string, ResolvedSeat>? SeatOf { get; init; }

    /// <summary>Why a position's folder refuses its launches because the last start could not rebuild it, or null (<see cref="AppHost.HomeRefusalFor"/>).</summary>
    public Func<string, string?> HomeRefusal { get; init; } = _ => null;

    /// <summary>The owner's repair of a position's folder, returning its words (<see cref="AppHost.RepairHome"/>); null where there is no host, and no press is drawn.</summary>
    public Func<string, string>? RepairHome { get; init; }

    /// <summary>
    /// THE OWNER'S PRESSES OVER THE CHIEF (<c>U-org-chief</c> item 7), drawn on the organisation's card — in-process only, never the pipe's — or
    /// null where there is no host, and none is drawn.
    /// </summary>
    public ChiefPresses? Chief { get; init; }

    /// <summary>The sources over the running app.</summary>
    public static OrgSurfaceSources For(AppHost host) => new()
    {
        Chief = new ChiefPresses(() => host.BoardActive, host.ChiefSeats, host.AppointChief, host.ReplaceChief, host.ActivateChief, host.RemoveChief),
        SeatOf = host.ResolvedSeatOf,
        Org = () => host.OrgLedger,
        Attempts = () => host.AttemptLedger,
        Scorecards = () => host.ScorecardLedger,
        ProofHeld = () => host.CellProofHeld,
        Currency = () => host.SpendToday.Currency,
        HomeRefusal = host.HomeRefusalFor,
        RepairHome = host.RepairHome,
    };
}

/// <summary>
/// THE OWNER'S PRESSES OVER THE CHIEF (<c>U-org-chief</c> item 7; the orchestrator's Q1): whether a board seat is active (read through the board's
/// one reader), the CLOSED list of seats a chief may be appointed on, and each press — appoint, replace, activate, remove — answering the app's
/// words of what it did or why it was refused. Each runs in-process, in the owner's own app; none is reachable from the pipe.
/// </summary>
sealed record ChiefPresses(Func<bool> BoardActive, Func<IReadOnlyList<ChiefSeat>> Seats, Func<ChiefSeat, string> Appoint,
    Func<ChiefSeat, string> Replace, Func<string> Activate, Func<string> Remove);

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

    /// <summary>
    /// The chart depth-first from the root, each unit with its depth — or, for a chart that is not one tree, the damage in words and NO order:
    /// a unit is never drawn as a root it is not. Damage is a count of roots other than one, a unit hanging from a unit the chart does not
    /// hold, and units the root cannot reach (parents that loop). Each names the units, so the owner can say which.
    /// </summary>
    public static (IReadOnlyList<(OrgUnitRow Unit, int Depth)> Order, string? Damage) Tree(IReadOnlyList<OrgUnitRow> units)
    {
        var ids = units.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var roots = units.Where(u => u.ParentId is null).ToList();
        var children = units.Where(u => u.ParentId is not null).ToLookup(u => u.ParentId!, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<(OrgUnitRow, int)>();
        void Walk(OrgUnitRow u, int depth)
        {
            if (!seen.Add(u.Id)) return;
            order.Add((u, depth));
            foreach (var child in children[u.Id]) Walk(child, depth + 1);
        }
        foreach (var root in roots) Walk(root, 0);

        var damage = new List<string>();
        if (units.Count > 0 && roots.Count != 1)
            damage.Add(roots.Count == 0 ? "no unit is the root" : $"{roots.Count} units claim to be the root ({string.Join(", ", roots.Select(r => r.Id))})");
        foreach (var lost in units.Where(u => u.ParentId is { } parent && !ids.Contains(parent)))
            damage.Add($"unit {lost.Id} hangs from {lost.ParentId}, which is not a unit");
        var unreached = units.Where(u => !seen.Contains(u.Id) && (u.ParentId is null || ids.Contains(u.ParentId))).Select(u => u.Id).ToList();
        if (unreached.Count > 0)
            damage.Add($"{(unreached.Count == 1 ? "unit" : "units")} {string.Join(", ", unreached)} cannot be reached from the root (their parents loop)");

        return damage.Count == 0 ? (order, null) : ([], string.Join("; ", damage));
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
