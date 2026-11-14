using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.Core;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE "FREE SPACE" CARD IS HANDED, as delegates (the <see cref="HeldFoldersSources"/> arrangement), so the card the owner sees and the
/// card a test presses are one control over different folders. Nothing here reaches the agent-facing pipe.
/// </summary>
sealed record FreeSpaceSources
{
    /// <summary>Press one's look: <see cref="FreeSpace.Inventory"/> over the app's home.</summary>
    public required Func<FreeSpacePlan> Look { get; init; }

    /// <summary>Press two: <see cref="FreeSpace.Remove"/> of exactly the plan press one showed.</summary>
    public required Func<FreeSpacePlan, FreeSpaceDone> Remove { get; init; }

    /// <summary>The disk, read again after the removal (<see cref="DiskSpace.Read"/>); null when it could not be read.</summary>
    public required Func<DiskSpace?> Read { get; init; }

    /// <summary>Whether the card shows with nothing to say: always in Settings; on the Dashboard while the free-space guard's pause stands.</summary>
    public required Func<bool> Shown { get; init; }

    /// <summary>The activity log. Told what the press removed, in kinds and sizes.</summary>
    public required Action<string> Activity { get; init; }

    /// <summary>Drawn as an ordinary card (Settings, where nothing is wrong) rather than a caution card (the Dashboard, under the guard's pause).</summary>
    public bool Calm { get; init; }

    /// <summary>Where the removal runs: off the window's thread, so a large older folder does not freeze it. A test runs it in place.</summary>
    public Func<Func<FreeSpaceDone>, Task<FreeSpaceDone>> Off { get; init; } = work => Task.Run(work);
}

/// <summary>
/// THE DISK GUARD'S WAY OUT WITH NO TERMINAL (<c>U-free-space</c>): "Free space", TWO PRESSES. The first only looks — it lists what TradeAgent
/// itself put on this disk and can let go (<see cref="FreeSpace"/>'s allowlist: an older home set aside by Start fresh, installers already
/// run, a check's leftover folder), each with its size and age, and the total — and arms the button with the sentence of what the second will
/// do. The second removes exactly what the first showed and nothing that changed since, then reads the disk again and says what was freed.
/// The records, the tape, the ledgers, the evidence, the positions and the AI's work are never on the list, whatever is pressed.
///
/// <list type="bullet">
/// <item><b>It never starts the AI.</b> "Let the AI work on its own" stays its own press, and the guard's resume rule is unchanged.</item>
/// <item><b>Built once and updated in place.</b> <see cref="Update"/> on the five-second tick sets text and visibility only and never walks the
/// disk; the button is never rebuilt, so a half-press survives the tick.</item>
/// </list>
/// </summary>
sealed class FreeSpaceCard
{
    public const string FreeSpaceLabel = "Free space";
    public const string Close = "Close this";

    const string Intro =
        "TradeAgent can remove what it put on this disk itself and no longer needs. The first press only shows you the list; nothing is removed " +
        "until you press again. Your records, the tape, your positions and the AI's work are never on it.";

    readonly FreeSpaceSources _sources;
    readonly TextBlock _intro = Ui.Body(Intro);
    readonly StackPanel _lines = new() { Spacing = Theme.S2 };
    readonly TextBlock _note = Ui.Body("");
    readonly Button _close;
    FreeSpacePlan? _armed;
    bool _busy;

    /// <summary>The card the page shows.</summary>
    public Border Root { get; }

    /// <summary>The two-press control itself, so a test presses what the owner presses.</summary>
    public Button FreeButton { get; }

    /// <summary>The words the last press left, or null.</summary>
    public string? Said { get; private set; }

    /// <summary>The removal in flight, if any: a test awaits it.</summary>
    internal Task? Pending { get; private set; }

