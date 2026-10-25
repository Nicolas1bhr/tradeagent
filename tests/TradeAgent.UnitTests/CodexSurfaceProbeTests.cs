using System.Diagnostics;
using System.Text;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>The probe's stand-in codex is a POSIX shell script; Windows runs no script of this shape.</summary>
public sealed class ShellScriptFactAttribute : FactAttribute
{
    public ShellScriptFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "the stand-in codex is a POSIX shell script";
    }
}

/// <summary>
/// THE SURFACE DRY RUN READS WHAT CODEX PRINTED BEFORE IT ACTS ON THE DEADLINE (<c>U-cell-model-catalog</c>, fix pass, review-2 finding
/// 2): a stand-in codex prints codex's fallback-metadata warning at once, its model request arrives just before the probe's deadline,
/// and it keeps its output open past it. The control run, without the warning, returns the request's tools — so the request did land
/// in time; the run with it is refused quoting the warning. No real codex, no model, a throwaway home.
///
/// <para>RED at base: the output reads were cancelled with the deadline and dropped what they had buffered, so the warned run
/// returned its tools.</para>
/// </summary>
public class CodexSurfaceProbeTests
{
    const string Fallback = "Model metadata for `gpt-6-luna` not found. Defaulting to fallback metadata; this can degrade performance and cause issues.";

    [ShellScriptFact]
    public async Task A_fallback_warning_printed_before_a_request_sent_just_before_the_deadline_still_refuses_the_seat()
    {
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var probe = new CodexSurfaceProbe { Deadline = TimeSpan.FromSeconds(5) };
        using var http = new HttpClient();

        foreach (var warn in new[] { false, true })
        {
            var dir = Path.Combine(TestEnv.Home, "surface-probe", Guid.NewGuid().ToString("n")[..12]);
            Directory.CreateDirectory(dir);
            var urlFile = Path.Combine(dir, "url");
            var script = Path.Combine(dir, "codex");
            File.WriteAllText(script,
                "#!/bin/sh\n" +
                "for a in \"$@\"; do case \"$a\" in model_providers." + CodexSurfaceProbe.ProviderId + ".base_url=*) " +
                "u=\"${a#*=}\"; u=\"${u#\\\"}\"; u=\"${u%\\\"}\";; esac; done\n" +
                (warn ? $"echo '{Fallback}' >&2\n" : "") +
                $"printf '%s' \"$u\" > \"{urlFile}.tmp\" && /bin/mv \"{urlFile}.tmp\" \"{urlFile}\"\n" +
                "exec /bin/sleep 30\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var clock = Stopwatch.StartNew();
            var run = probe.OfferedAsync(new SurfaceAsk(codex, script, SeatProfile.Sha256Of(script), SeatPlan.BrokerClass, BrokerSeat.Model), null, default);
            while (!File.Exists(urlFile) && clock.Elapsed < probe.Deadline) await Task.Delay(50);
            Assert.True(File.Exists(urlFile), "the stand-in codex never wrote the mock's address");
            var url = File.ReadAllText(urlFile).TrimEnd('/');

            // THE REQUEST, 1.5 SECONDS BEFORE THE DEADLINE; the stand-in never exits on its own, so the deadline ends the wait.
            var late = probe.Deadline - TimeSpan.FromSeconds(1.5) - clock.Elapsed;
            if (late > TimeSpan.Zero) await Task.Delay(late);
            using var body = new StringContent("{\"tools\":[{\"type\":\"function\",\"name\":\"request_user_input\"}]}", Encoding.UTF8, "application/json");
            using var answer = await http.PostAsync(url + "/responses", body);
            Assert.True(answer.IsSuccessStatusCode, $"the mock answered {(int)answer.StatusCode}");

            IReadOnlyList<string>? offered = null;
            var ex = await Record.ExceptionAsync(async () => offered = await run);
            if (!warn)
            {
                Assert.True(ex is null, $"the control run (no warning) was refused: {ex?.Message}");
                Assert.Equal(["request_user_input"], offered!);
                continue;
            }
            Assert.True(ex is not null && ex.Message.Contains("Defaulting to fallback metadata", StringComparison.Ordinal),
                $"a fallback warning printed before a request sent just before the deadline was not refused " +
                $"(offered {string.Join(", ", offered ?? [])}; {ex?.Message})");
        }
    }
}
