using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.Core;

namespace TradeAgent.Provisioning;

/// <summary>
/// What the caller knows about the bytes before this product uses them, stated at every call and
/// never defaulted.
///
/// The old signature took <c>string? sha256 = null</c>, so "check this against the publisher's hash"
/// and "run whatever arrives" were the same call with one argument left out — and the one file
/// TradeAgent runs ELEVATED, the ATAS platform installer, was on the second path by omission
/// (REVIEW 2026-09-05b finding 4, Codex F15). There is no null here. A caller with no published
/// checksum has to name the reason in words, and those words are written into the owner's activity
/// log before the file is used. The decision stays available — some vendors publish nothing, and
/// refusing to install at all would be a worse product — but it stops being invisible.
/// </summary>
public sealed record Integrity
{
    Integrity(string? sha256, string? because)
    {
        Sha256 = sha256;
        Because = because;
    }

    /// <summary>The publisher's hash, or null when the caller decided to go without one.</summary>
    public string? Sha256 { get; }

    /// <summary>Why there is no hash, in words the owner can read. Null when there is one.</summary>
    public string? Because { get; }

    /// <summary>Check the bytes against this hash, and throw the file away when they differ.</summary>
    public static Integrity Pinned(string sha256) =>
        string.IsNullOrWhiteSpace(sha256)
            ? throw new ArgumentException(
                "Integrity.Pinned needs a hash. A caller with nothing to check against says so with Unverified.",
                nameof(sha256))
            : new Integrity(sha256.Trim(), null);

    /// <summary>
    /// Take the bytes with no checksum, for a stated reason that is recorded before the file is
    /// used. The string is read by the account owner, so it says what the vendor does, not what the
    /// code does.
    /// </summary>
    public static Integrity Unverified(string because) =>
        string.IsNullOrWhiteSpace(because)
            ? throw new ArgumentException("an unverified download has to say why", nameof(because))
            : new Integrity(null, because);

    /// <summary>
    /// The pinned hash when the vendor data carries one, and the recorded decision when it does not.
    ///
    /// This is the shape of a call site whose hash is the OWNER'S to pin: shipping without one is a
    /// decision the log shows, and pinning one later is a data change with no code change.
    /// </summary>
    public static Integrity PinnedOr(string? sha256, string because) =>
        string.IsNullOrWhiteSpace(sha256) ? Unverified(because) : Pinned(sha256);
}

/// <summary>
/// Fetching things from the internet, with the three properties an unattended installer needs:
/// progress the user can watch, a partial file that can be resumed instead of restarted, and a
/// checksum check when the publisher gives us one.
///
/// Everything here is per-user and needs no administrator rights. Nothing here opens a window.
/// </summary>
public static class Downloader
{
    /// <summary>GitHub's REST API answers 403 to a request with no User-Agent. This is not optional.</summary>
    public const string UserAgent = "TradeAgent/0.1 (+https://github.com/nicolasbeeckman/tradeagent)";

    /// <summary>
    /// Where an "installed without a checksum" decision is written so the owner can read it
    /// afterwards. Wired once, at startup, to the activity log.
    ///
    /// Static because this layer sits below the database on purpose — a downloader that opens a
    /// database is a downloader that cannot run during setup, before there is one. The recording is
    /// done HERE rather than at each call site so that no caller can decide to skip it; the reason
    /// it prints is the caller's, and there is no way to ask for an unverified download without
    /// supplying one.
    /// </summary>
    public static Action<string>? RecordDecision { get; set; }

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,          // GitHub redirects release assets to its object store,
            MaxAutomaticRedirections = 10,     // and redirects the API itself when a repo is renamed.
            AutomaticDecompression = DecompressionMethods.All
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destFile"/> and returns the destination.
    ///
    /// The bytes land in a sibling part file and are only renamed into place once the whole body has
    /// arrived and any checksum has passed, so an interrupted download can never be mistaken for a
    /// finished one. If a part file from an earlier attempt at the same download is present the
    /// request asks the server to continue from that offset; a server that will not do ranges simply
    /// starts again, which is a slow success rather than a failure.
    ///
    /// <b>A part file is bound to what it is a part of.</b> Its name carries the URL it came from and
    /// the exact total length that download was going to be, and anything at the destination that
    /// does not match both is deleted rather than resumed. Before REVIEW 2026-09-05b finding 4 the
    /// name was <c>&lt;dest&gt;.part</c> and nothing else: an interrupted download of ANY file left
    /// bytes at the ATAS installer's fixed destination, the next attempt sent a Range header, a
    /// publisher's CDN answered 206, and the stale prefix was concatenated with the vendor's bytes,
    /// renamed to ATASPlatform.exe and run elevated. Resuming is still worth having on a 459 MB
    /// installer over a domestic line. What is gone is resuming into a different download.
    /// </summary>
    public static Task<string> DownloadAsync(
        string url,
        string destFile,
        Integrity integrity,
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default,
        ErrorCode integrityCode = ErrorCode.AI_INSTALL_FAILED) =>
        DownloadAsync(url, destFile, integrity, progress, ct, integrityCode, restarted: false);

