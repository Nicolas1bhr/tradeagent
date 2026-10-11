using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// TEMPORARY (U-red-says-why item 3), reverted before the report: red on the timing category's FIRST attempt only,
/// so one branch CI run can show the retry re-running this test alone. The mark lives in the job's temp directory,
/// keyed by the run, so the second attempt in the same job finds it and passes.
/// </summary>
[Trait("Category", "Timing")]
public class TemporaryRetryProbeTests
{
    [Fact]
    public void Red_on_the_timing_categorys_first_attempt_only()
    {
        var mark = Path.Combine(Path.GetTempPath(), "ta-retry-probe-" + (Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local"));
        if (File.Exists(mark)) return;
        File.WriteAllText(mark, "attempt 1 was here");
        Assert.Fail("TEMPORARY: red on the first attempt only, so the retry can be watched re-running this test alone");
    }
}
