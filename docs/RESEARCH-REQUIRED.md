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

## C5 — Forward market data host (measured 2026-09-19 by hand from this Mac; re-verify at build time)

**Decided: TradeAgent collects the configured pair's closed 1-minute bars itself while it runs**
(`U-forward-bars`), so it needs a public endpoint that serves an advancing window and needs no key.

| Fact | Value |
|---|---|
| Host | `https://data-api.binance.vision` — Binance's **market-data-only** host. It serves public market data and accepts **no authenticated or trading request at all**, which is why the collector holds no credential and can place nothing. Official: https://developers.binance.com/en/docs/products/spot/faqs/market_data_only |
| Endpoint | `GET /api/v3/klines?symbol={SYMBOL}&interval=1m&startTime={ms}&limit=1000` |
| Measured | 2026-09-19, from the dev Mac: `symbol=BTCUSDT&interval=1m&limit=2` → **HTTP 200 in 0.46 s, no API key**. That is the ONE real call this unit was allowed; the test suite talks to a loopback `HttpListener` and has never reached this host. |
| Answer shape | A JSON array of arrays. Columns read: `[0]` open time, `[1..4]` open/high/low/close, `[5]` volume, `[6]` close time. Timestamps are read through `KlineNormaliser.TryUnitOf`, so the millisecond→microsecond move Binance made in January 2025 reads correctly here for the same reason it does in the archive. |
| Checksum | **NONE, and none is possible.** No sidecar is published for a live window — the minute did not exist when one would have been signed. The ledger records only the hash THIS BUILD computed of the body it received, and `docs/CONTRACTS.md` "Forward bars" states what that does and does not prove. |
| Recorded as | `CandleSourceCatalog.BinanceForwardKlines` (`binance-spot-forward-klines`), `Verified = true` with the measurement quoted on the row, `ChecksumUrlShape = ""`, `CoverageTargetDays = 0`. **Data, not code**: correcting it is one line of `sources.json` in TradeAgent's own folder. |

**Still to re-verify at build time.** (1) The per-request `limit` ceiling — this build asks for 1000 and
has never been answered with more than two. (2) The host's rate limits for an unauthenticated caller: the
collector makes one request a minute per pair, which no published figure comes near, but the number is
not recorded here because nothing measured it. (3) Whether `startTime` is inclusive of the bar opening at
exactly that instant; this build asks from the first minute it does not hold, so an inclusive reading
costs nothing and an exclusive one loses no bar either — but it has not been measured.

---

## C5b — Binance USDⓈ-M public market context for the tape (measured 2026-10-02 from this Mac; re-verify at build time)

**Decided: while it runs, TradeAgent records the market's context into `state/tape.db`** (`U-tape-store`;
`docs/EDGE-FACTORY.md` § 4.1), from public endpoints that need no key. `docs/CONTRACTS.md` "The tape" states
what the record claims and does not.

