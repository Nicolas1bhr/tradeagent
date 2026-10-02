# 07 — Look-ahead leakage in model-derived features, and in the data under them

Research leg, 2026-10-02. Labels: **DOC@2026-10-02** = primary source read today (URLs in §6 by S-number);
**SECONDARY** = reported by another source; **INFERENCE** = this leg's reasoning; **UNKNOWN** = not established.
Everything is paraphrased; numbers are the sources' own. arXiv items are preprints unless a journal is named.

## 0. Bottom line
- A model asked today about a 2024 headline is not a 2024 observer. Prompting cannot make it one, masking does not
  reliably make it one, and its published cutoff does not say where its knowledge ends. [DOC S3, S10, S19, S20]
- Cleanest measured size (same family trained with vs without the outcome period, prompted good/bad/neutral headline
  calls): look-ahead adds +4% to +70% signal strength — DatedGPT-instruct Sharpe 3.20→3.65, PIT-4B-FT 0.86→1.46. Those are
  1.3B–4B models; memorization grows with model size, so frontier-model gaps are likely larger. [DOC S5, S11; INFERENCE]
- Agent pipelines on GPT-4o lose 51–62% of Sharpe once past the cutoff; open models show alpha decay beyond −15 pp.
  Both compare different market periods, so regime change is mixed in. [DOC S8, S9; INFERENCE]
- It can also be small: embeddings + a rolling-trained regressor showed no significant gap (Sharpe 4.92 point-in-time vs
  4.90 Llama-3.1-8B). Leakage is model- and task-specific: measure it per feature, never assume it away. [DOC S6]
- Relevance / direction / surprise / credibility calls are the judgment-laden, "not future-invariant" tasks where
  memorized outcomes leak into the decision rule itself, even when the input text was never in training. [DOC S3]
- Clean by construction only: (a) annotations by a model whose knowledge provably predates the observation, (b) forward
  recording. For a frontier model, clean history starts at its release, i.e. now. [DOC S19; INFERENCE]

