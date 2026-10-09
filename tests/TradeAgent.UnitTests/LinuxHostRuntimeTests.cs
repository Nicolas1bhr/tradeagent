using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Provisioning;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE AI RUNTIMES ON A LINUX HOST (<c>U-linux-host</c> item 1). The built-in install plans named Windows
/// archives on every computer, so a Linux host unpacked <c>codex.exe</c> and reported a runtime "installed but
/// would not run"; Codex signed in through a <c>localhost</c> callback a headless host cannot complete; and
/// the npm fallback refused off Windows with a sentence telling the owner to install Node.js himself — a
/// terminal, in words.
///
/// <para>Every request here goes to a loopback <see cref="FakeArchive"/> standing in for GitHub's release API
/// and its downloads, answering with the asset names both releases carried on 2026-10-09, and the programs
/// installed are stand-ins that print a version: nothing is downloaded and nothing of a vendor's is run.</para>
/// </summary>
public class LinuxHostRuntimeTests(ITestOutputHelper output)
{
    /// <summary>
    /// THE NAMES GITHUB'S API LISTED, read through it on 2026-10-09 for <c>openai/codex</c> rust-v0.162.0 and
    /// <c>anomalyco/opencode</c> v1.18.35: the archives of every platform and the near-misses beside the Linux
    /// one — a <c>.sigstore</c>, a <c>.tar.zst</c>, a bare binary, a musl and an AVX2-free build — in the API's
    /// own order, so a plan that matched loosely would take the first wrong one.
    /// </summary>
    static readonly IReadOnlyDictionary<string, string[]> Listed = new Dictionary<string, string[]>
    {
        ["openai/codex"] =
        [
            "codex-npm-linux-x64-0.162.0.tgz",
            "codex-package-aarch64-pc-windows-msvc.tar.gz",
            "codex-package-aarch64-unknown-linux-musl.tar.gz.sigstore",
            "codex-package-aarch64-unknown-linux-musl.tar.gz",
            "codex-package-aarch64-unknown-linux-musl.tar.zst",
            "codex-package-x86_64-pc-windows-msvc.tar.gz",
            "codex-package-x86_64-unknown-linux-musl.tar.gz.sigstore",
            "codex-package-x86_64-unknown-linux-musl.tar.gz",
            "codex-package-x86_64-unknown-linux-musl.tar.zst",
            "codex-x86_64-unknown-linux-musl.tar.gz"
        ],
        ["anomalyco/opencode"] =
        [
            "opencode-desktop-linux-x86_64.AppImage",
            "opencode-linux-arm64-musl.tar.gz",
            "opencode-linux-arm64.tar.gz",
            "opencode-linux-x64-baseline-musl.tar.gz",
            "opencode-linux-x64-baseline.tar.gz",
            "opencode-linux-x64-musl.tar.gz",
            "opencode-linux-x64.tar.gz",
            "opencode-windows-arm64.zip",
            "opencode-windows-x64.zip"
        ]
    };

    /// <summary>The Linux archive each runtime must ask for, by processor — the brief's names, measured on the day.</summary>
    static string LinuxAsset(string runtime, Architecture cpu) => (runtime, cpu) switch
    {
        ("codex", Architecture.X64) => "codex-package-x86_64-unknown-linux-musl.tar.gz",
        ("codex", Architecture.Arm64) => "codex-package-aarch64-unknown-linux-musl.tar.gz",
        ("opencode", Architecture.X64) => "opencode-linux-x64.tar.gz",
        ("opencode", Architecture.Arm64) => "opencode-linux-arm64.tar.gz",
        _ => throw new ArgumentOutOfRangeException(nameof(runtime), $"{runtime} on {cpu}")
    };

