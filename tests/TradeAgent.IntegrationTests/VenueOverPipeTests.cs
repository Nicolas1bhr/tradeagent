using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ITEM 2 — `trade venue list` AS AN AGENT ACTUALLY REACHES IT: over the pipe, under a launch grant,
/// on the rows the app recorded.
///
/// <para>The unit tests hold the schema's wording and the catalogue's arithmetic. This holds the one
/// thing only the wire settles: that a READ is served to the part of the AI that needs it. The
/// mutating list is the role gate — <c>GatewayPipeServer</c> refuses a mutating op to everyone but the
/// Operations Director's own launch — so an op that found its way onto that list would be refused to
/// the Research Director, whose whole job is to size positions and who therefore has to be able to
/// read what a size rounds down to.</para>
/// </summary>
public class VenueOverPipeTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-venue-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>A gateway and a connection that has proved which launch it is. See BacktestOverPipeTests.</summary>
    static async Task<(TradingGateway Gw, Database Db, Raw Client, IAsyncDisposable Server)>
        Connected(string? role = CouncilRoles.Research, string attempt = "attempt-v1")
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var client = await Raw.ConnectAsync(pipe);
        var grant = role is null ? null : grants.Issue(role, attempt).Token;
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
        Assert.True(hello.Ok, Json.Write(hello.Error));

        return (gw, db, client, server);
    }

    /// <summary>The wire by hand: the machine token on every frame, and the grant on the hello.</summary>
    sealed class Raw : IAsyncDisposable
    {
        NamedPipeClientStream _pipe = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<Raw> ConnectAsync(string pipeName)
        {
            var c = new Raw { _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._pipe.ConnectAsync(5000);
            c._r = new StreamReader(c._pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> SendAsync(IpcRequest req)
        {
            req.Token ??= IpcToken.Ensure();
            await _w.WriteLineAsync(Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            return Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        public ValueTask DisposeAsync()
        {
            _r.Dispose();
            _w.Dispose();
            return _pipe.DisposeAsync();
        }
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    /// <summary>
    /// THE RESEARCH DIRECTOR — WHICH MAY PLACE NO ORDER AT ALL — IS SERVED THE CATALOGUE.
    ///
    /// <para>This is the mutant's test. Putting <c>venue-list</c> in <c>Ops.Mutating</c> costs nothing
    /// visible: no handler changes, no answer changes, and every test about the ROWS stays green. What
    /// changes is that the one role whose job is to propose sizes is refused the numbers those sizes
    /// round down to, with ROLE_MAY_NOT_TRADE, on a call that sends nothing anywhere.</para>
    /// </summary>
    [Fact]
    public async Task The_research_director_may_read_the_catalogue_over_the_wire()
    {
        var (gw, db, client, server) = await Connected(CouncilRoles.Research);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.VenueList, Session = "agent-r" });

        log.WriteLine(Json.Write(reply.Error));
        Assert.True(reply.Ok, Json.Write(reply.Error));

        var data = Data(reply);
        var venues = data.GetProperty("venues").EnumerateArray().ToList();
        var binance = venues.Single(v => v.GetProperty("id").GetString() == VenueCatalog.BinanceSpot);

        Assert.Equal(CalendarKind.Continuous, binance.GetProperty("calendar_kind").GetString());
        Assert.False(binance.GetProperty("verified").GetBoolean());

        var pair = binance.GetProperty("instruments").EnumerateArray()
            .Single(i => i.GetProperty("symbol").GetString() == "BTCUSDT");
        Assert.Equal(0.00001m, pair.GetProperty("quantity_increment").GetDecimal());
        Assert.False(pair.GetProperty("verified").GetBoolean());
        Assert.Contains("NOT confirmed", pair.GetProperty("source").GetString()!, StringComparison.Ordinal);

        // A null `unreadable` is a KEY that is present and null, not a key this build does not have:
        // "the catalogue is fine" and "this build cannot tell you" are different answers.
        Assert.Equal(JsonValueKind.Null, data.GetProperty("unreadable").ValueKind);
        Assert.Contains("NO fee and NO minimum notional", data.GetProperty("note").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The chair reads the same rows. Not a duplicate of the test above: the point of that one is the
    /// role that may NOT trade, and the point of this one is that the answer does not vary by caller —
    /// a catalogue that said one thing to one director and another to the other would be two
    /// catalogues.
    /// </summary>
    [Fact]
    public async Task The_chair_is_served_the_same_catalogue()
    {
        var (gw, db, client, server) = await Connected(CouncilRoles.Operations);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.VenueList, Session = "agent-o" });
        Assert.True(reply.Ok, Json.Write(reply.Error));

        var data = Data(reply);
        Assert.Equal(gw.Venues.Venues().Count, data.GetProperty("venue_count").GetInt32());
        Assert.Equal(gw.Venues.Instruments().Count, data.GetProperty("instrument_count").GetInt32());
    }

    /// <summary>There is no op on this channel that writes a venue, and asking for one is refused.</summary>
    [Fact]
    public async Task There_is_no_operation_on_this_channel_that_writes_a_venue()
    {
        var (gw, db, client, server) = await Connected(CouncilRoles.Operations);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        foreach (var op in new[] { "venue-add", "venue-set", "venue-verify", "venue-delete" })
        {
            var reply = await client.SendAsync(new IpcRequest { Op = op, Session = "agent-o" });
            Assert.False(reply.Ok, op);
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
            Assert.Contains("unknown operation", reply.Error.Message, StringComparison.Ordinal);
        }
    }
}
