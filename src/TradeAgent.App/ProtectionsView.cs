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

    /// <summary>The fold that holds the register itself, closed by default: a reviewer's record, not the app's words to the owner.</summary>
    internal const string ReviewersTitle = "For reviewers: where each protection is written, kept and proved";

    /// <summary>The words above the fold.</summary>
    internal const string SummaryIntro =
        "What TradeAgent protects, and whether each protection holds on this computer for each kind of AI seat. \"Holds\" means TradeAgent's "
        + "code or the computer itself enforces it and tests that really ran prove it; \"holds only through TradeAgent's own tools\" means no "
        + "tool the AI is given crosses it, though the program itself could; \"only asked\" means the AI is told and nothing stops it.";

    /// <summary>
    /// EACH PROTECTION IN THE OWNER'S WORDS (<c>U-owner-words-3</c> item 3, the orchestrator's ruling): one sentence per protection, no file, no
    /// mark, no code path, no id. A protection the registry gains without words here fails its test (every id has a line).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Plain = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AUTH-001"] = "Your own controls — the kill switch, your sign-ins and accounts, paid spending, updates, your private line and your veto — are reached only from inside the app, by you.",
        ["AUTH-002"] = "Limits, money and real-money trading change only by recorded acts of the security board, and widening a limit or moving money takes two of its seats.",
        ["ORDER-001"] = "Only the Operations Director can send an order; heading a team never gives that right.",
        ["ORDER-002"] = "Every order carries TradeAgent's own number to the broker and is read back; where a broker cannot do that, fully automatic real-money trading is refused.",
        ["ORDER-003"] = "An order is called missing only when the broker's history really reaches back far enough to show it.",
        ["ORDER-004"] = "An order is marked refused only when the broker clearly refused it; anything unclear is checked again with the broker.",
        ["ORDER-005"] = "Orders go through the broker's programming interface, never by clicking on a screen.",
        ["UNKNOWN-001"] = "An order whose outcome is unclear is checked with the broker, never sent again.",
        ["EVID-001"] = "The AI cannot write the records its results are judged by.",
        ["INBOX-001"] = "What reaches the inbox is information, never an order to the AI; an approval written in a document grants nothing.",
        ["BUDGET-001"] = "AI spending is set aside before each turn, every team's allowance stays inside its parent's, and a cost nobody knows is never counted as free.",
        ["SIGNAL-001"] = "What trades is the tested, frozen strategy; no AI answer places, changes or times an order.",
        ["CRED-001"] = "No AI seat holds your passwords or keys.",
        ["ORG-001"] = "A title in the organisation gives the AI no extra power over the computer.",
        ["SIM-001"] = "Practice and simulation never count as evidence, as real trading or as permission.",
        ["CHANGE-001"] = "The AI improving itself never gives itself more power.",
    };

    /// <summary>The kinds of AI seat in the owner's words, for the line that says where a protection holds.</summary>
    static string SeatWords(string seatClass) => seatClass switch
    {
        TradeAgent.Core.Db.SeatClasses.Cli => "command-line seats (they run as you)",
        TradeAgent.Core.Db.SeatClasses.Broker => "seats without a shell",
        TradeAgent.Core.Db.SeatClasses.Harness => "seats on TradeAgent's own worker",
        TradeAgent.Core.Db.SeatClasses.Cell => "seats in a sealed workspace",
        _ => "another kind of seat"
    };

    /// <summary>
    /// WHETHER <paramref name="inv"/> HOLDS ON <paramref name="platform"/>, in words, from the class the register itself shows for each seat
    /// (<see cref="InvariantRegistry.Shown(Invariant, SeatClaim, ProofPlatform)"/>) — so the summary can never say more than the register below it.
    /// </summary>
    internal static string Holds(Invariant inv, ProofPlatform platform)
    {
        var shown = inv.Seats.Select(c => (Seat: SeatWords(c.SeatClass), Tier: Tier(InvariantRegistry.Shown(inv, c, platform).Class))).ToList();
        if (shown.Count > 0 && shown.All(s => s.Tier == Tiers[0])) return "Holds for every kind of AI seat.";
        var parts = Tiers.Select(t => (t, seats: shown.Where(s => s.Tier == t).Select(s => s.Seat).ToList())).Where(p => p.seats.Count > 0)
            .Select(p => $"{p.t} {string.Join(", ", p.seats)}").ToList();
        if (parts.Count == 0) return "Not yet closed for any kind of AI seat.";
        return char.ToUpperInvariant(parts[0][0]) + string.Join("; ", parts)[1..] + ".";
    }

    /// <summary>The four answers, strongest first: what each enforcement class means for the owner.</summary>
    static readonly string[] Tiers = ["holds for", "holds only through TradeAgent's own tools for", "only asked of", "not yet closed for"];

    static string Tier(EnforcementClass c) => c switch
    {
        EnforcementClass.HardPlatform or EnforcementClass.HardApp => Tiers[0],
        EnforcementClass.Mediated => Tiers[1],
        EnforcementClass.Advisory => Tiers[2],
        _ => Tiers[3]
    };

    /// <summary>The section as it reads on <paramref name="platform"/>: the plain summary, then the register in its closed fold.</summary>
    internal static Control Build(IReadOnlyList<Invariant> registry, ProofPlatform platform)
    {
        var summary = Ui.Col(Theme.S3, Ui.With(Ui.Muted(SummaryIntro), t => t.FontSize = Theme.Small));
        foreach (var inv in registry)
            summary.Children.Add(Ui.Col(Theme.S1,
                Ui.Body(Plain.TryGetValue(inv.Id, out var words) ? words : "A protection this version has no plain words for; the register below names it."),
                Ui.With(Ui.Micro(Holds(inv, platform)), t => t.Foreground = Brush(Weakest(inv, platform)))));
        return Ui.Section(Title, Ui.Col(Theme.S5, summary, Ui.Fold(ReviewersTitle, Register(registry, platform), open: false)));
    }

    /// <summary>
    /// THE REGISTER ITSELF, for a reviewer (U-invariant-registry): every protection with its id, statement, where it is written, the code that
    /// keeps it, the tests that prove it and its class per seat. It names files, doc sections and code paths by design.
    /// </summary>
    internal static Control Register(IReadOnlyList<Invariant> registry, ProofPlatform platform)
    {
        var body = Ui.Col(Theme.S4, Ui.With(Ui.Muted(Intro), t => t.FontSize = Theme.Small));
        body.Children.Add(Ui.With(Ui.Muted(Counts(registry, platform)), t => t.FontSize = Theme.Small));
        foreach (var inv in registry)
        {
            body.Children.Add(Ui.Divider());
            body.Children.Add(Card(inv, platform));
        }
        return body;
    }

    static EnforcementClass Weakest(Invariant inv, ProofPlatform platform) =>
        inv.Seats.Select(c => InvariantRegistry.Shown(inv, c, platform).Class).DefaultIfEmpty(EnforcementClass.Gap).Min();

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
