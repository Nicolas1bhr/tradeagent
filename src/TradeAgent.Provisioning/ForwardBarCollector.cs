using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Provisioning;

/// <summary>
/// THE ONE THING IN THIS PRODUCT THAT WATCHES AN ADVANCING MARKET: every minute, the closed 1-minute
/// bars of the configured pair, off Binance's market-data-only host, into the forward ledger.
///
/// <para><b>Why it exists.</b> Until this unit the only market data this build held was HISTORY —
/// twelve months of a vendor's archive, fetched once by the owner's press and frozen. A paper run
/// over that is a re-run of last year. <c>docs/PRINCIPLES.md</c>:65 asks for forward paper
/// observation, and forward observation needs a bar that did not exist a minute ago.</para>
///
/// <para><b>It places no order and reaches nothing that could.</b> The host it talks to accepts no
/// authenticated request at all, it holds no credential, and the only thing it writes is the forward
/// ledger. There is no verb and no pipe op that starts, stops or steers it; the owner's toggle on the
/// Settings page is the only control, and it is in-process.</para>
///
/// <para><b>A failing host is a status line, not a crash.</b> Every attempt is a <c>forward_fetch</c>
/// row — succeeded or failed, with the status and the reason in words — and a failure backs the
/// interval off to <see cref="MaxBackoff"/> rather than throwing out of the loop. A collector that
/// died on the vendor's bad afternoon would leave an app reporting fresh data forever, because
/// freshness is measured off the BARS and there would simply be no new ones.</para>
///
/// <para><b>The timer and the clock are injected</b>, so a test drives a day of minutes in
/// milliseconds and nothing in the suite ever waits for a real one. The same seam
/// <c>MissionLoop</c> takes, for the same reason.</para>
/// </summary>
public sealed class ForwardBarCollector : IAsyncDisposable
{
    /// <summary>One bar, one look. The interval this collector runs at when everything is working.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// THE REQUEST'S OWN LEASH, AND IT IS SHORT ON PURPOSE. Ten seconds: this asks for at most a
    /// thousand small rows from a CDN and is run again in sixty, so a request still outstanding after
    /// ten is a request whose answer has been overtaken. The archive's thirty minutes is right for a
    /// two-megabyte zip and would be a collector stuck on one hung socket for half an hour here.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest a failure may push the next look out to. Five minutes, then it stops growing.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>A klines answer is rows of numbers. Anything past this is not one and is not buffered.</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    // ONE CLIENT WITH NO TIMEOUT OF ITS OWN: the leash is a per-request CancellationTokenSource, so
    // the ten seconds is a value a caller chose and a test can shorten, rather than a property of a
    // client that was built once at class load.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    readonly ForwardBarStore _store;
    readonly Func<string?> _symbol;
    readonly Func<bool> _enabled;
    readonly Func<DateTimeOffset> _now;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly CandleSourceEntry _entry;
    readonly string _baseUrl;
    readonly CancellationTokenSource _stopping = new();
    readonly Lock _gate = new();

    Task? _loop;
    int _failures;

