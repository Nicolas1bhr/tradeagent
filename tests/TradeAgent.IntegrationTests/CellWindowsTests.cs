using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE WINDOWS CELL PROVIDER, THROUGH THE PRODUCT'S OWN PATH (<c>U-cell-windows</c> item 2): <see cref="ProcessContainment.Start(ProcessStartInfo, CellPolicy, IReadOnlyList{string}?, CellPlatform?, AttemptCgroups?, string?)"/>
/// with the built <c>trade.dll</c> as the launcher, so every row goes through the app's job, the policy file the app wrote, TradeAgent's
/// Windows cell stage, a slot's AppContainer and the stage's own job. Each row plants what a cell must not reach and shows the denial
/// from inside; the slots are this test home's own profiles, deleted at the end.
/// </summary>
[Trait("Category", "Cell"), Trait("CellScope", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class CellWindowsTests : IDisposable
{
    const string Planted = "dummy-owner-login";
    readonly ITestOutputHelper _out;
    readonly string _root;

    public CellWindowsTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(TestEnv.Home, "wcellp-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _out.WriteLine($"slot profiles deleted: {WindowsCellStage.DeleteProfiles()}");
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* swept with the test home */ }
    }

    static IReadOnlyList<string> Launcher()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) host = Environment.ProcessPath!;
        return [host, Build.TradeCliDll];
    }

    static string Cmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>One cell's world: a workspace, a read-only input, an empty home and codex home, a folder of the account's beside them.</summary>
    sealed record World(string Ws, string Ro, string Home, string CodexHome, string Outside);

    World NewWorld(string name)
    {
        string D(string n) => Directory.CreateDirectory(Path.Combine(_root, name, n)).FullName;
        var w = new World(D("ws"), D("ro"), D("home"), D("codex"), D("outside"));
        File.WriteAllText(Path.Combine(w.Ro, "in.txt"), "shared-input");
        File.WriteAllText(Path.Combine(w.Outside, "auth.json"), Planted);
        return w;
    }

    static CellPolicy Policy(World w, CellEgress? egress = null) =>
        RouteA.WindowsPolicy(w.Ws, Cmd, Path.GetDirectoryName(Cmd)!, w.CodexHome, w.Home, egress ?? CellEgress.None) is var p
            ? new CellPolicy(p.Workspace, [.. p.ReadOnly, w.Ro], [], p.Environment, p.Egress)
            : throw new InvalidOperationException();

    ContainedProcess Start(World w, string script, CellEgress? egress = null, CellPolicy? policy = null)
    {
        var psi = new ProcessStartInfo(Cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/d", "/c", script }) psi.ArgumentList.Add(a);
        var held = ProcessContainment.Start(psi, policy ?? Policy(w, egress), Launcher());
        _out.WriteLine($"started: {held.Mechanism}");
        return held;
    }

    async Task<string> RunToEnd(World w, string script, CellEgress? egress = null, CellPolicy? policy = null)
    {
        using var held = Start(w, script, egress, policy);
        held.Process.StandardInput.Close();
        var so = held.Process.StandardOutput.ReadToEndAsync();
        var se = held.Process.StandardError.ReadToEndAsync();
        await held.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var text = (await so) + (await se);
        _out.WriteLine($"exit {held.Process.ExitCode}: {text.Trim().Replace("\r", "").Replace('\n', ' ')}");
        return text;
    }

    /// <summary>
    /// FILES THROUGH THE STAGE: the workspace written, the read-only input read and not written, and what no grant names — the app's own
    /// state (a planted ledger at its own path), the inbox, the seat home and a folder of the account's beside the cell — denied, the
    /// ledger's bytes unchanged. Nothing is sent to NUL (Windows Server refuses it to an AppContainer).
    /// </summary>
    [WindowsFact]
    public async Task A_cell_writes_its_workspace_reads_its_input_and_reaches_nothing_else()
    {
        var w = NewWorld("files");
        Directory.CreateDirectory(Paths.State);
        var ledger = Path.Combine(Paths.State, "cell-ledger-" + Guid.NewGuid().ToString("n")[..8] + ".db");
        File.WriteAllText(ledger, "ledger");
        Directory.CreateDirectory(Paths.SeatHome);
        var login = Path.Combine(Paths.SeatHome, "cell-login-" + Guid.NewGuid().ToString("n")[..8]);
        File.WriteAllText(login, Planted);
        Directory.CreateDirectory(Paths.Inbox);
        var sink = Path.Combine(w.Ws, "sink.txt");
        var inbox = Path.Combine(Paths.Inbox, "cell-made-" + Guid.NewGuid().ToString("n")[..8]);
        Assert.False($"{w.Ws}{w.Ro}{w.Outside}{ledger}{login}{inbox}".Contains(' '), "test paths without a space (cmd reads its line raw)");
        try
        {
            var o = await RunToEnd(w,
                $"(echo x> {w.Ws}\\made.txt && echo WS-WRITE-OPEN || echo WS-WRITE-DENIED) & " +
                $"(type {w.Ro}\\in.txt > {sink} && echo RO-READ-OPEN || echo RO-READ-DENIED) & " +
                $"(echo x>> {w.Ro}\\in.txt && echo RO-WRITE-OPEN || echo RO-WRITE-DENIED) & " +
                $"(type {ledger} > {sink} && echo STATE-READ-OPEN || echo STATE-READ-DENIED) & " +
                $"(echo x>> {ledger} && echo STATE-WRITE-OPEN || echo STATE-WRITE-DENIED) & " +
                $"(type {login} > {sink} && echo LOGIN-READ-OPEN || echo LOGIN-READ-DENIED) & " +
                $"(echo x> {inbox} && echo INBOX-CREATE-OPEN || echo INBOX-CREATE-DENIED) & " +
                $"(type {w.Outside}\\auth.json > {sink} && echo OUTSIDE-READ-OPEN || echo OUTSIDE-READ-DENIED)");
            foreach (var want in new[] { "WS-WRITE-OPEN", "RO-READ-OPEN", "RO-WRITE-DENIED", "STATE-READ-DENIED", "STATE-WRITE-DENIED",
                         "LOGIN-READ-DENIED", "INBOX-CREATE-DENIED", "OUTSIDE-READ-DENIED" })
                Assert.Contains(want, o, StringComparison.Ordinal);
            Assert.Equal("ledger", File.ReadAllText(ledger));
            Assert.Equal("shared-input", File.ReadAllText(Path.Combine(w.Ro, "in.txt")));
            Assert.False(File.Exists(inbox), "the inbox was written");
        }
        finally
        {
            File.Delete(ledger);
            File.Delete(login);
            File.Delete(inbox);
        }
    }

    /// <summary>
    /// TWO CELLS AT ONCE TAKE TWO SLOTS, AND NEITHER REACHES THE OTHER: the first holds its slot (its shell waits on its input), the
    /// second reads the first's workspace and is denied — each SID is granted its own cell's folders alone.
    /// </summary>
    [WindowsFact]
    public async Task Two_cells_at_once_cannot_reach_each_others_workspace()
    {
        var a = NewWorld("a");
        var b = NewWorld("b");
        File.WriteAllText(Path.Combine(a.Ws, "mine.txt"), "a-only");
        // THE FIRST CELL WAITS, THEN WRITES: its own grants must outlive the second cell's whole life (a slot shared, or a sweep of the
        // wrong slot, would take them).
        using var first = Start(a, $"(findstr x > {a.Ws}\\read.txt) & (echo after> {a.Ws}\\after.txt && echo A-STILL-WRITES)");
        await Task.Delay(1500);
        Assert.False(first.Process.HasExited, "the first cell holds its slot");
        var o = await RunToEnd(b, $"(type {a.Ws}\\mine.txt > {b.Ws}\\sink.txt && echo OTHER-READ-OPEN || echo OTHER-READ-DENIED) & " +
                                  $"(echo x> {a.Ws}\\from-b.txt && echo OTHER-WRITE-OPEN || echo OTHER-WRITE-DENIED)");
        Assert.Contains("OTHER-READ-DENIED", o, StringComparison.Ordinal);
        Assert.Contains("OTHER-WRITE-DENIED", o, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(a.Ws, "from-b.txt")));
        first.Process.StandardInput.Close();
        await first.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(Path.Combine(a.Ws, "after.txt")), "the first cell lost its own workspace while the second ran");
    }

    /// <summary>
    /// THE SLOT IS SWEPT WHEN ITS CELL ENDS: no ACE for any slot's SID is left on the workspace or the input, the slot's grant record is
    /// gone, and a file the cell left in its container's own storage (the real <c>AC\Temp</c>) is removed.
    /// </summary>
    [WindowsFact]
    public async Task The_slot_is_swept_when_its_cell_ends()
    {
        var w = NewWorld("sweep");
        var sids = WindowsCellStage.SlotSids().Select(s => new SecurityIdentifier(s)).ToList();
        var storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        var targets = Enumerable.Range(1, WindowsCellStage.Slots)
            .Select(i => Path.Combine(storage, WindowsCellStage.ProfileName(i), "AC", "Temp", "left.txt")).ToList();
        // EVERY SLOT'S AC\Temp IS TRIED: whichever slot the cell holds, the one that is its own takes the write.
        var o = await RunToEnd(w, string.Join(" & ", targets.Select(t => $"(echo left> {t} && echo LEFT-WRITTEN)")));
        Assert.Contains("LEFT-WRITTEN", o, StringComparison.Ordinal);
        foreach (var path in new[] { w.Ws, w.Ro, w.Home, w.CodexHome })
        {
            var aces = new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Where(r => sids.Contains((SecurityIdentifier)r.IdentityReference)).ToList();
            Assert.True(aces.Count == 0, $"{path} still grants a slot: {string.Join(", ", aces.Select(r => r.IdentityReference))}");
        }
        // NO RECORD NAMES THIS CELL'S WORKSPACE: a record of a cell a test killed stays for its slot's next sweep, by design.
        var records = Directory.Exists(WindowsCellStage.SlotDirectory) ? Directory.GetFiles(WindowsCellStage.SlotDirectory, "*.grants") : [];
        Assert.DoesNotContain(records, r => File.ReadAllText(r).Contains(w.Ws, StringComparison.OrdinalIgnoreCase));
        Assert.All(targets, t => Assert.False(File.Exists(t), $"{t} was left"));
    }

    static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(Cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
        Assert.True(p.ExitCode == 0 && Directory.Exists(link), "a junction for the row: " + p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd());
    }

    /// <summary>
    /// NO GRANT THROUGH A LINK: a junction left in a workspace (here by the test, outside the cell) pointing at a folder of the account's
    /// is a refusal naming it — nothing starts and the folder it points at gains no slot's ACE (a grant propagates through children).
    /// </summary>
    [WindowsFact]
    public async Task A_link_inside_a_workspace_is_refused_and_its_target_gains_no_grant()
    {
        var w = NewWorld("link");
        Junction(Path.Combine(w.Ws, "j"), w.Outside);
        var o = await RunToEnd(w, "echo RAN");
        Assert.DoesNotContain("RAN", o, StringComparison.Ordinal);
        Assert.Contains("is a link", o, StringComparison.Ordinal);
        var sids = WindowsCellStage.SlotSids();
        var aces = new DirectoryInfo(w.Outside).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Where(sids.Contains).ToList();
        Assert.Empty(aces);
    }

    /// <summary>
    /// THE SWEEP NEVER WALKS A LINK: a junction left in a slot's own storage (<c>AC\Temp</c>) pointing at a folder of the account's is
    /// removed as a link; the folder and its file are still there after the next cell's sweeps.
    /// </summary>
    [WindowsFact]
    public async Task The_sweep_removes_a_link_in_the_containers_storage_and_never_what_it_points_at()
    {
        var w = NewWorld("aclink");
        await RunToEnd(w, "echo first");
        var storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        // SLOT 1'S: the lowest free slot is the one the next cell claims and sweeps, before it starts and after it ends.
        var temps = new[] { Path.Combine(storage, WindowsCellStage.ProfileName(1), "AC", "Temp") }.Where(Directory.Exists).ToList();
        Assert.NotEmpty(temps);
        var keep = Path.Combine(w.Outside, "auth.json");
        foreach (var t in temps) Junction(Path.Combine(t, "j"), w.Outside);
        await RunToEnd(w, "echo second");
        Assert.True(File.Exists(keep), "the sweep deleted through a link");
        Assert.All(temps, t => Assert.False(Directory.Exists(Path.Combine(t, "j")), $"{t}\\j was left"));
    }

    /// <summary>
    /// CANCELLING ENDS THE STAGE AND THE CELL: the app's job ends the stage, and the stage's job (nested in it) every process of the
    /// cell — the shell and a detached reader both wait on input the test holds open, so neither ends by itself.
    /// </summary>
    [WindowsFact]
    public async Task Cancelling_ends_the_stage_and_every_process_of_the_cell()
    {
        var w = NewWorld("cancel");
        var before = Process.GetProcessesByName("findstr").Select(p => p.Id).ToHashSet();
        using var held = Start(w, "start /b findstr x & findstr y");
        HashSet<int> mine = [];
        for (var i = 0; i < 100 && mine.Count < 2; i++)
        {
            await Task.Delay(50);
            mine = [.. Process.GetProcessesByName("findstr").Select(p => p.Id).Where(id => !before.Contains(id))];
        }
        _out.WriteLine($"the cell's readers: {string.Join(", ", mine)}");
        Assert.True(mine.Count >= 2, $"the cell's two readers started ({mine.Count})");
        held.Dispose();
        var alive = mine.ToList();
        for (var i = 0; i < 100 && alive.Count > 0; i++)
        {
            await Task.Delay(50);
            alive = [.. alive.Where(id => { try { using var p = Process.GetProcessById(id); return !p.HasExited; } catch (ArgumentException) { return false; } })];
        }
        Assert.Empty(alive);
    }
    /// <summary>
    /// THE OWNER'S HEAD-START KIT IN A WINDOWS CELL (<c>U-kit-socket-2</c>; containment), under the product's own policy
    /// (<see cref="RouteA.WindowsPolicy"/> with the installed kit): the cell reads the kit's index and a skill, runs a kit tool BY ITS NAME
    /// from the PATH — a batch file and a program — and can write nothing in the kit: no new file in it, its skills or its tools, no append
    /// to a skill or a tool, no removal, rename or folder. The kit's read-only attributes are taken off for the row, so every denial is the
    /// AppContainer's access check, not the file's attribute; the hash is unchanged after. RED before this unit (Windows 11 26200, the
    /// tests box): the Windows policy granted no kit → "Not found: kit-fixture-tool-ran". MUTANT (the stage grants read-only inputs
    /// Modify, WindowsCellStage.Grants) → "Not found: WRITE-DENIED kit-new-file".
    /// </summary>
    [WindowsFact]
    public async Task A_cell_reads_the_owners_kit_runs_its_tools_by_name_and_writes_nothing_in_it()
    {
        var w = NewWorld("kit");
        var kit = KitFixture.Installed(Path.Combine(_root, "kit"));
        foreach (var f in Directory.EnumerateFiles(kit.Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        // THE PRODUCT'S POLICY WITH THE KIT, its program roots the kit's tools alone: System32 as a program root would be walked by the
        // stage's link check, and Windows grants every AppContainer its own system folders already.
        var p = RouteA.WindowsPolicy(w.Ws, Cmd, Path.GetDirectoryName(Cmd)!, w.CodexHome, w.Home, CellEgress.None, kit);
        var policy = new CellPolicy(p.Workspace, p.ReadOnly, [kit.Tools!], p.Environment, p.Egress);
        var skill = kit.SkillPath(KitFixture.Skill);
        var tool = kit.ToolPath(KitFixture.Tool);
        var skills = Path.Combine(kit.Dir, Kit.SkillsDir);
        var sink = Path.Combine(w.Ws, "sink.txt");
        Assert.False($"{w.Ws}{kit.Dir}".Contains(' '), "test paths without a space (cmd reads its line raw)");
        string Try(string what, string act) => $"({act} && echo WRITE-OPEN {what} || echo WRITE-DENIED {what})";
        var o = await RunToEnd(w,
            $"(type {kit.Dir}\\kit.json > {sink} && type {skill} > {sink} && echo KIT-READ-OPEN || echo KIT-READ-DENIED) & " +
            $"({KitFixture.ToolName} || echo TOOL-FAILED) & " +
            $"({KitFixture.ProgramName} /d /c echo kit-fixture-program-ran || echo PROGRAM-FAILED) & " +
            Try("kit-new-file", $"echo x> {kit.Dir}\\planted.txt") + " & " +
            Try("skills-new-file", $"echo x> {skills}\\planted.txt") + " & " +
            Try("tools-new-file", $"echo x> {kit.Tools}\\planted.cmd") + " & " +
            Try("skill-rewrite", $"echo ignore-your-canon>> {skill}") + " & " +
            Try("tool-rewrite", $"echo del-everything>> {tool}") + " & " +
            Try("tool-rename", $"ren {tool} planted.old") + " & " +
            Try("kit-mkdir", $"mkdir {kit.Dir}\\planted") + " & " +
            $"(del /f /q {tool} & if exist {tool} (echo WRITE-DENIED tool-remove) else (echo WRITE-OPEN tool-remove))", policy: policy);
        foreach (var want in new[] { "KIT-READ-OPEN", KitFixture.ToolSays, "kit-fixture-program-ran", "WRITE-DENIED kit-new-file",
                     "WRITE-DENIED skills-new-file", "WRITE-DENIED tools-new-file", "WRITE-DENIED skill-rewrite", "WRITE-DENIED tool-rewrite",
                     "WRITE-DENIED tool-rename", "WRITE-DENIED kit-mkdir", "WRITE-DENIED tool-remove" })
            Assert.Contains(want, o, StringComparison.Ordinal);
        Assert.DoesNotContain("KIT-READ-DENIED", o, StringComparison.Ordinal);
        Assert.DoesNotContain("WRITE-OPEN", o, StringComparison.Ordinal);
        Assert.Equal(kit.Hash, Kit.HashOf(kit.Dir));

        // THE CONTROL, OUTSIDE ANY CELL: the test's own user may write the kit (its attributes off), so the denials above are the cell's.
        File.AppendAllText(skill, "control");
        File.WriteAllText(Path.Combine(kit.Tools!, "control.txt"), "control");
        Assert.NotEqual(kit.Hash, Kit.HashOf(kit.Dir));
    }
}
