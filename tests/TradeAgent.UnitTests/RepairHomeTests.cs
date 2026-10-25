using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
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

    /// <summary>
    /// A REPAIR NEVER MOVES A FOLDER THROUGH A LINK (the Codex review's [P1]): a <c>workspace/.refused</c> that is already a link — into the
    /// owner's scanned inbox, or out of the workspace — refuses the repair in words naming it, and the refused home, the link and where it
    /// points are byte for byte as they were. Before the fix the move followed the link and the cell's folder landed in the inbox.
    /// </summary>
    [Theory]
    [InlineData("inbox")]
    [InlineData("outside")]
    public void A_refused_dir_that_is_a_link_refuses_the_repair_and_changes_nothing(string into)
    {
        if (OperatingSystem.IsWindows()) return;   // no cell runs there and the repair refuses first (the first test asserts it)
        var (root, home, files, _) = Refused();
        var target = into == "inbox"
            ? Path.Combine(root, MaterialScanner.InboxDir)
            : Path.Combine(root, "..", $"outside-{Guid.NewGuid():n}");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "owner.txt"), "the owner's own document");
        var refused = Path.Combine(root, WorkspaceBuilder.RefusedDir);
        Directory.CreateSymbolicLink(refused, target);
        var (homeBefore, targetBefore) = (Tree(home), Tree(target));

        var result = WorkspaceBuilder.RepairHome(Ctx(), CouncilRoles.Research, root, files, () => At);

        Assert.False(result.Repaired, result.Words);
        Assert.Null(result.Aside);
        Assert.Contains(refused, result.Words, StringComparison.Ordinal);
        Assert.Contains("link", result.Words, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", result.Words, StringComparison.Ordinal);
        Assert.Equal(homeBefore, Tree(home));
        Assert.Equal(targetBefore, Tree(target));
        Assert.Equal(target, new FileInfo(refused).LinkTarget);
        Assert.NotNull(WorkspaceBuilder.RefusalFor(home));
    }

    /// <summary>
    /// THE HOME ITSELF, OR THE WORKSPACE, AS A LINK REFUSES THE REPAIR TOO (the fixer's cover of the same finding): nothing on this path
    /// refused either before, so a repair renamed a linked home's link or moved a home through a linked workspace. Now each refuses in words
    /// naming the link, and the link and what it points at are as they were.
    /// </summary>
    [Theory]
    [InlineData("home")]
    [InlineData("workspace")]
    public void A_home_or_workspace_that_is_a_link_refuses_the_repair_and_changes_nothing(string which)
    {
        if (OperatingSystem.IsWindows()) return;   // no cell runs there and the repair refuses first (the first test asserts it)
        var (root, home, files, _) = Refused();
        string ws = root, link, real;
        if (which == "home")
        {
            real = Path.Combine(root, "..", $"elsewhere-{Guid.NewGuid():n}");
            Directory.Move(home, real);
            Directory.CreateSymbolicLink(home, real);
            link = home;
        }
        else
        {
            real = root;
            ws = link = root + "-link";
            Directory.CreateSymbolicLink(link, real);
            WorkspaceBuilder.BuildAll(Ctx(), ws, files);                      // the start, through the linked workspace, that cannot rebuild it
        }
        var linkedHome = WorkspaceBuilder.HomeOf(ws, CouncilRoles.Research);
        Assert.NotNull(WorkspaceBuilder.RefusalFor(linkedHome));
        var before = Tree(real);

        var result = WorkspaceBuilder.RepairHome(Ctx(), CouncilRoles.Research, ws, files, () => At);

        Assert.False(result.Repaired, result.Words);
        Assert.Contains(link, result.Words, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", result.Words, StringComparison.Ordinal);
        Assert.Equal(before, Tree(real));
        Assert.Equal(real, new FileInfo(link).LinkTarget);
        Assert.NotNull(WorkspaceBuilder.RefusalFor(linkedHome));
    }

    /// <summary>Every entry under <paramref name="dir"/>, never following a link: a link by its target, a file by its bytes.</summary>
    static List<string> Tree(string dir)
    {
        var all = new List<string>();
        void Walk(string at)
        {
            foreach (var e in Directory.GetFileSystemEntries(at).Order(StringComparer.Ordinal))
            {
                var rel = Path.GetRelativePath(dir, e);
                var info = new FileInfo(e);
                if (info.LinkTarget is { } to) all.Add($"{rel} -> {to}");
                else if (Directory.Exists(e)) { all.Add($"{rel}/"); Walk(e); }
                else all.Add($"{rel} = {Convert.ToHexString(File.ReadAllBytes(e))}");
            }
        }
        Walk(dir);
        return all;
    }

    /// <summary>A position beyond the legacy pair in the app's own tree whose folder the last start could not rebuild, by a fresh id.</summary>
    static (string Pos, string Home) RefusedInTheAppsTree(AppHost host)
    {
        var pos = $"pos-rh{Guid.NewGuid():n}"[..14];
        var files = new AppFileManifest(Path.Combine(TestEnv.Home, $"files-{Guid.NewGuid():n}.tsv"));
        var home = WorkspaceBuilder.BuildAll(host.WorkspaceContext(), null, files, positions: [pos])[pos];
        var planted = Path.Combine(home, Canon.CliFile);
        File.Delete(planted);
        Directory.CreateDirectory(planted);
        WorkspaceBuilder.BuildAll(host.WorkspaceContext(), null, files, positions: [pos]);
        return (pos, home);
    }

    /// <summary>
    /// A FOLDER IS NEVER MOVED FROM UNDER A RUNNING SEAT (item 2): while the position has an open launch the host's repair refuses in words,
    /// and the folder and the refusal are as they were.
    /// </summary>
    [Fact]
    public async Task A_repair_while_the_position_has_an_open_launch_is_refused_in_words()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var (pos, home) = RefusedInTheAppsTree(host);
            var open = host.Meter!.Mint(pos);

            var words = host.RepairHome(pos);

            Assert.Contains("launch of it is open", words, StringComparison.Ordinal);
            Assert.Contains(open, words, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(home, Canon.CliFile)), "the folder was moved from under an open launch");
            Assert.NotNull(host.HomeRefusalFor(pos));
            host.Meter.Forget(open);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }
    /// <summary>
    /// THE HOST'S REPAIR CLEARS THE POSITION'S HOME REFUSAL AND RECORDS ITSELF (item 3): after it the host reports no home refusal, the folder
    /// the session's launch gate asks about (<see cref="WorkspaceBuilder.RefusalFor"/> of <see cref="AppHost.HomeFor"/>) is not refused, and the
    /// act is one Activity line and one engineering-log line naming where the old folder is kept.
    /// </summary>
    [Fact]
    public async Task The_hosts_repair_clears_the_refusal_and_records_one_activity_line()
    {
        if (OperatingSystem.IsWindows()) return;                           // the kernel refuses on Windows; its own test says so in words
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var (pos, _) = RefusedInTheAppsTree(host);
            Assert.NotNull(host.HomeRefusalFor(pos));

            var words = host.RepairHome(pos);

            Assert.StartsWith($"The folder of {pos} was repaired; the old one is kept at ", words, StringComparison.Ordinal);
            Assert.Null(host.HomeRefusalFor(pos));
            Assert.Null(WorkspaceBuilder.RefusalFor(host.HomeFor(pos)));
            Assert.Single(new LogStore(db).RecentActivity(), a => a.Text == words);
            var logged = db.Read(_ =>
            {
                using var c = db.Cmd("SELECT COUNT(*) FROM engineering_log WHERE event = 'home_repaired' AND metadata LIKE $p", ("$p", $"%{pos}%"));
                return Convert.ToInt32(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            });
            Assert.Equal(1, logged);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// GUARD — NO PIPE OP, VERB OR BOARD ACT REACHES THE REPAIR (CLAUDE.md, the owner's authority in-process only): no op or verb is named for
    /// a repair or a refused folder and none takes such an argument, and no source where pipe ops, door verbs and board acts are served
    /// names the act.
    /// </summary>
    [Fact]
    public void No_pipe_op_verb_or_board_act_reaches_the_repair()
    {
        string[] words = ["repair", "refused-home", "home-refusal", ".refused"];
        foreach (var op in TradeAgent.Gateway.GatewaySchema.Ops())
            foreach (var word in words)
            {
                Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, op.Cli, StringComparison.OrdinalIgnoreCase);
                foreach (var arg in op.Args) Assert.DoesNotContain(word, arg.Name, StringComparison.OrdinalIgnoreCase);
            }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tests"))) root = root.Parent;
        Assert.NotNull(root);
        string[] names = [nameof(WorkspaceBuilder.RepairHome), nameof(AppHost.HomeRefusalFor), nameof(WorkspaceBuilder.RefusedDir)];
        var reaching = new[] { "TradeAgent.Gateway", "TradeAgent.GatewayHost", "TradeAgent.TradeCli", Path.Combine("TradeAgent.Core", "Board") }
            .Select(d => Path.Combine(root.FullName, "src", d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(root.FullName, "src", "TradeAgent.AgentRuntime", "BrokerDoor.cs"))
            .Where(f => names.Any(n => File.ReadAllText(f).Contains(n, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(reaching.Count == 0, "an agent-facing or board source names the repair: " + string.Join(", ", reaching));
    }
    /// <summary>
    /// THE PRESS ON THE POSITION'S ROW (item 4): the row shows the refusal's sentence and one repair button only while its folder is refused; one
    /// press arms it and repairs nothing, a refresh between the presses rebuilds nothing and leaves it armed, and the second press repairs —
    /// after which the button is hidden and the row says what the repair did.
    /// </summary>
    [Fact]
    public void The_rows_press_arms_once_repairs_on_the_second_and_survives_a_refresh()
    {
        using var db = TestEnv.NewDb();
        OrgSurfaceSeed.Chart(db);
        string? refused = $"TradeAgent did not start the position {OrgSurfaceSeed.Member}: the last start could not rebuild its folder.";
        var repairs = new List<string>();
        var page = new OrganisationPage(new OrgSurfaceSources
        {
            Org = () => new OrgStore(db), Attempts = () => new AiAttemptStore(db), Currency = () => "USD",
            HomeRefusal = id => id == OrgSurfaceSeed.Member ? refused : null,
            RepairHome = id =>
            {
                repairs.Add(id);
                refused = null;
                return $"The folder of {id} was repaired; the old one is kept at /kept.";
            }
        });
        page.Update();
        var team = page.CardOf(OrgSurfaceSeed.Team) ?? throw new Xunit.Sdk.XunitException("the team's card was not made");
        var buttons = team.GetLogicalDescendants().OfType<Button>().ToList();
        var press = Assert.Single(buttons);                                  // the refused row's, and no other row's
        Assert.Equal(Labels.RepairHome, press.Content);
        Assert.Contains(refused!, team.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text));

        press.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Ui.IsArmed(press));
        Assert.Empty(repairs);

        page.Update();                                                       // the five-second refresh between the presses
        Assert.Same(team, page.CardOf(OrgSurfaceSeed.Team));
        Assert.Same(press, Assert.Single(team.GetLogicalDescendants().OfType<Button>()));
        Assert.True(Ui.IsArmed(press), "the refresh disarmed the half-pressed repair");

        press.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([OrgSurfaceSeed.Member], repairs);
        page.Update();
        Assert.False(press.IsVisible);
        Assert.False(Ui.IsArmed(press));
        var shown = team.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
        Assert.Contains($"The folder of {OrgSurfaceSeed.Member} was repaired; the old one is kept at /kept.", shown);
        Assert.DoesNotContain(shown, t => t?.StartsWith("TradeAgent did not start", StringComparison.Ordinal) == true);
    }
}
