using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TWO FLAGS THAT DECIDE WHETHER A WINDOWS JOB IS A LEASH, asserted where every platform can see
/// them.
///
/// The behaviour itself is `ContainmentTests.A_detached_grandchild_does_not_survive_the_cancel`, and
/// on Windows that is the real measurement. This is here because the flags are a one-word change with
/// no local symptom: `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK` makes every child of a job process start
/// OUTSIDE the job, so the job still exists, still reports healthy, and holds nothing — and on macOS
/// and Linux no test would notice at all. A constant this load-bearing gets an assertion that runs
/// wherever the suite runs.
/// </summary>
public class JobObjectFlagsTests
{
    const uint KillOnJobClose = 0x2000;
    const uint BreakawayOk = 0x0800;
    const uint SilentBreakawayOk = 0x1000;

    [Fact]
    public void The_job_kills_what_is_in_it_when_TradeAgent_closes_it()
    {
        Assert.True((ProcessContainment.JobLimitFlags & KillOnJobClose) != 0,
            "without KILL_ON_JOB_CLOSE the handle in TradeAgent's process is not a leash: the job " +
            "outlives the app and everything in it keeps running");
    }

    [Fact]
    public void Nothing_in_the_job_may_leave_it()
    {
        Assert.True((ProcessContainment.JobLimitFlags & BreakawayOk) == 0,
            "BREAKAWAY_OK lets a child that passes CREATE_BREAKAWAY_FROM_JOB start outside the job");
        Assert.True((ProcessContainment.JobLimitFlags & SilentBreakawayOk) == 0,
            "SILENT_BREAKAWAY_OK lets EVERY child start outside the job, asked for or not");
        Assert.False(ProcessContainment.JobAllowsBreakaway);
    }
}
