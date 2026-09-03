using TradeAgent.Gateway;
using TradeAgent.Provisioning;

namespace TradeAgent.Diagnostics;

/// <summary>
/// The two-way link between the updater and the trading gateway, in one place that a test can run.
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
public static class UpdateGatewayCoupling
{
    /// <summary>
    /// Hands each side the narrowest view of the other it can work with: a count, a log sink, and a
    /// flag. Neither object learns the other's type.
    ///
    /// Call once, after the gateway exists. Calling it again is harmless — it overwrites the same
    /// three delegates with equivalent ones.
    /// </summary>
    public static void Attach(TradingGateway gateway, UpdateService updates)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(updates);

        // The updater's view of trading: how many of the owner's orders have an outcome nobody has
        // established. Not the gateway, not the orders — a number.
        updates.UnconfirmedWork = () => gateway.Requests.NeedingReconciliation().Count;

        // Somewhere the owner can find a refusal afterwards. A refusal nobody can find later is
        // indistinguishable from a button that did nothing.
        updates.Activity = (text, level) => gateway.Log.Activity(text, level);

        // The gateway's view of updating: whether this process is in the middle of being replaced.
        // Without it the gateway would keep dispatching orders whose answers arrive after the
        // process that was going to reconcile them has been overwritten.
        gateway.InstallInProgress = () => updates.InstallInProgress;
    }
}
