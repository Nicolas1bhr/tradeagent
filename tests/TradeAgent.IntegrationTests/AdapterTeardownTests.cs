using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE TWO WAYS A STOPPED STRATEGY KEPT THE WITNESS, both of which live in a file this machine
/// cannot compile.
///
/// <c>AtasStrategyAdapter.cs</c> is <c>&lt;Compile Remove&gt;</c>d unless the build is run against a
/// real ATAS install, so its teardown has never had an executable test anywhere but
/// <c>tools/atas-gate</c> on the Windows box. The two defects here need no ATAS type: one is a
/// missing <c>finally</c>, the other is a check taken outside the lock that the disposal takes. So
/// the rule was lifted into <see cref="AdapterTeardown"/> and is driven here against a REAL
/// <see cref="CoidWitness"/> and a real lease on the real filesystem — the lease is the thing at
/// stake, and a double for it would prove nothing about it.
///
/// What is NOT proven here is that the adapter calls this class correctly; that is a compile away
/// and the compile only exists on the box.
/// </summary>
public class AdapterTeardownTests : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "teardown-" + Guid.NewGuid().ToString("n")[..8]);

    public AdapterTeardownTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    CoidWitness Session() => new(Path.Combine(_dir, "coid-witness.json"));

    static bool Submit(CoidWitness w, string id) => w.Submitting(id, "SIM", "ES", "Buy", 1m, null);

    /// <summary>
    /// F26 = R2. AN EXCEPTION ANYWHERE IN TEARDOWN MUST NOT COST THE NEXT BRIDGE ITS WITNESS.
    ///
    /// The adapter's teardown ran `UntrackSecurities()` and then disposed the witness, as two
    /// statements with nothing between them. `UntrackSecurities` unsubscribes from ATAS security
    /// events, which is a call into the platform on a path taken while the platform is taking the
    /// strategy down — the one moment it is most likely to answer with an exception. It throws, the
    /// disposal never runs, and the lease survives a TERMINAL path: this instance is stopped, will
    /// never write again, and holds the witness against every bridge started afterwards in the same
    /// ATAS process until the process itself dies.
    ///
    /// Both directions: the exception still reaches the caller (Guard is what swallows it, and a
    /// teardown that silently ate its own failures would be worse than the bug), and the witness is
    /// released anyway.
    /// </summary>
    [Fact]
    public void An_exception_in_teardown_does_not_keep_the_witness()
    {
        var witness = Session();
        Assert.True(Submit(witness, "TA-OWNED"));

        var teardown = new AdapterTeardown();
        var thrown = Assert.Throws<InvalidOperationException>(() => teardown.Stop(
            steps: () => throw new InvalidOperationException("UntrackSecurities failed on the way down"),
            releaseWitness: witness.Dispose));
        Assert.Contains("UntrackSecurities", thrown.Message);

        // The strategy is down. A bridge started after it must be able to take the witness.
        Assert.True(Submit(Session(), "TA-REPLACEMENT"),
            "the lease survived a terminal path — a stopped strategy is still refusing the witness to the live one");
    }

    /// <summary>
    /// And the ordinary way down still works, so the assertion above is about the exception and not
    /// about the release being unconditional in some way that never ran the steps.
    /// </summary>
    [Fact]
    public void A_teardown_that_does_not_throw_runs_its_steps_and_releases()
    {
        var witness = Session();
        Assert.True(Submit(witness, "TA-OWNED"));

        var ran = false;
        var teardown = new AdapterTeardown();
        teardown.Stop(() => ran = true, witness.Dispose);

        Assert.True(ran);
        Assert.True(teardown.Stopped);
        Assert.True(Submit(Session(), "TA-REPLACEMENT"));
    }

    /// <summary>
    /// PRIOR 21. THE STOPPED CHECK AND THE DISPOSAL MUST BE ONE ACT.
    ///
    /// The fan asked `if (!_stopped)` and then reached for the witness, and the teardown set that
    /// flag and then disposed the lease — two unsynchronised pairs. So the interleaving below was
    /// open: the fan reads the flag while the strategy is still running, ATAS stops the strategy,
    /// the lease is released, and the fan — already past its check — calls `Identified`, which
    /// leases the file again for a strategy that no longer exists. `Identified` looks before it
    /// leases (round 6), so this needs a record of the RUNNING session with no broker id yet, which
    /// is precisely the order the fan is about to identify.
    ///
    /// The fix is not a second flag read. It is that the check and the write happen under the lock
    /// the disposal takes, so one of the two orders is always chosen and neither can be half done.
    /// </summary>
    [Fact]
    public async Task A_write_that_began_before_the_stop_cannot_take_the_lease_back_after_it()
    {
        var witness = Session();
        Assert.True(Submit(witness, "TA-RESTING"));
        witness.Dispose();                     // the lease is not held between writes in this test

        var teardown = new AdapterTeardown();
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();

        // The order-event fan, already past the check, about to write the broker id.
        var fan = Task.Run(() => teardown.Record(() =>
        {
            entered.Set();
            released.Wait(5_000);
            witness.Identified("TA-RESTING", "BROKER-9");
        }));

        Assert.True(entered.Wait(5_000));

        // ATAS stops the strategy underneath it.
        var stop = Task.Run(() => teardown.Stop(() => { }, witness.Dispose));
        await Task.Delay(250);                 // long enough for an unsynchronised stop to finish
        released.Set();
        await fan.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        // Whatever order the two took, the witness is not owned by a stopped strategy afterwards.
        Assert.True(Submit(Session(), "TA-REPLACEMENT"),
            "a stopped strategy took the lease back after its own teardown released it");
    }

    /// <summary>
    /// AND THE FLAG IS RAISED BEFORE THE TEARDOWN STEPS, NOT BESIDE THE RELEASE.
    ///
    /// A mutant that moved `_stopped = true` down beside the release survived everything above: the
    /// lease still ends up free, so nothing about PRIOR 21's harm shows. What it changes is the
    /// round-6 rule — a stopped strategy records nothing — for the whole width of the teardown, and
    /// that width is not small: it disposes the bridge under a deadline and unsubscribes from ATAS.
    /// A fan callback landing in there would write into the witness of a strategy ATAS has already
    /// taken down. Recorded as a test rather than left to the mutant's silence.
    /// </summary>
    [Fact]
    public void A_write_that_arrives_while_the_teardown_is_running_does_not_run()
    {
        var witness = Session();
        Assert.True(Submit(witness, "TA-RESTING"));

        var teardown = new AdapterTeardown();
        var wroteDuringTeardown = true;
        teardown.Stop(
            steps: () => wroteDuringTeardown =
                teardown.Record(() => witness.Identified("TA-RESTING", "BROKER-9")),
            releaseWitness: witness.Dispose);

        Assert.False(wroteDuringTeardown,
            "the strategy was already being taken down and the fan still wrote to its witness");
    }

    /// <summary>
    /// The other direction of the same lock, and the reason the fix is not "check the flag twice":
    /// once the teardown has run, a fan callback that arrives afterwards must not write at all.
    /// </summary>
    [Fact]
    public void A_write_that_arrives_after_the_stop_does_not_run()
    {
        var witness = Session();
        Assert.True(Submit(witness, "TA-RESTING"));

        var teardown = new AdapterTeardown();
        teardown.Stop(() => { }, witness.Dispose);

        var ran = false;
        Assert.False(teardown.Record(() => ran = true));
        Assert.False(ran);

        // And a started strategy records again — the instance is reusable, which is the ATAS
        // stop/start cycle inside one process.
        teardown.Started();
        Assert.True(teardown.Record(() => ran = true));
        Assert.True(ran);
    }
}
