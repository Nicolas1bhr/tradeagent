# The edge factory — what TradeAgent becomes, and the order it gets built in

**Target architecture and build plan, 2026-10-02 (v2, after two read-only verifications of the briefs and an adversarial
review).** Everything here is DESIGN unless a line says otherwise; nothing in this file marks a feature built, a test passed
or a boundary enforced. It answers the owner's direction of 2026-10-02: the software is "way too stiff" with "nothing in place
that makes it a moat against the rest of the world"; it must become "an ai evolution software where in the end it generates
money by itself using all possible and future tools … to create a temporary or durable edge … like copy trading, news
treatment and scraping". `docs/PRINCIPLES.md` stays the product definition; every protection in `CLAUDE.md` and on the code
stands, and § 6 names each one this design touches. The research behind it — eleven legs, every external claim dated and
sourced — is in `docs/research/2026-10-02/`; `R01`…`R11` below cite those files.

**Later the same day the owner set the organisation that works this factory:** a hierarchy — one chief, top-level managers heading divisions, teams whose
head manager decides, executors who do the work, and an invisible watcher reporting only to him. It is `docs/ORGANISATION.md` (research R12–R17). It replaces
§ 4.7's "models own the organisation", re-sequences § 9 into two lanes, and with his answers to § 10 (Belgium among them, R16) changes §§ 2, 4.6, 4.8 and 8.

## 0. The diagnosis

The kernel is rare and is kept: a gateway that round-trips client ids and reconciles UNKNOWN, an app-owned referee with a
private holdout and counted trials, spend reserved before launch, provenance agents cannot edit. None of the 2026 platforms
that gave AI agents exchange access ships that (R06 §0). The factory floor around it is a keyhole:

- **One observation, one language.** The only datum is a 1-minute OHLCV bar of one pair; the strategy language can read
  nothing else (one instrument, long-or-flat, eight indicators, ~8 hours of memory, 139 days per run). Everything an agent
  can express is a function of public price history — the most-mined pond there is (R01 §D).
- **Nothing compounds.** No record of what the market said when, no structured memory of what was tried and why it died, no
  population, no allocation by forward record (R01 §A7). Day 300 knows what day 1 knew, plus prose.
- **The evidence cannot tell the truth yet.** The referee judges at increment 1, capital 10,000 and zero cost, so every
  BTCUSDT verdict is "no trade" (SOURCE, manager-checked: `GatewayPipeServer.cs:2496`, `Backtest.cs:76`); paper fills cost
  nothing; and 200 trials of pure noise on a year of data reach a best Sharpe of 2.77 (RUN `R04-calc`). Six blockers are in
  R01 §0; phase 0 fixes them.
