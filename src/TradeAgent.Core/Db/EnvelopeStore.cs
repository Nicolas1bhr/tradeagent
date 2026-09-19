using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// ONE IMMUTABLE GRANT OF BOUNDED PAPER EXPERIMENTATION, ON ONE ACCOUNT OF ONE PLATFORM.
///
/// <para><b>What it is for.</b> <c>docs/PRINCIPLES.md</c> § Evidence asks that "eligibility for paper
/// observation" and "eligibility for live capital" be different meanings, and that "forward paper
/// evidence cannot be required before the very first paper run that produces it". A version the
/// referee calls <c>paper_eligible</c> therefore has to be able to reach a paper account without the
/// owner pressing anything per version — and the thing that makes that safe rather than a hole is that
/// the owner pressed ONCE, here, and said on which platform, which account, which instrument, how much
/// and until when. Everything the app allocates afterwards is bounded by this row.</para>
///
/// <para><b><see cref="Id"/> is a SHA-256 over the facts the grant IS</b> — the platform, the account,
/// the instrument, the currency, the two ceilings, how many versions may be deployed under it, and the
/// two instants it runs between. It is <see cref="AllocationRow"/>'s shape for
/// <see cref="AllocationRow"/>'s reason: an id minted from the clock would let one press write two
/// standing grants, and a later reader would have no way of saying which of them the app was
/// spending.</para>
///
/// <para><b><see cref="Reason"/> and <see cref="WithdrawnAt"/> are NOT in the id.</b> The reason is the
/// account of the decision rather than the decision. The withdrawal is a LATER decision about this
/// row, and putting it in the hash would make withdrawing a grant mint a second grant.</para>
///
/// <para><b>Nothing here is capital and nothing here is live authority.</b> An envelope authorises the
/// app to write PAPER-scoped allocations inside it; a paper allocation authorises nothing in
/// <c>LIVE_CONFIRM</c> or <c>LIVE_AUTONOMOUS</c>, categorically, and <c>TradingGateway.Allocate</c> —
/// the owner's two presses, live capital — is untouched by any of it.</para>
/// </summary>
public sealed record PaperEnvelopeRow(
    string Id,
    string ConnectorId,
    string AccountId,
    string Symbol,
    string Currency,
    decimal MaxQuantity,
    decimal? MaxNotional,
    int MaxDeployments,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    string Reason,
    DateTimeOffset? WithdrawnAt)
{
    /// <summary>
    /// THE BOUND TUPLE, HASHED: nine facts, newline separated, in this order. The order and the
    /// spelling are part of the contract, exactly as <see cref="AllocationRow.IdOf"/>'s are — an id is
    /// compared against rows written by earlier builds.
    ///
    /// <para>An absent value ceiling hashes as an EMPTY field rather than as a zero, and both decimals
    /// are spelled by <see cref="AllocationRow.Num"/>, so the two ledgers agree on what a number is.</para>
    /// </summary>
    public static string IdOf(string connectorId, string accountId, string symbol, string currency,
        decimal maxQuantity, decimal? maxNotional, int maxDeployments,
        DateTimeOffset grantedAt, DateTimeOffset expiresAt) =>
        Sha256Hex.Of(string.Join('\n',
            connectorId,
            accountId,
            symbol,
            currency,
            AllocationRow.Num(maxQuantity),
            maxNotional is { } n ? AllocationRow.Num(n) : "",
            maxDeployments.ToString(CultureInfo.InvariantCulture),
            Sql.T(grantedAt),
            Sql.T(expiresAt)));

    /// <summary>The id these nine facts hash to, whatever <see cref="Id"/> currently holds.</summary>
    public string ComputedId =>
        IdOf(ConnectorId, AccountId, Symbol, Currency, MaxQuantity, MaxNotional, MaxDeployments,
            GrantedAt, ExpiresAt);

    /// <summary>
    /// Whether this grant is in force at <paramref name="now"/>: inside its own two instants and not
    /// withdrawn. Three conditions and they are all refusals — a row that says nothing stands is the
    /// safe answer to every one of them.
    /// </summary>
    public bool StandsAt(DateTimeOffset now) =>
        WithdrawnAt is null && GrantedAt <= now && now < ExpiresAt;
}

