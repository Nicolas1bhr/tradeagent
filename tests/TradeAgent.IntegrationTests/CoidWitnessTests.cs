using TradeAgent.AtasBridge;
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

    CoidWitness Session() => new(File_);

    static void Submit(CoidWitness w, string id) =>
        w.Submitting(id, "SIM123", "ES", "Buy", 1m, 4200.25m);

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
        Assert.False(File.Exists(File_ + ".tmp"), "the temporary file was left behind");
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
