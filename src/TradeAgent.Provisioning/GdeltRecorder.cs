using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Provisioning;

/// <summary>What one look at GDELT's newest file did. <see cref="Worked"/> decides whether the next look backs off.</summary>
public sealed record GdeltLook(bool Off, int Attempts, int Stored, bool Throttled, string? Failure)
{
    /// <summary>The owner has "Record GDELT news" switched off: nothing was asked and nothing written.</summary>
    public static GdeltLook SwitchedOff { get; } = new(true, 0, 0, false, null);

    /// <summary>Switched off, nothing new to read, or a file read; a failure backs the next look off.</summary>
    public bool Worked => Off || Failure is null;
}

/// <summary>What one backfill pass did: the files it planned, asked for, recorded, found unpublished or failed on, and why it stopped early, if it did.</summary>
public sealed record GdeltBackfill(bool Off, int Planned, int Asked, int Recorded, int NotPublished, int Failed, long Bytes, string? Stopped)
{
    /// <summary>The owner has "Record GDELT news" switched off: nothing was asked and nothing written.</summary>
    public static GdeltBackfill SwitchedOff { get; } = new(true, 0, 0, 0, 0, 0, 0, null);
}

/// <summary>
/// GDELT'S NEWS ITEMS ABOUT CRYPTO, RECORDED INTO THE TAPE WITH GDELT'S OWN FIRST-SEEN TIME (<c>U-tape-archive</c>;
/// <c>docs/EDGE-FACTORY.md</c> § 4.1): every fifteen minutes the newest GKG file, and at a start the files missed
/// while TradeAgent was not running — each streamed once through MD5, SHA-256 and <see cref="GdeltGkg"/>'s filter,
/// and appended only once its MD5 matches the one GDELT published.
///
/// <para><b>It places no order, holds no credential and reaches nothing that could.</b> Every request is an
/// unauthenticated GET of GDELT's public data host. It writes only the tape, through <see cref="TapeStore.AppendArchive"/>
/// and <see cref="TapeStore.Append"/>. There is no verb and no pipe op that starts, stops, steers or writes it: the
/// owner's one-press "Record GDELT news" on the Market data card is the only control, read before every request,
/// and it is in-process. Off, it asks nothing and writes nothing.</para>
///
/// <para><b>The live look</b> is at each label plus <see cref="ForwardBars.LookOffset"/>: it reads
/// <c>lastupdate.txt</c> for the newest GKG file's label, size and MD5 (<see cref="GdeltGkg.TryReadListing"/>) and asks
/// for that file — at an address built from the label on this recorder's own origin, never at the one the listing
/// names. A file already recorded is not asked for again. A failing look backs off on the tape collector's doubling
/// wait, which stops at five minutes.</para>
///
/// <para><b>The backfill</b> runs once a start, on its own task, after the first look: newest first, at most
/// <see cref="BackfillBatches"/> files the tape holds no record of, none labelled more than <see cref="BackfillWindow"/>
/// ago, at most <see cref="BackfillBytesPerDay"/> asked in a UTC day — counted from the tape, so a restart does not
/// reset it — one request at a time, at least <see cref="BackfillSpacing"/> apart and on the same doubling wait after a
/// failure. Its published MD5 is the storage's own (<c>x-goog-hash</c>), measured equal to the listing's.</para>
///
/// <para><b>The daily cap.</b> The crypto rows of one UTC day's files — by their labels — take at most
/// <see cref="DailyCapBytes"/> of the owner's disk. A file whose kept rows would pass it is not stored, and no more of
/// that day's files are asked for while TradeAgent runs; the recorder says so in words. Measured on 2026-10-06: about
/// 6.9 MB a day (<c>docs/RESEARCH-REQUIRED.md</c>, C5e).</para>
///
/// <para><b>No raw bytes are kept.</b> A GKG file is never written to disk: it is read once off the socket, and what
/// the tape keeps of it is its record — address, size, both MD5s, SHA-256, Last-Modified, rows, what was kept — and its
/// crypto rows. GDELT keeps serving the file, so the record stands as proof of what was read without the bytes.</para>
///
/// <para><b>A redirect is not followed and nothing is decompressed on the way</b>: the origin a fetch records is the one
/// that answered, and the hashes are of the bytes GDELT serves. A 404 is "not published", and nothing else is.</para>
/// </summary>
public sealed class GdeltRecorder : IAsyncDisposable
{
    /// <summary>A request's own leash: a GKG file of up to 16 MB, measured at under two seconds, is given two minutes.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The most of one GKG file that is read. Measured on 2026-10-06: 2.1-6.7 MB a file.</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>The most of <c>lastupdate.txt</c> that is read. Measured on 2026-10-06: 319 bytes.</summary>
    public const int MaxListingBytes = 4 * 1024;

