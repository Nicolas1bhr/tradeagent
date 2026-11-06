using Avalonia.Controls;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// THE PROTECTIONS, AS THE OWNER READS THEM (<c>U-invariant-registry</c>; VISION § 6.3): every protection in
/// <see cref="InvariantRegistry"/> with what it says, the code that keeps it, the tests that prove it and, per seat type, the class it
/// is SHOWN at on this kind of computer — <see cref="InvariantRegistry.Shown(Invariant, SeatClaim, ProofPlatform)"/>, so a claim no test backs
/// here reads "advisory" and says why.
///
/// <para><b>Read-only and built once.</b> The registry is compiled into the app and never changes while it runs, so the section is made
/// once and has nothing to update; it holds no press, and nothing here reaches the pipe.</para>
/// </summary>
static class ProtectionsSection
{
    internal const string Title = "Protections";

    internal const string Intro =
        "What is hard, what is mediated and what is only asked — per kind of seat, on this kind of computer. A class is shown only where named "
        + "tests prove both the protection and the seat's side of it, and every one of them really ran and passed on this kind of computer in the "
        + "project's automated checks; anything else reads advisory. This list records what exists; it enforces nothing itself.";

    /// <summary>The section over <paramref name="registry"/> on this computer (the app passes <see cref="InvariantRegistry.All"/>; a test may pass its own).</summary>
    internal static Control Build(IReadOnlyList<Invariant> registry) => Build(registry, InvariantRegistry.ThisPlatform);

    /// <summary>The section as it reads on <paramref name="platform"/>.</summary>
    internal static Control Build(IReadOnlyList<Invariant> registry, ProofPlatform platform)
    {
        var body = Ui.Col(Theme.S4, Ui.With(Ui.Muted(Intro), t => t.FontSize = Theme.Small));
        body.Children.Add(Ui.With(Ui.Muted(Counts(registry, platform)), t => t.FontSize = Theme.Small));
        foreach (var inv in registry)
        {
            body.Children.Add(Ui.Divider());
            body.Children.Add(Card(inv, platform));
        }
        return Ui.Section(Title, body);
    }

    /// <summary>How many seat-type cells are shown at each class, strongest first.</summary>
    internal static string Counts(IReadOnlyList<Invariant> registry, ProofPlatform platform)
    {
        var shown = registry.SelectMany(i => i.Seats.Select(s => InvariantRegistry.Shown(i, s, platform).Class)).ToList();
        var parts = Enum.GetValues<EnforcementClass>().OrderByDescending(c => c)
            .Select(c => (c, n: shown.Count(x => x == c))).Where(p => p.n > 0)
            .Select(p => $"{p.n} {InvariantRegistry.Word(p.c)}");
        return $"{registry.Count} protections × {InvariantRegistry.SeatTypes.Count} kinds of seat: {string.Join(", ", parts)}.";
    }

    static Control Card(Invariant inv, ProofPlatform platform)
    {
        var col = Ui.Col(Theme.S2,
            Ui.Col(Theme.S1, Ui.Mono(inv.Id), Ui.Body(inv.Statement)),
            Ui.Micro($"Written in: {inv.WrittenIn}. Kept by: {string.Join(", ", inv.EnforcedBy)}."));

        foreach (var claim in inv.Seats)
        {
            var title = InvariantRegistry.SeatTypes.FirstOrDefault(s => s.SeatClass == claim.SeatClass).Title ?? claim.SeatClass;
            var (cls, downgraded) = InvariantRegistry.Shown(inv, claim, platform);
            var line = $"{InvariantRegistry.Word(cls)} — {claim.Basis}" + (downgraded is null ? "" : $" ({downgraded})");
            col.Children.Add(Ui.KeyValueLive(title, Ui.With(Ui.Body(line, Brush(cls)), t => t.FontSize = Theme.Small)));
        }

        var here = inv.Tests.Where(t => (InvariantRegistry.RunsOn(t) & platform) != 0).ToList();
        col.Children.Add(Ui.Micro(here.Count == 0
            ? $"Proved by: no named test{(inv.Tests.Count == 0 ? "" : $" that runs on {InvariantRegistry.Word(platform)}")}."
            : $"Proved by {here.Count} named tests: {string.Join(", ", here)}."));
        if (here.Count < inv.Tests.Count)
            col.Children.Add(Ui.With(Ui.Body($"Named, but not run on {InvariantRegistry.Word(platform)}: {string.Join(", ", inv.Tests.Except(here))}.", Theme.Caution),
                t => t.FontSize = Theme.Small));
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
