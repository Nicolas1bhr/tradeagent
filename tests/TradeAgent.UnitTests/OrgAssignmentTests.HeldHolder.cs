using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-WAKE-LEFTOVERS ITEM 2 (R31 A1-2): NO DEAD MANDATE ON A HELD POSITION. <c>research</c> runs unconfined and is held by the activation gate for
/// as long as a position beyond the legacy pair is active (the no-unconfined-neighbour rule), yet it is an active position in the chief's
/// subtree, so <c>mandate-assign</c> wrote a mandate to it whose wake was kept and never served until its deadline expired it. The verb now asks
/// the gate about the holder, as the host's facts read it, and refuses in words with nothing written.
/// </summary>
public partial class OrgAssignmentTests
{
    /// <summary>
    /// A chief on the chart, active, heading the root (so <c>research</c>'s division and the test's team are in its subtree), and the host's facts
    /// as the app reads them: the legacy pair declared unconfined, every other position on a mediated seat, a proven cell.
    /// </summary>
    static async Task<(TradingGateway Gw, Database Db, GatewayPipeServer Server, string Chief)> ChiefPipe(bool facts = true)
    {
        var (gw, db, server) = await Pipe();
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var chief = org.AppointChief(new ChiefSeat("puppet", "scripted-1"), r => r == "puppet", now).Chief!;
        org.Activate(chief, _ => SeatClasses.Puppet, 3, now, () => false);
        if (facts)
            gw.SeatFactsOf = id => new SeatFacts
            {
                Standing = new OrgStore(db).Standing(id),
                DeclaredClass = CouncilRoles.IsKnown(id) ? SeatClasses.Cli : SeatClasses.Puppet,
                ProvenCell = true,
                ActiveBeyondLegacy = new OrgStore(db).ActivePositionsBeyondLegacy()
            };
        return (gw, db, server, chief);
    }

    static long MandatesTo(Database db, string holder) => Count(db, "org_mandate", $"holder = '{holder}'");

    static int MandateWakesTo(Database db, string holder) =>
        new MissionEventStore(db).OfKind(MissionEventKind.Mandate).Count(e => e.Role == holder);

    /// <summary>
    /// RED FIRST: A MANDATE TO A HELD <c>research</c> IS REFUSED WITH NOTHING WRITTEN, in words that say why and what to do instead — a refusal of
    /// what was asked, never of the chief's rights. RED at base: the mandate was written and its wake raised to a holder that cannot turn.
    /// </summary>
    [Fact]
    public async Task A_mandate_to_a_research_the_gate_holds_is_refused_with_nothing_written()
    {
        var (_, db, server, chief) = await ChiefPipe();
        Assert.Contains(chief, new OrgStore(db).ActivePositionsBeyondLegacy());

        var refused = await server.CallAsync(AssignFrame(CouncilRoles.Research), chief, Launch(db, chief, SeatClasses.Puppet));

        Assert.False(refused.Ok, "a mandate to a position the activation gate holds was written");
        Assert.Contains($"'{CouncilRoles.Research}' cannot take a turn on this computer now, so it can hold no mandate", refused.Error!.Message,
            StringComparison.Ordinal);
        Assert.Contains("give it to a position that can", refused.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", refused.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0L, MandatesTo(db, CouncilRoles.Research));
        Assert.Equal(0, MandateWakesTo(db, CouncilRoles.Research));

        // THE STORE'S OWN VERDICT: a refusal of what was asked, never marked as the chief's rights.
        var issue = new MandateIssue(chief, null, CouncilRoles.Research, MandateKind.Exploration, "look at the open", null, null, false, Due, 2m,
            MandateLimits.DefaultRetryLimit);
        var thrown = Assert.Throws<TradeAgentException>(() => new OrgStore(db).Assign(issue, DateTimeOffset.UtcNow, heldNow: _ => "held."));
        Assert.False(RefusalMark.IsMarked(thrown), "a held holder was refused as the chief's rights");
        Assert.Equal(0L, MandatesTo(db, CouncilRoles.Research));
    }

    /// <summary>
    /// THE GUARD'S OTHER SIDE: a mandate to an active position the gate admits is written as before, one wake to it — the deciding turn's own
    /// open seat counts for nothing — and a host that reads no facts asks nobody, so a mandate to <c>research</c> is written as it was.
    /// </summary>
    [Fact]
    public async Task A_mandate_to_a_holder_the_gate_admits_is_unchanged()
    {
        var (_, db, server, chief) = await ChiefPipe();

        var reply = await server.CallAsync(AssignFrame(Member), chief, Launch(db, chief, SeatClasses.Puppet));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        Assert.Equal(1L, MandatesTo(db, Member));
        Assert.Equal(1, MandateWakesTo(db, Member));

        var (_, unread, blind, blindChief) = await ChiefPipe(facts: false);
        var written = await blind.CallAsync(AssignFrame(CouncilRoles.Research), blindChief, Launch(unread, blindChief, SeatClasses.Puppet));
        Assert.True(written.Ok, Json.Write(written.Error));
        Assert.Equal(1L, MandatesTo(unread, CouncilRoles.Research));
    }

    /// <summary>
    /// A RE-SCOPE INTO A HELD HOLDER IS REFUSED THE SAME WAY, and the mandate already written stays open as it was — its decider cancels it, or its
    /// deadline ends it.
    /// </summary>
    [Fact]
    public async Task A_rescope_of_a_mandate_held_by_a_held_research_is_refused_and_the_old_one_stays()
    {
        var (gw, db, server, chief) = await ChiefPipe(facts: false);
        var written = await server.CallAsync(AssignFrame(CouncilRoles.Research), chief, Launch(db, chief, SeatClasses.Puppet));
        Assert.True(written.Ok, Json.Write(written.Error));
        var old = Body(written).GetProperty("mandate").GetString()!;

        var org = new OrgStore(db);
        gw.SeatFactsOf = id => new SeatFacts
        {
            Standing = org.Standing(id), DeclaredClass = SeatClasses.Cli, ProvenCell = true, ActiveBeyondLegacy = org.ActivePositionsBeyondLegacy()
        };
        var refused = await server.CallAsync(Frame(Ops.MandateRescope, ("mandate", old), ("why", "narrower"), ("kind", MandateKind.Exploration),
            ("goal", "look at the open only"), ("due", Due.ToString("O")), ("allocation", 2m)), chief, Launch(db, chief, SeatClasses.Puppet));

        Assert.False(refused.Ok, "a successor was written to a position the activation gate holds");
        Assert.Contains("can hold no mandate", refused.Error!.Message, StringComparison.Ordinal);
        Assert.True(org.IsOpen(old));
        Assert.Equal(1L, MandatesTo(db, CouncilRoles.Research));
    }
}