- **Some protections are advisory today.** The vendor CLI agent runs unconfined as the owner's user (`Containment.cs:43`): it
  can read the holdout files, write any ledger file, and rewrite `runtimes.json` — whose base URL decides where the owner's
  pasted provider key is sent after the next start (SOURCE `AppHost.cs:121-150`, `RuntimeManifest.cs:716-728`,
  `ApiConversation.cs:242-252,466-497`; R10 #6, R11 § 1). Phase 0 closes the key route; containment closes the rest.

## 1. The end state

**What the owner sees.** One window that answers: what TradeAgent is reading (sources, items per day, their cost); what it
believes (the edges it funds, each with a forward record the app measured); what it is testing (ideas → backtests → paper
incubation → live); what it costs (AI, perception, data, fees) against what it earns, and how long the honest path to
covering those costs is; and what it needs from the owner (a key, a budget, a venue, a live confirmation). The owner sets
budgets, risk and authority, confirms live capital with two presses, and never sees a terminal.

**What runs inside, continuously:**

```text
 sources ─►┌──────────── CREATIVE ZONE — the organisation (docs/ORGANISATION.md) ──────────────┐
 (data)    │ agents read the tape and the genome bank, ask questions, propose      │
           │ features, strategies, lenses, adapters and tools, delegate, retire    │
           └───────┬──────────────────── proposals ─────────────────────▲──────────┘
                   ▼                                                    │ reads
           ┌──────────── EVIDENCE ZONE — the app measures ─────────────┴──────────┐
           │ TAPE (as it arrived, never overwritten) → FEATURES (as-of, decimal)   │
           │ → REFEREE (costed, deflated, canaried; shows what it cannot yet judge)│
           │ → INCUBATOR (many paper runs) → STANDING (forward record) → GENOME    │
           │ BANK (everything tried, why it lived or died, priors by family)       │
           │ PERCEPTION (pinned decision models, recorded answers) feeds features  │
           └───────┬──────────── allocation proposals inside owner envelopes ─────┘
                   ▼
           ┌──────────── CAPITAL ZONE — deterministic ────────────────────────────┐
           │ allocator → frozen version → runner → gateway rules 1–4, risk, loss,  │
           │ kill switch → venue                                                   │
           └───────────────────────────────────────────────────────────────────────┘
```

When it is done the owner can say: *it reads everything it is allowed to read, remembers everything it saw and tried,
believes only what the app measured going forward, and funds only what keeps working — inside limits I set.*

**A week inside the end state (illustration, not a forecast).** Monday the tape records a funding-rate extreme and an
open-interest spike on SOL as `O-LIVE`; the positioning family's frozen version v7, incubating on paper, reads the as-of
features at its hourly close and opens a paper position; every gate applies as for a live order. Tuesday an agent asks the
genome bank whether "funding extreme + open-interest spike" was tried (41 variants; family killed in August for decaying
after costs), designs an orthogonal variant instead, and spends 30 of its family's trials; one survives and takes a scarce
paper slot. Saturday the allocator recomputes: v7's family shows a positive posterior over the incubation window and keeps
its slots; a version whose realised cost ran at 1.8× modelled is retired to watch; the Edge page shows one learning-tier
proposal for the family, with its marginal-exposure preview, waiting for two presses — and the break-even line beside it.

## 2. Where the money can come from

From R03 §4, R04 §0, R06 §3, R07 and R10; INFERENCE where not cited.

- **The first target, chosen because it needs nothing the owner has not yet decided:** positioning and forced-flow features —
  funding extremes, open-interest shocks, long/short and taker skew — on a six-symbol perpetual universe, traded long/flat on
  spot until shorts are decided (R03 #3; R10 #3). Free data, a deep exchange archive (metrics 2020-09→, funding 2019→, with a
  2026-03-18 backfill and a 2026-06-25 timestamp shift to handle), no new venue, no perception. Liquidations have no complete
  free history and are recorded forward. For the owner, a Belgian retail resident, spot is the venue on the texts: two FSMA regulations close crypto
  perpetuals to retail clients (R16 § 0), so a short side needs elective professional status or a venue's written answer to the contrary.
- **Next, as decisions and sources arrive:** on-chain incidents and depegs (governance tokens fall ~27% in 24 h, most of it
  after the public announcement, R03 #1), unlocks by recipient type (#2), delisting-risk flags after the first minutes (#5) —
  all three need a short side (§10.1) or paid or account-gated data; funding carry only when it is rich (~3% annualised on
  BTC in 2026, a quarter of prints negative, #6). **CME last-half-hour hedging momentum on prop capital (#4) is the one top
  edge today's language can already express, with 16+ years of honest minute history** — it waits only on the prop decision
  (§10.2).
- **Do not hunt:** latency races (headlines, listings in their first seconds); naive news sentiment (the published equity
  signal fell from Sharpe 6.5 to 1.2 and is gone at 20 bp; the one public Jev news test lost after costs, R02 §5, R03 §3.1);
  per-decision LLM trading (R06 §0); mirroring lead traders (no official API, terms forbid scraping, poor copier outcomes,
  Zenit bans it, R03 §0.5).
- **"Copy trading", as the owner put it,** becomes public positioning as a FEATURE (which tracked wallets add, how levered the
  crowd is; Hyperliquid's positions are public — at address and aggregate level only, R03 §5), evolved and measured like any
  other input; never mirrored.
- **"News treatment", as the owner put it,** is breadth and classification — reading announcements, post-mortems, vesting
  documents and filings across many assets for cents — feeding frozen deterministic strategies over hours to days, never
  trading per headline. Its clean evidence starts when we start recording (§3, §4.2).
- **"Scraping", as the owner put it,** means reading whatever a source's terms allow — official APIs, feeds, licensed and open
  data first. Several major crypto outlets forbid AI processing or robots (R03 §5); they are excluded, not evaded.
- **Honesty about the destination:** no architecture guarantees profit; an honest referee will kill most candidates (R06 §5);
  and under the default evidence thresholds the system does not pay for itself in its first year at modest capital (§8).

## 3. The moat — what compounds with calendar time and cannot be bought

A later competitor can buy the same models, the same exchange access, the same news API — and the market-data half of our
tape (vendors sell years of funding, open interest and liquidations, R10 #16). It cannot buy:

1. **Answers recorded at arrival on first-seen unstructured items.** A model asked today about a 2024 headline remembers what
   happened next (R07 §1; measured inflation +4% to +70%, though the published comparisons also span different market
   periods). Point-in-time model vintages exist for smaller models (R07 §2), but nobody else holds our items with our
   first-seen times and the answers given then.
2. **The forward ledger, anchored.** Verdicts, paper fills, costs and forward records, hash-chained, with a daily root anchored
   outside the machine by default (only a hash leaves). Local tamper-resistance is only as strong as containment (§6.11).
3. **The genome bank, and the priors it earns.** Every hypothesis, variant, measurement, verdict and kill reason, with base
   rates by family. This is measurable value: with no prior, a strategy needs a forward Sharpe of 1.88 over a year to reach
   the learning tier; with a family prior mean of 0.3 it needs 0.68 (RUN `R10-calc`).
4. **Calibration capital** — per-question maps fitted to our own outcomes, cost and slippage fitted to our own fills. It
   resets with each instrument version, so a successor model is shadow-run before a pin retires.
5. **Research yield per AI dollar** — which model, method and question produces forward-validated edge per dollar. It becomes
   measurable only once forward records exist (≥ 90 days per family).

An opt-in pooled outcome network across installations has the highest ceiling and is an owner and legal decision (§10).

## 4. Architecture — planes on the existing kernel

Each plane extends a working primitive (R01 §B); none replaces the kernel.

### 4.1 Tape — information
- Generalises the forward-bar ledger (`ForwardBarStore`): its own file `state/tape.db` with its own upgrade ladder,
  append-only, a row per fetch, first reading stands, a differing re-reading is a new REVISION with its own arrival instant.
  Three times per datum: source time, first-seen time, revision. Readers ask "as of t" and take a required audience argument.
- Sources are data (the `runtimes.json` pattern) — URL shape, parser family, cadence, universe, terms basis, cost — but an
  origin that is not a built-in row's yields only `O-ARCH` rows, and a keyed source's origin comes only from a built-in row (§6.10).
- **Evidence classes, computed by the app per observation, only ever downgraded** (R07 §5): `O-LIVE` — first received within
  its cadence plus a stated tolerance of its source time; `O-PIT` — an exchange-published print (klines, funding, open
  interest) whose vendor checksum verifies AND whose overlap with our own `O-LIVE` record matches within a measured revision
  rate, with the timestamp convention pinned per file date (R10 #1c), or a vendor first-seen timestamp with a declared
  basis; `O-ARCH` — anything else fetched after the fact; `O-HIND` — built with hindsight.
- First sources, free and measured from this Mac on 2026-10-02: Binance USDⓈ-M premium/funding, open interest, long/short and
  taker ratios for six symbols. Then the metrics archive, GDELT and official exchange announcements (raw items recorded now,
  annotated later — §4.2), Hyperliquid public positioning, Deribit DVOL, DefiLlama; Kalshi only where the owner's country
  permits reading it. The app records only while it runs: a "keep this computer awake while recording" press is offered (§10).

### 4.2 Perception — System One and System Two
- **Division of labour:** code for arithmetic, dates, identifiers, limits and every hard rule; **System One** (bounded decision
  models such as Jev) for semantic judgement at volume; **System Two** (frontier LLMs) for research, coding, synthesis and
  question design. None of them on the signal path (§6.1).
- **It is phase 4, not phase 1, on purpose** (R10 #2): raw items recorded now as `O-LIVE` stay clean for later annotation by any
  instrument released before them (`A-PRE`, R07 §5.2) — `jev-1.13.0` dates from 2026-09-15 — so the tape's clock matters now and
  the annotator's does not.
- `IDecisionModel` port: state + typed questions (Choice / Noul / Score) → full distributions. Adapters (R02 §6.1): a TypeSafe-
  wire client written in-repo (TypeSafe direct is waitlisted; OpenRouter serves Jev today under its own model id). A frontier
  LLM's verbalised distribution is a different instrument, deferred until a version needs it.
- **Lenses** are versioned, hashed question schemas over tape items with escape hatches and everything computable done by code
  first (R02 §6.7): event type, asset relevance, direction, sourcing, time-sensitivity, and `addresses_automated_reader` (which
  quarantines an item); social-post pump risk (to avoid, never to join); source reliability (a cold-start prior that decays as
  the measured record grows). **Lenses over the genome bank or agent traces run in code or locally, never at a vendor** —
  they would ship the moat and owner documents out (R10 #14). Agents propose lenses; the app validates and runs them inside an
  owner-set perception budget; pins and keys are owner-only.
- **Every answer is a recorded measurement**: instrument identity (adapter, host, answered model id), schema hash, state hash,
  the unrounded distribution, latency, cost, annotation class. An answer from a model other than the pin is `UNPINNED` and
  excluded. Backtests and paper replay recorded answers; nothing in the evidence path calls a model again. Agents read answers
  through a bounded operation, never in bulk.
- Calibration is fitted against our own outcomes per instrument and lens version, with drift alarms. TypeSafe's agreement
  forbids distilling or imitating Jev; its probabilities as inputs to our own models of market outcomes are permitted
  (R02 §2). Until containment, that line is held by agent instructions — advisory (§6.11).

### 4.3 Features
- A feature is a deterministic spec (data) over tape series, bars and recorded answers — latest value, change, window
  statistics, cross-sectional rank, event counts filtered by calibrated probabilities — joined as of first-seen time plus a
  declared latency, computed by app code in decimal. Identity = hash of the spec and its inputs.
- Each feature carries its **clean-history start**: the later of when its sources became `O-LIVE`/`O-PIT` and when its
  perception instrument existed (R07 §5.4).
- Agent-written feature code runs only in the contained environment (§4.7) and enters as a recorded derived series through
  the admission ladder, never as code the runner executes.

### 4.4 Strategy language — growing in the order the first target needs
- Kept from v1: total parse, freezing by hash, warm-up, execution bounds, exits-before-entries, "the runner never calls a
  model". Additions are additive: a v1 program's text and id do not change, and golden vectors prove its behaviour does not.
- **`bars` (phase 0):** the program declares the bar it is evaluated on (e.g. `bars 1h`); the feed resamples closed 1-minute
  bars, decisions are taken at that bar's close and fills stay at the next 1-minute open; lookback and run limits count
  evaluated bars (500 hourly bars ≈ 21 days). Attempt 2's minute-scale strategy paid 62% of its loss in fees (R10 #2).
- **v2a:** a `feature` reference bound by hash; required versus optional fields (anything constraining orders or risk is
  required; a runner that does not understand a required field refuses — R05 row 10).
- **Universe:** one program over a declared universe — one trial, positions per member, per-member caps — because a strategy
  per asset multiplies trials and starves each of events (R10 #3).
- **v2b (with shorts and perception):** a short side where the venue supports it; probability-weighted sizing from calibrated
  scores; frozen deterministic model artifacts (logistic and tree ensembles in JSON, decimal); evaluation triggered by a RAW
  `O-LIVE` observation's arrival plus a declared fixed delay, never by a model answer's arrival (§6.1); a text-derived entry
  needs two independent sources or a per-item influence cap in its frozen rules.
- A version's manifest names every input hash; a changed input is a different program.

### 4.5 Evidence — a referee that knows what it can and cannot judge
| Stage | What the app runs | With thin clean history (year one) | With depth (≈ 5 years clean) |
|---|---|---|---|
| E0 sanity | parse, hash, bounds, complexity, similarity to the genome bank | enforced | enforced |
| E1 research | backtest under the app's venue cost model at 1× and 2×; daily net returns stored; a counted trial | enforced: costs applied, trial counted | + ≥ 30 trades, net > 0 at 1×, ≥ 0 at 2× |
| E2 robustness | 8 sub-periods; ±20% on each constant; the family's overfitting probability | computed and SHOWN; orders the paper queue | enforced |
| E3 deflation | cluster every trial stream in the lineage → effective trials → deflated Sharpe, with the noise ceiling | SHOWN; orders the queue | enforced |
| E4 holdout | the existing verdict at app cost; α-wealth per lineage; flags a holdout the authoring model may remember | the existing paper-eligible path | blind consistency screen |
| E5 paper | forward runs on post-freeze data; e-process, posterior, realised vs modelled cost | the real test | the real test |
| E6 live L1 | learning tier, owner press, family prior from the genome bank | P(SR > 0) ≥ 0.80 | same |
| E7 live L2 | proven tier: e-LOND on forward returns, per strategy or family-pooled | E ≥ 1/α | same |

- **The referee computes the power of its own gates.** A gate that cannot discriminate at the lineage's clean depth — the
  deflated-Sharpe gate passes a true Sharpe-2 strategy 1–6% of the time at one year and 52–82% at five (RUN `R10-calc`) — is
  shown and used to rank, not enforced. Hard gates live where money is: E6 and E7.
- **One INCONCLUSIVE path into paper, whatever the reason** (thin history, contaminated model features, too few events): a
  trial-charged forward-incubation quota — at most a third of paper slots and one per family at a time — so attaching a model
  feature buys nothing (R10 #1). Paper confers no live authority.
- **One app-owned venue cost model at every stage**, pinned at campaign open; at Binance's 0.20% taker round trip, one round
  trip a day is a 73%/year drag (R04 §7).
- **Canaries.** Known-bad strategies — a look-ahead peeker, a fee ignorer, pure noise — run through the real referee on every
  referee, data or cost-model change and in CI; fill-at-mid and holdout-touching cheats are covered by referee and reader
  tests, since the language cannot express them (R05 row 14, R10 #24). A pass freezes promotion and marks recent verdicts suspect.
- **Prove families, not versions — and say what pooling is worth.** A family is one mechanism over uncorrelated assets or
  events; four streams at Sharpe 1 pool to 2.0 uncorrelated but to 1.14 at ρ 0.7 (RUN `R10-calc`). Report the ρ-adjusted figure.
- **Until containment, every verdict is labelled "unprotected evidence"** and cannot count toward E7 (R10 #5).
- Agents see verdict and reason class only, as today; forward figures are theirs; fitness never includes holdout figures.

### 4.6 Selection — the evolution engine
- **Paper books per deployment** first: two versions on one symbol must not net each other (R01 §A2); then the **incubator** —
  many concurrent paper runs under the owner's paper envelope, capped per instrument, cluster and family, with bounded
  replacement. The runner steps rules at the declared bar while protection still acts within the minute (§4.4).
- **Standing** is computed at read from a deployment's own fills: daily net returns, e-process, posterior, fidelity to its
  backtest, realised versus modelled cost. A divergence opens a contradiction record that freezes widening (R05 row 12).
- **Paper allocation** (weekly, app policy inside the envelope): a seeded Thompson gate on shrunk posteriors, hierarchical
  risk parity across correlation clusters, ≥ 25% of births and paper slots kept for exploration — never live capital.
- **Live proposals are deterministic**: a posterior quantile with hysteresis, raised only on a material change, total capped at
  2/(λ+1) of Kelly from the owner's drawdown answer; each confirmed with two presses, bound to a digest of exactly what runs.
- **Retirement:** fast alarms — realised cost above 1.5× modelled, behaviour outside the backtest's 99% band, drawdown past the
  posterior's 99th percentile, a volatility-regime change — or CUSUM demote to paper. Nothing is deleted. Drawdown cuts come in two steps scaled to each
  version's own backtest volatility σ_a (R14 § 4.2, adopted 2026-10-02): at max(1.0 σ_a, the backtest's 95th-percentile drawdown) a paper version gets no
  widening and no new slot for its family, and a live one is halved as app-owned risk reduction if the owner confirms that reading (`docs/ORGANISATION.md`
  § 16.4); at max(1.5 σ_a, the 99th-percentile alarm) it retires to watch. Absolute 5%/7.5% levels would retire 98% of Sharpe-1 crypto strategies in a year at 20% volatility (R14 § 4.2).
- **The genome bank:** the minimal record per stage of R04 §8(b), measurements and claims in separate tables; fingerprints and
  near-duplicate detection charge a family's trials so renaming never resets them; family priors for E6; agents read it through
  a read-only `experiments` operation.

### 4.7 Factory floor
- The 2026-09-27 factory plan (`TRADEAGENT_AUTONOMOUS_FACTORY_PLAN_2026-09-27.md`, the owner's consulting folder) stays the
  design for the agent substrate. Brief now only `R-containment` (the Windows spike, a light leg with no file overlap) and
  `U-worker-identity`; re-plan the rest after M0, the runtime profiles, the containment record and worker identity (its § 25.5).
- **The organisation is `docs/ORGANISATION.md`** (the owner's direction of 2026-10-02, superseding "models own the organisation"):
  one chief, divisions per information source or capability, a team per strategy family with a deciding head, executors who choose
  how and never what, an audit line, and an invisible watcher reporting to the owner; the chart, orders and decision records are app
  data. Code sets envelopes from measured cost and contribution; yield-based budgets still wait for ≥ 90 days of forward record (R10 #10).
- **Admission ladder** for agent-built adapters, features and tools: scratch → registered → conformance-tested (point-in-time
  vectors; a seeded look-ahead adapter must fail) → evidence-eligible → quarantined on a health signal. Maturity is never
  authority (R05 row 2).

### 4.8 Capital
- Gateway rules 1–4, risk and loss gates, operator controls and two-press: unchanged.
- Venues for an EU resident (R08, R03 §1; owner decision §10.1): perpetuals are MiFID derivatives in the EU. **Only if the owner becomes an elective
  professional client (R16, the next bullet):** **Kraken Futures
  (Kraken EU, CySEC)** ranks first — client ids up to 100 characters come back on status, fills and history; history is
  documented as complete; keys can trade with withdrawal set to "No Access" — all pending a probe with a real key (its demo
  redirected from this Mac). OKX Europe is the alternate (unfilled cancels vanish after 2 hours, so rule 2 is bounded).
  Revolut X for spot (MiCA). Binance (no MiCA authorisation; unfilled cancels vanish after 72 h) and Hyperliquid (unlicensed;
  history bounded by count) stay data sources unless the owner decides otherwise; Bybit is excluded. ATAS prop accounts stay
  ask-me-first until order history is provable.
- **The owner is a Belgian retail resident (2026-10-02; R16):** on the texts, the FSMA's 2014 and 2016 regulations close crypto perpetuals to him at Kraken
  Futures EU and OKX Europe, although Kraken's Belgian page still markets them — only a venue's or the FSMA's written answer settles that; elective
  professional status is the lawful door. Spot is open: Revolut, Kraken, Bitvavo, OKX Europe, Coinbase and Bitstamp are passported into Belgium on ESMA's
  register (30 Sep 2026); Binance, MEXC and Hyperliquid are not, and stay data sources. CME futures through a prop firm are open on eligibility (Zenit,
  Brussels; Topstep), each needing a written yes on AI-authored code. Polymarket is on the Belgian Gaming Commission's blacklist: not a venue, and not read
  if reading means using it.
- Connector capabilities gain a **retention watermark** — the provable history bound per venue — as the ATAS connector already
  carries; a capability certificate produced by a probe precedes automation.
- Paper and live fills are typed apart; a paper result becomes a fresh live proposal, never a copied state (R05 row 1).

### 4.9 Owner surface
- An **Edge** page beside the Dashboard answering § 1's questions; every figure labelled measurement, estimate or assumption;
  the honest timeline of § 8 for the owner's current settings.
- Sources, keys and budgets are set in the app; questions are asked in plain words (§10); live proposals arrive as decisions.

## 5. Evolution everywhere

| Unit under selection | Fitness, measured by the app | What code does with it |
|---|---|---|
| strategy version | forward net growth and marginal contribution to the ensemble | paper slots, live proposals, retirement |
| family | pooled forward record (ρ-adjusted) | prior for E6; trial budget |
| lens / feature / source | information value of what it produces, per dollar | keep, drop, ask the owner for a paid trial |
| position, unit, seat (model) | calibration of recorded forecasts from day one; forward-validated marginal contribution per AI dollar once ≥ 90 days exist | envelopes, replacement, seat shadowing, unit templates — `docs/ORGANISATION.md` § 8 |

Models propose and organise; deterministic code reading measurements allocates resources and capital.

## 6. Protected properties this design touches, and how each is kept

1. **"No inference on the signal path"** (`docs/COUNCIL.md` rule 8). Protected property: a frozen version executes
   deterministically from recorded inputs; no generative or agent output can place, alter or TIME an order; evidence is bound
   to the computation. Kept by: answers recorded by the app from a pinned instrument and bound into the version's identity;
   the runner never calls a model and a missing answer is no decision; evaluation triggered only by raw observations plus a
   fixed delay, so model latency cannot time an order; only bounded-decision instruments and frozen artifacts may feed a
   live-eligible version; text-derived entries need two sources or an influence cap. A cheap text can still move a
   probability — that exposure is bounded by the frozen rules and every gateway limit, and measured by canaries and standing.
2. **Evidence binding** (rule 9): inputs, evidence classes, cost model and evaluation semantics join the version's identity
   and the promotion facts; any change invalidates. App releases that change no semantics no longer invalidate anything.
3. **Measurement versus claim:** tape, answers, features, standings and the genome bank are app-written; agent notes, lens
   proposals and hypotheses stay claims in separate tables.
4. **Inbox as data, generalised to every source:** nothing in an observation grants authority; an item addressing an
   automated reader is quarantined; non-public "tips" are excluded from trading (MiCA Art. 89, R03 §5).
5. **Referee and holdout — a stated change to paper admission:** the verdict path is kept as it is; a second, capped path into
   paper (the INCONCLUSIVE quota, §4.5) is ADDED. Paper still confers no live authority; live gates only harden.
6. **Capital:** live authority, allocations and two-press unchanged; incubator allocations live inside the existing paper
   envelope; a live allocation is a proposal the owner confirms, bound to a digest.
7. **Spend:** perception, data and AI reserve before spending through the existing `AiAttemptStore` admission (one
   transaction holds the daily cap); unknown is never zero.
8. **Credentials:** keys are pasted in the app and held in memory only until containment, as `Security/HarnessKey.cs` does.
9. **No terminal:** every capability is enabled, keyed, verified and recovered in the app; no refusal tells the owner to edit
   a file (phase 0 removes the existing "edit venues.json" instructions).
10. **A key's destination is not agent-writable** (new, `U-key-host-pin`): a key the owner pastes into the app is bound to the
    ORIGIN (scheme, host, port) it was pasted for; a keyed endpoint's origin comes only from a built-in row; a send anywhere else
    is refused, charges nothing and clears the key. The same origin rule decides whether an instrument check can verify and
    whether a tape row can be `O-LIVE`. Keys handed to vendor CLIs are readable by those agents by design until containment.
11. **Advisory until containment, said so:** while the CLI agent runs unconfined, "the collector is the only writer", the
    sealed holdout, the local ledger chain, the ban on scraping against terms and the ban on imitating Jev hold for the app's
    own paths only; an agent could break them. Hence "unprotected evidence" labels, anchoring by default, and `R-containment`
    in the first wave.

## 7. What TradeAgent will not do

Race latency · manipulate, coordinate or publish to move prices (MiCA Art. 91) · trade on non-public information, inbox "tips"
included (Art. 89) · scrape against terms or evade a geo-block · act as a signal provider or manage anyone else's money without
the licences (owner decision) · mirror lead traders · distil or imitate Jev (its probabilities feeding our own outcome models
are fine) · count backfilled model answers as clean evidence · let a model answer time an order · let paper confer live
authority · claim an edge, a profit or a protection the app did not measure. The scraping and Jev lines are advisory for
agent-initiated actions until containment's egress policy exists (§6.11).

## 8. Economics — the honest timeline

| Quantity (RUN `R10-calc` unless marked) | Value |
|---|---|
| Operating cost at $5/day research + $2/day perception, free data | $2,520 a year ($210 a month), plus any vendor subscription used as a runtime |
| Learning tier (≤ 5% of capital) at Sharpe 2, 25% volatility | ≈ $250 a year on $10,000; ≈ $2,500 on $100,000 |
| Forward Sharpe needed for the learning tier (P ≥ 0.80) | 1.88 over a year with no prior; 0.68 with a family prior of 0.3 |
| Proven tier (e-LOND, first test E ≥ 100) at Sharpe 2 / 1.5 | 2.3–3.1 years / 4.1–5.5 years |
| A $199/month data feed (R03 §6) | needs ≈ 2%/month on $10,000 just to break even |
| The organisation at the owner's scale (R15 § 3b; `docs/ORGANISATION.md` § 10) | the plan alone: ≈ 4 managers + a few executors at the plan's fee; ≈ 25 agents at $20/day (≈ $600/month); ≈ 60 at $50/day (≈ $1,500/month) — 3–7× the research line above |

- **Under the default thresholds and modest capital, the system does not pay for itself in its first year.** The ways to
  shorten that are owner choices, each with its risk stated: more capital, a larger learning tier, prop-firm capital on the
  CME edge the current language can already express, or lower AI spend (§10.0).
- Reading is cheap (Jev ≈ $0.04 per 1,000 one-kilotoken items, R02 §6.6); the item supply may not be (X reads cost ~$0.005
  each, R03 §2.4), so the supply is costed with the reading.
- **Paid data:** one paid source per hypothesis family may be trialled for at most a month inside an owner data budget, charged
  to that family; it stays only if the family's forward record pays for it (R10 #3).
- Turnover is kept low (the fee drag above); the app shows the break-even return for the current budgets and capital.
- **Tax, Belgium (R16 § 3; not tax advice):** since 1 January 2026 gains from normal management of private wealth — crypto and derivatives included — are
  taxed at 10% above about EUR 10,000 a year; speculative gains at 33%; a professional activity as professional income. The law names automated software and
  the number of transactions as signs of abnormal management, so the honest net figure is shown under 33% until the owner's accountant says otherwise.

## 9. Build plan

Every unit follows `docs/HOW-WE-BUILD.md`: a fresh builder, a ≤ 40-line brief, red-first on the money path, the landing gate.
**At most two heavy builders at once**, plus a light third with no file overlap (the lights are named in `docs/ORGANISATION.md` § 15). **READY** = a brief in
`docs/queue/`, re-checked against `main` at dispatch and moved to `docs/briefs/`. **CARD** = briefed just before its turn,
against the code its dependencies actually landed. Schema rungs land in ladder order — never a higher rung before a lower one.

**Phase 0 — truth and safety in the existing loop** (runnable now)

| Unit | Closes / protects | Depends |
|---|---|---|
| `U-key-host-pin` READY | the owner's keys go only to built-in hosts; an override host needs an in-app confirmation | — |
| `U-cost-model` READY | one venue cost model, pinned per campaign, used by referee and research; instrument/dataset check (rung 27) | — |
| `U-runner-forward` READY | paper runs see bars after their first 10,000; on-time bars pass the quote gate; no future-stamped quote | — |
| `U-evidence-identity` READY | evidence bound to evaluation semantics with golden vectors; releases stop ending paper runs | `U-cost-model` |
| `U-timeframe-a` READY | `bars` in the language; backtests and the referee on hours and days; the runner refuses `bars` ≠ 1m | `U-evidence-identity` |
| `U-timeframe-b` READY | the runner steps rules on declared bars while protection still acts within the minute | `U-timeframe-a`, `U-runner-forward` |
| `U-venue-verify` READY | instruments verified by the app against the venue's own definition; no file to edit (the next free rung at landing, expected 29) | `U-cost-model`, `U-tape-store` |
| `U-paper-friction` READY | paper fills and undeclared research friction default to the venue cost model; the friction in force named | `U-cost-model` |
| `R-containment` CARD | the Windows containment spike (factory plan § 9.3), probe only, on the box | — |
| **M0** observed loop, attempt 3 | `docs/briefs/U-observed-loop.md`, on the owner's allowance | all of phase 0 (`U-org-ledger` may have landed; it is inert) |

**Phase 1 — start recording**

| Unit | Closes / protects | Depends |
|---|---|---|
| `U-tape-store` READY | `state/tape.db` with its ladder; collector; Binance USDⓈ-M context for six symbols; per-observation classes | — |
| `U-tape-read` READY | `data-tape` for every role, `data-list`, status and report lines | `U-tape-store` |
| `U-tape-archive` CARD | the exchange metrics and funding archives with the `O-PIT` overlap rule | `U-tape-store` |
| `U-tape-events` CARD | GDELT and official exchange announcements as raw items with first-seen times | `U-tape-store` |
| `U-tape-chain` CARD | Hyperliquid positioning (aggregate), Deribit DVOL, DefiLlama; Kalshi where lawful | `U-tape-store` |
| `U-tape-integrity` CARD | hash-chained tape and evidence ledgers; the daily root anchored by default | `U-tape-store` |

**Phase 2 — express the first target**

| Unit | Closes / protects | Depends |
|---|---|---|
| `U-features` CARD | deterministic as-of feature specs; clean-history start per feature | `U-tape-read` |
| `U-language-v2a` CARD | `feature` references and required fields | features, timeframe |
| `U-universe` CARD | one program over a declared universe; per-member positions and caps | v2a |
| `U-archive-depth` CARD | five years of spot archives with measured run cost | timeframe |
| `U-paper-books` CARD | per-deployment virtual books on spot paper | phase 0 |

**Phase 3 — selection**

| Unit | Closes / protects | Depends |
|---|---|---|
| `U-trial-returns` CARD | every trial's daily net returns at 1× and 2× cost | `U-cost-model`, timeframe |
| `U-referee-v2` CARD | E2–E4 computed with their own power; shown vs enforced; α-wealth; the INCONCLUSIVE quota | trial returns |
| `U-canaries` CARD | known-bad strategies through the real referee on every change and in CI | referee v2 |
| `U-experiments-op` CARD | the genome bank v1: experiment graph, fingerprints, family charging and priors | trial returns |
| `U-rejudge` CARD | a withdrawn standing judged again under new semantics, charged as a peek (a rung: the verdict key) | `U-evidence-identity` |
| `U-forward-standing` CARD | per-deployment forward record, e-process, posterior, fidelity, contradictions | `U-paper-books` |
| `U-incubator` CARD | many paper runs, weekly paper allocation, bounded replacement, retirement wired | standing |
| `U-mission-v2` CARD | the agents' mission rewritten for the edge factory; starting points as suggestions | phases 1–2 |

**Phase 4 — perception** (raw items have been recording since phase 1): `U-decision-port` READY (needs `U-tape-store` and
`U-key-host-pin`) → `U-decision-card` (the owner's Perception card and Test press) → `U-lenses` → `U-annotator` → `U-event-study`; `U-decision-fallbacks` (LLM-verbalised and local runtimes)
deferred until a version binds to one or a schema is too sensitive to send out.

**Phase 5 — factory floor, now the organisation lane:** `R-containment` (phase 0, light) and the O-units of `docs/ORGANISATION.md` § 15, which replace
`U-worker-identity` and the factory plan's identity, wake, scheduler, resource-tree and delegation units and run BESIDE phases 0–4 as lane B.

**Phase 6 — venues and live** (owner decisions §10): `U-perp-connector` (only if the owner becomes an elective professional client, R16 — then Kraken Futures EU first if chosen; demo first; the
retention watermark; a probe certificate) → `U-language-v2b` (short side, raw-observation triggers, probability sizing) →
`U-paper-perps` (funding accrual from the tape) → `U-live-tiers` (L1/L2 proposals, digest-bound two-press, typed fills,
aggregate ceilings) → `U-cme-archive` and `U-rulebooks` if the prop path is chosen.

**Phase 7 — moat extensions** (owner decisions): a local outcome-card format; an opt-in pooled outcome network after legal review.

**Dispatch waves — superseded 2026-10-02 by `docs/ORGANISATION.md` § 15**, whose waves table is the only copy (two lanes, two heavy builders and a
light third; M0 after W4b on lane A only; M-org0 after W7; M-org1 after W12, on confined seats). The earlier pairings are kept in git history.

**Milestones**, each a distinct claim under `BUILD-STATUS.md`'s honesty rule: **M0** the observed paper loop · **M1** the tape has
recorded seven days with gaps accounted, and its endpoints are verified from the owner's laptop · **M2** three or more non-price
candidates in forward incubation with e-processes running · **M3** a lens answers live and the app publishes an event study ·
**M4** six or more concurrent paper runs, a weekly allocation, a retirement fired, canaries never passing · **M5** the
sustained factory of the factory plan's phase 7 · **M6** live readiness — owner decisions.

## 10. Owner decisions — in plain words

**Answered 2026-10-02:** 0 — horizon indefinite (capital not named); 1 — resident in Belgium (venues: § 4.8, R16); 2 — prop firms yes, as one small
channel among many; 3 — paper runs "as many as we know won't melt the quota" (paper runs spend no AI allowance, so the chief proposes a count from the
measured runner cost, `docs/ORGANISATION.md` § 12); 4 — "no limits; those decisions should be handled smartly by the AIs", then, offered the route that
keeps operator authority, "AI proposes the ceiling": the chief proposes the drawdown ceiling and the share for strategies being proven, the owner confirms
with two presses, the app sizes inside it. 6 — "be prepared for all, for now just plans". The organisation's own questions are `docs/ORGANISATION.md` § 16.

As first asked (they change what phase 0 and the first campaign mean):
0. **Capital and horizon.** How much capital, and how long before the system must cover its costs? The trade-off is stated
   in § 8: money sooner means a larger learning tier and more risk.
1. **Country of residence and intended venues.** It decides spot (Revolut X), perpetuals (Kraken Futures EU or OKX Europe),
   whether Binance, Hyperliquid or prediction markets may be used, and which fees the first campaign is judged under.
2. **The prop-firm path.** Which firm (Zenit refuses BG, HR, MT, RO, SI residents), and its written answer on AI-authored code
   the owner holds; if yes, the CME edge comes forward.
3. **Paper envelope size.** How many paper runs may incubate at once (attempt 2's envelope allowed one).
4. **Two risk questions:** the largest fall of the account you accept, and how sure you want to be of not exceeding it; what
   share of the account may go to strategies still being proven. Defaults apply until answered.

Asked when their phase comes:
5. **Perception provider and budget** (phase 4): Jev through OpenRouter or TypeSafe's waitlist, a key, a daily budget — with
   the terms stated: processing in the US under standard contractual clauses, TypeSafe's right to use service telemetry,
   uncapped liability for imitating Jev, OpenRouter's own terms unknown, zero-retention only on enterprise plans.
6. **The AI research budget and runtime**: daily cap; the Codex plan, a larger plan, or an API key pasted in the app; and the
   vendor account's training setting for the owner's research.
7. **Laptop uptime**: may the app keep the computer awake while recording?
8. **Anchoring**: on by default (only a hash leaves the machine) — may be switched off.
9. **A paid-data budget** for one-month family trials.
10. **Serving anyone but the owner**, ever: MiFID II / MiCA counsel first; pooling waits on it.
11. **Releases and keys**: until containment, every self-update restart asks for the keys again — batch releases to the
    owner's machine or accept the re-paste.

## 11. Open questions (UNKNOWN) and how each closes

- Jev's training cutoff and real latency from the owner's laptop → the `U-decision-port` probe; until then a Jev feature's clean
  history starts at our first call (or `A-PRE` for items after 2026-09-15).
- Kraken Futures' history and key scopes as documented → the connector's probe with a real key.
- e-LOND with asynchronous decisions (R04 §9) → a simulation study before `U-live-tiers`.
- Out-of-sample evidence on positioning, unlocks and listings beyond vendor studies (R03 §7) → our own forward record.
- The Windows containment route → `R-containment`.
- Crypto perpetuals for a Belgian retail client (FSMA bans on the texts, Kraken's Belgian page to the contrary) → the venues' or the FSMA's written answer (R16 § 4).
