using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class CliCodeModeTests
{
    // ---- (D) the second vendor program ----------------------------------------------------------------------------------

    /// <summary>
    /// (D) GUARD: A CLI SEAT WHOSE <c>codex-code-mode-host</c> IS MISSING IS REFUSED IN WORDS. With the host on, the CLI class runs the
    /// vendor's second program beside codex; a seat that would run without it fails every exec call closed, as attempt 4 did. Red at
    /// base: nothing looked for it, and the seat was admitted.
    /// </summary>
    [Fact]
    public async Task A_cli_seat_whose_code_mode_host_is_missing_is_refused_in_words()
    {
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_cli_seat_whose_code_mode_host_is_missing_is_refused_in_words));
        foreach (var beside in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "codex-code-mode-host*")) File.Delete(beside);
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "role-home")).FullName;
        var argv = AgentArgs.For(codex, "", resuming: false, "gpt-6-luna");

        var verdict = await SeatProfile.VerifyAsync(codex, exe, argv, workspace, new SeatHomeTests.FakeProbe(), default);
        Assert.True(verdict.Refused, "a CLI seat with no codex-code-mode-host beside codex was admitted");
        Assert.Contains("codex-code-mode-host", verdict.Refusal, StringComparison.Ordinal);
        Assert.Contains(codex.Install.Companions.Single().Sha256, verdict.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// (D) GUARD: A CLI SEAT WHOSE <c>codex-code-mode-host</c> IS NOT THE PIN IS REFUSED, NAMING BOTH HASHES — wherever codex would find
    /// it: beside its real path, or in its package's <c>codex-resources/</c>, which codex prefers. A broker seat, whose class disables the
    /// host, is not refused for it.
    /// </summary>
    [Fact]
    public async Task A_cli_seat_whose_code_mode_host_is_not_the_pin_is_refused_naming_both_hashes()
    {
        var (codex, script, _) = SeatHomeTests.Seat(nameof(A_cli_seat_whose_code_mode_host_is_not_the_pin_is_refused_naming_both_hashes));
        // THE VENDOR'S PACKAGE SHAPE, this test's own: <package>/bin/codex, and <package>/codex-resources beside bin.
        var bin = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(script)!, "package", "bin")).FullName;
        var exe = Path.Combine(bin, Path.GetFileName(script));
        File.Copy(script, exe);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, File.GetUnixFileMode(script));
        codex.Executable = exe;
        SeatHomeTests.PinTo(codex, exe);
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(script)!, "role-home")).FullName;
        var argv = AgentArgs.For(codex, "", resuming: false, "gpt-6-luna");
        var companion = codex.Install.Companions.Single();
        Assert.Equal("code_mode_host", companion.Feature);

        var clean = await SeatProfile.VerifyAsync(codex, exe, argv, workspace, new SeatHomeTests.FakeProbe(), default);
        Assert.False(clean.Refused, clean.Refusal);

        foreach (var place in new[] { Path.GetDirectoryName(exe)!, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, "codex-resources") })
        {
            Directory.CreateDirectory(place);
            var other = Path.Combine(place, companion.Name);
            var before = File.Exists(other) ? File.ReadAllBytes(other) : null;
            File.WriteAllText(other, "a different program\n");
            var found = SeatProfile.Sha256Of(other);

            var verdict = await SeatProfile.VerifyAsync(codex, exe, argv, workspace, new SeatHomeTests.FakeProbe(), default);
            Assert.True(verdict.Refused, $"a CLI seat with another {companion.Name} at {place} was admitted");
            Assert.Contains(companion.Sha256, verdict.Refusal, StringComparison.Ordinal);
            Assert.Contains(found, verdict.Refusal, StringComparison.Ordinal);
            Assert.Equal(ErrorCode.AI_VERSION_UNSUPPORTED, verdict.Code);

            if (before is null) File.Delete(other); else File.WriteAllBytes(other, before);
        }

        // THE BROKER'S CLASS DISABLES THE HOST: its program never starts one, so a missing one refuses nothing there.
        Assert.Empty(SeatProfile.CompanionsFor(codex, SeatPlan.BrokerClass));
        Assert.Empty(SeatProfile.CompanionsFor(codex, SeatPlan.CellClass));
        Assert.Single(SeatProfile.CompanionsFor(codex, SeatPlan.CliClass));
    }

    /// <summary>
    /// (D) GUARD: CODEX STARTED THROUGH A LINKED FOLDER IS CHECKED WHERE CODEX ITSELF LOOKS. Codex canonicalizes its own path, every
    /// folder on it resolved (<c>install-context/src/lib.rs:246-248</c>), and prefers the host in its real package's
    /// <c>codex-resources/</c>; so a seat launched through a junction (Windows) or a folder link (elsewhere) to the package's <c>bin</c>
    /// is checked there, not beside the link. Here the host beside codex is the pin and the package's <c>codex-resources/</c> holds
    /// another program: verification must refuse it. Red at base on Windows, which resolved a link on the program itself only.
    /// </summary>
    [Fact]
    public async Task A_cli_seat_started_through_a_linked_folder_is_checked_where_codex_looks()
    {
        var (codex, script, _) = SeatHomeTests.Seat(nameof(A_cli_seat_started_through_a_linked_folder_is_checked_where_codex_looks));
        var root = Path.GetDirectoryName(script)!;
        var bin = Directory.CreateDirectory(Path.Combine(root, "package", "bin")).FullName;
        var real = Path.Combine(bin, Path.GetFileName(script));
        File.Copy(script, real);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(real, File.GetUnixFileMode(script));
        SeatHomeTests.PinTo(codex, real);
        var companion = codex.Install.Companions.Single();

        // THE LINK: <root>/elsewhere/bin -> <root>/package/bin, and no codex-resources beside the link.
        var link = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName, "bin");
        Assert.True(SeatHomeTests.Link(link, bin, folder: true));
        var exe = Path.Combine(link, Path.GetFileName(script));
        codex.Executable = exe;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "role-home")).FullName;
        var argv = AgentArgs.For(codex, "", resuming: false, "gpt-6-luna");

        var clean = await SeatProfile.VerifyAsync(codex, exe, argv, workspace, new SeatHomeTests.FakeProbe(), default);
        Assert.False(clean.Refused, clean.Refusal);

        var resources = Directory.CreateDirectory(Path.Combine(root, "package", "codex-resources")).FullName;
        var other = Path.Combine(resources, companion.Name);
        File.WriteAllText(other, "a different program, where codex looks first\n");
        var found = SeatProfile.Sha256Of(other);

        var verdict = await SeatProfile.VerifyAsync(codex, exe, argv, workspace, new SeatHomeTests.FakeProbe(), default);
        Assert.True(verdict.Refused, $"a CLI seat started through {link} was admitted while codex's real package holds another {companion.Name}");
        Assert.Contains(found, verdict.Refusal, StringComparison.Ordinal);
        Assert.Contains(companion.Sha256, verdict.Refusal, StringComparison.Ordinal);
        Assert.Equal(ErrorCode.AI_VERSION_UNSUPPORTED, verdict.Code);
    }

    /// <summary>
    /// THE MEASURED PINS ARE THE BUILD'S: every platform's codex plan names code mode's host, beside codex and in the package's
    /// resources, with the SHA-256 hashed from the vendor's own rust-v0.160.1 archive (2026-10-23).
    /// </summary>
    [Fact]
    public void Every_platforms_codex_plan_pins_code_modes_host()
    {
        foreach (var (on, sha) in new (HostPlatform, string)[]
                 {
                     (new HostPlatform(HostOs.Windows, System.Runtime.InteropServices.Architecture.X64), "b905b5169bd758dbe3c31ca22c22ad0c24df0047ba7ae32652c706d3ac3ba78d"),
                     (new HostPlatform(HostOs.Linux, System.Runtime.InteropServices.Architecture.X64), "b33e8a5283f3c65c2a0aca6d43a59cfe850f624d8fa16992e3cad4fcc27c14e1"),
                     (new HostPlatform(HostOs.Linux, System.Runtime.InteropServices.Architecture.Arm64), "fbccde22982e3e679678e203a9c18eee8342fb04b096063d05159f1f80df4fd8"),
                     (new HostPlatform(HostOs.MacOS, System.Runtime.InteropServices.Architecture.Arm64), "6ac432b170e2c30f90de5af0da584def5201497a0d3d988d37dc58b951236b2f"),
                     (new HostPlatform(HostOs.MacOS, System.Runtime.InteropServices.Architecture.X64), "345b92c2a331fbd2dbc8be3ed7ac5e122c778ce2384ad7fa378932094d00d2b2")
                 })
        {
            var plan = RuntimeCatalog.BuiltIn(on).Single(m => m.Id == "codex").Install;
            var companion = Assert.Single(plan.Companions);
            Assert.Equal((on.IsWindows ? "codex-code-mode-host.exe" : "codex-code-mode-host", sha), (companion.Name, companion.Sha256));
            Assert.Equal(["../codex-resources", "."], companion.Places);
        }
    }
}
