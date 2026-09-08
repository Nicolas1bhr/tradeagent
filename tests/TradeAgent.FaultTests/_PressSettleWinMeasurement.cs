using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// TEMPORARY — U-press-settle-win. Deliberately fails so that its numbers reach the CI log on all
/// three hosted runners: WHERE the press's own leg goes in
/// <c>PressSettlesAnUnknownCloseTests.A_press_cancels_the_unknown_close_before_it_closes_and_the_
/// book_ends_flat</c>, which ended `ES 2` on windows-latest at the U-archive-win merge (run
/// 34187380076) and passes on the other two runners, on the dev Mac, and on windows at the unit's
/// own merge sha.
///
/// It reproduces that test's shape exactly — same `Unresolved.WithALostClose` fixture, the same
/// close-all press, nothing in between — and prints, per press, the budget left of the two-second
/// emergency deadline at every step the press takes:
///   enter, captured, composite, leg0-enter, settle-enter, settle-read, settle-cancel-sent,
///   settle-cancel-answered, settle-written, leg0-settled, leg0-reread, leg0-row, leg0-close-sent,
///   leg0-close-answered — and the branch that refused it where it did not get that far.
/// Beside them: the answer the press gave, the book at the broker, the position at the end, what
/// one bare durable commit costs on this runner's disk right now, and the two tick clocks and the
/// GC pause that say whether the process was running while it went.
///
/// It carries NO `Timing` trait deliberately, so it runs in the same `Category!=Timing` step, in
/// the same test host and under the same load, as the test that went red.
///
/// Deleted once U-press-settle-win has its answer.
/// </summary>
public class ZPressSettleWinMeasurementTests
{
    /// <summary>How many presses the harness makes; each is one env, like the failing test.</summary>
    const int Presses = 8;

