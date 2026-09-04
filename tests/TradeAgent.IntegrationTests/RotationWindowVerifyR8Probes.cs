using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// VERIFIER ROUND 8, TARGET 3 — the rotation crash window, attacked from the side the builder's two
/// tests do not build.
///
/// Both `An_unresolved_gap_survives_a_crash_inside_the_rotation_window` and
/// `The_restatement_lands_before_the_generation_holding_the_gap_is_destroyed` seed the unresolved
/// ERROR into `.1` — one generation BACK — and then rotate. In that arrangement `.1` is untouched
/// until the very last two statements of `Rotate`, so the gap is readable throughout.
///
/// The ordinary state is the other one: the unresolved ERROR is in the CURRENT log, which is the log
/// being rotated. `Rotate` moves it to `<log>.rotating` — a name NO reader scans (`SidecarGenerations`
/// yields the log and `.1` only) — and the gap is invisible until the restatement lands.
/// </summary>
public class RotationWindowVerifyR8Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "rot8-" + Guid.NewGuid().ToString("n")[..8]);

    public RotationWindowVerifyR8Probes() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);

    void WriteForeignLeftover(int n)
    {
        var p = File_ + $".tmp-dead-{n:D3}";
        File.WriteAllText(p,
            $$"""{"version":1,"generation":99,"predecessor":"deadbeefdeadbeef","records":[{"client_order_id":"TA-X{{n}}","session_id":"dead","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK","identified_at":"2026-01-01T00:00:01+00:00"}]}""");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-5));
    }

    void Seed()
    {
        var seed = new CoidWitness(File_);
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        seed.Dispose();
    }

    /// <summary>THE CONTROL — the builder's arrangement, gap one generation back.</summary>
    [Fact]
    public void CONTROL_a_gap_one_generation_back_is_readable_at_the_window()
    {
        Seed();
        File.WriteAllText(Sidecar + ".1",
            $"{DateTimeOffset.UtcNow.AddMinutes(-5):O} ERROR coid-witness rewrite did not land. claim=TA-GAP"
            + Environment.NewLine);
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(new CoidWitness(File_).Trouble);

        WriteForeignLeftover(1);

        string? atTheWindow = null;
        var w = new CoidWitness(File_, writeSidecar: (p, t) =>
        {
            atTheWindow = new CoidWitness(File_).Trouble;
            File.WriteAllText(p, t);
        });
        Assert.True(w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        w.Dispose();

        Assert.NotNull(atTheWindow);
    }

    /// <summary>
    /// THE PROBE — the same window, the same seam, the gap in the CURRENT log instead.
    /// A machine that never finished the restatement write reads HEALTHY over an open durability gap.
    /// </summary>
    [Fact]
    public void A_gap_in_the_current_log_is_gone_at_the_instant_the_restatement_has_not_landed()
    {
        Seed();
        // No `.1` at all: this is a machine whose sidecar has never rotated, holding one unresolved
        // failure and then a lot of ordinary traffic. Reachable without contrivance — safety events
        // are unrationed and the cap is 64 KiB.
        File.WriteAllText(Sidecar,
            $"{DateTimeOffset.UtcNow.AddMinutes(-5):O} ERROR coid-witness rewrite did not land. claim=TA-GAP"
            + Environment.NewLine);
        File.AppendAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(new CoidWitness(File_).Trouble);   // the gap is plainly visible before the rotation

        WriteForeignLeftover(1);

        string? atTheWindow = null;
        var seen = new List<string>();
        var w = new CoidWitness(File_, writeSidecar: (p, t) =>
        {
            atTheWindow = new CoidWitness(File_).Trouble;
            seen.AddRange(Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "*").Select(Path.GetFileName)!);
            File.WriteAllText(p, t);
        });
        Assert.True(w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        w.Dispose();

        Assert.NotNull(atTheWindow);   // FAILS if the gap is invisible at the window
    }

    /// <summary>
    /// AND THE WINDOW DOES NOT CLOSE WHEN THE RESTATEMENT DOES NOT LAND — which is the exact failure
    /// the `_writeSidecar` seam's own doc says it exists for (a full disk, a read-only directory, a
    /// scanner holding the name). The retry then finds no log to rotate, appends to a fresh one, and
    /// the only copy of the unresolved line is left at `<log>.rotating`, which no reader scans.
    /// </summary>
    [Fact]
    public void A_restatement_that_does_not_land_leaves_the_only_copy_of_the_gap_unscanned()
    {
        Seed();
        File.WriteAllText(Sidecar,
            $"{DateTimeOffset.UtcNow.AddMinutes(-5):O} ERROR coid-witness rewrite did not land. claim=TA-GAP"
            + Environment.NewLine);
        File.AppendAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(new CoidWitness(File_).Trouble);

        WriteForeignLeftover(1);

        var w = new CoidWitness(File_, writeSidecar: (p, t) => throw new IOException("no space left on device"));
        w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null);
        w.Dispose();

        // What a reader that starts now can see. `.rotating` is not in SidecarGenerations().
        var files = Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "*").Select(Path.GetFileName).Order().ToArray();
        var scanned = string.Join("\n",
            new[] { Sidecar, Sidecar + ".1" }.Where(File.Exists).Select(File.ReadAllText));

        Assert.Fail("files=[" + string.Join(", ", files) + "]\n"
                    + "scanned generations contain ERROR: " + scanned.Contains("ERROR ") + "\n"
                    + "rotating file exists: " + File.Exists(Sidecar + ".rotating") + "\n"
                    + "rotating contains ERROR: " + (File.Exists(Sidecar + ".rotating") && File.ReadAllText(Sidecar + ".rotating").Contains("ERROR ")) + "\n"
                    + "Trouble now = " + (new CoidWitness(File_).Trouble ?? "<null>"));
    }
}
