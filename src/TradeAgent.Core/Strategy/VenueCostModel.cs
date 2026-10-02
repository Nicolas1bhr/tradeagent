using System.Globalization;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// ONE VENUE'S PUBLISHED STANDARD TAKER FEE, AS THIS BUILD READ IT: the rate, the sentence it was read
/// from, and the day.
///
/// <para><b>The TAKER rate, because every fill the judge models takes liquidity.</b> A signal fills at
/// the next bar's open — a market order — and protection fires through the book; nothing in a backtest
/// rests on it and earns the maker rate.</para>
/// </summary>
/// <param name="VenueId">The venue catalogue's id for the venue, e.g. <c>binance-spot</c>.</param>
/// <param name="Venue">The venue as a person names it.</param>
/// <param name="TakerRate">A FRACTION of each fill's notional: <c>0.001</c> is 0.100%.</param>
/// <param name="Source">Where the rate was read and what the page said, in words.</param>
/// <param name="ReadOn">The day it was read from that page. A rate with no date is a rumour.</param>
public sealed record PublishedFee(string VenueId, string Venue, decimal TakerRate, string Source, DateOnly ReadOn);

/// <summary>
/// THE COST MODEL A REFEREE JUDGES UNDER — ONE, APP-OWNED, AND PINNED BY EACH CAMPAIGN WHEN IT OPENS.
///
/// <para><b>Why it exists.</b> <c>docs/EDGE-FACTORY.md</c> § 4.5 asks for "one app-owned venue cost
/// model at every stage, pinned at campaign open". Before it, <c>trade verdict</c> passed no model and
/// the referee fell back to <see cref="ExecutionModel.Frictionless"/> — no fee, no slippage, a step of a
/// whole unit — so on a pair priced near 85,000 every size rounded down to nothing and every verdict
/// was <c>no-trade</c>, while still spending one of the campaign's three judgements.</para>
///
/// <para><b>Four numbers, each from the one place that can vouch for it.</b></para>
/// <list type="bullet">
/// <item><b>The fee</b> is the venue's published standard TAKER rate, from <see cref="PublishedFees"/>:
/// a table in CODE, each row with its source sentence and the day it was read. Not the venue catalogue —
/// <c>docs/COUNCIL.md</c> keeps fees out of it and so does this — and not an override file an agent could
/// write: the friction a verdict is scored under is a standard, and a standard the judged party can
/// edit is not one.</item>
/// <item><b>Slippage</b> is <see cref="SlippageRate"/>, two basis points a fill, and it is labelled for
/// what it is: TradeAgent's ASSUMPTION, never a measurement of any venue's book.</item>
/// <item><b>The quantity step</b> is the instrument's VERIFIED catalogue row and nothing else. An
/// unverified row, or none, is refused — the rule <c>Backtests.Increment</c> already follows — because a
/// step is what a size is rounded down to, and a judge that guessed it would judge a strategy nobody
/// submitted.</item>
/// <item><b>The capital</b> is the account owner's <c>JudgeCapital</c> setting at the moment the model
/// is pinned.</item>
/// </list>
///
/// <para><b>Pinned, so it cannot move under evidence.</b> The campaign copies <see cref="Canonical"/>
/// and its <see cref="Sha256"/> onto its own row in the transaction that opens it; a renewal carries
/// them; a later edit to this table, to the catalogue or to the setting moves no campaign already open.
/// What a campaign was judged under is the text on its row, read back by <see cref="Read"/>.</para>
///
/// <para><b>Two frictionless judges remain, and each says why.</b> Bars that record no venue have no fee
/// and no step to read, so they keep <see cref="ExecutionModel.Frictionless"/> labelled
/// <see cref="NoVenueJudge"/>; a campaign that charged a verdict before this model existed keeps the
/// judge that verdict was taken under, labelled <see cref="LegacyJudge"/>. The label is IN the pinned
/// text, so a reader of the row never has to guess which of the three it is.</para>
///
/// <para><b>What v1 does not model:</b> a minimum notional, a fee tier other than the standard one, a
/// rebate, a funding rate, and price impact beyond the assumed slippage. Each is a later version with a
/// later header, so a campaign pinned to v1 is never re-read as anything else.</para>
/// </summary>
public sealed record VenueCostModel
{
    VenueCostModel(string judge, string? venueId, string? instrument, ExecutionModel model, string canonical)
    {
        Judge = judge;
        VenueId = venueId;
        Instrument = instrument;
        Model = model;
        Canonical = canonical;
    }

