using TradeAgent.Core;
using TradeAgent.Core.Board;

namespace TradeAgent.Gateway;

/// <summary>
/// THE SECURITY BOARD'S ACTS AT THE GATEWAY (<c>U-board-b1</c>; R24 §§ 4.2–4.4). The board's verbs reach the store through here — INTERNAL,
/// so only the pipe's own handlers (this assembly) and the test host reach them, each behind the board's caller gate — and every act or
/// veto that commits is followed AT ONCE: the policy in force is read again before the verb answers, so the next order, the next turn
/// and the next sweep are held to it; and the standing paper envelope follows its terms — an act that changes them withdraws the one
/// granted under the old terms IN THE ACT'S OWN WRITE (the existing withdraw path), and the health pass grants what the new terms state
/// (<see cref="GrantPolicyEnvelopeIfDue"/>). A restart reads the store, as ever.
/// </summary>
public sealed partial class TradingGateway
{
    /// <summary>The policy in force, read again from the store: what every act and veto does the moment it commits.</summary>
    void RefreshPolicy()
    {
        _policy = PolicyInForce();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// ONE ACT'S WRITE, AND THE ENVELOPE FOLLOWING IT: <paramref name="act"/> runs inside one transaction with the withdrawal of the
    /// standing policy envelope its version's new terms no longer state; then the policy is read again. A refusal rolls both back.
    /// </summary>
    BoardActResult Followed(Func<BoardActResult> act)
    {
        var result = _db.Write(_ =>
        {
            var before = _board.InForce();
            var done = act();
            if (done.PolicyVersion is { } version) WithdrawEnvelopeIfTermsMoved(before, _board.InForce(), version);
            return done;
        });
        RefreshPolicy();
        return result;
    }

    /// <summary>
    /// A NEW VERSION WHOSE ENVELOPE TERMS DIFFER FROM THE ONE BEFORE WITHDRAWS THE POLICY'S STANDING ENVELOPE on the old terms' venue and
    /// account — the founding one or a board act's, never one the owner granted himself — inside the act's write. Said in the security
    /// board's name and the version's, never the owner's.
    /// </summary>
    void WithdrawEnvelopeIfTermsMoved(BoardPolicyInForce before, BoardPolicyInForce after, int version)
    {
        if (before.CouldNotBeRead || before.Policy.PaperEnvelope is not { } old) return;
        if (!after.CouldNotBeRead && Equals(after.Policy.PaperEnvelope, old)) return;
        var now = Now;
        foreach (var envelope in _envelopes.All(int.MaxValue).Where(e => e.StandsAt(now) && PolicyGranted(e)
                     && string.Equals(e.ConnectorId, old.Connector, StringComparison.Ordinal)
                     && string.Equals(e.AccountId, old.Account, StringComparison.Ordinal)).ToList())
        {
            var withdrawn = _envelopes.Withdraw(envelope.Id, now);
            if (!withdrawn.Ok) throw new TradeAgentException(ErrorCode.INVALID_REQUEST, withdrawn.Why + " Nothing was written.");
            _log.Activity($"The security board's version {version} changed the standing paper envelope's terms, so the envelope on account "
                          + $"{envelope.AccountId} granted under the earlier terms is withdrawn: nothing new starts under it, and what was "
                          + "allocated under it may trade nothing. The new terms, if any, are granted on the next pass.");
        }
    }

    /// <summary>ONE ACTIVE SEAT NARROWS (<see cref="BoardStore.Narrow"/>), and the gateway holds the next order to it.</summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    internal BoardActResult BoardNarrow(BoardActRequest request) => Followed(() => _board.Narrow(request, Now));

    /// <summary>ONE ACTIVE SEAT PROPOSES A WIDENING (<see cref="BoardStore.Propose"/>): a record; nothing changes.</summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    internal BoardActResult BoardPropose(BoardActRequest request) => _board.Propose(request, Now);

    /// <summary>
    /// ANOTHER ACTIVE SEAT DECIDES A PROPOSAL (<see cref="BoardStore.Decide"/>) — a confirmation writes the version in the decision's own
    /// write, the envelope following it, and the gateway holds the next order to it.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    internal BoardActResult BoardDecide(string actId, string seat, string? attempt, bool confirm, string intentSha256, string rationale) =>
        Followed(() => _board.Decide(actId, seat, attempt, confirm, intentSha256, rationale, Now));

    /// <summary>One act as <paramref name="seat"/> may read it — blind until its own word on it is sealed (<see cref="BoardStore.ShowTo"/>).</summary>
    internal (BoardActBlindView? Blind, BoardActRecord? Whole) BoardShow(string seat, string actId) => _board.ShowTo(seat, actId);

    /// <summary>A board seat's state now — dormant, active or retired — or null for an id that is no seat of the board.</summary>
    internal string? BoardSeatState(string seat) => _board.Seat(seat)?.Status;

    /// <summary>The acts, newest first, with no rationale or forecast (<see cref="BoardStore.ActList"/>).</summary>
    internal IReadOnlyList<BoardActBlindView> BoardList(int limit) => _board.ActList(limit);

    /// <summary>A seat's proposal of the board's own power (<see cref="BoardStore.ProposePower"/>): a record; nothing applies it.</summary>
    internal BoardPowerProposalRow BoardProposePower(string seat, string? attempt, string target, string text) =>
        _board.ProposePower(seat, attempt, target, text, Now);

    // ---- the owner's in-process seams over the board's own power (AUTH-001, CHANGE-001) ----------------------------------------------

    /// <summary>
    /// THE OWNER SETS THE BOARD'S BUDGET SHARE (R24 Q7) — his press, two presses in his UI; IN PROCESS ONLY: no pipe op, no verb and no
    /// <c>trade</c> command reaches it (a guard reads their sources). Said in the Activity log as his.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public decimal SetBoardBudgetShare(decimal share)
    {
        var set = _board.SetBoardBudgetShare(share, Now);
        _log.Activity($"You set the security board's share of the plan's AI day-total to {set.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        StateChanged?.Invoke();
        return set;
    }

    /// <summary>
    /// THE OWNER FOUNDS A BOARD SEAT (R24 Q6) — his press, in process only: dormant, on a class that is never a shell, the board kept on
    /// at least two model classes. Founding activates nothing.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public BoardSeat FoundBoardSeat(string seatClass, string modelClass, string vendorFamily)
    {
        var seat = _board.FoundBoardSeat(seatClass, modelClass, vendorFamily, Now);
        _log.Activity($"You founded the security board's seat {seat.Id} ({seat.SeatClass}, {seat.ModelClass}, {seat.VendorFamily}). It is dormant.");
        StateChanged?.Invoke();
        return seat;
    }

    /// <summary>THE OWNER RETIRES A BOARD SEAT — his press, in process only; the board kept on at least two model classes.</summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public BoardSeat RetireBoardSeat(string id)
    {
        var seat = _board.RetireBoardSeat(id, Now);
        _log.Activity($"You retired the security board's seat {seat.Id}. It acts no more.");
        StateChanged?.Invoke();
        return seat;
    }
}
