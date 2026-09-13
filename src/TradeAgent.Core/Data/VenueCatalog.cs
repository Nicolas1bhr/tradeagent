namespace TradeAgent.Core.Data;

/// <summary>
/// WHETHER A VENUE EVER CLOSES. <c>docs/COUNCIL.md</c>:95 — "calendars are per connector and per
/// instrument; a continuous venue inherits no other venue's weekend".
///
/// <para>Two words, and the column exists so a venue can DECLARE which it is rather than have a
/// reader infer it from the shape of the data. A gap in a continuous venue's bars is missing data; a
/// gap in a sessioned venue's bars may be the venue being shut, and this build holds NO session table
/// for any venue — so <see cref="Sessioned"/> means "this one closes and TradeAgent cannot tell you
/// when", which is a refusal to guess and not a calendar.</para>
/// </summary>
public static class CalendarKind
{
    /// <summary>Open every minute of every day. No weekend, no session, no holiday.</summary>
    public const string Continuous = "continuous";

    /// <summary>It closes, and this build does not hold the table that says when. See the type.</summary>
    public const string Sessioned = "sessioned";

    public static bool IsKnown(string? kind) => kind is Continuous or Sessioned;
}

/// <summary>
/// ONE INSTRUMENT AS THE CATALOGUE RECORDS IT — the grid it prices on, the step a size may move in,
/// and WHO SAID SO.
///
/// <para><see cref="Verified"/> carries the same meaning it carries on a runtime manifest
/// (<c>docs/DECISIONS.md</c>:73-78): false means nothing in this build has confirmed these numbers
/// against the venue's own instrument definition. An unverified row is served, and it is served AS
/// unverified — never quietly used as though somebody had checked it.</para>
///
/// <para>Settable properties rather than a positional record because these rows arrive from
/// <c>venues.json</c> as well as from this build, and <c>System.Text.Json</c> reads what the file
/// names and leaves the rest at its default — which for <see cref="Verified"/> is FALSE, the answer a
/// row that says nothing must get.</para>
/// </summary>
public sealed class VenueInstrumentEntry
{
    public string Symbol { get; set; } = "";

    /// <summary>The price grid, e.g. 0.01 on a spot pair quoted in cents.</summary>
    public decimal TickSize { get; set; }

    /// <summary>The step a quantity moves in. A size is rounded DOWN to a multiple of it.</summary>
    public decimal QuantityIncrement { get; set; }

    /// <summary>Who says so, in words. Never empty on a row this build ships.</summary>
    public string Source { get; set; } = "";

    /// <summary>When that was recorded, or null because the row did not say. Never a zero date.</summary>
    public DateTimeOffset? RecordedAt { get; set; }

    /// <summary>Confirmed against the venue's own instrument definition. False unless the row says so.</summary>
    public bool Verified { get; set; }
}

/// <summary>One venue and the instruments this installation has recorded for it. See <see cref="VenueCatalog"/>.</summary>
public sealed class VenueEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CalendarKind { get; set; } = Data.CalendarKind.Continuous;
    public string Source { get; set; } = "";
    public DateTimeOffset? RecordedAt { get; set; }
    public bool Verified { get; set; }
    public List<VenueInstrumentEntry> Instruments { get; set; } = [];
}

/// <summary>
/// The catalogue as it stands, or the reason there is none. Never both — see
/// <see cref="VenueCatalog.Read"/> for why an unreadable file yields NOTHING rather than the
/// built-ins.
/// </summary>
public sealed record VenueCatalogRead(IReadOnlyList<VenueEntry> Venues, string? Unreadable);

/// <summary>
/// THE VENUES AND INSTRUMENTS THIS INSTALLATION KNOWS OF, AS DATA.
///
/// <para><b>Why this is not a table somebody fills in.</b> <c>docs/COUNCIL.md</c>:145,152 wants bars
/// "with instrument increments" and a size "rounded down to the increment"; :239 wants venue
/// capabilities recorded before unattended real money. Before this file the word "venue" appeared in
/// this build only in comments, so the increment a backtest rounded to was whatever number arrived on
/// the request and the default was 1 — a whole Bitcoin on a pair whose real step is 0.00001.</para>
///
/// <para><b>Shipped, and overridable in one line.</b> Exactly the <c>runtimes.json</c> / <c>atas.json</c>
/// pattern <c>docs/DECISIONS.md</c>:73-78 settled: a venue that changes a step size should cost an edit
/// to <c>venues.json</c> in the app's own folder, not a rebuild. A file entry REPLACES the built-in
/// venue with the same id and is otherwise appended, which is <c>RuntimeManifest.Read</c>'s rule.</para>
///
/// <para><b>An unreadable override is the most restrictive override</b>, and that is the same
/// judgement <c>RuntimeManifest.Read</c> makes: a file the owner wrote to correct a step size that
/// silently reverted to the shipped number would be the worst outcome of all, because the number is
/// what a size is computed from. So the catalogue is the file's whole content or nothing at all — an
/// ABSENT file is a different fact and keeps meaning "the built-ins".</para>
///
/// <para><b>What is NOT here.</b> No fee and no minimum notional. <c>docs/COUNCIL.md</c> is silent on a
/// fee table and :155 keeps fees DECLARED per backtest, so a fee read out of a catalogue would put a
/// number nobody measured into a result and let it read as a measurement. No session table either —
/// see <see cref="CalendarKind"/>.</para>
/// </summary>
public static class VenueCatalog
{
    /// <summary>The overridable file, beside <c>runtimes.json</c> in the app's own folder.</summary>
    public static string OverridePath => Path.Combine(Paths.Home, "venues.json");

