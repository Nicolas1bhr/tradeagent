using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A TURN'S LAST STREAM LINES REACH ITS END, WHATEVER ORDER ITS LEADER'S EXIT, ITS TREE'S TEARDOWN AND ITS READ COME IN
/// (<c>U-turn-tail-drain</c>). The leader's exit ends the turn's tree at once (U-agent-tree); the question these answer is
/// whether that teardown can cut what the leader wrote before it exited — its usage, the vendor's refusal — out of the turn.
///
/// <para>The order is FORCED, never hoped for: the session's seam runs before a single byte of the stream is read, waits
/// for the leader to exit, and ends the tree itself — so every read below begins after the teardown, which is the order a
/// slow reader meets on a loaded machine.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class TurnTailDrainTests
{
    /// <summary>
    /// THE RECORDED REFUSAL, EMITTED ONCE AND LAST: the recording without its closing <c>turn.failed</c>, so the vendor's
    /// <c>error</c> event is the stream's FINAL line. The recording carries the refusal twice (<c>error</c>, then
    /// <c>turn.failed</c>), and an earlier copy satisfies every assertion below even when the final one is lost — the witness
    /// would not see the tail it exists to see.
    /// </summary>
    static string RefusalOnceAndLast
    {
        get
        {
            var lines = VendorLimitTests.Stdout.Split('\n');
            var kept = lines[..^1];
            Assert.Contains("\"type\":\"error\"", kept[^1], StringComparison.Ordinal);
            return string.Join('\n', kept);
        }
    }

    /// <summary>The seam: the leader exits, the tree is ended, and only then may the turn read. Says what the teardown found.</summary>
    static Action<ContainedProcess> EndBeforeTheRead(List<TreeEnd?> seen) => contained =>
    {
        contained.Process.WaitForExit();
        seen.Add(contained.End());
    };

    /// <summary>
    /// THE USAGE LINE, WRITTEN LAST BY A LEADER THAT EXITS AT ONCE, IS ON THE TURN'S END though the tree was torn down before
    /// the read began. Lost, the reservation stands on an estimate where the vendor had said what the turn used.
    /// </summary>
    [Fact]
    public async Task A_usage_line_written_last_reaches_the_turns_end_though_the_tree_ended_before_the_read()
    {
        var session = AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream,
            name: nameof(A_usage_line_written_last_reaches_the_turns_end_though_the_tree_ended_before_the_read));
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        var seen = new List<TreeEnd?>();

        AgentSession.BeforeTheRead.Value = EndBeforeTheRead(seen);
        try { await session.SendAsync("say hi"); }
        finally { AgentSession.BeforeTheRead.Value = null; }

        var said = AgentRuntimeProbe.Said(session, ended);
        Assert.True(seen is [{ Ended: true }], $"the tree was not ended before the read: {seen.Count} teardown(s). {said}");
        Assert.True(ended?.Usage is { InputTokens: 17232, OutputTokens: 6 }, $"the usage line did not reach the turn's end. {said}");
    }

    /// <summary>
    /// THE VENDOR'S REFUSAL, IN THE LAST LINES OF A LEADER THAT EXITS AT ONCE, IS ON THE TURN'S END — before any work, so the
    /// meter charges it nothing — though the tree was torn down before the read began. Lost, a refusal is charged the whole
    /// reservation and the loop never learns when to try again.
    /// </summary>
    [Fact]
    public async Task A_vendor_limit_written_last_reaches_the_turns_end_though_the_tree_ended_before_the_read()
    {
        var session = VendorLimitTests.Refusing(
            nameof(A_vendor_limit_written_last_reaches_the_turns_end_though_the_tree_ended_before_the_read),
            RefusalOnceAndLast);
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        var seen = new List<TreeEnd?>();

        AgentSession.BeforeTheRead.Value = EndBeforeTheRead(seen);
        try { await session.SendAsync("reply ok"); }
        finally { AgentSession.BeforeTheRead.Value = null; }

        var said = AgentRuntimeProbe.Said(session, ended);
        Assert.True(seen is [{ Ended: true }], $"the tree was not ended before the read: {seen.Count} teardown(s). {said}");
        Assert.True(ended?.Limit is { BeforeAnyWork: true }, $"the vendor's refusal did not reach the turn's end. {said}");
        Assert.Equal(VendorLimitTests.VendorSaid, ended!.Limit!.Message);
        Assert.Equal(VendorLimitTests.VendorSaid, session.History[^1].Text);
    }
}
