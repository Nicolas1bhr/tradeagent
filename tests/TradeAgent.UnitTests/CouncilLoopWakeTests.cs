using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EACH ROLE IS WOKEN FOR WHAT IT OWNS (<c>U-reconcile-wakes</c>): every wake names its role and says
/// truthfully what it is, a role with no open work is not looked at, and a renewal is bought only for a
/// role its allowance stopped.
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>
    /// (d) A NOTE WAKE IS NAMED AND QUOTED. The paper allocation and the forward-run notes are TradeAgent's
    /// own words to Research about its versions; a turn that read "Why you are awake: note" and nothing
    /// else had to spend a tool call opening the file the app already held.
    ///
    /// <para>RED at base 604a3133: the reason line read "note" and the note's text was not in the prompt.</para>
    /// </summary>
    [Fact]
    public async Task A_note_wake_is_named_and_quoted()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        const string content = "Forward run of version v-1 on BTCUSDT, day 2026-10-10: 3 trades, the run continues.";
        new PublicationStore(db).Commit(new Publication
        {
            Id = Publication.IdOf("allocator", PublicationKind.Note, content),
            Role = "allocator",
            Kind = PublicationKind.Note,
            Recipients = CouncilRoles.Research,
            Classification = PublicationClass.Council,
            CreatedAt = now,
            Content = content
        }, now, MissionEventIds.PaperRun("deployment-1", "2026-10-10"));

        await loop.TurnAsync();

        var (role, prompt) = Assert.Single(host.Opened);
        Assert.Equal(CouncilRoles.Research, role);
        Assert.Contains("- Why you are awake: TradeAgent's forward-run note arrived in `in/`" + Environment.NewLine,
            prompt, StringComparison.Ordinal);
        Assert.Contains("> " + content, prompt, StringComparison.Ordinal);
        Assert.Contains("A note TradeAgent wrote itself", prompt, StringComparison.Ordinal);
    }
}
