using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHICH ALLOCATION POLICY THIS BUILD APPLIES. One word, hashed into every allocation id.
///
/// <para><c>docs/COUNCIL.md</c>:56-57 puts the capital allocator among the things that are "code and
/// never a role", and :62 has "code applies the promotion and allocation policy so neither director
/// can veto an eligible deployment forever". A policy that is code has a VERSION for the same reason
/// a scoring policy does (<c>CampaignPolicy</c>): an allocation decided under one set of rules is not
/// an allocation decided under another, and a row that did not say which was applied is a row a later
/// reader cannot account for.</para>
///
/// <para>V1 is the whole of this build's policy and it is deliberately small: a ceiling is a number
/// the OWNER declared, for ONE promoted version, and nothing here is derived from a balance or an
/// equity curve — see `docs/CONTRACTS.md`, where both of those are recorded as choices.</para>
/// </summary>
public static class AllocationPolicy
{
    /// <summary>The only policy this build implements. Hashed into <see cref="AllocationRow.Id"/>.</summary>
    public const string V1 = "allocation-policy-v1";
}

/// <summary>
/// WHAT KIND OF THING AN ALLOCATION IS, AND THE TWO ARE NOT COMPARABLE.
///
/// <para><see cref="Live"/> is the owner's own capital, behind a promoted version, put there by two
/// presses on the Safety page. <see cref="Paper"/> is the app's own policy putting a paper-eligible
/// version into an envelope the owner granted once — no money, no real venue, and no authority
/// whatever in <c>LIVE_CONFIRM</c> or <c>LIVE_AUTONOMOUS</c>.</para>
///
/// <para><b>A row with no scope at all is LIVE</b> (<see cref="AllocationRow.EffectiveScope"/>). Every
/// allocation written before schema 25 was written by <c>TradingGateway.Allocate</c>, which is the
/// owner pressing twice, and an owner's press is never a paper grant. Reading the absence the other
/// way round would silently reclassify every existing capital decision as an experiment.</para>
/// </summary>
public static class AllocationScope
{
    public const string Live = "live";
    public const string Paper = "paper";
}

