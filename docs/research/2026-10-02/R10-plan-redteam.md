# 10 — Red team of `docs/EDGE-FACTORY.md` (2026-10-02)

Adversarial review leg, read-only; nothing in the repo was changed. Read: the plan in full; the working-tree amendments to `PRINCIPLES.md`,
`COUNCIL.md` (rule 8 note), `HOW-WE-BUILD.md`, `RESUME-HERE.md` and `manager-prompt.md` as they stood at ~03:40; `CLAUDE.md`; COUNCIL rules 1–10 and
the v1 language spec; R01–R07 in full (R08 not yet written); the six `docs/queue/` briefs; the 2026-09-27 factory plan §§ 4, 10, 17, 25 and headings;
the BUILD-STATUS records of attempt 2. **Labels:** SOURCE = read in the tree today (not runtime-verified); CALC = `research/redteam-calc/cascade_power.py`
in this scratchpad (same IID-normal, 365-obs/yr assumptions as `R04-calc`); INFERENCE otherwise.

## Verdict

The plan is right about the kernel, the tape and honest measurement, and wrong about the order. Its own gates make every honest stage past paper
unreachable for ~2 years at the data the system will have, so at that order the fleet builds 40-odd units of measurement before it is aimed at the
one edge it can actually reach. Its money thesis (breadth across hundreds of assets, event families, shorts) is not expressible in the language it schedules.
The three changes that matter most: (1) put resampling and archive depth before M0 attempt 3; (2) give every candidate that cannot be judged one honest,
capped forward-incubation path; (3) aim first at positioning features on the tape universe, and ask the owner now about venue, capital and the prop-firm path.

## Findings, ranked

### 1. HIGH | §4.5, §4.1, §9 M3, §10 last paragraph | The cascade cannot pass a real edge for ~2 years, M3 is unreachable as written, and the forward-only footnote is a bypass
- **Problem.** In months 0–12 the system has 12 months of spot klines, at most 139 days per run, a tape that starts today and a 3-month holdout. At that
  depth E3 rejects true edges almost surely, and E2 and E4 barely discriminate. The only non-price history with depth is the Binance metrics archive
  (2020-09→), and by the plan's own R07 rules it is O-ARCH: exploratory, never a verdict. So M3 ("a v2 strategy with a non-price feature passes E1–E4")
  cannot happen for about two years. Meanwhile §10 lets a candidate "contaminated for its model features" skip E1–E4 entirely. An evolving population will
  learn to attach a perception feature to get past the referee (R05 row 14 warns of exactly this).
- **Evidence.** CALC: P(DSR ≥ 0.95 | true SR 2) is 0.06 / 0.03 / 0.01 at 1 y (N_eff 20 / 50 / 200), 0.24 / 0.14 / 0.06 at 2 y, and 0.52–0.82 only at 5 y.
  E2's "≥ 6/8 sub-periods positive" passes noise 14% of the time and a true SR 1 over a year 40%. On a 91-day holdout, E4's z ≥ −2 tolerates 4.0 Sharpe
  units. PBO's authors suggest rejecting above 0.05 (R04 §2.3); a no-skill family sits near 0.5, which is the plan's gate. Binance metrics were backfilled
  pre-2021-12 on 2026-03-18, and timestamps moved on 2026-06-25 (R03 §2.1), so the source is not immutable ⇒ O-ARCH (R07 §5.1, §5.3). U-tape item 2
  makes fetched history O-ARCH by default. The same vendor's klines are evidence-grade today, so the class rule is incoherent across datasets.
- **Fix.** (a) §4.5: a gate that cannot discriminate at the available clean depth is SHOWN, not gated. DSR orders the queue for paper slots; the hard gates stay
  at E6/E7. (b) One INCONCLUSIVE path for every candidate, whatever the reason: a trial-charged forward-incubation quota (e.g. ≤ ⅓ of paper slots and
  ≤ 1 per family at a time), so a model feature buys nothing. (c) §4.1: one rule for exchange-published prints (klines, funding, OI). They are O-PIT
  when the vendor checksum verifies and our own O-LIVE overlap matches within a measured revision rate, with the timestamp convention pinned per file
  date; otherwise O-ARCH. (d) M3 → "≥ 3 non-price candidates in forward incubation with e-processes running". (e) §6.5 must name this as a change to
  paper admission: "semantics kept" stops being true.

