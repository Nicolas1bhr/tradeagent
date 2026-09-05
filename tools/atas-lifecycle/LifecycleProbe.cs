// Which ATAS teardown callback actually fires — OnStopping, OnDispose, or both.
//
// The bridge overrides both and routes both to the same idempotent teardown, deliberately, because
// "one callback is not a guarantee" (AtasStrategyAdapter.OnDispose). That makes the product correct
// and the question unanswerable from the product: neither hook writes a log line, and adding one
// would be a change to the money path for the sake of a measurement.
//
// So this is a SEPARATE strategy that does nothing but name the callback ATAS just called, with a
// timestamp, in %TEMP%\ta-lifecycle.log. It measures ATAS's ChartStrategy lifecycle, which is the
// question the backlog asks; it is not the bridge, and the report says so.
//
// Deployed by hand into %APPDATA%\ATAS\Strategies for one session and deleted afterwards. It is not
// prefixed TradeAgent.* on purpose: AtasInstallation.InstallBridge copies exactly that prefix, so
// this file can never ride into a real install on the back of a bridge reinstall.

using System.ComponentModel;
using ATAS.Strategies.Chart;

namespace TradeAgent.Probes;

[DisplayName("TA Lifecycle Probe")]
public sealed class LifecycleProbe : ChartStrategy
{
    static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "ta-lifecycle.log");

    static void Note(string callback, object? state)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {callback} state={state} pid={Environment.ProcessId}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // A probe that throws inside an ATAS callback would change the very behaviour it is
            // here to observe.
        }
    }

    public LifecycleProbe() => Note("ctor", null);

    protected override void OnCalculate(int bar, decimal value) { }

    protected override void OnStarted() => Note("OnStarted", State);

    protected override void OnStopping() => Note("OnStopping", State);

    protected override void OnDispose() => Note("OnDispose", State);
}
