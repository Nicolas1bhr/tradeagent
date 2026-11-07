using System.Diagnostics;
using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A LAUNCH THAT ENDED BEFORE THE APP LOOKED AT IT (<c>U-main-red-3</c>; main CI runs 41187197397 and 42340766269, macOS,
/// <c>A_runtime_with_no_resume_records_every_turn_fresh</c>, exit codes [-1, 0]). Reproduced on this Mac at 1 launch in about 8 400 beside the unit assembly, and
/// measured: "Process.Start took 116.5ms, GC paused 110.0ms over it, gen2 1", then ESRCH from the table and "process exited 0". A blocking collection held the
/// launching thread inside the start until the child had run to its end, and the app called a finished turn a launch it could not hold.
/// </summary>
public class FinishedLeaderTests
{
    static AgentSession Probe(int sleepSeconds = 0, [System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream, sleepSeconds: sleepSeconds, name: name);

    /// <summary>
    /// THE TABLE SHOWS A ZOMBIE ON BOTH SYSTEMS. A parent that never reaps its finished child holds it as one for as long as this test looks; it is read, as one,
    /// on macOS as on Linux.
    ///
    /// <para>RED on macOS at e88a7201: the read answered null, "no zombie was read, last read: nothing", because the kernel's call was asked with 0.</para>
    /// </summary>
    [OnlyOnFact("the process table is not asked on Windows", "linux", "macos")]
    public void A_finished_child_nobody_has_reaped_is_in_the_table_as_a_zombie()
    {
        var psi = new ProcessStartInfo("/usr/bin/perl") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-e", "$| = 1; my $k = fork(); exit 0 unless $k; print \"$k\\n\"; sleep 30;" }) psi.ArgumentList.Add(a);
        using var parent = Process.Start(psi)!;
        try
        {
            var child = int.Parse(parent.StandardOutput.ReadLine()!, CultureInfo.InvariantCulture);
            var until = DateTime.UtcNow.AddSeconds(10);
            ProcessEntry? read;
            do read = ProcessTable.Read(child);
            while (read is not { Zombie: true } && DateTime.UtcNow < until);

            Assert.True(read is { Zombie: true }, $"no zombie was read, last read: {read?.ToString() ?? "nothing"}");
            Assert.Equal(child, read!.Value.Pid);
            Assert.NotEqual(0UL, read.Value.Start);
        }
        finally { parent.Kill(entireProcessTree: true); }
    }

    /// <summary>
    /// THE TURN ENDED AS IT ENDED. The leader finishes and is reaped before its first read, which is what the collection did: the turn's exit code, its words and
    /// its session are the child's, and nothing says the app could not hold it.
    ///
    /// <para>RED at e88a7201 with this test's seam: "ExitCode -1; Raw: &lt;&lt;TradeAgent could not hold the AI's program it had just started".</para>
    /// </summary>
    [OnlyOnFact("a job holds a launch on Windows, and it is made before the program runs", "linux", "macos")]
    public async Task A_program_that_finished_before_its_leader_was_read_is_a_turn_that_ended_as_it_ended()
    {
        var session = Probe();
        var ended = new List<AgentTurnEnded>();
        session.TurnEnded += ended.Add;

        ProcessContainment.ReadLeader.Value = p => { p.WaitForExit(); return ProcessTable.Read(p.Id); };
        try { await session.SendAsync("anything"); }
        finally { ProcessContainment.ReadLeader.Value = null; }

        var turn = Assert.Single(ended);
        Assert.True(turn.ExitCode == 0, AgentRuntimeProbe.Said(session, turn));
        Assert.Equal("fresh", turn.Session?.State);
        Assert.NotNull(turn.Usage);
        Assert.Contains(session.History, t => t.Role == ChatRole.Ai);
    }

    /// <summary>A FINISHED LAUNCH SERVES NOBODY: the test process itself, or anything else that asks, is not inside what is left of it.</summary>
    [OnlyOnFact("a job holds a launch on Windows", "linux", "macos")]
    public void What_is_left_of_a_finished_launch_admits_nobody()
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exit 0");
        ProcessContainment.ReadLeader.Value = p => { p.WaitForExit(); return ProcessTable.Read(p.Id); };
        try
        {
            using var contained = ProcessContainment.Start(psi, requireHeld: true);
            var verdict = Assert.IsAssignableFrom<AttemptContainer>(contained.Container)
                .Contains(new PeerFacts(Environment.ProcessId, ProcessTable.Read(Environment.ProcessId)!.Value.Start, DateTimeOffset.UtcNow));
            Assert.NotEqual(Membership.Member, verdict.Is);
            Assert.Contains("already exited", verdict.Why);
        }
        finally { ProcessContainment.ReadLeader.Value = null; }
    }

    /// <summary>
    /// A LEADER STILL RUNNING THAT CANNOT BE READ IS STILL NOT HELD. Only a program that has finished is let past the read: one that runs on, unproved, is refused as
    /// it always was, because a grant bound to it could be served to nothing the app can name.
    /// </summary>
    [OnlyOnFact("a job holds a launch on Windows", "linux", "macos")]
    public async Task A_running_leader_that_cannot_be_read_is_refused()
    {
        var session = Probe(sleepSeconds: 2);
        var ended = new List<AgentTurnEnded>();
        session.TurnEnded += ended.Add;

        ProcessContainment.ReadLeader.Value = _ => null;
        try { await session.SendAsync("anything"); }
        finally { ProcessContainment.ReadLeader.Value = null; }

        var turn = Assert.Single(ended);
        Assert.Equal(-1, turn.ExitCode);
        Assert.Contains("could not hold", turn.Raw);
        Assert.Null(turn.Usage);
    }
}
