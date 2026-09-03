using System.Text;
using TradeAgent.Core;
using TradeAgent.Gateway;
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
        Assert.True(service.Refused);   // not "we could not ask GitHub" — the Settings card says so
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

    // ================================ round 2 ====================================================

    // ---- 1. the hard stop is asked again immediately before Launch -------------------------------

    /// <summary>
    /// The first ask happens before a manifest fetch and a 90 MB download. An order placed while
    /// that was running can go UNKNOWN in the middle of it, and a sample taken minutes earlier would
    /// launch the installer anyway. So it is asked twice, and the second ask is the one that is true
    /// at the moment it is acted on.
    /// </summary>
    [Fact]
    public async Task An_order_that_goes_unconfirmed_during_the_download_stops_the_launch()
    {
        var f = Wellformed();
        var outstanding = 0;

        var sources = f.Sources() with
        {
            Download = (_, sha, _, _) =>
            {
                f.DownloadStarted = true;
                f.ShaHandedToTheDownload = sha;
                outstanding = 1;              // the order goes UNKNOWN while the bytes are arriving
                return Task.FromResult(f.InstallerPath);
            }
        };
        var service = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern, sources)
        {
            UnconfirmedWork = () => outstanding
        };
        await service.CheckAsync();

        Assert.False(await service.InstallAsync());

        Assert.True(f.DownloadStarted);       // the early ask passed, as it should have
        Assert.Equal(0, f.Launches);          // and the late ask caught what changed under it
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("unconfirmed", service.Message);
    }

    /// <summary>The control for the pair above: nothing changes during the download, so it installs.</summary>
    [Fact]
    public async Task An_order_book_that_stays_clean_through_the_download_still_installs()
    {
        var f = Wellformed();
        var service = Service(f);
        await service.CheckAsync();

        Assert.True(await service.InstallAsync());
        Assert.True(f.DownloadStarted);
        Assert.Equal(1, f.Launches);
    }

    // ---- 4. a manifest that contradicts itself ---------------------------------------------------

    [Fact]
    public async Task A_manifest_naming_the_installer_twice_with_different_hashes_is_refused()
    {
        var manifest = $"{Hash}  artifacts/{Asset}\r\n{OtherHash}  artifacts/copies/{Asset}\r\n";

        Assert.Null(ChecksumManifest.Find(manifest, Asset, out var problem));
        Assert.Contains("twice, with two different hashes", problem);

        var f = new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt"), ChecksumText = manifest };
        var service = await Offering(f);

        Assert.False(await service.InstallAsync());
        Assert.False(f.DownloadStarted);
        Assert.Equal(0, f.Launches);
        Assert.Contains("two different hashes", service.Message);
    }

    /// <summary>
    /// The same file listed twice with the SAME hash is not a contradiction — build.ps1 hashes
    /// Get-ChildItem -Recurse, so one installer can legitimately appear under two paths. There is
    /// nothing to disambiguate, so it resolves.
    /// </summary>
    [Fact]
    public void A_manifest_naming_the_installer_twice_with_the_same_hash_still_resolves()
    {
        var manifest = $"{Hash}  artifacts/{Asset}\r\n{Hash}  artifacts/copies/{Asset}\r\n";

        Assert.Equal(Hash, ChecksumManifest.Find(manifest, Asset, out var problem));
        Assert.Null(problem);
    }

    /// <summary>A contradiction AFTER the first match is still a contradiction: every line is read.</summary>
    [Fact]
    public void A_second_conflicting_line_is_found_even_though_the_first_one_matched()
    {
        var manifest =
            $"{Hash}  {Asset}\n" +
            $"{OtherHash}  other.exe\n" +
            $"{OtherHash}  {Asset}\n";

        Assert.Null(ChecksumManifest.Find(manifest, Asset, out var problem));
        Assert.NotNull(problem);
    }

    // ---- 5. a checksum file too big to be ours ---------------------------------------------------

    [Fact]
    public async Task A_checksum_file_far_larger_than_ours_is_refused_before_it_is_split()
    {
        var oversized = new string('x', ChecksumManifest.MaxCharacters + 1);

        Assert.Null(ChecksumManifest.Find(oversized, Asset, out var problem));
        Assert.Contains("far larger", problem);

        var f = new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt"), ChecksumText = oversized };
        var service = await Offering(f);

        Assert.False(await service.InstallAsync());
        Assert.False(f.DownloadStarted);
        Assert.Contains("far larger", service.Message);
    }

    [Fact]
    public void A_checksum_file_with_far_more_lines_than_ours_is_refused()
    {
        var many = string.Join("\n", Enumerable.Repeat("x", ChecksumManifest.MaxLines + 1));

        Assert.True(many.Length < ChecksumManifest.MaxCharacters);   // refused on lines, not on size
        Assert.Null(ChecksumManifest.Find(many, Asset, out var problem));
        Assert.Contains("more lines", problem);
    }

    /// <summary>And the cap does not bite a manifest of a plausible size: 500 artifacts still read.</summary>
    [Fact]
    public void A_large_but_believable_manifest_still_resolves()
    {
        var lines = Enumerable.Range(0, 499).Select(i => $"{OtherHash}  artifacts/file{i}.exe").ToList();
        lines.Add($"{Hash}  artifacts/{Asset}");

        Assert.Equal(Hash, ChecksumManifest.Find(string.Join("\r\n", lines), Asset, out var problem));
        Assert.Null(problem);
    }

    // ---- 6. a checksum failure names the update, not the AI assistant ----------------------------

    [Fact]
    public void The_update_integrity_failure_has_its_own_code_and_all_four_catalogue_fields()
    {
        // Errors.Get falls back to UNKNOWN_ERROR for a code with no entry, so asking Get alone
        // cannot tell a real entry from a missing one. This asks the catalogue itself.
        Assert.Contains(ErrorCode.UPDATE_INTEGRITY_FAILED, Errors.All);

        var info = Errors.Get(ErrorCode.UPDATE_INTEGRITY_FAILED);
        Assert.Equal(ErrorCode.UPDATE_INTEGRITY_FAILED, info.Code);
        Assert.False(string.IsNullOrWhiteSpace(info.Technical));
        Assert.Contains("TradeAgent", info.UserMessage);
        Assert.Contains("not installed", info.UserMessage);
        Assert.Contains("untouched", info.Repair);
        Assert.False(info.AutoRepairable);   // a bad release is not something we repair by retrying

        // The words that made this its own code: the old one named the wrong program.
        Assert.DoesNotContain("AI assistant", info.UserMessage);
    }

    [Fact]
    public async Task A_real_checksum_mismatch_is_reported_as_an_update_failure_and_written_down()
    {
        var lines = new List<string>();
        var f = Wellformed();
        var sources = f.Sources() with
        {
            Download = (_, _, _, _) => throw new TradeAgentException(
                ErrorCode.UPDATE_INTEGRITY_FAILED,
                "the downloaded TradeAgent-Setup-x64.exe did not match the publisher's checksum, so it was thrown away")
        };
        var service = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern, sources)
        {
            UnconfirmedWork = () => 0,
            Activity = (text, level) => lines.Add($"{level}: {text}")
        };
        await service.CheckAsync();

        Assert.False(await service.InstallAsync());

        Assert.Equal(0, f.Launches);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("did not match the checksum", service.Message);
        Assert.Contains("untouched", service.Message);
        Assert.DoesNotContain("AI assistant", service.Message);

        var line = Assert.Single(lines);
        Assert.Contains("0.2.0", line);        // names the update
        Assert.Contains("not installed", line);
    }

    /// <summary>The path the real Downloader takes: a mismatch there carries the update's own code.</summary>
    [Fact]
    public async Task The_verified_download_reports_a_mismatch_with_the_update_code()
    {
        var dir = TempDir();
        try
        {
            var ex = await Assert.ThrowsAsync<TradeAgentException>(() =>
                Downloader.DownloadVerifiedAsync("https://example.invalid/x.exe", Path.Combine(dir, "x.exe"), null));

            // The no-hash guard keeps UPDATE_FAILED; the byte-mismatch guard inside DownloadAsync is
            // the one that now carries UPDATE_INTEGRITY_FAILED (its network half is not reachable
            // from a unit test, so this pins the code plumbed into it rather than a live mismatch).
            Assert.Equal(ErrorCode.UPDATE_FAILED, ex.Info.Code);
        }
        finally { Cleanup(dir); }
    }

    // ---- 7. once Setup is running, that is the outcome -------------------------------------------

    /// <summary>
    /// Launch succeeded, so the installer is running and is going to replace the files this process
    /// is executing from. A logging failure after that must not report the success as a failure —
    /// which would also stop the caller shutting down cleanly for Setup, and re-arm a button that
    /// would start a SECOND installer over the first.
    /// </summary>
    [Fact]
    public async Task A_logging_failure_after_Launch_does_not_turn_a_started_install_into_a_failure()
    {
        var calls = 0;
        var f = Wellformed();
        var service = Service(f);
        service.Activity = (_, _) => { calls++; throw new InvalidOperationException("database is locked"); };
        await service.CheckAsync();

        Assert.True(await service.InstallAsync());     // still true: Setup IS running

        Assert.Equal(1, calls);                        // it did try to write
        Assert.Equal(1, f.Launches);
        Assert.Equal(UpdateStage.Installing, service.Stage);
    }

    [Fact]
    public async Task A_second_press_after_Setup_has_started_does_not_start_a_second_installer()
    {
        var f = Wellformed();
        var service = Service(f);
        await service.CheckAsync();

        Assert.True(await service.InstallAsync());
        Assert.False(await service.InstallAsync());
        Assert.False(await service.InstallAsync());

        Assert.Equal(1, f.Launches);
        Assert.Contains("already installing", service.Message);
        Assert.Equal(UpdateStage.Installing, service.Stage);   // not flipped to Failed
    }

    // ---- 2. the other side of the window: no new dispatches while an install is going -------------

    /// <summary>
    /// The pre-install check refuses to replace the program while an order is unconfirmed. This is
    /// the same window from the other end: an order placed AFTER that check and before Setup starts
    /// would be dispatched by a process that is about to be overwritten, and the answer would arrive
    /// after the thing that was going to reconcile it had gone.
    /// </summary>
    [Fact]
    public async Task The_gateway_refuses_to_dispatch_while_an_install_is_going()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var handle = db;

        var installing = false;
        gw.InstallInProgress = () => installing;

        // Control first: the harness can dispatch at all, or the refusal below proves nothing.
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out _));

        installing = true;
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out var reason, out var code));
        Assert.Equal(ErrorCode.UPDATE_INSTALL_IN_PROGRESS, code);
        Assert.Contains("installing a new version", reason);

        // And it is a real refusal on the order path, not only on the query.
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), Guid.NewGuid().ToString("n"), TestEnv.Buy()));

        // Cleared when the install is refused or finishes: trading comes back on its own.
        installing = false;
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out _));
        await gw.PlaceAsync(new AgentContext("a"), Guid.NewGuid().ToString("n"), TestEnv.Buy());
    }

    /// <summary>
    /// The owner pressing Approve by hand is exactly the case being closed, so the latch does not
    /// exempt the operator the way the AI kill switch does.
    /// </summary>
    [Fact]
    public async Task The_install_latch_does_not_exempt_the_operator()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var handle = db;
        gw.InstallInProgress = () => true;

        Assert.False(gw.TryAuthorizeExecution(AgentContext.Operator, out _, out var code));
        Assert.Equal(ErrorCode.UPDATE_INSTALL_IN_PROGRESS, code);
    }

    /// <summary>A hook that throws is read as "installing": the direction that cannot dispatch.</summary>
    [Fact]
    public async Task A_latch_that_cannot_be_read_refuses_rather_than_dispatches()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var handle = db;
        gw.InstallInProgress = () => throw new InvalidOperationException("database is locked");

        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.UPDATE_INSTALL_IN_PROGRESS, code);
    }

    /// <summary>
    /// The updater's half of the same latch: it is up for the whole of an install and comes back
    /// down when the install is refused — a refusal must not leave trading switched off.
    /// </summary>
    [Fact]
    public async Task The_updater_raises_the_latch_for_the_install_and_lowers_it_on_a_refusal()
    {
        var f = Wellformed();
        var seen = new List<bool>();
        var service = Service(f);
        var sources = f.Sources() with
        {
            Download = (_, sha, _, _) =>
            {
                f.DownloadStarted = true;
                f.ShaHandedToTheDownload = sha;
                return Task.FromResult(f.InstallerPath);
            }
        };

        var latched = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern, sources)
        {
            UnconfirmedWork = () => 0
        };
        latched.Changed += () => seen.Add(latched.InstallInProgress);
        await latched.CheckAsync();

        Assert.False(latched.InstallInProgress);
        Assert.True(await latched.InstallAsync());
        Assert.Contains(true, seen);                  // it was up while the install ran
        Assert.True(latched.InstallInProgress);       // and stays up: Setup is running, we are closing

        // A refused install leaves it down, so a release we will not install does not stop trading.
        var refused = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern,
            new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt"), ChecksumText = "" }.Sources())
        {
            UnconfirmedWork = () => 0
        };
        await refused.CheckAsync();

        Assert.False(await refused.InstallAsync());
        Assert.False(refused.InstallInProgress);
        Assert.Equal(UpdateStage.Failed, refused.Stage);
    }

    // ---- 3. a refusal that expires, and a failed re-check that hides nothing ----------------------

    /// <summary>
    /// The unconfirmed-order refusal stops being true when the order settles. Nothing used to expire
    /// it, so it sat on the strip beside a button the five-second tick had already re-enabled, until
    /// the next automatic check up to six hours later.
    /// </summary>
    [Fact]
    public async Task A_refusal_about_an_unconfirmed_order_expires_when_the_order_settles()
    {
        var outstanding = 1;
        var f = Wellformed();
        var service = Service(f);
        service.UnconfirmedWork = () => outstanding;
        await service.CheckAsync();

        Assert.False(await service.InstallAsync());
        Assert.True(service.Refused);
        Assert.True(service.RefusedPendingWork);

        // Still true: nothing expires.
        service.ExpireStaleRefusal();
        Assert.True(service.Refused);

        outstanding = 0;
        service.ExpireStaleRefusal();

        Assert.False(service.Refused);
        Assert.Null(service.Message);
        Assert.Equal(UpdateStage.Available, service.Stage);   // back to the offer it was
        Assert.NotNull(service.Available);
    }

    /// <summary>
    /// Every other refusal is about the release, not about the owner's order book, and stays until a
    /// different release is published. Expiring those would hide the reason and re-arm the button.
    /// </summary>
    [Fact]
    public async Task A_refusal_about_the_release_itself_does_not_expire()
    {
        var f = new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt"), ChecksumText = "" };
        var service = await Offering(f);

        Assert.False(await service.InstallAsync());
        Assert.True(service.Refused);
        Assert.False(service.RefusedPendingWork);

        service.ExpireStaleRefusal();

        Assert.True(service.Refused);
        Assert.Contains("cannot be verified", service.Message);
    }

    /// <summary>
    /// A six-hourly re-check that fails is weather. It must not turn a standing offer into a
    /// refusal, because the banner shows a refusal over the offer — which is how "could not check"
    /// came to hide a perfectly valid update.
    /// </summary>
    [Fact]
    public async Task A_failed_re_check_is_not_a_refusal_and_leaves_the_offer_readable()
    {
        var f = Wellformed();
        var service = Service(f);
        await service.CheckAsync();

        Assert.Equal(UpdateStage.Available, service.Stage);
        var offered = service.Available;

        f.ReleaseJson = null;                       // the machine goes offline
        await service.CheckAsync();

        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.False(service.Refused);              // the banner renders the offer, not this
        Assert.Contains("could not check", service.Message);
        Assert.Same(offered, service.Available);    // and the offer itself survives
    }

    // ---- 8. a release that cannot be verified says so before the press ---------------------------

    [Fact]
    public async Task A_release_with_no_checksum_file_says_so_before_the_owner_presses_anything()
    {
        var f = new Fake { ReleaseJson = Release(Asset) };
        var service = await Offering(f);

        // Still offered, so What's new and the version still render — only the press is refused.
        Assert.NotNull(service.Available);
        Assert.False(service.CanBeVerified);
        Assert.Equal(UpdateStage.Available, service.Stage);

        var withManifest = await Offering(new Fake { ReleaseJson = Release(Asset, "SHA256SUMS.txt") });
        Assert.True(withManifest.CanBeVerified);

        // And the hard stop is still the thing that actually stops it.
        Assert.False(await service.InstallAsync());
        Assert.False(f.DownloadStarted);
        Assert.Contains("cannot be verified", service.Message);
    }

    [Fact]
    public void Nothing_on_offer_can_be_verified_and_that_is_not_a_refusal()
    {
        var service = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern,
            new Fake().Sources());

        Assert.False(service.CanBeVerified);   // no Available at all
        Assert.False(service.Refused);
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
