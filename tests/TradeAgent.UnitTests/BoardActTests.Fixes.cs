using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>THE FIX PASS (<c>U-board-b1</c>, the Codex review's five findings): each finding's settling check, red first.</summary>
public partial class BoardActTests
{
    /// <summary>
    /// A SAME-FAMILY REFUSAL RECORDS THE OTHER-FAMILY SEAT THAT IS ACTIVE (review P2, BoardStore.cs:376): the app's words sealed in a
    /// refusal from the proposer's own family are true — while a seat of another family is active they name it, and never say that no
    /// such seat could decide. RED before the fix: the refusal sealed "no active seat of another family could decide it". Watched red
    /// with the words chosen by the decision instead of by the seats.
    /// </summary>
    [Fact]
    public void A_same_family_refusal_records_the_other_family_seat_that_is_active()
    {
        using var db = TestEnv.NewDb();
        FoundOnAnotherFamily(db);
        Activate(db, "board-1", "board-2", "board-4");
        var store = new BoardStore(db);
        var now = DateTimeOffset.UtcNow;
        var act = store.Propose(Act("board-1", Raise()), now).Act;
        store.Decide(act.Id, "board-2", null, false, act.IntentSha256, "not on this evidence", now);
        var refused = store.Events(act.Id)[^1];
        log.WriteLine(refused.Detail);
        Assert.Equal((BoardActEventKind.Refused, (bool?)true), (refused.Kind, refused.SameFamily));
        Assert.DoesNotContain("no active seat of another family", refused.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("board-4", refused.Detail, StringComparison.Ordinal);
        Assert.Contains("anthropic", refused.Detail, StringComparison.Ordinal);
    }
}

public partial class BoardActTests
{
    /// <summary>
    /// AN OLDER READ OF THE POLICY NEVER REPLACES A NEWER ONE (review P1, TradingGateway.Board.cs:19): a refresh that read version 2 and
    /// was held there while another act wrote and published version 3 must not publish version 2 when it resumes — that would leave
    /// the gateway enforcing an older, wider limit after a narrowing completed. RED before the fix: the gateway held version 2's day of
    /// 30 after version 3's 20 was in force. Watched red with the version comparison removed from the publish.
    /// </summary>
    [Fact]
    public async Task An_older_read_of_the_policy_never_replaces_a_newer_one()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var armed = 1;
        gw.PolicyReadForRefresh = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1) return;
            paused.Set();
            resume.Wait(TimeSpan.FromSeconds(10));
        };

        var first = Task.Run(() => gw.BoardNarrow(Act("board-1", Change("plan_day_total", 40, 30))));
        Assert.True(paused.Wait(TimeSpan.FromSeconds(10)), "the first refresh never read the store");
        var second = gw.BoardNarrow(Act("board-2", Change("plan_day_total", 30, 20)));
        Assert.Equal(3, second.PolicyVersion);
        Assert.Equal(20m, gw.Policy.PlanDayTotal);
        resume.Set();
        Assert.Equal(2, (await first).PolicyVersion);

        Assert.Equal(20m, gw.Policy.PlanDayTotal);
        Assert.Equal(3, new BoardReader(db).InForce().Version);
    }
}
