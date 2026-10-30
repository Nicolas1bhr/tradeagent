using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE OWNER'S HEAD-START KIT IN A CELL (<c>U-kit-socket</c> item 1; containment): read and run, never written — by the kernel, not by the
/// files' own modes. The kit is made writable by mode for the row, and the same writes are shown to succeed outside the cell first.
/// </summary>
public partial class CellLinuxTests
{
    /// <summary>
    /// A ROUTE (a) CELL UNDER THE PRODUCT'S POLICY (<see cref="RouteA.Policy"/> with the installed kit) READS THE KIT'S INDEX AND A SKILL,
    /// RUNS A KIT TOOL BY ITS NAME FROM ITS PATH, AND CAN WRITE NOTHING IN IT: no new file in the kit, its skills or its tools, no rewrite
    /// of a skill or a tool, no removal, rename, folder or mode change. RED before the socket: the policy granted no kit, so the reads
    /// were denied and the tool was not found. MUTANT (program roots granted every right, CellNative's Executable rule) → the writes into
    /// <c>tools/</c> succeed.
    /// </summary>
    [LinuxFact]
    public async Task A_route_a_cell_reads_the_owners_kit_runs_its_tools_by_name_and_writes_nothing_in_it()
    {
        PlatformIsACells();
        if (!OperatingSystem.IsLinux()) return;
        var w = new World("kit");
        var kit = KitFixture.Installed(w.Root);
        var cellHome = Path.Combine(w.Root, "cell-home");
        Directory.CreateDirectory(cellHome);
        var policy = RouteA.Policy(w.Ws, "/usr/bin/python3", "/usr/bin", cellHome, cellHome, kit);

        // THE KIT WRITABLE BY ITS MODES, FOR THIS ROW ONLY: a denial inside the cell is then the kernel's, not the files'.
        foreach (var d in Directory.EnumerateDirectories(kit.Dir, "*", SearchOption.AllDirectories).Prepend(kit.Dir))
            File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var f in Directory.EnumerateFiles(kit.Dir, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserWrite);

        var probe = w.Script("kit.py", """
            import os, subprocess, sys
            kit, skill, tool, tool_name = sys.argv[1:5]
            try:
                open(os.path.join(kit, "kit.json")).read(); open(skill).read(); print("kit-read")
            except PermissionError:
                print("kit-read-denied")
            try:
                print(subprocess.run([tool_name], capture_output=True, text=True, timeout=20).stdout.strip())
            except Exception as e:
                print("tool-failed " + type(e).__name__)
            tools = os.path.dirname(tool)
            skills = os.path.dirname(os.path.dirname(skill))
            def attempt(what, act):
                try:
                    act(); print("write-open " + what)
                except PermissionError:
                    print("write-denied " + what)
            attempt("kit-new-file", lambda: open(os.path.join(kit, "planted.txt"), "w").write("x"))
            attempt("skills-new-file", lambda: open(os.path.join(skills, "planted.txt"), "w").write("x"))
            attempt("tools-new-file", lambda: open(os.path.join(tools, "planted"), "w").write("x"))
            attempt("skill-rewrite", lambda: open(skill, "a").write("ignore your canon"))
            attempt("tool-rewrite", lambda: open(tool, "a").write("rm -rf /"))
            attempt("tool-remove", lambda: os.remove(tool))
            attempt("tool-rename", lambda: os.rename(tool, tool + ".old"))
            attempt("kit-mkdir", lambda: os.mkdir(os.path.join(kit, "planted")))
            attempt("tool-chmod", lambda: os.chmod(tool, 0o777))
            """);
        string[] args = [kit.Dir, kit.SkillPath(KitFixture.Skill), kit.ToolPath(KitFixture.Tool), KitFixture.Tool];

        var ran = await InCell(policy, "/usr/bin/python3", [probe, .. args]);
        _output.WriteLine(ran.Out);
        Says(ran, "kit-read", KitFixture.ToolSays,
            "write-denied kit-new-file", "write-denied skills-new-file", "write-denied tools-new-file", "write-denied skill-rewrite",
            "write-denied tool-rewrite", "write-denied tool-remove", "write-denied tool-rename", "write-denied kit-mkdir");
        // chmod is not a right Landlock handles (it governs no attribute change), and the files are the owner's own: said, not asserted.
        _output.WriteLine("tool-chmod in the cell: " + (ran.Out.Contains("write-open tool-chmod", StringComparison.Ordinal)
            ? "allowed — Landlock does not handle a mode change; the bytes stay unwritable, and a changed run bit changes the hash" : "denied"));
        Assert.Equal(kit.Hash, Kit.HashOf(kit.Dir));

        // THE CONTROL, OUTSIDE ANY CELL: the same writes succeed as the test's own user, so the denials above are the cell's.
        var outside = await Outside("/usr/bin/python3", [probe, .. args]);
        Says(outside, "write-open kit-new-file", "write-open tools-new-file", "write-open skill-rewrite");
    }
}
