using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 2 — THE CATALOGUE AS AN AGENT DISCOVERS IT, AND AS A READ.
///
/// <para>Red first: the rows existed and nothing on the agent-facing surface named them, so
/// <c>trade schema --json</c> — the thing this product tells an agent to read instead of trusting a
/// prompt that drifts — described a build with no venues in it. A capability an agent cannot discover
/// is one it does not have.</para>
///
/// <para>The wire NAME is spelled out here rather than taken from <c>Ops</c>, because the name is the
/// contract: a constant renamed on both sides at once would keep this test green and break every
/// agent that had learned the word.</para>
/// </summary>
public class VenueOpsTests
{
    const string VenueList = "venue-list";

    static GatewaySchema.OpSpec Op(string name) =>
        GatewaySchema.Ops().SingleOrDefault(o => o.Op == name)
        ?? throw new Xunit.Sdk.XunitException($"the schema does not name '{name}'");

    [Fact]
    public void The_schema_names_venue_list_and_it_does_not_mutate()
    {
        var spec = Op(VenueList);

        Assert.False(spec.Mutating);
        Assert.Equal("trade venue list", spec.Cli);
        Assert.Empty(spec.Args);
        Assert.Contains("verified", spec.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// IT IS NOT IN <c>Ops.Mutating</c>, AND THAT LIST IS WHAT DECIDES WHO MAY CALL IT.
    ///
    /// <para>"Mutating" on this channel means "sends something to a broker", and reading a catalogue
    /// sends nothing anywhere. The list is also the role gate: <c>GatewayPipeServer</c> refuses a
    /// mutating op to everyone but the Operations Director's own launch, so a read that found its way
    /// onto it would be refused to the Research Director — the one part of the AI whose whole job is
    /// to know what an instrument's increment is before it sizes anything.</para>
    /// </summary>
    [Fact]
    public void Reading_the_catalogue_is_not_a_thing_that_moves_money()
    {
        Assert.DoesNotContain(VenueList, Ops.Mutating);
        Assert.False(Ops.IsMutating(VenueList));

        // And there is no op that WRITES one. The rows are the app's; `venues.json` is the owner's.
        var ops = GatewaySchema.Ops().Select(o => o.Op).ToList();
        Assert.DoesNotContain(ops, o => o is "venue-add" or "venue-set" or "venue-delete" or "venue-verify");
        Assert.Equal(1, ops.Count(o => o.StartsWith("venue", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A worker on the app's own harness reaches it too, and for every role. A new READ op joins
    /// <c>GrantedWorkerTools.TradeOps</c> deliberately — the list says which operations EXIST, and the
    /// gateway's own role check says who may use them.
    /// </summary>
    [Fact]
    public void The_harness_worker_may_read_the_catalogue()
    {
        Assert.Contains(VenueList, GrantedWorkerTools.TradeOps);
        Assert.DoesNotContain(GrantedWorkerTools.TradeOps, o => o is "venue-add" or "venue-set");
    }

    /// <summary>
    /// Every handler is in the deadline table, at zero: it asks the connector nothing. A handler that
    /// is ABSENT from that table is one nobody will notice growing a connector call.
    /// </summary>
    [Fact]
    public async Task The_catalogue_read_is_in_the_deadline_table_at_zero()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-venue-" + Guid.NewGuid().ToString("n")[..12]);

        var row = server.HandlerPaths.SingleOrDefault(h => h.Handler == VenueList);
        Assert.NotNull(row.Handler);
        Assert.Equal(TimeSpan.Zero, row.Path);
    }

    /// <summary>The preamble an agent reads before any op spec says the catalogue exists and what it is not.</summary>
    [Fact]
    public void The_schema_preamble_says_what_the_catalogue_does_not_claim()
    {
        var described = JsonSerializer.SerializeToElement(GatewaySchema.Describe(), Json.Options);
        var sentence = described.GetProperty("venue_catalogue").GetString()!;

        Assert.Contains("no fee", sentence, StringComparison.Ordinal);
        Assert.Contains("verified", sentence, StringComparison.Ordinal);
        Assert.Contains("cannot", sentence, StringComparison.Ordinal);
    }
}
