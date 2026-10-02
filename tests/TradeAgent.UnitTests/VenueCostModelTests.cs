using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-cost-model item 1 — THE VENUE COST MODEL ITSELF: four numbers, each from the one place that can
/// vouch for it, in a text that is pinned, hashed and read back.
///
/// <para>The fee is a published standard taker rate from a table in CODE with its source and its day;
/// the slippage is TradeAgent's stated assumption; the step is a VERIFIED catalogue row or a refusal;
/// the capital is the owner's setting. What these tests hold is that none of the four is ever guessed,
/// and that a pinned text is read back as exactly the model it was — or as nothing.</para>
/// </summary>
public class VenueCostModelTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset At = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A dataset row as the collector would write it. Never stored: the model reads two of its fields.</summary>
    static DatasetRecord Bars(string? venue = VenueCatalog.BinanceSpot, string? symbol = "BTCUSDT") =>
        new(7, BinanceArchive.Source, symbol ?? "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            "/not/read/here.csv", "aa11", 1000, At, At.AddDays(30), 0, [], false, 0, 0, 0, At,
            DatasetState.ACCEPTED, null, [])
        {
            VenueId = venue,
            InstrumentSymbol = symbol
        };

    /// <summary>A catalogue with one instrument in it, as this test declares it.</summary>
    static VenueStore Catalogue(Database db, string venue = VenueCatalog.BinanceSpot, string symbol = "BTCUSDT",
        decimal increment = 0.00001m, bool verified = true)
    {
        var store = new VenueStore(db);
        store.Sync(new VenueCatalogRead(
        [
            new VenueEntry
            {
                Id = venue, DisplayName = venue, CalendarKind = CalendarKind.Continuous,
                Source = "declared by this test", Verified = verified,
                Instruments =
                [
                    new VenueInstrumentEntry
                    {
                        Symbol = symbol, TickSize = 0.01m, QuantityIncrement = increment,
                        Source = "the account owner read the venue's own instrument definition",
                        Verified = verified
                    }
                ]
            }
        ], null));
        return store;
    }

    /// <summary>
    /// THE FEE TABLE IS CODE, AND EVERY ROW SAYS WHERE IT WAS READ AND WHEN.
    ///
    /// <para>Binance spot's Regular tier is 0.100% taker (<c>docs/research/2026-10-02/R04</c> § 7, read
    /// from the venue's own schedule on 2026-10-02). A rate with no source is a number nobody can check,
    /// and a venue with no row gets no fee at all rather than a neighbour's.</para>
    /// </summary>
    [Fact]
    public void The_fee_table_states_each_published_rate_with_its_source_and_the_day_it_was_read()
    {
        var binance = VenueCostModel.FeeOf(VenueCatalog.BinanceSpot);
        Assert.NotNull(binance);
        Assert.Equal(0.001m, binance.TakerRate);
        Assert.Contains("https://www.binance.com/en/fee/schedule", binance.Source, StringComparison.Ordinal);
        Assert.Contains("0.100% taker", binance.Source, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2026, 10, 2), binance.ReadOn);

        foreach (var fee in VenueCostModel.PublishedFees)
        {
            Assert.InRange(fee.TakerRate, 0.00001m, ExecutionModel.MaxFeeRate);
            Assert.False(string.IsNullOrWhiteSpace(fee.Source), $"{fee.VenueId} carries no source");
            Assert.False(string.IsNullOrWhiteSpace(fee.Venue));
            Assert.True(fee.ReadOn >= new DateOnly(2026, 10, 2), $"{fee.VenueId} has no plausible read date");
        }
        Assert.Equal(VenueCostModel.PublishedFees.Count,
            VenueCostModel.PublishedFees.Select(f => f.VenueId).Distinct(StringComparer.Ordinal).Count());

        // NOTHING READ, NOTHING CHARGED: Revolut X has no row, and neither has a venue nobody named.
        Assert.Null(VenueCostModel.FeeOf(VenueCatalog.RevolutX));
        Assert.Null(VenueCostModel.FeeOf(null));
    }

    /// <summary>
    /// A VENUE MODEL IS THE PUBLISHED FEE, THE ASSUMED SLIPPAGE, THE CONFIRMED STEP AND THE JUDGE CAPITAL —
    /// and its text says where each of the four came from.
    /// </summary>
    [Fact]
    public void A_venue_model_is_the_published_fee_the_assumed_slippage_the_confirmed_step_and_the_judge_capital()
    {
        using var db = TestEnv.NewDb();
        var resolved = VenueCostModel.For(Bars(), Catalogue(db), 25_000m);

        Assert.True(resolved.Ok, resolved.Why);
        var model = resolved.Model!;
        log.WriteLine(model.Canonical);

        Assert.Equal("fees=0.001;slippage=0.0002;increment=0.00001;capital=25000", model.Model.Canonical);
        Assert.Equal(VenueCostModel.VenueJudge, model.Judge);
        Assert.False(model.IsFrictionless);
        Assert.Equal(VenueCatalog.BinanceSpot, model.VenueId);
        Assert.Equal("BTCUSDT", model.Instrument);

        // THE TEXT CARRIES THE PROVENANCE OF EVERY NUMBER, and its first line is the version.
        Assert.StartsWith(VenueCostModel.Header + "\n", model.Canonical, StringComparison.Ordinal);
        Assert.Equal(1, VenueCostModel.Version);
        Assert.Contains("https://www.binance.com/en/fee/schedule", model.Canonical, StringComparison.Ordinal);
        Assert.Contains("read 2026-10-02", model.Canonical, StringComparison.Ordinal);
        Assert.Contains("TradeAgent's ASSUMPTION", model.Canonical, StringComparison.Ordinal);
        Assert.Contains("the account owner read the venue's own instrument definition", model.Canonical,
            StringComparison.Ordinal);
        Assert.Contains("judge capital", model.Canonical, StringComparison.Ordinal);
        Assert.Equal(Sha256Hex.Of(model.Canonical), model.Sha256);

        // AND THE SAME FACTS ARE THE SAME TEXT: two campaigns over one instrument pin one hash.
        Assert.Equal(model.Sha256, VenueCostModel.For(Bars(), new VenueStore(db), 25_000m).Model!.Sha256);
    }

    /// <summary>
    /// A STEP NOBODY CONFIRMED IS REFUSED RATHER THAN GUESSED — unverified, absent, or not even named.
    ///
    /// <para>This is the state BTCUSDT ships in. A step is what a size is rounded down to, so a judge that
    /// guessed it would judge a strategy nobody submitted; the words say what is not known and point at
    /// no file to edit.</para>
    /// </summary>
    [Fact]
    public void An_unconfirmed_or_unrecorded_step_is_refused_rather_than_guessed()
    {
        using var db = TestEnv.NewDb();
        var shipped = new VenueStore(db);
        shipped.Sync(VenueCatalog.Read(Path.Combine(TestEnv.Home, $"venues-absent-{Guid.NewGuid():n}.json")));

        var unverified = VenueCostModel.For(Bars(), shipped, 10_000m);
        log.WriteLine(unverified.Why);
        Assert.False(unverified.Ok);
        Assert.Null(unverified.Model);
        Assert.Equal("BTCUSDT's quantity step is not confirmed against the venue's own definition; a judge that "
            + "guessed it would judge a different strategy.", unverified.Why);
        Assert.DoesNotContain("venues.json", unverified.Why, StringComparison.Ordinal);

        var absent = VenueCostModel.For(Bars(symbol: "ETHUSDT"), shipped, 10_000m);
        Assert.False(absent.Ok);
        Assert.StartsWith("ETHUSDT's quantity step is not confirmed", absent.Why, StringComparison.Ordinal);

        var unnamed = VenueCostModel.For(Bars(symbol: null), shipped, 10_000m);
        Assert.False(unnamed.Ok);
        Assert.Contains("names no instrument", unnamed.Why, StringComparison.Ordinal);
        Assert.Contains("a judge that guessed it would judge a different strategy", unnamed.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A VENUE WHOSE FEE THIS BUILD HAS NEVER READ IS REFUSED, EVEN WITH A CONFIRMED STEP. Zero would be a
    /// fee nobody measured reading as a measurement, and a neighbour's rate would be somebody else's.
    /// </summary>
    [Fact]
    public void A_venue_with_no_published_fee_is_refused_rather_than_charged_a_guess()
    {
        using var db = TestEnv.NewDb();
        var resolved = VenueCostModel.For(Bars(VenueCatalog.RevolutX, "BTCUSD"),
            Catalogue(db, VenueCatalog.RevolutX, "BTCUSD", 0.0001m), 10_000m);

        log.WriteLine(resolved.Why);
        Assert.False(resolved.Ok);
        Assert.Equal("revolut-x's standard fee is not one TradeAgent has read from the venue's own published "
            + "schedule; a judge that guessed it would judge a different strategy.", resolved.Why);
    }

    /// <summary>
    /// THE TWO FRICTIONLESS JUDGES ARE <c>ExecutionModel.Frictionless</c> EXACTLY, AND EACH SAYS WHY IN
    /// ITS OWN TEXT. Exactly, so a holdout run under either has the run id it always had.
    /// </summary>
    [Fact]
    public void Bars_with_no_venue_get_the_frictionless_judge_and_each_frictionless_judge_names_itself()
    {
        using var db = TestEnv.NewDb();
        var resolved = VenueCostModel.For(Bars(venue: null, symbol: null), new VenueStore(db), 50_000m);

        Assert.True(resolved.Ok, resolved.Why);
        Assert.Same(VenueCostModel.NoVenueRecorded, resolved.Model);

        foreach (var (judge, words) in new[]
                 {
                     (VenueCostModel.NoVenueRecorded, "no venue recorded"),
                     (VenueCostModel.LegacyFrictionless, "legacy frictionless judge")
                 })
        {
            Assert.Equal(ExecutionModel.Frictionless.Canonical, judge.Model.Canonical);
            Assert.True(judge.IsFrictionless);
            Assert.Equal(words, judge.Judge);
            Assert.Contains($"judge: {words}", judge.Canonical, StringComparison.Ordinal);
            Assert.Null(judge.VenueId);
            Assert.Null(judge.Instrument);
            Assert.Contains(words, judge.Describe(), StringComparison.Ordinal);
        }

        Assert.NotEqual(VenueCostModel.NoVenueRecorded.Sha256, VenueCostModel.LegacyFrictionless.Sha256);
    }

    /// <summary>
    /// A PINNED TEXT READS BACK AS EXACTLY THE MODEL IT WAS — OR AS NOTHING. Null is the referee's cue to
    /// refuse, never to substitute: a text that is not its own hash's, a version this build does not
    /// implement, or numbers in a second spelling are not "close enough".
    /// </summary>
    [Fact]
    public void A_pinned_text_reads_back_as_the_same_model_and_anything_else_reads_as_nothing()
    {
        using var db = TestEnv.NewDb();
        var model = VenueCostModel.For(Bars(), Catalogue(db), 10_000m).Model!;

        var read = VenueCostModel.Read(model.Canonical, model.Sha256);
        Assert.NotNull(read);
        Assert.Equal(model.Model.Canonical, read.Model.Canonical);
        Assert.Equal(model.Judge, read.Judge);
        Assert.Equal(model.VenueId, read.VenueId);
        Assert.Equal(model.Instrument, read.Instrument);
        Assert.Equal(model.Canonical, read.Canonical);
        Assert.NotNull(VenueCostModel.Read(model.Canonical, model.Sha256.ToUpperInvariant()));

        foreach (var frictionless in new[] { VenueCostModel.NoVenueRecorded, VenueCostModel.LegacyFrictionless })
            Assert.Equal(frictionless, VenueCostModel.Read(frictionless.Canonical, frictionless.Sha256));

        // A TEXT EDITED UNDER ITS OLD HASH.
        var cheaper = model.Canonical.Replace("fees=0.001;", "fees=0;", StringComparison.Ordinal);
        Assert.Null(VenueCostModel.Read(cheaper, model.Sha256));

        // REHASHED, BUT NOT A MODEL ANY BUILD WOULD DECLARE — or a number in a second spelling.
        foreach (var edited in new[]
                 {
                     model.Canonical.Replace("fees=0.001;", "fees=0.5;", StringComparison.Ordinal),
                     model.Canonical.Replace("fees=0.001;", "fees=0.0010;", StringComparison.Ordinal),
                     model.Canonical.Replace(VenueCostModel.Header, "TradeAgent venue cost model v2", StringComparison.Ordinal),
                     model.Canonical.Replace(VenueCostModel.VenueJudge, "whatever the submitter liked", StringComparison.Ordinal),
                     model.Canonical.Replace("venue: binance-spot", "venue: none", StringComparison.Ordinal),
                     model.Canonical + "\nextra: line"
                 })
            Assert.Null(VenueCostModel.Read(edited, Sha256Hex.Of(edited)));

        Assert.Null(VenueCostModel.Read(null, null));
        Assert.Null(VenueCostModel.Read("", ""));
        Assert.Null(VenueCostModel.Read(model.Canonical, null));
    }

    /// <summary>
    /// THE JUDGE CAPITAL IS THE OWNER'S SETTING, TEN THOUSAND OUT OF THE BOX, AND ZERO OR LESS IS NOT A
    /// CAPITAL OF NOTHING.
    /// </summary>
    [Fact]
    public void A_judge_capital_of_zero_or_less_reads_as_the_shipped_ten_thousand()
    {
        Assert.Equal(10_000m, new TradeAgentSettings().JudgeCapital);
        Assert.Equal(VenueCostModel.DefaultCapital, new TradeAgentSettings().JudgeCapital);

        using var db = TestEnv.NewDb();
        var venues = Catalogue(db);
        foreach (var setting in new[] { 0m, -5m })
            Assert.Equal(10_000m, VenueCostModel.For(Bars(), venues, setting).Model!.Model.InitialCapital);

        Assert.Equal(1_250.5m, VenueCostModel.For(Bars(), venues, 1_250.5m).Model!.Model.InitialCapital);
    }
}
