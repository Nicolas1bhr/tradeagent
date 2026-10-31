using Avalonia.Controls;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// THE PROTECTIONS, AS THE OWNER READS THEM (<c>U-invariant-registry</c>; VISION § 6.3): every protection in
/// <see cref="InvariantRegistry"/> with what it says, the code that keeps it, the tests that prove it and, per seat type, the class it
/// is SHOWN at — <see cref="InvariantRegistry.Shown"/>, so a claim no named test backs reads "advisory" and says why.
///
/// <para><b>Read-only and built once.</b> The registry is compiled into the app and never changes while it runs, so the section is made
/// once and has nothing to update; it holds no press, and nothing here reaches the pipe.</para>
/// </summary>
static class ProtectionsSection
{
    internal const string Title = "Protections";

    internal const string Intro =
        "What is hard, what is mediated and what is only asked — per kind of seat. A class is shown only where a named test proves both the "
        + "protection and the seat's side of it; anything else reads advisory. This list records what exists; it enforces nothing itself.";

    /// <summary>The section over <paramref name="registry"/> (the app passes <see cref="InvariantRegistry.All"/>; a test may pass its own).</summary>
    internal static Control Build(IReadOnlyList<Invariant> registry)
    {
        var body = Ui.Col(Theme.S4, Ui.With(Ui.Muted(Intro), t => t.FontSize = Theme.Small));
        body.Children.Add(Ui.With(Ui.Muted(Counts(registry)), t => t.FontSize = Theme.Small));
        foreach (var inv in registry)
        {
            body.Children.Add(Ui.Divider());
            body.Children.Add(Card(inv));
        }
        return Ui.Section(Title, body);
    }

    /// <summary>How many seat-type cells are shown at each class, strongest first.</summary>
    internal static string Counts(IReadOnlyList<Invariant> registry)
    {
        var shown = registry.SelectMany(i => i.Seats.Select(s => InvariantRegistry.Shown(i, s).Class)).ToList();
        var parts = Enum.GetValues<EnforcementClass>().OrderByDescending(c => c)
            .Select(c => (c, n: shown.Count(x => x == c))).Where(p => p.n > 0)
            .Select(p => $"{p.n} {InvariantRegistry.Word(p.c)}");
        return $"{registry.Count} protections × {InvariantRegistry.SeatTypes.Count} kinds of seat: {string.Join(", ", parts)}.";
    }

    static Control Card(Invariant inv)
    {
        var col = Ui.Col(Theme.S2,
            Ui.Col(Theme.S1, Ui.Mono(inv.Id), Ui.Body(inv.Statement)),
            Ui.Micro($"Written in: {inv.WrittenIn}. Kept by: {string.Join(", ", inv.EnforcedBy)}."));

        foreach (var claim in inv.Seats)
        {
            var title = InvariantRegistry.SeatTypes.FirstOrDefault(s => s.SeatClass == claim.SeatClass).Title ?? claim.SeatClass;
            var (cls, downgraded) = InvariantRegistry.Shown(inv, claim);
            var line = $"{InvariantRegistry.Word(cls)} — {claim.Basis}" + (downgraded is null ? "" : $" ({downgraded})");
            col.Children.Add(Ui.KeyValueLive(title, Ui.With(Ui.Body(line, Brush(cls)), t => t.FontSize = Theme.Small)));
        }

        col.Children.Add(Ui.Micro(inv.Tests.Count == 0
            ? "Proved by: no named test."
            : $"Proved by {inv.Tests.Count} named tests: {string.Join(", ", inv.Tests)}."));
        if (inv.Open is { Length: > 0 } open)
            col.Children.Add(Ui.With(Ui.Body($"Not proven: {open}", Theme.Caution), t => t.FontSize = Theme.Small));
        return col;
    }

    static IBrush Brush(EnforcementClass c) => c switch
    {
        EnforcementClass.HardPlatform or EnforcementClass.HardApp => Theme.Positive,
        EnforcementClass.Mediated => Theme.Text,
        EnforcementClass.Advisory => Theme.Caution,
        _ => Theme.Danger
    };
}
