using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A ROW THAT RUNS THE PINNED CODEX NAMED BY <c>TA_CELL_CODEX</c>: on Linux it is required — CI's ubuntu job unpacks the linux-musl
/// package of the one release every seat runs, and a missing one FAILS naming it; elsewhere it runs only where a builder names one
/// (this build's own pin, the vendor's own package), and is skipped otherwise.
/// </summary>
public sealed class PinnedCodexFactAttribute : FactAttribute
{
    public PinnedCodexFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TA_CELL_CODEX")))
            Skip = "runs the pinned codex: CI's ubuntu job names it in TA_CELL_CODEX (Category=Cell); elsewhere only where a builder names one";
    }
}

/// <summary>
/// (B) U-CLI-CODE-MODE ON THE REAL CODEX (<c>fleet/tmp/M4-regression/report.md</c>): the CLI class's own argv and config bytes, a
/// code-mode-only model (<c>gpt-6-luna</c>, bundled metadata), a throwaway home with no sign-in, and a local mock model
/// (<c>CellRoute/mock_responses.py</c>) whose first answer is a code-mode <c>exec</c> calling the nested <c>exec_command</c>. The
/// second request must carry the command's output. RED at base: the CLI class carried <c>--disable code_mode_host</c>, and the exec
/// call came back "code-mode host is disabled" — attempt 4's every turn. No real model, no key, no account.
/// </summary>
[Trait("Category", "Cell")]
public class CliCodeModeHostTests
{
    internal static string PinnedCodex()
    {
        var path = Environment.GetEnvironmentVariable("TA_CELL_CODEX");
        Assert.True(!string.IsNullOrEmpty(path) && File.Exists(path), $"missing prerequisite: the pinned codex named by TA_CELL_CODEX (got '{path}')");
        var pin = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex").Install.ExecutableSha256;
        using var f = File.OpenRead(path!);
        var hash = Convert.ToHexStringLower(SHA256.HashData(f));
        Assert.True(hash == pin, $"{path} hashes to {hash}, not this build's codex pin {pin}");
        return path!;
    }

    [PinnedCodexFact]
    public async Task The_cli_class_runs_a_code_mode_exec_on_a_code_mode_only_model()
    {
        var exe = PinnedCodex();
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var seat = codex.Seat!;

        var root = Path.Combine(TestEnv.Home, "cli-code-mode-" + Guid.NewGuid().ToString("n")[..8]);
        var (codexHome, home, work, mockDir) = (Path.Combine(root, "codex"), Path.Combine(root, "home"), Path.Combine(root, "work"), Path.Combine(root, "mock"));
        foreach (var d in new[] { codexHome, home, work, mockDir }) Directory.CreateDirectory(d);
        // THE SEAT'S OWN CONFIG BYTES, as the app writes them before every start; the turn ignores them (--ignore-user-config).
        File.WriteAllBytes(Path.Combine(codexHome, "config.toml"), SeatProfile.ConfigBytes(seat));

        var caseFile = Path.Combine(mockDir, "case.json");
        File.WriteAllText(caseFile, JsonSerializer.Serialize(new
        {
            steps = new object[]
            {
                new { type = "custom_tool_call", call_id = "cm1", name = "exec",
                      input = "const r = await tools.exec_command({cmd:\"echo ok\"}); text(r.output)" }
            },
            final = "CODE-MODE-DONE"
        }));
        var portFile = Path.Combine(mockDir, "port");
        var requests = Path.Combine(mockDir, "req");
        using var mock = Process.Start(new ProcessStartInfo("/usr/bin/python3",
            ["-I", Path.Combine(TradeAgent.Tests.DayOnePrograms.RepoRoot(), "tests", "TradeAgent.IntegrationTests", "CellRoute", "mock_responses.py"),
             portFile, caseFile, requests]) { UseShellExecute = false })!;
        try
        {
            for (var i = 0; i < 100 && !File.Exists(portFile); i++) await Task.Delay(50);
            Assert.True(File.Exists(portFile), "missing prerequisite: the mock model's port");
            var port = File.ReadAllText(portFile).Trim();

            // THE CLI CLASS'S REAL ARGV, built by the app's own builder, with the mock pinned as the provider.
            string[] provider =
            [
                "-c", "model_provider=\"mock\"", "-c", "model_providers.mock.name=\"mock\"",
                "-c", $"model_providers.mock.base_url=\"http://127.0.0.1:{port}/v1\"", "-c", "model_providers.mock.wire_api=\"responses\""
            ];
            var args = AgentArgs.Build(codex.ExecArgs, "go", codex.JsonFlag, codex.UnattendedArgs, ["-m", "gpt-6-luna"],
                [.. seat.ArgsFor(SeatPlan.CliClass), .. provider], codex.PromptOnStdin, codex.StdinMarker, out var inArgv);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment.Clear();
            psi.Environment["PATH"] = "/usr/bin:/bin";
            psi.Environment["CODEX_HOME"] = codexHome;
            psi.Environment["HOME"] = home;
            using var p = Process.Start(psi)!;
            if (!inArgv) await p.StandardInput.WriteAsync("go");
            p.StandardInput.Close();
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); }
            catch (TimeoutException) { p.Kill(entireProcessTree: true); Assert.Fail($"the turn did not finish in 120 s; stderr: {(se.IsCompleted ? await se : "")}"); }
            var stdout = await so;
            var stderr = await se;

