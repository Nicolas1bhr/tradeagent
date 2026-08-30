namespace TradeAgent.AtasBridge;

/// <summary>
/// WHAT THE RULE-1 READ-BACK ACTUALLY OBSERVED — which is a different question from whether it
/// matched, and it is the whole of what <c>SupportsClientOrderId</c> is allowed to report.
///
/// Ordered WEAKEST TO STRONGEST and compared as such — the same convention the quote sources in
/// <c>AtasStrategyAdapter</c> follow, and for the same reason: a weaker reading must never displace
/// a stronger one that has already been taken.
///
///   NotProven  nothing carrying an id THIS adapter submitted has been found in ATAS's own order
///              collection with a broker-assigned Id on it. On its own that is three different
///              facts, not one — see <see cref="ClientOrderIdProofs.Token"/>.
///   SameRef    a match was found, and the object carrying our identifier is one THIS ADAPTER
///              TOUCHED. The identifier came back because it never left: the adapter read its own
///              field off its own object. The only thing established is that ATAS assigned an
///              Order.Id. NOT a round trip.
///
///              The measured reading was literally the instance Place submitted, which is where
///              both the name and the proven-sameref token come from. The state deliberately
///              covers a WIDER family than that one instance — every object this adapter
///              constructed or wrote a Comment onto, tracked by <see cref="AdapterTouchedOrders"/>
///              — because every member of that family carries our identifier for exactly the same
///              reason, and telling them apart would be a distinction without a difference: none
///              of them proves anything about ATAS.
///   Distinct   a genuinely different object carried our identifier — different, and one this
///              adapter never touched. ATAS put the id onto something we did not write, which is
///              the round trip rule 1 asks about, observed.
///
///              WITHIN ONE SESSION that is real evidence, because the same-reference outcome was
///              available and on real ATAS it is what happened. ACROSS a restart it is worth
///              nothing: a fresh process has constructed no Order at all, so every match is
///              reference-distinct by construction. That is why the reading below exists and why
///              the latch follows it rather than this one.
///   CrossSession
///              an order carrying an identifier a PREVIOUS process of this product submitted was
///              found in ATAS's collection, carrying the broker order id that previous process
///              saw ATAS assign. The claim "we submitted this id" was written down before the
///              order existed, by a process that is gone by the time the claim is read, so it
///              cannot be a story composed afterwards to fit an order somebody found.
///
/// The live reading on real ATAS 8.0.14.397, taken 2026-08-28 with a resting limit order on a sim
/// account, was <see cref="SameRef"/>. That is why this type exists.
/// </summary>
public enum ClientOrderIdProof
{
    NotProven = 0,
    SameRef = 1,
    Distinct = 2,

    /// <summary>
    /// THE READING THE RESTART EXPERIMENT TAKES, and the strongest one obtainable from inside a
    /// chart strategy.
    ///
    /// Established when an order in ATAS's own collection carries an identifier recorded in
    /// <see cref="CoidWitness"/> by a DIFFERENT session of this product, alongside the broker order
    /// id that session recorded ATAS assigning. Three things have to line up and each is written by
    /// a different party: the identifier is one this product submitted (the write-ahead record), the
    /// process that submitted it is not the one reading it back (the session id), and the order in
    /// front of us is the order that record is about (the broker order id, which this product never
    /// chose — see <see cref="CoidWitness.Identified"/>).
    ///
    /// WHAT IT DOES NOT PROVE, AND THE SENTENCE LIVES HERE RATHER THAN ONLY IN A DOC BECAUSE THIS
    /// IS WHERE SOMEBODY WILL READ IT. A cross-session match cannot distinguish ATAS rebuilding the
    /// order from the BROKER'S OWN ANSWER on reconnect, from ATAS rehydrating the order out of its
    /// own local store. Both survive a process restart and both look identical from inside a chart
    /// strategy: the same Order object, in the same collection, with the same Comment and the same
    /// Id. Only the broker's own report of the order separates them, and that is not a source this
    /// software can read at runtime. So this reading says the identifier survives ATAS being
    /// restarted; it does not say the identifier reached the broker.
    ///
    /// That bound is why it reports the capability rather than settling the question of rule 1
    /// forever: reconciliation after a dropped pipe needs the identifier to survive whatever killed
    /// the pipe, which is exactly what this measures.
    /// </summary>
    CrossSession = 3
}

