using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// HOW MUCH SLOWER IS THE MACHINE THIS SUITE IS RUNNING ON THAN THE ONE ITS MARGINS WERE TUNED ON?
///
/// Every timing fixture in this suite — a peer paced at so many bytes a window, a margin waited out
/// against a shipped deadline, a poll loop that has to observe a state before something else expires
/// — carries a number that was chosen on a developer machine. Three separate fix units
/// (U2a-fix, U-win-flakes, and the two instances this one starts from) each found the same shape:
/// the number was right here and wrong on `windows-latest`, by a factor nobody had measured.
///
/// This test measures it. It asserts nothing about the result — a probe that can fail is another
/// flake — but it PRINTS four durations, and the ratio of each against the reference machine below,
/// into the trx that CI already uploads for every platform. That is where the number in
/// <c>TA_TEST_TIME_SCALE</c> comes from, and where the number to change it to would come from.
///
/// The four workloads are the four things the fixtures actually spend their margins on:
/// arithmetic, file IO, a named-pipe round trip, and the floor of a short timer.
///
/// WHAT IT MEASURED. Three runs of the unit's PR — 33934109894, then 33935367698 whose windows job
/// was re-run once on the same sha — as ratios against the reference machine below:
///
///     windows-latest   cpu 1.20 1.22 1.06   file-io 3.79 39.52 4.89   pipe 2.24 2.54 3.31   timer 1.02 1.18 1.08
///     ubuntu-latest    cpu 0.97 1.20        file-io 0.74  0.13        pipe 1.50 1.54        timer 0.95 0.95
///     macos-latest     cpu 1.28 1.07        file-io 0.56  0.35        pipe 1.55 0.85        timer 4.58 3.26
///
/// WHY THERE IS NO SINGLE FACTOR TO SET. On windows-latest arithmetic, pipes and short timers are
/// within a small constant of this Mac — near 1.2x, 2.7x and 1.1x. FILE IO IS NOT: the same sixty
/// write-through-and-read-back cycles took 570 ms, 5944 ms and 735 ms on three runs of one commit,
/// a spread of ten, and file IO is what the failures are made of (a witness row is SQLite, and
/// SQLite is file IO). A single `TA_TEST_TIME_SCALE` would have to exceed 40 to cover the worst of
/// the three — past the ceiling <see cref="TestTime"/> itself refuses — and it would multiply every
/// fixture's patience in the suite, including fixtures bounded by a timer that is 1.08x. So it is
/// NOT set in CI, and the line below says so on every run: `TA_TEST_TIME_SCALE-in-effect=1.00`.
///
/// The second reason, from the same unit: of the two failures it started from, one was not a margin
/// at all but a missing premise (a caller cancelled while this end was still inside its write). No
/// scale, at any value, turns that into a pass.
///
/// WHAT WAS DONE INSTEAD. The two classes with a measured history of windows-only reds carry
/// `Trait("Category","Timing")`, `.github/workflows/build.yml` runs the rest of the suite with no
/// retry on every platform, and re-runs THAT CATEGORY ONCE, on windows-latest only, when it fails.
/// A rescued run is annotated and both attempts' trx are uploaded, so a green that needed a second
/// attempt cannot be read as a green that did not. What this cannot do is tell a slow runner from a
/// regression inside those two classes on Windows — the price of the category, paid on purpose,
/// against three fix units that each cost a day and fixed one test.
///
/// macOS is worth a look before it costs a day too: its short-timer floor is 3.3x-4.6x this Mac's,
/// which is where a macOS-only timing red would come from.
/// </summary>
public class RunnerSpeedProbeTests(ITestOutputHelper output)
{
    /// <summary>
    /// THE REFERENCE MACHINE, measured rather than assumed: the median of five Release runs on the
    /// development Mac (Apple silicon, macOS 25.5, .NET 10), 2026-09-05. Re-measure these before
    /// reading a ratio as anything but a comparison against that machine on that day; a new
    /// reference machine makes every ratio below meaningless until they are replaced.
    /// </summary>
    const double RefCpuMs = 106.4;
    const double RefFileIoMs = 150.4;
    const double RefPipeMs = 7.3;
    const double RefTimerMs = 666.3;

