using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE WALL-CLOCK LIMIT OF A TURN NOBODY'S LOOP IS WATCHING (<c>U-turn-bounds-2</c>; R28 W2): the turn the owner typed on the Chat page, which goes to its
/// conversation directly. The loop keeps its own clock around its own turns (<c>MissionLoop.RunAsync</c>) because it has more to end than the
/// conversation. Real time, never an injected clock: a stalled vendor stalls on the wall.
/// </summary>
public static class TurnClock
{
    /// <summary>THE LIMIT <paramref name="limit"/> ANSWERS. Never throws and never no limit: one that cannot say, or makes no sense, is <paramref name="fallback"/>.</summary>
    public static TimeSpan Of(Func<TimeSpan>? limit, TimeSpan fallback)
    {
        try { return limit?.Invoke() is { Ticks: > 0 } answered ? answered : fallback; }
        catch (Exception) { return fallback; }
    }

    /// <summary>
    /// Runs <paramref name="turn"/>, and past <paramref name="limit"/> calls <paramref name="expire"/>, which ends it. AWAITED before this returns, so a limit
    /// firing as the turn ends can never reach the conversation's next turn.
    /// </summary>
    public static async Task<T> RunAsync<T>(TimeSpan limit, Func<Task> expire, Func<Task<T>> turn)
    {
        using var stopped = new CancellationTokenSource();
        async Task Clock()
        {
            try { await Task.Delay(limit, stopped.Token); }
            catch (OperationCanceledException) { return; }
            try { await expire(); }
            catch (Exception) { /* a conversation that cannot end its turn has nothing running to end */ }
        }

        var clock = Clock();
        try { return await turn(); }
        finally
        {
            await stopped.CancelAsync();
            await clock;
        }
    }
}
