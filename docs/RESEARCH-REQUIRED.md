# Research required before release

Every item here is something this build could not verify from a macOS host with no ATAS install and
no AI-provider account. They are recorded as data-driven or single-file so that correcting one is a
small change, never a redesign.

**Rule: check against current official sources, not against this document and not from memory.**
Prefer official docs, then official release notes/source, then maintained examples.

---

## A1 — ATAS extension API  ·  LARGELY ANSWERED ON HARDWARE, read this first

**Overtaken by evidence, 2026-08-27 → 2026-09-01.** The adapter compiles against real ATAS, runs
inside it, and has placed, read back and cancelled orders on two simulated backends; `LIVE_CONFIRM`
has been walked end to end. Do not work this table from scratch — read `BUILD-STATUS.md`'s dated
sections first and treat anything not struck through below as still open. The two questions that
decide how much autonomy is safe are both answered, one true and one false, and neither is to be
re-litigated by hard-coding a capability.

**File:** `src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs` (the only file in the product that cannot
compile without ATAS). **Reference implementation:** `LoopbackAtasAdapter.cs` in the same folder shows
the exact shape and honesty each method needs.

Confirm, from ATAS's own documentation and the assemblies in your install:

| # | Question |
|---|---|
| 1 | Correct base class and lifecycle hooks for a user-loadable chart strategy, and the assembly names to reference (the `.csproj` guesses `ATAS.Indicators`, `ATAS.Strategies`, `ATAS.DataFeedsCore`). |
| 2 | ~~Target framework ATAS loads.~~ **ANSWERED 2026-08-27: `net10.0`**, read from the platform's own runtimeconfig on ATAS 8.0.14.397 by `probe atas`. The bridge builds `net10.0-windows`, which matches. A mismatch is not reported as an error — ATAS simply never lists the strategy. |
| 3 | The folder ATAS loads user strategies from, and whether a restart is required after copying files. |
| 4 | Portfolio/account enumeration, and how to tell a simulation connection from a live one. |
| 5 | Security/instrument enumeration, with tick size, tick value and contract size. |
| 6 | Best bid/ask access, and the timestamp of the last update (staleness detection depends on it). |
| 7 | Position enumeration and the position-changed callback. |
| 8 | ~~**Order placement carrying a client-supplied identifier, readable back from the order list.**~~ **ANSWERED 2026-08-30: YES.** An order was placed, ATAS was shut down, and the identifier was found again on an order in the restarted platform's own collection, beside the broker id the dead run had recorded in advance. `SupportsClientOrderId` is true **on evidence**. In-session the reading is still `proven-sameref` and still reports false, on two connectors, so that is how ATAS's collection works rather than one backend's quirk. |
| 9 | ~~**Order history including finished orders, covering an arbitrary `since` timestamp.**~~ **ANSWERED 2026-08-28: NO, for a known reason.** `IIndicatorDataProvider.GetService<T>()` throws `NotSupportedException` for *every* type, including one reachable as a property on the same interface, so every cache route is dead. `SupportsOrderHistory` is false and the gateway refuses `LIVE_AUTONOMOUS` on that basis. Shippable. |
| 10 | Modify, cancel, cancel-all, and programmatic position flattening. |
| 11 | Execution/trade callbacks, and whether they carry the client identifier. |
| 12 | Which failures are definite broker rejections versus ambiguous ones. |

Items 8 and 9 decide how much autonomy is safe. Report them truthfully in `Describe()`:

- no client-id round trip → `SupportsClientOrderId = false`
- incomplete history → `SupportsOrderHistory = false`

The gateway then refuses `LIVE_AUTONOMOUS` on that connector, by design. **Do not report a capability
you have not proven.** A partial history is worse than none: it makes "this order does not exist" look
provable when it is not.

Item 12 maps onto `AtasRejectedException` (definite) versus any other exception (indefinite). Getting
this backwards is the one mistake that can produce a duplicate live position.

## A2 — ATAS install layout

**File:** `src/TradeAgent.Connectors.Atas/AtasInstallation.cs` → `AtasLayout`.
Overridable at runtime via `%LOCALAPPDATA%\TradeAgent\atas.json`.

