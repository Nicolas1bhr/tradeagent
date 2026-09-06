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
        // BEFORE ANY VERDICT ABOUT THE MACHINE. With atas.json unreadable nothing was searched for,
        // so "ATAS is not installed on this computer" would be a claim about a computer nobody
        // looked at — and it names a repair (install ATAS) that is not the one this owner needs.
        if (d.LayoutUnreadable is { } layoutWhy) return (HealthState.FAILED, layoutWhy);
        if (!d.Installed) return (HealthState.FAILED, "ATAS is not installed on this computer");
        if (!d.Running) return (HealthState.DEGRADED, "not running — press Open ATAS on the Dashboard");
        return (HealthState.READY, d.Version is { Length: > 0 } v ? $"running · {v}" : "running");
    }

    /// <summary>
    /// Is the bridge strategy in ATAS actually dialled in? Pure, for the same reason.
    ///
    /// <paramref name="connection"/> is what the connector already reported for
    /// <see cref="Components.TradingConnection"/>; this never opens a second connection to decide.
    /// A named refusal wins over anything derived from the CONNECTION state, because a peer that
    /// spoke and was turned down is more specific news than a pipe that is quiet.
    ///
    /// IT DOES NOT WIN OVER THE MACHINE. Both refusals are permanent now — a protocol mismatch until
    /// a compatible hello, a credential failure until a peer proves itself — and a refusal describes
    /// a PEER, which cannot be on the pipe from a platform that is not running or from a strategy
    /// file that is not there. Whether ATAS is up and whether the DLL is installed are read fresh on
    /// every pass; they are the newer facts and they lead, with the refusal kept after them because
    /// it is the repair that will be needed once the platform is back. Otherwise the row tells
    /// somebody to reinstall an add-on inside a program that is closed.
    /// </summary>
    public static (HealthState State, string Detail) BridgeRow(
        bool atasSelected, AtasDetection d, HealthState connection, BridgeHello? hello, string? refusal)
    {
        if (!atasSelected) return (HealthState.UNKNOWN, NotInUse);

        // Ahead of the refusal too, and for the same reason the machine facts are: every sentence
        // below sends the owner to a folder — install it there, start it from there, press
        // Reinstall the bridge and it will be put there — and which folder that is, is exactly what
        // could not be read.
        if (d.LayoutUnreadable is { } layoutWhy) return (HealthState.FAILED, layoutWhy);

        var recorded = string.IsNullOrWhiteSpace(refusal) ? null : refusal;
        if (recorded is not null)
        {
            if (!d.BridgeInstalled)
                return (HealthState.FAILED, $"not installed in ATAS — press {Labels.ReinstallBridge} on the Checks page · " +
                                            "last refusal recorded on the pipe: " + recorded);
            if (!d.Running)
                return (HealthState.FAILED, "installed — waiting for ATAS to start · " +
                                            "last refusal recorded on the pipe: " + recorded);
            return (HealthState.FAILED, recorded);
        }

        if (connection == HealthState.READY)
        {
            // CONNECTED IS NOT THE SAME AS ABLE TO TRADE. The bridge refuses any order whose client
            // order id it could not write to the witness file, because rule 1 rests on that record —
            // and a permanent local failure at that path refuses every order forever. A READY row
            // over a bridge in that state is the row lying to the one person who could fix it, so
            // this is the row that says so. It is DEGRADED rather than FAILED: the pipe is up and
            // everything that does not place an order still works.
            if (!string.IsNullOrWhiteSpace(hello?.WitnessFailure))
                return (HealthState.DEGRADED,
                        "connected, but orders are being refused: " + IncompatibleBridge.Clean(hello.WitnessFailure, 200));

            return (HealthState.READY, hello is null
                ? "connected"
                : $"connected · bridge {hello.BridgeVersion}, protocol {hello.BridgeProtocolVersion}");
        }

        if (connection == HealthState.DEGRADED)
            return (HealthState.DEGRADED, "connected, but ATAS has stopped answering");

        if (connection == HealthState.STARTING) return (HealthState.STARTING, "connecting");

        // Nothing is on the pipe. Which of the three reasons it is, is the whole value of this row.
        if (!d.BridgeInstalled)
            return (HealthState.FAILED, $"not installed in ATAS — press {Labels.ReinstallBridge} on the Checks page");
        if (!d.Running)
            return (HealthState.FAILED, "installed — waiting for ATAS to start");
        return (HealthState.FAILED, "installed, but the strategy is not started on a chart in ATAS");
    }

    /// <summary>
    /// Whether <see cref="Labels.ReinstallBridge"/> belongs on the screen for this row. Same inputs
    /// as <see cref="BridgeRow"/>, deliberately, so the button and the sentence cannot disagree.
    ///
    /// Reinstalling puts this build's bridge file into the Strategies folder. That repairs exactly
    /// two situations: a bridge that spoke and was REFUSED — a protocol this build does not speak,
    /// or a peer that could not prove itself — and a bridge that is not there at all. It repairs
    /// nothing about a platform that is merely closed, a strategy the owner has not started on a
    /// chart, or a bridge that is connected and answering, and offering it on those invites somebody
    /// to replace a working file in the hope that it helps. A witness failure is likewise not this
    /// button's business: the file is fine and the folder it cannot write to is not.
    /// </summary>
    public static bool RepairOffered(bool atasSelected, AtasDetection d, HealthState connection, string? refusal)
    {
        if (!atasSelected) return false;
        // Not while the folders are unknown. The button's whole action is a copy INTO a folder, and
        // the only candidate left would be a built-in the owner had already overridden away from.
        if (d.LayoutUnreadable is not null) return false;
        if (!string.IsNullOrWhiteSpace(refusal)) return true;
        if (connection != HealthState.FAILED) return false;
        return !d.BridgeInstalled;
    }
}