/// <summary>
/// THE DECISION THAT GATES AUTONOMOUS LIVE TRADING, AND THE REASON IT IS NOT IN THE ADAPTER.
///
/// Rule 1 on <see cref="IAtasAdapter"/> says: carry ClientOrderId onto the broker order, read it
/// back, and if the backend cannot round-trip it, report <c>SupportsClientOrderId = false</c> —
/// "do not fake it". Combined with <c>SupportsOrderHistory</c> that boolean is the whole of
/// <c>ConnectorCapabilities.ReconciliationProvable</c>, which is what the gateway consults before
/// it will permit LIVE_AUTONOMOUS. So "which observations count as a round trip" is the single
/// most consequential predicate in this product.
///
/// It used to live inside <c>AtasStrategyAdapter.cs</c>, which the bridge project REMOVES from
/// compilation unless built with <c>-p:AtasBridgeBuild=true</c> on a Windows machine with ATAS
/// installed. No test on any other machine could reach it, so nothing checked it — the same shape
/// as trap 9 in docs/RESUME-HERE.md, where a capability that was true from the first frame meant no
/// test ever exercised the frame that carried it. The adapter now holds only the ATAS-specific
/// observation (is this the object I handed in?); the judgement made from that observation is here,
/// in a file every machine compiles and every test run exercises.
/// </summary>
public static class ClientOrderIdProofs
{
    /// <summary>
    /// THE CAPABILITY. True for <see cref="ClientOrderIdProof.Distinct"/> and
    /// <see cref="ClientOrderIdProof.CrossSession"/>, and nothing else.
    ///
    /// <see cref="ClientOrderIdProof.SameRef"/> is a real match — the comment is genuinely there,
    /// on an order genuinely in ATAS's collection, genuinely carrying a broker id — and it proves
    /// nothing, because the object it is read off is the one the adapter submitted. Reporting true
    /// from it would be rule 1 faked by the exact mechanism rule 1 names, and reconciliation after
    /// a dropped pipe would then be permitted on a round trip nobody has performed.
    ///
    /// Two readings report the capability because two different observations each answer rule 1,
    /// on different evidence: Distinct is "ATAS wrote our identifier onto an object we never
    /// touched", within one session. CrossSession is "our identifier was still on an order after
    /// the process that submitted it had gone", which is the stronger claim and the one
    /// reconciliation after a dropped pipe actually rests on.
    ///
    /// It is a predicate rather than a stored bool on purpose. A second variable holding "proven"
    /// alongside the observation is how the boolean and the <c>coid=</c> diagnostic drift apart,
    /// and a boolean that disagrees with the diagnostic beside it is worse than either alone.
    /// </summary>
    public static bool ProvesRoundTrip(this ClientOrderIdProof proof) =>
        proof is ClientOrderIdProof.Distinct or ClientOrderIdProof.CrossSession;

