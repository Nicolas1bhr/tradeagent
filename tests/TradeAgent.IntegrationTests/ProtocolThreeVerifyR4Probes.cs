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