    public FreeSpaceCard(FreeSpaceSources sources)
    {
        _sources = sources;
        _intro.TextWrapping = TextWrapping.Wrap;
        _note.TextWrapping = TextWrapping.Wrap;

        // PRESS ONE LOOKS AND ARMS; it is the only thing that sets the plan. With nothing to remove it arms nothing, and the press says so.
        FreeButton = Ui.ConfirmIf(FreeSpaceLabel, Look, Free);
        FreeButton.HorizontalAlignment = HorizontalAlignment.Left;
        _close = Ui.Secondary(Close, () => { Said = null; _armed = null; ShowLines(null); Update(); });
        _close.HorizontalAlignment = HorizontalAlignment.Left;

        Root = new Border
        {
            Background = sources.Calm ? Theme.BgElevated : Theme.CautionSoft,
            BorderBrush = sources.Calm ? Theme.Line : Theme.Caution,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S5),
            Child = Ui.Col(Theme.S4,
                Ui.With(Ui.Eyebrow(FreeSpaceLabel), t => t.Foreground = sources.Calm ? Theme.TextMuted : Theme.Caution),
                _intro,
                _lines,
                FreeButton,
                _note,
                _close)
        };

        Update();
    }

    /// <summary>THE FIVE-SECOND PASS: visibility and the last words; the disk is not walked and the button is untouched.</summary>
    public void Update()
    {
        bool shown;
        try { shown = _sources.Shown(); }
        catch (Exception) { shown = false; }
        Root.IsVisible = shown || Said is not null || _armed is not null || _busy;
        FreeButton.IsEnabled = !_busy;
        _note.IsVisible = Said is not null;
        _note.Text = Said ?? "";
        _close.IsVisible = Said is not null && !_busy;
    }

    /// <summary>Press one: the list on the card and the armed sentence, or null (nothing to remove: the press then only says so).</summary>
    string? Look()
    {
        FreeSpacePlan plan;
        try { plan = _sources.Look(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { plan = new FreeSpacePlan([], DateTimeOffset.UtcNow); }
        _armed = plan.Items.Count > 0 ? plan : null;
        Said = null;
        ShowLines(_armed);
        Update();
        return _armed is { } p ? Armed(p) : null;
    }

    /// <summary>Press two, or a press one that found nothing: only an armed plan is ever removed.</summary>
    void Free()
    {
        if (_armed is not { } plan)
        {
            Said = Nothing;
            ShowLines(null);
            Update();
            return;
        }
        _armed = null;
        _busy = true;
        Said = $"Removing {Items(plan.Items.Count)} ({Size(plan.Bytes)})…";
        Update();
        Pending = Finish(plan);
    }

    async Task Finish(FreeSpacePlan plan)
    {
        FreeSpaceDone? done;
        string? failed = null;
        try { done = await _sources.Off(() => _sources.Remove(plan)); }
        catch (Exception ex)
        {
            // NOT EXPECTED (the removal says what it left rather than throw), and said as what it is: never as items that "changed".
            done = null;
            failed = ex.Message;
            _sources.Activity($"Your press to free space stopped: {ex.Message}");
        }
        DiskSpace? now;
        try { now = _sources.Read(); }
        catch (Exception) { now = null; }
        void Show()
        {
            _busy = false;
            Said = done is null
                ? $"The removal stopped: {failed}. Press \"{FreeSpaceLabel}\" again to see what is still there."
                : Done(done, now);
            if (done is { Removed.Count: > 0 }) _sources.Activity(Logged(done));
            ShowLines(null);
            Update();
        }
        if (Dispatcher.UIThread.CheckAccess()) Show();
        else Dispatcher.UIThread.Post(Show);
    }

    void ShowLines(FreeSpacePlan? plan)
    {
        _lines.Children.Clear();
        if (plan is null) return;
        foreach (var item in plan.Items)
            _lines.Children.Add(Ui.With(Ui.Muted(Line(item, plan.At)), t => t.TextWrapping = TextWrapping.Wrap));
        _lines.Children.Add(Ui.With(Ui.Body($"{Items(plan.Items.Count)}, {Size(plan.Bytes)} in all."), t => t.TextWrapping = TextWrapping.Wrap));
        if (OperatingSystem.IsLinux())
            _lines.Children.Add(Ui.With(Ui.Micro(LinuxReleases), t => t.TextWrapping = TextWrapping.Wrap));
    }

    // ── The words ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    internal const string Nothing =
        "TradeAgent found nothing of its own on this disk that it can remove. What is left is your records, the tape, the AI's work and " +
        "your own files, which it never removes";

    internal const string LinuxReleases =
        "TradeAgent's installed versions on this computer belong to the system, so they are not on this list.";

    /// <summary>The armed second press, in full.</summary>
    internal static string Armed(FreeSpacePlan plan)
    {
        var what = plan.Items.Count == 1 ? $"this item ({Size(plan.Bytes)})" : $"these {plan.Items.Count} items ({Size(plan.Bytes)})";
        var older = plan.Items.Any(i => i.Kind == LetGoKind.OlderHome) ? ", the older version's records with its folder" : "";
        return $"Yes — remove {what}{older}. Your records, the tape and your positions are kept.";
    }

    /// <summary>One item as press one lists it: what it is, its size and its age. No path: what it is, not where.</summary>
    internal static string Line(LetGoItem item, DateTimeOffset now) => item.Kind switch
    {
        LetGoKind.OlderHome =>
            $"An older TradeAgent's folder, set aside when you started fresh{AsideOn(item.Path)}. This version cannot open it, and that older " +
            $"version's records go with it — {Size(item.Bytes)}.",
        LetGoKind.Installer =>
            $"The downloaded installer of TradeAgent {Path.GetFileName(item.Path)}, already installed — {Size(item.Bytes)}, {Age(item.NewestUtc, now)}.",
        _ =>
            $"A folder left over from a check of the AI's sealed workspace, with a spare copy of its test program — {Size(item.Bytes)}, {Age(item.NewestUtc, now)}."
    };

    /// <summary>What press two did, and the disk read again.</summary>
    internal static string Done(FreeSpaceDone done, DiskSpace? now)
    {
        var parts = new List<string>();
        parts.Add(done.Removed.Count == 0
            ? "Nothing was removed."
            : $"Removed {Items(done.Removed.Count)} and freed {Size(done.Freed)}. Your records, the tape and your positions were kept.");
        foreach (var (item, why) in done.Skipped)
            parts.Add($"{Short(item)} was left where it was: {Why(why)}.");
        if (done.FilesKept > 0)
            parts.Add($"{Files(done.FilesKept)} changed while they were being removed, so they were left.");
        parts.Add(now is { } read ? $"{Size(read.Free)} is free on this disk now." : "The free space could not be read just now.");
        return string.Join(" ", parts);
    }

    /// <summary>The activity log's line.</summary>
    internal static string Logged(FreeSpaceDone done)
    {
        var kinds = done.Removed.GroupBy(i => i.Kind).Select(g => g.Key switch
        {
            LetGoKind.OlderHome => $"{Count(g.Count(), "older folder")} set aside by Start fresh, with that older version's records",
            LetGoKind.Installer => Count(g.Count(), "installer") + " already installed",
            _ => Count(g.Count(), "leftover check folder")
        });
        return $"You freed {Size(done.Freed)} on your two presses: {string.Join("; ", kinds)}. Your records, the tape and your positions were kept.";
    }

    static string Why(LetGoSkip why) => why switch
    {
        LetGoSkip.NoLongerLettable => "it is no longer something TradeAgent may remove",
        LetGoSkip.HoldsLink => "it now points somewhere else, and TradeAgent never follows that",
        LetGoSkip.InUse => "a check was working in it a moment ago",
        _ => $"it changed after you looked, so press \"{FreeSpaceLabel}\" again to see it as it is now"
    };

    static string Short(LetGoItem item) => item.Kind switch
    {
        LetGoKind.OlderHome => "An older TradeAgent's folder" + AsideOn(item.Path),
        LetGoKind.Installer => $"The installer of TradeAgent {Path.GetFileName(item.Path)}",
        _ => "A leftover check folder"
    };

    /// <summary>" on 31 Oct 2026", read off the name Start fresh gave the folder; empty when it does not read.</summary>
    static string AsideOn(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 15 && DateTime.TryParseExact(name[^15..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? $" on {at.ToLocalTime():d MMM yyyy}" : "";
    }

    static string Age(DateTime writtenUtc, DateTimeOffset now)
    {
        var days = (int)Math.Floor((now.UtcDateTime - writtenUtc).TotalDays);
        return days <= 0 ? "last written today" : days == 1 ? "last written yesterday" : $"last written {days} days ago";
    }

    static string Items(int n) => Count(n, "item");

    static string Files(int n) => Count(n, "file");

    static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {what}s";

    /// <summary>A size as the owner reads one: "4.2 GB", "310 MB", "12 KB".</summary>
    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        _ => ((bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB"
    };
}