    /// <summary>
    /// (a) ON LINUX EACH RUNTIME INSTALLS ITS LINUX ARCHIVE: the plan asks for exactly the Linux asset of this
    /// processor, unpacks it in the layout the vendor packs it in — <c>bin/codex</c> from Codex's
    /// <c>scripts/codex_package/layout.py</c>, <c>opencode</c> at the root from OpenCode's
    /// <c>packages/opencode/script/build.ts</c>, both at the tag — and the program it unpacked runs. Before this
    /// unit the Windows asset was asked for, on every computer.
    /// </summary>
    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public async Task On_linux_each_runtime_installs_its_linux_archive(Architecture cpu)
    {
        // The stand-in programs are shell scripts, so the install that ends by running one is proven where one
        // runs. The plan's choice of asset is data, and that is proven below on every computer.
        if (OperatingSystem.IsWindows()) return;

        var downloads = RuntimeCatalog.BuiltIn(new HostPlatform(HostOs.Linux, cpu))
            .Where(m => m.Install.Kind == InstallKind.Download).ToList();
        Assert.Equal(new[] { "opencode", "codex" }, downloads.Select(m => m.Id));

        foreach (var manifest in downloads)
        {
            var runtime = manifest.Id;
            var wanted = LinuxAsset(runtime, cpu);
            using var fake = new FakeArchive();
            PublishRelease(fake, manifest.Install.GitHubRepo!);
            fake.PublishFile($"/download/{wanted}", runtime == "codex"
                ? TarGz(("bin/", null), ("bin/codex", Program("codex-cli 9.9.2")), ("codex-package.json", null))
                : TarGz(("opencode", Program("9.9.3"))));

            // Its own folder under tools/, so nothing another test installs is found instead.
            manifest.Id = $"{runtime}-{cpu}-{Guid.NewGuid():n}"[..32].ToLowerInvariant();
            var rt = new CliAgentRuntime(manifest) { ReleaseApi = fake.BaseUrl };

            RuntimeDetection detected;
            try { detected = await rt.InstallAsync(); }
            finally { foreach (var mark in fake.Marks) output.WriteLine($"{runtime}: {mark}"); }

            Assert.True(detected.Installed, $"{runtime} on linux-{cpu} was not installed: {detected.Reason}");
            Assert.Equal(runtime == "codex" ? "9.9.2" : "9.9.3", detected.Version);
            Assert.Equal(Path.Combine(Paths.Tools, manifest.Id,
                manifest.Install.ExecutableInArchive!.Replace('/', Path.DirectorySeparatorChar)), detected.Path);
            Assert.Equal(new[] { $"/download/{wanted}" }, Downloads(fake));
        }
    }

    /// <summary>
    /// (b) THE DEVICE-AUTH PROMPT YIELDS ITS LINK AND CODE. A headless host signs Codex in with
    /// <c>codex login --device-auth</c>, which prints a link and a one-time code for another device. This is the
    /// vendor's prompt as <c>codex-rs/login/src/device_code_auth.rs</c> prints it at rust-v0.162.0, ANSI colour
    /// included, with a code of the one shape measured — <c>AAA9-AA99A</c>, four characters and five, from one
    /// <c>--device-auth</c> run of codex-cli 0.160.1 on 2026-10-09, stopped at the code and never authorised.
    /// The app's pattern was <c>XXXX-XXXX</c>, so the screen got a link and no code; and it returned at the
    /// link, before the code's line had been read.
    /// </summary>
    [Fact]
    public async Task The_device_auth_prompt_yields_its_link_and_code()
    {
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.LinuxX64).Single(m => m.Id == "codex");
        Assert.Equal(new[] { "login", "--device-auth" }, codex.AuthArgs);

