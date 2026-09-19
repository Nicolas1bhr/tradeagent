using System.Globalization;
using System.Text.Json;

namespace TradeAgent.Core.Data;

/// <summary>
/// WHAT A FORWARD BAR IS, AND — JUST AS IMPORTANTLY — WHAT IT IS NOT.
///
/// <para>A forward bar is a CLOSED one-minute candle this installation asked a vendor for shortly
/// after it closed, kept with the instant it arrived. The archive datasets (<see cref="BinanceArchive"/>)
/// are months of history the vendor published with a checksum beside them; these are minutes
/// TradeAgent collected itself, off a market-data endpoint that publishes no sidecar for a live
/// window and could not, because the window did not exist when the file would have been signed.</para>
///
/// <para><b>So they are not evaluation evidence and the wording says so on every surface.</b> A
/// verdict is taken over frozen, checksummed, holdout-protected datasets; a forward bar has no
/// vendor checksum and no freeze. Letting the two read alike would let a program be judged on bars
/// that arrived while it was being judged — see <c>docs/CONTRACTS.md</c>, "Forward bars".</para>
///
/// <para><b>And no holdout applies to them.</b> A holdout is a time cutoff the owner set on a
/// dataset; every forward bar post-dates every freeze this installation holds, because it did not
/// exist when the freeze was taken. There is therefore nothing here to hold back and the read is
/// open to any role — which is a statement about WHAT these bars are, not a relaxation: the archive
/// reader's cutoff is untouched and still refuses every caller.</para>
/// </summary>
public static class ForwardBars
{
    /// <summary>What the forward ledger records as the origin of these bars. A catalogue id.</summary>
    public const string Source = "binance-spot-forward-klines";

    /// <summary>The only interval collected forward. One minute, closed, UTC.</summary>
    public const string Interval = "1m";

    /// <summary>The word a caller asks for these bars by: <c>data-bars --source forward</c>.</summary>
    public const string SourceWord = "forward";

    /// <summary>How long one forward bar is. The gap arithmetic is in these units and nothing else.</summary>
    public static readonly TimeSpan BarLength = TimeSpan.FromMinutes(1);

    /// <summary>
    /// THE SENTENCE EVERY SURFACE PRINTS, spelled once. <c>data-list</c>, <c>data-bars</c>, the
    /// Settings card and the owner's report all say it, and four copies of a caveat are four
    /// wordings — the one that drifts is the one nobody re-reads (the rule
    /// <see cref="BarQuality.Note"/> follows).
    /// </summary>
    public const string Evidence =
        "FORWARD — collected by TradeAgent minute by minute; no vendor checksum; not evaluation evidence";

    /// <summary>
    /// The most bars one forward request asks for. The vendor's own per-request ceiling, and the
    /// reason a collector that was off for a day catches up in one call rather than in a thousand.
    /// </summary>
    public const int RequestLimit = 1000;

    /// <summary>
    /// ONE CANDLE AS THE VENDOR SERVED IT, before anything has decided whether it may be stored.
    /// It carries no fetch id and no received instant: those are the STORE's, and a parse that
    /// minted them would be deciding provenance before the row exists.
    /// </summary>
    public sealed record Kline(
        DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close,
        decimal Volume, DateTimeOffset CloseTime);

    /// <summary>
    /// READS A KLINES BODY, OR SAYS WHY IT IS NOT ONE.
    ///
    /// <para><b>A body that does not parse is a recorded failure and never a bar.</b> False here
    /// means the caller writes a <c>forward_fetch</c> row whose note is <paramref name="why"/> and
    /// stores nothing — the alternative, skipping the rows it could not read, is a collector that
    /// reports a healthy minute for a vendor that answered with an error page.</para>
    ///
    /// <para>Timestamps go through <see cref="KlineNormaliser.TryUnitOf"/>, so a column that moved
    /// from milliseconds to microseconds reads correctly here for the same reason it does in the
    /// archive, and a number that is neither is refused rather than read as 1970.</para>
    /// </summary>
    public static bool TryParse(string? body, out IReadOnlyList<Kline> bars, out string? why)
    {
        bars = [];
        why = null;

        if (string.IsNullOrWhiteSpace(body)) { why = "the body was empty"; return false; }

        List<Kline> read = [];
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                why = $"the body is {doc.RootElement.ValueKind} and a klines answer is an array";
                return false;
            }

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                // TWELVE COLUMNS IS THE VENDOR'S SHAPE; this build reads the first seven and refuses a
                // row with fewer, because a short row is a different answer and not a bar with holes.
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7)
                {
                    why = "a row in the body is not a kline array of at least seven columns";
                    return false;
                }

                if (!TryInstant(row[0], out var open) || !TryInstant(row[6], out var close)
                    || !TryNumber(row[1], out var o) || !TryNumber(row[2], out var h)
                    || !TryNumber(row[3], out var l) || !TryNumber(row[4], out var c)
                    || !TryNumber(row[5], out var v))
                {
                    why = "a column in the body is not a number this build can read";
                    return false;
                }

                if (close <= open)
                {
                    why = $"a row closes at {close:O} which is not after its open at {open:O}";
                    return false;
                }

                read.Add(new Kline(open, o, h, l, c, v, close));
            }
        }
        catch (JsonException ex) { why = "the body is not JSON: " + ex.Message.ReplaceLineEndings(" "); return false; }

        // ASCENDING AND WITHOUT A REPEAT, which is what the vendor serves and what the gap arithmetic
        // below assumes. A body that arrived in another order would have its holes counted backwards.
        for (var i = 1; i < read.Count; i++)
            if (read[i].OpenTime <= read[i - 1].OpenTime)
            {
                why = $"the rows are not in ascending open-time order at {read[i].OpenTime:O}";
                return false;
            }

        bars = read;
        return true;
    }

    static bool TryInstant(JsonElement e, out DateTimeOffset at)
    {
        at = default;
        long raw;
        if (e.ValueKind == JsonValueKind.Number) { if (!e.TryGetInt64(out raw)) return false; }
        else if (e.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
                return false;
        }
        else return false;

        if (KlineNormaliser.TryUnitOf(raw) is not { } unit) return false;
        at = unit == KlineTimeUnit.Milliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
            : DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).AddTicks(raw % 1000L * 10L);
        return true;
    }

    static bool TryNumber(JsonElement e, out decimal d)
    {
        d = 0m;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetDecimal(out d),
            JsonValueKind.String => decimal.TryParse(e.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out d),
            _ => false
        };
    }
}