    /// <summary>
    /// THE LATCH: may the adapter stop looking?
    ///
    /// Only once the STRONGEST possible reading has been taken — not once ANY reading has been
    /// taken. The distinction is the whole point of this method existing separately from
    /// <see cref="ProvesRoundTrip"/>.
    ///
    /// <c>ProveClientOrderId</c> opens with an early return so the read-back does not rescan ATAS's
    /// order book on every order event once the answer is known. While a SameRef match set that
    /// latch, a SameRef reading froze the proof for the life of the process: a genuinely Distinct
    /// match arriving later in the same session — the very reading the product is waiting for —
    /// could never be observed, and the freeze would be invisible, because the diagnostic would go
    /// on reporting a perfectly truthful <c>proven-sameref</c>. Latching on the vacuous reading is
    /// how you make the real proof permanently unreachable and see nothing wrong.
    ///
    /// THE TWO PREDICATES HAVE NOW PARTED COMPANY, WHICH IS WHAT THEY WERE KEPT SEPARATE FOR. The
    /// note that used to sit here said they would separate "the moment a stronger reading than
    /// Distinct is added (an id recovered from a source outside this process, say)". That reading
    /// is <see cref="ClientOrderIdProof.CrossSession"/> and it is here, so:
    ///
    ///   * <see cref="ProvesRoundTrip"/> is true for Distinct AND CrossSession — both answer rule 1.
    ///   * settled is CrossSession ALONE.
    ///
    /// A Distinct taken early in a fresh session must NOT latch, and that is the whole safety of
    /// this method. After ATAS restarts, the adapter has constructed no Order at all, so an
    /// in-session read-back of some unrelated id could reach Distinct trivially and stop the scan —
    /// and the CrossSession reading the restart experiment exists to take would never be reached,
    /// with nothing looking wrong because the diagnostic would go on truthfully saying
    /// proven-distinct. That is exactly the freeze SameRef caused before, one reading up.
    /// </summary>
    public static bool IsSettled(this ClientOrderIdProof proof) =>
        proof is ClientOrderIdProof.CrossSession;

    /// <summary>
    /// May <paramref name="candidate"/> replace <paramref name="standing"/>? Only if it is strictly
    /// stronger. This is what keeps the record monotonic.
    ///
    /// The latch alone does not do it. <c>ProveClientOrderId</c> checks the latch and writes the
    /// result under two SEPARATE lock acquisitions, with an enumeration of ATAS's order collection
    /// in between, and it is called from two places at once — <c>Place</c> on the pipe thread and
    /// the order-event fan on ATAS's. Two passes can therefore both get past the latch, and without
    /// this the slower one could write SameRef over a Distinct the faster one had just established,
    /// silently demoting a real proof to a vacuous one.
    /// </summary>
    public static bool Supersedes(this ClientOrderIdProof candidate, ClientOrderIdProof standing) =>
        candidate > standing;

    /// <summary>
    /// The reading, from the one measurement the adapter can make: is the order that carried our
    /// identifier back an object THIS ADAPTER TOUCHED?
    ///
    /// It used to ask the narrower question — "is it the very instance we handed to ATAS?" — and
    /// narrow was wrong, because the adapter builds and labels order objects in two other places.
    /// <see cref="AdapterTouchedOrders"/> holds the whole family and says which. The mapping is
    /// unchanged: ours means <see cref="ClientOrderIdProof.SameRef"/>, and only an object we never
    /// touched can be <see cref="ClientOrderIdProof.Distinct"/>.
    ///
    /// Reference identity, never equality — ATAS's Order does not override equality anywhere in the
    /// API dump, and the question is literally "is this an object we touched", not "does it look
    /// like one". The caller does the identity test; this turns its answer into a proof state so
    /// the mapping is not written out longhand at the one call site that matters.
    ///
    /// IT CANNOT PRODUCE <see cref="ClientOrderIdProof.CrossSession"/> AND MUST NOT BE MADE TO.
    /// Object identity is a question about this process, and after a restart it has a free answer:
    /// nothing here was constructed by us, so "untouched" is true of everything and this method
    /// would hand back Distinct for any match at all. The cross-session reading is established from
    /// <see cref="CoidWitness"/> — a durable record written before the order existed — and never
    /// from what an object is or is not.
    /// </summary>
    public static ClientOrderIdProof Observed(bool adapterTouched) =>
        adapterTouched ? ClientOrderIdProof.SameRef : ClientOrderIdProof.Distinct;

