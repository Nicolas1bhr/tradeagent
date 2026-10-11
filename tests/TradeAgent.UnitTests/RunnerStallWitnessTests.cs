using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE WITNESS READS ITS CLOCK — proven by stepping one, not by stopping a process.
///
/// <see cref="RunnerStallWitness"/> labels a wall-clock red with how late a dedicated thread's and the
/// pool's 20 ms ticks were across the timed call. That label is only worth printing if a stall really
/// moves it, so this steps a manual clock by 1.5 s while both loops are running and reads at least
/// the step less one period from each. A witness that times its ticks on anything but the clock it
/// was given reads about nothing here and goes red.
///
/// IT ASSERTS NO CEILING. A real machine adds its own lateness to what the step put there, and an
/// upper bound on that would be one more test whose verdict needs the runner to keep time.
/// </summary>
public class RunnerStallWitnessTests
{
    sealed class ManualClock : TimeProvider
    {
        long _now = 1_000_000;
        public override long GetTimestamp() => Interlocked.Read(ref _now);
        public void Step(TimeSpan by) => Interlocked.Add(ref _now, (long)(by.TotalSeconds * TimestampFrequency));
    }

    [Fact]
    public async Task A_step_of_its_clock_is_read_as_lateness_by_both_loops()
    {
        var clock = new ManualClock();
        using var witness = RunnerStallWitness.Start(clock);

        // Both loops ticking before the step, and both ticking again after it: the step is then read
        // by a completed tick of each loop, not only by the tick still in progress when it stops.
        var before = witness.Ticks;
        await Until(() => witness.Ticks.Dedicated > before.Dedicated && witness.Ticks.Pool > before.Pool, "both loops ticked once");
        var atStep = witness.Ticks;
        clock.Step(TimeSpan.FromSeconds(1.5));
        await Until(() => witness.Ticks.Dedicated > atStep.Dedicated && witness.Ticks.Pool > atStep.Pool, "both loops ticked after the step");
        witness.Stop();

        var floor = TimeSpan.FromSeconds(1.5) - RunnerStallWitness.DefaultPeriod;
        Assert.True(witness.DedicatedLate >= floor, $"the dedicated thread read {witness.Clause} across a 1.5 s step of its clock");
        Assert.True(witness.PoolLate >= floor, $"the pool read {witness.Clause} across a 1.5 s step of its clock");
        Assert.Matches(@"^dedicated \d+ ms, pool \d+ ms late$", witness.Clause);
    }

    [Fact]
    public void A_stall_still_under_way_when_the_window_closes_is_counted()
    {
        var clock = new ManualClock();
        using var witness = RunnerStallWitness.Start(clock, TimeSpan.FromHours(1));   // no loop ever completes a tick
        clock.Step(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1.5));
        witness.Stop();
        clock.Step(TimeSpan.FromHours(5));   // after the window: frozen out

        Assert.True(witness.DedicatedLate >= TimeSpan.FromSeconds(1.5), witness.Clause);
        Assert.True(witness.PoolLate >= TimeSpan.FromSeconds(1.5), witness.Clause);
        Assert.True(witness.DedicatedLate < TimeSpan.FromHours(1), $"read past the end of its window: {witness.Clause}");
    }

    /// <summary>
    /// A TICK THAT COMPLETES WHILE A READING IS UNDER WAY CANNOT ERASE THE STALL IT CLOSES. A loop's reading is
    /// its worst lateness and the age of its last tick; read apart, a tick landing between the two reads left
    /// the old worst beside the new "last", and a stall that was measured read as none. For a clock that jumped
    /// 1500 ticks after a tick at 0, EVERY reading at 1500 — before the next tick, after it, or racing it — is at
    /// least 1480 (the jump less one period). Nothing here waits on a wall clock or asserts a ceiling: a fixed
    /// number of races between a ticking thread and a reading one, the smallest reading kept.
    /// </summary>
    [Fact]
    public void A_tick_completing_mid_reading_cannot_erase_the_stall_it_closes()
    {
        const long period = 20, jump = 1_500, trials = 200_000;
        StallLoop? current = null;
        var quit = false;
        var smallest = long.MaxValue;
        var reader = new Thread(() =>
        {
            var least = long.MaxValue;
            for (var spins = 0; !Volatile.Read(ref quit); spins++)
            {
                if ((spins & 1023) == 0) Thread.Yield();   // a one-core runner still lets the ticking thread run
                var loop = Volatile.Read(ref current);
                if (loop is null) continue;
                least = Math.Min(least, loop.Worst(jump, period));
            }
            Volatile.Write(ref smallest, least);
        }) { IsBackground = true };
        reader.Start();

        var rng = new Random(7);
        for (var i = 0; i < trials; i++)
        {
            var loop = new StallLoop();
            loop.Begin(0);
            Volatile.Write(ref current, loop);
            Thread.SpinWait(rng.Next(0, 64));
            loop.Tick(jump, period);
            Thread.SpinWait(rng.Next(0, 16));
        }
        Volatile.Write(ref quit, true);
        reader.Join();

        Assert.True(Volatile.Read(ref smallest) >= jump - period,
            $"a reading racing a tick read {smallest}, below the {jump - period} every reading of a {jump}-tick jump must carry");
    }

    /// <summary>A fixture's patience for the loops to tick — scaled, and bounding nothing under test.</summary>
    static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(TestTime.MarginMillis(5_000));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{what} — not reached");
            await Task.Delay(10);
        }
    }
}
