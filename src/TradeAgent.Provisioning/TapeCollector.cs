using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Provisioning;

/// <summary>What one look at one row did. <see cref="Worked"/> decides whether the next look backs off.</summary>
public sealed record TapeTick(bool Off, int Attempts, int Delivered, int Stored, bool Throttled)
{
    /// <summary>The owner has the recording switched off: nothing was asked and nothing written.</summary>
    public static TapeTick SwitchedOff { get; } = new(true, 0, 0, 0, false);

    /// <summary>
    /// Whether this look counts as working: switched off, or at least one attempt delivered and no answer
    /// told TradeAgent to slow down. One symbol the vendor refuses is a row with its reason — it does not
    /// push the other five symbols' looks out to five minutes; a host that answers nothing, or says
    /// "too many requests", does.
    /// </summary>
    public bool Worked => Off || (Delivered > 0 && !Throttled);
}

/// <summary>
/// THE TAPE'S COLLECTOR: while the app runs, the market's context — Binance USDⓈ-M premium index with
/// the live funding rate, open interest, the 5-minute long/short and taker ratios, settled funding — for
/// six symbols, into <c>state/tape.db</c> through <see cref="TapeStore"/> (<c>U-tape-store</c>), and OKX's
/// announcements for EU users, its first page once a minute (<c>U-tape-events</c>). Every announcement URL
/// it records is data: none is ever fetched.
///
/// <para><b>It places no order, holds no credential and reaches nothing that could.</b> Every request
/// is an unauthenticated GET of a public endpoint; there is nothing to send a key with and no key to
/// send. It writes only the tape. There is no verb and no pipe op that starts, stops, steers or writes
/// it: the owner's one-press toggle on the Settings page — "Record market context" — is the only
/// control, read at every look, and it is in-process.</para>
///
/// <para><b>One loop per catalogue row</b>, each on its own cadence: the next look on the working path
/// is at the next multiple of the row's cadence plus <see cref="ForwardBars.LookOffset"/>, through the
/// one alignment helper the forward collector also looks on (<see cref="TickAlignment"/>), so a
/// vendor's new point is asked for two seconds after it can exist. A failing row backs off on a
/// doubling wait that stops at <see cref="MaxBackoff"/> — a row whose cadence is longer than that
/// retries every five minutes while it fails — and is back on its cadence at the first look that
/// works. Every attempt is a <c>tape_fetch</c> row, succeeded or failed; a failure is a status line,
/// never a crash.</para>
///
/// <para><b>No first-start backfill.</b> The first look asks for exactly what every look asks for — the
/// latest point, or the latest few of a 5-minute or funding series as an overlap. History is
/// <c>U-tape-archive</c>'s, from the vendor's checksummed archive, where a class better than
/// <c>O-ARCH</c> can be earned.</para>
///
/// <para><b>A redirect is not followed.</b> The origin a fetch row records is the origin that answered
/// it, and it is what <c>O-LIVE</c> is decided against; a client that followed a 3xx elsewhere would
/// record one address and store another's bytes. A redirect is the status it is, with no items.</para>
///
/// <para><b>The timer and the clock are injected</b>, so a test drives hours of looks in milliseconds
/// and nothing in the suite waits for a real one — the seam <see cref="ForwardBarCollector"/> takes.</para>
/// </summary>
public sealed class TapeCollector : IAsyncDisposable
{
    /// <summary>
    /// THE REQUEST'S OWN LEASH: ten seconds, for the reason the forward collector's is ten — a small
    /// public answer still outstanding after ten seconds has been overtaken by the next look.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest a failure may push the next look out to — the forward collector's five minutes.</summary>
    public static TimeSpan MaxBackoff => ForwardBarCollector.MaxBackoff;

    /// <summary>An answer here is a few hundred small objects at most. Anything past this is not one and is not buffered.</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    // ONE CLIENT, NO TIMEOUT OF ITS OWN (the leash is per request) AND NO REDIRECTS: see the type summary.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    static readonly string?[] AllSymbols = [null];

    readonly TapeStore _store;
    readonly Func<bool> _enabled;
    readonly Func<DateTimeOffset> _now;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly string? _baseUrl;
    readonly CancellationTokenSource _stopping = new();
    readonly Lock _gate = new();
    readonly ConcurrentDictionary<string, RowState> _rows = new(StringComparer.Ordinal);

    Task[]? _loops;

