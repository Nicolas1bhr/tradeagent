# 08 — Perpetual-futures venues for the autonomous path (researched 2026-10-02)

Scope: TradeAgent's first DERIVATIVES venue (shorting, funding-aware strategies) under the gateway's four rules (client id
round-trip; history that truly reaches back to `since`; "rejected" only for a definite refusal; API only). Builds on, does
not repeat, `docs/RESEARCH-REQUIRED.md` § C (Binance SPOT, Revolut X) and sibling `research/03-edge-sources.md` § 1.
Labels: **DOC@2026-10-02** = read today on the official page named; **RUN@2026-10-02** = a public, key-less, read-only
request from the dev Mac today (nothing placed, cancelled or signed; no account endpoint); **INFERENCE**; **UNKNOWN**.
Raw captures and scripts: `research/raw08/`. Caveat: developers.binance.com refuses curl (HTTP 202, empty body), so Binance
pages were read through a summarising fetcher and every load-bearing fact was re-asked narrowly; the one contradiction
found is recorded. Hyperliquid, Bybit, OKX and Kraken docs were downloaded verbatim and grepped.

---

## 0. Bottom line

| Venue | Rule 1 (client id) | Rule 2 (history since T) | Test env | EU/Belgium | Verdict |
|---|---|---|---|---|---|
| **Kraken Futures (EU: PEDSL-CY, MiFID)** | PASS on paper: `cliOrdId` ≤100, "must be globally unique", on fills/status/history | PASS on paper: history API claims ALL order/execution history — prove with a key | docs: self-service demo; RUN: host redirects | MiFID perps marketed to Belgian traders | **1st** |
| **OKX Europe (X-Perps)** | PASS with discipline: `clOrdId` ≤32, unique only among pending; lookup = latest match | BOUNDED: unfilled cancels kept **2 h**; completed 7 d / 3 mo; fills 3 d / 3 mo | demo via header | MiFID entity; "perpetual-style" dated futures | 2nd |
| **Hyperliquid** | PASS: 128-bit `cloid`; status by cloid back to mid-2023 (RUN); fills carry cloid (RUN, undocumented) | BOUNDED by COUNT: 2,000 status entries (≈1,000 orders), 10,000 fills; bound provable per query | testnet needs a mainnet deposit; thin book | licensed nowhere; perps are MiFID derivatives | data source; venue = OWNER-DECISION |
| **Binance USDⓈ-M** | PASS: `newClientOrderId` ≤36, unique among open orders | BOUNDED: unfilled cancelled/expired **3 days**; all orders 90 d; trades 3 mo | demo (own book/funding) needs a Binance account | no EU authorisation found | not for a Belgian owner |
| **Bybit V5** | PASS: `orderLinkId` ≤36, unique | BOUNDED: unfilled cancels **24 h**; filled orders/executions 2 y | demo on mainnet prices | EEA API is broker-only | excluded |

---

## 1. Binance USDⓈ-M Futures

**Rule 1.** `POST /fapi/v1/order`: `newClientOrderId` matches `^[\.A-Z\:/a-z0-9_-]{1,36}$`, auto-generated if absent,
"unique among open orders" only; response carries `clientOrderId` DOC@2026-10-02
(https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api). `GET /fapi/v1/order` takes `orderId`
or `origClientOrderId`, returns `clientOrderId` DOC@2026-10-02 (…/trade/rest-api/Query-Order). Duplicate id among open
orders → `-4116` (added 2024-12-02) DOC@2026-10-02 (https://developers.binance.com/docs/derivatives/change-log ;
…/usds-margined-futures/error-code). `userTrades` rows carry `orderId`, not the client id → map through the order
DOC@2026-10-02 (…/trade/rest-api/Account-Trade-List). User stream `ORDER_TRADE_UPDATE` carries `c` client id, `i` order id,
`x`/`X` execution type/status, `t` trade id, `rp` realised PnL, `n` commission, `er` expiry reason, `V` STP mode
DOC@2026-10-02 (Binance's connector source, v17.5.0 of 2026-09-23:
https://github.com/binance/binance-connector-python/tree/master/clients/derivatives_trading_usds_futures —
`websocket_streams/models/order_trade_update_o.py`). Liquidation/ADL client-id prefixes not re-read → UNKNOWN; classify
"not ours" by a TradeAgent prefix instead. **Conditional orders moved:** since 2025-12-09 STOP/TAKE_PROFIT/TRAILING types
are refused on `/fapi/v1/order` (`-4120`) and live on `POST /fapi/v1/algoOrder` with `clientAlgoId` (same regex), queried
by `algoId`/`clientAlgoId`; once triggered, `actualOrderId` names the matching-engine order; `ALGO_UPDATE` carries
`caid`/`aid`/`ai` DOC@2026-10-02 (change log ; …/New-Algo-Order ; …/Query-Algo-Order ; connector `algo_update_o.py`).
Rule 1 therefore holds for stops via a two-hop link.