    /// <summary>The version of the model's rules and of its text. A different version is a different header.</summary>
    public const int Version = 1;

    /// <summary>The first line of every v1 text. <see cref="Read"/> refuses any other.</summary>
    public const string Header = "TradeAgent venue cost model v1";

    /// <summary>The slippage every fill is charged, as a fraction of its price: two basis points.</summary>
    public const decimal SlippageRate = 0.0002m;

    /// <summary>What <see cref="SlippageRate"/> is, in the words every pinned text carries beside it.</summary>
    public const string SlippageBasis =
        "TradeAgent's ASSUMPTION of two basis points of adverse price on every fill — not a measurement "
        + "of any venue's book";

    /// <summary>The judge's capital when the owner has set none: the ten thousand the frictionless judge always used.</summary>
    public const decimal DefaultCapital = 10_000m;

    /// <summary>The judge a venue model names: the venue's own published fee.</summary>
    public const string VenueJudge = "the venue's published standard taker fee";

    /// <summary>The judge of bars that record no venue. Frictionless, and it says why.</summary>
    public const string NoVenueJudge = "no venue recorded";

    /// <summary>The judge of a campaign that charged a verdict before this model existed. Frictionless, as that verdict was.</summary>
    public const string LegacyJudge = "legacy frictionless judge";

    /// <summary>
    /// THE PUBLISHED STANDARD TAKER RATES THIS BUILD HAS READ, one row per venue, each with its source
    /// and its day. A venue with no row here is REFUSED a venue model rather than given a guessed fee —
    /// the reading <c>FakeBroker.TickSize</c> takes for an unknown symbol. Re-read every row from the
    /// venue's own page before a release; <c>docs/RESEARCH-REQUIRED.md</c> says so.
    /// </summary>
    public static IReadOnlyList<PublishedFee> PublishedFees { get; } =
    [
        new PublishedFee(
            VenueCatalog.BinanceSpot, "Binance spot", 0.001m,
            "Binance's own fee schedule, https://www.binance.com/en/fee/schedule: spot trading at the "
            + "Regular tier pays 0.100% maker and 0.100% taker (0.075% when the fee is paid in BNB, which "
            + "TradeAgent does not assume)",
            new DateOnly(2026, 10, 2))
    ];

    /// <summary>The published rate for one venue, or null because this build has read none for it.</summary>
    public static PublishedFee? FeeOf(string? venueId) =>
        venueId is null ? null : PublishedFees.FirstOrDefault(f => string.Equals(f.VenueId, venueId, StringComparison.Ordinal));

    /// <summary>Which of the three judges this is: <see cref="VenueJudge"/>, <see cref="NoVenueJudge"/> or <see cref="LegacyJudge"/>.</summary>
    public string Judge { get; }

    /// <summary>The venue the fee and the step were read for, or null for a frictionless judge.</summary>
    public string? VenueId { get; }

    /// <summary>The instrument the step was read for, or null for a frictionless judge.</summary>
    public string? Instrument { get; }

    /// <summary>The four numbers a holdout run is made under. Its own canonical is what a promotion row records.</summary>
    public ExecutionModel Model { get; }

    /// <summary>
    /// THE MODEL AS IT IS PINNED AND HASHED: one line per field in a fixed order, the four numbers in
    /// <see cref="ExecutionModel.Canonical"/>'s own spelling, and where each of them came from in words.
    /// A model read back from a row carries the text that row holds, byte for byte.
    /// </summary>
    public string Canonical { get; }