/// <summary>
/// WHAT THE LEDGER DID WITH ONE PROPOSED GRANT, and why when it did nothing. <see cref="AllocationResult"/>'s
/// shape and for its reason: the one caller is the owner's own card, and the sentence it has to put on
/// the screen is the refusal itself.
/// </summary>
public sealed record EnvelopeResult(bool Ok, string Why, PaperEnvelopeRow? Envelope);

/// <summary>
/// THE PAPER-ENVELOPE LEDGER: WHAT BOUNDED EXPERIMENTATION THE OWNER GRANTED, WRITTEN ONCE.
///
/// <para><b>The app is the only writer and there is no <c>trade</c> verb and no pipe op behind any of
/// it</b> — the rule <see cref="Allocations"/> and <see cref="Promotions"/> keep, and for the sharper
/// reason: this is the row that decides whether a paper allocation may exist at all, and a boundary
/// its subject could widen is not a boundary. An agent that wanted a bigger envelope has nowhere to
/// ask.</para>
///
/// <para><b>There is one UPDATE and it only ever takes permission away.</b> <see cref="Withdraw"/>
/// writes <c>withdrawn_at</c>, once, on a row where it is still null — <c>WHERE withdrawn_at IS NULL</c>
/// is what makes it write-once rather than a rule the caller keeps, and a second press leaves the first
/// instant alone because WHEN the owner took permission back is a fact about their press. Nothing else
/// in the row can be edited and nothing can be deleted. That is the one respect in which this differs
/// from <see cref="Allocations"/>, which has no update at all: an allocation is a number the gateway
/// refuses orders against and is superseded by a fresh row, whereas withdrawing an envelope must stop
/// EVERY row underneath it at once, which a new row cannot say.</para>
/// </summary>
public sealed class Envelopes(Database db)
{
    const string Cols =
        "id, connector_id, account_id, symbol, currency, max_quantity, max_notional, " +
        "max_deployments, granted_at, expires_at, reason, withdrawn_at";

