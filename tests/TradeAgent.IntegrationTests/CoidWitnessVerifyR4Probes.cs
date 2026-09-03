using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// U14 round-4 ADVERSARIAL-VERIFY probes (leg [2]). Not part of the builder's suite: these exist to
/// make specific guards BITE, and each one is stated as the invariant it defends so that a build
/// which loses the guard goes red here.
/// </summary>
public class CoidWitnessVerifyR4Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "witness-probe-" + Guid.NewGuid().ToString("n")[..8]);
    public CoidWitnessVerifyR4Probes() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string[] CommittedIds() =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(File_)).RootElement.GetProperty("records")
            .EnumerateArray().Select(r => r.GetProperty("client_order_id").GetString()!).ToArray();

    /// <summary>
    /// THE LOCK'S OWN EXCLUSION IS LOAD-BEARING, AND NOTHING IN THE SUITE ASSERTS IT.
    ///
    /// The two existing lock tests hold `coid-witness.json.lock` FROM THE TEST with FileShare.None,
    /// so the witness is refused by the TEST's share mode. They stay green when the witness's own
    /// `Own()` is changed to FileShare.ReadWrite — i.e. when the lock stops excluding a second
    /// witness at all. This drives the interleaving that only the exclusion prevents: writer B is
    /// between its compare-and-swap and its rename when writer A runs an entire Submitting. Without
    /// exclusion A is told its write-ahead record is DURABLE (so Place sends the order) and B's
    /// rename then commits a file that does not contain A's claim.
    /// </summary>
    [Fact]
    public void The_lock_is_what_stops_a_claim_reported_durable_from_being_dropped()
    {
        var seed = new CoidWitness(File_);
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));

        CoidWitness? a = null;
        bool? aSaidDurable = null;

        // B's rename is the hook: A's whole claim runs inside B's replace, after B's CAS passed.
        var b = new CoidWitness(File_, null, CoidWitness.DefaultCap, (tmp, dest) =>
        {
            if (a is not null && aSaidDurable is null)
                aSaidDurable = a.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null);
            File.Move(tmp, dest, overwrite: true);
        });
        a = new CoidWitness(File_);

        // Both load the same committed content before either writes.
        _ = b.All();
        _ = a.All();

        b.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null);

        // THE INVARIANT: a claim Submitting called durable is on the committed file. Anything else
        // is an order that reached the wire with no write-ahead record behind it.
        Assert.NotNull(aSaidDurable);
        if (aSaidDurable == true)
            Assert.Contains("TA-A", CommittedIds());
    }
}
