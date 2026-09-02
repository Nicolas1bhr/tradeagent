using System.Text;
using TradeAgent.Core;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The updater has no signature to fall back on: there is no Authenticode anywhere in this product
/// and the installer it runs is unsigned. The published SHA256SUMS.txt is therefore not one check
/// among several — it is the entire trust chain, and the file it covers is a 90 MB executable that
/// replaces the program holding the owner's open orders.
///
/// So these tests are about the ways that chain can be *lost* rather than *failed*: a manifest that
/// does not name our file, a release that published none at all, two assets that both look like the
/// installer, and a file that changes between being checked and being started. Every one of them
/// used to end in an install; every one of them now ends in a refusal the owner can read.
///
/// <see cref="UpdateTests"/> keeps the ordinary behaviour — version ordering, what counts as an
/// update, the tolerant manifest parsing that is legitimate. This file keeps the refusals.
/// </summary>
public class UpdateTrustTests
{
    const string Hash = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";
    const string OtherHash = "3f79bb7b435b05321651daefd374cdc681dc06faa65e374e38337b88ca046dea";
    const string Asset = "TradeAgent-Setup-x64.exe";

    // ---- harness -------------------------------------------------------------------------------

    static string Release(params string[] assets) =>
        $$"""
        {
          "tag_name": "v0.2.0",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/owner/repo/releases/tag/v0.2.0",
          "body": "notes",
          "assets": [
            {{string.Join(",\n", assets.Select(a =>
                $$"""{"name": "{{a}}", "size": 90000000, "browser_download_url": "https://example.invalid/{{a}}"}"""))}}
          ]
        }
        """;

    /// <summary>
    /// Stands in for GitHub, the network and the installer. Records what the service tried to do,
    /// which is the only way to tell "refused" from "quietly did it anyway".
    /// </summary>
    sealed class Fake
    {
        public string? ReleaseJson;
        public string? ChecksumText;

        /// <summary>Set when <see cref="UpdateSources.Download"/> was reached at all — a 90 MB download
        /// with nothing to check it against is already the defect, whatever happens afterwards.</summary>
        public bool DownloadStarted;
        public string? ShaHandedToTheDownload;
        public string? Launched;
        public int Launches;

        /// <summary>The path the download hands back, and what the bytes at it hash to.</summary>
        public string InstallerPath = "C:/updates/0.2.0/" + Asset;
        public string FileOnDisk = Hash;
        public string? Hashed;

        public UpdateSources Sources() => new(
            _ => Task.FromResult(ReleaseJson),
            (_, _) => Task.FromResult(ChecksumText),
            (_, sha, _, _) =>
            {
                DownloadStarted = true;
                ShaHandedToTheDownload = sha;
                return Task.FromResult(InstallerPath);
            },
            path => { Launched = path; Launches++; },
            (path, _) => { Hashed = path; return Task.FromResult(FileOnDisk); });
    }

    /// <summary>
    /// A service wired the way <c>AppHost</c> wires it, with nothing outstanding. Tests about the
    /// unconfirmed-order stop override <see cref="UpdateService.UnconfirmedWork"/> themselves; every
    /// other test needs it wired, because an updater that cannot see the order book refuses.
    /// </summary>
    static UpdateService Service(Fake f, string current = "0.1.0") =>
        new(current, "owner/repo", UpdateService.DefaultAssetPattern, f.Sources()) { UnconfirmedWork = () => 0 };

    static async Task<UpdateService> Offering(Fake f)
    {
        var service = Service(f);
        await service.CheckAsync();
        return service;
    }

    // ---- 1. a checksum we cannot resolve is a refusal, not an unverified install -----------------

    [Theory]
    [InlineData("byte order mark", "\uFEFF" + Hash + "  artifacts/" + Asset)]
    [InlineData("tab separator", Hash + "\tartifacts/" + Asset)]
    [InlineData("asset renamed after packaging", Hash + "  artifacts/TradeAgent-Setup-x86.exe")]
    [InlineData("truncated hash", "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde" + "  artifacts/" + Asset)]
    [InlineData("empty body", "")]
    public async Task A_checksum_file_that_does_not_cover_the_installer_stops_the_install(string kind, string manifest)
    {
        _ = kind;   // the case name, so a failure says which degradation it was

        var f = new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt"), ChecksumText = manifest };
        var service = await Offering(f);

        Assert.False(await service.InstallAsync());

        Assert.False(f.DownloadStarted);          // not "downloaded 90 MB and then thought about it"
        Assert.Null(f.ShaHandedToTheDownload);    // and never a null hash handed to Downloader
        Assert.Null(f.Launched);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("cannot be verified", service.Message);
        Assert.Contains("Nothing was installed", service.Message);
    }