            string? output = null;
            foreach (var file in Directory.EnumerateFiles(requests, "*.json").Order(StringComparer.Ordinal))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.GetProperty("body").TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in input.EnumerateArray())
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "custom_tool_call_output"
                        && item.TryGetProperty("call_id", out var id) && id.GetString() == "cm1")
                        output = item.GetProperty("output").ToString();
            }
            var tail = $"exit {p.ExitCode}; stdout: {stdout[^Math.Min(stdout.Length, 1500)..]}; stderr: {stderr[^Math.Min(stderr.Length, 1500)..]}";
            Assert.True(output is not null, $"no request carried the exec call's output; {tail}");
            Assert.True(output!.Contains("ok", StringComparison.Ordinal) && !output.Contains("code-mode host is disabled", StringComparison.Ordinal),
                $"the code-mode exec did not run: its output was {output}; {tail}");
            Assert.DoesNotContain("Code Mode is unavailable", stdout, StringComparison.Ordinal);
        }
        finally
        {
            try { mock.Kill(); mock.WaitForExit(5000); } catch (Exception) { /* gone */ }
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* the test home is purged */ }
        }
    }

    /// <summary>
    /// (3a) THE APP'S OWN CLI DRY RUN ON THE REAL CODEX (<see cref="CodexToolProbe"/>): under the CLI class's argv on gpt-6-luna the mock's
    /// code-mode <c>exec</c> runs and the seat passes; under the same argv with <c>--disable code_mode_host</c> put back — attempt 4's —
    /// the call fails closed and the seat is refused in words quoting "code-mode host is disabled".
    /// </summary>
    [PinnedCodexFact]
    public async Task The_cli_dry_run_passes_the_cli_class_and_refuses_attempt_fours_argv_in_words()
    {
        var exe = PinnedCodex();
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var sha = codex.Install.ExecutableSha256!;

        var run = await CodexToolProbe.Instance.RunAsync(new SurfaceAsk(codex, exe, sha, SeatPlan.CliClass, "gpt-6-luna"), default);
        Assert.Equal("exec", run.Tried);
        Assert.True(SeatProfile.ToolRefusal(codex, "gpt-6-luna", run) is null,
            $"the CLI class's own argv failed its dry run: {SeatProfile.ToolRefusal(codex, "gpt-6-luna", run)}");

        var attempt4 = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        attempt4.Seat!.Args[SeatPlan.CliClass] = [.. attempt4.Seat.ArgsFor(SeatPlan.CliClass), "--disable", "code_mode_host"];
        var dead = await CodexToolProbe.Instance.RunAsync(new SurfaceAsk(attempt4, exe, sha, SeatPlan.CliClass, "gpt-6-luna"), default);
        var refusal = SeatProfile.ToolRefusal(attempt4, "gpt-6-luna", dead);
        Assert.True(refusal is not null, $"attempt 4's argv passed the dry run: {dead}");
        Assert.Contains("code-mode host is disabled", refusal, StringComparison.Ordinal);
    }
}