/// <summary>
/// ONE IMMUTABLE ALLOCATION OF CAPITAL TO ONE PROMOTED STRATEGY VERSION.
///
/// <para><b><see cref="Id"/> is a SHA-256 over the seven facts the allocation IS</b> — the version,
/// the promotion that made it eligible, the policy version applied, the quantity ceiling, the value
/// ceiling, the currency those are in, and the instant it takes effect. It is the
/// <see cref="PromotionRow"/> shape and it is that shape for the same reason: an id minted from the
/// CLOCK would let one decision write two rows, and a later reader would have no way of telling which
/// of them the gateway was enforcing. <c>docs/COUNCIL.md</c>:210-211 names "which strategy or
/// allocation caused an operation" as one of the four things that cannot be recovered afterwards.</para>
///
/// <para><b><see cref="EffectiveTo"/>, <see cref="Reason"/> and <see cref="At"/> are NOT in the id.</b>
/// They are the account of the decision rather than the decision: the same ceiling for the same
/// version from the same instant is the same allocation whoever wrote it down and whenever they did,
/// and putting them in the hash would only let a build that disagreed with itself write a second row
/// instead of colliding with the first. <c>ON CONFLICT DO NOTHING</c> is what makes the first one
/// stand.</para>
///
/// <para><b>There is no update and no delete</b> (see <see cref="Allocations"/>), so an allocation is
/// never edited and never withdrawn in place. It is SUPERSEDED: the owner records a fresh allocation
/// from a later instant, which is a different <see cref="EffectiveFrom"/> and therefore a different
/// id, and <see cref="Allocations.StandingForLive"/> answers with the newest one that stands. That
/// is the choice `docs/CONTRACTS.md` states, and it is the only shape in which a capital decision is a record
/// rather than a value somebody can quietly move after the outcome is known.</para>
/// </summary>
public sealed record AllocationRow(
    string Id,
    string VersionId,
    string PromotionId,
    string PolicyVersion,
    decimal MaxQuantity,
    decimal? MaxNotional,
    string Currency,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    string Reason,
    DateTimeOffset At)
{
    /// <summary>
    /// THE BOUND TUPLE, HASHED: seven facts, newline separated, in this order. The order and the
    /// spelling are part of the contract — an id is compared against rows written by earlier builds.
    ///
    /// <para>An absent value ceiling hashes as an EMPTY field rather than as a zero, because zero is a
    /// number the owner could mean and "not enforced" is not (the reading
    /// <see cref="RiskPolicy.MaxNotionalPerOrder"/> has). Both decimals are spelled by
    /// <see cref="Num"/>, so <c>1</c> and <c>1.00</c> are one allocation and not two.</para>
    /// </summary>
    public static string IdOf(string versionId, string promotionId, string policyVersion,
        decimal maxQuantity, decimal? maxNotional, string currency, DateTimeOffset effectiveFrom) =>
        Sha256Hex.Of(string.Join('\n',
            versionId,
            promotionId,
            policyVersion,
            Num(maxQuantity),
            maxNotional is { } n ? Num(n) : "",
            currency,
            Sql.T(effectiveFrom)));

    /// <summary>
    /// WHICH KIND OF ALLOCATION THIS ROW IS, or null because it is one written before schema 25 — see
    /// <see cref="AllocationScope"/>, where null is LIVE. Init-only properties rather than positional
    /// parameters so the seven facts and their order stay exactly what they were: an id is compared
    /// against rows written by earlier builds, and <see cref="IdOf"/> is the contract.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// THE PLATFORM, THE MODE AND THE ACCOUNT A PAPER ROW WAS WRITTEN FOR, and null on a live row.
    ///
    /// <para>They are what stop a paper allocation authorising anything anywhere else. A live
    /// allocation names none of them deliberately: capital is the owner's decision about a VERSION and
    /// applies wherever their account is, which is the reading <c>docs/CONTRACTS.md</c> already states
    /// — a paper grant is a decision about one account of one platform and says so.</para>
    /// </summary>
    public string? ConnectorId { get; init; }

    /// <summary>See <see cref="ConnectorId"/>. Always <c>PAPER</c> on a paper row, and null on a live one.</summary>
    public string? Mode { get; init; }

    /// <summary>See <see cref="ConnectorId"/>.</summary>
    public string? AccountId { get; init; }

    /// <summary>
    /// THE GRANT THIS PAPER ROW WAS WRITTEN UNDER, and null on a live row. Withdrawing that grant stops
    /// this row authorising anything at once, because <see cref="Allocations.StandingForPaper"/> asks
    /// the envelope ledger at read time and never a copy stored here.
    /// </summary>
    public string? EnvelopeId { get; init; }

    /// <summary>Whether this row is a PAPER allocation. The one question the money path splits on.</summary>
    public bool IsPaper => string.Equals(Scope, AllocationScope.Paper, StringComparison.Ordinal);

    /// <summary>
    /// The scope this row IS, with null read as <see cref="AllocationScope.Live"/>. For surfaces; the
    /// money path asks <see cref="IsPaper"/>, which is the same question with no third answer.
    /// </summary>
    public string EffectiveScope => IsPaper ? AllocationScope.Paper : AllocationScope.Live;

    /// <summary>
    /// THE PAPER TUPLE, HASHED: the same seven facts in the same order, and then the five that make a
    /// paper allocation the thing it is — the scope word itself, the platform, the mode, the account
    /// and the grant.
    ///
    /// <para><b>The scope facts are in the hash and that is not decoration.</b> Two envelopes on two
    /// accounts carrying the same version at the same instant are TWO allocations; hashing the seven
    /// facts alone would make them one id, the second write would be an <c>ON CONFLICT DO NOTHING</c>
    /// that changed nothing, and the second account would read as allocated while its row named the
    /// first. That is the mutant <c>PaperAllocationGateTests</c> measures.</para>
    ///
    /// <para>It is a SEPARATE method rather than an optional tail on <see cref="IdOf"/> so that a live
    /// id cannot move: <see cref="IdOf"/> hashes seven fields and did before this rung.</para>
    /// </summary>
    public static string PaperIdOf(string versionId, string promotionId, string policyVersion,
        decimal maxQuantity, decimal? maxNotional, string currency, DateTimeOffset effectiveFrom,
        string connectorId, string mode, string accountId, string envelopeId) =>
        Sha256Hex.Of(string.Join('\n',
            versionId,
            promotionId,
            policyVersion,
            Num(maxQuantity),
            maxNotional is { } n ? Num(n) : "",
            currency,
            Sql.T(effectiveFrom),
            AllocationScope.Paper,
            connectorId,
            mode,
            accountId,
            envelopeId));

    /// <summary>The id this row's facts hash to, whatever <see cref="Id"/> currently holds.</summary>
    public string ComputedId => IsPaper
        ? PaperIdOf(VersionId, PromotionId, PolicyVersion, MaxQuantity, MaxNotional, Currency,
            EffectiveFrom, ConnectorId ?? "", Mode ?? "", AccountId ?? "", EnvelopeId ?? "")
        : IdOf(VersionId, PromotionId, PolicyVersion, MaxQuantity, MaxNotional, Currency, EffectiveFrom);

    /// <summary>
    /// The one spelling of a ceiling, in the id and nowhere else it could drift. Trailing zeros are
    /// dropped, so the owner typing <c>1.0</c> and the owner typing <c>1</c> are the same allocation.
    /// </summary>
    public static string Num(decimal d) => d.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Whether this row is in force at <paramref name="now"/> by its own two instants.</summary>
    public bool StandsAt(DateTimeOffset now) =>
        EffectiveFrom <= now && (EffectiveTo is not { } to || now < to);

    /// <summary>
    /// WHETHER A PROPOSED ALLOCATION GIVES A VERSION MORE ROOM THAN <paramref name="from"/> DOES.
    ///
    /// <para>The comparison <see cref="RiskPolicy.Widenings"/> makes, applied to the two ceilings an
    /// allocation has, and it is here rather than at the widget for the reason that one is in Core:
    /// "wider" is not "larger" on both fields. A quantity ceiling is larger-is-wider. A VALUE ceiling
    /// reads absent (or zero) as "not enforced", so turning it off is the widest move there is and a
    /// plain <c>&gt;</c> would read it as a narrowing and let it through on one press.</para>
    ///
    /// <para>Nothing standing at all widens by definition: the version could hold nothing, and now it
    /// can hold something.</para>
    /// </summary>
    public static bool Widens(AllocationRow? from, decimal quantity, decimal? notional) =>
        from is null
        || quantity > from.MaxQuantity
        || (from.MaxNotional is { } cap && cap > 0m && (notional is null or <= 0m || notional > cap));
}

