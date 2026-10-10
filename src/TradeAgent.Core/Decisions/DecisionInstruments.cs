using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeAgent.Core.Decisions;

/// <summary>
/// WHAT ONE INSTRUMENT MAY BE ASKED, AT WHAT SIZE AND HOW FAST — the host's documented figures, kept as data on the
/// instrument. <see cref="TokensPerSecond"/> and <see cref="RequestsPerSecond"/> are ENFORCED by
/// <see cref="DecisionRateGate"/> before a call reads its key or reserves anything (<c>U-decision-card</c>). Zero is "not
/// documented", and the gate reads it as TradeAgent's own bound — one call in flight, one a second — never as no bound.
/// </summary>
public sealed record DecisionLimits
{
    /// <summary>The host's budget for one request: the state and every question together, in its tokens.</summary>
    public int MaxRequestTokens { get; init; }

    /// <summary>The host's budget for the state and the single longest question, in its tokens.</summary>
    public int MaxStateAndQuestionTokens { get; init; }

    public int MaxChoiceOptions { get; init; }
    public int MinScoreLevels { get; init; }
    public int MaxScoreLevels { get; init; }
    public int TokensPerSecond { get; init; }
    public int RequestsPerSecond { get; init; }
}

/// <summary>
/// ONE INSTRUMENT: one host serving one pinned model, with its price as read on a named day and its limits.
///
/// <para><b>Identity is (host, answered id).</b> The request id is what TradeAgent asks for and the pin is what an answer
/// has to NAME to be evidence — two fields, because they differ: OpenRouter is asked <c>typesafe/jev-1.13</c> and answers
/// <c>typesafe/jev-1.13-20260917</c>. Neither is ever an alias (<see cref="DecisionInstruments.IsAlias"/>): an alias moves
/// when a new release ships, and the answers behind it change with no change on this side.</para>
/// </summary>
public sealed record DecisionInstrument
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>The full URL a call is posted to. Its origin is the only one the instrument's key is ever released for.</summary>
    public required string Endpoint { get; init; }

    public required string RequestModel { get; init; }
    public required string Pin { get; init; }

    public required decimal InputPerMillion { get; init; }
    public decimal OutputPerMillion { get; init; }
    public required string Currency { get; init; }

    /// <summary>The day the price was read, <c>yyyy-MM-dd</c>, and the page it was read from.</summary>
    public required string PricedOn { get; init; }

    public required string PriceSource { get; init; }
    public required DecisionLimits Limits { get; init; }
    public required string DocUrl { get; init; }

    /// <summary>
    /// The day the RATES were read, <c>yyyy-MM-dd</c>, and the page they were read from — the page that documents them, or
    /// the one that says the host documents none. This build's own figures only: a rate <c>decision-models.json</c> moved
    /// carries no date, and the card and the gate say so (<c>U-decision-card</c>).
    /// </summary>
    public required string RatesReadOn { get; init; }

    public required string RatesSource { get; init; }

    /// <summary>Whether the host reports what it billed on every answer (<c>usage.cost</c>).</summary>
    public bool ReportsBilledCost { get; init; }

    /// <summary>
    /// THE MOST INPUT TOKENS ONE REQUEST CAN BE BILLED, which is what a call reserves. The host's per-request budget is
    /// "64k tokens"; it is read as 65,536, the larger reading, because a reservation that errs low is a ceiling that can
    /// be walked past. The host refuses a request over its budget, so no answer bills more — and an answer that never
    /// comes keeps this reservation as its cost.
    /// </summary>
    public int BillableTokensBound { get; init; } = 65_536;

    /// <summary>The origin of <see cref="Endpoint"/>, or null where it has none.</summary>
    public string? Origin => UrlOrigin.Of(Endpoint);

    /// <summary>
    /// THE MOST TOKENS ONE CALL IS COUNTED AT BEFORE ITS ANSWER SAYS OTHERWISE: the billable bound — or the request budget,
    /// where an override raised that past it. What a call reserves in money (<see cref="Reservation"/>) and what it is
    /// charged against a host's tokens a second while it flies (<see cref="DecisionRateGate"/>) are the same figure.
    /// </summary>
    public long ReservedTokens => Math.Max(BillableTokensBound, Limits.MaxRequestTokens);

    /// <summary>
    /// WHAT ONE CALL COMMITS BEFORE IT IS SENT: <see cref="ReservedTokens"/> at the dated price, input and output alike.
    /// Output is free at both hosts today; an override that prices it is still covered.
    /// </summary>
    public decimal Reservation => ReservedTokens * (InputPerMillion + OutputPerMillion) / 1_000_000m;

    /// <summary>What <paramref name="input"/> and <paramref name="output"/> tokens come to at the dated price.</summary>
    public decimal Estimate(long input, long output) =>
        (input * InputPerMillion + output * OutputPerMillion) / 1_000_000m;

    /// <summary>Where the estimate's rate came from, on the row beside the figure, because these go stale on the host's schedule.</summary>
    public string PriceBasis =>
        $"list price {InputPerMillion.ToString(CultureInfo.InvariantCulture)} {Currency} per million input tokens and "
        + $"{OutputPerMillion.ToString(CultureInfo.InvariantCulture)} per million output, read {PricedOn} from {PriceSource}";
}

