# The monitoring phase — the first weeks on a live machine

**Audience: Nicolas.** This file names commands and paths. `docs/USER-GUIDE.md` is the owner's
document and does neither.

The premise of these weeks is that the machine runs and nobody is watching it continuously. So the
question this file answers is not "is it working" but **"what do I read, how often, and what reading
makes me stop it".**

Every reading below is either derived from the code, with the file and line, or marked **NOT
verified** — meaning nobody has seen that reading on a running deployment and it is inference from
the source.

---

## 1. What you can see, and from where

### `trade status`, over SSH

```bash
tools/win-run.sh 'trade status --json'
```

`trade` lives in `%LOCALAPPDATA%\TradeAgent\bin` and is on the agent's PATH
(`src/TradeAgent.AgentRuntime/ToolDeployer.cs:22`, `WorkspaceBuilder.cs:36`). It talks to the running
app over a named pipe and authenticates with a token the app writes; if the app is not running you
get *"no access token found; is TradeAgent installed and running?"*
(`src/TradeAgent.TradeCli/PipeClient.cs:17-18`). **That message means "the app is down", not "the CLI
is broken".**

The fields, all of them, are the members of `GatewayStatus` (`src/TradeAgent.Gateway/GatewayTypes.cs:112-116`)
serialised snake-case (`src/TradeAgent.Core/Protocol.cs:90`):

```
protocol_version  app_version  mode  ai_trading_stopped  live_activated
execution_available  execution_blocked_reason  connector_id  connector_name
connector_is_paper  account_id  health[]  open_requests  unreconciled_requests  risk
```

`health[]` is the same twelve rows the Dashboard draws. *Verified against the code; and U3 and U4 both
quote `trade status` output from the box, so the command itself is walked.*

**`trade` has no verb for the activity history.** The verbs are status, schema, accounts, account,
instruments, quote, positions, position, orders, order, executions, the order commands, and material
(`src/TradeAgent.TradeCli/Program.cs:208-240`). The owner's plain-language history is readable in the
app or in the support package, and nowhere else.

### The support package

**Checks → Create support package**, in the app. It writes
`%LOCALAPPDATA%\TradeAgent\TradeAgent-support-<yyyyMMdd-HHmmss>.zip` and **Show the file** opens the
folder (`src/TradeAgent.Diagnostics/Doctor.cs:244-246`, `DashboardView.cs:864-868,941-946`).

Inside (`Doctor.cs:249-319`):

