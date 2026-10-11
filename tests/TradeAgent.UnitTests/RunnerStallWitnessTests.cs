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
