# TradeAgent

An AI agent, safely wired to an ATAS trading account, for someone who does not use a terminal.

ATAS stays the trading screen. OpenCode or Codex stays the place you talk to the AI. TradeAgent owns
everything in between: setup, the trading gateway, the safety rules, and the emergency stop.

> **This software can place real orders in a real brokerage account.** It is provided as is, with no
> warranty of any kind, and nothing here is financial advice. Trading futures involves a genuine risk
> of losing more than you deposit. Read [BUILD-STATUS.md](BUILD-STATUS.md) before pointing it at money:
> the ATAS integration is not finished, so it cannot trade through ATAS yet.

**Current state: [BUILD-STATUS.md](BUILD-STATUS.md).** Read it before trusting anything here — it
lists exactly what is verified, what is not, and what cannot trade yet.

```
┌──────────────────────────────────────────────┐
│  TradeAgent Desktop                          │  onboarding · health · modes · STOP
└───────────────────────┬──────────────────────┘
                        │
┌───────────────────────┴──────────────────────┐
│  Managed agent workspace                     │  shell · internet · tools · files
│    AGENTS.md          trade.exe              │  no broker credentials, by design
└───────────────────────┬──────────────────────┘
                        │  authenticated local named pipe
┌───────────────────────┴──────────────────────┐
│  Trading Gateway                             │  authority · idempotency · reconciliation · risk
└───────────────────────┬──────────────────────┘
                        │  ITradingConnector
              ┌─────────┴─────────┐
       ATAS Bridge          Simulator (built in)
              │
            ATAS ──► broker
```

## The rules the whole design serves

1. **Broker credentials never enter the agent's environment.** ATAS owns broker authentication. The
   agent expresses intent; the gateway executes it.
2. **A repeated request id can never place a second order.** Every mutating request is written to disk
   before it is dispatched, keyed by a caller-supplied id.
3. **`UNKNOWN` never means failure.** When an acknowledgement is lost, the order may be live. Trading
   pauses, the gateway reconciles against the broker, and nothing is ever resubmitted automatically.
4. **Real money requires a deliberate human act.** The existence of a live account is not consent.
5. **Stopping the AI and liquidating a portfolio are different buttons.** Always.

## Build and test

Requires the .NET 10 SDK. Everything except the ATAS adapter and the installer is cross-platform.

```bash
dotnet test TradeAgent.sln
```

86 tests: the trading core, the gateway, the CLI as a child process, and the ATAS bridge protocol
against a loopback adapter. All of it runs on macOS and Linux as well as Windows.

Run the trading core headless, with fault injection on stdin:

```bash
dotnet run --project src/TradeAgent.GatewayHost
```

Then, in another shell:

```bash
dotnet run --project src/TradeAgent.TradeCli -- status --json
```

Type `fault drop-after 1` into the gateway's stdin, place an order, and watch it become `UNKNOWN`,
pause trading, and reconcile. `help`-less by design: `mode`, `stop`, `enable`, `live on|off`,
`reconcile`, `health`, `approve <id>`, `cancel-all`, `close-all`, `risk`, `fault`, `quit`.

Package for Windows (on Windows):

```powershell
pwsh packaging/build.ps1 -AtasInstallDir "C:\Program Files\ATAS Platform"
```

Without `-AtasInstallDir` the build still succeeds but produces a bridge with **no ATAS adapter**, and
says so. That is deliberate: the build never pretends to have ATAS support it cannot have.

## Repository map

| Path | What it is |
|---|---|
| `src/TradeAgent.Core` | Models, error catalogue, health, onboarding, SQLite store, IPC protocol |
| `src/TradeAgent.ConnectorSdk` | `ITradingConnector`, DTOs, capability model, the rejection/transport distinction |
| `src/TradeAgent.Connectors.Fake` | Deterministic simulator **and the fault-injection harness** |
| `src/TradeAgent.Connectors.Atas` | ATAS connector, bridge protocol, install detection |
| `src/TradeAgent.Gateway` | The execution authority: authorisation, risk, idempotency, reconciliation, pipe server |
| `src/TradeAgent.TradeCli` | `trade` — the CLI every agent uses |
| `src/TradeAgent.AgentRuntime` | `IAgentRuntime`, manifest-driven CLI adapters, workspace generation, supervisor |
| `src/TradeAgent.AtasBridge` | Runs inside ATAS. `BridgeServer` is tested; `AtasStrategyAdapter` is the one unfinished file |
| `src/TradeAgent.Security` | IPC token, DPAPI secret storage, single-instance lock |
| `src/TradeAgent.Diagnostics` | Doctor and the sanitised support package |
| `src/TradeAgent.App` | The Avalonia desktop app: onboarding wizard and control panel |
| `src/TradeAgent.GatewayHost` | Headless gateway, for tests and diagnostics |
| `tests/` | 29 unit · 21 integration · 36 fault |
| `docs/` | [DECISIONS](docs/DECISIONS.md) · [RESEARCH-REQUIRED](docs/RESEARCH-REQUIRED.md) · [CONTRACTS](docs/CONTRACTS.md) · [USER-GUIDE](docs/USER-GUIDE.md) |

## Where things live at runtime

```
%LOCALAPPDATA%\TradeAgent\
  state\      tradeagent.db, ipc.token, gateway.lock
  workspace\  AGENTS.md and the AI's own work
  tools\      managed AI CLI installs
  bin\        trade.exe (this is what puts it on the agent's PATH)
  bridge\     the ATAS bridge assembly
  logs\
  runtimes.json, atas.json    overrides for anything the vendors change
```

`TRADEAGENT_HOME` relocates all of it — which is how the tests get an isolated install.

## License

[PolyForm Noncommercial License 1.0.0](LICENSE), plus
[additional permissions](LICENSE-EXCEPTIONS.md) from the licensor.

In short: use it, change it, share it, and run it on **your own** trading account, profit included.
Selling it, building a paid product or service on it, or trading **other people's** money with it
for a fee needs written permission — open an issue and ask.

This is a source-available license, not an OSI-approved open-source one. That is deliberate.

## Credits

Built by [Nicolas Beeckman](https://github.com/Nicolas1bhr).

The implementation was written with Claude Opus 5 (Anthropic), via Claude Code, working from a build
brief that fixed the architecture and the safety model up front. What was decided and why is in
[docs/DECISIONS.md](docs/DECISIONS.md); what is proven versus merely written is in
[BUILD-STATUS.md](BUILD-STATUS.md), including the defects that surfaced during the build.