    /// <summary>The most the crypto rows of one UTC day's files may take: 25 MB.</summary>
    public const long DailyCapBytes = 25L * 1024 * 1024;

    /// <summary>The most files one start's backfill asks for: a day of them.</summary>
    public const int BackfillBatches = 96;

    /// <summary>How far back the backfill reaches: a file labelled earlier than this is never asked for.</summary>
    public static readonly TimeSpan BackfillWindow = TimeSpan.FromDays(7);

    /// <summary>The most the backfill asks for in one UTC day: 500 MB.</summary>
    public const long BackfillBytesPerDay = 500L * 1024 * 1024;

    /// <summary>The least time between two of the backfill's requests.</summary>
    public static readonly TimeSpan BackfillSpacing = TimeSpan.FromSeconds(2);

    /// <summary>The series of a fetch of <c>lastupdate.txt</c>.</summary>
    public const string ListingSeries = "lastupdate";

    /// <summary>The series of a live look's fetch of a GKG file.</summary>
    public const string LiveSeries = "gkg-live";

    /// <summary>The series of a backfill's fetch of a GKG file — counted against <see cref="BackfillBytesPerDay"/>.</summary>
    public const string BackfillSeries = "gkg-backfill";

    // ONE CLIENT: NO TIMEOUT OF ITS OWN (the leash is per request), NO REDIRECTS AND NO DECOMPRESSION — see the summary.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    readonly TapeStore _store;
    readonly Func<bool> _enabled;
    readonly Func<DateTimeOffset> _now;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly Action<string> _say;
    readonly SemaphoreSlim _oneAtATime = new(1, 1);
    readonly CancellationTokenSource _stopping = new();
    readonly TaskCompletionSource _firstLook = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _gate = new();
    readonly Dictionary<DateOnly, long> _dayBytes = [];
    readonly HashSet<DateOnly> _closed = [];

    Task[]? _tasks;
    volatile string? _lastError;
    int _failures;

    /// <param name="store">The tape. This recorder writes it and does not own it: the app disposes it after this.</param>
    /// <param name="enabled">
    /// Whether the owner's "Record GDELT news" is on, read before every request and never captured. Off, a look costs one
    /// boolean and writes NOTHING — not even a failed attempt.
    /// </param>
    /// <param name="baseUrl">
    /// Where to ask instead of GDELT's own data host. Null is GDELT's. A test passes its loopback address here, and
    /// everything it records is then <c>O-ARCH</c> — the origin is not the built-in one.
    /// </param>
    /// <param name="dailyCap">The daily cap in bytes. Null is <see cref="DailyCapBytes"/>; a test passes a small one.</param>
    /// <param name="say">Where the recorder says what the owner should hear — the day's cap reached, the backfill's bound. The app passes its activity log.</param>
    public GdeltRecorder(
        TapeStore store,
        Func<bool>? enabled = null,
        string? baseUrl = null,
        TimeSpan? requestTimeout = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        long? dailyCap = null,
        Action<string>? say = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _enabled = enabled ?? (() => true);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        _say = say ?? (_ => { });
        Root = (baseUrl ?? GdeltGkg.BaseUrl).TrimEnd('/');
        RequestTimeout = requestTimeout ?? DefaultRequestTimeout;
        DailyCap = dailyCap ?? DailyCapBytes;
    }