/// <summary>The instruments as they stand: the ones a call may use, the reason the file could not be read, and the rows refused.</summary>
public sealed record DecisionInstrumentsRead(
    IReadOnlyList<DecisionInstrument> Instruments, string? Unreadable, IReadOnlyList<string> Refused);

/// <summary>
/// ONE ROW OF <c>decision-models.json</c>: the fields an override MAY change — a price and its date and page, and the
/// limits. Anything else the row names lands in <see cref="Other"/> and refuses the row.
/// </summary>
public sealed class DecisionInstrumentOverride
{
    public string? Id { get; set; }
    public decimal? InputPerMillion { get; set; }
    public decimal? OutputPerMillion { get; set; }
    public string? PricedOn { get; set; }
    public string? PriceSource { get; set; }
    public int? MaxRequestTokens { get; set; }
    public int? MaxStateAndQuestionTokens { get; set; }
    public int? MaxChoiceOptions { get; set; }
    public int? MinScoreLevels { get; set; }
    public int? MaxScoreLevels { get; set; }
    public int? TokensPerSecond { get; set; }
    public int? RequestsPerSecond { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }
}

/// <summary>
/// THE INSTRUMENTS THIS BUILD SHIPS — Jev 1.13 at TypeSafe and through OpenRouter, read from their own pages on
/// 2026-10-09 (<c>docs/RESEARCH-REQUIRED.md</c> § D2) — and what <c>decision-models.json</c> may change about them.
///
/// <para><b>The file may change a price or a limit. It may never change an address, a request id or a pin</b>, and it
/// may not add an instrument. It sits in TradeAgent's own folder, which an agent running unconfined can write: an
/// address it could move is a key sent wherever it chose, and a pin it could move is evidence it could re-label. So a
/// row naming anything else is refused in words, and so is the instrument it names, until the row is fixed — the
/// built-in price is not silently put back in place of a correction somebody wrote. An unreadable file stops every
/// call, for the same reason (<see cref="VendorFile"/>'s rule).</para>
///
/// <para><b>A price is never zero.</b> Zero is not a cheap rate, it is the absence of one: every call would cost
/// nothing and the perception budget would stop existing while reading as though it were in force. The rule
/// <see cref="OwnerPrice"/> keeps for the owner's own numbers, kept here for the file's.</para>
/// </summary>
public static class DecisionInstruments
{
    /// <summary>The file that may change a price or a limit, beside <c>runtimes.json</c>.</summary>
    public static string OverridePath => Path.Combine(Paths.Home, "decision-models.json");

    public const string TypeSafeDirect = "typesafe-direct";
    public const string OpenRouterJev = "openrouter-jev";

