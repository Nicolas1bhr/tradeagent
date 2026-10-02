# 03 — Edge sources: legitimate information and structural edges for a small autonomous TradeAgent

Research leg 03, 2026-10-02. Input to the edge-factory plan; it decides nothing and marks nothing as built.

**Labels.** DOC@2026-10-02 (URL) = read today on the provider's or author's own page (where a live official page was blocked, a
dated Wayback snapshot of it, marked "archived"). SECONDARY (URL) = third-party reporting, vendor research or a search-index
excerpt. INFERENCE = this leg's judgement. UNKNOWN = not established today. "Sub-leg computation" = arithmetic done today on an
official data source, not a published figure. Repository facts cite the repository file and carry no label.

**Method and limits.** Eight parallel research sub-legs plus this leg's own reading. The session's shared WebSearch budget (200
calls) ran out at about 03:00 CEST; later checks used direct fetches of known official URLs only, so several items are UNKNOWN
rather than guessed. The fetcher geolocates to Belgium (the dev Mac's Telenet resolver answers polymarket.com with a captive
block page; Kraken and Binance served Belgian variants), and availability facts are per country — the owner's country is not
recorded in this report and must be checked before any live step. Everything is paraphrased.

## 0. Bottom line (INFERENCE)

1. The current pond — rules over one Binance 1-minute OHLCV series, long-or-flat — has no informational moat, and §3 shows
   that even LLM news signals decay quickly, die at ordinary trading costs and lose to buy-and-hold when tested broadly.
   Nothing here should try to win a race measured in milliseconds or seconds.
2. What a patient, broad, modest system can win on: (a) slow, messy, public information whose price impact plays out over
   hours to weeks — on-chain incidents, unlock schedules, delisting flags, positioning extremes; (b) structural flows and premia
   it can wait for — hedging-flow momentum in CME index futures, funding carry when it is rich; (c) its own point-in-time tape
   of sources that have no honest archive, which compounds and which a later entrant cannot buy; (d) honest evaluation
   (post-cutoff, costed, forward), which most published LLM-trading claims fail.
3. Venue reality for an EU resident reshapes the list (§1): Binance has no MiCA authorisation and curtailed EU service in July
   2026; Revolut X is MiCA-authorised but spot-only; shorting through perps is cleanest at OKX Europe (MiFID) or Kraken
   (CySEC); Hyperliquid admits EU users by its terms but is unlicensed; Polymarket and Kalshi are blocked or restricted in much
   of the EU and ESMA has warned on retail event contracts; Zenit refuses residents of Bulgaria, Croatia, Malta, Romania and
   Slovenia.
4. Funding carry is no longer a standing double-digit premium: BTC perp funding was about 12% in 2024, 5% in 2025 and 3%
   annualised in 2026 to date, with a quarter of 2026 prints negative (§3.2). Treat it as a regime trade.
5. Copy trading is the weakest idea on the owner's list: no official API reads other lead traders, the terms forbid scraping,
   copier outcomes are poor, and Zenit bans it. Public positioning (Hyperliquid) is useful as a feature, not as a template.
6. The LLM's comparative advantage is breadth and classification — reading vesting documents, post-mortems, announcements and
   filings across hundreds of assets — feeding frozen deterministic strategies. Any LLM-labelled signal is evaluated only after
   the model's knowledge cutoff or from labels recorded at knowledge time (§3.1).

## 1. Venues — what an EU resident can use (rules 1 and 2 are the gateway's, see CLAUDE.md)

