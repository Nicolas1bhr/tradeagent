using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-SEAT-HOME: EVERY CODEX SEAT RUNS IN ONE APP-OWNED HOME UNDER A DEFAULT-DENY PROFILE, never under the owner's own
/// <c>~/.codex</c> (AUTH-001, ORDER-005, CRED-001; <c>docs/ORCHESTRATION.md</c> § 2.7).
///
/// <para>Measured on 2026-10-11 (codex-cli 0.160.1, a local mock provider, <c>fleet/tmp/A-org/seat-home/survey.md</c>): under a
/// copy of the owner's home the model was offered 14 tools, two of them the ChatGPT app's REPLs, and the notify hook received every
/// prompt; even an empty home offered 12. Under the deny set over a HOSTILE home: 4 tools and no child process. CI has no codex, so
/// every program here is a script and the probe is a fake.</para>
///
/// <para>In the vendor-file collection: the shipped manifest is read off the build, and the process-wide <c>CODEX_HOME</c> one test
/// sets must not be read by a neighbour in between.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class SeatHomeTests
{
    /// <summary>
    /// (a) THE VERSION PROBE — and every other codex process the app starts — CARRIES THE APP'S SEAT HOME. Red at base: the
    /// manifest's <c>KeepEnvironment = ["CODEX_HOME"]</c> let an inherited value through and the whitelist passed the owner's own
    /// <c>HOME</c>, so the CLI read <c>~/.codex</c> and <c>~/.agents/skills</c>.
    /// </summary>
    [Fact]
    public async Task A_codex_seat_runs_under_the_app_seat_home_never_the_owners()
    {
        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        var (exe, dump) = EnvDumper(nameof(A_codex_seat_runs_under_the_app_seat_home_never_the_owners));
        codex.Executable = exe;

        var owners = Path.Combine(TestEnv.Home, "the-owners-codex-home");
        var inherited = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", owners);
        try { await new CliAgentRuntime(codex, presence: new AgentPresence()).DetectAsync(); }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", inherited); }

        var env = ReadEnv(dump);
        var seat = Path.Combine(Paths.Home, "seat-home");
        Assert.Equal(Path.Combine(seat, "codex"), env.GetValueOrDefault("CODEX_HOME"));
        Assert.Equal(Path.Combine(seat, "home"), env.GetValueOrDefault("HOME"));
        Assert.DoesNotContain(env.Values, v => v.Contains(owners, StringComparison.Ordinal));

        // AND THE CLI'S OWN CONFIG FILE IS THE APP'S, rewritten before every start: a seat that edits it is undone.
        var config = SeatProfile.ConfigPath(codex.Seat!)!;
        Assert.Equal(SeatProfile.ConfigBytes(codex.Seat!), File.ReadAllBytes(config));
        File.AppendAllText(config, "chatgpt_base_url = \"http://127.0.0.1:9/\"\n");
        await new CliAgentRuntime(codex, presence: new AgentPresence()).DetectAsync();
        Assert.Equal(SeatProfile.ConfigBytes(codex.Seat!), File.ReadAllBytes(config));
        Assert.Contains("features.plugins = false\nfeatures.remote_plugin = false",
            File.ReadAllText(config), StringComparison.Ordinal);
        Assert.Contains("mcp_servers = {}", File.ReadAllText(config), StringComparison.Ordinal);
    }

    /// <summary>
    /// (b) THE DENY SET IS ON THE FIRST TURN AND ON EVERY RESUME, before the prompt. Red at base: the argv was
    /// <c>exec --json --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox -m &lt;model&gt;</c> and nothing kept the
    /// CLI's own config file, plugins, MCP servers or notify hook out. <c>--ignore-user-config</c> is the one flag that keeps a
    /// seat-written <c>config.toml</c>'s MCP servers out: <c>-c mcp_servers={}</c> merges, it does not clear (survey § 1).
    /// </summary>
    [Fact]
    public void A_codex_seat_argv_carries_the_deny_set_fresh_and_resumed()
    {
        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        foreach (var resuming in new[] { false, true })
        {
            var argv = AgentArgs.For(codex, "PROMPT", resuming, "gpt-5.6-sol");
            var prompt = argv.IndexOf("PROMPT");

            foreach (var flag in new[] { "--ignore-user-config", "--ignore-rules", "--strict-config" })
            {
                Assert.Contains(flag, argv);
                Assert.True(argv.IndexOf(flag) < prompt, $"{flag} lands after the prompt (resuming: {resuming})");
            }
            foreach (var feature in new[] { "apps", "plugins", "remote_plugin", "computer_use", "browser_use", "multi_agent", "hooks", "goals" })
                Assert.True(Pair(argv, "--disable", feature), $"--disable {feature} missing (resuming: {resuming})");
            foreach (var key in new[] { "mcp_servers={}", "notify=[]", "web_search=\"disabled\"", "skills.bundled.enabled=false",
                                         "cli_auth_credentials_store=\"file\"" })
                Assert.True(Pair(argv, "-c", key), $"-c {key} missing (resuming: {resuming})");

            Assert.DoesNotContain("--ephemeral", argv);
            Assert.Equal(argv.Count - 1, prompt);
        }
    }

    /// <summary>
    /// (d) A PROGRAM THAT IS NOT THE PIN IS NOT INSTALLED FOR A SEAT AND IS HANDED NOTHING. The version probe may ask it what it
    /// is — that is how the words name its version — and nothing else is started from it: not the sign-in status, and not the
    /// pasted key, which would otherwise go on stdin to whatever program sat first on PATH.
    /// </summary>
    [Fact]
    public async Task A_binary_that_is_not_the_pin_is_refused_naming_both_hashes()
    {
        var (codex, exe, dump) = Seat(nameof(A_binary_that_is_not_the_pin_is_refused_naming_both_hashes), version: "9.9.9");
        var pin = new string('a', 64);
        codex.Install.Version = "0.160.1";
        codex.Install.ExecutableSha256 = pin;
        var found = SeatProfile.Sha256Of(exe);
        var rt = new CliAgentRuntime(codex, presence: new AgentPresence());

        var detected = await rt.DetectAsync();
        Assert.False(detected.Installed);
        foreach (var said in new[] { pin, found, "0.160.1", "9.9.9", exe }) Assert.Contains(said, detected.Reason, StringComparison.Ordinal);

        var key = await Assert.ThrowsAsync<TradeAgentException>(() => rt.SignInWithApiKeyAsync("sk-FAKE-0000"));
        Assert.Equal(ErrorCode.AI_VERSION_UNSUPPORTED, key.Code);
        foreach (var said in new[] { pin, found, "0.160.1", "9.9.9" }) Assert.Contains(said, key.Message, StringComparison.Ordinal);
        Assert.Equal(AuthState.NotAuthenticated, await rt.GetAuthenticationStateAsync());

        // Only the version probe ever ran it.
        Assert.All(Argv(dump), line => Assert.Equal("--version", line));

        // The pin itself is the program, wherever it was found.
        codex.Install.ExecutableSha256 = found.ToUpperInvariant();
        Assert.True((await rt.DetectAsync()).Installed);
    }

    /// <summary>
    /// THE SHIPPED CODEX MANIFEST pointed at a script and at a seat home of this test's own: only the program, the pin and the
    /// home's root change — the environment, the deny set and the config file are the build's own data.
    /// </summary>
    internal static (RuntimeManifest Codex, string Exe, string Dump) Seat(string name, string version = "0.160.1")
    {
        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        var (exe, dump) = EnvDumper(name, version);
        codex.Executable = exe;
        codex.Install.ExecutableSha256 = SeatProfile.Sha256Of(exe);
        codex.Seat!.Home = Path.Combine(Path.GetDirectoryName(exe)!, "seat-home");
        return (codex, exe, dump);
    }

    /// <summary>Every command line the script was started with, one per line, in order.</summary>
    internal static string[] Argv(string dump)
    {
        var log = Path.Combine(Path.GetDirectoryName(dump)!, "argv.txt");
        return File.Exists(log) ? [.. File.ReadAllLines(log).Select(l => l.Trim()).Where(l => l.Length > 0)] : [];
    }

    static bool Pair(List<string> argv, string flag, string value)
    {
        for (var i = 0; i + 1 < argv.Count; i++)
            if (argv[i] == flag && argv[i + 1] == value) return true;
        return false;
    }

    /// <summary>A program that writes its whole environment to a file and answers a version, on either platform.</summary>
    internal static (string Exe, string Dump) EnvDumper(string name, string version = "0.160.1")
    {
        var dir = Path.Combine(TestEnv.Home, "seat", $"{name}-{Guid.NewGuid():n}"[..40]);
        Directory.CreateDirectory(dir);
        var dump = Path.Combine(dir, "env.txt");
        var argv = Path.Combine(dir, "argv.txt");
        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(dir, "codex.cmd");
            File.WriteAllText(bat, $"@echo off\r\nset > \"{dump}\"\r\necho %* >> \"{argv}\"\r\necho codex-cli {version}\r\n");
            return (bat, dump);
        }
        var sh = Path.Combine(dir, "codex");
        File.WriteAllText(sh, $"#!/bin/sh\nenv > \"{dump}\"\necho \"$*\" >> \"{argv}\"\necho 'codex-cli {version}'\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (sh, dump);
    }

    internal static Dictionary<string, string> ReadEnv(string dump) =>
        File.ReadAllLines(dump)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last()[1], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
