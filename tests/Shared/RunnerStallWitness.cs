using System.Globalization;

namespace TradeAgent.Tests;

/// <summary>
/// WAS THIS PROCESS RUNNING? — the one question a wall-clock red cannot answer about itself.
///
/// A test that asserts "this call took less than six seconds" measures the RUNNER as well as the
/// product, and when it goes red it can only say "this machine took that long". Windows run
/// 37524459410 is the case this exists for: the operator's emergency cancel-all went red at 11.80 s
/// with the sentence "it is still on the full deadline" — a cause nothing in the test had measured —
/// and the survey that followed reproduced that red to the hundredth by stopping the whole test host
/// for 9.8 s from 1.9 s into the call, while the product's own bound held at 2.00 s every time it
/// was allowed to run.
///
/// THE INSTRUMENT is the one U-press-stopwatch used (`build.yml`, the timing category's comment): a
/// short sleep on a DEDICATED THREAD and a short <c>Task.Delay</c> loop on the THREAD POOL, each
/// keeping the worst lateness it saw across the window. A tick that should have come back after
/// 20 ms and came back after 9.8 s says the process — or the pool — did not run for 9.8 s; both
/// ticks on time say it did, and the time went somewhere the product decided.
///
/// IT LABELS A RED AND NEVER DECIDES ONE. Nothing here is subtracted from, excused against or
/// compared with any bound, and it asserts nothing. A late pool can be product code blocking the
/// pool, which is a real defect; a late dedicated thread can be the test host itself starved. Which
/// one is for whoever reads the red, and the red stays a red.
///
/// ITS CLOCK IS A SEAM (<see cref="TimeProvider"/>) so its arithmetic can be proven without
/// stopping a process: <c>RunnerStallWitnessTests</c> steps a manual clock. The sleeps themselves
/// are real; only the reading of how long they took goes through the clock. Each loop measures from
/// the END of its previous tick, never from the start of the current one, so a step of the clock at
/// any instant lands inside exactly one interval; and a reading includes the tick still in progress,
/// so a stall that is still under way when the window closes is counted rather than lost.
/// </summary>
public sealed class RunnerStallWitness : IDisposable
{
    /// <summary>The tick both loops sleep, the one U-press-stopwatch measured with.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMilliseconds(20);

    readonly TimeProvider _clock;
    readonly long _periodTicks;
    readonly Loop _dedicated = new();
    readonly Loop _pool = new();
    readonly CancellationTokenSource _stop = new();
    (TimeSpan Dedicated, TimeSpan Pool)? _final;

    RunnerStallWitness(TimeProvider clock, TimeSpan period)
    {
        _clock = clock;
        _periodTicks = (long)(period.TotalSeconds * clock.TimestampFrequency);
        var now = Now();
        _dedicated.Begin(now);
        _pool.Begin(now);

        // A sleep that Stop can end: waiting on the token's handle is Thread.Sleep with a way out.
        var stopping = _stop.Token;
        var thread = new Thread(() =>
        {
            while (!stopping.WaitHandle.WaitOne(period))
                _dedicated.Tick(Now(), _periodTicks);
        }) { IsBackground = true, Name = "runner-stall-witness" };
        thread.Start();

        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try { await Task.Delay(period, stopping); }
                catch (OperationCanceledException) { return; }
                _pool.Tick(Now(), _periodTicks);
            }
        });
    }

    /// <summary>Starts watching now. <paramref name="clock"/> is the seam; every real use passes nothing.</summary>
    public static RunnerStallWitness Start(TimeProvider? clock = null, TimeSpan? period = null) =>
        new(clock ?? TimeProvider.System, period ?? DefaultPeriod);

    long Now() => _clock.GetTimestamp();

    /// <summary>The worst lateness of the dedicated thread's tick across the window.</summary>
    public TimeSpan DedicatedLate => _final?.Dedicated ?? Late(_dedicated);

    /// <summary>The worst lateness of the pool's tick across the window.</summary>
    public TimeSpan PoolLate => _final?.Pool ?? Late(_pool);

    /// <summary>Both readings as one clause, the shape a red message carries: "dedicated 9807 ms, pool 9810 ms late".</summary>
    public string Clause => string.Create(CultureInfo.InvariantCulture,
        $"dedicated {DedicatedLate.TotalMilliseconds:0} ms, pool {PoolLate.TotalMilliseconds:0} ms late");

    /// <summary>Closes the window: the readings are frozen here, so nothing after it can move them.</summary>
    public void Stop()
    {
        if (_final is not null) return;
        _final = (Late(_dedicated), Late(_pool));
        _stop.Cancel();
    }

    /// <summary>Stops both loops. The token source is not disposed: a loop may still be reading it as it ends.</summary>
    public void Dispose() => Stop();

    TimeSpan Late(Loop loop)
    {
        var worst = Math.Max(loop.WorstTicks, Now() - loop.LastTicks - _periodTicks);
        return worst <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)worst / _clock.TimestampFrequency);
    }

    /// <summary>How many ticks each loop has completed — for the witness's own test, which waits on them.</summary>
    public (long Dedicated, long Pool) Ticks => (Interlocked.Read(ref _dedicated.Count), Interlocked.Read(ref _pool.Count));

    sealed class Loop
    {
        long _last;
        long _worst = long.MinValue;
        public long Count;

        public long LastTicks => Interlocked.Read(ref _last);
        public long WorstTicks => Interlocked.Read(ref _worst);

        public void Begin(long now) => Interlocked.Exchange(ref _last, now);

        public void Tick(long now, long period)
        {
            var late = now - Interlocked.Exchange(ref _last, now) - period;
            long seen;
            while (late > (seen = Interlocked.Read(ref _worst)) &&
                   Interlocked.CompareExchange(ref _worst, late, seen) != seen) { }
            Interlocked.Increment(ref Count);
        }
    }
}