    [Fact]
    public async Task A_checksum_file_that_cannot_be_fetched_stops_the_install()
    {
        var f = new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt"), ChecksumText = null };
        var service = await Offering(f);

        Assert.False(await service.InstallAsync());

        Assert.False(f.DownloadStarted);
        Assert.Null(f.Launched);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("cannot be verified", service.Message);
    }

    /// <summary>
    /// A release with no SHA256SUMS.txt at all. The tempting reading is "an old release, published
    /// before we started shipping a manifest, and still installable". packaging/build.ps1:288-297
    /// has always written one, so the honest reading is that this release did not come from our
    /// pipeline as we know it — and the tempting reading is the one that makes the only link in the
    /// trust chain optional.
    /// </summary>
    [Fact]
    public async Task A_release_that_published_no_checksum_file_at_all_stops_the_install()
    {
        var f = new Fake { ReleaseJson = Release(Asset) };
        var service = await Offering(f);

        Assert.Equal(UpdateStage.Available, service.Stage);   // it is still offered and readable
        Assert.False(await service.InstallAsync());

        Assert.False(f.DownloadStarted);
        Assert.Null(f.Launched);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("cannot be verified", service.Message);
        Assert.Contains("without the checksum file", service.Message);
    }

    // ---- 2. exactly one installer -----------------------------------------------------------------

    /// <summary>
    /// Two assets both matching the installer pattern is not a hypothetical: publishing an arm64
    /// build beside the x64 one produces exactly this release. "Take whichever the JSON array
    /// happens to list first" is not a decision anybody made.
    /// </summary>
    [Fact]
    public async Task A_release_carrying_two_files_that_both_look_like_the_installer_is_refused()
    {
        var f = new Fake { ReleaseJson = Release("TradeAgent-Setup-arm64.exe", Asset, "SHA256SUMS.txt") };
        var service = await Offering(f);

        Assert.Null(service.Available);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("look like the installer", service.Message);

        Assert.False(await service.InstallAsync());
        Assert.False(f.DownloadStarted);
        Assert.Null(f.Launched);
    }

    /// <summary>
    /// The adversarial probe's own case: a decoy listed before the real installer, under a pattern
    /// loose enough to match it. Position in the array decided what ran.
    /// </summary>
    [Fact]
    public void A_decoy_listed_before_the_real_installer_no_longer_wins_by_being_first()
    {
        const string json = """
            {"tag_name":"v9.9.9","draft":false,"prerelease":false,"body":"","html_url":"h",
             "assets":[{"name":"TradeAgent-Setup-x64.exe.bak","browser_download_url":"decoy","size":1},
                       {"name":"TradeAgent-Setup-x64.exe","browser_download_url":"real","size":90000000}]}
            """;
        UpdateVersion.TryParse("0.1.0", out var current);

        Assert.Null(ReleaseFeed.Parse(json, current, @"TradeAgent-Setup.*\.exe"));
    }

    [Fact]
    public void One_installer_beside_other_release_files_still_resolves()
    {
        UpdateVersion.TryParse("0.1.0", out var current);
        var found = ReleaseFeed.Parse(
            Release(Asset, "SHA256SUMS.txt", "manifest.json", "TradeAgent-notes.txt"),
            current, UpdateService.DefaultAssetPattern);

        Assert.NotNull(found);
        Assert.Equal(Asset, found.AssetName);
    }

    // ---- 3. the file is re-read immediately before it is started ----------------------------------

