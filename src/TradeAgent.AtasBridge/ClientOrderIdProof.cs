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
///   SameRef    a match was found, and it is REFERENCE-EQUAL to the Order instance the adapter
///              constructed, set Comment on, and handed to ATAS. The identifier came back because
///              it never left: the adapter read its own field off its own object. The only thing
///              established is that ATAS assigned an Order.Id. NOT a round trip.
///   Distinct   a genuinely different object carried our identifier. ATAS put the id onto something
///              this adapter did not write, which is the round trip rule 1 asks about, observed.
///
/// The live reading on real ATAS 8.0.14.397, taken 2026-08-28 with a resting limit order on a sim
/// account, was <see cref="SameRef"/>. That is why this type exists.
/// </summary>
public enum ClientOrderIdProof
{
    NotProven = 0,
    SameRef = 1,
    Distinct = 2
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
    /// THE CAPABILITY. True for <see cref="ClientOrderIdProof.Distinct"/> and nothing else.
    ///
    /// <see cref="ClientOrderIdProof.SameRef"/> is a real match — the comment is genuinely there,
    /// on an order genuinely in ATAS's collection, genuinely carrying a broker id — and it proves
    /// nothing, because the object it is read off is the one the adapter submitted. Reporting true
    /// from it would be rule 1 faked by the exact mechanism rule 1 names, and reconciliation after
    /// a dropped pipe would then be permitted on a round trip nobody has performed.
    ///
    /// It is a predicate rather than a stored bool on purpose. A second variable holding "proven"
    /// alongside the observation is how the boolean and the <c>coid=</c> diagnostic drift apart,
    /// and a boolean that disagrees with the diagnostic beside it is worse than either alone.
    /// </summary>
    public static bool ProvesRoundTrip(this ClientOrderIdProof proof) =>
        proof is ClientOrderIdProof.Distinct;

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
    /// So: settled means Distinct, which today is the same set as <see cref="ProvesRoundTrip"/>.
    /// That tie is deliberate and asserted in the tests rather than assumed — the two would part
    /// company the moment a stronger reading than Distinct is added (an id recovered from a source
    /// outside this process, say), and the latch must follow the strongest reading, not the
    /// capability.
    /// </summary>
    public static bool IsSettled(this ClientOrderIdProof proof) =>
        proof is ClientOrderIdProof.Distinct;

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
    /// The reading, from the one measurement the adapter can make: was the order that carried our
    /// identifier back the very instance we handed to ATAS?
    ///
    /// Reference identity, never equality — ATAS's Order does not override equality anywhere in the
    /// API dump, and the question is literally "is this the same object", not "does it look the
    /// same". The caller does the <c>ReferenceEquals</c>; this turns its answer into a proof state
    /// so the mapping is not written out longhand at the one call site that matters.
    /// </summary>
    public static ClientOrderIdProof Observed(bool sameInstance) =>
        sameInstance ? ClientOrderIdProof.SameRef : ClientOrderIdProof.Distinct;

    /// <summary>
    /// The rule-1 read-back in one token, for <c>BridgeHello.TradingSurface</c>'s <c>coid=</c>
    /// field. Five readings, kept apart because they mean five different things and only one of
    /// them is proof:
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
    ///   proven-distinct  a genuinely different object carried our identifier. The round trip.
    ///
    /// THESE FIVE STRINGS ARE A WIRE CONTRACT, not prose. tools/probe/Program.cs switches on them
    /// verbatim and BUILD-STATUS.md quotes them as evidence. Changing one silently turns a recorded
    /// reading into an unrecognised one, so they are pinned by test.
    /// </summary>
    public static string Token(ClientOrderIdProof proof, int attempts, int checks) => proof switch
    {
        ClientOrderIdProof.Distinct => "proven-distinct",
        ClientOrderIdProof.SameRef => "proven-sameref",
        _ => attempts == 0 ? "unattempted" : checks == 0 ? "unchecked" : "notfound"
    };
}
