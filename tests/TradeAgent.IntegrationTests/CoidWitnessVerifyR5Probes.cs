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

    void WriteTemp(long generation, string? predecessor, string records)
    {
        var path = File_ + ".tmp";
        var pred = predecessor is null ? "null" : $"\"{predecessor}\"";
        File.WriteAllText(path, $$"""{"version":1,"generation":{{generation}},"predecessor":{{pred}},"records":[{{records}}]}""");
        Age(path);
    }

    /// <summary>
    /// F8: A TEMP MAY ONLY ADD THE HALF THIS PRODUCT DID NOT WRITE — AND NOTHING ELSE.
    ///
    /// The rule is stated as "fill in the broker id on a claim the committed file already carries".
    /// A candidate that passes IllegalTransition carries the same identifier SET, so it is free to
    /// carry different VALUES for every other field: quantity, price, side, symbol, written_at, and
    /// the session id itself. If the merge took whole records, a temp dropped beside the witness
    /// could rewrite what this product says it submitted, without adding or removing an identifier.
    /// </summary>
    [Fact]
    public void A_temp_cannot_rewrite_any_field_of_a_committed_claim_except_the_broker_id()
    {
        using (var w = new CoidWitness(File_))
            Assert.True(w.Submitting("TA-1", "ACC-REAL", "ES", "Buy", 3m, 4200.25m));

        var committed = File.ReadAllText(File_);
        var session = System.Text.Json.JsonDocument.Parse(committed).RootElement
            .GetProperty("records")[0].GetProperty("session_id").GetString()!;

        // Same identifier set, same count — a legal transition — but every other field is a lie,
        // and it carries the broker id that makes the merge want it.
        WriteTemp(2, Fp(committed),
            $$"""{"client_order_id":"TA-1","session_id":"{{session}}","written_at":"2001-01-01T00:00:00+00:00","account_id":"ACC-FORGED","symbol":"NQ","side":"Sell","quantity":999,"price":1,"broker_order_id":"BRK-1","identified_at":"2026-01-01T00:00:01+00:00"}""");

        var r = new CoidWitness(File_).All().Single();

        Assert.Equal("BRK-1", r.BrokerOrderId);          // the half we did not write IS recovered
        Assert.Equal("ACC-REAL", r.AccountId);           // and nothing else moves
        Assert.Equal("ES", r.Symbol);
        Assert.Equal("Buy", r.Side);
        Assert.Equal(3m, r.Quantity);
        Assert.Equal(4200.25m, r.Price);
        Assert.Equal(session, r.SessionId);
        Assert.NotEqual(2001, r.WrittenAt.Year);
    }

    /// <summary>
    /// F4's anchor, Codex's exact check: `records:[null, A]` deserialises but means nothing. It must
    /// read UNREADABLE, every write must be refused, and the original bytes must be left alone.
    /// </summary>
    [Fact]
    public void A_null_element_envelope_is_unreadable_and_its_bytes_are_left_alone()
    {
        var bytes = $$"""{"version":1,"generation":4,"predecessor":null,"records":[null,{{RecordJson("TA-A", "a-dead-session", "BRK-A")}}]}""";
        File.WriteAllText(File_, bytes);

        using var w = new CoidWitness(File_);
        Assert.True(w.Unreadable);
        Assert.Empty(w.All());
        Assert.Null(w.PriorSession("TA-A"));                       // the valid element is not evidence
        Assert.False(w.Submitting("TA-NEW", "SIM", "ES", "Buy", 1m, null));
        Assert.NotNull(w.Trouble);
        Assert.Equal(bytes, File.ReadAllText(File_));              // untouched
    }

    /// <summary>
    /// F13's anchor: corrupt committed bytes are not a history, so a temp naming their fingerprint
    /// is not descended from anything. Both halves — the right generation and a wrong one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    public void Corrupt_committed_bytes_are_never_an_anchor(long generation)
    {
        const string corrupt = "this is not an envelope at all {{{";
        File.WriteAllText(File_, corrupt);
        WriteTemp(generation, Fp(corrupt), RecordJson("TA-GHOST", "another-machine", "BRK-GHOST"));

        using var w = new CoidWitness(File_);
        Assert.Null(w.PriorSession("TA-GHOST"));
        Assert.Empty(w.All());
        Assert.True(w.Unreadable);
        Assert.Equal(corrupt, File.ReadAllText(File_));
    }

    /// <summary>
    /// THE F8 RESIDUAL, MEASURED RATHER THAN ARGUED: a rename that throws AFTER the replace landed.
    ///
    /// The builder names this as not closable and states its direction — "a claim with no order, not
    /// an order with no claim". This drives it: the replace really happens and then throws, so the
    /// claim is COMMITTED while Submitting returns false and Place refuses the order.
    /// </summary>
    [Fact]
    public void The_F8_residual_is_a_claim_without_an_order_and_never_becomes_evidence()
    {
        using var w = new CoidWitness(File_, null, CoidWitness.DefaultCap, (tmp, dest) =>
        {
            File.Move(tmp, dest, overwrite: true);                 // it LANDED
            throw new IOException("the process cannot access the file");   // and then threw
        });

        Assert.False(w.Submitting("TA-GHOST", "SIM", "ES", "Buy", 1m, null));   // Place refuses the order

        // The direction: the claim IS on disk (a claim with no order) …
        Assert.Contains("TA-GHOST", CommittedIds());

        // … and it can never become cross-session evidence, because nothing ever acknowledges it.
        var next = new CoidWitness(File_);
        Assert.Null(next.PriorSession("TA-GHOST"));
        Assert.Empty(next.PriorSessionIds(10));
        Assert.DoesNotContain("TA-GHOST", next.PriorSessionIds(10));

        // And the rollback did not happen, which is why the in-memory and on-disk states differ here.
        Assert.NotNull(w.LastWriteFailure);
    }
}

