namespace TradeAgent.AtasBridge;

/// <summary>
/// We stopped waiting for an ATAS call. ATAS did not stop working.
///
/// This is NOT <see cref="AtasRejectedException"/> and must never become one — the two are opposites.
/// A rejection says the broker refused and nothing is live; this says we have no idea, because
/// <see cref="System.Threading.Tasks.Task.WaitAsync(TimeSpan)"/> ends OUR wait and has no way to
/// recall a request already handed to the platform. An order behind one of these may well be resting
/// at the broker right now. Rule 3: propagate it, let the gateway record UNKNOWN, and reconcile.
/// </summary>
public sealed class AtasCallTimeoutException(string message) : Exception(message);

/// <summary>
/// Waits for one of ATAS's async calls from a synchronous adapter method, with a deadline.
///
/// This lives OUTSIDE <c>#if ATAS_SDK</c> on purpose. Its previous home was a private helper inside
/// <c>AtasStrategyAdapter</c>, a file that is <c>&lt;Compile Remove&gt;</c>d on every machine without
/// ATAS installed — so by construction not one test on the dev Mac or in CI could reach the one piece
/// of code that decides whether the bridge's command loop ever runs again. Moving it here is what
/// makes the behaviour below testable at all.
/// </summary>
public static class AtasCall
{
    /// <summary>
    /// The default consequence, and it is the one that matters: an expiry on a write path means an
    /// order may be resting at the broker with nobody watching it. Rule 3 in one sentence.
    /// </summary>
    public const string OrderConsequence =
        "The outcome is UNKNOWN — the order may be live at the broker — and must be reconciled, never assumed failed.";

