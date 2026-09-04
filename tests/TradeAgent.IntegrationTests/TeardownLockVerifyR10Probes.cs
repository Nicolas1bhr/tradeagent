using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUND-10 VERIFIER, leg [2]. Target 4 — the state machine, and specifically the builder's
/// SURVIVOR: mutant MR10-4d, "the `Running → Stopping` transition leaves the lock", recorded as
/// "NOT verified to be load-bearing". These are probes, not fixes.
/// </summary>
public class TeardownLockVerifyR10Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "r10td-" + Guid.NewGuid().ToString("n")[..8]);

    public TeardownLockVerifyR10Probes() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    CoidWitness Session() => new(Path.Combine(_dir, "coid-witness.json"));

    static bool Submit(CoidWitness w, string id) => w.Submitting(id, "SIM", "ES", "Buy", 1m, null);

    /// <summary>
    /// THE INTERLEAVING THAT NEEDS THE LOCK ON `Running → Stopping`, AND IT IS OBSERVABLE.
    ///
    /// The builder records this transition's lock as unpinned because "the states that would
    /// separate the two are ones where a `Record` is already inside the lock, and such a write has
    /// already passed its check and completes under both". That is true of the WRITE. It is not true
    /// of the ORDER: with the lock, `Stop` cannot enter STOPPING — and therefore cannot start the
    /// teardown steps, which unsubscribe the strategy from ATAS — until the write in flight has
    /// finished. Without it, the steps run over a witness write that is still going.
    ///
    /// One writer parked inside `Record`, one stopper arriving behind it, and the order the two
    /// complete in is asserted. Deterministic: no sleep decides the outcome, only the lock.
    /// </summary>
    [Fact]
    public async Task The_teardown_steps_do_not_start_while_a_write_is_still_inside_the_guard()
    {
        var teardown = new AdapterTeardown(Session());
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();

        using var holding = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();

        var writer = Task.Run(() => teardown.Record(() =>
        {
            holding.Set();
            letGo.Wait(5_000);
            order.Enqueue("the write finished");
        }));
        Assert.True(holding.Wait(5_000));

        var stopper = Task.Run(() => teardown.Stop(steps: () => order.Enqueue("the teardown steps ran")));
        Thread.Sleep(300);                      // long enough for an unlocked transition to get past

        letGo.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        await stopper.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["the write finished", "the teardown steps ran"], order.ToArray());
    }

    /// <summary>
    /// AND THE SECOND ONE THE SAME LOCK DECIDES: a start arriving while the teardown's steps are
    /// running must find STOPPING. The transition is what publishes that state to another thread;
    /// with it outside the lock there is no release to pair with `Started()`'s acquire.
    /// </summary>
    [Fact]
    public async Task A_start_on_another_thread_during_the_steps_is_refused()
    {
        var teardown = new AdapterTeardown(Session());
        using var inSteps = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();

        bool? startedFromOutside = null;
        var stopper = Task.Run(() => teardown.Stop(steps: () => { inSteps.Set(); finish.Wait(5_000); }));
        Assert.True(inSteps.Wait(5_000));

        var starter = Task.Run(() => startedFromOutside = teardown.Started());
        await starter.WaitAsync(TimeSpan.FromSeconds(5));

        finish.Set();
        await stopper.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(startedFromOutside, "a start on another thread reopened the door mid-teardown");
        Assert.True(teardown.Stopped);
        Assert.False(teardown.Submitting("TA-AFTER", "SIM", "ES", "Buy", 1m, null));
    }

    /// <summary>
    /// THE TWO-THREAD RACE, 40 ROUNDS, carried from round 8 and re-run at this sha: a writer and a
    /// stopper on real threads, and no write may land after the lease is released.
    /// </summary>
    [Fact]
    public void The_two_thread_race_never_lets_a_write_land_after_the_release()
    {
        for (var round = 0; round < 40; round++)
        {
            var dir = Path.Combine(_dir, "round-" + round);
            Directory.CreateDirectory(dir);
            var witness = new CoidWitness(Path.Combine(dir, "coid-witness.json"));
            var teardown = new AdapterTeardown(witness);
            Assert.True(Submit(witness, "TA-RESTING"));

            bool? recorded = null;
            var writer = new Thread(() => recorded = teardown.Submitting("TA-RACE", "SIM", "ES", "Buy", 1m, null));
            var stopper = new Thread(() => teardown.Stop(() => { }));
            writer.Start(); stopper.Start();
            Assert.True(writer.Join(5_000)); Assert.True(stopper.Join(5_000));

            // Whatever the order, the two must agree: a recorded write means the state was RUNNING
            // when it happened, and a replacement bridge must always be able to take the file.
            var replacement = new CoidWitness(Path.Combine(dir, "coid-witness.json"));
            Assert.True(Submit(replacement, "TA-REPLACEMENT-" + round));
            replacement.Dispose();
            Assert.NotNull(recorded);
        }
    }
}
