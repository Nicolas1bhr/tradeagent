using System.Globalization;
using System.Text;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Proving;

/// <summary>
/// THE PROVING GROUND'S PRNG: SplitMix64 (Steele, Lea and Flood, 2014), in-repo so a seed means the same numbers on
/// every platform and every runtime — <see cref="Random"/>'s algorithm is not a cross-version contract. Integers only:
/// nothing a market is built from passes through floating point, whose transcendental functions differ by platform.
/// </summary>
public sealed class SplitMix64(ulong seed)
{
    ulong _state = seed;

    /// <summary>The next 64 bits.</summary>
    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A whole number from <paramref name="lo"/> to <paramref name="hi"/>, both inclusive.</summary>
    public long Between(long lo, long hi)
    {
        if (hi < lo) throw new ArgumentOutOfRangeException(nameof(hi), "the range is empty");
        return lo + (long)(Next() % (ulong)(hi - lo + 1));
    }
}

/// <summary>
/// ONE PLANTED EDGE (<c>U-proving-ground-a</c> item 4): the instrument's return over <see cref="HorizonMinutes"/> from the
/// first hourly close after a signal reading was FIRST SEEN gains <see cref="BetaTicks"/> ticks per unit of that reading
/// — one fixed β, one series, one horizon. A hand-set constant, never fitted to anything.
/// </summary>
/// <param name="BetaTicks">Ticks of the planted move per unit of the signal's integer value (−50 to 50).</param>
/// <param name="HorizonMinutes">How many minutes after that close the move is spread over: 1 to 60.</param>
public sealed record SyntheticEdge(long BetaTicks, int HorizonMinutes = 60);

/// <summary>
/// WHAT ONE SYNTHETIC INSTRUMENT IS: a seed, its symbol (one of <see cref="ProvingSources.Subjects"/>), the catalogue
/// venue whose published costs judge it, the first minute, how many days, and its planted edge — none for noise.
/// </summary>
public sealed record SyntheticMarketSpec(
    ulong Seed, string Instrument, string VenueId, DateTimeOffset Start, int Days, SyntheticEdge? Edge = null);

/// <summary>What <see cref="SyntheticMarket.Write"/> recorded, and the hashes that say it is the same market every time.</summary>
/// <param name="Dataset">The dataset row, as the ledger recorded it.</param>
/// <param name="RawSha256">SHA-256 over the raw files' SHA-256s, in month order.</param>
/// <param name="Readings">The signal readings written through <see cref="TapeStore.Append"/>.</param>
/// <param name="PayloadDigest">SHA-256 over the stored readings' payload hashes, in the order the tape wrote them.</param>
/// <param name="TruthFile">The manifest of the market's truth, in the proving home only.</param>
public sealed record SyntheticMarketWritten(
    DatasetRecord Dataset, string RawSha256, int Readings, string PayloadDigest, string TruthFile);

/// <summary>
/// A PROVING GROUND'S HOME (<c>U-proving-ground-a</c>): its directory and its two stores, created marked as a proving home's.
/// </summary>
public sealed record ProvingHome(string Dir)
{
    /// <summary>The home's database, marked <see cref="HomeKind.Proving"/>.</summary>
    public string DatabaseFile => Path.Combine(Dir, "state", "tradeagent.db");

    /// <summary>The home's tape, marked <see cref="HomeKind.Proving"/>.</summary>
    public string TapeFile => Path.Combine(Dir, "state", "tape.db");

    /// <summary>Where its datasets' files live, as <c>Paths.Data</c> is a real home's.</summary>
    public string DataDir => Path.Combine(Dir, "state", "data");

    /// <summary>Where each market's truth is kept: the score's key, in this home only, never shown to a seat.</summary>
    public string TruthDir => Path.Combine(Dir, "proving-truth");

    /// <summary>
    /// CREATES A PROVING HOME AT <paramref name="dir"/>: both stores, each marked a proving home's before anything else
    /// is written. Refused for the default home, and — by the stores themselves — over a store of a real home.
    /// </summary>
    public static ProvingHome Create(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Paths.DefaultHome())), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{dir} is the default home — the real one — and a proving home is never made there.");

        var home = new ProvingHome(dir);
        using (new Database(home.DatabaseFile, HomeKind.Proving)) { }
        using (new TapeStore(home.TapeFile, kind: HomeKind.Proving)) { }
        return home;
    }
}