/// <summary>
/// ONE STORED FORWARD BAR: the candle, the fetch that brought it and the instant it arrived.
///
/// <para><see cref="ReceivedAt"/> is on the bar and not only on the fetch, because it is the fact
/// that makes the bar's closure checkable afterwards: a reader can see that this bar's close time
/// precedes the moment it was read, without trusting that the collector checked.</para>
/// </summary>
public sealed record ForwardBar(
    string Source, string Symbol, DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low,
    decimal Close, decimal Volume, DateTimeOffset CloseTime, DateTimeOffset ReceivedAt, long FetchId);

/// <summary>
/// ONE ATTEMPT TO FETCH FORWARD BARS, SUCCEEDED OR FAILED, as the collector observed it.
///
/// <para>Every attempt is one of these and every one of them is written. A fetch that got no answer
/// at all has a null <see cref="HttpStatus"/> and a note; the row still exists, because a minute
/// with no row against it is indistinguishable from a minute the collector never ran in.</para>
/// </summary>
public sealed record ForwardFetchAttempt
{
    public required string Source { get; init; }
    public required string Symbol { get; init; }

    /// <summary>The URL asked for, as asked. Evidence of the window this attempt covered.</summary>
    public required string Url { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>When the answer — or the failure — was in hand. What CLOSURE is measured against.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The HTTP status, or null when nothing was answered at all (a timeout, a refused socket).</summary>
    public int? HttpStatus { get; init; }

    /// <summary>The SHA-256 of the body this build computed. NEVER a vendor's: there is none.</summary>
    public string? BodySha256 { get; init; }

    /// <summary>Why this attempt produced no bars, in words, or null for one that did.</summary>
    public string? Note { get; init; }
}

/// <summary>What one <c>Append</c> actually did, for the caller's status line and for a test.</summary>
public sealed record ForwardAppend(
    long FetchId, int Stored, int NotClosedYet, int AlreadyHeld, int Disagreed,
    IReadOnlyList<ForwardGapRun> Gaps)
{
    /// <summary>The newest bar this call stored, or null because it stored none.</summary>
    public DateTimeOffset? LastOpen { get; init; }
}

/// <summary>
/// A RUN OF MINUTES WITH NO BAR IN IT, recorded where it was seen and never filled.
///
/// <para>Both ends are OPEN TIMES OF MISSING BARS — the first minute with no bar and the last — so a
/// run of one is a row whose two ends are equal. Counting them is not the same as having them:
/// nothing here interpolates, carries a price forward or invents a volume, and a research run over
/// a window with a gap in it gets the gap.</para>
/// </summary>
public sealed record ForwardGapRun(
    string Source, string Symbol, DateTimeOffset FromOpen, DateTimeOffset ToOpen, int BarsMissing,
    DateTimeOffset SeenAt);

/// <summary>
/// EVERYTHING <c>data-list</c> AND THE STATUS LINE SAY ABOUT ONE FORWARD SERIES, computed from the
/// rows and stored nowhere — a count kept in a second place is a count that can disagree with the
/// table it is about.
/// </summary>
public sealed record ForwardSeries(
    string Source, string Symbol, string Interval, int Bars, DateTimeOffset? FirstBar,
    DateTimeOffset? LastBar, int Gaps, int BarsMissing, DateTimeOffset? LastReceivedAt)
{
    /// <summary>What these bars are and are not. One sentence, <see cref="ForwardBars.Evidence"/>.</summary>
    public string Note => ForwardBars.Evidence;

    /// <summary>The last attempt's note, when the last attempt failed. Null when the last one worked.</summary>
    public string? LastError { get; init; }

    /// <summary>When that failing attempt was made. Null when the last attempt worked.</summary>
    public DateTimeOffset? LastErrorAt { get; init; }
}