## 1. Measured evidence, 2023–2026
| Study | Design | Measured | Label |
|---|---|---|---|
| S1 Glasserman & Lin 2023 | GPT-3.5 (cutoff Sep 2021); 129k scraped + 182k Reuters headlines; in-sample 2015–Sep 2021, out-of-sample Oct 2021–Dec 2022; names and products masked | In-sample, masked headlines beat raw by 5.89 bp/day (p=0.066) and 3.10 bp/day (p=0.017; Table 3 — the text misprints 4.37). Out-of-sample: n.s. / borderline. Their reading: "distraction" by general firm knowledge outweighed look-ahead; the test measures both at once | DOC S1 |
| S2 Lopez-Lira & Tang, v6 Oct 2025 | GPT-4 et al. on post-cutoff headlines Oct 2021–May 2024 | ~90% portfolio-day hit rate on the non-tradable initial reaction; drift predicted (small caps, bad news); returns fall as LLM adoption rises. Caveats they state: GPT-4 may have seen later data in fine-tuning; Claude/Gemini excluded because versions with pre-sample cutoffs are no longer served | DOC S2 |
| S3 Lopez-Lira, Tang & Zhu, v2 Dec 2025 | GPT-4o (cutoff Oct 2023), Llama-3.1-70B; recall, fake-cutoff and masking tests | S&P 500 level recall MAPE 0.61% pre-cutoff vs 16.9% post. Prompt "ignore data after 2010": GDP-direction accuracy 97.6% before vs 98.0% after the fake cutoff, vs 40% on truly post-cutoff quarters. Masked earnings calls: firm named in 100% of Apple/Meta/Microsoft calls, >85% for every Mag-7 firm; Apple quarter+year 93.2%. "Economic logic" abstraction still let 11.9–46.3% of firm IDs through for 5 large names. Embeddings memorize too. Proven: pre-cutoff skill is non-identified; small post-cutoff samples cannot settle it; memorization tests give only a lower bound | DOC S3 |
| S4 Gao, Jiang & Yan "LAP", v2 Jun 2026 | Llama-3.3-70B (cutoff Dec 2023); 91,357 Bloomberg headline firm-days 2012–2023 | Given only firm + ticker + date (no headline), the model claims ≥0.95 knowledge of next-day direction on ~23% of in-sample firm-days, and that query alone predicts returns (26 bp per unit, t=3.53). +1 SD of this "lookahead propensity" raises the headline signal's effect ~32% (news) / ~12% (capex). All of it vanishes on 2024 data | DOC S4 |
| S5 DatedGPT, Yan et al., Mar 2026 | 12×1.3B models, annual cutoffs 2013–2024; 61,290 headline firm-days 2014–2024; vintage y−1 vs latest vintage on the same headlines | bp per SD / Sharpe, clean→leaky: DatedGPT-instruct 28.3→33.0 / 3.20→3.65; PIT-4B 29.6→32.7 / 3.37→3.52; PIT-4B-FT 5.9→9.7 / 0.86→1.46. Leak-only residual signal earns 26.4 bp (t=10.65) to 40.1 bp (t=10.98) | DOC S5 |
| S6 He, Lv, Manela & Wu, v3 Jul 2025 | ChronoBERT/GPT annual vintages 1999–2024; news embeddings → trained return model, 2008–Jul 2023 | Long-short Sharpe 4.80 / 4.92 (real-time vintages) vs 4.90 Llama-3.1-8B, n.s. (p=0.315 / 0.547). Later vintages read old news *worse* (meaning drift) | DOC S6 |
| S7 Kelly, Malamud, Schwab & Xu, NBER May 2026 | ≤4B params, 1T time-filtered tokens, monthly checkpoints 2013–2024 | Point-in-time embedding portfolios come close to leaky full-sample ones | DOC S7 (abstract) |
| S8 "Profit Mirage", Oct 2025 | FinMem, FinAgent, QuantAgent, FinCON, TradingAgents on GPT-4o | After the cutoff: Sharpe −51.5% to −62.2%, total return −50.2% to −71.9%; most agents then fail to beat a random baseline | DOC S8 |
| S9 Look-Ahead-Bench, Jan 2026 | Apr–Sep 2021 vs Jul–Dec 2024 | Llama 3.1 / DeepSeek 3.2 alpha decay beyond −15 pp (DeepSeek +20.7 pp, then a −21.8 pp decay). The paper also promotes proprietary point-in-time models whose claims are not auditable here | DOC S9; UNKNOWN |
| S10 HindsightBench, v2 Aug 2026 (single author) | 15 models, 7 vendors, ALFRED-vintage macro panel | Effective cutoffs span 22 months and run up to 8 months *earlier* than vendor-stated; date-triggered recall in every 2026-generation model tested; results move with serving quantization and reasoning mode | DOC S10 |
| S11 Didisheim et al., Econ. Letters 2025; S12 Levy, J. Acct. Research 2026 | Return-recall proxy; commercial LLM tests | Bias rises with model size, lower frequency and aggregation, and is negligible for small models on granular data (S11). Significant look-ahead may explain much of commercial LLMs' reported skill (S12) | DOC S11, S12 (abstracts) |
| S13 Sarkar & Vafa 2024 | Llama, earnings calls → next-year risks | 2019 calls produce "COVID-19" risks | SECONDARY via S3, S23 |
| S14 Engelberg, Manela, Mullins & Vulicevic 2025 | LLM masks and paraphrases until a second LLM cannot name the firm; >500k articles | Firm ID falls to chance; raw vs neutered sentiment agree >90% with similar return predictability; the gap is an upper bound on look-ahead | DOC S14 (author page) |
| S15 Fake Date Tests, v2 Mar 2026 | Prompt-sensitivity tests on macro forecasts | None of the modern LLMs tested passed the first test | DOC S15 (abstract) |

## 2. Mitigations and their limits
- **"Pretend it is 2019" / date-restricting prompts:** no evidential value. Accuracy did not drop past the fake cutoff,
  and even a drop could be suppression rather than forgetting (non-identified). [DOC S3, S15]
- **Entity masking / neutering:** it measures distraction and look-ahead together (S1). Iterative neutering reaches
  chance-level re-identification on news (S14), yet GPT-4o recovered firm, quarter and year from a neutered transcript
  and >85% of Mag-7 calls (S3). Passing a re-identification test does not prove future-invariance, because industry and
  macro pathways remain (S3), and masking destroys information (S6). One study masks identifiers *and* calendar
  consistently across prompts and tools; agents' returns then reduce mostly to market and style beta (S17). Use: a bound or
  a diagnostic, never an upgrade to clean. [DOC; INFERENCE]
