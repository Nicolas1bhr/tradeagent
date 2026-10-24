using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S REPAIR OF A REFUSED POSITION'S FOLDER (<c>U-repair-position-home</c>). A cell writes its whole home, so it can leave a real
/// folder where the app's canon belongs; the start that cannot rebuild the home refuses the position, and before this unit nothing in the
/// app could clear it — the only fix was deleting the cell's folder in a terminal. The repair moves the refused folder aside WHOLE, by one
/// rename, and builds the home afresh: nothing of the cell's is deleted, and a link inside it is never followed.
/// </summary>
public class RepairHomeTests
{
    static readonly DateTimeOffset At = new(2026, 10, 24, 18, 30, 5, TimeSpan.Zero);

    static WorkspaceContext Ctx() => new("Simulator (built in)", ConnectorIsPaper: true, "SIM-001", TradingMode.PAPER,
        ExecutionAvailable: true, null, new RiskPolicy { MaxDailyLoss = 500m, InstrumentAllowlist = ["ES"] });

    /// <summary>A built tree whose Research home has a planted folder where <c>AGENTS.md</c> belongs, refused by the start after it.</summary>
    static (string Root, string Home, AppFileManifest Files, string CanonText) Refused()
    {
        var root = Path.Combine(TestEnv.Home, "repair-home", Guid.NewGuid().ToString("n"));
        var files = new AppFileManifest(Path.Combine(root, "..", $"files-{Guid.NewGuid():n}.tsv"));
        var home = WorkspaceBuilder.BuildAll(Ctx(), root, files)[CouncilRoles.Research];
        var text = File.ReadAllText(Path.Combine(home, Canon.CliFile));
        var planted = Path.Combine(home, Canon.CliFile);
        File.Delete(planted);
        Directory.CreateDirectory(planted);                                   // the cell's folder where the app's canon belongs
        File.WriteAllBytes(Path.Combine(planted, "planted.bin"), [0, 1, 2, 250, 251, 252]);
        WorkspaceBuilder.BuildAll(Ctx(), root, files);                        // the start that cannot rebuild it
        return (root, home, files, text);
    }

    /// <summary>
    /// THE REPAIR MOVES THE REFUSED FOLDER ASIDE WHOLE AND REBUILDS THE HOME (item 1). After it the position is no longer refused, the home
    /// holds the app's canon afresh, the kept folder holds the planted folder byte for byte, and a link the cell made inside the old home
    /// still points where it did, its target untouched. On Windows, where no cell runs, it refuses in words and changes nothing.
    /// </summary>
    [Fact]
    public void A_repair_moves_the_refused_folder_aside_whole_and_rebuilds_the_home()
    {
        var (root, home, files, canon) = Refused();
        Assert.NotNull(WorkspaceBuilder.RefusalFor(home));
        var target = Path.Combine(root, "..", $"outside-{Guid.NewGuid():n}.txt");
        File.WriteAllText(target, "the cell's link target, outside its home");
        var link = Path.Combine(home, "scratch", "link-out");
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(link, target);

        var result = WorkspaceBuilder.RepairHome(Ctx(), CouncilRoles.Research, root, files, () => At);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(result.Repaired);
            Assert.Contains("Windows", result.Words, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(home, Canon.CliFile)), "the refused folder was changed on Windows");
            Assert.NotNull(WorkspaceBuilder.RefusalFor(home));
            return;
        }

        var aside = Path.Combine(root, WorkspaceBuilder.RefusedDir, $"research-{At.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
        Assert.True(result.Repaired, result.Words);
        Assert.Equal(aside, result.Aside);
        Assert.Null(WorkspaceBuilder.RefusalFor(home));
        Assert.True(File.Exists(Path.Combine(home, Canon.CliFile)), "the home holds no canon file");
        Assert.Equal(canon, File.ReadAllText(Path.Combine(home, Canon.CliFile)));
        Assert.Equal([0, 1, 2, 250, 251, 252], File.ReadAllBytes(Path.Combine(aside, Canon.CliFile, "planted.bin")));
        Assert.Equal(target, new FileInfo(Path.Combine(aside, "scratch", "link-out")).LinkTarget);
        Assert.Equal("the cell's link target, outside its home", File.ReadAllText(target));
        Assert.Contains(aside, result.Words, StringComparison.Ordinal);
    }

    /// <summary>A REPAIR OF A POSITION THE LAST START REBUILT CHANGES NOTHING (item 2): no folder is moved and no kept folder is made.</summary>
    [Fact]
    public void A_repair_of_an_unrefused_position_changes_nothing()
    {
        var root = Path.Combine(TestEnv.Home, "repair-home", Guid.NewGuid().ToString("n"));
        var files = new AppFileManifest(Path.Combine(root, "..", $"files-{Guid.NewGuid():n}.tsv"));
        var home = WorkspaceBuilder.BuildAll(Ctx(), root, files)[CouncilRoles.Research];
        var mark = Path.Combine(home, "scratch", "mine.txt");
        File.WriteAllText(mark, "the role's own file");

        var result = WorkspaceBuilder.RepairHome(Ctx(), CouncilRoles.Research, root, files, () => At);

        Assert.False(result.Repaired);
        Assert.Null(result.Aside);
        Assert.Contains("nothing to repair", result.Words, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, WorkspaceBuilder.RefusedDir)), "a kept-folder directory was made");
        Assert.Equal("the role's own file", File.ReadAllText(mark));
    }

    /// <summary>
    /// A KEPT FOLDER BY THE SAME NAME IS NEVER WRITTEN OVER (item 2): the repair refuses in words, the kept folder and the refused home are as
    /// they were, and the position stays refused.
    /// </summary>
    [Fact]
    public void A_kept_folder_by_the_same_name_refuses_the_repair_rather_than_being_overwritten()
    {
        var (root, home, files, _) = Refused();
        var aside = Path.Combine(root, WorkspaceBuilder.RefusedDir, $"research-{At.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
        Directory.CreateDirectory(aside);
        File.WriteAllText(Path.Combine(aside, "kept.txt"), "an earlier repair's folder");

        var result = WorkspaceBuilder.RepairHome(Ctx(), CouncilRoles.Research, root, files, () => At);

        Assert.False(result.Repaired);
        Assert.Contains("Nothing was changed", result.Words, StringComparison.Ordinal);
        Assert.Equal(["kept.txt"], Directory.GetFileSystemEntries(aside).Select(Path.GetFileName));
        Assert.True(Directory.Exists(Path.Combine(home, Canon.CliFile)), "the refused home was changed");
        Assert.NotNull(WorkspaceBuilder.RefusalFor(home));
    }
}
