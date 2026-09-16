using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A LAUNCH GRANT IS WORTH WHAT IT IS WORTH RIGHT NOW, ON EVERY FRAME, NOT WHAT IT WAS WORTH AT HELLO.
///
/// <para>REVIEW 2026-09-16 finding 5, probe <c>C1</c>, brought over here and turned from an
/// observation into an assertion. The probe found that <c>GatewayPipeServer</c> verified the grant
/// once, inside the <c>hello</c> arm, and then carried the role it proved into every later frame on
/// that connection: an EXPIRED, REVOKED or turn-ended grant kept placing orders for as long as the
/// caller held the socket, while a NEW connection presenting the same grant was refused
/// <c>IPC_UNAUTHENTICATED</c>. Revocation that does not reach a live connection is not revocation.</para>
///
/// <para>Four endings, because they are four different mechanisms and only two of them are the same
/// fix: expiry and revocation are caught by re-verifying the grant on the frame, the turn ending
/// INSIDE the 60 s grace is caught only by the ending closing the socket — <see cref="AgentGrants.Grace"/>
/// is for a call already in flight and for a <c>trade</c> launched a moment before the turn ended,
/// never for a further frame on a connection whose turn is over.</para>
///
/// <para>Every assertion here is settled at the WIRE, through <c>RecordingConnector</c>, because a
/// refusal code proves nothing about whether an order left this process.</para>
/// </summary>
[Collection("containment")]
public class GrantLivenessTests(ITestOutputHelper log)
{
    /// <summary>How the turn ended. Each is a mechanism, not a restatement of the others.</summary>
    public enum Ending
    {
        /// <summary>Nobody said anything; the clock passed the expiry an hour ago.</summary>
        Expired,

        /// <summary>The owner revoked the token outright.</summary>
        Revoked,

        /// <summary>The turn ended, and the grace elapsed too.</summary>
        DisposedAndLapsed,

        /// <summary>The turn ended one moment ago and the 60 s grace is still running.</summary>
        DisposedInsideTheGrace
    }

    /// <summary>
    /// C1, renamed and asserted: a buy on the connection the grant authenticated, after the turn is
    /// over, must not reach the wire — and the ending must reach the socket, so that in three of the
    /// four the caller cannot even send the frame.
    /// </summary>
    [Fact]
    public async Task An_ended_grant_places_no_further_order_on_the_connection_it_authenticated()
    {
        var verdicts = new List<string>();
        var failures = new List<string>();

        foreach (var how in Enum.GetValues<Ending>())
        {
            var (before, after, placedAfter, fresh) = await OneEnding(how, Ops.Buy);

            // THE GRACE IS NOT A HOLE AND MUST NOT BECOME ONE. Inside it a FRESH connection is still
            // served — that is a `trade` the agent started a moment before its turn ended, and it is
            // the whole reason the expiry is pulled in rather than deleted. Asserted here so that
            // closing the live socket cannot be mistaken for permission to delete the grace.
            var expected = how == Ending.DisposedInsideTheGrace ? "ACCEPTED" : nameof(ErrorCode.IPC_UNAUTHENTICATED);

            verdicts.Add($"[{how}] before={before} after={after} orders after={placedAfter} new connection={fresh}");
            log.WriteLine($"[{how}]");
            log.WriteLine($"  buy before, on the live connection : {before}");
            log.WriteLine($"  buy after,  on the live connection : {after}");
            log.WriteLine($"  orders that reached the wire after : {placedAfter}");
            log.WriteLine($"  a NEW connection with the same grant: {fresh}");
            log.WriteLine("");

            if (before != "SENT") failures.Add($"[{how}] the setup never placed the FIRST order: {before}");
            if (placedAfter != 0) failures.Add($"[{how}] {placedAfter} order(s) reached the broker AFTER the turn ended");
            if (after == "SENT") failures.Add($"[{how}] the gateway served a buy on a connection whose grant had ended");
            if (fresh != expected)
                failures.Add($"[{how}] a NEW connection with the same grant was answered {fresh}, expected {expected}");
        }

        Assert.True(failures.Count == 0,
            "an ended launch grant kept its connection's permission:\n  " + string.Join("\n  ", failures) +
            "\nall four endings:\n  " + string.Join("\n  ", verdicts));
    }

