using TradeAgent.AtasBridge;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE OTHER HALF OF RULE 1: not "which reading counts as proof" (that is
/// <see cref="ClientOrderIdProofTests"/>) but "which OBJECT is allowed to produce a reading at all".
///
/// Once <c>SupportsClientOrderId</c> was narrowed to <see cref="ClientOrderIdProof.Distinct"/>, the
/// only thing between this product and autonomous live trading became a single question asked of a
/// single object: did ATAS put our identifier on this, or did we? And the adapter builds order
/// objects carrying our identifier in two places besides <c>Place</c> —
///
///   * <c>Modify</c> does <c>order.Clone()</c>, and Clone copies Comment. The replacement is an
///     object THE ADAPTER CONSTRUCTED holding OUR client order id, and it is not the instance in
///     <c>_submitted</c>. A read-back that asked only "is this the instance I submitted" answers no
///     and records Distinct — the adapter proving rule 1 against itself;
///   * <c>ClosePosition</c> writes <c>created.Comment = clientOrderId</c> onto an order ATAS built.
///
/// Whether ATAS's own collection ever holds the Modify clone is NOT VERIFIED — the API dump lists
/// public members only — so the guard is written to be indifferent to the answer, and these tests
/// hold it that way on a machine where nothing about ATAS can be run at all.
///
/// <see cref="AtasStrategyAdapter"/> is <c>&lt;Compile Remove&gt;</c>d everywhere except a Windows
/// box with ATAS installed, so the three <c>_touched.Add(...)</c> call sites cannot be compiled
/// here, let alone exercised. What CAN be tested is the rule they feed, and that is deliberately all
/// of the decision: <see cref="AdapterTouchedOrders"/> owns "does this object count as evidence",
/// and <see cref="Scan"/> below composes it exactly as the adapter's read-back loop does.
/// </summary>
public class AdapterTouchedOrdersTests
{
    const string Coid = "TA-PROBE-20260829120000";

    /// <summary>
    /// Stands in for ATAS's Order, and OVERRIDES EQUALITY ON PURPOSE — two orders with the same
    /// comment and id are <c>Equals</c> here. ATAS's real Order does not override equality anywhere
    /// in the dump, so today identity and equality coincide for it; a future SDK giving Order value
    /// semantics would silently turn "is this the object I touched" into "does it look like one",
    /// in the one place where a wrong answer reads as a proof. This double makes that substitution
    /// visible: any test below that still passes when the set is switched to a default comparer is
    /// a test that was not asking the right question.
    /// </summary>
    sealed class Ord(string comment, string id = "7968887")
    {
        public string Comment { get; set; } = comment;
        public string Id { get; } = id;

        public override bool Equals(object? obj) =>
            obj is Ord o && o.Comment == Comment && o.Id == Id;
        public override int GetHashCode() => HashCode.Combine(Comment, Id);
    }

    // ------------------------------------------------------------------ identity, never equality

    [Fact]
    public void An_object_the_adapter_touched_is_never_evidence()
    {
        var touched = new AdapterTouchedOrders();
        var submitted = new Ord(Coid);
        touched.Add(submitted);

        Assert.False(touched.CountsAsEvidence(submitted));
    }

    [Fact]
    public void An_object_the_adapter_never_touched_is_evidence()
    {
        var touched = new AdapterTouchedOrders();
        touched.Add(new Ord(Coid));

        Assert.True(touched.CountsAsEvidence(new Ord(Coid, id: "7968888")));
    }

    /// <summary>
    /// The question is "is this an object we touched", not "does it look like one". A twin that is
    /// <c>Equals</c> to something we touched is still ATAS's object as far as anyone can tell, and
    /// refusing it would throw away a real proof; conversely a set keyed on equality would refuse
    /// every order that merely resembled ours. Both directions are wrong and both are pinned here.
    /// </summary>
    [Fact]
    public void Equality_is_not_identity_and_the_set_keys_on_identity()
    {
        var touched = new AdapterTouchedOrders();
        var ours = new Ord(Coid);
        var twin = new Ord(Coid);

        touched.Add(ours);

        Assert.Equal(ours, twin);                       // equal by value...
        Assert.NotSame(ours, twin);                     // ...and a different object
        Assert.False(touched.CountsAsEvidence(ours));
        Assert.True(touched.CountsAsEvidence(twin));
    }

    [Fact]
    public void Registering_the_same_object_twice_changes_nothing()
    {
        var touched = new AdapterTouchedOrders();
        var order = new Ord(Coid);

        touched.Add(order);
        touched.Add(order);

        Assert.Equal(1, touched.Count);
        Assert.False(touched.CountsAsEvidence(order));
    }

    /// <summary>Bookkeeping on a write path must not be able to throw, and the absence of an object
    /// must not be able to prove anything.</summary>
    [Fact]
    public void Nothing_is_not_evidence()
    {
        var touched = new AdapterTouchedOrders();
        touched.Add(null);

        Assert.Equal(0, touched.Count);
        Assert.False(touched.CountsAsEvidence(null));
    }

    // ------------------------------------------------------------------ the two routes that were open

    /// <summary>
    /// ROUTE 1 — Modify's clone, and the reason this type exists.
    ///
    /// <c>replacement = order.Clone()</c> carries our client order id because Clone copied Comment,
    /// and <c>_submitted[id]</c> still holds the ORIGINAL. So the clone is a different object with
    /// our identifier on it, which is the exact shape of a round trip — performed by the adapter,
    /// against itself. If this ever passes as evidence, <c>SupportsClientOrderId</c> reads true and
    /// the gateway may permit LIVE_AUTONOMOUS on it.
    /// </summary>
    [Fact]
    public void The_clone_Modify_builds_is_not_a_round_trip()
    {
        var touched = new AdapterTouchedOrders();
        var submitted = new Ord(Coid);
        touched.Add(submitted);

        var clone = new Ord(submitted.Comment);         // Clone() copies Comment
        touched.Add(clone);

        Assert.NotSame(submitted, clone);
        Assert.False(touched.CountsAsEvidence(clone));
        Assert.Equal(ClientOrderIdProof.SameRef, Scan(touched, submitted, [clone]));
    }

    /// <summary>
    /// The same clone, in the book ALONE — the case the narrow rule got wrong most cleanly, because
    /// there is nothing else for the scan to look at and the instance in <c>_submitted</c> is not
    /// there to compare against. Reference-equality against the submitted instance answers "not
    /// mine" and the whole capability turns on it.
    /// </summary>
    [Fact]
    public void The_clone_alone_in_the_book_still_proves_nothing()
    {
        var touched = new AdapterTouchedOrders();
        var submitted = new Ord(Coid);
        var clone = new Ord(Coid);
        touched.Add(submitted);
        touched.Add(clone);

        // What the narrow rule saw: a different object carrying our id. It is not evidence.
        Assert.False(ReferenceEquals(clone, submitted));
        Assert.Equal(ClientOrderIdProof.SameRef, Scan(touched, submitted, [clone]));
        Assert.False(Scan(touched, submitted, [clone]).ProvesRoundTrip());
    }

    /// <summary>
    /// ROUTE 2 — ClosePosition labels an order ATAS created. Registered BEFORE the Comment is
    /// written, so there is no instant at which the object carries our identifier and is not yet
    /// refused; the order-event fan runs on ATAS's thread and can read it in between.
    /// </summary>
    [Fact]
    public void An_order_ATAS_built_that_we_labelled_is_not_a_round_trip()
    {
        var touched = new AdapterTouchedOrders();
        var created = new Ord("");                      // ATAS's own object, no comment yet

        touched.Add(created);                           // registered first...
        created.Comment = Coid;                         // ...then labelled

        Assert.False(touched.CountsAsEvidence(created));
        Assert.Equal(ClientOrderIdProof.SameRef, Scan(touched, submitted: null, [created]));
    }

    /// <summary>
    /// The proof this whole guard must NOT smother: an order that is genuinely ATAS's, carrying our
    /// identifier and a broker id, and touched by nobody. A guard that refuses everything is as
    /// useless as one that refuses nothing.
    /// </summary>
    [Fact]
    public void An_untouched_object_carrying_our_id_is_still_the_round_trip()
    {
        var touched = new AdapterTouchedOrders();
        var submitted = new Ord(Coid);
        touched.Add(submitted);

        var fromAtas = new Ord(Coid, id: "7968889");

        Assert.True(touched.CountsAsEvidence(fromAtas));
        Assert.Equal(ClientOrderIdProof.Distinct, Scan(touched, submitted, [fromAtas]));
        Assert.True(Scan(touched, submitted, [fromAtas]).ProvesRoundTrip());
    }

    /// <summary>
    /// A whole pass is scanned rather than stopping at the first hit, so one of our own objects
    /// being enumerated first cannot hide a real match behind it. The book here is the realistic
    /// one after a modify: our submitted instance, our clone, and ATAS's own order.
    /// </summary>
    [Fact]
    public void One_of_our_objects_first_in_the_book_does_not_hide_a_real_match()
    {
        var touched = new AdapterTouchedOrders();
        var submitted = new Ord(Coid);
        var clone = new Ord(Coid);
        touched.Add(submitted);
        touched.Add(clone);

        var fromAtas = new Ord(Coid, id: "7968889");

        Assert.Equal(ClientOrderIdProof.Distinct, Scan(touched, submitted, [submitted, clone, fromAtas]));
    }

    // ------------------------------------------------------------------ trimming

    [Fact]
    public void Below_the_cap_nothing_is_forgotten()
    {
        var touched = new AdapterTouchedOrders();
        var ours = new Ord(Coid);
        touched.Add(ours);
        touched.Add(new Ord("TA-2"));

        Assert.False(touched.Trim(cap: 2));             // at the cap is not over it
        Assert.False(touched.HasForgotten);
        Assert.Equal(2, touched.Count);
        Assert.False(touched.CountsAsEvidence(ours));
    }

    /// <summary>
    /// THE DIRECTION TO FAIL IN. A bare <c>Clear()</c> would leave a forgotten clone looking exactly
    /// like an object of ATAS's own, so the very next read-back would record Distinct — trimming
    /// would MANUFACTURE the proof this type exists to prevent, silently, four thousand orders into
    /// a session nobody is watching. So a trim that drops anything refuses every proof afterwards
    /// instead.
    /// </summary>
    [Fact]
    public void A_trim_refuses_every_proof_rather_than_inventing_one()
    {
        var touched = new AdapterTouchedOrders();
        var submitted = new Ord(Coid);
        var clone = new Ord(Coid);
        touched.Add(submitted);
        touched.Add(clone);

        Assert.True(touched.Trim(cap: 1));
        Assert.True(touched.HasForgotten);
        Assert.Equal(0, touched.Count);

        // The forgotten clone is the object the old code would now call ATAS's own.
        Assert.False(touched.CountsAsEvidence(clone));
        Assert.Equal(ClientOrderIdProof.SameRef, Scan(touched, submitted, [clone]));

        // And an order that really is ATAS's is refused too: after a drop the set cannot show that
        // ANYTHING is untouched, and an unprovable negative is not a proof.
        Assert.False(touched.CountsAsEvidence(new Ord(Coid, id: "7968889")));
    }

    /// <summary>
    /// The refusal latches. Registering more objects after a trim does not restore the set's ability
    /// to say that something is untouched — it never regains knowledge of what it dropped.
    /// </summary>
    [Fact]
    public void Forgetting_is_permanent()
    {
        var touched = new AdapterTouchedOrders();
        touched.Add(new Ord(Coid));
        Assert.True(touched.Trim(cap: 0));

        var later = new Ord("TA-LATER");
        touched.Add(later);

        Assert.True(touched.HasForgotten);
        Assert.False(touched.CountsAsEvidence(later));
        Assert.False(touched.CountsAsEvidence(new Ord("TA-SOMETHING-ELSE")));
        Assert.False(touched.Trim(cap: 4096));          // nothing more to drop, still forgotten
        Assert.True(touched.HasForgotten);
    }

    /// <summary>
    /// What the permanence actually costs, stated as a test rather than as a claim: a session that
    /// reaches the cap is a session that has already failed to prove the round trip every time it
    /// looked, because the proof LATCHES on the first Distinct and the read-back stops. So refusing
    /// after a trim refuses a reading that has been negative thousands of times running.
    /// </summary>
    [Fact]
    public void A_settled_proof_is_never_at_risk_from_a_later_trim()
    {
        Assert.True(ClientOrderIdProof.Distinct.IsSettled());
        Assert.False(ClientOrderIdProof.SameRef.IsSettled());
        Assert.False(ClientOrderIdProof.NotProven.IsSettled());
    }

    // ------------------------------------------------------------------ the composed read-back

    /// <summary>
    /// The session the product is actually waiting for, run end to end through the composed rule:
    /// nothing found, then our own instance, then our clone, then finally an order of ATAS's own.
    /// Only the last step may report the capability, and no earlier step may block it.
    /// </summary>
    [Fact]
    public void A_session_of_our_own_objects_neither_proves_nor_blocks_the_real_thing()
    {
        var touched = new AdapterTouchedOrders();
        var submitted = new Ord(Coid);
        touched.Add(submitted);

        var state = ClientOrderIdProof.NotProven;

        // Nothing in ATAS's book carries the id yet.
        state = Advance(state, Scan(touched, submitted, []));
        Assert.Equal(ClientOrderIdProof.NotProven, state);
        Assert.Equal("notfound", ClientOrderIdProofs.Token(state, 1, 1));

        // ATAS's collection holds the instance we handed it. The measured reading on real ATAS.
        state = Advance(state, Scan(touched, submitted, [submitted]));
        Assert.Equal(ClientOrderIdProof.SameRef, state);
        Assert.False(state.ProvesRoundTrip());

        // A modify replaces it with our clone. A different object, our id — and still not a proof.
        var clone = new Ord(Coid);
        touched.Add(clone);
        state = Advance(state, Scan(touched, submitted, [clone]));
        Assert.Equal(ClientOrderIdProof.SameRef, state);
        Assert.False(state.ProvesRoundTrip());
        Assert.Equal("proven-sameref", ClientOrderIdProofs.Token(state, 2, 3));

        // An order ATAS built, carrying our identifier. THE round trip.
        state = Advance(state, Scan(touched, submitted, [clone, new Ord(Coid, id: "7968889")]));
        Assert.Equal(ClientOrderIdProof.Distinct, state);
        Assert.True(state.ProvesRoundTrip());
        Assert.Equal("proven-distinct", ClientOrderIdProofs.Token(state, 2, 4));
    }

    /// <summary>
    /// One read-back pass over ATAS's order collection, composed exactly as
    /// <c>AtasStrategyAdapter.ProveClientOrderId</c> composes it — the comment test, the
    /// broker-id test, the identity test, and the whole-pass scan that stops only on a match nobody
    /// here touched.
    ///
    /// It MIRRORS the adapter; it is not the adapter and does not pretend to test it. That file
    /// compiles on one Windows machine and nowhere else. What is under test is that the decision it
    /// delegates composes into the answers above — which is the part that could be got wrong
    /// silently, and the part that was.
    /// </summary>
    static ClientOrderIdProof Scan(
        AdapterTouchedOrders touched, object? submitted, IEnumerable<Ord> book, string clientOrderId = Coid)
    {
        Ord? match = null;
        var matchIsOurs = true;
        foreach (var o in book)
        {
            if (!string.Equals(o.Comment, clientOrderId, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(o.Id)) continue;
            match = o;
            var ours = ReferenceEquals(o, submitted) || !touched.CountsAsEvidence(o);
            if (!ours) { matchIsOurs = false; break; }
        }
        return match is null ? ClientOrderIdProof.NotProven : ClientOrderIdProofs.Observed(matchIsOurs);
    }

    /// <summary>The monotonic write the adapter performs under its gate: only ever strengthen.</summary>
    static ClientOrderIdProof Advance(ClientOrderIdProof standing, ClientOrderIdProof observed) =>
        observed.Supersedes(standing) ? observed : standing;
}
