using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>One venue as this installation has it recorded. See <see cref="VenueStore"/>.</summary>
public sealed record VenueRow(
    string Id,
    string DisplayName,
    string CalendarKind,
    string Source,
    DateTimeOffset? RecordedAt,
    bool Verified);

/// <summary>
/// One instrument on one venue, as this installation has it recorded.
///
/// <para><see cref="Verified"/> false means nothing in this build has confirmed
/// <see cref="TickSize"/> and <see cref="QuantityIncrement"/> against the venue's own instrument
/// definition. The row is still served — it is served as UNVERIFIED, and what refuses to act on it is
/// the caller that would have to turn it into a size.</para>
/// </summary>
public sealed record VenueInstrumentRow(
    string VenueId,
    string Symbol,
    decimal TickSize,
    decimal QuantityIncrement,
    string Source,
    DateTimeOffset? RecordedAt,
    bool Verified);

/// <summary>
/// THE VENUE CATALOGUE AS THIS INSTALLATION HOLDS IT — app-owned data, like <c>dataset</c>.
///
/// <para><b>The rows are the catalogue's, and the catalogue is <see cref="VenueCatalog"/>.</b> This
/// class is where they are RECORDED, so that everything that needs them — the pipe's <c>venue-list</c>,
/// the runner's increment, the owner's own window — reads one set of rows rather than three readings
/// of a file. <see cref="Sync"/> replaces the whole table from the catalogue; there is no other
/// writer, no partial update and no merge, because a file that is the truth and a table that is half
/// of an older truth are two catalogues.</para>
///
/// <para><b>Nothing on the agent pipe writes here.</b> <c>venue-list</c> is a read and is not in
/// <c>Ops.Mutating</c>; there is no <c>trade</c> verb that adds, edits or removes a venue. That is the
/// same split <c>DatasetStore</c> and <c>MaterialStore</c> make: an agent that could write its own
/// instrument increment could size a position however it liked and have the record agree with it.</para>
///
/// <para><b>An unreadable <c>venues.json</c> empties the table</b> and records why. It is the most
/// restrictive reading and the one <c>RuntimeManifest.Read</c> already takes: with no rows, every
/// increment a request did not declare is refused in words, which is the outcome an owner whose
/// correction could not be read should get.</para>
/// </summary>
public sealed class VenueStore(Database db)
{
    /// <summary>Where <see cref="Sync"/> leaves the reason the catalogue could not be read, or "".</summary>
    public const string UnreadableKey = "venue_catalogue_unreadable";

    const string VenueCols = "id, display_name, calendar_kind, source, recorded_at, verified";

    const string InstrumentCols =
        "venue_id, symbol, tick_size, quantity_increment, source, recorded_at, verified";

    /// <summary>
    /// REPLACES THE RECORDED CATALOGUE WITH <paramref name="read"/>, in one transaction. Returns how
    /// many instrument rows there now are.
    ///
    /// <para>Delete-then-insert rather than an upsert: the catalogue is a whole statement of what this
    /// installation knows, and a venue the owner REMOVED from <c>venues.json</c> has to disappear from
    /// the table too. Instruments go first because the foreign key is on and pointing at the venues.</para>
    /// </summary>
    public int Sync(VenueCatalogRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return db.Write(_ =>
        {
            using (var wipeInstruments = db.Cmd("DELETE FROM venue_instrument")) wipeInstruments.ExecuteNonQuery();
            using (var wipeVenues = db.Cmd("DELETE FROM venue")) wipeVenues.ExecuteNonQuery();

            db.SetKv(UnreadableKey, read.Unreadable ?? "");

            var instruments = 0;
            foreach (var venue in read.Venues)
            {
                using (var v = db.Cmd($"""
                    INSERT INTO venue({VenueCols}) VALUES($id,$name,$cal,$src,$at,$ok)
                    ON CONFLICT(id) DO NOTHING
                    """,
                    ("$id", venue.Id), ("$name", venue.DisplayName), ("$cal", venue.CalendarKind),
                    ("$src", venue.Source), ("$at", venue.RecordedAt is { } t ? Sql.T(t) : null),
                    ("$ok", venue.Verified ? 1 : 0)))
                {
                    if (v.ExecuteNonQuery() == 0) continue;
                }

                foreach (var instrument in venue.Instruments)
                {
                    using var i = db.Cmd($"""
                        INSERT INTO venue_instrument({InstrumentCols})
                        VALUES($venue,$sym,$tick,$step,$src,$at,$ok)
                        ON CONFLICT(venue_id, symbol) DO NOTHING
                        """,
                        ("$venue", venue.Id), ("$sym", instrument.Symbol),
                        ("$tick", Sql.D(instrument.TickSize)),
                        ("$step", Sql.D(instrument.QuantityIncrement)),
                        ("$src", instrument.Source),
                        ("$at", instrument.RecordedAt is { } t ? Sql.T(t) : null),
                        ("$ok", instrument.Verified ? 1 : 0));
                    instruments += i.ExecuteNonQuery();
                }
            }

            return instruments;
        });
    }

    /// <summary>Why there is no catalogue, or null because there is one. See <see cref="Sync"/>.</summary>
    public string? Unreadable => db.GetKv(UnreadableKey) is { Length: > 0 } why ? why : null;

    /// <summary>Every venue this installation has recorded, by id.</summary>
    public IReadOnlyList<VenueRow> Venues() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {VenueCols} FROM venue ORDER BY id");
        var rows = new List<VenueRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new VenueRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Sql.TimeN(r.IsDBNull(4) ? null : r.GetString(4)), r.GetInt32(5) != 0));
        return rows;
    });

    /// <summary>Every instrument recorded, or only one venue's. Ordered so a listing is stable.</summary>
    public IReadOnlyList<VenueInstrumentRow> Instruments(string? venueId = null) => db.Read(_ =>
    {
        using var c = venueId is null
            ? db.Cmd($"SELECT {InstrumentCols} FROM venue_instrument ORDER BY venue_id, symbol")
            : db.Cmd($"SELECT {InstrumentCols} FROM venue_instrument WHERE venue_id=$v ORDER BY symbol",
                ("$v", venueId));
        return ReadInstruments(c);
    });

    /// <summary>
    /// ONE INSTRUMENT ON ONE VENUE, or null because this installation has no such row.
    ///
    /// <para>Null is the answer an unknown symbol gets, and it is the answer <c>FakeBroker.TickSize</c>
    /// already gives for the same reason: substituting a grid for a symbol nobody recorded would make
    /// "TradeAgent cannot tell you what that is" indistinguishable from a measurement. A row that
    /// EXISTS and is unverified comes back as it is — the caller reads <c>Verified</c> and decides.</para>
    /// </summary>
    public VenueInstrumentRow? Instrument(string venueId, string symbol) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {InstrumentCols} FROM venue_instrument WHERE venue_id=$v AND symbol=$s",
            ("$v", venueId), ("$s", symbol));
        return ReadInstruments(c).FirstOrDefault();
    });

    static List<VenueInstrumentRow> ReadInstruments(SqliteCommand c)
    {
        var rows = new List<VenueInstrumentRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new VenueInstrumentRow(
                r.GetString(0), r.GetString(1),
                decimal.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                decimal.Parse(r.GetString(3), CultureInfo.InvariantCulture),
                r.GetString(4), Sql.TimeN(r.IsDBNull(5) ? null : r.GetString(5)), r.GetInt32(6) != 0));
        return rows;
    }
}
