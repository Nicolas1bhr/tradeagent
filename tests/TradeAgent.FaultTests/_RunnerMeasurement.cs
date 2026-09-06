using TradeAgent.Connectors.Fake;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// TEMPORARY — U-press-win-3. Deliberately fails so that its numbers reach the CI log on all three
/// hosted runners: WHERE the 2813 ms went that `Close_all_gives_up_on_a_stalled_platform_inside_the_
/// emergency_budget` reported past its own deadline on windows-latest (run 34043185411, both
/// attempts), and whether the process was running while it went there.
///
/// It reproduces BOTH failing tests exactly — same fixture, same 1.2 s stall, same connector — and
/// reads the step marks out of `TradingGateway.PressStep`. Three independent instruments run beside
/// them, because "a step took 2.8 s", "the process was not scheduled for 2.8 s" and "this runner's
/// disk is slow today" are different findings and no total tells them apart:
///   - a dedicated thread ticking every 20 ms (the OS descheduling us),
///   - a thread-pool timer ticking every 20 ms (pool starvation or a late timer),
///   - GC pause time over the same window,
///   - and a bare-SQLite probe: N one-row commits at `synchronous=FULL` on a database of this
///     suite's own making, timed immediately before and after the press.
///
/// Deleted once U-press-win-3 has its answer.
/// </summary>
[Trait("Category", "Timing")]
public class ZPressWin3MeasurementTests
{
    /// <summary>The failing tests' own constant.</summary>
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

    /// <summary>
    /// What one durable commit costs on THIS runner's disk right now, in the same shape the press's
    /// own rows are written in: one `Write` transaction each, `synchronous=FULL`, WAL.
    /// </summary>
    static string DiskProbe(Database db, int n)
    {
        var each = new List<long>(n);
        for (var i = 0; i < n; i++)
        {
            var t = Environment.TickCount64;
            db.SetKv($"probe-{Guid.NewGuid():n}", "x");
            each.Add(Environment.TickCount64 - t);
        }
        each.Sort();
        return $"n={n} min={each[0]} med={each[n / 2]} max={each[n - 1]} total={each.Sum()}";
    }

    static async Task<string> OnePress(bool closeAll, int latencyMs)
    {
        var (gw, c, db) = closeAll
            ? await Recovery.Ready()
            : await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        var id = "z-" + Guid.NewGuid().ToString("N")[..8];
        await gw.PlaceAsync(AgentContext.Operator, id, closeAll ? TestEnv.Buy("ES", 2m) : TestEnv.Buy());
        c.Inner.Faults.LatencyMs = latencyMs;

        long? deadlineAt = null;
        c.BeforePositionsRead = () => deadlineAt ??= RiskReducingScope.DeadlineAt;

        var before = DiskProbe(db, 10);

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
            if (closeAll) await gw.OperatorCloseAllAsync();
            else await gw.OperatorCancelAllAsync();
            returned = Environment.TickCount64;
        }
        finally
        {
            TradingGateway.PressStep = null;
            stall.Dispose();
        }

        var after = DiskProbe(db, 10);
        var gcMs = (long)(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds;
        var deadline = deadlineAt is { } d ? d - start : -1;
        var overrun = deadlineAt is { } d2 ? returned - d2 : -1;

        return $"total={returned - start} deadlineAt={deadline} overrun={overrun} " +
               $"gcPause={gcMs} gc={GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2} " +
               $"threadGap={stall.ThreadGap} poolGap={stall.PoolGap} " +
               $"diskBefore[{before}] diskAfter[{after}] [{string.Join(" ", steps)}]";
    }

    [Fact]
    public async Task Zz_where_the_windows_press_overrun_goes()
    {
        var lines = new List<string>();

        // CONTROL A — a bare 1200 ms timer, five times. How late does THIS runner deliver one?
        for (var i = 0; i < 5; i++)
        {
            var t0 = Environment.TickCount64;
            await Task.Delay(StalledMs);
            lines.Add($"timer1200={Environment.TickCount64 - t0}");
        }

        // CONTROL B — the same presses with NO injected latency: the product's own cost, per step,
        // every SQLite write at synchronous=FULL included.
        for (var i = 0; i < 2; i++) lines.Add("CLOSE0 " + await OnePress(true, 0));
        for (var i = 0; i < 2; i++) lines.Add("CANCEL0 " + await OnePress(false, 0));

        // THE MEASUREMENT — the two presses the failing tests make.
        for (var i = 0; i < 4; i++) lines.Add("CLOSE1200 " + await OnePress(true, StalledMs));
        for (var i = 0; i < 4; i++) lines.Add("CANCEL1200 " + await OnePress(false, StalledMs));

        Assert.Fail("MEASUREMENT || " + string.Join(" || ", lines));
    }
}
