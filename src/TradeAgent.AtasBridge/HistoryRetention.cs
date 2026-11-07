namespace TradeAgent.AtasBridge;

/// <summary>
/// RULE 2 ON <see cref="IAtasAdapter"/>, THE ATAS HALF (ORDER-003): "Order history must really reach back to the timestamp asked for. If it
/// cannot, report <c>SupportsOrderHistory = false</c>. A partial history is worse than none." The ATAS adapter is compiled only on a box
/// with ATAS, so the rule it answers with lives here, compiled and tested everywhere, and the adapter calls it for both of its answers —
/// the capability in its handshake and the refusal in its history read (a test reads the adapter's source for the two calls).
///
/// <para>ATAS's order cache states how far back it keeps orders (<c>ICache.ClearCachePeriod</c>), or states nothing. A cache that states
/// nothing cannot show coverage of any window — "unstated" is not "keeps everything" (review 2026-09-05b UNVERIFIED 1 / Codex F8, where a
/// cache's mere presence answered every window as covered).</para>
/// </summary>
public static class HistoryRetention
{
    /// <summary>May the adapter report <c>SupportsOrderHistory = true</c>: only with a cache found AND a retention the platform states.</summary>
    public static bool Claims(bool cacheFound, TimeSpan retention) => cacheFound && retention > TimeSpan.Zero;

    /// <summary>
    /// Null when a history back to <paramref name="since"/> is covered by the stated <paramref name="retention"/> at <paramref name="now"/>;
    /// otherwise the refusal, in words, that the adapter throws as an ordinary exception — the gateway must see "I cannot answer that",
    /// never a short list. Compared as an age, so a retention too long to subtract from <paramref name="now"/> is still answered.
    /// </summary>
    public static string? Refusal(DateTimeOffset since, TimeSpan retention, DateTimeOffset now) =>
        retention <= TimeSpan.Zero
            ? $"ATAS does not say how long it keeps order history on this platform, so a history back to {since:O} cannot be shown to be "
              + "complete and must not be treated as proof; reconcile against the platform itself"
            : now - since > retention
                ? $"ATAS keeps order history for {retention}; {since:O} is further back than that, so this history would be incomplete and "
                  + "must not be treated as proof"
                : null;
}