| Fact | Value |
|---|---|
| Host | `https://fapi.binance.com` — Binance's USDⓈ-M futures REST host. Every call below is an unauthenticated GET of public market data; the collector sends no key and holds none. |
| Built-in rows | `binance-um-premium` (60 s): `GET /fapi/v1/premiumIndex` with no symbol — every symbol in one list, kept to the six. `binance-um-oi` (60 s): `GET /fapi/v1/openInterest?symbol={S}`. `binance-um-oi-5m` (300 s): `GET /futures/data/openInterestHist?symbol={S}&period=5m&limit=3`. `binance-um-ratios-5m` (300 s): `GET /futures/data/globalLongShortAccountRatio?symbol={S}&period=5m&limit=3` and `GET /futures/data/takerlongshortRatio?symbol={S}&period=5m&limit=3`. `binance-um-funding` (900 s): `GET /fapi/v1/fundingRate?symbol={S}&limit=2`. Universe: BTCUSDT ETHUSDT SOLUSDT BNBUSDT XRPUSDT DOGEUSDT. The `limit` is an overlap so a missed look loses nothing; it is the same on the first look, so there is no backfill. |
| Measured (the brief) | RUN 2026-10-02 from this Mac, no key, all HTTP 200 in 0.31–0.37 s: `GET https://fapi.binance.com/fapi/v1/premiumIndex?symbol=BTCUSDT`, `/fapi/v1/openInterest`, `/futures/data/openInterestHist?…&period=5m`, `/fapi/v1/fundingRate`, `/futures/data/globalLongShortAccountRatio?…&period=5m`, `/futures/data/takerlongshortRatio?…`. |
| Re-measured (the builder) | The same six with `symbol=BTCUSDT`, once each, read-only, at 2026-10-02 15:28:27 UTC: HTTP 200 in 0.357, 0.313, 0.329, 0.313, 0.313 and 0.320 s. These are the only requests this unit made to the host; the test suite talks to a loopback `HttpListener`, and `SuiteReachesNoVendorTests` refuses a test that names this host or builds a `TapeCollector` without a `baseUrl`. |
| Answer shapes | Decimals are JSON strings and times JSON integers in milliseconds. `premiumIndex`: `symbol, markPrice, indexPrice, estimatedSettlePrice, lastFundingRate, interestRate, nextFundingTime, time`. `openInterest`: `symbol, openInterest, time`. `openInterestHist`: `symbol, sumOpenInterest, sumOpenInterestValue, CMCCirculatingSupply, timestamp`. `globalLongShortAccountRatio`: `symbol, longAccount, longShortRatio, shortAccount, timestamp`. `takerlongshortRatio`: `buySellRatio, sellVol, buyVol, timestamp` — **no symbol field**, so the symbol asked for is the subject. `fundingRate`: `symbol, fundingTime, fundingRate, markPrice, rateType`. |
| Times, as read | At 15:28:28 UTC the newest `openInterestHist` and account-ratio point was 15:25:00 (208 s old) and the newest taker point 15:20:00 (508 s old): in that one reading the taker series ran one period behind the other two. `premiumIndex.time` read 15:28:28.000, about 0.2 s after the local clock was read before the request; `openInterest.time` 2.9 s before it. The last settled funding was 08:00:00 with `nextFundingTime` 16:00:00, and one earlier `fundingTime` read `…200002` — two milliseconds past the hour — which the tape keeps as served. |
| Documentation | The per-endpoint pages under `https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/` redirect to one catalogue page, recorded on each row with its anchor: `https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data` with `#mark-price`, `#open-interest`, `#open-interest-statistics`, `#long-short-ratio`, `#taker-buy-sell-volume`, `#get-funding-rate-history`; each answered HTTP 202 on 2026-10-02. **The page content was not read.** |
| Recorded as | `TapeSourceCatalog.BuiltIn()` — five rows, data in the `runtimes.json` pattern. `tape-sources.json` in TradeAgent's own folder may only ADD unkeyed rows, and every observation they produce is `O-ARCH`. |

**Still to re-verify at build time.** (1) Binance's API terms and its unauthenticated rate limits for these
endpoints were NOT re-read. The collector makes one premium call (every symbol) and six open-interest calls a
minute, eighteen 5-minute calls every five minutes and six funding calls every fifteen, and stops a look at
the first 429 or 418; no published figure is recorded here because none was read. (2) How soon after a
5-minute boundary each series publishes its point: a point first seen more than 330 s after its timestamp is
`O-ARCH` by rule, and the taker series' one-period lag in the reading above suggests many of its rows may be. The tape's own
`received_at` against `source_time` is the measurement. (3) Whether `timestamp` marks a period's start or its
end for each 5-minute series; the tape stores the vendor's value as served and assigns it no meaning.

---

## C5c — Binance spot's published instrument definition (measured 2026-10-02 and 2026-10-03 from this Mac; re-verify at build time)

**Decided: TradeAgent verifies an instrument by reading the venue's own definition itself** (`U-venue-verify`, schema
29), so the step a size is rounded down to is the venue's and no screen tells anyone to edit `venues.json`.
`docs/CONTRACTS.md` "The instrument check" states what a check claims and does not.

| Fact | Value |
|---|---|
| Host | `https://data-api.binance.vision` — the same market-data-only host as C5; no key is sent and none is held. It is the BUILT-IN origin of the check (`VenueCatalog.BinanceSpotDefinitionShape`); a `venues.json` override on any other origin is refused before a request is made. |
| Endpoint | `GET /api/v3/exchangeInfo?symbol={SYMBOL}` |
| Measured (the brief) | RUN 2026-10-02 from this Mac, no key: `symbol=BTCUSDT` → HTTP 200 in ~0.3 s; `tickSize 0.01`, `stepSize 0.00001`, `minQty 0.00001`, `NOTIONAL minNotional 5`. |
| Re-measured (the builder) | Once, read-only, at 2026-10-03 04:48:54 UTC: HTTP 200 in 1.03 s, 5,335 bytes, no redirect, one symbol listed, `status TRADING`. `PRICE_FILTER.tickSize "0.01000000"`, `LOT_SIZE.minQty "0.00001000"` and `stepSize "0.00001000"`, `NOTIONAL.minNotional "5.00000000"`. The only request this unit made to the host; the suite talks to a loopback `HttpListener` and `SuiteReachesNoVendorTests` refuses a test that builds an `InstrumentVerifier` without naming its built-in address. |
| Answer shape | `{"timezone","serverTime","rateLimits","exchangeFilters","symbols":[{"symbol","status","baseAsset","quoteAsset",…,"filters":[…]}]}`, decimals as JSON strings. The filters read: `PRICE_FILTER`, `LOT_SIZE`, `NOTIONAL` (else the older `MIN_NOTIONAL`). `MARKET_LOT_SIZE` carries `stepSize "0.00000000"` and is NOT read — a parser that took the first step it found would serve a step of nothing. |
| Recorded as | One `instrument_check` row per attempt, served for seven days by `VenueStore`. Data in the `runtimes.json` pattern: the path is overridable on the same origin by one line of `venues.json` (`definition_url`). |