    /// <summary>The SHA-256 of <see cref="Canonical"/>, which is what a campaign records beside it.</summary>
    public string Sha256 => Sha256Hex.Of(Canonical);

    /// <summary>Whether this is one of the two frictionless judges rather than a venue's.</summary>
    public bool IsFrictionless => !string.Equals(Judge, VenueJudge, StringComparison.Ordinal);

    /// <summary>
    /// THE JUDGE OF BARS THAT RECORD NO VENUE: <see cref="ExecutionModel.Frictionless"/>, exactly, and
    /// labelled. Exactly, so every holdout run over such bars has the run id it always had.
    /// </summary>
    public static VenueCostModel NoVenueRecorded { get; } = Frictionless(NoVenueJudge,
        "0 — these bars record no venue, so there is no published fee to charge and TradeAgent invents none",
        "0 — frictionless, for the same reason",
        "1 — whole units, visibly arbitrary rather than guessed",
        "10000 — the frictionless default");

    /// <summary>
    /// THE JUDGE OF A CAMPAIGN THAT CHARGED A VERDICT BEFORE THIS MODEL EXISTED: frictionless, because
    /// every such verdict was judged frictionless, and two verdicts of one campaign under two judges
    /// would be answers to two different questions.
    /// </summary>
    public static VenueCostModel LegacyFrictionless { get; } = Frictionless(LegacyJudge,
        "0 — this campaign charged a verdict before TradeAgent pinned a cost model, and every such "
        + "verdict was judged frictionless",
        "0 — as those verdicts were",
        "1 — as those verdicts were",
        "10000 — as those verdicts were");

    /// <summary>
    /// THE MODEL FOR ONE DATASET'S BARS, OR WHY THERE CAN BE NONE.
    ///
    /// <para>The venue and the instrument are the DATASET's, as the collector recorded them beside the
    /// bytes — never a program's <c>instrument</c> line, which is something the judged party types. A
    /// dataset with no venue gets <see cref="NoVenueRecorded"/>. One whose step is unconfirmed, or whose
    /// venue has no published fee here, is refused in words that say what is not known and a judge that
    /// guessed it would judge a different strategy — never in words about a feature, and never with an
    /// instruction to edit a file.</para>
    /// </summary>
    /// <param name="judgeCapital">The owner's <c>JudgeCapital</c>; zero or less reads as <see cref="DefaultCapital"/>.</param>
    public static VenueCostModelResolved For(DatasetRecord dataset, VenueStore venues, decimal judgeCapital)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(venues);

        if (dataset.VenueId is not { Length: > 0 } venue) return VenueCostModelResolved.Yes(NoVenueRecorded);

        if (dataset.InstrumentSymbol is not { Length: > 0 } symbol)
            return VenueCostModelResolved.No(
                $"dataset {dataset.Id} records its bars as {venue}'s and names no instrument, so there is no "
                + "quantity step to look up; a judge that guessed it would judge a different strategy.");

        // THE STEP, FROM A VERIFIED ROW OR NOT AT ALL. A missing row and an unverified one are the same
        // fact for this purpose — nothing has confirmed the number — and they get the same sentence.
        if (venues.Instrument(venue, symbol) is not { Verified: true } row)
            return VenueCostModelResolved.No(
                $"{symbol}'s quantity step is not confirmed against the venue's own definition; a judge that "
                + "guessed it would judge a different strategy.");

        if (FeeOf(venue) is not { } fee)
            return VenueCostModelResolved.No(
                $"{venue}'s standard fee is not one TradeAgent has read from the venue's own published "
                + "schedule; a judge that guessed it would judge a different strategy.");

        var capital = CapitalOf(judgeCapital);
        var declared = ExecutionModel.Declare(fee.TakerRate, SlippageRate, row.QuantityIncrement, capital);
        if (declared.Model is not { } model)
            return VenueCostModelResolved.No(
                $"the judge's model for {symbol} on {venue} cannot be declared: {declared.Why}.");