    /// <param name="store">The tape. This collector writes it and does not own it: the app disposes it after this.</param>
    /// <param name="enabled">
    /// Whether the owner's "Record market context" toggle is on, read at every look and never captured.
    /// Off, a look costs one boolean and writes NOTHING — not even a failed attempt, because "the owner
    /// switched it off" is not a fetch that went wrong.
    /// </param>
    /// <param name="baseUrl">
    /// Where to ask instead of every row's own host. Null means each row's, which is the vendor's. A test
    /// passes its loopback address here, and everything it records is then <c>O-ARCH</c> — the origin is
    /// not the built-in one.
    /// </param>
    /// <param name="catalog">The rows to record. Null reads <see cref="TapeSourceCatalog.Read"/> once, here.</param>
    public TapeCollector(
        TapeStore store,
        Func<bool>? enabled = null,
        string? baseUrl = null,
        TimeSpan? requestTimeout = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TapeSourceCatalogRead? catalog = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _enabled = enabled ?? (() => true);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        _baseUrl = baseUrl?.TrimEnd('/');
        RequestTimeout = requestTimeout ?? DefaultRequestTimeout;

        // THE CATALOGUE IS READ ONCE, AT CONSTRUCTION: a collector that re-read the file every look would
        // change what it records mid-series with nothing in the record saying when.
        var read = catalog ?? TapeSourceCatalog.Read();

        // A ROW WITH NO CADENCE HAS NO NEXT LOOK. The catalogue never yields one — its own rows are fixed
        // and a file's are held to a minute or more — so this is a caller's mistake, refused here in words
        // rather than discovered as a loop that died on its first wait.
        if (read.Sources.FirstOrDefault(r => r.Cadence <= TimeSpan.Zero) is { } still)
            throw new ArgumentException($"tape row '{still.Id}' has a cadence of {still.CadenceSeconds} s, which is not a cadence", nameof(catalog));

        // A ROW WHOSE PARSER THIS BUILD DOES NOT HAVE CANNOT BE READ, so it is refused here in words for the
        // same reason: the catalogue's own rows are fixed and a file's parser is checked, so only a caller
        // can hand one over — and a look that guessed at a parser would record a guess as a delivery.
        if (read.Sources.FirstOrDefault(r => r.Parser is not (TapeSourceCatalog.JsonParser or TapeSourceCatalog.AnnouncementParser)) is { } unread)
            throw new ArgumentException($"tape row '{unread.Id}' names the parser '{unread.Parser}', which this build does not have", nameof(catalog));

        Rows = read.Sources;
        CatalogProblem = read.Unreadable;
        Refused = read.Refused;
    }

    /// <summary>The rows this collector records: the built-ins and whatever valid rows the file added.</summary>
    public IReadOnlyList<TapeSourceEntry> Rows { get; }

    /// <summary>Why <c>tape-sources.json</c> could not be read, or null. The app writes it as an activity line.</summary>
    public string? CatalogProblem { get; }

    /// <summary>The file's rows that are not recorded, each with its reason. The app writes each as an activity line.</summary>
    public IReadOnlyList<string> Refused { get; }

    /// <inheritdoc cref="DefaultRequestTimeout"/>
    public TimeSpan RequestTimeout { get; }

    /// <summary>How many looks in a row this row's loop has failed, which is what its backoff stands on.</summary>
    public int ConsecutiveFailures(string rowId) => Row(rowId).Failures;

    /// <summary>What went wrong at this row's last look, in words, or null because nothing did.</summary>
    public string? LastError(string rowId) => Row(rowId).LastError;

