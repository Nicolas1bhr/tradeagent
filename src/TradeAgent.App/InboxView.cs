using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// Where the account owner hands the AI something to work with, and where they can see what became
/// of it.
///
/// Two things this page has to get right, and they pull in opposite directions:
///
/// <b>Handing something over must be trivial.</b> Drop it on the window, or press a button. The
/// person using this does not have a terminal and should not need a file manager either — though
/// "Open folder" is offered, because Explorer is not a console and copying forty files is genuinely
/// easier there.
///
/// <b>What came back must be legible.</b> The list separates what TradeAgent MEASURED — name, size,
/// SHA-256, when it appeared — from what the AI SAYS it did with it. They are different kinds of
/// knowledge and the page never blends them into one sentence, because the first is true and the
/// second is a report.
/// </summary>
sealed class InboxPage
{
    readonly AppHost _host;
    readonly StackPanel _items = new() { Spacing = Theme.S2 };
    readonly StackPanel _notes = new() { Spacing = Theme.S2 };
    readonly TextBlock _empty = Ui.Muted(
        "Nothing handed over yet. Drop files here — programs, documents, spreadsheets, data — and the AI can open, run and experiment with them.");
    readonly TextBlock _notesEmpty = Ui.Muted("The AI has not recorded anything yet.");
    readonly TextBlock _status = Ui.Muted("");
    readonly Border _dropZone;

    string _itemSignature = "";
    string _noteSignature = "";

    public Control Root { get; }