    /// <summary>
    /// The same question asked of a RISK-REDUCING op, so that a guard written on the placement path
    /// alone is a red test rather than a reading. <c>close-all</c> is an order at the broker exactly
    /// as a buy is, and the ending it is sent after is the one that closes no socket — a grant
    /// nobody revoked and nobody disposed, which simply expired.
    /// </summary>
    [Fact]
    public async Task An_expired_grant_sends_no_close_all_on_the_connection_it_authenticated()
    {
        var (before, after, sentAfter, fresh) = await OneEnding(Ending.Expired, Ops.CloseAll);

        log.WriteLine($"  buy before, on the live connection    : {before}");
        log.WriteLine($"  close-all after, on that connection   : {after}");
        log.WriteLine($"  mutating calls at the wire after      : {sentAfter}");
        log.WriteLine($"  a NEW connection with the same grant  : {fresh}");

        Assert.Equal("SENT", before);
        Assert.False(after == "SENT",
            "the gateway served a close-all on a connection whose launch grant had expired an hour earlier");
        Assert.True(sentAfter == 0,
            $"{sentAfter} mutating call(s) reached the broker on an expired launch grant — a risk-reducing " +
            "op is still an order, and the grant that authorised it was over");
    }

    /// <summary>
    /// One connection, authenticated with the chair's own grant: a buy that must be served, then the
    /// ending, then <paramref name="after"/> on the SAME connection, then a fresh connection
    /// presenting the same grant so that the guard which does exist is stated beside the one that
    /// did not.
    /// </summary>
    async Task<(string Before, string After, int Wire, string Fresh)> OneEnding(Ending how, string after)
    {
        var pipe = "ta-glive-" + Guid.NewGuid().ToString("n")[..12];
        var now = DateTimeOffset.UtcNow;
        var grants = new AgentGrants(() => now);
        using var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var chair = grants.Issue(CouncilRoles.Operations, "attempt-liveness", TimeSpan.FromMinutes(5));
        await using var client = await Raw.ConnectAsync(pipe);
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = chair.Token });
        Assert.True(hello.Ok, $"the chair's own grant was refused at hello: {hello.Error?.Code}");

        var first = await client.SendAsync(Buy(chair.Token));
        // EVERY MUTATING CALL, not just the placements: a close-all reaches the platform as a close
        // OR, for a connector that ignores the close intent, as an ordinary offsetting placement,
        // and "no order left this process" has to cover both.
        var wireBefore = conn.Mutations;

        switch (how)
        {
            case Ending.Expired: now = now.AddHours(1); break;
            case Ending.Revoked: grants.Revoke(chair.Token); break;
            case Ending.DisposedAndLapsed: chair.Dispose(); now = now.AddHours(1); break;
            default: chair.Dispose(); break;      // inside the 60 s grace, which is not for this frame
        }

        // The SAME connection, after the turn is over.
        var second = await client.TrySendAsync(after == Ops.CloseAll
            ? new IpcRequest { Op = Ops.CloseAll, Token = IpcToken.Ensure(), Session = "operations", RequestId = Rid(), Grant = chair.Token }
            : Buy(chair.Token));

        var wire = conn.Mutations - wireBefore;

        // And a FRESH connection presenting the same grant, so the guard that does exist is stated.
        await using var again = await Raw.ConnectAsync(pipe);
        var rehello = await again.TrySendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = chair.Token });

        return (Outcome(first), second, wire, rehello == "SENT" ? "ACCEPTED" : rehello);
    }

    static string Rid() => $"gl-{Guid.NewGuid():n}"[..16];

    static string Outcome(IpcResponse r) => r.Ok ? "SENT" : r.Error?.Code ?? "REFUSED";

    static IpcRequest Buy(string grantToken) => new()
    {
        Op = Ops.Buy, Token = IpcToken.Ensure(), Session = "operations", RequestId = Rid(), Grant = grantToken,
        Args = new()
        {
            ["symbol"] = JsonSerializer.SerializeToElement("ES"),
            ["quantity"] = JsonSerializer.SerializeToElement("1")
        }
    };

    /// <summary>
    /// A client that sends exactly the frame it is given, as <c>LaunchGrantTests.Raw</c> does:
    /// <c>PipeClient</c> always says hello with whatever grant the environment holds, which is right
    /// for the product and the wrong instrument for asking what happens when a peer does otherwise.
    /// </summary>
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
            await _w.WriteLineAsync(Core.Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            return Core.Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        /// <summary>
        /// The frame's outcome as a WORD, because "the gateway closed the connection on me" is one of
        /// the answers this test is asking about and an exception is not an answer it can compare.
        /// </summary>
        public async Task<string> TrySendAsync(IpcRequest req)
        {
            try { return Outcome(await SendAsync(req)); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return "CLOSED"; }
        }

        public async ValueTask DisposeAsync()
        {
            try { _w.Dispose(); _r.Dispose(); await _pipe.DisposeAsync(); } catch (Exception) { }
        }
    }
}