- **Models with a cutoff before the test period:** valid (S2). But models retire, so the window closes: Anthropic retired
  claude-2.x on 2025-07-21; OpenAI shut down text-davinci-003 (2024-01-04), gpt-3.5-turbo-0301 (2024-09-13) and gpt-4-0314
  (2026-03-26). Re-running is not replay: serving infrastructure can shift behaviour under a fixed ID, and Claude 4.7+
  rejects non-default temperature/top_p. [DOC S2, S27, S28, S30]
- **Point-in-time / chronologically consistent models** (S5, S6, S7, S23, S24): clean by construction if corpus dates
  hold, with fixed open weights. Limits: 1.3B–4B scale, and chat ability is weak (ChronoGPT-Instruct wins 12.6–16.8% vs a 1.8B
  chat model, S23), so they fit embeddings and simple direction calls, not agent-grade judgment. Misdated documents in
  the corpus are a residual leak. One family was pulled from Hugging Face (SECONDARY via S6). [DOC; INFERENCE]
- **Evaluate only after the cutoff:** S3 and S22 recommend it, but the cutoff must be measured, not read off a page. Effective
  cutoffs differ from stated ones (S20) and can run 8 months early (S10); a jailbroken gpt-4o-2024-08-06 hinted at
  post-cutoff knowledge (S19, unconfirmed). The release date of a fixed snapshot is a strict upper bound, unless
  scaffolding injects later facts (S19). Small post-cutoff samples lack power (S3). [DOC]
- **Forward-only (live-recorded) evaluation:** ForecastBench asks only unresolved questions for exactly this reason (S21).
  It is the only design with no leakage path, provided retrieval is time-bounded too: date-filtered web search leaks
  through updated pages and through ranking that uses future knowledge (S19). [DOC]
- **Ensembling across cutoffs:** UNKNOWN; no study found shows that averaging models with different cutoffs reduces leakage.
  What works is the *as-of* construction: at date t, use only members whose knowledge bound is before t (S5, S6 "real-time"
  vintages). Membership then changes over time, so the feature differs by date. [INFERENCE]
- **Measuring a leakage gap:** the within-family vintage contrast (S5) is cleanest. Pre/post-cutoff contrasts (S8, S9) mix
  in regime change. The LAP interaction test (S4) scores contamination per observation but needs log-probs (open
  weights); an API-only approximation by sampling is untested (UNKNOWN). A zero gap never certifies clean (S3 lower bound).
- **Steering and counterfactual training** (S16 time-awareness features; S8 FactFin): research-stage, open weights only.
- **Adjacent:** survivorship and data snooping alone erase reported LLM-strategy edges over 20 years and 100+ symbols (S18).

## 3. Model cutoff transparency (checked 2026-10-02)
- **Anthropic:** two dates per model, a reliable-knowledge cutoff and a training-data cutoff (Opus 5.5: Jun 2026 / Jun 2026;
  Haiku 4.5: Feb 2025 / Jul 2025). Every model ID is a pinned snapshot (dateless from 4.6 on), and weights and config are
  never updated under an ID. Serving infrastructure can still cause minor behaviour drift. Pre-4.6 aliases move. Requests to
  retired models fail; notice is at least 60 days. [DOC S26–S28]
- **OpenAI:** a knowledge cutoff per model (GPT-6 Astra and 6.1 Sol: 2026-04-30; GPT-6 Luna: 2026-05-18). The docs say
  snapshots lock a version so behaviour stays consistent. At shutdown a model is no longer accessible; notice is at least
  6 months for GA models and can be about 2 weeks for previews. [DOC S29, S30]
- **Google:** older model pages state a cutoff (Gemini 2.5 Pro: Jan 2025). The current 3.x API pages (3.8/3.6/3.5 Flash,
  3.1 Pro Preview) show only "latest update" and no cutoff. The 3.8 Flash model card says March 2026, but some domains stop at
  January 2025. Stable versions "usually don't change"; `-latest` aliases are hot-swapped. [DOC S31–S33]
