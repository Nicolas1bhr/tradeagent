# U-forward-bars — real, advancing one-minute bars the app collects itself: closed bars only, receipt provenance, apart from the frozen evaluation datasets
**Arrow closed:** forward paper on ADVANCING market data (`manager-prompt.md` § 5 "Actual market observation"). **Observable result:** while the app runs, every
CLOSED 1m bar of the configured pair arrives from Binance's market-data-only host within about a minute of its close, with the instant it was received, gaps
counted and never filled, served read-only to the roles and to the runner to come; nothing here places an order. **Schema 24** (after `U-paper-verdict`'s 23, which lands before you; if it has not landed when you gate, your ladder reads 22 → 24 and that is expected — write no 23 block).
Not the money path. RUN from this Mac 2026-09-19: `GET https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=1m&limit=2` → HTTP 200, 0.46 s,
no key (official: developers.binance.com/en/docs/products/spot/faqs/market_data_only). Never call the real host from a test.
Read first: `docs/PRINCIPLES.md` § Evidence; `docs/CONTRACTS.md` "Candle sources" (2130-2174); `KlineNormaliser.cs:68-134`; `BinanceArchive.cs:27-36`;
`CandleSourceCatalog.cs:80-134`; `CandleSourceClient.cs:30-60`; `MarketDataService.cs:42-112,189-208`; `DatasetReader.cs:49-86`; `Protocol.cs:41-47`
(`data-list`/`data-bars`); `GatewayPipeServer.cs` their handlers; `AppHost.cs:928-993`; `Database.cs:1118-1183`; `tests/…/BinanceArchiveTests.cs` (the loopback
`HttpListener` harness with an injectable seconds-long client timeout — the shape `U-archive-win` demanded).
Items, one commit each with a one-sentence message:
1. Schema 25: `forward_bar` (source, symbol, open_time, open, high, low, close, volume, close_time, received_at, fetch_id; PRIMARY KEY (source, symbol, open_time)),
   `forward_fetch` (id, source, symbol, url, requested_at, received_at, http_status, bars, first_open, last_open, body_sha256, note) and `forward_gap` (source,
   symbol, from_open, to_open, bars_missing, seen_at). A bar is written only when its `close_time` precedes `received_at` (closed) and only through
   `ForwardBarStore.Append(fetch, bars)` in one transaction; a re-fetched bar that differs from the stored one is NOT overwritten — the first reading stands and
   the disagreement is counted in `forward_fetch.note`. Times normalised through `KlineNormaliser.TryUnitOf`; a body that does not parse is a recorded failure.
2. `ForwardBarCollector` (Provisioning): every 60 s for `Settings.MarketDataPair`, while the Data page's "collect live bars" toggle is on (one press, default ON;
   the `SettingsView.cs:157-165` family), GET `/api/v3/klines?symbol=S&interval=1m&startTime=<last stored open + 60 s>&limit=1000` on the host recorded as a
   catalogue entry `binance-spot-forward-klines` beside the archive's (`CandleSourceCatalog.cs:119-134`, `Verified = true`, the doc URL on the row — data, not
   code); 10 s request timeout; exponential backoff to 5 min on failure; every attempt a `forward_fetch` row; a gap between the stored last bar and the next
   received bar a `forward_gap` row. Started by `AppHost` with the app, stopped with it, independent of the mission loop; raises an in-process
   `BarClosed(symbol, openTime)` event the runner will subscribe to. Never blocks a UI thread; a failing host is a status line, not a crash.
3. Read-only surface: `data-list` names the forward series (symbol, first/last bar, bars, gaps, `last_received_at`, and the sentence "FORWARD — collected by
   TradeAgent minute by minute; no vendor checksum; not evaluation evidence"); `data-bars --source forward` serves a bounded window (≤ `DatasetReader.MaxBars`) to
   any role — no holdout applies (every forward bar post-dates every freeze; say so in the contract); `ForwardBarStore.Since(symbol, openExclusive, limit)` and
   `Freshness(symbol, now)` (the last closed bar's age) for the runner; `status` carries `forward_data {symbol, last_bar, age_seconds, gaps_today, last_error}`;
   the owner's report prints one line in section 7. Staleness is a fact answered here and consumed by the program's `data_freshness` bound at dispatch.
4. `CONTRACTS.md` new section "Forward bars" (what is claimed: closed bars as the vendor served them at receipt; what is not: a vendor checksum, executability,
   evaluation evidence); `USER-GUIDE.md` the Data page paragraph; `docs/RESEARCH-REQUIRED.md` the host and endpoint with their source and date.
Red-first tests (Unit/Integration, loopback listener serving canned kline JSON, never the real host): (a) `Only_closed_bars_are_stored_and_the_open_one_is_not`;
(b) `Every_fetch_is_recorded_succeeded_or_failed_and_a_bar_names_its_fetch`; (c) `A_gap_is_recorded_and_never_filled`; (d) `A_differing_refetch_does_not_
overwrite_the_first_reading`; (e) `Data_bars_serves_forward_bars_to_a_role_bounded_and_without_a_holdout_refusal`; (f) `The_collector_backs_off_after_failures_
and_recovers`; (g) `A_body_that_does_not_parse_is_a_recorded_failure_not_a_bar`. Mutants to watch red and quote: (i) the closed-bar check dropped → (a) red;
(ii) `INSERT OR REPLACE` in place of first-reading-stands → (d) red. The collector's timer injectable so tests never wait a minute.
Gate and report as `docs/HOW-WE-BUILD.md`: `--no-incremental` Release build 0 warnings; the three suites 0 failed; touched classes 3×; names vs `main` 0
removed; `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
