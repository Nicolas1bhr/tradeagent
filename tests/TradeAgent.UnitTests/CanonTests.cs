using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CANON: each role's <c>AGENTS.md</c> a short deliberate text whose capability facts are generated from what the app
/// grants that role on that runtime (<c>U-canon</c>; VISION § 6.7; R20 § 1).
///
/// <para>The pairs that run are three: the Operations Director on a vendor CLI (the chair never leaves it), and the
/// Research Director on a CLI or on the app-owned harness. Every assertion below is about one of them.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class CanonTests(ITestOutputHelper log) : IDisposable
{
    readonly string _root = Path.Combine(TestEnv.Home, $"canon-{Guid.NewGuid():n}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    /// <summary>The three pairs that run, and Operations on the harness, which the reach answers for and nothing runs.</summary>
    public static TheoryData<string, RuntimeClass> Reaches => new()
    {
        { CouncilRoles.Operations, RuntimeClass.Cli },
        { CouncilRoles.Research, RuntimeClass.Cli },
        { CouncilRoles.Research, RuntimeClass.Harness },
        { CouncilRoles.Operations, RuntimeClass.Harness }
    };

    // ---- (c) the reach is read from what enforces it --------------------------------------------------

    /// <summary>
    /// (c) WHAT A SEAT CAN REACH IS READ FROM THE CODE THAT GRANTS OR REFUSES IT, item by item. Each line here sets one
    /// field of the reach against the thing that enforces it — the gateway's schema and the line the pipe refuses an
    /// order on, the harness's tools and write roots and the path rule behind them, the loop's wake file and cap, the
    /// relay's grammar, the sandbox's answer — so a reach that drifted from any of them, or a hand-kept list standing in
    /// for one, fails here before a canon is rendered from it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Reaches))]
    public void The_reach_is_read_from_what_enforces_it(string role, RuntimeClass runtime)
    {
        var reach = AgentReach.For(role, runtime);
        bool Refused(string op) => Ops.IsMutating(op) && !CouncilRoles.MayPlaceOrders(role);
        log.WriteLine($"{role}·{runtime}: {reach.Verbs.Count} verbs, tools {string.Join(" ", reach.Tools)}, "
                      + $"writes {string.Join(" ", reach.Writes)}, wake {reach.WakeFile ?? "none"}");

        // NEVER A VERB THE GATEWAY REFUSES THIS ROLE, on either runtime: the pipe's own line is IsMutating && !MayPlaceOrders.
        Assert.DoesNotContain(reach.Verbs, v => Refused(v.Op));
        Assert.All(reach.Verbs, v => Assert.Equal(Ops.IsMutating(v.Op), v.Mutating));

        // WHAT IT HANDS BACK IS THE RELAY'S GRAMMAR: its kinds, the files they are read from, their caps.
        Assert.Equal(
            CouncilRelay.KindsFor(role).Select(k => (k, $"{CouncilRoles.OutDir}/{CouncilRelay.Pattern(k).Replace("*", "<attempt>")}", CouncilRelay.Limit(k))),
            reach.HandsBack.Select(h => (h.Kind, h.Path, h.Lines)));

        using var home = TestEnv.NewScratch("reach");
        Directory.CreateDirectory(home.Dir);

        if (runtime == RuntimeClass.Cli)
        {
            // THE GATEWAY'S SCHEMA, in its order, less exactly what the pipe refuses — and the forms the CLI spells them in.
            var served = GatewaySchema.Ops().Where(o => !Refused(o.Op)).ToList();
            Assert.Equal(served.Select(o => o.Op), reach.Verbs.Select(v => v.Op));
            Assert.Equal(served.Select(o => o.Cli), reach.Verbs.Select(v => v.Invocation));
            Assert.Equal(["trade"], reach.Tools);

            // NO WALL WITHOUT A SANDBOX: the folders are a convention exactly while Containment says nothing confines it.
            Assert.Equal(Containment.Sandbox().Ok, reach.Walled);
            Assert.True(reach.RunsCode);
            Assert.Equal(AgentReach.OsName(), reach.Os);
            Assert.Equal([AgentReach.OwnFolder, AgentReach.Inbox], reach.Reads);
            Assert.Equal([.. WorkspaceBuilder.SubDirs, CouncilRoles.OutDir, AgentReach.TopFolder(MissionLoop.WakeFile)], reach.Writes);
            Assert.DoesNotContain(CouncilRoles.InDir, reach.Writes);

            // THE LOOP'S WAKE FILE AND ITS CAP, and the owner's session length or the loop's default.
            Assert.Equal(MissionLoop.WakeFile, reach.WakeFile);
            Assert.Equal(new MissionOptions().MaxDelay, reach.WakeCap);
            Assert.Equal(new MissionOptions().TurnsPerSession, reach.TurnsPerSession);
            Assert.Equal(7, AgentReach.For(role, runtime, turnsPerSession: 7).TurnsPerSession);
        }
        else
        {
            // THE HARNESS'S OWN SIX TOOLS, as the provider is offered them, and the ops each one's call accepts.
            Assert.Equal(new GrantedWorkerTools(role, () => home.Dir, () => null).Offered.Select(t => t.Name), reach.Tools);
            Assert.Equal(reach.Tools.SelectMany(t => GrantedWorkerTools.OpsOf(t).Where(op => !Refused(op))), reach.Verbs.Select(v => v.Op));
            Assert.All(reach.Verbs, v => Assert.Contains(v.Op, GrantedWorkerTools.OpsOf(v.Invocation)));

            // A WALL, AND THE PATH RULE IS WHAT HOLDS IT: every folder it writes is accepted by write_file's own rule, every
            // folder a CLI seat writes and it does not is refused by it, and so is the wake file and the owner's inbox.
            Assert.True(reach.Walled);
            Assert.False(reach.RunsCode);
            Assert.Null(reach.Os);
            Assert.Equal(GrantedWorkerTools.Writable, reach.Writes);
            foreach (var dir in reach.Writes)
                Assert.Null(ToolPaths.Resolve(home.Dir, $"{dir}/x.md", GrantedWorkerTools.Writable).Refusal);
            foreach (var dir in AgentReach.For(role, RuntimeClass.Cli).Writes.Except(reach.Writes))
                Assert.NotNull(ToolPaths.Resolve(home.Dir, $"{dir}/x.md", GrantedWorkerTools.Writable).Refusal);
            Assert.NotNull(ToolPaths.Resolve(home.Dir, MissionLoop.WakeFile, GrantedWorkerTools.Writable).Refusal);
            Assert.NotNull(ToolPaths.Resolve(home.Dir, $"{AgentReach.Inbox}/x.md").Refusal);
            Assert.Equal([AgentReach.OwnFolder], reach.Reads);

            // NO WAKE IT CAN WRITE, and a session that is every turn: the harness sends its system text and the turn's
            // message, fresh, on every turn.
            Assert.Null(reach.WakeFile);
            Assert.Null(reach.WakeCap);
            Assert.Equal(1, reach.TurnsPerSession);
        }
    }
}
