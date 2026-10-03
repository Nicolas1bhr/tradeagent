namespace TradeAgent.Core.Data;

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
