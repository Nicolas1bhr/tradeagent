using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>
/// THE SECURITY BOARD'S SEATS, AS IDS (<c>U-board-a1</c>; R24 § 4.1): positions outside the chief's subtree and outside every
/// <c>org_*</c> table, minted by the app and never by an organisation verb — the third accountability line beside the audit line
/// and the watcher (VISION § 5.3). Being a board id refuses it wherever an app principal is refused today: a minted position, a
/// mandate's holder, a decider, a note's sender or recipient and a caller of the organisation's verbs (<c>OrgStore</c>).
///
/// <para><b>By prefix, spelled once.</b> No minted position id begins with it (<see cref="OrgIds.PositionPrefix"/> is
/// <c>pos-</c>), no legacy position and no app principal does, so the test is exact and cannot catch a position.</para>
/// </summary>
public static class BoardSeats
{
    /// <summary>The prefix every board seat id carries, and that nothing else on the chart may.</summary>
    public const string Prefix = "board-";

    /// <summary>The three seats of the founding composition (R24 § 4.1), in their order.</summary>
    public static readonly string[] Founding = ["board-1", "board-2", "board-3"];

    /// <summary>Whether <paramref name="id"/> names a board seat — ordinal, exact, by <see cref="Prefix"/>.</summary>
    public static bool IsBoardId(string? id) => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// THE SEAT CLASSES A BOARD SEAT MAY HAVE: broker-only, confined in a cell, or the app's own harness — never <c>cli</c>, a
    /// vendor program started with a shell (R24 § 4.1: "broker-only or confined, never unconfined"). The rung's CHECK holds the
    /// same list, so a row naming another class cannot be stored.
    /// </summary>
    public static readonly string[] SeatClasses = [Db.SeatClasses.Broker, Db.SeatClasses.Cell, Db.SeatClasses.Harness];

    /// <summary>The sentence refusing a board id where a position is named — the one spelling every refusal site uses.</summary>
    public static string NotAPosition(string id) =>
        $"'{id}' is a seat of the security board: outside the organisation's chart, never a position, and no organisation verb reaches it.";
}

/// <summary>
/// A BOARD SEAT'S LIFE (<c>board_seat_state</c>, rung 51): a seat's row is sealed and says <see cref="Dormant"/> for ever, so its state
/// now is its latest state row. Nothing in the product launches or activates a board seat yet (ORCHESTRATION § 2.3).
/// </summary>
public static class BoardSeatStatus
{
    /// <summary>Founded and never launched: no turn, no act, no reach.</summary>
    public const string Dormant = "dormant";

    /// <summary>Past the activation gate and acting — written by the app alone (in this build, by the test host's seam only).</summary>
    public const string Active = "active";

    /// <summary>Retired by the owner's press (CHANGE-001): no turn, no act, and never active again.</summary>
    public const string Retired = "retired";
}

/// <summary>
/// ONE BOARD SEAT AS ITS ROW HOLDS IT: id, seat class (<see cref="BoardSeats.SeatClasses"/>), model class (<c>strong</c> or
/// <c>standard</c> — ORGANISATION § 10's class, which the runtime profile maps to a model), vendor family, status and when the app
/// founded it.
/// </summary>
public sealed record BoardSeat(string Id, string SeatClass, string ModelClass, string VendorFamily, string Status, DateTimeOffset FoundedAt);

/// <summary>
/// THE ACTIVATION GATE FOR A BOARD SEAT (<c>U-board-b2</c> item 1; R24 § 4.1 "Its seats are real-model seats under the activation gate"):
/// the one rule the meter's admission, the host's seam and the last check before the process all ask for a board id, through
/// <see cref="PositionActivation.Decide"/>. A board seat is no position, so the chart's gate never passes it; this one does, and only:
/// its state now (<see cref="BoardStore.Seats"/>, the one reader) is <see cref="BoardSeatStatus.Active"/>; the seat about to start is
/// its RECORDED class — <see cref="BoardSeats.SeatClasses"/>, never <c>cli</c> — and that class carries the board's decision verbs
/// (<see cref="CapabilityPolicy.Minimum"/> of <see cref="Capability.DecisionVerbs"/>); a runtime that is not a test host's puppet runs a
/// real model and needs this host's PROVEN cell (<see cref="CellProofStore.HoldsProof"/>); and no unconfined launch is open beside it.
/// </summary>
public static class BoardSeatGate
{
    /// <summary>
    /// THE SEAT'S STANDING: null when <paramref name="seat"/> — as <see cref="BoardStore.Seat"/> read it now — is active, otherwise a
    /// refusal about the seat itself (<see cref="RefusalKind.Position"/>): dormant, retired, or no seat of the board at all.
    /// </summary>
    public static TurnRefusal? Standing(string id, BoardSeat? seat) => seat switch
    {
        { Status: BoardSeatStatus.Active } when seat.Id == id => null,
        null => new(RefusalKind.Position,
            $"'{id}' is no seat of the security board, so no turn is launched under it and none is charged to anyone. Nothing was written."),
        _ => new(RefusalKind.Position,
            $"board seat {id} is {seat.Status}, not active, so no turn is launched for it (R24 § 4.1: only an active seat turns). "
            + "Nothing was written.")
    };

    /// <summary>
    /// THE GATE'S ONE RULE FOR A BOARD SEAT: null when <paramref name="id"/> may launch now on the seat about to start
    /// (<paramref name="verifiedClass"/>, or the DECLARED one at the seam where nothing is verified yet), otherwise the refusal with its
    /// kind. <paramref name="puppet"/> is a test host's puppet runtime — no model runs — and is never true in the product.
    /// </summary>
    public static TurnRefusal? Decide(string id, BoardSeat? seat, string? declaredClass, string? verifiedClass, bool puppet,
        bool provenCell, IReadOnlyList<string> openUnconfined)
    {
        if (Standing(id, seat) is { } standing) return standing;
        var recorded = seat!.SeatClass;
        if (Array.IndexOf(BoardSeats.SeatClasses, recorded) < 0
            || SeatClasses.AssuranceOf(recorded) is not { } assurance
            || assurance < CapabilityPolicy.Minimum(Capability.DecisionVerbs))
            return new(RefusalKind.Position,
                $"board seat {id} is recorded on a '{recorded}' seat, which cannot carry the board's decision verbs (a board seat is "
                + "broker-only, confined or the app's harness, never unconfined), so nothing is started for it.");

        var about = verifiedClass ?? declaredClass;
        if (about is null)
            return new(RefusalKind.HostOrLaunch,
                $"board seat {id} has no seat this host established, so nothing is started for it. Its wakes are kept.");
        if (about != recorded)
            return new(RefusalKind.HostOrLaunch,
                $"board seat {id} is recorded on a {recorded} seat and the seat about to start is a {about} seat, so nothing is started: "
                + "a board seat launches only on the class it was founded on. Its wakes are kept.");

        if (!puppet && !provenCell)
            return new(RefusalKind.HostOrLaunch,
                $"board seat {id} would run a real model ({recorded}) and this host has no proven executor cell recorded: no board seat "
                + "runs on a real model before one (R24 § 4.1, ORCHESTRATION § 2.3). Nothing is started; its wakes are kept.");

        if (openUnconfined.Count > 0)
            return new(RefusalKind.HostOrLaunch,
                $"board seat {id} is not started while an unconfined launch is open on this host "
                + $"({string.Join(", ", openUnconfined.Select(o => $"'{o}'"))}). It is tried again when that turn has ended.");
        return null;
    }
}
