using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// WHEN EACH ROLE WAS LAST TOLD WHAT ARRIVED IN THE OWNER'S INBOX (<c>U-reconcile-wakes</c>).
///
/// <para>The Situation's "New in <c>../inbox</c> since your last turn" used to be measured from the last
/// Situation of EITHER role: a file listed in the Research Director's turn was never listed to the chair,
/// whose turn the inbox wake actually buys. Each role now has its own mark, so the sentence is true of the
/// role it is said to.</para>
///
/// <para><b>Read from the launch ledger, never kept</b> (review 1, findings 5 and 6). A role was told what its last
/// ADMITTED turn's Situation listed, and nothing a Situation composed for a launch that was then refused, or that a
/// restart forgot: the mark is the instant the role's latest launch that took a wake was minted
/// (<see cref="AiAttemptStore.LastWoken"/>, <see cref="TurnMeter.MintedAt"/>) — at or before the moment its
/// Situation read the material, so a file is listed again rather than never. A role with no such launch has been
/// told nothing, and is listed everything present. No row is written for it: composing a Situation moves no mark.</para>
/// </summary>
public static class RoleInboxMarks
{
    /// <summary>
    /// The instant <paramref name="role"/> was last told what had arrived, from its latest admitted launch, or
    /// <see cref="DateTimeOffset.MinValue"/> where it has none — or where the ledger cannot be read, which lists a
    /// file twice rather than hides one.
    /// </summary>
    public static DateTimeOffset Since(Database db, string role)
    {
        try
        {
            return new AiAttemptStore(db).LastWoken(role) is { } last
                ? TurnMeter.MintedAt(last.Id) ?? last.StartedAt
                : DateTimeOffset.MinValue;
        }
        catch (Exception) { return DateTimeOffset.MinValue; }
    }

    /// <summary>
    /// What has turned up in the owner's folder since <paramref name="since"/> — BOTH words for it,
    /// because the AI is being told a file exists, not who put it there; the distinction the ledger keeps
    /// is the owner's to read on the Inbox page. A ledger that cannot be read lists nothing rather than
    /// throwing out of the middle of a turn's message.
    /// </summary>
    public static IReadOnlyList<string> Arrived(Database db, DateTimeOffset since)
    {
        try
        {
            var store = new MaterialStore(db);
            return store.Present(MaterialOrigin.Inbox).Concat(store.Present(MaterialOrigin.InboxUnattested))
                .Where(m => m.FirstSeenAt >= since)
                .OrderBy(m => m.FirstSeenAt)
                .Select(m => m.Name)
                .Take(50)
                .ToArray();
        }
        catch (Exception) { return []; }
    }
}
