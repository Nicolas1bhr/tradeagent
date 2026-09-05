using TradeAgent.Gateway;
using TradeAgent.Provisioning;

namespace TradeAgent.Diagnostics;

/// <summary>
/// The interlock between updating and trading: each side stops the other from acting at the one
/// moment it must not, in one place that a test can run.
///
/// <b>Why it is called that.</b> It was <c>UpdateGatewayCoupling</c>, which named the two objects
/// and not the thing being built out of them — and "coupling" reads as the defect a reviewer would
/// go looking for rather than as the safety device this is. An interlock is exactly what these three
/// delegates make: the updater will not replace the program while an order's outcome is unknown, and
/// the gateway will not start an order while the program is being replaced. Neither half is optional
/// and neither half is meaningful alone.
///
/// <b>Why this exists as a seam rather than as three lines in AppHost.</b> The updater must not
/// replace the program while an order's outcome is unknown, and the gateway must not dispatch an
/// order while the program is being replaced. Both halves of that were assignments in the
/// composition root — and `TradeAgent.App` is not built by the test suite, so a reviewer deleted one
/// of them and watched the entire suite stay green. A source-text assertion caught a deletion and
/// nothing else: commenting the line out defeated it. A guard that can only be checked by grepping
/// for it is a guard nobody is checking.
///
/// <b>Why it lives in Diagnostics.</b> This is the only project that can already see both
/// <see cref="TradingGateway"/> and <see cref="UpdateService"/> without inverting a layer or adding
/// a reference: Diagnostics → Gateway, and Diagnostics → AgentRuntime → Provisioning. Making the
/// gateway know about updates, or the provisioner know about trading, would be a worse structure
/// than a slightly oddly-placed file — and the test project already references this one.
/// </summary>
public static class UpdateTradingInterlock
{
    /// <summary>
    /// One gateway, for the life of the process. Tests use it; the composition root does not, because
    /// switching trading platforms replaces the gateway (see the other overload).
    /// </summary>
    public static void Attach(TradingGateway gateway, UpdateService updates)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        Attach(() => gateway, updates);
    }

    /// <summary>
    /// Hands each side the narrowest view of the other it can work with: a count, a log sink, and a
    /// flag. Neither object learns the other's type.
    ///
    /// <b>Why a source rather than a gateway.</b> <c>AppHost.SwitchConnectorAsync</c> disposes the
    /// gateway and builds another one when the owner changes trading platform. Attached to an
    /// instance, this stop went on reading the discarded gateway — so the latch it held, and the
    /// bound it derived from the connector that was just replaced, described a gateway nobody was
    /// trading through, while the live one was never asked and was never told an install had started.
    /// The composition root passes <c>() =&gt; Gateway</c>: the indirection is read at every question,
    /// so there is no moment at which this points at the wrong one and no re-attach to forget.
    ///
    /// Call once. Calling it again is harmless — it overwrites the same three delegates.
    /// </summary>
    /// <param name="liveGateway">
    /// The gateway that is live RIGHT NOW, re-read at every question. May answer null (before the
    /// first gateway exists, or mid-switch); null is not "all clear" and refuses, below.
    /// </param>
    public static void Attach(Func<TradingGateway?> liveGateway, UpdateService updates)
    {
        ArgumentNullException.ThrowIfNull(liveGateway);
        ArgumentNullException.ThrowIfNull(updates);

        var binder = new Binder(liveGateway, updates);

        // The updater's view of trading: how many of the owner's orders the wire may still be holding.
        // Not the gateway, not the orders — a number.
        updates.UnconfirmedWork = binder.WireTouchedCount;

        // Somewhere the owner can find a refusal afterwards. A refusal nobody can find later is
        // indistinguishable from a button that did nothing.
        updates.Activity = binder.Record;

        // The gateway's view of updating goes on now rather than at the first question, so a gateway
        // that exists already is never briefly free to dispatch into an install.
        binder.Live();
    }

    /// <summary>
    /// Holds the one piece of state this seam has: which gateway it last wired the updating half into.
    /// Every question resolves the live gateway first, and a gateway it has not seen before is wired
    /// as it is resolved — so the half that cannot be a delegate on the updater (the flag lives on the
    /// gateway) still follows a switch, without the composition root having to remember anything.
    ///
    /// <c>InstallAsync</c> asks for the count BEFORE it raises <c>InstallInProgress</c>, so by the
    /// moment the flag matters the live gateway has already been wired to read it.
    /// </summary>
    sealed class Binder(Func<TradingGateway?> liveGateway, UpdateService updates)
    {
        readonly object _gate = new();
        TradingGateway? _wired;

        public TradingGateway? Live()
        {
            var gateway = liveGateway();
            lock (_gate)
            {
                if (!ReferenceEquals(gateway, _wired))
                {
                    _wired = gateway;
                    // Without it the gateway would keep dispatching orders whose answers arrive after
                    // the process that was going to reconcile them has been overwritten.
                    if (gateway is not null) gateway.InstallInProgress = () => updates.InstallInProgress;
                }
            }
            return gateway;
        }

        /// <summary>
        /// The GATEWAY'S question (<c>WireTouched</c>: flagged, DISPATCHING at any age, UNKNOWN,
        /// RECONCILING, and the in-memory latch), asked once, never a count assembled here. It used to
        /// be <c>Requests.NeedingReconciliation()</c> with no argument — the raw needs_reconciliation
        /// flag — which is a narrower set than the gateway's own, and the milestone review of
        /// 2026-09-05 (finding 3, probes P4 and P5) walked an install straight over an order the
        /// gateway was at that moment refusing to trade over. An updater that reads a smaller number
        /// than the gate is not a second opinion, it is a hole.
        ///
        /// No gateway is not zero. −1 is the updater's "I cannot tell", which refuses — the same
        /// answer it gives when nobody wired this up at all.
        /// </summary>
        public int WireTouchedCount() => Live() is { } gateway ? gateway.WireTouched().Count : -1;

        public void Record(string text, string level) => Live()?.Log.Activity(text, level);
    }
}
