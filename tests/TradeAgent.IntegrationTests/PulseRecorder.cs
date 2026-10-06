using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Connectors.Atas;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// DIAGNOSTIC ONLY — U-fix-bridge-heartbeat, branch only, removed before the proving run.
///
/// Records every pulse the bridge sleeps for, wakes for and sends, and every read, poll, pulse and
/// quiet verdict on the connector, for ONE pipe; alongside three canaries that separate the
/// candidate causes of a late pulse: a pool <c>Task.Delay(10)</c> (the .NET timer plus the pool),
/// a dedicated thread's <c>Thread.Sleep(10)</c> (the OS scheduler, no .NET timer, no pool), and
/// the queue-to-run latency of a pool work item (the pool alone). GC pause, process CPU and the
/// pool's thread count and backlog are sampled too.
/// </summary>
sealed class PulseRecorder : IAsyncDisposable
{
    readonly string _pipe;
    readonly string _label;
    readonly double _intervalMs;
    readonly long _t0 = Stopwatch.GetTimestamp();
    readonly ConcurrentQueue<(long Ts, string What, int Tid)> _events = new();
    readonly CancellationTokenSource _stop = new();
    readonly Thread _sampler;
    readonly Task _timerCanary;
    readonly Lock _gate = new();
    readonly List<(long Ts, string Kind, double Ms)> _late = [];
    (double Ms, long Ts) _timerMax, _sleepMax, _dispatchMax;
    int _threadsMin = int.MaxValue, _threadsMax;
    long _pendingMax;
    readonly TimeSpan _gcPause0 = GC.GetTotalPauseDuration();
    readonly int _gen0 = GC.CollectionCount(0), _gen1 = GC.CollectionCount(1), _gen2 = GC.CollectionCount(2);
    readonly TimeSpan _cpu0 = Process.GetCurrentProcess().TotalProcessorTime;

