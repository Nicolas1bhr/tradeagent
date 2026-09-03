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
        var w = Session(RefusedTimes(1, () => new UnauthorizedAccessException("Access to the path is denied.")));
        Submit(w, "TA-DENIED");

        Assert.Empty(Temps());
        Assert.Single(Session().All());
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

        // And the shape that round 2 still accepted: generation 1, descended from nothing. It is
        // not a lineage test — every first rewrite of every witness on earth looks exactly like
        // this — and it is refused too.
        foreach (var f in Temps()) File.Delete(f);
        WriteTemp(generation: 1, predecessor: null,
                  records: RecordJson("TA-ALSO-IMPORTED", "a-dead-session"), at: DateTime.UtcNow);

        var again = Session();
        Assert.Empty(again.All());
        Assert.Empty(again.PriorSessionIds(16));
        Assert.Contains("io:degraded", again.Token());
        Assert.Contains("TA-ALSO-IMPORTED".Length > 0 ? "nothing anchors it" : "", 
                        File.ReadAllText(Path.Combine(_dir, CoidWitness.ErrorLogName)));
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
        Assert.Contains("io:degraded", reader.Token());
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

        var generation = CommittedGeneration() + 1;
        var predecessor = Fingerprint(CommittedText());
        foreach (var (suffix, id) in new[] { ("-a-1", "TA-RIVAL-A"), ("-b-1", "TA-RIVAL-B") })
            File.WriteAllText(File_ + ".tmp" + suffix,
                $$"""{"version":1,"generation":{{generation}},"predecessor":"{{predecessor}}","records":[{{RecordJson("TA-COMMITTED", "s")}},{{RecordJson(id, "s")}}]}""");

        var reader = Session();
        Assert.Equal(["TA-COMMITTED"], reader.All().Select(r => r.ClientOrderId));
        Assert.Contains("io:degraded", reader.Token());
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
        var first = Session();
        Submit(first, "TA-COMMITTED");

        var stranded = Session(NeverLands);
        Submit(stranded, "TA-STRANDED");
        File.SetLastWriteTimeUtc(Temps().Single(), File.GetLastWriteTimeUtc(File_).AddHours(-1));

        var reader = Session();
        Assert.Equal(["TA-COMMITTED", "TA-STRANDED"], reader.All().Select(r => r.ClientOrderId));
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

        // The restart. This session writes perfectly well; the gap is still a fact about the file.
        foreach (var f in Temps()) Age(f);
        var next = Session();
        Assert.Contains("io:degraded", next.Token());
        Assert.DoesNotContain(' ', next.Token());

        // Cleared by deleting it, which takes effect at the next start. The stranded temp does NOT
        // have to be cleaned up by hand any more: the session above quarantined it out of the
        // candidate glob, which is what stops one crash degrading the witness for ever.
        Assert.Empty(Temps());
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
        var blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "a file, so nothing can be written underneath it");
        var w = new CoidWitness(Path.Combine(blocker, "coid-witness.json"));

        Submit(w, "TA-NO-TEMP");

        Assert.Contains("temp_not_written=", w.LastWriteFailure);
        Assert.DoesNotContain("temp_holding_newer_state=", w.LastWriteFailure);
    }

    /// <summary>
    /// A permanently unwritable destination turns every order into a log line. The per-session line
    /// cap is what stops the report about a disk problem from becoming one.
    /// </summary>
    [Fact]
    public void The_sidecar_stops_after_a_bounded_number_of_failures_in_one_session()
    {
        var w = Session((tmp, destination) => throw new FileNotFoundException("gone", tmp));
        for (var i = 0; i < 40; i++) Submit(w, $"TA-{i}");

        Assert.Equal(32, File.ReadAllLines(Path.Combine(_dir, CoidWitness.ErrorLogName)).Length);
    }

    /// <summary>
    /// And across sessions the cap resets, so the bound that matters there is the file's size. It is
    /// restarted rather than trimmed: the newest failures are the ones worth keeping, and rewriting
    /// a log to drop its head is more file IO than a failing disk deserves.
    /// </summary>
    [Fact]
    public void An_oversized_sidecar_is_restarted_rather_than_grown_forever()
    {
        var log = Path.Combine(_dir, CoidWitness.ErrorLogName);
        File.WriteAllText(log, new string('x', 70 * 1024));

        var w = Session((tmp, destination) => throw new FileNotFoundException("gone", tmp));
        Submit(w, "TA-BOUND");

        Assert.True(new FileInfo(log).Length < 4096, $"the sidecar is {new FileInfo(log).Length} bytes");
        Assert.Contains("TA-BOUND", File.ReadAllText(log));
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

        var reader = Session();
        Assert.Equal(["TA-REAL"], reader.All().Select(r => r.ClientOrderId));
        Assert.Empty(Temps());
        Assert.Single(Directory.GetFiles(_dir, "coid-witness.json.rejected-*"));

        var linesAfterFirstLook = File.ReadAllLines(Path.Combine(_dir, CoidWitness.ErrorLogName)).Length;

        // Every later session finds nothing to complain about.
        Assert.Equal(["TA-REAL"], Session().All().Select(r => r.ClientOrderId));
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

        Assert.Contains("io:ok", Session().Token());
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

    /// <summary>The sidecar lives beside the witness, so a person told about one has found the other.</summary>
    [Fact]
    public void The_sidecar_sits_beside_the_witness_file()
    {
        Assert.Equal(Path.Combine(_dir, CoidWitness.ErrorLogName), Session().ErrorLogPath);
        Assert.Null(new CoidWitness(path: null).ErrorLogPath);
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
        var b = Session(NeverLands);
        Submit(a, "TA-A");
        Submit(b, "TA-B");

        Assert.Equal(2, Temps().Length);
        Assert.Equal(2, Temps().Select(Path.GetFileName).Distinct().Count());
    }

    /// <summary>
    /// TWO WRITERS LOADED AT THE SAME GENERATION, AND NEITHER CLAIM IS LOST. Before the
    /// compare-and-swap, B — loaded when the file was at generation N — would replace A's freshly
    /// committed N+1 with its own N+1, deleting A's claim silently and AFTER A's read-back had
    /// already told A the claim was durable. B now notices the file is not what its lineage says,
    /// rebases onto A's commit, and carries both.
    /// </summary>
    [Fact]
    public void Two_writers_at_the_same_generation_both_keep_their_claims()
    {
        var seed = Session();
        Submit(seed, "TA-SEED");

        // Both load the same committed state, then commit in turn.
        var a = Session();
        var b = Session();
        Assert.Empty(a.PriorSessionIds(0));   // force both to load before either writes

        Assert.True(a.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null));
        Assert.True(b.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));

        Assert.Contains("TA-A", CommittedIds());
        Assert.Contains("TA-B", CommittedIds());
        Assert.Contains("TA-SEED", CommittedIds());
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

        var w = Session(NeverLands);
        for (var i = 0; i < 5; i++) Submit(w, $"TA-{i}");

        Assert.Single(Temps());
        // Each refusal is rolled back before the next attempt, so the surviving rewrite holds the
        // committed state plus the one claim that was in flight when the writer stopped — not five
        // abandoned claims for five orders that were never sent.
        Assert.Equal(["TA-SEED", "TA-4"], Session().All().Select(r => r.ClientOrderId));
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
    /// A truncated or hand-edited file is not a crash, and it is not evidence either. The claims
    /// lost were about orders from runs that have already ended, and the token says the read failed
    /// rather than reporting a confident zero.
    /// </summary>
    [Fact]
    public void A_corrupt_file_reads_as_unreadable_rather_than_as_empty()
    {
        File.WriteAllText(File_, "{\"version\":1,\"records\":[{\"client_order");

        var w = Session();
        Assert.Empty(w.All());
        Assert.Null(w.PriorSession("TA-ANY"));
        Assert.Contains("records:err", w.Token());

        // And it recovers: this session can still write, and the next one can read what it wrote.
        Submit(w, "TA-AFTER");
        w.Identified("TA-AFTER", "BRK-AFTER");
        Assert.NotNull(Session().PriorSession("TA-AFTER"));
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
        try
        {
            var zip = Doctor.CreateSupportPackage(TestEnv.NewDb(),
                Path.Combine(_dir, "support.zip"));

            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            var entry = archive.Entries.SingleOrDefault(e => e.FullName.Contains(CoidWitness.ErrorLogName));
            Assert.NotNull(entry);
        }
        finally { try { File.Delete(sidecar); } catch (IOException) { } }
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
