using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// WHERE THE PINNED CODEX FINDS THE OWNER'S SKILLS, ON THE REAL BINARY (<c>U-kit-socket</c> item 1): the cell class's own argv, gpt-6-luna
/// on the app's catalog, the probe's loopback mock and stub door, a throwaway codex home holding exactly what a launch lays in a harness's
/// home — the link <c>skills</c> to an installed fixture kit's <c>skills/</c> (<see cref="RouteA.Make"/>) — and nothing else. The first
/// model request must list the fixture skill at the KIT's own path (the path a cell reads, read-only) and no bundled skill; the broker
/// class, whose argv keeps <c>skills.include_instructions=false</c>, lists none from the same home. No real model, no key, no account.
/// </summary>
[Trait("Category", "Cell")]
public class KitSkillsProbeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [PinnedCodexFact]
    public async Task The_cell_classs_codex_lists_the_kits_skill_from_the_kits_own_path_and_no_bundled_skill()
    {
        var exe = CliCodeModeHostTests.PinnedCodex();
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var sha = codex.Install.ExecutableSha256!;
        var kit = KitFixture.Installed(Path.Combine(TestEnv.Home, "kit-probe-" + Guid.NewGuid().ToString("n")[..8]));
        var real = CellNative.RealPath(kit.Skills!) ?? kit.Skills!;

        async Task<string> FirstRequest(string seatClass)
        {
            string? body = null;
            var probe = new CodexSurfaceProbe
            {
                PrepareHome = home => File.CreateSymbolicLink(Path.Combine(home, RouteA.SkillsLink), kit.Skills!),
                OnBody = b => body = b
            };
            var offered = await probe.OfferedAsync(new SurfaceAsk(codex, exe, sha, seatClass, BrokerSeat.Model), default);
            output.WriteLine($"{seatClass} :: offered {string.Join(" ", offered)}; the request {(body?.Contains(KitFixture.Skill, StringComparison.Ordinal) == true ? "names" : "does not name")} the fixture skill");
            return body ?? throw new InvalidOperationException("the dry run returned no request body");
        }

        var cell = await FirstRequest(SeatPlan.CellClass);
        Assert.True(cell.Contains(KitFixture.Skill, StringComparison.Ordinal), "the cell class's first request does not list the kit's skill");
        Assert.True(cell.Contains(real, StringComparison.Ordinal),
            $"the cell class's first request does not name the kit's own skills folder {real}; around the skill it says: " +
            Around(cell, KitFixture.Skill));
        foreach (var bundled in new[] { "skill-creator", "skill-installer", "openai-docs", "imagegen" })
            Assert.False(cell.Contains(bundled, StringComparison.Ordinal), $"the cell class's first request lists the bundled skill {bundled}");

        var broker = await FirstRequest(SeatPlan.BrokerClass);
        Assert.False(broker.Contains(KitFixture.Skill, StringComparison.Ordinal), "the broker class, which lists no skill, listed the kit's");
    }

    static string Around(string text, string what)
    {
        var i = text.IndexOf(what, StringComparison.Ordinal);
        return i < 0 ? "(absent)" : text[Math.Max(0, i - 400)..Math.Min(text.Length, i + 400)];
    }
}

/// <summary>
/// THE SAME ON WINDOWS, ON THE REAL PINNED BINARY (<c>U-kit-socket-2</c>): the harness's codex home holds the skill root the product lays
/// there (<see cref="RouteA.LaySkills"/>) — a JUNCTION, never a symlink — and the cell class's first request lists the fixture skill at the
/// KIT's own path (the path a Windows cell is granted, read and execute), never at the junction's path inside the codex home; the broker
/// class lists none. CI's first windows shard names the pinned windows codex in <c>TA_CELL_CODEX</c>.
/// </summary>
[Trait("Category", "Cell"), Trait("CellScope", "Windows")]
public class KitSkillsProbeWindowsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [WindowsFact]
    public async Task The_cell_classs_codex_lists_the_kits_skill_through_a_junction_at_the_kits_own_path()
    {
        var exe = CliCodeModeHostTests.PinnedCodex();
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var sha = codex.Install.ExecutableSha256!;
        var kit = KitFixture.Installed(Path.Combine(TestEnv.Home, "kit-probe-" + Guid.NewGuid().ToString("n")[..8]));
        string Json(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
        var atKit = Json(Path.Combine("app-home", Kit.Dir(""), Kit.SkillsDir, KitFixture.Skill));
        var atLink = Json(Path.Combine("codex", RouteA.SkillsLink, KitFixture.Skill));

        async Task<string> FirstRequest(string seatClass)
        {
            string? body = null;
            var probe = new CodexSurfaceProbe
            {
                PrepareHome = home =>
                {
                    RouteA.LaySkills(home, kit.Skills!);
                    var laid = new DirectoryInfo(Path.Combine(home, RouteA.SkillsLink));
                    Assert.True((laid.Attributes & FileAttributes.ReparsePoint) != 0 && laid.LinkTarget is not null, "the skill root laid is not a junction");
                },
                OnBody = b => body = b
            };
            var offered = await probe.OfferedAsync(new SurfaceAsk(codex, exe, sha, seatClass, BrokerSeat.Model), default);
            output.WriteLine($"{seatClass} :: offered {string.Join(" ", offered)}; the request {(body?.Contains(KitFixture.Skill, StringComparison.Ordinal) == true ? "names" : "does not name")} the fixture skill");
            return body ?? throw new InvalidOperationException("the dry run returned no request body");
        }

        var cell = await FirstRequest(SeatPlan.CellClass);
        var i = cell.IndexOf(KitFixture.Skill, StringComparison.Ordinal);
        output.WriteLine("around the skill: " + (i < 0 ? "(absent)" : cell[Math.Max(0, i - 300)..Math.Min(cell.Length, i + 300)]));
        Assert.True(i >= 0, "the cell class's first request does not list the kit's skill");
        Assert.True(cell.Contains(atKit, StringComparison.OrdinalIgnoreCase), $"the cell class's first request does not name the kit's own skills folder (…{atKit})");
        Assert.False(cell.Contains(atLink, StringComparison.OrdinalIgnoreCase), $"the cell class's first request names the junction's path (…{atLink}), which no cell is granted");
        var broker = await FirstRequest(SeatPlan.BrokerClass);
        Assert.False(broker.Contains(KitFixture.Skill, StringComparison.Ordinal), "the broker class, which lists no skill, listed the kit's");
    }
}