    public PulseRecorder(string pipe, string label, double intervalMs)
    {
        _pipe = pipe;
        _label = label;
        _intervalMs = intervalMs;
        PulseProbe.Sink = (p, w) => { if (p == _pipe) _events.Enqueue((Stopwatch.GetTimestamp(), w, Environment.CurrentManagedThreadId)); };

        _timerCanary = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var t = Stopwatch.GetTimestamp();
                try { await Task.Delay(10, _stop.Token); } catch (OperationCanceledException) { break; }
                Record("timer", t, Stopwatch.GetElapsedTime(t).TotalMilliseconds - 10);
            }
        });

        _sampler = new Thread(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var t = Stopwatch.GetTimestamp();
                Thread.Sleep(10);
                Record("sleep", t, Stopwatch.GetElapsedTime(t).TotalMilliseconds - 10);

                var q = Stopwatch.GetTimestamp();
                ThreadPool.UnsafeQueueUserWorkItem(_ => Record("dispatch", q, Stopwatch.GetElapsedTime(q).TotalMilliseconds), null);

                var threads = ThreadPool.ThreadCount;
                var pending = ThreadPool.PendingWorkItemCount;
                lock (_gate)
                {
                    _threadsMin = Math.Min(_threadsMin, threads);
                    _threadsMax = Math.Max(_threadsMax, threads);
                    _pendingMax = Math.Max(_pendingMax, pending);
                }
            }
        }) { IsBackground = true, Name = "pulse-sampler" };
        _sampler.Start();
    }

    void Record(string kind, long ts, double ms)
    {
        lock (_gate)
        {
            ref var max = ref kind == "timer" ? ref _timerMax : ref kind == "sleep" ? ref _sleepMax : ref _dispatchMax;
            if (ms > max.Ms) max = (ms, ts);
            if (ms > 50) _late.Add((ts, kind, ms));
        }
    }

    public void Mark(string what) => _events.Enqueue((Stopwatch.GetTimestamp(), "t." + what, Environment.CurrentManagedThreadId));

    double Rel(long ts) => (ts - _t0) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Prints one PULSE line always, and the whole timeline when the verdict was lost or a gap came near the timeout.</summary>
    public void Report(bool connected, HealthState health, bool provable)
    {
        var now = Stopwatch.GetTimestamp();
        var ev = _events.ToArray();
        double At(string what) => ev.Where(e => e.What == what).Select(e => Rel(e.Ts)).DefaultIfEmpty(double.NaN).First();

        var hello = At("c.hello");
        var recv = ev.Where(e => e.What.StartsWith("c.beat")).Select(e => Rel(e.Ts)).ToList();
        var sent = ev.Where(e => e.What.StartsWith("b.sent")).Select(e => Rel(e.Ts)).ToList();

        static (double Gap, double At) MaxGap(double start, List<double> ts)
        {
            var best = (Gap: 0.0, At: double.NaN);
            var prev = start;
            foreach (var t in ts)
            {
                if (!double.IsNaN(prev) && t - prev > best.Gap) best = (t - prev, t);
                prev = t;
            }
            return best;
        }

        var recvGap = MaxGap(hello, recv);
        var sendGap = MaxGap(hello, sent);
        var trailing = recv.Count > 0 ? Rel(now) - recv[^1] : double.NaN;

        // Each b.delay is followed by its b.wake; the lateness is what the timer added to the interval.
        var wakeLate = (Late: 0.0, At: double.NaN);
        var delays = 0; var wakes = 0;
        double? open = null;
        foreach (var e in ev)
        {
            if (e.What == "b.delay") { delays++; open = Rel(e.Ts); }
            else if (e.What == "b.wake" && open is { } d)
            {
                wakes++;
                var lateBy = Rel(e.Ts) - d - _intervalMs;
                if (lateBy > wakeLate.Late) wakeLate = (lateBy, Rel(e.Ts));
                open = null;
            }
        }

        var exits = string.Join(",", ev.Where(e => e.What.StartsWith("b.exit") || e.What.StartsWith("b.sendraw")).Select(e => e.What));
        var quiet = string.Join(",", ev.Where(e => e.What.StartsWith("c.quiet") || e.What is "c.eof" or "c.refused" or "c.drop" || e.What.StartsWith("c.threw"))
                                         .Select(e => $"{e.What}@{Rel(e.Ts):0}"));
        var polls = ev.Count(e => e.What == "c.poll");
        var reads = ev.Count(e => e.What == "c.read");

        double timerMax, sleepMax, dispatchMax; double timerAt, sleepAt, dispatchAt;
        int tMin, tMax; long pMax;
        List<(long Ts, string Kind, double Ms)> late;
        lock (_gate)
        {
            (timerMax, timerAt) = (_timerMax.Ms, Rel(_timerMax.Ts));
            (sleepMax, sleepAt) = (_sleepMax.Ms, Rel(_sleepMax.Ts));
            (dispatchMax, dispatchAt) = (_dispatchMax.Ms, Rel(_dispatchMax.Ts));
            (tMin, tMax, pMax) = (_threadsMin, _threadsMax, _pendingMax);
            late = [.. _late];
        }

        var wall = Rel(now);
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpu0).TotalMilliseconds;
        var gcPause = (GC.GetTotalPauseDuration() - _gcPause0).TotalMilliseconds;

        var line =
            $"PULSE {_label} os={(OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux")} " +
            $"connected={connected} health={health} provable={provable} hello@{hello:0} wall={wall:0}ms " +
            $"sent={sent.Count} recv={recv.Count} delays={delays} wakes={wakes} reads={reads} polls={polls} " +
            $"maxSendGap={sendGap.Gap:0}ms@{sendGap.At:0} maxRecvGap={recvGap.Gap:0}ms@{recvGap.At:0} trailing={trailing:0}ms " +
            $"maxWakeLate={wakeLate.Late:0}ms@{wakeLate.At:0} quiet=[{quiet}] exits=[{exits}] " +
            $"timerCanaryMaxLate={timerMax:0}ms@{timerAt:0} sleepCanaryMaxLate={sleepMax:0}ms@{sleepAt:0} " +
            $"poolDispatchMax={dispatchMax:0}ms@{dispatchAt:0} gcPause={gcPause:0}ms " +
            $"gc={GC.CollectionCount(0) - _gen0}/{GC.CollectionCount(1) - _gen1}/{GC.CollectionCount(2) - _gen2} " +
            $"cpu={cpu:0}ms/{wall:0}ms poolThreads={tMin}-{tMax} poolPendingMax={pMax}";

        var sb = new StringBuilder(line).Append('\n');
        if (!connected || recvGap.Gap > 300 || trailing > 300)
        {
            foreach (var e in ev) sb.Append($"PULSE-T {_label} +{Rel(e.Ts):0.0} tid={e.Tid} {e.What}\n");
            foreach (var l in late.OrderBy(l => l.Ts)) sb.Append($"PULSE-T {_label} +{Rel(l.Ts):0.0} canary={l.Kind} took={l.Ms:0}ms\n");
        }

        var text = sb.ToString();
        Console.Write(text);
        var log = Environment.GetEnvironmentVariable("TA_PULSE_LOG");
        if (!string.IsNullOrEmpty(log))
        {
            lock (typeof(PulseRecorder)) File.AppendAllText(log, text);
        }
    }

    public async ValueTask DisposeAsync()
    {
        PulseProbe.Sink = null;
        await _stop.CancelAsync();
        try { await _timerCanary; } catch (Exception) { }
        _sampler.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