        return VenueCostModelResolved.Yes(new VenueCostModel(VenueJudge, venue, symbol, model, Text(
            VenueJudge, model, venue, symbol,
            FeeLine(fee),
            SlippageLine,
            $"{N(row.QuantityIncrement)} — {venue}/{symbol} in the venue catalogue, verified: {Line(row.Source)}",
            $"{N(capital)} — the account owner's judge capital when this model was pinned")));
    }

    /// <summary>
    /// THE FEE LINE OF A PINNED TEXT, WITHOUT ITS <c>fee: </c> KEY — the rate, whose published rate it
    /// is, the day it was read and the page's own sentence. Spelled once, because a
    /// <see cref="VenueFriction"/> carries the same line and a paper fill and a verdict on one venue
    /// must be charged in the same words.
    /// </summary>
    internal static string FeeLine(PublishedFee fee) =>
        $"{N(fee.TakerRate)} per fill — {fee.Venue}'s published standard taker rate, read "
        + $"{fee.ReadOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: {Line(fee.Source)}";

    /// <summary>The slippage line of a pinned text, without its key. See <see cref="FeeLine"/>.</summary>
    internal static string SlippageLine => $"{N(SlippageRate)} per fill — {SlippageBasis}";

    /// <summary>The capital a model is pinned with. Zero or less is not a capital of nothing: an account that can buy nothing judges nothing.</summary>
    public static decimal CapitalOf(decimal judgeCapital) => judgeCapital > 0m ? judgeCapital : DefaultCapital;

    /// <summary>
    /// A PINNED TEXT READ BACK, or null because it cannot be: no text, a SHA-256 that is not the text's
    /// own, a header this build does not implement, a judge it does not know, or four numbers that are
    /// not a model <see cref="ExecutionModel.Declare"/> accepts in exactly their canonical spelling.
    ///
    /// <para>Null is the referee's cue to REFUSE, never to substitute: a campaign whose pinned model
    /// cannot be read is not judged by some other one.</para>
    /// </summary>
    public static VenueCostModel? Read(string? canonical, string? sha256)
    {
        if (string.IsNullOrEmpty(canonical) || string.IsNullOrEmpty(sha256)) return null;
        if (!string.Equals(Sha256Hex.Of(canonical), sha256, StringComparison.OrdinalIgnoreCase)) return null;

        var lines = canonical.Split('\n');
        if (lines.Length != 9 || !string.Equals(lines[0], Header, StringComparison.Ordinal)) return null;

        if (Field(lines[1], "judge") is not { } judge
            || judge is not (VenueJudge or NoVenueJudge or LegacyJudge)) return null;
        if (Field(lines[2], "model") is not { } spelled || Parse(spelled) is not { } model) return null;
        if (Field(lines[3], "venue") is not { } venue || Field(lines[4], "instrument") is not { } instrument)
            return null;

        var venueId = venue == None ? null : venue;
        var symbol = instrument == None ? null : instrument;

        // A VENUE JUDGE NAMES ITS VENUE AND INSTRUMENT, AND A FRICTIONLESS ONE IS FRICTIONLESS. Anything
        // else is a text no build of v1 writes.
        var coherent = judge == VenueJudge
            ? venueId is not null && symbol is not null
            : venueId is null && symbol is null && model.Canonical == ExecutionModel.Frictionless.Canonical;

        return coherent ? new VenueCostModel(judge, venueId, symbol, model, canonical) : null;
    }

    /// <summary>
    /// THE MODEL IN ONE LINE FOR A PERSON — the holdout card's "Judged under". The pinned text is the
    /// record; this is a reading of it.
    /// </summary>
    public string Describe() => Judge switch
    {
        VenueJudge =>
            $"{VenueId} {Instrument}: fee {N(Model.FeeRate * 100m)}% a fill, the venue's published standard "
            + $"taker rate; slippage {N(Model.SlippageRate * 100m)}% a fill, TradeAgent's assumption; step "
            + $"{N(Model.QuantityIncrement)}; capital {N(Model.InitialCapital)}",
        NoVenueJudge =>
            $"frictionless — no venue recorded on these bars, so no fee, no slippage, whole units and "
            + $"capital {N(Model.InitialCapital)}",
        _ =>
            "frictionless — the legacy frictionless judge: this campaign charged a verdict before "
            + "TradeAgent pinned a cost model, and keeps the judge that verdict was taken under"
    };

    const string None = "none";

    static VenueCostModel Frictionless(string judge, string fee, string slippage, string increment, string capital) =>
        new(judge, null, null, ExecutionModel.Frictionless,
            Text(judge, ExecutionModel.Frictionless, null, null, fee, slippage, increment, capital));

    /// <summary>
    /// The pinned text: nine lines, this order, no trailing newline. The four provenance lines are for a
    /// reader and are never parsed; the first five are, by <see cref="Read"/>.
    /// </summary>
    static string Text(string judge, ExecutionModel model, string? venue, string? instrument,
        string fee, string slippage, string increment, string capital) =>
        string.Join('\n',
            Header,
            $"judge: {judge}",
            $"model: {model.Canonical}",
            $"venue: {venue ?? None}",
            $"instrument: {instrument ?? None}",
            $"fee: {fee}",
            $"slippage: {slippage}",
            $"increment: {increment}",
            $"capital: {capital}");

    static string? Field(string line, string name) =>
        line.StartsWith(name + ": ", StringComparison.Ordinal) ? line[(name.Length + 2)..] : null;

    /// <summary>
    /// <c>fees=…;slippage=…;increment=…;capital=…</c> back into a model, or null — and only when the
    /// model this declares spells itself the same way, so a number written in a second spelling is not
    /// quietly the same pin.
    /// </summary>
    static ExecutionModel? Parse(string spelled)
    {
        var parts = spelled.Split(';');
        if (parts.Length != 4) return null;

        decimal? Value(int i, string key) =>
            parts[i].StartsWith(key + "=", StringComparison.Ordinal)
            && decimal.TryParse(parts[i][(key.Length + 1)..], NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var v)
                ? v
                : null;

        if (Value(0, "fees") is not { } fees || Value(1, "slippage") is not { } slippage
            || Value(2, "increment") is not { } increment || Value(3, "capital") is not { } capital)
            return null;

        var declared = ExecutionModel.Declare(fees, slippage, increment, capital);
        return declared.Model is { } model && model.Canonical == spelled ? model : null;
    }

    static string N(decimal value) => StrategyParser.Number(value);

    /// <summary>A source sentence as one line: the pinned text is line-oriented and a newline inside a field would end it.</summary>
    static string Line(string text) => text.ReplaceLineEndings(" ");
}

