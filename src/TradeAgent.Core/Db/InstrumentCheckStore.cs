using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE INSTRUMENT CHECKS — THE ONLY WRITER OF <c>instrument_check</c>, and it only ever appends
/// (<c>U-venue-verify</c>).
///
/// <para><b>A row per attempt, succeeded, failed or refused.</b> <see cref="Append"/> is the whole write
/// surface: there is no update, no delete and no "latest check" cell that a later attempt overwrites,
/// so what TradeAgent read from a venue on a given day stays what it read. A check that left no row
/// would be indistinguishable from one that never ran, which is the reason <c>forward_fetch</c> gives
/// for the same shape.</para>
///
/// <para><b>Written by the app and by nothing else.</b> The verifier in the app's own process is the
/// one caller; no pipe op and no <c>trade</c> verb appends here, edits a row or asks for a check — an
/// agent that could record its own instrument definition could choose the step its sizes are rounded
/// to and have the record agree with it. The rows are MEASUREMENT; nothing an agent says about an
/// instrument is stored beside them.</para>
///
/// <para><b>The origin is read off the URL here</b>, through <see cref="UrlOrigin"/>, and never taken
/// from the caller: the address a check went to and the address it is judged by cannot be two claims.
/// The same one rule decides whether a pasted key may be released and whether a tape row is live
/// (<c>docs/EDGE-FACTORY.md</c> § 6.10).</para>
///
/// <para>Like every table in <c>tradeagent.db</c>, the file itself can be written by the CLI agent
/// running unconfined as the owner's user until containment lands; "written only by the app" holds
/// for the app's own paths, and <c>docs/CONTRACTS.md</c> says so.</para>
/// </summary>
public sealed class InstrumentCheckStore(Database db)
{
    const string Cols =
        "id, venue_id, symbol, url, origin, requested_at, received_at, http_status, body_sha256, "
        + "tick_size, quantity_increment, min_quantity, min_notional, outcome, note";

    /// <summary>
    /// WRITES ONE ATTEMPT, AS IT HAPPENED, and answers the row as written — its id and the origin read
    /// off its URL included.
    ///
    /// <para>Refused here, before anything is written: an attempt that names no venue, symbol or URL; an
    /// outcome that is not one of the three words; a VERIFIED attempt without a tick size and a quantity
    /// increment above zero, which would serve a step of nothing; and an attempt of any other outcome
    /// that carries numbers, which would be half a definition from an answer that did not verify.</para>
    /// </summary>
    public InstrumentCheckRow Append(InstrumentCheckAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (string.IsNullOrWhiteSpace(attempt.VenueId)) throw new ArgumentException("a check names its venue", nameof(attempt));
        if (string.IsNullOrWhiteSpace(attempt.Symbol)) throw new ArgumentException("a check names its instrument", nameof(attempt));
        if (string.IsNullOrWhiteSpace(attempt.Url)) throw new ArgumentException("a check names the URL it asked", nameof(attempt));
        if (!InstrumentCheckOutcome.IsKnown(attempt.Outcome))
            throw new ArgumentException($"'{attempt.Outcome}' is not an outcome an instrument check can have", nameof(attempt));

        var numbers = attempt.TickSize is not null || attempt.QuantityIncrement is not null
                      || attempt.MinQuantity is not null || attempt.MinNotional is not null;

        if (attempt.Outcome == InstrumentCheckOutcome.Verified)
        {
            if (attempt.TickSize is not > 0m || attempt.QuantityIncrement is not > 0m)
                throw new ArgumentException(
                    "a verified check carries the venue's tick size and quantity increment, both above zero",
                    nameof(attempt));
        }
        else if (numbers)
            throw new ArgumentException("only a verified check carries the venue's numbers", nameof(attempt));

        // THE ORIGIN IS READ OFF THE URL, NOT TAKEN FROM THE CALLER. See the type summary.
        var origin = UrlOrigin.Of(attempt.Url);

        return db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO instrument_check(venue_id, symbol, url, origin, requested_at, received_at,
                                             http_status, body_sha256, tick_size, quantity_increment,
                                             min_quantity, min_notional, outcome, note)
                VALUES($venue,$sym,$url,$origin,$req,$recv,$status,$sha,$tick,$step,$minq,$minn,$outcome,$note);
                SELECT last_insert_rowid();
                """,
                ("$venue", attempt.VenueId), ("$sym", attempt.Symbol), ("$url", attempt.Url),
                ("$origin", origin), ("$req", Sql.T(attempt.RequestedAt)), ("$recv", Sql.T(attempt.ReceivedAt)),
                ("$status", attempt.HttpStatus), ("$sha", attempt.BodySha256),
                ("$tick", D(attempt.TickSize)), ("$step", D(attempt.QuantityIncrement)),
                ("$minq", D(attempt.MinQuantity)), ("$minn", D(attempt.MinNotional)),
                ("$outcome", attempt.Outcome), ("$note", attempt.Note));

            var id = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
            return ById(id) ?? throw new InvalidOperationException($"instrument check {id} could not be read back");
        });
    }

    /// <summary>One row by its id, or null because there is no such row.</summary>
    public InstrumentCheckRow? ById(long id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM instrument_check WHERE id=$id", ("$id", id));
        return Rows(c).FirstOrDefault();
    });

    /// <summary>
    /// THE NEWEST ATTEMPT FOR ONE INSTRUMENT, OF ANY OUTCOME, or null because there has been none. What a
    /// status line reads to say why an instrument is not verified: the last thing that happened.
    /// </summary>
    public InstrumentCheckRow? Latest(string venueId, string symbol) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM instrument_check WHERE venue_id=$v AND symbol=$s ORDER BY id DESC LIMIT 1",
            ("$v", venueId), ("$s", symbol));
        return Rows(c).FirstOrDefault();
    });

    /// <summary>
    /// THE NEWEST VERIFIED ATTEMPT FOR EACH INSTRUMENT THAT HAS ONE, by venue and symbol — what the served
    /// read overlays onto the catalogue once it has judged its age (<c>VenueStore</c>). Newest by id, which
    /// is the order the attempts were written in: a clock that stepped back cannot make an older reading
    /// stand in for a newer one.
    /// </summary>
    public IReadOnlyList<InstrumentCheckRow> LatestVerified() => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {Cols} FROM instrument_check c
             WHERE c.outcome = $ok
               AND c.id = (SELECT MAX(d.id) FROM instrument_check d
                            WHERE d.venue_id = c.venue_id AND d.symbol = c.symbol AND d.outcome = $ok)
             ORDER BY c.venue_id, c.symbol
            """,
            ("$ok", InstrumentCheckOutcome.Verified));
        return Rows(c);
    });

    static string? D(decimal? value) => value is { } d ? Sql.D(d) : null;

    static decimal? Dec(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : decimal.Parse(r.GetString(i), CultureInfo.InvariantCulture);

    static List<InstrumentCheckRow> Rows(SqliteCommand c)
    {
        var rows = new List<InstrumentCheckRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new InstrumentCheckRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                Sql.Time(r.GetString(5)), Sql.Time(r.GetString(6)),
                r.IsDBNull(7) ? null : r.GetInt32(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                Dec(r, 9), Dec(r, 10), Dec(r, 11), Dec(r, 12),
                r.GetString(13),
                r.IsDBNull(14) ? null : r.GetString(14)));
        return rows;
    }
}
