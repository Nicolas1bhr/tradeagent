using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.AgentRuntime;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE "MOVE THESE ASIDE" CARD IS HANDED, as delegates (the <see cref="SeatSignInSources"/> arrangement), so the card the owner
/// sees and the card a test presses are one control over different folders. Nothing here reaches the agent-facing pipe.
/// </summary>
sealed record HeldFoldersSources
{
    /// <summary>The folders that hold the AI now: <see cref="HeldFolders.Here"/> in the app.</summary>
    public required Func<IReadOnlyList<HeldFolder>> Find { get; init; }

    /// <summary>The activity log. Told what the press moved and where — never anything in what moved.</summary>
    public required Action<string> Activity { get; init; }
}

/// <summary>
/// THE DASHBOARD'S WAY OUT OF A HELD AI (<c>U-windows-cards-2</c>): on Windows no AI seat starts while the account's own skills folder or
/// the machine's codex folder holds anything (<see cref="SeatProfile.AccountRefusal"/>, <see cref="SeatProfile.MachineRefusal"/>), and
/// the owner never opens a terminal to clear that. This card names each such folder and how much it holds — never what — and offers
/// "Move these aside": a SECOND press, whose sentence names how many items go to which folder and that nothing is deleted, renames each
/// folder whole to a TradeAgent-named folder beside it (<see cref="HeldFolders.Carry"/>), looks again, and says what moved, how to put
/// it back, and whether the AI can start.
///
/// <list type="bullet">
/// <item><b>Visible iff</b> a folder holds the AI, or the last press has words to show (until he closes them).</item>
/// <item><b>Two presses</b>, the plan fixed at the first: the second does exactly what its sentence said or refuses that step in words.</item>
/// <item><b>Built once and updated in place.</b> <see cref="Update"/> on the five-second tick sets text and visibility and rewrites the
/// folder lines only when the folders changed; the button is never rebuilt, so a half-press survives the tick.</item>
/// </list>
/// </summary>
sealed class HeldFoldersCard
{
    public const string MoveAside = "Move these aside";
    public const string Close = "Close this";

    readonly HeldFoldersSources _sources;
    readonly TextBlock _headline = Ui.Body("");
    readonly StackPanel _lines = new() { Spacing = Theme.S2 };
    readonly TextBlock _note = Ui.Body("");
    readonly Button _close;
    string _shown = "";
    AsidePlan? _armed;

    /// <summary>The card the dashboard shows.</summary>
    public Border Root { get; }

    /// <summary>The two-press control itself, so a test presses what the owner presses.</summary>
    public Button MoveButton { get; }

    /// <summary>The words the last press left, or null.</summary>
    public string? Said { get; private set; }

    public HeldFoldersCard(HeldFoldersSources sources)
    {
        _sources = sources;
        _headline.TextWrapping = TextWrapping.Wrap;
        _note.TextWrapping = TextWrapping.Wrap;
        _note.IsVisible = false;

        // THE PLAN IS FIXED AT THE FIRST PRESS, its sentence the armed label; null (nothing held) and the press only looks again.
        MoveButton = Ui.ConfirmIf(MoveAside, () =>
        {
            _armed = HeldFolders.Plan(_sources.Find());
            return _armed?.Sentence;
        }, Carry);
        MoveButton.HorizontalAlignment = HorizontalAlignment.Left;
        _close = Ui.Secondary(Close, () => { Said = null; Update(); });
        _close.HorizontalAlignment = HorizontalAlignment.Left;

        Root = new Border
        {
            Background = Theme.CautionSoft,
            BorderBrush = Theme.Caution,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S5),
            IsVisible = false,
            Child = Ui.Col(Theme.S4,
                Ui.With(Ui.Eyebrow("Your folders the AI would read"), t => t.Foreground = Theme.Caution),
                _headline,
                _lines,
                MoveButton,
                _note,
                _close)
        };

        Update();
    }

    /// <summary>THE FIVE-SECOND PASS: what holds the AI now, read and shown; the button untouched.</summary>
    public void Update()
    {
        IReadOnlyList<HeldFolder> held;
        try { held = _sources.Find(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { held = []; }

        Root.IsVisible = held.Count > 0 || Said is not null;
        MoveButton.IsVisible = held.Count > 0;
        _close.IsVisible = Said is not null;
        _note.IsVisible = Said is not null;
        _note.Text = Said ?? "";

        _headline.Text = held.Count > 0
            ? "TradeAgent will not start its AI while these folders hold anything: the AI tool reads them as its own skills and " +
              "settings, whatever TradeAgent sets. TradeAgent never opens what is in them. Moving them aside renames each one, whole, " +
              "to a folder beside it that the AI tool never reads; nothing is deleted."
            : "Nothing in your folders holds TradeAgent's AI any more.";

        var shown = string.Join("\n", held.Select(Line));
        if (shown == _shown) return;
        _shown = shown;
        _lines.Children.Clear();
        foreach (var line in held.Select(Line))
            _lines.Children.Add(Ui.With(Ui.Muted(line), t => t.TextWrapping = TextWrapping.Wrap));
    }

    static string Line(HeldFolder h) =>
        $"{h.Path} — {(h.OnMachine ? "settings and skills for every account on this computer" : "your Windows account's own skills")} — " +
        (h.Holds == "holds something" ? $"holds {h.Count}." : $"{h.Holds}.");

    void Carry()
    {
        if (_armed is not { } plan) { Update(); return; }
        _armed = null;
        var done = HeldFolders.Carry(plan, _sources.Find);
        Said = done.Words;
        foreach (var step in done.Moved)
            _sources.Activity($"You moved {step.Held.Count} from {step.Held.Move} aside to {step.To}, on your two presses; nothing was deleted");
        if (done.Moved.Count == 0) _sources.Activity("Your press to move your folders aside moved nothing: " + done.Words);
        _note.Foreground = done.Cleared ? Theme.TextMuted : Theme.Caution;
        Update();
    }
}