### 2. HIGH | §9 phases 0–4 | Resampling and archive depth come too late; M0 attempt 3 will repeat attempt 2's fee death
- **Problem.** v1 steps every 1-minute bar with ≤ 500 bars of lookback (~8.3 h) and halts at 200,000 traced bars (~139 days). §4.5 itself says to favour
  hours-to-weeks horizons, yet resampling and depth sit in phase 3, behind U-features → U-language-v2 → U-archive-depth. After phase 0, M0 attempt 3 will
  meet a venue-cost referee with only minute-scale strategies to offer, so its verdicts come back refused and the paper arrow never closes. Every run is a
  trial (R01 A3), so the cap also multiplies trials: the referee's own refusal tells agents "a year is four runs".
- **Evidence.** SOURCE `StrategyLimits.cs:64,71`, `Backtest.cs:328`; R01 A2 (`timeframe` is "hashed and stored, never used to resample"). BUILD-STATUS.md:6919–6923:
  attempt 2's model-authored version ran 321 trades in a quarter, with fees of 3,597.03 on 10,000. The Research agent's own diagnosis was "turnover/friction.
  Fees are 61.92% of …".
- **Fix.** Add a phase-0 unit, `U-timeframe`, before M0. The feed resamples closed 1m bars to the declared timeframe; decisions are taken at its close and
  fills stay at the next 1m open; the cap counts evaluated bars; the FillSemantics version is bumped with golden vectors. Wave B = U-venue-verify,
  U-evidence-identity, U-tape-archive. Wave C = U-archive-depth (5 y), U-features, and `U-language-v2a` (FeatureRef + required fields only; triggers, short
  side and sizing go in v2b). U-trial-returns and U-referee-v2 depend on U-timeframe and U-archive-depth. U-decision-port comes after: U-tape-events
  keeps the raw-item clock running, and answers from jev-1.13.0 on those items remain A-PRE later while the pin is served (R07 §5.2).

