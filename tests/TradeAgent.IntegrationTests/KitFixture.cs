using TradeAgent.Core;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A FIXTURE KIT FOR THE KIT SOCKET'S ROWS (<c>U-kit-socket</c>): one skill and one tool, made in a throwaway folder and installed by the
/// product's own <see cref="Kit.Install"/> into a throwaway app home. Test data — the product ships no skill and no tool.
/// </summary>
static class KitFixture
{
    public const string Skill = "kit-fixture-skill";

    /// <summary>The tool's name as a cell runs it: on Windows the PATHEXT finds its <c>.cmd</c>.</summary>
    public const string ToolName = "kit-fixture-tool";

    /// <summary>The tool's file name, as the index names it: a shell script, or on Windows a batch file.</summary>
    public static string Tool => OperatingSystem.IsWindows() ? ToolName + ".cmd" : ToolName;

    public const string ToolSays = "kit-fixture-tool-ran";

    /// <summary>
    /// ON WINDOWS A SECOND TOOL, A PROGRAM: a copy of the system's own <c>cmd.exe</c> under this name, so a row shows a kit's program run
    /// by name from the kit's <c>tools/</c> apart from a batch file, which the shell must also read.
    /// </summary>
    public const string ProgramName = "kit-fixture-program";

    public static Kit.View Installed(string root)
    {
        var src = Path.Combine(root, "kit-src");
        Directory.CreateDirectory(Path.Combine(src, Kit.SkillsDir, Skill));
        Directory.CreateDirectory(Path.Combine(src, Kit.ToolsDir));
        File.WriteAllText(Path.Combine(src, Kit.IndexFile),
            $"{{\"kit\": 1, \"name\": \"Fixture kit\", \"skills\": [{{\"name\": \"{Skill}\", \"description\": \"the fixture {Skill}\"}}], " +
            $"\"tools\": [{{\"name\": \"{Tool}\", \"description\": \"the fixture {Tool}\"}}" +
            (OperatingSystem.IsWindows() ? $", {{\"name\": \"{ProgramName}.exe\", \"description\": \"the fixture program\"}}" : "") + "]}");
        File.WriteAllText(Path.Combine(src, Kit.SkillsDir, Skill, Kit.SkillFile),
            $"---\nname: {Skill}\ndescription: the fixture {Skill}, which a test lists\n---\nA fixture skill.\n");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(src, Kit.ToolsDir, Tool), $"@echo {ToolSays}\r\n");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(src, Kit.ToolsDir, ProgramName + ".exe"));
        }
        else File.WriteAllText(Path.Combine(src, Kit.ToolsDir, Tool), $"#!/bin/sh\necho {ToolSays}\n");
        var home = Path.Combine(root, "app-home");
        Directory.CreateDirectory(Path.Combine(home, "state"));
        var (view, refusal) = Kit.Install(src, home);
        return view ?? throw new InvalidOperationException("the fixture kit did not install: " + refusal);
    }
}
