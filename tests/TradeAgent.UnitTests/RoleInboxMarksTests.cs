using TradeAgent.App;
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
    /// </summary>
    [Fact]
    public void New_material_is_listed_to_each_role_since_its_own_last_turn()
    {
        using var db = TestEnv.NewDb();
        var root = Path.Combine(TestEnv.Home, $"inbox-marks-{Guid.NewGuid():n}");
        var inbox = Path.Combine(root, MaterialScanner.InboxDir);
        Directory.CreateDirectory(inbox);
        var marks = new RoleInboxMarks(Start);

        File.WriteAllText(Path.Combine(inbox, "owner-notes.md"), "the owner's notes");
        new MaterialScanner(db, root, now: () => Start.AddMinutes(1)).Scan();

        // RESEARCH TURNS FIRST AND IS TOLD.
        Assert.Equal(["owner-notes.md"],
            RoleInboxMarks.Arrived(db, marks.Take(CouncilRoles.Research, Start.AddMinutes(2))));

        // AND THE CHAIR IS STILL TOLD AT ITS OWN TURN: the file is news to a role that has not seen it.
        Assert.Equal(["owner-notes.md"],
            RoleInboxMarks.Arrived(db, marks.Take(CouncilRoles.Operations, Start.AddMinutes(3))));

        // Each role is told once: nothing new since either role's own last turn.
        Assert.Empty(RoleInboxMarks.Arrived(db, marks.Take(CouncilRoles.Research, Start.AddMinutes(4))));
        Assert.Empty(RoleInboxMarks.Arrived(db, marks.Take(CouncilRoles.Operations, Start.AddMinutes(5))));

        // A second file after the chair's turn reaches both, each from its own mark.
        File.WriteAllText(Path.Combine(inbox, "second.csv"), "a,b\n1,2\n");
        new MaterialScanner(db, root, now: () => Start.AddMinutes(6)).Scan();
        Assert.Equal(["second.csv"],
            RoleInboxMarks.Arrived(db, marks.Take(CouncilRoles.Operations, Start.AddMinutes(7))));
        Assert.Equal(["second.csv"],
            RoleInboxMarks.Arrived(db, marks.Take(CouncilRoles.Research, Start.AddMinutes(8))));
    }
}