**Rule 2 — bounded, timestamp-shaped.**
- `allOrders` / `Query Order`: CANCELED or EXPIRED orders with no fills vanish after **3 days**, every order after **90
  days**; `allOrders` ≤7 days per call (default last 7), `limit` ≤1000, `orderId` paging; algo orders follow the same
  3-day/90-day rule DOC@2026-10-02 (…/All-Orders ; …/Query-Order ; …/Query-Algo-Order).
- `userTrades`: ≤7 days per call, `fromId` not combinable with times, **past 3 months only** (cut from 6 months, change
  log 2026-08-26) DOC@2026-10-02. `income` (FUNDING_FEE, COMMISSION, REALIZED_PNL; `SPECIAL_FUNDING_FEE` since 2026-09-10):
  last 3 months, default 7 days, ≤1000 per page DOC@2026-10-02 (…/account/rest-api/Get-Income-History ; change log).
- Async exports `/fapi/v1/order/asyn` (+ `trade/asyn`, `income/asyn`): ≤1 year span, **10/month** (orders) and 5/month,
  shared with the website, link valid 7 days, reach not stated DOC@2026-10-02 (…/Get-Download-Id-For-Futures-Order-History).
  Archive use only, never reconciliation.
- Contradiction: change log says `symbol` became optional on `allOrders` on 2026-08-25; the endpoint page reads it as
  required → UNKNOWN until a demo key tries both.
- **Answer:** "history since T" is provable for every order state only for **T ≥ now − 72 h** (minus a margin); for filled
  orders and trades back to ~90 days. An older query can make an unfilled order that DID land look like it never existed —
  the rule-2 trap — so the connector must refuse it.

