# 04 — Evidence machinery for an evolving strategy population

Research leg, 2026-10-02. Question: how can TradeAgent run an AI "evolution" of strategies at scale and still tell the truth?
**Labels.** `[DOC n]` = read on 2026-10-02 at source *n* (URL in § Sources; primary paper, docs or this repo). `[SEC n]` = secondary
source. **INFERENCE** = my derivation or arithmetic from DOC inputs (scripts in `R04-calc/calc*.py`). **DESIGN** = proposal.
**UNKNOWN** = not verified. Repo facts come from `docs/CONTRACTS.md`, `docs/STRATEGY-LANGUAGE.md`, `Strategy/Backtest.cs` and `Trading.cs` [DOC R].

## 0. Bottom line

1. **At today's data depth, evolution cannot tell the truth (INFERENCE).** The campaign allows 200 trials [DOC R]. On a
   9–12-month research window, the best Sharpe expected from 200 *zero-skill* trials is 2.8–3.2 (annualised). A ~3-month
   holdout cannot certify one strategy below an observed SR of ≈ 4 at 97.5% (§2.2). The biggest lever is archive depth:
   with 5 years of data, an SR of 2 survives deflation against 200 effective trials (DSR 0.96).
2. **"Net after costs > 0" is currently empty.** The referee's default execution model is `ExecutionModel.Frictionless`, and
   the paper connector's fee and slippage both default to 0 [DOC R]. Evidence needs a cost model the app owns and
   calibrates to the venue.
3. **Forward evidence is slow.** At SR 1–2, showing SR > 0 takes 0.7–2.7 years of daily net returns, testing one strategy
   alone. It takes longer under multiplicity (§4). Temporary edges are therefore testable only as a *process*: the pooled
   returns of a family, not one version. Live capital should come in two tiers. A small, owner-approved learning tier is
   sized by the posterior. A proven tier requires an FDR-controlled discovery.
4. **Three controls matter most:**
   - an app-owned venue cost model, applied identically in backtest, holdout and paper;
   - the deflated Sharpe ratio, with the effective number of trials N_eff taken from clustering *every* trial's return
     stream;
   - anytime-valid e-processes on forward net returns, with e-LOND (valid under arbitrary dependence) at the live boundary.
5. **Allocation:**
   - Thompson-gate the shrunk posteriors: a strategy whose draw is ≤ 0 gets no new capital that period.
   - Shape the survivors with HRP. Size the total with a fractional Kelly cap derived from risk-constrained Kelly (RCK):
     about 0.14–0.27 of Kelly for typical owner drawdown specs.
   - The fitness fed back to evolution is a strategy's marginal contribution to the ensemble, not its standalone Sharpe.

## 1. Automated discovery systems, 2023–2026