/// <summary>
/// AN ALLOCATION THAT STANDS BY ITS DATES, AND WHERE THE PROMOTION UNDER IT STANDS RIGHT NOW.
///
/// <para>Two facts and they are deliberately two. A row is in force or it is not, which is arithmetic
/// on its own instants; whether the version it names may still trade at all is
/// <see cref="Promotions.Standing"/>'s answer and nobody else's, computed at read time against today's
/// facts. <see cref="Authorises"/> is the only question the money path may ask, and it is true for
/// exactly one combination of the two — so an allocation whose promotion has been invalidated is not
/// "an allocation with a note".</para>
///
/// <para><b>A PAPER row carries a third fact and answers a different question.</b>
/// <see cref="Envelope"/> is the grant it was written under, re-read at this instant, and it is null
/// for a live row and for a paper row whose grant has expired or been withdrawn. The LIVE arm of
/// <see cref="Authorises"/> is untouched and still asks <c>IsPromoted</c> and nothing else — one
/// question, which is what keeps an <c>IsPromoted</c> that started answering true for
/// <c>paper_eligible</c> catchable here rather than hidden behind an extra check.</para>
/// </summary>
public sealed record AllocationStanding(
    AllocationRow Allocation, PromotionStanding Promotion, PaperEnvelopeRow? Envelope = null)
{
    /// <summary>
    /// Whether this allocation may authorise an order right now.
    ///
    /// <para>Live: the promotion stands. Paper: the grant stands AND the verdict stands, where
    /// "stands" for a paper experiment includes <c>paper_eligible</c> — which is the whole distinction
    /// <c>docs/PRINCIPLES.md</c> § Evidence asks for, and it buys the version no capital, because a
    /// paper row is never read in a live mode at all.</para>
    /// </summary>
    public bool Authorises => Allocation.IsPaper
        ? Envelope is not null && (Promotion.IsPromoted || Promotion.IsPaperEligible)
        : Promotion.IsPromoted;
}

