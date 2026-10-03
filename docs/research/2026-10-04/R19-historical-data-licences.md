# R19 — Licences of historical 1-minute kline sources: research backtests and evidence that may gate live eligibility (read 2026-10-04)

Scope: Binance's public archive (data.binance.vision) is now under the Binance Vision Dataset Terms v1.0, CC BY-NC-SA 4.0. The
orchestrator ruled on 2026-10-04 that archive datasets stay usable for non-production research only, and that nothing able to confer
LIVE eligibility may rest on them. This leg asks which historical 1-minute kline sources may (a) feed research backtests (EDGE
§ 4.5, E1–E4) and (b) serve as evidence that may gate live eligibility (E4's holdout verdict, the E6/E7 live gates). TradeAgent places
real orders on the owner's own account (a Belgian retail client, R16), and it is built for him and delivered to him. **This reports
what the terms say; it is not legal advice.** Labels: **DOC** = read today on the vendor's own page (URL in § 8); **RUN** = measured
today from this Mac without a key; **SECONDARY** = a search-index excerpt; **INFERENCE** = this leg's reasoning; **UNKNOWN / NOT
VERIFIED** = not established today, with what was tried; **REPO** = this repository at `a68b14b4`. Method: 6 of the 15 allowed web
searches, used only to find the terms' URLs. The terms were read in the desktop app's browser from Belgium: several are rendered by
JavaScript or answer curl with HTTP 202, and binance.com serves a Belgian visitor its Belgium terms. Docs pages were read by curl, and
7 reads went through WebFetch. Keyless GETs from this Mac ran 2026-10-03 22:37–23:00 UTC (00:37–01:00 on 2026-10-04, Brussels): about 130
GETs and 3 HEADs, ≈ 0.25 MB of market data in all. No key was used and no download is kept. Clauses are paraphrased closely, each with
its section or heading, because the folder keeps no page captures.

## 0. Ten-line answer

1. **No candidate grants both uses unconditionally.** Every free source is tied to personal or own-account use, and each reserves
   commercial use for a separate written licence. Whether TradeAgent, built for the owner and delivered to him, is a "commercial"
   product or use under these terms is counsel's first question (§ 6 Q1); every ranking below assumes it is not.
2. **(a) Research backtests, best first:** the Binance archive (§ 4.1 permits personal non-production backtesting outright), OKX
   EEA, Kraken, Binance REST, Bitvavo. **Coinbase and Bitstamp are out without a written licence.**
3. **(b) Evidence that may gate live:** only the execution venue's own data, recorded by the app as it arrives, has terms that
   reach this use (INFERENCE): OKX Europe (§ 9.3: trading your own account is not commercial resale), then Kraken (own-benefit
   use; get a written OK), then Bitvavo. Binance REST is UNKNOWN (§ 6 Q2–Q3). The Binance archive (§ 4.2), Coinbase and Bitstamp
   are excluded.
4. **Coinbase** (Market Data Terms, 7 Aug 2026) bars using its data to develop, validate or benchmark any algorithm, agent or
   automated system. TradeAgent is such a system, so both uses need marketdata@coinbase.com's written consent (INFERENCE).
5. **Bitstamp Europe** (terms in force from 15 Oct 2026) defines internal research, back-testing, signals, models, AI and combining
   venues' data as "Commercial Market Data Use", which needs a Market Data Agreement. It also bars storing or recording its data. It
   has the deepest history (2011), and none of it is usable here without a licence.
6. **OKX** (API Agreement, 28 Jul 2026) allows market data for your own personal, non-commercial trading, and binds keyless users of
   its public endpoints too. It bars evaluating AI systems meant to be commercialized (§ 9.4(c)). Its historical-data page counts
   developing your own strategy as personal use.
7. **Kraken** (EEA ToS, 29 Jun 2026) lets you use "Our Content", pricing data included, only for approved purposes and your own
   benefit. Non-personal commercial use of public data needs marketdata@kraken.com. § 11.5 also restricts automation and
   third-party applications. The OHLCVT bulk download has no licence of its own.
8. **Depth (RUN, BTC pairs):** Bitstamp 2011-08-18, then Kraken's first public trade 2013-09-10 (EUR; its bulk OHLCVT runs "from each
   pair's first trade" but was not measured), Coinbase 2015-01-26, Binance 2017-08-17, OKX EEA BTC-USDT 2018-01-11 (BTC-EUR only
   2023-11-21) and Bitvavo BTC-EUR 2019-08-01. Kraken's REST OHLC reaches back only 720 minutes.