- **Binance.** Not in ESMA's interim MiCA CASP register (entries to 2026-09-29, sub-leg read; my own re-read was truncated by the
  fetch tool) DOC@2026-10-02 (https://www.esma.europa.eu/sites/default/files/2024-12/CASPS.csv). Withdrew its Greek application on
  24 June 2026, stopped new EU registrations and suspended some services, aiming for France SECONDARY
  (https://www.coindesk.com/policy/2026/06/26/binance-tells-eu-users-it-will-no-longer-provide-services-after-failing-to-secure-mica-license);
  still serving EU users under a reverse-solicitation claim that ESMA, France, Germany and Greece are examining SECONDARY
  (https://www.cryptotimes.io/2026/10/01/binances-eu-comeback-draws-regulatory-questions-over-mica-ft/). USDⓈ-M availability
  per EU country UNKNOWN. Rule 1: futures orders queryable by origClientOrderId (1–36 chars) DOC@2026-10-02
  (https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/Query-Order). Rule 2: futures allOrders
  in windows under 7 days, orders older than 90 days gone, unfilled cancels gone after 3 days DOC@2026-10-02
  (https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/All-Orders). Test: futures Demo Trading
  (prices "similar to" live; funding simulation UNKNOWN) and a spot testnet DOC@2026-10-02
  (https://developers.binance.com/docs/derivatives/usds-margined-futures/general-info ; https://testnet.binance.vision/).
  INFERENCE: keep Binance for public data and demo; live is OWNER-DECISION.
- **Revolut X.** Revolut Digital Assets (Europe) Ltd MiCA-authorised by CySEC since 2025-10-20 DOC@2026-10-02 (ESMA CASPS.csv).
  Spot only; client_order_id is a required UUID, returned on placement, active and historical orders, but there is no lookup by
  it (single orders and fills by venue_order_id); historical orders ≤30 days per query (default 7); public order book, tickers,
  candles and trades need no auth; replace, TWAP and post-only exist; no sandbox DOC@2026-10-02
  (https://developer.revolut.com/docs/x-api/ ; https://developer.revolut.com/docs/api/revolut-x-crypto-exchange.yml). Fees
  UNKNOWN (revolut.com showed a bot check). Changes against docs/RESEARCH-REQUIRED.md §C: 30-day (not 1-week) history window,
  per-endpoint rate limits, public market data now exists.
- **Zenit Funding (CME/COMEX/NYMEX/CBOT futures via ATAS, Quantower, Volumetrica or its own platform).** Operator Rhea Digital
  Partners SRL, Brussels law DOC@2026-10-02 (https://zenitfunding.com/en/termsandconditions). Re-read by me today: residents of
  Bulgaria, Croatia, Malta, Romania and Slovenia (and ~50 non-EU states) are not accepted; only algorithms whose source code you
  own are allowed; HFT (many orders per second) and copy-trading services are banned; intraday only, flat before 22:10 GMT+1; no
  position taken in the minute before and after NFP, CPI, PPI, FOMC, central-bank decisions/speeches, oil inventories, Michigan,
  PMI DOC@2026-10-02 (https://zenitfunding.com/en/index). Also: scalping allowed; 5 challenge / 3 funded accounts max, yet the
  T&Cs let the firm block several accounts controlled by one person; live accounts split 80/20 (sim-funded 90/10); commissions
  $5.00 round turn minis, $1.60 micros; CME L1 data included, L2 $12/month DOC@2026-10-02 (same two URLs, sub-leg read). ATAS
  documents C# chart strategies for automated trading DOC@2026-10-02
  (https://help.atas.net/en/support/solutions/articles/72000602352-chart-strategies). Repository: ATAS order history is false,
  so the gateway refuses LIVE_AUTONOMOUS there (docs/RESEARCH-REQUIRED.md A1).
- **Hyperliquid.** Terms (updated 2026-06-15) restrict US persons, Ontario and sanctioned jurisdictions, name no EU state, put
  local legality on the user and ban VPN evasion DOC@2026-10-02 (https://app.hyperliquid.xyz/terms). Not a MiCA/MiFID venue
  (INFERENCE). Rule 1: optional 128-bit cloid, orderStatus by cloid, cancelByCloid, fills carry cloid DOC@2026-10-02
  (https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/exchange-endpoint). Rule 2 is count-bounded, not
  time-bounded: userFills and historicalOrders return the 2,000 most recent, userFillsByTime reaches only the 10,000 most recent
  fills; one active address's 2,000 orders spanned about 13 minutes DOC@2026-10-02
  (https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint ; sub-leg queries). Dead-man scheduleCancel
  exists; testnet faucet needs a prior mainnet deposit; tier-0 perps 0.045% taker / 0.015% maker DOC@2026-10-02
  (https://hyperliquid.gitbook.io/hyperliquid-docs/trading/fees). HIP-3 builder perps are live: trade.xyz lists 110 markets
  (equities, gold, indices), another dex lists pre-IPO perps; six builder dexes have every market delisted DOC@2026-10-02
  (sub-leg perpDexs/meta queries).
- **OKX (EEA).** OKX Europe Ltd MiCA-authorised by Malta's MFSA (2025-01-27) DOC@2026-10-02 (ESMA CASPS.csv, sub-leg read);
  derivatives offered to EEA retail through OKX Europe Markets Ltd, a MiFID II firm DOC@2026-10-02
  (https://www.okx.com/en-eu/help/terms-of-service-derivatives-eea). Rule 1 is weak: clOrdId (≤32 chars) is unique only among
  pending orders and a query returns the latest match; Rule 2: 7 days plus a 3-month archive; demo via a header DOC@2026-10-02
  (https://www.okx.com/docs-v5/en/). EU spot 0.08% maker / 0.10% taker; futures fees UNKNOWN DOC@2026-10-02
  (https://www.okx.com/en-eu/fees).
- **Kraken (EEA).** MiCA via the Central Bank of Ireland (2025-06-25); derivatives via Payward Europe Digital Solutions (CY), CySEC
  342/17, perps up to 10x DOC@2026-10-02 (https://www.kraken.com/legal/disclosures ; https://www.kraken.com/features/futures).
  cl_ord_id (UUID) filterable on ClosedOrders; retention UNKNOWN; futures demo self-service, spot sandbox by request; fees spot
  tier 1 0.40%/0.80%, futures 0.02%/0.05% DOC@2026-10-02 (https://docs.kraken.com/api/docs/rest-api/add-order ;
  https://www.kraken.com/features/fee-schedule).
- **Bybit EU.** MiCA via Austria's FMA (2025-05-28); help centre lists only spot and spot margin; EEA users must use api.bybit.eu,
  which supports only API-broker third-party connections (so probably no self-built bot keys — INFERENCE); flat 0.25% from
  2026-10-05 DOC@2026-10-02 (https://www.bybit.eu/en-EU/help-center/article/Trading-Fee-Structure ;
  https://bybit-exchange.github.io/docs/v5/guide). Bybit's global free data is still valuable (§2.1).
- **Deribit.** Coinbase agreed to buy it (announced 2025-05-08) SECONDARY (https://en.wikipedia.org/wiki/Coinbase); closing and
  EU-resident access UNKNOWN. Public options/DVOL data is free (§2.1).
- **Polymarket.** Close-only even through the API in BE, FR, DE, IT, PL, SK; IE, NL and MT (sports only) restricted on the
  website but not the API; VPNs breach the terms DOC@2026-10-02 (archived 2026-09-22: https://docs.polymarket.com/api-reference/geoblock);
  the help centre (updated 2026-08-14) lists IE, NL and MT as fully restricted DOC@2026-10-02 (archived:
  https://help.polymarket.com/en/articles/13364163-geographic-restrictions). Dutch Ksa penalty order 2026-01-20 DOC@2026-10-02
  (https://kansspelautoriteit.nl/adventure-one-qss-inc-polymarket); Belgian blacklist and ISP blocks in France, Portugal, Hungary
  and Spain SECONDARY (https://igamingexpress.com/belgium-blocks-polymarket-over-regulatory-non-compliance/ ;
  https://igamingbusiness.com/prediction-markets/anj-orders-isps-block-polymarket-illegal-gambling-concerns/ ;
  https://coinmarketcap.com/academy/article/spain-blocks-polymarket-kalshi).
- **Kalshi.** Offered in 140+ countries since October 2025, excluding France, Poland, UK and others SECONDARY
  (https://www.pokerscout.com/kalshi-announces-international-service-which-countries-excluded/); blocked in Spain since May 2026
  SECONDARY (same CoinMarketCap URL). ESMA (2026-07-03): event contracts that are MiFID instruments fall under national
  binary-option retail bans and need investment-firm authorisation DOC@2026-10-02
  (https://www.esma.europa.eu/press-news/esma-news/esma-reminds-firms-existing-rules-and-obligations-under-binary-option-measures).
  API: optional client_order_id on Create Order V2, demo environment, and old data moved to /historical/* endpoints (a backtester
  reading only live endpoints gets a silently shortened history) DOC@2026-10-02 (https://docs.kalshi.com/api-reference/orders/create-order-v2.md ;
  https://docs.kalshi.com/getting_started/historical_data.md).

## 2. Catalog by category

### 2.1 Market microstructure and derivatives

| Source | Access and cost 2026 | Latency | Point-in-time history | ToS / reliability |
|---|---|---|---|---|
| data.binance.vision | free bulk zips, each with a .CHECKSUM; spot aggTrades/klines (1s–1mo)/trades; USDⓈ-M daily aggTrades, bookDepth, index/mark/premium-index klines, klines, metrics, trades; monthly fundingRate DOC@2026-10-02 (https://github.com/binance/binance-public-data ; S3 listing https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?delimiter=/&prefix=data/futures/um/daily/) | daily files next day (~01:45–09:20 UTC); monthly ~1 week late DOC@2026-10-02 (S3 LastModified) | YES: spot from 2017-08; USDⓈ-M trades 2019-09, klines and funding 2020-01; BTCUSDT 5-min metrics (OI, top-trader and global long/short, taker ratio) from 2020-09 (pre-2021-12 backfilled on 2026-03-18); bookDepth (±0.2–5% bands, ~30 s) from 2023-01 DOC@2026-10-02 (S3 listing) | Dead: USDⓈ-M bookTicker (stopped 2024-03/04), liquidationSnapshot (COIN-M only, stopped 2024-10-14), EOHSummary (2023-10); BVOL 21-day gap DOC@2026-10-02 (S3 listing). Reported: metrics timestamps silently moved from period end to start on 2026-06-25 (five minutes of look-ahead if unhandled); bookDepth inner ask bands frozen since 2026-09-03 SECONDARY (https://github.com/binance/binance-public-data/issues/491 ; .../issues/496). Licence UNKNOWN |
| Binance USDⓈ-M REST/WS | free; funding history back to the first settlement (BTCUSDT 2019-09-10); OI history, long/short, taker ratio and basis keep only 30 days (older start rejected); liquidation stream sends at most one order per symbol per second (a sample) DOC@2026-10-02 (https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Open-Interest-Statistics ; .../websocket-market-streams/All-Market-Liquidation-Order-Streams) | real time | funding yes; the rest only via the archive or forward recording; no complete liquidation history anywhere free | market-data-only host for spot (docs/RESEARCH-REQUIRED.md C5) |
| Bybit archive + v5 API | free; public.bybit.com derivatives trade ticks from 2020-03-25; API funding to 2020-03, open interest to ~2020-08, account long/short from 2020-07-20; allLiquidation stream documented as all liquidations DOC@2026-10-02 (https://public.bybit.com/ ; https://bybit-exchange.github.io/docs/v5/market/open-interest ; .../v5/websocket/public/all-liquidation) | real time | YES — the cheapest free positioning history; order-book downloads back to 2023 in 7-day picks SECONDARY (https://blog.katanaquant.com/p/scraping-historical-orderbook-data) | liquidation completeness unverified; data terms UNKNOWN |
| OKX API | free; funding ~3 months, OI/long-short 180 days DOC@2026-10-02 (live probes of https://www.okx.com/api/v5/public/funding-rate-history) | real time | months only | — |
| Deribit | free public API: DVOL daily from 2021-03-24; history.deribit.com serves option trades back to 2019 DOC@2026-10-02 (live https://www.deribit.com/api/v2/public/get_volatility_index_data) | real time | YES for trades and DVOL | rate limits UNKNOWN |
| Hyperliquid | free info API (funding history, OI, premium, impact prices; 1,200 weight/min/IP); S3 buckets of L2 snapshots, asset contexts and node fills, requester-pays, monthly, no completeness guarantee DOC@2026-10-02 (https://hyperliquid.gitbook.io/hyperliquid-docs/historical-data) | real time | partial | — |
| Tardis.dev | tick trades, L2/L3, liquidations, funding, OI, options chains, 7+ years; $350–650/month academic to $3,000–6,000 business; first day of each month free DOC@2026-10-02 (https://tardis.dev/) | archive | YES (gold standard) | — |
| CoinGlass API | $29–699/month; minute history 6–40 days on low tiers, daily all-time; Hobbyist/Startup personal use only DOC@2026-10-02 (https://www.coinglass.com/pricing) | near real time | daily | personal-use tiers |
| Coinalyze API | free key, 40 calls/min; OI, funding, predicted funding, liquidations, long/short; intraday keeps ~1,500–2,000 points DOC@2026-10-02 (https://api.coinalyze.net/v1/doc/) | near real time | daily only; record intraday forward | citation requested |
| Databento (CME for Zenit) | usage-based historical with $125 free credits; Standard $199/month adds live; CME/CBOT/NYMEX/COMEX 16+ years, OHLCV-1s/1m on all plans DOC@2026-10-02 (https://databento.com/pricing) | archive / live | YES | licence per plan |

### 2.2 News, calendars and exchange announcements

| Source | Access and cost 2026 | Latency | Point-in-time history | Terms for automated / AI use |
|---|---|---|---|---|
| GDELT 2.0 | free; Events/Mentions/GKG every 15 minutes since 2015-02-19; DOC API searches a rolling 3 months, ≤250 results DOC@2026-10-02 (https://blog.gdeltproject.org/gdelt-2-0-our-global-world-in-realtime/ ; https://blog.gdeltproject.org/gdelt-doc-2-0-api-debuts/) | 15-minute batches | YES (Mentions carry publication time) | unlimited commercial use with citation and link DOC@2026-10-02 (https://www.gdeltproject.org/about.html) |
| Alpaca News (Benzinga content) | REST and WebSocket, stock and crypto tickers, ~130 articles/day; current price of news and EU account eligibility UNKNOWN DOC@2026-10-02 (https://docs.alpaca.markets/us/docs/historical-news-data ; https://docs.alpaca.markets/us/docs/streaming-real-time-news) | stream, no SLA | YES from 2015 (created_at/updated_at) | terms not read (UNKNOWN) |
| Massive (Polygon.io, renamed 2025-10-30) incl. Benzinga | news with published_utc: free Basic 2 years, paid $29–199/month from 2016; Benzinga dataset $99/month from 2009; all individual use DOC@2026-10-02 (https://massive.com/blog/polygon-is-now-massive ; https://massive.com/docs/rest/stocks/news ; https://massive.com/partners/benzinga). Benzinga direct prices UNKNOWN; AI/LLM licences negotiated privately SECONDARY (https://www.prnewswire.com/news-releases/benzinga-expands-ai-ready-financial-data-infrastructure-for-llms-and-rag-applications-302776568.html) | REST / stream | YES | individual-use licences |
| Tiingo News | $30/month individual, $50 commercial; 3 months queryable (15 years via sales); publishedDate and crawlDate (first seen) DOC@2026-10-02 (https://www.tiingo.com/about/pricing ; https://www.tiingo.com/documentation/news) | REST | partial, but crawlDate is honest knowledge time | commercial tier exists |
| NewsAPI.org | free plan 24 h delayed, 1 month, development only; Business $449/month real-time, 5 years DOC@2026-10-02 (https://newsapi.org/pricing) | real time on paid | 5 years on Business | free plan not for production |
| CryptoPanic | no free plan listed: $50/week or $199/month (1 month of history), Enterprise from $899/month; ≥30 s server cache; access revocable at will DOC@2026-10-02 (https://cryptopanic.com/developers/api/plans/ ; https://cryptopanic.com/developers/api/about) | ≥30 s | 1 month | revocable |
| CoinDesk Data (ex-CryptoCompare) | free tier ended 2026-05-21; plans unpriced; news walkable back by timestamp with PUBLISHED_ON and CREATED_ON; 10 s cache DOC@2026-10-02 (https://data.coindesk.com/blogs/changes-to-coindesk-data-indices-api-free-tier-access ; https://developers.coindesk.com/documentation/data-api/news_v1_article_list) | 10 s | YES (paid) | Personal plan non-commercial |
| Finnhub / EODHD / Marketaux | Finnhub free personal, crypto market news has no history; EODHD news with crypto tickers and sentiment, personal €0–99.99/month; Marketaux $0–199/month DOC@2026-10-02 (https://finnhub.io/docs/api/market-news ; https://eodhd.com/pricing ; https://www.marketaux.com/pricing) | REST | EODHD by date range | personal licences |
| Crypto outlet RSS (CoinDesk, Cointelegraph, The Block, Decrypt) | live feeds of 20–40 items DOC@2026-10-02 (https://www.coindesk.com/arc/outboundfeeds/rss/ ; https://cointelegraph.com/rss ; https://www.theblock.co/rss.xml ; https://decrypt.co/feed) | minutes | none — record forward | The Block bans AI processing of its content, even summaries; Cointelegraph bans AI training or operation on it without consent; CoinDesk and Decrypt ban robots/scrapers and allow personal non-commercial use only DOC@2026-10-02 (https://www.theblock.co/terms-of-service ; https://cointelegraph.com/terms-and-privacy ; https://www.coindesk.com/terms ; https://decrypt.co/terms-of-service) |
| Macro calendars and data | BLS ICS feed (08:30 ET releases), BEA ICS/JSON, Fed/ECB calendars DOC@2026-10-02 (https://www.bls.gov/schedule/news_release/ ; https://www.bea.gov/news/schedule); ALFRED vintages are day-level DOC@2026-10-02 (https://alfred.stlouisfed.org/help); Trading Economics API $199–399/month DOC@2026-10-02 (https://tradingeconomics.com/api/pricing.aspx) | scheduled | vintages, day-level | FRED's legal page bans use in connection with developing or training ML/LLM systems DOC@2026-10-02 (https://fred.stlouisfed.org/legal/); Investing.com bans use of its data without permission DOC@2026-10-02 (https://www.investing.com/about-us/terms-and-conditions) |
| Exchange announcements | Binance: official signed WebSocket topic com_announcement_en with publishDate DOC@2026-10-02 (https://developers.binance.com/docs/cms/announcement); from the Belgian connection the site hid the new-listing category DOC@2026-10-02 (https://www.binance.com/en/support/announcement/list/48). Coinbase opens transfers before trading DOC@2026-10-02 (https://www.coinbase.com/listings). Upbit and Bithumb Korean notice boards are live; Upbit's announcement WebSocket covers only SG/ID/TH DOC@2026-10-02 (https://upbit.com/service_center/notice ; https://global-docs.upbit.com/reference/websocket-announcement) | seconds (stream) | none official — record forward | Binance website terms UNKNOWN; use the API stream |

### 2.3 Official sources, filings, ETF flows

| Source | Access and cost | Latency | Point-in-time history | Notes |
|---|---|---|---|---|
| SEC EDGAR | free, no key; 10 req/s; declared User-Agent DOC@2026-10-02 (https://www.sec.gov/about/developer-resources) | submissions API usually <1 s after dissemination, XBRL <1 min, XBRL RSS every 10 min DOC@2026-10-02 (https://www.sec.gov/search-filings/edgar-application-programming-interfaces) | YES: acceptanceDateTime in true UTC per filing; insider data sets 2006Q1–2026Q2; 13F sets 2013→ DOC@2026-10-02 (https://data.sec.gov/submissions/CIK0000320193.json ; https://www.sec.gov/data-research/sec-markets-data/insider-transactions-data-sets) | Form 4 two business days; 8-K four; 13F 45 days; 13D five business days since 2024-02-05 DOC@2026-10-02 (https://www.sec.gov/newsroom/press-releases/2023-219). Equities only — reaches this system via crypto-treasury companies and index futures (INFERENCE) |
| US congressional PTRs | official PDFs free; Quiver $30/month (non-commercial) … Unusual Whales $150–375/month personal use DOC@2026-10-02 (https://api.quiverquant.com/pricing/ ; https://unusualwhales.com/public-api) | up to 30 days after notice / 45 after trade DOC@2026-10-02 (https://ethics.house.gov/financial-disclosure/) | vendor vintages UNKNOWN | 5 U.S.C. §13107(c) bars most commercial use of the reports SECONDARY (https://www.law.cornell.edu/uscode/text/5/13107); House passed a purchase ban 2026-07-22, Senate failed 2026-09-30 SECONDARY (https://rollcall.com/2026/07/22/congressional-stock-trading-bill-passes-the-house/) |
| Spot crypto ETF flows | Farside HTML (terms UNKNOWN, blocks bots); SoSoValue API free demo key, but summary-history reaches only ~1 month; issuer pages (e.g., iShares IBIT daily shares and NAV as CSV) DOC@2026-10-02 (https://sosovalue.gitbook.io/soso-value-api-doc/2.-etf/summary-history ; https://www.ishares.com/us/products/333011/ishares-bitcoin-trust-etf) | US evening / next morning SECONDARY (https://www.tftc.io/bitcoin-etf-flows) | rebuild from issuer files or record forward | revisions UNKNOWN |
| Central banks | Fed RSS feeds, FOMC 2026 Oct 27–28 and Dec 8–9, statements 14:00 ET DOC@2026-10-02 (https://www.federalreserve.gov/feeds/feeds.htm ; https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm); ECB RSS, decisions 14:15 CET, speeches CSV updated monthly DOC@2026-10-02 (https://www.ecb.europa.eu/home/html/rss.en.html ; https://www.ecb.europa.eu/press/govcdec/mopo/html/index.en.html ; https://www.ecb.europa.eu/press/key/html/downloads.en.html) | seconds–minutes | YES (Fed minutes from 1993, ECB speeches) | the calendar also drives Zenit's blackout |

### 2.4 Social media (automated reading)

| Source | Cost / access 2026 | ToS for this use |
|---|---|---|
| X API | pay-per-use only: $0.005 per post read, cap 3M reads/month (≈$15k at list, sub-leg arithmetic) before Enterprise; recent search 450 req/15 min DOC@2026-10-02 (https://docs.x.com/x-api/getting-started/pricing ; https://docs.x.com/x-api/fundamentals/rate-limits) | developer agreement (2026-04-27) bans training or fine-tuning foundation/frontier models on X content; ≤1.5M post IDs to any entity per 30 days DOC@2026-10-02 (https://docs.x.com/developer-terms/agreement). Full-archive pricing UNKNOWN |
| Reddit Data API | UNKNOWN (pages unreachable today) | OWNER-DECISION until read |
| Telegram | Bot API sees only chats it belongs to DOC@2026-10-02 (https://core.telegram.org/bots/faq) | bot terms forbid scraping public groups/channels for datasets, ML or AI products; API terms forbid using Telegram data to train or develop AI DOC@2026-10-02 (https://telegram.org/tos/bot-developers ; https://core.telegram.org/api/terms) |
| Discord | — | terms (2025-09-29) forbid scraping without written consent and commercial use of scraped data DOC@2026-10-02 (https://discord.com/terms) |
| Bluesky Jetstream | free, unauthenticated whole-network stream, self-hostable DOC@2026-10-02 (https://bsky.network/docs/jetstream/) | no explicit scraping/AI clause in the 2025-08-14 terms DOC@2026-10-02 (https://bsky.social/about/support/tos) |
| Farcaster | Neynar free tier 10M credits/month, or a Snapchain node DOC@2026-10-02 (https://docs.neynar.com/reference/what-are-the-rate-limits-on-neynar-apis ; https://snapchain.farcaster.xyz/) | open protocol |
| LunarCrush / Santiment | LunarCrush $90–900/month, daily history from 2020 DOC@2026-10-02 (https://lunarcrush.com/pricing); Santiment API free/Pro: 1 year, last 30 days hidden; Max $249/month DOC@2026-10-02 (https://academy.santiment.net/products-and-plans/sanapi-plans/) | vendor licences; revision policy UNKNOWN |

### 2.5 On-chain

| Provider | Cheapest useful API tier 2026 | Point-in-time? | Licence |
|---|---|---|---|
| Glassnode | Advanced $49/month = 14 days of 1d data (no backtests); on-chain API with PiT ≈ $12–14k/year (sub-leg sum of listed packages) DOC@2026-10-02 (https://studio.glassnode.com/pricing) | PiT metrics immutable but only since 2024 (all metrics since July 2025); other history revised by clustering DOC@2026-10-02 (https://docs.glassnode.com/data/point-in-time-metrics) | personal below Professional |
| CryptoQuant | $99/month first on-chain API, 1 year history; $799/month full history DOC@2026-10-02 (https://cryptoquant.com/pricing) | UNKNOWN | personal below Premium |
| Nansen | API Pro $49/month (2,000 credits) or pay-per-call from $0.01 DOC@2026-10-02 (https://www.nansen.ai/api) | historical smart-money snapshots may change after label corrections DOC@2026-10-02 (https://docs.nansen.ai/api/backtesting-data/historical-smart-money-positions) | — |
| Arkham Intel API | by request, usage plans "from $100"; tag-update feed with timestamps allows a first-seen label log DOC@2026-10-02 (https://arkm.com/llms/guides/getting-access.md ; https://arkm.com/llms/get-intelligence-address_tags-updates.md) | labels change continuously | UNKNOWN |
| Dune | free tier has no API exports; Analyst $65/month DOC@2026-10-02 (https://dune.com/pricing) | query-dependent | internal use only |
| DefiLlama | free keyless API (TVL, prices, stablecoins, yields, DEX volumes, fees); unlocks/emissions need the $250/month API plan DOC@2026-10-02 (https://api-docs.defillama.com/ ; https://defillama.com/subscription) | emissions: body + lastModified, no versions DOC@2026-10-02 (https://api-docs.defillama.com/llms-pro.txt) | — |
| Tokenomist (unlocks) | Pro $69/month annual: 1,000 calls, 1 year back/ahead, commercial licence DOC@2026-10-02 (https://tokenomist.ai/pricing) | schedules change; no versioning documented DOC@2026-10-02 (https://docs.tokenomist.ai/llms.txt) | commercial on Pro |
| Whale Alert | $29.95/month alert stream (14 chains, stablecoin mints/burns), personal use; free archive of past alerts DOC@2026-10-02 (https://developer.whale-alert.io/) | alert archive | personal |
| Etherscan V2 / RPCs | Etherscan free 3/s on selected chains, non-commercial; Alchemy 30M CU, Infura 3M credits/day, QuickNode 10M credits free DOC@2026-10-02 (https://etherscan.io/apis ; https://www.alchemy.com/pricing ; https://www.infura.io/pricing ; https://www.quicknode.com/pricing) | raw chain data is PIT by block time; use finalised blocks (Ethereum ~12.8 min) DOC@2026-10-02 (https://ethereum.org/en/developers/docs/consensus-mechanisms/pos/) | per provider |

### 2.6 Copy trading and smart money

- **Hyperliquid, fully public.** Any address's positions, orders, fills, funding and sub-accounts are readable without auth; the
  leaderboard is an undocumented public JSON (~39 MB, 46,954 rows: account value and PnL/ROI/volume by day/week/month/all-time);
  user-specific WebSocket subscriptions are capped at 10 users per IP DOC@2026-10-02
  (https://stats-data.hyperliquid.xyz/Mainnet/leaderboard ; https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/rate-limits-and-user-limits).
  History: no official archive of positions; record forward or buy (CoinGlass whale endpoints for positions above $1M, not on
  the Hobbyist tier; Nansen's Hyperliquid leaderboard, reliable from May 2025) DOC@2026-10-02
  (https://docs.coinglass.com/reference/hyperliquid-whale-alert ; https://docs.nansen.ai/api/hyperliquid/hyperliquid-leaderboard).
- **Hyperliquid vaults** (the venue's own copy mechanism; legacy HyperCore vaults: 10% profit share, 1-day lockup) DOC@2026-10-02
  (https://hyperliquid.gitbook.io/hyperliquid-docs/hypercore/vaults/for-vault-depositors-legacy). Sub-leg computation on the
  public vault feed: 72% of 3,101 open vaults are negative all-time, but 66% of the 168 with ≥$20k TVL are positive — capital
  sits in survivors DOC@2026-10-02 (https://stats-data.hyperliquid.xyz/Mainnet/vaults).
- **Binance and Bybit copy trading.** Their copy-trading APIs only serve the caller's own lead-trader account; nothing reads other
  traders DOC@2026-10-02 (https://developers.binance.com/docs/copy_trading/future-copy-trading ;
  https://bybit-exchange.github.io/docs/v5/copytrade). Binance's terms forbid bots and crawlers SECONDARY (search-index excerpt of
  https://www.binance.com/en/terms). Profit share up to 30% (Binance futures), 10–15% (Bybit) DOC@2026-10-02
  (https://www.bybitglobal.com/en/help-center/article/Copy-Trading-Profit-Sharing-Explained).
- **Insider Form 4, congressional, 13F (US equities).** Free and point-in-time via EDGAR (§2.3); OpenInsider's terms forbid
  commercial reuse DOC@2026-10-02 (http://openinsider.com/terms.txt). Only aggregate insider buying and crypto-treasury company
  filings plausibly touch this system's instruments (INFERENCE).

### 2.7 Prediction markets as signals and as venues

- **Polymarket API**: CLOB, Gamma, Data API and WebSockets; automated trading and market making are supported; servers in AWS
  eu-west-2; prices-history takes an interval and a minute fidelity (look-back UNKNOWN); maker rebates and liquidity rewards on
  fee-enabled markets DOC@2026-10-02 (archived 2026-09-16/18: https://docs.polymarket.com/api-reference/markets/get-prices-history ;
  https://docs.polymarket.com/trading/market-making). Rebate share 20% of taker fees in crypto markets, 25% in most others
  SECONDARY (https://startpolymarket.com/learn/polymarket-rebates-rewards/, dated 2026-09-12). Client order id UNKNOWN. The full
  trade history is on-chain DOC@2026-10-02 (https://arxiv.org/abs/2508.03474).
- **Kalshi API**: REST, WebSocket and FIX; demo environment; client_order_id; per-series fee model (formula UNKNOWN);
  candlesticks of 1/60/1,440 minutes under /historical for old markets DOC@2026-10-02 (https://docs.kalshi.com/llms.txt).
- **As signals**: Kalshi macro markets give continuously updated, distributional forecasts of rates and inflation that a Fed
  note compares with surveys and market-based measures DOC@2026-10-02
  (https://www.federalreserve.gov/econres/feds/kalshi-and-the-rise-of-macro-markets.htm). Reading public prices is not trading
  on the venue (INFERENCE); the Belgian resolver block above shows even reading can be ISP-blocked.
- **Others**: Manifold is play money and allows bots DOC@2026-10-02 (https://docs.manifold.markets/api); Betfair, Metaculus API,
  PredictIt status UNKNOWN.

## 3. Evidence — what survived out of sample and after costs

### 3.1 LLM- and news-driven trading
- Lopez-Lira & Tang (arXiv 2304.07619 v6, Oct 2025): GPT-4 headline scores, Oct 2021–May 2024 (after the model's cutoff),
  4,123 US stocks. Tradable drift 34 bp/day for overnight news (Sharpe ~2.97) and 50 bp/day for intraday news entered 15 minutes
  late, before costs; profitable at 10 bp round trip, unprofitable at 20 bp; concentrated in small caps, negative news and the
  short leg; Sharpe fell from 6.54 (2021Q4) to 3.68, 2.33 and 1.22 (Jan–May 2024) DOC@2026-10-02 (https://arxiv.org/html/2304.07619v6).
- Kirtac & Germano (Finance Research Letters 2024): OPT sentiment on 965k US news items, Sharpe 3.05 after 10 bp costs over
  Aug 2021–Jul 2023 DOC@2026-10-02 (https://researchonline.lse.ac.uk/id/eprint/122592/). Chen, Kelly & Xiu remain a working paper
  claiming after-cost Sharpe ratios across 16 markets, with slower decay in small stocks SECONDARY (search index of SSRN 4416687).
- Speed: in the 50 most liquid S&P 500 stocks (2008–2020) trading on earnings releases starts ~15 ms after release; returns per
  trade fall from 0.72% (real spreads) to 0.41% with a 5-second delay and are insignificant beyond; since 2016 any delay earns
  nothing DOC@2026-10-02 (https://arxiv.org/html/2601.08962v2).
- Look-ahead: GPT-4o reproduces pre-cutoff macro and market figures almost exactly and collapses after its cutoff; prompting it
  to ignore later data does not work DOC@2026-10-02 (https://arxiv.org/html/2504.14765v2). Published LLM agents' backtest returns
  collapse once moved past the knowledge cutoff ("profit mirage") DOC@2026-10-02 (https://arxiv.org/abs/2510.07920). Point-in-time
  models disagree on the size of the bias (ChronoGPT modest, DatedGPT significant) DOC@2026-10-02
  (https://arxiv.org/abs/2502.21206 ; https://arxiv.org/abs/2603.11838).
- Agents: FINSABER (KDD 2026) re-ran FinMem/FinAgent over 2004–2024 and 63–91 stocks with commissions — the advantages vanish
  (alpha p > 0.34); agents are too cautious in bull and too aggressive in bear markets DOC@2026-10-02 (https://arxiv.org/html/2505.07078v6).
  StockBench: most of 14 LLMs failed to beat buy-and-hold (Mar–Jun 2025) DOC@2026-10-02 (https://arxiv.org/html/2510.02209v2).
  AI-Trader live crypto (1–14 Nov 2025): all six agents lost 12–19% against an index down 14.3% DOC@2026-10-02
  (https://arxiv.org/html/2512.10971v1). Agent Market Arena: agent design matters far more than the LLM DOC@2026-10-02
  (https://arxiv.org/html/2510.11695v2). A 2026 survey of 19 core studies found two with a consistent time split and one with
  costs DOC@2026-10-02 (https://arxiv.org/html/2605.19337v1). Nof1 "Alpha Arena" results: UNKNOWN (site unreachable today).
- Crypto: momentum survives replication; the size effect disappears out of sample DOC@2026-10-02
  (https://ideas.repec.org/a/eee/riibaf/v83y2026ics0275531926000255.html); ML alpha sits in small, illiquid coins on the long
  side DOC@2026-10-02 (https://ideas.repec.org/a/eee/finana/v94y2024ics1057521924001765.html). Social sentiment predicts BTC only
  ~15 minutes ahead and too weakly to trade DOC@2026-10-02 (https://ideas.repec.org/a/eee/finlet/v38y2021ics1544612319314199.html);
  tweet counts predict volume and volatility, not returns DOC@2026-10-02 (https://ideas.repec.org/a/eee/ecolet/v174y2019icp118-122.html).
- INFERENCE: what survives is a 1–2 day drift after news in less-covered names, net of costs only for cheap executors, decaying
  as LLM use spreads; what fails is end-to-end LLM trading and anything decided in the first seconds. For this system the LLM
  should classify and synthesise events into recorded features, with the edge coming from horizon and coverage, not speed.

### 3.2 Structural premia and flows
- Crypto carry: averages over 10% a year and up to 60%, driven by retail leverage demand and scarce arbitrage capital; high carry
  predicts crashes DOC@2026-10-02 (https://www.bis.org/publ/work1087.htm). Perp deviations from no-arbitrage co-move across coins and
  shrink over time DOC@2026-10-02 (https://arxiv.org/abs/2212.06888). Sub-leg computation on Binance funding history: BTCUSDT paid
  11.96% (2024), 5.13% (2025), ~2.95% annualised (2026 YTD); negative prints 8.4% → 12.9% → 25.9%; ETHUSDT 12.96%, 4.93%,
  ~1.95% DOC@2026-10-02 (live https://fapi.binance.com/fapi/v1/fundingRate). Cause of the compression UNKNOWN.
- CME intraday flows: the first half-hour return predicts the last half-hour (SPY 1993–2013) DOC@2026-10-02
  (https://www.researchwithrutgers.org/en/publications/market-intraday-momentum/); across 60+ futures 1974–2020 the rest-of-day
  return predicts the last 30 minutes, driven by option-dealer gamma hedging and leveraged-ETF rebalancing, and reverts over
  following days DOC@2026-10-02 (https://pure.eur.nl/en/publications/hedging-demand-and-market-intraday-momentum/). The
  overnight-return variant fails out of sample (1997–2020) except in high-momentum regimes SECONDARY
  (https://law-journals-books.vlex.com/vid/understanding-intraday-momentum-strategies-1049461706). A noise-band intraday momentum
  rule on SPY reports 19.6%/year, Sharpe 1.33 net of costs, 2007–2024 (working paper) DOC@2026-10-02
  (https://www.sfi.ch/en/publications/n-24-97-beat-the-market-an-effective-intraday-momentum-strategy-for-s-p500-etf-spy). The
  pre-FOMC drift essentially disappeared after 2015 DOC@2026-10-02 (https://pmc.ncbi.nlm.nih.gov/articles/PMC7525326/).
  Bitcoin shows the same first/last half-hour pattern, attributed to liquidity provision DOC@2026-10-02
  (https://research.birmingham.ac.uk/en/publications/bitcoin-intraday-time-series-momentum/).
- Prediction markets: on 300k+ Kalshi contracts, prices are informative, cheap longshots lose after fees and makers out-earn
  takers DOC@2026-10-02 (https://www.karlwhelan.com/Papers/Kalshi.pdf); ~$40M of arbitrage was extracted on Polymarket
  DOC@2026-10-02 (https://arxiv.org/abs/2508.03474).

### 3.3 Event studies (the "temporary edge" family)
- DeFi hacks: governance tokens fall ~27% over 24 hours from the first on-chain hack transaction; ~36% of that happens before the
  public announcement, so most of it happens after DOC@2026-10-02 (https://www.newyorkfed.org/research/staff_reports/sr1153.html).
- Token unlocks (vendor, 16,000+ events, 40 tokens): pressure starts ~30 days before and accelerates in the last week; team
  unlocks are worst, investor unlocks barely move (OTC/TWAP), ecosystem unlocks slightly positive; sample period and data source
  not disclosed SECONDARY (https://keyrock.com/from-locked-to-liquidity-what-16000-token-unlocks-teach-us/).
- Binance Monitoring Tag (delisting-risk) announcements: repeated 5–34% drops within hours SECONDARY
  (https://cryptorank.io/news/feed/8450d-binance-monitoring-tag-nfp-delisting ; https://beincrypto.com/binance-monitoring-tag-delisting/).
- Listing announcements: studies of their size, capture speed and 2024–26 decay were not reached today (UNKNOWN). Enforcement is
  on record: a Coinbase manager's tips funded over $1.1M of trading ahead of listings in at least 25 assets (2021–22)
  DOC@2026-10-02 (https://www.sec.gov/newsroom/press-releases/2022-127).
- Stablecoin mints: no verified out-of-sample predictability; issuance tends to follow price drops DOC@2026-10-02
  (https://ideas.repec.org/a/eee/ecolet/v171y2018icp19-22.html ; https://ideas.repec.org/a/eee/finlet/v43y2021ics1544612321000726.html);
  the supply-push result is 2017-specific DOC@2026-10-02 (https://ideas.repec.org/a/bla/jfinan/v75y2020i4p1913-1964.html).
- Whale/exchange flows: large BTC transfers show significant effects only when split by size and direction (event study)
  DOC@2026-10-02 (https://ideas.repec.org/a/eee/finlet/v39y2021ics1544612320304438.html); an exchange-balance strategy that looked
  competitive on revised data did noticeably worse on point-in-time data SECONDARY (vendor:
  https://research.glassnode.com/why-use-point-in-time-data/).

### 3.4 Copy trading and smart money
- Copiers: a scraped 90-day study of 1,486 lead traders on three exchanges found 97% of leaders positive on their own books but
  only 44% positive for followers; 48% of copiers profitable SECONDARY (unlicensed vendor: https://yieldfund.com/is-copy-trading-profitable-a-90-day-multi-exchange-study/).
  In a lab market, a copy option raised risk-taking DOC@2026-10-02 (https://doi.org/10.1287/mnsc.2019.3508); performance fees push
  signal providers toward lottery-like bets DOC@2026-10-02 (https://doi.org/10.1007/s11846-022-00560-6).
- Famous wallets: the Hyperliquid wallet that shorted >$1B before the 10 Oct 2025 tariff post later lost ~$250M SECONDARY
  (https://www.theblock.co/post/387927/infamous-hyperunit-whale-exits-entire-eth-position-for-250-million-loss-left-with-53-in-account-arkham);
  House Oversight asked Hyperliquid Labs about insider controls on 2026-09-29 DOC@2026-10-02
  (https://oversight.house.gov/release/comer-continues-investigation-into-insider-trading-on-online-prediction-market-platforms/).
- Insiders: opportunistic Form 4 trades earned ~82 bp/month, routine ones nothing DOC@2026-10-02 (https://www.nber.org/papers/w16454);
  ~17% of purchase-portfolio alpha arrives within 5 days of the trade and a third within the first month DOC@2026-10-02
  (https://www.nber.org/papers/w6913), so part is gone before the filing is public (INFERENCE); aggregate insider trading
  predicts market returns DOC@2026-10-02
  (https://doi.org/10.2139/ssrn.970987).
- Congress: senators' purchases 2012–2020 underperformed DOC@2026-10-02 (https://www.nber.org/papers/w26975); future leaders beat
  peers by 47 pp/year but nobody tested capture after the disclosure lag DOC@2026-10-02 (https://www.nber.org/papers/w34524).
  13F copycats: returns appear right after disclosure with little long-term benefit DOC@2026-10-02 (https://doi.org/10.2139/ssrn.1683628).
- On-chain "smart money" labels: no independent evidence found that following them pays after delay and costs (UNKNOWN).

### 3.5 LLM forecasting versus markets
- A retrieval-augmented LLM scored Brier 0.179 against the crowd's 0.149 (blend 0.146) DOC@2026-10-02 (https://arxiv.org/html/2402.18563);
  ForecastBench: experts beat the best LLM (p < 0.001, v5 Feb 2025) DOC@2026-10-02 (https://arxiv.org/abs/2409.19839); an LLM
  ensemble matched a 925-person crowd on 31 questions DOC@2026-10-02 (https://arxiv.org/abs/2402.19379). LiveTradeBench: 21 LLMs on
  Polymarket for 50 days ranged from −57.6% to +20.5% DOC@2026-10-02 (https://arxiv.org/html/2511.03628v1). Verified evidence that
  LLM forecasts beat market prices after fees: none found (UNKNOWN).

## 4. Ranked edges for this system (INFERENCE throughout)

Ranking weighs evidence quality, independence from latency, data cost and honest history, legal venue fit for an EU resident,
fit with LLM breadth, and decay. "Venue" names where the trade can legally sit; "History" says whether an honest backtest exists.

| # | Edge (temporary / durable) | Mechanism and evidence | Expected decay | Minimum data · honest history? | Venue fit (EU resident) | Capital / latency | Main way it fails |
|---|---|---|---|---|---|---|---|
| 1 | On-chain incidents — exploits, hacks, depegs → exit or short the affected token (temporary) | processing public chain data is costly; governance tokens fall ~27% in 24 h and most of it after the public announcement (NY Fed SR 1153) | pre-announcement share grows as monitors multiply; the post-announcement tail should outlast it | chain RPC/indexer free tiers, DefiLlama TVL (free), first-seen tape of alerts · chain history YES; announcement times only from our own recording | short via OKX Europe / Kraken perps when listed; Hyperliquid OWNER-DECISION; long-only exit on Revolut X | small size; minutes | false positives, no perp or a halted market, recovered funds squeeze shorts, thin liquidity |
| 2 | Unlock schedules classified by recipient type (temporary, scheduled) | predictable supply plus recipient hedging; pressure starts ~30 days before, team unlocks worst, investor unlocks muted (vendor study, not replicated) | front-running moves earlier; OTC/hedged unlocks dilute it | Tokenomist Pro ($69/month, 1 year back) or DefiLlama API ($250/month) + daily hashed snapshots; LLM reads vesting documents · PARTIAL (schedules are revised) | perps at OKX/Kraken; long-only filter on Revolut X | modest; days | crowded shorts paying funding, schedule changes, effect absent out of sample |
| 3 | Positioning and forced-flow features — funding extremes, OI shocks, liquidation cascades, long/short skew (durable-ish) | leverage demand and forced deleveraging overshoot; high carry predicts crashes (BIS WP 1087); other out-of-sample evidence UNKNOWN | public dashboards make it crowded | Binance archive metrics 2020-09→ (handle the 2026-06-25 timestamp shift), funding 2019→, Bybit OI and long/short 2020→ · YES except liquidations (record forward) | filter for long-only spot today; shorts at OKX/Kraken | low; hours | regime change, revisions, overfitting many free series |
| 4 | CME index-futures last-half-hour hedging-flow momentum (durable, structural) | dealer short-gamma and leveraged-ETF rebalancing (JFE 2018, 2021); 2024 working paper: Sharpe 1.33 net of costs on SPY; the overnight variant failed out of sample | published; 0DTE growth may change it (UNKNOWN) | Databento 1-minute OHLCV, 16+ years, usage-priced · YES | Zenit allows it, but in winter the 16:00 ET close is 22:00 GMT+1, when the firm starts liquidating — exit minutes early, check its DST convention; residency and ATAS-autonomy limits (§1) | prop capital for $49–659/month; minutes | trailing-drawdown breach, regime dependence, needs a short side |
| 5 | Delisting-risk flags and listing-process signals, after the first minutes (temporary) | Monitoring Tag notices preceded 5–34% drops within hours (SECONDARY); the first seconds belong to bots; multi-hour follow-through untested | fast | Binance announcement WebSocket (signed), Korean notice boards, Coinbase listings · prices YES, announcements only by recording | shorts often impossible (perps go too); exits on spot | low; minutes–hours | latency race, illiquidity, leaked-information rules |
| 6 | Funding carry, regime-conditional (durable premium, currently thin) | paid for supplying leverage; >10%/year historically and crash-prone; ~3% annualised on BTC in 2026 YTD with 26% negative prints | compressing | funding history and spot/perp prices · YES, free | OKX Europe or Kraken (spot + perps), or Revolut X spot + perps elsewhere | capital-heavy; latency-free | funding flips, venue failure, ADL or liquidation of the short leg |
| 7 | Hyperliquid public positioning as a feature, not a copy template | complete visibility of positions, leverage and liquidation prices on one venue; copy evidence negative, vaults survivor-selected | — | free info API and leaderboard JSON · forward-only (fills capped at 10,000; S3 node data requester-pays) | used as a signal elsewhere; trading there OWNER-DECISION | low; minutes | sub-account obfuscation, 10-user WebSocket cap, survivorship |
| 8 | LLM news-event classification, 1–2 day horizon, less-covered tokens (temporary) | equity analogue: post-cutoff drift in small caps and negative news; Sharpe 6.5 → 1.2 over 2021–24; gone at 20 bp costs | fastest | licensed news with first-seen times (Alpaca/Benzinga 2015→, Tiingo crawlDate, GDELT) · YES, but LLM labels are honest only after the cutoff or forward | any | low; minutes–hours | costs (alt spreads and fees ≥20 bp), look-ahead, decay |
| 9 | Prediction-market probabilities as features (Kalshi macro, Polymarket events) | markets beat LLMs on Brier; Kalshi macro markets give continuous rate and inflation distributions (Fed note) | — | Kalshi / Polymarket public APIs · YES (Kalshi /historical) | reading only; trading EXCLUDE or OWNER-DECISION | none | ISP blocks, thin markets |
| 10 | Macro and central-bank calendar as a risk filter, not alpha | liquid markets price releases in seconds; the pre-FOMC drift vanished after 2015 | — | BLS/BEA ICS, Fed/ECB RSS · YES | all; mandatory for Zenit's blackout | none | — |

Not ranked, on evidence or terms: copying exchange lead traders, congressional and 13F copying, social sentiment as a return signal
(at most a volatility input), Form 4 insider data (equities; at most an aggregate tilt for index futures).

## 5. Legal and terms-of-service flags

**EXCLUDE**
- Trading on non-public information — leaked listing decisions, private exploit disclosures, "tips" arriving in inbox material:
  insider dealing under MiCA Art. 89 (including cancelling or amending an order because of it), which Art. 86 applies to any
  person, on or off a trading platform, for acts in the EU or abroad, since 30 Dec 2024 DOC@2026-10-02
  (https://eur-lex.europa.eu/legal-content/EN/TXT/HTML/?uri=CELEX:32023R1114 ; https://www.esma.europa.eu/esmas-activities/digital-finance-and-innovation/markets-crypto-assets-regulation-mica).
- Any posting, amplification, wash trading, spoofing or coordinated activity meant to move prices: market manipulation under
  MiCA Art. 91, which names false or misleading information spread through social media DOC@2026-10-02 (same EUR-Lex URL). The
  agents get no publishing capability that touches markets.
- Scraping or AI processing against terms: X beyond its API and policy limits (and any model training on X content), Telegram
  channels for datasets/AI, Discord, Binance copy-trading pages, OpenInsider (§2.4, §2.6), and feeding crypto outlets' articles or
  RSS to an LLM — The Block forbids AI processing even to summarise (re-read today), Cointelegraph forbids AI operation on its
  content, CoinDesk and Decrypt forbid robots (§2.2). EU text-and-data-mining is permitted only where the rightholder has not
  reserved it in machine-readable form, and terms of service can reserve it DOC@2026-10-02
  (https://eur-lex.europa.eu/eli/dir/2019/790/oj/eng, Art. 4). Licensed feeds, GDELT and official sources replace them.
- VPN or other geo-evasion for Polymarket, Kalshi, Hyperliquid or any venue — each venue's terms ban it (§1).
- Copy trading at Zenit, and several Zenit accounts steered by one agent fleet where the T&Cs allow blocking (§1).
- Becoming a signal provider, lead trader or vault leader for third parties without a licence — portfolio management/advice
  questions under MiFID/MiCA (INFERENCE; ESMA copy-trading statements UNKNOWN).
- US congressional reports as a commercial input (5 U.S.C. §13107(c), SECONDARY above) — also no demonstrated edge after the lag.
- Latency races on headlines and listings: legal, but excluded on economics (§3.1).

**OWNER-DECISION**
- Live trading on Binance as an EU resident (no MiCA authorisation; reverse-solicitation scrutiny) (§1).
- Hyperliquid as a venue (unlicensed, terms put legality on the user, congressional scrutiny); reading its public data is fine.
- Prediction-market venues: likely EXCLUDE in BE/FR/DE/IT/PL/SK/ES/PT/HU/NL and under ESMA's retail statement; reading prices as
  signals is fine where reachable.
- Zenit: the owner's residency (BG/HR/MT/RO/SI refused); written confirmation that AI-authored code the owner holds counts as
  "own source code"; ATAS live autonomy stays blocked by order history (repository).
- Derivatives at all (perps for shorting, carry): OKX Europe Markets or Kraken CySEC are the clean routes; new live authority.
- Every paid data subscription (an operating cost in the economic score); Reddit until its terms are read; FRED, whose legal page
  bars use in connection with developing ML/LLM systems (§2.2) — prefer BLS/BEA/Fed primary releases.
- Wallet profiling: the EDPB adopted blockchain personal-data guidelines (02/2025) DOC@2026-10-02
  (https://www.edpb.europa.eu/our-work-tools/documents/public-consultations/2025/guidelines-022025-processing-personal-data_en);
  keep wallet analytics to addresses and aggregates, never to named individuals (INFERENCE).

## 6. Build implications (INFERENCE unless labelled)

- Start the tape now for forward-only sources; it needs no inference and its value compounds: exchange announcements through the
  official streams (first-seen time + content hash), Binance OI/long-short/liquidation streams and Bybit's allLiquidation,
  Hyperliquid leaderboard, vault and whale-position snapshots, unlock schedules (daily snapshot hashed with lastModified),
  headlines only from sources whose terms allow it (GDELT, licensed APIs, official releases) with first-seen time, on-chain labels
  with first-seen time, Kalshi/Polymarket books where lawful to read.
- Free history that can be ingested at once: Binance archive (funding, 5-min metrics from 2020-09, premium index, bookDepth),
  Bybit OI and long/short from 2020, Deribit DVOL and trades, EDGAR with acceptance timestamps, Fed/ECB texts, GDELT, chain data.
  Handle the reported 2026-06-25 Binance metrics timestamp shift explicitly.
- The v1 strategy language can express none of the top edges except a long-only slice of CME intraday momentum: it is one
  instrument, long-or-flat, OHLCV-only, bar-close evaluation (docs/STRATEGY-LANGUAGE.md). The smallest unlocks, in payoff order:
  recorded external series aligned to bars (funding, OI, event flags); a short side on perps; event-time triggers; a two-leg
  spot/perp pair for carry.
- Venue work worth doing first for an EU resident: a perps connector at OKX Europe or Kraken (both have demo/test environments and
  client ids; OKX's clOrdId reuse and both venues' history windows need the rule-2 treatment Hyperliquid also needs: a
  count/time-bounded history declared as such, plus TradeAgent's own order ledger).
- LLM-labelled features must be recorded at knowledge time with model id, cutoff and prompt hash; anything older than the model's
  cutoff is contaminated and is labelled so (§3.1). Triage at volume belongs to a cheap API model or deterministic filters: the
  repository's price table (read 2026-09-06) lists a nano-class model at $0.05 in / $0.40 out per million tokens
  (docs/RESEARCH-REQUIRED.md §D); attempt 2 of the observed run ended at a subscription plan limit.
- Economics: with modest capital a $199/month feed needs about 2%/month (≈24%/year) on $10k, or 0.2%/month on $100k, before it
  breaks even. Prefer free and archived sources until a forward paper result justifies a subscription.

## 7. UNKNOWN today, and how to close each
- Binance USDⓈ-M availability per EU country; Revolut X fees; OKX futures fees; whether Binance/Bybit demos accrue funding — read
  the venue pages from the owner's country.
- Reddit Data API terms and price; Farside terms; X full-archive price; Bybit data terms; data.binance.vision licence.
- Nof1 Alpha Arena standings; ForecastBench current gap; Metaculus bot-vs-pro results; an LLM-vs-market trading study after fees.
- Academic out-of-sample evidence on OI/liquidation/long-short signals, cross-exchange funding arbitrage, options-skew signals,
  token unlocks (beyond the vendor study), exchange-flow predictability, and the 10–11 Oct 2025 liquidation cascade (size, ADL).
- Kalshi fee formula and archive start; Polymarket prices-history look-back and client order id; Deribit EU access.
- ESMA or national statements on copy trading as advice; legal status of profiling wallets under GDPR.
