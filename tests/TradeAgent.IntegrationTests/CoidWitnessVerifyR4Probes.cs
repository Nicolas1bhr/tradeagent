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

    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    string[] SidecarLines() => File.Exists(Sidecar)
        ? File.ReadAllLines(Sidecar).Where(l => l.Trim().Length > 0).ToArray() : [];
    static string RecordJson(string id, string session) =>
        $$"""{"client_order_id":"{{id}}","session_id":"{{session}}","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK-{{id}}","identified_at":"2026-01-01T00:00:01+00:00"}""";
    static void Age(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

    /// <summary>
    /// THE QUOTA CAN RATION THE **RESOLVED MARKER**, AND THE MARKER IS WHAT ENDS A DEGRADATION.
    ///
    /// Item 3's rule is "safety events never rationed; warnings and markers are". The RESOLVED
    /// marker is written with `safety: false` (CoidWitness.cs:1398), so a session that has already
    /// spent its 32 non-safety lines on quarantine warnings cannot write it. `_degraded` is then
    /// cleared IN MEMORY while the file's last line still reads as an unresolved gap, so the next
    /// process to load reports DEGRADED over a witness that committed cleanly — the permanent
    /// degradation loop commit 5e5b011 was written to end, re-entered through the quota.
    /// </summary>
    [Fact]
    public void A_clean_commit_marks_the_sidecar_resolved_even_after_the_quota_is_spent()
    {
        var owner = new CoidWitness(File_);
        Assert.True(owner.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));

        // 40 stale foreign temps: one quarantine WARNING each, past the 32-line quota.
        for (var i = 0; i < 40; i++)
        {
            var p = File_ + $".tmp-dead-{i:D2}";
            File.WriteAllText(p, $$"""{"version":1,"generation":99,"predecessor":"deadbeefdeadbeef","records":[{{RecordJson($"TA-X{i}", "dead")}}]}""");
            Age(p);
        }

        var next = new CoidWitness(File_);
        Assert.True(next.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));

        // The warnings ARE rationed — that half is the design.
        var lines = SidecarLines();
        Assert.Equal(32, lines.Count(l => l.Contains("ignored ")));

        // THE INVARIANT: a witness that has committed cleanly says so on its last line, so the next
        // process does not report DEGRADED over a gap that is closed.
        Assert.Contains("RESOLVED", lines[^1]);
        Assert.Null(new CoidWitness(File_).Trouble);
    }

    /// <summary>
    /// A SAFETY EVENT SURVIVES ROTATION. Item 3 keeps failures unrationed and bounds the file by
    /// rotating one generation back. Drives the sidecar past MaxErrorLogBytes with real refused
    /// rewrites and asserts the LAST failure is still on disk after the roll.
    /// </summary>
    [Fact]
    public void A_safety_event_is_still_written_after_the_sidecar_has_rotated()
    {
        var w = new CoidWitness(File_, null, CoidWitness.DefaultCap,
                                (_, _) => throw new IOException("the process cannot access the file"));
        for (var i = 0; i < 400; i++)
            Assert.False(w.Submitting($"TA-FAIL-{i:D4}", "SIM", "ES", "Buy", 1m, null));

        Assert.True(File.Exists(Sidecar + ".1"), "the sidecar never rotated — raise the iteration count");
        Assert.True(new FileInfo(Sidecar + ".1").Length > 0);

        // Unrationed: the last claim's failure is on disk, far past the 32-line quota.
        Assert.Contains(SidecarLines(), l => l.Contains("TA-FAIL-0399"));
        Assert.True(SidecarLines().Count(l => l.Contains("did not land")) > 32);
    }

    /// <summary>
    /// HOW LONG THE RATIONED MARKER LASTS, as an invariant rather than a measurement: a witness that
    /// has just committed cleanly must not make the NEXT process report a durability gap, because
    /// `Trouble` non-null is what drops `SupportsClientOrderId` to false in Describe().
    ///
    /// 40 leftovers: the 64 `.rejected-n` quarantine slots suffice, so the mess clears and only the
    /// first session is wrong. 100 leftovers: the slots run out, the surplus is re-rejected every
    /// session, the quota is spent every session, and the marker is never written again — the
    /// permanent degradation loop 5e5b011 closed, re-entered through the quota.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    public void A_witness_that_commits_cleanly_does_not_make_the_next_start_report_a_gap(int leftovers)
    {
        var owner = new CoidWitness(File_);
        Assert.True(owner.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        for (var i = 0; i < leftovers; i++)
        {
            var p = File_ + $".tmp-dead-{i:D3}";
            File.WriteAllText(p, $$"""{"version":1,"generation":99,"predecessor":"deadbeefdeadbeef","records":[{{RecordJson($"TA-X{i}", "dead")}}]}""");
            Age(p);
        }

        var report = new List<string>();
        var clean = true;
        for (var session = 1; session <= 4; session++)
        {
            var w = new CoidWitness(File_);
            var wrote = w.Submitting($"TA-S{session}", "SIM", "ES", "Buy", 1m, null);
            var after = new CoidWitness(File_);          // a fresh process, i.e. the next bridge start
            report.Add($"session {session}: committed={wrote} nextStartTrouble={(after.Trouble is null ? "none" : "DEGRADED")} token={after.Token()}");
            Assert.True(wrote, "the claim did not commit; this probe is about a HEALTHY witness");
            clean &= after.Trouble is null;
            foreach (var f in Directory.GetFiles(_dir, "*.tmp-dead-*")) Age(f);
        }
        Assert.True(clean, $"leftovers={leftovers} — a cleanly committed witness still reports a gap:\n  "
                           + string.Join("\n  ", report));
    }

    string CommittedText() => File.ReadAllText(File_);
    long CommittedGeneration() =>
        System.Text.Json.JsonDocument.Parse(CommittedText()).RootElement.GetProperty("generation").GetInt64();
    static string Fp(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text)) { hash ^= b; hash *= 1099511628211UL; }
        return hash.ToString("x16");
    }
    void WriteTemp(long generation, string? predecessor, string records)
    {
        var path = File_ + ".tmp";
        var pred = predecessor is null ? "null" : $"\"{predecessor}\"";
        File.WriteAllText(path, $$"""{"version":1,"generation":{{generation}},"predecessor":{{pred}},"records":[{{records}}]}""");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(File_).AddMinutes(5));
    }

    /// <summary>
    /// WHICH CROSS-CAP DIRECTION THE NEW ADOPTION RULE ACTUALLY REFUSES.
    ///
    /// `DropsACommittedRecord` reads THIS instance's `_cap` to decide whether a missing leading run
    /// is explained by Trim — but the candidate was written by a different instance, whose cap this
    /// build cannot see. Every cap-using test in the suite sets writer and reader to the SAME cap,
    /// so the cross-cap case is unpinned. This measures it in both directions: a legitimate at-cap
    /// rewrite, read by a build whose cap is larger, and by one whose cap is smaller.
    /// </summary>
    [Theory]
    [InlineData(3, 5)]   // writer's cap SMALLER than the reader's — a cap RAISE on upgrade
    [InlineData(5, 3)]   // writer's cap LARGER than the reader's — a cap LOWER on upgrade
    public void An_at_cap_rewrite_is_adopted_whatever_cap_the_reading_build_has(int writerCap, int readerCap)
    {
        var writer = new CoidWitness(File_, null, writerCap);
        for (var n = 1; n <= writerCap; n++)
        {
            writer.Submitting($"TA-{n}", "SIM", "ES", "Buy", 1m, null);
            writer.Identified($"TA-{n}", $"BRK-{n}");
        }
        Assert.Equal(writerCap, CommittedIds().Length);

        // The next claim arrived, the writer's Trim dropped TA-1 off the front, the rename never
        // landed. A perfectly legitimate uncommitted rewrite, at the WRITER's cap.
        var kept = Enumerable.Range(2, writerCap - 1).Select(n => RecordJson($"TA-{n}", "a-dead-session"));
        var fresh = RecordJson($"TA-{writerCap + 1}", "a-dead-session");
        WriteTemp(CommittedGeneration() + 1, Fp(CommittedText()), string.Join(",", kept.Append(fresh)));

        var reader = new CoidWitness(File_, null, readerCap);
        var adopted = reader.All().Select(r => r.ClientOrderId).ToArray();

        Assert.True(adopted.Contains($"TA-{writerCap + 1}"),
            $"writerCap={writerCap} readerCap={readerCap}: the legitimate at-cap rewrite was NOT adopted; "
            + $"reader sees [{string.Join(", ", adopted)}]");
    }
}