    /// <summary>The day every figure below was read from the page beside it.</summary>
    public const string ReadOn = "2026-10-09";

    public static List<DecisionInstrument> BuiltIn() =>
    [
        new DecisionInstrument
        {
            Id = TypeSafeDirect,
            DisplayName = "Jev 1.13 at TypeSafe",
            // docs.typesafe.ai/api: "POST https://api.typesafe.ai/v1/systemone", Bearer key.
            Endpoint = "https://api.typesafe.ai/v1/systemone",
            // docs.typesafe.ai/models: jev-1.13.0 is the one model; jev-latest and jev-preview are aliases of it, and
            // "the response's model field reports the versioned ID that answered". Asked and pinned by that id.
            RequestModel = "jev-1.13.0",
            Pin = "jev-1.13.0",
            // "$42 / $0.042" per Btok / per Mtok; "Output tokens are free".
            InputPerMillion = 0.042m,
            OutputPerMillion = 0m,
            Currency = "USD",
            PricedOn = ReadOn,
            PriceSource = "https://docs.typesafe.ai/models",
            // "64k tokens per request; 32k tokens for state plus the longest question"; "100K tokens per second / 80
            // requests per second"; docs.typesafe.ai/api: "a maximum of 255 options per Choice", "A Score should have at
            // least two levels; the API accepts up to 10" — none of which its OpenAPI schema enforces.
            Limits = new DecisionLimits
            {
                MaxRequestTokens = 64_000,
                MaxStateAndQuestionTokens = 32_000,
                MaxChoiceOptions = 255,
                MinScoreLevels = 2,
                MaxScoreLevels = 10,
                TokensPerSecond = 100_000,
                RequestsPerSecond = 80
            },
            DocUrl = "https://docs.typesafe.ai/api",
            // docs.typesafe.ai/models: "100K tokens per second / 80 requests per second", a 429 over either; "can change
            // without notice". Re-read 2026-10-10 (U-decision-card), unchanged.
            RatesReadOn = ReadOn,
            RatesSource = "https://docs.typesafe.ai/models",
            ReportsBilledCost = false
        },
        new DecisionInstrument
        {
            Id = OpenRouterJev,
            DisplayName = "Jev 1.13 through OpenRouter",
            // openrouter.ai/docs/guides/community/typesafe-sdk: "requests are sent to https://openrouter.ai/api/v1/systemone",
            // TypeSafe's request and response shapes, "OpenRouter additionally returns id, provider, and usage.cost".
            // Never the alpha Decisions API, which has its own shape.
            Endpoint = "https://openrouter.ai/api/v1/systemone",
            // Asked by OpenRouter's model id; "the model field in the response contains the OpenRouter model ID of the
            // System One model that served the request" — typesafe/jev-1.13-20260917 in both documented examples and in
            // the endpoint's own name. That dated id is the pin.
            RequestModel = "typesafe/jev-1.13",
            Pin = "typesafe/jev-1.13-20260917",
            // openrouter.ai/typesafe/jev-1.13: "$0.042 / $0 per 1M".
            InputPerMillion = 0.042m,
            OutputPerMillion = 0m,
            Currency = "USD",
            PricedOn = ReadOn,
            PriceSource = "https://openrouter.ai/typesafe/jev-1.13",
            // The model page says "64K" and the Jev guide "32,000 tokens"; the smaller is kept for what is sent. No rate
            // limit is documented for it: zero, which the gate reads as TradeAgent's own bound.
            Limits = new DecisionLimits
            {
                MaxRequestTokens = 32_000,
                MaxStateAndQuestionTokens = 32_000,
                MaxChoiceOptions = 255,
                MinScoreLevels = 2,
                MaxScoreLevels = 10
            },
            DocUrl = "https://openrouter.ai/docs/guides/community/typesafe-sdk",
            // openrouter.ai/docs/api-reference/limits, read 2026-10-10: capacity is "governed globally", per model and not
            // per key; a paid model has "no platform-level request cap"; a 429 carries Retry-After only sometimes, a credit
            // limit answers 402. No figure a call could be held to: the rates stay zero.
            RatesReadOn = "2026-10-10",
            RatesSource = "https://openrouter.ai/docs/api-reference/limits",
            ReportsBilledCost = true
        }
    ];