| Entry | What it is |
|---|---|
| `activity.txt` | the last 2,000 owner-facing activity lines, oldest first |
| `environment.json` | app version, IPC protocol, **bridge protocol**, db schema, OS, arch, .NET, home path |
| `engineering.log` | the last 5,000 engineering rows: time, component, event, severity, request id, metadata, exception |
| everything in `logs\` | except any filename containing `token` or `secret` |
| `bridge-coid-witness.errors.log*` | the bridge's own failure log and its rotated generation |
| `bridge-sidecar-UNREADABLE.txt` | present **only** if the sidecar could not be read — and its absence is then not evidence that there was nothing to carry |

That last row is the one to look for first when you are chasing a durability problem. It exists
because an archive with no sidecar in it used to be indistinguishable from a machine that never had a
failure.

**No secret files are collected.** *Verified against the code. NOT verified: nobody has opened one of
these zips from a deployed machine — the collector has unit tests, the artefact has no record.*

### The on-machine logs, and which of them rotate

Everything lives under `%LOCALAPPDATA%\TradeAgent` (`src/TradeAgent.Core/Paths.cs:9-30`).

| Where | What | Rotates? |
|---|---|---|
| `state\tradeagent.db` → `activity` table | the owner's plain-language history | **Rotates.** Trimmed to the newest 5,000 rows (`Db/Stores.cs:373-381`) |
| `state\tradeagent.db` → `engineering_log` | the engineering trail | **Rotates.** Newest 20,000 rows |
| `state\tradeagent.db` → `health_event` | every health transition | **Rotates.** Newest 20,000 rows |
| `state\tradeagent.db` → execution requests, orders, composite requests, material | the trading record | **Append-only.** Nothing in the source deletes from these tables |
| `bridge\coid-witness.json` | the bridge's write-ahead record of order identifiers | bounded by a 512-entry cap (`CoidWitness.cs:116`), not by time |
| `bridge\coid-witness.errors.log` | rewrites that did not land, and safety events | **Rotates**, one generation back to `…errors.log.1` past 64 KiB (`CoidWitness.cs:235-239, 2864`) |
| `logs\` | the directory the support package sweeps | **Nothing in `src/` writes to it.** It is created and swept and, as far as the source goes, is empty |

The trimming runs on the app's five-second background pass (`src/TradeAgent.App/AppHost.cs:235`) and
in the headless host (`src/TradeAgent.GatewayHost/Program.cs:135`).

**Two directories named `bridge`, and they are not the same one.** The installer stages the DLL at
`<install dir>\bridge`; the witness and its error log live at `%LOCALAPPDATA%\TradeAgent\bridge`
(`CoidWitness.cs:758`). Do not go looking for the witness beside the assembly.

*All of the above is read from the code. NOT verified: no record shows any of these files inspected
on a machine that had been running for days.*

### The Dashboard's health rows

Twelve, always in this order (`src/TradeAgent.Core/Health.cs:25-29`):

`TradeAgent` · `Agent runtime` · `Agent process` · `Workspace` · `Gateway` · `trade CLI` ·
`ATAS process` · `ATAS bridge` · `Trading connection` · `Account` · `Market data` ·
`Execution capability`

**Four of them are load-bearing.** If `Gateway`, `Trading connection`, `Account` or
`Execution capability` is anything other than READY, the gateway revokes execution outright rather
than guessing, and `execution_blocked_reason` says which one and why (`Health.cs:69-80`).

On screen the states read as words, not enum names: `not checked yet`, `starting up`,
`working, but not fully`, `not working`, `paused`, `ready` (`DashboardView.cs:931-938`).

**Known cosmetic defects** (U4 defects 2, 3, 4, 11): the top bar clips a long detail mid-word; the
same ~180-character sentence prints twice, on `ATAS bridge` and on `Trading connection`, pushing the
last three rows off the bottom of the window; and `Agent process` can read "paused — stopped". None
of these change what the machine is doing.

---

## 2. The daily check — five readings

One SSH session. Each item has the reading that means fine and the reading that means stop.

| # | Read | Fine | Stop |
|---|---|---|---|
| 1 | `trade status --json` answers at all | any JSON | the token error, or no answer — the app is down, and while it is down nothing is reconciling |
| 2 | `unreconciled_requests` and `open_requests` | `0` and `0` | anything non-zero that is still non-zero on the next look — see §4 |
| 3 | the four gating rows in `health[]` | all `READY` | any of them not READY. `execution_blocked_reason` names it |
| 4 | the `ATAS bridge` row's detail | `connected · bridge <v>, protocol 3` | anything containing `press Reinstall the bridge` or `could not prove it holds this installation's bridge secret` |
| 5 | `ai_trading_stopped` and `mode` | what you last set them to | `ai_trading_stopped true` when you did not set it — it is saved and survives restarts, so somebody pressed it; the reason is in the activity history |

Item 4's two sentences are deliberately different diagnoses with different repairs, and the code goes
out of its way to keep them apart (`Versioning.cs:13-34`, `AtasConnector.cs:280-290`). A protocol
refusal means **redeploy the DLL**. A credential refusal means the bridge is holding a secret from a
different installation of TradeAgent — the U4 walk produced exactly that, by running a scratch home
against the real bridge.

*Item 4's second sentence was seen rendered on the Dashboard and the Checks rows (U4). Item 4's first
sentence at protocol 3 has NOT been seen on hardware — the box's bridge is still a protocol-2 build.*

---

## 3. The weekly check

1. **Open the app and press Check everything** on the Checks page. Read the `what to do:` lines. The
   "Fully automatic trading" warning on ATAS is correct and expected, not a fault
   (`Doctor.cs:193-213`).
2. **Create a support package and actually open it.** Look for `bridge-sidecar-UNREADABLE.txt` and for
   any line in `bridge-coid-witness.errors.log` — every line in that file is a rewrite of the
   write-ahead record that did not land, and the first one is the one that matters, which is why the
   rotated generation is collected too.
3. **Look at the size of `state\tradeagent.db`.** The three log tables are bounded; the trading record
   is not. A database growing at a rate that surprises you is worth understanding before it is
   large.
4. **Confirm the app has been restarted at least once since the last update**, and that the version in
   `app_version` is the one you deployed.
5. **Do a two-press drill in Practice mode**: press STOP AI TRADING, confirm the button reads RESUME
   AI TRADING, press it back. One press each way (`DashboardView.cs:638-642`). Costs nothing and
   proves the one control that must never be stiff.

---

## 4. The stop rule

**Stop the machine if any of these is true.** They are deliberately blunt; a monitoring phase is not
the moment for judgment calls at three in the morning.

1. **An order sitting UNKNOWN or RECONCILING for more than five minutes with no card resolved.**
   Five minutes is a judgment, not a constant in the code — the reconciler's own numbers are a
   15-second absence grace (`GatewayTypes.cs:39`) and a 30-second dispatch-stranded threshold
   (`Db/Stores.cs:95`), and it runs on the five-second pass whenever there is unconfirmed work
   (`AppHost.cs:234`). Anything not settled in five minutes is not going to settle by itself.
   **On ATAS it definitively will not**: ATAS cannot prove order history, so an ambiguous order stays
   `RECONCILING` until a person answers the card. U4 watched exactly this — the bridge was restarted
   and the record still did not self-resolve over eight samples in 70 seconds, which is the correct
   behaviour.
