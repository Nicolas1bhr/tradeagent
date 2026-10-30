using TradeAgent.Core;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A FIXTURE KIT FOR THE KIT SOCKET'S ROWS (<c>U-kit-socket</c>): one skill and one tool, made in a throwaway folder and installed by the
/// product's own <see cref="Kit.Install"/> into a throwaway app home. Test data — the product ships no skill and no tool.
/// </summary>
static class KitFixture
{
    public const string Skill = "kit-fixture-skill";
    public const string Tool = "kit-fixture-tool";
    public const string ToolSays = "kit-fixture-tool-ran";

    public static Kit.View Installed(string root)
    {
        var src = Path.Combine(root, "kit-src");
        Directory.CreateDirectory(Path.Combine(src, Kit.SkillsDir, Skill));
        Directory.CreateDirectory(Path.Combine(src, Kit.ToolsDir));
        File.WriteAllText(Path.Combine(src, Kit.IndexFile),
            $"{{\"kit\": 1, \"name\": \"Fixture kit\", \"skills\": [{{\"name\": \"{Skill}\", \"description\": \"the fixture {Skill}\"}}], " +
            $"\"tools\": [{{\"name\": \"{Tool}\", \"description\": \"the fixture {Tool}\"}}]}}");
        File.WriteAllText(Path.Combine(src, Kit.SkillsDir, Skill, Kit.SkillFile),
            $"---\nname: {Skill}\ndescription: the fixture {Skill}, which a test lists\n---\nA fixture skill.\n");
        File.WriteAllText(Path.Combine(src, Kit.ToolsDir, Tool), $"#!/bin/sh\necho {ToolSays}\n");
        var home = Path.Combine(root, "app-home");
        Directory.CreateDirectory(Path.Combine(home, "state"));
        var (view, refusal) = Kit.Install(src, home);
        return view ?? throw new InvalidOperationException("the fixture kit did not install: " + refusal);
    }
}
