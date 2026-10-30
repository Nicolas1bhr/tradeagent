using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE RELEASE CHANNEL AND THE RELEASE THE WORKFLOW CUTS (<c>U-windows-release</c>). A release cut in CI carries the installer of a
/// build WITHOUT the ATAS adapter, named for it, and one SHA256SUMS.txt naming that installer and the Linux kit. The updater takes
/// exactly the installer of its own kind (an update never takes ATAS away, nor adds a product nobody installed), refuses a release
/// carrying only the other kind in words, and installs the CI release only against the hash its manifest names.
///
/// <para>The production default is under test, not a pattern handed in: every service here is built the way <c>AppHost</c> builds
/// it — no asset pattern — with the kind of build passed explicitly, or measured from this test process's own (absent) bridge.</para>
/// </summary>
public class UpdateChannelTests
{
    const string Hash = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";
    const string KitHash = "3f79bb7b435b05321651daefd374cdc681dc06faa65e374e38337b88ca046dea";
    const string Kit = "TradeAgent-0.2.0-linux-x64.tar.gz";

    /// <summary>The SHA256SUMS.txt exactly as <c>.github/ci/release-assets.sh</c> writes it: one line per asset, two spaces, bare names.</summary>
    const string CiManifest = Hash + "  " + UpdateChannel.WithoutAtas + "\n" + KitHash + "  " + Kit + "\n";

    static string Release(params string[] assets) =>
        $$"""
        {
          "tag_name": "v0.2.0", "draft": false, "prerelease": false,
          "html_url": "https://github.com/owner/repo/releases/tag/v0.2.0", "body": "notes",
          "assets": [
            {{string.Join(",\n", assets.Select(a =>
                $$"""{"name": "{{a}}", "size": 90000000, "browser_download_url": "https://example.invalid/{{a}}"}"""))}}
          ]
        }
        """;

    sealed class Fake
    {
        public string? ReleaseJson;
        public string? ChecksumText;
        public string? ShaHandedToTheDownload;
        public string? DownloadedName;
        public string? Launched;
        public string FileOnDisk = Hash;

        public UpdateSources Sources() => new(
            _ => Task.FromResult(ReleaseJson),
            (_, _) => Task.FromResult(ChecksumText),
            (info, sha, _, _) =>
            {
                ShaHandedToTheDownload = sha;
                DownloadedName = info.AssetName;
                return Task.FromResult("C:/updates/0.2.0/" + info.AssetName);
            },
            path => Launched = path,
            (_, _) => Task.FromResult(FileOnDisk));
    }

    static UpdateService Service(Fake f, bool? atasAdapter) =>
        new("0.1.2", "owner/repo", sources: f.Sources(), atasAdapter: atasAdapter) { UnconfirmedWork = () => 0 };

    [Fact]
    public async Task A_build_with_ATAS_is_never_offered_the_installer_without_it()
    {
        var f = new Fake { ReleaseJson = Release(UpdateChannel.WithoutAtas, Kit, "SHA256SUMS.txt"), ChecksumText = CiManifest };
        var service = Service(f, atasAdapter: true);
        await service.CheckAsync();

        Assert.Null(service.Available);
        Assert.True(service.Refused);
        Assert.Contains("0.2.0", service.Message);
        Assert.Contains("ATAS", service.Message);
        Assert.False(await service.InstallAsync());
        Assert.Null(f.ShaHandedToTheDownload);
        Assert.Null(f.Launched);
    }

    [Theory]
    [InlineData(true, UpdateChannel.WithAtas)]
    [InlineData(false, UpdateChannel.WithoutAtas)]
    public async Task A_release_carrying_both_kinds_gives_each_build_its_own(bool atasAdapter, string expected)
    {
        var f = new Fake { ReleaseJson = Release(UpdateChannel.WithAtas, UpdateChannel.WithoutAtas, "SHA256SUMS.txt") };
        var service = Service(f, atasAdapter);
        await service.CheckAsync();

        Assert.False(service.Refused, service.Message);
        Assert.Equal(expected, service.Available?.AssetName);
    }

