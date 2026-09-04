using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// VERIFIER ROUND 8, TARGET 10 — the AdapterTeardown extraction, and whether the CLASS it names is
/// actually closed.
///
/// The round-8 record states the class as: "nothing made 'this strategy is down' and 'this strategy
/// no longer owns the witness' the same fact", and lists three call sites into `AdapterTeardown`
/// (`Started()` in `StartBridge`, `Stop(...)` in `StopBridge`, `Record(...)` in `OnOrderPayload`).
///
/// `grep -n "_witness" AtasStrategyAdapter.cs` finds FOUR write sites, not one:
///
///   :2055  `_teardown.Record(() => _witness.Identified(o.Comment, o.Id))`   — guarded
///   :1409  `_witness.Submitting(...)`  in `Place`                            — NOT guarded
///   :1562  `_witness.Identified(cmd.ClientOrderId, order.Id)` in `Place`     — NOT guarded
///   :1824  `_witness.Submitting(...)`  in `ClosePosition`                    — NOT guarded
///
/// The three unguarded ones run on the BridgeServer frame loop, and that loop can outlive the
/// teardown by construction: `BridgeServer.DisposeAsync` waits 5 s and then gives up
/// (`BridgeServer.cs:450` — "would not let go: either way we are done"), and `StopBridge` wraps that
/// wait in its own `StopTimeout` and catches the timeout (`AtasStrategyAdapter.cs:499-502`), whose
/// own doc says the abandoned loop "still holds its pipe client until whatever wedged it returns".
/// </summary>
public class TeardownReachVerifyR8Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "tdr8-" + Guid.NewGuid().ToString("n")[..8]);
    public TeardownReachVerifyR8Probes() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }
    string File_ => Path.Combine(_dir, "coid-witness.json");

    /// <summary>
    /// THE GUARDED SITE — the control. `Record` refuses, and the replacement adapter gets the witness.
    /// </summary>
    [Fact]
    public void CONTROL_the_guarded_write_site_cannot_take_the_lease_back()
    {
        var witness = new CoidWitness(File_);
        var teardown = new AdapterTeardown(witness);
        teardown.Started();
        Assert.True(witness.Submitting("TA-1", "SIM", "ES", "Buy", 1m, null));

        teardown.Stop(steps: () => { });

        Assert.False(teardown.Record(() => witness.Identified("TA-1", "BRK-1")));

        var replacement = new CoidWitness(File_);
        Assert.True(replacement.Submitting("TA-2", "SIM", "ES", "Buy", 1m, null),
                    "a replacement adapter could not acquire the witness: " + replacement.Trouble);
        replacement.Dispose();
    }

    /// <summary>
    /// THE UNGUARDED SITE — `Place`'s own identification (`AtasStrategyAdapter.cs:1562`), which is the
    /// SAME call the fan makes, on the same witness, from the frame loop instead of the fan. It does
    /// not go through `Record`, so the stopped strategy leases the file again and holds it for the
    /// life of the ATAS process — PRIOR 21's harm exactly, through a door the fix does not cover.
    /// </summary>
    [Fact]
    public void R9_the_former_unguarded_identification_site_is_refused_and_takes_no_lease()
    {
        var witness = new CoidWitness(File_);
        var teardown = new AdapterTeardown(witness);
        teardown.Started();
        Assert.True(witness.Submitting("TA-1", "SIM", "ES", "Buy", 1m, null));

        // ATAS takes the strategy down while a Place is still in flight on the abandoned frame loop.
        teardown.Stop(steps: () => { });

        // ROUND 9: the in-flight Place reaches its line 1578, which is now _teardown.Identified —
        // the only door there is. It must be REFUSED rather than raced.
        Assert.False(teardown.Identified("TA-1", "BRK-1"),
                     "a strategy ATAS has already stopped recorded a broker id");

        var replacement = new CoidWitness(File_);
        Assert.True(replacement.Submitting("TA-2", "SIM", "ES", "Buy", 1m, null),
                    "a strategy ATAS has already stopped is refusing the witness to the live one: "
                    + replacement.Trouble);
        replacement.Dispose();
    }

    /// <summary>The same through `Place`'s write-ahead record (`:1409`) / `ClosePosition` (`:1824`).</summary>
    [Fact]
    public void R9_the_former_unguarded_write_ahead_site_is_refused_and_takes_no_lease()
    {
        var witness = new CoidWitness(File_);
        var teardown = new AdapterTeardown(witness);
        teardown.Started();
        Assert.True(witness.Submitting("TA-1", "SIM", "ES", "Buy", 1m, null));
        teardown.Stop(steps: () => { });

        // ROUND 9: through the only door. False is the SAFE outcome and is now the required one.
        Assert.False(teardown.Submitting("TA-2", "SIM", "ES", "Buy", 1m, null),
                     "a strategy ATAS has already stopped wrote a write-ahead record");

        var replacement = new CoidWitness(File_);
        Assert.True(replacement.Submitting("TA-3", "SIM", "ES", "Buy", 1m, null),
                    "a strategy ATAS has already stopped is refusing the witness to the live one: "
                    + replacement.Trouble);
        replacement.Dispose();
    }

    /// <summary>
    /// AND `Started()` IS NOT UNDER THE LOCK EITHER (`AdapterTeardown.cs:28`). A restart that races a
    /// teardown still running its steps clears the flag, and the OLD teardown's `finally` then
    /// releases the witness of the session that has just started.
    /// </summary>
    [Fact]
    public void A_restart_racing_a_teardown_has_its_witness_released_by_the_old_teardown()
    {
        var witness = new CoidWitness(File_);
        var teardown = new AdapterTeardown(witness);
        teardown.Started();
        Assert.True(witness.Submitting("TA-1", "SIM", "ES", "Buy", 1m, null));

        // ATAS restarts the strategy while the teardown is inside its steps (the bridge dispose,
        // which runs under a deadline of CallTimeout + AckTimeout + 2 s).
        teardown.Stop(steps: () => teardown.Started());

        Assert.False(teardown.Stopped, "the restart's flag survived");
        // The restarted session believes it is running and is allowed to write …
        Assert.True(teardown.Record(() => { }));
        // … but a rival can now take the witness out from under it, because the old teardown
        // released the lease after the restart had already begun.
        var rival = new CoidWitness(File_);
        Assert.True(rival.Submitting("TA-RIVAL", "SIM", "ES", "Buy", 1m, null),
                    "the rival was refused — the lease survived the racing restart: " + rival.Trouble);
        rival.Dispose();
    }
}
