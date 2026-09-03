using TradeAgent.AtasBridge;
using TradeAgent.Core;
using TradeAgent.Diagnostics;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE MECHANISM THAT LETS RULE 1 BE SETTLED, UNDER TEST ON EVERY MACHINE.
///
/// The decisive experiment is: place a resting order, restart ATAS, and read the order book.
/// Anything surviving a process restart cannot be the <c>Order</c> instance the adapter submitted.
/// The obstacle is that after a restart the adapter's in-memory <c>_submitted</c> map is empty and
/// the read-back refuses any identifier not in it — a deliberate safety fix that must not be
/// weakened. <see cref="CoidWitness"/> is the durable, write-ahead record that answers the same
/// question <c>_submitted</c> answers, for a process that has already ended.
///
/// THE TRAP THESE TESTS EXIST TO KEEP SHUT. After a restart the adapter has constructed no
/// <c>Order</c> at all, so EVERY match is reference-distinct by construction. Wiring the restart
/// proof to <see cref="ClientOrderIdProof.Distinct"/> would not be a proof, it would be an
/// automatic <c>true</c> — the exact vacuity <see cref="ClientOrderIdProof.SameRef"/> exists to
/// expose, re-imported one level up. Hence a fourth reading, and hence
/// <see cref="The_restart_reading_alone_stops_the_search"/>, which is the regression that would
/// silently make that reading unreachable.
///
/// Lives in the integration project only because that is the one test project referencing
/// TradeAgent.AtasBridge; nothing here touches a pipe, ATAS or the gateway. It is all real file IO
/// in a scratch directory, because the file being real is the whole point of it.
/// </summary>
public class CoidWitnessTests : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "witness-" + Guid.NewGuid().ToString("n")[..8]);

    public CoidWitnessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    string File_ => Path.Combine(_dir, "coid-witness.json");

    /// <summary>
    /// Every temp lying beside the witness. A glob rather than one name because a writer gives its
    /// own rewrites unique names — see <see cref="CoidWitness"/> — so "was a temp left behind" is a
    /// question about a set.
    /// </summary>
    string[] Temps() => Directory.Exists(_dir) ? Directory.GetFiles(_dir, "coid-witness.json.tmp*") : [];

    /// <summary>
    /// FNV-1a 64 over UTF-8 — the same arithmetic <see cref="CoidWitness"/> uses, restated here
    /// deliberately. A test that asked the production code for the fingerprint could not notice the
    /// production fingerprint changing; this way a change to it shows up as a failing lineage test.
    /// </summary>
    static string Fingerprint(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text)) { hash ^= b; hash *= 1099511628211UL; }
        return hash.ToString("x16");
    }

    string CommittedText() => File.ReadAllText(File_);

    /// <summary>
    /// Makes a file look like a leftover rather than a rewrite in flight. A candidate younger than
    /// the quarantine grace is deliberately left alone: it may be another process between its write
    /// and its rename, and a reader has no business breaking a writer.
    /// </summary>
    static void Age(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

    /// <summary>The sidecar beside the witness, and its non-blank lines.</summary>
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    string[] SidecarLines() => File.Exists(Sidecar)
        ? File.ReadAllLines(Sidecar).Where(l => l.Trim().Length > 0).ToArray() : [];

    /// <summary>
    /// A stale foreign temp: a perfectly good envelope that descends from nothing on this machine.
    /// One quarantine WARNING each, which is what spends the non-safety quota.
    /// </summary>
    void WriteForeignLeftover(int n)
    {
        var p = File_ + $".tmp-dead-{n:D3}";
        File.WriteAllText(p, $$"""{"version":1,"generation":99,"predecessor":"deadbeefdeadbeef","records":[{{RecordJson($"TA-X{n}", "dead")}}]}""");
        Age(p);
    }

    /// <summary>
    /// The identifiers in the COMMITTED file, read straight out of it. Deliberately not through
    /// <see cref="CoidWitness"/>: a durability assertion that goes through the reader can be
    /// satisfied by an uncommitted temp the reader recovered, which is the opposite of what
    /// "durable" means.
    /// </summary>
    string[] CommittedIds() =>
        System.Text.Json.JsonDocument.Parse(CommittedText()).RootElement.GetProperty("records")
            .EnumerateArray().Select(r => r.GetProperty("client_order_id").GetString()!).ToArray();

    long CommittedGeneration() =>
        System.Text.Json.JsonDocument.Parse(CommittedText()).RootElement.GetProperty("generation").GetInt64();

    /// <summary>
    /// The broker order id the COMMITTED file carries for an identifier, or null when it carries
    /// none. Read out of the file rather than through <see cref="CoidWitness"/> for the same reason
    /// <see cref="CommittedIds"/> is: an assertion that goes through the reader can be satisfied by
    /// an uncommitted temp the reader recovered. The property is absent, not null, when there is no
    /// id — the writer omits nulls.
    /// </summary>
    string? CommittedBrokerId(string id) =>
        System.Text.Json.JsonDocument.Parse(CommittedText()).RootElement.GetProperty("records")
            .EnumerateArray()
            .Where(r => r.GetProperty("client_order_id").GetString() == id)
            .Select(r => r.TryGetProperty("broker_order_id", out var b) ? b.GetString() : null)
            .FirstOrDefault();

    /// <summary>One acknowledged record, in the shape the file stores.</summary>
    static string RecordJson(string id, string session) =>
        $$"""{"client_order_id":"{{id}}","session_id":"{{session}}","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK-{{id}}","identified_at":"2026-01-01T00:00:01+00:00"}""";

    /// <summary>Writes a temp beside the witness with exactly the lineage a test wants to try.</summary>
    void WriteTemp(long generation, string? predecessor, string records, DateTime? at = null)
    {
        var path = File_ + ".tmp";
        var pred = predecessor is null ? "null" : $"\"{predecessor}\"";
        File.WriteAllText(path,
            $$"""{"version":1,"generation":{{generation}},"predecessor":{{pred}},"records":[{{records}}]}""");
        File.SetLastWriteTimeUtc(path, at ?? File.GetLastWriteTimeUtc(File_).AddMinutes(5));
    }

    /// <summary>A temp at a name of the caller's choosing, for the cases that need two of them.</summary>
    void WriteTempAt(string path, long generation, string? predecessor, string records)
    {
        var pred = predecessor is null ? "null" : $"\"{predecessor}\"";
        File.WriteAllText(path,
            $$"""{"version":1,"generation":{{generation}},"predecessor":{{pred}},"records":[{{records}}]}""");
    }

    CoidWitness Session() => new(File_);

    /// <summary>A session whose rename behaves the way <paramref name="replace"/> says it does.</summary>
    CoidWitness Session(Action<string, string> replace) =>
        new(File_, null, CoidWitness.DefaultCap, replace);

    static void Submit(CoidWitness w, string id) =>
        w.Submitting(id, "SIM123", "ES", "Buy", 1m, 4200.25m);

    /// <summary>
    /// WHAT WINDOWS DOES, SAID IN AN EXCEPTION. <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c> refuses
    /// with a sharing violation — surfaced by .NET as <see cref="IOException"/> — while the
    /// destination is open without <c>FileShare.Delete</c>. It cannot be provoked on macOS or Linux,
    /// where <c>rename(2)</c> does not consult open handles at all, so it is injected instead.
    /// </summary>
    static IOException SharingViolation() =>
        new("The process cannot access the file because it is being used by another process.");

    /// <summary>A rename that fails at once — no retry budget, so a test can run many of them.</summary>
    static Action<string, string> VanishesUnless(Func<bool> lands) => (tmp, destination) =>
    {
        if (!lands()) throw new FileNotFoundException("it is gone", tmp);
        File.Move(tmp, destination, overwrite: true);
    };

    /// <summary>A rename that is refused for good: the destination is never released.</summary>
    static void NeverLands(string tmp, string destination) => throw SharingViolation();

    /// <summary>
    /// A rename that lands until the switch is thrown and never again after — the shape the real
    /// recoverable case has. <c>Submitting</c> commits and the order goes out; the acknowledgement
    /// that follows it is what gets stranded.
    /// </summary>
    static Action<string, string> LandsUntil(Func<bool> refused) => (tmp, destination) =>
    {
        if (refused()) throw SharingViolation();
        File.Move(tmp, destination, overwrite: true);
    };

    /// <summary>
    /// A rename refused <paramref name="times"/> times and then allowed — the ordinary contended
    /// case, where a scanner or an indexer had the file open for a moment.
    /// </summary>
    static Action<string, string> RefusedTimes(int times, Func<Exception> error)
    {
        var seen = 0;
        return (tmp, destination) =>
        {
            if (seen++ < times) throw error();
            File.Move(tmp, destination, overwrite: true);
        };
    }

    // ------------------------------------------------------------------ PriorSession, the contract

    /// <summary>
    /// The guard that stops this becoming an automatic true. An identifier THIS run submitted is
    /// still governed by the adapter's <c>_submitted</c> map exactly as it was before this file
    /// existed — otherwise a fresh process could reach the cross-session reading for an order it
    /// placed itself moments ago, which proves nothing about surviving anything.
    /// </summary>
    [Fact]
    public void A_record_from_the_running_session_is_not_a_prior_session_record()
    {
        var w = Session();
        Submit(w, "TA-1");
        w.Identified("TA-1", "BRK-1");

        Assert.Null(w.PriorSession("TA-1"));
        Assert.Empty(w.PriorSessionIds(16));
    }

    /// <summary>
    /// Write-ahead means the record exists with NO broker id for the whole window in which the
    /// order is being submitted — and in that state it is not evidence. A match on the comment
    /// alone is satisfiable by any order carrying that comment, which is precisely the reading the
    /// 2026-08-27 safety fix removed.
    /// </summary>
    [Fact]
    public void A_prior_session_record_with_no_broker_id_is_not_evidence()
    {
        var writer = Session();
        Submit(writer, "TA-2");                 // write-ahead: submitted, never acknowledged

        var reader = Session();
        Assert.NotEqual(writer.SessionId, reader.SessionId);
        Assert.Null(reader.PriorSession("TA-2"));
        Assert.Empty(reader.PriorSessionIds(16));
    }

    /// <summary>
    /// The reading the experiment is for: a different session wrote the claim before the order
    /// existed, and that same session recorded the broker id ATAS assigned.
    /// </summary>
    [Fact]
    public void A_prior_session_record_carrying_a_broker_id_is_returned()
    {
        var writer = Session();
        Submit(writer, "TA-3");
        writer.Identified("TA-3", "BRK-3");

        var reader = Session();
        var record = reader.PriorSession("TA-3");

        Assert.NotNull(record);
        Assert.Equal("TA-3", record.ClientOrderId);
        Assert.Equal("BRK-3", record.BrokerOrderId);
        Assert.Equal(writer.SessionId, record.SessionId);
        Assert.NotEqual(reader.SessionId, record.SessionId);
        Assert.NotNull(record.IdentifiedAt);
        Assert.Equal(["TA-3"], reader.PriorSessionIds(16));
    }

    [Fact]
    public void An_identifier_this_product_never_submitted_is_not_on_file()
    {
        var writer = Session();
        Submit(writer, "TA-4");
        writer.Identified("TA-4", "BRK-4");

        var reader = Session();
        Assert.Null(reader.PriorSession("SOMEBODY-ELSES-COMMENT"));
        Assert.Null(reader.PriorSession(""));
    }

    // ------------------------------------------------------------------ the half we did not write

    /// <summary>
    /// THE ONE THAT STOPS THE PROOF BEING MANUFACTURED OUT OF ITSELF.
    ///
    /// If a running session could write a broker id into a PRIOR session's record, then any order
    /// found in ATAS's book carrying an old comment would have its own id copied into the record
    /// and would match that record perfectly on the very next read-back. The record would no longer
    /// contain a half this process did not write; it would contain a half this process copied off
    /// the thing it was supposed to be evidence about.
    ///
    /// MEASURED, NOT ASSUMED: THIS TEST IS BLIND TO THE SESSION GUARD ON ITS OWN. Deleting the
    /// session check from <c>Identified</c> leaves this test PASSING, because the record here is
    /// already acknowledged and the separate first-non-empty-id-wins guard catches the write. The
    /// test that actually isolates the session guard is
    /// <see cref="A_running_session_cannot_complete_a_prior_sessions_unacknowledged_record"/> —
    /// where the record has no broker id and the session check is the only thing standing there.
    /// Both guards are real and both are load-bearing (dropping first-wins to "handle a late id
    /// assignment" is a plausible change), so both are kept; what is recorded here is which test
    /// covers which, so nobody reads this one as coverage it does not provide.
    /// </summary>
    [Fact]
    public void A_running_session_cannot_write_a_broker_id_into_a_prior_sessions_record()
    {
        var writer = Session();
        Submit(writer, "TA-5");
        writer.Identified("TA-5", "BRK-REAL");

        var reader = Session();
        reader.Identified("TA-5", "BRK-FORGED");

        Assert.Equal("BRK-REAL", reader.PriorSession("TA-5")!.BrokerOrderId);
        // And it did not merely fail to overwrite in memory — the file itself is unchanged.
        Assert.Equal("BRK-REAL", Session().PriorSession("TA-5")!.BrokerOrderId);
    }

    /// <summary>
    /// The same refusal for the case that has no broker id yet, which is the more tempting one: the
    /// record looks incomplete, and completing it from an order in the book is exactly the move
    /// that would fabricate the evidence.
    /// </summary>
    [Fact]
    public void A_running_session_cannot_complete_a_prior_sessions_unacknowledged_record()
    {
        var writer = Session();
        Submit(writer, "TA-6");

        var reader = Session();
        reader.Identified("TA-6", "BRK-FORGED");

        Assert.Null(reader.PriorSession("TA-6"));
        Assert.Null(Session().PriorSession("TA-6"));
    }

    /// <summary>First non-empty broker id wins, so no later event can rewrite the half this
    /// process did not choose.</summary>
    [Fact]
    public void The_broker_id_is_written_once_and_not_revised()
    {
        var w = Session();
        Submit(w, "TA-7");
        w.Identified("TA-7", "BRK-FIRST");
        w.Identified("TA-7", "BRK-SECOND");

        Assert.Equal("BRK-FIRST", Session().PriorSession("TA-7")!.BrokerOrderId);
    }

    [Fact]
    public void An_empty_broker_id_is_not_recorded()
    {
        var w = Session();
        Submit(w, "TA-8");
        w.Identified("TA-8", null);
        w.Identified("TA-8", "");

        Assert.Null(Session().PriorSession("TA-8"));
    }

    [Fact]
    public void Identifying_something_never_submitted_records_nothing()
    {
        var w = Session();
        w.Identified("NEVER-SUBMITTED", "BRK-9");

        Assert.Empty(Session().All());
    }

    // ------------------------------------------------------------------ across the restart

    /// <summary>
    /// The whole scenario in one test, in the order the experiment performs it: a session submits
    /// and is acknowledged, that session ends, and a NEW session — a different process on the real
    /// machine, a second instance here — reads the file it left behind.
    ///
    /// What makes it evidence is the ordering, and the ordering is asserted rather than assumed:
    /// the claim was on file, with no broker id, BEFORE the broker id existed.
    /// </summary>
    [Fact]
    public void A_file_written_by_one_session_is_read_by_the_next_one()
    {
        var first = Session();
        Submit(first, "TA-RESTART");

        // The write-ahead window: the claim is durable and the order is not yet acknowledged.
        var midflight = Session();
        Assert.Single(midflight.All());
        Assert.Null(midflight.All()[0].BrokerOrderId);
        Assert.Null(midflight.PriorSession("TA-RESTART"));

        first.Identified("TA-RESTART", "12007695");

        // "Restart": a fresh instance, with a session id it minted itself.
        var second = Session();
        Assert.NotEqual(first.SessionId, second.SessionId);

        var record = second.PriorSession("TA-RESTART");
        Assert.NotNull(record);
        Assert.Equal("12007695", record.BrokerOrderId);
        Assert.Equal("SIM123", record.AccountId);
        Assert.Equal("ES", record.Symbol);
        Assert.Equal("Buy", record.Side);
        Assert.Equal(1m, record.Quantity);
        Assert.Equal(4200.25m, record.Price);
        Assert.True(record.WrittenAt <= record.IdentifiedAt);
    }

    /// <summary>Newest first, and bounded — the sweep runs on every heartbeat.</summary>
    [Fact]
    public void Prior_session_ids_come_back_newest_first_and_capped()
    {
        var writer = Session();
        foreach (var n in new[] { 1, 2, 3, 4 })
        {
            Submit(writer, $"TA-{n}");
            writer.Identified($"TA-{n}", $"BRK-{n}");
        }
        // One left unacknowledged: it must not appear at all.
        Submit(writer, "TA-5");

        var reader = Session();
        Assert.Equal(["TA-4", "TA-3", "TA-2", "TA-1"], reader.PriorSessionIds(16));
        Assert.Equal(["TA-4", "TA-2"], new[] { reader.PriorSessionIds(2)[0], reader.PriorSessionIds(4)[2] });
        Assert.Empty(reader.PriorSessionIds(0));
    }

    /// <summary>A resubmitted identifier is one record, not two, and the newest claim wins.</summary>
    [Fact]
    public void Resubmitting_an_identifier_replaces_its_record()
    {
        var first = Session();
        Submit(first, "TA-DUP");
        first.Identified("TA-DUP", "BRK-OLD");

        // The run that owned the witness has ended — which is what a restart IS.
        first.Dispose();
        var second = Session();
        Submit(second, "TA-DUP");

        Assert.Single(second.All());
        // It belongs to the running session now, so it is not prior-session evidence for anyone.
        Assert.Null(second.PriorSession("TA-DUP"));
        Assert.Null(second.All()[0].BrokerOrderId);
    }

    // ------------------------------------------------------------------ the file itself

    /// <summary>
    /// The replace is a replace, not a delete followed by a move. A window with no file at all
    /// reads, for this file, as "this product never submitted that identifier" — which is the one
    /// answer that must never be produced by accident.
    ///
    /// AND THE THING THIS TEST DID NOT ASK, WHICH IS THE THING THAT MATTERS. On 3931c10 this failed
    /// on `test (windows-latest)` with `missing` at 0 and the temp left behind — a rename refused
    /// under load. Absence of the file and absence of the temp are both symptoms; the question is
    /// whether a claim was LOST, and nothing here asked it. It does now: every identifier the writer
    /// submitted is read back out of the file by a session that did not write it.
    /// </summary>
    [Fact]
    public async Task The_file_is_never_absent_while_it_is_being_rewritten()
    {
        var w = Session();
        Submit(w, "TA-SEED");
        Assert.True(File.Exists(File_));

        var stop = false;
        var missing = 0;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++) Submit(w, $"TA-CHURN-{i}");
            Volatile.Write(ref stop, true);
        });

        while (!Volatile.Read(ref stop))
            if (!File.Exists(File_)) missing++;
        await writer;

        Assert.Equal(0, missing);

        // DURABILITY, asserted before the tidiness below it, because this is the property the file
        // exists for and the other one is housekeeping. 301 = the seed plus 300 churn claims, all
        // inside DefaultCap, so nothing was trimmed and nothing may be missing.
        //
        // READ OUT OF THE COMMITTED FILE, NOT THROUGH A SESSION. A session recovers an uncommitted
        // temp — that is the whole of this unit — so asking one whether the claims are there can be
        // answered by a rewrite that never landed. That is precisely the state this assertion is
        // supposed to detect, and it would pass anyway.
        Assert.Equal(301, CommittedIds().Length);
        Assert.Equal("TA-CHURN-299", CommittedIds()[^1]);
        Assert.Contains("TA-SEED", CommittedIds());

        // A leftover temp no longer means a lost record — the assertions above just proved none was
        // lost, and a reader prefers a newer temp. It means a rename was still refused after the
        // full retry budget, which on Windows is a fact about the machine worth surfacing.
        Assert.Empty(Temps());
    }

    // ------------------------------------------------------- the rename that does not land

    /// <summary>
    /// THE ONE THAT MATTERS, AND THE REASON THIS SECTION EXISTS.
    ///
    /// GitHub CI on `test (windows-latest)`, commit 3931c10, failed the churn test above with
    /// "the temporary file was left behind" while `missing` was 0 — so a rename onto the real file
    /// was refused, permanently, on a contended runner. The leftover file is a symptom. The
    /// question worth asking is whether the write that failed to land LOST the record, and before
    /// this test it did: the newer state sat in `coid-witness.json.tmp`, which nothing ever opened,
    /// and the durable answer to "did this product submit this identifier" became NO for an
    /// identifier that was handed to ATAS microseconds later. That is rule 1 losing its evidence to
    /// a scanner holding a file open.
    ///
    /// The recovery rule this pins: a reader that finds a temp NEWER than the real file, and that
    /// parses, uses the temp. A successful save consumes its own temp, so a temp that outlives one
    /// can only be the product of a rewrite whose rename failed — and it is then, by construction,
    /// the more complete record.
    /// </summary>
    [Fact]
    public void An_acknowledgement_whose_rename_never_landed_is_still_readable_after_a_restart()
    {
        // THE SHAPE THIS MECHANISM IS ACTUALLY FOR, once Place refuses an order whose write-ahead
        // record did not land. A refused Submitting means no order was sent, so its claim describes
        // nothing and is taken back out — there is nothing there to recover. What CAN be stranded is
        // the half we did not write: the order is live, ATAS has assigned an id, and the rewrite
        // that records it is the one the replace refuses.
        var refused = false;
        var w = Session(LandsUntil(() => refused));
        Assert.True(w.Submitting("TA-LOST", "SIM123", "ES", "Buy", 1m, 4200.25m));

        refused = true;
        w.Identified("TA-LOST", "BRK-LOST");
        Assert.NotEmpty(Temps());

        // The restart: the process that wrote it is gone, and a new session reads what it left.
        var next = Session();
        Assert.Equal("BRK-LOST", next.PriorSession("TA-LOST")!.BrokerOrderId);
    }

    /// <summary>
    /// THE HOLE ROUND 3 FOUND, AND IT MANUFACTURES EVIDENCE. A refused <c>Submitting</c> means
    /// <c>Place</c> does not send the order — but the claim used to stay in memory, and the
    /// order-event fan calls <c>Identified</c> for EVERY order it sees carrying a comment. An
    /// unrelated order in ATAS's book bearing that identifier would complete the abandoned claim
    /// with its own broker id, and the result is a full prior-session record: a write-ahead claim,
    /// acknowledged, for an order this product never submitted.
    /// </summary>
    [Fact]
    public void A_refused_claim_cannot_be_completed_by_an_unrelated_order()
    {
        var seed = Session();
        Submit(seed, "TA-SEED");

        var w = Session(NeverLands);
        Assert.False(w.Submitting("TA-REFUSED", "SIM", "ES", "Buy", 1m, null));

        // What the fan does, unconditionally, for any order in the book carrying that comment.
        w.Identified("TA-REFUSED", "BRK-SOMEBODY-ELSES");

        Assert.DoesNotContain(w.All(), r => r.ClientOrderId == "TA-REFUSED");
        Assert.Null(w.PriorSession("TA-REFUSED"));

        // And no later session can read it as evidence either: the temp still holds the abandoned
        // claim, but it never acquired a broker id, so it is not evidence of anything.
        var next = Session();
        Assert.Null(next.PriorSession("TA-REFUSED"));
        Assert.Empty(next.PriorSessionIds(16));
    }

    /// <summary>
    /// THE ASYMMETRY, PINNED FROM BOTH SIDES, BECAUSE IT LOOKS LIKE AN INCONSISTENCY AND IS NOT.
    ///
    /// A refused <c>Submitting</c> is rolled back: <c>Place</c> will not send the order, so the claim
    /// describes nothing, and leaving it in memory lets an unrelated order in ATAS's book complete it
    /// with a real broker id — manufactured prior-session evidence for an order this product never
    /// submitted.
    ///
    /// A refused <c>Identified</c> is NOT rolled back, and the difference is the direction of the
    /// facts. The order is LIVE at the broker and the id is REAL. Rolling it back would throw away
    /// the half this product did not write, for an order it did send — so the next save could commit
    /// the record without an id it already knew, and the restart experiment would read that order as
    /// unacknowledged for ever.
    ///
    /// The test that already covers the acknowledgement path reads it back through a RESTART, which
    /// the stranded temp satisfies whether or not memory kept the id. This one asks the running
    /// session, which is the only place the asymmetry is visible.
    /// </summary>
    [Fact]
    public void A_refused_acknowledgement_is_kept_where_a_refused_claim_is_taken_back()
    {
        var refused = false;
        var w = Session(LandsUntil(() => refused));

        // The claim lands, so Place sends the order and ATAS assigns it an id.
        Assert.True(w.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));

        // THE ACKNOWLEDGEMENT HALF. The rewrite that records the broker id is refused for good.
        refused = true;
        w.Identified("TA-LIVE", "BRK-LIVE");
        Assert.Equal("BRK-LIVE", w.All().Single(r => r.ClientOrderId == "TA-LIVE").BrokerOrderId);
        Assert.Null(CommittedBrokerId("TA-LIVE"));

        // THE CLAIM HALF, in the same session, against the same refusal.
        Assert.False(w.Submitting("TA-REFUSED", "SIM", "ES", "Buy", 1m, null));
        Assert.DoesNotContain(w.All(), r => r.ClientOrderId == "TA-REFUSED");

        // And the next commit carries exactly that asymmetry onto the disk: the acknowledgement
        // survived the refusal, the abandoned claim did not.
        refused = false;
        Assert.True(w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(["TA-LIVE", "TA-NEXT"], CommittedIds());
        Assert.Equal("BRK-LIVE", CommittedBrokerId("TA-LIVE"));
    }

    /// <summary>
    /// A temp that is a perfectly good envelope, with records, but that is not descended from THIS
    /// committed file. Something else's rewrite, a copy, a hand-restored file. It is not a recovery
    /// and it does not displace anything.
    /// </summary>
    [Fact]
    public void A_temp_that_does_not_descend_from_the_committed_file_is_ignored()
    {
        var first = Session();
        Submit(first, "TA-COMMITTED");
        first.Identified("TA-COMMITTED", "BRK-COMMITTED");

        WriteTemp(generation: CommittedGeneration() + 1, predecessor: "not-this-file",
                  records: RecordJson("TA-FOREIGN", "some-other-session"),
                  at: File.GetLastWriteTimeUtc(File_) - TimeSpan.FromMinutes(5));

        var reader = Session();
        Assert.Equal("BRK-COMMITTED", reader.PriorSession("TA-COMMITTED")!.BrokerOrderId);
    }

    /// <summary>
    /// The temp is written with <c>File.WriteAllText</c>, which is not atomic, so a crash in the
    /// middle of one leaves a truncated file. A newer temp that does not parse is ignored in favour
    /// of the committed file rather than treated as corruption — the committed file is intact and
    /// the token must not claim otherwise.
    /// </summary>
    [Fact]
    public void A_temp_that_does_not_parse_is_ignored_rather_than_believed()
    {
        var first = Session();
        Submit(first, "TA-GOOD");
        first.Identified("TA-GOOD", "BRK-GOOD");

        File.WriteAllText(File_ + ".tmp", "{\"version\":1,\"records\":[{\"client_order");
        File.SetLastWriteTimeUtc(File_ + ".tmp", File.GetLastWriteTimeUtc(File_) + TimeSpan.FromMinutes(5));

        var reader = Session();
        Assert.Equal("BRK-GOOD", reader.PriorSession("TA-GOOD")!.BrokerOrderId);

        // And the committed file being intact means the READ did not fail. Reporting records:err
        // here would call a healthy witness broken because something unrelated was lying beside it.
        Assert.DoesNotContain("records:err", reader.Token());
        Assert.False(reader.Unreadable);
    }

    /// <summary>
    /// A sharing violation on Windows is transient — the scanner finishes, the reader closes — so
    /// the replace is retried before it is given up on. Four refusals in a row is past what the
    /// original three retries covered, and the record still has to land.
    /// </summary>
    [Fact]
    public void A_rename_refused_four_times_running_still_lands()
    {
        var w = Session(RefusedTimes(4, SharingViolation));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Submit(w, "TA-CONTENDED");
        clock.Stop();

        // THE WAITS ARE REAL AND THEY LENGTHEN: 20 + 40 + 60 + 80 ms between the five attempts. A
        // retry loop with the sleeps taken out passes every other assertion here — it lands on the
        // fifth attempt just the same — while doing on a live Windows machine exactly what the
        // budget exists to prevent: five refusals inside a microsecond, then giving up.
        Assert.True(clock.ElapsedMilliseconds >= 150,
            $"the whole retry ran in {clock.ElapsedMilliseconds} ms — the backoff is not being taken");

        Assert.Empty(Temps());
        Assert.Single(Session().All());
    }

    /// <summary>
    /// The same refusal arrives as <see cref="UnauthorizedAccessException"/> when the destination is
    /// held by something the process may not displace — an anti-virus handle, a read-only attribute
    /// set by a backup tool. It is as transient as the sharing violation and was not retried at all.
    /// </summary>
    [Fact]
    public void A_rename_refused_with_an_access_error_is_retried_too()
    {
        var w = Session(RefusedTimes(4, () => new UnauthorizedAccessException("Access to the path is denied.")));
        Submit(w, "TA-DENIED");

        Assert.Empty(Temps());
        w.Dispose();
        Assert.Single(Session().All());
    }

    /// <summary>
    /// THE BUDGET IS FIVE ATTEMPTS AND IT IS BOUNDED, FOR BOTH REFUSALS — counted, not inferred.
    ///
    /// The existing retry tests succeed on the fifth attempt and assert only a LOWER time bound, so
    /// raising the attempt count from 5 to 500 leaves them green while a wholly contended order
    /// spends twenty seconds inside Place, past the gateway's 10 s RPC deadline, where the order is
    /// recorded UNKNOWN and a disk problem becomes a reconciliation. The number is a judgment
    /// (CoidWitness documents why) but it has to be the number the code actually uses.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_refused_rename_is_attempted_exactly_five_times_and_then_gives_up(bool accessDenied)
    {
        var attempts = 0;
        var w = Session((_, _) =>
        {
            attempts++;
            throw accessDenied
                ? new UnauthorizedAccessException("Access to the path is denied.")
                : SharingViolation();
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(w.Submitting("TA-NEVER", "SIM", "ES", "Buy", 1m, null));
        clock.Stop();

        Assert.Equal(5, attempts);
        // 20 + 40 + 60 + 80 = 200 ms of sleeps. The lower bound proves they are taken; the upper one
        // proves the budget is bounded, which is what the RPC deadline depends on.
        Assert.True(clock.ElapsedMilliseconds >= 150,
                    $"the whole retry ran in {clock.ElapsedMilliseconds} ms — the backoff is not taken");
        Assert.True(clock.ElapsedMilliseconds < 2000,
                    $"the retry took {clock.ElapsedMilliseconds} ms — the budget is not bounded");
    }

    /// <summary>
    /// AND THE REFUSAL IS NOT CARRIED FORWARD EITHER. Round 2 kept a failed claim in memory so the
    /// next save would commit it — right when the order might still be live, wrong now that
    /// <c>Place</c> refuses the order outright. A claim that describes no order must not turn up in
    /// the durable file two orders later, where nothing distinguishes it from one that was sent.
    /// </summary>
    [Fact]
    public void A_refused_claim_is_not_carried_into_the_next_commit()
    {
        var refused = true;
        var w = Session(LandsUntil(() => refused));

        Assert.False(w.Submitting("TA-REFUSED", "SIM", "ES", "Buy", 1m, null));
        Assert.False(File.Exists(File_), "nothing was committed under the real name");

        refused = false;
        Assert.True(w.Submitting("TA-SENT", "SIM", "ES", "Buy", 1m, null));

        Assert.Empty(Temps());
        Assert.Equal(["TA-SENT"], CommittedIds());
    }

    /// <summary>
    /// THE ONE THAT WOULD HAVE DESTROYED THE FILE. An envelope deserialises with <c>Records</c>
    /// defaulting to an empty list, so <c>{}</c> — or any rewrite that happens to carry no records —
    /// parses perfectly and says nothing at all. A recovery rule that asked only "is this temp
    /// newer" adopts it, shadows a good committed file with a void, and the very next save COMMITS
    /// the void: every claim on the machine gone, permanently, caused by the mechanism that exists
    /// to stop claims being lost.
    ///
    /// The lineage here is otherwise PERFECT — the right predecessor and the right generation — so
    /// what refuses it is the record count and nothing else.
    /// </summary>
    [Fact]
    public void A_temp_with_no_records_never_shadows_the_committed_file()
    {
        var first = Session();
        Submit(first, "TA-REAL");
        first.Identified("TA-REAL", "BRK-REAL");

        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(CommittedText()),
                  records: "");

        // The run that owned the witness has ended — which is what a restart IS.
        first.Dispose();
        var reader = Session();
        Assert.Equal("BRK-REAL", reader.PriorSession("TA-REAL")!.BrokerOrderId);

        // And it did not merely read past it: the next save must not commit the void either.
        Submit(reader, "TA-AFTER");
        Assert.Equal(["TA-REAL", "TA-AFTER"], Session().All().Select(r => r.ClientOrderId));
    }

    /// <summary>
    /// THE OTHER HALF OF THE SAME HOLE, AND THE ONE THAT REACHES A CAPABILITY. A genuine, older
    /// envelope of this same file — preserved by a backup tool, a copy, a hand-restore — given a
    /// later mtime. Under a newest-wins rule it is adopted, and the identifiers <see cref="Trim"/>
    /// removed come back to life. Those go straight into <c>PriorSessionIds</c>, into the
    /// cross-session reading, and set SupportsClientOrderId TRUE out of state that is not in the
    /// committed file at all.
    ///
    /// Lineage refuses it whatever its mtime says, and the cap's promise — a trimmed identifier is
    /// permanently unprovable — stays true.
    /// </summary>
    [Fact]
    public void A_preserved_older_envelope_cannot_resurrect_trimmed_identifiers()
    {
        var writer = new CoidWitness(File_, null, cap: 4);
        writer.Submitting("TA-1", "SIM", "ES", "Buy", 1m, null);
        writer.Identified("TA-1", "BRK-TA-1");
        var earlyGeneration = CommittedGeneration();
        var earlyRecords = RecordJson("TA-1", "a-dead-session");

        for (var i = 2; i <= 7; i++)
        {
            writer.Submitting($"TA-{i}", "SIM", "ES", "Buy", 1m, null);
            writer.Identified($"TA-{i}", $"BRK-{i}");
        }

        // TA-1 has been trimmed out of the committed file. Put it back as a "newer" temp.
        WriteTemp(generation: earlyGeneration + 1, predecessor: Fingerprint(CommittedText()),
                  records: earlyRecords);

        var reader = new CoidWitness(File_, null, cap: 4);
        Assert.Null(reader.PriorSession("TA-1"));
        Assert.DoesNotContain("TA-1", reader.PriorSessionIds(16));
        Assert.Equal(["TA-7", "TA-6", "TA-5", "TA-4"], reader.PriorSessionIds(16));
    }

    /// <summary>
    /// THE EMPTY-DIRECTORY CASE, WHICH IS THE EASIEST ONE TO GET WRONG. With no committed file there
    /// is no fingerprint to match against, so the temptation is to accept any temp that parses and
    /// has records — and that is an import route: a fragment of some other witness's history,
    /// dropped in the bridge directory, becomes this machine's record of what it submitted. Those
    /// records are acknowledged, so they reach PriorSessionIds, the cross-session reading, and
    /// SupportsClientOrderId — a capability set true out of a file this product never wrote.
    ///
    /// With nothing committed, the only thing a rewrite can be is the FIRST one: generation 1, and
    /// descended from nothing. Anything claiming otherwise is describing a history this file does
    /// not have.
    ///
    /// MEASURED, NOT ASSUMED: this test exists because the round-2 mutation sweep found the branch
    /// unguarded. Replacing the whole condition with `true` left every other test in this file
    /// passing.
    /// </summary>
    [Fact]
    public void With_nothing_committed_only_a_first_rewrite_is_adopted()
    {
        // Claims descent from something, on a machine where nothing has ever been committed.
        WriteTemp(generation: 12, predecessor: "some-other-witness-file",
                  records: RecordJson("TA-IMPORTED", "a-dead-session"), at: DateTime.UtcNow);

        var w = Session();
        Assert.Empty(w.All());
        Assert.Null(w.PriorSession("TA-IMPORTED"));
        Assert.Empty(w.PriorSessionIds(16));

        // A FLAGGED ZERO, AND BOTH WORDS ARE LOAD-BEARING. Zero, because nothing was ever committed
        // here and the refused candidate does not change that. FLAGGED, because a confident zero
        // from this file means "this product never submitted that identifier" — the one answer that
        // must never be produced by accident — and something WAS refused, so a reader is told.
        // Unreadable stays false: the zero is a fact about the disk, not a failure to read it.
        //
        // The flag is `io:noted` and not `io:degraded`: a refused import is a diagnostic, not a
        // durability gap, and only a gap may reach Trouble and drop SupportsClientOrderId.
        Assert.Contains("records:0", w.Token());
        Assert.Contains("io:noted", w.Token());
        Assert.Null(w.Trouble);
        Assert.False(w.Unreadable);

        // And the shape that round 2 still accepted: generation 1, descended from nothing. It is
        // not a lineage test — every first rewrite of every witness on earth looks exactly like
        // this — and it is refused too.
        foreach (var f in Temps()) File.Delete(f);
        WriteTemp(generation: 1, predecessor: null,
                  records: RecordJson("TA-ALSO-IMPORTED", "a-dead-session"), at: DateTime.UtcNow);

        var again = Session();
        Assert.Empty(again.All());
        Assert.Empty(again.PriorSessionIds(16));
        Assert.Contains("records:0", again.Token());
        Assert.Contains("io:noted", again.Token());
        Assert.Null(again.Trouble);
        Assert.False(again.Unreadable);

        // THE REASON IS WRITTEN DOWN BY THE OWNER. A reader flags its own answer and writes nothing
        // (see A_reader_changes_nothing_on_disk_even_when_no_owner_holds_the_witness), so the
        // sidecar line appears when a party entitled to write one next runs.
        again.Dispose();
        var owner = Session();
        Assert.True(owner.Submitting("TA-OURS", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("nothing anchors it",
                        File.ReadAllText(Path.Combine(_dir, CoidWitness.ErrorLogName)));
    }

    /// <summary>
    /// A TEMP IS NEVER ADOPTED AS A NEW CLAIM, AND THE REASON IS A CONTRACT THIS BUILD ALREADY KEEPS.
    ///
    /// Since round 2 <c>Place</c> refuses the order when <c>Submitting</c> returns false. So a temp
    /// holding a claim that was never committed is, by that contract, a submission THAT DID NOT
    /// HAPPEN — no order carrying that identifier was ever handed to ATAS. Recovering it writes a
    /// write-ahead record for an order this product never submitted, and the record is then
    /// indistinguishable from a real one: complete it with any acknowledgement and it is
    /// prior-session evidence. At the cap it is worse still, because the phantom evicts a genuine
    /// committed claim to make room for itself.
    ///
    /// Recovery cannot tell a failed SUBMISSION temp from a failed ACKNOWLEDGEMENT temp by looking
    /// at it — both are "the rewrite that did not land". So the rule is stated instead of inferred:
    /// a temp may only add acknowledgement information to a claim ALREADY in the committed file. It
    /// may never introduce an identifier.
    /// </summary>
    [Fact]
    public void A_temp_is_never_adopted_as_a_new_claim()
    {
        var a = Session();
        Assert.True(a.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null));
        a.Dispose();

        // B's rewrite never lands, so Place refused that order and nothing was sent. The temp stays.
        var b = Session(NeverLands);
        Assert.False(b.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
        Assert.Single(Temps());
        b.Dispose();

        // The restart, and then an order that DID go out.
        var c = Session();
        Assert.True(c.Submitting("TA-C", "SIM", "ES", "Buy", 1m, null));

        Assert.Equal(["TA-A", "TA-C"], CommittedIds());
        Assert.Null(c.PriorSession("TA-B"));
        c.Dispose();
        Assert.DoesNotContain("TA-B", Session().PriorSessionIds(16));
    }

    /// <summary>
    /// AND THE DIRECTION THAT MUST STILL WORK, because it is what the recovery exists for: the order
    /// IS live, ATAS assigned it an id, and the rewrite recording that id is the one that failed.
    /// The claim is already committed, so the temp adds only the half this product did not write.
    /// </summary>
    [Fact]
    public void A_temp_may_still_add_an_acknowledgement_to_a_committed_claim()
    {
        var refused = false;
        var w = Session(LandsUntil(() => refused));
        Assert.True(w.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));
        refused = true;
        w.Identified("TA-LIVE", "BRK-LIVE");
        Assert.Single(Temps());
        w.Dispose();

        var next = Session();
        Assert.Equal("BRK-LIVE", next.PriorSession("TA-LIVE")!.BrokerOrderId);
        Assert.Equal(["TA-LIVE"], next.All().Select(r => r.ClientOrderId));
    }

    /// <summary>
    /// A FORGED ACKNOWLEDGEMENT IS STILL REFUSED. <c>Identified</c> will not write a broker id into a
    /// record belonging to another session — that refusal is what stops an order found in ATAS's book
    /// writing its own id into a prior record and then matching itself. Recovery must not become the
    /// way around it, so a temp may only complete a claim whose session it shares.
    /// </summary>
    [Fact]
    public void A_temp_cannot_acknowledge_a_claim_that_belongs_to_another_session()
    {
        var owner = Session();
        Assert.True(owner.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));
        var committed = CommittedText();
        owner.Dispose();

        // Someone drops a rewrite beside the witness that completes the claim under a session that
        // never wrote it.
        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(committed),
                  records: RecordJson("TA-LIVE", "a-session-that-never-wrote-this"));

        var reader = Session();
        Assert.Null(reader.PriorSession("TA-LIVE"));
        Assert.Null(reader.All().Single().BrokerOrderId);
    }

    /// <summary>
    /// AND A TEMP CANNOT REVISE A BROKER ID THAT IS ALREADY ON FILE. A broker order id does not
    /// change once ATAS has assigned it, which is why <c>Identified</c> takes the first non-empty one
    /// and refuses every later write. Recovery has to keep that rule or it becomes the way around it:
    /// a rewrite dropped beside the witness could restate the half this product did not write, and
    /// the read-back would then match an order against an id somebody else chose.
    /// </summary>
    [Fact]
    public void A_temp_cannot_revise_a_broker_id_that_is_already_recorded()
    {
        var owner = Session();
        Assert.True(owner.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));
        owner.Identified("TA-LIVE", "BRK-REAL");
        var session = owner.SessionId;
        var committed = CommittedText();
        owner.Dispose();

        // Same session, same identifier, a different broker id.
        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(committed),
                  records: $$"""{"client_order_id":"TA-LIVE","session_id":"{{session}}","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK-FORGED","identified_at":"2026-01-01T00:00:01+00:00"}""");

        var reader = Session();
        Assert.Equal("BRK-REAL", reader.PriorSession("TA-LIVE")!.BrokerOrderId);
    }

    /// <summary>
    /// AN ENVELOPE THAT DESERIALISES IS NOT AN ENVELOPE THAT MEANS ANYTHING.
    ///
    /// `Parse` asked only whether the JSON deserialised. `records:[null, A]` does — and then
    /// iterating it throws on the null before it reaches A, the public reader swallows that, and the
    /// instance is left LOADED with an empty record list and no read failure recorded. Everything
    /// downstream then reads a confident zero: `All()` empty, `io:ok`, and the next `Submitting`
    /// skips loading entirely and replaces the anchor with a file holding one claim — A's record,
    /// which was in the bytes all along, gone.
    ///
    /// So the envelope is validated semantically, `_loaded` is set only once the records are
    /// actually in memory, and an unreadable committed file refuses every write while it stands:
    /// the bytes this build could not read are the bytes it must not overwrite.
    /// </summary>
    [Fact]
    public void A_semantically_invalid_envelope_is_unreadable_and_no_write_replaces_it()
    {
        File.WriteAllText(File_,
            $$"""{"version":1,"generation":3,"predecessor":null,"records":[null,{{RecordJson("TA-A", "a-dead-session")}}]}""");
        var original = File.ReadAllText(File_);

        // The startup/describe path, which is what actually runs first in the bridge.
        var w = Session();
        Assert.Empty(w.All());
        Assert.True(w.Unreadable);
        Assert.Contains("records:err", w.Token());
        Assert.NotNull(w.Trouble);

        // And the write does not get to replace the bytes it could not read.
        Assert.False(w.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(original, File.ReadAllText(File_));

        // Repaired by hand, the witness works again — the refusal is a state, not a latch.
        w.Dispose();
        File.Delete(File_);
        var next = Session();
        Assert.True(next.Submitting("TA-C", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(["TA-C"], CommittedIds());
    }

    /// <summary>
    /// UNREADABLE IS NOT ABSENT, AND MISTAKING THE ONE FOR THE OTHER DESTROYS THE FILE.
    ///
    /// The parse half of this was closed: bytes that are there and are not an envelope refuse every
    /// write. The I/O half was not. A read that FAILED — a scanner holding the file, a denied ACL, a
    /// disk error — returned no text, and no text was read as "nothing has ever been written here":
    /// the lineage reset to generation 0 with no predecessor, the compare-and-swap compared null
    /// against null and passed, and the rewrite replaced a file of acknowledged claims with one
    /// holding the new claim alone. The post-replace read-back then succeeded, so
    /// <c>Submitting</c> returned TRUE and <c>Place</c> sent the order — with the machine's whole
    /// history gone and nothing said about it.
    ///
    /// Absent is exactly <see cref="FileNotFoundException"/> on the committed path. Every other way
    /// of not getting the bytes is unreadable, and unreadable refuses every write.
    /// </summary>
    /// <param name="from">the first open of the committed path to deny (0 = the load's).</param>
    /// <param name="to">one past the last. The load takes 1 open when it succeeds and 4 when every
    /// attempt is refused, so (0,4) denies the load, (1,5) denies the compare-and-swap after a
    /// successful load, and (0,8) denies both — Codex's own shape.</param>
    [Theory]
    [InlineData(0, 4, "the load's four reads")]
    [InlineData(1, 5, "the compare-and-swap's four reads")]
    [InlineData(0, 8, "both, with the post-replace read permitted")]
    public void A_committed_file_that_cannot_be_read_is_not_treated_as_absent(int from, int to, string which)
    {
        var a = Session();
        Assert.True(a.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null));
        a.Identified("TA-A", "BRK-A");
        a.Dispose();
        var original = File.ReadAllText(File_);

        var opens = 0;
        var w = new CoidWitness(File_, null, CoidWitness.DefaultCap, null,
            open: p =>
            {
                if (string.Equals(p, File_, StringComparison.Ordinal))
                {
                    var n = opens++;
                    if (n >= from && n < to)
                        throw new IOException("The process cannot access the file because it is being used by another process.");
                }
                return new FileStream(p, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
            });

        Assert.False(w.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null), which);
        Assert.Equal(original, File.ReadAllText(File_));

        // AND THE DIAGNOSIS IS THE REPAIR. "Could not be read" sends a person to a lock or a damaged
        // file; "changed underneath this writer" sends them hunting a second bridge that is not
        // there. Each guard produces the first only if it is the one that fired, which is what makes
        // them separately load-bearing rather than redundant.
        Assert.Contains("could not be read", w.Trouble);
        Assert.DoesNotContain("changed underneath", w.Trouble);
    }

    /// <summary>
    /// INVALID BYTES ARE NOT A VALIDATED ANCHOR, AND FINGERPRINT ALONE IS NOT LINEAGE.
    ///
    /// When the committed file existed but did not parse, its generation was unknowable, so the
    /// lineage test fell back to the fingerprint by itself and any temp claiming ANY generation was
    /// adopted. The argument for that was that the fingerprint is over exact bytes and is the
    /// stronger of the two checks. It is — over bytes that mean something. Corrupt bytes are not a
    /// history this file has; they are a file that has to be replaced, and adopting a rewrite that
    /// claims descent from them walks its acknowledged identifiers into <c>PriorSession</c> while
    /// the witness is simultaneously reporting that it cannot be read.
    ///
    /// So an anchor has to PARSE. Both halves of the lineage or no adoption.
    /// </summary>
    [Fact]
    public void An_unreadable_committed_file_is_not_an_anchor()
    {
        File.WriteAllText(File_, "this is not a witness envelope at all");
        var corrupt = File.ReadAllText(File_);

        WriteTemp(generation: 999, predecessor: Fingerprint(corrupt),
                  records: RecordJson("TA-GHOST", "a-dead-session"));

        var reader = Session();
        Assert.Null(reader.PriorSession("TA-GHOST"));
        Assert.Empty(reader.PriorSessionIds(16));
        Assert.True(reader.Unreadable);
        Assert.Contains("records:err", reader.Token());

        // And it is the missing anchor rather than the arithmetic: the generation a first rewrite
        // would carry is refused just the same.
        foreach (var f in Temps()) File.Delete(f);
        WriteTemp(generation: 1, predecessor: Fingerprint(corrupt),
                  records: RecordJson("TA-GHOST-2", "a-dead-session"));

        var again = Session();
        Assert.Null(again.PriorSession("TA-GHOST-2"));
        Assert.Empty(again.PriorSessionIds(16));
    }

    /// <summary>
    /// LINEAGE AUTHENTICATES THE PARENT, NOT THE CONTENT. A rewrite can descend perfectly well from
    /// the committed file and still hold fewer records than it — and adopting one displaces
    /// committed claims, because the adopted set is what the next save commits. At the cap that
    /// reaches further than it looks: the dropped claims are gone from the file, and an identifier
    /// the cap had trimmed can come back in their place.
    /// </summary>
    [Fact]
    public void A_candidate_holding_fewer_records_than_the_committed_file_is_ignored()
    {
        var first = Session();
        foreach (var n in new[] { 1, 2, 3 })
        {
            Submit(first, $"TA-{n}");
            first.Identified($"TA-{n}", $"BRK-{n}");
        }

        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(CommittedText()),
                  records: RecordJson("TA-1", "a-dead-session"));

        var reader = Session();
        Assert.Equal(["TA-1", "TA-2", "TA-3"], reader.All().Select(r => r.ClientOrderId));
        Assert.Contains("io:noted", reader.Token());   // written down, but not a durability gap
        Assert.Null(reader.Trouble);
    }
    /// <summary>
    /// THE SAME DEFECT ONE STEP ALONG, AND A COUNT CANNOT SEE IT. A candidate that holds the SAME
    /// NUMBER of records as the committed file, but not the same ones: it dropped a committed claim
    /// and added one of its own. Every count check in the world reads that as "no records were
    /// lost" — three against three — and adopting it drops the claim, because the adopted set is
    /// what the next save commits.
    ///
    /// The property that matters is MEMBERSHIP: every identifier that was committed is still there.
    /// The one exception is the prefix <c>Trim</c> takes at the cap, and this file is nowhere near
    /// its cap, so nothing can account for TA-1 going missing.
    /// </summary>
    [Fact]
    public void A_candidate_that_swapped_a_committed_claim_for_another_is_ignored()
    {
        var first = Session();
        foreach (var n in new[] { 1, 2, 3 })
        {
            Submit(first, $"TA-{n}");
            first.Identified($"TA-{n}", $"BRK-{n}");
        }

        // Three records against three, perfect lineage — and TA-1 is not in it.
        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(CommittedText()),
                  records: string.Join(",", RecordJson("TA-2", "a-dead-session"),
                                            RecordJson("TA-3", "a-dead-session"),
                                            RecordJson("TA-SWAPPED", "a-dead-session")));

        var reader = Session();
        Assert.Equal(["TA-1", "TA-2", "TA-3"], reader.All().Select(r => r.ClientOrderId));
        Assert.NotNull(reader.PriorSession("TA-1"));
        Assert.Null(reader.PriorSession("TA-SWAPPED"));
        Assert.Contains("io:noted", reader.Token());   // flagged, but not a durability gap
        Assert.Null(reader.Trouble);

        // THE REASON IS WRITTEN DOWN BY THE OWNER. A reader flags its own answer and writes nothing
        // (see A_reader_changes_nothing_on_disk_even_when_no_owner_holds_the_witness), so the
        // sidecar line appears when a party entitled to write one next runs.
        first.Dispose();
        var owner = Session();
        Assert.True(owner.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("TA-1 is committed and not in it",
                        File.ReadAllText(Path.Combine(_dir, CoidWitness.ErrorLogName)));
    }

    /// <summary>
    /// A REWRITE IS A LEGAL TRANSITION FROM THE COMMITTED STATE, NOT A FILE OF THE RIGHT SHAPE.
    ///
    /// The membership rule skipped a LEADING RUN of committed identifiers the candidate did not
    /// have, on the argument that <c>Trim</c> takes one. When ALL of them are absent that loop
    /// walked off the end and there was nothing left to check: committed A/B/C at cap 3 against a
    /// perfectly lined-up X/Y/Z was adopted, and X/Y/Z are acknowledged, so they become cross-session
    /// proof for orders this product never submitted. The rule was validating the candidate's SHAPE.
    ///
    /// One rewrite does one thing: it adds at most one claim and, only at the cap, drops the ONE
    /// oldest to make room. Anything else is not a rewrite of this file.
    /// </summary>
    [Fact]
    public void A_candidate_that_replaces_the_whole_record_set_is_not_a_legal_rewrite()
    {
        var first = new CoidWitness(File_, null, cap: 3);
        foreach (var n in new[] { 1, 2, 3 })
        {
            first.Submitting($"TA-{n}", "SIM", "ES", "Buy", 1m, null);
            first.Identified($"TA-{n}", $"BRK-{n}");
        }
        Assert.Equal(["TA-1", "TA-2", "TA-3"], CommittedIds());

        // At the cap, correct predecessor, correct generation — and not one committed claim in it.
        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(CommittedText()),
                  records: string.Join(",", RecordJson("TA-X", "a-dead-session"),
                                            RecordJson("TA-Y", "a-dead-session"),
                                            RecordJson("TA-Z", "a-dead-session")));

        var reader = new CoidWitness(File_, null, cap: 3);
        Assert.Null(reader.PriorSession("TA-X"));
        Assert.Equal(["TA-1", "TA-2", "TA-3"], reader.All().Select(r => r.ClientOrderId));
        Assert.DoesNotContain(reader.PriorSessionIds(16),
                              id => id.StartsWith("TA-X", StringComparison.Ordinal));
    }

    /// <summary>
    /// TWO RECORDS UNDER ONE IDENTIFIER MAKE <c>PriorSession</c> AMBIGUOUS — it answers with the
    /// first it finds, and which one that is depends on the order a foreign file happened to be
    /// written in. <c>Submitting</c> removes an existing id before adding it, so no rewrite this
    /// build produces can contain a duplicate; one that does was not produced by this build.
    /// </summary>
    [Fact]
    public void A_candidate_carrying_a_duplicate_identifier_is_not_a_legal_rewrite()
    {
        var first = Session();
        Submit(first, "TA-ONE");
        first.Identified("TA-ONE", "BRK-ONE");

        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(CommittedText()),
                  records: string.Join(",", RecordJson("TA-ONE", "a-dead-session"),
                                            RecordJson("TA-ONE", "a-dead-session")));

        var reader = Session();
        Assert.Equal(["TA-ONE"], reader.All().Select(r => r.ClientOrderId));
    }

    /// <summary>
    /// ONE REWRITE ADDS ONE CLAIM. A candidate carrying three new identifiers on top of the
    /// committed set is not the rewrite this file was about to become — it is somebody's whole
    /// history, and each of those identifiers is acknowledged.
    /// </summary>
    [Fact]
    public void A_candidate_that_adds_more_than_one_rewrite_can_is_ignored()
    {
        var first = Session();
        Submit(first, "TA-ONE");
        first.Identified("TA-ONE", "BRK-ONE");

        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(CommittedText()),
                  records: string.Join(",", RecordJson("TA-ONE", "a-dead-session"),
                                            RecordJson("TA-EXTRA-1", "a-dead-session"),
                                            RecordJson("TA-EXTRA-2", "a-dead-session"),
                                            RecordJson("TA-EXTRA-3", "a-dead-session")));

        var reader = Session();
        Assert.Equal(["TA-ONE"], reader.All().Select(r => r.ClientOrderId));
        Assert.Null(reader.PriorSession("TA-EXTRA-1"));
    }

    /// <summary>
    /// THE AT-CAP REWRITE IS REFUSED TOO, AND ROUND 5 CHANGED THIS ON PURPOSE.
    ///
    /// This test used to assert the opposite: at the cap a legitimate rewrite MUST drop the oldest
    /// record to make room, so a candidate missing a leading identifier was adopted rather than
    /// refused. That case only ever arises from a <c>Submitting</c> rewrite — the trim happens when a
    /// NEW claim arrives — and a temp is never adopted as a new claim, so there is nothing left to
    /// adopt: the order it describes was refused by <c>Place</c> and never sent.
    ///
    /// What the refusal costs is nothing, and that is the point: the committed file is kept whole,
    /// the phantom does not evict the oldest genuine claim to make room for itself, and no identifier
    /// this product never submitted reaches the cross-session reading.
    /// </summary>
    [Fact]
    public void An_at_cap_rewrite_carrying_a_new_claim_is_refused_and_costs_nothing()
    {
        var writer = new CoidWitness(File_, null, cap: 3);
        foreach (var n in new[] { 1, 2, 3 })
        {
            writer.Submitting($"TA-{n}", "SIM", "ES", "Buy", 1m, null);
            writer.Identified($"TA-{n}", $"BRK-{n}");
        }
        Assert.Equal(["TA-1", "TA-2", "TA-3"], CommittedIds());
        writer.Dispose();

        // TA-4 arrived, Trim dropped TA-1 off the front, the rename never landed — so Place refused
        // the TA-4 order and nothing carrying it was sent.
        WriteTemp(generation: CommittedGeneration() + 1, predecessor: Fingerprint(CommittedText()),
                  records: string.Join(",", RecordJson("TA-2", "a-dead-session"),
                                            RecordJson("TA-3", "a-dead-session"),
                                            RecordJson("TA-4", "a-dead-session")));

        var reader = new CoidWitness(File_, null, cap: 3);
        Assert.Equal(["TA-1", "TA-2", "TA-3"], reader.All().Select(r => r.ClientOrderId));
        Assert.Null(reader.PriorSession("TA-4"));
        Assert.Equal(["TA-1", "TA-2", "TA-3"], CommittedIds());
    }

    /// <summary>
    /// AND THE CROSS-CAP QUESTION IS GONE WITH IT, which is a better answer than the one round 4
    /// recorded and the round-5 review asked to have pinned.
    ///
    /// The old rule read THIS instance's cap to decide whether a missing oldest record was explained
    /// by a trim, while the candidate came from an instance whose cap it could not see — so the same
    /// temp was adopted or refused depending on which build read it, and the round-4 record named the
    /// affected direction backwards. A rewrite that trims is now refused by everybody, so the rule no
    /// longer reads the cap at all and both cross-cap directions behave identically.
    /// </summary>
    [Theory]
    [InlineData(5, 3)]
    [InlineData(3, 5)]
    public void An_at_cap_rewrite_is_refused_whatever_cap_the_reading_build_has(int writerCap, int readerCap)
    {
        var writer = new CoidWitness(File_, null, writerCap);
        for (var n = 1; n <= writerCap; n++)
        {
            writer.Submitting($"TA-{n}", "SIM", "ES", "Buy", 1m, null);
            writer.Identified($"TA-{n}", $"BRK-{n}");
        }
        var committedBefore = CommittedIds();
        writer.Dispose();

        var kept = Enumerable.Range(2, writerCap - 1).Select(n => RecordJson($"TA-{n}", "a-dead-session"));
        var fresh = RecordJson($"TA-{writerCap + 1}", "a-dead-session");
        WriteTemp(CommittedGeneration() + 1, Fingerprint(CommittedText()),
                  string.Join(",", kept.Append(fresh)));

        var reader = new CoidWitness(File_, null, readerCap);
        Assert.Equal(committedBefore, reader.All().Select(r => r.ClientOrderId));
        Assert.Null(reader.PriorSession($"TA-{writerCap + 1}"));
        Assert.Equal(committedBefore, CommittedIds());
    }

    /// <summary>
    /// Every viable candidate descends from the same commit and so carries the same generation —
    /// nothing in the files distinguishes them, and letting mtime pick means silently choosing
    /// between two rewrites that may hold different claims. One writer cannot produce this, so it
    /// means two writers or a copied file. Decline both and keep what is committed.
    /// </summary>
    [Fact]
    public void Two_rival_candidates_at_the_same_generation_are_both_declined()
    {
        var first = Session();
        Submit(first, "TA-COMMITTED");

        // Two rewrites that are each a legal transition — the committed claim, acknowledged — and
        // that disagree about the broker id. Nothing in the files says which one ATAS assigned.
        var generation = CommittedGeneration() + 1;
        var predecessor = Fingerprint(CommittedText());
        var session = first.SessionId;
        foreach (var (suffix, brk) in new[] { ("-a-1", "BRK-A"), ("-b-1", "BRK-B") })
            File.WriteAllText(File_ + ".tmp" + suffix,
                $$"""{"version":1,"generation":{{generation}},"predecessor":"{{predecessor}}","records":[{"client_order_id":"TA-COMMITTED","session_id":"{{session}}","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"{{brk}}","identified_at":"2026-01-01T00:00:01+00:00"}]}""");

        var reader = Session();
        Assert.Equal(["TA-COMMITTED"], reader.All().Select(r => r.ClientOrderId));
        Assert.Null(reader.All().Single().BrokerOrderId);   // neither broker id was believed
        Assert.Contains("io:noted", reader.Token());        // flagged, but not a durability gap
        Assert.Null(reader.Trouble);

        // THE REASON IS WRITTEN DOWN BY THE OWNER. A reader flags its own answer and writes nothing
        // (see A_reader_changes_nothing_on_disk_even_when_no_owner_holds_the_witness), so the
        // sidecar line appears when a party entitled to write one next runs.
        first.Dispose();
        var owner = Session();
        Assert.True(owner.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("rival uncommitted rewrites",
                        File.ReadAllText(Path.Combine(_dir, CoidWitness.ErrorLogName)));
    }

    /// <summary>
    /// The fingerprint proves the temp was derived from these exact committed bytes; the generation
    /// proves it is the rewrite that came immediately after them. A candidate with the right
    /// predecessor and the wrong place in the sequence is not this file's next state, however much
    /// of its content it shares.
    /// </summary>
    [Fact]
    public void A_temp_whose_generation_is_not_the_next_one_is_ignored()
    {
        var first = Session();
        Submit(first, "TA-REAL");
        first.Identified("TA-REAL", "BRK-REAL");

        WriteTemp(generation: CommittedGeneration() + 7, predecessor: Fingerprint(CommittedText()),
                  records: RecordJson("TA-GHOST", "some-dead-session"));

        var reader = Session();
        Assert.Null(reader.PriorSession("TA-GHOST"));
        Assert.Equal(["TA-REAL"], reader.All().Select(r => r.ClientOrderId));
    }

    /// <summary>
    /// Time no longer qualifies a candidate, only orders one — so a genuine failed rewrite is
    /// adopted even when the filesystem gives it the same timestamp as the committed file. That case
    /// is not hypothetical: FAT32 records 2-second timestamps, and the previous rule silently
    /// declined to recover on any filesystem coarse enough to tie.
    /// </summary>
    [Fact]
    public void A_failed_rewrite_is_adopted_when_the_timestamps_tie()
    {
        var refused = false;
        var stranded = Session(LandsUntil(() => refused));
        Assert.True(stranded.Submitting("TA-STRANDED", "SIM", "ES", "Buy", 1m, null));
        refused = true;
        stranded.Identified("TA-STRANDED", "BRK-STRANDED");
        File.SetLastWriteTimeUtc(Temps().Single(), File.GetLastWriteTimeUtc(File_));

        var reader = Session();
        Assert.Equal("BRK-STRANDED", reader.PriorSession("TA-STRANDED")!.BrokerOrderId);
    }

    /// <summary>
    /// And adopted when the clock went BACKWARDS between the commit and the failed rewrite — an NTP
    /// correction, a VM resuming, a dual-boot machine. Under a newest-wins rule the recovery is
    /// declined and the claim is lost for a reason that has nothing to do with the claim.
    /// </summary>
    [Fact]
    public void A_failed_rewrite_is_adopted_when_the_clock_went_backwards()
    {
        var refused = false;
        var stranded = Session(LandsUntil(() => refused));
        Assert.True(stranded.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));
        refused = true;
        stranded.Identified("TA-LIVE", "BRK-LIVE");
        File.SetLastWriteTimeUtc(Temps().Single(), File.GetLastWriteTimeUtc(File_).AddHours(-1));
        stranded.Dispose();

        var reader = Session();
        Assert.Equal("BRK-LIVE", reader.PriorSession("TA-LIVE")!.BrokerOrderId);
    }

    /// <summary>
    /// The genuine case, on top of a committed file rather than in place of one: the rewrite carries
    /// everything the commit had plus the claim that failed to land, and it is adopted.
    /// </summary>
    [Fact]
    public void A_failed_rewrite_on_top_of_a_committed_file_is_adopted()
    {
        var first = Session();
        Submit(first, "TA-ONE");
        first.Identified("TA-ONE", "BRK-ONE");

        // The run that owned the witness has ended — which is what a restart IS.
        first.Dispose();
        var refused = false;
        var stranded = Session(LandsUntil(() => refused));
        Assert.True(stranded.Submitting("TA-TWO", "SIM", "ES", "Buy", 1m, null));
        refused = true;
        stranded.Identified("TA-TWO", "BRK-TWO");

        var reader = Session();
        Assert.Equal(["TA-ONE", "TA-TWO"], reader.All().Select(r => r.ClientOrderId));
        Assert.Equal("BRK-TWO", reader.PriorSession("TA-TWO")!.BrokerOrderId);
    }

    /// <summary>
    /// The proof path, through a file that was never committed. Both saves are refused — the
    /// write-ahead claim and the broker id that completes it — so the acknowledged record exists
    /// only in the temp when the process ends. A recovered record has to be evidence exactly as a
    /// committed one is, or the recovery is bookkeeping rather than a fix.
    /// </summary>
    [Fact]
    public void A_record_recovered_from_an_uncommitted_rewrite_is_evidence_like_any_other()
    {
        var refused = false;
        var w = Session(LandsUntil(() => refused));
        Assert.True(w.Submitting("TA-STRANDED", "SIM123", "ES", "Buy", 1m, 4200.25m));

        refused = true;
        w.Identified("TA-STRANDED", "BRK-STRANDED");

        var next = Session();
        var record = next.PriorSession("TA-STRANDED");

        Assert.NotNull(record);
        Assert.Equal("BRK-STRANDED", record.BrokerOrderId);
        Assert.NotEqual(next.SessionId, record.SessionId);
        Assert.Equal(["TA-STRANDED"], next.PriorSessionIds(16));
    }

    /// <summary>
    /// The write-ahead record is what makes rule 1 answerable, so <c>Place</c> has to be able to
    /// find out whether the claim it just made is on disk — it runs BEFORE the order is handed to
    /// ATAS and can still refuse. This is the true direction.
    /// </summary>
    [Fact]
    public void Submitting_says_when_the_write_ahead_reached_the_disk()
    {
        var w = Session();
        Assert.True(w.Submitting("TA-OK", "SIM", "ES", "Buy", 1m, null));
        Assert.True(w.Submitting("TA-OK-2", "SIM", "ES", "Buy", 1m, null));
    }

    /// <summary>
    /// And the false direction, in all three of its forms. An order whose identifier could not be
    /// recorded must not be sent: the whole value of a write-ahead record is that it exists before
    /// the order does, and a claim that only ever lived in this process's memory is not one.
    /// </summary>
    [Fact]
    public void Submitting_says_when_the_write_ahead_did_not_reach_the_disk()
    {
        // The rewrite will not land. The claim is kept and it is recoverable, but it is not durable.
        Assert.False(Session(NeverLands).Submitting("TA-NOT-DURABLE", "SIM", "ES", "Buy", 1m, null));

        // No identifier to record.
        Assert.False(Session().Submitting("", "SIM", "ES", "Buy", 1m, null));

        // Nowhere at all to record one.
        Assert.False(new CoidWitness(path: null).Submitting("TA-INERT", "SIM", "ES", "Buy", 1m, null));
    }

    /// <summary>
    /// NOT SILENTLY. A rewrite that never lands is written down beside the witness, naming the file,
    /// the temp that holds the newer state and the claim at risk — because this assembly has no
    /// logger and may not acquire one (trap 34), and a durability gap nobody can see is the same as
    /// no gap until the day it matters.
    /// </summary>
    [Fact]
    public void A_rewrite_that_never_lands_is_written_down_where_it_can_be_found()
    {
        var w = Session(NeverLands);
        Submit(w, "TA-UNWRITABLE");

        Assert.NotNull(w.LastWriteFailure);
        Assert.Contains("TA-UNWRITABLE", w.LastWriteFailure);
        Assert.Contains(File_, w.LastWriteFailure);
        Assert.Contains(Temps().Single(), w.LastWriteFailure);
        Assert.Contains("io:failed", w.Token());

        var log = Path.Combine(_dir, CoidWitness.ErrorLogName);
        Assert.True(File.Exists(log), "the failure belongs on disk beside the witness");
        Assert.Contains("TA-UNWRITABLE", File.ReadAllText(log));
    }

    /// <summary>
    /// A CONFIDENT ZERO IS THE WORST ANSWER THIS FILE CAN GIVE. An interrupted rewrite with nothing
    /// committed beside it hands back no records — and so does a machine where nothing was ever
    /// submitted. They are opposite answers: the second one says this product never submitted the
    /// identifier being asked about, which is exactly the claim a lost witness must never make by
    /// accident. It used to report records:0, io:ok.
    /// </summary>
    [Fact]
    public void An_unreadable_rewrite_with_no_committed_file_is_not_a_confident_zero()
    {
        File.WriteAllText(File_ + ".tmp-interrupted",
            "{\"version\":1,\"generation\":1,\"records\":[{\"client_order");

        var w = Session();
        Assert.Empty(w.All());
        Assert.True(w.Unreadable);
        Assert.Contains("records:err", w.Token());
    }

    /// <summary>Nothing on disk is not a failed read: it is a clean "nothing was ever written".</summary>
    [Fact]
    public void An_absent_witness_file_is_not_an_unreadable_one()
    {
        var w = Session();
        Assert.Empty(w.All());
        Assert.False(w.Unreadable);
        Assert.Contains("records:0", w.Token());
        Assert.False(new CoidWitness(path: null).Unreadable);
    }

    /// <summary>
    /// A DURABILITY GAP THAT ENDED WHEN THE PROCESS DID IS THE ONE NOBODY WOULD SEE. The next run
    /// starts with a clean <c>LastWriteFailure</c> and a witness that looks perfect, so the only
    /// thing left saying a claim once failed to reach the disk is the sidecar — and it said it to
    /// nobody. The surface token now carries it, in the field that already exists and without adding
    /// one, so a probe splitting the report on spaces reads it exactly as before.
    /// </summary>
    [Fact]
    public void A_sidecar_left_by_an_earlier_run_makes_the_token_say_so()
    {
        var earlier = Session(NeverLands);
        Submit(earlier, "TA-GAP");
        Assert.Contains("io:failed", earlier.Token());
        Assert.True(File.Exists(Path.Combine(_dir, CoidWitness.ErrorLogName)));

        // The restart. A READER sees the gap the earlier run left, and changes nothing.
        foreach (var f in Temps()) Age(f);
        var next = Session();
        Assert.Contains("io:degraded", next.Token());
        Assert.DoesNotContain(' ', next.Token());
        Assert.Single(Temps());

        // Moving the leftover out of the candidate glob is a WRITE, so it belongs to the next OWNER
        // rather than to whoever happens to look — that is what stops one crash degrading the
        // witness for ever, and it is now done by the party entitled to do it.
        earlier.Dispose();
        var owner = Session();
        Assert.True(owner.Submitting("TA-LATER", "SIM", "ES", "Buy", 1m, null));
        Assert.Empty(Temps());

        // Cleared by deleting the sidecar, which takes effect at the next start.
        File.Delete(Path.Combine(_dir, CoidWitness.ErrorLogName));
        Assert.Contains("io:ok", Session().Token());
    }

    /// <summary>
    /// A line-oriented file gets one line. Most of what lands in it is an OS exception message and a
    /// path, and neither is under this product's control — a newline in the middle turns one event
    /// into two half-events and lets whatever follows pose as a fresh, timestamp-free record.
    /// </summary>
    [Fact]
    public void A_failure_message_cannot_forge_extra_lines_in_the_sidecar()
    {
        var w = Session((tmp, destination) =>
            throw new IOException("refused\n2026-01-01T00:00:00.0000000+00:00 ERROR everything is fine\r\u0007"));
        Submit(w, "TA-INJECT");

        var text = File.ReadAllText(Path.Combine(_dir, CoidWitness.ErrorLogName));
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("everything is fine\r", text);
        Assert.DoesNotContain('\u0007', text);
        Assert.Contains("TA-INJECT", text);
    }

    /// <summary>
    /// The claim at risk is the one being written, not the newest record on the list. For
    /// <c>Submitting</c> those are the same; for <c>Identified</c> they are not — it updates a
    /// record wherever it sits — so reading the last entry named an unrelated identifier and sent
    /// whoever was holding the sidecar looking for the wrong order.
    /// </summary>
    [Fact]
    public void The_sidecar_names_the_claim_that_was_at_risk_not_the_newest_one()
    {
        var landing = true;
        var w = Session((tmp, destination) =>
        {
            if (!landing) throw new FileNotFoundException("gone", tmp);
            File.Move(tmp, destination, overwrite: true);
        });

        Submit(w, "TA-FIRST");
        Submit(w, "TA-SECOND");
        landing = false;
        w.Identified("TA-FIRST", "BRK-FIRST");

        Assert.Contains("claim=TA-FIRST", w.LastWriteFailure);
        Assert.DoesNotContain("claim=TA-SECOND", w.LastWriteFailure);
    }

    /// <summary>
    /// When the temp is what failed to be written, it does not hold anything — saying it holds the
    /// newer state sends whoever reads the sidecar to a file that is absent or half-written, looking
    /// for a claim that is not in it.
    /// </summary>
    [Fact]
    public void A_temp_that_could_not_be_written_is_not_reported_as_holding_the_claim()
    {
        // The lock has to be takeable, or the refusal happens before the temp is ever attempted —
        // a witness that cannot take its own lock is refused earlier and for a different reason.
        var w = Session();
        Directory.CreateDirectory($"{File_}.tmp-{Environment.ProcessId}-{w.SessionId[..8]}-1");

        Submit(w, "TA-NO-TEMP");

        Assert.Contains("temp_not_written=", w.LastWriteFailure);
        Assert.DoesNotContain("temp_holding_newer_state=", w.LastWriteFailure);
    }

    /// <summary>
    /// A SAFETY EVENT IS NEVER DROPPED. The per-session quota used to silence everything after the
    /// 32nd line — including a later write-ahead or acknowledgement failure, which for a live order
    /// is the only cross-process record that the gap happened at all. The quota now applies to
    /// warnings and markers; failures always go in.
    /// </summary>
    [Fact]
    public void A_write_failure_is_never_dropped_by_the_sidecar_quota()
    {
        var w = Session((tmp, destination) => throw new FileNotFoundException("gone", tmp));
        for (var i = 0; i < 40; i++) Submit(w, $"TA-{i}");

        var text = File.ReadAllText(Path.Combine(_dir, CoidWitness.ErrorLogName));
        Assert.Contains("TA-39", text);
        Assert.True(File.ReadAllLines(Path.Combine(_dir, CoidWitness.ErrorLogName)).Length >= 40);
    }

    /// <summary>
    /// And across sessions the cap resets, so the bound that matters there is the file's size. It is
    /// restarted rather than trimmed: the newest failures are the ones worth keeping, and rewriting
    /// a log to drop its head is more file IO than a failing disk deserves.
    /// </summary>
    [Fact]
    public void An_oversized_sidecar_is_rotated_rather_than_thrown_away()
    {
        var log = Path.Combine(_dir, CoidWitness.ErrorLogName);
        File.WriteAllText(log, new string('x', 70 * 1024));

        var w = Session(VanishesUnless(() => false));
        Submit(w, "TA-BOUND");

        Assert.True(new FileInfo(log).Length < 4096, $"the sidecar is {new FileInfo(log).Length} bytes");
        Assert.Contains("TA-BOUND", File.ReadAllText(log));

        // ROTATED, NOT DELETED. Deleting was fine while the quota capped what could be lost; with
        // failures unrationed the file being thrown away is the one holding them.
        Assert.True(File.Exists(log + ".1"), "the previous window of history is kept");
    }

    /// <summary>
    /// THE QUOTA USED TO SILENCE THE THING THE FILE EXISTS FOR. Thirty-two lines into a session it
    /// stopped writing — and the next event might be an acknowledgement that never reached the disk
    /// for an order that is live at the broker, which this file is the only cross-process record of.
    /// Warnings and markers are rationed; failures are not, and a failure after a RESOLVED marker
    /// reopens the gap for the next session to see.
    /// </summary>
    [Fact]
    public void A_safety_event_after_the_quota_and_after_a_resolved_marker_is_still_recorded()
    {
        // THE QUOTA IS SPENT ON WARNINGS, WHICH IS WHAT IT RATIONS. This test used to spend it on
        // 31 failures — and failures have never counted against it, so the quota was never
        // exhausted and the assertion below proved nothing. 40 foreign leftovers produce 40
        // quarantine warnings against a 32-line allowance.
        var seed = Session();
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        seed.Dispose();
        for (var i = 0; i < 40; i++) WriteForeignLeftover(i);

        var lands = true;
        var w = Session(VanishesUnless(() => lands));
        Assert.True(w.Submitting("TA-OK", "SIM", "ES", "Buy", 1m, null));

        // Rationed: exactly the allowance, and the 33rd warning is absent.
        Assert.Equal(32, SidecarLines().Count(l => l.Contains("ignored ")));

        // Unrationed: a durability gap after the allowance is spent still reaches the file, and the
        // next start sees it.
        lands = false;
        Assert.False(w.Submitting("TA-AFTER-QUOTA", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("TA-AFTER-QUOTA", File.ReadAllText(Sidecar));
        w.Dispose();
        Assert.NotNull(Session().Trouble);
    }

    /// <summary>
    /// A ROW THAT CRIES WOLF IS A ROW NOBODY READS THE DAY IT IS RIGHT. The failure was permanent
    /// for the life of the process, so a contended replace that succeeded on the very next order
    /// left the ATAS bridge health row saying "orders are being refused" while every order was going
    /// through. A failure superseded by a commit carrying the same records no longer describes
    /// anything.
    /// </summary>
    [Fact]
    public void A_write_failure_that_resolves_stops_being_reported()
    {
        var refused = true;
        var w = Session(LandsUntil(() => refused));

        Assert.False(w.Submitting("TA-ONE", "SIM", "ES", "Buy", 1m, null));
        Assert.NotNull(w.LastWriteFailure);
        Assert.Contains("io:failed", w.Token());

        refused = false;
        Assert.True(w.Submitting("TA-TWO", "SIM", "ES", "Buy", 1m, null));

        Assert.Null(w.LastWriteFailure);
        Assert.DoesNotContain("io:failed", w.Token());
    }

    /// <summary>
    /// ONE CRASH USED TO DEGRADE THE WITNESS FOR EVER. The rejected leftover stayed where it was,
    /// every later session rejected it again, every rejection wrote another sidecar line, and the
    /// sidecar's existence was what made the witness look degraded — so the probe shouted about a
    /// file that harmed nothing, permanently. It is reported once and moved out of the candidate
    /// glob, kept rather than deleted so somebody can still look at it.
    /// </summary>
    [Fact]
    public void A_rejected_leftover_is_reported_once_and_moved_aside()
    {
        var first = Session();
        Submit(first, "TA-REAL");

        WriteTemp(generation: 99, predecessor: "some-other-witness", records: RecordJson("TA-GHOST", "s"));
        Age(Temps().Single());

        // Moving it aside is a write, so it is the next OWNER that does it — a reader only reads.
        first.Dispose();
        var owner = Session();
        Assert.True(owner.Submitting("TA-LATER", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(["TA-REAL", "TA-LATER"], owner.All().Select(r => r.ClientOrderId));
        Assert.Empty(Temps());
        Assert.Single(Directory.GetFiles(_dir, "coid-witness.json.rejected-*"));

        var linesAfterFirstLook = File.ReadAllLines(Path.Combine(_dir, CoidWitness.ErrorLogName)).Length;

        // Every later session finds nothing to complain about.
        owner.Dispose();
        var later = Session();
        Assert.True(later.Submitting("TA-LATEST", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(linesAfterFirstLook,
                     File.ReadAllLines(Path.Combine(_dir, CoidWitness.ErrorLogName)).Length);
    }

    /// <summary>
    /// A candidate written moments ago may be another process between its write and its rename.
    /// Moving it would make that writer's replace fail — safe in itself, but a reader has no
    /// business breaking a writer.
    /// </summary>
    [Fact]
    public void A_candidate_written_moments_ago_is_left_where_it_is()
    {
        var first = Session();
        Submit(first, "TA-REAL");
        WriteTemp(generation: 99, predecessor: "some-other-witness", records: RecordJson("TA-GHOST", "s"),
                  at: DateTime.UtcNow);

        var reader = Session();
        Assert.Equal(["TA-REAL"], reader.All().Select(r => r.ClientOrderId));
        Assert.Single(Temps());
        Assert.Empty(Directory.GetFiles(_dir, "coid-witness.json.rejected-*"));
    }

    /// <summary>
    /// The recovered rewrite is a duplicate once its records are committed, not a safety net. Left
    /// in place it is re-examined and re-rejected by every later session — the same permanent
    /// degradation, one step further along.
    /// </summary>
    [Fact]
    public void An_adopted_rewrite_is_deleted_once_it_has_been_committed()
    {
        var refused = false;
        var w = Session(LandsUntil(() => refused));
        Assert.True(w.Submitting("TA-ONE", "SIM", "ES", "Buy", 1m, null));
        refused = true;
        w.Identified("TA-ONE", "BRK-ONE");
        Assert.Single(Temps());

        // A new session recovers it, then writes something of its own.
        // The run that owned the witness has ended — which is what a restart IS.
        w.Dispose();
        var next = Session();
        Assert.Equal("BRK-ONE", next.PriorSession("TA-ONE")!.BrokerOrderId);
        Assert.True(next.Submitting("TA-TWO", "SIM", "ES", "Buy", 1m, null));

        Assert.Empty(Temps());
        Assert.Contains("TA-ONE", CommittedIds());
        Assert.Contains("TA-TWO", CommittedIds());
    }

    /// <summary>
    /// Degraded asks whether there is an UNRESOLVED failure, not whether anything ever went wrong. A
    /// witness that has since committed cleanly is working, and a state that stays on for ever is
    /// useless the moment it matters.
    /// </summary>
    [Fact]
    public void A_gap_that_was_committed_over_stops_reading_as_degraded()
    {
        var refused = true;
        var w = Session(LandsUntil(() => refused));
        Assert.False(w.Submitting("TA-ONE", "SIM", "ES", "Buy", 1m, null));

        refused = false;
        Assert.True(w.Submitting("TA-TWO", "SIM", "ES", "Buy", 1m, null));

        // The history is still on disk; the last line says the problem ended.
        var log = Path.Combine(_dir, CoidWitness.ErrorLogName);
        Assert.True(File.Exists(log));
        Assert.Contains("RESOLVED", File.ReadAllLines(log)[^1]);

        // NOT DEGRADED any more — which is the property. The sidecar still has history in it, so
        // the token says `io:noted` rather than `io:ok`: the file is worth looking at, and no gap
        // is open.
        Assert.DoesNotContain("io:degraded", Session().Token());
        Assert.Contains("io:noted", Session().Token());
        Assert.Null(Session().Trouble);
    }

    /// <summary>
    /// THE STATE THAT WAS INVISIBLE TO THE APP. A failure in an earlier session leaves nothing in
    /// memory — the process that saw it is gone — so the hello carried null, and the ATAS bridge row
    /// said READY over a witness with an unresolved durability gap. All three states have to reach
    /// the wire, including the witness that has nowhere to live at all, which would otherwise refuse
    /// every order in silence.
    /// </summary>
    [Fact]
    public void The_hello_carries_a_gap_left_by_an_earlier_run()
    {
        Assert.Null(Session().Trouble);

        var earlier = Session(NeverLands);
        Submit(earlier, "TA-GAP");
        Assert.Contains("did not land", earlier.Trouble);

        // The restart: nothing in memory, and the gap is still real.
        foreach (var f in Temps()) Age(f);
        var next = Session();
        Assert.NotNull(next.Trouble);
        Assert.Contains(CoidWitness.ErrorLogName, next.Trouble);

        // A witness with nowhere to live says so rather than refusing every order in silence.
        Assert.Contains("nowhere to live", new CoidWitness(path: null).Trouble);
    }

    /// <summary>And it goes quiet again once a clean commit has resolved the gap.</summary>
    [Fact]
    public void The_hello_stops_carrying_a_gap_that_was_resolved()
    {
        var refused = true;
        var w = Session(LandsUntil(() => refused));
        Assert.False(w.Submitting("TA-ONE", "SIM", "ES", "Buy", 1m, null));
        Assert.NotNull(w.Trouble);

        refused = false;
        Assert.True(w.Submitting("TA-TWO", "SIM", "ES", "Buy", 1m, null));

        Assert.Null(w.Trouble);
        Assert.Null(Session().Trouble);
    }

    /// <summary>
    /// THE FINGERPRINT HAS TO DISCRIMINATE, AND NOTHING ELSE IN THIS FILE CHECKS THAT IT DOES.
    ///
    /// FNV-1a is a multiply and an xor per byte, and the MULTIPLY is where all of the discrimination
    /// lives. Change the prime to 1 and the whole thing collapses to an xor fold into the low byte:
    /// at most 256 distinct values, every permutation of the same bytes colliding. Every lineage
    /// test in this file goes on passing, because each of them compares a fingerprint against itself
    /// — and the lineage rule quietly becomes "any candidate whose parent has the same bytes in any
    /// order", which is most of the way back to accepting a foreign file.
    ///
    /// The decision to keep FNV-1a rather than a cryptographic digest stands (see
    /// <see cref="CoidWitness.Fingerprint"/>: it authenticates lineage, not authorship, and anyone
    /// who can write the temp can write the committed file). What the decision requires is that the
    /// arithmetic actually works, which is what this measures.
    /// </summary>
    [Fact]
    public void The_lineage_fingerprint_discriminates()
    {
        // Order-sensitive: an xor fold cannot tell these apart.
        Assert.NotEqual(CoidWitness.Fingerprint("ab"), CoidWitness.Fingerprint("ba"));
        Assert.NotEqual(CoidWitness.Fingerprint("TA-12"), CoidWitness.Fingerprint("TA-21"));

        // And wide: 4000 distinct witness-shaped texts, 4000 distinct fingerprints. An 8-bit fold
        // has 256 values to work with and collides inside the first few hundred.
        var seen = Enumerable.Range(0, 4000)
            .Select(i => CoidWitness.Fingerprint($$"""{"version":1,"generation":{{i}},"records":[]}"""))
            .Distinct().Count();
        Assert.Equal(4000, seen);

        // It is the value written into the file, so the file's own predecessor must equal it.
        var w = Session();
        Submit(w, "TA-ONE");
        var committed = CommittedText();
        Submit(w, "TA-TWO");
        Assert.Equal(CoidWitness.Fingerprint(committed),
                     System.Text.Json.JsonDocument.Parse(CommittedText()).RootElement
                         .GetProperty("predecessor").GetString());
    }

    /// <summary>
    /// The superseding rewrite is on disk BEFORE the one it replaces is removed. Swapping those two
    /// leaves an instant with no temp holding the claim at all — and if the new write is what fails,
    /// that instant is permanent.
    /// </summary>
    [Fact]
    public void A_failed_write_does_not_leave_the_earlier_rewrite_swept_away()
    {
        var seed = Session();
        Submit(seed, "TA-SEED");

        // The run that owned the witness has ended — which is what a restart IS.
        seed.Dispose();
        var w = Session(NeverLands);
        Submit(w, "TA-ONE");
        Assert.Single(Temps());

        // Block the next rewrite's own temp path, so writing it fails rather than the replace.
        Directory.CreateDirectory($"{File_}.tmp-{Environment.ProcessId}-{w.SessionId[..8]}-2");
        Assert.False(w.Submitting("TA-TWO", "SIM", "ES", "Buy", 1m, null));

        Assert.Contains(Temps(), t => t.EndsWith("-1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other unreadable branch: a candidate that cannot be opened at all, as opposed to one that
    /// opens and does not parse. Both have to count as a failed read, or a witness with nothing
    /// committed beside it reports a confident zero.
    ///
    /// UNIX ONLY, AND SAID RATHER THAN HIDDEN: there is no portable way to make a file unopenable on
    /// Windows from inside a test, so on Windows this asserts nothing. The parse branch beside it is
    /// covered everywhere.
    /// </summary>
    [Fact]
    public void A_candidate_that_cannot_be_opened_is_a_failed_read_not_an_empty_one()
    {
        var candidate = File_ + ".tmp-unreadable";
        File.WriteAllText(candidate, "{\"version\":1,\"generation\":1,\"records\":[]}");

        // INJECTED AT THE SEAM, so this runs on Windows too — which is the platform the product
        // trades on and the one where the old form of this test returned immediately and asserted
        // nothing. See CoidWitness._open.
        var w = new CoidWitness(File_, null, CoidWitness.DefaultCap, null,
            open: p => p == candidate
                ? throw new UnauthorizedAccessException("Access to the path is denied.")
                : new FileStream(p, FileMode.Open, FileAccess.Read,
                                 FileShare.ReadWrite | FileShare.Delete));

        Assert.Empty(w.All());
        Assert.True(w.Unreadable);
        Assert.Contains("records:err", w.Token());
    }

    /// <summary>
    /// AND THE SEAM IS NOT A LIE, checked where the real refusal can be produced. A seam that stopped
    /// resembling what the operating system actually does would make the test above assert nothing
    /// about anything — so on Unix the same case is driven through a real unopenable file, with no
    /// injection at all, and has to reach the same answer.
    /// </summary>
    [Fact]
    public void A_real_unopenable_candidate_reaches_the_same_answer()
    {
        if (OperatingSystem.IsWindows()) return;   // no portable way to produce it here; the seam covers it

        var candidate = File_ + ".tmp-unreadable";
        File.WriteAllText(candidate, "{\"version\":1,\"generation\":1,\"records\":[]}");
        File.SetUnixFileMode(candidate, UnixFileMode.None);

        var w = Session();
        Assert.Empty(w.All());
        Assert.True(w.Unreadable);
        Assert.Contains("records:err", w.Token());
    }

    /// <summary>
    /// READERS NEVER WRITE. The scan runs on every read path — <c>PriorSession</c> on ATAS's event
    /// thread, <c>All()</c> from the probe in another process — and a reader that adopted,
    /// quarantined and wrote the sidecar could do all three in the middle of the owner's rewrite:
    /// the candidate it "recovers" is the rewrite in flight, the sidecar line it leaves says a gap
    /// happened, and the rewrite then commits perfectly cleanly, leaving an unresolved failure
    /// recorded about nothing. A process that cannot take the lock reads and answers, and that is
    /// all it does.
    /// </summary>
    [Fact]
    public void A_reader_that_does_not_own_the_witness_changes_nothing_on_disk()
    {
        var owner = Session();
        Submit(owner, "TA-SEED");

        // A leftover the owner would quarantine and report.
        WriteTemp(generation: 99, predecessor: "some-other-witness", records: RecordJson("TA-GHOST", "s"));
        Age(Temps().Single());

        // The owner is alive and holds its lease — no hand-held file handle needed any more, which
        // is the round-5 change: ownership is a lifetime, so "somebody else owns it" is the
        // ordinary state of the world for every reader.
        var reader = Session();
        Assert.Equal(["TA-SEED"], reader.All().Select(r => r.ClientOrderId));
        Assert.Null(reader.PriorSession("TA-GHOST"));

        Assert.Single(Temps());
        Assert.Empty(Directory.GetFiles(_dir, "coid-witness.json.rejected-*"));
        Assert.False(File.Exists(Path.Combine(_dir, CoidWitness.ErrorLogName)),
                     "a reader wrote the sidecar");
    }

    /// <summary>
    /// And the owner re-reads the tail before marking a gap resolved. The flag was decided when this
    /// instance loaded and the file has been open to anything since; a second RESOLVED under a first
    /// says a gap was closed twice, and writing one at all when the tail already says so means
    /// reporting on a gap that was not this instance's.
    /// </summary>
    [Fact]
    public void A_resolved_marker_is_not_appended_over_one_that_is_already_there()
    {
        var lands = false;
        var w = Session(VanishesUnless(() => lands));
        Assert.False(w.Submitting("TA-ONE", "SIM", "ES", "Buy", 1m, null));

        var log = Path.Combine(_dir, CoidWitness.ErrorLogName);
        File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} RESOLVED coid-witness committed cleanly " +
                                $"after the failures above.{Environment.NewLine}");

        lands = true;
        Assert.True(w.Submitting("TA-TWO", "SIM", "ES", "Buy", 1m, null));

        Assert.Equal(1, File.ReadAllLines(log).Count(l => l.Contains("RESOLVED")));
    }

    /// <summary>The sidecar lives beside the witness, so a person told about one has found the other.</summary>
    [Fact]
    public void The_sidecar_sits_beside_the_witness_file()
    {
        Assert.Equal(Path.Combine(_dir, CoidWitness.ErrorLogName), Session().ErrorLogPath);
        Assert.Null(new CoidWitness(path: null).ErrorLogPath);
    }

    /// <summary>
    /// THE LINE THAT ENDS A DEGRADATION IS NOT A DIAGNOSTIC, AND THE QUOTA MUST NOT RATION IT.
    ///
    /// `RESOLVED` was written as a warning, so a session that had already spent its 32 non-safety
    /// lines on quarantine notes could not write it. `_degraded` was cleared in memory while the
    /// file's last word was still an open gap, so the NEXT process reported DEGRADED over a witness
    /// that had committed cleanly — and `Describe()` computes `SupportsClientOrderId` from exactly
    /// that. Permanent once the 64 `.rejected-n` slots are gone, because then the surplus is
    /// re-rejected and the quota re-spent every session.
    ///
    /// The marker is a state transition, at most once per session and already guarded against
    /// duplication by the re-read in `Settled`, so it cannot flood.
    /// </summary>
    [Fact]
    public void A_clean_commit_says_the_gap_is_closed_even_after_the_warning_quota_is_spent()
    {
        var seed = Session();
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        seed.Dispose();

        // A REAL durability gap from an earlier run — a safety event, not a warning.
        var failed = Session(NeverLands);
        Assert.False(failed.Submitting("TA-GAP", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains(SidecarLines(), l => l.Contains("did not land"));

        // 40 stale foreign temps: one quarantine warning each, well past the 32-line quota.
        for (var i = 0; i < 40; i++) WriteForeignLeftover(i);

        failed.Dispose();
        var next = Session();
        Assert.True(next.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));

        // The warnings ARE still rationed — that half of the rule is the design.
        Assert.Equal(32, SidecarLines().Count(l => l.Contains("ignored ")));

        // And the witness says the gap is closed, so the next start does not report one.
        Assert.Contains("RESOLVED", SidecarLines()[^1]);
        next.Dispose();
        Assert.Null(Session().Trouble);
    }

    /// <summary>
    /// A QUARANTINE WARNING IS NOT A DURABILITY GAP, AND CONFLATING THEM IS WHAT MADE THE ROW CRY
    /// WOLF. `Note()` set the degraded state for every line it wrote, so a foreign leftover moved
    /// aside — a tidy-up — was indistinguishable downstream from a claim that never reached the
    /// disk. `Trouble` non-null is what puts DEGRADED on the ATAS bridge row and drops
    /// `SupportsClientOrderId` to false, so a machine with an old temp beside the witness reported
    /// that orders were being refused while every order went through.
    ///
    /// The zero is still FLAGGED — `io:noted` says the sidecar has something in it — but only an
    /// unresolved SAFETY line reaches `Trouble`.
    /// </summary>
    [Fact]
    public void A_quarantined_leftover_is_noted_without_claiming_a_durability_gap()
    {
        var owner = Session();
        Assert.True(owner.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        WriteForeignLeftover(1);
        owner.Dispose();

        var next = Session();
        Assert.True(next.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains(SidecarLines(), l => l.Contains("ignored "));

        // Written down, visible in the token — and NOT a reason to tell the operator that orders
        // are being refused.
        Assert.Contains("io:noted", next.Token());
        Assert.Null(next.Trouble);
        next.Dispose();
        Assert.Null(Session().Trouble);
    }

    // -------------------------------------------------------------- two writers, one file

    /// <summary>
    /// Trap 35 says a second bridge can be running, and with one shared temp name the two interleave
    /// inside a rewrite: B's write lands between A's write and A's rename, so A renames B's content
    /// onto the file. Distinct names per writer are what stop that, and this is the cheapest
    /// statement of it — two writers, two temps.
    /// </summary>
    [Fact]
    public void Two_writers_do_not_share_a_temp_name()
    {
        var a = Session(NeverLands);
        Submit(a, "TA-A");

        // A second LIVE writer never reaches the temp stage at all now — it is refused before it
        // writes anything (see A_second_live_writer_is_refused_even_when_it_never_overlaps_a_call).
        // The per-writer name still matters across RUNS: the first run's stranded temp must not be
        // the name the next run writes, or the next run's replace consumes it.
        var blocked = Session(NeverLands);
        Assert.False(blocked.Submitting("TA-BLOCKED", "SIM", "ES", "Buy", 1m, null));
        Assert.Single(Temps());

        a.Dispose();
        var b = Session(NeverLands);
        Submit(b, "TA-B");

        Assert.Equal(2, Temps().Length);
        Assert.Equal(2, Temps().Select(Path.GetFileName).Distinct().Count());
    }

    /// <summary>
    /// ONE OWNER PER WITNESS IS A LIFETIME LEASE, NOT A PER-CALL LOCK — and the difference is a
    /// second live bridge writing the file.
    ///
    /// The lock used to be taken and released inside each call, so two live instances that simply
    /// did not overlap took turns successfully: A writes, releases; B writes, releases. Every test
    /// that claimed to prove "one owner" simulated the rival by holding the lock FROM THE TEST, so
    /// none of them exercised the sequence that actually happens — two bridges on one machine, each
    /// perfectly polite, alternating. The owner now holds an exclusive handle from its first write
    /// until it is disposed, so the second instance is refused on every call rather than only when
    /// it collides with one.
    ///
    /// The OS releases the handle when the process dies, so a crashed bridge strands nothing.
    /// </summary>
    [Fact]
    public void A_second_live_writer_is_refused_even_when_it_never_overlaps_a_call()
    {
        var a = Session();
        Assert.True(a.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null));

        // A is alive and idle. Nothing external holds the lock; B is simply not the owner.
        var b = Session();
        Assert.False(b.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("another writer owns this witness", b.Trouble);
        Assert.Equal(["TA-A"], CommittedIds());

        // BOTH DIRECTIONS. A lease that could never be handed on would be a witness one crash could
        // wedge for ever. When the owner lets go — Dispose here, process death in the field, and the
        // OS does that one whether the process asked or not — the next writer takes it.
        a.Dispose();
        var c = Session();
        Assert.True(c.Submitting("TA-C", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(["TA-A", "TA-C"], CommittedIds());
        Assert.Null(c.Trouble);
    }

    /// <summary>
    /// WHAT THE LEASE IS ACTUALLY FOR, DRIVEN THROUGH THE ONE INTERLEAVING THAT SHOWS IT. Lifted
    /// from the round-4 adversarial-verify leg, whose mutant MV2 — `FileShare.None` →
    /// `FileShare.ReadWrite`, i.e. the lock stops excluding anybody — left all 80 tests green.
    ///
    /// Writer B is between its compare-and-swap and its rename when writer A runs an entire
    /// `Submitting`. Without exclusion A is told its write-ahead record is DURABLE — so `Place`
    /// sends that order — and B's rename then commits a file that does not contain A's claim. An
    /// order on the wire with no record behind it is the one outcome rule 1 exists to prevent.
    /// </summary>
    [Fact]
    public void The_lease_is_what_stops_a_claim_reported_durable_from_being_dropped()
    {
        var seed = Session();
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        seed.Dispose();

        CoidWitness? a = null;
        bool? aSaidDurable = null;

        // B's rename is the hook: A's whole claim runs inside B's replace, after B's CAS passed.
        var b = Session((tmp, dest) =>
        {
            if (a is not null && aSaidDurable is null)
                aSaidDurable = a.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null);
            File.Move(tmp, dest, overwrite: true);
        });
        a = Session();

        // Both load the same committed content before either writes.
        _ = b.All();
        _ = a.All();

        b.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null);

        // THE INVARIANT: a claim Submitting called durable is on the committed file.
        Assert.NotNull(aSaidDurable);
        if (aSaidDurable == true) Assert.Contains("TA-A", CommittedIds());
    }

    /// <summary>
    /// A READER CHANGES NOTHING ON DISK — INCLUDING WHEN NOBODY OWNS THE WITNESS.
    ///
    /// Item 4 asserted only that a reader does not write while somebody ELSE holds the lock. The
    /// read paths took the lock opportunistically and treated getting it as being the owner, so a
    /// reader over an unowned witness quarantined temps, created the lock file and wrote the
    /// sidecar. `tools/probe` is the diagnostic an operator runs when the bridge is NOT running,
    /// which is exactly when it became the owner — and the line it left ("could not write the
    /// write-ahead record") misdescribes a tidy-up as a durability gap.
    ///
    /// Reading is now read-only in the literal sense: the whole directory is byte-identical after.
    /// </summary>
    [Fact]
    public void A_reader_changes_nothing_on_disk_even_when_no_owner_holds_the_witness()
    {
        var owner = Session();
        Assert.True(owner.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        owner.Identified("TA-SEED", "BRK-SEED");
        owner.Dispose();                       // nobody owns the witness now — the probe's own case
        WriteForeignLeftover(1);

        var before = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();

        // Everything a reader does, in one instance, with no writer running and no lock held.
        var reader = Session();
        Assert.Equal(["TA-SEED"], reader.All().Select(r => r.ClientOrderId));
        Assert.Equal("BRK-SEED", reader.PriorSession("TA-SEED")!.BrokerOrderId);
        _ = reader.PriorSessionIds(16);
        _ = reader.Token();
        _ = reader.Trouble;
        _ = reader.Unreadable;

        // Byte-for-byte the same directory: nothing quarantined, nothing renamed, nothing appended.
        Assert.Equal(before, Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.False(File.Exists(Sidecar), "a reader wrote the sidecar");
        Assert.Empty(Directory.GetFiles(_dir, "coid-witness.json.rejected-*"));

        // And it still answered about the leftover: the zero-is-flagged rule needs the reader to
        // KNOW it refused something, which it can do without writing it down.
        Assert.Contains("io:noted", reader.Token());
    }

    /// <summary>
    /// ONE OWNER PER WITNESS, AND A SECOND WRITER IS REFUSED RATHER THAN MERGED WITH.
    ///
    /// Round 3 tried to make two writers work: compare-and-swap, then rebase onto whatever the other
    /// one committed. Every interleaving that design has to survive is an interleaving of a scenario
    /// the product does not support — trap 35 calls a second bridge a misconfiguration — and
    /// hardening a path nobody is meant to take costs correctness arguments for ever. Refusing it
    /// costs one branch, and the refusal is the safe direction: <c>Place</c> declines the order
    /// rather than sending one whose write-ahead record is racing somebody.
    ///
    /// What must hold is what the verifier's three-process harness measures: nothing lost and
    /// nothing phantom. The owner's file is exactly as the owner left it.
    /// </summary>
    [Fact]
    public void A_second_writer_is_refused_rather_than_merged_with()
    {
        var owner = Session();
        Submit(owner, "TA-SEED");
        owner.Dispose();

        // Something that is not a CoidWitness holds the witness for the duration — a backup tool, a
        // scanner, an operator's shell. The rival-bridge case is the lease, one test up.
        using var held = new FileStream(File_ + ".lock", FileMode.OpenOrCreate,
                                        FileAccess.ReadWrite, FileShare.None);

        var second = Session();
        Assert.False(second.Submitting("TA-SECOND", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("another writer owns this witness", second.Trouble);
        Assert.Contains(".lock", second.Trouble);

        // Nothing lost, nothing phantom.
        Assert.Equal(["TA-SEED"], CommittedIds());
        Assert.Null(Session().PriorSession("TA-SECOND"));
    }
    /// <summary>
    /// REFUSED WITHOUT THE LOCK — AND THE ACKNOWLEDGEMENT PATH IS REFUSED TOO.
    ///
    /// One owner per witness. <see cref="CoidWitnessTests.A_second_writer_is_refused_rather_than_merged_with"/>
    /// states that for the claim; nothing stated it for the acknowledgement, and that is the harder
    /// half to argue. There the record is this session's OWN, the broker id is real, and the write is
    /// a small in-place edit — every reason to let it through. It is still refused, because a writer
    /// that does not own the file cannot know what it would be overwriting, and a witness this
    /// process does not own is one it may not touch at all.
    ///
    /// The direction is the safe one: the id stays in memory for a save that does own the file, and
    /// nothing on disk is invented in the meantime.
    /// </summary>
    [Fact]
    public void A_claim_and_an_acknowledgement_are_both_refused_without_the_lock()
    {
        var w = Session();
        Assert.True(w.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));

        // THE SHAPE THIS ACTUALLY HAS. This bridge is taken down, a second one starts and takes the
        // witness — and this one's order-event fan is still running against ATAS. No hand-held file
        // handle: the rival is a real witness holding a real lease.
        w.Dispose();
        var other = Session();
        Assert.True(other.Submitting("TA-OTHER", "SIM", "ES", "Buy", 1m, null));

        // THE CLAIM PATH. Refused, and Place gets the sentence it needs to say why.
        Assert.False(w.Submitting("TA-BLOCKED", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("another writer owns this witness", w.Trouble);
        Assert.DoesNotContain(w.All(), r => r.ClientOrderId == "TA-BLOCKED");

        // THE ACKNOWLEDGEMENT PATH. Same session, its own record, a real broker id — refused all the
        // same, and nothing reaches the file.
        w.Identified("TA-LIVE", "BRK-LIVE");
        Assert.Null(w.All().Single(r => r.ClientOrderId == "TA-LIVE").BrokerOrderId);
        Assert.Null(CommittedBrokerId("TA-LIVE"));

        // Nothing lost, nothing phantom: the file is exactly as its owner left it.
        Assert.Equal(["TA-LIVE", "TA-OTHER"], CommittedIds());
    }

    /// <summary>
    /// A COMPARE-AND-SWAP MISS IS A REFUSAL ON BOTH PATHS, AND IT IS NOT A MERGE.
    ///
    /// This writer holds the lock, so a file that changed underneath it is not another bridge playing
    /// by the rules — it is an older build, a hand edit or a restored backup. There is no safe merge
    /// with a party whose semantics this build does not know, so the rewrite is refused and the
    /// foreign file is left exactly as it was found.
    ///
    /// <see cref="CoidWitnessTests.A_witness_file_changed_by_something_else_is_refused_not_merged"/>
    /// states this for the claim. The acknowledgement path runs through the same
    /// <c>Save</c> and had nothing asserting it.
    /// </summary>
    [Fact]
    public void A_claim_and_an_acknowledgement_are_both_refused_when_the_file_changed_underneath()
    {
        var w = Session();
        Assert.True(w.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));

        var foreign = "{\"version\":1,\"generation\":9,\"predecessor\":null,\"records\":[" +
                      RecordJson("TA-FOREIGN", "somebody-else") + "]}";
        File.WriteAllText(File_, foreign);

        // THE ACKNOWLEDGEMENT PATH.
        w.Identified("TA-LIVE", "BRK-LIVE");
        Assert.Contains("changed underneath this writer", w.LastWriteFailure);
        Assert.Equal(foreign, File.ReadAllText(File_));

        // THE CLAIM PATH, from the same state.
        Assert.False(w.Submitting("TA-AFTER", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(foreign, File.ReadAllText(File_));
        Assert.Contains("io:failed", w.Token());
    }

    /// <summary>
    /// A file that changed under a writer holding the lock cannot be another bridge playing by the
    /// rules — it is an older build, a hand edit or a restored backup. There is no safe merge with a
    /// party whose semantics are unknown, so the write is refused.
    /// </summary>
    [Fact]
    public void A_witness_file_changed_by_something_else_is_refused_not_merged()
    {
        var w = Session();
        Assert.True(w.Submitting("TA-ONE", "SIM", "ES", "Buy", 1m, null));

        // Something that is not this build rewrites the file between saves.
        File.WriteAllText(File_,
            "{\"version\":1,\"generation\":9,\"predecessor\":null,\"records\":[" +
            RecordJson("TA-FOREIGN", "somebody-else") + "]}");

        Assert.False(w.Submitting("TA-TWO", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("changed underneath this writer", w.LastWriteFailure);
        Assert.DoesNotContain("TA-TWO", File.ReadAllText(File_));
    }

    /// <summary>
    /// AND THE PROMISE IS STILL CHECKED. What matters is not whose bytes won but whether THIS claim
    /// is in what got committed. A writer that rebased carries it forward, and refusing an order
    /// whose record is demonstrably on disk would be inventing a failure — but a clobber that drops
    /// it must report false, because <c>Place</c> has to refuse that order.
    /// </summary>
    [Fact]
    public void Submitting_is_false_when_the_claim_is_not_in_what_got_committed()
    {
        var seed = Session();
        Submit(seed, "TA-SEED");

        // Something that is not a CoidWitness writes over the file the instant after our rename.
        var foreign = "{\"version\":1,\"generation\":99,\"predecessor\":null,\"records\":[" +
                      RecordJson("TA-FOREIGN", "somebody-else") + "]}";
        var w = Session((tmp, destination) =>
        {
            File.Move(tmp, destination, overwrite: true);
            File.WriteAllText(destination, foreign);
        });

        Assert.False(w.Submitting("TA-MINE", "SIM", "ES", "Buy", 1m, null));
        Assert.DoesNotContain("TA-MINE", File.ReadAllText(File_));
        Assert.Contains("io:failed", w.Token());
    }

    /// <summary>
    /// THE RENAME IS NOT THE EVIDENCE. Round 2 returned true when the destination could not be read
    /// back, on the argument that the rename is the durability event. Rule 1 is a question about
    /// evidence: a record this process cannot read back is one it does not know is there, and the
    /// honest answer is no — so the order is refused rather than sent on an assumption.
    /// </summary>
    [Fact]
    public void A_rewrite_that_cannot_be_read_back_is_not_reported_as_durable()
    {
        var seed = Session();
        Submit(seed, "TA-SEED");
        // The run that owned the witness has ended — which is what a restart IS.
        seed.Dispose();

        var w = Session((tmp, destination) =>
        {
            File.Move(tmp, destination, overwrite: true);
            File.Delete(destination);
        });

        Assert.False(w.Submitting("TA-VANISHED", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("could not be read back", w.LastWriteFailure);
        Assert.Contains("io:failed", w.Token());
    }

    /// <summary>
    /// A temp that is GONE is not contention, and waiting 200 ms in 20 ms steps for a file that is
    /// never coming back is 200 ms of an order's life spent on a certainty. Both of the exceptions
    /// that say so derive from <see cref="IOException"/>, so they have to be excluded by name.
    /// </summary>
    [Fact]
    public void A_vanished_temp_is_not_waited_for()
    {
        var w = Session((tmp, destination) => throw new FileNotFoundException("it is gone", tmp));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(w.Submitting("TA-GONE", "SIM", "ES", "Buy", 1m, null));
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 100,
            $"burned {clock.ElapsedMilliseconds} ms of the retry budget on a file that is not coming back");
    }

    /// <summary>
    /// One writer keeps at most one uncommitted rewrite. The new one is on disk before the old one
    /// is removed, so the claim is never unheld — and two temps of the same lineage from one writer
    /// would be genuinely ambiguous to the recovery scan, since they are written milliseconds apart
    /// and mtime cannot order them.
    /// </summary>
    [Fact]
    public void A_writer_leaves_at_most_one_uncommitted_rewrite()
    {
        var seed = Session();
        Submit(seed, "TA-SEED");
        // The run that owned the witness has ended — which is what a restart IS.
        seed.Dispose();

        var w = Session(NeverLands);
        for (var i = 0; i < 5; i++) Submit(w, $"TA-{i}");

        Assert.Single(Temps());

        // ROUND 5 REWROTE THIS ASSERTION, and it is the one Codex named. It used to read
        // `["TA-SEED", "TA-4"]` — the surviving temp's abandoned claim reappearing after a restart.
        // TA-4's Submitting returned false, so Place refused that order and nothing carrying it was
        // ever sent; a later session that adopted it would be writing a write-ahead record for an
        // order this product never submitted. One temp still survives, and none of the five refused
        // claims comes back from it.
        w.Dispose();
        var next = Session();
        Assert.Equal(["TA-SEED"], next.All().Select(r => r.ClientOrderId));
        for (var i = 0; i < 5; i++) Assert.Null(next.PriorSession($"TA-{i}"));
    }

    /// <summary>
    /// Two threads, which is what actually happens: <c>Submitting</c> is called from <c>Place</c> on
    /// the bridge's pipe thread and <c>Identified</c> from the order-event fan on ATAS's.
    /// </summary>
    [Fact]
    public void Concurrent_writers_do_not_lose_or_corrupt_records()
    {
        var w = Session();
        Parallel.For(0, 100, i =>
        {
            Submit(w, $"TA-P-{i}");
            w.Identified($"TA-P-{i}", $"BRK-{i}");
        });

        var reader = Session();
        Assert.Equal(100, reader.All().Count);
        Assert.Equal(100, reader.PriorSessionIds(1000).Count);
    }

    /// <summary>Oldest first, and a trimmed record is unprovable rather than wrongly provable.</summary>
    [Fact]
    public void The_file_is_capped_and_drops_the_oldest_first()
    {
        var writer = new CoidWitness(File_, null, cap: 4);
        for (var i = 1; i <= 7; i++)
        {
            writer.Submitting($"TA-{i}", "SIM", "ES", "Buy", 1m, null);
            writer.Identified($"TA-{i}", $"BRK-{i}");
        }

        var reader = new CoidWitness(File_, null, cap: 4);
        Assert.Equal(["TA-7", "TA-6", "TA-5", "TA-4"], reader.PriorSessionIds(16));
        Assert.Null(reader.PriorSession("TA-1"));
        Assert.NotNull(reader.PriorSession("TA-4"));
    }

    /// <summary>
    /// An exception escaping <c>Submitting</c> lands inside <c>Place</c>, where the gateway reads it
    /// as an ambiguous placement and starts reconciling an order that was never submitted. That is
    /// rule 3 broken by a diagnostic, so every public method here swallows IO failure instead.
    /// </summary>
    [Fact]
    public void Every_public_method_survives_a_path_it_cannot_write()
    {
        var blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "this is a file, so nothing can be written underneath it");
        var w = new CoidWitness(Path.Combine(blocker, "coid-witness.json"));

        Submit(w, "TA-IO");
        w.Identified("TA-IO", "BRK-IO");
        Assert.Null(w.PriorSession("TA-IO"));
        Assert.Empty(w.PriorSessionIds(16));
        Assert.DoesNotContain(' ', w.Token());
    }

    /// <summary>A witness with nowhere to live records nothing and still answers every question.</summary>
    [Fact]
    public void A_witness_with_no_path_is_inert_rather_than_broken()
    {
        var w = new CoidWitness(path: null);

        Submit(w, "TA-NULL");
        w.Identified("TA-NULL", "BRK");
        Assert.Null(w.PriorSession("TA-NULL"));
        Assert.Empty(w.All());
        Assert.Contains("io:disabled", w.Token());
        Assert.DoesNotContain(' ', w.Token());
    }

    /// <summary>
    /// A truncated or hand-edited file is not a crash, and it is not evidence either: the token says
    /// the read failed rather than reporting a confident zero.
    ///
    /// WHAT ROUND 5 REVERSED HERE, deliberately. This test used to end "and it recovers: this session
    /// can still write", on the argument that the claims lost were about orders from runs that had
    /// already ended. That argument assumes we know what was in the bytes. We do not — that is what
    /// unreadable means — and the file may hold an acknowledged identifier for an order still resting
    /// in ATAS, which is precisely the evidence the restart experiment goes looking for. Writing over
    /// it destroys that silently and reports success. So the write is refused while the file stands,
    /// the row says why, and recovery is a person repairing or removing it.
    /// </summary>
    [Fact]
    public void A_corrupt_file_reads_as_unreadable_and_is_not_written_over()
    {
        File.WriteAllText(File_, "{\"version\":1,\"records\":[{\"client_order");
        var original = File.ReadAllText(File_);

        var w = Session();
        Assert.Empty(w.All());
        Assert.Null(w.PriorSession("TA-ANY"));
        Assert.Contains("records:err", w.Token());

        Assert.False(w.Submitting("TA-AFTER", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("could not be read", w.Trouble);
        Assert.Equal(original, File.ReadAllText(File_));

        // Removed by hand — the refusal is a state, not a latch.
        w.Dispose();
        File.Delete(File_);
        var next = Session();
        Submit(next, "TA-AFTER");
        next.Identified("TA-AFTER", "BRK-AFTER");
        next.Dispose();
        Assert.NotNull(Session().PriorSession("TA-AFTER"));
    }

    // -------------------------------------------------- what a person is told (tools/probe)

    /// <summary>
    /// THE WORDING AN OPERATOR READS, UNDER TEST — which nothing in this repository could do before.
    ///
    /// The probe's witness block sits behind a live bridge-pipe connection, so it never executes on a
    /// machine that is not running ATAS; no test project references <c>tools/probe</c>; and the
    /// round-4 verify leg's mutant, which made every sidecar read as UNRESOLVED, left all 81 tests
    /// green. The decision is three booleans and no IO, so it is a pure function now and this is it
    /// under test, in both directions and in all four states.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, WitnessStanding.Clean)]
    [InlineData(false, false, true, WitnessStanding.Noted)]
    [InlineData(true, false, true, WitnessStanding.Historical)]
    [InlineData(true, true, true, WitnessStanding.Unresolved)]
    public void The_probe_reads_the_witness_standing_off_the_witness(
        bool sidecarExists, bool troubled, bool noted, WitnessStanding expected)
    {
        Assert.Equal(expected, CoidWitnessReport.Standing(sidecarExists, troubled, noted));
    }

    /// <summary>
    /// A ZERO IS PROVISIONAL WHENEVER SOMETHING WAS REFUSED ON THE WAY TO COUNTING IT. "No records"
    /// and "this product never submitted that identifier" are the same sentence to a reader and are
    /// only the same FACT when nothing was declined — which is exactly what a refused import looks
    /// like, and exactly the reading that must never be produced by accident.
    /// </summary>
    [Theory]
    [InlineData(WitnessStanding.Clean, false)]
    [InlineData(WitnessStanding.Historical, false)]
    [InlineData(WitnessStanding.Noted, true)]
    [InlineData(WitnessStanding.Unresolved, true)]
    public void A_zero_is_provisional_when_something_was_refused(WitnessStanding s, bool provisional)
    {
        Assert.Equal(provisional, CoidWitnessReport.ZeroIsProvisional(s));
    }

    /// <summary>
    /// AND THE THREE WORDINGS SAY DIFFERENT THINGS. A report that shouts the same sentence at a
    /// closed gap and an open one is a report nobody reads the day it is right — which is the defect
    /// commit a8b3fb0 fixed and which nothing then pinned.
    /// </summary>
    [Fact]
    public void The_three_witness_headlines_are_distinguishable()
    {
        var log = "/x/coid-witness.errors.log";
        Assert.Contains("UNRESOLVED", CoidWitnessReport.Headline(WitnessStanding.Unresolved, log));
        Assert.Contains("historical", CoidWitnessReport.Headline(WitnessStanding.Historical, log));
        Assert.DoesNotContain("UNRESOLVED", CoidWitnessReport.Headline(WitnessStanding.Historical, log));
        Assert.DoesNotContain("UNRESOLVED", CoidWitnessReport.Headline(WitnessStanding.Noted, log));
        Assert.Contains("refused", CoidWitnessReport.Headline(WitnessStanding.Noted, log));
        Assert.Equal("none recorded", CoidWitnessReport.Headline(WitnessStanding.Clean, log));

        Assert.NotEmpty(CoidWitnessReport.Explanation(WitnessStanding.Unresolved));
        Assert.NotEmpty(CoidWitnessReport.Explanation(WitnessStanding.Historical));
        Assert.NotEmpty(CoidWitnessReport.Explanation(WitnessStanding.Noted));
        Assert.Empty(CoidWitnessReport.Explanation(WitnessStanding.Clean));
    }

    /// <summary>
    /// AND THE INPUTS THE PROBE FEEDS IT ARE THE ONES A REAL WITNESS PRODUCES, over the exact
    /// directory Codex's check names: nothing committed and one anchorless temp. The zero has to come
    /// out provisional, and the reader has to have changed nothing to say so.
    /// </summary>
    [Fact]
    public void An_anchorless_temp_makes_the_probes_zero_provisional_without_touching_anything()
    {
        WriteTempAt(File_ + ".tmp-foreign", generation: 12, predecessor: "some-other-witness-file",
                    records: RecordJson("TA-IMPORTED", "a-dead-session"));
        var before = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();

        var witness = Session();
        var sidecarExists = witness.ErrorLogPath is not null && File.Exists(witness.ErrorLogPath);
        var standing = CoidWitnessReport.Standing(sidecarExists, witness.Trouble is not null, witness.Noted);

        Assert.Empty(witness.All());
        Assert.Equal(WitnessStanding.Noted, standing);
        Assert.True(CoidWitnessReport.ZeroIsProvisional(standing));
        Assert.Equal(before, Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    // ------------------------------------------------------------------ the surface token

    /// <summary>
    /// The trading-surface report is space-joined and tools/probe splits it on spaces, so a space
    /// anywhere in this value silently becomes two fields and corrupts the token after it.
    /// </summary>
    [Fact]
    public void The_surface_token_never_contains_a_space()
    {
        var writer = Session();
        Submit(writer, "TA-T1");
        writer.Identified("TA-T1", "BRK-T1");
        Submit(writer, "TA-T2");

        var reader = Session();
        Submit(reader, "TA-T3");

        foreach (var token in new[] { writer.Token(), reader.Token(), new CoidWitness(null).Token() })
            Assert.DoesNotContain(' ', token);
    }

    /// <summary>
    /// The session prefix is what lets a reader tell "the bridge has restarted since that record
    /// was written" from "you are looking at the run that wrote it" — the difference between an
    /// experiment that has been performed and one that has not.
    /// </summary>
    [Fact]
    public void The_surface_token_reports_the_session_and_what_is_on_file()
    {
        var writer = Session();
        Submit(writer, "TA-T1");
        writer.Identified("TA-T1", "BRK-T1");
        Submit(writer, "TA-T2");           // written ahead, never acknowledged

        Assert.Equal($"session:{writer.SessionId[..8]},records:2,prior:0,io:ok", writer.Token());

        var reader = Session();
        Assert.Equal($"session:{reader.SessionId[..8]},records:2,prior:1,io:ok", reader.Token());
        Assert.NotEqual(writer.SessionId[..8], reader.SessionId[..8]);
    }

    // ------------------------------------------------------------------ the reading it feeds

    /// <summary>
    /// THE REGRESSION THAT WOULD SILENTLY MAKE THE RESTART PROOF UNREACHABLE, and the reason the
    /// latch and the capability are two predicates rather than one.
    ///
    /// <c>ProveClientOrderId</c> returns early once the reading is settled. After a restart the
    /// adapter has constructed no <c>Order</c>, so an in-session read-back can reach
    /// <see cref="ClientOrderIdProof.Distinct"/> for free. If Distinct settled the search, the scan
    /// would stop on that free reading and the cross-session one — the only one the experiment can
    /// take — would never be reached, with nothing looking wrong because the diagnostic would go on
    /// truthfully printing <c>proven-distinct</c>.
    /// </summary>
    [Fact]
    public void The_restart_reading_alone_stops_the_search()
    {
        Assert.False(ClientOrderIdProof.Distinct.IsSettled());
        Assert.False(ClientOrderIdProof.SameRef.IsSettled());
        Assert.False(ClientOrderIdProof.NotProven.IsSettled());
        Assert.True(ClientOrderIdProof.CrossSession.IsSettled());
    }

    /// <summary>Both readings answer rule 1; only one of them settles the search.</summary>
    [Fact]
    public void The_capability_and_the_latch_have_parted_company()
    {
        Assert.True(ClientOrderIdProof.Distinct.ProvesRoundTrip());
        Assert.True(ClientOrderIdProof.CrossSession.ProvesRoundTrip());
        Assert.NotEqual(ClientOrderIdProof.Distinct.ProvesRoundTrip(), ClientOrderIdProof.Distinct.IsSettled());
    }

    /// <summary>
    /// The enum is ordered weakest to strongest and compared as such, which is what lets
    /// <c>Supersedes</c> stay a bare <c>&gt;</c>. A cross-session reading must never be demoted by a
    /// straggling in-session pass.
    /// </summary>
    [Fact]
    public void The_restart_reading_is_the_strongest_one()
    {
        Assert.True(ClientOrderIdProof.CrossSession.Supersedes(ClientOrderIdProof.Distinct));
        Assert.True(ClientOrderIdProof.CrossSession.Supersedes(ClientOrderIdProof.SameRef));
        Assert.True(ClientOrderIdProof.CrossSession.Supersedes(ClientOrderIdProof.NotProven));
        Assert.False(ClientOrderIdProof.Distinct.Supersedes(ClientOrderIdProof.CrossSession));
        Assert.False(ClientOrderIdProof.SameRef.Supersedes(ClientOrderIdProof.CrossSession));
        Assert.False(ClientOrderIdProof.CrossSession.Supersedes(ClientOrderIdProof.CrossSession));
    }

    /// <summary>
    /// THE SIXTH STRING IS A WIRE CONTRACT. tools/probe switches on it verbatim and BUILD-STATUS.md
    /// quotes it as evidence, so it is pinned here as a literal exactly as the other five are.
    /// </summary>
    [Fact]
    public void The_sixth_token_is_pinned_as_a_literal()
    {
        Assert.Equal("proven-crosssession", ClientOrderIdProofs.Token(ClientOrderIdProof.CrossSession, 1, 1));
        Assert.Equal("proven-crosssession", ClientOrderIdProofs.Token(ClientOrderIdProof.CrossSession, 0, 0));
    }

    /// <summary>
    /// AND THE SUPPORT PACKAGE CAN SEE IT. The collector walked <c>Paths.Logs</c> only, and the
    /// bridge cannot write there — it runs inside ATAS and may not take a dependency on anything
    /// that would not be deployed with it, so it writes a plain file beside the witness. A
    /// durability gap in the write-ahead record is precisely what a support package is for, and it
    /// was the one thing the package could not carry.
    /// </summary>
    [Fact]
    public void The_support_package_carries_the_witness_failure_log()
    {
        var sidecar = Path.Combine(Paths.BridgeDir, CoidWitness.ErrorLogName);
        File.WriteAllText(sidecar, "2026-09-03T00:00:00.0000000+00:00 ERROR coid-witness rewrite did not land.");

        // AND THE ROTATED GENERATION, which is the half the collector could not see. The sidecar
        // rotates one back past its size bound, and rotation happens on exactly the machine whose
        // support package matters — the one that produced enough durability failures to fill the
        // file. The older generation holds the FIRST of them, which is where a fault starts.
        var rolled = sidecar + ".1";
        File.WriteAllText(rolled, "2026-09-02T00:00:00.0000000+00:00 ERROR the first one, which is the one that explains it.");
        try
        {
            var zip = Doctor.CreateSupportPackage(TestEnv.NewDb(),
                Path.Combine(_dir, "support.zip"));

            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, e => e.FullName.EndsWith(CoidWitness.ErrorLogName, StringComparison.Ordinal));
            Assert.Contains(archive.Entries, e => e.FullName.EndsWith(CoidWitness.ErrorLogName + ".1", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(sidecar); } catch (IOException) { }
            try { File.Delete(rolled); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Object identity is a question about THIS process, and after a restart it has a free answer —
    /// nothing here was constructed by us, so "untouched" is true of everything. The cross-session
    /// reading therefore cannot come from <c>Observed</c>, and it does not.
    /// </summary>
    [Fact]
    public void Object_identity_can_never_produce_the_restart_reading()
    {
        Assert.Equal(ClientOrderIdProof.SameRef, ClientOrderIdProofs.Observed(adapterTouched: true));
        Assert.Equal(ClientOrderIdProof.Distinct, ClientOrderIdProofs.Observed(adapterTouched: false));
    }
}