**ANSWERED 2026-08-27 against a real, signed-in ATAS 8.0.14.397** — `probe atas` reported
`LAYOUT VERIFIED : YES`, install dir `C:\Program Files (x86)\ATAS Platform`, strategies
`%APPDATA%\ATAS\Strategies`, indicators `%APPDATA%\ATAS\Indicators` (a *different* folder, never a
fallback), processes `OFT.Platform` / `OFT.PlatformX`. What remains unconfirmed is only whether these
hold across ATAS's other editions.

## A3 — ATAS version compatibility

How does an ATAS update affect a compiled bridge assembly? Which version range does one build cover?
`Versions.BridgeProtocolVersion` already gates a mismatched bridge (tested), but the *assembly*
compatibility rule is unknown. Feed the answer into the "Trading paused — press Repair" path.

---

## B1 — OpenCode CLI

**File:** `src/TradeAgent.AgentRuntime/RuntimeManifest.cs` → `RuntimeCatalog.BuiltIn()`.
Overridable at runtime via `%LOCALAPPDATA%\TradeAgent\runtimes.json`.

The manifest now claims a self-contained Windows x64 download, a headless conversation and a
browser sign-in. Every one of those values was read from published metadata and documentation, not
from running the program, which is why `Verified` is still `false`. Confirm against
<https://opencode.ai/docs/> **and by running the real CLI on Windows**:

- The install route: the GitHub repo, the asset pattern, and the path of the executable inside the
  archive. Then the npm fallback, through TradeAgent's own private Node.
- The version, sign-in and sign-in-status commands, and what success looks like on stdout — that
  string is what makes the wizard advance by itself, and the sign-in URL pattern is what lets
  TradeAgent open the browser instead of showing a console.
- The one-shot and resume commands, the flag that turns stdout into a machine-readable stream, and
  the approval/sandbox flags. A headless run that still waits for a keypress hangs the chat panel
  forever, which is the failure mode to look for.

Then set `Verified = true`.

## B2 — Codex CLI

Same fields, against <https://developers.openai.com/codex/cli/>. Additionally:

- Confirm ChatGPT-account sign-in works without an API key, and whether a device-code flow exists for
  when a browser cannot open.
- Confirm the sandbox and git-repo flags the manifest passes are still the right ones. The agent's
  workspace is not a git repository, and a CLI that refuses to run outside one would fail every
  message.

## B3 — Agent workspace conventions

Confirm both runtimes read `AGENTS.md` from the working directory. If either expects a different
filename, `WorkspaceBuilder.Build` should write both.

---

## C1 — Windows secret storage

`SecretStore` uses DPAPI (`ProtectedData`, CurrentUser) for the IPC token, falling back to a
`0600` file elsewhere. Confirm this is the right choice versus the Windows Credential Manager for a
per-user, per-machine secret. **Broker credentials are deliberately not in scope: ATAS owns those.**

## C2 — Installer and signing

`packaging/TradeAgent.iss` has compiled and installed on real Windows 11. What has changed since and
is therefore unconfirmed:

- `PrivilegesRequiredOverridesAllowed` is now `commandline`, not `dialog`, so setup no longer asks a
  non-technical user an all-users/just-me question it has no way to answer. Confirm setup runs with
  no elevation prompt at all, and that `{autopf}` lands under `%LOCALAPPDATA%\Programs`.
- `CloseApplications=yes` with `RestartApplications=no`. Confirm that installing over a **running**
  TradeAgent names the running program and offers to close it, and that the app is launched exactly
  once afterwards, by the `[Run]` entry.
- `AppMutex=TradeAgent.SingleInstance` is inert: the app's single-instance guard is a file lock,
  which Setup cannot see. Either create a named mutex with that exact name at startup, or delete the
  directive so nobody mistakes it for protection that exists.
- Confirm the uninstaller still leaves `%LOCALAPPDATA%\TradeAgent` (trading records and the AI's
  work) intact.

Code signing remains open: unsigned builds show a SmartScreen warning, which
[docs/USER-GUIDE.md](USER-GUIDE.md) now tells the user to expect. Budget for a certificate before
this goes to anyone who did not build it.

