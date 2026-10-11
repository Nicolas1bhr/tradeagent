using System.IO.Pipes;
using TradeAgent.Core;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FACTS A GRANT'S CONTAINER IS ASKED ABOUT, AND THE ONE PREDICATE THE KILL AND THE GRANT SHARE (<c>U-grant-tree</c>
/// item 1). The pipe's peer is read from the real kernel on every runner; the membership walk is driven over a table this
/// test writes, because a parent that started after its child is not something a test can make the kernel do.
/// </summary>
public class AttemptContainerTests
{
    [Fact]
    public async Task The_kernel_names_the_peer_of_a_connection_with_its_start_before_now()
    {
        var name = "ta-facts-" + Guid.NewGuid().ToString("n")[..12];
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await accepted;
        var mark = ProcessClock.Now();

        var facts = PeerImage.Facts(server);

        Assert.NotNull(facts);
        Assert.Equal(Environment.ProcessId, facts!.Pid);
        Assert.True(facts.Start > 0, $"the kernel gave no start for this process ({facts.Source})");
        Assert.True(facts.Start <= mark, $"this process started at {facts.Start}, after the accept at {mark} ({facts.Source})");
    }

    // ---- the predicate ------------------------------------------------------------------------------------------

    const int Leader = 50_000, Child = 50_001, Grandchild = 50_002, Stranger = 50_003;

    static Func<int, ProcessEntry?> Table(params ProcessEntry[] rows) =>
        pid => rows.FirstOrDefault(r => r.Pid == pid) is { Pid: > 0 } e ? e : null;

    static ProcessEntry P(int pid, int parent, ulong start) => new(pid, parent, pid, 1, start, false, false);

    [Fact]
    public void A_descendant_of_the_leader_is_a_member_and_a_stranger_is_not()
    {
        if (OperatingSystem.IsWindows()) return;   // the session's proof; Windows asks its job
        var table = Table(P(Leader, 1, 100), P(Child, Leader, 110), P(Grandchild, Child, 120), P(Stranger, 1, 130));

        Assert.True(TreeTeardown.IsMember(Grandchild, 120, Leader, 100, 0, table));
        Assert.True(TreeTeardown.IsMember(Leader, 100, Leader, 100, 0, table));
        Assert.False(TreeTeardown.IsMember(Stranger, 130, Leader, 100, 0, table));
    }

    [Fact]
    public void A_pid_whose_start_is_not_the_one_read_is_not_a_member()
    {
        if (OperatingSystem.IsWindows()) return;
        var table = Table(P(Leader, 1, 100), P(Child, Leader, 110));

        Assert.False(TreeTeardown.IsMember(Child, 999, Leader, 100, 0, table));
        Assert.False(TreeTeardown.IsMember(Child, 110, Leader, 101, 0, table));
    }

    [Fact]
    public void A_parent_that_started_after_its_child_is_a_pid_handed_on_and_breaks_the_chain()
    {
        if (OperatingSystem.IsWindows()) return;
        // The child's parent pid now names a process that started after the child did: not the parent it had.
        var table = Table(P(Leader, 1, 200), P(Child, Leader, 110));

        Assert.False(TreeTeardown.IsMember(Child, 110, Leader, 200, 0, table));
    }
}
