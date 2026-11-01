using System.Diagnostics;
using System.Runtime.Versioning;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE WINDOWS CELLS' WEB, AND WHO MAY SPEAK TO THE DOOR (<c>U-cell-windows-egress</c>). Through the product's own path — the built
/// <c>trade.dll</c> as the launcher, the app's job, the cell stage, a slot's AppContainer:
/// <list type="bullet">
/// <item>the one fixed elevated helper places the 443 rule on EVERY slot's container, idempotently, refuses anything but its one argument, and
/// creates nothing in the account's home (CI's elevated, disposable runner only — it changes the machine; the UAC prompt itself cannot be shown
/// there);</item>
/// <item>the benchmark's web rows hold only when every slot was measured under the rule: a rule on seven slots of eight is NOT web;</item>
/// <item>the grant's job container refuses a peer whose process runs in an AppContainer though it is in the job (card 2);</item>
/// <item>the attempt's processes are Windows' own list of its job, never a name: a process outside the cell is never one of them (card 3).</item>
/// </list>
/// </summary>
[Trait("Category", "Cell"), Trait("CellScope", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class CellWindowsEgressTests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly string _root;

    public CellWindowsEgressTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(TestEnv.Home, "wegress-" + Guid.NewGuid().ToString("n")[..8]);
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

    /// <summary>
    /// THE HELPER ON EVERY SLOT, AND EVERY SLOT MEASURED UNDER IT: sixteen rules (TCP all-but-443 and all UDP, per slot) absent, placed, read
    /// back exactly, placed again unchanged; then the benchmark's two web rows hold, naming all eight slots.
    /// </summary>
    [WindowsAdminFact]
    public async Task The_helper_places_the_443_rule_on_every_slot_and_the_benchmark_measures_every_slot_under_it()
    {
        RequireElevated();
        var rules = WindowsCellEgress.RulesFor(Paths.Home);
        Assert.Equal(2 * WindowsCellSlots.Count, rules.Count);
        Assert.Equal(WindowsCellStage.SlotSids(), [.. rules.Select(r => r.Sid).Distinct()]);
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            Assert.Equal(rules.Count, WindowsCellEgress.Missing(rules).Count);
            WindowsCellEgress.Place(rules);
            Assert.Empty(WindowsCellEgress.Missing(rules));
            WindowsCellEgress.Place(rules);   // idempotent: already exactly right, left alone
            Assert.Empty(WindowsCellEgress.Missing(rules));

            var rows = await WebRows();
            foreach (var r in rows) _out.WriteLine($"  {(r.Held ? "held" : "FAIL")}  {r.Name}: {r.Seen}");
            Assert.All(rows, r => Assert.True(r.Held, $"{r.Name}: {r.Seen}"));
            for (var slot = 1; slot <= WindowsCellSlots.Count; slot++)
                Assert.Contains($"slot {slot} reached", rows.Single(r => r.Name == "web-443-granted").Seen, StringComparison.Ordinal);
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// A RULE ON SEVEN SLOTS OF EIGHT IS NOT WEB: the benchmark measures every slot, so slot 8 — whose container no rule names — reaches port 80
    /// and the proof's web row does not hold, naming slot 8.
    /// </summary>
    [WindowsAdminFact]
    public async Task A_rule_on_seven_slots_of_eight_is_not_web()
    {
        RequireElevated();
        var rules = WindowsCellEgress.RulesFor(Paths.Home);
        var seven = rules.Where(r => r.Slot != WindowsCellSlots.Count).ToList();
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            WindowsCellEgress.Place(seven);
            Assert.Empty(WindowsCellEgress.Missing(seven));
            Assert.Equal(2, WindowsCellEgress.Missing(rules).Count);

            var rows = await WebRows();
            foreach (var r in rows) _out.WriteLine($"  {(r.Held ? "held" : "FAIL")}  {r.Name}: {r.Seen}");
            var nothingElse = rows.Single(r => r.Name == "web-nothing-else");
            Assert.False(nothingElse.Held, "web held although slot 8's container has no rule: " + nothingElse.Seen);
            Assert.Contains($"slot {WindowsCellSlots.Count} 80 NOT refused", nothingElse.Seen, StringComparison.Ordinal);
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// THE HELPER'S OWN ENTRY, AS WINDOWS STARTS IT: anything but its one argument is refused and places nothing; exactly that argument places
    /// the account's default home's sixteen rules, read back — and creates nothing in that home.
    /// </summary>
    [WindowsAdminFact]
    public void The_helpers_entry_refuses_anything_but_its_one_argument_and_places_the_default_homes_rules()
    {
        RequireElevated();
        var home = AccountHome.Default();
        var existed = Directory.Exists(home);
        var rules = WindowsCellEgress.RulesFor(home);
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            Assert.Equal(WindowsCellEgress.Refused, WindowsCellEgress.RunHelper([WindowsCellEgress.Verb, "--open-every-port"]));
            Assert.Equal(WindowsCellEgress.Refused, WindowsCellEgress.RunHelper([]));
            Assert.Equal(rules.Count, WindowsCellEgress.Missing(rules).Count);

            Assert.Equal(WindowsCellEgress.Done, WindowsCellEgress.RunHelper([WindowsCellEgress.Verb]));
            Assert.Empty(WindowsCellEgress.Missing(rules));
            Assert.Equal(WindowsCellEgress.Done, WindowsCellEgress.RunHelper([WindowsCellEgress.Verb]));
            if (!existed) Assert.False(Directory.Exists(home), $"the helper created {home}");
            _out.WriteLine($"default home {home} existed before: {existed}; rules placed and read back: {rules.Count}");
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// CARD 2 — A CELL'S PROCESS IS NEVER A GRANT'S PEER: the cell's shell is in the attempt's job (the stage's job is nested in it), and the
    /// grant's container still answers Outside for it, naming the AppContainer; the stage itself, in the same job and outside any container,
    /// is a Member (the control).
    /// </summary>
    [WindowsFact]
    public async Task The_grant_container_refuses_a_cell_process_though_it_is_in_the_attempts_job()
    {
        using var held = StartCell("findstr x");
        var (cell, cellStart) = await CellChild(held);
        var stageStart = WindowsProcessTable.StartOf(held.Process.Id)!.Value;
        Assert.NotNull(held.Container);

        var stage = held.Container!.Contains(new PeerFacts(held.Process.Id, stageStart, DateTimeOffset.UtcNow));
        Assert.True(stage.Is == Membership.Member, "the stage (control) is not a member: " + stage.Why);
        var verdict = held.Container.Contains(new PeerFacts(cell, cellStart, DateTimeOffset.UtcNow));
        _out.WriteLine($"cell process {cell}: {verdict.Is} — {verdict.Why}");
        Assert.True(verdict.Is == Membership.Outside, $"a cell's process was answered {verdict.Is}: {verdict.Why}");
        Assert.Contains("AppContainer", verdict.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// CARD 3 — THE ATTEMPT'S PROCESSES ARE ITS JOB'S: a reader of the same name started OUTSIDE the cell after it is never listed; the job
    /// lists the stage and the cell's reader, and the benchmark's cell members are the cell's alone (each an AppContainer's); after the cancel
    /// the cell's reader is gone and the outside one still runs.
    /// </summary>
    [WindowsFact]
    public async Task The_attempts_processes_are_its_jobs_and_never_a_process_outside()
    {
        Process? outside = null;
        try
        {
            var held = StartCell("findstr x");
            int cell;
            using (held)
            {
                (cell, _) = await CellChild(held);
                var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "findstr.exe"), "x")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
                outside = Process.Start(psi)!;

                var members = held.JobMembers();
                Assert.NotNull(members);
                _out.WriteLine($"job members: {string.Join(", ", members!)}; stage {held.Process.Id}, cell {cell}, outside {outside.Id}");
                Assert.Contains(held.Process.Id, members);
                Assert.Contains(cell, members);
                Assert.DoesNotContain(outside.Id, members);

                var cellMembers = WindowsCellBenchmark.CellMembers(held)!;
                Assert.Contains(cellMembers, m => m.Pid == cell && m.Name == "findstr");
                Assert.DoesNotContain(cellMembers, m => m.Pid == outside.Id || m.Pid == held.Process.Id);
                Assert.All(cellMembers, m => Assert.StartsWith("S-1-15-2-", m.Sid, StringComparison.Ordinal));
            }
            Assert.Null(held.JobMembers());
            Assert.True(await Gone(cell), $"the cell's reader {cell} outlived the cancel");
            Assert.False(outside.HasExited, "the reader outside the cell was ended with the cell");
        }
        finally
        {
            try { outside?.Kill(); } catch (Exception) { /* gone */ }
            outside?.Dispose();
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------

    static void RequireElevated() =>
        Assert.True(WindowsCellEgress.Elevated(), $"missing prerequisite: an elevated session ({WindowsAdminFactAttribute.Variable}=1 says this runner is CI's own)");

    /// <summary>A web policy over a fresh workspace, and the benchmark's per-slot web rows for it.</summary>
    async Task<IReadOnlyList<BenchRow>> WebRows()
    {
        var ws = Directory.CreateDirectory(Path.Combine(_root, "web-ws")).FullName;
        var p = RouteA.WindowsPolicy(ws, Cmd, Path.GetDirectoryName(Cmd)!, Directory.CreateDirectory(Path.Combine(_root, "codex")).FullName,
            Directory.CreateDirectory(Path.Combine(_root, "home")).FullName, CellEgress.Web);
        return await WindowsCellBenchmark.WebRowsAsync(p, ws, Launcher(), CellPlatform.Read());
    }

    ContainedProcess StartCell(string script)
    {
        var ws = Directory.CreateDirectory(Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("n")[..6])).FullName;
        var policy = RouteA.WindowsPolicy(ws, Cmd, Path.GetDirectoryName(Cmd)!, Directory.CreateDirectory(Path.Combine(_root, "c-" + Path.GetFileName(ws))).FullName,
            Directory.CreateDirectory(Path.Combine(_root, "h-" + Path.GetFileName(ws))).FullName, CellEgress.None);
        var psi = new ProcessStartInfo(Cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/d", "/c", script }) psi.ArgumentList.Add(a);
        var held = ProcessContainment.Start(psi, policy, Launcher());
        _out.WriteLine($"started: {held.Mechanism}");
        return held;
    }

    /// <summary>
    /// The cell's reader, found WITHOUT the job's list (so card 2's row stands on its own): the stage's descendants as the toolhelp snapshot has
    /// them, the one named findstr whose token is an AppContainer's.
    /// </summary>
    static async Task<(int Pid, ulong Start)> CellChild(ContainedProcess held)
    {
        var stageStart = WindowsProcessTable.StartOf(held.Process.Id) ?? 0;
        for (var i = 0; i < 200; i++)
        {
            foreach (var e in WindowsProcessTable.Tree(held.Process.Id, stageStart) ?? [])
            {
                if (e.Pid == held.Process.Id || WindowsToken.AppContainerSidOf(e.Pid) is not { Length: > 0 }) continue;
                try
                {
                    using var p = Process.GetProcessById(e.Pid);
                    if (p.ProcessName == "findstr" && WindowsProcessTable.StartOf(e.Pid) is { } start) return (e.Pid, start);
                }
                catch (ArgumentException) { /* gone */ }
            }
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("the cell's reader never appeared under the stage");
    }

    static async Task<bool> Gone(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return true; }
            catch (ArgumentException) { return true; }
            await Task.Delay(50);
        }
        return false;
    }
}
