using System.Globalization;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BOARD SITS (<c>U-board-alive</c> item 1; R24 § 4.1, CHANGE-001, containment): the owner activates a dormant board seat by his
/// in-process press, and the app writes <c>active</c> only where this host could seat it — on the class it was founded on, a class whose
/// door carries the board's verbs, and never a real model before a RECORDED proven cell.
/// </summary>
public class BoardActivationTests(ITestOutputHelper log)
{
    static BoardSeat Seat(string id, string seatClass, string status = BoardSeatStatus.Dormant) =>
        new(id, seatClass, BoardModelClass.Strong, "openai", status, DateTimeOffset.UtcNow);

    static long StateRows(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM board_seat_state");
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// THE HOST'S FACTS DECIDE WHETHER A SEAT MAY BE ACTIVATED: a broker seat starting on a broker seat with a proven cell may (or on a test
    /// host's puppet, which runs no model); without a proven cell a real model is refused (containment); a seat that would start on another
    /// class than its founding one is refused naming both; a harness seat is refused — the harness's own surface carries no board verb; a
    /// retired, an active, a missing seat, or one no runtime could start yet, each refused in words. RED at base: <c>CS0117</c>, no
    /// <c>ActivationRefusal</c>. Watched red with the proven-cell conjunct removed.
    /// </summary>
    [Fact]
    public void A_board_seat_is_activated_only_where_this_host_could_seat_it()
    {
        var broker = Seat("board-1", SeatClasses.Broker);
        Assert.Null(BoardSeatGate.ActivationRefusal("board-1", broker, SeatClasses.Broker, puppet: false, provenCell: true, runtimeFamily: "openai"));
        Assert.Null(BoardSeatGate.ActivationRefusal("board-1", broker, SeatClasses.Broker, puppet: true, provenCell: false));

        var refusals = new (string Why, string? Refusal)[]
        {
            ("no proven cell", BoardSeatGate.ActivationRefusal("board-1", broker, SeatClasses.Broker, puppet: false, provenCell: false)),
            ("another class", BoardSeatGate.ActivationRefusal("board-1", broker, SeatClasses.Cli, puppet: false, provenCell: true)),
            ("no runtime", BoardSeatGate.ActivationRefusal("board-1", broker, null, puppet: false, provenCell: true)),
            ("harness", BoardSeatGate.ActivationRefusal("board-2", Seat("board-2", SeatClasses.Harness), SeatClasses.Harness, false, true)),
            ("retired", BoardSeatGate.ActivationRefusal("board-1", Seat("board-1", SeatClasses.Broker, BoardSeatStatus.Retired), SeatClasses.Broker, false, true)),
            ("active", BoardSeatGate.ActivationRefusal("board-1", Seat("board-1", SeatClasses.Broker, BoardSeatStatus.Active), SeatClasses.Broker, false, true)),
            ("missing", BoardSeatGate.ActivationRefusal("board-9", null, SeatClasses.Broker, false, true)),
            // THE REVIEW'S P1: a seat founded as one vendor family is never activated on another's runtime, nor on one of no known family.
            ("another vendor family", BoardSeatGate.ActivationRefusal("board-1", broker with { VendorFamily = "anthropic" }, SeatClasses.Broker, false, true, "openai")),
            ("no known family", BoardSeatGate.ActivationRefusal("board-1", broker, SeatClasses.Broker, false, true, null))
        };
        foreach (var (why, refusal) in refusals)
        {
            log.WriteLine($"{why}: {refusal}");
            Assert.True(refusal is { Length: > 0 }, $"a seat was activated with {why}");
        }
        Assert.Contains("no proven executor cell recorded", refusals[0].Refusal, StringComparison.Ordinal);
        Assert.Contains("founded on a broker seat and this host would start it on a cli seat", refusals[1].Refusal, StringComparison.Ordinal);
        Assert.Contains("only the app's door", refusals[3].Refusal, StringComparison.Ordinal);
        Assert.Contains("founded on the vendor family 'anthropic' and this host would start it on a runtime of 'openai'", refusals[7].Refusal,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OWNER'S PRESS, THROUGH THE PRODUCT'S OWN COMPOSITION: on a fresh home with no proven cell, activating board-1 is refused in the
    /// host's words and writes no state row — the board stays dormant; with a proven cell recorded on this host the same press writes ONE
    /// <c>active</c> row by <c>app</c>, the board is active (so his chief and activation presses are the board's), and nothing launched.
    /// A second press on the active seat is refused, and a retired seat never activates. RED at base: <c>CS1061</c>, no
    /// <c>AppHost.ActivateBoardSeat</c>.
    /// </summary>
    [Fact]
    public async Task The_owners_activation_writes_active_only_once_a_proven_cell_is_recorded()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            // THE RUNTIME HE CHOSE AT SETUP — codex, whose built-in manifest gives a board id the broker-only class — and no AI started.
            Assert.Contains("no AI runtime chosen", Assert.Throws<TradeAgentException>(() => host.ActivateBoardSeat("board-1")).Message,
                StringComparison.Ordinal);
            host.Gateway.Update(s => s.SelectedRuntimeId = TradeAgent.AgentRuntime.BrokerDoor.CodexId);
            var refused = Assert.Throws<TradeAgentException>(() => host.ActivateBoardSeat("board-1"));
            log.WriteLine(refused.Message);
            Assert.Contains("no proven executor cell recorded", refused.Message, StringComparison.Ordinal);
            Assert.EndsWith("Nothing was written.", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0L, StateRows(db));
            Assert.False(host.Board.AnyActive);

            ThinkingSeatsTests.ProveCell(db);
            var words = host.ActivateBoardSeat("board-1");
            log.WriteLine(words);
            Assert.Equal(BoardSeatStatus.Active, host.Board.Seats().Single(s => s.Id == "board-1").Status);
            Assert.True(host.Board.AnyActive);
            Assert.Equal(("active", "app"), db.Read(_ =>
            {
                using var c = db.Cmd("SELECT state, by FROM board_seat_state WHERE seat = 'board-1'");
                using var r = c.ExecuteReader();
                Assert.True(r.Read());
                return (r.GetString(0), r.GetString(1));
            }));
            Assert.DoesNotContain(new AiAttemptStore(db).Between(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)),
                a => BoardSeats.IsBoardId(a.Role));
            // ITS HOME AT ONCE (the review's P2): the board's canon in its folder, not at the next start.
            Assert.Contains("You act only through the board's verbs", File.ReadAllText(Path.Combine(host.HomeFor("board-1"), "AGENTS.md")),
                StringComparison.Ordinal);
            // A SEAT FOUNDED AS ANOTHER VENDOR FAMILY IS NOT ACTIVATED ON CODEX (the review's P1).
            var other = host.Gateway.FoundBoardSeat(SeatClasses.Broker, BoardModelClass.Strong, "anthropic");
            Assert.Contains("founded on the vendor family 'anthropic'", Assert.Throws<TradeAgentException>(() => host.ActivateBoardSeat(other.Id)).Message,
                StringComparison.Ordinal);

            Assert.Contains("is active: only a dormant seat is activated", Assert.Throws<TradeAgentException>(() => host.ActivateBoardSeat("board-1")).Message,
                StringComparison.Ordinal);
            host.Gateway.RetireBoardSeat("board-2");
            Assert.Contains("retired", Assert.Throws<TradeAgentException>(() => host.ActivateBoardSeat("board-2")).Message, StringComparison.Ordinal);
            Assert.Equal(2L, StateRows(db));
            Assert.Equal(BoardSeatStatus.Dormant, host.Board.Seats().Single(s => s.Id == other.Id).Status);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// A BOARD SEAT IS TOLD THE NAMES ITS DOOR SERVES (<c>U-board-alive</c> item 1): on a broker seat there is no <c>trade</c> command, so the
    /// packet and the canon name each board verb by the door's own name (<c>BrokerDoor.VerbName</c>) — and the packet still names the
    /// <c>trade board</c> form a puppet's path has. Neither names an organisation verb or a harness tool as the seat's.
    /// </summary>
    [Fact]
    public void A_board_seats_packet_and_canon_name_the_doors_board_verbs()
    {
        using var db = TestEnv.NewDb();
        var packet = BoardPacket.For(new BoardStore(db), "board-1", null, [], DateTimeOffset.UtcNow);
        var canon = BoardPacket.Canon("board-1");
        foreach (var op in Ops.Board)
        {
            Assert.Equal(TradeAgent.AgentRuntime.BrokerDoor.VerbName(op), BoardPacket.DoorName(op));
            Assert.Contains($"`{TradeAgent.AgentRuntime.BrokerDoor.VerbName(op)}`", packet, StringComparison.Ordinal);
            Assert.Contains(TradeAgent.AgentRuntime.BrokerDoor.VerbName(op), canon, StringComparison.Ordinal);
        }
        Assert.Contains("trade board decide", packet, StringComparison.Ordinal);
        foreach (var other in new[] { "mandate_", "read_file", "write_file", "`trade`" })
        {
            Assert.DoesNotContain(other, packet, StringComparison.Ordinal);
            Assert.DoesNotContain(other, canon, StringComparison.Ordinal);
        }
    }
}
