using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>
/// THE ONE WRITER OF THE BOARD'S TABLES (<c>U-board-a1</c>). The reader's shape: until rung 47 lands, every read answers the founding
/// record as the app will write it.
/// </summary>
public sealed class BoardStore(Database db)
{
    readonly Database _db = db;

    public BoardPolicyInForce InForce()
    {
        var body = BoardPolicyBody.Canonical(BoardFounding.V1);
        return new BoardPolicyInForce(1, BoardDecider.App, BoardBasis.Founding, default, BoardPolicyBody.Sha256(body),
            BoardFounding.V1, [.. BoardPolicyBody.Flatten(body).Select(f => new BoardPolicyField(f.Path, f.Value, 1))]);
    }

    public IReadOnlyList<BoardPolicyVersion> History()
    {
        var now = InForce();
        return [new BoardPolicyVersion(1, BoardDecider.App, BoardBasis.Founding, now.WrittenAt, now.BodySha256, null, [BoardDecider.App], null, null)];
    }

    public IReadOnlyList<BoardSeat> Seats() =>
        [.. BoardFounding.Seats.Select(s => new BoardSeat(s.Id, s.SeatClass, s.ModelClass, s.VendorFamily, BoardSeatStatus.Dormant, default))];
}

/// <summary>
/// WHAT AN EARLIER BUILD LEFT ARMED IN THIS HOME'S SETTINGS ROW (<c>U-board-a1</c> item 5): the raw <c>mode</c> and
/// <c>live_activated</c> values as found (null where the key was absent), the schema stamp the home had when this build first opened
/// it, and the sentence that refuses it — naming both versions and the repair. Read only; the row itself is never rewritten by
/// reading it.
/// </summary>
public sealed record EarlierLiveSetting(string? Mode, string? LiveActivated, int HomeSchema, string Sentence);
