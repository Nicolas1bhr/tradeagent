# 06 — Competitive landscape and moats for autonomous AI trading (research leg, 2026-10-02)

Question from the owner: "we have put nothing in place that makes it a moat against the rest of the world."
Scope: who else does AI/agentic trading, which edges last, where a small system wins, which flywheels TradeAgent
could own (and what they cost legally), why these products fail — then five moats, ordered by durability.

**Labels.** `DOC` = primary source read on 2026-10-02 (i.e. DOC@2026-10-02). `SECONDARY` = press, review or
aggregator. `INFERENCE` = this leg's reasoning. `UNKNOWN` = not established. `REPO` = a fact read in this repository.
"(search summary)" = the figure came from a search-engine digest of that page, not from a direct read.
**Limits.** The session's shared WebSearch budget (200 calls) ran out mid-leg; later checks used direct fetches only.
Cloudflare blocked direct reads of Apex's and IBKR's own pages. Everything is paraphrased; nothing here is legal advice.

---

## 0. Bottom line

- **The plumbing became free in 2026.** Between March and September 2026 Binance, OKX, Bybit, Bitget, Robinhood, eToro
  and IBKR all shipped AI-agent access: MCP servers, walled "agent sub-accounts", in-app agents. Several name Claude Code
  and Codex, the very CLIs TradeAgent drives (§1). Connectivity, natural-language strategy authoring, a backtester,
  multi-agent debate and "no terminal" are therefore **not** moats. The owner could point the same CLIs straight at Binance.
- **What none of them ships is what TradeAgent already has:** an app-owned referee with a holdout and counted trials, a
  deterministic kernel between the model and the money, and P&L net of every cost including inference. The platforms
  disclaim supervision and leave all agent risk with the user (Robinhood says it does not supervise or audit agents;
  no independent audit of any exchange MCP implementation had been published by Aug 2026 — §1).
- **The evidence says per-decision LLM trading loses** (Alpha Arena), and **LLM-discovered strategies mostly fail honest
  testing** (an Aug-2026 paper rejected every one under leakage-safe, trial-counted evaluation — §5). An honest referee
  will reject most candidates. So the moat cannot be a strategy (strategies decay, §2). It has to be what the factory
  accumulates: time-stamped forward evidence, honest memory, verified venue knowledge and cost-to-outcome data.
- **Five moats (INFERENCE, most durable first):** (1) forward-evidence ledger nobody can backfill; (2) referee-standardised
  opt-in outcome network (owner decision); (3) private negative-results memory with search-aware statistics;
  (4) certified venues and rulebooks under the deterministic kernel; (5) research yield per AI dollar.
- **Hard flags:** Apex forbids AI/fully automated trading on funded and live accounts; Topstep allows API bots only from
  the trader's own device. Serving anyone but the owner (signals, strategies, managed execution) pulls in MiFID II / MiCA
  licensing, and MiCA's transition ended 1 July 2026 (§4).

---

## 1. Landscape, as of 2026-10-02

