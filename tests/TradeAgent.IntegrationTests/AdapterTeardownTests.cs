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

        var teardown = new AdapterTeardown(witness);
        var thrown = Assert.Throws<InvalidOperationException>(() => teardown.Stop(
            steps: () => throw new InvalidOperationException("UntrackSecurities failed on the way down")));
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
        var teardown = new AdapterTeardown(witness);
        teardown.Stop(() => ran = true);

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

        var teardown = new AdapterTeardown(witness);
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
        var stop = Task.Run(() => teardown.Stop(() => { }));
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

        var teardown = new AdapterTeardown(witness);
        var wroteDuringTeardown = true;
        teardown.Stop(
            steps: () => wroteDuringTeardown = teardown.Identified("TA-RESTING", "BROKER-9"));

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

        var teardown = new AdapterTeardown(witness);
        teardown.Stop(() => { });

        var ran = false;
        Assert.False(teardown.Record(() => ran = true));
        Assert.False(ran);

        // And a started strategy records again — the instance is reusable, which is the ATAS
        // stop/start cycle inside one process.
        teardown.Started();
        Assert.True(teardown.Record(() => ran = true));
        Assert.True(ran);
    }

    /// <summary>
    /// R8-2. THE WRITE-AHEAD RECORD IS A WITNESS WRITE TOO, AND IT ARRIVES ON A THREAD THAT OUTLIVES
    /// THE TEARDOWN.
    ///
    /// Round 8 drove this class only through `Record`, which is the order-event fan's door — one of
    /// FOUR. `Place`'s write-ahead record, `Place`'s identification and `ClosePosition`'s
    /// write-ahead record all reached the witness with no flag and no lock, and all three run on the
    /// BridgeServer frame loop, which outlives the teardown BY CONSTRUCTION: `DisposeAsync` waits
    /// five seconds for that loop and gives up, `StopBridge` catches its own timeout, and the doc on
    /// that method says the abandoned loop still holds its pipe client until whatever wedged it
    /// returns. So a `Place` in flight lands here AFTER the release.
    ///
    /// Both halves are asserted, because either alone is satisfiable by the wrong fix: the late
    /// write is REFUSED (not written into the witness of a strategy ATAS has taken down), and the
    /// replacement bridge can still take the witness (not raced back out of its hands).
    /// </summary>
    [Fact]
    public void A_write_ahead_record_that_arrives_after_the_teardown_is_refused_not_raced()
    {
        var witness = Session();
        Assert.True(Submit(witness, "TA-OWNED"));

        var teardown = new AdapterTeardown(witness);
        teardown.Stop(() => { });

        Assert.False(teardown.Submitting("TA-LATE", "SIM", "ES", "Buy", 1m, null),
            "a strategy ATAS has already stopped wrote a write-ahead record for a new order");

        Assert.True(Submit(Session(), "TA-REPLACEMENT"),
            "a strategy ATAS has already stopped is refusing the witness to the live one");
    }

    /// <summary>
    /// AND THE SAME FOR THE IDENTIFICATION INSIDE `Place`, which is the fourth site. It is a
    /// separate test rather than a second assertion because the two calls fail differently in the
    /// witness — `Submitting` writes a new record and `Identified` updates one that already exists,
    /// and round 6's "look before leasing" means only the second is narrowed by anything.
    /// </summary>
    [Fact]
    public void An_identification_that_arrives_after_the_teardown_is_refused_not_raced()
    {
        var witness = Session();
        Assert.True(Submit(witness, "TA-RESTING"));

        var teardown = new AdapterTeardown(witness);
        teardown.Stop(() => { });

        Assert.False(teardown.Identified("TA-RESTING", "BROKER-9"),
            "a strategy ATAS has already stopped recorded a broker id");

        Assert.True(Submit(Session(), "TA-REPLACEMENT"),
            "a strategy ATAS has already stopped is refusing the witness to the live one");
    }

    /// <summary>
    /// AND WHILE THE STRATEGY IS RUNNING BOTH DOORS STILL WORK — the other direction, without which
    /// the two tests above are satisfied by a guard that refuses everything.
    /// </summary>
    [Fact]
    public void A_running_strategy_records_through_both_doors()
    {
        var teardown = new AdapterTeardown(Session());

        Assert.True(teardown.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));
        Assert.True(teardown.Identified("TA-LIVE", "BROKER-1"));
        Assert.Null(teardown.Trouble);

        teardown.Stop(() => { });

        var reader = Session();
        var record = reader.PriorSession("TA-LIVE");
        Assert.NotNull(record);
        Assert.Equal("BROKER-1", record.BrokerOrderId);
    }

    /// <summary>
    /// R8-4. THE CHECK AND THE WRITE ARE ONE ACT, AND A HALF-MOVED GUARD MUST NOT SURVIVE.
    ///
    /// PRIOR 21's rule is "the check and the write are ONE act, under the lock the release takes" —
    /// but no test distinguished that from "the write is under the lock". A mutant that moved only
    /// the CHECK out (`if (_stopped) return false;` above the lock, the write still inside it)
    /// survived every case above, and it is a real weakening: the fan reads the flag, the stop
    /// completes and releases, and the fan then takes the lock and re-leases.
    ///
    /// It is not deterministic from one round — the losing interleaving needs the stop to complete
    /// between the check and the lock — so the probe is repeated, with a fresh directory, a fresh
    /// witness and a real lease each round. Forty rounds is what the verifier's own harness used and
    /// what it takes to make the half-moved guard fail reliably; each round is a few milliseconds of
    /// file IO.
    /// </summary>
    [Fact]
    public async Task A_stop_that_lands_mid_write_never_leaves_the_lease_held()
    {
        for (var round = 0; round < 40; round++)
        {
            var dir = Path.Combine(_dir, "r" + round);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "coid-witness.json");

            var witness = new CoidWitness(path);
            Assert.True(witness.Submitting("TA-RESTING", "SIM", "ES", "Buy", 1m, null));

            var teardown = new AdapterTeardown(witness);
            using var entered = new ManualResetEventSlim();

            var fan = Task.Run(() => teardown.Record(() =>
            {
                entered.Set();
                witness.Identified("TA-RESTING", "BROKER-9");
            }));

            // The stop lands while the write is in flight, and its own steps throw — the terminal
            // path, so the release has to come from the finally rather than from reaching the end.
            var stop = Task.Run(() =>
            {
                entered.Wait(5_000);
                try { teardown.Stop(() => throw new InvalidOperationException("UntrackSecurities")); }
                catch (InvalidOperationException) { }
            });

            await fan.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(new CoidWitness(path).Submitting("TA-REPLACEMENT", "SIM", "ES", "Buy", 1m, null),
                $"round {round}: a stopped strategy still holds the witness against the live one");
        }
    }
}