### 3. HIGH | §2, §4.4, §8 | The money thesis is not expressible or sourceable in this plan
- **Problem.** §2's edge thesis rests on breadth ("across hundreds of assets") and on event families: #1 hacks, #2 unlocks, #5 delisting flags. But v2 stays
  single-instrument, the paper envelope covers one symbol, and the paper book holds one position per symbol. One version per asset means each one is a
  trial (inflating DSR's N) and sees only a handful of events, so none reaches E1 ≥ 30 trades or E5 ≥ 50 round trips. #1, #2 and #5 also need shorts (phase 6
  plus the owner's venue choice) or paid data. §8 forbids paid data "until a forward result pays", and no forward result can exist without the data. #5's Binance
  stream is signed, which needs an account, and new EU registrations have stopped (R03 §1). No unit sources exploits or unlocks at all.
- **Evidence.** Plan §4.4; R01 A2 "Multi-instrument"; SOURCE `PaperBook.cs` `paper_position (symbol TEXT PRIMARY KEY)`; R03 §4 rows 1, 2, 5 and §6
  ("v1 can express none of the top edges except a long-only slice of CME intraday momentum").
- **Fix.** §2 names the first target: hunt #3 (positioning on the six-symbol tape universe, long/flat), the only hunt with free, deep data, no new venue and no
  perception. Add to v2 (or a `U-universe` card) "one program over a declared universe: one trial, positions per member, per-member caps". Replace §8's
  rule with "one paid source per hypothesis family may be trialled for ≤ 1 month inside an owner data budget, charged to that family". Mark #1, #2 and #5
  as gated on §10.1 (shorts).

### 4. HIGH | §8, §4.5–4.6, §10 | Under its own defaults the system cannot pay for itself for 2–3+ years, and the plan never says so
- **Problem.** L1 is capped at ≤ 5% of capital. E7's e-LOND at α 0.10 with γ = 1/10 needs E ≥ 100 on its first test. The operating bill leaves out the Codex
  subscription that PRINCIPLES counts as a cost.
- **Evidence.** CALC: at SR 2 and 25% volatility, the L1 slice earns about $250/yr on $10k and about $2,500/yr on $100k, against the $2,520/yr in §8. E ≥ 100
  takes 2.3–3.1 y at SR 2, 4.1–5.5 y at SR 1.5 and 9–12 y at SR 1. A forward-only candidate (prior mean 0, τ 0.5) needs a forward SR of 6.9 over 90 days, or
  1.9 over a year, to reach L1's P ≥ 0.80. §4.5 quotes R04's MinTRL figures, while R04 §4 notes that anytime validity costs 2–4× as long.
- **Fix.** §8 gains an owner-facing table: earliest L1, earliest L2 and break-even capital under the defaults. §10 gains decision 0, capital and horizon, framed as
  the trade-off: a larger learning tier means money sooner and more risk. Ask §10.2 (the prop path) now. R03 #4 is the one top edge v1 can express, it has
  16+ years of honest 1m history (Databento, within its $125 free credit), and it trades firm capital. Add a CARD, `U-cme-archive`, conditional on a yes.

### 5. HIGH | §4.1, §4.5, §6, §7, §9 phase 5 | Advisory controls described as hard until containment, which the plan pushes behind M0
- **Problem.** The CLI agent runs unconfined as the app's user, so a long list of the plan's protections are advisory, not hard: E4's "sealed" holdout figures and
  "blind" holdout, "the collector is the only writer", a hash-chained ledger that "nobody can backfill", and §7's "scrape against terms" and "train any
  model on Jev's answers". The agent can:
  - read the referee rows and holdout CSVs in `state/`, or download the same public months from data.binance.vision;
  - write `state/tape.db` and recompute an unanchored chain;
  - scrape outlets whose terms forbid AI processing;
  - fit a Jev surrogate from the recorded answers. That breach is uncapped and survives termination (MCA §2.3(b), §12), and the plan's own cost-per-yield
    pressure invites it.

  Meanwhile containment sits at the head of a 13-unit chain that runs "alongside" other work.
- **Evidence.** SOURCE `Containment.cs:43` ("can read and write whatever that user can"); PRINCIPLES ("a process group, a job object or a prompt instruction
  alone does not establish that protection"); BUILD-STATUS.md:7019 ("no verdict of this run is protected evidence"). The 09-27 plan §25.2 ran the
  containment spike in parallel with M0.
- **Fix.**
  - §4.5/§6.5: every verdict before containment is labelled "unprotected evidence" and cannot count toward E7.
  - §7: mark the scrape and Jev items "advisory for agent-initiated actions until containment's egress policy".
  - Dispatch `R-containment` in Wave A; it is a spike on the Windows box with no file overlap.
  - Make external anchoring the default (only a hash leaves the machine).
  - Agents read Jev answers through a bounded op, never in bulk.

### 6. HIGH | §4.1, §4.2, §6.8 + existing code | The creative zone can choose where the owner's key is sent
- **Problem.** "No key ever reaches the creative zone" holds, but the destination of a key is data the creative zone can write. `RuntimeCatalog.Read()` re-reads
  `%LOCALAPPDATA%\TradeAgent\runtimes.json` on every call, and an override replaces a built-in by id, `BaseUrl` included. The harness posts `Bearer <key>`
  to `manifest.Endpoint`. The unconfined CLI can rewrite that file and receive the owner's provider key. U-decision-port copies the pattern
  (`decision-models.json`, hosts as data), and U-tape adds `tape-sources.json` for future keyed sources.
- **Evidence.** SOURCE `RuntimeManifest.cs:384, 716–728`; `ApiConversation.cs:468–472`; `Containment.cs:43`. NOT runtime-verified.
- **Fix.** A fix unit now, outside the phases. A keyed endpoint's host comes only from the built-in row. An override that changes it clears the held key and
  shows the new host for an in-app confirmation. Red test: "an override host never receives the key"; mutant: drop the host check. Add the same item to
  U-decision-port and to every keyed source.

### 7. HIGH | §6.1, §4.4, U-decision-port; COUNCIL rule-8 note | The rule-8 reading is sound on replay and broken on timing
- **Problem.** §6.1 holds for replay: answers are recorded and the runner never calls a model. But it names as protected that "no generative or agent output can
  place, alter or TIME an order". §4.4's "evaluation triggered by an observation's arrival" breaks that if an annotation counts as an observation, and so does
  admitting `LlmJsonDecision`, a generative model's verbalised distribution, as a strategy input. The "misleading price print" analogy also understates the
  attack. A false print costs capital to produce; a text item costs nothing to publish; and Jev's injection recall is 50% at 0.5 (R02 §3).
- **Fix.**
  - §4.4: triggers fire only on raw O-LIVE observations. Evaluation runs at arrival plus a declared fixed delay and reads whatever answers exist by then,
    so model latency cannot time an order.
  - Only bounded-decision instruments and frozen artifacts may feed a live-eligible version; LLM-verbalised instruments stay research/paper-only.
  - A text-derived entry needs ≥ 2 independent sources, or a per-item influence cap in the frozen rules.
  - Amend the COUNCIL note to state these three constraints.

### 8. HIGH | §10.1, §4.5, U-cost-model | The venue decision blocks phase 0's meaning, not only phase 6
- **Problem.** U-cost-model pins a venue cost model at campaign open and binds evidence to it. Fees decide which strategies survive: Binance spot charges 0.10%
  taker, Kraken spot tier 1 charges 0.40%/0.80% and perps 0.02–0.05% (R03 §1); Revolut X fees are UNKNOWN. Months of paper evidence judged under Binance's
  costs become invalid on another venue (rule 9), and re-judging spends verdicts (three per lineage). The first tape source was measured from Belgium, and
  USDⓈ-M availability per country is UNKNOWN (R03 §7).
- **Fix.** Ask §10.1 now, in one message: country, plus the intended spot and perp venues. Judge under the intended venue's model, or under the dearest
  candidate venue, from the first campaign onward. Verify the tape's endpoints from the owner's laptop as part of M1.

### 9. MED | §9 Parallelism | Three builders contradicts `HOW-WE-BUILD.md:18` ("At most two heavy legs at once")
The working tree leaves line 18 unchanged, and RESUME-HERE item 7 says three. Memory (2026-09-07) records that three Opus legs hit the session limit in about
2 h. Builders' full suites overlap and flake `Timing` (HOW-WE-BUILD.md:80). Wave A also overlaps on files: `PaperConnector.cs` (cost-model item 4 and
runner-forward item 3), `SettingsView.cs` (cost-model and tape), `GatewayPipeServer.cs`, and CONTRACTS, USER-GUIDE and RESEARCH-REQUIRED in all three
units. **Fix:** either amend HOW-WE-BUILD with the measured cost, or run two heavy builders plus R-containment as a light third, and stagger the doc edits.

### 10. MED | §4.7, §5, U-desk-yield, U-mission-v2 | Code-owned desks, and fitness that cannot be measured in year one
Code seeds, budgets, retires and re-seeds "desks". That is a fixed role list in data form, against PRINCIPLES ("models own the organisation", "do not grow a
fixed list of roles") and the 09-27 plan §10.4 ("do not rebuild `CouncilRoles.All` as `WorkerRoles.All`"). "Forward-validated edge per AI dollar", model
routing and ensemble fitness take 1–3 years to measure (R04 §4). Until then they track backtest noise (R² < 0.025 from backtest Sharpe, R04 §4).
**Fix:** code sets resource envelopes from measured cost and E5 standings only; models create, merge and retire desks through delegation; desk seeds are
suggestions in the mission text; defer U-desk-yield and fitness feedback until a family has ≥ 90 days of forward record.

### 11. MED | §4.5 "Prove processes, not versions" | Pooling assumes uncorrelated streams, but variants within a family correlate
CALC: four streams with ρ 0.7 pool to SR 1.14, not 2.0 (with ρ 0.3: 1.45). **Fix:** define a family as one mechanism applied across uncorrelated
assets or events, pool across correlation clusters, and quote the ρ-adjusted figure.

### 12. MED | §10.6, §4.6 | Owner questions in statistical jargon, and live proposals by random draw
The owner is asked for α, a drawdown spec and a Kelly basis, and the plan says they "block E5–E7 defaults", which is ambiguous. Live allocation by "weekly
Thompson draw" makes proposals flip at random and costs the owner two presses a week. **Fix:** two plain questions ("the largest fall you accept, and how
sure"; "what share of the account goes to strategies still being proven"); defaults apply until changed. Live proposals become deterministic (a posterior
quantile with hysteresis) and are raised only on a material change; Thompson stays for paper.

### 13. MED | §10 | Missing owner asks
- Capital and horizon (#4).
- Paper envelope size: the incubator needs it, and attempt 2's envelope allowed one deployment (BUILD-STATUS.md:6800).
- Laptop uptime: U-tape claims nothing while the app is closed, so a sleeping laptop punches daily holes in moat #1. Offer an in-app "keep awake while
  recording" press.
- ChatGPT's training setting for the research IP.
- A Binance account, if the signed announcement stream is wanted.
- Re-pasting two keys after every self-update restart until containment. Batch releases to the owner's machine.

### 14. MED | §4.2 lenses, §10.3 | Moat #3 and the owner's documents leak to vendors
"Hypothesis novelty against the genome bank" and "agent-trace audit" send moat #3, and traces that can quote inbox material (owner documents), to TypeSafe
via OpenRouter. TypeSafe keeps a perpetual right to process Customer Data as Telemetry, "classifications" and "learnings" included (R02 §2, MCA §4.1–4.3);
OpenRouter's model terms are UNKNOWN. §10.3 names only the SCC transfer. **Fix:** run genome-bank and trace lenses in code or locally only. §10.3 should also
name the telemetry right, the uncapped §2.3 liability, OpenRouter's unknown terms and ZDR.

### 15. MED | §4.2, §7 | The Jev training restriction is misstated
"TypeSafe's agreement prohibits training any model on Jev's answers" misreads R02 §2. The agreement bars distillation and "train[ing] a model to imitate the
output"; Jev probabilities used as inputs to our own outcome models are "clearly permitted". Read literally, §7 forbids the plan's own calibration maps and
meta-labelling. **Fix:** "never distil or imitate Jev (MCA §2.3(b)); its probabilities as inputs to models of market outcomes are permitted (R02 §2(a))".

### 16. MED | §3 | The moat is overclaimed
- (a) "The only leakage-free semantic history that will ever exist": R07 S5–S7 point-in-time model vintages give clean-by-construction historical annotations
  (smaller models).
- (b) The market-data half of the tape can be bought today (Tardis.dev: 7+ years of funding, OI and liquidations, R03 §2.1).
- (c) "Nobody can backfill" needs anchoring, which the plan makes optional, plus containment.
- (d) Calibration capital resets with each instrument version (jev-1.13.0 exists two weeks after launch, with no deprecation policy, R07 §3).

**Fix:** rewrite §3 to claim only the unbuyable parts: A-LIVE answers on first-seen unstructured items, the anchored forward ledger and the genome bank.
Make anchoring the default, and shadow-run a successor instrument before a pin retires.

### 17. MED | §9 phase 4 dependencies | U-incubator waits on perps; spot needs per-deployment books, and the runner cost grows with age
U-incubator depends on U-paper-perps, but spot paper needs per-deployment books first: two versions on one symbol net each other's positions (R01 A2).
The runner also replays O(age) per pass per deployment, which U-runner-forward accepts. By day 90 that is about 130k 1m bars × 12 deployments every
minute (INFERENCE; the brief measures one pass). **Fix:** a `U-paper-books` unit (per-deployment virtual books, spot) before U-incubator; the runner
evaluates at the declared timeframe (#2) or caches snapshots.

### 18. MED | PRINCIPLES working-tree amendment | The owner's judgment is overstated
The amendment says the owner "judged the need demonstrated for … derivatives venues and an evolutionary selection layer". The owner's quoted words name
evolution, copy trading, news and scraping, not derivatives venues, and §10.1 is still open. **Fix:** quote the owner and attribute the derivatives
inference to the manager, "pending §10.1".

### 19–27. LOW
- 19 | §4.8, §11 | R08 is cited but not written yet.
- 20 | §2 #3 | "Liquidations … the free archive reaches 2020-09": R03 §2.1 says no complete free liquidation history exists, so record forward.
- 21 | §2, §8 | "≈ $3/day for 50,000 items" assumes 30k social posts a day (R02 §6.6). X reads cost $0.005 each (≈ $150/day, R03 §2.4), and several outlets'
  article bodies are excluded by their terms. Cost the item supply, not only the reading.
- 22 | §3 | "Lose 51–62% of their Sharpe past the cutoff" omits R07's caveat that the periods differ (regime change is mixed in) and S6's no-gap result.
- 23 | U-local-decider, `LlmJsonDecision` | A fallback that is a different instrument cannot stand in for Jev in any version bound to Jev, and the local
  runtime is a 10-day-old, one-maintainer binary (R02 §4). Defer until a version binds to it or a schema is too sensitive to send out.
- 24 | §4.5 canaries | "Fill-at-mid cheater" and "holdout toucher" cannot be written in the DSL (the app chooses fills and audience is enforced). They are
  referee and reader tests. Running them on a schedule adds nothing for deterministic code; run them on every change and in CI.
- 25 | §4.1 sources | Condition Kalshi reads on §10.1 (geo restrictions and ISP blocks, R03 §1, §2.7). Hyperliquid's leaderboard is an undocumented endpoint.
  Wallet features stay at address and aggregate level (EDPB 02/2025, R03 §5).
- 26 | §4.6 | The "deterministic" allocator's Thompson draw must be seeded. "≥ 25% kept for exploration" must say births or paper slots, never live capital.
- 27 | §9 phase 5 | The 13-unit chain is fixed, but the 09-27 plan §25.5 says to re-plan after M0, the runtime profiles, the containment record and worker
  identity. Brief only R-containment and U-worker-identity now.

## Spot-checks: plan claims against the cited research

| # | Claim (plan §) | Source | Verdict |
|---|---|---|---|
| 1 | Noise reaches Sharpe ≈ 3 with 200 trials on ~12 months (§0) | R04 §0; `R04-calc` 2.77 (1 y) – 3.20 (9 mo) | SUPPORTED |
| 2 | Six blockers, four briefs (§0) | R01 §0 | SUPPORTED |
| 3 | Governance tokens −27% in 24 h, most of it after the announcement (§2) | R03 §3.3 (~36% before) | SUPPORTED |
| 4 | The free archive reaches 2020-09, liquidations included (§2) | R03 §2.1 | PARTLY: no liquidations; the 2026-03-18 backfill is omitted |
| 5 | BTC funding ~3%, a quarter of prints negative (§2) | R03 §3.2 (2.95%, 25.9%) | SUPPORTED |
| 6 | Equity signal Sharpe 6.5 → 1.2, gone at 20 bp (§2) | R03 §3.1 (6.54 → 1.22) | SUPPORTED |
| 7 | The one public Jev news test lost after costs (§2) | R02 §5 (−18.4 bp; unreviewed) | SUPPORTED (unreviewed source) |
| 8 | $0.042/M tokens ⇒ ≈ $3/day for 50k items (§2, §8) | R02 §1, §6.6 ($2.94) | SUPPORTED arithmetic; item supply uncosted |
| 9 | Signals inflated +4% to +70%; agents lose 51–62% of Sharpe (§3) | R07 §0 | SUPPORTED; caveats omitted |
| 10 | "The only leakage-free semantic history" (§3) | R07 §2 (PIT vintages) | CONTRADICTED in part |
| 11 | "Prohibits training any model on Jev's answers" (§4.2, §7) | R02 §2 | OVERSTATED: imitation/distillation only |
| 12 | 0.20% taker RT ⇒ 73%/yr drag at one round trip a day (§4.5) | R04 §7 | SUPPORTED |
| 13 | 0.7–2.7 y forward; four streams ≈ 4× faster (§4.5) | R04 §4 | MinTRL with uncorrelated streams only; e-process figures are 1.5–6 y |
| 14 | PBO ≤ 0.5 as a gate (§4.5) | R04 §2.3 vs §8 | INCOHERENT within R04 (the authors use 0.05) |
| 15 | The Codex plan ran out in ~2.5 h (§8) | BUILD-STATUS 2026-10-01 (~15:47Z → 18:08Z) | SUPPORTED (~2.4 h) |
| 16 | Revolut X MiCA; Binance none; Zenit refuses BG/HR/MT/RO/SI (§4.8, §10) | R03 §1 | SUPPORTED |
| 17 | Venues per R08 (§4.8, §11) | — | UNSUPPORTED until R08 lands |

## What the plan gets right (do not break)

- **Phase 0 first**, with M0 after it: six SOURCE-checked blockers are fixed before the owner's allowance is spent (R01 §0). The four briefs are precise
  and red-first.
- **The tape as its own append-only store**: three times per datum, revisions never overwritten, a row per fetch, readers that require an audience. Its
  INSERT-OR-REPLACE mutant is the right guard.
- **"Nothing in the evidence path calls a model"**: replaying recorded answers gives determinism over a vendor that does not promise it.
- **One app-owned venue cost model**, pinned at campaign open and hashed into evidence; the submitter never chooses the judge's friction.
- **An evidence-based "do not hunt" list**: latency races, naive sentiment, per-decision LLM trading and mirroring leaders.
- **Evidence classes only ever downgraded**, computed by the app and never asserted by an agent. Measurement and claim stay in separate tables.
- **Live capital stays an owner two-press**, bound to a digest. Paper never confers authority; paper → live is a fresh proposal.
- **Canaries through the real referee on every referee, data or cost change**: the right answer to a population that will hunt simulator bugs.
- **An honest economics headline in the app** (break-even, unknown ≠ zero, perception and data reserved before spend); the legal exclusions are explicit,
  and the owner decisions are kept as asks.
