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
/// <para>In memory, from the moment the app started, exactly as the shared mark was: material that arrived
/// while the app was closed is the inbox wake's to announce, and a mark that outlived a restart would be a
/// second ledger of what the agent was told.</para>
/// </summary>
public sealed class RoleInboxMarks(DateTimeOffset start)
{
    readonly Dictionary<string, DateTimeOffset> _at = [];
    readonly Lock _gate = new();

    /// <summary>
    /// The instant <paramref name="role"/> was last told, and from now on <paramref name="now"/>: one
    /// read and one move, so two roles composing at once each get their own window.
    /// </summary>
    public DateTimeOffset Take(string role, DateTimeOffset now)
    {
        lock (_gate)
        {
            var since = _at.TryGetValue(role, out var at) ? at : start;
            _at[role] = now;
            return since;
        }
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