/// <summary>
/// The two machine facts the ATAS rows are read from, behind an interface.
///
/// It is a seam and not a convenience. Both questions are answered by asking the computer the app is
/// running on — Program Files for the install, the process table for whether the platform is up —
/// and a caller that cannot substitute those answers cannot be tested anywhere except on a machine
/// that happens to be in the state the test wants. That is not a hypothetical: the reporter's own
/// unit test passed on every machine without ATAS and failed on the one Windows box that had ATAS
/// installed and running, because the verdict it asserted was a property of the build host.
/// </summary>
public interface IAtasProbe
{
    /// <summary>Everything: where it is installed, its version, whether the bridge file is there.</summary>
    AtasDetection Detect();

    /// <summary>Only whether a platform process is up right now — the one answer that goes stale.</summary>
    bool IsRunning();

    /// <summary>
    /// THE CHEAP QUESTION: would <see cref="Detect"/> still answer what it answered for
    /// <paramref name="of"/>?
    ///
    /// Any two passes that produce equal strings may share a reading. The real implementation reads
    /// nothing but a handful of directory entries — a file's presence, its length, when it was last
    /// written — while a detection reads a version resource out of a PE image and parses ATAS's own
    /// runtimeconfig, which is why the reading is cached at all. It exists because caching on TIME
    /// alone let a row outlive the machine (Codex F20): the bridge assembly removed from the
    /// strategies folder outside the app, or ATAS replaced under it, changed nothing the cache was
    /// watching.
    /// </summary>
    string Stamp(AtasDetection of);
}

/// <summary>
/// The real probe, and the default one: the actual filesystem and the actual process table, through
/// <see cref="AtasInstallation"/>. The layout is read once when this is constructed, which is where
/// the reporter used to read it.
/// </summary>
public sealed class AtasProbe(AtasLayout? layout = null) : IAtasProbe
{
    // NOT read once and kept. It used to be, and that made an unreadable or corrected atas.json a
    // fact about the moment the app started rather than about the file: an owner who fixed the file
    // had to restart TradeAgent before anything noticed. Passing null through means the file is read
    // on the pass that asks, which is what the caching above is for.
    public AtasDetection Detect() => AtasInstallation.Detect(layout);

    public bool IsRunning() => AtasInstallation.IsRunning(layout);

