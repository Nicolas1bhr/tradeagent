using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// (h) EACH REFUSED MEMORY FILE IS HEADED BY WHAT HAPPENED TO IT (<c>U-reconcile-wakes</c>, owed by
/// <c>U-memory-kept</c>). One header — "put the version before it back" — headed every notice, and it was false of the
/// first-ever refusal (there was nothing to put back) and of a file whose text could not be kept (nothing was put back
/// over it).
/// </summary>
public class RevisionNoticeHeaderTests
{
    static readonly DateTimeOffset At = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    static string Lines(int n, string what) => string.Join("\n", Enumerable.Range(1, n).Select(i => $"{what} {i}")) + "\n";

    /// <summary>
    /// RED at base 604a3133's Situation (every notice under the one header): the plan refused for the first time —
    /// nothing put back — read under "put the version before it back".
    /// </summary>
    [Fact]
    public void Each_refused_memory_file_is_headed_by_what_happened_to_it()
    {
        var root = Path.Combine(TestEnv.Home, $"notice-headers-{Guid.NewGuid():n}");
        using var db = new Database(Path.Combine(root, "state.db"));
        var role = CouncilRoles.Research;
        string Home(string r) => WorkspaceBuilder.HomeOf(root, r);
        Directory.CreateDirectory(Path.Combine(Home(role), "trading"));
        string PathOf(string kind) => Path.Combine(Home(role),
            WorkspaceRevisions.Files.Single(f => f.Kind == kind).Path.Replace('/', Path.DirectorySeparatorChar));
        int Cap(string kind) => WorkspaceRevisions.Files.Single(f => f.Kind == kind).Lines;
        var revisions = new WorkspaceRevisions(db, Home, () => At);

        // THE JOURNAL IS ACCEPTED ONCE, then refused: kept and put back.
        File.WriteAllText(PathOf(PublicationKind.Journal), Lines(3, "2026-10-11 a note"));
        Assert.Empty(revisions.Snapshot(role, "turn-a"));
        File.WriteAllText(PathOf(PublicationKind.Journal), Lines(Cap(PublicationKind.Journal) + 5, "2026-10-11 more"));
        // THE PLAN IS REFUSED THE FIRST TIME IT IS EVER WRITTEN: nothing to put back.
        File.WriteAllText(PathOf(PublicationKind.Plan), Lines(Cap(PublicationKind.Plan) + 5, "plan line"));
        WorkspaceRevisions.Apply(revisions.Snapshot(role, "turn-b"));

        var text = new MissionSituation
        {
            Role = role,
            Restored = WorkspaceRevisions.Notices(db, role, putBack: true),
            NotRestored = WorkspaceRevisions.Notices(db, role, putBack: false)
        }.Text();

        var lines = text.Replace("\r\n", "\n").Split('\n');
        string HeaderOf(string file)
        {
            var at = Array.FindIndex(lines, l => l.StartsWith($"- `{file}`", StringComparison.Ordinal));
            Assert.True(at >= 0, $"no notice for {file}:\n{text}");
            for (var i = at; i >= 0; i--)
                if (lines[i].StartsWith("**", StringComparison.Ordinal)) return lines[i];
            Assert.Fail($"the notice for {file} has no header:\n{text}");
            return "";
        }

        var journal = HeaderOf("trading/JOURNAL.md");
        Assert.Contains("put the version before it back", journal, StringComparison.Ordinal);
        Assert.Contains("kept it in `trading/archive/`", journal, StringComparison.Ordinal);

        var plan = HeaderOf("trading/PLAN.md");
        Assert.DoesNotContain("put the version before it back", plan, StringComparison.Ordinal);
        Assert.Contains("put nothing back", plan, StringComparison.Ordinal);
    }
}