    /// <summary>The pin of each built-in instrument, read from this build's rows once and from nothing a file says.</summary>
    static readonly FrozenDictionary<string, string> Pins =
        BuiltIn().ToFrozenDictionary(i => i.Id, i => i.Pin, StringComparer.Ordinal);

    /// <summary>The limits each built-in instrument ships with, read from this build's rows once and from nothing a file says.</summary>
    static readonly FrozenDictionary<string, DecisionLimits> ShippedLimits =
        BuiltIn().ToFrozenDictionary(i => i.Id, i => i.Limits, StringComparer.Ordinal);

    /// <summary>
    /// THE LIMITS <paramref name="instrumentId"/> SHIPS WITH, or null because it is no built-in instrument. What the card and
    /// the gate compare a limit against to say whether it is the host's dated figure or one <c>decision-models.json</c> moved.
    /// </summary>
    public static DecisionLimits? BuiltInLimits(string? instrumentId) =>
        instrumentId is not null && ShippedLimits.TryGetValue(instrumentId, out var limits) ? limits : null;

    /// <summary>
    /// THE ANSWERED ID THAT MAKES A CALL TO <paramref name="instrumentId"/> EVIDENCE, or null because it is no built-in
    /// instrument and nothing it answers can be. The tape's store reads this and nothing else.
    /// </summary>
    public static string? BuiltInPin(string? instrumentId) =>
        instrumentId is not null && Pins.TryGetValue(instrumentId, out var pin) ? pin : null;

    /// <summary>
    /// WHETHER AN ANSWER THAT NAMED <paramref name="answeredModel"/> IS EVIDENCE FROM <paramref name="instrumentId"/>: the
    /// id the host named is EXACTLY the built-in pin. A call that named no model, an instrument this build does not ship
    /// and a model one character off the pin are all UNPINNED.
    /// </summary>
    public static bool IsPinned(string? instrumentId, string? answeredModel) =>
        BuiltInPin(instrumentId) is { } pin && string.Equals(answeredModel, pin, StringComparison.Ordinal);

    /// <summary>
    /// WHETHER A MODEL NAME IS AN ALIAS — a name that moves to another model when a new release ships: TypeSafe's
    /// <c>jev-latest</c> and <c>jev-preview</c>, and OpenRouter's <c>~</c>-prefixed names.
    /// </summary>
    public static bool IsAlias(string? model) =>
        model is null
        || model.StartsWith('~')
        || model.EndsWith("-latest", StringComparison.OrdinalIgnoreCase)
        || model.EndsWith("-preview", StringComparison.OrdinalIgnoreCase);