9. **Deepest licensed for research:** with an implicit licence, Kraken (EUR pairs from 2013, own-benefit use, SHA-256 published for
   its bulk parts; bulk depth NOT VERIFIED). With an explicit one, Binance's archive (2017-08) and OKX (2018-01).
10. **Binance's terms cover "data.binance.vision and associated endpoints"** (§ 2.1). Whether that reaches data-api.binance.vision,
    the host TradeAgent's forward bar collector polls today (REPO `CandleSourceCatalog.cs:106`, landed 2026-09-19), is open. If it
    does, those forward bars cannot gate live either (§ 4.2).

## 1. Governing terms read today (all on 2026-10-04)

| Source | Terms read | Version or date as printed | Party |
|---|---|---|---|
| Binance archive (data.binance.vision) | Binance Vision Dataset Terms (S1) | v1.0, last updated 26 Aug 2026; first committed `bd110bb` 2026-09-30 (RUN, GitHub API) | Nest Exchange Ltd (ADGM) |
| Binance REST (`api.binance.com`, `data-api.binance.vision`) | Spot API "Terms of Use" page (S2, modified 2 Oct 2026), which points to the Product Terms; binance.com/en/terms redirects a Belgian visitor to "Global Terms of Use V2 (Belgium)" (S3) | Belgium terms last updated 22 Dec 2025 | Binance Poland sp. z o.o. |
| Kraken (REST OHLC and Trades, OHLCVT bulk) | EEA Terms of Service, Part A (S6); legacy API docs "Notices" (S7); OHLCVT article (S9) | ToS effective 29 Jun 2026; article updated 16 Sep 2026; notice undated | Payward Europe Solutions Ltd |
| Coinbase Exchange and Advanced Trade candles | Market Data Terms of Use (S10); the Exchange docs say accessing its market data API binds you to them (S11); CDP Terms (S13) | Market Data Terms 7 Aug 2026; CDP Terms 23 Jun 2026 | Coinbase, Inc., LMX Labs and affiliates; California law |
| OKX EEA (`eea.okx.com`) | OKX API Agreement, OKX Europe help centre (S14); historical-data T&C (S15) | Published 26 Mar 2026, last updated 28 Jul 2026; T&C 26 Oct 2023 | OKX (supplements the OKX ToS) |
| Bitvavo | Developer Agreement (S16) | Version of 18 Sep 2026 | Bitvavo |
| Bitstamp | Bitstamp Europe S.A. Terms of Use (S18); API page, "Request limits" and "Commercial Use" (S17) | In force: 30 May 2025; next: 15 Oct 2026 | Bitstamp Europe S.A. |

## 2. Sources × clauses (DOC unless labelled)

