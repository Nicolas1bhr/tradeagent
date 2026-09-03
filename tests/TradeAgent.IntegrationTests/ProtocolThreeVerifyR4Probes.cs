using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// U14 round-4 ADVERSARIAL-VERIFY probes for target 2 (protocol 3), over a REAL named pipe with the
/// REAL authenticating stand-in bridge. The suite's existing wire-level version test uses
/// `BridgeProtocolVersion + 1` (a NEWER peer); nothing exercised the literal **2** that the DLL
/// deployed on the ATAS box actually answers, which is the case the bump exists for.
/// </summary>
public class ProtocolThreeVerifyR4Probes
{
    static string NewPipe() => "ta-p3-" + Guid.NewGuid().ToString("n")[..12];

    static async Task Wait(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>A literal version-2 bridge — the DLL on the box — is refused, and gains nothing.</summary>
    [Fact]
    public async Task A_version_two_bridge_is_refused_and_nothing_it_claims_gets_through()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var stub = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = 2,                 // the deployed DLL, literally
            BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM",
            SupportsClientOrderId = true, SupportsOrderHistory = true,
            SupportsModify = true, SupportsClosePosition = true
        });
        await stub.ConnectAsync();

        await Wait(() => connector.Incompatible is not null);

        Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);
        Assert.Equal(3, connector.Incompatible!.ExpectedProtocolVersion);
        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.SupportsClientOrderId);
        Assert.False(connector.Capabilities.SupportsOrderHistory);
        Assert.False(connector.Capabilities.ReconciliationProvable);
        Assert.False(await connector.IsConnectedAsync());
    }

    /// <summary>The other direction: a version-3 bridge is accepted and its hello is the app's.</summary>
    [Fact]
    public async Task A_version_three_bridge_is_accepted()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var stub = new StubBridge(pipe);   // defaults to Versions.BridgeProtocolVersion
        await stub.ConnectAsync();

        await Wait(() => connector.Bridge is not null);

        Assert.Equal(3, Versions.BridgeProtocolVersion);
        Assert.Null(connector.Incompatible);
        Assert.Equal(3, connector.Bridge!.BridgeProtocolVersion);
        Assert.True(connector.Capabilities.SupportsClientOrderId);
    }

    /// <summary>
    /// witness_failure travels the whole way: bridge hello → connector → AtasHealth.BridgeRow, which
    /// must read DEGRADED and NAME THE FILE. The suite's health test builds the hello by hand; this
    /// takes the one the connector actually received off the wire.
    /// </summary>
    [Fact]
    public async Task A_witness_failure_on_a_version_three_hello_reaches_the_health_row_naming_the_file()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        const string trouble = @"ERROR coid-witness rewrite did not land. file=C:\Users\m\AppData\Local\TradeAgent\bridge\coid-witness.json claim=TA-7 IOException: sharing violation";
        await using var stub = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM",
            SupportsClientOrderId = true, SupportsOrderHistory = true,
            WitnessFailure = trouble
        });
        await stub.ConnectAsync();

        await Wait(() => connector.Bridge is not null);

        var hello = connector.Bridge!;
        Assert.Equal(trouble, hello.WitnessFailure);

        var (state, detail) = AtasHealth.BridgeRow(
            true, Machine(), HealthState.READY, hello, null);

        Assert.Equal(HealthState.DEGRADED, state);
        Assert.Contains("orders are being refused", detail);
        Assert.Contains("coid-witness.json", detail);
    }

    static AtasDetection Machine() =>
        new(true, @"C:\ATAS", @"C:\strategies", "8.0.14.397", true, true, true);
}

/// <summary>
/// U14 round-5 probe for target 5: the builder fixed F9's events half for an INCOMPATIBLE peer and
/// flagged an adjacent one — "a peer whose hello is refused as UNPROVED is also authenticated; its
/// events still flow" — as a separate finding for the manager. This runs that check rather than
/// reading it: a raw pipe client that never answers the challenge, sends a hello, and then raises
/// events, against a real AtasConnector.
/// </summary>
public class UnprovedPeerVerifyR5Probes
{
    static string NewPipe() => "ta-up5-" + Guid.NewGuid().ToString("n")[..12];