        codex.Executable = DeviceAuthStub(VendorPrompt("https://auth.openai.com/codex/device", "QRM4-KX27P"));
        // ITS OWN PRESENCE REGISTER, never the process-wide one: the login it starts is an agent process, the shared
        // register is sticky, and every later scan in this test process would read an owner's file as the agent's
        // (CI run 37921982682: MaterialLedgerTests red on all three runners).
        var rt = new CliAgentRuntime(codex, presence: new AgentPresence());
        try
        {
            var challenge = await rt.BeginAuthenticationAsync();
            output.WriteLine($"url {challenge.Url} · code {challenge.Code} · {challenge.Message}");

            Assert.Equal("https://auth.openai.com/codex/device", challenge.Url);
            Assert.Equal("QRM4-KX27P", challenge.Code);
        }
        finally { await rt.StopAsync(); }
    }

    /// <summary>
    /// (c) OFF WINDOWS NO REFUSAL TELLS THE OWNER TO INSTALL ANYTHING. TradeAgent's own Node.js is a Windows
    /// zip, and off Windows its refusal read "install Node.js yourself"; the npm fallback that follows a failed
    /// archive reached exactly that sentence. Now the private Node's refusal instructs nobody, the fallback
    /// refuses by naming the archive's own failure, and a computer with no plan at all is told so — none of them
    /// with a "yourself", a "manually", a terminal or a download page.
    /// </summary>
    [Fact]
    public async Task Off_windows_no_refusal_tells_the_owner_to_install_anything()
    {
        if (OperatingSystem.IsWindows()) return;

        // TradeAgent's own Node.js, asked for directly. It refuses before it asks anything of anyone.
        var node = await Assert.ThrowsAsync<TradeAgentException>(() => NodeRuntime.InstallAsync());
        output.WriteLine("node: " + node.Message);
        TellsNobodyToInstall(node);

        // The archive fails — the release list and the download both answer 404 — and the plan declares an
        // npm package. The refusal is the archive's failure, said as one.
        using (var fake = new FakeArchive())
        {
            var codex = RuntimeCatalog.BuiltIn(HostPlatform.LinuxX64).Single(m => m.Id == "codex");
            Assert.False(string.IsNullOrEmpty(codex.Install.NpmPackage));
            codex.Id = $"codex-refusal-{Guid.NewGuid():n}"[..24];
            codex.Install.Url = $"{fake.BaseUrl}/download/{LinuxAsset("codex", Architecture.X64)}";
            var refused = await Assert.ThrowsAsync<TradeAgentException>(
                () => new CliAgentRuntime(codex) { ReleaseApi = fake.BaseUrl }.InstallAsync());
            output.WriteLine("archive: " + refused.Message);
            TellsNobodyToInstall(refused);
            Assert.Contains("was refused by the server (404)", refused.Message, StringComparison.Ordinal);
        }

        // A computer no built-in plan is for.
        using (var fake = new FakeArchive())
        {
            var codex = RuntimeCatalog.BuiltIn(new HostPlatform(HostOs.Other, Architecture.X64)).Single(m => m.Id == "codex");
            codex.Id = $"codex-noplan-{Guid.NewGuid():n}"[..24];
            if (codex.Install.Url is not null) codex.Install.Url = $"{fake.BaseUrl}/download/none";
            var refused = await Assert.ThrowsAsync<TradeAgentException>(
                () => new CliAgentRuntime(codex) { ReleaseApi = fake.BaseUrl }.InstallAsync());
            output.WriteLine("no plan: " + refused.Message);
            TellsNobodyToInstall(refused);
        }
    }

    // ---- the refusal's words -----------------------------------------------------------------------

    static readonly Regex Instruction =
        new(@"\byourself\b|\bby hand\b|\bmanual(ly)?\b|\bterminal\b|\bcommand line\b|\bnpm\b|https?://", RegexOptions.IgnoreCase);

    static void TellsNobodyToInstall(TradeAgentException refused)
    {
        Assert.Equal(ErrorCode.AI_INSTALL_FAILED, refused.Code);
        Assert.False(Instruction.IsMatch(refused.Message),
            $"a refusal tells the owner to do something about it by hand: \"{refused.Message}\"");
    }

    // ---- the loopback release --------------------------------------------------------------------

    /// <summary>GitHub's "latest release" answer for the repository, its assets downloadable from the fake.</summary>
    static void PublishRelease(FakeArchive fake, string repo) =>
        fake.PublishAt($"/repos/{repo}/releases/latest", JsonSerializer.Serialize(new
        {
            tag_name = repo == "openai/codex" ? "rust-v0.162.0" : "v1.18.35",
            assets = Listed[repo].Select(name => new
            {
                name,
                browser_download_url = $"{fake.BaseUrl}/download/{name}",
                size = 1
            })
        }));

    /// <summary>The paths of every download the fake was asked for, in order.</summary>
    static string[] Downloads(FakeArchive fake) =>
        [.. fake.Marks.Select(m => Regex.Match(m, @"srv got GET (/download/\S+)")).Where(m => m.Success).Select(m => m.Groups[1].Value)];

    /// <summary>A program that answers <c>--version</c> the way the vendor's does, and nothing else.</summary>
    static string Program(string version) => $"#!/bin/sh\necho '{version}'\n";

    /// <summary>
    /// A .tar.gz with these entries in this order: a name ending in <c>/</c> is a folder, any other a file,
    /// executable when it has text and empty when it has none — the way the vendors' own archives hold them.
    /// </summary>
    static byte[] TarGz(params (string Name, string? Text)[] entries)
    {
        using var into = new MemoryStream();
        using (var gz = new GZipStream(into, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            const UnixFileMode Runnable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                          | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                          | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            foreach (var (name, text) in entries)
            {
                if (name.EndsWith('/'))
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name.TrimEnd('/')) { Mode = Runnable });
                    continue;
                }
                var bytes = Encoding.UTF8.GetBytes(text ?? "{}");
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    Mode = text is null ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead : Runnable,
                    DataStream = new MemoryStream(bytes)
                });
            }
        }
        return into.ToArray();
    }

    // ---- the device-auth stand-in ------------------------------------------------------------------

    const string Esc = "\u001b";

    /// <summary>
    /// <c>device_code_prompt</c> of <c>codex-rs/login/src/device_code_auth.rs</c> at rust-v0.162.0, character for
    /// character, printed with <c>println!</c>; the same text codex-cli 0.160.1 printed on 2026-10-09.
    /// </summary>
    static string VendorPrompt(string url, string code) =>
        $"\nWelcome to Codex [v{Esc}[90m0.162.0{Esc}[0m]\n{Esc}[90mOpenAI's command-line coding agent{Esc}[0m\n" +
        "\nFollow these steps to sign in with ChatGPT using device code authorization:\n" +
        $"\n1. Open this link in your browser and sign in to your account\n   {Esc}[94m{url}{Esc}[0m\n" +
        $"\n2. Enter this one-time code {Esc}[90m(expires in 15 minutes){Esc}[0m\n   {Esc}[94m{code}{Esc}[0m\n" +
        $"\n{Esc}[90mContinue only if you started this login in Codex. If a website or another person gave you this code, cancel.{Esc}[0m\n" +
        "\n";

    /// <summary>
    /// A program that prints the prompt and then waits, as codex does while it polls for the code to be
    /// entered. Stopped by the runtime's own StopAsync, which ends the sign-in it started.
    /// </summary>
    static string DeviceAuthStub(string prompt)
    {
        var dir = Path.Combine(TestEnv.Home, "device-auth", Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(dir);
        var text = Path.Combine(dir, "prompt.txt");
        File.WriteAllText(text, prompt, new UTF8Encoding(false));

        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(dir, "codex.cmd");
            File.WriteAllText(cmd, $"@echo off\r\ntype \"{text}\"\r\nping -n 120 127.0.0.1 > nul\r\n");
            return cmd;
        }

        var sh = Path.Combine(dir, "codex");
        File.WriteAllText(sh, $"#!/bin/sh\ncat '{text}'\nexec sleep 120\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }
}
