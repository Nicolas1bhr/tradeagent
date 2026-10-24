using TradeAgent.Core;
using TradeAgent.Core.Board;

namespace TradeAgent.Gateway;

/// <summary>
/// THE SECURITY BOARD'S ACTS AT THE GATEWAY (<c>U-board-b1</c>; R24 §§ 4.2–4.4). The board's verbs reach the store through here — INTERNAL,
/// so only the pipe's own handlers (this assembly) and the test host reach them, each behind the board's caller gate — and every act or
/// veto that commits is followed AT ONCE: the policy in force is read again before the verb answers, so the next order, the next turn
/// and the next sweep are held to it. A restart reads the store, as ever.
/// </summary>
public sealed partial class TradingGateway
{
    /// <summary>The policy in force, read again from the store: what every act and veto does the moment it commits.</summary>
    void RefreshPolicy()
    {
        _policy = PolicyInForce();
        StateChanged?.Invoke();
    }

    /// <summary>ONE ACTIVE SEAT NARROWS (<see cref="BoardStore.Narrow"/>), and the gateway holds the next order to it.</summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    internal BoardActResult BoardNarrow(BoardActRequest request)
    {
        var result = _board.Narrow(request, Now);
        RefreshPolicy();
        return result;
    }

    /// <summary>ONE ACTIVE SEAT PROPOSES A WIDENING (<see cref="BoardStore.Propose"/>): a record; nothing changes.</summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    internal BoardActResult BoardPropose(BoardActRequest request) => _board.Propose(request, Now);

    /// <summary>
    /// ANOTHER ACTIVE SEAT DECIDES A PROPOSAL (<see cref="BoardStore.Decide"/>) — a confirmation writes the version in the decision's own
    /// write, and the gateway holds the next order to it.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    internal BoardActResult BoardDecide(string actId, string seat, string? attempt, bool confirm, string intentSha256, string rationale)
    {
        var result = _board.Decide(actId, seat, attempt, confirm, intentSha256, rationale, Now);
        RefreshPolicy();
        return result;
    }

    /// <summary>One act as <paramref name="seat"/> may read it — blind until its own word on it is sealed (<see cref="BoardStore.ShowTo"/>).</summary>
    internal (BoardActBlindView? Blind, BoardActRecord? Whole) BoardShow(string seat, string actId) => _board.ShowTo(seat, actId);

    /// <summary>The acts, newest first, with no rationale or forecast (<see cref="BoardStore.ActList"/>).</summary>
    internal IReadOnlyList<BoardActBlindView> BoardList(int limit) => _board.ActList(limit);
}