## C2b — Icons and setup artwork

There is no `.ico` or bitmap anywhere in this repository, so `SetupIconFile`, `WizardImageFile` and
`WizardSmallImageFile` are deliberately absent from `TradeAgent.iss` (naming a file that does not
exist fails the ISCC compile), and `TradeAgent.exe` carries the default .NET icon. A trading
application whose taskbar button is a generic icon looks unfinished on a machine it did not build
itself. Add artwork, then add those three directives and `<ApplicationIcon>` to
`src/TradeAgent.App/TradeAgent.App.csproj`.

## C3 — Bridge dependency closure

The bridge currently references `TradeAgent.ConnectorSdk`, which transitively drags
`Microsoft.Data.Sqlite` into the ATAS process. It is never used there (no connection is ever opened),
but the DLLs land in the ATAS folder. Before release, either trim the closure — move the shared enums
into a small contracts assembly — or confirm the extra assemblies are harmless inside ATAS.

## C4 — .NET runtime on the target laptop

The app and CLI publish self-contained, so no runtime is required. Confirm the resulting install size
and cold-start time are acceptable on the actual laptop this will run on, and revisit
`PublishReadyToRun` / trimming only if they are not.

---

## C — Venues for fully automatic real money (researched 2026-09-06 from official pages; re-verify at build time)

**Decided: no human in the loop for real money, so the venue's API must prove rules 1 and 2** — an order carries a
client id that comes back on queries and fills, and order history really reaches back to the timestamp asked for.

| Venue | Verdict | Facts read on 2026-09-06 |
|---|---|---|
| **Binance** | direct connector, rules 1–2 provable, testnet for paper | `newClientOrderId`; `allOrders` and `myTrades` take `startTime`/`endTime`, at most 24 h per query, so the connector pages; a testnet exists. https://developers.binance.com/docs/binance-spot-api-docs |
| **Revolut X** (crypto, UK/EEA) | direct connector, same shape; spot only; NO sandbox | REST at `https://revx.revolut.com/api/1.0`, Ed25519-signed headers, API keys with trading permission; `POST /orders` takes `client_order_id` (UUID) and it returns on `GET /orders/active`, `GET /orders/historical`, `GET /orders/{venue_order_id}` and fills; `GET /orders/historical` takes `start_date`/`end_date` (≤ 1 week per query, cursor paging, `order_states` filled/cancelled/rejected/replaced); `GET /trades/private/{symbol}` with `start_date`; 1000 req/min. Paper must be our simulator over its live market data; live starts at dust size. https://developer.revolut.com/docs/x-api/revolut-x-crypto-exchange-rest-api and https://github.com/revolut-engineering/revolut-x-api |
| **Zenit Funding** (futures prop firm) | the venue is the firm's platform: **ATAS**, Quantower, Volumetrica or Zenit's own; data DXFeed, CME L1 included | Automation allowed only with code you own ("using an algorithm whose source code you do not own is prohibited"); prohibited: HFT, bracketing, cross-account and cross-client hedging, copy trading ("the account must be traded solely by you"), account lending, challenge-passing services, spread/product hedging, sim-only strategies. Flat before 22:10 GMT+1 (the firm liquidates 5–10 min before); no overnight; no positions in the minute before and after NFP, CPI, PPI, FOMC, central-bank speeches and rate decisions, oil inventories, Michigan, PMI. Classic: trailing INTRADAY drawdown incl. unrealized ($2,500 on $50k, $5,000 on $150k, $7,500 on $300k), targets $3k/$9k/$20k, 2 min days, funded consistency 30 %, max 5/15/30 lots. Expert: EOD drawdown $2,000, daily loss $1,000, target $3,000, consistency ≤ 50 % challenge / ≤ 40 % funded, 1–5 minis scaling. Fees: Classic $165/$399/$659 per month, activation $149; Expert challenge $49 or $115 all-in; reset $649; payouts 90/10, Classic windows 1st–4th and 16th–20th, Expert every 5 business days. https://www.zenitfunding.com/ |

