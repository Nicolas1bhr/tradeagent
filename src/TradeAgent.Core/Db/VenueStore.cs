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
/// One instrument on one venue, AS THIS INSTALLATION SERVES IT (<see cref="VenueStore.Instruments"/>).
///
/// <para><see cref="Verified"/> false means nothing has confirmed <see cref="TickSize"/> and
/// <see cref="QuantityIncrement"/> against the venue's own instrument definition. The row is still
/// served — it is served as UNVERIFIED, and what refuses to act on it is the caller that would have to
/// turn it into a size.</para>
///
/// <para>Where a successful instrument check of seven days or less stands over the catalogue's row
/// (<c>U-venue-verify</c>), the numbers are the VENUE'S, <see cref="Check"/> is that check,
/// <see cref="Source"/> names the address and the instant it was read, and a catalogue number that
/// disagrees is kept beside it in <see cref="CatalogueTickSize"/> or
/// <see cref="CatalogueQuantityIncrement"/> rather than lost.</para>
/// </summary>
public sealed record VenueInstrumentRow(
    string VenueId,
    string Symbol,
    decimal TickSize,
    decimal QuantityIncrement,
    string Source,
    DateTimeOffset? RecordedAt,
    bool Verified)
{
    /// <summary>The check whose numbers these are, or null because the catalogue's own row is served.</summary>
    public InstrumentCheckRow? Check { get; init; }

    /// <summary>The catalogue's own tick size where it DISAGREES with the venue's; null when they agree or no check stands.</summary>
    public decimal? CatalogueTickSize { get; init; }

    /// <summary>The catalogue's own quantity increment where it DISAGREES with the venue's; null when they agree or no check stands.</summary>
    public decimal? CatalogueQuantityIncrement { get; init; }
}

/// <summary>
/// WHETHER ONE INSTRUMENT IS VERIFIED RIGHT NOW, AND WHY — the one sentence the Market data card,
/// <c>status</c> and <c>venue-list</c> all say (<c>U-venue-verify</c>).
/// </summary>
/// <param name="Says">
/// "verified against … on …" with the venue's numbers, or "not verified: …" with the reason — the last
/// check's failure, its refusal, its age, or that none has been made.
/// </param>
public sealed record InstrumentVerification(string VenueId, string Symbol, bool Verified, string Says)
{
    /// <summary>The row as served, or null because this installation serves no such instrument.</summary>
    public VenueInstrumentRow? Served { get; init; }

    /// <summary>The newest check of any outcome, or null because none has been made.</summary>
    public InstrumentCheckRow? LastCheck { get; init; }
}

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
///
/// <para><b>THE SERVED READ (<c>U-venue-verify</c>).</b> <see cref="Instruments"/>,
/// <see cref="Instrument"/> and <see cref="Catalogue"/> answer the recorded catalogue OVERLAID by the
/// latest successful instrument check of seven days or less (<see cref="CheckServedFor"/>): the venue's
/// own tick and step, with the address and the instant they were read, and a catalogue number that
/// disagrees kept beside them. A pair the app checked that the catalogue holds no row for is served from
/// its check, on a venue the catalogue does hold. It is the ONE read every consumer of an increment goes
/// through — research's default increment, the referee's cost model, the paper connector and the forward
/// runner — so a check that succeeds, or lapses, reaches each of them at its next read and nothing has to
/// restart. The rows themselves are never edited: the overlay is computed at read time, on the clock this
/// store is given.</para>
///
/// <para><b>A <c>"verified": true</c> in <c>venues.json</c> stays what it was</b> — the catalogue's own
/// row, served as verified and said as "verified by the venue catalogue's own row, not by a TradeAgent
/// check". The file is one the CLI agent can write until containment lands, so that flag is ADVISORY,
/// and <c>docs/CONTRACTS.md</c> says so.</para>
/// </summary>
public sealed class VenueStore(Database db, Func<DateTimeOffset>? now = null)
{
    /// <summary>
    /// HOW LONG A SUCCESSFUL CHECK IS SERVED FOR: seven days, measured from the instant its answer arrived
    /// to the clock this store reads. Past it the instrument is unverified again until a check succeeds —
    /// a venue that changed its step since is not served the old one indefinitely. The app re-checks every
    /// six hours while it runs, so a working connection never reaches the edge.
    /// </summary>
    public static readonly TimeSpan CheckServedFor = TimeSpan.FromDays(7);

    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    readonly InstrumentCheckStore _checks = new(db);

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