    static async Task Wait(Func<bool> c, int ms = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline) { if (c()) return; await Task.Delay(50); }
        throw new TimeoutException("condition was not met in time");
    }

    [Fact]
    public async Task An_unproved_peer_raises_no_events_into_the_application()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        var seen = 0;
        connector.OrderChanged += _ => Interlocked.Increment(ref seen);
        connector.QuoteChanged += _ => Interlocked.Increment(ref seen);
        connector.ExecutionReceived += _ => Interlocked.Increment(ref seen);
        connector.PositionChanged += _ => Interlocked.Increment(ref seen);
        connector.AccountChanged += _ => Interlocked.Increment(ref seen);
        connector.ConnectionChanged += _ => { };

        // A raw peer: it NEVER answers the auth challenge, and its protocol version is current, so
        // the only thing wrong with it is that it presented no proof.
        await using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        // NOT `await using`: the connector drops an unproved peer, and disposing a StreamWriter over
        // a dead pipe throws from the flush in teardown — the test's own plumbing, not the subject.
        var w = new StreamWriter(client, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

        // Hello and events in ONE burst, so the events are on the wire before this end can act on
        // the refusal. Any write may fail once the peer is dropped — that outcome is a refusal too.
        try
        {
            await w.WriteLineAsync(Json.Write(new BridgeFrame
            {
                Op = BridgeOps.Hello,
                Data = System.Text.Json.JsonSerializer.SerializeToElement(new BridgeHello
                {
                    BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    BridgeVersion = "9.9.9", AtasVersion = "6.1.2.3",
                    SupportsClientOrderId = true, SupportsOrderHistory = true
                }, Json.Options)
            }));
            await w.WriteLineAsync(Json.Write(new { v = Versions.BridgeProtocolVersion, @event = BridgeEvents.Quote, data = new { symbol = "ES", bid = 1.0, ask = 2.0 } }));
            await w.WriteLineAsync(Json.Write(new { v = Versions.BridgeProtocolVersion, @event = BridgeEvents.Order, data = new { id = "X", status = "Filled" } }));
        }
        catch (IOException) { /* the peer was dropped mid-burst; that is a refusal too */ }

        // Either outcome is a refusal: the connector names it unauthenticated, or it has already
        // dropped the peer. Waiting for the first alone raced the second under load.
        try { await Wait(() => connector.Unauthenticated is not null, 3_000); } catch (TimeoutException) { }
        await Task.Delay(400);

        Assert.Equal(0, seen);
        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.SupportsClientOrderId);
        Assert.False(connector.Capabilities.ReconciliationProvable);
    }

    /// <summary>
    /// THE HELLO REFUSAL IS WORTH NOTHING WITHOUT THE HEARTBEAT ONE — the connector's own comment,
    /// applied to the case it does not cover.
    ///
    /// F9 added `_incompatible is null` to the EVENT branch. The HEARTBEAT branch still guards only
    /// on `_authenticated`, and a heartbeat carries a whole BridgeHello which that branch assigns to
    /// `_hello`. So a peer whose hello was refused as protocol-2 — this product's own older DLL, or
    /// anything holding the pipe secret — can send a heartbeat whose PAYLOAD claims protocol 3 and
    /// set `_hello` after the refusal. Capabilities derive from `_hello`, and
    /// ReconciliationProvable is what TradingGateway consults before permitting LIVE_AUTONOMOUS.
    ///
    /// This drives it over a real pipe with a real authenticated peer.
    /// </summary>
    [Fact]
    public async Task A_refused_bridge_cannot_set_capabilities_through_a_heartbeat()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        await using var w = new StreamWriter(client, new System.Text.UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, new System.Text.UTF8Encoding(false), false, 8192, leaveOpen: true);

        // Authenticate for real, exactly as StubBridge does — this peer passes every gate but one.
        var cred = BridgePipeAuth.ReadForClient()!;
        var nonce = BridgePipeAuth.NewNonce();
        await w.WriteLineAsync(Json.Write(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Challenge,
            data = new { nonce, proof = BridgePipeAuth.Proof(cred.Secret, BridgePipeAuth.BridgeRole, nonce) }
        }));
        string? line;
        while ((line = await r.ReadLineAsync()) is not null)
            if (Json.Read<BridgeFrame>(line)?.Op == BridgePipeAuth.Response) break;

        // A version-2 hello: refused, _hello stays null.
        await w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(
                new BridgeHello { BridgeProtocolVersion = 2, BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3" }, Json.Options)
        }));
        await Wait(() => connector.Incompatible is not null);
        Assert.Null(connector.Bridge);

        // Now the same refused peer heartbeats, claiming protocol 3 and both capabilities.
        await w.WriteLineAsync(Json.Write(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgeOps.Heartbeat,
            data = new BridgeHello
            {
                BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM",
                SupportsClientOrderId = true, SupportsOrderHistory = true
            }
        }));
        await Task.Delay(400);

        // THE INVARIANT: a peer this connector has refused gains nothing by any later frame.
        var c = connector.Capabilities;
        var readout = $"Bridge={(connector.Bridge is null ? "null" : "SET proto=" + connector.Bridge.BridgeProtocolVersion)} "
                    + $"Incompatible={(connector.Incompatible is null ? "null" : "reported=" + connector.Incompatible.ReportedProtocolVersion)} "
                    + $"SupportsClientOrderId={c.SupportsClientOrderId} SupportsOrderHistory={c.SupportsOrderHistory} "
                    + $"ReconciliationProvable={c.ReconciliationProvable} IsConnected={await connector.IsConnectedAsync()} "
                    + $"StatusDetail=\"{connector.StatusDetail}\"";
        Assert.True(connector.Bridge is null && !c.SupportsClientOrderId && !c.ReconciliationProvable,
            "a refused v2 peer set capabilities through a heartbeat: " + readout);
    }
}