    /// <param name="symbol">
    /// The pair to collect, read at every tick and never captured: the owner changes it on the
    /// Settings page while the app is running, and the next look is the one that has to obey.
    /// </param>
    /// <param name="enabled">
    /// Whether the owner's "collect live bars" toggle is on, read at every tick for the same reason.
    /// A tick while it is off costs one boolean and writes nothing — no row, not even a failure,
    /// because "the owner switched it off" is not a fetch that went wrong.
    /// </param>
    /// <param name="baseUrl">
    /// Where to ask. Null means the catalogue row's, which is the vendor's. A test passes its
    /// loopback address here and nothing in this suite has ever passed anything else.
    /// </param>
    public ForwardBarCollector(
        Database db,
        Func<string?> symbol,
        Func<bool>? enabled = null,
        string? baseUrl = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? interval = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CandleSourceEntry? entry = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        _store = new ForwardBarStore(db);
        _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        _enabled = enabled ?? (() => true);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));

        // THE ROW IS THE ENDPOINT. Read once at construction rather than at every tick: an
        // unreadable sources.json is a startup fact the owner is told about, and a collector that
        // re-read it every minute would change host mid-series with nothing in the record saying so.
        _entry = entry ?? CandleSourceCatalog.Read().Sources
            .FirstOrDefault(s => s.Id == ForwardBars.Source)
            ?? throw new TradeAgentException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"'{ForwardBars.Source}' is not in this installation's candle-source catalogue, so "
                + "there is no forward endpoint to ask. Check sources.json in TradeAgent's own folder.");

        _baseUrl = (baseUrl ?? _entry.BaseUrl).TrimEnd('/');
        RequestTimeout = requestTimeout ?? DefaultRequestTimeout;
        Tick = interval ?? Interval;
    }

    /// <inheritdoc cref="DefaultRequestTimeout"/>
    public TimeSpan RequestTimeout { get; }

    /// <summary>How long between looks when nothing is failing. Injected so a test never waits a minute.</summary>
    public TimeSpan Tick { get; }

    /// <summary>The catalogue row this collector is driven by. Data, and the evidence of the endpoint.</summary>
    public CandleSourceEntry Entry => _entry;

    /// <summary>
    /// A BAR CLOSED AND IS NOW IN THE LEDGER. In-process, and the runner to come subscribes to it.
    ///
    /// <para>Raised AFTER the row is committed, once per bar actually stored, and never for a bar
    /// that was already held: a re-fetch of a minute a strategy has already acted on must not make it
    /// act again. A handler that throws is logged and ignored — a subscriber's fault is not a reason
    /// to stop collecting evidence.</para>
    /// </summary>
    public event Action<string, DateTimeOffset>? BarClosed;

    /// <summary>What went wrong at the last look, in words, or null because nothing did.</summary>
    public string? LastError { get; private set; }

    /// <summary>When the last look happened at all, succeeded or failed.</summary>
    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>How many consecutive failures the backoff is currently standing on.</summary>
    public int ConsecutiveFailures => _failures;

    /// <summary>
    /// THE NEXT WAIT, GIVEN <paramref name="failures"/> CONSECUTIVE FAILURES. Doubling from the tick
    /// and capped at <see cref="MaxBackoff"/>.
    ///
    /// <para>Static and public because it is arithmetic, and a backoff that can only be observed by
    /// waiting for it is a backoff nobody checks. Capped rather than unbounded: a vendor that comes
    /// back after an hour must be noticed in the next five minutes, not in the next hour.</para>
    /// </summary>
    public static TimeSpan Backoff(TimeSpan tick, int failures)
    {
        if (failures <= 0) return tick;

        var ticks = tick.Ticks;
        for (var i = 0; i < failures && ticks < MaxBackoff.Ticks; i++) ticks *= 2;
        return TimeSpan.FromTicks(Math.Min(ticks, MaxBackoff.Ticks));
    }

    /// <summary>
    /// Starts the loop. Idempotent: a second call does nothing, so a restart path that calls it twice
    /// cannot leave two loops fetching the same minute.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _stopping.IsCancellationRequested) return;
            _loop = Task.Run(() => RunAsync(_stopping.Token));
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CollectOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // THE LOOP DOES NOT DIE. Anything that reached here is a defect rather than a vendor
                // having a bad afternoon — CollectOnceAsync records those itself — and it is recorded
                // as the last error and backed off exactly the same way.
                _failures++;
                LastError = ex.Message.ReplaceLineEndings(" ");
            }

            try { await _delay(Backoff(Tick, _failures), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// ONE LOOK: ask for everything after the newest bar held, store what has closed, record the
    /// attempt whatever happened.
    ///
    /// <para>Public so the app can force one from the owner's press and a test can drive the whole
    /// path without a timer. It never throws for a vendor failure — that is a row and a status line
    /// — and it answers what it did.</para>
    /// </summary>
    public async Task<ForwardAppend?> CollectOnceAsync(CancellationToken ct = default)
    {
        if (!_enabled()) return null;

        // A PAIR THAT IS NOT ONE IS NOT ASKED FOR, and it is not a failure either: the owner is
        // halfway through typing in the Pair box and the next look will have a real one.
        var configured = _symbol();
        if (!BinanceArchive.IsPair(configured)) return null;
        var symbol = configured!;

        var last = _store.LastOpen(ForwardBars.Source, symbol);
        var url = Url(symbol, last);
        var requestedAt = _now();

        var (status, body, failure) = await GetAsync(url, ct);
        var receivedAt = _now();
        LastAttemptAt = receivedAt;

        string? note = failure;
        IReadOnlyList<ForwardBars.Kline> bars = [];

        if (failure is null && status != 200)
            note = $"the host answered {status} and no bars were read";
        else if (failure is null && !ForwardBars.TryParse(body, out bars, out var why))
        {
            // A BODY THAT DOES NOT PARSE IS A RECORDED FAILURE AND NEVER A BAR. Not "the rows it
            // could read": an error page, a truncated answer or a shape this build does not know is
            // a fetch that told us nothing, and reporting the readable part of it as a healthy
            // minute is how a collector comes to claim coverage it does not have.
            note = $"the answer could not be read as klines: {why}";
            bars = [];
        }

        var result = _store.Append(new ForwardFetchAttempt
        {
            Source = ForwardBars.Source,
            Symbol = symbol,
            Url = url,
            RequestedAt = requestedAt,
            ReceivedAt = receivedAt,
            HttpStatus = status,
            // THIS BUILD'S OWN HASH OF THE BODY, AND IT IS NEVER A VENDOR'S. There is no published
            // one to be: it proves the answer has not changed since it was recorded, which is a
            // different claim from proving it is what the vendor meant to publish. The two are kept
            // apart here exactly as `dataset_file.published_sha256` keeps them apart.
            BodySha256 = body is null ? null : Sha256(body),
            Note = note
        }, bars);

        if (note is null) { _failures = 0; LastError = null; }
        else { _failures++; LastError = note; }

        // AFTER THE COMMIT, AND ONLY FOR A BAR THIS CALL ACTUALLY STORED. A re-fetch of a minute a
        // strategy has already acted on must not make it act again, which is why this is driven by
        // the rows that went in rather than by the rows that arrived.
        if (result.Stored > 0)
            foreach (var bar in _store.Since(symbol, last, result.Stored))
                Raise(symbol, bar.OpenTime);

        return result;
    }

    void Raise(string symbol, DateTimeOffset openTime)
    {
        try { BarClosed?.Invoke(symbol, openTime); }
        catch (Exception ex)
        {
            // A SUBSCRIBER'S FAULT IS NOT A REASON TO STOP COLLECTING. The bar is committed; what a
            // listener did with the news is the listener's problem and is recorded as this
            // collector's last error so it is visible rather than silent.
            LastError = $"a subscriber to BarClosed threw: {ex.Message.ReplaceLineEndings(" ")}";
        }
    }

    /// <summary>
    /// THE URL FOR THE NEXT WINDOW, BUILT FROM THE CATALOGUE ROW'S SHAPE and never from a literal
    /// here. <paramref name="lastOpen"/> null asks for the most recent bars there are, which is what
    /// a first run wants.
    ///
    /// <para><c>startTime</c> is the newest held bar's open PLUS one bar, in milliseconds, which is
    /// the first minute this installation does not have. Asking from the newest held bar instead
    /// would re-fetch a minute already stored on every single tick — harmless, because the first
    /// reading stands, and a thousand pointless disagreement checks an hour.</para>
    /// </summary>
    public string Url(string symbol, DateTimeOffset? lastOpen)
    {
        var url = _entry.UrlShape
            .Replace("{base}", _baseUrl, StringComparison.Ordinal)
            .Replace("{symbol}", BinanceArchive.RequirePair(symbol), StringComparison.Ordinal)
            .Replace("{interval}", _entry.Interval, StringComparison.Ordinal)
            .Replace("{limit}", ForwardBars.RequestLimit.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        if (lastOpen is { } open)
            return url.Replace("{from}",
                (open + ForwardBars.BarLength).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        // NO START TIME AT ALL on a first run: the parameter is removed rather than sent empty,
        // because a vendor reading an empty startTime as zero would answer with 2017.
        return RemoveEmptyFrom(url);
    }

    static string RemoveEmptyFrom(string url)
    {
        foreach (var shape in (string[])["&startTime={from}", "?startTime={from}&", "&from={from}", "?from={from}&"])
            if (url.Contains(shape, StringComparison.Ordinal))
                return url.Replace(shape, shape.StartsWith('?') ? "?" : "", StringComparison.Ordinal);

        return url.Replace("{from}", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE REQUEST, ON ITS OWN LEASH. Answers the status, the body and — when there was no answer at
    /// all — why, in words.
    ///
    /// <para>A timeout, a refused socket and a DNS failure come back as a NULL status with a reason,
    /// never as a status this build invented: "the vendor said nothing" and "the vendor said 503" are
    /// different facts about the vendor and the ledger keeps them apart.</para>
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
                return (status, null, $"the answer declared {declared} bytes, which is not a klines answer");

            await using var stream = await response.Content.ReadAsStreamAsync(leash.Token);
            var body = await Downloader.ReadLimitedAsync(stream, MaxBodyBytes, leash.Token);

            return body is null
                ? (status, null, $"the answer was longer than {MaxBodyBytes} bytes, which is not a klines answer")
                : (status, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (null, null, $"the host did not answer within {RequestTimeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex)
        {
            return (null, null, $"the host could not be reached: {ex.Message.ReplaceLineEndings(" ")}");
        }
    }

    static string Sha256(string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    /// <summary>Stops the loop and waits for the look in flight. Safe to call twice.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate) { loop = _loop; _loop = null; }

        try { await _stopping.CancelAsync(); } catch (Exception) { /* already gone */ }
        if (loop is not null)
        {
            try { await loop; } catch (Exception) { /* a stopping loop's last fault is not news */ }
        }

        _stopping.Dispose();
    }
}