- **TypeSafe Jev (System One):** `jev-1.13.0`, with aliases `jev-latest` / `jev-preview` that move on release. The response
  reports the versioned ID, and `GET /v1/models` gives release dates. No knowledge cutoff or deprecation policy appears in
  the full docs. It is an RLCD adaptation of a pretrained LM, so it carries world knowledge of unknown vintage. [DOC S34; INFERENCE]
- **Implications:** [INFERENCE]
  - The published cutoff is advisory: providers publish it for users, not for train/test separation (S19).
  - The only verifiable knowledge bound for a closed model is the date its pinned ID existed: the documented release date,
    or the first answer this installation received from it.
  - A vendor with no published cutoff must be treated as knowing everything up to its release.
  - Store every annotation when it is made. The model will retire, and re-running is not replay.
  - Aliases have no place in the evidence plane; record the resolved ID the response returns.
  - For open weights, pin the weights hash, quantization, inference stack and reasoning mode (S10).

## 4. The same leak in non-model data; what point-in-time requires
- **Backfilled or re-dated news:**
  - Tiingo keeps `publishedDate` (source-declared, falling back to crawl time) apart from `crawlDate` (Tiingo's own
    insert time). It states that a large gap between the two means the story was backfilled. [DOC S37]
  - GDELT keeps the event date apart from `DATEADDED` (when the record entered the database, 15-minute resolution).
    [DOC S38]
  - Web pages get updated without their date changing. [DOC S19]
- **Survivorship:** returns missing for delisted stocks are large and negative (a −55% correction makes the Nasdaq size
  effect vanish) [DOC S42 abstract]; universes picked from today's index members embed look-ahead [DOC S18].
  X requires stored posts to be deleted or modified within 24 h of a request [DOC S39], so archives silently drop posts
  that existed at the time; engagement counts accrue later. [INFERENCE]
- **Labels restated with hindsight:** Nansen "Smart Trader" means top wallets by PnL over trailing 30/90/180-day,
  2-year or all-time windows [DOC S40]. Applied to earlier trades, the label selects winners after the fact. [INFERENCE]
  OFAC sanctioned Tornado Cash on 2022-08-08 (it had been laundering since 2019) and removed it on 2025-03-21 [DOC S41]:
  one label with both an assignment time and a removal time.
- **Revised economic data:** FRED/ALFRED real-time periods return values as they were known on a past date [DOC S35].
  LLMs blend first prints with revisions and "believe" data were in hand before release [DOC S25 abstract].
- **Filings:** an EDGAR filing submitted after 5:30 p.m. ET is deemed filed the next business day (10 p.m. for Forms 3/4/5,
  13D/G and 144) [DOC S36]. The deemed date is not the moment the filing became public; dissemination latency is UNKNOWN here.
- **Requirement — bitemporal records** (valid/event time vs record/knowledge time; record history append-only) [DOC S43].
  - Per item, store: event time; source-claimed publish time; **knowledge time** (first seen by our recorder, or a vendor
    first-seen field we accept); the revision chain, each version with its own knowledge time; as-of universe membership;
    label assignment and removal times.
  - Join rule: an item may influence a decision at t only if knowledge_time + latency ≤ t, using the version current at t.
    Event time is never the join key. [INFERENCE]

## 5. DESIGN — evidence rules a deterministic referee can enforce
**5.1 Observation class** (per raw item, computed by the app, never asserted by an agent)
- `O-LIVE` — first seen by TradeAgent's own recorder; receipt time and content hash stored on receipt; revisions appended.
- `O-PIT` — vendor archive with a vendor-recorded first-seen time per record (e.g. `crawlDate`, `DATEADDED`, ALFRED
  `realtime_start`), the crawl–publish gap within a declared threshold, and the revision history available or the
  source immutable.
- `O-ARCH` (vendor archive, unverified timestamp): source-declared time only, a large crawl–publish gap, unknown revision
  or deletion policy, or survivorship from deletion compliance.
- `O-HIND` — constructed with hindsight: today's constituents, trailing-PnL labels, labels applied before their
  assignment date, revised macro values without a vintage, engagement counts collected later.

