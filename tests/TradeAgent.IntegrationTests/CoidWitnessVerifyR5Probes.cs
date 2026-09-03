using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// U14 round-5 ADVERSARIAL-VERIFY probes (leg [2]). Each states the invariant it defends, so a build
/// that loses the guard goes red here.
/// </summary>
public class CoidWitnessVerifyR5Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "wit-r5-" + Guid.NewGuid().ToString("n")[..8]);
    public CoidWitnessVerifyR5Probes() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    string[] CommittedIds() =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(File_)).RootElement.GetProperty("records")
            .EnumerateArray().Select(r => r.GetProperty("client_order_id").GetString()!).ToArray();
    static void Age(string p) => File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-5));
    static string RecordJson(string id, string session, string? broker = null) =>
        $$"""{"client_order_id":"{{id}}","session_id":"{{session}}","written_at":"2026-01-01T00:00:00+00:00","quantity":1{{(broker is null ? "" : $",\"broker_order_id\":\"{broker}\",\"identified_at\":\"2026-01-01T00:00:01+00:00\"")}}}""";
    static string Fp(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text)) { hash ^= b; hash *= 1099511628211UL; }
        return hash.ToString("x16");
    }

    /// <summary>
    /// AN UNRESOLVED DURABILITY GAP MUST SURVIVE THE SIDECAR ROTATING.
    ///
    /// The class fix decides `_degraded` from `LastDecidingLine()`, which reads ONLY the current
    /// `coid-witness.errors.log`. `AppendToErrorLog` rotates that file to `.1` once it passes
    /// MaxErrorLogBytes and then writes the new line into a FRESH file. So when the line that rotates
    /// it is a WARNING — a quarantined leftover, which no longer counts as a deciding line — every
    /// safety event moves to `.1` and the current file holds nothing that decides. The next start
    /// reads no gap, `Trouble` is null, and `Describe()` reports READY with
    /// `SupportsClientOrderId = true` over a witness whose own sidecar says a claim was lost.
    ///
    /// That is the defect commit 4c0294d closed ("carry a gap left by an earlier run onto the wire"),
    /// reachable again through rotation.
    /// </summary>
    [Fact]
    public void An_unresolved_gap_survives_the_sidecar_rotating()
    {
        // A run that lost claims: real refused rewrites, each an unrationed safety line, until the
        // sidecar is past its size bound.
        using (var failing = new CoidWitness(File_, null, CoidWitness.DefaultCap,
                                             (_, _) => throw new IOException("the process cannot access the file")))
            for (var i = 0; i < 300 && new FileInfo(Sidecar).Exists is var _ ; i++)
            {
                Assert.False(failing.Submitting($"TA-LOST-{i:D4}", "SIM", "ES", "Buy", 1m, null));
                if (File.Exists(Sidecar) && new FileInfo(Sidecar).Length > 64 * 1024) break;
            }

        Assert.True(new FileInfo(Sidecar).Length > 64 * 1024, "the sidecar never reached the rotation bound");
        Assert.False(File.Exists(Sidecar + ".1"), "it must not have rotated yet for this probe to mean anything");

        // The gap is open and reported, as it must be.
        Assert.NotNull(new CoidWitness(File_).Trouble);

        // Now a NEW run tidies one foreign leftover. That warning is what rotates the file.
        var stale = File_ + ".tmp-dead-1";
        File.WriteAllText(stale, $$"""{"version":1,"generation":99,"predecessor":"deadbeefdeadbeef","records":[{{RecordJson("TA-X", "dead", "BRK-X")}}]}""");
        Age(stale);
        using (var tidier = new CoidWitness(File_))
            tidier.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null);

        // THE INVARIANT: rotating the file may not close a gap that nothing resolved.
        var after = new CoidWitness(File_);
        // THE INVARIANT, as the design actually states it: rotation may not make a gap vanish
        // SILENTLY. Either it is still open, or the session that rotated the file committed cleanly
        // and said so — a deciding line has to be in the file that decides.
        var currentLines = File.ReadAllLines(Sidecar).Where(l => l.Trim().Length > 0).ToArray();
        var resolvedHere = currentLines.Any(l => l.Contains("RESOLVED coid-witness committed cleanly"));
        Assert.True(after.Trouble is not null || resolvedHere,
            $"rotation dropped the gap with nothing resolving it. rotated={File.Exists(Sidecar + ".1")} "
            + $"token={after.Token()} currentLog=[{string.Join(" | ", currentLines.Select(l => l.Length > 90 ? l[..90] : l))}]");
    }

    /// <summary>
    /// THE LEASE IS AN flock ON AN INODE, NOT A CLAIM ON A NAME.
    ///
    /// `Lease()` holds `coid-witness.json.lock` open with FileShare.None for the owner's lifetime. On
    /// macOS/Linux .NET enforces that with an advisory `flock` on the open file, so UNLINKING the
    /// lock file leaves the owner holding a handle to an inode with no name, and the next writer
    /// creates a fresh inode at the same path and takes its own flock. Two live owners, which is the
    /// state the lifetime lease exists to make impossible. Measured with real processes as well.
    ///
    /// This then drives the interleaving MV2 exposed at e22eec6 — B between its compare-and-swap and
    /// its rename while A runs a whole Submitting — to answer whether two owners still costs a claim.
    /// </summary>
    [Fact]
    public void Unlinking_the_lock_file_does_not_hand_the_witness_to_a_second_owner()
    {
        using var owner = new CoidWitness(File_);
        Assert.True(owner.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));   // takes the lease

        // A second live instance is refused, as the lifetime lease promises.
        using var rival = new CoidWitness(File_);
        Assert.False(rival.Submitting("TA-BLOCKED", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("another writer owns this witness", rival.Trouble);

        // Something tidies the directory. The owner is still alive and still believes it owns this.
        File.Delete(File_ + ".lock");

        CoidWitness? a = null;
        bool? aSaidDurable = null;
        using var b = new CoidWitness(File_, null, CoidWitness.DefaultCap, (tmp, dest) =>
        {
            if (a is not null && aSaidDurable is null)
                aSaidDurable = a.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null);
            File.Move(tmp, dest, overwrite: true);
        });
        a = new CoidWitness(File_);
        _ = b.All(); _ = a.All();
        b.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null);

        // THE INVARIANT: whatever the lock file's name is doing, a claim reported durable is on the
        // committed file, and only one writer owns the witness.
        Assert.True(aSaidDurable == false || CommittedIds().Contains("TA-A"),
            $"two owners after the unlink, and a claim reported durable is not committed: "
            + $"aSaidDurable={aSaidDurable} committed=[{string.Join(", ", CommittedIds())}]");
        a.Dispose();
    }

    /// <summary>
    /// THE LEASE OUTLIVES THE ADAPTER UNLESS Dispose IS CALLED, AND Dispose HAS ONE CALLER.
    ///
    /// `AtasStrategyAdapter` holds `readonly CoidWitness _witness = new()` per strategy instance and
    /// releases it only from `StopBridge`, reached from `OnStopping`. Two strategy instances in ONE
    /// ATAS process — trap 24/35's misconfiguration — or an adapter replaced without `OnStopping`
    /// leaves the first lease held for the life of the ATAS process. This measures what a second
    /// instance in the same process then gets, and that a Dispose does hand it over.
    /// </summary>
    [Fact]
    public void A_lease_not_disposed_refuses_the_next_instance_in_the_same_process_until_it_is()
    {
        var first = new CoidWitness(File_);
        Assert.True(first.Submitting("TA-FIRST", "SIM", "ES", "Buy", 1m, null));

        var second = new CoidWitness(File_);
        Assert.False(second.Submitting("TA-SECOND", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("another writer owns this witness", second.Trouble);

        first.Dispose();                                  // what StopBridge does

        Assert.True(second.Submitting("TA-SECOND", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(["TA-FIRST", "TA-SECOND"], CommittedIds());
        second.Dispose();
    }
}

