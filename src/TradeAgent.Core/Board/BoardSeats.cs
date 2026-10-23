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

/// <summary>A BOARD SEAT'S LIFE. Only <see cref="Dormant"/> is storable at rung 49: nothing launches a board seat yet (ORCHESTRATION § 2.3).</summary>
public static class BoardSeatStatus
{
    /// <summary>Founded and never launched: no turn, no act, no reach.</summary>
    public const string Dormant = "dormant";

    /// <summary>Past the activation gate and acting. No build stores it yet; <c>U-board-b</c>'s rung admits it.</summary>
    public const string Active = "active";
}

/// <summary>
/// ONE BOARD SEAT AS ITS ROW HOLDS IT: id, seat class (<see cref="BoardSeats.SeatClasses"/>), model class (<c>strong</c> or
/// <c>standard</c> — ORGANISATION § 10's class, which the runtime profile maps to a model), vendor family, status and when the app
/// founded it.
/// </summary>
public sealed record BoardSeat(string Id, string SeatClass, string ModelClass, string VendorFamily, string Status, DateTimeOffset FoundedAt);