/// <summary>
/// A SYNTHETIC MARKET, WRITTEN THROUGH THE REAL COLLECTION PATH (<c>U-proving-ground-a</c> item 4; <c>docs/ORCHESTRATION.md</c>
/// § 2.14: "synthetic markets in the tape's formats"). Raw vendor-shaped one-minute kline files go through the real
/// <see cref="KlineNormaliser.Normalise"/> and <see cref="DatasetStore.RecordNew"/>; the signal series go through the real
/// <see cref="TapeStore.Append"/> as fetches of <see cref="ProvingSources.Synthetic"/>, each received its declared delay after
/// its source time, so the store classes them itself. The unchanged backtest reads nothing else.
///
/// <para><b>SYNTHETIC BY CONSTRUCTION.</b> Every number below is a hand-set constant or a draw from <see cref="SplitMix64"/>
/// under the spec's seed. Nothing is fitted to, estimated from or seeded by any real tape, dataset or ledger, and no
/// instrument here is a venue's: the symbols are <see cref="ProvingSources.Subjects"/>. Written only into a proving home —
/// both stores must be of kind <see cref="HomeKind.Proving"/> (SIM-001).</para>
///
/// <para><b>The market.</b> A minute's move is the sum of four draws from −10 to 10 ticks, doubled in a high-volatility
/// regime that switches with a one-in-24 chance each hour (volatility clustering), plus — for an instrument with an edge —
/// the planted drift. A signal reading per hour, stamped at ten past and drawn from −50 to 50 on a stream of its own, so
/// a noise instrument's series is independent of its returns. The planted edge spreads β times reading k's value over the
/// horizon starting at the close of hour k + 1, the first hourly close after the reading is first seen.</para>
/// </summary>
public static class SyntheticMarket
{
    /// <summary>The price step every synthetic instrument quotes in.</summary>
    public const decimal TickSize = 0.01m;

    /// <summary>The quantity step the proving ground declares for every synthetic instrument.</summary>
    public const decimal QuantityIncrement = 0.001m;

    /// <summary>The first open, in ticks: 1000.00.</summary>
    public const long StartTicks = 100_000;

    /// <summary>The half-width of each of the four draws a minute's noise is the sum of, in ticks.</summary>
    public const long StepHalfWidth = 10;

    /// <summary>The signal's integer range, both ends included; a reading's value is this over 10,000.</summary>
    public const long SignalRange = 50;

    /// <summary>The minute past each hour a signal reading is stamped at.</summary>
    public const int SignalMinute = 10;

    /// <summary>The most days one market spans.</summary>
    public const int MaxDays = 366;

    /// <summary>The word the dataset's interval and the raw files carry.</summary>
    public const string Interval = "1m";

    /// <summary>A reading's value as the tape records it: the integer over 10,000, as a plain decimal.</summary>
    public static string SignalText(long z) => (z / 10_000m).ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>
    /// THE VENUE CATALOGUE A PROVING HOME DECLARES: this build's venues, with the synthetic instruments added to
    /// <paramref name="venueId"/> at the proving ground's own declared steps — so the venue's published fee judges them,
    /// unchanged. Synced into a proving home's ledger only.
    /// </summary>
    public static VenueCatalogRead Venues(string venueId, IEnumerable<string> instruments)
    {
        var venues = VenueCatalog.BuiltIn();
        var venue = venues.FirstOrDefault(v => string.Equals(v.Id, venueId, StringComparison.Ordinal))
                    ?? throw new ArgumentException($"'{venueId}' is not a venue of this build's catalogue", nameof(venueId));
        foreach (var symbol in instruments)
            venue.Instruments.Add(new VenueInstrumentEntry
            {
                Symbol = symbol, TickSize = TickSize, QuantityIncrement = QuantityIncrement, Verified = true,
                Source = "declared by TradeAgent's proving ground for a synthetic instrument; no venue lists it"
            });
        return new VenueCatalogRead(venues, null);
    }

