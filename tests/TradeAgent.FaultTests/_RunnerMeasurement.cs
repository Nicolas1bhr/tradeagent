using TradeAgent.Connectors.Fake;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// TEMPORARY — U-press-stopwatch. Deliberately fails so that its numbers reach the CI log on all
/// three hosted runners: WHICH STEP of the press spends the time when the whole press overruns the
/// deadline it opened for itself, and whether the process was running at all while it did.
///
/// It reproduces `Cancel_all_gives_up_on_a_stalled_platform_inside_the_emergency_budget` exactly —
/// same fixture, same 1.2 s stall, same connector — and reads the step marks out of
/// `TradingGateway.PressStep`. Two independent stall detectors run beside it, because "a step took
/// 1.2 s" and "the process was not scheduled for 1.2 s" are different findings and the total cannot
/// tell them apart: a dedicated thread ticking every 20 ms (the OS descheduling us) and a thread-pool
/// timer ticking every 20 ms (pool starvation or a late timer). GC pause time is read over the same
/// window for the third candidate.
///
/// Deleted once U-press-stopwatch has its answer.
/// </summary>
public class ZPressStopwatchMeasurementTests
{
    /// <summary>The failing test's own constant.</summary>
    const int StalledMs = 1200;

    /// <summary>
    /// Two clocks that keep ticking while the press runs. A gap far above the 20 ms tick is the
    /// process not running — which no per-step total can distinguish from a slow step.
    /// </summary>
    sealed class Stall : IDisposable
    {
        volatile bool _stop;
        readonly Thread _thread;
        public long ThreadGap;
        public long PoolGap;

        public Stall()
        {
            _thread = new Thread(() =>
            {
                var last = Environment.TickCount64;
                while (!_stop)
                {
                    Thread.Sleep(20);
                    var gap = Environment.TickCount64 - last;
                    if (gap > ThreadGap) ThreadGap = gap;
                    last = Environment.TickCount64;
                }
            })
            { IsBackground = true };
            _thread.Start();

            _ = Task.Run(async () =>
            {
                var last = Environment.TickCount64;
                while (!_stop)
                {
                    await Task.Delay(20);
                    var gap = Environment.TickCount64 - last;
                    if (gap > PoolGap) PoolGap = gap;
                    last = Environment.TickCount64;
                }
            });
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(1000);
        }
    }

    static async Task<string> OnePress(int latencyMs, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "z-" + Guid.NewGuid().ToString("N")[..8], TestEnv.Buy());
        c.Inner.Faults.LatencyMs = latencyMs;
        if (wait is not null) c.Inner.Faults.Wait = (d, ct) => wait(d, ct);

        long? deadlineAt = null;
        c.BeforePositionsRead = () => deadlineAt ??= RiskReducingScope.DeadlineAt;

        var steps = new List<string>();
        var start = Environment.TickCount64;
        TradingGateway.PressStep = s => steps.Add($"{s}={Environment.TickCount64 - start}");

        var pause0 = GC.GetTotalPauseDuration();
        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);

        var stall = new Stall();
        long returned;
        try
        {
            await gw.OperatorCancelAllAsync();
            returned = Environment.TickCount64;
        }
        finally
        {
            TradingGateway.PressStep = null;
            stall.Dispose();
        }

        var gcMs = (long)(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds;
        var deadline = deadlineAt is { } d ? d - start : -1;
        var overrun = deadlineAt is { } d2 ? returned - d2 : -1;

        return $"total={returned - start} deadlineAt={deadline} overrun={overrun} " +
               $"gcPause={gcMs} gc={GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2} " +
               $"threadGap={stall.ThreadGap} poolGap={stall.PoolGap} [{string.Join(" ", steps)}]";
    }

    [Fact]
    public async Task Zz_which_step_of_the_press_carries_the_overrun()
    {
        var lines = new List<string>();

        // CONTROL A — a bare 1200 ms timer, five times. How late does THIS runner deliver one?
        for (var i = 0; i < 5; i++)
        {
            var t0 = Environment.TickCount64;
            await Task.Delay(StalledMs);
            lines.Add($"timer1200={Environment.TickCount64 - t0}");
        }

        // CONTROL B — the same press with NO injected latency: the product's own cost, per step,
        // every SQLite write at synchronous=FULL included.
        for (var i = 0; i < 3; i++) lines.Add("PRESS0 " + await OnePress(0));

        // THE MEASUREMENT — the press the failing test makes.
        for (var i = 0; i < 5; i++) lines.Add("PRESS1200 " + await OnePress(StalledMs));

        Assert.Fail("MEASUREMENT || " + string.Join(" || ", lines));
    }
}
