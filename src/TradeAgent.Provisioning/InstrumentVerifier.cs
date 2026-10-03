using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Provisioning;

/// <summary>
/// THE APP CHECKING AN INSTRUMENT AGAINST ITS VENUE'S OWN PUBLISHED DEFINITION (<c>U-venue-verify</c>):
/// one GET of the venue's definition of one symbol, from the BUILT-IN origin, recorded as a row whatever
/// happened.
///
/// <para><b>Why it exists.</b> The step a size is rounded down to has to be the venue's, and until this
/// unit the only way to say so was a hand-edited <c>venues.json</c> that no screen writes — a terminal in
/// all but name. Now the app reads the venue's definition itself, when the owner picks or changes the pair,
/// at start for the configured pair (and every six hours while it runs), and on the owner's one-press
/// "Check now"; a verified row is served to every reader of the catalogue for seven days
/// (<c>VenueStore</c>).</para>
///
/// <para><b>The address is a built-in venue fact.</b> The shape comes from
/// <see cref="VenueCatalog.DefinitionShape"/>, compiled into this build. A <c>venues.json</c> override
/// (<see cref="VenueEntry.DefinitionUrl"/>) is honoured only on the SAME origin; any other origin is
/// recorded <see cref="InstrumentCheckOutcome.RefusedOrigin"/> and nothing is sent to it — the
/// <c>U-key-host-pin</c> rule through the one <see cref="UrlOrigin"/>, decided before a socket is opened.
/// A redirect is not followed either: the answer has to come from the address that was asked.</para>
///
/// <para><b>It places no order, holds no credential and reaches nothing that could.</b> The host is
/// Binance's market-data-only one, which accepts no authenticated or trading request at all. No verb and
/// no pipe op starts a check, points it elsewhere or writes a row: the triggers above are the app's own
/// and the owner's, in-process, and the pipe server's assembly does not reference this one.</para>
///
/// <para><b>A failure is a row and a status line, never a crash.</b> No answer within
/// <see cref="DefaultRequestTimeout"/>, a status that is not 200, a body over <see cref="MaxBodyBytes"/>
/// or one this build cannot read as the instrument's definition is a <c>failed</c> row with the reason in
/// words, and the instrument stays unverified until a check succeeds.</para>
/// </summary>
public sealed class InstrumentVerifier
{
    /// <summary>
    /// THE REQUEST'S OWN LEASH: ten seconds, the forward collector's, for a small public answer. A
    /// definition still outstanding after ten seconds is a check that failed, and says so.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One symbol's definition is a few kilobytes. Anything past this is not one and is not buffered.</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    // ONE CLIENT, NO TIMEOUT OF ITS OWN (the leash is per request) AND NO REDIRECTS: see the type summary.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    readonly InstrumentCheckStore _store;
    readonly VenueCatalogRead _catalogue;
    readonly Func<string, string?> _builtIn;
    readonly Func<DateTimeOffset> _now;
    readonly SemaphoreSlim _one = new(1, 1);