| Source | (1) Commercial use | (2) Redistribution or storage inside a product | (3) Automated or live trading on the data | (4) Rate limits, attribution |
|---|---|---|---|---|
| Binance archive | § 3.1 CC BY-NC-SA 4.0; § 3.4 any commercial use needs a separate written enterprise data licence | § 4.3 no licensing, hosting or commercial exploitation of the data or derived feeds; § 4.4 no commercial trading-bot platforms or financial products; § 2.4 derived models and indicators are covered too | § 4.2 no live proprietary trading execution or automated commercial order generation; § 4.1 backtesting allowed only for personal non-production research | § 7.1 obey posted limits; § 4.5 attribute Binance Vision, and redistributed derivatives keep the same licence; § 8.3 no "Binance" name in marketing a commercial app |
| Binance REST | Belgium terms § 22: a licence to use Binance IP only as needed to receive its Services, for non-commercial personal or internal business use; § 26(c) no resale or commercial purposes without written agreement | No data clause in the Belgium terms; the global terms (not served to Belgium) forbid, without written consent, trading services using Binance quotes, data feeds, and paid services built on its market data (SECONDARY) | Nothing on trading with the data; § 26(i) bars bots that take material not purposely provided (the API is provided: INFERENCE); § 2.1 of S1 may reach `data-api.binance.vision` (UNKNOWN) | REQUEST_WEIGHT 6,000/min (RUN, exchangeInfo); a klines call costs 2 (RUN, header); § 22 excludes the trade marks |
| Kraken | § 11.1 "Our Content" (pricing data included) only for approved purposes and your own benefit, else prior permission via marketdata@kraken.com; S7: non-personal commercial use of public-endpoint data needs permission | § 11.5 no licensing, selling, distributing or making Our Content available to third parties; no scraping or data extraction | § 11.5 no bots or automation for activity on Our Content, and no third-party applications that interact with it, without written consent (how this reads on the owner's own app: § 6 Q5) | OHLC returns the 720 most recent entries, older ones never (S8); public REST limit not stated (NOT VERIFIED); no attribution duty found |
| Coinbase | Personal or research purposes only; not for an application meant for end users other than you or your staff ("Permission to Use Market Data") | No redistribution or display of Market Data or Derived Works outside your organisation; no indexes, valuations or benchmark use ("Restrictions") | No use to develop, train, validate, benchmark or improve any AI or ML model, algorithm, agent or automated system ("Restrictions") | Exchange public: 10 req/s per IP, bursts to 15 (S11); CDP § 7 Coinbase may cap calls; no marks ("Ownership") |
| OKX EEA | § 9.2 only for your own internal purposes and own account; § 9.4 market data only for your own personal, non-commercial trading; S15 counts developing your own strategy as personal use | § 9.4(a) no redistribution, publication or display; (b) no competing data product or analytics platform; § 9.3(b) not inside a commercial product, SaaS or signal service | § 9.3 trading your own account, even profitably, is not commercial resale; § 9.4(c) no evaluating AI systems meant to be commercialized; § 10.1 AI-agent details on request | § 3.1 limits per the API docs; § 9.5 no circumventing limits, no OKX marks; § 9.4 binds keyless users of its public endpoints |
| Bitvavo | "Licenses" ¶ 2: API use only to facilitate your own use of Bitvavo's Services ("Authorized Use"); ¶ 1 Bitvavo keeps all rights in data provided | "Use Restrictions" ¶ 2: no collecting, caching, aggregating or storing API data except for the Authorized Use; no benchmarking or competitive use | Trading on Bitvavo through the API is the Authorized Use; nothing covers using its data to trade elsewhere (INFERENCE) | 1,000 weight points/min, candles cost 1; an unauthenticated caller over the limit is blocked 15 min by IP (S16 docs); ¶ 4 no implied endorsement |
| Bitstamp | § 26 (in force): a personal, non-commercial licence solely to receive the Services; S17: commercial use under a Data License Agreement (partners@bitstamp.net); § 3 (15 Oct 2026): research and back-testing are "Commercial Market Data Use" | § 26 (in force) no copying, scraping, republishing or derivative works; § 26 (15 Oct 2026) no downloading, caching, storing, recording or aggregating its market data without a Market Data Agreement | § 3 (15 Oct 2026): the permitted use covers viewing, submitting orders and executing on Bitstamp; "trading signals", models and AI are commercial use | 400 req/s, default 10,000 per 10 min (S17); § 26 no trading names or logos without consent |

## 3. Depth of 1-minute history (RUN, keyless, 2026-10-03 22:37–23:00 UTC)

| Source | Request shape | Status | Oldest 1-minute bar returned | Cap per call, or size |
|---|---|---|---|---|
| Binance REST | `https://api.binance.com/api/v3/klines?symbol=BTCUSDT&interval=1m&startTime=0&limit=2` | 200, 310 B | BTCUSDT `1502942400000` = 2017-08-17T04:00Z; BTCEUR 2020-01-03T08:00Z | not read today (the repo asks for 1000) |
| same, `data-api.binance.vision` | the same path | 200, 310 B | identical bars | — |
| Binance archive | monthly zip per pair (REPO `BinanceArchive.cs:11`) | not requested; the brief drops this host | 2026-08 file served on 2026-09-07 (REPO) | one month |
| Kraken REST OHLC | `https://api.kraken.com/0/public/OHLC?pair=XBTUSD&interval=1&since=0` | 200, 57 KB | 721 rows; oldest 2026-10-03T10:37Z, i.e. 12 h back | 720 |
| Kraken REST Trades | `https://api.kraken.com/0/public/Trades?pair=XBTEUR&since=0&count=1` | 200 | first trade XBTEUR 2013-09-10T23:47:07Z; XBTUSD 2013-10-06T21:34:16Z (bars could be rebuilt: INFERENCE) | — |
| Kraken OHLCVT bulk | `https://assets.kraken.com/marketing/institutions/Kraken_OHLCVT_Full_2026Q2.zip.part00`–`04` (HEAD only) | 200 | DOC: each pair from its first trade through 30 Jun 2026, no-trade minutes omitted; oldest bar NOT VERIFIED | 4 × 2,097,152,000 B + 583,772,104 B ≈ 8.97 GB; Q2 increment 537,866,903 B; all Last-Modified 2026-09-14 |
| Coinbase Exchange | `https://api.exchange.coinbase.com/products/BTC-USD/candles?granularity=60&start=2015-01-26T00:00:00Z&end=2015-01-26T04:59:00Z` | 200, 1.9 KB | BTC-USD 2015-01-26T00:32Z; 2015-01-20 (1 min) and 2015-01-14→25 (1 h) empty | 300 |
| Coinbase Advanced Trade | `https://api.coinbase.com/api/v3/brokerage/market/products/BTC-USD/candles?start=1422230400&end=1422248340&granularity=ONE_MINUTE` | 200, 4.4 KB | 2015-01-26T00:32Z, the same series | 350 |
| OKX EEA | `https://eea.okx.com/api/v5/market/history-candles?instId=BTC-USDT&bar=1m&after=…&before=…&limit=100`, binary-searched on `after` | 200 | BTC-USDT 2018-01-11T11:12Z; BTC-EUR 2023-11-21T10:27Z | 100 |
| Bitvavo | `https://api.bitvavo.com/v2/BTC-EUR/candles?interval=1m&start=0&end=…&limit=3` | 200 | BTC-EUR 2019-08-01T05:46Z (1-hour candles reach 2019-07-31) | 1440 |
| Bitstamp | `https://www.bitstamp.net/api/v2/ohlc/btcusd/?step=60&limit=120&end=1313675620` | 200, 9.4 KB | BTC/USD 2011-08-18T12:37Z; BTC/EUR 2016-04-16T16:55Z | 1000 |

Quality notes (DOC): Bitvavo's FAQ says its candles can change without new trades because order-book updates enter the calculation,
which weakens them as evidence and complicates EDGE § 4.1's O-PIT overlap test. Kraken publishes SHA-256 sums for each bulk part and
for the joined zip (RUN: the 515-byte list served 200). All five full-history parts and the Q2 increment carry Last-Modified
2026-09-14 (RUN): the whole history is rebuilt each quarter, so a silent revision would show only against an earlier copy
(INFERENCE). Binance's archive has a `.CHECKSUM` sidecar per file (REPO). No REST source publishes a checksum.

## 4. First-party records versus third-party archives

- **First-party, recorded by the app as it arrives:** the forward ledger (verdicts, paper fills, costs), the forward bars polled from
  `data-api.binance.vision` (REPO `CandleSourceCatalog.cs:106`) and the tape polled from `fapi.binance.com` (REPO
  `TapeSourceCatalog.cs:129`). These rows can earn `O-LIVE` (EDGE § 4.1). **Recording fixes provenance, not licence**
  (INFERENCE): the bytes stay the vendor's data under its terms, and several terms reach derived data as well (Binance § 2.4,
  Coinbase "Derived Works", Bitstamp "derived values"). Bitstamp's definition from 15 Oct 2026 excludes your own account and trade
  history, which supports the owner's own fills as first-party in licence terms too.
