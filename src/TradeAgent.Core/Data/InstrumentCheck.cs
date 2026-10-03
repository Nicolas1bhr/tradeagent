using System.Globalization;
using System.Text.Json;

namespace TradeAgent.Core.Data;

/// <summary>
/// WHAT A VENUE'S OWN PUBLISHED DEFINITION SAYS ABOUT ONE INSTRUMENT — the four numbers an instrument
/// check records (<c>U-venue-verify</c>).
///
/// <para><b>Read strictly, because these are the numbers a size is rounded down to.</b> An answer that
/// is not JSON, that does not list the instrument exactly once, that lacks a price filter with a tick
/// above zero or a lot-size filter with a step above zero, or that states a filter twice, is NOT a
/// definition: the caller records a failed check in those words, and nothing is served from it. Reading
/// "the part that parsed" is how a check comes to claim a step the venue never published — the reason
/// <c>ForwardBars.TryParse</c> refuses a half-readable klines answer.</para>
///
/// <para>The minimum quantity and the minimum notional are recorded when the venue states them and are
/// NOT applied anywhere: TradeAgent's v1 cost model ignores a minimum notional, and no size is refused or
/// raised for either. Numbers come back with their trailing zeros removed, so <c>"0.01000000"</c> and
/// <c>0.01</c> are the same tick in every message and every hash.</para>
/// </summary>
public sealed record InstrumentDefinition(
    decimal TickSize,
    decimal QuantityIncrement,
    decimal? MinQuantity,
    decimal? MinNotional)
{
    /// <summary>
    /// BINANCE'S <c>exchangeInfo</c> ANSWER FOR <paramref name="symbol"/>, or why it is not one. The shape,
    /// measured 2026-10-03: <c>{"symbols":[{"symbol":"BTCUSDT","filters":[{"filterType":"PRICE_FILTER",
    /// "tickSize":"0.01000000"},{"filterType":"LOT_SIZE","minQty":"0.00001000","stepSize":"0.00001000"},
    /// {"filterType":"NOTIONAL","minNotional":"5.00000000"}, …]}]}</c>, decimals as JSON strings. The older
    /// filter name <c>MIN_NOTIONAL</c> is read where <c>NOTIONAL</c> is absent.
    /// </summary>
    public static bool TryParse(string? body, string symbol, out InstrumentDefinition? definition, out string why)
    {
        definition = null;
        why = "";

        if (string.IsNullOrWhiteSpace(body)) { why = "the answer was empty"; return false; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { why = "the answer is not JSON"; return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Array)
            {
                why = "the answer holds no list of symbols, so it is not an instrument definition";
                return false;
            }

            var entries = symbols.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.Object
                            && s.TryGetProperty("symbol", out var name) && name.ValueKind == JsonValueKind.String
                            && string.Equals(name.GetString(), symbol, StringComparison.Ordinal))
                .ToList();
            if (entries.Count != 1)
            {
                why = entries.Count == 0
                    ? $"the answer does not list {symbol}"
                    : $"the answer lists {symbol} {entries.Count} times, so it is not one definition";
                return false;
            }

            if (!entries[0].TryGetProperty("filters", out var filters) || filters.ValueKind != JsonValueKind.Array)
            {
                why = $"{symbol}'s entry holds no filters";
                return false;
            }

            // THE FOUR FILTERS THIS READS, EACH AT MOST ONCE. A filter stated twice is two answers to one
            // question, and picking either would be choosing a step the venue did not single out.
            var read = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var f in filters.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("filterType", out var type)
                    || type.ValueKind != JsonValueKind.String) continue;

                var name = type.GetString()!;
                if (name is not ("PRICE_FILTER" or "LOT_SIZE" or "NOTIONAL" or "MIN_NOTIONAL")) continue;
                if (!read.TryAdd(name, f))
                {
                    why = $"{symbol}'s entry states its {name} filter more than once, so it is not one definition";
                    return false;
                }
            }

            if (!read.TryGetValue("PRICE_FILTER", out var price)
                || !Number(price, "tickSize", out var tick) || tick is not { } tickSize || tickSize <= 0m)
            {
                why = $"{symbol}'s entry has no PRICE_FILTER with a tick size above zero";
                return false;
            }

            if (!read.TryGetValue("LOT_SIZE", out var lot)
                || !Number(lot, "stepSize", out var step) || step is not { } stepSize || stepSize <= 0m)
            {
                why = $"{symbol}'s entry has no LOT_SIZE filter with a step size above zero";
                return false;
            }

            if (!Number(lot, "minQty", out var minQty))
            {
                why = $"{symbol}'s LOT_SIZE filter states a minimum quantity that is not a number of zero or more";
                return false;
            }

            decimal? minNotional = null;
            if ((read.TryGetValue("NOTIONAL", out var minimum) || read.TryGetValue("MIN_NOTIONAL", out minimum))
                && !Number(minimum, "minNotional", out minNotional))
            {
                why = $"{symbol}'s minimum-notional filter states a value that is not a number of zero or more";
                return false;
            }

            definition = new InstrumentDefinition(Plain(tickSize), Plain(stepSize),
                minQty is { } q ? Plain(q) : null, minNotional is { } n ? Plain(n) : null);
            return true;
        }
    }

    /// <summary>
    /// A filter's number: absent is true with null, a decimal of zero or more — as a JSON string, which is
    /// how Binance writes them, or a JSON number — is true with the value, and anything else is false.
    /// </summary>
    static bool Number(JsonElement filter, string name, out decimal? value)
    {
        value = null;
        if (!filter.TryGetProperty(name, out var v)) return true;

        var parsed = 0m;
        var ok = v.ValueKind == JsonValueKind.String
            ? decimal.TryParse(v.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed)
            : v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out parsed);
        if (!ok || parsed < 0m) return false;

        value = parsed;
        return true;
    }

    /// <summary>The same number with its trailing zeros removed: <c>0.01000000</c> is <c>0.01</c>.</summary>
    static decimal Plain(decimal value) =>
        decimal.Parse(value.ToString("0.############################", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
}

/// <summary>
/// WHAT ONE INSTRUMENT CHECK CAME TO, IN THE THREE WORDS THE LEDGER KEEPS (<c>U-venue-verify</c>).
///
/// <para>Text and not an enum, validated by the store and never by a CHECK, for the reason rung 28
/// gives: a word a newer build writes reads back as itself rather than as a failure to parse.</para>
/// </summary>
public static class InstrumentCheckOutcome
{
    /// <summary>The venue's own definition was read and it gave a tick size and a quantity increment.</summary>
    public const string Verified = "verified";

    /// <summary>Something was asked and nothing usable came back: no answer, a status that is not 200, or a body this build could not read.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The definition address was not on the built-in origin, so NOTHING was sent to it. The
    /// <c>U-key-host-pin</c> rule (<see cref="UrlOrigin"/>): the address a check may be made to comes
    /// from TradeAgent's built-in row and from nowhere else.
    /// </summary>
    public const string RefusedOrigin = "refused-origin";

    public static bool IsKnown(string? outcome) => outcome is Verified or Failed or RefusedOrigin;
}

/// <summary>
/// ONE ATTEMPT TO CHECK ONE INSTRUMENT AGAINST ITS VENUE'S PUBLISHED DEFINITION, as the verifier hands
/// it to <c>InstrumentCheckStore</c>.
///
/// <para>Every attempt is one of these and every one is written — verified, failed or refused. The
/// four numbers are present on a <see cref="InstrumentCheckOutcome.Verified"/> attempt and on no other:
/// a failed attempt that carried half a definition would be a measurement of nothing.</para>
/// </summary>
public sealed record InstrumentCheckAttempt
{
    /// <summary>The venue catalogue's id for the venue, e.g. <c>binance-spot</c>.</summary>
    public required string VenueId { get; init; }

    /// <summary>The instrument as the venue writes it, e.g. <c>BTCUSDT</c>.</summary>
    public required string Symbol { get; init; }

    /// <summary>The URL asked — or, for a refused attempt, the URL that was NOT asked — exactly as built.</summary>
    public required string Url { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>When the attempt ended: the answer in hand, the failure, or the refusal.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The HTTP status, or null because nothing answered at all — or nothing was asked.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>The SHA-256 of the body THIS BUILD computed. Never a vendor's: none is published.</summary>
    public string? BodySha256 { get; init; }

    /// <summary>The price grid as the venue published it. Verified attempts only.</summary>
    public decimal? TickSize { get; init; }

    /// <summary>The step a quantity moves in, as the venue published it. Verified attempts only.</summary>
    public decimal? QuantityIncrement { get; init; }

    /// <summary>The smallest quantity the venue accepts, where it published one. Verified attempts only.</summary>
    public decimal? MinQuantity { get; init; }

    /// <summary>
    /// The smallest order value the venue accepts, where it published one. Verified attempts only.
    /// RECORDED AND NOT APPLIED: TradeAgent's v1 cost model ignores it, and nothing refuses or raises a
    /// size for it.
    /// </summary>
    public decimal? MinNotional { get; init; }

    /// <summary>One of the three words of <see cref="InstrumentCheckOutcome"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Why the attempt failed or was refused, in words, or null for one that verified.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// ONE ROW OF <c>instrument_check</c>, as it was written. <see cref="Origin"/> is what the STORE read
/// off <see cref="Url"/> — never a value the caller supplied — and null where the URL has none.
/// </summary>
public sealed record InstrumentCheckRow(
    long Id,
    string VenueId,
    string Symbol,
    string Url,
    string? Origin,
    DateTimeOffset RequestedAt,
    DateTimeOffset ReceivedAt,
    int? HttpStatus,
    string? BodySha256,
    decimal? TickSize,
    decimal? QuantityIncrement,
    decimal? MinQuantity,
    decimal? MinNotional,
    string Outcome,
    string? Note)
{
    /// <summary>Whether this attempt read the venue's definition and found a tick size and a step in it.</summary>
    public bool IsVerified => string.Equals(Outcome, InstrumentCheckOutcome.Verified, StringComparison.Ordinal);
}
