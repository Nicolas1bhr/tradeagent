using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

namespace TradeAgent.Connectors.Atas;

/// <summary>
/// What the dashboard's two ATAS rows say.
///
/// They existed in <see cref="Components.All"/> from the first build and nothing anywhere ever wrote
/// them, so both read <c>unknown</c> for the whole of the session on 2026-08-31 in which the bridge
/// was demonstrably serving quotes and carrying a live order through to a broker id. A row that is
/// permanently unknown is worse than an absent one: the rail counts it as "not checked yet" and the
/// user is told something is outstanding when nothing is.
///
/// The readings are deliberately NOT a second opinion on <see cref="Components.TradingConnection"/>.
/// That row answers "can the gateway talk to the backend"; these two answer the question a user
/// actually has when it says no — <b>which half is missing</b>. ATAS not started, the bridge never
/// installed, and the bridge installed but not started on a chart are three completely different
/// mornings, and until now the product could not tell them apart on screen. The third is the one
/// that keeps happening: ATAS restores a chart strategy STOPPED after every restart (trap 24), which
/// looks identical to a bridge that failed to load unless something says so in words.
/// </summary>
public static class AtasHealth
{
    /// <summary>
    /// What both rows say when ATAS is not the chosen platform.
    ///
    /// UNKNOWN and not READY: nothing here has been checked, because there is nothing to check. The
    /// alternative — reporting the real state of an ATAS install the user is not using — puts
    /// "ATAS is not running" in front of somebody on the practice simulator who has never installed
    /// it and has nothing to fix.
    /// </summary>
    public const string NotInUse = "not in use — you are on the practice simulator";

    /// <summary>Is the ATAS platform itself up? Pure, so the table above is testable off Windows.</summary>
    public static (HealthState State, string Detail) ProcessRow(bool atasSelected, AtasDetection d)
    {
        if (!atasSelected) return (HealthState.UNKNOWN, NotInUse);
        if (!d.Installed) return (HealthState.FAILED, "ATAS is not installed on this computer");
        if (!d.Running) return (HealthState.DEGRADED, "not running — press Open ATAS on the Dashboard");
        return (HealthState.READY, d.Version is { Length: > 0 } v ? $"running · {v}" : "running");
    }

    /// <summary>
    /// Is the bridge strategy in ATAS actually dialled in? Pure, for the same reason.
    ///
    /// <paramref name="connection"/> is what the connector already reported for
    /// <see cref="Components.TradingConnection"/>; this never opens a second connection to decide.
    /// A named refusal always wins over anything derived from the connection state, because a peer
    /// that spoke and was turned down is more specific news than a pipe that is quiet.
    /// </summary>
    public static (HealthState State, string Detail) BridgeRow(
        bool atasSelected, AtasDetection d, HealthState connection, BridgeHello? hello, string? refusal)
    {
        if (!atasSelected) return (HealthState.UNKNOWN, NotInUse);
        if (!string.IsNullOrWhiteSpace(refusal)) return (HealthState.FAILED, refusal);

        if (connection == HealthState.READY)
            return (HealthState.READY, hello is null
                ? "connected"
                : $"connected · bridge {hello.BridgeVersion}, protocol {hello.BridgeProtocolVersion}");

        if (connection == HealthState.DEGRADED)
            return (HealthState.DEGRADED, "connected, but ATAS has stopped answering");

        if (connection == HealthState.STARTING) return (HealthState.STARTING, "connecting");

        // Nothing is on the pipe. Which of the three reasons it is, is the whole value of this row.
        if (!d.BridgeInstalled)
            return (HealthState.FAILED, "not installed in ATAS — press Install bridge on the Checks page");
        if (!d.Running)
            return (HealthState.FAILED, "installed — waiting for ATAS to start");
        return (HealthState.FAILED, "installed, but the strategy is not started on a chart in ATAS");
    }
}

/// <summary>
/// Writes the two rows on the health tick.
///
/// It is a class rather than a static call because it caches: the tick runs every five seconds for
/// the life of the app, and a detection is filesystem work that cannot change while the app runs —
/// except for whether the process is up, which is the one part re-probed every pass.
/// </summary>
public sealed class AtasHealthReporter
{
    /// <summary>How long a filesystem detection is reused. The process check ignores this.</summary>
    public TimeSpan DetectionTtl { get; set; } = TimeSpan.FromMinutes(1);

    readonly AtasLayout _layout = AtasLayout.Load();
    AtasDetection? _cached;
    DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public void Report(HealthRegistry health, ITradingConnector connector, HealthState connection)
    {
        var atas = connector as AtasConnector;
        var selected = atas is not null;

        // Detection is skipped entirely when ATAS is not the chosen platform: a simulator user's
        // health tick has no business enumerating processes every five seconds.
        var d = selected ? Detect() : Nothing;

        var (ps, pd) = AtasHealth.ProcessRow(selected, d);
        health.Set(Components.AtasProcess, ps, pd);

        var (bs, bd) = AtasHealth.BridgeRow(selected, d, connection, atas?.Bridge, atas?.StatusDetail);
        health.Set(Components.AtasBridge, bs, bd);
    }

    static readonly AtasDetection Nothing =
        new(false, null, null, null, false, false, true);

    AtasDetection Detect()
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is null || now - _cachedAt > DetectionTtl)
        {
            _cached = AtasInstallation.Detect(_layout);
            _cachedAt = now;
            return _cached;
        }
        // Everything but "is it up" is reused; that one is asked afresh, because it is the answer
        // that changes while somebody is watching the screen.
        return _cached with { Running = AtasInstallation.IsRunning(_layout) };
    }
}
