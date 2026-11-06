using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE WALL-CLOCK LIMIT OF A TURN NOBODY'S LOOP IS WATCHING (<c>U-turn-bounds-2</c>; R28 W2): the turn the owner typed on the Chat page, which goes to its
/// conversation directly. The loop keeps its own clock around its own turns (<c>MissionLoop.RunAsync</c>) because it has more to end than the
/// conversation. Real time, never an injected clock: a stalled vendor stalls on the wall. One clock is one turn's: the conversation starts it with the turn
/// and stops it BEFORE the turn stops being busy, so a limit firing as the turn ends can never reach the next turn.
/// </summary>
public sealed class TurnClock
{
    readonly CancellationTokenSource _stopped = new();
    readonly Task _running;

    /// <summary>Starts the clock: past <paramref name="limit"/> it calls <paramref name="expire"/>, which ends the turn.</summary>
    public TurnClock(TimeSpan limit, Func<Task> expire) => _running = Run(limit, expire);

    async Task Run(TimeSpan limit, Func<Task> expire)
    {
        try { await Task.Delay(limit, _stopped.Token); }
        catch (OperationCanceledException) { return; }
        try { await expire(); }
        catch (Exception) { /* a conversation that cannot end its turn has nothing running to end */ }
    }

    /// <summary>Stops the clock, and returns once nothing of it can still act on the turn.</summary>
    public async Task StopAsync()
    {
        await _stopped.CancelAsync();
        await _running;
        _stopped.Dispose();
    }

    /// <summary>THE LIMIT <paramref name="limit"/> ANSWERS. Never throws and never no limit: one that cannot say, or makes no sense, is <paramref name="fallback"/>.</summary>
    public static TimeSpan Of(Func<TimeSpan>? limit, TimeSpan fallback)
    {
        try { return limit?.Invoke() is { Ticks: > 0 } answered ? answered : fallback; }
        catch (Exception) { return fallback; }
    }
}