    /// <summary>
    /// Four directory entries and no more: the file that says where to look, the two folders, the
    /// bridge assembly, and the platform executable the version was read out of. Between them they
    /// carry every fact <see cref="AtasDetection"/> derives from a file rather than from the process
    /// table — installed or not, which version, bridge present or not.
    ///
    /// What it CANNOT see is a folder appearing where the last pass found none: with nothing found
    /// there is no path to watch, and watching the candidate list instead would mean parsing
    /// atas.json on every tick, which is the work this is avoiding. That case is what the reporter's
    /// time bound is still for, and it is the harmless direction — an ATAS installed a minute ago
    /// reported a minute late, rather than an ATAS removed a minute ago reported as present.
    /// </summary>
    public string Stamp(AtasDetection of) => string.Join('|',
        Mark(AtasLayout.OverridePath),
        Mark(of.InstallDir),
        Mark(of.StrategyDir),
        Mark(of.StrategyDir is null ? null : Path.Combine(of.StrategyDir, AtasInstallation.BridgeAssembly)),
        Mark(of.PlatformExe));

    static string Mark(string? path)
    {
        if (path is null) return "-";
        try
        {
            var file = new FileInfo(path);
            if (file.Exists) return $"f{file.Length}@{file.LastWriteTimeUtc.Ticks}";
            var dir = new DirectoryInfo(path);
            return dir.Exists ? $"d@{dir.LastWriteTimeUtc.Ticks}" : "x";
        }
        // An unreadable entry is not a stable one: answering "?" every time would freeze the cache
        // on whatever it happened to hold, so it changes, and the next pass detects again.
        catch (Exception) { return $"?{Guid.NewGuid():n}"; }
    }
}

/// <summary>
/// Writes the two rows on the health tick.
///
/// It is a class rather than a static call because it caches: the tick runs every five seconds for
/// the life of the app, and a detection is filesystem work — a PE version resource, ATAS's own
/// runtimeconfig — that is wasted on most passes.
///
/// "CANNOT CHANGE WHILE THE APP RUNS" WAS THE PART THAT WAS WRONG, and this is where the row went
/// stale (Codex F20). It can: a folder is tidied, an antivirus quarantines the assembly, ATAS
/// updates itself, somebody copies a build in by hand. The reading is therefore kept against a
/// STAMP — a handful of directory entries, read every pass because reading them is nothing — and
/// dropped the moment they disagree. The time bound stays underneath as the backstop for the one
/// case a stamp cannot cover: an install appearing where the last pass found nothing to watch.
/// </summary>
public sealed class AtasHealthReporter(IAtasProbe? probe = null)
{
    /// <summary>
    /// How long a filesystem detection may be reused when nothing it was read from has changed. The
    /// process check ignores it, and so does <see cref="IAtasProbe.Stamp"/> disagreeing.
    /// </summary>
    public TimeSpan DetectionTtl { get; set; } = TimeSpan.FromMinutes(1);

    readonly IAtasProbe _probe = probe ?? new AtasProbe();
    AtasDetection? _cached;
    string _stamp = "";
    DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Whether the last pass wrote a row a reinstall would repair. Read by the Checks page to decide
    /// whether the button is on the screen, so the control appears for exactly the rows whose words
    /// send the owner to it.
    /// </summary>
    public bool RepairOffered { get; private set; }

    /// <summary>
    /// Drops the cached detection so the next pass asks the filesystem again.
    ///
    /// The cache exists because a detection is filesystem work on a five-second tick and nothing in
    /// it changes while the app runs — except after a reinstall, which changes the one fact the row
    /// is derived from. Without this the owner presses the button, the bridge lands, and the row
    /// goes on saying "not installed in ATAS" for up to a minute: a repair that worked, reported as
    /// a repair that did not.
    /// </summary>
    public void Forget() => _cached = null;

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

        RepairOffered = AtasHealth.RepairOffered(selected, d, connection, atas?.StatusDetail);
    }

    static readonly AtasDetection Nothing =
        new(false, null, null, null, false, false, true);

    AtasDetection Detect()
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is null || now - _cachedAt > DetectionTtl || _probe.Stamp(_cached) != _stamp)
        {
            _cached = _probe.Detect();
            // Stamped from the reading it belongs to, and after taking it: a stamp read beforehand
            // would belong to a machine state the detection may have raced past, which is a cache
            // that can be born stale.
            _stamp = _probe.Stamp(_cached);
            _cachedAt = now;
            return _cached;
        }
        // Everything but "is it up" is reused; that one is asked afresh, because it is the answer
        // that changes while somebody is watching the screen and leaves no mark on any file.
        return _cached with { Running = _probe.IsRunning() };
    }
}