    /// <summary>
    /// EVERY INSTRUMENT AS SERVED, or only one venue's — the recorded catalogue overlaid by the latest
    /// successful check of seven days or less. See the type summary. Ordered so a listing is stable.
    /// </summary>
    public IReadOnlyList<VenueInstrumentRow> Instruments(string? venueId = null) => db.Read(_ =>
    {
        var now = _now();
        var venues = Venues().ToDictionary(v => v.Id, StringComparer.Ordinal);

        // THE LATEST SUCCESSFUL CHECK OF EACH INSTRUMENT, IF IT IS STILL FRESH, AND ONLY ON A VENUE THE
        // CATALOGUE HOLDS. An older success is not served at all: past seven days the catalogue's own row
        // stands again, unverified unless the catalogue itself says otherwise.
        var fresh = _checks.LatestVerified()
            .Where(c => venueId is null || string.Equals(c.VenueId, venueId, StringComparison.Ordinal))
            .Where(c => venues.ContainsKey(c.VenueId))
            .Where(c => now - c.ReceivedAt <= CheckServedFor)
            .ToDictionary(c => (c.VenueId, c.Symbol));

        var served = new List<VenueInstrumentRow>();
        foreach (var row in CatalogueRows(venueId))
            served.Add(fresh.Remove((row.VenueId, row.Symbol), out var check)
                ? Overlay(row, check, venues[row.VenueId])
                : row);

        // A PAIR THE APP CHECKED THAT THE CATALOGUE HOLDS NO ROW FOR is served from its check: the venue's
        // own definition is exactly the looking a catalogue row would have needed.
        foreach (var check in fresh.Values)
            served.Add(FromCheck(check, venues[check.VenueId]));

        return (IReadOnlyList<VenueInstrumentRow>)
        [
            .. served.OrderBy(r => r.VenueId, StringComparer.Ordinal).ThenBy(r => r.Symbol, StringComparer.Ordinal)
        ];
    });

    /// <summary>
    /// ONE INSTRUMENT ON ONE VENUE AS SERVED, or null because this installation serves no such row.
    ///
    /// <para>Null is the answer an unknown symbol gets, and it is the answer <c>FakeBroker.TickSize</c>
    /// already gives for the same reason: substituting a grid for a symbol nobody recorded would make
    /// "TradeAgent cannot tell you what that is" indistinguishable from a measurement. A row that
    /// EXISTS and is unverified comes back as it is — the caller reads <c>Verified</c> and decides.</para>
    /// </summary>
    public VenueInstrumentRow? Instrument(string venueId, string symbol) =>
        Instruments(venueId).FirstOrDefault(r => string.Equals(r.Symbol, symbol, StringComparison.Ordinal));

    /// <summary>
    /// THE SERVED CATALOGUE IN THE SHAPE <see cref="VenueCatalog.Read"/> ANSWERS — what the paper connector
    /// is handed through its option and reads at every use, so a check that succeeds or lapses reaches a
    /// running connector without a restart. An unreadable catalogue is no venues and the reason, as the file
    /// read is.
    /// </summary>
    public VenueCatalogRead Catalogue()
    {
        var instruments = Instruments();
        var venues = Venues().Select(v => new VenueEntry
        {
            Id = v.Id,
            DisplayName = v.DisplayName,
            CalendarKind = v.CalendarKind,
            Source = v.Source,
            RecordedAt = v.RecordedAt,
            Verified = v.Verified,
            Instruments =
            [
                .. instruments.Where(i => string.Equals(i.VenueId, v.Id, StringComparison.Ordinal))
                    .Select(i => new VenueInstrumentEntry
                    {
                        Symbol = i.Symbol,
                        TickSize = i.TickSize,
                        QuantityIncrement = i.QuantityIncrement,
                        Source = i.Source,
                        RecordedAt = i.RecordedAt,
                        Verified = i.Verified
                    })
            ]
        }).ToList();
        return new VenueCatalogRead(venues, Unreadable);
    }

    /// <summary>
    /// WHETHER ONE INSTRUMENT IS VERIFIED RIGHT NOW, AND WHY, in the one sentence every surface says. See
    /// <see cref="InstrumentVerification"/>.
    /// </summary>
    public InstrumentVerification Verification(string venueId, string symbol)
    {
        var venue = Venues().FirstOrDefault(v => string.Equals(v.Id, venueId, StringComparison.Ordinal));
        var name = venue?.DisplayName ?? venueId;
        var served = venue is null ? null : Instrument(venueId, symbol);
        var last = _checks.Latest(venueId, symbol);

        string says;
        if (Unreadable is { } why)
            says = $"not verified: TradeAgent's venue catalogue could not be read, so no instrument is served — {why}";
        else if (venue is null)
            says = $"not verified: TradeAgent's venue catalogue holds no venue '{venueId}'";
        else if (served is { Verified: true, Check: { } check })
            says = $"verified against {name}'s published instrument definition on {When(check.ReceivedAt)}: "
                   + Numbers(check)
                   + Beside(served)
                   + (last is { IsVerified: false } later && later.Id > check.Id
                       ? $"; a later check, at {When(later.ReceivedAt)}, {(later.Outcome == InstrumentCheckOutcome.RefusedOrigin ? "was refused" : "failed")}: {later.Note}"
                       : "");
        else if (served is { Verified: true })
            says = $"verified by the venue catalogue's own row, not by a TradeAgent check: {served.Source}";
        else
            says = "not verified: " + Reason(name, last, venueId, symbol);

        return new InstrumentVerification(venueId, symbol, served?.Verified ?? false, says)
        {
            Served = served,
            LastCheck = last
        };
    }