/// <summary>
/// WHAT THE LEDGER DID WITH ONE PROPOSED ALLOCATION, and why when it did nothing.
///
/// <para>A result rather than an exception, for the reason <c>DatasetStore.SetHoldout</c> and
/// <c>CampaignStore.Open</c> answer this shape: the one caller is the owner's own card, and the
/// sentence it has to put on the screen is the refusal itself. <see cref="Why"/> is always written,
/// including on the way through, so a surface that reports the success has something true to say.</para>
/// </summary>
public sealed record AllocationResult(bool Ok, string Why, AllocationRow? Allocation);

/// <summary>
/// THE ALLOCATION LEDGER: WHAT CAPITAL THE OWNER PUT BEHIND A PROMOTED VERSION, WRITTEN ONCE.
///
/// <para><b>The app is the only writer and there is no update and no delete.</b> Not "no pipe op" — no
/// method at all: this class exposes TWO writes (<see cref="Record"/>, the owner's capital, and
/// <see cref="RecordPaper"/>, the app's own policy inside an envelope the owner granted), both
/// <c>ON CONFLICT DO NOTHING</c>, and the reads below. The same rule <see cref="Promotions"/> has, for
/// a sharper version of the same reason: an allocation is the number the gateway refuses orders
/// against, and a limit its subject could edit is not a limit. There is no <c>trade</c> verb and no
/// pipe op behind any of this, so an agent that wanted more capital has nowhere to ask.</para>
///
/// <para><b>The two writers cannot reach each other's rows.</b> <see cref="Record"/> refuses a paper
/// scope and <see cref="RecordPaper"/> refuses anything that is not one, and the readers are split the
/// same way — <see cref="StandingForLive"/> sees live rows only and <see cref="StandingForPaper"/>
/// paper rows only. A paper allocation therefore has no mode, no reader and no code path in which it
/// could stand in for capital.</para>
///
/// <para><b>Withdrawal is a NEW ROW, never an edit.</b> See <see cref="AllocationRow"/>: the owner
/// records a fresh allocation from a later instant — a smaller ceiling, or a zero — and
/// <see cref="StandingForLive"/> answers with the newest row that stands. Nothing is ever overwritten,
/// so the sequence of what was allowed when is on the table where a reader can see it. A PAPER row is
/// withdrawn by withdrawing the ENVELOPE, which stops every row under it at once.</para>
/// </summary>
public sealed class Allocations(Database db)
{
    readonly Promotions _promotions = new(db);
    readonly Envelopes _envelopes = new(db);

    const string Cols =
        "id, version_id, promotion_id, policy_version, max_quantity, max_notional, currency, " +
        "effective_from, effective_to, reason, at, scope, connector_id, mode, account_id, envelope_id";

    /// <summary>
    /// RECORDS ONE ALLOCATION AGAINST A VERSION THAT STANDS PROMOTED AT THIS INSTANT, or leaves the
    /// row that is already there alone. Returns the row AS WRITTEN, with the id its seven facts hash
    /// to.
    ///
    /// <para><b>The eligibility is <see cref="Promotions.Standing"/> and never "a promotion row
    /// exists".</b> <c>docs/COUNCIL.md</c>:32-33 lets only a PROMOTED version execute, and :35 makes a
    /// changed assumption invalidate the evidence that rested on it — so a version whose holdout
    /// dataset has since been rejected, re-collected or reclassified, or which was judged under an
    /// interpreter or a scoring policy this build no longer applies, has no standing promotion and
    /// may not be given the owner's money. A row-exists check is the mutant: it reads an invalidated
    /// promotion as a live one and allocates capital on evidence TradeAgent has withdrawn.</para>
    ///
    /// <para><b>And it must be THAT promotion.</b> The id binds the allocation to the verdict that
    /// made it eligible; a caller naming some older verdict of the same version would be recording an
    /// allocation whose provenance does not match the reason it was allowed.</para>
    ///
    /// <para>The check and the write are ONE <see cref="Database.Write"/>, so nothing can change the
    /// standing in between. The id is computed here rather than taken from the caller, for the reason
    /// <see cref="Promotions.Record"/> computes its own: a caller that had to remember to hash the
    /// tuple is a caller that can forget, and an allocation keyed by anything else is an allocation
    /// that says nothing about what it was an allocation OF.</para>
    /// </summary>
    public AllocationResult Record(AllocationRow allocation) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(allocation);