| Product | What it does, for whom | Price | Performance evidence | Autonomy | Source |
|---|---|---|---|---|---|
| **TradingAgents** (Tauric Research) | Multi-agent LLM "trading firm" (analysts, bull/bear debate, trader, risk); developers. 109.5k stars, 21.0k forks, v0.5.2 Sep 2026 | Free + own LLM keys | None audited; README says returns depend on model, temperature, dates and sampling and are not replicable | Research scaffold; orders go to a simulated exchange | DOC https://github.com/TauricResearch/TradingAgents |
| **ai-hedge-fund** (virattt) | 18 "legendary investor" persona agents emit signals; 63.8k stars | Free | None; states it makes no trades | Educational wrapper | DOC https://github.com/virattt/ai-hedge-fund |
| **Microsoft R&D-Agent(Q)** | LLM agents iterate factor + model research on Qlib; quants | Free | Backtest only (CSI 300): ~2x a classical factor library's annual return with 70% fewer factors; ARR 14.21%, IC 0.0532, IR 1.74, MDD -7.42% | Autonomous research, no execution | DOC abstract https://arxiv.org/abs/2505.15155; figures SECONDARY https://saulius.io/blog/automated-quant-research-ai-agents-rd-agent |
| **FinRL / FinRL-Trading; FinRobot** (AI4Finance) | DRL trading library with an Alpaca live layer (15.2k / 3.2k stars); LLM equity-research agents (7k+) | Free | Backtests; authors warn models decay across regimes | Libraries | SECONDARY https://pinggy.io/blog/best_ai_trading_agents/ |
| **Freqtrade** | Crypto bot + FreqAI ML; CCXT venues incl. Binance, Hyperliquid; 55k stars | Free | None; "educational purposes" disclaimer | Deterministic bot | DOC https://github.com/freqtrade/freqtrade |
| **Hummingbot + Condor** | Market-making/arbitrage, 50+ connectors, 20.3k stars; Condor wires LLM decisions to deterministic execution | Free | Claims $34B user volume in a year; no P&L | LLM-supervised deterministic bots — **closest architecture to TradeAgent** | DOC https://github.com/hummingbot/hummingbot |
| **QuantConnect** (LEAN, Mia, MCP) | Mia ideates, codes, backtests, paper-trades; MCP exposes 60+ endpoints incl. deploy-to-live; LEAN claims 300+ hedge funds | Free tier; paid seats/nodes (prices not listed) | None for Mia | Agentic research loop; live under user control | DOC https://www.quantconnect.com/docs/v2/ai-assistance/predefined-agents/mia ; SECONDARY (search summary) https://www.quantconnect.com/announcements/19439/quantconnect-mcp-server/ |
| **Binance Ai Pro** (beta 2026-03-25) | In-app agent: user sets parameters, AI executes spot/perps; ChatGPT, Claude, Qwen, MiniMax, Kimi | $9.99/mo beta, $29.99/mo | None; Binance disclaims advice and outcomes | Autonomous inside a no-withdrawal sub-account | DOC (Binance release) https://www.tradingview.com/news/reuters.com,2026-03-30:newsml_Zaw82qGSV:0-zawya-binance-beta-launches-binance-ai-pro-bringing-ai-agentic-trading-to-users/ |
| **Binance Agent OS** (2026-08-20) | MCP platform: Claude, Claude Code, Codex, ChatGPT, Cursor, VS Code trade spot/margin/futures via isolated sub-accounts | — | None; no loss control beyond sub-account size | External agents | SECONDARY https://crypto.news/binance-agent-os-ai-trading-safeguards/ |
| **OKX Agent Trade Kit** (2026-03-10) | Open-source MCP server + CLI, 80+ tools, demo mode, keys stay local | Free | None | External agents | SECONDARY https://coinalertnews.com/news/2026/03/10/okx-launches-ai-agent-trade-kit ; https://x.com/okx/status/2031765978531365092 |
| **Bybit** AI Skills (03-13), AI Subaccounts (06-24), Bybit AI (09-09) | 253 endpoints for six assistants; walled sub-accounts with leverage/withdrawal caps; in-app agent for spot/futures/options | — | None | Both | SECONDARY https://cryptonews.net/news/market/33055847/ ; https://chainwire.org/2026/03/13/bybit-launches-ai-skills-powering-ai-agents-for-crypto-trading-with-zero-setup-253-api-endpoints-and-growing/ |
| **Bitget GetAgent** (07-2025) + Playbook (06-2026) | Conversational analysis → strategy → execution | — | 450k+ users; >1M users made AI trades worth $1.2B — volume, not P&L | Assisted to autonomous | SECONDARY https://en.cryptonomist.ch/2026/04/30/bitget-and-ai-in-trading-nearly-half-a-million-users-for-the-new-infrastructure/ |
| **Robinhood** open to agents (2026-05-27); **Robinhood Agents** (2026-09-29) | Third-party agents via MCP into a dedicated agentic account; in-app agents that analyse, build strategies and trade around the clock; "Agent Apps" data $5–30/mo; free GPT-Luna use to end-2026; eligible US customers only | No core fee | None; Robinhood does not supervise, monitor or audit agents. 150k agentic accounts by Sep (search summary) | Autonomous; per-trade approval on by default, switchable off | DOC https://robinhood.com/us/en/newsroom/hood-summit-2026/ ; DOC https://robinhood.com/us/en/newsroom/robinhood-is-now-open-to-agents/ |
| **eToro Agent Portfolios** (2026-03-26) | Sub-portfolio from $200, scoped API key; Claude Code, Cursor, OpenClaw, Hermes Agent; eToro is CySEC-regulated in the EU, but the feature's regional availability is not stated | Normal spreads/fees | None; 51% of retail CFD accounts lose money (its warning) | External agents within limits | DOC https://www.etoro.com/news-and-analysis/etoro-updates/agent-portfolios-let-your-ai-agent-trade-for-you/ |
| **IBKR × Claude** (2026-06-01) | Research + trade instructions; the client approves each order in an "AI Instructions" tab | No extra cost | None | Human in the loop | SECONDARY https://fintech.global/2026/06/02/interactive-brokers-launches-agentic-trading-via-claude/ |
| **Hyperliquid vaults** | Anyone runs a vault; leader must keep ≥5% and takes 10% of profits; 10k USDC creation fee (legacy vaults) | — | Public on-chain P&L | Any strategy, any agent | DOC https://hyperliquid.gitbook.io/hyperliquid-docs/hypercore/vaults/for-vault-leaders-legacy |
| **3Commas** | DCA/grid/signal bots, AI assistant, TradingView webhooks | $20 / $50 / $140 per month | None audited; Dec-2022 API-key leak, ~$20M stolen | Rule bots | SECONDARY (search summary) https://coinbureau.com/review/3commas-review ; https://siliconangle.com/2022/12/29/crypto-trading-service-3commas-confirms-massive-api-key-leak-hack/ |
| **Cryptohopper** | Strategy marketplace; "Algorithm Intelligence" switches strategies by regime; free plan dropped June 2026 | Paid tiers | None audited | Rule bots + selector | SECONDARY (search summary) https://bagengine.com/articles/cryptohopper-review |
| **Pionex** | Exchange with 16 built-in bots, 0.05% fee; PionexGPT writes bot configs | Free bots | None | Parameter recommender, not adaptive | SECONDARY https://gainium.io/review/pionex |
| **Composer** (SoFi since June 2026) | No-code equity strategies; AI turns prose into editable rules; automated execution | $40/mo or $384/yr | Backtests labelled hypothetical | User-designed rules | SECONDARY https://www.therundown.ai/tools/composer |
| **Numerai** | Crowd models on obfuscated data, staked in NMR, blended into a meta-model running a global equity fund | Free to enter | 2024 +25.45% net, Sharpe 2.75; AUM $60M→$450M (Aug 2025); JPMorgan $500M capacity; 2025 +8% net to Oct; $30M Series C at $500M (Nov 2025) | Crowd → fund | DOC https://blog.numer.ai/jpmorgan-secures-500m-capacity/ ; SECONDARY https://fintech.global/2025/11/24/numerai-lands-30m-to-scale-ai-powered-hedge-fund/ |
| **WorldQuant BRAIN** | Alpha-simulation platform; 550k+ users, 16k+ paid consultants, 400k+ data fields | Free; consultants paid | n/a (feeds WorldQuant) | Crowd research | DOC https://www.worldquant.com/brain/ |
| **Taoshi Vanta** (Bittensor SN8) | Miners submit signals scored on-chain; "Vanta Trading" prop evaluation launched Feb 2026 | Token incentives | On-chain scoring; no audited fund | Crowd signals | SECONDARY https://finance.yahoo.com/news/taoshi-announces-vanta-trading-decentralized-150000841.html |
| **Darwinex Zero** | Traders build a verified, investable track record (DARWIN); virtual then real allocations; FCA-regulated | €45/mo; 15% performance fee | Platform-verified track records | Human or algo | DOC https://www.darwinexzero.com/ |
| **Prop firms** | Topstep: API bots allowed (TopstepX/ProjectX, $29/mo), no HFT, trading must originate from the trader's own device (no VPS/VPN). Apex: AI, bots, algorithms and fully automated systems prohibited on PA and live accounts; semi-automation only while actively monitored; violation forfeits funds | — | Topstep 2025: 16.8% of Combines passed; 33.3% of funded traders got a payout; 0.71% called up to live | Varies | DOC https://help.topstep.com/en/articles/11187768-topstepx-api-access ; DOC https://www.topstep.com/our-program ; DOC (search-indexed, fetch blocked) https://support.apextraderfunding.com/hc/en-us/articles/31519788944411-Performance-Account-PA-and-Compliance |
| **Nof1 Alpha Arena** | S1 (late 2025): six LLMs, $10k real each, Hyperliquid perps — Qwen3 Max +22.3%, DeepSeek +4.9%, four lost >40%. S1.5 (US equities, Dec 2025): 32 accounts, 6 profitable, about a third of capital lost, one model placed 1,418 trades in a round; the founder concluded direct LLM trading does not work yet | — | Real money, short windows | LLM in the order loop | SECONDARY https://www.traderank.ai/blog/alpha-arena-alternatives-2026 ; https://finance.biggo.com/news/5BSX_Z0BaoGGrU-ID27J ; DOC https://nof1.ai (now a lab with unreleased models) |
| **DeFi agents** | Giza retired its ARMA/Pulse agents (Feb–Mar 2026), funds returned; Theoriq's AI vault drew ~$100M TVL in a month | — | No verified P&L | Autonomous on-chain | SECONDARY https://ownyourmind.ai/projects/giza/ ; https://coinmarketcap.com/cmc-ai/theoriq/latest-updates/ |

