using TradeAgent.Core;
using TradeAgent.Tests;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE "FREE SPACE" PRESS'S RULE (<c>U-free-space</c>): what TradeAgent may let go is decided by an ALLOWLIST of kinds, never a denylist, so a
/// home holding every protected kind beside every lettable one lists only the lettable ones; a folder that is, or holds, a link is never
/// listed and never followed; and press two removes exactly what press one showed — an item that changed, grew a link or is no longer a
/// lettable kind stays where it is, and a file added between the presses survives. The folders are the tests' own, so this runs on every runner.
/// </summary>
public class FreeSpaceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "tradeagent-tests", "free-space", Guid.NewGuid().ToString("N")[..12]);
    readonly string _home;
    readonly string _outside;
    readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    readonly List<string> _protected = [];

    static readonly DateTime Old = DateTime.UtcNow.AddHours(-3);

    public FreeSpaceTests()
    {
        _home = Path.Combine(_root, "TradeAgent");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "his-file.txt"), "the owner's own file, outside the home");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetAttributes(f, FileAttributes.Normal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    FreeSpaceRoots Roots() => new(_home, v => v is "0.2.0" or "0.1.9");

    string Protect(params string[] parts)
    {
        var f = Write(Path.Combine([_home, .. parts]));
        _protected.Add(f);
        return f;
    }

    static string Write(string file, string text = "bytes")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    /// <summary>Every file and folder under <paramref name="dir"/> written <see cref="Old"/>, the folder itself last.</summary>
    static void Age(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, Old);
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).Reverse()) Directory.SetLastWriteTimeUtc(d, Old);
        Directory.SetLastWriteTimeUtc(dir, Old);
    }

    string OlderHome => Path.Combine(_root, "TradeAgent-from-0.1.2-20261031-101500");
    string Installed => Path.Combine(_home, "updates", "0.2.0");
    string Installed2 => Path.Combine(_home, "updates", "0.1.9");
    string Leftover => Path.Combine(_home, "cell-bench", "0123456789ab");

    /// <summary>THE FIXTURE: every protected kind of the current home, and things named like lettable ones that are not, beside the lettable ones.</summary>
    void Fixture()
    {
        // THE CURRENT HOME'S RECORDS, TAPE, LEDGERS, DATASETS, REPORTS, KIT, SIGN-IN AND WORK: never listed.
        Protect("state", "tradeagent.db");
        Protect("state", "tradeagent.db-wal");
        Protect("state", "tape.db");
        Protect("state", "tape.db-wal");
        Protect("state", "kit-record.jsonl");
        Protect("state", "data", "binance", "BTCUSDT-1m.csv");
        Protect("state", "reports", "2026-11-13.md");
        Protect("workspace", "agent", "journal.md");
        Protect("workspace", "inbox", "his-document.pdf");
        Protect("seat-home", "codex", "auth.json");
        Protect("seat-home", "home", ".profile");
        Protect("kit", "kit.json");
        Protect("kit-staging", "pending", "kit.json");
        Protect("cells", "a-cell", "workspace", "out.txt");
        Protect("bin", "TradeAgent.Bridge.dll");
        Protect("tools", "codex", "codex.exe");
        Protect("logs", "bridge.log");
        Protect("bridge", "state.json");
        // NAMED LIKE A LETTABLE KIND, AND NOT ONE: a download newer than the running version, a check folder in use, a folder of the
        // check's that is not one of its own names, and loose files where only folders are lettable.
        Protect("updates", "9.9.9", "TradeAgent-Setup-9.9.9.exe");
        Protect("updates", "notes.txt");
        Protect("cell-bench", "fedcba987654", "probe", "probe.exe");
        var notOurs = Protect("cell-bench", "his-folder", "his.txt");
        Age(Path.GetDirectoryName(notOurs)!);
        // BESIDE THE HOME: the tape copy a Start fresh carries, someone else's folder, and a folder named like an older home that holds no home.
        _protected.Add(Write(Path.Combine(_root, "TradeAgent-tape-carrying-20261031-101500", "tape.db")));
        _protected.Add(Write(Path.Combine(_root, "Other-from-0.1.2-20261031-101500", "state", "tradeagent.db")));
        _protected.Add(Write(Path.Combine(_root, "TradeAgent-from-0.1.3-20261101-090000", "notes.txt")));

        // LETTABLE: an older home set aside whole, two installers already run, a check's leftover folder untouched for hours.
        Write(Path.Combine(OlderHome, "state", "tradeagent.db"), "an older version's records");
        Write(Path.Combine(OlderHome, "state", "tape.db"), "an older version's tape");
        Write(Path.Combine(Installed, "TradeAgent-Setup-0.2.0.exe"), new string('x', 4096));
        Write(Path.Combine(Installed2, "TradeAgent-Setup-0.1.9.exe"), new string('y', 2048));
        Write(Path.Combine(Leftover, "probe", "probe.exe"), "the probe's spare copy");
        Write(Path.Combine(Leftover, "ws", "control.txt"), "control");
        Age(Leftover);
    }

    /// <summary>
    /// THE ALLOWLIST. RED on the stub, which listed every folder of the home: "an inventory without the allowlist lists the tape".
    /// </summary>
    [Fact]
    public void The_inventory_lists_only_the_lettable_kinds_and_never_a_protected_one()
    {
        Fixture();
        var plan = FreeSpace.Inventory(Roots(), _now);

        var listed = plan.Items.SelectMany(i => i.Files.Select(f => f.Path)).ToHashSet();
        var leaked = _protected.Where(p => listed.Contains(p) || plan.Items.Any(i => Under(p, i.Path))).ToList();
        Assert.True(leaked.Count == 0, $"the inventory lists {leaked.Count} protected files: {string.Join(", ", leaked.Take(6))}");

        Assert.Equal(
            new[] { (LetGoKind.CheckLeftover, Leftover), (LetGoKind.Installer, Installed2), (LetGoKind.Installer, Installed), (LetGoKind.OlderHome, OlderHome) }
                .OrderBy(x => x.Item2, StringComparer.Ordinal),
            plan.Items.Select(i => (i.Kind, i.Path)).OrderBy(x => x.Item2, StringComparer.Ordinal));
        Assert.Equal(plan.Items.Sum(i => i.Bytes), plan.Bytes);
        Assert.Equal(4096, plan.Items.Single(i => i.Path == Installed).Bytes);
    }

    [Fact]
    public void Press_two_removes_what_press_one_showed_and_says_what_it_freed()
    {
        Fixture();
        var plan = FreeSpace.Inventory(Roots(), _now);
        var done = FreeSpace.Remove(plan, Roots(), _now);

        Assert.Empty(done.Skipped);
        Assert.Equal(4, done.Removed.Count);
        Assert.Equal(plan.Bytes, done.Freed);
        foreach (var item in plan.Items) Assert.False(Directory.Exists(item.Path), $"{item.Path} is still there");
        foreach (var p in _protected) Assert.True(File.Exists(p), $"{p} was removed");
        Assert.True(File.Exists(Path.Combine(_outside, "his-file.txt")));
    }

    [Fact]
    public void A_file_added_between_the_presses_survives_and_its_item_is_skipped_as_changed()
    {
        Fixture();
        var plan = FreeSpace.Inventory(Roots(), _now);
        var added = Write(Path.Combine(Installed, "arrived-after.txt"), "new");
        File.AppendAllText(Path.Combine(Leftover, "ws", "control.txt"), " grown");

        var done = FreeSpace.Remove(plan, Roots(), _now);

        Assert.True(File.Exists(added));
        Assert.True(File.Exists(Path.Combine(Installed, "TradeAgent-Setup-0.2.0.exe")));
        Assert.True(File.Exists(Path.Combine(Leftover, "probe", "probe.exe")));
        Assert.Equal(new[] { Installed, Leftover }.Order(StringComparer.Ordinal), done.Skipped.Where(s => s.Why == LetGoSkip.Changed).Select(s => s.Item.Path).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(Installed2));
        Assert.False(Directory.Exists(OlderHome));
    }

    [Fact]
    public void An_item_that_is_no_longer_a_lettable_kind_is_skipped_and_kept()
    {
        Fixture();
        var plan = FreeSpace.Inventory(Roots(), _now);
        // The running version moved back under the download (say, a rollback): its installer is no longer one that has been run.
        var done = FreeSpace.Remove(plan, new FreeSpaceRoots(_home, v => v is "0.1.9"), _now);

        Assert.Contains(done.Skipped, s => s.Item.Path == Installed && s.Why == LetGoSkip.NoLongerLettable);
        Assert.True(File.Exists(Path.Combine(Installed, "TradeAgent-Setup-0.2.0.exe")));
    }

    /// <summary>
    /// THE ESCAPE GUARD: a folder that is a link, or holds one, is never listed — a link to the owner's own folder outside the home
    /// would otherwise be walked and its files listed as TradeAgent's — and press two over a plan that names one leaves its target whole.
    /// </summary>
    [Fact]
    public void A_folder_that_is_or_holds_a_link_is_never_listed_and_its_target_survives_press_two()
    {
        Fixture();
        var linked = Path.Combine(_home, "cell-bench", "aaaaaaaaaaaa");
        LinkDirectory(linked, _outside);
        var holding = Path.Combine(_home, "cell-bench", "bbbbbbbbbbbb");
        Write(Path.Combine(holding, "ws", "control.txt"));
        LinkDirectory(Path.Combine(holding, "probe"), _outside);
        Directory.CreateDirectory(Path.Combine(_home, "updates"));
        LinkDirectory(Path.Combine(_home, "updates", "0.1.8"), Path.Combine(_home, "state"));
        Age(holding);

        var plan = FreeSpace.Inventory(Roots() with { Installed = v => v is "0.2.0" or "0.1.9" or "0.1.8" }, _now);
        Assert.DoesNotContain(plan.Items, i => i.Path == linked || i.Path == holding || i.Path.EndsWith("0.1.8", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Items.SelectMany(i => i.Files), f => f.Path.Contains("his-file", StringComparison.Ordinal));

        var His = new FileInfo(Path.Combine(_outside, "his-file.txt"));
        // A PLAN NAMING THEM ANYWAY (as a plan made before the link was swapped in would): nothing behind a link is removed.
        var forged = new FreeSpacePlan(
        [
            new LetGoItem(LetGoKind.CheckLeftover, linked, [new LetGoFile(Path.Combine(linked, "his-file.txt"), His.Length, His.LastWriteTimeUtc)], [linked], Old),
            new LetGoItem(LetGoKind.CheckLeftover, holding,
                [new LetGoFile(Path.Combine(holding, "probe", "his-file.txt"), His.Length, His.LastWriteTimeUtc)],
                [holding, Path.Combine(holding, "probe")], Old),
        ], _now);
        var done = FreeSpace.Remove(forged, Roots(), _now);
        Assert.Empty(done.Removed);
        Assert.True(File.Exists(Path.Combine(_outside, "his-file.txt")));
        foreach (var p in _protected) Assert.True(File.Exists(p), $"{p} was removed");
    }

    /// <summary>A link swapped in between the presses, inside an item press one listed: the item is skipped, and what the link points at survives.</summary>
    [Fact]
    public void A_link_swapped_in_between_the_presses_is_skipped_and_never_followed()
    {
        Fixture();
        var plan = FreeSpace.Inventory(Roots(), _now);
        var probe = Path.Combine(Leftover, "probe");
        Directory.Delete(probe, recursive: true);
        Write(Path.Combine(_outside, "probe.exe"), "the probe's spare copy");
        File.SetLastWriteTimeUtc(Path.Combine(_outside, "probe.exe"), Old);
        LinkDirectory(probe, _outside);

        var done = FreeSpace.Remove(plan, Roots(), _now);

        Assert.Contains(done.Skipped, s => s.Item.Path == Leftover);
        Assert.True(File.Exists(Path.Combine(_outside, "probe.exe")));
        Assert.True(File.Exists(Path.Combine(_outside, "his-file.txt")));
    }

    /// <summary>A path that climbs out with "..", handed to press two: refused, and nothing it names is removed.</summary>
    [Fact]
    public void A_path_that_climbs_out_of_its_folder_is_refused()
    {
        Fixture();
        var climb = Path.Combine(_home, "cell-bench", "..", "state");
        var tape = Path.Combine(_home, "state", "tape.db");
        var forged = new FreeSpacePlan(
        [
            new LetGoItem(LetGoKind.CheckLeftover, climb, [new LetGoFile(Path.Combine(climb, "tape.db"), new FileInfo(tape).Length, File.GetLastWriteTimeUtc(tape))], [climb], Old),
            new LetGoItem(LetGoKind.CheckLeftover, Leftover,
                [new LetGoFile(Path.Combine(Leftover, "..", "..", "state", "tape.db"), new FileInfo(tape).Length, File.GetLastWriteTimeUtc(tape))], [Leftover], Old),
        ], _now);

        Assert.Null(FreeSpace.MayLetGo(climb, Roots()));
        var done = FreeSpace.Remove(forged, Roots(), _now);
        Assert.Empty(done.Removed);
        Assert.True(File.Exists(tape));
    }

    /// <summary>A file link inside a lettable folder, to a file of the owner's outside the home: the folder is not listed.</summary>
    [OnlyOnFact("a file symbolic link needs a privilege on Windows that a test process has no business assuming", "linux", "macos")]
    public void A_folder_holding_a_file_link_is_never_listed()
    {
        Fixture();
        File.CreateSymbolicLink(Path.Combine(Installed2, "his-file.txt"), Path.Combine(_outside, "his-file.txt"));
        var plan = FreeSpace.Inventory(Roots(), _now);
        Assert.DoesNotContain(plan.Items, i => i.Path == Installed2);
    }

    static bool Under(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>A directory link: a junction on Windows (it needs no privilege), a symbolic link elsewhere.</summary>
    static void LinkDirectory(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new IOException("the test could not start cmd.exe to make a junction");
        var said = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();
        if (mklink.ExitCode != 0 || !Directory.Exists(link)) throw new IOException($"the test could not make a junction at {link}: {said.Trim()}");
    }
}
