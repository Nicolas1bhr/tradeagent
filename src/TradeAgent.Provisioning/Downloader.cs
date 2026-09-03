using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.Core;

namespace TradeAgent.Provisioning;

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
    /// The bytes land in a sibling <c>.part</c> file and are only renamed into place once the whole
    /// body has arrived and any checksum has passed, so an interrupted download can never be
    /// mistaken for a finished one. If a <c>.part</c> from an earlier attempt is present the request
    /// asks the server to continue from that offset; a server that will not do ranges simply starts
    /// again, which is a slow success rather than a failure.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string url,
        string destFile,
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default,
        string? sha256 = null,
        ErrorCode integrityCode = ErrorCode.AI_INSTALL_FAILED)
    {
        var dir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var part = destFile + ".part";
        var name = Path.GetFileName(destFile);
        var resumeFrom = File.Exists(part) ? new FileInfo(part).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // A range we asked for and did not get means the server is sending the whole file again.
        var appending = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!appending) resumeFrom = 0;

        if (!response.IsSuccessStatusCode)
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                $"the download of {name} was refused by the server ({(int)response.StatusCode})");

        var total = response.Content.Headers.ContentLength is { } len ? len + resumeFrom : (long?)null;

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
        }

        if (sha256 is { Length: > 0 })
        {
            progress?.Report(new ProvisionProgress("verify", $"Checking {name} is exactly what the publisher released"));
            var actual = await Sha256Async(part, ct);
            if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new TradeAgentException(integrityCode,
                    $"the downloaded {name} did not match the publisher's checksum, so it was thrown away");
            }
        }

        if (File.Exists(destFile)) File.Delete(destFile);
        File.Move(part, destFile);
        return destFile;
    }

    /// <summary>
    /// The same download, for a file this product is going to EXECUTE, where the hash is not
    /// optional and a missing one is a defect rather than a lenient case.
    ///
    /// <see cref="DownloadAsync"/> deliberately accepts a null <c>sha256</c> and skips verification,
    /// because two of its three callers have nothing to check against: the ATAS installer comes from
    /// ATAS's own site (<c>Prerequisites.cs:118</c>) and a runtime plan may ship without a pinned
    /// hash. That tolerance is correct there and was catastrophic on the update path, where the file
    /// being fetched replaces the program holding the owner's open orders and the checksum is the
    /// entire trust chain — there is no signature underneath it.
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

        return DownloadAsync(url, destFile, progress, ct, sha256, ErrorCode.UPDATE_INTEGRITY_FAILED);
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
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default,
        string? sha256 = null)
    {
        Directory.CreateDirectory(destDir);

        var fileName = FileNameFromUrl(url);
        var staging = Path.Combine(destDir, ".download");
        Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, fileName);

        try
        {
            await DownloadAsync(url, archive, progress, ct, sha256);
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
