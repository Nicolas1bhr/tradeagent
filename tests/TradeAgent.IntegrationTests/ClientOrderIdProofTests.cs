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
        [ClientOrderIdProof.NotProven, ClientOrderIdProof.SameRef, ClientOrderIdProof.Distinct];

    // ------------------------------------------------------------------ the capability

    [Theory]
    [InlineData(ClientOrderIdProof.NotProven, false)]
    [InlineData(ClientOrderIdProof.SameRef, false)]
    [InlineData(ClientOrderIdProof.Distinct, true)]
    public void Only_a_distinct_object_counts_as_a_round_trip(ClientOrderIdProof proof, bool expected) =>
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
        Assert.True(ClientOrderIdProof.Distinct.IsSettled());
    }

    /// <summary>
    /// The latch tracks the STRONGEST reading; the capability tracks what counts as proof. Today
    /// those are the same state, and they are separate methods because they would stop being the
    /// same the moment a reading stronger than Distinct is added — at which point the latch must
    /// follow that one and the capability must not be silently dragged along with it.
    /// </summary>
    [Fact]
    public void Settling_the_search_and_proving_the_round_trip_coincide_for_now()
    {
        foreach (var proof in All) Assert.Equal(proof.ProvesRoundTrip(), proof.IsSettled());
    }

    // ------------------------------------------------------------------ monotonicity

    [Theory]
    // A weaker or equal reading never displaces what is already recorded.
    [InlineData(ClientOrderIdProof.NotProven, ClientOrderIdProof.NotProven, false)]
    [InlineData(ClientOrderIdProof.SameRef, ClientOrderIdProof.SameRef, false)]
    [InlineData(ClientOrderIdProof.Distinct, ClientOrderIdProof.Distinct, false)]
    [InlineData(ClientOrderIdProof.NotProven, ClientOrderIdProof.SameRef, false)]
    [InlineData(ClientOrderIdProof.NotProven, ClientOrderIdProof.Distinct, false)]
    // The one that matters: a late SameRef pass must not demote a Distinct already established.
    [InlineData(ClientOrderIdProof.SameRef, ClientOrderIdProof.Distinct, false)]
    // Strictly stronger, and only that.
    [InlineData(ClientOrderIdProof.SameRef, ClientOrderIdProof.NotProven, true)]
    [InlineData(ClientOrderIdProof.Distinct, ClientOrderIdProof.NotProven, true)]
    [InlineData(ClientOrderIdProof.Distinct, ClientOrderIdProof.SameRef, true)]
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
        foreach (var proof in All)
            foreach (var attempts in new[] { 0, 1, 9 })
                foreach (var checks in new[] { 0, 1, 9 })
                    Assert.Equal(proof.ProvesRoundTrip(),
                                 ClientOrderIdProofs.Token(proof, attempts, checks) == "proven-distinct");
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
        // for, and the only one that may report the capability true.
        Assert.True(Record(ref state, sameInstance: false));
        Assert.Equal(ClientOrderIdProof.Distinct, state);
        Assert.True(state.ProvesRoundTrip());
        Assert.Equal("proven-distinct", ClientOrderIdProofs.Token(state, 2, 2));

        // Settled: the scan stops, and a straggling same-reference pass cannot undo it.
        Assert.False(Record(ref state, sameInstance: true));
        Assert.Equal(ClientOrderIdProof.Distinct, state);
        Assert.True(state.ProvesRoundTrip());
    }

    /// <summary>One read-back pass. Returns false when the latch turned it away.</summary>
    static bool Record(ref ClientOrderIdProof state, bool sameInstance)
    {
        if (state.IsSettled()) return false;
        var observed = ClientOrderIdProofs.Observed(sameInstance);
        if (observed.Supersedes(state)) state = observed;
        return true;
    }
}