**5.2 Annotation class** (per model output over one observation version)
- `A-LIVE` (recorded live at knowledge time):
  - computed by the app's recorder before the decision time t_dec, from O-LIVE or O-PIT content;
  - pinned ID resolved from the response; tools and retrieval off;
  - question/schema frozen before the observation's knowledge time.
- `A-PRE` (backfilled with a model that predates the item): computed later, but the model's knowledge bound K(m) is earlier
  than the observation's knowledge time and tools are off; flag `schema_after_obs` if the prompt or schema was written later.
- `A-POST` (backfilled with a post-cutoff model): K(m) ≥ knowledge time. This includes masked or neutered inputs (flag
  `masked`, with the measured re-identification rate).
- `A-UNK` (unknown provenance): any required field missing; an unresolved alias; tools on without a time-bounded index;
  vendor-precomputed scores with no model provenance; a vendor with no release date.
- K(m) for a closed model = documented release date (DOC + URL recorded), else the first successful call this
  installation recorded. Never the published cutoff. For a point-in-time open-weight vintage = documented corpus cutoff
  plus a 3-month buffer (INFERENCE from S19/S20).

**5.3 What each class may support**
- A bar's class is the **worst** class among all its inputs.
- A favourable historical verdict counts only bars whose observations are all O-LIVE/O-PIT and whose model features are
  all A-LIVE/A-PRE.
- Anything touching O-ARCH, O-HIND, A-POST or A-UNK is exploratory: agents may use it in the Creative zone, and the
  referee may compute on it, but labels the output exploratory and never counts it toward a verdict or the evidence budget.
- Classes only move down. A contaminated row can never become clean ("a leaked holdout cannot become unseen").
  Re-annotating writes a new row with its own class.
- If the clean span is shorter than the evaluator's minimum, the verdict is `INCONCLUSIVE: insufficient clean history`.
  It never falls back to contaminated bars.

**5.4 Clean-history start per feature.** Feature identity = (resolved model ID, prompt hash, schema hash, input source).
- clean_start = max(K(m), first date the source is O-LIVE/O-PIT).
- For an as-of vintage schedule: the first date some scheduled vintage has K < t.
- Switching model, prompt or schema starts a new feature with its own clean history. Shadow-annotate with the successor
  for a while before switching, so it has already accrued clean history at cut-over.
- Today, for frontier and Jev features, this puts clean_start at release, i.e. effectively forward. An older pinned model
  can backfill A-PRE only for [its release, its retirement), e.g. Haiku 4.5 is retired no sooner than 2026-10-15. [INFERENCE]

**5.5 Forward recording + forward paper — the only fully clean test for news-driven strategies**
- The recorder stores every raw item on receipt (O-LIVE) and annotates registered features live (A-LIVE).
- Paper consumes only O-LIVE/A-LIVE. A backfill cannot enter paper, since by definition it is after the fact.
- Every input's knowledge time and every model's knowledge bound then precede the decision, by our own clock.
- This fits PRINCIPLES ("forward paper evidence cannot be required before the first paper run that produces it").
  A news-driven candidate whose history is contaminated gets `INCONCLUSIVE` historically. Whether it may still enter paper
  observation is an owner/manager decision, flagged here, not assumed.
- Cost: live annotation spends real AI allowance. Record raw items broadly, annotate only registered features, and make the
  annotator idempotent and bounded. [INFERENCE]

**5.6 Store with every annotation** (an app-written measurement row, hash-chained; agent notes live elsewhere)
- Annotation identity and timing: annotation_id; observation_id, version and content hash of the exact bytes sent;
  observation knowledge time and class; t_ann (wall clock); t_dec it served; latency.
- Model identity: provider; model string requested; model ID resolved in the response; K(m) with its source; published
  cutoffs (training-data and reliable-knowledge) with URL and retrieval date; release date; for open weights, the weights
  hash, quantization, engine version and reasoning mode.
- Request and output: effort/sampling settings; tools/retrieval used (and the index time bound); system-prompt, prompt,
  schema and few-shot hashes, with the creation time of each and the knowledge times of any few-shot or context items;
  raw response hash, response id, parsed value, probabilities/confidence, cost.