- **Third-party archives, fetched after the fact:** Binance's monthly zips, Kraken's OHLCVT parts, OKX's historical-data downloads,
  and any REST backfill of past minutes. By EDGE § 4.1 these are `O-ARCH`, or `O-PIT` only with a vendor checksum and a matching
  overlap with our own `O-LIVE` record. REST history has no checksum, so it stays `O-ARCH` whatever its licence (INFERENCE).
- **Gap for (b)** (INFERENCE): the app's built-in live market inputs are all Binance's today (REPO), yet Binance cannot be the owner's
  execution venue in Belgium (R16 E15) and its archive cannot be a live basis (§ 4.2). Anything meant to gate live should be recorded
  from a (b)-eligible venue's own public feed.

## 5. Ranking

Criteria, in order: how directly the text permits the use for the owner's own account (explicit, implicit, consent needed,
forbidden), then the quality of the evidence (checksums, trade-derived bars, immutability), then depth.

**(a) Research backtests**
1. **Binance archive.** § 4.1 permits personal non-production backtesting in so many words, and its files carry checksums; the
   orchestrator's ruling already confines it to this use.
2. **OKX EEA.** § 9.4 allows personal trading use, and S15 counts developing your own strategy as personal; BTC-EUR goes back only
   to 2023-11 and REST has no checksum.
