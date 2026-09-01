using TradeAgent.Core;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The updater replaces the program that holds the user's open orders, so these tests are aimed at
/// the four ways it could do harm rather than at the happy path: offering a build that is older than
/// the one running, offering a release whose installer never uploaded, running an installer whose
/// bytes did not match the publisher's checksum, and installing anything at all without being asked.
/// </summary>
public class UpdateTests
{
    // ---- version ordering ------------------------------------------------------------------

    [Theory]
    [InlineData("0.2.0", 0, 2, 0, "")]
    [InlineData("v0.2.0", 0, 2, 0, "")]
    [InlineData("V1.2.3", 1, 2, 3, "")]
    [InlineData("2", 2, 0, 0, "")]
    [InlineData("2.1", 2, 1, 0, "")]
    [InlineData("1.0.0-rc.2", 1, 0, 0, "rc.2")]
    [InlineData("1.0.0+build.5", 1, 0, 0, "")]
    public void A_release_tag_is_read_the_way_it_was_written(string tag, int major, int minor, int patch, string pre)
    {
        Assert.True(UpdateVersion.TryParse(tag, out var v));
        Assert.Equal(new UpdateVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("release-2026-09-01")]
    [InlineData("v")]
    [InlineData("1.2.3.4")]
    [InlineData("99999999999")]
    public void A_tag_that_is_not_a_version_is_refused_rather_than_guessed(string tag)
    {
        Assert.False(UpdateVersion.TryParse(tag, out _));
    }

    [Fact]
    public void A_finished_release_outranks_its_own_pre_releases()
    {
        UpdateVersion.TryParse("1.0.0", out var release);
        UpdateVersion.TryParse("1.0.0-rc.1", out var candidate);
        Assert.True(release.CompareTo(candidate) > 0);
        Assert.True(candidate.CompareTo(release) < 0);
    }

    [Fact]
    public void Ordering_is_numeric_and_not_alphabetical()
    {
        UpdateVersion.TryParse("0.10.0", out var ten);
        UpdateVersion.TryParse("0.9.0", out var nine);
        // "0.10.0" sorts BEFORE "0.9.0" as text. That is the bug this exists to prevent.
        Assert.True(ten.CompareTo(nine) > 0);
    }

    // ---- what counts as an update ----------------------------------------------------------

    static UpdateVersion Current(string text)
    {
        UpdateVersion.TryParse(text, out var v);
        return v;
    }

    static string Release(string tag, string[] assets, bool draft = false, bool prerelease = false, string body = "notes") =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/owner/repo/releases/tag/{{tag}}",
          "body": "{{body}}",
          "assets": [
            {{string.Join(",\n", assets.Select(a =>
                $$"""{"name": "{{a}}", "size": 90000000, "browser_download_url": "https://example.invalid/{{a}}"}"""))}}
          ]
        }
        """;

    [Fact]
    public void A_newer_release_with_an_installer_is_offered()
    {
        var found = ReleaseFeed.Parse(
            Release("v0.2.0", ["TradeAgent-Setup-x64.exe", "SHA256SUMS.txt"]),
            Current("0.1.0"), UpdateService.DefaultAssetPattern);

        Assert.NotNull(found);
        Assert.Equal("0.2.0", found.Version);
        Assert.Equal("v0.2.0", found.Tag);
        Assert.Equal("TradeAgent-Setup-x64.exe", found.AssetName);
        Assert.Equal("https://example.invalid/TradeAgent-Setup-x64.exe", found.DownloadUrl);
        Assert.Equal("https://example.invalid/SHA256SUMS.txt", found.ChecksumUrl);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v0.2.0", found.ReleaseUrl);
    }

    [Theory]
    [InlineData("v0.1.0")]   // the one already running
    [InlineData("v0.0.9")]   // older
    public void A_release_that_is_not_newer_is_never_offered(string tag)
    {
        Assert.Null(ReleaseFeed.Parse(
            Release(tag, ["TradeAgent-Setup-x64.exe"]), Current("0.1.0"), UpdateService.DefaultAssetPattern));
    }

    [Fact]
    public void A_draft_or_a_pre_release_is_not_an_update()
    {
        var current = Current("0.1.0");
        Assert.Null(ReleaseFeed.Parse(Release("v0.2.0", ["TradeAgent-Setup-x64.exe"], draft: true),
            current, UpdateService.DefaultAssetPattern));
        Assert.Null(ReleaseFeed.Parse(Release("v0.2.0", ["TradeAgent-Setup-x64.exe"], prerelease: true),
            current, UpdateService.DefaultAssetPattern));
    }

    /// <summary>
    /// The case that matters most. A release whose assets failed to upload still answers the API
    /// perfectly; offering it puts a button on screen that can only fail after a download.
    /// </summary>
    [Fact]
    public void A_release_with_no_installer_in_it_is_not_an_update()
    {
        Assert.Null(ReleaseFeed.Parse(
            Release("v0.2.0", ["SHA256SUMS.txt", "source.zip"]), Current("0.1.0"), UpdateService.DefaultAssetPattern));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"Not Found\"}")]
    public void A_malformed_or_empty_answer_is_never_an_update(string json)
    {
        Assert.Null(ReleaseFeed.Parse(json, Current("0.1.0"), UpdateService.DefaultAssetPattern));
    }

    // ---- checksums --------------------------------------------------------------------------

    const string Hash = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";

    [Theory]
    [InlineData("{0}  artifacts/TradeAgent-Setup-x64.exe")]   // what packaging/build.ps1 writes
    [InlineData("{0}  artifacts\\TradeAgent-Setup-x64.exe")]  // the same, on a Windows-style path
    [InlineData("{0} *TradeAgent-Setup-x64.exe")]             // sha256sum's binary marker
    [InlineData("{0}  TradeAgent-Setup-x64.exe")]
    public void The_hash_is_found_however_the_manifest_names_the_file(string line)
    {
        Assert.Equal(Hash, ChecksumManifest.Find(string.Format(line, Hash), "TradeAgent-Setup-x64.exe"));
    }

    [Fact]
    public void A_manifest_that_does_not_mention_the_file_yields_no_hash()
    {
        var manifest = $"{Hash}  artifacts/tradeagent-gateway.exe\n";
        Assert.Null(ChecksumManifest.Find(manifest, "TradeAgent-Setup-x64.exe"));
        Assert.Null(ChecksumManifest.Find(null, "TradeAgent-Setup-x64.exe"));
        Assert.Null(ChecksumManifest.Find("nonsense line\n", "TradeAgent-Setup-x64.exe"));
    }

    [Fact]
    public void Something_that_is_not_a_sha256_is_not_treated_as_one()
    {
        Assert.Null(ChecksumManifest.Find("deadbeef  TradeAgent-Setup-x64.exe", "TradeAgent-Setup-x64.exe"));
    }

    // ---- the service ------------------------------------------------------------------------

    sealed class Recorder
    {
        public string? ShaHandedToTheDownload;
        public string? Launched;
        public bool DownloadFails;
        public string? ReleaseJson;
        public string? ChecksumText;

        public UpdateSources Sources() => new(
            _ => Task.FromResult(ReleaseJson),
            (_, _) => Task.FromResult(ChecksumText),
            (info, sha, _, _) =>
            {
                ShaHandedToTheDownload = sha;
                if (DownloadFails)
                    throw new TradeAgentException(ErrorCode.UPDATE_FAILED,
                        "the downloaded file did not match the publisher's checksum");
                return Task.FromResult($"C:/updates/{info.AssetName}");
            },
            path => Launched = path);
    }

    static UpdateService Service(Recorder r, string current = "0.1.0") =>
        new(current, "owner/repo", UpdateService.DefaultAssetPattern, r.Sources());

    [Fact]
    public async Task An_offline_machine_reports_that_it_could_not_ask_and_offers_nothing()
    {
        var r = new Recorder { ReleaseJson = null };
        var service = Service(r);

        await service.CheckAsync();

        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Null(service.Available);
        Assert.False(service.ShouldPrompt);
        Assert.Contains("could not check", service.Message);
    }

    [Fact]
    public async Task The_newest_build_already_installed_prompts_nobody()
    {
        var r = new Recorder { ReleaseJson = Release("v0.1.0", ["TradeAgent-Setup-x64.exe"]) };
        var service = Service(r);

        await service.CheckAsync();

        Assert.Equal(UpdateStage.UpToDate, service.Stage);
        Assert.Null(service.Available);
        Assert.False(service.ShouldPrompt);
        Assert.NotNull(service.LastCheckedUtc);
    }

    [Fact]
    public async Task Later_hides_the_prompt_without_pretending_the_update_went_away()
    {
        var r = new Recorder { ReleaseJson = Release("v0.2.0", ["TradeAgent-Setup-x64.exe"]) };
        var service = Service(r);

        await service.CheckAsync();
        Assert.True(service.ShouldPrompt);

        service.Dismiss();
        Assert.False(service.ShouldPrompt);
        Assert.NotNull(service.Available);   // still on offer in Settings

        // A DIFFERENT release is a different question, so it asks again.
        r.ReleaseJson = Release("v0.3.0", ["TradeAgent-Setup-x64.exe"]);
        await service.CheckAsync();
        Assert.True(service.ShouldPrompt);
        Assert.Equal("0.3.0", service.Available!.Version);
    }

    [Fact]
    public async Task Nothing_is_downloaded_or_installed_until_the_user_asks()
    {
        var r = new Recorder { ReleaseJson = Release("v0.2.0", ["TradeAgent-Setup-x64.exe"]) };
        var service = Service(r);

        await service.CheckAsync();

        Assert.Null(r.ShaHandedToTheDownload);
        Assert.Null(r.Launched);
        Assert.Equal(UpdateStage.Available, service.Stage);
    }

    [Fact]
    public async Task Installing_verifies_against_the_published_checksum_before_it_runs_anything()
    {
        var r = new Recorder
        {
            ReleaseJson = Release("v0.2.0", ["TradeAgent-Setup-x64.exe", "SHA256SUMS.txt"]),
            ChecksumText = $"{Hash}  artifacts/TradeAgent-Setup-x64.exe\n"
        };
        var service = Service(r);

        await service.CheckAsync();
        Assert.True(await service.InstallAsync());

        Assert.Equal(Hash, r.ShaHandedToTheDownload);
        Assert.Equal("C:/updates/TradeAgent-Setup-x64.exe", r.Launched);
        Assert.Equal(UpdateStage.Installing, service.Stage);
    }

    [Fact]
    public async Task A_download_that_fails_verification_runs_nothing_and_says_so()
    {
        var r = new Recorder
        {
            ReleaseJson = Release("v0.2.0", ["TradeAgent-Setup-x64.exe", "SHA256SUMS.txt"]),
            ChecksumText = $"{Hash}  TradeAgent-Setup-x64.exe\n",
            DownloadFails = true
        };
        var service = Service(r);

        await service.CheckAsync();
        Assert.False(await service.InstallAsync());

        Assert.Null(r.Launched);
        Assert.Equal(UpdateStage.Failed, service.Stage);
        Assert.Contains("could not be installed", service.Message);
    }

    [Fact]
    public async Task A_release_without_a_checksum_file_still_installs_without_inventing_one()
    {
        var r = new Recorder { ReleaseJson = Release("v0.2.0", ["TradeAgent-Setup-x64.exe"]) };
        var service = Service(r);

        await service.CheckAsync();
        Assert.True(await service.InstallAsync());

        Assert.Null(r.ShaHandedToTheDownload);
        Assert.Equal("C:/updates/TradeAgent-Setup-x64.exe", r.Launched);
    }

    [Fact]
    public async Task Installing_nothing_is_a_no_op_rather_than_a_crash()
    {
        var r = new Recorder { ReleaseJson = Release("v0.1.0", ["TradeAgent-Setup-x64.exe"]) };
        var service = Service(r);

        await service.CheckAsync();
        Assert.False(await service.InstallAsync());
        Assert.Null(r.Launched);
    }

    [Fact]
    public void A_malformed_repository_override_falls_back_to_the_real_one()
    {
        Assert.Equal("owner/repo", new UpdateService("0.1.0", "owner/repo").Repository);
        Assert.Equal(UpdateService.DefaultRepository, new UpdateService("0.1.0", "https://example.invalid/x").Repository);
        Assert.Equal(UpdateService.DefaultRepository, new UpdateService("0.1.0", "owner/repo; rm -rf /").Repository);
        Assert.Equal(UpdateService.DefaultRepository, new UpdateService("0.1.0", "  ").Repository);
    }
}