2. **The `ATAS bridge` row says `press Reinstall the bridge`.** The refusal is permanent until a
   compatible bridge connects. Trading is not happening. The owner can do this themselves now: the
   card is on Checks whenever the row calls for it and always on Settings; close ATAS, press the
   button twice, reopen ATAS and start the strategy on a chart.
3. **An update was refused.** Read the sentence. `cannot be verified` means the release itself is not
   installable and needs republishing; `will not replace itself while an order's outcome is still
   unconfirmed` means you have an open card — which is rule 1, arriving by another door
   (`UpdateService.cs:543-571, 577-599`).
4. **The kill switch is on and you did not press it.** Nothing sets it by itself — the only callers
   of `StopAiTrading` are the two buttons and the dev host's `stop` command
   (`TradingGateway.cs:232-239`), and the setting is saved, so it survives a restart. Finding it on
   means a person pressed it or a previous session left it on, and the activity history says which
   and why: *"AI trading stopped (you pressed STOP AI TRADING)"*. Find out before you clear it.
5. **`trade status` does not answer.** A dead app is a machine where nothing is reconciling and
   nothing is watching the bridge — and any order it had in flight will be swept to UNKNOWN the next
   time it starts (`TradingGateway.cs:147-186`).

### How to stop

In this order.

1. **Press STOP AI TRADING.** One press, instantly, from any page (`MainWindow.cs:289-291`). It
   removes the AI's permission and touches nothing else. If you cannot reach the window, there is no
   remote equivalent that is safe to recommend — the operator controls are deliberately in-process
   only and are not reachable from the agent-facing pipe.
2. **Then go to ATAS and close the positions by hand.** Not through TradeAgent. If you are stopping
   the machine, it is because you do not trust its account of itself, and ATAS is the record that
   does not depend on TradeAgent being right.
3. **Only then** clear the unconfirmed cards on the Dashboard, with what you actually saw in ATAS.
   The note is required and it is the only durable trace of a person overriding the machine
   (`DashboardView.cs:363-371`).

**Do not use "Close all positions" as the panic button while you are already unsure.** It writes a
record per target, sends the closes, and pauses trading until every one of those records is
confirmed by a person — which is the correct design and exactly the wrong thing to add to a situation
you are already trying to simplify. Its own answer arrives inside two seconds or not at all
(`AtasConnector.cs:118`), and "not at all" reads *"'close-all' is NOT confirmed — check your positions
and orders in ATAS"*, which is the sentence sending you to ATAS anyway.

---

## 5. Outbound alerts

**Not built.** Nothing in this product sends anything out: no email, no webhook, no push, no SMS.
Every reading above is pull — you connect and look.

**Why it is not built.** An outbound alerter is a component that has to be more reliable than the
thing it is watching, and it has to hold a credential for whatever it sends through. This product's
whole containment story is that the agent cannot reach the operator's authority and that no
credential the owner cares about lives inside it; adding a mail account or a webhook secret to
`%LOCALAPPDATA%\TradeAgent` widens that on the first day, for a machine that currently has one
monitored deployment and one person watching it. There has also been nothing to alert *about* yet:
the deployment is not real-money and the readings above have never been taken on a long-running
machine, so nobody knows which of them would fire and how often.

**What it would need, concretely.** A process that outlives the app — the app is exactly the thing
whose death is worth alerting on, so an in-process alerter cannot report the most important event.
That means a small scheduled task or service that runs `trade status --json`, applies §4's rules, and
sends on transition rather than on state, with a secret in Windows' own credential store rather than
in the home directory. It should send at most once per transition and it must degrade to silence
rather than to noise. The honest first version is smaller than that: a scheduled task that appends
`trade status --json` and a timestamp to a file, so that when something does go wrong there is a
history of the five readings instead of one live sample. Nothing of this exists.

---

## 6. Marked NOT verified

- No support package produced on a deployed machine has been opened. *(§1)*
- No log file, sidecar or database has been inspected after days of running. *(§1)*
- `protocol 3` has never been read from a bridge on hardware; the box still carries a protocol-2 DLL. *(§2 item 4)*
- The five-minute threshold in the stop rule is a judgment. Nothing in the code names it. *(§4)*
- The `PresentedNoProof` refusal sentence — a peer that speaks the protocol but holds no secret — has
  never been rendered (U4 § NOT VERIFIED).
- Nothing in this file has been exercised against a real broker or real money.
