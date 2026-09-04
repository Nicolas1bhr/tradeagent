using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// U14 round-7 probes, target 3: V2's boundary is pinned by the PAIR (MV2b + MV2c), and each half
/// survives alone. This asks whether that is necessary — whether a single test can pin MV2c on its
/// own, which is what decides if a real defect can hide in the gap between two edits.
/// </summary>
public class SidecarBoundaryVerifyR7Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "wit-r7-" + Guid.NewGuid().ToString("n")[..8]);
    public SidecarBoundaryVerifyR7Probes() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);

    /// <summary>
    /// A REFUSED WRITER'S LINES MAY NOT DECIDE THIS MACHINE'S DURABILITY STATE — and the state that
    /// makes that observable is the one the pair-pinning leaves unbuilt: the canonical sidecar EXISTS
    /// (so the guard passes) but holds no deciding line, while a refused writer's own file holds an
    /// unresolved safety event.
    ///
    /// A second bridge turned away cost no order — the refusal is what stops the order being sent —
    /// so its lines must flag a zero and must NOT drop SupportsClientOrderId over somebody else's
    /// misconfiguration. That is the boundary MV2c crosses, and this pins it alone.
    /// </summary>
    [Fact]
    public void A_refused_writers_safety_line_flags_the_zero_without_degrading_the_machine()
    {
        using (var owner = new CoidWitness(File_))
            Assert.True(owner.Submitting("TA-OWNED", "SIM", "ES", "Buy", 1m, null));

        // The canonical sidecar exists and holds only a DIAGNOSTIC — no deciding line of its own.
        File.WriteAllText(Sidecar,
            $"{DateTimeOffset.UtcNow.AddMinutes(-1):O} ignored {File_}.tmp-dead: it does not descend from the committed file"
            + Environment.NewLine);

        // A refused writer's own file, holding a real unresolved safety event.
        File.WriteAllText(Path.Combine(_dir, CoidWitness.ErrorLogName + "-4242-deadbeef"),
            $"{DateTimeOffset.UtcNow:O} ERROR claim=TA-REFUSED another writer owns this witness ({File_}.lock): IOException"
            + Environment.NewLine);

        var w = new CoidWitness(File_);

        // The flag is raised — a zero from this directory is provisional …
        Assert.True(w.Noted);
        Assert.NotEqual(WitnessStanding.Clean, CoidWitnessReport.Standing(w));

        // … and the machine is NOT degraded by somebody else's refusal.
        Assert.Null(w.Trouble);
        Assert.False(w.GapClosed);
        Assert.DoesNotContain("io:degraded", w.Token());
    }
}