    /// <summary>
    /// Runs <paramref name="task"/> to completion on the calling thread, or gives up after
    /// <paramref name="timeout"/> and reports the outcome as unknown.
    ///
    /// WHY THERE IS A DEADLINE AT ALL. <see cref="BridgeServer.RunAsync"/> reads frames in a loop and
    /// awaits <c>HandleFrame</c> before reading the next one, so frames are strictly serialised. A
    /// call that never returns means no further frame is ever read off the pipe — including
    /// <see cref="TradeAgent.Connectors.Atas.BridgeOps.CancelAll"/>, which is what the operator's
    /// "cancel everything" control sends to clear the book. Meanwhile the heartbeat is a separate
    /// <c>Task.Run</c> and keeps beating, so the connector goes on reporting READY. A wedged bridge
    /// that reports healthy defeats the one check meant to catch it, so the wait has to end by itself.
    /// (The in-process kill switch is unaffected — it never travels over this pipe. The operator
    /// controls that actually cancel and close do.)
    ///
    /// WHY THE DEADLINE IS NOT A REJECTION. Expiry throws <see cref="AtasCallTimeoutException"/>,
    /// which <see cref="BridgeServer"/> classifies as indefinite. Turning it into
    /// <see cref="AtasRejectedException"/> would tell the gateway a live order was refused — rule 3
    /// broken in the direction that loses money rather than the direction that wastes a reconcile.
    ///
    /// WHY <c>GetAwaiter().GetResult()</c> AND NOT <c>.Wait()</c> / <c>.Result</c>. A single-fault
    /// task rethrown through the awaiter comes out via <c>ExceptionDispatchInfo</c>, keeping its own
    /// type and stack. <c>.Wait()</c> and <c>.Result</c> wrap it in an <see cref="AggregateException"/>
    /// instead, and <see cref="BridgeServer"/>'s <c>catch (AtasRejectedException)</c> would then miss
    /// a definite refusal — sending <c>rejected=false</c> with the message degraded to
    /// "One or more errors occurred." (The wire classifier unwraps that shape too, belt and braces;
    /// this is the belt.)
    ///
    /// WHY A MULTI-FAULT TASK IS HANDED BACK WHOLE. The awaiter alone would throw only the FIRST of
    /// several faults, which could present a task that failed two ways as one definite refusal. Two
    /// failures are ambiguous by definition, so the <see cref="AggregateException"/> is what
    /// propagates and the wire reads it as indefinite. That is the truthful reading of the task, not
    /// a case to paper over.
    ///
    /// Blocking here is safe because every caller is on the bridge's pipe-handling thread, never
    /// ATAS's UI thread, and ConfigureAwait(false) keeps it off any captured context.
    ///
    /// ---- carried over from AtasStrategyAdapter.Block, where this helper used to live ----
    ///
    /// Only the CONNECTOR path needs this: it is asked for work through its Async methods, and the
    /// adapter's own methods are synchronous. The ITradingManager path calls the synchronous
    /// overloads directly, which also sidesteps blocking on a task that may be marshalled to ATAS's
    /// GUI thread.
    ///
    /// THOSE SYNCHRONOUS OVERLOADS ARE OBSOLETE. Building against the real ATAS 8.0.14.397 SDK says
    /// so, verbatim:
    ///
    ///     warning CS0618: 'ITradingManager.OpenOrder(Order, bool, bool, bool)' is obsolete:
    ///                     'Use OpenOrderAsync instead.'
    ///     warning CS0618: 'ITradingManager.ModifyOrder(Order, Order, bool, bool)' is obsolete:
    ///                     'Use ModifyOrderAsync instead.'
    ///     warning CS0618: 'ITradingManager.CancelOrder(Order, bool, bool)' is obsolete:
    ///                     'Use CancelOrderAsync instead.'
    ///     warning CS0618: 'ITradingManager.ClosePosition(Position, bool, bool)' is obsolete:
    ///                     'Use ClosePositionAsync instead.'
    ///
    /// They are still what the adapter calls, deliberately, for now — and the reason recorded here
    /// used to be wrong, which is why it is spelled out rather than repeated.
    ///
    /// THE OLD REASON, AND WHY IT IS FALSE. It said switching "moves every refusal from thrown out of
    /// the call to faulted task", and that rule 3's classification is built on the first shape. Read
    /// the adapter's write path: there is no `catch` in it anywhere. Not one AtasRejectedException
    /// after submission comes out of an order call. Every one of them is manufactured from the
    /// `_failures` dictionary, which is written only by OnFailurePayload, which is fed only by ATAS's
    /// OrderRegisterFailed / OrderCancelFailed / OrderModifyFailed events — a path the sync/async
    /// choice does not touch at all. So the switch does not move the refusal path, and rule 3's
    /// classification is not what is at stake in it.
    ///
    /// WHAT IS ACTUALLY AT STAKE, AND WHY THE CALL SITES ARE STILL NOT FLIPPED. Timing. Place costs
    /// the call itself plus WaitFor(AckTimeout), and the connector gives up on any bridge operation
    /// after its own RPC timeout. If OpenOrderAsync's task completes on SUBMISSION, blocking on it
    /// costs about what the synchronous call costs and the switch is safe. If it completes only on
    /// broker ACKNOWLEDGEMENT, blocking on it puts Place past the connector's deadline and turns
    /// every order into UNKNOWN — the failure that is expensive to have and cheap to avoid. Which of
    /// the two it is has not been measured, and cannot be measured off Windows. Measure it there,
    /// then flip the four call sites as their own change.
    /// </summary>
    /// <param name="task">The ATAS call already in flight.</param>
    /// <param name="timeout">How long to wait before declaring the outcome unknown.</param>
    /// <param name="operation">Named in the message, so a log says which call went unanswered.</param>
    /// <param name="consequence">What an expiry MEANS for this particular caller, appended to the
    /// message. Defaults to <see cref="OrderConsequence"/>, which is right for every write path and
    /// wrong for anything else — the default was written when the only caller placed orders, and a
    /// message that tells a reader to reconcile an order that does not exist is worse than a vague
    /// one. Pass your own on any call that is not placing, modifying or cancelling.</param>
    public static void Block(Task task, TimeSpan timeout, string operation, string? consequence = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        // The wait is deliberately separated from the classification. Whatever WaitAsync raises is
        // swallowed here and the outcome is decided below from the state of the ORIGINAL task, so a
        // call that finishes in the same instant the deadline expires still reports what it really
        // did instead of losing to a race with a timer.
        try { task.WaitAsync(timeout).ConfigureAwait(false).GetAwaiter().GetResult(); }
        catch (Exception) { /* classified below, from `task` itself */ }

        if (!task.IsCompleted)
        {
            Observe(task);
            throw new AtasCallTimeoutException(
                $"ATAS did not answer '{operation}' within {timeout.TotalSeconds:0.##}s. We stopped waiting; " +
                $"ATAS did not, so whatever it was doing may still complete. {consequence ?? OrderConsequence}");
        }

        if (task.IsFaulted && task.Exception is { InnerExceptions.Count: > 1 } several) throw several;

        // Completed: success returns, a single fault comes back out with its own type and stack.
        task.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Keeps an abandoned call from becoming a stray.
    ///
    /// After the deadline the ATAS call is still running and may fault minutes later. Nobody is
    /// waiting on it by then, so its exception would go unobserved. This costs one continuation and
    /// removes a whole class of "something failed somewhere, later".
    /// </summary>
    static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