    /// <summary>The instrument a call may use, or null: unknown, refused, or every instrument stopped by an unreadable file.</summary>
    public static DecisionInstrument? Find(string id, string? overridePath = null) =>
        Read(overridePath).Instruments.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>The built-ins with whatever valid prices and limits the file sets. See the type summary.</summary>
    /// <param name="overridePath">The file to read instead of <see cref="OverridePath"/>. For tests.</param>
    public static DecisionInstrumentsRead Read(string? overridePath = null)
    {
        var builtIn = BuiltIn();
        var file = VendorFile.Read<List<DecisionInstrumentOverride?>>(overridePath ?? OverridePath);
        if (file.Unreadable is { } why)
            return new([],
                $"TradeAgent found a decision-models.json in its own folder and could not read it: {why}. No decision model is "
                + "called until it is fixed or removed, because the built-in prices would otherwise stand in for a correction "
                + "somebody wrote.", []);

        var refused = new List<string>();
        var stopped = new HashSet<string>(StringComparer.Ordinal);
        var result = builtIn.ToDictionary(i => i.Id, StringComparer.Ordinal);

        foreach (var row in file.Value ?? [])
        {
            var id = row?.Id ?? "";
            if (row is null || !result.ContainsKey(id))
            {
                refused.Add($"decision-models.json names '{id}', which is not one of TradeAgent's decision models; the file may "
                            + "change a built-in model's price or limits and cannot add one.");
                continue;
            }

            if (Refusal(row, result[id]) is { } no)
            {
                refused.Add($"'{id}' in decision-models.json {no}. It is not called until the row is fixed.");
                stopped.Add(id);
                continue;
            }

            var i = result[id];
            result[id] = i with
            {
                InputPerMillion = row.InputPerMillion ?? i.InputPerMillion,
                OutputPerMillion = row.OutputPerMillion ?? i.OutputPerMillion,
                PricedOn = row.PricedOn ?? i.PricedOn,
                PriceSource = row.PriceSource ?? i.PriceSource,
                Limits = i.Limits with
                {
                    MaxRequestTokens = row.MaxRequestTokens ?? i.Limits.MaxRequestTokens,
                    MaxStateAndQuestionTokens = row.MaxStateAndQuestionTokens ?? i.Limits.MaxStateAndQuestionTokens,
                    MaxChoiceOptions = row.MaxChoiceOptions ?? i.Limits.MaxChoiceOptions,
                    MinScoreLevels = row.MinScoreLevels ?? i.Limits.MinScoreLevels,
                    MaxScoreLevels = row.MaxScoreLevels ?? i.Limits.MaxScoreLevels,
                    TokensPerSecond = row.TokensPerSecond ?? i.Limits.TokensPerSecond,
                    RequestsPerSecond = row.RequestsPerSecond ?? i.Limits.RequestsPerSecond
                }
            };
        }

        return new([.. builtIn.Select(b => result[b.Id]).Where(i => !stopped.Contains(i.Id))], null, refused);
    }

    /// <summary>Why a file row may not be applied to <paramref name="shipped"/>, as the end of a sentence, or null because it may.</summary>
    static string? Refusal(DecisionInstrumentOverride row, DecisionInstrument shipped)
    {
        if (row.Other is { Count: > 0 } other)
            return $"names {string.Join(", ", other.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}, and a row there "
                   + "may change only a price, its date and page, and the limits: an address, a request id and a pin come "
                   + "from this build and from nowhere else";
        if (row.InputPerMillion is { } input && input <= 0m)
            return "prices input at zero or less, which is no price at all";
        if (row.OutputPerMillion is { } output && output < 0m)
            return "prices output below zero";
        if ((row.InputPerMillion is not null || row.OutputPerMillion is not null) && (row.PricedOn is null || row.PriceSource is null))
            return "changes a price without the day it was read and the page it was read from";
        if (row.PricedOn is { } on && !DateOnly.TryParseExact(on, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return $"dates its price '{on}', which is not a day written yyyy-MM-dd";
        if (row.PriceSource is { } source && UrlOrigin.Of(source) is null)
            return $"reads its price from '{source}', which is not a web address";

        int?[] limits = [row.MaxRequestTokens, row.MaxStateAndQuestionTokens, row.MaxChoiceOptions, row.MinScoreLevels, row.MaxScoreLevels];
        if (limits.Any(l => l is <= 0)) return "sets a limit to zero or less";
        if (row.TokensPerSecond is < 0 || row.RequestsPerSecond is < 0) return "sets a rate below zero";

        // NO FILE LIFTS THE APP'S OWN BOUND (U-decision-card). Where the host documents no rate, TradeAgent applies its own —
        // one call in flight, one a second — and a figure in a file an agent can write is not documentation. Zero is no
        // lift: it is the same bound.
        if ((row.TokensPerSecond is > 0 && shipped.Limits.TokensPerSecond == 0)
            || (row.RequestsPerSecond is > 0 && shipped.Limits.RequestsPerSecond == 0))
            return "sets a rate its host does not document; TradeAgent applies its own bound there — one call in flight and "
                   + "one a second — and a file cannot lift it";
        return null;
    }
}
