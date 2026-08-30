using TradeAgent.AtasBridge;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// Rule 1's decision, under test on every machine.
///
/// The observation itself — "is the order ATAS handed back the very instance I gave it?" — can only
/// be made inside <c>AtasStrategyAdapter.cs</c>, which the bridge project removes from compilation
/// unless built against a real ATAS install. The JUDGEMENT made from that observation does not need
/// ATAS at all, so it lives in <see cref="ClientOrderIdProofs"/> and is exercised here.
///
/// That split is the point. This is trap 9 (docs/RESUME-HERE.md) with the sides swapped: there, a
/// test double answered a capability at the first frame so no test ever exercised the frame that
/// carried it. Here, the predicate that gates autonomous live trading used to sit in the one file
/// no test on any machine could reach. These tests are the machine-independent half of that.
///
/// Lives in the integration project rather than the unit project only because that is the one test
/// project referencing TradeAgent.AtasBridge; nothing here touches a pipe, ATAS or the gateway.
/// </summary>
public class ClientOrderIdProofTests
{
    static readonly ClientOrderIdProof[] All =
    [
        ClientOrderIdProof.NotProven, ClientOrderIdProof.SameRef,
        ClientOrderIdProof.Distinct, ClientOrderIdProof.CrossSession
    ];

    // ------------------------------------------------------------------ the capability

    [Theory]
    [InlineData(ClientOrderIdProof.NotProven, false)]
    [InlineData(ClientOrderIdProof.SameRef, false)]
    [InlineData(ClientOrderIdProof.Distinct, true)]
    [InlineData(ClientOrderIdProof.CrossSession, true)]
    public void Only_an_object_we_did_not_write_counts_as_a_round_trip(ClientOrderIdProof proof, bool expected) =>
        Assert.Equal(expected, proof.ProvesRoundTrip());

    /// <summary>
    /// The live reading on real ATAS, and the whole reason this type exists. A same-reference match
    /// is a genuine match — the comment is there, on an order in ATAS's own collection, with a
    /// broker id on it — and it is the adapter reading its own field off its own object. Reporting
    /// SupportsClientOrderId from it would let the gateway permit LIVE_AUTONOMOUS on a round trip
    /// nobody performed, which is the "do not fake it" rule 1 states by name.
    /// </summary>
    [Fact]
    public void A_same_reference_match_never_reports_the_capability_true()
    {
        Assert.False(ClientOrderIdProof.SameRef.ProvesRoundTrip());
        Assert.Equal("proven-sameref", ClientOrderIdProofs.Token(ClientOrderIdProof.SameRef, 1, 1));
    }

    // ------------------------------------------------------------------ the latch

    /// <summary>
    /// The failure this whole change could have introduced, named so a regression names it back.
    ///
    /// <c>ProveClientOrderId</c> returns early once the reading is settled, so the read-back does
    /// not rescan ATAS's order book on every order event. If SameRef settled it, the scan would stop
    /// on the vacuous reading and a genuinely Distinct match later in the same session could never
    /// be observed — with nothing looking wrong, because the diagnostic would go on truthfully
    /// reporting proven-sameref for the life of the process.
    /// </summary>
    [Fact]
    public void A_same_reference_match_does_not_stop_the_search_for_a_real_one()
    {
        Assert.False(ClientOrderIdProof.SameRef.IsSettled());
        Assert.False(ClientOrderIdProof.NotProven.IsSettled());
    }

