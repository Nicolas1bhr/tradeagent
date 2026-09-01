# TradeAgent

> **Picking this up?** Read [docs/RESUME-HERE.md](docs/RESUME-HERE.md) first — what to do next,
> what is still open, and the traps already paid for. [BUILD-STATUS.md](BUILD-STATUS.md) is the
> honest record of what is proven and what is not.

An AI agent, safely wired to an ATAS trading account, for someone who does not use a terminal.

ATAS stays the trading screen. Everything else is TradeAgent's: it installs its own dependencies,
hosts the conversation with the AI in its own window, runs the trading gateway, enforces the safety
rules, and owns the emergency stop. The user never sees a console, and never installs a prerequisite.

> **This software can place real orders in a real brokerage account.** It is provided as is, with no
> warranty of any kind, and nothing here is financial advice. Trading futures involves a genuine risk
> of losing more than you deposit. Read [BUILD-STATUS.md](BUILD-STATUS.md) before pointing it at
> money. It does trade through ATAS — orders have been placed, read back and cancelled on hardware,
> and `LIVE_CONFIRM` has been walked end to end — but every account it has ever touched was
> simulated, **no broker has ever been attached**, and fully automatic live trading is refused by
> design on this platform because ATAS cannot serve order history.

**Latest release: [v0.1.0](https://github.com/Nicolas1bhr/tradeagent/releases/tag/v0.1.0)** —
`TradeAgent-Setup-x64.exe`, per-user, no administrator. Cut by hand from a machine with ATAS, because
a release built without one cannot trade through it; see `docs/RESUME-HERE.md` task 7.

**Current state: [BUILD-STATUS.md](BUILD-STATUS.md).** Read it before trusting anything here — it
lists exactly what is verified, what is not, and what cannot trade yet.

```
┌──────────────────────────────────────────────┐
│  TradeAgent Desktop                          │  chat · dashboard · safety · activity · checks
│    chat panel · onboarding · STOP            │  the AI's console, replaced by a window
└───────────────────────┬──────────────────────┘
                        │
┌───────────────────────┴──────────────────────┐
│  Provisioning                                │  Node + the AI CLI, per-user, silent, no admin
│    %LOCALAPPDATA%\TradeAgent\tools           │  nothing on PATH, nothing machine-wide
└───────────────────────┬──────────────────────┘
                        │  headless runs, stdout parsed into chat turns
┌───────────────────────┴──────────────────────┐
│  Managed agent workspace                     │  internet · tools · files
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

The trading core, the gateway, the CLI as a child process, and the ATAS bridge protocol against a
loopback adapter. All of it runs on macOS and Linux as well as Windows. The count that was actually
observed, and on which machines, is recorded in [BUILD-STATUS.md](BUILD-STATUS.md) rather than here,
so this file cannot quietly go stale.

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
powershell -ExecutionPolicy Bypass -File packaging/build.ps1 -AtasInstallDir "C:\Program Files\ATAS Platform"
```

`pwsh` works too, but Windows PowerShell 5.1 is what a stock Windows 11 machine has, and the script
runs on both. Inno Setup 6.3 or newer is needed for the installer; the script finds it in either
Program Files, in the per-user location winget uses, or on `PATH`.

| Switch | Effect |
|---|---|
| `-AtasInstallDir <path>` | Compiles the bridge's ATAS adapter against a real ATAS install |
| `-RequireInstaller` | A missing Inno Setup becomes an error instead of a warning |
| `-SkipTests` | Package without running the suite (CI runs it in its own job) |
| `-SkipPublish` | Repackage the existing `artifacts/stage` without recompiling — seconds, not minutes, while iterating on `TradeAgent.iss` |

Before it packages anything, the script **verifies the stage**: every shipping artifact must exist and
be plausibly sized (`TradeAgent.exe`, `trade.exe`, `tradeagent-gateway.exe`, the Provisioning,
AgentRuntime and Gateway assemblies, and a non-empty `bridge/`), and the whole stage must be large
enough to be a self-contained publish. A run that produces nothing fails, loudly, naming the missing
file. It cannot reach the checksum step and print "Done".

It finishes by printing what the artifact actually contains — file counts, sizes, the installer's
size, and whether `AtasStrategyAdapter` is really compiled into the bridge assembly. That last one is
read out of the binary, not out of the flag that was passed in.

Without `-AtasInstallDir` the build still succeeds but produces a bridge with **no ATAS adapter**, and
says so. That is deliberate: the build never pretends to have ATAS support it cannot have.

The installer is per-user: `PrivilegesRequired=lowest`, no elevation, no PATH edits, nothing outside
`%LOCALAPPDATA%`. It is not code-signed, so Windows SmartScreen warns on first run.

## Repository map

| Path | What it is |
|---|---|
| `src/TradeAgent.Core` | Models, error catalogue, health, onboarding, SQLite store, IPC protocol |
| `src/TradeAgent.ConnectorSdk` | `ITradingConnector`, DTOs, capability model, the rejection/transport distinction |
| `src/TradeAgent.Connectors.Fake` | Deterministic simulator **and the fault-injection harness** |
| `src/TradeAgent.Connectors.Atas` | ATAS connector, bridge protocol, install detection |
| `src/TradeAgent.Gateway` | The execution authority: authorisation, risk, idempotency, reconciliation, pipe server |
| `src/TradeAgent.TradeCli` | `trade` — the CLI every agent uses |
| `src/TradeAgent.Provisioning` | Installs what the app needs, itself: portable Node into `%LOCALAPPDATA%`, the AI CLI from its vendor's release, ATAS handed to Windows' own elevation prompt. No admin, no PATH, no terminal |
| `src/TradeAgent.AgentRuntime` | `IAgentRuntime`, manifest-driven CLI adapters, workspace generation, supervisor, and `IAgentConversation` — headless runs whose output becomes chat turns instead of a console |
| `src/TradeAgent.AtasBridge` | Runs inside ATAS. `BridgeServer` is tested; `AtasStrategyAdapter` is the one unfinished file |
| `src/TradeAgent.Security` | IPC token, DPAPI secret storage, single-instance lock |
| `src/TradeAgent.Diagnostics` | Doctor and the sanitised support package |
| `src/TradeAgent.App` | The Avalonia desktop app: `Theme`/`Ui` design system, onboarding wizard, chat panel, dashboard, safety, activity, checks |
| `src/TradeAgent.GatewayHost` | Headless gateway, for tests and diagnostics |
| `tests/` | Unit, integration and fault suites; counts and where they last ran are in [BUILD-STATUS.md](BUILD-STATUS.md) |
| `docs/` | [DECISIONS](docs/DECISIONS.md) · [RESEARCH-REQUIRED](docs/RESEARCH-REQUIRED.md) · [CONTRACTS](docs/CONTRACTS.md) · [USER-GUIDE](docs/USER-GUIDE.md) |

## Where things live at runtime

```
%LOCALAPPDATA%\TradeAgent\
  state\      tradeagent.db, ipc.token, gateway.lock
  workspace\  AGENTS.md and the AI's own work
  tools\      Node.js and the AI CLI, installed here by TradeAgent.Provisioning
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