    public InboxPage(AppHost host)
    {
        _host = host;

        _dropZone = new Border
        {
            Background = Theme.BgSunken,
            BorderBrush = Theme.LineStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S6),
            Child = Ui.Col(Theme.S3,
                Ui.With(Ui.Body("Drop files here to hand them to the AI"),
                    t => t.HorizontalAlignment = HorizontalAlignment.Center),
                Ui.With(Ui.Muted("It can read, run and experiment with anything you put here."),
                    t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.FontSize = Theme.Small; }),
                Ui.With(Ui.Row(Theme.S2,
                        Ui.Secondary("Choose files…", ChooseFilesAsync),
                        Ui.Secondary("Open folder", OpenFolder)),
                    r => r.HorizontalAlignment = HorizontalAlignment.Center),
                Ui.With(_status, t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.FontSize = Theme.Small; }))
        };

        DragDrop.SetAllowDrop(_dropZone, true);
        _dropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _dropZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(false));
        _dropZone.AddHandler(DragDrop.DropEvent, OnDrop);

        var body = Ui.Col(Theme.S6,
            _dropZone,
            Ui.Col(Theme.S3,
                Ui.H2("What is here"),
                Ui.With(Ui.Muted("Measured by TradeAgent — the name, size and fingerprint of every file, and when it appeared."),
                    t => t.FontSize = Theme.Small),
                _empty, _items),
            Ui.Col(Theme.S3,
                Ui.H2("What the AI says it did"),
                // The wording is deliberate. These lines are the agent's own account of its work; the
                // list above is measurement. Presenting them as one history would quietly promote a
                // report into a fact.
                Ui.With(Ui.Muted("Reported by the AI as it worked. Its account, not a measurement."),
                    t => t.FontSize = Theme.Small),
                _notesEmpty, _notes));

        var scroll = Pages.Scroll(body);
        scroll[Grid.RowProperty] = 1;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header("Inbox",
            "Hand the AI programs, documents and data to work with. Everything that arrives is recorded automatically."));
        root.Children.Add(scroll);
        Root = root;
    }

    // ---- taking things in ----------------------------------------------------------------------

    void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        Highlight(files);
        e.Handled = true;
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        Highlight(false);
        e.Handled = true;
        var dropped = e.DataTransfer.TryGetFiles()?.ToList();
        if (dropped is null || dropped.Count == 0) return;

        await AcceptAsync(dropped.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList());
    }

    async Task ChooseFilesAsync()
    {
        if (TopLevel.GetTopLevel(Root)?.StorageProvider is not { } storage) return;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose files to hand to the AI",
            AllowMultiple = true
        });
        await AcceptAsync(picked.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList());
    }

    /// <summary>
    /// Copies rather than moves. What the owner dropped is still theirs and still where they left it;
    /// a tool that relocates someone's installer because they dragged it at the wrong window is a
    /// tool they stop trusting with their files.
    /// </summary>
    async Task AcceptAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _status.Text = paths.Count == 1 ? "Copying…" : $"Copying {paths.Count} items…";
        _status.Foreground = Theme.TextMuted;

        var (copied, failed) = await Task.Run(() =>
        {
            int ok = 0, bad = 0;
            foreach (var path in paths)
            {
                try
                {
                    if (Directory.Exists(path)) CopyTree(path, Unique(Path.Combine(Paths.Inbox, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)))));
                    else if (File.Exists(path)) File.Copy(path, Unique(Path.Combine(Paths.Inbox, Path.GetFileName(path))));
                    else { bad++; continue; }
                    ok++;
                }
                catch (Exception) { bad++; }
            }
            return (ok, bad);
        });

        // Scan immediately rather than waiting for the thirty-second tick: a list that stays empty
        // after a drop reads as a drop that did not work.
        var result = await Task.Run(() => _host.ScanMaterials());

        _status.Text = failed == 0
            ? $"Added {copied} item{(copied == 1 ? "" : "s")}."
            : $"Added {copied}, could not read {failed}.";
        _status.Foreground = failed == 0 ? Theme.Positive : Theme.Caution;
        if (copied > 0) _host.Gateway.Log.Activity($"You handed the AI {copied} file{(copied == 1 ? "" : "s")}");
        if (result.HashBudgetSpent) _status.Text += " Still reading some of them.";

        Update();
    }

    /// <summary>Never overwrite. Two files called setup.exe are two different things, and the ledger has to be able to say so.</summary>
    static string Unique(string wanted)
    {
        if (!File.Exists(wanted) && !Directory.Exists(wanted)) return wanted;
        var dir = Path.GetDirectoryName(wanted)!;
        var stem = Path.GetFileNameWithoutExtension(wanted);
        var ext = Path.GetExtension(wanted);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(from))
            CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    void OpenFolder() => MainWindow.OpenPath(Paths.Inbox, m =>
    {
        _status.Text = m;
        _status.Foreground = Theme.Caution;
    });

    void Highlight(bool on)
    {
        _dropZone.BorderBrush = on ? Theme.Accent : Theme.LineStrong;
        _dropZone.Background = on ? Theme.AccentSoft : Theme.BgSunken;
    }

    // ---- showing what is here ------------------------------------------------------------------

    public void Update()
    {
        var store = _host.Gateway.Materials;
        // What the owner handed over reads first — it is the half they are looking for, and the
        // half they can act on. What the AI produced follows underneath.
        var items = store.Present().OrderBy(m => m.Origin).ThenByDescending(m => m.FirstSeenAt).ToList();
        var notes = store.RecentNotes(25);

        // Signature-compared like every other list in this shell: a background tick that changed
        // nothing must not rebuild a tree the user is reading or scrolled.
        var itemSignature = string.Join('|', items.Select(m => $"{m.Id}:{m.Sha256}"));
        if (itemSignature != _itemSignature)
        {
            _itemSignature = itemSignature;
            _empty.IsVisible = items.Count == 0;
            _items.Children.Clear();
            foreach (var m in items) _items.Children.Add(ItemRow(m));
        }

        var noteSignature = string.Join('|', notes.Select(n => n.Id));
        if (noteSignature != _noteSignature)
        {
            _noteSignature = noteSignature;
            _notesEmpty.IsVisible = notes.Count == 0;
            _notes.Children.Clear();
            foreach (var n in notes) _notes.Children.Add(NoteRow(n));
        }
    }

    Control ItemRow(Material m)
    {
        var heading = Ui.Row(Theme.S2, Ui.With(Ui.Body(m.Name), t => t.FontWeight = FontWeight.SemiBold));
        if (m.Runnable) heading.Children.Add(Ui.Pill("runs", Theme.CautionSoft));

        return Ui.Card(Ui.Col(Theme.S1,
            heading,
            Ui.With(Ui.Muted($"{Origin(m)} · {Size(m.SizeBytes)} · arrived {m.FirstSeenAt.ToLocalTime():d MMM HH:mm}"),
                t => t.FontSize = Theme.Small),
            Ui.With(Ui.Mono($"{m.RelPath}   {m.ShortSha}", Theme.TextFaint), t => t.FontSize = Theme.Small)));
    }

    static Control NoteRow(MaterialNote n) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        Children =
        {
            Ui.With(Ui.Pill(n.Kind.ToString().ToLowerInvariant(), Theme.NeutralSoft),
                p => { p.VerticalAlignment = VerticalAlignment.Top; p.Margin = new Thickness(0, 0, Theme.S2, 0); }),
            Ui.With(Ui.Col(0,
                Ui.With(Ui.Body(n.Text), t => t.TextWrapping = TextWrapping.Wrap),
                Ui.With(Ui.Mono($"{n.At.ToLocalTime():d MMM HH:mm}" +
                                (n.SubjectSha is null ? "" : $" · {n.SubjectSha[..Math.Min(12, n.SubjectSha.Length)]}") +
                                (n.ParentSha is null ? "" : $" ← {n.ParentSha[..Math.Min(12, n.ParentSha.Length)]}"),
                    Theme.TextFaint), t => t.FontSize = Theme.Small)),
                c => c[Grid.ColumnProperty] = 1)
        }
    };

    static string Origin(Material m) => m.Origin == MaterialOrigin.Inbox ? "you gave this to the AI" : "the AI made this";

    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