**Rule 3.** HTTP 503 "Unknown error…" = execution status UNKNOWN, may have succeeded; "Service Unavailable." = failure;
`-1008` throttled = failure (reduce-only/close exempt); `-1006`/`-1007` say "execution status unknown" DOC@2026-10-02
(…/usds-margined-futures/general-info ; …/error-code). `-5028` = timestamp outside the matching engine's receive window
DOC@2026-10-02 → INFERENCE: with a small `recvWindow`, a lost request cannot execute after `timestamp + recvWindow`, so an
UNKNOWN settles by querying `origClientOrderId` after that instant (`-2013` = does not exist) — verify on demo first.
Definite refusals: `-2010/-2019/-2021/-2022/-4061/-4116/-4120/-5021/-5022`; UNKNOWN: 503-unknown, `-1001/-1006/-1007`,
timeouts, disconnects. A `-4116` on a retry is EVIDENCE the first attempt landed.
**Rule 4 and surface** DOC@2026-10-02 (New Order ; …/Change-Position-Mode): one-way vs hedge via
`POST /fapi/v1/positionSide/dual` (all symbols; refused with open orders/positions); `positionSide` BOTH/LONG/SHORT;
`reduceOnly` not in hedge mode nor with `closePosition`; `closePosition` = close-all STOP_MARKET/TAKE_PROFIT_MARKET (now
algo); STP EXPIRE_TAKER/MAKER/BOTH (+NONE per change log), default read as EXPIRE_MAKER; TIF GTC/IOC/FOK/GTX/GTD/RPI;
modify LIMIT only (`reduceOnly` on modify since 2026-09-15); `POST /fapi/v1/multiAssetsMargin`. Margin-type and leverage
pages exist but were not re-read → parameters UNKNOWN in detail.
**Limits** RUN@2026-10-02 (`fapi.binance.com/fapi/v1/exchangeInfo`): 2,400 weight/min, 1,200 orders/min, 300 orders/10 s;
571 trading perps; MIN_NOTIONAL **50 USDT on BTCUSDT**, 20 on ETHUSDT, 5 on 561 others; BTCUSDT ≤200 open orders.
**User stream:** `POST /fapi/v1/listenKey`, valid 60 min, extended by `PUT` DOC@2026-10-02 (…/Start-User-Data-Stream);
connection-lifetime rules not on the pages read → UNKNOWN.
**Fees:** VIP 0 maker 0.02 % / taker 0.05 %, 10 % off in BNB (updated 2026-05-01) DOC@2026-10-02
(https://www.binance.com/en/support/faq/binance-futures-fee-structure-fee-calculations-360033544231).
**Funding:** default 8-hourly at 00/08/16 UTC; at cap/floor a symbol moves to 1-hourly, back to 4-hourly after 16 calm
cycles; paid only if a position is held at the instant (±15 s); taken from available balance, then position margin
(updated 2026-03-06) DOC@2026-10-02 (https://www.binance.com/en/support/faq/introduction-to-binance-futures-funding-rates-360033525031).
RUN@2026-10-02 (`/fapi/v1/fundingInfo`, 804 rows): 469 symbols 4-hourly, 333 8-hourly, 2 hourly; BTC/ETH 8-hourly, ±0.3 %.
**API keys:** Reading / Spot & Margin / Futures / Withdrawals / Universal Transfer; withdrawal REQUIRES an IP allow-list;
since 2023-01-30 an unrestricted-IP HMAC key can only read; self-generated Ed25519/RSA keys are the stated alternative;
a key created before the futures account cannot get Futures permission; needs 2FA, a deposit, identity verification
DOC@2026-10-02 (https://www.binance.com/en/support/faq/how-to-create-api-keys-on-binance-360002502072). A futures key with
withdrawals off is possible (DOC); an unrestricted Ed25519/RSA key holding Futures permission → UNKNOWN.
**Testnet/demo:** `https://demo-fapi.binance.com`, `wss://demo-fstream.binance.com` DOC@2026-10-02 (general-info).
RUN@2026-10-02: `fapi`, `demo-fapi` and `testnet.binancefuture.com` all answer; demo and old-testnet hosts gave the SAME
BTCUSDT mark 84,618.3 and funding 0.00009767 vs production 84,621.6 and 0.00001169 → one demo backend with its own book and
funding (INFERENCE); the old testnet website 301-redirects to `demo.binance.com/en/futures/BTCUSDT`. Demo keys need a
login to a real Binance account DOC@2026-10-02 (spot demo page:
https://developers.binance.com/docs/binance-spot-api-docs/demo-mode/general-info; futures = INFERENCE). Parity is API-
contract only (fills, funding differ) and hinges on a Belgian holding a Binance account (UNKNOWN; leg 03: secondary
reports of EU withdrawal).

## 2. Hyperliquid

**Rule 1.** Orders take optional `c` = cloid (128-bit hex); `cancelByCloid` and modify-by-cloid exist; the order response
returns only `oid` DOC@2026-10-02 (https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/exchange-endpoint).
`orderStatus` takes an oid or 16-byte cloid and returns the order with `cloid`, or `unknownOid` DOC@2026-10-02
(https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint). Fills are documented WITHOUT a cloid
(`WsFill`) DOC@2026-10-02 (…/api/websocket/subscriptions), BUT RUN@2026-10-02: the top leaderboard address's `userFills`
rows carry a `cloid` key (and all 2,000 of its logged orders had one) → true in practice, absent in docs; test on our own
fills. Duplicate-cloid behaviour undocumented → UNKNOWN.

**Rule 2 — count-bounded, provable per query.** `userFills` ≤2,000 most recent; `userFillsByTime` ≤2,000 per response and
only the 10,000 most recent exist; `historicalOrders` ≤2,000 most recent DOC@2026-10-02 (info-endpoint). Measured
RUN@2026-10-02 on public addresses (`raw08/hlq.py`, `raw08/hl-*.txt`):
- `historicalOrders` is a **status-change log**: 1,870 entries = 940 orders (open + terminal), sorted by `statusTimestamp`
  descending → cap ≈1,000 orders. It spanned **14 minutes** for the busiest market maker, ~200 days for a mid trader.
- `orderStatus` resolved filled/cancelled orders **400–1,065 days** old (a cancel at 650 days) but answered `unknownOid` for
  April–May 2023 orders that have fills → a horizon near mid-2023; before it, "unknownOid" proves nothing.
- Fills with no order behind their `oid` are system events — `Spot Dust Conversion` (00:00 UTC), `Auto-Deleveraging` (with
  a `liquidation` object) — classify by `dir`, never as ours.
- `userFillsByTime` from 0 returned all 1,176 fills back to 2023-05-27; `userFunding` pages 500 rows back to 2023-12.
**Answer:** if `historicalOrders` returns <2,000 entries the history is complete (to the 2023 horizon); at 2,000 it is
provable only back to the oldest `statusTimestamp` returned, and an older `since` must be refused. Use a fresh address
dedicated to TradeAgent (sub-accounts need $100,000 of volume first DOC@2026-10-02, …/trading/sub-accounts); at ≤100
orders/day the bound is ≈10 days (INFERENCE). Beyond the caps only requester-pays S3 node data exists (`replica_cmds`,
`node_fills_by_block`) DOC@2026-10-02 (…/historical-data).

**Rule 3.** The API server answers only after the transaction is in a committed block DOC@2026-10-02 (…/hypercore/api-servers);
per-order errors come back in `statuses` (min $10, margin, reduce-only, ALO, IOC, oracle, OI caps), batch pre-validation as
one error DOC@2026-10-02 (…/api/error-responses). `expiresAfter` voids an action after that instant (stale cancels cost 5×
rate limit) DOC (exchange-endpoint) → INFERENCE: send orders with `expiresAfter = now + few s`; after a timeout wait past it,
then `orderStatus(cloid)`; within the horizon `unknownOid` means "never landed". Dead-man `scheduleCancel` (≥5 s ahead, ≤10
triggers/day) DOC. **Rule 4:** API only. **Surface:** one net position per asset (no hedge mode in the API — INFERENCE),
`updateLeverage` with `isCross`, reduce-only, TP/SL triggers, TIF Alo/Ioc/Gtc, STP cancels the resting order DOC@2026-10-02
(exchange-endpoint ; …/trading/self-trade-prevention ; …/trading/order-types).
**Limits:** 1,200 weight/min per IP (actions 1, `orderStatus` 2, history +1 per 20 items); per address 1 request per 1 USDC
traded since inception + 10,000 buffer, then 1 per 10 s; 1,000 open orders DOC@2026-10-02 (…/api/rate-limits-and-user-limits).
Nonces: 100 highest kept per signer, within (T−2 d, T+1 d) DOC@2026-10-02 (…/api/nonces-and-api-wallets).
**Fees / funding:** tier-0 perps taker 0.045 %, maker 0.015 % DOC@2026-10-02 (…/trading/fees). Funding **hourly** at 1/8 of
the 8-hour formula, interest 0.01 %/8 h, cap 4 %/hour, notional at ORACLE price DOC@2026-10-02 (…/trading/funding).
**API (agent) wallets:** 1 unnamed + 3 named per account (+2 per sub-account); named expiry ≤180 days; pruned agents must
never be reused DOC@2026-10-02 (exchange-endpoint ; nonces page). Agents "do not have permission to transfer or withdraw
funds" DOC@2026-10-02 (official SDK https://github.com/hyperliquid-dex/hyperliquid-python-sdk, `examples/basic_agent.py`);
`withdraw3` is user-signed; `agentSendAsset` only moves funds between the owner's own balances DOC (exchange-endpoint).
**Testnet:** `https://api.hyperliquid-testnet.xyz` DOC@2026-10-02 (…/for-developers/api); faucet (1,000 mock USDC) needs a
prior MAINNET deposit from the same address DOC@2026-10-02 (…/onboarding/testnet-faucet). RUN@2026-10-02: testnet answers
(212 perps) but BTC open interest 52.8 vs 34,650 mainnet, daily notional $3.1 M vs $2.1 B, funding 0.0000834 vs 0.0000049 →
contract tests only; paper = our simulator over mainnet data.
**Public data of ANY address** (no auth; copy-trading source): `clearinghouseState`, `userFills`, `userFillsByTime`,
`historicalOrders`, `orderStatus`, `userFunding`, `portfolio`, `frontendOpenOrders` DOC@2026-10-02 (info-endpoint ;
…/info-endpoint/perpetuals), RUN@2026-10-02 on third-party addresses; leaderboard `stats-data.hyperliquid.xyz/Mainnet/leaderboard`.
**Signing / C#:** msgpack(action) ‖ nonce (8 bytes BE) ‖ vault flag/address ‖ optional expiresAfter → Keccak-256 → EIP-712
"Agent" {source "a"/"b", connectionId}, domain "Exchange", chainId 1337, zero contract; user-signed actions use domain
"HyperliquidSignTransaction" DOC@2026-10-02 (SDK `hyperliquid/utils/signing.py`). Docs warn that msgpack key order,
trailing zeros and address case break signatures, and a bad one only shows as "User or API Wallet … does not exist"
DOC@2026-10-02 (…/api/signing). INFERENCE on effort: an ordered msgpack writer (~150 lines), Keccak-256 (.NET's SHA3_256
pads differently; ~100 lines) and secp256k1 with recovery id (~200 lines of point math, or Nethereum/BouncyCastle);
verifiable byte-for-byte against the SDK's 13 tests (`tests/signing_test.py`, incl. order-with-cloid). Moderate-to-high.
**Terms:** "Last updated on June 15, 2026", operator Hyperliquid Corp.; restricted = US, Ontario, sanctioned territories and
their citizens; no EU state named; VPN/location concealment prohibited; neither interface nor Hyperliquid licensed or
registered anywhere; bots barred only when abusive DOC@2026-10-02 (https://app.hyperliquid.xyz/terms, text read from the
app's `TermsOfUse` bundle because the page is client-rendered).

## 3. Bybit V5 (brief) — ranked out for an EEA owner

- Rule 1: `orderLinkId` ≤36 (letters, digits, `-`, `_`), "should be unique"; duplicate → `110072`; history and executions
  filter by it DOC@2026-10-02 (https://bybit-exchange.github.io/docs/v5/order/create-order ; …/v5/error ; …/v5/order/execution).
- Rule 2: `/v5/order/history` keeps unfilled Cancelled/Rejected/Deactivated orders only **24 hours**, other closed orders 7
  days, beyond that only orders with fills, up to 2 years, ≤7-day windows, cursor paging, "may delay" DOC@2026-10-02
  (…/v5/order/order-list); executions 2 years in 7-day windows; `/v5/order/realtime` holds the latest 500 closed orders,
  unreliable after a server restart DOC@2026-10-02 (…/v5/order/open-order). Any-state bound: 24 h.
- Rule 3: `10000` Server Timeout, `10016` server error, no "status unknown" wording → INFERENCE: treat as UNKNOWN.
- Test: testnet `api-testnet.bybit.com`; demo `api-demo.bybit.com` made from a mainnet account, public data identical to
  mainnet, demo orders kept 7 days DOC@2026-10-02 (…/v5/guide ; …/v5/demo).
- EU: EEA users must use `api.bybit.eu`, which supports only "Connect to Third-Party Applications" for API broker users
  DOC@2026-10-02 (…/v5/guide) → no self-built keys for an EEA retail owner (INFERENCE). Bybit EU GmbH is a MiCA CASP
  (Austria FMA, 2025-05-28) RUN@2026-10-02 (ESMA register CSV).

## 4. OKX V5 (brief) — the EEA route is OKX Europe and "X-Perps"

- Rule 1: `clOrdId` ≤32 alphanumerics, unique only among live/partially-filled orders, reusable after a terminal state;
  lookup by `clOrdId` returns only the LATEST match; not forwarded to attached TP/SL; order details only for live
  instruments DOC@2026-10-02 (https://www.okx.com/docs-v5/en/ ; EU copy https://my.okx.com/docs-v5/en/). Passes if the
  connector never reuses an id (INFERENCE).
- Rule 2: "last 7 days" order history keeps never-filled cancelled orders **only 2 hours**; "last 3 months" for completed
  orders; fills "last 3 days" / "last 3 months" DOC@2026-10-02 (same). Any-state bound: 2 h (leg 03's "7 days + 3-month
  archive" misses the 2-hour rule).
- Rule 3: `50004` timeout "does not mean that the request was successful or failed"; `expTime` voids a place/amend request
  after a deadline DOC@2026-10-02 (same) → strong UNKNOWN resolution.
- Keys: Read / Trade / Withdraw separate; ≤20 IPs; trade/withdraw keys with no IP binding expire after 14 days of
  inactivity (demo keys never) DOC@2026-10-02. Demo: `x-simulated-trading: 1` with demo keys made after logging into OKX;
  also in the EU docs DOC@2026-10-02. EU-account access to demo → UNKNOWN.
- EU: my.okx.com accounts must use `eea.okx.com` DOC@2026-10-02 (docs overview). EEA derivatives come from OKX Europe Markets
  Ltd (MFSA, MiFID II), with an appropriateness test for retail; the terms name "expiry futures referencing crypto-assets",
  no perpetual swaps, no country list DOC@2026-10-02 (https://www.okx.com/en-eu/help/terms-of-service-derivatives-eea).
  RUN@2026-10-02 on `eea.okx.com` public endpoints: 495 SWAPs (same as global, proves nothing) and 255 FUTURES, 225 with
  `ruleType = xperp` ("perpetual-style futures"), e.g. `BTC-USD_UM_XPERP-310404`: linear, USD-settled, expires 2031-04-04,
  funded 8-hourly (cap ±0.375 %); 24 h volume 2,572 BTC vs 81,958 on `BTC-USDT-SWAP`. INFERENCE: the X-Perp is OKX's
  EEA-compliant perp substitute — fine for shorting and funding work, thinner; Belgian retail access UNKNOWN.

## 5. Kraken Futures (Kraken Derivatives EU) — the strongest rules 1–2 on paper

- Rule 1: `cliOrdId` on `sendorder`, maxLength 100, "must be globally unique"; returned on the send response, open orders,
  `orders/status` (queryable by `cliOrdIds`) and fills; history events carry `clientId` DOC@2026-10-02
  (https://docs.kraken.com/api-reference/order-management/send-order ; …/order-management/get-specific-orders-status ;
  …/historical-data/get-your-fills ; …/account-history/get-order-events).
- Rule 2: `orders/status` covers only open orders and those filled/cancelled in the last 5 seconds; the history API
  (`futures.kraken.com/api/history/v3`) gives "access to all private order history" and all execution, trigger and
  account-log history by timestamp (`since`/`before`) or ID range, ≤1,000 per page + continuation token; events include
  OrderPlaced/OrderRejected/OrderCancelled; account-log CSV = 500,000 latest rows DOC@2026-10-02
  (…/get-order-events ; …/get-execution-events ; …/get-account-log). History budget 100 tokens, +100 per 10 min, 1 per
  order-history call DOC@2026-10-02 (https://docs.kraken.com/exchange/guides/futures/ratelimits). The only venue here
  claiming unbounded history — must be PROVEN with a key (UNKNOWN until then).
- Rule 3: no request-deadline parameter in the futures `sendorder` spec (INFERENCE); dead-man `cancelallordersafter` (call
  every 15–20 s, 60 s timeout) DOC@2026-10-02 (…/order-management/dead-mans-switch). UNKNOWNs settle via `orders/status`
  (≤5 s), then the history API.
- Keys: General API No / Read-only / Full (Full **excludes withdrawals**); Withdrawal API separately No / Full; ≤50 keys;
  HMAC-SHA512 over SHA-256; Kraken publishes C# sample code DOC@2026-10-02 (https://docs.kraken.com/exchange/guides/futures/introduction).
- Test env: that page describes a self-service demo at `demo-futures.kraken.com` (auto-generated credentials, identical API)
  DOC@2026-10-02, BUT RUN@2026-10-02: every path there (`/`, `/derivatives/api/v3/instruments`, `/tickers/PF_XBTUSD`) returns
  301 → `kraken.com/gb/features/futures` from this Mac. Retired or region-blocked → UNKNOWN; retry from the Windows host.
- Market: production `tickers` lists 204 `PF_` perps; `historical-funding-rates?symbol=PF_XBTUSD` returns 8,770 HOURLY rows
  2025-10-01 08:00 → 2026-10-02 01:00 RUN@2026-10-02 → funding hourly. Fees 0.02 % maker / 0.05 % taker per leg 03
  (DOC@2026-10-02 there: https://www.kraken.com/features/fee-schedule).
- EU: EEA perps come from Payward Europe Digital Solutions (CY) Ltd, CySEC licence 342/17, "MiFID regulated platform", up
  to 10x, EUR contracts; the page as served to this Mac (Belgian/French locale) pitches crypto perps to "active traders in
  Belgium" and answers "where can I trade regulated crypto futures in Belgium?" with Kraken Pro DOC@2026-10-02
  (https://www.kraken.com/pro/perps/crypto-perpetuals ; https://support.kraken.com/articles/risk-disclosure-eea, updated
  2025-07-03). Kraken's MiCA CASPs (Payward Global Solutions, Payward Europe Solutions; CBI, 2025-06-25) are on ESMA's
  register RUN@2026-10-02.

## 6. Regulatory reality for a Belgian retail owner (INFERENCE on top of what follows — not legal advice)

- ESMA Guideline 5 (final report, Dec 2024): perpetual futures, though they never expire, "should be treated as derivative
  contracts" under MiFID II Annex I Section C(4)–(10); crypto as a derivative's underlying is outside MiCA DOC@2026-10-02
  (https://www.esma.europa.eu/sites/default/files/2024-12/ESMA75453128700-1323_Final_Report_Guidelines_on_the_conditions_and_criteria_for_the_qualification_of_CAs_as_FIs.pdf,
  para 47). Offering perps to EU clients needs MiFID investment-firm authorisation (+ passport), not MiCA.
- MiCA grandfathering ended 1 July 2026; ESMA's statement of 17 April 2026 expects unauthorised firms to wind down orderly
  DOC@2026-10-02 (https://www.esma.europa.eu/esmas-activities/digital-finance-and-innovation/markets-crypto-assets-regulation-mica ;
  https://www.esma.europa.eu/sites/default/files/2026-04/ESMA75-113276571-1679_Statement_on_the_end_of_transitional_periods_under_MiCA.pdf);
  the FSMA says the same for Belgium DOC@2026-10-02 (https://www.fsma.be/en/crypto-asset-service-provider-casp).
- ESMA interim CASP register (updated 30 Sep 2026): Bybit EU, Kraken (Payward), OKX Europe, Coinbase, Gemini, Bitvavo,
  Bitpanda, Revolut present; **no Binance, no Hyperliquid** RUN@2026-10-02 (https://www.esma.europa.eu/sites/default/files/2024-12/CASPS.csv).
- Per venue: **Kraken** and **OKX Europe** = MiFID-authorised providers serving the EEA (DOC above) → the clean route.
  **Binance**: no EU authorisation found; its terms/restriction pages are client-rendered and unreadable today → UNKNOWN
  from official pages (leg 03: secondary reports of new EU sign-ups stopped June 2026, reverse-solicitation claims under
  examination). **Bybit** routes EEA users to a broker-only API (DOC). **Hyperliquid** admits EU users by its terms but is
  licensed nowhere (DOC), and its perps are MiFID derivatives (ESMA GL5).
- Whether a Belgian retail CLIENT breaks a rule by trading on an unauthorised venue, or a Belgian national rule restricts
  selling crypto-referenced derivatives to retail, could not be established (the FSMA site search errors; no page found)
  → UNKNOWN. **OWNER-DECISION (with legal advice):** live derivatives on Binance, Bybit-global or Hyperliquid. Recommended
  default: only a MiFID-authorised venue that onboards the owner as a Belgian retail client (Kraken first).

## 7. Recommendation (INFERENCE) and the flags each connector would truthfully report

1. **Kraken Futures via Kraken EU (PEDSL-CY).** Legal route naming Belgium; the only venue claiming complete history (rule 2
   without a window) and globally unique client ids (rule 1 without discipline); keys trade but never withdraw; HMAC auth and
   Kraken's C# sample make it the cheapest build. Weak spots: demo host redirects (paper = our simulator over Kraken public
   data, live at minimum size); 10x cap; status lookup ≤5 s, so reconciliation leans on a rate-limited history API (fine for a
   small bot). Owner actions (browser, no terminal): open Kraken EU, pass the derivatives appropriateness test, create a key
   (General = Full, Withdrawal = No Access) and paste it into TradeAgent.
2. **OKX Europe X-Perps.** MiFID, demo header, `expTime` deadlines; but unfilled cancels vanish after 2 h, ids are unique
   only among pending orders, and X-Perp volume is ~3 % of the swap's. Good alternate.
3. **Hyperliquid** — best public data, good rule 1, rule 2 count-bounded but provable. As a venue: OWNER-DECISION
   (unlicensed MiFID derivative, self-custody wallet and bridging, testnet needs a mainnet deposit, signing code). Data now.
4. **Binance USDⓈ-M** — best-documented rule 3 and a 72 h order-existence bound, but no clean legal footing for a Belgian
   owner → public market data only, as today.
5. **Bybit** — excluded for an EEA retail self-built bot.

`ConnectorCapabilities` is boolean (`src/TradeAgent.ConnectorSdk/Contracts.cs`; `ReconciliationProvable = SupportsClientOrderId
&& SupportsOrderHistory`). Following the ATAS precedent (history true only when retention is stated, and `GetOrdersAsync(since)`
refuses a `since` older than it), each connector needs a retention watermark; I recommend putting it on the record.

| Venue | SupportsClientOrderId | SupportsOrderHistory (proven bound) | ReconciliationProvable |
|---|---|---|---|
| Kraken Futures | true AFTER a key probe shows `cliOrdId` on status, fills and history | true, bound = account inception per docs, ONLY after a probe returns the account's first OrderPlaced/OrderRejected from `since` = inception; false until then | true after both probes |
| OKX (X-Perps) | true (never reuse ids) | true, watermark **2 h** any-state (7 d / 3 mo completed, 3 mo fills) | true inside 2 h; `expTime` settles UNKNOWNs in seconds |
| Hyperliquid | true (verify our fills carry cloid on testnet) | true, DYNAMIC watermark: complete if `historicalOrders` < 2,000 entries, else oldest `statusTimestamp`; never before mid-2023 | true; `expiresAfter` + `orderStatus(cloid)` |
| Binance USDⓈ-M | true (stops via `clientAlgoId` → `actualOrderId`) | true, watermark **72 h** any-state; 90 d filled orders; 3 mo trades | true inside 72 h |
| Bybit | true | true, watermark **24 h** any-state; 2 y fills | true inside 24 h |

## 8. Still open (each needs a key or the owner)

1. Kraken: demo retired or region-blocked (retry from Windows; ask support)? Does the history API return the account's FIRST
   order, including a rejected one? Does a Belgian retail account get perps after onboarding (OWNER)?
2. OKX: can an EEA (my.okx.com) retail account trade X-Perps and use demo trading? X-Perp fees.
3. Hyperliquid: duplicate-cloid behaviour; cloid on OUR fills; rejected-at-placement orders in `historicalOrders` (the
   MM's log held `badAloPxRejected`/`iocCancelRejected` rows — RUN — so probably yes).
4. Binance: `allOrders` without `symbol`; unrestricted Ed25519/RSA key with Futures permission; liquidation/ADL client-id
   prefixes; whether the matching engine enforces `recvWindow` on every order type.
5. Legal: the Belgian retail position on crypto-referenced derivatives and on unauthorised venues → OWNER-DECISION.