    /// <summary>The spot venue the app's own collector downloads months from.</summary>
    public const string BinanceSpot = "binance-spot";

    /// <summary>TradeAgent's built-in practice simulator, which IS the venue its instruments trade on.</summary>
    public const string Simulator = "sim-futures";

    /// <summary>
    /// WHEN THE ROWS BELOW WERE WRITTEN INTO THIS BUILD, as a constant and not a clock reading.
    ///
    /// A <c>DateTimeOffset.UtcNow</c> here would stamp every installation with the moment it happened
    /// to start, which says nothing about when anybody looked the number up. This is the day the unit
    /// that wrote them landed.
    /// </summary>
    public static readonly DateTimeOffset ShippedAt = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

    const string NotConfirmed =
        "shipped with TradeAgent; NOT confirmed against the venue's own instrument definition";

    const string TheSimulatorItself =
        "TradeAgent's own practice simulator, which is the venue these instruments trade on";

    /// <summary>
    /// THE CATALOGUE THIS BUILD SHIPS. Two venues, five instruments, and one of the two is honest
    /// about not having been checked.
    ///
    /// <para><b>Binance spot is UNVERIFIED and stays that way until something measures it.</b> The
    /// numbers below are what this product has always assumed BTCUSDT's grid to be; nothing in this
    /// build has read Binance's own instrument definition, and this unit reaches no network. So the row
    /// is served as unverified, a run that would have taken its increment is REFUSED instead of
    /// guessing, and the owner who confirms it writes one line into <c>venues.json</c>. Only BTCUSDT is
    /// here: the app collects whatever pair the owner types, and a row for a pair nobody looked at
    /// would be a fabricated fact rather than a missing one.</para>
    ///
    /// <para><b>The simulator is VERIFIED, and that is not a double standard.</b> Its instrument
    /// definition is not a claim about somebody else's venue — it is this application's own, and the
    /// four rows are the same four <c>FakeBroker.Instruments</c> serves, which
    /// <c>VenueCatalogTests</c> holds them to. There is no third party who could disagree.</para>
    ///
    /// <para>Both venues are <see cref="CalendarKind.Continuous"/>. Binance spot never closes; the
    /// simulator never closes either, and calling it sessioned because the real CME contracts these
    /// symbols name are would be describing a venue this installation does not trade on.</para>
    /// </summary>
    public static List<VenueEntry> BuiltIn() =>
    [
        new VenueEntry
        {
            Id = BinanceSpot,
            DisplayName = "Binance spot",
            CalendarKind = Data.CalendarKind.Continuous,
            Source = NotConfirmed,
            RecordedAt = ShippedAt,
            Verified = false,
            Instruments =
            [
                new VenueInstrumentEntry
                {
                    Symbol = "BTCUSDT",
                    TickSize = 0.01m,
                    QuantityIncrement = 0.00001m,
                    Source = NotConfirmed,
                    RecordedAt = ShippedAt,
                    Verified = false
                }
            ]
        },
        new VenueEntry
        {
            Id = Simulator,
            DisplayName = "TradeAgent's practice simulator",
            CalendarKind = Data.CalendarKind.Continuous,
            Source = TheSimulatorItself,
            RecordedAt = ShippedAt,
            Verified = true,
            Instruments =
            [
                Sim("ES", 0.25m), Sim("NQ", 0.25m), Sim("MES", 0.25m), Sim("YM", 1m)
            ]
        }
    ];

    /// <summary>One simulated future: whole contracts, on the simulator's own price grid.</summary>
    static VenueInstrumentEntry Sim(string symbol, decimal tick) => new()
    {
        Symbol = symbol,
        TickSize = tick,
        QuantityIncrement = 1m,
        Source = TheSimulatorItself,
        RecordedAt = ShippedAt,
        Verified = true
    };

    /// <summary>
    /// The catalogue, or the reason there is none. See the type summary: an unreadable
    /// <c>venues.json</c> yields NO venues at all, and an absent one yields the built-ins.
    /// </summary>
    /// <param name="overridePath">
    /// The file to read instead of <see cref="OverridePath"/>. For tests, which share one
    /// <c>TRADEAGENT_HOME</c> across a whole assembly and must not be able to empty another class's
    /// catalogue by writing a deliberately broken file into it.
    /// </param>
    public static VenueCatalogRead Read(string? overridePath = null)
    {
        var file = VendorFile.Read<List<VenueEntry>>(overridePath ?? OverridePath);
        if (file.Unreadable is { } why)
            return new([],
                $"TradeAgent found a venues.json in its own folder and could not read it: {why}. No "
                + "venue or instrument is being served from it, and the ones this build ships are NOT "
                + "standing in for it — a step size the file was written to correct must never quietly "
                + "revert to the shipped number. Fix the file or remove it.");

        var venues = BuiltIn();
        foreach (var o in file.Value ?? [])
        {
            var i = venues.FindIndex(v => string.Equals(v.Id, o.Id, StringComparison.Ordinal));
            if (i >= 0) venues[i] = o; else venues.Add(o);
        }
        return new(venues, null);
    }
}