3. **Kraken.** Own-benefit use is implicit (S7 asks permission only for non-personal commercial use; § 11.1), and it has the deepest
   checksummed EUR history; a written OK from marketdata@kraken.com would move it to first.
4. **Binance REST.** The same series as the archive, so it adds nothing for research; the Belgium terms tie use to receiving
   Binance's Services (§ 22), which a backtest for another venue is not (INFERENCE); if § 2.1 reaches `data-api.binance.vision`,
   that host is research-only like the archive.
5. **Bitvavo.** Allowed only within the Authorized Use (the owner trading at Bitvavo); shallow (2019-08), with candles that can change.
- **Excluded without a written licence:** Coinbase (the AI and automated-system clause) and Bitstamp (already limited to receiving the
  Services, and from 15 Oct 2026 back-testing is commercial use and storing is barred).

**(b) Evidence that may gate live eligibility** (E4 and E6 rest on history; E7 on forward returns, which must come from an eligible feed)
1. **OKX Europe's own data, if the owner trades there.** § 9.3 and § 9.4 cover own-account trading; the open points are § 9.4(c)
   and § 10.1, and the EUR history is short.
2. **Kraken's own data, if the owner trades there.** Own-benefit use covers it, and the EUR history is deep and checksummed; § 11.5
   calls for a written OK before live rests on it.
3. **Bitvavo's own data, if the owner trades there.** Trading on Bitvavo is the Authorized Use, but candles that change after the
   fact are weak evidence.
4. **Binance REST: UNKNOWN.** It is not a Belgian venue (R16), the global terms' market-data clause is SECONDARY, and the reach of
   § 2.1 is open (§ 6 Q2–Q3).
- **Not eligible:** the Binance archive (§ 4.2, already ruled); Coinbase (the AI clause); Bitstamp (a Market Data Agreement is needed,
  because its permitted use stops at viewing and executing, and "trading signals" and models count as commercial use).

## 6. The owner's or counsel's call — open questions

1. **Is TradeAgent a commercial product or use?** It is built for the owner and delivered to him (CLAUDE.md calls the no-terminal
   experience "what is being sold"). The answer decides Binance §§ 3.4/4.4, OKX § 9.4(c), Coinbase's end-user clause and Kraken's
   "non-personal commercial use". If it is commercial, (a) shrinks too.
2. **Does Binance's "associated endpoints" (§ 2.1), or § 7.1's "API base endpoints", reach `data-api.binance.vision`?** If it does,
   the forward bars the app records there, first-party as they are, cannot gate live (§ 4.2).
3. **Since Binance cannot be the execution venue, may its prices feed live trading at another venue** under the terms Binance serves
   a Belgian visitor? The global terms' market-data clause was not served here.
4. **Coinbase:** ask marketdata@coinbase.com for written consent to backtest AI-authored, frozen strategies, or exclude Coinbase.
5. **Kraken:** ask marketdata@kraken.com in writing whether the owner's own app is an approved purpose (research on OHLCVT, live
   trading at Kraken), and whether § 11.5's third-party-application clause applies to it.
6. **Bitstamp:** get a Market Data Agreement (partners@bitstamp.net) before any research, recording or modelling from 15 Oct 2026,
   or exclude Bitstamp.
7. **Bitvavo:** is storing candles for research an Authorized Use when the owner trades there, and how often do candles change?
8. **OKX:** does a frozen strategy authored by an LLM make TradeAgent an "Authorized AI Agent" with § 10.1 duties, and does it count
   as "intended to be commercialized" under § 9.4(c)?
9. **Binance archive files fetched before v1.0 was public** (REPO: a file served on 2026-09-07; terms dated 26 Aug, committed
   30 Sep): which terms governed them? § 1.4 protects data downloaded under earlier terms, but no earlier dataset terms were found.

## 7. NOT VERIFIED today

- **Kraken bulk OHLCVT:** the oldest bar per pair (≈ 9 GB, not downloaded). It is inferred only from the first public trade.
- **Binance's global Terms of Use:** binance.com redirects a Belgian visitor to the Belgium terms, so the global text is known only
  from a search-index excerpt (S5).
- **Rate limits and caps:** Kraken's public REST limit and Coinbase Advanced Trade's public limit (neither found on the pages read);
  Binance's per-call klines maximum (docs not read today).