**Still to re-verify at build time.** (1) Binance's developer documentation for `exchangeInfo` and its filter
definitions was NOT re-read; the filter names above are the measured answer, not the documented contract.
(2) The minimum notional is recorded and NOT applied: whether `applyMinToMarket` should make the paper
connector refuse a market order below it is a decision for the cost model's next version. (3) The host's
unauthenticated rate limit for `exchangeInfo` (weight) was not read; the app makes four requests a day per
pair plus the owner's presses, which no published figure is likely to come near, but it is not measured.

---

## C5d — OKX's announcements for EU users on the tape (measured 2026-10-03 and 2026-10-06 from this Mac; re-verify at build time)

**Decided: while it runs, TradeAgent records OKX's first page of announcements once a minute** (`U-tape-events`;
`docs/EDGE-FACTORY.md` § 4.2), each item screened at every read (`TapeScreen` v1). `docs/CONTRACTS.md` "The tape"
states what the record and the screen claim and do not.

| Fact | Value |
|---|---|
| Host | `https://eea.okx.com` — OKX's domain for EU users. An unauthenticated GET: with no `OK-ACCESS-KEY` header the endpoint is public and its answer is restricted by the asking IP. No key is sent and none is held. |
| Endpoint | `GET /api/v5/support/announcements` — page 1 only (`page` and `annType` are optional and not sent). |
| Documentation | Read 2026-10-06 ~01:12 CEST at `https://www.okx.com/docs-v5/en/#announcement-get-announcements`: sorted by `pTime` and `businessPTime`, newest first, and "the sort will not be affected if the announcement is updated"; 20 records a page; rate limit 5 requests per 2 seconds per IP; `pTime` is "the actual time the announcement was first published" and the "response may be delayed around 5 minutes"; `businessPTime` is the time shown on the page. |
| Measured (the brief) | RUN 2026-10-03 from this Mac, no key: HTTP 200 in 0.13 s, at most 20 items of at most 352 B, newest `pTime` first, 32 days a page. |
| Re-measured (the second builder) | Pages 1 to 15, one GET a second, no key, 2026-10-05 23:06:38–23:06:55 UTC: every answer HTTP 200 in 0.16–0.21 s, 5,047–5,416 bytes, `code "0"`, `totalPage "92"`; twenty items a page, each exactly `{annType, businessPTime, pTime, title, url}` in 175–347 bytes, newest `pTime` first on every page; page 1 spans 32.99 days. Item URLs are under `https://my.okx.com/en-eu/help/` and none is ever fetched. The 300 items through this build's `TapeParse.TryReadItems`: all 15 pages read, 300 distinct subjects; through `TapeScreen.Check`: none flagged — "OKX will launch AI/USD and AI/EUR for spot trading" among them. The suite talks to a loopback listener, and `SuiteReachesNoVendorTests` refuses a test that names an OKX or a Bybit host. |
| Terms basis | Re-read 2026-10-06 ~01:02 CEST. OKX API Agreement (`https://www.okx.com/en-eu/help/okx-api-agreement`, last updated 28 July 2026): § 3.2(a) public endpoints are usable without an API key, subject to the Agreement, the ToS and § 9; § 9.2 licenses use for one's own internal purposes; § 9.3(b) needs OKX's written authorisation to offer the API Services as part of a commercial product; § 9.4 keeps the data to one's own personal, non-commercial trading, never redistributed, and allows automated means at a rate not beyond what is "reasonably necessary for your personal trading use" and without unreasonable load, binding callers without an OKX account the same way. OKX's Terms of Service – EEA (`https://www.okx.com/en-eu/help/terms-of-service-eea`, last updated 26 May 2026) § 1.14 names that Agreement for the API Services and incorporates it; its body forbids no bot or script (the words occur only in the site's menu). One page a minute against 5 requests per 2 s, kept on the owner's machine for his own trading. |
| Bybit — DROPPED | Measured by the survey on 2026-10-03 (`https://api.bybit.eu/v5/announcements/index?locale=en-US&limit=50` → HTTP 200 in 0.49 s). The first builder read Bybit EU's General Terms on 2026-10-04 as forbidding bots, scripts and other automatic means to access or monitor any part of its platform (12 June 2026 version, § 9.2.2) — NOT re-verified. On 2026-10-06 they could not be read at all: the help-centre page (last updated 2026-01-23) points to `https://www.bybit.eu/legal/terms-and-conditions/General-Terms-and-Conditions`, which is drawn in a browser from a service that answered a plain GET "403 Access Denied" (nothing was tried past it), and a WebFetch of the page timed out after 60 s. No terms basis on the day, so no row. |
| Recorded as | `TapeSourceCatalog.Announcements()` — one row, `okx-eea-announcements`: cadence 60 s, `PublicationDelay` 300 s (its live window 60 + 300 + 30 s), the `announcement-json` parser, `Terms`, `TermsUrl`, `DocUrl`, `Measured`. `tape-sources.json` may not name that parser. No schema rung: `tape.db` stays at its own version. |

**Still to re-verify at build time.** (1) Whether selling TradeAgent needs OKX's written authorisation under
§ 9.3(b) is a legal question for the owner, NOT settled here. (2) Whether OKX's "around 5 minutes" holds: an
item first seen more than 390 s after its `pTime` is `O-ARCH` by rule, and the tape's own `received_at` against
`pTime` is the measurement. (3) Bybit EU's terms, read by a person in a browser, before any Bybit row is
reconsidered. (4) The screen's misses and its false flags beyond these 300 items are not measured.

---

## C5e — GDELT's news items about crypto on the tape (measured 2026-10-06 from this Mac; re-verify at build time)

**Decided: while it runs, TradeAgent records the crypto rows of GDELT's fifteen-minute GKG files, with GDELT's own
first-seen time** (`U-tape-archive`; `docs/EDGE-FACTORY.md` § 4.1), on the owner's own "Record GDELT news" switch.
`docs/CONTRACTS.md` "The tape" states what the record, the classes and the cap claim and do not.

| Fact | Value |
|---|---|
| Host | `https://data.gdeltproject.org` — GDELT's data host, served from Google Cloud Storage (`via: 1.1 google`, `server: UploadServer`). No key exists and none is sent. `http://` answers 301 to `https://…:443`; the recorder asks `https://` only and follows no redirect. |
| Listing | RUN 2026-10-06 00:42:08Z: `GET /gdeltv2/lastupdate.txt` → HTTP 200 in 0.83 s, 319 B, three lines `size md5 http://data.gdeltproject.org/gdeltv2/<label>.<feed>.zip` (export, mentions, gkg). Its GKG line named `20261006004500` — a label 2 min 52 s AHEAD of the clock: GDELT lists a file before its label. (The survey's RUN 2026-10-03: HTTP 200 in 0.46 s, 319 B.) |
| One file, inspected | `20261006004500.gkg.csv.zip` at 00:42–00:43Z: HTTP 200, 4,126,527 B; `last-modified` 00:33:51 (11 min 09 s before its label); `etag` and `x-goog-hash: md5=` equal to the listing's MD5; one deflated entry `20261006004500.gkg.csv`, sizes in its header, 12,743,789 B inflated; 979 rows of exactly 27 tab-separated fields, V2.1DATE the label on every row; `V2EXTRASXML` carries `<PAGE_TITLE>` on 979 rows, written with character references (`&#x2013;` 39 times, 63 in all), `<PAGE_PRECISEPUBTIMESTAMP>` on 623; longest row 35,860 B; `gkg-crypto-v1` kept 17. |
| The sample (the extract's size) | 24 files, one an hour, labelled 2026-10-05T01:00Z to 2026-10-06T00:00Z, streamed 00:58–01:00Z through this build's `GdeltGkg.ReadBatchAsync` and kept nowhere: every answer HTTP 200 in 0.53–1.94 s, 2,083,828–6,696,560 B (104,837,720 B in all — 4.37 MB a file, **419 MB a day** at 96 files); each storage MD5 and ETag equal to this build's MD5; Last-Modified 569–724 s BEFORE its label, none after; every GKGRECORDID `<label>-N` and every V2.1DATE the label; 24,671 rows, 99 kept (0–13 a file), 1,720,729 B as the tape keeps them — **6.88 MB a day (6.56 MiB), against the 25 MB cap** — the most a file kept 210,926 B, the longest kept row 44,294 B. 2 of the 24 files (labels `20261005040000`, `20261005090000`) held one row each with a raw Latin-1 byte (0xE1 in an address in `V2EXTRASXML`), neither kept: the first reading refused both files whole, so a row the filter does not keep is now read leniently and only a KEPT row must be UTF-8 — both re-read (01:00Z), 1 row kept each. |
| The screen | `TapeScreen` v2 over the 99 kept rows: none flagged. v2 resolves character references before its rules read — under v1, a title's `&#x200B;` was not a zero-width space and `&lt;|im_start|&gt;` was not markup. |
| Terms basis | GDELT's Terms of Use, `https://www.gdeltproject.org/about.html#termsofuse`: read by the survey on 2026-10-03 and re-read on 2026-10-06 00:42Z (HTTP 200 in 0.90 s, served from `https://gdeltproject.org/about.html`): every GDELT dataset is open to unlimited, unrestricted use — academic, commercial or governmental — without fee, and may be redistributed, rehosted, republished and mirrored in any form, provided any use or redistribution cites the GDELT Project and links to `https://www.gdeltproject.org/`. The row's `Citation` carries that credit; the Market data card and the user guide show it, and `U-tape-read` must show it wherever the items are shown. |
| Codebook | GKG Codebook V2.1 (`https://data.gdeltproject.org/documentation/GDELT-Global_Knowledge_Graph_Codebook-V2.1.pdf`), read 2026-10-06: the 27 fields in the order `GdeltGkg.Fields` names them; each GKGRECORDID begins with the date and time of the fifteen-minute update batch its record was created in; V2.1DATE is the same on every row of a file. `<PAGE_TITLE>` is not in that 2015 codebook — it is what the files carry today. |
| Binance archive — DROPPED | The survey read Binance's Dataset Terms v1.0 (2026-08-26; repository commit `bd110bb`, 2026-09-30) on 2026-10-03: CC BY-NC-SA 4.0, commercial use only under a written licence, no live proprietary trading or trading-bot platforms (§§ 3.1, 3.4, 4.2, 4.4). The decision taken on the owner's behalf on 2026-10-04 was to comply (C7). This unit makes no request to `data.binance.vision` and did not re-read those terms. |
| Recorded as | `TapeSourceCatalog.Archives()` — one row, `gdelt-gkg`: cadence 900 s, no delay, parser `gdelt-gkg-zip` (built-in only; the tape's poller leaves it to `GdeltRecorder`), `Terms`, `TermsUrl`, `Citation`, `DocUrl`, `Measured`. No schema rung: `tape.db` stays at its own version 1. |

**Still to re-verify at build time.** (1) GDELT's lead: 24 of 24 sampled files were dated 9.5–12 min before their
labels; one dated after its label is `O-ARCH` by rule, and how often that happens is not measured. (2) Whether GDELT
ever rebuilds a file it already served: the record's SHA-256 is the check, and only these files were read. (3) The
filter's recall and precision: not measured — 99 rows in a day's sample, against the survey's INFERENCE of ~750 a day.
(4) The screen's misses and false flags on GDELT beyond these 99 rows. (5) The terms reading is a reading on the day,
not legal advice.

---

## C5f — Hyperliquid's perpetual contexts on the tape; Deribit, DefiLlama and Kalshi left out (read and measured 2026-10-08 from this Mac; re-verify at build time)

**Decided: while it runs, TradeAgent records Hyperliquid's public perpetual contexts for BTC, ETH, SOL, BNB, XRP and
DOGE every five minutes, on the "Record market context" switch** (`U-tape-chain`; `docs/EDGE-FACTORY.md` § 4.1,
"Hyperliquid public positioning" — aggregates only, R03 § 5). `docs/CONTRACTS.md` "The tape" states what the record
claims and does not.

| Fact | Value |
|---|---|
| Host | `https://api.hyperliquid.xyz` — Hyperliquid's API, served through CloudFront (`X-Amz-Cf-Pop: BRU51-P1`, `Server: nginx/1.22.1`, `X-Cache: Miss from cloudfront`). No key exists for this read and none is sent. |
| Endpoint | `POST /info`, body `{"type":"metaAndAssetCtxs"}`, `Content-Type: application/json`; no `dex`, which the documentation defaults to "the first perp dex". Nothing per address — `clearinghouseState`, fills, vaults, the leaderboard — is asked. |
| Documentation | `https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint/perpetuals`, read 2026-10-08 22:47Z: "Retrieve perpetuals asset contexts (includes mark price, current funding, open interest, etc.)", a POST with `Content-Type` "application/json" and `type` "metaAndAssetCtxs". Rate limits (`…/api/rate-limits-and-user-limits`, read 10:42Z and 22:47Z): "an aggregated weight limit of 1200 per minute" per IP; `metaAndAssetCtxs` is not among the info requests of weight 2 or 60, so it is one of "all other documented info requests", weight 20. Historical data (`…/historical-data`, read 22:47Z): `asset_ctxs` uploaded "approximately once a month", the requester paying for transfer, "no guarantee of timely updates and data may be missing", and "You can use the API to record additional historical data sets yourself." |
| Measured (the brief) | RUN 00:19:47Z and 00:28:24Z, no key: HTTP 200 in 0.40 and 0.65 s, 72,225 and 72,294 B, uncompressed even with gzip offered; `[{universe[234], marginTables, collateralToken}, contexts[234]]`; a context is `{dayBaseVlm, dayNtlVlm, funding, impactPxs[2], markPx, midPx, openInterest, oraclePx, premium, prevDayPx}`, decimals as strings, with no time and no name; universe and context keys never collide; all six coins changed between the samples. The only vendor time is the `Date` header (00:19:48 GMT, received 00:19:48.23Z). |
| Re-measured (the first builder) | 10:38:38.76Z: HTTP 200 in 0.60 s, 71,993 B, BRU51; 234 universe entries = 234 contexts; BTC 0, ETH 1, SOL 5, BNB 7, XRP 25, DOGE 12; no key in both halves; the six merged in 299–330 B; `Date: Thu, 08 Oct 2026 10:38:39 GMT`, received 10:38:39.44Z. |
| Re-measured (the second builder) | 22:13:12.193Z: HTTP 200 in 0.51 s, 72,429 B, BRU51; 234 = 234; the same six indexes; no key in both halves; the six merged and canonical in 311–333 B (BNB, XRP 311 · DOGE 326 · BTC 327 · ETH 330 · SOL 333), every coin of the 234 in 258–337 B; 56 entries `isDelisted`, their `premium`, `midPx` and `impactPxs` null; `Date: Thu, 08 Oct 2026 22:13:12 GMT`, received 22:13:12.730Z. Each six's prices sit where its name is (BTC `markPx` 81779.1, ETH 2478.3, DOGE 0.08446). |
| What a day costs | 288 looks: about 20.9 MB on the wire and about 0.56 MB kept. Bounded by construction: an answer over 512 KB is refused unread (≤ 151 MB a day) and a coin's merged item over 2 KB refuses the answer (≤ 3.5 MB a day kept). |
| Terms basis — Hyperliquid, IN | No API terms are published (the documentation's index lists none). The Terms of Use at `https://app.hyperliquid.xyz/terms` govern the Interface; their text is in the page's own chunk `assets/TermsOfUse-D5AmfbGD.js`, loaded by `assets/index-BQnwZrhY.js`. Read in full for the brief on 2026-10-08, re-read at 10:38Z and at 22:12Z (42,673 B, `Last-Modified: Wed, 07 Oct 2026 03:40:34 GMT`, SHA-256 `e4327ea3ecc234671c6c8d7a6a5c367532c4d4a72cf24b25a2b0475730e5f91a`): "Last updated on June 15, 2026"; § 1.6 makes the Interface unavailable to persons in the United States, Ontario and sanctioned territories (not Belgium); § 1.7 closes outcome markets to "Excluded Persons" — perpetuals are not outcome markets; § 3.1.8 bars bots, scripts or other automated methods only "in ways that exceed reasonable usage, bypass rate limits, cause denial-of-service conditions, or disrupt" Hyperliquid; §§ 4.1–4.2 make its information informational only and possibly inaccurate; no clause speaks to a data licence, storage, redistribution or credit, so the row carries no `Citation`. One request of weight 20 every 300 s against 1,200 a minute. |
| Deribit — OUT | Exchange Membership Terms (13 August 2026), binding anyone "accessing or making use of Deribit's website and/or services", read for the brief on 2026-10-08 through the help centre's own API (its pages answered curl 403): § 27.1(i) "you will not … conduct any systematic or automated data collection activities (including … data harvesting) on our systems"; § 1.8 ranks the documentation's "market data collection" guidance below the Terms; § 2.10 names "prior written approval". Belgium is not on its restricted list of 2026-10-06. No request was made to Deribit by this unit. |
| DefiLlama — OUT | Terms of Use (effective 24 June 2025), read for the brief on 2026-10-08: § 8 "You may view the Site and its data under a limited licence; you agree not to: modify or copy the materials"; § 14 up to USD 100,000 per violation. Its documented `api.llama.fi/stablecoins` answered 404 and `/v2/historicalChainTvl` 200, daily `{date, tvl}` in seconds. No request was made to DefiLlama by this unit. |
| Kalshi — OUT | `kalshi.com` answered curl and WebFetch 429 — a Vercel Security Checkpoint, not passed — so its terms and its country list are unread. No request was made to Kalshi by this unit. |
| The routes | `U-tape-chain-dvol` — Deribit's DVOL once Deribit approves recording in writing (§ 2.10; info@deribit.com, § 38.4), measured by its own survey. `U-tape-chain-llama` — DefiLlama once it consents in writing to a stored copy (§ 8) and a documented address answers. Hyperliquid's monthly `asset_ctxs` archive — a requester-pays cost the owner decides. Kalshi — once its terms and country list can be read without a checkpoint and Belgium is in. Each OUT route is the vendor's written OK, which is the owner's message to send. |
| Recorded as | `TapeSourceCatalog.Positioning()` — one row, `hyperliquid-asset-ctxs`: cadence 300 s, no delay (its live window 300 + 30 s), one request for every coin, parser `hyperliquid-ctx-json` (built-in only), series `asset-ctxs` with its POST body (built-in only), subjects BTC ETH SOL BNB XRP DOGE (built-in only), `Terms`, `TermsUrl`, `DocUrl`, `Measured`, no `Citation`. `tape-sources.json` may not name the parser, and a body or subjects it names are not read. The suite talks to a loopback listener, and `SuiteReachesNoVendorTests` refuses a test that names a Hyperliquid host. No schema rung: `tape.db` stays at its own version 1. |

**Still to re-verify at build time.** (1) The Terms of Use govern Hyperliquid's Interface; that they are the terms
for its API is a reading, and a reading on the day is not legal advice — nor is whether selling TradeAgent changes
anything. (2) How far the `Date` header lies from when the figures were computed is not measured: the answer names
no block. (3) Whether Hyperliquid ever reorders one list without the other: the lengths are checked, the alignment
only by the measured prices. (4) The four readings above, before any OUT route is reconsidered.

---

## C6 — Venue fees the referee judges with (read 2026-10-02 from the venue's own page; re-read before every release)

**File:** `src/TradeAgent.Core/Strategy/VenueCostModel.cs`, `PublishedFees` — a table in CODE on purpose: the
friction a verdict is scored under is a standard, so it is neither in the venue catalogue nor in an override file
an agent could write. Each row is a venue's published standard **taker** rate (every fill the judge models takes
liquidity), with its source sentence and the day it was read. A campaign copies the whole model onto its row when
it opens, so correcting a row here changes the next campaign and never an open one.

| Venue | Rate used | Source and what it said | Still open |
|---|---|---|---|
| `binance-spot` | 0.001 (0.100%) per fill | <https://www.binance.com/en/fee/schedule>: Regular tier spot, 0.100% maker / 0.100% taker, 0.075% when paid in BNB — read 2026-10-02 (`docs/research/2026-10-02/R04` § 7, DOC 78). This build's author did not re-open the page; the figure is the research leg's. | The BNB discount, VIP tiers and any promotion are not modelled; the owner's real tier may be cheaper. |
| `revolut-x` | none | No fee page has been read for it. A campaign over its bars is REFUSED a model rather than given a neighbour's rate. | Read Revolut X's own published fee schedule and add the row with its date. |

**Not a measurement, either of them:** the 0.0002 slippage beside the fee is TradeAgent's stated ASSUMPTION and is
labelled as one in every pinned text; realised slippage against it is a paper-ledger measurement that does not exist
yet. **The rule for whoever re-reads these** is the D rule below: the page, not this table; a venue the page does not
price gets no row; and the read date changes on the same commit as any rate.

---

## C7 — Data licences (open since 2026-10-04; R19 § 6; `docs/CONTRACTS.md` "Data licences")

**Decided (the orchestrator's COMPLY decision, 2026-10-04):** the Binance archive's datasets read `research-only`
and confer no live eligibility; the forward ledger reads `unverified`; live allocation is closed until a dataset
whose terms reach own-account use exists. Each question below stays open until the step named beside it is done,
and is closed by a schema rung that APPENDS a `data_licence` reading — never by editing a class in place, and never
by gate code.

| # | Question (R19 § 6) | What depends on it | How it closes |
|---|---|---|---|
| Q1 | Is TradeAgent, built for the owner and delivered to him, a "commercial" product or use under the venues' data terms (Binance §§ 3.4 and 4.4, OKX § 9.4(c), Coinbase's end-user clause, Kraken's "non-personal commercial use")? | Every class this build records assumes it is not; if it is, research use shrinks too. | Counsel's written answer, filed under `docs/research/` with its date; a "commercial" answer is a rung appending narrower readings, and a written licence (`commercial-ok`) is a rung appending that reading for the source it covers. The owner's call, asked by the orchestrator. |
| Q2 | Does "data.binance.vision and associated endpoints" (Terms § 2.1), or § 7.1's "API base endpoints", reach `data-api.binance.vision`, the host the forward collector polls? | Whether the forward ledger could ever confer; it reads `unverified` meanwhile. | Binance's written answer, or counsel's reading of the Terms and of Binance's Spot API terms re-read on the day; the answer becomes a rung appending a `binance-spot-forward-klines` reading (`research-only` if the terms reach it). |
| Q9 | Which terms governed archive files downloaded before v1.0 was public (a file served on 2026-09-07; the Terms are dated 26 Aug and were committed 30 Sep)? § 1.4 protects data downloaded under earlier terms, and no earlier dataset terms were found. | Only the archive rows collected before 30 Sep 2026; they read `research-only` like every archive row. | An earlier version of the dataset terms found in Binance's own history (the repository's commit log) or Binance's written answer; until then nothing here relies on the earlier terms being wider. |

---

## D — AI list prices (read 2026-10-02 from OpenAI's own pages; re-read before every release)

**File:** `src/TradeAgent.AgentRuntime/ListPrices.cs` — the whole catalogue, in `costs.json`'s shape.
Overridable at runtime by `%LOCALAPPDATA%\TradeAgent\costs.json`, and beaten outright by the two
numbers the owner types on the Safety page.

These are the only figures in the product that are somebody else's published claim rather than a
measurement. They are not a bill: the same CLI costs per-token on an API key and nothing per-token on
a subscription. They exist so the daily cost cap bites out of the box, and they are charged as a
ceiling that says on screen that it is one.

| # | Source read | What was taken from it | Still open |
|---|---|---|---|
| D1 | <https://developers.openai.com/api/docs/pricing> | The STANDARD tier's short-context columns for the current-generation text models: input, cached input, cache writes, output per million. Twenty-one rows: twenty from the flagship table, `gpt-6-astra` 10.00 / 1.00 / 12.50 / 50.00 down to `gpt-5-nano` 0.05 / 0.005 / — / 0.40, plus the specialised Codex row `gpt-5.3-codex` 1.75 / 0.175 / — / 14.00. Added on 2026-10-02: `gpt-6.1-sol` 2.00 / 0.10 / 2.50 / 10.00, `gpt-6-luna` 0.10 / 0.01 / 0.125 / 0.50, `gpt-6-sol` 2.00 / 0.20 / 2.50 / 10.00; every figure of the eighteen rows read on 2026-09-06 was unchanged. A dash on the page is null in the file, and null means the input rate. | The page also carries a **long-context** tier (astra 20.00 / 75.00) and a **Fast** tier on every row (twice standard for the GPT-6 rows; `gpt-5.3-codex` 3.50 / 28.00). Neither the token counts nor either CLI's stream says which tier a turn ran under, so the standard tier is charged and an owner on another one uses the Safety page. If a later CLI reports the tier, take it and price accordingly. **`gpt-5.6-sol`'s 4.00 / 0.40 / 5.00 / 20.00 is promotional:** the page says it is available at least through 2026-11-21 and prints no other figure, and its model page calls it a 20% and a 33% reduction without naming the price reduced from. The row carries it because nothing else can be read. **Re-read before 2026-11-21:** if the promotion ends with the row unchanged, every turn on `gpt-5.6-sol` — the `codex` default — is under-charged. |
| D2 | <https://learn.chatgpt.com/docs/models> | Which model ids Codex can be set to, as read on 2026-10-02: recommended `gpt-6-astra`, `gpt-6.1-sol`, `gpt-6-luna`; also available `gpt-6-sol` (named as the replacement for `gpt-5.5` and `gpt-5.4`) and, during the rollout, `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`; other `gpt-5.5` (retiring from Codex with ChatGPT sign-in on 2026-10-14, not from the API); deprecated with ChatGPT sign-in `gpt-5.4` and `gpt-5.4-mini` (retired 2026-08-31; the API and Codex on an own key unaffected), `gpt-5.2`, `gpt-5.3-codex`; retired `gpt-5.3-codex-spark` (2026-09-14). Twelve are in the `codex` catalogue — every id the page names that the pricing page prices. | `gpt-5.3-codex-spark` has **no row** on the pricing page and is now retired, so it stays absent rather than guessed from the `gpt-5.3-codex` row beside it; a turn on it is priced by the highest-list-price estimate or by the owner's own numbers. The retiring and deprecated ids stay because a deprecated model still bills on an API key. |
| D3 | The same two pages, for `opencode` | OpenCode ships no model and bills nothing itself, and TradeAgent's built-in sign-in for it writes an **OpenAI** key (`ApiKeyPlan.Label = "your OpenAI API key"`, `auth.json` keyed `"openai"`), so under the configuration this build ships an OpenCode turn is charged by OpenAI. All twenty-one rows apply to it, without the Codex model restriction. | An owner who points OpenCode at Anthropic, Google or a local model is paying somebody else entirely and nothing here knows it. That is what `costs.json` and the Safety page's two numbers are for. If OpenCode's own sign-in gains other providers, this runtime's catalogue has to become provider-dependent rather than a copy of OpenAI's. |
| D4 | Not read from any page | The `custom` runtime has **no** shipped price. Its command, and therefore its provider, is an engineer's own line in `runtimes.json`. | Leave it empty. A price shipped for `custom` would be a guess about a vendor this build has never heard of. |

**The rule for whoever re-reads these.** Check the page, not this table and not memory. A model the
page does not price is absent from `ListPrices`, never interpolated from a neighbouring row. Change
`ListPrices.ReadOn` on the same commit as any figure, because the date travels onto the owner's
screen beside the number and a stale date beside a fresh price is the one reading nobody can catch. A
row carries the standard price; a promotional price is named in a comment with its end date, and
where the page prints only the promotional figure — `gpt-5.6-sol` today — the re-read is due before
that date.