/// <summary>
/// A VENUE COST MODEL, OR WHY THERE IS NONE — the shape <see cref="ExecutionModelDeclared"/> and
/// <see cref="BacktestOpened"/> already have: one of the two, never both, never an exception.
/// </summary>
public sealed record VenueCostModelResolved
{
    VenueCostModelResolved(VenueCostModel? model, string? refusal)
    {
        Model = model;
        Refusal = refusal;
    }

    /// <summary>The model, or null when there can be none.</summary>
    public VenueCostModel? Model { get; }

    /// <summary>Why there is none, in words, or null when there is one.</summary>
    public string? Refusal { get; }

    public bool Ok => Model is not null;

    /// <summary>The refusal's text, or the empty string when a model came out.</summary>
    public string Why => Refusal ?? "";

    internal static VenueCostModelResolved Yes(VenueCostModel model) => new(model, null);

    internal static VenueCostModelResolved No(string why) => new(null, why);
}

/// <summary>
/// THE VENUE COST MODEL'S FRICTION ON ONE VENUE — the fee and the slippage, without a step or a
/// capital, which is all a paper fill and a research run's undeclared friction need of it
/// (<c>U-paper-friction</c>; <c>docs/EDGE-FACTORY.md</c> § 4.5, "one app-owned venue cost model at
/// every stage").
///
/// <para><b>The same two numbers, in the same words, as the referee's pinned model.</b> The fee is the
/// venue's row in <see cref="VenueCostModel.PublishedFees"/> and the slippage is
/// <see cref="VenueCostModel.SlippageRate"/>, labelled TradeAgent's assumption; the two lines of
/// <see cref="Canonical"/> are the very lines <see cref="VenueCostModel.For"/> writes into a pinned
/// text. So a paper fill, a research run and a verdict on one venue are charged one fee, out of one
/// table, read on one day.</para>
///
/// <para><b>Named by an id and a sha, as a source has to be.</b> <see cref="Id"/> says which model and
/// which venue; <see cref="Sha256"/> is the hash of <see cref="Canonical"/>, so a fill or a run that
/// records both says exactly which text it was charged under, and a later edit to the fee table is a
/// different sha rather than the same name quietly meaning something else. The text has its own header
/// and four lines, so it is never read as a campaign's nine-line pin: <see cref="VenueCostModel.Read"/>
/// refuses it.</para>
///
/// <para><b>A venue with no published fee here has no friction model</b> — <see cref="Of"/> answers
/// null — which is the reading <see cref="VenueCostModel.For"/> takes for the referee: a guessed fee is
/// not a standard, and a run charged one would be a figure about a venue nobody priced.</para>
/// </summary>
public sealed record VenueFriction
{
    VenueFriction(PublishedFee fee)
    {
        VenueId = fee.VenueId;
        Venue = fee.Venue;
        FeeRate = fee.TakerRate;
        FeeReadOn = fee.ReadOn;
        Canonical = string.Join('\n',
            Header,
            $"venue: {fee.VenueId}",
            $"fee: {VenueCostModel.FeeLine(fee)}",
            $"slippage: {VenueCostModel.SlippageLine}");
    }