    /// <summary>
    /// Real bytes and the real SHA-256, because this guard is about what is on disk at the moment
    /// Launch is called rather than about what a fake said earlier. The download writes the file the
    /// manifest covers; something then rewrites it in <c>updates\0.2.0\</c> before the installer is
    /// started, which is the entire window this guard exists to close.
    /// </summary>
    [Fact]
    public async Task An_installer_rewritten_after_the_download_is_not_started()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, Asset);
            await File.WriteAllTextAsync(path, "the installer that was published");
            var published = await Downloader.Sha256Async(path);

            var f = new Fake
            {
                ReleaseJson = Release(Asset, "SHA256SUMS.txt"),
                ChecksumText = $"{published}  artifacts/{Asset}\n",
                InstallerPath = path
            };

            // Between Downloader renaming the verified bytes into place and Launch reading them,
            // something else writes the file.
            var sources = f.Sources() with
            {
                Download = async (_, sha, _, _) =>
                {
                    f.DownloadStarted = true;
                    f.ShaHandedToTheDownload = sha;
                    await File.WriteAllTextAsync(path, "a different installer");
                    return path;
                },
                // Null on purpose: this is also the assertion that a caller supplying no hasher
                // gets the real one (Downloader.Sha256Async) rather than a skipped check.
                Hash = null
            };
            var tampered = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern, sources)
            {
                UnconfirmedWork = () => 0
            };
            await tampered.CheckAsync();

            Assert.False(await tampered.InstallAsync());

            Assert.Null(f.Launched);
            Assert.Equal(0, f.Launches);
            Assert.Equal(UpdateStage.Failed, tampered.Stage);
            Assert.Contains("changed", tampered.Message);
            Assert.Contains("Nothing was installed", tampered.Message);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task An_installer_still_matching_its_checksum_is_started_exactly_once()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, Asset);
            await File.WriteAllTextAsync(path, "the installer that was published");
            var published = await Downloader.Sha256Async(path);

            var f = new Fake
            {
                ReleaseJson = Release(Asset, "SHA256SUMS.txt"),
                ChecksumText = $"{published}  artifacts/{Asset}\n",
                InstallerPath = path
            };
            var sources = f.Sources() with { Hash = Downloader.Sha256Async };
            var service = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern, sources)
            {
                UnconfirmedWork = () => 0
            };
            await service.CheckAsync();

            Assert.True(await service.InstallAsync());

            Assert.Equal(published, f.ShaHandedToTheDownload);
            Assert.Equal(path, f.Launched);
            Assert.Equal(1, f.Launches);
            Assert.Equal(UpdateStage.Installing, service.Stage);
        }
        finally { Cleanup(dir); }
    }

    // ---- 4. the unconfirmed-order hard stop, on the code path -------------------------------------

    static Fake Wellformed() => new()
    {
        ReleaseJson = Release(Asset, "SHA256SUMS.txt"),
        ChecksumText = $"{Hash}  artifacts/{Asset}\r\n"
    };

    /// <summary>
    /// The documented hard stop used to live in <c>MainWindow.RefreshUpdateBanner</c> as
    /// <c>_updateInstall.IsEnabled = !unconfirmed</c>, and Settings built a second Install button
    /// onto the same code path without it. A guard in a view is not a guard on a code path with two
    /// views — and it is re-evaluated only on the five-second tick, so even the banner's button
    /// stayed pressable for up to five seconds after an order went UNKNOWN.
    /// </summary>
    [Fact]
    public async Task An_order_whose_outcome_is_unconfirmed_stops_the_install()
    {
        var f = Wellformed();
        var service = Service(f);
        service.UnconfirmedWork = () => 1;
        await service.CheckAsync();

        Assert.False(await service.InstallAsync());

        Assert.False(f.DownloadStarted);
        Assert.Null(f.Launched);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("unconfirmed", service.Message);
    }

    /// <summary>
    /// Both Install buttons call the same <c>MainWindow.InstallUpdateAsync(host)</c>, which is one
    /// line: <c>host.Updates.InstallAsync()</c>. Pressing from Settings is therefore the same call
    /// as pressing from the banner, and pressing twice does not accumulate into a yes.
    /// </summary>
    [Fact]
    public async Task Pressing_install_again_from_the_other_view_is_refused_the_same_way()
    {
        var f = Wellformed();
        var service = Service(f);
        service.UnconfirmedWork = () => 2;
        await service.CheckAsync();

        Assert.False(await service.InstallAsync());   // banner
        Assert.False(await service.InstallAsync());   // Settings, same instance, same code path

        Assert.False(f.DownloadStarted);
        Assert.Equal(0, f.Launches);
        Assert.Contains("2 orders", service.Message);
    }

    /// <summary>
    /// Not knowing is not the same as knowing there is none. A provider that was never wired, and a
    /// provider that throws because the gateway is mid-shutdown or the database is locked, both mean
    /// TradeAgent cannot say whether an order is outstanding — and it will not replace itself on
    /// that basis.
    /// </summary>
    [Fact]
    public async Task An_updater_that_cannot_tell_whether_work_is_outstanding_refuses()
    {
        var unwired = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern, Wellformed().Sources());
        await unwired.CheckAsync();
        Assert.False(await unwired.InstallAsync());
        Assert.Contains("cannot tell", unwired.Message);

        var f = Wellformed();
        var throwing = Service(f);
        throwing.UnconfirmedWork = () => throw new InvalidOperationException("database is locked");
        await throwing.CheckAsync();

        Assert.False(await throwing.InstallAsync());
        Assert.False(f.DownloadStarted);
        Assert.Null(f.Launched);
        Assert.Equal(UpdateStage.Failed, throwing.Stage);
        Assert.Contains("cannot tell", throwing.Message);
    }

    [Fact]
    public async Task Nothing_outstanding_installs_normally()
    {
        var f = Wellformed();
        var service = Service(f);
        service.UnconfirmedWork = () => 0;
        await service.CheckAsync();

        Assert.True(await service.InstallAsync());
        Assert.Equal(Hash, f.ShaHandedToTheDownload);
        Assert.Equal(1, f.Launches);
    }

    // ---- 5. every refusal is written down ---------------------------------------------------------

    [Fact]
    public async Task A_refusal_is_written_to_the_activity_log_in_plain_language()
    {
        var lines = new List<string>();
        var f = new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt"), ChecksumText = "" };
        var service = Service(f);
        service.UnconfirmedWork = () => 0;
        service.Activity = (text, level) => lines.Add($"{level}: {text}");
        await service.CheckAsync();

        Assert.False(await service.InstallAsync());

        var line = Assert.Single(lines);
        Assert.StartsWith("warn: ", line);
        Assert.Contains("cannot be verified", line);
    }

    [Fact]
    public async Task An_install_that_goes_ahead_is_written_down_too()
    {
        var lines = new List<string>();
        var f = Wellformed();
        var service = Service(f);
        service.UnconfirmedWork = () => 0;
        service.Activity = (text, level) => lines.Add($"{level}: {text}");
        await service.CheckAsync();

        Assert.True(await service.InstallAsync());

        var line = Assert.Single(lines);
        Assert.Contains("0.2.0", line);
    }

    // ---- the class gate: the shape packaging/build.ps1 actually writes ---------------------------

    /// <summary>
    /// Copied from <c>packaging/build.ps1:288-297</c>: a lowercase Get-FileHash, two spaces, the
    /// repository-relative path with backslashes replaced by forward slashes, one line per shipped
    /// .exe/.msi, written with <c>Set-Content -Encoding ascii</c> — so no BOM, and CRLF endings.
    ///
    /// This is the gate the whole unit rests on. Every refusal above assumes a manifest that does
    /// not resolve is evidence of a mismatch; if a packaging change ever makes our OWN manifest
    /// stop resolving, that assumption turns every legitimate release into "cannot be verified".
    /// This test is what makes that a red build instead of a silent wall in front of the owner.
    /// </summary>
    [Fact]
    public async Task The_manifest_shape_packaging_writes_is_the_shape_the_updater_reads()
    {
        var manifest =
            $"{Hash}  artifacts/{Asset}\r\n" +
            $"{OtherHash}  artifacts/tools/trade.exe\r\n";

        Assert.Equal(Hash, ChecksumManifest.Find(manifest, Asset));
        Assert.Equal(manifest, Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(manifest)));   // ASCII, no BOM

        var f = new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt"), ChecksumText = manifest, FileOnDisk = Hash };
        var service = await Offering(f);

        Assert.True(await service.InstallAsync());
        Assert.Equal(Hash, f.ShaHandedToTheDownload);
        Assert.Equal(1, f.Launches);
    }

    /// <summary>
    /// The other half of the gate, and the reason the refusals above are safe to be strict: every
    /// shape a CORRECT manifest is allowed to take still resolves. These are the adversarial probe's
    /// own positive controls, kept verbatim — if tightening the trust chain had broken one of them,
    /// it would have turned a legitimate release into "cannot be verified".
    /// </summary>
    [Fact]
    public void The_tolerance_a_correct_manifest_is_entitled_to_is_untouched()
    {
        Assert.Equal(Hash, ChecksumManifest.Find($"{Hash}  artifacts/{Asset}\r\n", Asset));      // CRLF
        Assert.Equal(Hash, ChecksumManifest.Find($"{Hash} *{Asset}", Asset));                    // binary marker
        Assert.Equal(Hash, ChecksumManifest.Find($"junk\n{Hash}  {Asset}", Asset));              // junk first line
        Assert.Equal(Hash, ChecksumManifest.Find($"{Hash}  artifacts/TradeAgent-Setup-x64.EXE", Asset));  // case
        Assert.Equal(Hash, ChecksumManifest.Find($"{Hash}  artifacts\\{Asset}", Asset));         // Windows path
    }

    /// <summary>
    /// The guard under the guard. Everything above keeps a null hash from reaching the download; this
    /// keeps a null hash from being downloadable at all on the path that produces an executable.
    /// <c>Downloader.DownloadAsync</c> itself stays tolerant on purpose — the ATAS installer
    /// (<c>Prerequisites.cs:118</c>) has no published checksum to be checked against.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_download_of_something_we_will_execute_refuses_to_start_without_a_hash(string? sha)
    {
        var dir = TempDir();
        try
        {
            var ex = await Assert.ThrowsAsync<TradeAgentException>(() =>
                Downloader.DownloadVerifiedAsync("https://example.invalid/x.exe", Path.Combine(dir, "x.exe"), sha));

            Assert.Equal(ErrorCode.UPDATE_FAILED, ex.Info.Code);
            Assert.False(File.Exists(Path.Combine(dir, "x.exe")));
        }
        finally { Cleanup(dir); }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tradeagent-u2d-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