    /// <summary>
    /// The rule-1 read-back in one token, for <c>BridgeHello.TradingSurface</c>'s <c>coid=</c>
    /// field. Six readings, kept apart because they mean six different things and only two of
    /// them are proof:
    ///
    ///   unattempted      no order carrying a client order id has been submitted yet. Says nothing
    ///                    about ATAS.
    ///   unchecked        one was submitted, and no read-back has ever run — there was no trading
    ///                    surface to look in at the moment it would have. Still says nothing.
    ///   notfound         a read-back RAN and no order in ATAS's own collection carried our id with
    ///                    a broker-assigned Id on it. This one is evidence, and it is negative.
    ///   proven-sameref   an order carried it back — and it is REFERENCE-EQUAL to the instance this
    ///                    adapter submitted. True by construction; proves nothing beyond Order.Id
    ///                    being assigned. MUST NOT be trusted as a round trip, and does not set
    ///                    SupportsClientOrderId.
    ///   proven-distinct  a genuinely different object carried our identifier. The round trip,
    ///                    within one session.
    ///   proven-crosssession
    ///                    an identifier a PREVIOUS process of this product wrote down before
    ///                    submitting was found on an order in ATAS's collection, carrying the
    ///                    broker id that process recorded. It survived the process that made it.
    ///
    /// THESE SIX STRINGS ARE A WIRE CONTRACT, not prose. tools/probe/Program.cs switches on them
    /// verbatim and BUILD-STATUS.md quotes them as evidence. Changing one silently turns a recorded
    /// reading into an unrecognised one, so they are pinned by test. The sixth joins the five that
    /// were already on the wire; it does not replace any of them.
    /// </summary>
    public static string Token(ClientOrderIdProof proof, int attempts, int checks) => proof switch
    {
        ClientOrderIdProof.CrossSession => "proven-crosssession",
        ClientOrderIdProof.Distinct => "proven-distinct",
        ClientOrderIdProof.SameRef => "proven-sameref",
        _ => attempts == 0 ? "unattempted" : checks == 0 ? "unchecked" : "notfound"
    };
}

/// <summary>
/// EVERY ORDER OBJECT THE ADAPTER TOUCHED, BY REFERENCE IDENTITY — the set of objects that can
/// never be evidence of a round trip, because our identifier is on them for a reason that has
/// nothing whatever to do with ATAS.
///
/// WHY THIS EXISTS WHEN THE ADAPTER ALREADY HAS <c>_submitted</c>.
///
/// <c>_submitted</c> is keyed by client order id and holds only what <c>Place</c> built, so the
/// question it can answer is "is this THE instance I handed in for this id". That is narrower than
/// the question rule 1 needs answered, which is "did ATAS put my identifier onto this object, or
/// did I?". Two objects the adapter itself produces slip straight through the narrow test, and
/// both would have been recorded as <see cref="ClientOrderIdProof.Distinct"/> — a round trip the
/// adapter performed against itself, with <c>SupportsClientOrderId</c> flipping true on it:
///
///   * <c>Modify</c>'s <c>order.Clone()</c>. Clone copies Comment, so the replacement is an object
///     THIS ADAPTER CONSTRUCTED carrying OUR client order id, and <c>_submitted[id]</c> still holds
///     the original — so <c>ReferenceEquals(candidate, mine)</c> is false for it. Whether ATAS's
///     own order collection ever contains that replacement is NOT VERIFIED: the API dump at
///     docs/atas-api-8.0.14.397.txt lists public members only and cannot answer it. The guard must
///     not depend on the answer. A proof that turns on an unverified platform behaviour is rule 1
///     faked with extra steps.
///   * <c>ClosePosition</c>'s <c>created.Comment = clientOrderId</c>, written by hand onto an order
///     ATAS created. Safe today only because that id never enters <c>_submitted</c>, so the read-back
///     refuses it at its first guard — incidental safety, not designed safety. Registered here so
///     that the guard survives anyone who later makes <c>_submitted</c> symmetric.
///
/// REFERENCE IDENTITY, NEVER EQUALITY. ATAS's Order does not override equality anywhere in the
/// dump, so <c>Equals</c> would be identity today and could silently become value equality in any
/// future SDK — at which point two different orders that merely looked alike would be conflated,
/// in the one place where being wrong reads as a proof. <see cref="ReferenceEqualityComparer"/>
/// makes the intent explicit and immune to that.
///
/// NOT THREAD-SAFE, ON PURPOSE. It is written by <c>Place</c> and <c>Modify</c> on the bridge's
/// pipe thread and read by the order-event fan on ATAS's, and every caller holds the adapter's
/// <c>_gate</c> — the same lock every other side table in that file is under. A second lock in
/// here would be a second lock ordering to reason about for no gain.
/// </summary>
public sealed class AdapterTouchedOrders
{
    readonly HashSet<object> _touched = new(ReferenceEqualityComparer.Instance);
    bool _forgotten;