    /// <summary>Why an instrument is not verified: the last check's failure, its refusal, its age, or that none has been made.</summary>
    string Reason(string name, InstrumentCheckRow? last, string venueId, string symbol)
    {
        if (last is null)
            return $"TradeAgent has not checked it against {name}'s published instrument definition yet";

        if (last.IsVerified)
            return $"the last check, on {When(last.ReceivedAt)}, is more than seven days old";

        var stale = _checks.LatestVerified().FirstOrDefault(c =>
            string.Equals(c.VenueId, venueId, StringComparison.Ordinal) && string.Equals(c.Symbol, symbol, StringComparison.Ordinal));
        return $"the last check, at {When(last.ReceivedAt)}, "
               + (last.Outcome == InstrumentCheckOutcome.RefusedOrigin ? "was refused" : "failed")
               + $": {last.Note}"
               + (stale is null ? "" : $"; the last successful one, on {When(stale.ReceivedAt)}, is more than seven days old");
    }

    /// <summary>The catalogue's row with a fresh check's numbers over it, the disagreeing catalogue numbers kept beside.</summary>
    static VenueInstrumentRow Overlay(VenueInstrumentRow catalogue, InstrumentCheckRow check, VenueRow venue) =>
        new(catalogue.VenueId, catalogue.Symbol, check.TickSize!.Value, check.QuantityIncrement!.Value,
            CheckSource(check, venue), check.ReceivedAt, true)
        {
            Check = check,
            CatalogueTickSize = catalogue.TickSize != check.TickSize ? catalogue.TickSize : null,
            CatalogueQuantityIncrement = catalogue.QuantityIncrement != check.QuantityIncrement ? catalogue.QuantityIncrement : null
        };

    /// <summary>A checked pair the catalogue holds no row for, served from its check alone.</summary>
    static VenueInstrumentRow FromCheck(InstrumentCheckRow check, VenueRow venue) =>
        new(check.VenueId, check.Symbol, check.TickSize!.Value, check.QuantityIncrement!.Value,
            CheckSource(check, venue), check.ReceivedAt, true)
        {
            Check = check
        };

    /// <summary>Where a served number came from, in words: the venue, the address and the instant, and which check.</summary>
    static string CheckSource(InstrumentCheckRow check, VenueRow venue) =>
        $"{venue.DisplayName}'s own published instrument definition, read by TradeAgent from {check.Url} at "
        + $"{check.ReceivedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} (check {check.Id})";

    static string When(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    static string Numbers(InstrumentCheckRow check) =>
        $"tick {Plain(check.TickSize)}, step {Plain(check.QuantityIncrement)}"
        + (check.MinQuantity is { } q ? $", minimum quantity {Plain(q)}" : "")
        + (check.MinNotional is { } n ? $", minimum notional {Plain(n)} (recorded, not applied)" : "");

    static string Beside(VenueInstrumentRow served) =>
        (served.CatalogueTickSize, served.CatalogueQuantityIncrement) switch
        {
            (null, null) => "",
            (var t, var s) => "; TradeAgent's catalogue says "
                              + string.Join(" and ", new[]
                              {
                                  t is { } tick ? $"tick {Plain(tick)}" : null,
                                  s is { } step ? $"step {Plain(step)}" : null
                              }.Where(x => x is not null))
                              + ", and the venue's number is the one used"
        };

    static string Plain(decimal? value) =>
        value is { } v ? v.ToString("0.############################", CultureInfo.InvariantCulture) : "none";

    /// <summary>The recorded catalogue rows themselves, before any check is overlaid. Only the served read uses them.</summary>
    List<VenueInstrumentRow> CatalogueRows(string? venueId)
    {
        using var c = venueId is null
            ? db.Cmd($"SELECT {InstrumentCols} FROM venue_instrument ORDER BY venue_id, symbol")
            : db.Cmd($"SELECT {InstrumentCols} FROM venue_instrument WHERE venue_id=$v ORDER BY symbol",
                ("$v", venueId));
        return ReadInstruments(c);
    }

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
