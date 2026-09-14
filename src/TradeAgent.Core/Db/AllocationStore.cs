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
/// id, and <see cref="Allocations.StandingFor"/> answers with the newest one that stands. That is the
/// choice `docs/CONTRACTS.md` states, and it is the only shape in which a capital decision is a record
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

    /// <summary>The id these seven facts hash to, whatever <see cref="Id"/> currently holds.</summary>
    public string ComputedId =>
        IdOf(VersionId, PromotionId, PolicyVersion, MaxQuantity, MaxNotional, Currency, EffectiveFrom);

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
/// </summary>
public sealed record AllocationStanding(AllocationRow Allocation, PromotionStanding Promotion)
{
    /// <summary>Whether this allocation may authorise an order right now.</summary>
    public bool Authorises => Promotion.IsPromoted;
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
/// method at all: this class exposes one write (<see cref="Record"/>), which is
/// <c>ON CONFLICT DO NOTHING</c>, and the reads below. The same rule <see cref="Promotions"/> has, for
/// a sharper version of the same reason: an allocation is the number the gateway refuses orders
/// against, and a limit its subject could edit is not a limit. There is no <c>trade</c> verb and no
/// pipe op behind any of this, so an agent that wanted more capital has nowhere to ask.</para>
///
/// <para><b>Withdrawal is a NEW ROW, never an edit.</b> See <see cref="AllocationRow"/>: the owner
/// records a fresh allocation from a later instant — a smaller ceiling, or a zero — and
/// <see cref="StandingFor"/> answers with the newest row that stands. Nothing is ever overwritten, so
/// the sequence of what was allowed when is on the table where a reader can see it.</para>
/// </summary>
public sealed class Allocations(Database db)
{
    readonly Promotions _promotions = new(db);

    const string Cols =
        "id, version_id, promotion_id, policy_version, max_quantity, max_notional, currency, " +
        "effective_from, effective_to, reason, at";

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

        var standing = _promotions.Standing(allocation.VersionId);
        if (!standing.IsPromoted)
            return new AllocationResult(false,
                $"version {Short(allocation.VersionId)} does not stand promoted, so no capital was "
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

        var row = allocation with { Id = allocation.ComputedId };

        using var c = db.Cmd($"""
            INSERT INTO strategy_allocation({Cols})
            VALUES($id,$ver,$prom,$policy,$qty,$notional,$ccy,$from,$to,$reason,$at)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", row.Id), ("$ver", row.VersionId), ("$prom", row.PromotionId),
            ("$policy", row.PolicyVersion), ("$qty", Sql.D(row.MaxQuantity)),
            ("$notional", row.MaxNotional is { } n ? Sql.D(n) : null), ("$ccy", row.Currency),
            ("$from", Sql.T(row.EffectiveFrom)),
            ("$to", row.EffectiveTo is { } to ? Sql.T(to) : null),
            ("$reason", row.Reason), ("$at", Sql.T(row.At)));
        c.ExecuteNonQuery();

        var written = ById(row.Id) ?? row;
        return new AllocationResult(true,
            $"version {Short(written.VersionId)} may trade up to {AllocationRow.Num(written.MaxQuantity)} "
            + $"from {written.EffectiveFrom:u}.", written);
    });

    static string Short(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>One allocation by its id, or null when this installation has never recorded it.</summary>
    public AllocationRow? ById(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM strategy_allocation WHERE id=$id", ("$id", id));
        return Read(c).FirstOrDefault();
    });

    /// <summary>
    /// Every allocation ever recorded for one version, newest effective-from first. Rows, never a
    /// standing: which of them is in force is <see cref="StandingFor"/>'s answer.
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
    public AllocationStanding? StandingFor(string versionId, DateTimeOffset now) =>
        For(versionId).FirstOrDefault(a => a.StandsAt(now)) is { } row
            ? new AllocationStanding(row, _promotions.Standing(row.VersionId))
            : null;

    /// <summary>
    /// EVERY ALLOCATION IN FORCE AT <paramref name="now"/>, one per version, newest first. What the
    /// owner's report lists — including the ones whose promotion no longer stands, because "this
    /// version is allocated capital and TradeAgent has withdrawn the evidence under it" is the line
    /// that most needs printing and the one a filter would silently drop.
    /// </summary>
    public IReadOnlyList<AllocationStanding> Standing(DateTimeOffset now, int limit = 100) =>
        [.. All(limit)
            .Where(a => a.StandsAt(now))
            .GroupBy(a => a.VersionId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(a => new AllocationStanding(a, _promotions.Standing(a.VersionId)))];

    static List<AllocationRow> Read(SqliteCommand c)
    {
        var rows = new List<AllocationRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new AllocationRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Sql.Dec(r.GetValue(4)), Sql.DecN(r.GetValue(5)), r.GetString(6),
                Sql.Time(r.GetValue(7)), Sql.TimeN(r.GetValue(8)), r.GetString(9),
                Sql.Time(r.GetValue(10))));
        return rows;
    }
}