    /// <summary>How many objects are being remembered. For diagnostics and for <see cref="Trim"/>.</summary>
    public int Count => _touched.Count;

    /// <summary>
    /// Whether this set has ever dropped an entry, and therefore whether it can still be trusted to
    /// say that an object is one the adapter did NOT touch. Latches true and never clears.
    /// </summary>
    public bool HasForgotten => _forgotten;

    /// <summary>
    /// Record that the adapter constructed this order object, or wrote a Comment onto it.
    ///
    /// CALL IT BEFORE THE OBJECT CAN BE SEEN BY ANYONE ELSE — before handing it to ATAS, and before
    /// writing the Comment onto an order ATAS already owns. The order-event fan runs on ATAS's
    /// thread and can reach the read-back the instant the object becomes visible, so registering
    /// afterwards leaves a window in which our own object is the proof.
    ///
    /// Null is accepted and ignored rather than throwing: this is called from write paths whose
    /// failure mode must never be an exception raised by bookkeeping.
    /// </summary>
    public void Add(object? order)
    {
        if (order is not null) _touched.Add(order);
    }

    /// <summary>
    /// MAY THIS OBJECT COUNT AS EVIDENCE THAT ATAS CARRIED OUR IDENTIFIER? True only for an object
    /// the adapter never touched, on a set that has never forgotten anything.
    ///
    /// The two false cases are different facts and both must answer the same way:
    ///
    ///   * we touched it — then our identifier is on it because we put it there, and the reading is
    ///     <see cref="ClientOrderIdProof.SameRef"/>: a real match that proves nothing;
    ///   * the set has forgotten (see <see cref="Trim"/>) — then we cannot show that we did NOT
    ///     touch it, and an unprovable negative is not a proof.
    ///
    /// A null candidate is not evidence. Nothing in the adapter can produce one, and answering true
    /// for the absence of an object would be the wrong direction to be defensive in.
    /// </summary>
    public bool CountsAsEvidence(object? candidate) =>
        candidate is not null && !_forgotten && !_touched.Contains(candidate);

    /// <summary>
    /// Same discipline as the adapter's <c>Trim()</c>: this bridge stays loaded for weeks, so the
    /// set cannot grow forever. Over <paramref name="cap"/>, drop the lot.
    ///
    /// WHAT HAPPENS TO A PROOF AFTER AN ENTRY HAS BEEN TRIMMED AWAY — the whole reason this is not
    /// a bare <c>Clear()</c>. Dropping quietly would leave a forgotten clone looking exactly like an
    /// object of ATAS's own, and the very next read-back would record it as
    /// <see cref="ClientOrderIdProof.Distinct"/>: trimming would MANUFACTURE the proof this type
    /// exists to prevent. So the drop is recorded, and from that moment
    /// <see cref="CountsAsEvidence"/> answers false for every object, permanently — the set can no
    /// longer demonstrate that anything is untouched, so it refuses every proof instead of
    /// inventing one. That is the direction to fail in.
    ///
    /// WHAT THE PERMANENCE COSTS, WHICH IS ALMOST NOTHING. The proof latches on the first
    /// <see cref="ClientOrderIdProof.Distinct"/> reading (see <see cref="ClientOrderIdProofs.IsSettled"/>),
    /// so the only session that can ever reach the cap is one that has already answered "not proven"
    /// <paramref name="cap"/> times over. Refusing the cap-plus-first is refusing a reading that has
    /// failed thousands of times running. The comparison is <c>_submitted</c>, which clears the same
    /// way and whose documented cost is that "a very old id stops being provable".
    ///
    /// Retention is bounded by <paramref name="cap"/> strong references to Order objects, which is
    /// the bound <c>_submitted</c> already carries — not a new class of retention.
    /// </summary>
    /// <returns>Whether anything was dropped by this call.</returns>
    public bool Trim(int cap)
    {
        if (_touched.Count <= cap) return false;
        _touched.Clear();
        _forgotten = true;
        return true;
    }
}