    /// <summary>The origin every request is built on: GDELT's own data host unless a test said otherwise.</summary>
    public string Root { get; }

    /// <inheritdoc cref="DefaultRequestTimeout"/>
    public TimeSpan RequestTimeout { get; }

    /// <inheritdoc cref="DailyCapBytes"/>
    public long DailyCap { get; }

    /// <summary>What went wrong at the last look, in words, or null because nothing did. The day's cap and the backfill's bound are said through <c>say</c>.</summary>
    public string? LastError => _lastError;

    /// <summary>How many looks in a row have failed, which is what the live backoff stands on.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _failures);

    /// <summary>
    /// Starts the live loop, and the backfill on its own task after the first look. Idempotent: a second call does
    /// nothing, so a restart path that calls it twice cannot have two loops asking the same question.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_tasks is not null || _stopping.IsCancellationRequested) return;
            var ct = _stopping.Token;
            _tasks = [Task.Run(() => LiveAsync(ct)), Task.Run(() => BackfillAsync(ct))];
        }
    }

    async Task LiveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool worked;
            try { worked = (await LookOnceAsync(ct)).Worked; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // THE LOOP DOES NOT DIE. A vendor's bad afternoon is recorded by the look itself; anything that
                // reached here is a defect, and it is the last note and backed off the same way.
                worked = false;
                _lastError = ex.Message.ReplaceLineEndings(" ");
            }
            finally { _firstLook.TrySetResult(); }

            Volatile.Write(ref _failures, worked ? 0 : _failures + 1);
            var wait = _failures == 0
                ? TickAlignment.WaitForNextLook(_now(), GdeltGkg.Cadence, ForwardBars.LookOffset)
                : ForwardBarCollector.Backoff(GdeltGkg.Cadence, _failures);

            try { await _delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task BackfillAsync(CancellationToken ct)
    {
        try
        {
            await _firstLook.Task.WaitAsync(ct);

            // ONCE A START, AS SOON AS THE SWITCH IS ON: switched off, the pass asks nothing, and is tried again at the
            // next quarter hour while TradeAgent runs.
            while (!ct.IsCancellationRequested)
            {
                if (!(await BackfillOnceAsync(ct)).Off) return;
                await _delay(GdeltGkg.Cadence, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Say("the backfill stopped: " + ex.Message.ReplaceLineEndings(" ")); }
    }

    /// <summary>
    /// ONE LIVE LOOK: GDELT's listing, then the newest GKG file it names unless the tape already holds it or its day's
    /// cap is reached. Public so a test can drive the whole path without a timer. It never throws for a vendor failure
    /// — that is a row and a note — and it answers what it did.
    /// </summary>
    public async Task<GdeltLook> LookOnceAsync(CancellationToken ct = default)
    {
        if (!_enabled()) return GdeltLook.SwitchedOff;

        await _oneAtATime.WaitAsync(ct);
        try
        {
            var url = GdeltGkg.ListingUrl(Root);
            var requestedAt = _now();
            var (status, body, failure) = await GetListingAsync(url, ct);
            var receivedAt = _now();

            GkgListing? listing = null;
            var note = failure
                       ?? (status != 200 ? $"GDELT answered {status} and nothing was read"
                           : GdeltGkg.TryReadListing(body, out listing, out var why) ? null
                           : "the listing could not be read: " + why);

            _store.Append(new TapeFetch
            {
                Source = GdeltGkg.Source,
                Series = ListingSeries,
                Url = url,
                RequestedAt = requestedAt,
                ReceivedAt = receivedAt,
                HttpStatus = status,
                BodySha256 = body is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
                Note = note
            });

            if (note is not null) return Ended(new GdeltLook(false, 1, 0, status is 429, note));

            var label = listing!.Label;
            if (Holds(label) || IsClosed(label)) return Ended(new GdeltLook(false, 1, 0, false, null));

            if (listing.Size > MaxFileBytes)
                return Ended(new GdeltLook(false, 1, 0, false,
                    $"the listing gives the file for {GdeltGkg.LabelText(label)} as {listing.Size} bytes, past the {MaxFileBytes} TradeAgent reads of one"));

            // SWITCHED OFF BETWEEN THE TWO REQUESTS: the file is not asked for.
            if (!_enabled()) return GdeltLook.SwitchedOff;

            var file = await ReadFileAsync(label, listing.Md5, LiveSeries, ct);
            return Ended(new GdeltLook(false, 2, file.Stored, file.Throttled,
                file.Result is FileResult.Failed or FileResult.NotPublished ? file.Note : null));
        }
        finally { _oneAtATime.Release(); }
    }

    /// <summary>
    /// ONE BACKFILL PASS — see the type summary for its bounds. Public so a test can drive it without a timer.
    /// </summary>
    public async Task<GdeltBackfill> BackfillOnceAsync(CancellationToken ct = default)
    {
        if (!_enabled()) return GdeltBackfill.SwitchedOff;

        var now = _now();
        var plan = BackfillPlan(now, _store.SourceTimes(GdeltGkg.Source, GdeltGkg.BatchSeries, GdeltGkg.BatchSubject, now - BackfillWindow));
        var spent = BackfillSpentOn(now);

        int asked = 0, recorded = 0, notPublished = 0, failed = 0, failures = 0;
        long bytes = 0;
        string? stopped = null;

        foreach (var label in plan)
        {
            if (!_enabled()) { stopped = "switched off"; break; }
            if (spent + bytes >= BackfillBytesPerDay)
            {
                stopped = $"the backfill asked for {spent + bytes} bytes today, and it asks for at most {BackfillBytesPerDay} a UTC day; "
                          + "the files it did not reach are asked for at a later start, while they are under seven days old";
                Say(stopped);
                break;
            }
            if (IsClosed(label)) continue;

            await _delay(failures == 0 ? BackfillSpacing : ForwardBarCollector.Backoff(BackfillSpacing, failures), ct);

            await _oneAtATime.WaitAsync(ct);
            try
            {
                // RESUMED FROM THE TAPE, AT EVERY FILE: the live look may have recorded it while this one waited.
                if (Holds(label)) continue;
                if (!_enabled()) { stopped = "switched off"; break; }

                var file = await ReadFileAsync(label, null, BackfillSeries, ct);
                asked++;
                bytes += file.Bytes;
                switch (file.Result)
                {
                    case FileResult.Recorded: recorded++; failures = 0; break;
                    case FileResult.NotPublished: notPublished++; failures = 0; break;
                    case FileResult.Capped: failures = 0; break;
                    default: failed++; failures++; break;
                }
            }
            finally { _oneAtATime.Release(); }
        }

        return new GdeltBackfill(false, plan.Count, asked, recorded, notPublished, failed, bytes, stopped);
    }

    /// <summary>
    /// THE FILES ONE BACKFILL PASS ASKS FOR, newest first: every label from the newest at or before
    /// <paramref name="now"/> back to <see cref="BackfillWindow"/> ago that <paramref name="held"/> does not hold, at most
    /// <see cref="BackfillBatches"/> of them. A file older than the window is never asked for.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> BackfillPlan(DateTimeOffset now, IReadOnlySet<DateTimeOffset> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        var oldest = now - BackfillWindow;
        var plan = new List<DateTimeOffset>(BackfillBatches);
        for (var label = GdeltGkg.LabelAtOrBefore(now); label >= oldest && plan.Count < BackfillBatches; label -= GdeltGkg.Cadence)
            if (!held.Contains(label)) plan.Add(label);
        return plan;
    }

    /// <summary>
    /// ONE GKG FILE BY ITS LABEL, with the MD5 its storage publishes — the backfill's step, public so a test can read a
    /// file twice. It does not ask whether the tape already holds the file: the second reading of one writes its attempt
    /// and nothing else.
    /// </summary>
    public async Task<string?> CollectFileAsync(DateTimeOffset label, CancellationToken ct = default)
    {
        if (!_enabled()) return "switched off";
        await _oneAtATime.WaitAsync(ct);
        try { return (await ReadFileAsync(label, null, BackfillSeries, ct)).Note; }
        finally { _oneAtATime.Release(); }
    }

    enum FileResult { Recorded, NotPublished, Capped, Failed }

    readonly record struct FileOutcome(FileResult Result, long Bytes, int Stored, bool Throttled, string? Note);

    /// <summary>
    /// ONE GKG FILE: asked for at the address its label builds on this recorder's origin, streamed through
    /// <see cref="GdeltGkg.ReadBatchAsync"/>, and appended — the attempt, the file's record and its kept rows in one
    /// transaction — only once the MD5 this build computed equals the one GDELT published: the listing's on a live look,
    /// the storage's own on the backfill. Anything else is the attempt and nothing more.
    /// </summary>
    async Task<FileOutcome> ReadFileAsync(DateTimeOffset label, string? listedMd5, string series, CancellationToken ct)
    {
        var url = GdeltGkg.BatchUrl(Root, label);
        var day = DateOnly.FromDateTime(label.UtcDateTime);
        var budget = Math.Max(0, DailyCap - DayBytes(day));

        int? status = null;
        GkgBatchRead? read = null;
        DateTimeOffset? lastModified = null;
        string? storageMd5 = null;
        string? note = null;

        var requestedAt = _now();
        using (var leash = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            leash.CancelAfter(RequestTimeout);
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, leash.Token);
                status = (int)response.StatusCode;

                if (status == 404) note = $"GDELT has not published the file for {GdeltGkg.LabelText(label)}";
                else if (status != 200) note = $"GDELT answered {status} and nothing was read";
                else if (response.Content.Headers.ContentLength is { } declared && declared > MaxFileBytes)
                    note = $"the file declared {declared} bytes, past the {MaxFileBytes} TradeAgent reads of one GKG file";
                else
                {
                    lastModified = response.Content.Headers.LastModified;
                    storageMd5 = StorageMd5(response);
                    await using var body = await response.Content.ReadAsStreamAsync(leash.Token);
                    read = await GdeltGkg.ReadBatchAsync(body, label, MaxFileBytes, budget, leash.Token);
                    if (read.Refused is { } why) note = read.OverBudget ? GdeltGkg.CapNotePrefix + why : "the file was not read: " + why;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { note = $"GDELT did not answer within {RequestTimeout.TotalSeconds:0.#} s"; }
            catch (Exception ex) { note = "GDELT could not be reached: " + ex.Message.ReplaceLineEndings(" "); }
        }
        var receivedAt = _now();

        // THE MD5 DECIDES, AND NOTHING IS WRITTEN BEFORE IT HAS: a file whose bytes are not the ones GDELT published —
        // a rebuild between the listing and the request, a proxy's page, a broken transfer — keeps its attempt and
        // nothing else, whatever its rows looked like.
        var published = listedMd5 ?? storageMd5;
        if (note is null && published is null)
            note = "GDELT's storage gave no MD5 for the file, so nothing in it can be checked and none of it is kept";
        if (note is null && !string.Equals(published, read!.Md5, StringComparison.OrdinalIgnoreCase))
            note = $"the file's MD5 is {read.Md5} and GDELT published {published}, so it is not the file GDELT listed and none of it is kept";

        var fetch = new TapeFetch
        {
            Source = GdeltGkg.Source,
            Series = series,
            Url = url,
            RequestedAt = requestedAt,
            ReceivedAt = receivedAt,
            HttpStatus = status,
            // THIS BUILD'S OWN HASH OF THE FILE, of every byte, or nothing where it was not read to its end.
            BodySha256 = read?.Sha256,
            Note = note
        };

        if (note is not null)
        {
            _store.Append(fetch);
            if (read is { OverBudget: true })
            {
                Close(day, note);
                return new FileOutcome(FileResult.Capped, read.Bytes, 0, false, note);
            }
            return new FileOutcome(status == 404 ? FileResult.NotPublished : FileResult.Failed, read?.Bytes ?? 0, 0, status is 429, note);
        }

        TapeAppend appended;
        try
        {
            appended = _store.AppendArchive(fetch, new TapeArchiveBatch
            {
                RecordSeries = GdeltGkg.BatchSeries,
                RecordSubject = GdeltGkg.BatchSubject,
                ItemsSeries = GdeltGkg.ItemsSeries,
                Label = label,
                Bytes = read!.Bytes,
                PublishedMd5 = published!,
                ComputedMd5 = read.Md5!,
                Sha256 = read.Sha256!,
                LastModified = lastModified,
                Rows = read.Rows,
                Filter = GdeltGkg.Filter
            }, read.Kept);
        }
        catch (ArgumentException ex)
        {
            // THE STORE REFUSED THE FILE BEFORE ITS TRANSACTION, so the attempt is still a row, with the reason.
            var refused = "the tape refused the file: " + ex.Message.ReplaceLineEndings(" ");
            _store.Append(fetch with { Note = refused });
            return new FileOutcome(FileResult.Failed, read!.Bytes, 0, false, refused);
        }

        if (appended.Stored > 0) AddDayBytes(day, read.KeptBytes);
        return new FileOutcome(FileResult.Recorded, read.Bytes, appended.Stored, false, null);
    }

    /// <summary>
    /// <c>lastupdate.txt</c>, on its own leash: the status, the body, and — when nothing was answered, or the answer was
    /// not a listing's size — why, in words. A redirect is the status it is.
    /// </summary>
    async Task<(int? Status, string? Body, string? Failure)> GetListingAsync(string url, CancellationToken ct)
    {
        using var leash = CancellationTokenSource.CreateLinkedTokenSource(ct);
        leash.CancelAfter(RequestTimeout);

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, leash.Token);
            var status = (int)response.StatusCode;
            if (status != 200) return (status, null, null);

            if (response.Content.Headers.ContentLength is { } declared && declared > MaxListingBytes)
                return (status, null, $"the listing declared {declared} bytes, which is not GDELT's listing");

            await using var stream = await response.Content.ReadAsStreamAsync(leash.Token);
            var body = await Downloader.ReadLimitedAsync(stream, MaxListingBytes, leash.Token);
            return body is null
                ? (status, null, $"the listing was longer than {MaxListingBytes} bytes, which is not GDELT's listing")
                : (status, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, null, $"GDELT did not answer within {RequestTimeout.TotalSeconds:0.#} s"); }
        catch (Exception ex) { return (null, null, "GDELT could not be reached: " + ex.Message.ReplaceLineEndings(" ")); }
    }

    /// <summary>
    /// The MD5 GDELT's storage publishes for a file, from its <c>x-goog-hash: md5=&lt;base64&gt;</c> header, as hex — or
    /// null where it gives none. Measured on 2026-10-06 on 24 files: equal to the listing's MD5 and to the ETag.
    /// </summary>
    static string? StorageMd5(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-goog-hash", out var values)) return null;
        foreach (var value in values)
            foreach (var part in value.Split(','))
            {
                var p = part.Trim();
                if (!p.StartsWith("md5=", StringComparison.Ordinal)) continue;
                try
                {
                    var md5 = Convert.FromBase64String(p[4..]);
                    return md5.Length == 16 ? Convert.ToHexStringLower(md5) : null;
                }
                catch (FormatException) { return null; }
            }
        return null;
    }

    /// <summary>
    /// What the backfill has asked for today, counted from the tape: each of today's backfill fetches at its file's size
    /// where it recorded one, at <see cref="MaxFileBytes"/> — the most it may have cost — where it was answered or cut off
    /// without recording one, and at nothing where the answer was a status alone.
    /// </summary>
    long BackfillSpentOn(DateTimeOffset now)
    {
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        long spent = 0;
        foreach (var f in _store.Fetches(GdeltGkg.Source, BackfillSeries, limit: 10_000))
        {
            if (f.RequestedAt < dayStart) break;
            if (f.Note is null && f.HttpStatus == 200 && RecordedSize(f) is { } size) spent += size;
            else if (f.HttpStatus is null or 200) spent += MaxFileBytes;
        }
        return spent;
    }

    /// <summary>The size the tape's record of a fetch's file gives, or null where it holds none.</summary>
    long? RecordedSize(TapeFetchRecord fetch)
    {
        if (GdeltGkg.LabelOf(fetch.Url) is not { } label) return null;
        var records = _store.Revisions(GdeltGkg.Source, GdeltGkg.BatchSeries, TapeStore.NaturalKey(GdeltGkg.BatchSubject, label));
        var record = records.LastOrDefault(r => r.FetchId == fetch.Id) ?? records.LastOrDefault();
        if (record?.Payload is not { } payload) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var n) ? n : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    bool Holds(DateTimeOffset label) =>
        _store.SourceTimes(GdeltGkg.Source, GdeltGkg.BatchSeries, GdeltGkg.BatchSubject, label).Contains(label);

    bool IsClosed(DateTimeOffset label)
    {
        lock (_gate) return _closed.Contains(DateOnly.FromDateTime(label.UtcDateTime));
    }

    /// <summary>The crypto rows held for one UTC day's files: read off the tape the first time a day is asked about, then counted here.</summary>
    long DayBytes(DateOnly day)
    {
        lock (_gate)
            if (_dayBytes.TryGetValue(day, out var counted)) return counted;

        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var held = _store.PayloadBytes(GdeltGkg.Source, GdeltGkg.ItemsSeries, from, from.AddDays(1));
        lock (_gate)
            return _dayBytes.TryAdd(day, held) ? held : _dayBytes[day];
    }

    void AddDayBytes(DateOnly day, long bytes)
    {
        DayBytes(day);
        lock (_gate) _dayBytes[day] += bytes;
    }

    /// <summary>THE DAY'S CAP IS REACHED: no more of that day's files are asked for while TradeAgent runs, and the owner is told.</summary>
    void Close(DateOnly day, string why)
    {
        lock (_gate)
            if (!_closed.Add(day)) return;
        Say($"the crypto items of the files labelled {day:yyyy-MM-dd} (UTC) reached the daily cap of {DailyCap} bytes, so no more of "
            + $"that day's GDELT files are read while TradeAgent runs ({why})");
    }

    /// <summary>The look as it ends, its failure — or none — the last error.</summary>
    GdeltLook Ended(GdeltLook look)
    {
        _lastError = look.Failure;
        return look;
    }

    void Say(string text)
    {
        try { _say(text); }
        catch (Exception) { /* a sink that throws is not news about GDELT */ }
    }

    /// <summary>Stops both tasks and waits for the request in flight. Safe to call twice.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[]? tasks;
        lock (_gate) { tasks = _tasks; _tasks = null; }

        try { await _stopping.CancelAsync(); } catch (Exception) { /* already gone */ }
        if (tasks is not null)
        {
            try { await Task.WhenAll(tasks); } catch (Exception) { /* a stopping task's last fault is not news */ }
        }

        _stopping.Dispose();
    }
}