**Takeaways (INFERENCE).**
- Two architectures: (a) LLM in the order loop — Alpha Arena, most exchange agents — where the evidence is poor; (b) LLM in
  the research loop, deterministic execution — R&D-Agent(Q), QuantConnect Mia, Hummingbot Condor, TradeAgent. TradeAgent is
  on the better side, but free and well-funded competitors are there too. Live benchmarks agree the framework around the
  model matters more than the model (DOC https://arxiv.org/abs/2510.11695).
- **Nobody in the table publishes counted trials, holdout discipline or P&L net of inference.** The only records that are
  verifiable by construction are on-chain vaults, Numerai's live rounds, Darwinex and prop-firm funnels. Verified evidence
  is the scarce good.
- Venue-native agents are paid by volume (Bitget reports AI volume, not P&L); TradeAgent is scored net of all costs, so it
  is aligned with the owner. That is positioning, not a moat.

---

## 2. Which edges last, which decay

**Durable, with evidence**
- **A research factory with multiple-testing discipline.** López de Prado: funds that work run an assembly line of
  independently measured stations and deflate Sharpe for the number of trials; lone "Sisyphus" researchers produce
  overfit backtests (SECONDARY https://quantpedia.com/why-machine-learning-funds-fail/, SSRN 3104816). With microcaps
  controlled, 65% of 452 published anomalies fail a t=1.96 test and 82% fail a 2.78 multiple-test hurdle
  (DOC https://academic.oup.com/rfs/article-abstract/33/5/2019/5236964).
- **Crowd + data that cannot leave + skin in the game.** Numerai's obfuscated data makes models useless outside its
  tournament (SECONDARY search summary of docs.numer.ai FAQ); payouts weight contribution to the meta-model above raw
  correlation (2026: 0.75×CORR + 2.25×MMC, capped ±5% of stake per round — SECONDARY search summary of
  https://blog.numer.ai/numerai-monthly-numercon-speakers-new-dataset-target-2026-payout-updates/). That rewards
  orthogonality, which is what keeps a crowd from crowding itself.
- **Speed and infrastructure, for short horizons only.** Being slow cost a mean-reversion alpha 5.6%/yr in the US and
  9.9% in Europe, rising ~36/16 bp a year (DOC https://www.mavensecurities.com/alpha-decay-what-does-it-look-like-and-what-does-it-mean-for-systematic-traders/).
- **Capacity discipline.** Medallion is held near $10B and pays out profits because its edge does not scale
  (SECONDARY https://pwlcapital.com/renaissance-technologies-medallion-fund-an-exception-to-the-indexing-rule/).
- **Licences and verified track records.** Only 213 CASPs held MiCA authorisation on 29 June 2026
  (SECONDARY https://www.elliptic.co/blog/the-end-of-micas-transitional-period); Darwinex sells the verified record itself.
- **AI alone is not an edge.** A 2026 Neudata survey (191 respondents): 17% credit AI with better investment performance,
  41% with efficiency (SECONDARY https://alternativefundinsight.com/hedge-funds-struggle-to-turn-ai-adoption-into-alpha/).

**Decays fastest**
- Published anomalies: returns 26% lower out-of-sample and 58% lower after publication
  (DOC https://onlinelibrary.wiley.com/doi/abs/10.1111/jofi.12365).
- LLM-read news: drift predictability declines as LLM adoption rises (DOC https://arxiv.org/abs/2304.07619, v6 2025-10-28).
- Homogenised AI signals: a 2026 working paper models signal half-lives of ~18 months vs 5–7 years pre-AI and finds 13F
  portfolio convergence up 42% over 2013–24 — a model estimate, not a measurement (SECONDARY https://arxiv.org/abs/2605.23905).
- Simple TA and public-API sentiment: INFERENCE, consistent with all of the above; no direct 2026 measurement found.
- Crowds without honest evaluation: Quantopian closed in 2020 (SECONDARY https://www.quantrocket.com/blog/quantopian-shutting-down/).

---

## 3. Where one laptop with modest capital can win

- **Capacity.** Fund size erodes performance, more so for small-cap funds (DOC
  https://www.aeaweb.org/articles?id=10.1257%2F0002828043052277). Many anomalies live in microcaps (Hou-Xue-Zhang above):
  too small for institutions, possibly tradable for a small account after costs. INFERENCE: the edge is in niches too small
  to matter to big firms, not in beating them at their own game.
- **Cheap, broad reading.** LLM-scored news predicts drift mainly in small stocks and after bad news, and the effect fades
  where adoption is high (DOC arXiv 2304.07619). The price of a fixed level of LLM capability fell 9×–900× a year,
  depending on task (DOC https://epoch.ai/data-insights/llm-inference-price-trends). INFERENCE: neglected corners and
  breadth, not headline names.
- **No redemption or career risk.** Professional arbitrage runs on other people's money, which leaves just when a
  mispricing widens (DOC https://onlinelibrary.wiley.com/doi/abs/10.1111/j.1540-6261.1997.tb03807.x). INFERENCE: a
  self-funded system can hold positions a fund cannot, but only inside its own loss envelope.
- **24/7 crypto and patience.** INFERENCE: thin long-tail books cap institutional size but not a small account's. No
  current measurement found (the Kaiko notes reached were from 2023).
- **Where not to compete:** latency and short-horizon mean reversion (Maven), crowded factors, and per-decision LLM
  trading (Alpha Arena).
- **A trap specific to LLM research.** Models recall pre-cutoff prices and returns even when told not to; after the
  cutoff the recall vanishes (DOC https://arxiv.org/abs/2504.14765). INFERENCE: an agent can write rules that fit a past
  it remembers. TradeAgent's holdout protects verdicts only if it post-dates the authoring model's training cutoff —
  UNKNOWN for the current 12-month archive (REPO `src/TradeAgent.Core/Data/Holdout.cs`: the holdout is a time cutoff).

---

## 4. Flywheels TradeAgent could build, and what they cost legally

| Option | What accumulates | Why it compounds | Main risk |
|---|---|---|---|
| A. Opt-in pooled **outcome cards** (outcomes of referee tests, not strategies) | Family descriptors, venue, regime tag, counted trials, verdict, forward-vs-backtest decay, cost per verdict | Every install gets better priors on where to spend trials; comparable only because one app-owned protocol produced every card | Re-identification; needs users; MAR if published as suggestions |
| B. Shared **venue-semantics library** (certified connectors, prop-firm rulebooks as data) | Verified per-venue facts: coid round-trip, history depth, rejection semantics, rule clauses | Each venue proven once serves every install | Copyable (cf. CCXT, Hummingbot's 50+ connectors); upkeep |
| C. **Signal marketplace / copy trading** | Followers, leader track records | Classic two-sided network | Licensing (below); crowding kills the signals; Quantopian's fate |
| D. **Track-record certification** for allocators or prop firms (Darwinex-like) | Sealed forward records | Allocators pay for verified records | Becomes an investment service if TradeAgent places capital |
| E. Shared **agent tool library** (indicators, research helpers) | Reusable code | Cheaper research per install | Low durability; open source does it free |

INFERENCE: A and B fit the product (protocol-standardised, no advice); C is the most regulated and the most
crowding-prone; D only after a long sealed record exists; E is not a moat.

**Regulatory flags (high level; OWNER-DECISION; take counsel in the owner's member state)**
- **Own account.** MiFID II exempts dealing on own account, but not for anyone using a high-frequency algorithmic
  technique or providing other investment services (DOC https://www.esma.europa.eu/publications-and-data/interactive-single-rulebook/mifid-ii/article-2-exemptions ;
  SECONDARY https://www.corporatesolutions.euronext.com/blog/mifid-ii/dealing-on-own-account). INFERENCE: the owner running
  TradeAgent on the owner's own money is generally outside licensing (nuances for commodity derivatives: UNKNOWN, ask counsel);
  prop-firm contracts are a separate, contractual constraint (§1).
- **Anything for third parties.** ESMA (30 Mar 2023) treats copy trading as portfolio management or investment advice
  under MiFID II (DOC https://www.esma.europa.eu/press-news/esma-news/esma-provides-guidance-supervision-copy-trading-services).
  For crypto, ESMA Q&A 2463 applies the same reasoning: auto-executing someone else's signals is typically portfolio
  management of crypto-assets, which needs CASP authorisation (SECONDARY https://www.skadden.com/insights/publications/2025/05/update-on-mica-implementation).
  MiCA's transitional periods ended by 1 July 2026; unauthorised providers must stop (DOC
  https://www.esma.europa.eu/sites/default/files/2026-04/ESMA75-113276571-1679_Statement_on_the_end_of_transitional_periods_under_MiCA.pdf).
  INFERENCE: shipping "default strategies" with the product, or running agents on customers' money, risks the same
  qualification; selling software the customer configures may not — that line needs a lawyer.
- **Publishing any strategy suggestion** (a marketplace, a public leaderboard): MAR Art. 20 requires objective
  presentation and conflict disclosure from anyone, regulated or not (SECONDARY
  https://www.amf-france.org/en/news-publications/news-releases/amf-news-releases/investment-recommendations-social-media-amf-backs-esmas-reminder).
- **Pooled data.** Pseudonymised data stays personal data; only data that is anonymous under the "means reasonably
  likely to be used" test leaves the GDPR (DOC https://gdpr-info.eu/recitals/no-26/).
- **AI inside an investment service.** ESMA (30 May 2024): MiFID duties apply in full when AI is used
  (DOC https://www.esma.europa.eu/press-news/esma-news/esma-provides-guidance-firms-using-artificial-intelligence-investment-services).
- UNKNOWN (not researched): EU AI Act obligations; national rules on selling trading software; US rules (SEC "AI washing"
  is an exam priority for 2026 — SECONDARY https://www.goodwinlaw.com/en/insights/publications/2025/12/alerts-privateequity-pif-2026-sec-exam-priorities-for-registered-investment-advisers).

---

## 5. Why retail AI trading products fail

1. **Backtests that predict nothing.** Across 888 Quantopian algorithms, backtest Sharpe explained almost none of the
   out-of-sample result (R² < 0.025) (DOC https://www.pm-research.com/content/iijinvest/25/3/69). Under leakage-safe tools,
   deflated Sharpe and trial counting, an Aug-2026 study rejected **every** LLM-discovered strategy across models, budgets of
   up to 100 candidates and five reruns (DOC https://arxiv.org/abs/2608.27734).
2. **Look-ahead and memorisation.** LLMs recall pre-cutoff data (DOC arXiv 2504.14765). The most-starred framework's
   v0.3.1 release lists a look-ahead filtering fix for one of its data sources (SECONDARY, search summary of
   https://aitoolly.com/ai-news/article/2026-05-04-tradingagents-tauricresearch-launches-multi-agent-llm-framework-for-financial-trading).
3. **Benchmarks are not trading.** LMArena scores did not predict 50 days of live trading (DOC https://arxiv.org/abs/2511.03628);
   most agents failed to beat buy-and-hold (DOC https://arxiv.org/abs/2510.02209); over two decades and 100+ symbols the
   reported LLM edge disappeared — too timid in bull markets, too aggressive in bear markets (DOC https://arxiv.org/abs/2505.07078);
   a 2026 survey finds capability does not reliably become live performance (DOC https://arxiv.org/abs/2608.31041).
4. **Overtrading and costs.** Alpha Arena S1.5: 6 of 32 accounts profitable, about a third of capital lost, one model at
   1,418 trades in a round (SECONDARY biggo, above).
5. **Base rates.** 97% of Brazilian day traders who persisted 300+ days lost money (DOC
   https://papers.ssrn.com/sol3/papers.cfm?abstract_id=3423101); Topstep's 2025 funnel: 16.8% pass, 33.3% of funded
   traders paid, 0.71% reach live (DOC above).
6. **Security and custody.** The 3Commas API-key leak (Dec 2022, ~$20M). No independent audit of exchange MCP
   implementations had been published by Aug 2026 (SECONDARY crypto.news, above).
7. **Marketing ahead of evidence.** The CFTC warned that AI cannot turn trading bots into money machines (2024, SECONDARY
   https://datamatters.sidley.com/2024/02/07/u-s-cftc-seeks-public-input-on-use-of-artificial-intelligence-in-commodity-markets-and-simultaneously-warns-of-ai-scams/);
   the SEC has pursued "AI washing" since the Delphia and Global Predictions cases (Mar 2024) (SECONDARY
   https://www.sidley.com/en/insights/newsupdates/2025/10/2025-fiscal-year-in-review-sec-enforcement-against-investment-advisers).
8. **Liability vacuum.** Platform terms put all agent risk on the user (SECONDARY crypto.news); Robinhood says it does not
   supervise or audit agents (DOC). INFERENCE: the first large agent incident will reward whoever can show a kernel and a ledger.
9. **Business models that don't close.** Quantopian (2020), Giza's ARMA wind-down (2026), Cryptohopper ending its free
   plan (2026) — SECONDARY, above.
10. **Platform rules.** An unattended bot on an Apex funded account forfeits the account (DOC search-indexed, above).

---

## 6. Five moats TradeAgent could realistically build (INFERENCE)

Ordering criterion: how long a well-funded competitor starting today would need to match it, discounted by how easily
it is lost. **Build order is different** (see the end of this section).

### M1. A forward-evidence ledger nobody can backfill — most durable, unconditional
- **Accumulates:** for every frozen version (hash), the trials counted before it, the referee's verdict, paper-forward fills
  and P&L after declared costs, live fills when authorised, and the AI, data and fee costs attributed to it — sealed daily,
  so no row can be added or changed after the fact without detection.
- **Why a late competitor cannot copy it quickly:** forward time cannot be bought. An externally timestamped record proves it
  was not written later. Allocators and gatekeepers price exactly this (Darwinex sells the verified record; Topstep's funnel
  is a record gate; Numerai scores live rounds only). No product in §1 shows trials or inference cost beside P&L.
- **Seeds already in TradeAgent:** scoring policy V1 binds evidence to the version hash, dataset hash, execution model,
  interpreter build and policy hash, and requires forward evidence before capital (REPO `src/TradeAgent.Core/Db/CampaignStore.cs`);
  the referee charges a verdict before computing it (REPO `src/TradeAgent.Core/Strategy/Referee.cs`); forward bars and runs,
  fills, AI-attempt costs (REPO `Db/ForwardBarStore.cs`, `Gateway/ForwardRuns.cs`, `Db/FillStore.cs`, `Db/AiAttemptStore.cs`);
  "unknown is never zero" (docs/PRINCIPLES.md).
- **First concrete build:** an append-only hash chain over evidence rows (verdict, promotion, allocation, forward fill, cost),
  with each day's head hash timestamped externally (RFC 3161 or OpenTimestamps: only a hash leaves the machine) and shown
  in the daily report, plus an exportable track-record card a third party can check against the chain. A repo grep found no
  hash chaining today (REPO).

### M2. A referee-standardised outcome network — highest ceiling, conditional (OWNER-DECISION)
- **Accumulates:** opt-in outcome cards (option A in §4) from many installs. Each install gets priors on which families
  survive, on which venue and in which regime, before it spends trials.
- **Why hard to copy:** the value exists only because every card came from the same hashed protocol, not from agents grading
  themselves. Pooled self-reported backtests are close to noise (Wiecki). Network effects compound. Numerai shows how to stop
  a crowd crowding itself: reward contribution to the whole (its 2026 MMC weight is three times its CORR weight), not raw returns.
- **Seeds:** the policy hash on every campaign row; canonical strategy identity (REPO `Strategy/StrategyCanonical.cs`); the
  measurement/claim split in the ledgers (CLAUDE.md); a daily report written from measured facts (REPO `Gateway/DailyReports.cs`).
- **First concrete build:** local only. Define the card schema; generate cards for the owner's own campaigns; show a "what
  would be shared" preview; refuse any card that is unique in its bucket (k-anonymity). No upload until counsel has looked
  at MAR Art. 20 and GDPR Recital 26. Sharing outcomes rather than signals is designed to stay clear of advice and
  portfolio management — that is INFERENCE and needs confirming.
- **Caveat:** one install has no network effect. This is a later option, not current work.

### M3. Private negative-results memory with search-aware statistics — compounding switching cost
- **Accumulates:** every hypothesis tried, its family, the trials it consumed, why it died and what it cost to reach the
  verdict. The factory builds its own map of dead ends per venue and regime, and stops paying twice for them.
- **Why hard to copy:** it needs time plus honesty. A competitor without counted trials accumulates false positives, not
  knowledge; the 2026 honest-evaluation study shows how few candidates survive. The memory also stays with the owner's install.
- **Seeds:** a campaign-wide trial budget that survives a team's replacement; versions with a declared parent (REPO
  `CampaignStore.cs`, `Strategy/StrategyVersions.cs`); the material ledger; an app-owned research library (REPO
  `AgentRuntime/ResearchLibrary.cs`); the durable-memory clause in docs/PRINCIPLES.md.
- **First concrete build:** a read-only "prior attempts" tool. Given a draft, the app returns the nearest judged versions
  (same instrument, timeframe and indicator family) with their verdicts, trials and costs. The referee also reports a
  Deflated Sharpe that uses the lineage's counted trials: the app already counts them, and no DSR exists in code (REPO grep).

### M4. Certified venues and rulebooks under the deterministic kernel — trust plus integration, medium durability
- **Accumulates:** per venue/backend, recorded proofs of the four adapter rules (client-order-id round-trip, history depth,
  definite vs ambiguous rejection, API only); prop-firm rulebooks as enforced envelopes (daily loss, trailing drawdown,
  consistency, automation and device clauses, each quoted with a date); incident and reconciliation history — the traps
  already paid for (docs/RESUME-HERE.md).
- **Why hard to copy:** exchange agents put the model next to the order endpoint and disclaim supervision. Retrofitting an
  idempotent, reconciling, two-press kernel is a rewrite. Real venue semantics take calendar time to learn. Trust compounds
  with incident-free hours — but a single incident can lose it, hence "medium".
- **Seeds:** the IAtasAdapter rules; client-order-id proof and witness (REPO `AtasBridge/ClientOrderIdProof.cs`,
  `CoidWitness.cs`); the order state machine; loss budget, flatten and reopen (REPO `Gateway/Loss*.cs`); operator controls
  unreachable from the pipe; vendor commands as data (`atas.json`); the re-runnable probe (REPO `tools/probe`).
- **First concrete build:** a "venue certificate" record per connector, produced by the probe and shown in the app. A
  connector cannot be enabled for automation without one. Then one prop firm's rulebook encoded as an envelope — the firm
  chosen by the owner in light of Apex's ban and Topstep's device rule.

### M5. Research yield per AI dollar — perishable data, durable capability
- **Accumulates:** lineage from cost (model, runtime, tokens; actual vs estimated vs list price) to outcome (verdict, paper
  result): cost per paper-eligible candidate by model and role, and when further research spending stops paying.
- **Why hard to copy:** competitors give inference away or fold it into a flat fee (Robinhood's free GPT-Luna use to
  end-2026; Binance Ai Pro's monthly credit bundle), so they have no reason to measure cost against outcome. TradeAgent's mission (cover full costs) requires it. Prices move 9×–900×
  a year (Epoch), so any snapshot goes stale; the moat is the measuring loop, not the numbers.
- **Seeds:** turn metering and list prices (REPO `AgentRuntime/TurnMeter.cs`, `ListPrices.cs`); AI-attempt store; the
  vendor-limit handling that charges a refused turn nothing (`U-vendor-limit`, landed 2026-10-01); the daily cap.
- **First concrete build:** join AI-attempt costs to the versions they authored, and put "cost per verdict and per
  paper-eligible candidate, by model" in the daily report.

**Build order (INFERENCE):** M1 and M3 first (cheap, local, and they protect everything else); M5 next (the self-funding
mission); M4 as each venue is added; M2 only after the owner and counsel decide. Each fits the priority rule in
docs/PRINCIPLES.md as a local extension of a working primitive. None is a new subsystem.

---

## 7. What is not a moat in 2026 (INFERENCE, from §1)

- Agent-to-exchange connectivity (free MCP kits), natural-language strategy authoring (Composer, Ai Pro), backtest engines
  (LEAN, Freqtrade), multi-agent debate (TradingAgents), and "no terminal" compared with in-app exchange agents (Robinhood
  Agents, Bybit AI, Ai Pro). "No terminal" still beats the open-source frameworks, but not the venues.
- Any individual strategy: it decays (§2), and an honest referee will kill most of them (§5).
- Using frontier models: everyone has them. The live benchmark says the framework matters more than the model.

---

## 8. Owner decisions and open items

1. **Prop-firm path (contractual, urgent if CME automation is planned):** Apex prohibits AI/fully automated trading on
   funded and live accounts; Topstep allows API bots only from the trader's own device and through TopstepX. Whether ATAS
   routes are allowed at the chosen firm is UNKNOWN. Choose the firm and record its clause before any funded-account automation.
2. **Will TradeAgent ever serve anyone but the owner?** If yes: MiFID II / MiCA counsel before shipping strategies, signals
   or managed execution.
3. **Pool outcome cards (M2)?** Only opt-in, anonymised, after legal review.
4. **External timestamping for M1:** only hashes leave the machine. Pick a provider.
5. **UNKNOWN, not verified this leg** (search budget exhausted): Public.com's in-house agents and other 2026 start-ups
   reported by CNBC (2026-07-28; page blocked); FINRA/SEC guidance on agentic retail trading; Revolut X agent features;
   Binance copy-trading terms under MiCA; EU AI Act duties; CrunchDAO and Collective2 status.
