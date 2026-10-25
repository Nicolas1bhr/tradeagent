using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class OrgChiefTests
{
    // ---- item 8: the guards ----------------------------------------------------------------------------------------------------------

    /// <summary>The chief's appointment, replacement and removal: the chart's one writer and the owner's in-process route to it (AUTH-001, AUTH-002).</summary>
    static readonly string[] ChiefSeams = ["SeatChief", "AppointChief", "ReplaceChief", "VacateChief"];

    static string Src() => Path.Combine(DayOnePrograms.RepoRoot(), "src");

    static bool Built(string f) => f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                   || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>The src files whose text names <paramref name="name"/> followed by an opening parenthesis — a call or a definition — relative, sorted.</summary>
    static string[] Naming(string name) =>
        [.. Directory.GetFiles(Src(), "*.cs", SearchOption.AllDirectories).Where(f => !Built(f))
            .Where(f => File.ReadAllText(f).Contains(name + "(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Src(), f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    /// <summary>
    /// (8) NO OP OR VERB REACHES THE CHIEF'S APPOINTMENT (AUTH-001; CHANGE-001; ORDER-001): no source of the pipe server, the broker door, the
    /// worker's tools, the schema or the CLI names the chart's chief writer or the owner's route to it — comments included — and no op of the
    /// pipe is named for the chief; the chart's writer is internal and called by the owner's route and the board's confirmed act alone
    /// (<c>OrgStore.Chief.cs</c>, <c>BoardStore.cs</c>); the owner's route is called by his own app's presses alone (<c>AppHost.cs</c>); and a
    /// chief's id never holds order permission, as a role or as a caller's context. Watched red with a pipe source naming <c>AppointChief</c>.
    /// </summary>
    [Fact]
    public void No_op_or_verb_reaches_the_chiefs_appointment()
    {
        var src = Src();
        var agentFacing = Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.Gateway"), "GatewayPipeServer*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.AgentRuntime"), "BrokerDoor*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.AgentRuntime"), "WorkerTools*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.TradeCli"), "*.cs", SearchOption.AllDirectories))
            .Concat([Path.Combine(src, "TradeAgent.Gateway", "GatewaySchema.cs")])
            .Where(f => !Built(f)).ToList();
        Assert.Contains(agentFacing, f => f.EndsWith("GatewayPipeServer.Board.cs", StringComparison.Ordinal));
        Assert.Contains(agentFacing, f => f.EndsWith("BrokerDoor.cs", StringComparison.Ordinal));
        var hits = agentFacing.SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(l => ChiefSeams.Any(s => l.line.Contains(s, StringComparison.Ordinal)))
            .Select(l => $"{Path.GetFileName(l.f)}:{l.i + 1}: {l.line.Trim()}")
            .ToList();
        foreach (var h in hits) log.WriteLine(h);
        Assert.Empty(hits);
        Assert.DoesNotContain(GatewaySchema.Ops(), op => op.Op.Contains("chief", StringComparison.OrdinalIgnoreCase));

        // THE CHART'S WRITER: internal, and named by the owner's route and the board's act alone; THE OWNER'S ROUTE: by his own app's presses.
        Assert.True(typeof(OrgStore).GetMethod("SeatChief", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            is { IsAssembly: true });
        Assert.Equal(["TradeAgent.Core/Board/BoardStore.cs", "TradeAgent.Core/Db/OrgStore.Chief.cs"], Naming("SeatChief"));
        foreach (var route in ChiefSeams.Skip(1))
            Assert.Equal(["TradeAgent.App/AppHost.cs", "TradeAgent.Core/Db/OrgStore.Chief.cs"], Naming("." + route) .Concat(Naming(" " + route)).Distinct().Order(StringComparer.Ordinal));

        // A CHIEF NEVER PLACES AN ORDER (ORDER-001): not as a role, not as a caller's context.
        using var db = TestEnv.NewDb();
        var chief = new OrgStore(db).AppointChief(Puppet, Known, Now).Chief!;
        Assert.False(CouncilRoles.MayPlaceOrders(chief));
        Assert.False(AgentContext.ForAgent("chief", chief, "turn-chief", null).MayPlaceOrders);
        Assert.True(CouncilRoles.MayPlaceOrders(CouncilRoles.Operations));
    }
}