    /// <summary>
    /// RECORDS ONE GRANT, or leaves the row that is already there alone. Returns the row AS WRITTEN,
    /// with the id its nine facts hash to.
    ///
    /// <para>The invariants asked here are the ones that belong to the DATA: a ceiling that is not
    /// negative, at least one deployment, and a window that is really a window. Whether the account is
    /// provably simulated and whether the mode is PAPER at the press are asked by
    /// <c>TradingGateway.GrantPaperEnvelopeAsync</c>, because they are questions about a platform this
    /// assembly cannot see — and they are asked THERE rather than on a screen for the reason
    /// <c>SetHoldout</c> is in the gateway.</para>
    /// </summary>
    public EnvelopeResult Grant(PaperEnvelopeRow envelope) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.MaxQuantity <= 0m)
            return new EnvelopeResult(false,
                "a paper envelope with no quantity in it grants nothing, so nothing was written. Say "
                + "the most a strategy may hold on this account.", null);

        if (envelope.MaxDeployments < 1)
            return new EnvelopeResult(false,
                "a paper envelope must allow at least one deployment, so nothing was written.", null);

        if (envelope.ExpiresAt <= envelope.GrantedAt)
            return new EnvelopeResult(false,
                "a paper envelope must end after it begins, so nothing was written. Choose a date in "
                + "the future: an envelope that never expires is a standing authority nobody has to "
                + "look at again.", null);

        var row = envelope with { Id = envelope.ComputedId, WithdrawnAt = null };

        using var c = db.Cmd($"""
            INSERT INTO paper_envelope({Cols})
            VALUES($id,$conn,$acct,$sym,$ccy,$qty,$notional,$deploy,$from,$to,$reason,NULL)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", row.Id), ("$conn", row.ConnectorId), ("$acct", row.AccountId),
            ("$sym", row.Symbol), ("$ccy", row.Currency), ("$qty", Sql.D(row.MaxQuantity)),
            ("$notional", row.MaxNotional is { } n ? Sql.D(n) : null),
            ("$deploy", row.MaxDeployments), ("$from", Sql.T(row.GrantedAt)),
            ("$to", Sql.T(row.ExpiresAt)), ("$reason", row.Reason));
        c.ExecuteNonQuery();

        var written = ById(row.Id) ?? row;
        return new EnvelopeResult(true,
            $"TradeAgent may run paper experiments on account {written.AccountId} in {written.Symbol}, "
            + $"up to {AllocationRow.Num(written.MaxQuantity)} at a time, until "
            + $"{written.ExpiresAt:yyyy-MM-dd}. No live authority comes with it.", written);
    });

    /// <summary>
    /// TAKES ONE GRANT BACK, ONCE. Every paper allocation written under it stops authorising anything
    /// the moment this returns, because <c>Allocations.StandingForPaper</c> asks this ledger at read
    /// time and never reads a copy stored beside the allocation.
    /// </summary>
    public EnvelopeResult Withdraw(string id, DateTimeOffset at) => db.Write(_ =>
    {
        using var c = db.Cmd(
            "UPDATE paper_envelope SET withdrawn_at=$at WHERE id=$id AND withdrawn_at IS NULL",
            ("$at", Sql.T(at)), ("$id", id));
        var changed = c.ExecuteNonQuery() == 1;

        if (ById(id) is not { } row)
            return new EnvelopeResult(false,
                $"there is no paper envelope {Short(id)} on this installation, so nothing was "
                + "withdrawn.", null);

        return new EnvelopeResult(true,
            changed
                ? $"TradeAgent may start no further paper experiments on account {row.AccountId}, and "
                  + "every paper allocation written under that grant authorises nothing from now on."
                : $"that paper envelope was already withdrawn on {row.WithdrawnAt:yyyy-MM-dd HH:mm:ssK}, "
                  + "and the instant it was withdrawn at is left as it was.", row);
    });

    static string Short(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>One grant by its id, or null because this installation has never recorded it.</summary>
    public PaperEnvelopeRow? ById(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM paper_envelope WHERE id=$id", ("$id", id));
        return Read(c).FirstOrDefault();
    });

    /// <summary>Every grant this installation has recorded, newest first. What the card lists.</summary>
    public IReadOnlyList<PaperEnvelopeRow> All(int limit = 100) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM paper_envelope ORDER BY granted_at DESC, id DESC LIMIT $n",
            ("$n", limit));
        return Read(c);
    });

    /// <summary>
    /// THE GRANT IN FORCE ON ONE (PLATFORM, ACCOUNT) PAIR AT <paramref name="now"/>, or null because
    /// none is.
    ///
    /// <para>The pair is matched exactly and ordinally. An envelope granted on the simulator does not
    /// stand on a broker that happens to be connected under the same account name, and vice versa:
    /// which platform an order would actually reach is the fact the whole grant is about.</para>
    /// </summary>
    public PaperEnvelopeRow? Standing(string connectorId, string accountId, DateTimeOffset now) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM paper_envelope WHERE connector_id=$conn AND account_id=$acct "
            + "ORDER BY granted_at DESC, id DESC",
            ("$conn", connectorId), ("$acct", accountId));
        return Read(c).FirstOrDefault(e => e.StandsAt(now));
    });

    /// <summary>
    /// Whether ANY grant stands on this account right now, whatever platform it was granted on. The
    /// question the dispatch gate asks of an order that names no version at all: an account the owner
    /// has handed to the app for experiments is not one an agent may trade unattributed on.
    /// </summary>
    public bool AnyStandingOn(string accountId, DateTimeOffset now) =>
        All().Any(e => string.Equals(e.AccountId, accountId, StringComparison.Ordinal) && e.StandsAt(now));

    static List<PaperEnvelopeRow> Read(SqliteCommand c)
    {
        var rows = new List<PaperEnvelopeRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new PaperEnvelopeRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                Sql.Dec(r.GetValue(5)), Sql.DecN(r.GetValue(6)), r.GetInt32(7),
                Sql.Time(r.GetValue(8)), Sql.Time(r.GetValue(9)), r.GetString(10),
                Sql.TimeN(r.GetValue(11))));
        return rows;
    }
}
