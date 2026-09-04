using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// VERIFIER ROUND 8, TARGET 7 — the one of the manager's five refutations that carries a live safety
/// claim rather than a record-wording one: PRIOR R4, "the lock is a pathname lock; on macOS an unlink
/// yields a second owner, MEASURED TO COST NO CLAIM (CAS + read-back refuse)". Re-measured here rather
/// than read off the round-6 record.
/// </summary>
public class RefutationSpotCheckR8 : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "r4-" + Guid.NewGuid().ToString("n")[..8]);
    public RefutationSpotCheckR8() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }
    string File_ => Path.Combine(_dir, "coid-witness.json");

    [Fact]
    public void R4_an_unlinked_lock_yields_a_second_owner_and_costs_the_first_every_later_claim()
    {
        var a = new CoidWitness(File_);
        Assert.True(a.Submitting("TA-A1", "SIM", "ES", "Buy", 1m, null));

        // The lease is a claim on a PATHNAME. Unlink it while A is alive and holding the handle.
        File.Delete(File_ + ".lock");

        var b = new CoidWitness(File_);
        Assert.True(b.Submitting("TA-B1", "SIM", "ES", "Buy", 1m, null),
                    "a second owner did NOT appear — the refutation's premise would be wrong: " + b.Trouble);

        // The claim the refutation rests on: A's later writes are REFUSED, not silently lost.
        var accepted = a.Submitting("TA-A2", "SIM", "ES", "Buy", 1m, null);
        Assert.False(accepted, "A's claim was accepted while B owns the file — the compare-and-swap did not refuse");
        Assert.NotNull(a.Trouble);

        // And the whole run is refused from here, which is the stated fail-closed direction.
        Assert.False(a.Submitting("TA-A3", "SIM", "ES", "Buy", 1m, null));

        // B's claims are on disk; A's post-unlink ones are not.
        var committed = File.ReadAllText(File_);
        Assert.Contains("TA-B1", committed);
        Assert.DoesNotContain("TA-A2", committed);
        Assert.DoesNotContain("TA-A3", committed);
        a.Dispose(); b.Dispose();
    }
}
