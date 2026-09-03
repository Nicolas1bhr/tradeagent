using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;

// Headless gateway. The desktop app hosts the same objects in-process; this exists so the trading
// core can be run, driven and killed without a GUI — which is exactly what the fault tests need.
//
// Operator authority lives on stdin here, never on the agent-facing pipe.

var connectorArg = Arg("--connector") ?? "fake";

using var instance = SingleInstanceLock.TryAcquire();
if (instance is null)
{
    Console.Error.WriteLine("GATEWAY_ALREADY_RUNNING another TradeAgent gateway already owns this installation");
    return 3;
}

Paths.EnsureAllVerbose();
using var db = new Database();
var health = new HealthRegistry();

ITradingConnector connector = connectorArg switch
{
    "atas" => new AtasConnector(),
    _ => new FakeConnector(new FakeBroker { AccountId = Arg("--account") ?? "SIM-001" })
};

await using var gateway = new TradingGateway(db, connector, health);
var token = IpcToken.Ensure();
await using var server = new GatewayPipeServer(gateway, token);

health.Set(Components.App, HealthState.READY);
health.Set(Components.Workspace, Directory.Exists(Paths.Workspace) ? HealthState.READY : HealthState.FAILED);
await connector.ConnectAsync();
await gateway.RefreshHealthAsync();
server.Start();

using var stopping = new CancellationTokenSource();
var loop = Task.Run(() => Background(gateway, stopping.Token));

Console.WriteLine($"READY pipe={server.PipeName} connector={connector.Id} home={Paths.Home}");
Console.Out.Flush();

// Operator console. Deliberately terse: this is a developer/test surface, not the product's UI.
string? line;
while ((line = Console.ReadLine()) is not null)
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) continue;
    try
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "quit" or "exit": goto done;
            case "status": Console.WriteLine(Json.Write(await gateway.StatusAsync())); break;
            case "mode": gateway.SetMode(Enum.Parse<TradingMode>(parts[1], true)); Console.WriteLine("OK"); break;
            case "stop": gateway.StopAiTrading(parts.Length > 1 ? string.Join(' ', parts[1..]) : "operator"); Console.WriteLine("OK"); break;
            case "enable": gateway.EnableAiTrading(); Console.WriteLine("OK"); break;
            case "live": gateway.ActivateLive(parts.ElementAtOrDefault(1) is "on"); Console.WriteLine("OK"); break;
            case "reconcile": Console.WriteLine(Json.Write(await gateway.ReconcileAsync())); break;
            case "health": await gateway.RefreshHealthAsync(); Console.WriteLine(Json.Write(health.Snapshot())); break;
            case "approve": Console.WriteLine(Json.Write(await gateway.ApproveAsync(parts[1]))); break;
            case "cancel-all": Console.WriteLine(Json.Write(await gateway.OperatorCancelAllAsync())); break;
            case "close-all": Console.WriteLine(Json.Write(await gateway.OperatorCloseAllAsync())); break;
            case "risk":
                gateway.Update(s =>
                {
                    if (parts[1] == "max-qty") s.Risk.MaxOrderQuantity = decimal.Parse(parts[2]);
                    if (parts[1] == "max-notional") s.Risk.MaxNotionalPerOrder = decimal.Parse(parts[2]);
                    if (parts[1] == "max-positions") s.Risk.MaxOpenPositions = int.Parse(parts[2]);
                    if (parts[1] == "max-per-minute") s.Risk.MaxOrdersPerMinute = int.Parse(parts[2]);
                });
                Console.WriteLine("OK");
                break;
            case "fault": Console.WriteLine(Fault(connector, parts) ? "OK" : "ERR not a fake connector"); break;
            default: Console.WriteLine("ERR unknown command"); break;
        }
    }
    catch (Exception ex) { Console.WriteLine($"ERR {ex.GetType().Name}: {ex.Message}"); }
    Console.Out.Flush();
}

done:
await stopping.CancelAsync();
try { await loop; } catch (OperationCanceledException) { }
return 0;

static bool Fault(ITradingConnector c, string[] p)
{
    if (c is not FakeConnector f) return false;
    switch (p[1].ToLowerInvariant())
    {
        case "disconnect": f.Faults.Disconnected = p.ElementAtOrDefault(2) is not "off"; return true;
        case "drop-after": f.Faults.DropAfterBrokerAccept = int.Parse(p[2]); return true;
        case "drop-before": f.Faults.DropBeforeBrokerAccept = int.Parse(p[2]); return true;
        case "reject": f.Faults.RejectNext = int.Parse(p[2]); return true;
        case "hide-history": f.Faults.HideOrderHistory = p.ElementAtOrDefault(2) is not "off"; return true;
        case "quote-age": f.Faults.QuoteAge = TimeSpan.FromSeconds(int.Parse(p[2])); return true;
        case "fill":
            f.Faults.Fill = p[2].ToLowerInvariant() switch
            {
                "working" => FillBehaviour.LeaveWorking,
                "partial" => FillBehaviour.PartialFill,
                _ => FillBehaviour.FillImmediately
            };
            return true;
        default: return false;
    }
}

/// <summary>
/// One slow timer, not a busy loop. On a low-spec laptop this must stay invisible in Task Manager:
/// a health poll every few seconds, and reconciliation attempted only while something is unconfirmed.
/// </summary>
static async Task Background(TradingGateway gateway, CancellationToken ct)
{
    var backoff = TimeSpan.FromSeconds(2);
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await gateway.RefreshHealthAsync(ct);
            if (gateway.HasUnconfirmedWork())   // the gate's own question, not the raw flag
            {
                var r = await gateway.ReconcileAsync(ct);
                backoff = r.Clean ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
            else backoff = TimeSpan.FromSeconds(2);

            gateway.Log.Rotate();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { gateway.Log.Engineering("Background", "loop_error", "warn", ex: ex); }

        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return; }
    }
}

static string? Arg(string name)
{
    var a = Environment.GetCommandLineArgs();
    for (var i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
    return null;
}