    [Fact]
    [Trait("Category", "Timing")]
    public async Task How_slow_this_machine_is_against_the_one_the_margins_were_tuned_on()
    {
        var (cpuSum, cpuMs) = Cpu();
        var (ioBytes, ioMs) = FileIo();
        var (frames, pipeMs) = await PipeRoundTrips();
        var (ticks, timerMs) = await ShortTimers();

        var ratios = new[]
        {
            Line("cpu       ", cpuMs, RefCpuMs),
            Line("file-io   ", ioMs, RefFileIoMs),
            Line("pipe-rt   ", pipeMs, RefPipeMs),
            Line("timer-25ms", timerMs, RefTimerMs)
        };

        output.WriteLine($"TA_RUNNER_PROBE os={RuntimeOs()} cores={Environment.ProcessorCount} " +
                         $"rid={System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        foreach (var r in ratios) output.WriteLine("TA_RUNNER_PROBE " + r.Text);
        output.WriteLine($"TA_RUNNER_PROBE worst-ratio={ratios.Max(r => r.Ratio):0.00} " +
                         $"TA_TEST_TIME_SCALE-in-effect={TestTime.Scale:0.00}");

        // The same lines on stdout, because the trx is an artifact somebody has to download and the
        // job log is not.
        Console.WriteLine($"TA_RUNNER_PROBE os={RuntimeOs()} cores={Environment.ProcessorCount}");
        foreach (var r in ratios) Console.WriteLine("TA_RUNNER_PROBE " + r.Text);
        Console.WriteLine($"TA_RUNNER_PROBE worst-ratio={ratios.Max(r => r.Ratio):0.00}");

        // THE ONLY ASSERTIONS ARE THAT THE WORK HAPPENED. Nothing here compares a duration against a
        // bound: this test exists to explain the flakes, not to become one.
        Assert.NotEqual(0, cpuSum);
        Assert.Equal(Files * PayloadBytes * 2, ioBytes);
        Assert.Equal(Frames, frames);
        Assert.Equal(Timers, ticks);
    }

    (string Text, double Ratio) Line(string name, double ms, double refMs)
    {
        var ratio = ms / refMs;
        return ($"{name} {ms,8:0.0} ms   ref {refMs,7:0.0} ms   ratio {ratio,6:0.00}x", ratio);
    }

    static string RuntimeOs() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Fixed arithmetic, allocation-free; the result is returned so nothing can elide it.</summary>
    static (long Sum, double Ms) Cpu()
    {
        const int rounds = 40_000_000;
        var sw = Stopwatch.StartNew();
        var x = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < rounds; i++)
        {
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            x *= 0x2545F4914F6CDD1DUL;
        }
        sw.Stop();
        return ((long)(x & 0x7FFFFFFF), sw.Elapsed.TotalMilliseconds);
    }

    const int Files = 60;
    const int PayloadBytes = 4096;

    /// <summary>
    /// Create, write through to the device, read back, delete — the shape a witness rewrite or a
    /// sidecar append has, and the one an on-access virus scanner charges the most for.
    /// </summary>
    static (long Bytes, double Ms) FileIo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ta-probe-" + Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(dir);
        var payload = new byte[PayloadBytes];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

        long bytes = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            for (var i = 0; i < Files; i++)
            {
                var f = Path.Combine(dir, $"p{i}.bin");
                using (var s = new FileStream(f, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                              PayloadBytes, FileOptions.WriteThrough))
                {
                    s.Write(payload);
                    s.Flush(true);
                }
                bytes += payload.Length;
                bytes += File.ReadAllBytes(f).Length;
            }
        }
        finally
        {
            sw.Stop();
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return (bytes, sw.Elapsed.TotalMilliseconds);
    }

    const int Frames = 300;

    /// <summary>
    /// A line written and echoed back over a real named pipe, which is the transport every test in
    /// this assembly is built on: the connector's bridge, the gateway's agent pipe, the stub bridge.
    /// </summary>
    static async Task<(int Frames, double Ms)> PipeRoundTrips()
    {
        var name = "ta-probe-" + Guid.NewGuid().ToString("n")[..12];
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var accepting = server.WaitForConnectionAsync();
        await client.ConnectAsync(10_000);
        await accepting;

        var utf8 = new UTF8Encoding(false);
        using var sw = new StreamWriter(server, utf8, 8192, leaveOpen: true) { AutoFlush = true };
        using var sr = new StreamReader(server, utf8, false, 8192, leaveOpen: true);
        using var cw = new StreamWriter(client, utf8, 8192, leaveOpen: true) { AutoFlush = true };
        using var cr = new StreamReader(client, utf8, false, 8192, leaveOpen: true);

        var echo = Task.Run(async () =>
        {
            string? line;
            while ((line = await cr.ReadLineAsync()) is not null)
            {
                await cw.WriteLineAsync(line);
                if (line == "done") return;
            }
        });

        var seen = 0;
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
        {
            await sw.WriteLineAsync($"{{\"v\":3,\"id\":\"probe-{i}\",\"op\":\"noop\"}}");
            if (await sr.ReadLineAsync() is not null) seen++;
        }
        timer.Stop();

        await sw.WriteLineAsync("done");
        await sr.ReadLineAsync();
        await echo;
        return (seen, timer.Elapsed.TotalMilliseconds);
    }

    const int Timers = 25;

    /// <summary>
    /// Twenty-five turns of the 25 ms poll every <c>Wait(...)</c> helper in this assembly is built
    /// out of. On a machine with a coarse timer, or a thread pool with nothing free to resume the
    /// continuation, this is the floor under every "observe a state before something else expires"
    /// fixture — and it is a fact about the platform, not about the code being tested.
    /// </summary>
    static async Task<(int Ticks, double Ms)> ShortTimers()
    {
        var ticks = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < Timers; i++)
        {
            await Task.Delay(25);
            ticks++;
        }
        sw.Stop();
        return (ticks, sw.Elapsed.TotalMilliseconds);
    }
}