    /// <summary>
    /// WRITES ONE SYNTHETIC INSTRUMENT INTO A PROVING HOME: its raw files and dataset through the collection path, its
    /// signal through the tape's one writer, and its truth into <see cref="ProvingHome.TruthDir"/>.
    /// </summary>
    public static SyntheticMarketWritten Write(ProvingHome home, Database db, TapeStore tape, SyntheticMarketSpec spec)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tape);
        ArgumentNullException.ThrowIfNull(spec);
        if (db.Kind != HomeKind.Proving || tape.Kind != HomeKind.Proving)
            throw new InvalidOperationException(
                "a synthetic market is written only into a proving home, and this database or tape is a real home's (SIM-001)");
        if (!ProvingSources.Subjects.Contains(spec.Instrument, StringComparer.Ordinal))
            throw new ArgumentException(
                $"'{spec.Instrument}' is not a synthetic instrument; a proving market names one of {string.Join(", ", ProvingSources.Subjects)}",
                nameof(spec));
        if (!VenueCatalog.BuiltIn().Any(v => string.Equals(v.Id, spec.VenueId, StringComparison.Ordinal)))
            throw new ArgumentException($"'{spec.VenueId}' is not a venue of this build's catalogue", nameof(spec));
        if (spec.Days is < 1 or > MaxDays)
            throw new ArgumentException($"a proving market spans 1 to {MaxDays} days, and this one {spec.Days}", nameof(spec));
        if (spec.Edge is { HorizonMinutes: < 1 or > 60 })
            throw new ArgumentException("a planted edge's horizon is 1 to 60 minutes", nameof(spec));

        var start = spec.Start.ToUniversalTime();
        if (start.Second != 0 || start.Millisecond != 0 || start.Minute != 0)
            throw new ArgumentException("a proving market starts on the hour", nameof(spec));

        var hours = spec.Days * 24;
        var signals = Signals(spec.Seed, hours);
        var dir = Path.Combine(home.DataDir, ProvingSources.Synthetic, spec.Instrument, Interval);
        var fetchedAt = start.AddDays(spec.Days).AddHours(1);

        var files = WriteRaw(spec, start, hours, signals, Path.Combine(dir, "raw"), fetchedAt);
        var staged = Path.Combine(dir, ".staged.csv");
        var set = KlineNormaliser.Normalise([.. files.Select(f => new RawArchiveFile(f.Month, f.Path))], fetchedAt, staged);

        var record = new DatasetRecord(
            0, ProvingSources.Synthetic, spec.Instrument, Interval, "",
            files.Count, files.Count, [],
            set.Path, set.Sha256, set.Bars, set.FirstBar, set.LastBar,
            set.Gaps, set.GapRuns, set.GapRunsTruncated, set.Duplicates, set.Incomplete, set.Unreadable,
            fetchedAt, DatasetState.ACCEPTED, null, files)
        {
            VenueId = spec.VenueId,
            InstrumentSymbol = spec.Instrument,
            CoverageTargetDays = spec.Days,
            SourceCarriesVolume = true,
            MidpointBars = set.MidpointDerived
        };
        var stored = new DatasetStore(db).RecordNew(record, dir);

        var digest = new StringBuilder();
        for (var k = 0; k < hours; k++)
        {
            var at = start.AddHours(k).AddMinutes(SignalMinute);
            var received = at + ProvingSources.Delay;
            var payload = string.Create(CultureInfo.InvariantCulture,
                $$"""{"symbol":"{{spec.Instrument}}","time":{{at.ToUnixTimeMilliseconds()}},"value":"{{SignalText(signals[k])}}"}""");
            var appended = tape.Append(new TapeFetch
            {
                Source = ProvingSources.Synthetic,
                Series = ProvingSources.Signal,
                Url = ProvingSources.Origin + "/signal",
                RequestedAt = received.AddMilliseconds(-100),
                ReceivedAt = received,
                HttpStatus = 200,
                BodySha256 = Sha256Hex.Of(payload)
            }, [new TapeItem(spec.Instrument, at, payload)]);
            foreach (var row in tape.ObservationsOf(appended.FetchId)) digest.Append(row.PayloadSha256).Append('\n');
        }

        Directory.CreateDirectory(home.TruthDir);
        var truth = Path.Combine(home.TruthDir, spec.Instrument + ".json");
        File.WriteAllText(truth, Json.Write(new
        {
            synthetic_by_construction = "hand-set constants and a seed; never fitted to, estimated from or seeded by any real tape, dataset or ledger",
            seed = spec.Seed.ToString(CultureInfo.InvariantCulture),
            instrument = spec.Instrument,
            venue = spec.VenueId,
            start = start.ToString("O", CultureInfo.InvariantCulture),
            days = spec.Days,
            dataset = stored.Id,
            normalised_sha256 = stored.NormalisedSha256,
            series = new { source = ProvingSources.Synthetic, series = ProvingSources.Signal, subject = spec.Instrument, field = ProvingSources.ValueField },
            planted = spec.Edge is { } edge
                ? new
                {
                    beta_ticks_per_unit = edge.BetaTicks,
                    horizon_minutes = edge.HorizonMinutes,
                    sign = edge.BetaTicks >= 0 ? "+" : "-",
                    relation = "the return from the first hourly close after a reading is first seen gains beta ticks per unit of its value over the horizon"
                }
                : null
        }) + "\n", new UTF8Encoding(false));

        return new SyntheticMarketWritten(
            stored,
            Sha256Hex.Of(string.Concat(files.Select(f => f.ComputedSha256 + "\n"))),
            hours, Sha256Hex.Of(digest.ToString()), truth);
    }

    /// <summary>The signal's integer value for each hour, on a stream of its own.</summary>
    static long[] Signals(ulong seed, int hours)
    {
        var rng = new SplitMix64(seed ^ 0x5349474E414C0001UL);
        var z = new long[hours];
        for (var k = 0; k < hours; k++) z[k] = rng.Between(-SignalRange, SignalRange);
        return z;
    }

    /// <summary>The raw files, one per calendar month, each row a vendor-shaped closed one-minute kline.</summary>
    static List<DatasetFile> WriteRaw(SyntheticMarketSpec spec, DateTimeOffset start, int hours, long[] signals, string rawDir,
        DateTimeOffset fetchedAt)
    {
        Directory.CreateDirectory(rawDir);
        var rng = new SplitMix64(spec.Seed);
        var price = StartTicks;
        var high = false;
        var months = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);

        for (var h = 0; h < hours; h++)
        {
            if (rng.Between(1, 24) == 1) high = !high;

            // THE PLANTED MOVE OF THIS HOUR: reading h − 1's, first seen at ten past the hour before, spread over the horizon
            // from this hour's open — the first hourly close after it arrived.
            var move = spec.Edge is { } edge && h > 0 ? edge.BetaTicks * signals[h - 1] : 0L;
            var horizon = spec.Edge?.HorizonMinutes ?? 60;

            for (var m = 0; m < 60; m++)
            {
                var noise = 0L;
                for (var i = 0; i < 4; i++) noise += rng.Between(-StepHalfWidth, StepHalfWidth);
                if (high) noise *= 2;

                var drift = m < horizon ? move / horizon + (m == horizon - 1 ? move % horizon : 0) : 0L;
                var open = price;
                var close = Math.Max(1L, open + noise + drift);
                var hi = Math.Max(open, close) + rng.Between(0, 3);
                var lo = Math.Max(1L, Math.Min(open, close) - rng.Between(0, 3));
                var volume = rng.Between(1, 500) / 1000m;
                var trades = rng.Between(1, 99);
                price = close;

                var at = start.AddHours(h).AddMinutes(m);
                var ms = at.ToUnixTimeMilliseconds();
                var month = at.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                if (!months.TryGetValue(month, out var text)) months[month] = text = new StringBuilder();
                text.Append(string.Create(CultureInfo.InvariantCulture,
                    $"{ms},{P(open)},{P(hi)},{P(lo)},{P(close)},{volume:0.000},{ms + 59_999},{close * TickSize * volume:0.00000},{trades},{volume / 2:0.0000},{close * TickSize * volume / 2:0.00000},0\n"));
            }
        }

        var files = new List<DatasetFile>();
        foreach (var (month, text) in months)
        {
            var path = Path.Combine(rawDir, $"{spec.Instrument}-{Interval}-{month}.csv");
            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
            File.WriteAllBytes(path, bytes);
            var sha = Sha256Hex.OfFile(path)!;
            files.Add(new DatasetFile(month, $"{ProvingSources.Origin}/klines/{spec.Instrument}/{Interval}/{month}.csv",
                sha, sha, bytes.LongLength, fetchedAt, KlineTimeUnit.Milliseconds, path));
        }
        return files;
    }

    static string P(long ticks) => (ticks * TickSize).ToString("0.00", CultureInfo.InvariantCulture);
}
