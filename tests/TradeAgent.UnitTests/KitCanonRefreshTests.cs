using System.Text;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// AN INSTALL OR REMOVAL OF THE OWNER'S KIT REACHES EVERY SEAT'S CANON AT ONCE (<c>U-kit-socket-2</c> item 2): every home a start built is
/// rewritten — the kit's part only, byte for byte what the next start would write — and a turn in flight keeps the canon it started with,
/// taking the new one when it ends (<see cref="HomeCanons"/>). Every kit here is a fixture in a throwaway home.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class KitCanonRefreshTests(ITestOutputHelper log) : IDisposable
{
    readonly string _root = Path.Combine(TestEnv.Home, $"kit-canon-{Guid.NewGuid():n}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* swept with the test home */ }
    }

    /// <summary>A position beyond the legacy pair, built as an executor (a Member): its canon is the cell's.</summary>
    const string Executor = "pos-6b2e0c1f";

    static WorkspaceContext Ctx(string role) => new(
        "Simulator (built in)", ConnectorIsPaper: true, null, TradingMode.PAPER, true, null, new RiskPolicy(),
        ConnectorIsBuiltInSimulator: true, Role: role, Kind: role == Executor ? TradeAgent.Core.Db.OrgPositionKind.Member : null);

    Kit.View Installed(string name, string tool)
    {
        var src = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("n")[..6]);
        Directory.CreateDirectory(Path.Combine(src, Kit.ToolsDir));
        File.WriteAllText(Path.Combine(src, Kit.IndexFile),
            $"{{\"kit\": 1, \"name\": \"{name}\", \"skills\": [], \"tools\": [{{\"name\": \"{tool}\", \"description\": \"the fixture {tool}\"}}]}}");
        File.WriteAllText(Path.Combine(src, Kit.ToolsDir, tool), "#!/bin/sh\necho fixture\n");
        var home = Path.Combine(_root, "app-" + Guid.NewGuid().ToString("n")[..6]);
        Directory.CreateDirectory(Path.Combine(home, "state"));
        var (view, refusal) = Kit.Install(src, home);
        return view ?? throw new InvalidOperationException(refusal);
    }

    /// <summary>Every canon file of <paramref name="role"/>'s home, with the text a start would write there for <paramref name="kit"/>.</summary>
    static IEnumerable<(string RelPath, string Text)> Expected(string role, Kit.View? kit) =>
        WorkspaceBuilder.RuntimesOf(role, Ctx(role).Kind).Select(runtime =>
            (Canon.FileFor(runtime), Canon.Render(Ctx(role) with { Kit = kit }, AgentReach.For(role, runtime, null)).Text));

    static string Read(string home, string relPath) => File.ReadAllText(Path.Combine(home, relPath.Replace('/', Path.DirectorySeparatorChar)));

    void SaysKit(string home, string role, Kit.View? kit, AppFileManifest files, string when)
    {
        foreach (var (relPath, text) in Expected(role, kit))
        {
            var key = AppFileManifest.Key(CouncilRoles.HomeDir(role), relPath);
            Assert.True(Read(home, relPath) == text, $"{when}: {role}'s {relPath} is not what a start would write for {(kit is null ? "no kit" : kit.Index.Name)}");
            Assert.True(files.Kept(key, home) == text, $"{when}: {role}'s kept {relPath} is not the new text");
            Assert.True(files.Wrote(key, Sha256Hex.Of(text)), $"{when}: {role}'s {relPath} is not recorded as the app's");
        }
    }

    /// <summary>
    /// THE CHAIR (a CLI), RESEARCH (a CLI and the harness) AND AN EXECUTOR (the cell's canon and the harness's), built with no kit: an
    /// install rewrites every canon file of all three at once to exactly what the next start would write with the kit, kept and recorded
    /// as the app's; a second install replaces the first's part; a removal takes the part out. RED at base (no refresh between starts: the
    /// canon changed only at the next one) → "Expected: Tuple (3, 0, 0) Actual: Tuple (0, 0, 0)".
    /// </summary>
    [Fact]
    public void An_install_or_removal_reaches_every_built_homes_canon_at_once()
    {
        var files = new AppFileManifest(Path.Combine(_root, "app-files.tsv"));
        var ws = Path.Combine(_root, "ws");
        var roles = new[] { CouncilRoles.Operations, CouncilRoles.Research, Executor };
        var homes = roles.ToDictionary(r => r, r => WorkspaceBuilder.Build(Ctx(r), ws, files));
        foreach (var r in roles) SaysKit(homes[r], r, null, files, "after the start");

        var a = Installed("Kit A", "alpha-tool");
        var reach = HomeCanons.Refresh(a, files);
        log.WriteLine(reach.Words);
        Assert.Equal((3, 0, 0), (reach.Now, reach.AfterTheirTurn, reach.Failed.Count));
        foreach (var r in roles) SaysKit(homes[r], r, a, files, "after the install");
        Assert.Contains(a.Hash[..12], Read(homes[Executor], Canon.CliFile), StringComparison.Ordinal);

        var b = Installed("Kit B", "beta-tool");
        HomeCanons.Refresh(b, files);
        foreach (var r in roles) SaysKit(homes[r], r, b, files, "after a second install");
        Assert.DoesNotContain("alpha-tool", Read(homes[Executor], Canon.CliFile), StringComparison.Ordinal);

        HomeCanons.Refresh(null, files);
        foreach (var r in roles) SaysKit(homes[r], r, null, files, "after the removal");
        Assert.DoesNotContain("head-start kit", Read(homes[CouncilRoles.Research], Canon.HarnessFile), StringComparison.Ordinal);
        // NOTHING LEFT BESIDE IT: each replacement was moved over its name.
        Assert.Empty(Directory.GetFiles(Path.Combine(homes[CouncilRoles.Operations], ".tradeagent"), "canon-*.tmp"));
    }

    /// <summary>
    /// A TURN IN FLIGHT KEEPS THE CANON IT STARTED WITH: a real child process launched in the chair's home (it waits three seconds before
    /// it answers) holds the home; an install meanwhile leaves its AGENTS.md, its kept text and its record as they were and is said to wait;
    /// when the turn ends the home has the kit's canon. MUTANT (the session does not hold its home, AgentSession.RunTurnAsync) → "the
    /// turn in flight's canon changed under it".
    /// </summary>
    [Fact]
    public async Task A_turn_in_flight_keeps_the_canon_it_started_with_and_takes_the_new_one_when_it_ends()
    {
        var files = new AppFileManifest(Path.Combine(_root, "app-files-turn.tsv"));
        var home = WorkspaceBuilder.Build(Ctx(CouncilRoles.Operations), Path.Combine(_root, "ws-turn"), files);
        var before = Read(home, Canon.CliFile);
        var marker = Path.Combine(_root, "started");
        var session = AgentRuntimeProbe.SessionOverStream("""{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}""",
            sleepSeconds: 3, marker: marker, workspace: home, appFiles: files);
        var turn = session.SendAsync("hello");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(marker) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(File.Exists(marker), "the turn's process never started");
        Assert.False(turn.IsCompleted, "the turn ended before the kit changed");

        var reach = HomeCanons.Refresh(Installed("Kit A", "alpha-tool"), files);
        log.WriteLine(reach.Words);
        var key = AppFileManifest.Key(CouncilRoles.HomeDir(CouncilRoles.Operations), Canon.CliFile);
        Assert.True(Read(home, Canon.CliFile) == before, "the turn in flight's canon changed under it");
        Assert.True(files.Kept(key, home) == before, "the turn in flight's kept canon changed under it");
        Assert.Equal((0, 1), (reach.Now, reach.AfterTheirTurn));

        await turn.WaitAsync(TimeSpan.FromSeconds(30));
        var after = Read(home, Canon.CliFile);
        Assert.Contains("Kit A", after, StringComparison.Ordinal);
        Assert.Equal(after, files.Kept(key, home));
        Assert.True(files.Wrote(key, Sha256Hex.Of(Encoding.UTF8.GetBytes(after))), "the new canon is not recorded as the app's");
    }
}