    static async Task<string> DownloadAsync(
        string url, string destFile, Integrity integrity, IProgress<ProvisionProgress>? progress,
        CancellationToken ct, ErrorCode integrityCode, bool restarted)
    {
        ArgumentNullException.ThrowIfNull(integrity);

        var dir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var name = Path.GetFileName(destFile);
        var (resumable, resumeFrom, expectedTotal) = Resumable(destFile, url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumable is not null) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // A range we asked for and did not get means the server is sending the whole file again.
        var appending = resumable is not null && response.StatusCode == HttpStatusCode.PartialContent;
        if (!appending) resumeFrom = 0;

        if (!response.IsSuccessStatusCode)
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                $"the download of {name} was refused by the server ({(int)response.StatusCode})");

        var range = response.Content.Headers.ContentRange;
        var total = appending
            ? range?.Length ?? (response.Content.Headers.ContentLength is { } l ? l + resumeFrom : null)
            : response.Content.Headers.ContentLength;

        // THE BINDING, CHECKED AGAINST THE ANSWER. The bytes on disk are part of a download of this
        // URL that was going to be exactly `expectedTotal` bytes long. A 206 continuing a body of a
        // different length, or from an offset we did not ask for, is not the rest of THIS file, and
        // appending it would assemble a file that never existed anywhere. Throw the prefix away and
        // fetch the whole thing once; the retry has nothing left to resume from, so it cannot loop.
        if (appending && (total != expectedTotal || (range?.From is { } from && from != resumeFrom)))
        {
            TryDelete(resumable!);
            if (restarted)
                throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                    $"the download of {name} could not be continued and could not be started again");
            response.Dispose();
            return await DownloadAsync(url, destFile, integrity, progress, ct, integrityCode, restarted: true);
        }

        // A body whose length the server never declared gets a part file named for length 0, which
        // `Resumable` never offers back — an unknown length cannot be bound to anything.
        var part = appending ? resumable! : PartPath(destFile, url, total);
        if (!appending && resumable is not null && !string.Equals(resumable, part, StringComparison.Ordinal))
            TryDelete(resumable);

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, appending ? FileMode.Append : FileMode.Create,
                     FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            var buffer = new byte[128 * 1024];
            var done = resumeFrom;
            var lastReport = -1;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;

                // One report per whole percent: a progress bar does not need 40 000 events.
                var pct = total is > 0 ? (int)(done * 100 / total.Value) : -1;
                if (pct != lastReport)
                {
                    lastReport = pct;
                    progress?.Report(new ProvisionProgress("download",
                        total is > 0 ? $"Downloading {name} — {Megabytes(done)} of {Megabytes(total.Value)}"
                                     : $"Downloading {name} — {Megabytes(done)}",
                        total is > 0 ? done / (double)total.Value : null));
                }
            }

            // A body that stopped short is not a finished file. Nothing checked this before: with no
            // checksum, a server that closed early had its truncated bytes renamed into place and,
            // for the ATAS installer, run elevated. The part file is deliberately LEFT — that is
            // what it is for, and the next attempt resumes it because it is bound to this download.
            if (total is { } expectedLength && done != expectedLength)
                throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                    $"the download of {name} stopped after {done} of {expectedLength} bytes");
        }

        if (integrity.Sha256 is { Length: > 0 } expected)
        {
            progress?.Report(new ProvisionProgress("verify", $"Checking {name} is exactly what the publisher released"));
            var actual = await Sha256Async(part, ct);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new TradeAgentException(integrityCode,
                    $"the downloaded {name} did not match the publisher's checksum, so it was thrown away");
            }
        }
        else
        {
            // An unverified install is a DECISION, and it is written down where the owner can read
            // it rather than being the absence of a line. `Integrity` has no null, so the caller had
            // to state the reason; this is where those words go.
            var decision = $"Installed {name} without checking it against a publisher's checksum: {integrity.Because}";
            progress?.Report(new ProvisionProgress("verify", decision));
            try { RecordDecision?.Invoke(decision); }
            catch (Exception) { /* a sink that throws must not fail the install it is only describing */ }
        }

        if (File.Exists(destFile)) File.Delete(destFile);
        File.Move(part, destFile);
        return destFile;
    }

    /// <summary>The part file naming that binds a resumable prefix to one URL and one total length.</summary>
    static string PartPath(string destFile, string url, long? total) =>
        $"{destFile}{PartMark}-{UrlKey(url)}-{total ?? 0}";

    const string PartMark = ".part";

    static string UrlKey(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];

    /// <summary>
    /// The one part file beside <paramref name="destFile"/> that may be resumed for
    /// <paramref name="url"/>, and every other part file at that destination deleted on the way
    /// past — including the unbound <c>&lt;dest&gt;.part</c> written by builds before this one.
    ///
    /// A part is offered back only when its name says it belongs to this URL, its name declares the
    /// total length of the body it was part of, and what is on disk is a strict prefix of that
    /// length. Anything else is somebody else's bytes.
    /// </summary>
    static (string? Part, long From, long Total) Resumable(string destFile, string url)
    {
        var dir = Path.GetDirectoryName(destFile);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return (null, 0, 0);

        var prefix = Path.GetFileName(destFile) + PartMark;
        string[] entries;
        // Enumerated and matched in code rather than with a search pattern: a destination file name
        // is not a glob, and on a case-preserving filesystem it can legally contain one.
        try { entries = Directory.GetFiles(dir); }
        catch (IOException) { return (null, 0, 0); }
        catch (UnauthorizedAccessException) { return (null, 0, 0); }

        string? found = null;
        long from = 0, total = 0;

        foreach (var file in entries)
        {
            var fileName = Path.GetFileName(file);
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) continue;

            long length;
            try { length = new FileInfo(file).Length; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var tag = fileName[prefix.Length..].Split('-');
            if (found is null && tag is ["", var key, var declared] && key == UrlKey(url)
                && long.TryParse(declared, out var t) && t > 0 && length > 0 && length < t)
            {
                (found, from, total) = (file, length, t);
                continue;
            }

            TryDelete(file);
        }

        return (found, from, total);
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { /* a part we could not remove is one we also will not resume from */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The same download, for a file this product is going to EXECUTE, where the hash is not
    /// optional and a missing one is a defect rather than a lenient case.
    ///
    /// <see cref="DownloadAsync"/> accepts <see cref="Integrity.Unverified"/> and installs anyway,
    /// because two of its three callers have nothing to check against: the ATAS installer comes from
    /// ATAS's own site (<c>Prerequisites.cs</c>) and a runtime plan may ship without a pinned hash.
    /// That tolerance is correct there — recorded, since finding 4 — and was catastrophic on the
    /// update path, where the file being fetched replaces the program holding the owner's open
    /// orders and the checksum is the entire trust chain: there is no signature underneath it.
    ///
    /// So the update path uses this instead. It cannot be handed a null by accident: a caller that
    /// loses its hash gets a refusal here even if every check upstream of it is one day removed, and
    /// a real mismatch is reported as <see cref="ErrorCode.UPDATE_INTEGRITY_FAILED"/> rather than as
    /// AI_INSTALL_FAILED, which names a different program.
    /// </summary>
    public static Task<string> DownloadVerifiedAsync(
        string url,
        string destFile,
        string? sha256,
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha256))
            throw new TradeAgentException(ErrorCode.UPDATE_FAILED,
                $"{Path.GetFileName(destFile)} was not downloaded because there is no published checksum to check it against");

        return DownloadAsync(url, destFile, Integrity.Pinned(sha256), progress, ct, ErrorCode.UPDATE_INTEGRITY_FAILED);
    }

    /// <summary>
    /// Downloads an archive and unpacks it into <paramref name="destDir"/>, returning that directory.
    ///
    /// Understands <c>.zip</c> and <c>.tar.gz</c>/<c>.tgz</c>. Anything else is treated as the file
    /// itself and simply placed in the directory under its own name — which is what a publisher who
    /// ships a bare <c>.exe</c> expects.
    /// </summary>
    public static async Task<string> DownloadAndUnpackAsync(
        string url,
        string destDir,
        Integrity integrity,
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        var fileName = FileNameFromUrl(url);
        var staging = Path.Combine(destDir, ".download");
        Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, fileName);

        try
        {
            await DownloadAsync(url, archive, integrity, progress, ct);
            progress?.Report(new ProvisionProgress("unpack", $"Unpacking {fileName}"));
            await UnpackAsync(archive, destDir, ct);
            return destDir;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { /* a leftover temp folder is untidy, not broken */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Unpacks a local archive into a directory. Same format rules as the download form.</summary>
    public static async Task UnpackAsync(string archiveFile, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var lower = archiveFile.ToLowerInvariant();

        if (lower.EndsWith(".zip"))
        {
            // Runs on a worker: ZipFile has no async form and this can take tens of seconds.
            await Task.Run(() => ZipFile.ExtractToDirectory(archiveFile, destDir, overwriteFiles: true), ct);
            return;
        }

        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
        {
            await using var file = File.OpenRead(archiveFile);
            await using var gz = new GZipStream(file, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gz, destDir, overwriteFiles: true, ct);
            return;
        }

        if (lower.EndsWith(".tar"))
        {
            await TarFile.ExtractToDirectoryAsync(archiveFile, destDir, overwriteFiles: true, ct);
            return;
        }

        // Not an archive: the download is the program.
        var target = Path.Combine(destDir, Path.GetFileName(archiveFile));
        File.Copy(archiveFile, target, overwrite: true);
    }

    /// <summary>
    /// Asks GitHub for the newest release of <paramref name="ownerRepo"/> ("owner/repo") and returns
    /// the download URL of the first asset whose file name matches <paramref name="assetNameRegex"/>.
    ///
    /// Returns null — never throws — when the machine is offline, the rate limit is hit, the repo has
    /// moved or no asset matches, because every caller has a pinned URL to fall back to and a failure
    /// to look up the newest version must not become a failure to install at all.
    /// </summary>
    public static async Task<string?> ResolveGitHubAssetAsync(string ownerRepo, string assetNameRegex, CancellationToken ct = default)
    {
        using var release = await GitHubLatestReleaseAsync(ownerRepo, ct);
        if (release is null) return null;
        try
        {
            var rx = new Regex(assetNameRegex, RegexOptions.IgnoreCase);
            if (!release.RootElement.TryGetProperty("assets", out var assets)) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !rx.IsMatch(name)) continue;
                return asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            }
        }
        catch (Exception) { /* a malformed answer is the same as no answer */ }
        return null;
    }

    /// <summary>The newest release's tag, or null when it cannot be looked up.</summary>
    public static async Task<string?> ResolveGitHubTagAsync(string ownerRepo, CancellationToken ct = default)
    {
        using var release = await GitHubLatestReleaseAsync(ownerRepo, ct);
        if (release is null) return null;
        try { return release.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null; }
        catch (Exception) { return null; }
    }

    static async Task<JsonDocument?> GitHubLatestReleaseAsync(string ownerRepo, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{ownerRepo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// What the server says about a URL without fetching its body, or null when it could not be
    /// asked at all.
    ///
    /// It exists so "the vendor has not published this month" and "the vendor published the file and
    /// not its checksum" can be told apart without pulling two megabytes of a file that is going to
    /// be refused anyway. A HEAD is the whole request; nothing is written to disk by it.
    /// </summary>
    public static async Task<HttpStatusCode?> TryStatusAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.StatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Fetches a text file (checksum manifests, version indexes). Null when unreachable.</summary>
    public static async Task<string?> TryGetStringAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Fetches a text file that we already know must be SMALL, and refuses it while it is arriving
    /// rather than after.
    ///
    /// <see cref="TryGetStringAsync"/> buffers the whole body before anyone can look at it: no
    /// content-length check, decompression on, and this client's 30-minute timeout. A size limit
    /// applied to its RESULT is not a limit at all — the allocation has already happened, and on the
    /// update path the caller is holding a latch that stops the owner trading while it waits. So the
    /// checksum manifest comes through here: a declared length is refused unopened, the body is read
    /// through <see cref="ReadLimitedAsync"/> so at most <paramref name="maxBytes"/> + 1 bytes are
    /// ever pulled off the socket, and the whole thing is on a short leash of its own.
    ///
    /// Null means "could not be read", and the caller turns that into a sentence blaming the release
    /// for it. So the ONE thing that does not become null is <paramref name="ct"/> being cancelled:
    /// that is this application shutting down, not a publisher who shipped an unreadable manifest,
    /// and reporting it as the latter puts an accusation in the owner's activity log about a release
    /// that did nothing. The leash expiring stays null — a server that stopped answering really is a
    /// file that could not be read.
    /// </summary>
    public static async Task<string?> TryGetSmallTextAsync(
        string url, int maxBytes, TimeSpan timeout, CancellationToken ct = default)
    {
        using var leash = CancellationTokenSource.CreateLinkedTokenSource(ct);
        leash.CancelAfter(timeout);

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, leash.Token);
            if (!response.IsSuccessStatusCode) return null;

            // The server said how big it is and it is too big. Nothing needs to be read at all.
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes) return null;

            await using var body = await response.Content.ReadAsStreamAsync(leash.Token);
            return await ReadLimitedAsync(body, maxBytes, leash.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Reads a stream as UTF-8, pulling at most <paramref name="maxBytes"/> + 1 bytes, and returns
    /// null the moment it is clear there are more than <paramref name="maxBytes"/>.
    ///
    /// The +1 is the whole trick: one byte past the limit is enough to know the body is too big, and
    /// it is the last byte this ever asks for. A declared length can lie and a chunked body declares
    /// nothing, so this is the check that actually holds.
    ///
    /// Separate from the fetch so it can be tested against a stream that counts what was taken from
    /// it — "refused without buffering it all" is a claim about reads, and only a read count can
    /// prove it.
    /// </summary>
    public static async Task<string?> ReadLimitedAsync(Stream body, int maxBytes, CancellationToken ct = default)
    {
        // The +1 is arithmetic on a number a caller chose, and at int.MaxValue it wraps to
        // int.MinValue — which Math.Min then passes to new byte[], throwing OverflowException out of
        // the middle of a fetch. "As much as an int can count" is a request for no practical limit,
        // so that is what it gets: there is no byte past int.MaxValue for the +1 to reach for. A
        // NEGATIVE limit is a defect in the caller, and answering it with null would say "the body
        // was too big" about a body nobody read.
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var cap = maxBytes == int.MaxValue ? int.MaxValue : maxBytes + 1;
        var buffer = new byte[Math.Min(64 * 1024, cap)];
        using var into = new MemoryStream();

        var total = 0;
        while (total < cap)
        {
            var want = Math.Min(buffer.Length, cap - total);
            var read = await body.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read <= 0) break;

            into.Write(buffer, 0, read);
            total += read;
        }

        return total > maxBytes ? null : Encoding.UTF8.GetString(into.GetBuffer(), 0, (int)into.Length);
    }

    public static async Task<string> Sha256Async(string file, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    static string FileNameFromUrl(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.#} MB";
}
