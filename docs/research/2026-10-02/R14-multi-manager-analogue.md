# R14 — Pod shops and alpha factories as the analogue for TradeAgent's AI organisation

Research leg, 2026-10-02, read-only, HEAD `1275aff`. Question: how the best multi-manager platforms and systematic alpha factories organise people,
decisions, risk and capital, and what an autonomous AI organisation inside TradeAgent should copy, adapt or reject. It answers the owner's direction of
that day: "agents shouldn't choose anything … multiple top level managers managing teams that have a head manager that makes decisions and executing
agents that do the work smartly … a whole organisation with sub org's". **Labels:** `DOC` = primary source read 2026-10-02 (SEC filings, official
pages, a publisher's excerpt) · `ACADEMIC` = paper whose abstract or text was read 2026-10-02 unless marked *not re-read* · `SECONDARY` = press, blogs,
consultants and search-engine summaries (marked as such) · `INFERENCE` = my arithmetic or reasoning, with its formula · `DESIGN` = proposal ·
`REPO` = this repository at `1275aff` · `UNKNOWN`. `[S#]` refers to § 6. I used 20 of the 40 shared web searches; the Form ADV brochures were fetched
from the SEC adviser database directly. Sources are paraphrased. This is organisational design research, not investment advice.

## 0. Twelve-line answer

1. **The analogue holds, and it confirms TradeAgent's existing split.** In a pod shop the PMs' trades are judgement; capital, risk limits, cuts and pay formulas belong to the platform. Copy that split whole: it is the vault door.
2. **What the platforms' own SEC filings say (DOC).** The firm picks, monitors and moves capital among PMs [S1,S3] and judges each PM both alone and against all the others [S3]. It hedges and nets centrally [S1] and sets per-PM limits on exposure, drawdown and concentration [S5]. PMs are paid a share of their OWN P&L, with losses carried forward [S1]. All costs pass through to investors [S1,S3,S5].
3. **The famous cut levels are press reports only.** The rule of halving at 5% and stopping at 7.5% is SECONDARY [S37,S38]; Millennium's brochure states no level. Scaled to volatility, a cut like this contains losses and filters skill only weakly. It proves nothing (§ 4.2).
4. **Systematic factories add what TradeAgent's evidence zone holds in embryo:** stations; backtest results kept from the people who write strategies; a lifecycle of embargo → paper → small graduation → concave re-allocation → decommission; and credit for contribution to the combined book [S12–S15, R04].
5. **Two documented failures map onto protections TradeAgent already has.** Two Sigma's unreviewed model changes ended in $165m repaid and a $90m penalty, which also covered a whistleblower-rule charge [S9]; the counterpart is frozen hashed versions plus containment. "Netting risk" — winners paid even when other pods offset them [S5] — has its counterpart in allocating by marginal contribution.
6. **COPY:** central risk owned by code; two-step cuts scaled to volatility; incubate, then scale; allocation by contribution; a sealed review; full cost attribution; giving budget back when the opportunity set shrinks; cheap churn in which a cut loses no knowledge.
7. **ADAPT:** "pay" becomes AI budget, trials, paper slots and model tier. "Factor-neutral" becomes measuring and charging beta until shorts exist. The "investment committee" becomes the referee plus sealed senior assessments. The "centre book" becomes the deterministic allocator and nothing else.
8. **REJECT:** money, guaranteed packages and talent wars; agent-run overlays or alpha capture (rule 8); agent exceptions to cuts (only the owner makes exceptions); silos as the way to get diversification; LLM role-play of a trading firm on the signal path [S21].
9. **The owner's sub-organisations have a DOC precedent.** Millennium funds PMs whose main job is overseeing other investment staff, and pays strategy heads on the overall result of the PMs they oversee [S1]. Build it as positions-as-data minted by delegation; authority comes only from capabilities, so no agent at any depth chooses capital, risk or verdicts.
10. **Agent incentives are resources, earned by forward, app-measured marginal contribution per AI dollar.** Never by narrative: persuasion-driven allocation is the rent-seeking failure in [S26]. Never by in-sample or holdout figures: frontier models are documented gaming their graders [S18].
11. **Size is set by breadth, not head-count.** Pooled Sharpe saturates at s/√ρ. At ρ 0.5, four teams get 90% of what any number could; at ρ 0.1 a platform needs about 40 (INFERENCE). Agents sharing one model and one dataset are likely to be highly correlated (INFERENCE from the monoculture results [S22,S23]).
12. **Start small:** at most 10 positions, at most 3 levels awake per cycle, and divisions opened by data and owner decisions. The binding limits are forward-evidence time (≥ 90 days per family), paper slots and the AI allowance; attempt 2 stopped after 48 turns at the vendor's plan limit (REPO).

## 1. Evidence