    /// <summary>
    /// Two clocks that keep ticking while a press runs. A gap far above the 20 ms tick is the
    /// process not running, which no step total can tell from a slow step.
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
    /// What one durable commit costs on THIS runner's disk right now, in the shape the press's own
    /// composite, settle and leg rows are written in: one `Write` transaction each,
    /// `synchronous=FULL`, WAL.
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
        return $"n={n} min={each[0]} med={each[n / 2]} p95={each[(int)(n * 0.95)]} max={each[n - 1]} total={each.Sum()}";
    }

    static async Task<string> OnePress(int index)
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose();
        using var _1 = db;

        var disk = DiskProbe(db, 20);
        var pause0 = GC.GetTotalPauseDuration();
        using var stall = new Stall();

        var steps = new List<string>();
        TradingGateway.PressMark = m => { lock (steps) steps.Add(m); };

        var before = $"{await Unresolved.Pos(c)} — {Unresolved.Book(c)}";
        var t0 = Environment.TickCount64;
        var press = await gw.OperatorCloseAllAsync();
        var pressMs = Environment.TickCount64 - t0;
        TradingGateway.PressMark = null;

        // The same fill the failing test makes: a real book fills a resting market order, and this
        // is the step that reverses the position when the press left it working.
        var resting = c.Inner.Broker.Orders.FirstOrDefault(o => o.ClientOrderId == lost.ClientOrderId);
        if (resting is not null) c.Inner.Broker.FillWorking(resting.ConnectorOrderId);

        string taken;
        lock (steps) taken = string.Join(" ", steps);

        var gcMs = (long)(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds;
        var line =
            $"P{index} disk[{disk}] gcPause={gcMs} threadGap={stall.ThreadGap} poolGap={stall.PoolGap} " +
            $"before=[{before}] pressMs={pressMs} targets={press.Targets.Count} " +
            $"states=[{string.Join(",", press.Targets.Select(t => $"{t.Target}:{t.State}"))}] " +
            $"lost={gw.GetRequest(lost.RequestId)?.State.ToString() ?? "-"} " +
            $"book=[{Unresolved.Book(c)}] pos=[{await Unresolved.Pos(c)}] " +
            $"sellsFilled={c.Inner.Broker.Orders.Count(o => o.Side == OrderSide.Sell && o.State == ExecutionState.FILLED)} " +
            $"budget[{taken}] summary=\"{press.Summary}\"";
        await gw.DisposeAsync();
        return line;
    }

    /// <summary>
    /// THE CONTROL. The settle's cancel is answered correctly — the broker really cancels the
    /// resting close — and the caller is then held until the operation's own deadline has gone,
    /// which is what the two SQLite commits <c>SettleTheUnresolved</c> makes at
    /// <c>synchronous=FULL</c> immediately after that cancel do to this method on a runner whose
    /// disk stalls (U-press-win-3: ten bare one-row commits measured 16-2234 ms on windows-latest).
    /// Nothing else is changed: same fixture, same press, same assertions available afterwards.
    /// </summary>
    static async Task<string> TheControl()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose();
        using var _1 = db;

        c.OnCancelled = () =>
        {
            if (RiskReducingScope.DeadlineAt is { } d)
                while (Environment.TickCount64 <= d + 20) Thread.Sleep(5);
        };

        var steps = new List<string>();
        TradingGateway.PressMark = m => { lock (steps) steps.Add(m); };
        var press = await gw.OperatorCloseAllAsync();
        TradingGateway.PressMark = null;
        c.OnCancelled = null;

        var resting = c.Inner.Broker.Orders.FirstOrDefault(o => o.ClientOrderId == lost.ClientOrderId);
        if (resting is not null && resting.State == ExecutionState.WORKING)
            c.Inner.Broker.FillWorking(resting.ConnectorOrderId);

        string taken;
        lock (steps) taken = string.Join(" ", steps);

        var line =
            $"CONTROL targets={press.Targets.Count} " +
            $"states=[{string.Join(",", press.Targets.Select(t => $"{t.Target}:{t.State}"))}] " +
            $"lost={gw.GetRequest(lost.RequestId)?.State.ToString() ?? "-"} " +
            $"book=[{Unresolved.Book(c)}] pos=[{await Unresolved.Pos(c)}] " +
            $"sellsFilled={c.Inner.Broker.Orders.Count(o => o.Side == OrderSide.Sell && o.State == ExecutionState.FILLED)} " +
            $"budget[{taken}] summary=\"{press.Summary}\"";
        await gw.DisposeAsync();
        return line;
    }

    /// <summary>
    /// THE SWEEP OF THE CLASS'S SIBLINGS, under the same marks rather than by assumption. A 1 ms
    /// declared latency makes the simulator take its own wait on EVERY wire call, and the wait
    /// samples the ambient <see cref="RiskReducingScope"/> from inside the connector — which is the
    /// only place the answer is not a guess, because the scope is an `AsyncLocal` that flows INTO
    /// the call and is invisible from the test's own frame.
    ///
    /// `deadlines=[none]` means no operation clock exists on that path at all, so no commit can run
    /// into one and the fixture's verdict cannot depend on the runner's disk. A number is the budget
    /// the wire actually saw.
    /// </summary>
    static async Task<string> SiblingSweep(string name, TimeSpan? budget,
        Func<TradingGateway, RecoveryConnector, ExecutionRequest, Task> body,
        Func<TimeSpan?, Task<(TradingGateway, RecoveryConnector, Database, ExecutionRequest)>>? make = null)
    {
        var (gw, c, db, lost) = await (make ?? (b => Unresolved.WithALostClose(budget: b)))(budget);
        using var _1 = db;

        var seen = new List<string>();
        c.Inner.Faults.LatencyMs = 1;
        c.Inner.Faults.Wait = (d, ct) =>
        {
            lock (seen) seen.Add(RiskReducingScope.DeadlineAt is { } x ? $"{x - Environment.TickCount64}" : "none");
            return Task.Delay(d, ct);
        };

        string outcome;
        try { await body(gw, c, lost); outcome = "ran"; }
        catch (Exception ex) { outcome = ex.GetType().Name; }

        string calls;
        lock (seen) calls = $"n={seen.Count} deadlines=[{string.Join(",", seen.Distinct())}]";
        await gw.DisposeAsync();
        return $"SWEEP {name} {outcome} {calls}";
    }

    /// <summary>
    /// `An_unknown_order_on_the_other_side_does_not_hold_the_press_up`'s own fixture, which is not
    /// <see cref="Unresolved.WithALostClose"/>: the UNKNOWN order is a BUY under a long, so the
    /// press's settle finds no reducer to settle and goes straight to its leg.
    /// </summary>
    static async Task<(TradingGateway, RecoveryConnector, Database, ExecutionRequest)> TheOtherSide(TimeSpan? budget)
    {
        var (gw, c, db) = await Recovery.Ready(emergencyBudget: budget);
        await gw.PlaceAsync(new AgentContext("ai"), "sw-os-open", TestEnv.Buy("ES", 2m));
        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;
        c.ThrowAfterPlace = new ConnectorTransportException("connection lost after the order was accepted");
        var buy = await gw.PlaceAsync(new AgentContext("ai"), "sw-os-buy",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        c.ThrowAfterPlace = null;
        c.Inner.Faults.Fill = FillBehaviour.FillImmediately;
        return (gw, c, db, buy);
    }

    static async Task<List<string>> TheSiblings()
    {
        var lines = new List<string>();

        // The three presses now on the file's own budget: the wire should see ~20 s, not 2.
        lines.Add(await SiblingSweep("press-flat-book", Unresolved.PressBudget,
            async (gw, _, _) => await gw.OperatorCloseAllAsync()));
        lines.Add(await SiblingSweep("press-hidden-history", Unresolved.PressBudget,
            async (gw, c, _) => { c.Inner.Faults.HideOrderHistory = true; await gw.OperatorCloseAllAsync(); },
            make: b => Unresolved.WithALostClose(alsoOpen: "NQ", budget: b)));
        lines.Add(await SiblingSweep("press-other-side", Unresolved.PressBudget,
            async (gw, _, _) => await gw.OperatorCloseAllAsync(), make: TheOtherSide));

        // The fixture that IS about the budget keeps the simulator's two seconds.
        lines.Add(await SiblingSweep("press-deadline-runs-out", null,
            async (gw, c, _) => { c.Inner.Faults.LatencyMs = 1200; await gw.OperatorCloseAllAsync(); }));

        // The four in AgentCloseOverAnUnknownCloseTests. No press, so no scope, so no clock.
        lines.Add(await SiblingSweep("agent-second-close", null, async (gw, _, lost) =>
        {
            await Unresolved.ResolveWithoutAnOutcome(gw, lost.RequestId);
            await gw.CloseAsync(new AgentContext("ai"), "sw-second", "ES");
        }));
        lines.Add(await SiblingSweep("agent-reduce", null, async (gw, _, lost) =>
        {
            await Unresolved.ResolveWithoutAnOutcome(gw, lost.RequestId);
            await gw.PlaceAsync(new AgentContext("ai"), "sw-reduce",
                new PlaceIntent("ES", OrderSide.Sell, OrderType.Market, 1m, null, null, TimeInForce.Day, null));
        }));
        lines.Add(await SiblingSweep("agent-open-and-other-instrument", null, async (gw, _, lost) =>
        {
            await Unresolved.ResolveWithoutAnOutcome(gw, lost.RequestId);
            await gw.PlaceAsync(new AgentContext("ai"), "sw-add", TestEnv.Buy("ES", 1m));
            await gw.PlaceAsync(new AgentContext("ai"), "sw-nq", TestEnv.Buy("NQ", 1m));
            await gw.CloseAsync(new AgentContext("ai"), "sw-nq-close", "NQ");
        }));
        lines.Add(await SiblingSweep("agent-outcome-lifts-refusal", null, async (gw, c, lost) =>
        {
            await Unresolved.ResolveWithoutAnOutcome(gw, lost.RequestId);
            c.Inner.Broker.Cancel(c.Inner.Broker.Orders.First(o => o.ClientOrderId == lost.ClientOrderId).ConnectorOrderId);
            gw.ForceResolve(lost.RequestId, ExecutionState.CANCELLED, "checked in ATAS: it never worked");
            await gw.RefreshHealthAsync();
            await gw.CloseAsync(new AgentContext("ai"), "sw-after", "ES");
        }));
        return lines;
    }

    [Fact]
    public async Task Zz_where_the_presss_own_leg_goes()
    {
        var lines = new List<string> { await TheControl() };
        for (var i = 0; i < Presses; i++) lines.Add(await OnePress(i));
        lines.AddRange(await TheSiblings());
        Assert.Fail("MEASUREMENT || " + string.Join(" |||| ", lines));
    }
}
