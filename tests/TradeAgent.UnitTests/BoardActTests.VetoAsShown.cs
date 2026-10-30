using TradeAgent.Core;
using TradeAgent.Core.Board;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    /// <summary>
    /// <c>U-VETO-ASSHOWN-REQUIRED</c> (the OPEN GAP named at U-org-surface-b's landing; AUTH-001): THE OWNER'S VETO IS BOUND TO THE PREVIEW HIS FIRST
    /// PRESS SHOWED, AND A CALLER THAT HANDS NONE IS REFUSED. <c>asShown</c> was optional — his press always passed it, but an in-process caller
    /// that left it out acted on an act whose effect it had never been shown. Now neither the gateway's veto nor the store's takes the
    /// call without one: the refusal is the first line, nothing is read and nothing is written, and the same act is vetoed by the same call
    /// handed the preview. RED at base: the veto went through with no preview (<c>Assert.Throws() Failure: No exception was thrown</c>).
    /// </summary>
    [Fact]
    public async Task A_veto_handed_no_preview_is_refused_and_writes_nothing()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        var open = gw.BoardPropose(Act("board-1", Raise()));
        var written = () => Scalar(db, "SELECT (SELECT COUNT(*) FROM board_act_event) || '/' || (SELECT COUNT(*) FROM board_policy)")!;
        var before = written();

        Assert.Throws<ArgumentNullException>(() => gw.VetoBoardAct(open.Act.Id, null!));
        Assert.Throws<ArgumentNullException>(() => new BoardStore(db).VetoBoardAct(open.Act.Id, DateTimeOffset.UtcNow, null!));
        Assert.Equal(before, written());
        Assert.Equal(BoardActStatus.Open, new BoardStore(db).Status(open.Act.Id));

        var vetoed = gw.VetoBoardAct(open.Act.Id, gw.PreviewBoardVeto(open.Act.Id));
        Assert.Equal(BoardActEventKind.Vetoed, vetoed.Status);
    }
}