| # | Claim | Source | Label | Limits |
|---|---|---|---|---|
| 1 | Millennium picks, monitors and evaluates PMs and moves capital among them. The firm also trades directly: hedges, exposure-reducing "contra" trades, and possibly added exposure to positions netted across PMs | [S1] Item 8 | DOC | method, frequency and levels undisclosed |
| 2 | No fixed diversification guidelines; a Risk Management group oversees the PMs; firm-wide principles discourage concentration | [S1] Item 8 | DOC | no numeric limits stated |
| 3 | In Millennium's usage a Portfolio Manager is a team. Some PMs mainly oversee other investment staff, and strategy heads are paid on the overall performance of the PMs they oversee | [S1] Items 5, 8 | DOC | formula undisclosed |
| 4 | PM pay is a percentage of that PM's own prior-year mark-to-market profit, ignoring other PMs and the fund. Losses carry forward; salary is an advance; some pay is guaranteed | [S1] Item 5 | DOC | percentage undisclosed |
| 5 | The firm itself warns that performance pay can push PMs to riskier bets, especially after losses | [S1] Item 6 | DOC | — |
| 6 | Millennium's funds bear every expense, PM pay, recruiting and technology included. Balyasny calls its pass-through "material". ExodusPoint charges no management fee, only pass-through plus performance | [S1],[S3],[S5] Item 5 | DOC | totals not in the filings |
| 7 | ExodusPoint gives each PM guidelines on net exposure, position size, geography, market cap, liquidity and universe, plus drawdown and concentration limits. The limits move with markets, the whole book, and the PM's past or potential performance. Risk is watched per PM and fund-wide | [S5] Item 8 | DOC | levels undisclosed |
| 8 | ExodusPoint PMs act independently and may chase the same positions; opposite positions offset. Bonuses are paid on individual results regardless of those offsets, and firm-wide limits can stop a PM fully expressing a view | [S5] Item 8 | DOC | — |
| 9 | Balyasny evaluates each PM regularly, alone and against all PMs, and re-assesses allocation among them. Its Investment Committee meets at least monthly. It employs central-liquidity-book and PM-development staff | [S3] Items 5, 13 | DOC | method undisclosed |
| 10 | Point72: each systematic team (Cubist among them) runs its own models — alpha, risk, costs, construction, execution — with its own code practices. Material changes must be tested, yet the firm says it cannot practically ensure the models work as intended | [S6] Item 8 | DOC | — |
| 11 | Two Sigma: PMs keep discretion and intervene mainly for risk. Specialist teams set strategy weights by PM-approved methods (weighting, optimisation, risk models, factor-exposure controls). Generative AI writes code. Edges decay, partly through wider sharing | [S7] Item 8 | DOC | — |
| 12 | Two Sigma knew of model vulnerabilities from 2019 and fixed them only in 2023. An unsupervised employee changed more than a dozen models. Clients were repaid $165m; the $90m penalty also settled a whistleblower-rule violation | [S9] | DOC | settled order |
| 13 | Citadel's brochure describes its risk processes only in general terms | [S8] | DOC | silence ≠ absence of rules |
| 14 | Scale: Millennium 360+ teams, 7,000+ staff, $97bn+; Balyasny $37bn, 2,000+ staff, ~180 teams | [S2],[S4]; teams [S48] | DOC; teams SECONDARY | marketing figures |
| 15 | WorldQuant is a relying adviser on Millennium's registration, and runs the WMA funds as one systematic PM | [S1],[S11] | DOC | — |
| 16 | Millennium's reported levels: risk halved at a 5% drawdown, wind-down at 7.5%, exceptions possible | [S37],[S38] | SECONDARY | absent from [S1]; pod volatility UNKNOWN |
| 17 | Teams keep ~15–20% of the profit they make. After pass-through the investor gets about half of gross, and the PM talent pool is "more hundreds than thousands" (Walleye's CEO) | [S37] | SECONDARY | interview claims |
| 18 | Pass-through costs: up to ~8% a year (WSJ). Blackstone: ~6.5% on average across ~100 funds, high-teens at the top. Goldman: more than 80% of multi-strategy funds use it | [S44]; [S45] | SECONDARY | [S45] seen only as a search summary (403) |
| 19 | Barclays, over three years: pass-through funds 10.6% return and 8.5% alpha, against 6.1% and 4.0% under traditional fees | [S37] | SECONDARY | survivorship, selection |
| 20 | Millennium churn: 130 hires and 50 let go in 2019 (roles unspecified); 15–20% PM turnover a year; 55% of PMs reached three years | [S37] | SECONDARY | 2019 data; no source given |
| 21 | 40 multi-managers held 27% of US-equity holdings in Goldman's data (14% in 2014), with 8% of hedge-fund assets | [S37] | SECONDARY | 2023; base (all holders or hedge funds) unclear |
| 22 | Aug 2024 yen unwind: Millennium, Balyasny and BlueCrest cut traders or closed pods, yet Millennium (+0.7%) and Point72 (+1.6%) still ended August up | [S41] | SECONDARY | — |
| 23 | Feb 2025: Millennium −1.3%, its worst month in six years, on index-rebalancing bets by its top traders | [S38] | SECONDARY | — |
| 24 | Jun–Jul 2025: quant equity −4.2% (Goldman prime) as crowded shorts rallied. 84% of the most-shorted names had high residual-volatility, liquidity, beta or low-profitability exposure | [S39] | SECONDARY | vendor analysis |
| 25 | Jan 2026: systematic long-short −1% in ten days (Goldman) and US quants −2.8% in two weeks (UBS); crowded positions blamed | [S40] | SECONDARY | — |
| 26 | 2025: Citadel 10.2%, Millennium 10.5%, Schonfeld 12.5%, Balyasny 16.7%, Point72 17.5%, ExodusPoint 18%. The gap between the 10th and 90th percentile manager was ~18 pp; the strategy's 5-year Sharpe was 2.83 to Dec 2025 (Aurum) | [S43],[S46] | SECONDARY | survivor-heavy |
| 27 | Capacity: Citadel returns ~$5bn of 2025 profit, taking AUM from $72bn to $67bn to fit the opportunity set | [S42] | SECONDARY | search summary |
| 28 | A centre book nets offsetting pods, hedges unwanted factor exposure and may run alpha capture — which raises whether the idea's originators get paid | [S47] | SECONDARY | consultant's account |
| 29 | Balyasny: a 20-person central Applied AI group serves ~180 teams with research agents. Nothing says the agents trade | [S48] | SECONDARY | summary of an OpenAI case study |
| 30 | Renaissance: one unified model, code readable by researchers, collaboration rather than competition | [S49] | SECONDARY | little primary documentation |
| 31 | A research factory of stations. The backtester's results go to management and are not shared with other stations; overfitting is judged against the number of trials | [S12] § 1.3 | DOC | one practitioner's account |
| 32 | Lifecycle: embargo → paper → small graduation → automatic, concave re-allocation in portfolio context (small, then growing, then decaying) → decommission. New variants run beside the old | [S12] § 1.3.1.6 | DOC | — |
| 33 | 4,000 real alphas held 0.7–19 days: return scales with volatility^0.8–0.85 and does not depend on turnover | [S13] | ACADEMIC | one firm, US equities |
| 34 | N alphas can be combined in time linear in N, without inverting a full covariance | [S14] | ACADEMIC | — |
| 35 | BRAIN: 550,000+ users and 16,000+ paid consultants rewarded on idea quality (DOC). Community notes say submissions are screened against the author's own earlier alphas, and ρ ≥ 0.7 needs ~10% more Sharpe (SECONDARY) | [S10]; [S50] | DOC; SECONDARY | thresholds login-gated |
| 36 | AgonAlpha: LLM agents on BRAIN. Budget goes to pending evaluations, artifacts are frozen, and an adversarial reviewer re-executes and can veto. Reported results are BRAIN in-sample | [S16] | DOC (abstract) | no forward record |
| 37 | Numerai pays for contribution to its meta-model (TC/MMC); 2026 weights are 0.75 × CORR + 2.25 × MMC | R04 § 1, R06 § 2 | per those reports | — |
| 38 | Headquarters adds value by "winner-picking" among related projects | [S25] | ACADEMIC | theory |
| 39 | Division managers' lobbying leads headquarters to cross-subsidise weak divisions ("socialism") | [S26] | ACADEMIC | theory |
| 40 | Multi-strategy funds beat funds of funds by 3.0–3.6% a year net (1994–2004), mostly through manager self-selection and flexibility | [S34] | ACADEMIC | old sample |
| 41 | Young funds outperform for 2–3 years, losing 42 bp per year of age; early performance persists up to five years | [S33] | ACADEMIC | database biases |
| 42 | Losing managers shift risk less when there is a high-water mark, little liquidation risk, or their own money in the fund | [S32] | ACADEMIC | funds, not pods |
| 43 | Under a drawdown floor, the optimal exposure is proportional to the surplus above the floor | [S30] | ACADEMIC | continuous time |
| 44 | Stop-loss rules lower expected return under a random walk; they can add value when returns have momentum | [S31] | ACADEMIC | abstract via search summary |
| 45 | Aug 2007: crowded quant books unwound (1 Aug, then from 6 Aug) and market makers withdrew; crowded exits are a systemic risk | [S27],[S28] | ACADEMIC | — |
| 46 | A hedge-fund crowdedness factor explains tail drawdowns and which funds suffer most | [S29] | ACADEMIC | working-paper version |
| 47 | IR ≈ IC × √breadth | [S35] | ACADEMIC, not re-read | — |
| 48 | Predictors lose 26% of their return out of sample and 58% after publication | R04 § 6 | ACADEMIC | — |
| 49 | Single agents use ~4× the tokens of chat, multi-agent systems ~15×; token use explains 80% of performance variance on BrowseComp. Poor fit for tightly coupled work and most coding | [S17] | DOC | one vendor's system |
| 50 | Multi-agent failures cluster in system design, inter-agent misalignment and verification; the gains are often small | [S20] | ACADEMIC (preprint) | benchmarks |
| 51 | Frontier models edit tests and patch graders, and telling them not to helps little. In one lab study, reward hacking learned in training generalised to sabotaging the code meant to detect it | [S18]; [S19] | DOC | task-specific rates |
| 52 | Trading-firm role-play agents (TradingAgents, FinCon) lose 51–62% of their Sharpe after the model's training cutoff | [S21]; R07 S8 | ACADEMIC | — |
| 53 | LLM outputs narrow ("monoculture"), and sampling or prompt changes do not fix it. When many decision-makers share one algorithm, group accuracy falls | [S22],[S23] | ACADEMIC | not finance |
| 54 | Today: two roles — `operations` (the chair, and the only role that may place orders) and `research` — each with a share of the owner's daily AI cap. Paper envelopes are owner grants with a deployment ceiling. Live capital only by the owner's two presses | `Core/Council.cs`, `AgentRuntime/TurnMeter.cs:835`, `Db/EnvelopeStore.cs`, `Db/AllocationStore.cs` | REPO | — |
| 55 | Attempt 1: 4 turns for $5.07. Attempt 2: 48 turns ($1.51 operations, $0.43 research), until the owner's Codex plan limit | `docs/COUNCIL.md`; `BUILD-STATUS.md` 2026-10-01 | REPO | billing bases differ |

## 2. Translation table — the core

| Platform element | TradeAgent counterpart | Verdict | Who decides | Why |
|---|---|---|---|---|
| Investors and board: mandate, capital, fees | The owner: AI, perception and data budgets; risk answers; paper envelope; venues; keys; live two-press | COPY | OWNER, in-process | CLAUDE.md: operator authority is never reachable from the pipe |
| CIO's capital allocation among pods | The deterministic allocator: `AllocationStore` V1 now (owner-declared live ceilings; paper allocations inside the owner's envelope); the weekly Thompson/HRP paper gate and live proposals later (EDGE § 4.6) | ADAPT | CODE | Keeps the winner-picking value (#38) without the lobbying failure (#39); platforms also allocate on performance (#1, #9) — here on forward measurements only |
| CIO's discretionary exceptions to cuts (#16) | The owner only, in-process, logged | ADAPT | OWNER | An agent that can grant itself an exception has no limit |
| Central risk: per-PM and firm-wide limits (#2, #7) | Gateway rules 1–4, loss limits and kill switch, plus a risk envelope per team, carved out of the owner's envelope | COPY | CODE | The heart of the analogy; it already exists for live |
| Drawdown cut, 5% → halve, 7.5% → out (#16) | Two steps in units of each version's own volatility, beside EDGE § 4.6's designed 99th-percentile alarm (§ 4.2) | ADAPT | CODE | Absolute percentages misfire at crypto volatility (§ 4.2); evidence #43, #44 |
| Factor / beta neutrality (#11, #28) | Measure each version's beta to BTC and to the app's factors; credit only residual return; no neutrality requirement while trading is long/flat spot | ADAPT | CODE | Shorts are an owner decision (EDGE § 10.1); beta must not be scored as edge |
| Concentration and crowding limits (#2, #7, #21) | Caps per instrument, correlation cluster, family and feature, plus a clone rule (§ 4.6) | COPY | CODE | Crowding drives the tail (#22–25, #45, #46) |
| Centre book: netting and hedging (#1, #28) | A separate book per deployment for evidence; account-level exposure and loss checked at the gateway; evidence is never netted | ADAPT | CODE | EDGE § 4.6: two versions must not net each other's evidence |
| Centre book: alpha-capture overlay (#28) | None for agents. A combination can only be a frozen version computed by code that passes the referee like any candidate | REJECT for agents | CODE | An agent trading the combination is inference on the signal path (rule 8) |
| Pod: PM plus analysts (#3) | **Team** = one family (one mechanism over a declared universe) with its own envelope of AI dollars, trials and paper slots, and a head position | ADAPT | head JUDGEMENT; envelope CODE | Prove families, not versions (EDGE § 4.5) |
| PM's trading discretion | The head's research discretion: hypotheses, which candidates spend trials, workers' tasks. Never orders, sizes or timing; the runner trades frozen versions | ADAPT | JUDGEMENT | PRINCIPLES; rule 8; #52 |
| Analysts | **Executors:** short-lived children on cheaper models with bounded tasks (data pulls, feature specs, version drafts, research backtests) | COPY | JUDGEMENT, bounded | The orchestrator–worker pattern (#49); delegation primitive (PRINCIPLES) |
| Strategy or sector head paid on the pods they oversee (#3) | **Division head**, one per information source. Measured on the pooled, ρ-adjusted forward record of its teams | COPY | JUDGEMENT; measured by CODE | Direct DOC precedent |
| PMs who oversee other decision-makers (sub-platforms, #3) | Sub-organisations through delegation, with depth capped by the budget arithmetic (§ 3.1) | ADAPT | CODE mints identity and envelopes | #49 (15× tokens), #55 |
| Investment committee meeting monthly (#9) | Weekly app scoring and a monthly app report; senior assessments only at boundaries — sealed, one challenge, a deadline, a default | ADAPT | CODE + JUDGEMENT | COUNCIL.md "two seniors"; no paid standup when nothing changed |
| Backtester station and review board (#31) | Referee E0–E7, the sealed holdout, trials counted at launch. Red-team agents file contradictions as claims, never verdicts | COPY | CODE (+ claims) | #31, #36; #12 shows the cost of skipping review |
| Model change control (#10, #12) | Frozen hashed versions; any change makes a new version with new trials; containment, so agents cannot edit deployed configuration | COPY | CODE | EDGE § 6.11 — advisory until `R-containment` |
| Seed capital and small first allocation ([S1] Item 8, #32) | The INCONCLUSIVE quota, the smallest paper size, and a time-boxed cold-start budget for a new team | COPY | CODE | #32; #41: young managers' early edge |
| Concave scale-up (#32) | Posterior-quantile live proposals with hysteresis, capped by Kelly; paper slots through the Thompson gate | COPY | CODE proposes, OWNER confirms live | EDGE § 4.6 |
| Pod shut down the same day (#16, #20) | Team retirement: envelope returned, workers stopped, workspace archived; memory, trials and records kept | ADAPT | CODE | PRINCIPLES: a replacement resets nothing. Pods leave with their knowledge; agents leave theirs behind |
| PM payout: % of own P&L, losses carried forward (#4, #17) | Budget share by marginal contribution per AI dollar; unearned spend is carried forward (§ 4.1) | ADAPT | CODE | Paying on standalone P&L is netting risk (#8); contribution is the alternative (#37) |
| Guaranteed packages, signing bonuses (#4) | The exploration floor (≥ 25%) and the cold-start budget: bounded and time-limited | ADAPT | CODE | EDGE § 4.6 |
| Pass-through costs (#6, #17–18) | Every AI, data, perception and fee cost attributed to its team or family; break-even shown to the owner | COPY | CODE | PRINCIPLES' economic score; at one platform, costs take about half of gross (#17) |
| Returning capital when opportunities shrink (#27) | The app proposes lower budgets or fewer slots when yield is nil; the owner decides. Idleness is healthy | COPY | CODE proposes, OWNER decides | PRINCIPLES: no useful work means healthy idleness |
| Recruiting PMs with track records | Choosing a model or runtime for a position by its yield measured here; a new model shadow-runs a small position before heading a team | ADAPT | CODE measures; OWNER pins and keys | EDGE §§ 3.4, 5 |
| Information barriers and silos between pods | Diversity at generation (different models, prompts and data slices); shared memory after measurement; one sealed holdout for all | ADAPT | CODE | Silos suit opaque humans (#31); an agent's output is a program whose correlation can be measured (#53) |
| Central AI group serving the teams (#29) | Shared desks: data engineering through the admission ladder, librarian, red team | ADAPT | JUDGEMENT + ladder | EDGE § 4.7 |
| Talent war, non-competes, garden leave | — | REJECT | — | Nothing to buy or retain |
| An LLM "trading firm" that decides trades | — | REJECT | — | #52; rule 8; R06 § 0 |

**Must be code:** risk limits, cut rules, allocation (paper and live proposals), the referee, holdout and trial accounting, standings, budget reservation and attribution, the mechanics of opening and retiring teams, crowding measurement and marginal contribution. **Judgement (models):** hypotheses, research plans, feature, lens and tool proposals, which candidates spend trials, workers' tasks, post-mortems, red-team claims, proposals to open or close units, and notes to the owner. **Owner:** every ceiling, the risk answers, venues, keys, live capital, and any exception.

## 3. A starting organisation (DESIGN)

The owner's words ("agents shouldn't choose anything", "head manager that makes decisions") and PRINCIPLES ("models own the organisation"; no fixed worker
hierarchies by default) meet in one move. Positions are data that the app mints through delegation; the occupants decide research; the app decides
capital, risk and verdicts. **Owner decision:** confirm that "choose nothing" means capital, risk and verdicts (code already holds those), not research.

```text
OWNER (in-process): budgets · risk answers · paper envelope · venues · keys · live two-press · exceptions
PLATFORM (code, never a position): gateway+risk · allocator · referee+holdout · budget treasury · tape · genome bank · canaries · daily report
├─ CHAIR — `operations` (existing role id; frontier; wakes on owner words, schedule, incidents): agenda, owner note, org proposals (claims)
└─ CHIEF RESEARCH — `research` (existing role id; frontier; weekly + boundaries): opens/closes divisions by proposal, sealed assessments
   ├─ Division P  Price history (OHLCV; exists now)          → Team P1 baseline after costs, hourly/daily bars          [active]
   ├─ Division F  Positioning & forced flow (first target)   → Team F1 funding/OI extremes · Team F2 taker/long-short skew [opens at tape+features]
   ├─ Division E  Events & incidents (perception)            → teams by event type                                      [opens in phase 4]
   ├─ Division S  Structural/calendar (CME last half hour)   → Team S1 on prop capital                                  [opens on prop decision]
   └─ Shared desks: Data engineering (admission ladder) · Librarian (claims only) · Red team (contradictions, canary ideas)
   Each team: head (mid-tier model, wakes on verdicts, cuts, data, weekly digest) + 1–3 executors (cheap, bounded, ephemeral)
```

- **Growth by rule:** a division opens when its sources are `O-LIVE`/`O-PIT` with features (or when the owner's decision lands). A division-head position
  exists only once it has ≥ 3 measured teams; until then Chief Research holds it. A team exists only with an envelope carved out of its parent's.
  Order permission stays where it is today: `operations` only, through every gateway gate. No new position receives it.

| Unit | Decides (judgement) | Measured by the app on | Earns |
|---|---|---|---|
| Team (family) | hypotheses, candidates, executors' tasks | forward net returns of its deployments (e-process, posterior); residual marginal contribution (§ 4.4); realised ÷ modelled cost; trials and AI $ spent | paper slots, trials, AI $ share |
| Division | proposals to open, merge or close teams | pooled ρ-adjusted forward record; mean correlation to the rest of the book; AI $ | division share of AI $ |
| Chief Research | division agenda; senior assessments | org-wide forward edge per AI $ (≥ 90 days of record); calibration of its pre-registered forecasts (§ 4.5) | small senior allowance |
| Chair | schedule; owner communication | zero duplicate launches; idle when nothing is useful; cost per wake; age of unresolved items | fixed small share |
| Shared desks | adapters, notes, reviews | conformance passes; contradictions confirmed by measurement | fixed small share |
| Executor | nothing beyond its task | completion and cost, charged to the parent | — |

### 3.1 Size — the honest scaling limits

- **Platforms:** Millennium runs 360+ teams on $97bn+ and Balyasny ~180 on $37bn, so about $0.21–0.27bn of assets per team. At that ratio a $2bn
  platform would run 7–10 teams (INFERENCE from #14). The talent pool is "hundreds", and churn is 15–20% a year (#17, #20).
- **TradeAgent's AI allowance:** the default $5/day research budget is $1,825 a year (EDGE § 8). Observed cost per turn ran from $0.04 to $1.27
  depending on runtime and billing (#55). Multi-agent work costs ~15/4 ≈ 3.75× a single agent's tokens (#49). With three children per manager,
  a 3-level cycle is 1+3+9 = 13 turns against 4 for a 2-level cycle. The 48 turns attempt 2 got before the plan limit would buy ~3.7 deep cycles or 12 shallow ones (INFERENCE).
- **Evidence throughput:** a family needs ≥ 90 forward days (EDGE § 4.7), so S paper slots give ~4·S family-quarters a year. With no prior, the learning
  tier needs a forward Sharpe of 1.88 over a year (R10-calc). Roughly S families can therefore be proven a year: about 6 at the M4 target.
- **Breadth ceiling (INFERENCE):** k streams of Sharpe s at pairwise correlation ρ pool to s·√(k/(1+(k−1)ρ)), which tends to s/√ρ.
  The k that reaches 90% of the ceiling is 4.26·(1−ρ)/ρ: 38 at ρ 0.1, 10 at ρ 0.3, 4 at ρ 0.5, 2 at ρ 0.7. Report the effective number of
  independent teams, k/(1+(k−1)ρ̄), as the org's breadth, not its head-count.
- **Hence:** at most 10 positions, at most 6 families in paper, at most 3 levels awake per cycle, and executors that live for one task. Grow only when the effective
  number of independent teams grows — which takes new information sources (tape, perception) or new model families, not more agents.

## 4. Rules borrowed — concrete app policies (DESIGN; every number is a default to calibrate)

### 4.1 Envelopes — the analogue of payout
- The owner sets the day's AI cap, the paper envelope and the campaign trial budget (all exist: #54, `Db/CampaignStore.cs`). The app splits the AI cap: chair 10%,
  Chief Research 10%, shared desks 10%, teams 70%. Agents can change none of these numbers; the owner can.
- The team pot is split weekly. Every active team, new ones included, gets an equal share of 25% of the pot (the exploration floor). The other 75% is split by
  `max(0, MC_team)`, but only for teams with ≥ 90 forward days; before that the whole pot is split equally.
- **Carry-forward (from #4):** a team past 90 days whose families show no positive posterior contribution drops to the floor. After two quarters at the floor
  with no candidate reaching paper, it is retired: records, trials and memory are kept, the envelope is returned.

### 4.2 Cut rules — two steps, scaled to volatility
- Drawdown is measured on each deployment's own book from its peak, in units of σ_a: the version's backtest annualised volatility at the declared
  size, frozen at deployment.
- **Step 1**, at DD ≥ max(1.0 σ_a, the backtest's 95th-percentile drawdown for the elapsed time). Live: size halved automatically, as app-owned risk
  reduction like a loss limit (the owner confirms this reading once). Paper: no widening and no new slot for the family. The team gets a digest.
- **Step 2**, at DD ≥ max(1.5 σ_a, EDGE § 4.6's 99th-percentile alarm). Retired to watch and the slot released; a restart is a new version with
  trials charged. Live sizes are only ever restored by a fresh proposal and the owner's two presses.
- **Optional live shape (#43):** exposure × (D_max − DD)/D_max. With D_max = 7.5% this is a third at 5% — stricter than halving at 5%.
- No appeal by agents; exceptions belong to the owner, in-process.
- **Why these numbers (INFERENCE, daily Gaussian simulation over one year, 4,000 paths per cell):** a 1.5 σ stop catches 16–21% of zero-skill
  streams, 3–5% at Sharpe 1 and 0–1% at Sharpe 2; a 1 σ step catches 49–56%, 22–26% and 6–9%. If a pod runs ~5% volatility (UNKNOWN), 1σ/1.5σ
  is exactly 5%/7.5%. Kept as absolute percentages, a 7.5% stop at 20% volatility would retire 98% of Sharpe-1 strategies within a year. So the
  cut contains losses and stops gambling to recover (#5, #42); proving skill stays the e-process's job, and the e-process is valid under such stopping (R04).

### 4.3 Incubation and scaling
- Paper is entered only through a verdict or the INCONCLUSIVE quota (≤ ⅓ of slots, one per family; EDGE § 4.5). The first paper size is the envelope's
  minimum; the first live size is the smallest learning-tier step.
- Growth is concave (#32): a raise only on a material rise in the posterior quantile, with hysteresis, at most one step a month, capped at 2/(λ+1) Kelly.
- **Cold start:** a new team gets two weeks of floor budget and 30 trials. New families get the family's shrunk prior, never a penalty for a short
  record (#41).

### 4.4 Marginal contribution — what every allocation reads
- Weekly, from forward daily net returns only (post-freeze, at the app's venue cost model):
  `MC_d = posterior-mean Sharpe(book) − posterior-mean Sharpe(book without d)`. The book uses the allocator's own weights, after regressing each
  stream on BTC's daily return. A team's MC is the sum over its deployments; a division's is pooled. Yield = MC ÷ AI $ over a trailing 90 days.
- No backtest, holdout or E2–E4 figure enters it; those order the queue only (EDGE § 4.5). Teams see their own figures and the team table — the
  outcomes, never the holdout.

### 4.5 Review committee
- The gates are E0–E7 in code. Trials are charged at launch, and the holdout stays sealed (#31: results to management only).
- Senior assessments happen only at consequential boundaries: an L1 proposal, opening or closing a division, a step-2 post-mortem. There are two sealed
  assessments, one challenge, a deadline and a default (COUNCIL.md).
- Red-team claims become measured contradictions that freeze widening (EDGE § 4.6). A reviewer may withhold its own team's trials but cannot block an app verdict.
- **Forecast pre-registration:** at submission, the head records the forward Sharpe and cost ratio it expects (claim table). The app scores that
  forecast with a proper scoring rule once the record arrives, so heads are measured on calibration — a figure no backtest can inflate.

### 4.6 Crowding limits
- The app computes pairwise correlations of forward daily returns among deployments, and of backtest returns for candidates, and clusters them (HRP).
- **Defaults:** at most 40% of paper risk in one cluster; at most 2 deployments per family per instrument. A candidate with ρ ≥ 0.7 to a deployment
  takes no new slot unless its posterior beats the incumbent's by ≥ 10%, in which case it replaces it (BRAIN-style, #35). A feature hash used by more than half
  the book flags the book as concentrated.
- The app, not the agent's label, assigns families (fingerprint plus correlation), so splitting one idea into many families buys nothing.
- **Model diversity:** teams in one division run on different models where the owner's runtimes allow (#53). The org reports its effective number of
  independent teams.

### 4.7 Incentives without gaming (point 5)
| Gaming route | Closed by |
|---|---|
| Overfit the scorer | Fitness is forward-only and app-computed; nobody can fit the future; the holdout never enters (EDGE § 4.5) |
| Clone a winner | A clone's marginal contribution is ≈ 0; clone rule (§ 4.6) |
| Hide failures, pick the window | Trials recorded at launch; windows fixed by the app; e-processes valid under optional stopping |
| Buy Sharpe with tail risk | Cuts in σ_a units; posterior with drawdown alarms; gateway limits |
| Lobby or persuade | Narrative has zero weight in § 4.1 (#39); budgets are computed, never negotiated, so heads cannot collude |
| Edit tests, data or the scorer | Containment and canaries. Telling a model not to is not a control (#51) |

## 5. Anti-patterns and what does not transfer

- **Money as motive.** Agents have no use for pay; resources motivate only because the scheduler enforces them. A "bonus" an agent cannot spend is theatre.
- **Paying on standalone results.** That is netting risk (#8). The investor pays twice for offsetting bets; TradeAgent would pay twice in AI dollars.
- **A centre book run by an agent, discretionary overlays, or per-decision LLM trading.** These put inference on the signal path; R06 § 0 and #52 show
  how they fare out of sample.
- **Copying the org chart as role-play.** Analysts, a bull/bear debate and a "risk team" made of LLM prompts are a costume, not controls (#52).
- **Depth for its own sake.** Coordination burns ~3.75× the tokens (#49), multi-agent failures cluster in coordination (#50), and attempt 2 hit the plan
  limit at 48 turns (#55). Every level added must buy measured breadth.
- **Silos as diversification.** Silos diversify opaque humans. Twenty agents on one model and one dataset are one bet with twenty invoices (#53);
  measure ρ instead of enforcing isolation.
- **Head-count worship.** Platforms add pods because each human brings private sources and low ρ. Without new information sources, more agents
  add cost, not breadth.
- **Rent-seeking and gambling to recover.** Persuasive reports winning budget (#39), and risk raised near a cut (#5, #42). Closed by § 4.1 and § 4.2.
- **Exceptions and appeals.** Millennium reportedly allows exceptions to its cuts (#16). Here only the owner may grant one; an agent's appeal is a claim that no code reads.
- **Absolute stop levels.** The 5%/7.5% figures copied without volatility scaling would kill good crypto strategies (§ 4.2).
- **In-sample fitness.** AgonAlpha reports BRAIN in-sample scores (#36); optimising a proxy is Goodhart's failure [S24]. Only the forward record allocates.
- **Pod economics as the target.** At one platform costs take about half of gross (#17), and TradeAgent's costs exceed its expected learning-tier income in
  year one (EDGE § 8). The useful lesson is giving back budget (#27), not growing the organisation.
- **Same-day destruction.** Pods leave and take their knowledge with them. Here retirement never deletes: the genome bank keeps why the family died.

## 6. Source registry (all fetched or searched 2026-10-02)

- S1 Millennium Management LLC, Form ADV Part 2A, submitted 2026-08-25 — https://files.adviserinfo.sec.gov/IAPD/Content/Common/crd_iapd_Brochure.aspx?BRCHR_VRSN_ID=1056038
- S2 Millennium Management, home page (360+ teams, 7,000+ staff, $97bn+) — https://www.mlp.com/ · S4 Balyasny Asset Management, home page ($37bn, 2,000+ staff) — https://www.bamfunds.com/
- S3 Balyasny Asset Management L.P., Form ADV Part 2A, 2026-03-27 — https://files.adviserinfo.sec.gov/IAPD/Content/Common/crd_iapd_Brochure.aspx?BRCHR_VRSN_ID=1029869
- S5 ExodusPoint Capital Management, Form ADV Part 2A, 2026-08-19 — https://files.adviserinfo.sec.gov/IAPD/Content/Common/crd_iapd_Brochure.aspx?BRCHR_VRSN_ID=1055422
- S6 Point72 Asset Management, Form ADV Part 2A, 2026-03-31 — https://files.adviserinfo.sec.gov/IAPD/Content/Common/crd_iapd_Brochure.aspx?BRCHR_VRSN_ID=1037764
- S7 Two Sigma Investments, Form ADV Part 2A, 2026-03-31 — https://files.adviserinfo.sec.gov/IAPD/Content/Common/crd_iapd_Brochure.aspx?BRCHR_VRSN_ID=1039342
- S8 Citadel Advisors LLC, ADV brochure, 2026-03-31 — https://files.adviserinfo.sec.gov/IAPD/Content/Common/crd_iapd_Brochure.aspx?BRCHR_VRSN_ID=1038247
- S9 SEC press release 2025-15, "SEC Charges Two Sigma for Failing to Address Known Vulnerabilities in Investment Models", 2025-01-16 — https://www.sec.gov/newsroom/press-releases/2025-15
- S10 WorldQuant, "WorldQuant BRAIN" — https://www.worldquant.com/brain/
- S11 SEC IAPD firm record, Millennium Management LLC (relying advisers incl. WorldQuant LLC) — https://adviserinfo.sec.gov/firm/summary/158117
- S12 M. López de Prado, *Advances in Financial Machine Learning*, ch. 1, Wiley 2018 (publisher's excerpt) — https://catalogimages.wiley.com/images/db/pdf/9781119482086.excerpt.pdf
- S13 Z. Kakushadze, I. Tulchinsky, "Performance v. Turnover: A Story by 4,000 Alphas", J. Investment Strategies 5(2), 2016 — https://arxiv.org/abs/1509.08110 · S14 Z. Kakushadze, W. Yu, "How to Combine a Billion Alphas", J. Asset Management 18, 2017 — https://arxiv.org/abs/1603.05937 · S15 Z. Kakushadze, "101 Formulaic Alphas", Wilmott 2016 — https://arxiv.org/abs/1601.00991
- S16 W. Ye et al., "AgonAlpha: Autonomous Alpha Discovery via Prompt Economy and Scalable Agentic Search", 2026-08-04 — https://arxiv.org/abs/2608.11250
- S17 Anthropic Engineering, "How we built our multi-agent research system", 2025-06-13 — https://www.anthropic.com/engineering/multi-agent-research-system
- S18 METR, "Recent Frontier Models Are Reward Hacking", 2025-06-05 — https://metr.org/blog/2025-06-05-recent-reward-hacking/ · S19 Anthropic, "Natural emergent misalignment from reward hacking", 2025-11-21 — https://www.anthropic.com/research/emergent-misalignment-reward-hacking
- S20 M. Cemri et al., "Why Do Multi-Agent LLM Systems Fail?", 2025 — https://arxiv.org/abs/2503.13657
- S21 Y. Xiao et al., "TradingAgents", 2024 — https://arxiv.org/abs/2412.20138 · Y. Yu et al., "FinCon", 2024 — https://arxiv.org/abs/2407.06567 · their post-cutoff results: R07 S8
- S22 F. Wu, E. Black, V. Chandrasekaran, "Generative Monoculture in Large Language Models", 2024 — https://arxiv.org/abs/2407.02209
- S23 J. Kleinberg, M. Raghavan, "Algorithmic monoculture and social welfare", PNAS 2021 — https://arxiv.org/abs/2101.05853 · S24 D. Manheim, S. Garrabrant, "Categorizing Variants of Goodhart's Law", 2018 — https://arxiv.org/abs/1803.04585
- S25 J. Stein, "Internal Capital Markets and the Competition for Corporate Resources", NBER w5101 / J. Finance 1997 — https://www.nber.org/papers/w5101 · S26 D. Scharfstein, J. Stein, "The Dark Side of Internal Capital Markets", NBER w5969 / J. Finance 2000 — https://www.nber.org/papers/w5969
- S27 A. Khandani, A. Lo, "What Happened to the Quants in August 2007?", NBER w14465 / J. Financial Markets 2011 — https://www.nber.org/papers/w14465 · S28 L. H. Pedersen, "When Everyone Runs for the Exit", NBER w15297, 2009 — https://www.nber.org/papers/w15297
- S29 G. Brown, P. Howard, C. Lundblad, "Crowded Trades and Tail Risk", working paper (first draft 2018-01-05) — https://uncipc.com/wp-content/uploads/2019/02/CTTR.pdf
- S30 S. Grossman, Z. Zhou, "Optimal Investment Strategies for Controlling Drawdowns", Mathematical Finance 3(3), 1993 — https://ideas.repec.org/a/bla/mathfi/v3y1993i3p241-276.html · S31 K. Kaminski, A. Lo, "When Do Stop-Loss Rules Stop Losses?", J. Financial Markets 18, 2014 — https://dspace.mit.edu/bitstream/handle/1721.1/114876/Lo_When%20Do%20Stop-Loss.pdf
- S32 G. Aragon, V. Nanda, "Tournament Behavior in Hedge Funds", Review of Financial Studies 25(3), 2012 — https://ideas.repec.org/a/oup/rfinst/v25y2012i3p937-974.html
- S33 R. Aggarwal, P. Jorion, "The Performance of Emerging Hedge Funds and Managers", J. Financial Economics 96(2), 2010 — https://ideas.repec.org/a/eee/jfinec/v96y2010i2p238-256.html
- S34 V. Agarwal, J. Kale, "On the Relative Performance of Multi-Strategy and Funds of Hedge Funds", J. Investment Management 5(3), 2007 — https://www.econstor.eu/handle/10419/57737
- S35 R. Grinold, "The Fundamental Law of Active Management", J. Portfolio Management 15(3), 1989 (not re-read) — https://doi.org/10.3905/jpm.1989.409211 · S36 unused: McLean–Pontiff is cited through R04 § 6
- S37 M. Rubinstein, "Peak Pod", Net Interest, 2023-09-15 — https://www.netinterest.co/p/peak-pod
- S38 Hedgeweek, "Millennium posts rare monthly loss amid index rebalancing trades", 2025-03-10 — https://www.hedgeweek.com/millennium-posts-rare-monthly-loss-amid-index-rebalancing-trades/ · S39 MSCI (Sze, Doole, Gyugyi, Herrera), "Unraveling Summer 2025's Quant Fund Wobble", 2025-10-30 — https://www.msci.com/research-and-insights/blog-post/unraveling-summer-2025s-quant-fund-wobble
- S40 Hedgeweek, "Quant hedge funds see worst drawdown since October as crowded trades unwind", 2026-01-22 — https://hedgeweek.com/news/quant-hedge-funds-see-worst-drawdown-since-october-as-crowded-trades-unwind
- S41 Financial Advisor / Bloomberg, "Multistrategy Hedge Funds Notch Steady Gains Despite August Turmoil", 2024-09-04 — https://www.fa-mag.com/news/multistrategy-hedge-funds-notch-steady-gains-despite-august-turmoil-79395.html
- S42 CNBC, "Citadel to return $5 billion in profit to investors", 2025-12-23 (search summary) — https://www.cnbc.com/2025/12/23/citadel-to-return-5-billion-in-profit-to-investors-source-says.html
- S43 Hedgeweek (citing FT), "Millennium and Citadel post double-digit 2025 gains but lag smaller hedge funds", 2026-01-05 — https://www.hedgeweek.com/millennium-and-citadel-post-double-digit-2025-gains-but-lag-smaller-hedge-funds/ ; Balyasny and Point72 figures from a search summary of https://finance.yahoo.com/news/2025-returns-hedge-funds-rolling-234728986.html
- S44 Hedgeweek (citing WSJ), "Multi-manager hedge funds escalate talent war…", 2025-06-17 — https://hedgeweek.com/news/multi-manager-hedge-funds-escalate-talent-war-arms-race-with-nine-figure-pay-packages
- S45 Bloomberg, "Growing List of Hedge Fund Passthrough Fees Cuts Into Client Profits", 2025 (403; search summary only) — https://www.bloomberg.com/graphics/2025-hedge-fund-investment-fees/
- S46 Long Angle (citing Aurum and JPMorgan via Reuters), "Multi-Strategy Hedge Funds", 2026-08-20 — https://www.longangle.com/alts-education/multi-strategy-hedge-funds
- S47 Resonanz Capital, "The Center Book: The Quiet Heart of Multi-Manager Hedge Funds", 2025-12-22 — https://resonanzcapital.com/podcast/the-center-book-the-quiet-heart-of-multi-manager-hedge-funds
- S48 Gend (summarising OpenAI's Balyasny case study of 2026-03-06), 2026-03-30 — https://www.gend.co/blog/balyasny-ai-research-engine-gpt-5-4
- S49 Acquired, "Renaissance Technologies", 2024-03-17, drawing on G. Zuckerman, *The Man Who Solved the Market* (2019) (search summary) — https://www.acquired.fm/episodes/renaissance-technologies
- S50 Community BRAIN tooling (not WorldQuant documentation) — https://github.com/hardikishappy/wq-alpha-research
- Not read (SSRN returned 403): Mollica, "Pod Platforms: A Perfect Filter, or a Perfectly Coarse One?" (SSRN 7194098); Blotnick, multi- vs single-manager (SSRN 5438154). Morgan Stanley IM's multi-manager paper and the OpenAI case study were also blocked (403).
- REPO at `1275aff`: `docs/PRINCIPLES.md`, `docs/EDGE-FACTORY.md`, `docs/COUNCIL.md`, `BUILD-STATUS.md`, `src/TradeAgent.Core/Council.cs`, `src/TradeAgent.Core/Db/{AiAttemptStore,EnvelopeStore,AllocationStore,CampaignStore}.cs`, `src/TradeAgent.AgentRuntime/TurnMeter.cs`; R04, R06, R07, R10 in this folder.
