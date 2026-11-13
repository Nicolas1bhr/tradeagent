using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S WAY OUT OF A HELD AI (<c>U-windows-cards-2</c>): a folder that keeps every Windows AI seat from starting is moved aside —
/// renamed whole, beside itself, never deleted, never opened, never followed through a link — on the Dashboard's two presses, and the
/// same look the seat check makes then finds it clear. The folders are the tests' own, so the rule runs on every runner.
/// </summary>
public class HeldFoldersTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "tradeagent-tests", "held-folders", Guid.NewGuid().ToString("N"));
    readonly string _profile;
    readonly string _machine;

    public HeldFoldersTests()
    {
        _profile = Path.Combine(_root, "profile");
        _machine = Path.Combine(_root, "program-data");
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(_machine);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static IEnumerable<SeatPlan> Seats() => RuntimeCatalog.BuiltIn().Select(m => m.Seat).OfType<SeatPlan>();

    static RuntimeManifest Codex() => RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");

    IReadOnlyList<HeldFolder> Find() => HeldFolders.Find(_profile, _machine, Seats());

    string? Refusal() =>
        SeatProfile.AccountRefusal(Codex(), _profile, Codex().Seat!.AccountForbidden)
        ?? SeatProfile.MachineRefusal(Codex(), _machine, Codex().Seat!.MachineForbidden);

    /// <summary>
    /// THE OWNER'S SKILLS AND THE MACHINE'S SETTINGS, MOVED ASIDE WHOLE ON THE SECOND PRESS: the plan names how many items go to which
    /// folder and that nothing is deleted; carried, each folder is renamed beside itself with every byte in it as it was, the seat check
    /// is clear, and the words say how to put it back. Nothing in either folder is named anywhere.
    ///
    /// <para><b>RED on the base</b>: no way out existed — the seat stayed refused until the owner moved the folder from a terminal or
    /// File Explorer, with nothing in the app to press.</para>
    /// </summary>
    [Fact]
    public void Held_folders_are_moved_aside_whole_on_the_plan_the_first_press_armed_and_the_seat_check_is_clear()
    {
        var skills = Path.Combine(_profile, ".agents", "skills");
        Directory.CreateDirectory(Path.Combine(skills, "his-first-skill"));
        File.WriteAllText(Path.Combine(skills, "his-first-skill", "SKILL.md"), "his own words");
        File.WriteAllText(Path.Combine(skills, "loose-note.md"), "kept");
        var codexFolder = Path.Combine(_machine, "OpenAI", "Codex");
        Directory.CreateDirectory(codexFolder);
        File.WriteAllText(Path.Combine(codexFolder, "config.toml"), "planted = true");
        Assert.NotNull(Refusal());

        var held = Find();
        Assert.Equal([skills, codexFolder], held.Select(h => h.Path));
        Assert.Equal([2, 1], held.Select(h => h.Items));
        Assert.Equal([false, true], held.Select(h => h.OnMachine));

        var plan = HeldFolders.Plan(held)!;
        var skillsAside = Path.Combine(_profile, ".agents", $"skills ({HeldFolders.AsideMark})");
        var codexAside = Path.Combine(_machine, "OpenAI", $"Codex ({HeldFolders.AsideMark})");
        Assert.Equal($"Yes — move 2 items to {skillsAside} and 1 item to {codexAside}; nothing is deleted", plan.Sentence);

        var done = HeldFolders.Carry(plan, Find);

        Assert.True(done.Cleared, done.Words);
        Assert.Equal(2, done.Moved.Count);
        Assert.Null(Refusal());
        Assert.Empty(Find());
        Assert.False(Directory.Exists(skills));
        Assert.False(Directory.Exists(codexFolder));
        Assert.Equal("his own words", File.ReadAllText(Path.Combine(skillsAside, "his-first-skill", "SKILL.md")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(skillsAside, "loose-note.md")));
        Assert.Equal("planted = true", File.ReadAllText(Path.Combine(codexAside, "config.toml")));
        Assert.Contains("Nothing was deleted", done.Words, StringComparison.Ordinal);
        Assert.Contains("rename that folder to \"skills\" again", done.Words, StringComparison.Ordinal);
        Assert.Contains("it can start", done.Words, StringComparison.Ordinal);
        foreach (var inside in new[] { "his-first-skill", "loose-note", "config.toml", "planted" })
        {
            Assert.DoesNotContain(inside, done.Words, StringComparison.Ordinal);
            Assert.DoesNotContain(inside, plan.Sentence, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A LINK ON THE WAY IS WHAT MOVES, NEVER WHAT IT LEADS TO: with <c>.agents</c> a link to a folder elsewhere, the press renames the
    /// link itself, and the folder it pointed at — its skills included — is exactly where and as it was.
    ///
    /// <para><b>The mutant</b> that moves the forbidden path whatever is on the way renames <c>skills</c> inside the link's target: the
    /// folder elsewhere loses its skills, and this test fails on it.</para>
    /// </summary>
    [Fact]
    public void A_link_on_the_way_is_moved_as_itself_and_what_it_leads_to_is_left_where_it_was()
    {
        var elsewhere = Path.Combine(_root, "somewhere-else");
        Directory.CreateDirectory(Path.Combine(elsewhere, "skills", "a-skill"));
        var agents = Path.Combine(_profile, ".agents");
        Assert.True(SeatHomeTests.Link(agents, elsewhere, folder: true));

        var held = Assert.Single(Find());
        Assert.Equal(agents, held.Move);
        Assert.Equal(1, held.Items);

        var done = HeldFolders.Carry(HeldFolders.Plan([held])!, Find);

        Assert.True(done.Cleared, done.Words);
        Assert.True(Directory.Exists(Path.Combine(elsewhere, "skills", "a-skill")), "the link's target was moved: " + done.Words);
        Assert.False(Directory.Exists(Path.Combine(elsewhere, $"skills ({HeldFolders.AsideMark})")));
        Assert.True((File.GetAttributes(Path.Combine(_profile, $".agents ({HeldFolders.AsideMark})")) & FileAttributes.ReparsePoint) != 0);
        Assert.Null(Refusal());
    }

    /// <summary>
    /// NEVER OVER ANYTHING: a name already taken beside the folder gives the plan the next free one; a destination taken between the two
    /// presses, or a folder that changed after the first, refuses that step in words and moves nothing.
    /// </summary>
    [Fact]
    public void A_taken_name_is_never_written_over_and_a_folder_that_changed_after_the_first_press_is_left_as_it_is()
    {
        var skills = Path.Combine(_profile, ".agents", "skills");
        Directory.CreateDirectory(Path.Combine(skills, "x"));
        Directory.CreateDirectory(Path.Combine(_profile, ".agents", $"skills ({HeldFolders.AsideMark})"));

        var plan = HeldFolders.Plan(Find())!;
        var second = Path.Combine(_profile, ".agents", $"skills ({HeldFolders.AsideMark} 2)");
        Assert.Equal(second, Assert.Single(plan.Steps).To);

        File.WriteAllText(second, "something else came first");
        var refused = HeldFolders.Carry(plan, Find);
        Assert.False(refused.Cleared);
        Assert.Empty(refused.Moved);
        Assert.Contains("exists already", refused.Words, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(skills, "x")));
        Assert.Equal("something else came first", File.ReadAllText(second));

        File.Delete(second);
        Directory.Delete(skills, recursive: true);
        var changed = HeldFolders.Carry(plan, Find);
        Assert.Empty(changed.Moved);
        Assert.Contains("changed after you pressed", changed.Words, StringComparison.Ordinal);
        Assert.True(changed.Cleared);
        Assert.Null(HeldFolders.Plan(Find()));
    }
}