    /// <summary>
    /// THE TWO PREDICATES HAVE PARTED COMPANY, AND THIS IS WHAT THEY WERE KEPT SEPARATE FOR.
    ///
    /// The note that used to stand here said they would stop coinciding "the moment a reading
    /// stronger than Distinct is added — at which point the latch must follow that one and the
    /// capability must not be silently dragged along with it". That reading is
    /// <see cref="ClientOrderIdProof.CrossSession"/>. Distinct now proves the round trip and does
    /// NOT settle the search, which is the whole safety of the restart experiment: after ATAS
    /// restarts, the adapter has constructed no Order at all, so a Distinct reading is free — and
    /// a free reading that latched would make the cross-session one unreachable in silence.
    /// </summary>
    [Fact]
    public void Settling_the_search_no_longer_follows_the_capability()
    {
        Assert.True(ClientOrderIdProof.Distinct.ProvesRoundTrip());
        Assert.False(ClientOrderIdProof.Distinct.IsSettled());

        // Exactly one reading settles it, and it is the strongest one.
        Assert.Equal([ClientOrderIdProof.CrossSession], All.Where(p => p.IsSettled()).ToArray());
        Assert.Equal(ClientOrderIdProof.CrossSession, All.Max());
    }

    // ------------------------------------------------------------------ monotonicity

    [Theory]
    // A weaker or equal reading never displaces what is already recorded.
    [InlineData(ClientOrderIdProof.NotProven, ClientOrderIdProof.NotProven, false)]
    [InlineData(ClientOrderIdProof.SameRef, ClientOrderIdProof.SameRef, false)]
    [InlineData(ClientOrderIdProof.Distinct, ClientOrderIdProof.Distinct, false)]
    [InlineData(ClientOrderIdProof.NotProven, ClientOrderIdProof.SameRef, false)]
    [InlineData(ClientOrderIdProof.NotProven, ClientOrderIdProof.Distinct, false)]
    [InlineData(ClientOrderIdProof.CrossSession, ClientOrderIdProof.CrossSession, false)]
    // The one that matters: a late SameRef pass must not demote a Distinct already established.
    [InlineData(ClientOrderIdProof.SameRef, ClientOrderIdProof.Distinct, false)]
    // Nor may an in-session pass demote the cross-session reading, which is the strongest there is.
    [InlineData(ClientOrderIdProof.Distinct, ClientOrderIdProof.CrossSession, false)]
    [InlineData(ClientOrderIdProof.SameRef, ClientOrderIdProof.CrossSession, false)]
    // Strictly stronger, and only that.
    [InlineData(ClientOrderIdProof.SameRef, ClientOrderIdProof.NotProven, true)]
    [InlineData(ClientOrderIdProof.Distinct, ClientOrderIdProof.NotProven, true)]
    [InlineData(ClientOrderIdProof.Distinct, ClientOrderIdProof.SameRef, true)]
    [InlineData(ClientOrderIdProof.CrossSession, ClientOrderIdProof.NotProven, true)]
    [InlineData(ClientOrderIdProof.CrossSession, ClientOrderIdProof.SameRef, true)]
    [InlineData(ClientOrderIdProof.CrossSession, ClientOrderIdProof.Distinct, true)]
    public void A_reading_replaces_only_a_weaker_one(
        ClientOrderIdProof candidate, ClientOrderIdProof standing, bool expected) =>
        Assert.Equal(expected, candidate.Supersedes(standing));

    [Theory]
    [InlineData(true, ClientOrderIdProof.SameRef)]
    [InlineData(false, ClientOrderIdProof.Distinct)]
    public void The_object_identity_reading_maps_to_the_proof_it_is_worth(
        bool sameInstance, ClientOrderIdProof expected) =>
        Assert.Equal(expected, ClientOrderIdProofs.Observed(sameInstance));

    // ------------------------------------------------------------------ the diagnostic