- **Who the terms bind:** the market-data terms bind a keyless user explicitly at OKX (§ 9.4), Coinbase (access binds), the Binance
  archive (§ 1.4) and Bitvavo (any person using the API). Kraken's, Bitstamp's and Binance's Belgium terms are written for clients;
  whether they bind a keyless visitor is UNKNOWN.

## 8. Source registry (all read 2026-10-04 unless marked)

- S1 Binance Vision Dataset Terms v1.0 — https://github.com/binance/binance-public-data/blob/master/TERMS_AND_CONDITIONS.md (commit history: https://api.github.com/repos/binance/binance-public-data/commits?path=TERMS_AND_CONDITIONS.md)
- S2 Binance Spot API "Terms of Use" page (modified 2 Oct 2026) — https://developers.binance.com/docs/binance-spot-api-docs/PROD-TERMS-OF-USE
- S3 Binance Terms of Use, Belgium — https://www.binance.com/en/terms → https://www.binance.com/en/about-legal/be-terms (PDF: https://bin.bnbstatic.com/static/cms/cg08ou2ak0tn7mcplvfg/file/59ba09f6aca57beab35178bd13deaf32a0718a4f6bb2875d4ec3a176d1a31c52.pdf)
- S4 Binance "Market Data Only URLs" (updated 2 Oct 2026; no terms on it) — https://developers.binance.com/docs/binance-spot-api-docs/faqs/market_data_only
- S5 Binance global Terms of Use (search-index excerpt only; SECONDARY) — https://www.binance.com/en/terms
- S6 Kraken EEA Terms of Service — https://www.kraken.com/legal/eea-terms
- S7 Kraken legacy API docs, "Notices" — https://docs-legacy.kraken.com/api/docs/guides/global-intro/ (the current intro, https://docs.kraken.com/api/docs/guides/global-intro/, does not carry it)
- S8 Kraken OHLC endpoint — https://docs.kraken.com/api/docs/rest-api/get-ohlc-data
- S9 Kraken OHLCVT downloads — https://support.kraken.com/articles/360047124832-downloadable-historical-ohlcvt-open-high-low-close-volume-trades-data ; sums — https://assets.kraken.com/marketing/institutions/OHLCVT_Full_PARTS_SHA256SUMS.txt
- S10 Coinbase Market Data Terms of Use — https://www.coinbase.com/legal/market_data
- S11 Coinbase Exchange docs — https://docs.cdp.coinbase.com/exchange/introduction/welcome ; candles — https://docs.cdp.coinbase.com/api-reference/exchange-api/rest-api/products/get-product-candles ; limits — https://docs.cdp.coinbase.com/exchange/rest-api/rate-limits
- S12 Coinbase Advanced Trade public candles — https://docs.cdp.coinbase.com/api-reference/advanced-trade-api/rest-api/public/get-public-product-candles
- S13 Coinbase Developer Platform Terms of Service — https://www.coinbase.com/legal/developer-platform/terms-of-service
- S14 OKX API Agreement — https://www.okx.com/en-eu/help/okx-api-agreement
- S15 OKX historical-data T&C — https://www.okx.com/en-eu/help/historicaldata-terms-and-conditions
- S16 Bitvavo Developer Agreement — https://bitvavo.com/en/developer-agreement ; docs — https://docs.bitvavo.com/docs/rest-api/get-candlestick-data/ , https://docs.bitvavo.com/docs/rate-limits/ , https://docs.bitvavo.com/docs/faqs/
- S17 Bitstamp API, "Request limits" and "Commercial Use of Bitstamp's Exchange Data" — https://www.bitstamp.net/api/
- S18 Bitstamp Europe S.A. Terms of Use, in force — https://www.bitstamp.net/legal/terms-of-use/sa/ ; from 15 Oct 2026 — https://www.bitstamp.net/legal/terms-of-use/sa/upcoming/
- Repository (`a68b14b4`): `src/TradeAgent.Core/Data/CandleSourceCatalog.cs:106`, `src/TradeAgent.Core/Data/TapeSourceCatalog.cs:129`,
  `src/TradeAgent.Core/Data/BinanceArchive.cs:9-33`, `docs/queue/U-tape-archive.md:4`, `docs/EDGE-FACTORY.md` §§ 4.1, 4.5; `docs/research/2026-10-02/R16-belgium-venues.md` E15.
