using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TradeAgent.Core;

namespace TradeAgent.App;

/// <summary>
/// THE OWNER'S HEAD-START KIT, ON SETTINGS (<c>U-kit-socket</c> item 3): what is installed, with its hash, and his two acts — "Add a
/// kit" from a folder or a zip, and "Remove the kit" — each two presses, no terminal. Press one of an add shows what the chosen kit
/// holds (<see cref="Kit.Preview"/>: staged, checked, hashed, thrown away); press two installs exactly that, refused if the kit changed
/// between them (<see cref="AppHost.InstallKit"/>). Built once and updated in place: the tick rewrites the value and never disarms a
/// half-pressed confirmation.
/// </summary>
sealed class KitCard
{
    readonly AppHost _host;
    readonly TextBlock _installedValue = Ui.Mono("—");
    readonly TextBlock _chosenValue = Ui.Mono("");
    readonly TextBlock _note = Ui.Muted("");
    readonly Button _install;
    readonly Button _remove;
    string? _chosen;
    string? _chosenHash;
    bool _looking;

    public KitCard(AppHost host)
    {
        _host = host;
        _install = Ui.Confirm(Labels.InstallKitPress, Labels.InstallKitConfirm, Install);
        _remove = Ui.Confirm(Labels.RemoveKitPress, Labels.RemoveKitConfirm, Remove);
        _install.IsVisible = false;
        _chosenValue.IsVisible = false;
        Root = Ui.Section("Head-start kit", Ui.Col(Theme.S4,
            Ui.KeyValueLive("Installed", _installedValue),
            Ui.Wrap(Theme.S2,
                Ui.Secondary(Labels.ChooseKitFolder, () => ChooseAsync(folder: true)),
                Ui.Secondary(Labels.ChooseKitZip, () => ChooseAsync(folder: false)),
                _remove),
            _chosenValue,
            _install,
            _note,
            Ui.Divider(),
            Ui.Muted("Your own skills and tools for the AI. Every executor's sealed cell can read the kit and run its tools; nothing the AI " +
                     "runs can change it, and if the folder ever changes after you install it, no seat is given it until you add it again."),
            Ui.Micro($"A kit is a folder, or a zip of one, holding {Kit.IndexFile} (its name and a one-line description of each skill and tool), " +
                     $"{Kit.SkillsDir}/<name>/{Kit.SkillFile} for each skill and {Kit.ToolsDir}/<name> for each tool. Executors' cells read it from " +
                     "their next turn; every seat is told of it when the AI next starts.")));
        Update();
    }

    public Control Root { get; }

    /// <summary>The VALUE only, on the tick: what is installed now, or why nothing is given to any seat.</summary>
    public void Update()
    {
        var state = Kit.Current();
        _installedValue.Text = state.Installed is { } kit
            ? $"{kit.Index.Name} — {Count(kit.Index.Skills.Count, "skill")}, {Count(kit.Index.Tools.Count, "tool")} — kit {kit.Hash[..12]}, " +
              $"{(kit.Source == Kit.BundledSource ? Kit.BundledSource : "from " + kit.Source)}, {kit.At.ToLocalTime():yyyy-MM-dd HH:mm}"
            : state.Problem ?? "No kit installed.";
        _remove.IsVisible = state.Installed is not null || state.Problem is not null && Directory.Exists(Kit.Dir());
    }

    static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    async Task ChooseAsync(bool folder)
    {
        if (_looking || TopLevel.GetTopLevel(Root)?.StorageProvider is not { } storage) return;
        string? path;
        if (folder)
        {
            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Labels.ChooseKitFolder, AllowMultiple = false });
            path = picked.Count == 1 ? picked[0].TryGetLocalPath() : null;
        }
        else
        {
            var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Labels.ChooseKitZip, AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] }]
            });
            path = picked.Count == 1 ? picked[0].TryGetLocalPath() : null;
        }
        if (path is null) return;
        await LookAsync(path);
    }

    /// <summary>
    /// PRESS ONE: what the chosen kit holds, checked as an install would check it. A new choice disarms a half-pressed install, so press
    /// two never installs a kit the owner was not shown.
    /// </summary>
    internal async Task LookAsync(string path)
    {
        _looking = true;
        Ui.DisarmConfirm(_install);
        _install.IsVisible = false;
        _chosen = _chosenHash = null;
        _note.Text = "Checking the kit…";
        try
        {
            var (index, hash, refusal) = await Task.Run(() => Kit.Preview(path));
            _chosenValue.IsVisible = true;
            if (index is null || hash is null)
            {
                _chosenValue.Text = path;
                _note.Text = $"This kit cannot be installed: {refusal}";
                return;
            }
            _chosen = path;
            _chosenHash = hash;
            _chosenValue.Text = $"{index.Name} — kit {hash[..12]}\n" +
                                $"Skills: {(index.Skills.Count == 0 ? "none" : string.Join(", ", index.Skills.Select(s => s.Name)))}\n" +
                                $"Tools: {(index.Tools.Count == 0 ? "none" : string.Join(", ", index.Tools.Select(t => t.Name)))}";
            _note.Text = Kit.Current().Installed is { } now
                ? $"Installing it replaces {now.Index.Name} (kit {now.Hash[..12]})."
                : "Nothing is installed yet.";
            _install.IsVisible = true;
        }
        finally { _looking = false; }
    }

    /// <summary>PRESS TWO of an add: exactly the kit press one showed, or a refusal in words.</summary>
    void Install()
    {
        if (_chosen is null || _chosenHash is null) return;
        var refusal = _host.InstallKit(_chosen, _chosenHash);
        _note.Text = refusal ?? "Installed. Executors' cells read it from their next turn; every seat is told of it when the AI next starts.";
        _install.IsVisible = false;
        _chosen = _chosenHash = null;
        Update();
    }

    /// <summary>PRESS TWO of a removal.</summary>
    void Remove()
    {
        _note.Text = _host.RemoveKit() ?? "Removed. No cell is given it from its next turn.";
        Update();
    }
}
