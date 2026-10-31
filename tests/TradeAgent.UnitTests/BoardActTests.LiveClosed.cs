using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    /// <summary>
    /// REAL MONEY STAYS CLOSED, WHOEVER ASKS, IN WORDS (<c>U-board-alive</c> item 4; R24 §§ 0.6, 4.5, SIGNAL-001): with two board seats
    /// active, one seat's narrowing naming <c>live_activated</c>, a proposal selecting <c>LIVE_AUTONOMOUS</c> (which a second seat would have
    /// confirmed) and a position's petition for real money's switch are each refused before any store is asked, saying that the connected
    /// venue is a practice platform and no live venue is connected — nothing written: no act, no petition, no wake, version 1 in force, the
    /// gateway in PAPER with real money off. No op of the pipe names a mode, live or an activation. RED at base: the store's own words
    /// (no venue named). Watched red with the gateway's check removed from <c>BoardPropose</c>.
    /// </summary>
    [Fact]
    public async Task Real_money_stays_closed_whoever_asks_and_says_no_live_venue_is_connected()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Activate(db, "board-1", "board-3");
        var before = Counts(db);

        var asks = new (string Who, string Role, string SeatClass, IpcRequest Frame)[]
        {
            ("one seat's narrowing", "board-1", SeatClasses.Broker, Frame(Ops.BoardNarrow,
                ("intent", """[{"field":"live_activated","from":true,"to":false}]"""), ("rationale", "a narrowing that names the switch"))),
            ("a proposal for two seats", "board-1", SeatClasses.Broker, Frame(Ops.BoardPropose,
                ("intent", """[{"field":"mode","from":"PAPER","to":"LIVE_AUTONOMOUS"}]"""), ("rationale", "the board decides alone"))),
            ("a position's petition", CouncilRoles.Research, SeatClasses.Harness, Frame(Ops.PetitionFile,
                ("intent", """[{"field":"live_activated","from":false,"to":true}]"""), ("rationale", "switch it on")))
        };
        foreach (var (who, role, seatClass, frame) in asks)
        {
            var refused = await server.CallAsync(frame, role, Launch(db, role, seatClass));
            log.WriteLine($"{who}: {refused.Error?.Message}");
            Assert.False(refused.Ok, $"{who} reached real money");
            Assert.Contains("Real money stays closed", refused.Error!.Message, StringComparison.Ordinal);
            Assert.Contains("is a practice platform, so no live venue is connected", refused.Error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, Counts(db));
        Assert.Empty(new BoardStore(db).Petitions());
        Assert.Equal(1, new BoardStore(db).InForce().Version);
        Assert.Equal((TradingMode.PAPER, false), (gw.Policy.Mode, gw.Policy.LiveActivated));
        // BY WORD (an op's name split on '-'): `mandate-deliver` holds "live" inside a word and is no real-money op.
        foreach (var word in new[] { "mode", "live", "activate", "activation", "arm" })
            Assert.DoesNotContain(GatewaySchema.Ops(), o => o.Op.Split('-').Contains(word, StringComparer.OrdinalIgnoreCase));
    }
}