    /// <summary>The first line of every friction text: the cost model's own header, and the word that says this is its friction half.</summary>
    public const string Header = VenueCostModel.Header + " friction";

    /// <summary>The venue catalogue's id for the venue, e.g. <c>binance-spot</c>.</summary>
    public string VenueId { get; }

    /// <summary>The venue as a person names it.</summary>
    public string Venue { get; }

    /// <summary>The venue's published standard TAKER rate, a fraction of each fill's notional.</summary>
    public decimal FeeRate { get; }

    /// <summary>TradeAgent's assumed slippage, a fraction of the price — the same on every venue, and an assumption on every venue.</summary>
    public decimal SlippageRate => VenueCostModel.SlippageRate;

    /// <summary>The day the fee was read from the venue's own page.</summary>
    public DateOnly FeeReadOn { get; }

    /// <summary>The text that is hashed: the header, the venue, and the cost model's own fee and slippage lines.</summary>
    public string Canonical { get; }

    /// <summary>The SHA-256 of <see cref="Canonical"/>.</summary>
    public string Sha256 => Sha256Hex.Of(Canonical);

    /// <summary>Which model and which venue, e.g. <c>venue-cost-model-v1/binance-spot</c>.</summary>
    public string Id => $"venue-cost-model-v{VenueCostModel.Version}/{VenueId}";

    /// <summary>The model as a sentence names it: <c>TradeAgent's venue cost model v1 for binance-spot (sha256 …)</c>.</summary>
    public string Named => $"TradeAgent's venue cost model v{VenueCostModel.Version} for {VenueId} (sha256 {Sha256})";

    /// <summary>What the fee is, in words: whose published rate, and the day it was read.</summary>
    public string FeeWords =>
        $"{Venue}'s published standard taker rate, read {FeeReadOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>What the slippage is, in words: an assumption, said as one.</summary>
    public string SlippageWords => VenueCostModel.SlippageBasis;

    /// <summary>The friction for one venue, or null because this build has read no published fee for it.</summary>
    public static VenueFriction? Of(string? venueId) =>
        VenueCostModel.FeeOf(venueId) is { } fee ? new VenueFriction(fee) : null;
}
