using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE TIME A FLOW HAS SPENT WAITING ON THIS APP'S OWN STORE — counted for the flow that asked, and
/// for nobody else (<c>U-fix-loss-reopen</c>).
///
/// <para><b>Why anything counts it.</b> The app's own flatten runs on the connector's emergency
/// budget, and that budget is a real wall clock. Everything the flatten writes before a close may go
/// out — the press row, the composite, the leg's write-ahead row — is a durable commit at
/// <c>synchronous=FULL</c> on the same clock, and a commit is the machine's disk, not the platform. On
/// windows-latest one commit has measured 2234 ms, which is the whole of two seconds: the budget was
/// gone before the first platform call, the call was refused before the wire, and a confirmed breach
/// closed the day with the book still open. A slow Windows PC is exactly where this product runs, so
/// a budget that the app's own bookkeeping can spend is a budget that decides nothing about the
/// platform it was set for. <c>RiskReducingScope.BeginExcludingTheStore</c> is the one reader.</para>
///
/// <para><b>What is counted.</b> Every outermost <see cref="Database.Write{T}"/> and
/// <see cref="Database.Read{T}"/> on the counting flow, from before its lock to after its commit — so
/// a wait behind another thread's commit on the same store counts too, because that is the same disk.
/// A nested call is inside its outer call's time and is not counted twice. Nothing else is: a
/// connector that keeps its own book in its own SQLite file (the paper connector does) is the
/// PLATFORM from up here, and stays on the platform's clock.</para>
///
/// <para><b>Whose flow.</b> An <see cref="AsyncLocal{T}"/>, for <c>RiskReducingScope</c>'s reason:
/// set where the operation begins, it flows into every await below it and into nothing beside it, so
/// the event stream, the UI and the background loop writing at the same moment charge nobody.</para>
/// </summary>
public static class StoreTime
{
    static readonly AsyncLocal<StrongBox<long>?> Meter = new();

    /// <summary>
    /// Counts, into <paramref name="stopwatchTicks"/>, the <see cref="Stopwatch"/> ticks this flow and
    /// every flow it awaits spend inside a <see cref="Database"/>, until the handle is disposed.
    /// </summary>
    public static IDisposable CountInto(StrongBox<long> stopwatchTicks)
    {
        ArgumentNullException.ThrowIfNull(stopwatchTicks);
        var previous = Meter.Value;
        Meter.Value = stopwatchTicks;
        return new Restore(previous);
    }

    /// <summary>Whether the calling flow is being counted at all — the cheap check before a timestamp.</summary>
    internal static bool Counting => Meter.Value is not null;

    /// <summary>Adds one store call's time to the flow's meter, when it has one.</summary>
    internal static void Charge(long started)
    {
        if (Meter.Value is { } meter) Interlocked.Add(ref meter.Value, Stopwatch.GetTimestamp() - started);
    }

    sealed class Restore(StrongBox<long>? previous) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Meter.Value = previous;
        }
    }
}