| System | Loop: generator → evaluator → selection | Shown out of sample | Cost | Reported failure modes / limits |
|---|---|---|---|---|
| AlphaEvolve [DOC 1,2] | Gemini Flash (breadth) + Pro (depth); prompt sampler; **evaluation cascade** (stage k runs only if every earlier stage is promising); optional LLM-judged criteria; program DB inspired by MAP-Elites + islands; multi-score | Verified problems only: 4×4 complex matmul in 48 multiplications; 0.7% of Google fleet compute recovered; 23% kernel speedup (1% of Gemini training time); ~75% of 50+ open math problems matched, ~20% improved | single evaluations ~100 compute-h, run async | needs an automated evaluator; not for domains that can only be partly simulated |
| FunSearch [DOC 3] | PaLM 2 proposes programs; evaluator scores; island-based pool for diversity | largest cap sets in 20 years; better bin-packing heuristics | UNKNOWN | the evaluator is what guards against hallucination |
| Georgiev, Gómez-Serrano, Tao, Wagner 2025 [DOC 6] | AlphaEvolve on 67 problems | mostly rediscovered the best known; several improved | — | evaluator exploits: UNKNOWN (abstract silent) |
| OpenEvolve / ShinkaEvolve / AVO [DOC 4,5,7] | OpenEvolve (open source): MAP-Elites + ring-migration islands + cascade evaluation. Shinka: parent sampling balanced for exploration, code-novelty rejection sampling, bandit choice of LLM. AVO (Mar 2026): agents as the variation operators | circle packing at state of the art with 150 samples (Shinka); attention kernels up to 3.5% faster than cuDNN (AVO) | Shinka targets sample efficiency | earlier systems needed thousands of samples |
| Alpha-GPT [DOC 8] | human–AI ideation → implementation → review (EMNLP'25 demo) | detail UNKNOWN | — | — |
| AlphaAgent (KDD'25) [DOC 9] | idea / factor / eval agents; regularisers: max **AST** similarity to an alpha zoo, LLM-judged hypothesis–factor alignment, complexity (length, free parameters) | test 2021–24 with costs (CSI500: 0.05% buy / 0.15% sell; US: 0.05% sell). CSI500 11.0% ann., IR 1.49; S&P500 8.7%, IR 1.05 | GPT-3.5; 30% fewer tokens than RD-Agent | Alpha158 and GP factors decayed to IC ≈ 0 over 2021–24 (crowding) |
| R&D-Agent(Q) [DOC 10] | hypotheses → Co-STEER code → feedback; contextual Thompson-sampling scheduler (factor vs model work) | CSI300 test 2017–Aug 2020: IC 0.053, ARR 14.2%, IR 1.74, MDD −7.4% (Alpha158 ARR 5.7%); extra check 2024–Jun 2025 | < $10 and ~12 h per run, 44 loops | "relies solely on the LLM's internal knowledge". The test window lies inside LLM pretraining: possible leakage (INFERENCE) |
| QuantAgent / AlphaForge / AlphaGen / Chain-of-Alpha [DOC 11–14] | QuantAgent: writer/judge loop + market-feedback loop. AlphaForge: generative-predictive mining + dynamic weights. AlphaGen: RL rewarded by the **combination** model's gain. Chain-of-Alpha: generation and optimisation chains with candidate/deprecated pools | claims beats baselines (detail UNKNOWN) | — | fixed factor weights fail as markets drift (AlphaForge); mining factors one by one ignores how they combine (AlphaGen) |
| Hubble / AlphaMemo / FaVOR / AlphaEval (2025–26) [DOC 15–18] | Hubble: AST sandbox, family-aware selection, similarity penalty. AlphaMemo: memory of edit motifs with a veto on known failures. FaVOR: decompose and validate hypotheses. AlphaEval: 5-dimension score, no backtest needed | Hubble holdout Jun 2025–Mar 2026: 4 factors stayed significant, 1 trend factor decayed | Hubble: 104 candidates, 0 crashes | redundant rediscovery; overfitting by reusing successes (AlphaMemo); return-chasing breaks across regimes (FaVOR); backtest-only scoring misses stability and diversity (AlphaEval) |
| GP / QD baselines [DOC 19,20] | gplearn: tournament, crossover, subtree/hoist/point mutation, parsimony coefficient. MAP-Elites: one elite per behaviour cell | — | cheap | bloat (fought with the parsimony coefficient) |
| Production analogues [DOC 21,22] | 101 real alphas: holding 0.6–6.4 days, mean pairwise correlation 15.9%. Numerai: a stake-weighted meta-model; TC = gradient of the optimised portfolio's return with respect to a model's stake; MMC = covariance with the target after neutralising to the meta-model | live tournament | — | rewards *non-redundant* contribution |

**Lessons (INFERENCE).**
- Every success rests on a cheap evaluator that is (nearly) noiseless and hard to game. A trading backtest is a *noisy
  estimate*, so selecting on it produces a winner's curse. Fitness must be deflated and re-tested on fresh data.
- Cascades are standard practice.
- Diversity machinery (islands, MAP-Elites, novelty rejection, AST originality, family quotas) pays twice: uncorrelated
  streams for allocation, and an honest N_eff.
- None of the LLM alpha-mining abstracts reports a trial count, a DSR or a PBO; their full texts are UNKNOWN.
- Pretraining look-ahead is real. Standard LLMs show it; point-in-time models generalise better [DOC 23]. One news-to-returns
  test found it modest [DOC 24]; its effect on *strategy generation* is UNKNOWN. A holdout that ends before the generating
  model's knowledge cutoff is contaminated.
- Population fitness should be marginal contribution (AlphaGen, Numerai TC/MMC).

## 2. Statistics that keep evolution honest

**2.1 PSR and minimum track record [DOC 27].** SR̂, SR* are per-period (not annualised); γ̂3 = skewness, γ̂4 = *raw* kurtosis
(normal = 3); n = observations:
  `PSR(SR*) = Φ[ (SR̂ − SR*)·√(n−1) / √(1 − γ̂3·SR̂ + ((γ̂4−1)/4)·SR̂²) ]`
  `MinTRL = 1 + [1 − γ̂3·SR̂ + ((γ̂4−1)/4)·SR̂²] · (z_α / (SR̂ − SR*))²` observations.
Valid under stationary-ergodic returns (Mertens/Opdyke, as cited in [27]); the moments need long samples. Serial correlation
can overstate an annualised SR by up to 65% [DOC 52].

**2.2 Selection bias [DOC 28, 29].**
- Expected maximum: `E[max_N] ≈ (1−γ)·Φ⁻¹(1−1/N) + γ·Φ⁻¹(1−1/(N·e))`, γ ≈ 0.5772; upper bound √(2 ln N).
- Deflation: `SR̂0 = √V[{SR̂_n}]·E[max_N]`; `DSR = PSR(SR̂0)`.
- Worked example in [28]: N = 100, V = ½, T = 1250, γ3 = −3, γ4 = 10, SR̂ = 2.5 gives DSR 0.90 (fails 0.95). After 46 trials
  it would have passed; with normal returns, after 88.
- MinBTL [29]: `MinBTL ≈ (E[max_N]/SR_target)² < 2·ln N / SR_target²` years. With 5 years, at most 45 independent
  configurations keep the best expected *noise* SR below 1. Ten trials on one year already expect an in-sample SR of 1.57.
  When returns have memory, overfitting makes out-of-sample performance *negative*, not zero.
- Effective N: either `N̂ = ρ̄ + (1−ρ̄)·M` from the average correlation [28 App. 3], or clusters of the trials' return series.
  López de Prado & Lewis give an unsupervised algorithm for the "number of effectively uncorrelated trials" [DOC 42].
  Jensen–Kelly–Pedersen cluster published factors into 13 themes [DOC 46].

**Noise ceiling for TradeAgent (INFERENCE; IID normal, 365 obs/yr).** Each cell is the expected best annualised SR among N
zero-skill trials over that window:

| window | N=10 | N=50 | N=200 | N=1000 | DSR of an observed SR of 2.0 vs N=50 / N=200 |
|---|---|---|---|---|---|
| 3 months | 3.15 | 4.56 | 5.54 | 6.52 | even a single test gives only PSR(0) = 0.84 |
| 9 months | 1.82 | 2.63 | 3.20 | 3.76 | 0.29 / 0.15 |
| 1 year | 1.57 | 2.28 | 2.77 | 3.26 | 0.39 / 0.22 |
| 3 years | 0.91 | 1.31 | 1.60 | 1.88 | 0.88 / 0.76 |
| 5 years | 0.70 | 1.02 | 1.24 | 1.46 | 0.99 / 0.96 |

**2.3 Probability of backtest overfitting via CSCV [DOC 30].**
1. Take the T×N P&L matrix of **all** trials.
2. Split it into S even row blocks and form all C(S, S/2) train/test unions (S = 16 gives 12,780).
3. In each union, take the in-sample best n*, its out-of-sample relative rank ω̄ = r̄/(N+1) and the logit
   λ = ln(ω̄/(1−ω̄)). Then `PBO = ∫_{−∞}^0 f(λ) dλ`.
4. By-products: a degradation slope (usually negative) and P(OOS loss).

The authors suggest rejecting above 0.05 and call S = 16 reasonable. For guided searches, use each search's final outcome as a
column. They warn explicitly against making PBO the selection objective (Goodhart).

**2.4 Family-wise tests.**
- White's Reality Check [DOC 31,34]: does the *best* model in a search beat a benchmark? `V = max_k √n·f̄_k`, judged
  against a bootstrap distribution of the same maximum (for dependent returns, e.g. the stationary bootstrap [DOC 35]).
- Hansen's SPA [DOC 32]: more power and less sensitivity to poor or irrelevant alternatives, by studentising and using a
  null distribution that depends on the sample.
- Romano–Wolf StepM [DOC 33]: stepwise, controls FWER, captures the joint dependence, rejects more false nulls.
- Use them for a periodic audit of the whole population against cash and buy-and-hold, not as a per-candidate gate.

**2.5 Cross-validation.**
- Purging drops training observations whose labels overlap the test labels; an embargo also drops observations right after
  each test fold. CPCV gives C(N,k) splits [DOC 48], and every observation is tested C(N−1,k−1) times, which makes that many
  full paths.
- In a synthetic study, CPCV had lower PBO and better DSR than k-fold, purged k-fold and especially walk-forward [DOC 49].
- **INFERENCE: TradeAgent programs fit nothing.** They are long/flat rules with constants and ≤ 16 indicators [DOC R]. The
  overfitting channel is *selection* over constants and versions. Use DSR, CSCV-PBO, sub-period stability and parameter
  plateaus. Keep CPCV/purging for a future fitted component (for example a meta-labeller).

**2.6 Online FDR for a never-ending stream [DOC 36–41].**
- α-investing controls mFDR = E[V]/E[R]. A rejection *earns* α-wealth [36]. A non-rejection costs α_j/(1−α_j) [SEC 36s].
- Generalised α-investing (incl. LORD) controls FDR when null p-values are independent [37].
- LORD++: `α_t = γ_t·W0 + (α−W0)·γ_{t−τ1} + α·Σ_{j≥2} γ_{t−τj}`, where the τ are earlier rejection times.
- SAFFRON: `α_t = min{λ, (1−λ)·[W0·γ_{t−C0+} + (α−W0)·γ_{t−τ1−C1+} + α·Σ γ_{t−τj−Cj+}]}`; the C count "candidates"
  (p < λ).
- ADDIS: the same with (τ−λ), counting only tests that are not discarded (p ≤ τ). It gains power when nulls are
  **conservative** — p-values above uniform, which is exactly a strategy that *loses* after costs [38, 39].
- LOND covers PRDS dependence. LORD for arbitrary dependence has "substantial" power loss [38].
- **e-LOND** [40]: `α_t = α·γ_t·(R_{t−1}+1)`; reject if `E_t ≥ 1/α_t`; **FDR ≤ α under arbitrary dependence**.
- Default `γ_j = C·log(max(j,2)) / (j·e^{√log j})`, C ≈ 0.0772, γ1 ≈ 0.054; SAFFRON/ADDIS use ∝ j^−1.6 [38].
- INFERENCE: with that default γ and α = 0.1, e-LOND's first test needs E ≥ 187. A finite, front-loaded γ (1/K over K
  tests) suits a small yearly stream.

**2.7 Anytime-valid evidence and holdout reuse.**
- A test martingale is nonnegative and starts at 1. Ville gives `P(sup_t M_t ≥ 1/α) ≤ α`, so you may monitor and stop for
  any reason [DOC 58].
- Betting capital `K_t = Π(1 + λ_i(X_i − m))` with predictable λ_i, for bounded X [DOC 59]. Always-valid p-values support
  continuous monitoring [DOC 60].
- INFERENCE: for unlevered net returns r_t ≥ −1 and predictable λ_t ∈ [0, λmax], `E_t = Π(1 + λ_t·r_t)` is a nonnegative
  supermartingale under H0: E[r_t | past] ≤ 0. The evidence is literally a cautious Kelly bettor's wealth.
- Adaptive reuse of a holdout overfits it. Limiting what each answer reveals preserves validity [DOC 50, 51]. TradeAgent's
  class-only verdicts plus a verdict budget approximate the Ladder (INFERENCE).

**2.8 How much published work survives?**
- Harvey–Liu–Zhu: t > 3.0 for new factors [DOC 43]; Harvey–Liu: Sharpe haircuts for data mining [DOC 44].
- Hou–Xue–Zhang: 65% of 452 anomalies fail |t| 1.96, and 82% fail 2.78 [DOC 45].
- Jensen–Kelly–Pedersen (Bayesian): most factors replicate [DOC 46].
- Barras–Scaillet–Wermers: 75% of funds have zero alpha; skilled funds had nearly vanished by 2006 [DOC 47].
- Claims without a causal mechanism are likely false [DOC 53].

## 3. Meta-labeling with a semantic (LLM) secondary model

- A secondary ML layer sizes positions and filters false positives from a primary signal; its output is P(profitable)
  [DOC 54]. Calibration significantly improves *fixed* sizing functions; sizing functions fitted on training data gain
  little [DOC 55].
- LLM verbalised confidence is overconfident. Sampling consistency helps, but no method is consistently best [DOC 25]. So
  calibrate (isotonic or Platt on a disjoint window, INFERENCE) before an LLM score sizes anything.
- **Fit to TradeAgent (INFERENCE/DESIGN).** The runner has no model client and the language has no model-call node [DOC R].
  An LLM meta-labeller can therefore only enter as a **published, timestamped, hashed score feed** that the frozen program
  reads as a feature, with staleness bounds. A stale score means default size or no trade, so protection never depends on
  it.
- Its identity is (model id, knowledge cutoff, prompt sha, sampling, calibration map).
- It has **no valid backtest** over windows its pretraining covers. Judge it forward only, *paired* against the unlabelled
  primary signal: an e-process on the daily difference cancels common noise.

## 4. Forward paper incubation: the honest out-of-sample

- "Model sequestration" — announce first, measure later — is the extreme form of a holdout [DOC 29, 61].
- Quantopian, 888 algorithms with ≥ 6 months out of sample: backtest Sharpe gives R² < 0.025 for out-of-sample Sharpe; more
  backtesting meant a larger IS–OOS gap; volatility and drawdown persist [SEC 56].
- An ML model on backtest features reached R² 0.17; its top 10 had OOS SR 1.8 vs 0.7 for the top 10 in-sample [SEC 56].
- Live vs backtest: median Sharpe deterioration of 73%, worse for complex strategies [DOC 57]. Publication roughly halves
  Sharpe [DOC 72], or 26% out of sample and 58% post publication [DOC 71].

**Track record needed for crypto (INFERENCE; 365 obs/yr, one-sided 95%).**

| observed annual SR | MinTRL vs 0: normal / skew −1, kurt 12 | MinTRL vs 0.5 (normal) | e-process to E ≥ 20 / ≥ 200 (≈ 2·ln(1/α)/SR²) |
|---|---|---|---|
| 1.0 | 2.71 y / 2.87 y | 10.8 y | 6.0 y / 10.6 y |
| 1.5 | 1.21 y / 1.32 y | 2.72 y | 2.7 y / 4.7 y |
| 2.0 | 0.68 y / 0.77 y | 1.21 y | 1.5 y / 2.7 y |
| 3.0 | 0.31 y / 0.37 y | 0.44 y | 0.7 y / 1.2 y |

Anytime validity plus multiplicity costs roughly 2–4× the fixed-horizon time. Pooling k uncorrelated SR-1 streams gives a
pooled SR of √k. Four such streams prove SR > 0 in 0.68 y instead of 2.7 y. **Test families and processes; do not wait on
individual versions** (INFERENCE).

## 5. Capital allocation across a live population

- **Thompson sampling:** sample from the posterior, act greedily on the sample [DOC 62]. R&D-Agent(Q) schedules research
  with a contextual variant [DOC 10].
- **Kelly** maximises long-run growth, but its wagers can be very large and risky in the short term. Fractional Kelly cuts
  drawdowns at the cost of growth [DOC 64].
- **RCK** [DOC 63]: maximise `E log(rᵀb)` subject to `E(rᵀb)^(−λ) ≤ 1`, with `λ = ln β / ln α`. This guarantees
  `P(W_min < α) < β`, and in fact bounds the whole CDF by α^λ (α = 0.7, β = 0.1 gives λ = 6.46). RCK beat fractional Kelly at
  equal risk on finite outcomes (growth 0.047 vs ~0.035) and tied it on a lognormal mixture.
- The quadratic form, `E(ρᵀb)² ≤ 2/(λ+1)·E ρᵀb`, gives **f ≤ 2/(λ+1) × Kelly** (INFERENCE from eq. 14). A 30% drawdown at
  ≤ 10% probability means ≤ 0.27 Kelly; a 20% drawdown at ≤ 5% means ≤ 0.14 Kelly.
- **Grossman–Zhou** [DOC 68]: invest in proportion to the surplus over α × (running maximum of wealth).
- **Shape:**
  - equal risk contribution lies between minimum-variance and 1/N [DOC 65];
  - HRP needs no covariance inversion and has lower OOS variance than CLA [DOC 66];
  - none of 14 optimisers consistently beats 1/N; mean-variance needs about 3000 months of data for 25 assets [DOC 67];
  - the max-PSR portfolio is more diversified than the max-SR one [DOC 27].
- **Ensemble over winner-takes-all.** Estimation error dominates; pooling builds evidence √k faster; contribution-based
  reward (Numerai TC [DOC 22], AlphaGen [DOC 14]) selects for originality. Allocation by track record *is* the selection
  pressure, so it must score marginal contribution and keep an exploration floor. TradeAgent already reserves 50 of 200
  trials for exploration [DOC R].

## 6. Decay and regime

- Decay is the norm: 26% (out of sample) / 58% (post publication) [DOC 71]. Sharpe roughly halves after publication, and
  publication year explains 30% of the variance [DOC 72]. Mined factors decay within a few years [DOC 9, 15].
- CUSUM: `S_t = max(0, S_{t−1} + x_t − ω)`, alarm when S_t > h [DOC 69, SEC 69w]. BOCPD: an online posterior over the run
  length since the last changepoint [DOC 70].
- **Detection is as slow as proof (INFERENCE).** The evidence for "edge → 0" grows about SR_d²/2 per day, so an SR-2
  strategy needs ~1260 days to accumulate ln(1000). Return-based decay alarms alone are far too slow.
- The **fast** retirement signals are operational:
  - realised cost or slippage above the modelled level;
  - trade frequency, holding time or turnover outside the backtest's 99% band;
  - drawdown beyond the posterior's 99th percentile for the elapsed horizon;
  - a volatility-regime changepoint (BOCPD on realised volatility, estimable within days).

## 7. Cost realism

- Anomalies with one-sided turnover below 50% a month mostly survive costs; few above do. A buy/hold spread is the best single
  mitigation, and capacity shrinks as turnover rises [DOC 73].
- Across 204 anomalies, after spreads, post-publication decay and the modern era, the average expected return is
  4 bps/month (best ~10, combinations ~20) — **before** price impact [DOC 74].
- Impact follows a square-root law, I(Q) ∝ Q^½ [DOC 75]. It holds over four decades in more than a million Bitcoin
  metaorders [DOC 76], and the exponent is ½ per stock and per trader [DOC 77]. The normalisation Y·σ_day·√(Q/V_day) with
  Y ~ 1 is conventional (UNKNOWN here).
- Binance spot regular tier: 0.100% maker / 0.100% taker, or 0.075% with BNB [DOC 78]. A taker round trip costs 0.20%.
- **Drag = round trips per year × round-trip cost (INFERENCE):**

| frequency | 0.20% RT | 0.15% RT | 0.10% RT |
|---|---|---|---|
| 1 round trip / day | 73.0% / yr | 54.8% / yr | 36.5% / yr |
| 1 / week | 10.4% / yr | 7.8% / yr | 5.2% / yr |
| 1 / month | 2.4% / yr | 1.8% / yr | 1.2% / yr |

- At laptop size, fees dominate impact; recompute capacity as allocations grow.
- "Fill at the next closed bar's open" is already used in both backtest and paper [DOC R] and must stay identical at every
  stage.

## 8. DESIGN — the evidence machinery mapped onto TradeAgent

**(a) Evaluator cascade.** Every stage is deterministic referee code; there is no LLM in it.

| Stage | Cost | What runs | Gate (defaults in c) | TradeAgent concept |
|---|---|---|---|---|
| E0 sanity | ms | parse/hash; execution bounds; language caps; record complexity (nodes, consts) and AST similarity to the registry | parses; bounds; ≤ complexity cap | `strategy_version`, U-promote-bounds |
| E1 research backtest | s | app **VenueCostModel vN** (fee tier + half-spread + √-impact, next-open fills) at 1× and 2× cost; store the **daily net-return series** | ≥ 30 closed trades; net > 0 at 1×; net ≥ 0 at 2× | a registered **trial** (`charged_to` lineage) |
| E2 robustness | s–min | S = 8 sub-period blocks; ±20% on each numeric constant, one at a time; CSCV-PBO over the family's trials | ≥ 6/8 blocks positive; ≥ 70% of neighbours positive; family PBO ≤ 0.5 | same trial; PBO per family |
| E3 deflation | s | cluster *all* trial streams in the campaign lineage (\|ρ\| ≥ 0.7) → N_eff; V[SR] across clusters → SR̂0 → DSR; show the noise ceiling | DSR ≥ 0.95 before a verdict may be requested | `trial_budget`, `exploration_budget` |
| E4 holdout verdict | 1 verdict | frozen program over the held-back months at app cost: net, fidelity z = (SR_h − deflated SR_research)/se_h, DD, betting e-value E_h (sealed) | paper-eligible if net > 0, z ≥ −2, DD ≤ 2× research DD; with a ≥ 12-month holdout, also ADDIS p_h ≤ α_j | `Referee.Verdict`, PaperV1; the verdict budget becomes **α-wealth** |
| E5 paper | days–months | forward runner on post-freeze bars; **fills ledger** net of app costs → daily returns, e-process E_j(t), posterior, fidelity, realised vs modelled cost | ≥ 90 days and ≥ 50 round trips before any L1 request; evicted by bounded replacement | `AllocatePaperDue`, `max_deployments`, deployment |
| E6 live L1 "learning" | owner press | shrunk posterior P(SR_net > 0) | P ≥ 0.80; fidelity ≥ 0.3; realised cost ≤ 1.5× modelled | a new reason class (policy V2); owner two-press allocation |
| E7 live L2 "proven" | owner press | e-LOND over live hypotheses, per strategy **or family-pooled** e-process | E_j ≥ 1/α_j | `promoted`; `Allocations.Record` unchanged |
| Retire / demote | continuous | fast alarms (§6) OR CUSUM (h = ln 1000) OR an e-process *against* the promised edge | any alarm → back to paper, never delete | retirement boundary, suspension |

**Why the holdout carries little FDR weight (INFERENCE).** ADDIS at α = 0.25 (W0 = α/2, λ = 0.25, τ = 0.5, γ ∝ j^−1.6) starts at
α1 ≈ 0.014. That needs a holdout SR of 4.4 over 91 days, or 2.2 over 365 days. Holdout p-values also share one market path, so
independence fails. **Default:**
- The holdout is a blind consistency screen and a rationed resource.
- α-wealth is spent per verdict, lineage-wide, never reset by renewal, with a hard cap per lineage.
- Exact FDR control lives at E7, using forward data only, which keeps V1's post-freeze clause.

**Posterior (DESIGN).**
- Prior per family F: `SR ~ N(μ_F, τ_F²)`, fitted by empirical Bayes from earlier versions' realised *forward* SRs.
- Cold start: μ0 = 0, τ0 = 0.5, plus h × the deflated research SR, with h = 0.3 (the 50–75% haircuts in [57, 71, 72]).
- Likelihood: `ŜR ~ N(SR, (1 − γ3·SR + ((γ4−1)/4)·SR²)/n)` [27]. Paper evidence is weighted by fidelity; live fills count fully.

**Allocation (DESIGN), recomputed weekly to limit turnover.**
1. For each eligible strategy, draw θ_i from its posterior; θ_i ≤ 0 means no new capital this week.
2. Shape the survivors by HRP, with an equal risk budget per correlation cluster.
3. Scale the total to `2/(λ+1)` × the Kelly fraction of the posterior-mean portfolio, where `λ = ln β / ln α_dd` is the
   owner's drawdown spec.
4. Clip each strategy to its owner ceilings. L1 strategies are capped by the learning budget.
5. **Fitness back to evolution** = the posterior-mean gain in ensemble log-growth from including the strategy, on paper
   streams, by family and MAP-Elites cell. Births follow fitness, with ≥ 25% kept for exploration.

**(b) Minimal record per stage — the proprietary meta-dataset.** Append-only. Measurement and claim go in separate tables, as
in the material ledger.

| Stage | Record |
|---|---|
| E0 | version sha, parent, family, generator (runtime, model id, **knowledge cutoff**, prompt sha, role, tokens and $), hypothesis tags (signal type, horizon, regime thesis) as the agent's *claim*, complexity, nearest AST similarity |
| E1–E2 | dataset sha + window, cost-model version, trades, **daily net returns** at 1× and 2×, turnover, holding time, skew/kurt, max DD, block and neighbour results, family PBO at that time |
| E3 | lineage M, N_eff, clustering version, cluster id, V[SR], SR̂0, DSR, the noise ceiling shown |
| E4 | verdict and reason class, α_j, wealth before/after, sealed holdout figures (never served), pretraining-overlap flag |
| E5–E7 | deployment ids, daily net returns from the fills ledger, realised vs modelled cost per fill, fidelity, e-process and posterior paths, regime features (realised vol, trend, volume state, BOCPD run length), alarms |
| Exit | retirement reason, lifetime, forward SR at 30/90/180/365 d — the labels for predicting forward success (cf. R² 0.17 in [56]), validated only on later cohorts |

**(c) Default thresholds.**

| Owner-owned (Safety/Data page; two-press where it widens) | App-owned constants (versioned, hashed into the policy text) |
|---|---|
| archive depth ≥ 4–5 years before scaling evolution (§2.2); holdout ≥ 12 months | E1: ≥ 30 trades; 2× cost stress; VenueCostModel, never Frictionless as evidence |
| α_paper = 0.25 (holdout currency); α_live = 0.10 (e-LOND, γ_j = 1/K, K = 10 per year) | E2: 8 blocks, ≥ 6 positive; ±20% plateau ≥ 70%; family PBO ≤ 0.5 |
| drawdown spec α_dd = 0.8, β = 0.05 → λ = 13.4 → ≤ 0.14 Kelly | E3: \|ρ\| ≥ 0.7 clusters; DSR ≥ 0.95 (owner may lower to 0.90) |
| learning budget ≤ 1% of the account per L1 strategy, ≤ 5% in total | E4: z ≥ −2; DD ≤ 2× research; ≤ 10 verdicts per lineage per holdout |
| `max_deployments` = 12 per instrument; ≤ 2 per cluster, ≤ 3 per family | E5: ≥ 90 d and ≥ 50 round trips; e-process λ_t = clip(plug-in Kelly/2, 0, 0.5) |
| L1 needs P(SR_net > 0) ≥ 0.80; CUSUM h (retirement aggressiveness) | alarms: cost > 1.5× modelled; behaviour outside the 99% band; DD > posterior p99 |

**(d) What agents may see without contaminating evidence.**

| Stage | Visible to agents | Never visible |
|---|---|---|
| E0–E3 (research data) | everything they compute; M, N_eff, noise ceiling, remaining α-wealth; their own DSR and PBO | other lineages' sealed holdout facts |
| E4 | verdict + reason class only (existing `RefereeFeedback`); one answer per (version, campaign) | holdout figures, E_h, z, any "near miss" |
| E5–E7 | all forward figures, e-process and posterior (post-freeze, existing rule) | — (a new version's clock starts at its own freeze) |
| meta-dataset | aggregates of forward outcomes by family and cell | anything derived from sealed holdout figures |

Two more rules:
- If a holdout window ends before the generating model's knowledge cutoff, the verdict is flagged and confers paper
  eligibility only [DOC 23, 24].
- Fitness never includes holdout figures. Otherwise the holdout becomes a training set [DOC 50] — Goodhart, as [DOC 30]
  warns.

**(e) First moves, smallest first (DESIGN).**
1. Replace the Frictionless defaults with a venue cost model for referee and paper evidence; keep frictionless runs as
   labelled upper bounds.
2. Store every trial's daily net returns.
3. Compute N_eff and DSR at verdict request.
4. Raise the archive depth to multiple years.
5. Add the forward e-process and posterior from the fills ledger.
6. Put e-LOND at the boundary to `promoted`.
7. Add family-pooled hypotheses.
8. Only then build the allocation proposal shown to the owner.

## 9. Not verified / open (UNKNOWN)
- FunSearch sample counts; full texts of the LLM alpha-mining papers (trial counts, DSR); Hansen's exact recentring constant.
- Whether e-LOND stays valid when each hypothesis's level is frozen at deployment start but decisions arrive asynchronously.
  The rule is predictable, but I read no proof for this setting.
- Binance BTCUSDT 1-minute archive depth; realised BTC skew and kurtosis (scenarios used instead).
- Whether pretraining look-ahead materially affects strategy *generation*.

## Sources (all accessed 2026-10-02)

Repository
- R — DOC: `docs/CONTRACTS.md` (campaign, verdict, paper connector, runner, U-allocator-2), `docs/STRATEGY-LANGUAGE.md`,
  `src/TradeAgent.Core/Strategy/Backtest.cs`, `src/TradeAgent.Core/Trading.cs`

Discovery systems (all DOC)
- 1 https://arxiv.org/abs/2506.13131 · 2 https://deepmind.google/discover/blog/alphaevolve-a-gemini-powered-coding-agent-for-designing-advanced-algorithms/
- 3 https://deepmind.google/discover/blog/funsearch-making-new-discoveries-in-mathematical-sciences-using-large-language-models/ (paper Nature s41586-023-06924-6 not fetched)
- 4 https://github.com/codelion/openevolve · 5 https://arxiv.org/abs/2509.19349 · 6 https://arxiv.org/abs/2511.02864 · 7 https://arxiv.org/abs/2603.24517
- 8 https://arxiv.org/abs/2308.00016 · 9 https://arxiv.org/html/2502.16789 · 10 https://arxiv.org/html/2505.15155v2 · 11 https://arxiv.org/abs/2402.03755
- 12 https://arxiv.org/abs/2406.18394 · 13 https://arxiv.org/abs/2508.06312 · 14 https://arxiv.org/abs/2306.12964 · 15 https://arxiv.org/abs/2604.09601
- 16 https://arxiv.org/abs/2606.20625 · 17 https://arxiv.org/abs/2608.30192 · 18 https://arxiv.org/abs/2508.13174 · 19 https://gplearn.readthedocs.io/en/stable/intro.html
- 20 https://arxiv.org/abs/1504.04909 · 21 https://arxiv.org/abs/1601.00991 · 22 https://docs.numer.ai/numerai-tournament/scoring/true-contribution-tc (and the MMC page)
- 23 https://arxiv.org/abs/2601.13770 · 24 https://arxiv.org/pdf/2502.21206 · 25 https://arxiv.org/abs/2306.13063

Statistics (DOC; DOI abstracts read via api.openalex.org)
- 27 https://www.davidhbailey.com/dhbpapers/sharpe-frontier.pdf · 28 https://www.davidhbailey.com/dhbpapers/deflated-sharpe.pdf
- 29 https://www.ams.org/notices/201405/rnoti-p458.pdf · 30 https://www.davidhbailey.com/dhbpapers/backtest-prob.pdf
- 31 https://doi.org/10.1111/1468-0262.00152 · 32 https://doi.org/10.1198/073500105000000063 · 33 https://doi.org/10.1111/j.1468-0262.2005.00615.x
- 34 https://homepage.ntu.edu.tw/~ckuan/pdf/Step-SPA-20090720.pdf · 35 https://doi.org/10.1080/01621459.1994.10476870
- 36 https://doi.org/10.1111/j.1467-9868.2007.00643.x · 36s SEC: search-result restatement of Foster–Stine and https://rdrr.io/bioc/onlineFDR/man/Alpha_investing.html
- 37 https://doi.org/10.1214/17-AOS1559 · 38 https://dsrobertson.github.io/onlineFDR/articles/theory.html · 39 https://arxiv.org/abs/1905.11465
- 40 https://arxiv.org/html/2311.06412 · 41 https://arxiv.org/abs/2208.11418 · 42 https://doi.org/10.1080/14697688.2019.1622311
- 43 https://doi.org/10.1093/rfs/hhv059 · 44 https://doi.org/10.3905/jpm.2015.42.1.013 · 45 https://doi.org/10.1093/rfs/hhy131
- 46 https://doi.org/10.1111/jofi.13249 · 47 https://doi.org/10.1111/j.1540-6261.2009.01527.x
- 48 https://skfolio.org/generated/skfolio.model_selection.CombinatorialPurgedCV.html · 49 https://www.sciencedirect.com/science/article/abs/pii/S0950705124011110 (search-result abstract)
- 50 https://doi.org/10.1126/science.aaa9375 · 51 https://arxiv.org/abs/1502.04585 · 52 https://doi.org/10.2469/faj.v58.n4.2453 · 53 https://doi.org/10.1017/9781009397315

Meta-labeling, forward evidence and allocation
- 54 https://doi.org/10.3905/jfds.2022.1.098 (DOC) · 55 https://doi.org/10.3905/jfds.2023.1.119 (DOC)
- 56 SEC: https://www.cxoadvisory.com/big-ideas/in-sample-vs-out-of-sample-performance-of-888-trading-strategies/ (paper SSRN 2745220 not readable here)
- 57 https://www.pm-research.com/content/iijpormgmt/43/2/90 (DOC, abstract) · 58 https://arxiv.org/abs/2210.01948 · 59 https://arxiv.org/abs/2010.09686
- 60 https://arxiv.org/abs/1512.04922 · 61 https://doi.org/10.3905/jpm.2011.38.1.110 · 62 https://arxiv.org/abs/1707.02038 · 63 https://arxiv.org/abs/1603.06183 (full text read)
- 64 https://www.stat.berkeley.edu/~aldous/157/Papers/Good_Bad_Kelly.pdf (DOC, via search summary) · 65 https://doi.org/10.3905/jpm.2010.36.4.060
- 66 https://doi.org/10.3905/jpm.2016.42.4.059 · 67 https://doi.org/10.1093/rfs/hhm075 · 68 https://doi.org/10.1111/j.1467-9965.1993.tb00044.x

Decay and costs
- 69 https://doi.org/10.1093/biomet/41.1-2.100 (DOC) · 69w SEC: https://en.wikipedia.org/wiki/CUSUM · 70 https://arxiv.org/abs/0710.3742
- 71 https://doi.org/10.1111/jofi.12365 · 72 https://doi.org/10.1080/14697688.2022.2098810 · 73 https://doi.org/10.1093/rfs/hhv063
- 74 https://doi.org/10.1017/S0022109022000874 · 75 https://arxiv.org/abs/1105.1694 · 76 https://arxiv.org/abs/1412.4503 · 77 https://arxiv.org/abs/2411.13965
- 78 DOC@2026-10-02 https://www.binance.com/en/fee/schedule
