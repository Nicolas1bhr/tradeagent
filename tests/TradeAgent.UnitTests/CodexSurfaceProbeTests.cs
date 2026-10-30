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

    /// <summary>
    /// NO WALL-CLOCK RACE (<c>U-probe-test-timing</c>): the first version sent its request 1.5 s before a 5 s deadline on a clock that
    /// started before the probe's own timer, so a stall of 1.5 s anywhere between the test's clock and the probe's capture — the client's
    /// first use, a loaded thread pool, the shell's start — refused the CONTROL run ("made no model request within 5 seconds") on slow
    /// CI runners. Here the deadline is infinite until the probe has seen the request and the probe's own seam
    /// (<see cref="CodexSurfaceProbe.OnFirstRequest"/>) ends it that instant: "the request landed in time, then the deadline passed while
    /// codex still held its output open" holds on any machine, however slow, and nothing is sent against a clock. The only waits are for
    /// readiness (the stand-in's address file, the mock's answer), bounded by a generous safety limit that fails loudly, never by a race.
    /// </summary>
    [ShellScriptFact]
    public async Task A_fallback_warning_printed_before_a_request_sent_just_before_the_deadline_still_refuses_the_seat()
    {
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        var probe = new CodexSurfaceProbe { Deadline = Timeout.InfiniteTimeSpan, OnFirstRequest = timer => timer.Cancel() };
        using var http = new HttpClient();
        var readiness = TimeSpan.FromMinutes(2);

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

            var run = probe.OfferedAsync(new SurfaceAsk(codex, script, SeatProfile.Sha256Of(script), SeatPlan.BrokerClass, BrokerSeat.Model), null, default);
            // READINESS, NOT A CLOCK: the stand-in has printed its warning (if any) and published the mock's address.
            var waited = Stopwatch.StartNew();
            while (!File.Exists(urlFile) && !run.IsCompleted && waited.Elapsed < readiness) await Task.Delay(20);
            Assert.True(File.Exists(urlFile), $"the stand-in codex never wrote the mock's address ({(run.IsCompleted ? "the probe ended first: " + (await Record.ExceptionAsync(async () => await run))?.Message : "not ready in " + readiness)})");
            var url = File.ReadAllText(urlFile).TrimEnd('/');

            // THE REQUEST; the stand-in never exits on its own. Its answer means the mock has captured the body, and the probe's seam
            // ends the deadline the instant the probe has seen it.
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
                $"a fallback warning printed before a request the deadline then followed was not refused " +
                $"(offered {string.Join(", ", offered ?? [])}; {ex?.Message})");
        }
    }
}