        // THIS WRITER IS THE OWNER'S CAPITAL AND NOTHING ELSE. A paper scope arriving here is refused
        // rather than written as live: `TradingGateway.Allocate` is the only caller and it never sets
        // one, so a row that did would be a paper allocation that had walked in through the live door
        // and acquired the one thing a paper allocation must never have — authority in a live mode.
        if (allocation.IsPaper || allocation.EnvelopeId is { Length: > 0 })
            return new AllocationResult(false,
                "this is the live capital ledger and the allocation offered to it is scoped to paper, "
                + "so nothing was written. A paper allocation is written by TradeAgent's own policy "
                + "inside an envelope you granted, never by the capital card.", null);

        var standing = _promotions.Standing(allocation.VersionId);

        // THE GATE IS `IsPromoted` AND NOTHING ELSE, AND A PAPER-ELIGIBLE VERSION ONLY CHANGES THE
        // WORDS OF ITS REFUSAL.
        //
        // The temptation is a second condition above this one, and it is the wrong shape: it would make
        // an `IsPromoted` that started answering true for `paper_eligible` — the mutant — invisible
        // here, because the extra check would go on refusing while `AllocationStanding.Authorises` and
        // every other reader on the money path quietly began to allow. ONE question, asked once, is
        // what makes this the capital gate `docs/COUNCIL.md`:14-15 names.
        //
        // The sentence is its own because the general one ("does not stand promoted") would be shown on
        // the owner's card beside a verdict that had just come back FAVOURABLE. `docs/PRINCIPLES.md` §
        // Evidence keeps the meanings apart — "a paper experiment also cannot confer live authority" —
        // and the historical verdict beneath a paper experiment is weaker still: a result over months
        // the submission may already have been written around.
        if (!standing.IsPromoted)
            return new AllocationResult(false,
                standing.IsPaperEligible
                    ? $"version {Short(allocation.VersionId)} is paper-eligible and not promoted, so no "
                      + "capital was allocated to it: its favourable verdict is over held-back months "
                      + "that do not post-date its own freeze — historical evidence; paper only. Observe "
                      + "it forward on paper; only forward evidence collected after the freeze can "
                      + "promote a version, and only a promoted version may be given the account owner's "
                      + "money."
                    : $"version {Short(allocation.VersionId)} does not stand promoted, so no capital was "
                      + $"allocated to it: {standing.Why}", null);

        if (!string.Equals(standing.Promotion!.Id, allocation.PromotionId, StringComparison.Ordinal))
            return new AllocationResult(false,
                $"the promotion this allocation names ({Short(allocation.PromotionId)}) is not the one "
                + $"version {Short(allocation.VersionId)} stands on ({Short(standing.Promotion.Id)}), so "
                + "nothing was allocated.", null);

        if (allocation.MaxQuantity < 0m)
            return new AllocationResult(false,
                "a ceiling cannot be negative, so nothing was allocated. Zero is a real allocation and "
                + "means this version may open nothing.", null);

        var row = allocation with { Id = allocation.ComputedId, Scope = AllocationScope.Live };
        Insert(row);