    [Fact]
    public async Task A_build_without_ATAS_installs_the_CI_release_against_the_hash_its_manifest_names()
    {
        var f = new Fake { ReleaseJson = Release(UpdateChannel.WithoutAtas, Kit, "SHA256SUMS.txt", "SHA256SUMS"), ChecksumText = CiManifest };
        var service = Service(f, atasAdapter: false);
        await service.CheckAsync();

        Assert.Equal(UpdateChannel.WithoutAtas, service.Available?.AssetName);
        Assert.True(await service.InstallAsync(), service.Message);
        Assert.Equal(Hash, f.ShaHandedToTheDownload);
        Assert.Equal(UpdateChannel.WithoutAtas, f.DownloadedName);
        Assert.EndsWith(UpdateChannel.WithoutAtas, f.Launched);
    }

    [Fact]
    public async Task The_CI_release_whose_installer_is_not_the_file_its_manifest_names_is_not_started()
    {
        var f = new Fake
        {
            ReleaseJson = Release(UpdateChannel.WithoutAtas, Kit, "SHA256SUMS.txt"), ChecksumText = CiManifest, FileOnDisk = KitHash
        };
        var service = Service(f, atasAdapter: false);
        await service.CheckAsync();

        Assert.False(await service.InstallAsync());
        Assert.Null(f.Launched);
        Assert.True(service.Refused);
    }

    [Fact]
    public async Task The_production_default_reads_the_kind_from_this_build_and_this_one_has_no_adapter()
    {
        // This test process has no bridge\ beside it, so it is a build without the adapter, and its updater takes the CI installer.
        Assert.False(BuildContents.AtasAdapterInThisBuild);
        if (Environment.GetEnvironmentVariable("TRADEAGENT_UPDATE_ASSET") is not null) return;

        var f = new Fake { ReleaseJson = Release(UpdateChannel.WithAtas, UpdateChannel.WithoutAtas, "SHA256SUMS.txt") };
        var service = new UpdateService("0.1.2", "owner/repo", sources: f.Sources()) { UnconfirmedWork = () => 0 };
        await service.CheckAsync();

        Assert.Equal(UpdateChannel.WithoutAtas, service.Available?.AssetName);
    }

    [Fact]
    public void The_adapter_is_read_from_the_bridge_assembly_and_nothing_else()
    {
        var root = Path.Combine(TestEnv.Home, "build-contents", Guid.NewGuid().ToString("n"));
        var with = Path.Combine(root, "with");
        var without = Path.Combine(root, "without");
        Directory.CreateDirectory(with);
        Directory.CreateDirectory(without);
        File.WriteAllBytes(Path.Combine(with, BuildContents.BridgeAssembly), [0x4D, 0x5A, .. "..AtasStrategyAdapter.."u8.ToArray(), 0]);
        // The name as UTF-16, the way a string literal is stored: not the type in the metadata, so not the adapter.
        File.WriteAllBytes(Path.Combine(without, BuildContents.BridgeAssembly),
            [0x4D, 0x5A, .. System.Text.Encoding.Unicode.GetBytes("AtasStrategyAdapter"), 0]);

        Assert.True(BuildContents.AtasAdapterIn(with));
        Assert.False(BuildContents.AtasAdapterIn(without));
        Assert.False(BuildContents.AtasAdapterIn(Path.Combine(root, "missing")));
    }

    /// <summary>
    /// A build without the adapter refuses ATAS at the one switch every surface presses, in the installer's words, before anything is
    /// written or torn down; onboarding offers TradeAgent's own paper venue in its place.
    /// </summary>
    [Fact]
    public async Task A_build_without_the_adapter_refuses_ATAS_at_the_switch_in_the_installers_words()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            host.AtasInThisBuild = false;
            var before = db.GetKv("connector");
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SwitchConnectorAsync("atas"));
            Assert.Equal("ATAS is not in this build; paper runs on TradeAgent's own venue.", refused.Message);
            Assert.Equal(before, db.GetKv("connector"));
        }
        finally { await host.Gateway.DisposeAsync(); }
    }
}