    /// <summary>
    /// The five tokens are a wire contract: tools/probe switches on them verbatim and BUILD-STATUS.md
    /// quotes them as recorded evidence. Changing one turns a recorded reading into an unrecognised
    /// one, silently, on a machine nobody is watching — so they are pinned here as literals.
    /// </summary>
    [Theory]
    [InlineData(ClientOrderIdProof.NotProven, 0, 0, "unattempted")]
    [InlineData(ClientOrderIdProof.NotProven, 1, 0, "unchecked")]
    [InlineData(ClientOrderIdProof.NotProven, 1, 1, "notfound")]
    [InlineData(ClientOrderIdProof.NotProven, 7, 3, "notfound")]
    [InlineData(ClientOrderIdProof.SameRef, 1, 1, "proven-sameref")]
    [InlineData(ClientOrderIdProof.Distinct, 1, 1, "proven-distinct")]
    [InlineData(ClientOrderIdProof.CrossSession, 1, 1, "proven-crosssession")]
    public void Each_reading_has_its_own_word(
        ClientOrderIdProof proof, int attempts, int checks, string expected) =>
        Assert.Equal(expected, ClientOrderIdProofs.Token(proof, attempts, checks));

    /// <summary>
    /// A match is a match whatever the counters say. Once something came back, the counters describe
    /// how much looking happened, not what was found — and the token must report the finding.
    /// </summary>
    [Fact]
    public void A_match_outranks_the_counters_that_led_to_it()
    {
        Assert.Equal("proven-sameref", ClientOrderIdProofs.Token(ClientOrderIdProof.SameRef, 0, 0));
        Assert.Equal("proven-distinct", ClientOrderIdProofs.Token(ClientOrderIdProof.Distinct, 0, 0));
        // And it must for the cross-session reading above all: the counters describe THIS session,
        // and the whole claim is about an order a previous one submitted. A fresh process that has
        // attempted nothing and checked nothing can still take this reading.
        Assert.Equal("proven-crosssession", ClientOrderIdProofs.Token(ClientOrderIdProof.CrossSession, 0, 0));
    }

    /// <summary>
    /// The invariant the old shape could not hold: the capability boolean and the coid= word beside
    /// it must never contradict each other. They used to be two variables — a bool set by any match
    /// and an enum recording which match — so "SupportsClientOrderId: true / ROUND TRIP, MEASURED:
    /// proven-sameref" was a printable state, and it was printed, on real ATAS.
    /// </summary>
    [Fact]
    public void The_capability_and_the_word_reported_beside_it_cannot_disagree()
    {
        string[] proving = ["proven-distinct", "proven-crosssession"];
        foreach (var proof in All)
            foreach (var attempts in new[] { 0, 1, 9 })
                foreach (var checks in new[] { 0, 1, 9 })
                    Assert.Equal(proof.ProvesRoundTrip(),
                                 proving.Contains(ClientOrderIdProofs.Token(proof, attempts, checks)));
    }

    /// <summary>Six readings, six distinct words. A reading that shared a word with another would
    /// be invisible on the wire, which is where every recorded measurement is read from.</summary>
    [Fact]
    public void No_two_readings_share_a_word()
    {
        var words = new List<string> { ClientOrderIdProofs.Token(ClientOrderIdProof.NotProven, 0, 0),
                                       ClientOrderIdProofs.Token(ClientOrderIdProof.NotProven, 1, 0),
                                       ClientOrderIdProofs.Token(ClientOrderIdProof.NotProven, 1, 1) };
        words.AddRange(All.Where(p => p != ClientOrderIdProof.NotProven)
                          .Select(p => ClientOrderIdProofs.Token(p, 1, 1)));

        Assert.Equal(6, words.Count);
        Assert.Equal(6, words.Distinct().Count());
    }

    // ------------------------------------------------------------------ the sequence