**What follows for the design.** (1) A prop firm's rulebook is a risk profile the gateway must enforce AHEAD of the firm
with a margin, because the firm's breach is permanent and ours is recoverable: trailing floor with unrealized P&L in
real time, a flat-by timer, a news blackout from a calendar file, a consistency governor, contract caps per account
size — `U-rules`, as data per firm and plan. (2) On ATAS the order-history bound is the open question: after a restart
the platform's own collection held the earlier order (2026-08-30); if a hardware probe shows it carries ALL of the
session's orders and fills back to the session open, `SupportsOrderHistory` becomes a TIMESTAMP bound rather than a
boolean, and every order on a flat-by-close account is inside it. Probe it; do not assume it. (3) Confirm with Zenit in
writing that a self-owned, AI-authored algorithm run by the account holder is "traded solely by you"; nothing in the
design hides that it is automated. (4) Several accounts at one firm must never hold opposite positions: the allocator
enforces it.

---

## D — AI list prices (read 2026-09-06 from OpenAI's own pages; re-read before every release)

**File:** `src/TradeAgent.AgentRuntime/ListPrices.cs` — the whole catalogue, in `costs.json`'s shape.
Overridable at runtime by `%LOCALAPPDATA%\TradeAgent\costs.json`, and beaten outright by the two
numbers the owner types on the Safety page.

These are the only figures in the product that are somebody else's published claim rather than a
measurement. They are not a bill: the same CLI costs per-token on an API key and nothing per-token on
a subscription. They exist so the daily cost cap bites out of the box, and they are charged as a
ceiling that says on screen that it is one.

| # | Source read | What was taken from it | Still open |
|---|---|---|---|
| D1 | <https://developers.openai.com/api/docs/pricing> | The STANDARD tier's short-context columns for the current-generation text models: input, cached input, cache writes, output per million. Eighteen rows, `gpt-6-astra` 10.00 / 1.00 / 12.50 / 50.00 down to `gpt-5-nano` 0.05 / 0.005 / — / 0.40, plus the specialised Codex row `gpt-5.3-codex` 1.75 / 0.175 / — / 14.00. A dash on the page is null in the file, and null means the input rate. | The page also carries a **long-context** tier (astra 20.00 / 75.00) and a **Codex fast mode** tier (`gpt-5.3-codex` 3.50 / 28.00). Neither the token counts nor either CLI's stream says which tier a turn ran under, so the standard tier is charged and an owner on another one uses the Safety page. If a later CLI reports the tier, take it and price accordingly. |
| D2 | <https://learn.chatgpt.com/docs/models> | Which model ids Codex can be set to: recommended `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.3-codex-spark`; other `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.2`, `gpt-5.3-codex`. Nine of the ten are in the `codex` catalogue. | `gpt-5.3-codex-spark` is recommended and the pricing page has **no row for it**, so it is absent rather than guessed from the `gpt-5.3-codex` row beside it. A turn on it is priced by the highest-list-price estimate or by the owner's own numbers. Add it the day the vendor publishes it. |
| D3 | The same two pages, for `opencode` | OpenCode ships no model and bills nothing itself, and TradeAgent's built-in sign-in for it writes an **OpenAI** key (`ApiKeyPlan.Label = "your OpenAI API key"`, `auth.json` keyed `"openai"`), so under the configuration this build ships an OpenCode turn is charged by OpenAI. All eighteen rows apply to it, without the Codex model restriction. | An owner who points OpenCode at Anthropic, Google or a local model is paying somebody else entirely and nothing here knows it. That is what `costs.json` and the Safety page's two numbers are for. If OpenCode's own sign-in gains other providers, this runtime's catalogue has to become provider-dependent rather than a copy of OpenAI's. |
| D4 | Not read from any page | The `custom` runtime has **no** shipped price. Its command, and therefore its provider, is an engineer's own line in `runtimes.json`. | Leave it empty. A price shipped for `custom` would be a guess about a vendor this build has never heard of. |

**The rule for whoever re-reads these.** Check the page, not this table and not memory. A model the
page does not price is absent from `ListPrices`, never interpolated from a neighbouring row. Change
`ListPrices.ReadOn` on the same commit as any figure, because the date travels onto the owner's
screen beside the number and a stale date beside a fresh price is the one reading nobody can catch.