- Referee-derived fields: class; flags (`masked` + re-identification rate, `schema_after_obs`, entity/date present in
  prompt); optional probe results (LAP-style date-only recall, S4) with the probe's own model ID.

**5.7 Referee invariants** (deterministic and testable)
1. Classes are computed from the recorded fields; there is no write path for an agent-asserted class.
2. If the requested ID ≠ the resolved ID, or the resolved ID is missing → `A-UNK`.
3. Any context item (few-shot, retrieval, state) whose knowledge time is after the observation's → `A-POST` at best.
4. Observations join on knowledge time + latency, using the version current at t_dec.
5. Universe and label membership are evaluated as of t_dec, honouring assignment and removal times.
6. Macro values come by vintage; filings by dissemination time, never by deemed date.
7. A gap between A-POST and clean performance is shown as a diagnostic only. It never certifies anything (S3).

**5.8 Open / UNKNOWN**
- A holdout dated before the *authoring* agent's training cutoff is not unseen by that agent's weights (S3 recall
  results). Recording the authoring model's K(m) with each candidate would make such holdouts visible as
  "memory-exposed". [INFERENCE — worth a manager decision]
- Sampling-based LAP for APIs without log-probs, EDGAR dissemination latency, the auditability of commercial
  point-in-time models, and the threshold for an acceptable crawl–publish gap: UNKNOWN.

## 6. Sources (all accessed 2026-10-02)
S1 arxiv.org/abs/2309.17322 · S2 arxiv.org/abs/2304.07619 · S3 arxiv.org/abs/2504.14765 · S4 arxiv.org/abs/2512.23847
S5 arxiv.org/abs/2603.11838 · S6 arxiv.org/abs/2502.21206 · S7 doi.org/10.3386/w35247 (abstract via OpenAlex)
S8 arxiv.org/abs/2510.07920 · S9 arxiv.org/abs/2601.13770 · S10 arxiv.org/abs/2607.18867 · S11 orbilu.uni.lu/handle/10993/68692 (Economics Letters) · S12 doi.org/10.1111/1475-679x.70058 (abstract via OpenAlex)
S13 doi.org/10.2139/ssrn.4754678 (not readable: SSRN/OpenReview blocked) · S14 asaf.manela.org (SSRN 5182756 blocked)
S15 arxiv.org/abs/2601.07992 · S16 arxiv.org/abs/2606.27199 · S17 arxiv.org/abs/2605.28359 · S18 arxiv.org/abs/2505.07078
S19 arxiv.org/abs/2506.00723 · S20 arxiv.org/abs/2403.12958 · S21 arxiv.org/abs/2409.19839 · S22 doi.org/10.1146/annurev-economics-120925-105620 (abstract via OpenAlex) · S23 arxiv.org/abs/2510.11677
S24 arxiv.org/abs/2404.18543 · S25 doi.org/10.17016/feds.2025.044 (abstract via OpenAlex) · S26 platform.claude.com/docs/en/about-claude/models/overview · S27 …/models/model-ids-and-versions
S28 platform.claude.com/docs/en/about-claude/model-deprecations · S29 developers.openai.com/api/docs/models (+ /gpt-6-astra)
S30 developers.openai.com/api/docs/deprecations · S31 ai.google.dev/gemini-api/docs/models
S32 ai.google.dev/gemini-api/docs/models/gemini-2.5-pro, …/gemini-3.8-flash · S33 deepmind.google/models/model-cards/gemini-3-8-flash/
S34 docs.typesafe.ai/models.md, docs.typesafe.ai/llms-full.txt · S35 fred.stlouisfed.org/docs/api/fred/realtime_period.html
S36 law.cornell.edu/cfr/text/17/232.13 · S37 tiingo.com/documentation/news (field text verified in the docs JS bundle) · S38 data.gdeltproject.org/documentation/GDELT-Event_Codebook-V2.0.pdf · S39 docs.x.com/developer-terms/policy
S40 academy.nansen.ai/articles/2132837-smart-money-101 · S41 home.treasury.gov/news/press-releases/jy0916, …/sb0057
S42 doi.org/10.1111/0022-1082.00192 (abstract via OpenAlex) · S43 martinfowler.com/articles/bitemporal-history.html
