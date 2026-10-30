using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class OrgChiefTests
{
    // ---- item 3: the board's route, on U-board-b1's machinery -----------------------------------------------------------------------

    /// <summary>The board's store as the gateway holds it: the runtimes this host knows are the ones a chief may be seated on.</summary>
    static BoardStore Board(Database db) => new(db) { KnownRuntime = Known };

    /// <summary>An intent of ONE change of the chief: from the sitting chief (or null), to a seat (or null).</summary>
    static string ChiefIntent(string? from, ChiefSeat? to) => JsonSerializer.Serialize(new object[]
    {
        new Dictionary<string, object?>
        {
            ["field"] = BoardDirection.Chief, ["from"] = from,
            ["to"] = to is null ? null : new Dictionary<string, string> { ["runtime"] = to.Runtime, ["model"] = to.Model }
        }
    });

    static BoardActRequest Act(string seat, string intent) => new(seat, null, intent, "the seat's own reasons, a claim", [], []);

    /// <summary>
    /// (3a) ONE BOARD SEAT NEVER APPOINTS OR REPLACES THE CHIEF: every change of <c>org.chief</c> WIDENS — an appointment, a replacement and a
    /// removal alike — so one active seat's narrowing of it is refused in words, nothing written and the chart as it was; a proposal of it
    /// applies nothing until a second seat confirms; and a change of the chief is an act of its own, never mixed with a policy field. RED
    /// at base: no <c>org.chief</c>. THE WATCHED MUTANT: <c>BoardDirection.Of</c> answering <c>org.chief</c> Narrows lets one seat's
    /// narrowing appoint a chief.
    /// </summary>
    [Fact]
    public void One_board_seat_never_appoints_or_replaces_the_chief()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var board = Board(db);
        ActivateBoard(db, "board-1", "board-2");

        var before = Written(db);
        var outcome = Record.Exception(() => board.Narrow(Act("board-1", ChiefIntent(null, Puppet)), Now));
        Assert.True(outcome is TradeAgentException && org.Chief() is null, $"one seat's narrowing appointed the chief {org.Chief()}");
        log.WriteLine(outcome!.Message);
        Assert.Contains("this intent widens", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        foreach (var (from, to) in new[] { ((string?)null, (ChiefSeat?)Puppet), (null, Codex) })
            Assert.Equal(BoardDirectionKind.Widens, BoardIntent.Parse(ChiefIntent(from, to)).Intent!.Direction);

        // A SITTING CHIEF (seated by the chart's writer, as a precondition): neither its replacement nor its removal is one seat's.
        var sitting = Seat(org, null, Puppet).Chief!;
        before = Written(db);
        foreach (var intent in new[] { ChiefIntent(sitting, Codex), ChiefIntent(sitting, null) })
        {
            Assert.Equal(BoardDirectionKind.Widens, BoardIntent.Parse(intent).Intent!.Direction);
            Assert.Contains("this intent widens", Assert.Throws<TradeAgentException>(() => board.Narrow(Act("board-1", intent), Now)).Message,
                StringComparison.Ordinal);
        }
        Assert.Equal(before, Written(db));
        Assert.Equal(sitting, org.Chief());

        // A PROPOSAL APPLIES NOTHING; A CHIEF NEVER RIDES WITH A POLICY FIELD; A CHANGE THAT CHANGES NOTHING IS NO CHANGE.
        var proposed = board.Propose(Act("board-1", ChiefIntent(sitting, Codex)), Now);
        Assert.Equal((BoardActStatus.Open, (int?)null), (proposed.Status, proposed.PolicyVersion));
        Assert.Equal(sitting, org.Chief());
        var mixed = JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, object?> { ["field"] = BoardDirection.Chief, ["from"] = sitting, ["to"] = null },
            new Dictionary<string, object?> { ["field"] = "plan_day_total", ["from"] = 40, ["to"] = 30 }
        });
        Assert.Contains("an act of its own", BoardIntent.Parse(mixed).Why, StringComparison.Ordinal);
        Assert.Contains("changes nothing", BoardIntent.Parse(ChiefIntent(null, null)).Why, StringComparison.Ordinal);
        Assert.Contains("runtime", BoardIntent.Parse(ChiefIntent(null, new ChiefSeat("", "m"))).Why, StringComparison.Ordinal);
        Assert.Contains("position", BoardIntent.Parse(ChiefIntent("operations", null)).Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// (3b) A CHIEF ACT BINDS TO ITS HASH, AND A MOVED CHIEF CLOSES IT STALE: one seat proposes a chief; a second seat's confirmation naming
    /// another hash is refused, nothing written; naming the act's own hash it appoints the chief in the decision's own write — DORMANT,
    /// decided by <c>board</c> on the basis of the act with the act's id in the detail, and NO policy version written — a chief change is
    /// structure, not policy (fix pass 1, seat B's ruling as the orchestrator amended it; no rung): <c>board_policy</c> holds as many rows as
    /// before, the policy in force is the one it was, and the confirmed event names the version IN FORCE it was decided under — never none.
    /// RED at the builder's tip (the deviation): the act wrote an unchanged version. THE WATCHED MUTANTS: the chief act writing the unchanged
    /// version again, or its event naming none, turns this red. A proposal to replace
    /// it, overtaken by the owner's removal, closes <c>stale</c> at its confirmation and applies nothing. A runtime this host does not know
    /// is refused at the proposal. RED at base: no <c>org.chief</c>.
    /// </summary>
    [Fact]
    public void A_chief_act_binds_to_its_hash_and_a_moved_chief_closes_it_stale()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var board = Board(db);
        ActivateBoard(db, "board-1", "board-2");
        var versions = Count(db, "board_policy");

        var proposed = board.Propose(Act("board-1", ChiefIntent(null, Puppet)), Now);
        var before = Written(db);
        Assert.Contains("is not the hash", Assert.Throws<TradeAgentException>(() =>
            board.Decide(proposed.Act.Id, "board-2", null, true, Sha256Hex.Of("another intent"), "my own reading", Now)).Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        Assert.Null(org.Chief());

        var inForce = new BoardReader(db).InForce();
        var confirmed = board.Decide(proposed.Act.Id, "board-2", null, true, proposed.Act.IntentSha256, "my own reading", Now);
        log.WriteLine(confirmed.Words);
        Assert.Equal((BoardActEventKind.Confirmed, (int?)null), (confirmed.Status, confirmed.PolicyVersion));
        var chief = org.Chief()!;
        Assert.Equal((OrgPositionStatus.Dormant, "puppet"), (org.Position(chief)!.Status, org.Position(chief)!.SeatRuntime));
        Assert.Equal(1, Count(db, "org_event",
            $"kind='{OrgEventKind.ChiefAppointed}' AND decider='board' AND detail LIKE '%basis=act act={proposed.Act.Id} %'"));
        // NO VERSION: the policy's rows as they were, the version in force the one it was, and the confirmed event naming none.
        var after = new BoardReader(db).InForce();
        Assert.Equal((versions, inForce.Version, inForce.BodySha256, inForce.Decider),
            (Count(db, "board_policy"), after.Version, after.BodySha256, after.Decider));
        Assert.Equal(1, Count(db, "board_act_event",
            $"act_id='{proposed.Act.Id}' AND kind='confirmed' AND policy_version = {inForce.Version} AND detail LIKE '%decided under version {inForce.Version}; no policy version is written%'"));
        Assert.Contains($"Decided under version {inForce.Version}; no policy version is written", confirmed.Words, StringComparison.Ordinal);
        Assert.Contains(chief, confirmed.Words, StringComparison.Ordinal);

        // OVERTAKEN: the owner removes the chief while a replacement waits; its confirmation closes it stale and applies nothing.
        var replace = board.Propose(Act("board-1", ChiefIntent(chief, Codex)), Now);
        org.VacateChief(org.Chief(), Now);
        var stale = board.Decide(replace.Act.Id, "board-2", null, true, replace.Act.IntentSha256, "my own reading", Now);
        Assert.Equal(BoardActEventKind.Stale, stale.Status);
        Assert.Contains("org.chief", stale.Words, StringComparison.Ordinal);
        Assert.Null(org.Chief());
        Assert.Equal(0, Count(db, "org_event", $"kind='{OrgEventKind.ChiefReplaced}'"));

        // A PROPOSAL NAMES THE CHIEF IN FORCE, AND A RUNTIME THIS HOST KNOWS.
        Assert.Contains("is not what", Assert.Throws<TradeAgentException>(() =>
            board.Propose(Act("board-1", ChiefIntent(chief, Codex)), Now)).Message, StringComparison.Ordinal);
        Assert.Contains("not a runtime this build knows", Assert.Throws<TradeAgentException>(() =>
            board.Propose(Act("board-1", ChiefIntent(null, new ChiefSeat("a-runtime-nobody-knows", "m1"))), Now)).Message, StringComparison.Ordinal);
        Assert.Contains("not a runtime this build knows", Assert.Throws<TradeAgentException>(() =>
            new BoardStore(db).Propose(Act("board-1", ChiefIntent(null, Puppet)), Now)).Message, StringComparison.Ordinal);
    }

    // ---- item 4: the owner's veto of a chief act ---------------------------------------------------------------------------------

    /// <summary>One chief act proposed by board-1 and confirmed by board-2, as the test's seats make it.</summary>
    static BoardActResult Confirmed(BoardStore board, string? from, ChiefSeat? to)
    {
        var proposed = board.Propose(Act("board-1", ChiefIntent(from, to)), Now);
        return board.Decide(proposed.Act.Id, "board-2", null, true, proposed.Act.IntentSha256, "my own reading", Now);
    }

    /// <summary>
    /// (4) THE OWNER'S VETO REMOVES A BOARD-APPOINTED CHIEF AND RESTORES NO ONE: vetoing a confirmed appointment whose chief still sits ends
    /// that chief through the chart's writer — decided by the owner on the basis of his veto, the act's id in the detail — and leaves the
    /// root vacant, writing no policy version; vetoing an act whose chief has since been replaced is recorded and removes no one, the chief
    /// now untouched; vetoing a replacement removes the replacing chief and restores NOT the one it replaced; vetoing a removal restores
    /// no one; vetoing an open chief proposal closes it, applying nothing. Press one shows what press two will do, and writes nothing. RED
    /// at base: no chief act.
    /// </summary>
    [Fact]
    public void The_owners_veto_removes_a_board_appointed_chief_and_restores_no_one()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var board = Board(db);
        ActivateBoard(db, "board-1", "board-2");

        var appointed = Confirmed(board, null, Puppet);
        var first = org.Chief()!;
        var before = Written(db);
        var preview = board.PreviewBoardVeto(appointed.Act.Id);
        log.WriteLine(preview.Words);
        Assert.True(preview.Allowed);
        Assert.Contains($"removes the chief {first}", preview.Words, StringComparison.Ordinal);
        Assert.Empty(preview.Fields);
        Assert.Equal(before, Written(db));
        var versions = Count(db, "board_policy");
        var vetoed = board.VetoBoardAct(appointed.Act.Id, Now, board.PreviewBoardVeto(appointed.Act.Id));
        Assert.Equal((BoardActEventKind.Vetoed, (int?)null), (vetoed.Status, vetoed.PolicyVersion));
        Assert.Null(org.Chief());
        Assert.Equal(OrgPositionStatus.Ended, org.Position(first)!.Status);
        Assert.Equal(versions, Count(db, "board_policy"));
        Assert.Equal(1, Count(db, "org_event",
            $"kind='{OrgEventKind.ChiefVacated}' AND decider='owner' AND detail LIKE '%basis=veto act={appointed.Act.Id} %'"));

        // AN ACT WHOSE CHIEF NO LONGER SITS: recorded, and the chief now untouched; THE REPLACEMENT's veto restores not the one it replaced.
        var second = Confirmed(board, null, Puppet);
        var b = org.Chief()!;
        var third = Confirmed(board, b, Codex);
        var c = org.Chief()!;
        var stale = board.VetoBoardAct(second.Act.Id, Now, board.PreviewBoardVeto(second.Act.Id));
        Assert.Contains("nothing is removed", stale.Words, StringComparison.Ordinal);
        Assert.Equal(c, org.Chief());
        board.VetoBoardAct(third.Act.Id, Now, board.PreviewBoardVeto(third.Act.Id));
        Assert.Null(org.Chief());
        Assert.Equal(OrgPositionStatus.Ended, org.Position(b)!.Status);
        Assert.Contains("vetoed already", Assert.Throws<TradeAgentException>(() => board.VetoBoardAct(third.Act.Id, Now, board.PreviewBoardVeto(third.Act.Id))).Message, StringComparison.Ordinal);

        // A REMOVAL'S VETO RESTORES NO ONE; AN OPEN PROPOSAL'S VETO APPLIES NOTHING.
        var d = Confirmed(board, null, Puppet);
        var sitting = org.Chief()!;
        var removal = Confirmed(board, sitting, null);
        Assert.Null(org.Chief());
        Assert.Contains("no one is restored", board.VetoBoardAct(removal.Act.Id, Now, board.PreviewBoardVeto(removal.Act.Id)).Words, StringComparison.Ordinal);
        Assert.Null(org.Chief());
        Assert.Equal(OrgPositionStatus.Ended, org.Position(sitting)!.Status);
        var open = board.Propose(Act("board-1", ChiefIntent(null, Codex)), Now);
        Assert.Equal(BoardActEventKind.Vetoed, board.VetoBoardAct(open.Act.Id, Now, board.PreviewBoardVeto(open.Act.Id)).Status);
        Assert.Null(org.Chief());
        Assert.Contains("vetoed", Assert.Throws<TradeAgentException>(() =>
            board.Decide(open.Act.Id, "board-2", null, true, open.Act.IntentSha256, "my own reading", Now)).Message, StringComparison.Ordinal);
        Assert.NotNull(d);
    }
}
