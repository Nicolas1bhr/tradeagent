using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// (f) NEW MATERIAL IS LISTED TO EACH ROLE SINCE ITS OWN LAST TURN (<c>U-reconcile-wakes</c>). The
/// Situation's "New in <c>../inbox</c> since your last turn" was measured from the last Situation of EITHER
/// role, so a file listed in the Research Director's turn was never listed to the chair.
/// </summary>
public class RoleInboxMarksTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// RED at base 604a3133's semantics (one mark shared by both roles, extracted unchanged): the chair's
    /// list after Research's turn was empty.
    ///
    /// <para>Rewritten by review 1's fixer on the durable marks (findings 5 and 6): a role's turn is now what its
    /// Situation lists from its own mark and then the launch its admission writes, where it was a call that moved an
    /// in-memory mark. The scenario and every assertion are the builder's.</para>
    /// </summary>
    [Fact]
    public void New_material_is_listed_to_each_role_since_its_own_last_turn()
    {
        using var db = TestEnv.NewDb();
        var root = Path.Combine(TestEnv.Home, $"inbox-marks-{Guid.NewGuid():n}");
        var inbox = Path.Combine(root, MaterialScanner.InboxDir);
        Directory.CreateDirectory(inbox);

        // ONE TURN OF A ROLE: what its Situation lists from its own mark, then the launch its admission writes.
        IReadOnlyList<string> Turn(string role, DateTimeOffset at)
        {
            var listed = RoleInboxMarks.Arrived(db, RoleInboxMarks.Since(db, role));
            Admitted(db, role, at);
            return listed;
        }

        File.WriteAllText(Path.Combine(inbox, "owner-notes.md"), "the owner's notes");
        new MaterialScanner(db, root, now: () => Start.AddMinutes(1)).Scan();

        // RESEARCH TURNS FIRST AND IS TOLD.
        Assert.Equal(["owner-notes.md"], Turn(CouncilRoles.Research, Start.AddMinutes(2)));

        // AND THE CHAIR IS STILL TOLD AT ITS OWN TURN: the file is news to a role that has not seen it.
        Assert.Equal(["owner-notes.md"], Turn(CouncilRoles.Operations, Start.AddMinutes(3)));

        // Each role is told once: nothing new since either role's own last turn.
        Assert.Empty(Turn(CouncilRoles.Research, Start.AddMinutes(4)));
        Assert.Empty(Turn(CouncilRoles.Operations, Start.AddMinutes(5)));

        // A second file after the chair's turn reaches both, each from its own mark.
        File.WriteAllText(Path.Combine(inbox, "second.csv"), "a,b\n1,2\n");
        new MaterialScanner(db, root, now: () => Start.AddMinutes(6)).Scan();
        Assert.Equal(["second.csv"], Turn(CouncilRoles.Operations, Start.AddMinutes(7)));
        Assert.Equal(["second.csv"], Turn(CouncilRoles.Research, Start.AddMinutes(8)));
    }

    /// <summary>The owner's file, recorded by the app's own scanner at <paramref name="at"/>.</summary>
    static void Arrives(Database db, string name, DateTimeOffset at)
    {
        var root = Path.Combine(TestEnv.Home, $"inbox-durable-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, name), $"the owner's {name}");
        new MaterialScanner(db, root, now: () => at).Scan();
    }

    /// <summary>What the app's own Situation lists to <paramref name="role"/> as new in the owner's inbox.</summary>
    static async Task<IReadOnlyList<string>> Listed(AppHost.MissionHost mission, string role) =>
        (await mission.SituationAsync(role, CancellationToken.None)).NewMaterial;

    /// <summary>
    /// A LAUNCH THE LOOP'S ADMISSION WROTE FOR <paramref name="role"/>, its id minted at <paramref name="minted"/>,
    /// consuming one wake in the same commit — what a turn the role actually took leaves behind.
    /// </summary>
    static void Admitted(Database db, string role, DateTimeOffset minted)
    {
        var wake = $"review:{Guid.NewGuid():n}";
        new MissionEventStore(db).Raise(wake, MissionEventKind.Review, minted, role: role);
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = TurnMeter.IdMintedAt(minted),
            StartedAt = minted.AddSeconds(2),
            ReservedCost = 0.10m,
            Role = role
        }, [wake]);
        Assert.True(admission.Admitted);
    }

    /// <summary>
    /// MATERIAL A ROLE WAS NEVER SHOWN IS LISTED ON ITS FIRST TURN, whenever the app started. A file that arrived
    /// before this process did, and that no turn of the Research Director was ever shown, is news to it.
    ///
    /// <para>RED at a2e839e6 (review 1, finding 5): each role's mark began at the host's construction, so the file was
    /// never listed to either role.</para>
    /// </summary>
    [Fact]
    public async Task Material_a_role_was_never_shown_is_listed_on_its_first_turn_after_a_restart()
    {
        using var db = TestEnv.NewDb();
        Arrives(db, "before-the-restart.md", DateTimeOffset.UtcNow.AddHours(-3));
        var connector = new FakeConnector(new FakeBroker());
        var host = AppHost.Composed(db, connector, new AgentPresence());
        try
        {
            var mission = new AppHost.MissionHost(host);
            Assert.Equal(["before-the-restart.md"], await Listed(mission, CouncilRoles.Research));
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// A SITUATION NO TURN WAS ADMITTED FOR TELLS THE ROLE NOTHING, so what it listed is listed again. Only a launch
    /// the loop's admission wrote moves a role's mark — to the instant that launch's id was minted, before its
    /// Situation was composed — and the other role's turn never moves it.
    ///
    /// <para>RED at a2e839e6 (review 1, finding 6): composing the Situation moved the mark before the lease and the
    /// admission, so a refused launch spent the listing and the next turn was told nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_launch_spends_no_listing_and_only_an_admitted_one_moves_the_mark()
    {
        using var db = TestEnv.NewDb();
        var connector = new FakeConnector(new FakeBroker());
        var host = AppHost.Composed(db, connector, new AgentPresence());
        try
        {
            var mission = new AppHost.MissionHost(host);
            // ON THE WALL CLOCK THE HOST READS, each instant after the one before it.
            await Task.Delay(20);
            Arrives(db, "first.csv", DateTimeOffset.UtcNow);
            await Task.Delay(20);

            // COMPOSED, THEN REFUSED: no launch record, so nothing was told.
            Assert.Equal(["first.csv"], await Listed(mission, CouncilRoles.Research));
            Assert.Equal(["first.csv"], await Listed(mission, CouncilRoles.Research));

            // ADMITTED: the role's own launch moves its own mark, and only its own.
            Admitted(db, CouncilRoles.Research, DateTimeOffset.UtcNow);
            await Task.Delay(20);
            Assert.Empty(await Listed(mission, CouncilRoles.Research));
            Assert.Equal(["first.csv"], await Listed(mission, CouncilRoles.Operations));

            // A LATER FILE reaches the role from its own mark.
            Arrives(db, "second.csv", DateTimeOffset.UtcNow);
            Assert.Equal(["second.csv"], await Listed(mission, CouncilRoles.Research));
        }
        finally { await host.Gateway.DisposeAsync(); }
    }
}