    /// <param name="catalogue">
    /// The catalogue whose venue entries may override a definition address. Null reads
    /// <see cref="VenueCatalog.Read"/> once, here — the same file the gateway records the catalogue from.
    /// </param>
    /// <param name="builtInDefinition">
    /// The BUILT-IN definition shape for a venue id. Null means <see cref="VenueCatalog.DefinitionShape"/>,
    /// the address compiled into this build, which is the vendor's. A test passes its loopback stand-in
    /// here, and nothing in this suite has ever passed anything else.
    /// </param>
    public InstrumentVerifier(
        Database db,
        VenueCatalogRead? catalogue = null,
        Func<string, string?>? builtInDefinition = null,
        TimeSpan? requestTimeout = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        _store = new InstrumentCheckStore(db);
        _catalogue = catalogue ?? VenueCatalog.Read();
        _builtIn = builtInDefinition ?? VenueCatalog.DefinitionShape;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        RequestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <inheritdoc cref="DefaultRequestTimeout"/>
    public TimeSpan RequestTimeout { get; }

    /// <summary>
    /// ONE CHECK OF <paramref name="symbol"/> ON <paramref name="venueId"/>, recorded — or null because
    /// nothing could be asked: the symbol is not one TradeAgent will put in an address (upper-case letters
    /// and digits), the catalogue holds no such venue (an unreadable <c>venues.json</c> holds none at all),
    /// or neither the venue's entry nor this build names a definition address for it.
    ///
    /// <para>Checks run one at a time; a second caller waits for the first and then makes its own. It never
    /// throws for a vendor failure — that is the row — and a cancelled caller's request is abandoned
    /// without one.</para>
    /// </summary>
    public async Task<InstrumentCheckRow?> CheckAsync(string venueId, string symbol, CancellationToken ct = default)
    {
        if (!BinanceArchive.IsPair(symbol)) return null;
        if (_catalogue.Venues.FirstOrDefault(v => string.Equals(v.Id, venueId, StringComparison.Ordinal)) is not { } venue)
            return null;

        var builtIn = _builtIn(venueId);
        var shape = string.IsNullOrWhiteSpace(venue.DefinitionUrl) ? builtIn : venue.DefinitionUrl.Trim();
        if (string.IsNullOrWhiteSpace(shape)) return null;

        var url = Address(shape, symbol);
        var expected = builtIn is null ? null : UrlOrigin.Of(Address(builtIn, symbol));

        await _one.WaitAsync(ct);
        try
        {
            var requestedAt = _now();

            // THE ORIGIN RULE, BEFORE ANYTHING IS SENT. An address whose origin is not the built-in one is
            // recorded and refused here; it never reaches the socket below.
            var origin = UrlOrigin.Of(url);
            if (origin is null || expected is null || !string.Equals(origin, expected, StringComparison.Ordinal))
                return _store.Append(new InstrumentCheckAttempt
                {
                    VenueId = venueId,
                    Symbol = symbol,
                    Url = url,
                    RequestedAt = requestedAt,
                    ReceivedAt = requestedAt,
                    Outcome = InstrumentCheckOutcome.RefusedOrigin,
                    Note = RefusedNote(url, origin, expected, venue.DisplayName)
                });

            var (status, body, failure) = await GetAsync(url, ct);
            var receivedAt = _now();

            var note = failure;
            InstrumentDefinition? definition = null;
            if (note is null && status != 200)
                note = status is >= 300 and < 400
                    ? $"the host answered {status}, a redirect, which TradeAgent does not follow — a definition "
                      + "has to come from the built-in address itself"
                    : $"the host answered {status} and no definition was read";
            else if (note is null && !InstrumentDefinition.TryParse(body, symbol, out definition, out var why))
                note = $"the answer could not be read as {symbol}'s definition: {why}";

            return _store.Append(new InstrumentCheckAttempt
            {
                VenueId = venueId,
                Symbol = symbol,
                Url = url,
                RequestedAt = requestedAt,
                ReceivedAt = receivedAt,
                HttpStatus = status,
                // THIS BUILD'S OWN HASH OF THE BODY, AND NEVER A VENDOR'S: none is published. It proves the
                // answer has not changed since it was recorded, which is a different claim from proving it
                // is what the venue meant to publish.
                BodySha256 = body is null ? null : Sha256(body),
                TickSize = note is null ? definition!.TickSize : null,
                QuantityIncrement = note is null ? definition!.QuantityIncrement : null,
                MinQuantity = note is null ? definition!.MinQuantity : null,
                MinNotional = note is null ? definition!.MinNotional : null,
                Outcome = note is null ? InstrumentCheckOutcome.Verified : InstrumentCheckOutcome.Failed,
                Note = note
            });
        }
        finally { _one.Release(); }
    }

    /// <summary>
    /// ONE ATTEMPT IN ONE SENTENCE, for the activity log: what was read, or why nothing was.
    /// </summary>
    public static string Describe(InstrumentCheckRow row, string venueName) =>
        row.IsVerified
            ? $"{row.Symbol} verified against {venueName}'s published instrument definition: tick "
              + $"{Plain(row.TickSize)}, step {Plain(row.QuantityIncrement)}"
              + (row.MinQuantity is { } q ? $", minimum quantity {Plain(q)}" : "")
              + (row.MinNotional is { } n ? $", minimum notional {Plain(n)} (recorded, not applied)" : "")
            : $"{row.Symbol} was not verified against {venueName}'s published instrument definition: {row.Note}";

    /// <summary>A definition shape with the symbol in it. The symbol is upper-case letters and digits, checked above.</summary>
    static string Address(string shape, string symbol) => shape.Replace("{symbol}", symbol, StringComparison.Ordinal);

    static string RefusedNote(string url, string? origin, string? expected, string venueName) =>
        $"the definition address {url} is "
        + (origin is null ? "not an http or https address" : $"on {origin}")
        + ", and " + (expected is null
            ? $"TradeAgent holds no built-in definition address for {venueName}"
            : $"{venueName}'s built-in definition address is on {expected}")
        + ", so TradeAgent sent nothing to it. The address an instrument check is made to comes from "
        + "TradeAgent's own built-in row: venues.json may move the path on the venue's own host, never the host.";

    /// <summary>
    /// ONE REQUEST, ON ITS OWN LEASH. Answers the status, the body and — when there was no answer at all —
    /// why, in words. A timeout, a refused socket and a DNS failure are a NULL status with a reason, never a
    /// status this build invented — the reading <c>ForwardBarCollector</c> takes.
    /// </summary>
    async Task<(int? Status, string? Body, string? Failure)> GetAsync(string url, CancellationToken ct)
    {
        using var leash = CancellationTokenSource.CreateLinkedTokenSource(ct);
        leash.CancelAfter(RequestTimeout);

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, leash.Token);
            var status = (int)response.StatusCode;

            if (response.Content.Headers.ContentLength is { } declared && declared > MaxBodyBytes)
                return (status, null, $"the answer declared {declared} bytes, which is not an instrument definition");

            await using var stream = await response.Content.ReadAsStreamAsync(leash.Token);
            var body = await Downloader.ReadLimitedAsync(stream, MaxBodyBytes, leash.Token);

            return body is null
                ? (status, null, $"the answer was longer than {MaxBodyBytes} bytes, which is not an instrument definition")
                : (status, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (null, null, $"the host did not answer within {RequestTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s");
        }
        catch (Exception ex)
        {
            return (null, null, $"the host could not be reached: {ex.Message.ReplaceLineEndings(" ")}");
        }
    }

    static string Sha256(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    static string Plain(decimal? value) =>
        value is { } v ? v.ToString("0.############################", CultureInfo.InvariantCulture) : "none";
}