        var written = ById(row.Id) ?? row;
        return new AllocationResult(true,
            $"version {Short(written.VersionId)} may trade up to {AllocationRow.Num(written.MaxQuantity)} "
            + $"from {written.EffectiveFrom:u}.", written);
    });

    /// <summary>
    /// RECORDS ONE PAPER ALLOCATION INSIDE ONE STANDING ENVELOPE, or leaves the row that is already
    /// there alone. The app's own policy is the only caller (<c>TradingGateway.AllocatePaperDue</c>);
    /// there is no press, no <c>trade</c> verb and no pipe op behind it.
    ///
    /// <para><b>Four questions, and the check and the write are ONE transaction</b> so nothing can move
    /// between them. THE VERDICT must stand as <c>promoted</c> or <c>paper_eligible</c> at this instant
    /// — <c>Promotions.Standing</c> and never "a promotion row exists", the same reading
    /// <see cref="Record"/> takes, so a version whose dataset has since been rejected is not observed
    /// forward on evidence TradeAgent has withdrawn. THE ENVELOPE must stand at this instant, read from
    /// its own ledger rather than from anything stored here. THE CEILING must be inside the envelope's,
    /// both figures, because the envelope is the whole of what the owner agreed to. And NO OTHER
    /// VERSION may already hold a standing paper allocation in it, up to <c>max_deployments</c>: an
    /// envelope is one experiment at a time, so what the owner granted cannot be spent twice over by
    /// the app noticing two eligible versions.</para>
    ///
    /// <para><b>None of this allocates capital and none of it authorises a live order.</b> The row is
    /// scoped <c>paper</c> and named to one platform, one mode and one account;
    /// <c>TradingGateway.AllocationFor</c> reads paper rows only in PAPER mode and live rows only
    /// otherwise, so there is no mode in which this row and the owner's capital are both on the
    /// table.</para>
    /// </summary>
    public AllocationResult RecordPaper(AllocationRow allocation, DateTimeOffset now) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(allocation);

        if (!allocation.IsPaper
            || allocation.ConnectorId is not { Length: > 0 } connector
            || allocation.AccountId is not { Length: > 0 } account
            || allocation.EnvelopeId is not { Length: > 0 } envelopeId
            || !string.Equals(allocation.Mode, TradingMode.PAPER.ToString(), StringComparison.Ordinal))
            return new AllocationResult(false,
                "a paper allocation names the scope, the platform, the mode, the account and the "
                + "envelope it was written under, and this one does not, so nothing was written.", null);

        var standing = _promotions.Standing(allocation.VersionId);

        // PROMOTED *OR* PAPER-ELIGIBLE, and this is the one place in the product where the second
        // answer opens anything. `docs/PRINCIPLES.md` § Evidence: "forward paper evidence cannot be
        // required before the very first paper run that produces it" — a version that can never reach
        // paper because it has no forward evidence can never acquire any. What it opens is an
        // experiment on an account the owner proved is a simulation, and nothing else.
        if (!standing.IsPromoted && !standing.IsPaperEligible)
            return new AllocationResult(false,
                $"version {Short(allocation.VersionId)} has no verdict that stands, so it was not put "
                + $"on paper: {standing.Why}", null);

        if (_envelopes.ById(envelopeId) is not { } envelope || !envelope.StandsAt(now))
            return new AllocationResult(false,
                $"the paper envelope {Short(envelopeId)} does not stand right now, so nothing was "
                + "allocated to paper under it. An envelope is granted by the account owner in "
                + "TradeAgent and ends on its own date; there is no command that asks for one.", null);

        if (!string.Equals(envelope.ConnectorId, connector, StringComparison.Ordinal)
            || !string.Equals(envelope.AccountId, account, StringComparison.Ordinal))
            return new AllocationResult(false,
                $"the paper envelope {Short(envelopeId)} was granted on account {envelope.AccountId} at "
                + $"{envelope.ConnectorId} and this allocation names {account} at {connector}, so "
                + "nothing was written.", null);

        if (allocation.MaxQuantity < 0m || allocation.MaxQuantity > envelope.MaxQuantity)
            return new AllocationResult(false,
                $"the paper envelope on account {envelope.AccountId} allows "
                + $"{AllocationRow.Num(envelope.MaxQuantity)} at a time and this allocation asks for "
                + $"{AllocationRow.Num(allocation.MaxQuantity)}, so nothing was written.", null);

        if (envelope.MaxNotional is { } cap && (allocation.MaxNotional is not { } asked || asked > cap))
            return new AllocationResult(false,
                $"the paper envelope on account {envelope.AccountId} allows "
                + $"{AllocationRow.Num(cap)} {envelope.Currency} at a time and this allocation asks for "
                + (allocation.MaxNotional is { } a ? AllocationRow.Num(a) : "no value limit at all")
                + ", so nothing was written.", null);

        // ONE EXPERIMENT AT A TIME, AND IT IS ABOUT *OTHER* VERSIONS. Re-recording the version that is
        // already in the envelope — a restart, a second sweep of the same policy — raises the id that
        // is already there and is not a second deployment.
        var occupants = InEnvelope(envelopeId, now)
            .Where(a => !string.Equals(a.VersionId, allocation.VersionId, StringComparison.Ordinal))
            .Select(a => a.VersionId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (occupants.Count >= envelope.MaxDeployments)
            return new AllocationResult(false,
                $"the paper envelope on account {envelope.AccountId} already carries "
                + $"{occupants.Count} version{(occupants.Count == 1 ? "" : "s")} "
                + $"({string.Join(", ", occupants.Select(Short))}) and allows "
                + $"{envelope.MaxDeployments} at a time, so version "
                + $"{Short(allocation.VersionId)} was not put on paper.", null);

        var row = allocation with { Id = allocation.ComputedId };
        Insert(row);

        var written = ById(row.Id) ?? row;
        return new AllocationResult(true,
            $"version {Short(written.VersionId)} is allocated to PAPER on account {account} at "
            + $"{connector}, up to {AllocationRow.Num(written.MaxQuantity)} at a time. No capital and "
            + "no live authority come with it.", written);
    });

    void Insert(AllocationRow row)
    {
        using var c = db.Cmd($"""
            INSERT INTO strategy_allocation({Cols})
            VALUES($id,$ver,$prom,$policy,$qty,$notional,$ccy,$from,$to,$reason,$at,
                   $scope,$conn,$mode,$acct,$envelope)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", row.Id), ("$ver", row.VersionId), ("$prom", row.PromotionId),
            ("$policy", row.PolicyVersion), ("$qty", Sql.D(row.MaxQuantity)),
            ("$notional", row.MaxNotional is { } n ? Sql.D(n) : null), ("$ccy", row.Currency),
            ("$from", Sql.T(row.EffectiveFrom)),
            ("$to", row.EffectiveTo is { } to ? Sql.T(to) : null),
            ("$reason", row.Reason), ("$at", Sql.T(row.At)),
            ("$scope", row.Scope), ("$conn", row.ConnectorId), ("$mode", row.Mode),
            ("$acct", row.AccountId), ("$envelope", row.EnvelopeId));
        c.ExecuteNonQuery();
    }

    static string Short(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>One allocation by its id, or null when this installation has never recorded it.</summary>
    public AllocationRow? ById(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM strategy_allocation WHERE id=$id", ("$id", id));
        return Read(c).FirstOrDefault();
    });

    /// <summary>
    /// Every allocation ever recorded for one version, newest effective-from first. Rows, never a
    /// standing: which of them is in force is <see cref="StandingForLive"/>'s answer.
    /// </summary>
    public IReadOnlyList<AllocationRow> For(string versionId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_allocation WHERE version_id=$v ORDER BY effective_from DESC, at DESC, id DESC",
            ("$v", versionId));
        return Read(c);
    });

    /// <summary>Every allocation this installation has recorded, newest effective-from first.</summary>
    public IReadOnlyList<AllocationRow> All(int limit = 100) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_allocation ORDER BY effective_from DESC, at DESC, id DESC LIMIT $n",
            ("$n", limit));
        return Read(c);
    });

    /// <summary>
    /// THE ALLOCATION IN FORCE FOR ONE VERSION AT <paramref name="now"/>, with where its promotion
    /// stands — or null because this version has none.
    ///
    /// <para>The newest row whose window contains <paramref name="now"/> is the answer: a later
    /// allocation supersedes an earlier one, which is how a ceiling is lowered or withdrawn in a ledger
    /// that has no update. The promotion is asked SEPARATELY and at this instant, never read off
    /// anything stored beside the allocation — a version whose evidence has been withdrawn since must
    /// stop authorising orders the moment it is withdrawn, and a copy of "it was promoted" kept on the
    /// allocation row would be exactly the stale answer <see cref="Promotions.Standing"/> exists to
    /// avoid.</para>
    /// </summary>
    public AllocationStanding? StandingForLive(string versionId, DateTimeOffset now) =>
        For(versionId).FirstOrDefault(a => !a.IsPaper && a.StandsAt(now)) is { } row
            ? new AllocationStanding(row, _promotions.Standing(row.VersionId))
            : null;

    /// <summary>
    /// THE PAPER ALLOCATION IN FORCE FOR ONE VERSION ON ONE (PLATFORM, ACCOUNT) PAIR AT
    /// <paramref name="now"/>, with where its verdict and its grant stand — or null because there is
    /// none.
    ///
    /// <para><b>All three facts are matched and none of them is a default.</b> The platform, the
    /// account and the MODE on the row — which is always <c>PAPER</c>, so a row that somehow said
    /// anything else matches nothing rather than matching whatever is running. A version observed
    /// forward on one simulation account has been observed on THAT account; the same row standing in
    /// for another account, or for a platform the owner has since switched to, would be a paper
    /// experiment claiming evidence it never collected.</para>
    ///
    /// <para><b>The envelope is asked at this instant</b>, from its own ledger, and a row whose grant
    /// has expired or been withdrawn answers null here — not "an allocation with a note". That is what
    /// makes one press on the withdrawal card stop every experiment underneath it.</para>
    /// </summary>
    public AllocationStanding? StandingForPaper(string versionId, string connectorId, string accountId,
        DateTimeOffset now)
    {
        foreach (var row in For(versionId).Where(a => a.IsPaper && a.StandsAt(now)))
        {
            if (!string.Equals(row.ConnectorId, connectorId, StringComparison.Ordinal)) continue;
            if (!string.Equals(row.AccountId, accountId, StringComparison.Ordinal)) continue;
            if (!string.Equals(row.Mode, TradingMode.PAPER.ToString(), StringComparison.Ordinal)) continue;
            if (row.EnvelopeId is not { Length: > 0 } id) continue;
            if (_envelopes.ById(id) is not { } envelope || !envelope.StandsAt(now)) continue;

            return new AllocationStanding(row, _promotions.Standing(row.VersionId), envelope);
        }

        return null;
    }

    /// <summary>
    /// EVERY PAPER ALLOCATION IN FORCE IN ONE ENVELOPE AT <paramref name="now"/>, whichever version it
    /// names. What <see cref="RecordPaper"/> counts deployments with, and what the app's policy asks
    /// before writing another.
    /// </summary>
    public IReadOnlyList<AllocationRow> InEnvelope(string envelopeId, DateTimeOffset now) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_allocation WHERE envelope_id=$e "
            + "ORDER BY effective_from DESC, at DESC, id DESC",
            ("$e", envelopeId));
        return (IReadOnlyList<AllocationRow>)[.. Read(c).Where(a => a.IsPaper && a.StandsAt(now))];
    });

    /// <summary>
    /// EVERY ALLOCATION IN FORCE AT <paramref name="now"/>, one per version, newest first. What the
    /// owner's report lists — including the ones whose promotion no longer stands, because "this
    /// version is allocated capital and TradeAgent has withdrawn the evidence under it" is the line
    /// that most needs printing and the one a filter would silently drop.
    /// </summary>
    public IReadOnlyList<AllocationStanding> Standing(DateTimeOffset now, int limit = 100) =>
        [.. All(limit)
            .Where(a => !a.IsPaper && a.StandsAt(now))
            .GroupBy(a => a.VersionId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(a => new AllocationStanding(a, _promotions.Standing(a.VersionId)))];

    /// <summary>
    /// EVERY PAPER ALLOCATION IN FORCE AT <paramref name="now"/>, one per version and envelope, newest
    /// first — listed APART from the live ones, in the owner's report and nowhere near the capital
    /// card, because "this version is being observed on a practice account" and "this version has your
    /// money behind it" are the two facts this product most needs never to blur.
    ///
    /// <para>A row whose grant no longer stands is listed and MARKED by the caller rather than
    /// filtered out, exactly as a withdrawn promotion is under <see cref="Standing"/>: the row is
    /// still on the table and the experiment is over.</para>
    /// </summary>
    public IReadOnlyList<AllocationStanding> PaperStanding(DateTimeOffset now, int limit = 100) =>
        [.. All(limit)
            .Where(a => a.IsPaper && a.StandsAt(now))
            .GroupBy(a => (a.VersionId, a.EnvelopeId))
            .Select(g => g.First())
            .Select(a => new AllocationStanding(a, _promotions.Standing(a.VersionId),
                a.EnvelopeId is { Length: > 0 } id && _envelopes.ById(id) is { } e && e.StandsAt(now)
                    ? e : null))];

    static List<AllocationRow> Read(SqliteCommand c)
    {
        var rows = new List<AllocationRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new AllocationRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Sql.Dec(r.GetValue(4)), Sql.DecN(r.GetValue(5)), r.GetString(6),
                Sql.Time(r.GetValue(7)), Sql.TimeN(r.GetValue(8)), r.GetString(9),
                Sql.Time(r.GetValue(10)))
            {
                Scope = Sql.S(r.GetValue(11)),
                ConnectorId = Sql.S(r.GetValue(12)),
                Mode = Sql.S(r.GetValue(13)),
                AccountId = Sql.S(r.GetValue(14)),
                EnvelopeId = Sql.S(r.GetValue(15))
            });
        return rows;
    }
}