    /// <summary>
    /// The three decisions composed the way the adapter composes them, over the session this product
    /// is actually waiting for: nothing placed, then a vacuous match, then the real one.
    ///
    /// <see cref="Record"/> mirrors the three lines of <c>ProveClientOrderId</c> that cannot be
    /// compiled off Windows — the latch, the observation and the monotonic write. It is not the
    /// adapter and does not pretend to test it; what it tests is that those three decisions compose
    /// into a state machine that can still reach the proof after a SameRef reading. Under the old
    /// latch this test's third step was unreachable.
    /// </summary>
    [Fact]
    public void A_vacuous_match_does_not_block_the_real_proof_that_follows_it()
    {
        var state = ClientOrderIdProof.NotProven;

        Assert.False(state.ProvesRoundTrip());
        Assert.Equal("unattempted", ClientOrderIdProofs.Token(state, 0, 0));

        // ATAS hands back the very Order instance we submitted. A real match, worth nothing.
        Assert.True(Record(ref state, sameInstance: true));
        Assert.Equal(ClientOrderIdProof.SameRef, state);
        Assert.False(state.ProvesRoundTrip());
        Assert.Equal("proven-sameref", ClientOrderIdProofs.Token(state, 1, 1));

        // More of the same changes nothing, and must not be reported as if it had.
        Assert.True(Record(ref state, sameInstance: true));
        Assert.Equal(ClientOrderIdProof.SameRef, state);
        Assert.False(state.ProvesRoundTrip());

        // A separate object carries our identifier. THIS is the reading the latch had to stay open
        // for, and it reports the capability true.
        Assert.True(Record(ref state, sameInstance: false));
        Assert.Equal(ClientOrderIdProof.Distinct, state);
        Assert.True(state.ProvesRoundTrip());
        Assert.Equal("proven-distinct", ClientOrderIdProofs.Token(state, 2, 2));

        // AND THE SEARCH GOES ON, which is the change a fourth reading brought. A straggling
        // same-reference pass cannot demote it, but it is not turned away either — because a
        // stronger reading than Distinct now exists and the latch must stay open for it.
        Assert.True(Record(ref state, sameInstance: true));
        Assert.Equal(ClientOrderIdProof.Distinct, state);
        Assert.True(state.ProvesRoundTrip());

        // The identifier is found on an order a PREVIOUS process submitted. Only this settles it.
        Assert.True(RecordCrossSession(ref state));
        Assert.Equal(ClientOrderIdProof.CrossSession, state);
        Assert.Equal("proven-crosssession", ClientOrderIdProofs.Token(state, 2, 2));

        Assert.False(Record(ref state, sameInstance: false));
        Assert.Equal(ClientOrderIdProof.CrossSession, state);
        Assert.True(state.ProvesRoundTrip());
    }

    /// <summary>
    /// The same three decisions from the other direction: a fresh session, where the in-session
    /// reading is FREE. Nothing here was constructed by this process, so the object-identity test
    /// answers "distinct" for anything at all — and that must not stop the scan before the
    /// cross-session reading can be taken. This is trap 30 in docs/RESUME-HERE.md as a test.
    /// </summary>
    [Fact]
    public void A_free_distinct_reading_in_a_fresh_session_does_not_block_the_restart_proof()
    {
        var state = ClientOrderIdProof.NotProven;

        // Nothing in the book is an object this process built, so every match reads Distinct.
        Assert.True(Record(ref state, sameInstance: false));
        Assert.Equal(ClientOrderIdProof.Distinct, state);

        // The scan MUST still run. If Distinct latched here, the line below would never execute in
        // the adapter and the experiment could not be performed at all.
        Assert.False(state.IsSettled());
        Assert.True(RecordCrossSession(ref state));
        Assert.Equal(ClientOrderIdProof.CrossSession, state);
        Assert.True(state.IsSettled());
    }

    /// <summary>One read-back pass. Returns false when the latch turned it away.</summary>
    static bool Record(ref ClientOrderIdProof state, bool sameInstance)
    {
        if (state.IsSettled()) return false;
        var observed = ClientOrderIdProofs.Observed(sameInstance);
        if (observed.Supersedes(state)) state = observed;
        return true;
    }

    /// <summary>The same pass down the witness branch, where the reading is fixed rather than
    /// derived from object identity.</summary>
    static bool RecordCrossSession(ref ClientOrderIdProof state)
    {
        if (state.IsSettled()) return false;
        if (ClientOrderIdProof.CrossSession.Supersedes(state)) state = ClientOrderIdProof.CrossSession;
        return true;
    }
}