    /// <summary>
    /// Starts one loop per row. Idempotent: a second call does nothing, so a restart path that calls it
    /// twice cannot have two loops asking the same question.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loops is not null || _stopping.IsCancellationRequested) return;
            var ct = _stopping.Token;
            _loops = [.. Rows.Select(row => Task.Run(() => RunAsync(row, ct)))];
        }
    }

    async Task RunAsync(TapeSourceEntry row, CancellationToken ct)
    {
        var state = Row(row.Id);

        while (!ct.IsCancellationRequested)
        {
            bool worked;
            try { worked = (await CollectOnceAsync(row, ct)).Worked; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // THE LOOP DOES NOT DIE. A vendor's bad afternoon is recorded by the look itself; anything
                // that reached here is a defect, and it is the last error and backed off the same way.
                worked = false;
                state.LastError = ex.Message.ReplaceLineEndings(" ");
            }

            // WORKING: the next cadence boundary plus the forward collector's two seconds, through the one
            // shared helper. FAILING: the forward collector's doubling backoff, capped at five minutes.
            state.Failures = worked ? 0 : state.Failures + 1;
            var wait = state.Failures == 0
                ? TickAlignment.WaitForNextLook(_now(), row.Cadence, ForwardBars.LookOffset)
                : ForwardBarCollector.Backoff(row.Cadence, state.Failures);

            try { await _delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// ONE LOOK AT ONE ROW: every series of it, once per symbol or once for all, each attempt a row in the
    /// tape whatever happened. Public so a test can drive the whole path without a timer. It never throws
    /// for a vendor failure — that is a row and a status line — and it answers what it did.
    ///
    /// <para>An answer of 429 or 418 ends the look at once and counts it as failed: that is the vendor
    /// saying slow down, and the requests still to come in this look would be the ones it bans for.</para>
    /// </summary>
    public async Task<TapeTick> CollectOnceAsync(TapeSourceEntry row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!_enabled()) return TapeTick.SwitchedOff;

        var state = Row(row.Id);
        var root = (_baseUrl ?? row.BaseUrl).TrimEnd('/');
        IEnumerable<string?> symbols = row.PerSymbol ? TapeSourceCatalog.Universe.Cast<string?>() : AllSymbols;

        int attempts = 0, delivered = 0, stored = 0;
        var throttled = false;
        string? problem = null;

        foreach (var series in row.Series)
        {
            foreach (var symbol in symbols)
            {
                var url = Url(series, root, symbol);
                var requestedAt = _now();
                var (status, body, failure) = await GetAsync(url, ct);
                var receivedAt = _now();
                attempts++;

                var note = failure;
                IReadOnlyList<TapeItem> items = [];
                if (note is null && status != 200)
                    note = $"the host answered {status} and nothing was read";
                else if (note is null && !Read(row, series, body, symbol, out items, out var why))
                {
                    note = $"the answer could not be read: {why}";
                    items = [];
                }

                var appended = _store.Append(new TapeFetch
                {
                    Source = row.Id,
                    Series = series.Id,
                    Url = url,
                    RequestedAt = requestedAt,
                    ReceivedAt = receivedAt,
                    HttpStatus = status,
                    // THIS BUILD'S OWN HASH OF THE BODY, NEVER A VENDOR'S: there is none for a live answer.
                    BodySha256 = body is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
                    Note = note
                }, items);

                if (note is null)
                {
                    delivered++;
                    stored += appended.Stored;
                }
                else problem ??= $"{series.Id}{(symbol is null ? "" : " " + symbol)}: {note}";

                if (status is 429 or 418) { throttled = true; break; }
            }

            if (throttled) break;
        }

        state.LastError = problem;
        return new TapeTick(false, attempts, delivered, stored, throttled);
    }

    /// <summary>
    /// THE ROW'S OWN PARSER, NEVER A GUESS AT ONE: the market family reads the body as its items and keeps
    /// them to the universe; the announcement family reads the list at the series' path. The constructor
    /// refused every other name, so there is no third arm for a row to fall into.
    /// </summary>
    static bool Read(TapeSourceEntry row, TapeSeriesEntry series, string? body, string? symbol,
        out IReadOnlyList<TapeItem> items, out string? why) =>
        row.Parser == TapeSourceCatalog.AnnouncementParser
            ? TapeParse.TryReadItems(body, series, out items, out why)
            : TapeParse.TryRead(body, series, symbol, TapeSourceCatalog.Universe, out items, out why);

    /// <summary>The URL for one series, built from the row's shape and never from a literal here.</summary>
    public static string Url(TapeSeriesEntry series, string root, string? symbol) =>
        series.UrlShape
            .Replace("{base}", root, StringComparison.Ordinal)
            .Replace("{symbol}", symbol ?? "", StringComparison.Ordinal);

    /// <summary>
    /// ONE REQUEST, ON ITS OWN LEASH: the status, the body, and — when nothing was answered — why, in
    /// words. A timeout or a refused socket is a NULL status with a reason, never a status this build
    /// invented: "the vendor said nothing" and "the vendor said 503" are different facts.
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
                return (status, null, $"the answer declared {declared} bytes, which is not a market-context answer");

            await using var stream = await response.Content.ReadAsStreamAsync(leash.Token);
            var body = await Downloader.ReadLimitedAsync(stream, MaxBodyBytes, leash.Token);

            return body is null
                ? (status, null, $"the answer was longer than {MaxBodyBytes} bytes, which is not a market-context answer")
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

    RowState Row(string id) => _rows.GetOrAdd(id, _ => new RowState());

    sealed class RowState
    {
        volatile string? _lastError;
        int _failures;

        public string? LastError { get => _lastError; set => _lastError = value; }
        public int Failures { get => Volatile.Read(ref _failures); set => Volatile.Write(ref _failures, value); }
    }

    /// <summary>Stops every loop and waits for the looks in flight. Safe to call twice.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[]? loops;
        lock (_gate) { loops = _loops; _loops = null; }

        try { await _stopping.CancelAsync(); } catch (Exception) { /* already gone */ }
        if (loops is not null)
        {
            try { await Task.WhenAll(loops); } catch (Exception) { /* a stopping loop's last fault is not news */ }
        }

        _stopping.Dispose();
    }
}
