# 15 — The organisation's control plane (System One) and its model seats under the AI allowance

Research leg, 2026-10-02, read-only at HEAD `1275aff`, for the owner's direction of that day ("multiple top level managers managing teams that have a head
manager that makes decisions and executing agents …", "a whole organisation with sub org's"). Read first: `CLAUDE.md`, `docs/PRINCIPLES.md`,
`docs/EDGE-FACTORY.md` §§ 4.2, 6, 8, 10, `R02`, `R10` #14, `BUILD-STATUS.md` 2026-10-01, and the owner's Jev paper (§§ 6.5, 6.8, 13.11–13.15, 14, 15, 20 —
used as leads only). **Labels:** `DOC[id]` primary page read 2026-10-02 (§ 6); `SEC[id]` third-party, independent benchmark or project-measured;
`RUN` a command run here today; `HIST` `BUILD-STATUS.md` 2026-10-01; `SOURCE` repo code read at `1275aff` (not run); `INF` my arithmetic or reasoning;
`UNKNOWN` looked for, not found. Two web searches of the shared 40 were used; everything else is a direct fetch of an official URL. Paraphrased; not legal
advice; prices move — re-check at dispatch.

## 0. Twelve-line answer
1. **(A) Yes, with limits.** A System-One layer can be the organisation's cheap nervous system — routing work, gating manager wakes, retry/stop/escalate, trace flags, acceptance pre-checks — if it runs LOCALLY for anything internal, abstains to a head manager (System Two), and can only make the organisation cheaper or more cautious: it never grants authority, never touches an order, never issues a verdict.
2. **Local quality, not price, is the constraint.** The realistic laptop stack is Ollaya (Apache-2.0, Windows zip, TypeSafe wire) serving decider-0.8b/2b (Apache-2.0; the project states nothing was distilled from Jev): 1.8–4.4 s per 5-question request on 24-core desktop CPUs, slower on a laptop (UNKNOWN); JevBench Intelligence 42 for decider-2b vs Jev 72 vs GPT-6 Luna 96. Laya (421M) scores chance-level with a 512-token context: a short-text pre-filter at most.
3. **Jev only for public items** (news, announcements) with generic schemas, at $0.042 per M input tokens, output free, through OpenRouter with no waitlist — never traces, plans, the genome bank or owner documents (TypeSafe keeps unrestricted telemetry rights), and never as a teacher for a local model (MCA § 2.3(b)).
4. **Shadow first.** Every question records its full distribution and is scored against outcomes code can observe (did the woken manager change its plan; the code's own refusal log for boundary flags); code floors — a maximum interval between wakes, retry caps, the daily cap — bound the damage of a bad threshold.
5. **(B) Paper runs do not spend the AI allowance** — the runner never calls a model; agent turns do, and they alone size the organisation.
6. **Measured (HIST):** a gpt-5.6-luna turn was ≈167K input (85% cached) + 4.3K output = $0.0132 at list (44 reporting turns averaged $0.0146); 48 turns — ≈51 active minutes by the record's timestamps (INF) — exhausted the owner's Codex plan window (tier not recorded): ≈16 credit-equivalents.
7. **Seats from today's prices (DOC) and independent indices (SEC):** executors on GPT-6 Luna ($0.006/turn on that profile), head managers on GPT-6.1 Sol ($0.11–0.27), top-level managers on 6.1 Sol xhigh, Claude Opus 5.5 or GPT-6 Astra ($0.11–1.40); Artificial Analysis: 6.1 Sol high 50.2 at $0.32/task vs Opus 5.5 high 53.6 at $1.82.
8. **Arithmetic (INF):** the default $5/day buys ≈810 GPT-6 Luna or ≈45 GPT-6.1 Sol turns; a 16-agent sub-org costs ≈$55/day on 30-minute timer wakes and ≈$13/day event-driven with triage → ≈6 agents at $5, ≈25 at $20, ≈62 at $50, ≈250 at $200 (value seats).
9. **On a subscription:** if plan usage scales with credits (INF; the vendor says it need not), the measured window holds ≈100 GPT-6 Luna or ≈6 GPT-6.1 Sol heavy turns; weekly caps are unpublished; Pro has no 5-hour limit; Sign in with ChatGPT lets the owner cap TradeAgent's weekly share, but eligibility for a paid closed app is UNKNOWN.
10. **Two blockers before cheap seats work (SOURCE + INF):** the shipped price table (read 2026-09-06) lacks gpt-6-luna, gpt-6-sol and gpt-6.1-sol, so they are priced as the dearest row ($16–39.60 reservations, refused under $5); and the 1.2M-token reservation lets one GPT-6.1 Sol turn fly at $5 — per-seat, enforceable allowances (the harness) are needed.
11. **Vendor lines:** Claude seats only via an API key (Anthropic bars claude.ai login in third-party products); Gemini's free tier trains on prompts; OpenRouter adds 5.5%; the harness's chat-completions route cannot call tools on GPT-6.1 Sol (Responses API needed).
12. **Protections stay as they are:** no model on the signal path, authority in-process, spend reserved before launch, unknown ≠ zero, keys origin-pinned, nothing internal leaves the machine (§ 4).

## 1. Control-plane design (A)

**Shape (INF, from the paper's § 14 pattern, re-checked against EDGE-FACTORY § 4.2):** code computes every exact fact (counts, budgets, timestamps, exit codes,
retry counters, evidence values) and owns every hard rule; System One answers bounded semantic questions over a code-assembled state, batched per state
(paper Rule 4), with an escape option in every Choice (Rule 3); a versioned threshold policy (data) turns each distribution into one of four outcomes —
act, skip, flag, abstain → System Two (the head manager's next turn). OpenRouter's own Jev cookbook describes the same cheap-gate-then-escalate cascade
DOC[R4]. Thresholds that trade money for attention may be proposed by managers (claims) and applied by app policy inside the budget; Q9's recall-first
threshold is app-fixed.

### 1.1 Question catalogue
| # | Question (primitive) | State it reads (assembled by code) | Runs | Consumer | Threshold · abstention | What code does |
|---|---|---|---|---|---|---|
| Q1 | `event_type`: R02 § 6.7a's 12 options incl. `other`, `insufficient_information` (Choice) | public tape item: headline, ≤1.5k-token excerpt, source tier, code-matched assets (R02 § 6.7a) | Jev or local | router | act if p₁ ≥ τ (start 0.6); p₂ ≥ 0.3 → both; else the "unrouted" digest | routes type × asset to unit inboxes by a code table; unit mandates never leave the machine |
| Q2 | `relevant_to_unit_k` (Noul per unit) | the item + unit k's mandate card | local | router | p ≥ τ; abstain → the head's daily digest | enqueues; counts feed the unit's envelope statistics |
| Q3 | `wake_needed` (Noul) | the manager's plan summary + code-deduplicated events since its last turn | local | wake scheduler | wake if p ≥ τ_w; code always wakes on owner messages, budget, verdict and alarm events, and at least every H_max | launches (reservation first) or logs "skipped, p = …" |
| Q4 | `urgency`: background / today / hours / now (Score 4) | as Q3 | local | manager queue | ordering only | sorts the digest |
| Q5 | `next_action`: execute, retry, ask_head, call_specialist, escalate, stop, wait, cannot_tell (Choice) | task card (goal, criteria, budget left), last step's exit code / error class / artifacts (code), redacted trace tail | local | executor loop | act if p ≥ τ; else `escalate` | only inside code bounds: retries ≤ N, deadline, reservation |
| Q6 | `retry_could_help` (Noul) | the failed step + prior attempts (code) | local | executor loop | retry only if p ≥ τ AND the code count < N | otherwise escalates to the head |
| Q7 | `progress_made` (Noul) | the turn's diff: rows and artifacts written (code) + report prose | local | worker record | code facts decide; the model reads prose only | three no-progress turns → escalate (a code rule) |
| Q8 | `criterion_met_i` (Noul per criterion) | the head's acceptance criteria + evidence index (app values) + deliverable | local | head manager | numeric criteria by code; VERIFIED only if code passes AND every p ≥ τ; else NEEDS REVIEW | never rejects on a model answer alone |
| Q9 | `boundary_attempt`: 9 classes of R02 § 6.7c (Choice) | trusted mission and grants (app), redacted trace, the code's refusal log | local | owner alert | recall-first: flag if p(≠ none) ≥ 0.2 | holds the worker's outputs for review; enforcement already happened in code |
| Q10 | `claim_supported_i` (Noul per claim) | claims extracted by code + evidence index | local | genome bank claims table, head | unsupported if p < τ | annotates the claim; measurement rows untouched |
| Q11 | `same_mechanism_as` / `reparametrisation_of_i` (Choice / Noul) | hypothesis + ≤20 killed ones retrieved by code (R02 § 6.7d) | local | trial ledger | charge if p ≥ τ | adds a family trial; never removes one |
| Q12 | `seat_needed`: local, luna, sol, frontier, cannot_tell (Choice) | task card + outcomes of similar past tasks (code) | local | dispatcher | cheapest seat with p ≥ τ; one failure → next tier (code) | clamps to seat ceilings and the daily cap |
| Q13 | `wasted_spend` (Score 4) | step list + per-step cost (code) | local | seat statistics | statistics only | an envelope input after ≥ 90 days (EDGE § 5) |
| Q14 | `addresses_automated_reader` (Noul) | any inbox or tape text | local (inbox) · Jev (public) | quarantine | p ≥ 0.3 | quarantines the item; data is never instruction |

**Calibration plan (shadow first, INF; paper Rules 7–11 used as a checklist):**
1. **Shadow (S0).** Every question is asked and recorded as a measurement — instrument (runtime, model, weights hash, device class), schema hash, state hash,
   full distribution, latency, CPU seconds — while code keeps today's behaviour (timer wakes, routing to every subscriber, the head reviews everything).
2. **Outcomes code can observe:** Q3 — did the woken manager write a directive or change its plan; Q1/Q2 — which unit accepted or re-routed the item; Q5/Q6 —
   what the head decided and whether the retry succeeded; Q8 — the head's or referee's later result; Q9 — the gateway/pipe refusal log (ground truth from
   code, cheap); Q11 — the exact-parameter diff on a labelled sample. Never Jev's answers as labels (MCA § 2.3(b)).
3. **Fit** isotonic or Platt maps per question and instrument on a rolling window with a frozen holdout; report ECE, Brier, selective risk and coverage,
   per unit and seat; alarm on drift (A7 in R02 shows aggregate calibration hiding subgroup failures).
4. **Grant autonomy one question at a time,** only in the saving direction (skip a wake, pick a cheaper seat), after ≥200 outcomes and coverage at a stated
   error; code floors stay (H_max, retry caps, the cap). A new model or runtime version is a new instrument: shadow again before the pin moves.

### 1.2 Local-model candidates (ordinary Windows laptop, CPU only)
CPU = Ollaya's p50 for a 5-question request (≈280–760 tokens) on an i9-13900K (24 threads, WSL2) / a Threadripper 3960X, 2026-10-01, SEC[L2]; quality =
Bespoke Labs' public benchmark (3,880 questions) measured by Ollaya, SEC[L2], and JevBench v1.5.4 Intelligence/Calibration, SEC[B1]. A laptop is slower; by how
much is UNKNOWN (R02 guessed 2–3×). RAM = parameters × 4 bytes at fp32 (INF).

| Candidate | Licence (code / weights) | Size, context, RAM | CPU latency | Quality and calibration | Windows, no-terminal path | Verdict | Labels |
|---|---|---|---|---|---|---|---|
| **Ollaya** v0.9.0 (released 2026-10-02) | Apache-2.0 | `ollaya-windows-amd64.zip` 39 MB + `sha256sum.txt`; ONNX Runtime fp32, llama.cpp for GGUF; TypeSafe wire on `127.0.0.1:11435`, bearer optional | n/a | applies fitted temperatures (same Nimble weights: ECE 0.022 vs 0.122 on Ollama); Windows parity: winnow:e4b on CPU matches the CUDA golden on 501/505 decisions | the app fetches the pinned zip into `%LOCALAPPDATA%\TradeAgent\tools`, checks a hash held in built-in data, runs `ollaya serve` with `CreateNoWindow`; NOT the documented `irm … \| iex` | **the runtime**; one maintainer, repo created 2026-09-23 → pin and hash every release | licence, assets, defaults DOC[L1]; quality SEC[L2]; path INF |
| **decider-0.8b** | Apache-2.0 / Apache-2.0, 752M params | ≈3.0 GB fp32 | 1.8 / 3.0 s | acc 0.669, ECE 0.061 | via Ollaya | **default** for Q3–Q13 on 8–16 GB machines | DOC[L3]; SEC[L2]; RAM INF |
| **decider-2b** | Apache-2.0 / Apache-2.0, 1.88B; "nothing was distilled from Jev" (self-declared) | ≈7.5 GB fp32; GGUF Q4_K_M 1.3 GB, 32k context | 3.4 / 4.4 s fp32; GGUF 0.12–0.31 s per 40–120-token request on 8 server threads (author); ≈0.22 s per 3 questions on a Snapdragon X Elite, a laptop-class chip (community) | acc 0.703, ECE 0.087; JevBench I 42.3, C 71.5 | via Ollaya (fp32); GGUF via the author's Python package today | quality tier for ≥16 GB or batches; the GGUF route is a probe | DOC[L3] (author/community figures self-reported); SEC[L2,B1]; RAM INF |
| kev-0.8b | Apache-2.0 | 0.8B | 1.3 / 2.8 s | acc 0.611, ECE 0.035 (best-calibrated small model) | via Ollaya | Noul gates (Q3, Q6) if its accuracy holds on our data | SEC[L2] |
| Laya en (ModernBERT-large) | Apache-2.0 / Apache-2.0, 421M (base 396M, Apache-2.0) | 512-token context; ≈1.7 GB | 0.62 / 0.92 s; its README 0.19–0.46 s preloaded | acc 0.532 with 453 of 3,880 requests rejected, Score questions 0.087; JevBench I 0.0, C 73.7; confidently wrong on non-Latin scripts (its README) | via Ollaya (ONNX); its own install needs Python | **pre-filter only** for short English public text; never traces | DOC[L4,L5]; SEC[L2,B1] |
| Laya multilingual (mmBERT-base) | Apache-2.0 | 322M, 8,192 context | 0.31 / 0.47 s | acc 0.579, ECE 0.155; JevBench I 2.4 | via Ollaya | language/topic pre-filter at most | DOC[L4]; SEC[L2,B1] |
| winnow:e4b | Gemma-based, Apache-2.0 weights; teacher unnamed | Q8 GGUF 8.0 GB | 6.1 / 4.0 s | acc 0.734, ECE 0.071 | via Ollaya | overnight batches on ≥16 GB; provenance unclear → not by default | R02 (licence); SEC[L2] |
| Logit readout of a small local LLM (llama.cpp + our readout, AnyJev method) | llama.cpp MIT (`win-cpu-x64` zip 19.3 MB, b11344 of 2026-10-02); AnyJev Apache-2.0 | any GGUF | the model's own prefill time | raw readouts are order-biased; AnyJev self-reports order flips 0.230 → 0.073 without labels and a closed-form head fitted on 100–300 of OUR labels | binaries yes; AnyJev's tooling is Python/vLLM | fallback and research path; we would own prompt layout and calibration | DOC[L6,L7]; latency INF |
| (for comparison) Jev 1.13 | proprietary | 32K via OpenRouter | median 0.62 s, hosted | JevBench I 72.0, C 88.0 at $0.032 per 1,000 decisions | API key in the app | public items only (§ 1.3) | DOC[R4]; SEC[B1] |

**CPU budget (INF):** decisions per day ≈ CPU-seconds granted ÷ seconds per request × questions per request; e.g. 2 CPU-hours a day at ≈6 s per 5-question
request (decider-0.8b at an assumed 2× desktop time) ≈ 1,200 requests ≈ 6,000 answers. Hence: code first, batch per state, smallest adequate model, queue the
non-urgent, and record CPU seconds as the local cost (unknown ≠ zero).

### 1.3 Jev — where it may and may not be used
- **Facts (DOC[T1,T2,R4]):** `jev-1.13.0`, $0.042 per M input tokens, output free; 100K tokens/s and 40 requests/s direct, limits change without notice;
  64K per request (32K for state + longest question); text only; not trained on customer requests; aliases move, so pin the versioned id. Through
  OpenRouter: same price, 32K context, no waitlist, `/api/v1/systemone`; OpenRouter's data policy for that endpoint says no training and no prompt retention.
  MCA: no standalone resale (§ 2.3(a)); no distillation, imitation or competing product (§ 2.3(b)); telemetry — logs, hashes, classifications, learnings — usable
  by TypeSafe without restriction, surviving termination (§ 4.3, § 10.4). A 1–2K-token public item costs ≈ $0.00004–0.00008 (INF).
- **May:** Q1 and Q14 on public tape items, a source-reliability cold-start prior (R02 § 6.7e) — with generic, versioned schemas, on the owner's own key bound
  to its origin (`U-key-host-pin`).
- **May not:** traces, task cards, plans, digests, the genome bank, hypotheses, owner documents (inbox), unit mandates, schemas that encode strategy ideas
  (R10 #14; MCA § 4.3); the "Jev Router" on OpenRouter, which would send agent requests out DOC[R3]; Jev answers as labels or teacher for any local model,
  or agreement-with-Jev to choose or tune one (§ 2.3(b); R02 § 2); anything on the signal path except § 4.2's recorded-answer route with rule 8's conditions.

### 1.4 Privacy and legal lines
- **Local means loopback.** Ollaya binds `127.0.0.1` with no auth by default DOC[L1]: the app sets a per-start bearer it generates (never an owner key). Download
  URLs and hashes (the Ollaya zip; weights pinned by Hugging Face commit and sha256, fetched from their authors) are built-in data, never agent-writable.
- **No new recipient for traces.** The vendor running an agent already sees that agent's transcript — that is the agent's own work; the control plane adds
  none. OpenAI API data is not used for training by default and abuse logs are kept ≤30 days, ZDR for eligible customers DOC[O7]; plan usage follows the
  account's training setting (EDGE § 10.6).
- **Free tiers that train are out** for owner or trace material: the Gemini API free tier is used to improve Google's products, the paid tier not DOC[G1].
- **Anthropic** does not allow third-party products to offer claude.ai login or plan limits unless approved: Claude seats only through the owner's API key DOC[A4].
- **ChatGPT plan inside an app** is an official preview (Sign in with ChatGPT) for open-source and locally hosted apps; paid or remotely hosted apps go through
  an interest form DOC[O5]. TradeAgent is local, paid and closed: UNKNOWN until OpenAI answers.
- **Licences:** Apache-2.0 / MIT across the local stack; record the licence with each instrument; weights are never re-hosted by us.

## 2. Seats (B)

### 2.1 Runtimes TradeAgent can host — price and limits read today
| Runtime | Price and limits | Data and terms | Fits |
|---|---|---|---|
| OpenAI API key (the app's chat-completions harness, or Codex CLI on a key) | per M tokens, input / cached / cache-write / output, ≤272K context: GPT-6 Luna 0.10/0.01/0.125/0.50; GPT-6.1 Sol 2.00/0.10/2.50/10.00; GPT-6 Sol 2.00/0.20/2.50/10.00; GPT-6 Astra 10/1/12.50/50; GPT-5.6 Luna 0.20/0.02/0.25/1.20; GPT-5.6 Sol 4/0.40/5/20 (the page notes a promotional price through at least 2026-11-21); Batch and Flex −50% DOC[O1]; Tier 1: 500 RPM, 500K TPM for 6 Luna and 6.1 Sol DOC[O4] | no training by default DOC[O7]; on chat-completions GPT-6.1 Sol has no tool calls and GPT-6 Luna/Sol call functions only at reasoning `none` DOC[O4] | every seat; the harness needs the Responses API for GPT-6 tools |
| ChatGPT plan through Codex CLI (today's runtime) | Free $0, Go $8, Plus $20, Pro $100/$200/$500, Business $20–25 per user; Plus local messages per 5 h: Astra 5–45, 6.1 Sol 15–160, 6 Sol 15–150, 6 Luna 350–3,000; weekly limits may apply; Pro has no 5-h limit; Fast uses 2.5×, Astra Ultrafast 8× DOC[O2]; Pro $100 = 5× Plus, $200 = 20× until 29 Oct then 10× SEC[S1]; credit token rates imply 1 credit ≡ $0.04 of API list (INF, O1 ÷ O2); credit purchase price UNKNOWN (help.openai.com → HTTP 403, RUN) | the account's training setting | a few seats; capacity per window (§ 3d) |
| ChatGPT plan via Sign in with ChatGPT (Responses API, OAuth in the system browser) | counts against the plan, no new allowance; the owner sets a weekly % cap per app DOC[O6]; preview: `store:false`, `stream:true`, several fields and hosted tools unsupported DOC[O5] | open-source / locally hosted apps; paid apps by interest form | the no-terminal plan path, if eligible |
| Anthropic API key | Opus 5.5 4 / 5 (5-min write) / 0.20 (hit) / 20; Sonnet 5.5 2 / 2.50 / 0.20 / 10; Haiku 4.5 1 / 1.25 / 0.10 / 5; Fable 5.1 10 / 12.50 / 0.25 / 50; Batch −50%; Claude 4.7+ tokenizer ≈30% more tokens DOC[A1]; its OpenAI-SDK compatibility is described as not production-ready DOC[A5] | API terms | top-level manager (Opus), head alternative (Sonnet) |
| Claude Pro/Max plans | Pro $20 ($17 a month annually), Max from $100 (5× or 20× Pro per 5-h session), weekly limits, Claude Code shares the pool, no fixed message counts DOC[A2,A3] | claude.ai login barred in third-party products DOC[A4] | not usable by TradeAgent |
| Gemini API | 3.8 Flash 0.75 / 0.075 / 3.75; 3.1 Pro Preview 2.00 / 0.20 / 12.00; 3.1 Flash-Lite 0.25 / 0.025 / 1.50; Batch −50%; per-model RPM/TPM shown only in AI Studio DOC[G1,G2] | free tier trains, paid does not DOC[G1] | executor alternative, paid only |
| Gemini CLI | Google login 1,000 requests/day (Code Assist individual), AI Pro 1,500, Ultra 2,000; API-key free tier 250/day, Flash only DOC[G3] | free tiers as above | not for owner material |
| OpenRouter | provider price + 5.5% platform fee (Standard) DOC[R1]; an in-flight spend budget refuses a request (402) before it reaches a provider; free models 50 or 1,000 requests/day DOC[R2]; DeepSeek V4.1 Flash 0.30/1.20 DOC[R3] | data policy per provider; EU in-region routing on Business only DOC[R1] | Jev; an open-weight executor fallback |

### 2.2 Seat → model class
$/turn: E = the measured executor profile (167,264 input, 141,568 cached, 4,336 output; HIST); M = the same input with 20,000 output for a reasoning-heavy
manager turn (INF). Claude ×1.3 for its tokenizer (INF from DOC[A1]). Index = Artificial Analysis Intelligence Index and its cost per index task, SEC[B2].

| Seat (count) | Pick (alternatives) | Why — evidence | $/turn E · M | Notes |
|---|---|---|---|---|
| Top-level manager (1–3: direction, envelopes inside owner caps) | GPT-6.1 Sol xhigh (Claude Opus 5.5 high; GPT-6 Astra high) | index 51.0 at $0.39/task (Opus 53.6 at $1.82; Astra 50.9 at $1.73) | 0.109 · 0.266 (Opus 0.283 · 0.690; Astra 0.615 · 1.399) | wakes on digests and escalations; Opus only on an API key |
| Head manager (1 per team: decides, writes task cards and criteria) | GPT-6.1 Sol medium/high (Claude Sonnet 5.5 high) | index 47.8–50.2 at $0.21–0.32/task (Sonnet 46.8 at $1.12); Codex recommends 6.1 Sol for agentic work DOC[O3] | 0.109 · 0.266 | Codex CLI, or a Responses-API harness |
| Executor (3–6 per team) | GPT-6 Luna high/max (GPT-5.6 Luna; DeepSeek V4.1 Flash via OpenRouter) | index 32.9–38.1 at $0.029–0.068/task; "Use Luna for focused, repeatable tasks" DOC[O3] | 0.0062 · 0.014 | half of today's gpt-5.6-luna per token; needs a price row (§ 4) |
| Deep auditor (only when Q9/Q10 flag) | GPT-6.1 Sol medium | as head manager | 0.109 | reads the flagged trace only |
| Router, triage, verifier (every event) | code + local System One (decider-0.8b/2b, kev-0.8b); Jev for public items | JevBench: decider-2b I 42.3/C 71.5; Jev 72.0/88.0; GPT-6 Luna as a JSON-schema decider 96.2/95.6 at $0.114 per 1,000 decisions SEC[B1] | $0 vendor; seconds of CPU | abstains to the head manager |

Not picked: Claude Haiku 4.5 (index 16.9 at $0.28/task) and Gemini 3.8 Flash (40.9 at $1.24/task) cost more per index task than GPT-6 Luna or GPT-6.1 Sol SEC[B2].

## 3. Quota arithmetic (INF from DOC prices and HIST measurements)
```
c_turn = (U·p_in + C·p_cached + O·p_out) / 1e6          (Claude ≥ 4.7: × 1.3)
U = 25,696  C = 141,568  O = 4,336   (HIST: this profile = $0.0132 on gpt-5.6-luna; 44 reporting turns averaged $0.0146)
turn time (HIST): first turn ended ≤1 m 44 s after the start press; five turns by 15:54Z; refused turns 3–4 s
reservation (HIST): $0.324 per gpt-5.6-luna turn (1.2M × 0.25 + 20K × 1.20) ≈ 22× its average cost
manager turns: O = 20,000            (INF; measure per seat in the first week)
daily:     D = Σ_seat n_s · w_s · c_s ≤ B          (B = the owner's daily cap; list-price equivalent even on a plan)
in flight: Σ R ≤ B − spent,  R = A_in · p_write + A_out · p_out      (A = 1,200,000 / 20,000 by default, SOURCE Trading.cs:691)
sub-org  = 1 top-level manager + 3 teams × (1 head + 4 executors) = 16 agents
timer wakes: every agent 48 turns/day (the 30-min default, HIST) · event-driven + triage: top 6, head 12, executor 24
```
**(a) Turns per day if the whole budget went to one seat model (API list, profile E):**

| B per day | GPT-6 Luna | GPT-5.6 Luna | GPT-6.1 Sol | Opus 5.5 | GPT-6 Astra |
|---|---|---|---|---|---|
| $5 (today's default cap) | 812 | 379 | 45 | 17 | 8 |
| $20 | 3,250 | 1,518 | 183 | 70 | 32 |
| $50 | 8,125 | 3,795 | 459 | 176 | 81 |
| $200 | 32,502 | 15,181 | 1,836 | 706 | 325 |

**(b) Organisation size (agents) — managers on profile M, executors on E:**

| B per day | value seats (6.1 Sol / 6.1 Sol / 6 Luna) timer · triage | premium (Opus top) timer · triage | today's models (5.6 Sol / 5.6 Sol / 5.6 Luna) timer · triage |
|---|---|---|---|
| cost per sub-org-day | $54.53 · $12.93 | $74.92 · $15.47 | $115.00 · $27.29 |
| $5 | 1.5 · 6.2 | 1.1 · 5.2 | 0.7 · 2.9 |
| $20 | 5.9 · 24.8 | 4.3 · 20.7 | 2.8 · 11.7 |
| $50 | 14.7 · 61.9 | 10.7 · 51.7 | 7.0 · 29.3 |
| $200 | 58.7 · 247.6 | 42.7 · 206.8 | 27.8 · 117.3 |

Event-driven waking with triage multiplies the affordable organisation by ≈4.2× (value) to ≈4.8× (premium); moving from today's models to GPT-6.1 Sol +
GPT-6 Luna by ≈2.1×; together ≈8.9× per dollar. A skipped manager wake saves one manager turn ($0.11–1.40 by seat) for a few CPU-seconds. The triage
multiplier rests on an assumed wake profile (UNKNOWN, § 5 #8). Paper runs add nothing to these figures.

**(c) Turns in flight under the reservation (cap B, nothing spent yet):**

| B | GPT-6 Luna 1.2M · 300K allowance | GPT-5.6 Luna 1.2M | GPT-6.1 Sol 1.2M · 300K | Opus 5.5 1.2M · 300K |
|---|---|---|---|---|
| $5 | 31 · 105 | 15 | 1 · 5 | 0 · 2 |
| $20 | 124 · 421 | 61 | 6 · 21 | 3 · 10 |
| $50 | 312 · 1,052 | 154 | 15 · 52 | 7 · 26 |

A smaller allowance is only honest where it is enforced: the harness sends `max_completion_tokens` (SOURCE `RuntimeManifest.cs:671-682`) and, being app
code, can bound its own context (INF); a Codex CLI turn is bounded only by the model's context window, so its reservation stays at 1.2M.

**(d) On a ChatGPT plan (INF; "credit prices alone don't determine included usage", DOC[O2]):**

| Model on the owner's plan (5-hour window, tier not recorded) | Heavy turns per window | Per day at most (24 h ÷ 5 h = 4.8 windows) |
|---|---|---|
| GPT-5.6 Luna — measured: 48 turns in ≈51 active min (15:48–18:08Z), reset "10:30 PM" = 20:30Z if the zone was CEST, as this Mac's is today (RUN `date`) | ≥48 (≈16 credit-equivalents, $0.64 of list) | ≈230, before weekly caps (UNKNOWN) |
| GPT-6 Luna (0.154 credits/turn) | ≈100 | ≈490 |
| GPT-6.1 Sol (2.72 credits/turn) | ≈6 | ≈28 |
| GPT-6 Astra (15.4 credits/turn) | ≈1 | ≈5 |
| Pro $100 / $200 / $500 | no 5-h window; weekly ≈5× / 10–20× / ? Plus (SEC[S1]) | UNKNOWN |

Cross-check: the vendor's own Plus ranges put 6.1 Sol at ≈1/20 of 6 Luna per window; the credit arithmetic gives ≈1/18. Our turns are heavy (167K
context), so they sit below the vendor's message ranges. In attempt 2 the plan bound first: $1.94 of the $5 cap was charged, including four $0.324
reservations (HIST). A plan holds a few executors and occasional manager turns; an organisation needs an API budget.

## 4. Protections touched, and how each is kept
1. **No model on the signal path** (`docs/COUNCIL.md` rule 8 as read in EDGE-FACTORY § 6.1). Control-plane answers decide about WORK. They never reach the
   runner or the gateway, never time, place or size an order, and never enter a version's evidence; perception features stay on § 4.2's recorded-answer path
   with rule 8's three conditions.
2. **Operator authority, in-process.** Managers' decision rights are over work only. Mode, kill switch, live activation, approvals, limits, capital, keys, pins
   and updates stay owner-only with no pipe op; a control-plane answer can only skip, route, flag or escalate. The owner's "no limits" becomes one owner-set
   ceiling with app-computed sizing beneath it, never an agent's choice.
3. **Spend reserved before launch** (rules 3–4, `AiAttemptStore` admission). Every turn still reserves first; a triage mistake spends inside the cap, never past
   it; smaller per-seat reservations only where the bound is enforced (§ 3c).
4. **Unknown cost is never zero.** The shipped table (`ListPrices.cs`, read 2026-09-06) has no gpt-6-luna, gpt-6-sol or gpt-6.1-sol; an unpriced model is
   charged at the runtime's dearest row (`RuntimeManifest.cs:963-971`): Astra on codex ($16 reservation), a "-pro" row on the harness ($39.60) — refused under $5
   (SOURCE + INF). Fix as data: dated rows from DOC[O1]. Plan usage stays a list-price equivalent with the vendor refusal held (`U-vendor-limit`); local
   answers record CPU seconds; vendor-native subagents inside one CLI turn must fit that turn's reservation (reporting UNKNOWN, § 5 #5).
5. **Keys origin-pinned** (`docs/queue/U-key-host-pin.md`). Each new provider — `api.anthropic.com`, `generativelanguage.googleapis.com`, `openrouter.ai`,
   `api.typesafe.ai` — is a built-in row and a pasted key is bound to that origin; the local runtime gets an app-generated bearer, never an owner key.
6. **Nothing sensitive leaves the machine** (R10 #14). Q2–Q13 run locally; only Q1/Q14 on public items may go to Jev; every answer records the host that computed
   it, so the rule is auditable; training free tiers are refused for this material.
7. **Measurement versus claim.** Answers are app-written rows in their own table — instrument including device class (CPU and GPU differ on ≈1% of decisions,
   SEC[L2]), schema hash, state hash, full distribution; agents' notes and threshold proposals stay claims.
8. **Evidence and trials.** Q11 can add a family trial and never remove one; verdicts stay the referee's; Q8 can mark VERIFIED or NEEDS REVIEW, never a verdict.
9. **No terminal.** The app installs Ollaya from its zip and pulls weights itself, behind an owner press that names size and source; plan sign-in opens the system
   browser; keys are pasted in the app.
10. **Advisory until containment.** The unconfined CLI agent can reach `127.0.0.1` and read the bearer from the same user's processes; say so, as EDGE § 6.11 does.

## 5. UNKNOWNs and the probes that close them
| # | UNKNOWN | Why it matters | Probe |
|---|---|---|---|
| 1 | The owner's ChatGPT tier; whether other Codex use shared the 2026-10-01 window (a 20:30Z reset implies a window opened ≈15:30Z, before the app's first turn at 15:48Z — INF) | the only plan-capacity datum | owner reads Settings › Usage; the app logs turns per window from `U-vendor-limit`'s refusal records |
| 2 | Weekly caps per plan; credit purchase price | daily capacity on a plan | help.openai.com refused fetches (403, RUN); one metered week |
| 3 | Pro multiples (5× / 10× / 20×) | plan choice | SEC only today; the official article when fetchable |
| 4 | Sign in with ChatGPT eligibility for a paid, closed, local app | the no-terminal plan path with a per-app weekly cap | OpenAI interest form (owner) |
| 5 | Whether `codex exec --json` reports subagent usage | vendor subagents could spend inside one turn | one turn that spawns a subagent; read its usage |
| 6 | Local latency and RAM on the owner's laptop | all of Q2–Q13 | Ollaya zip on the Windows box via `tools/`: decider-0.8b/2b, kev-0.8b, laya on a 50-state fixture — p50/p95, RAM, load time |
| 7 | Local accuracy on OUR questions | every threshold | shadow, ≥200 outcomes per question |
| 8 | Share of manager wakes with nothing to do | the ≈4× triage multiplier | in shadow, count wakes whose turn wrote no directive |
| 9 | decider via GGUF in Ollaya (2B is ≈7.5 GB at fp32) | laptop fit | release notes; box probe |
| 10 | A same-vendor System One for traces (GPT-6 Luna as a JSON decider: I 96 vs decider-2b 42) | quality; adds no new recipient | owner/manager decision; default stays local (R10 #14) |
| 11 | The harness on the Responses API | GPT-6 tools with reasoning | code survey and a brief |
| 12 | Anthropic and Gemini rate tiers | adding runtimes | read at dispatch |
| 13 | Jev latency from the laptop | Q1 timing | `U-decision-port` probe (EDGE § 11) |
| 14 | Turn profile per seat (managers assumed O = 20K) | all of § 3 | the first metered week per seat |

## 6. Source registry (all read 2026-10-02)
| id | URL |
|---|---|
| O1 · O2 · O3 | https://developers.openai.com/api/docs/pricing (.md) · https://learn.chatgpt.com/docs/pricing (.md; developers.openai.com/codex/pricing redirects there) · https://learn.chatgpt.com/docs/models (.md) |
| O4 | https://developers.openai.com/api/docs/models/gpt-6.1-sol · /gpt-6-luna · /gpt-6-sol · /gpt-6-astra · /gpt-5.6-luna (.md) |
| O5 · O6 · O7 | https://developers.openai.com/siwc/token-sharing-open-source (.md) + /preview-limitations · https://learn.chatgpt.com/docs/sign-in-with-chatgpt (.md) · https://developers.openai.com/api/docs/guides/your-data (.md) |
| A1 · A2 · A3 | https://platform.claude.com/docs/en/about-claude/pricing (.md) · https://claude.com/pricing · https://support.claude.com/en/articles/11145838-using-claude-code-with-your-pro-or-max-plan |
| A4 · A5 | https://code.claude.com/docs/en/agent-sdk (from platform.claude.com/docs/en/agent-sdk/overview) · https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk |
| G1 · G2 · G3 | https://ai.google.dev/gemini-api/docs/pricing (last updated 2026-10-01) · https://ai.google.dev/gemini-api/docs/rate-limits · https://raw.githubusercontent.com/google-gemini/gemini-cli/main/docs/resources/quota-and-pricing.md |
| R1 · R2 · R3 · R4 | https://openrouter.ai/pricing · https://openrouter.ai/docs/api_reference/limits (.md) · https://openrouter.ai/api/v1/models · https://openrouter.ai/typesafe/jev-1.13 + https://openrouter.ai/docs/guides/community/jev (.md) |
| T1 · T2 · T3 | https://docs.typesafe.ai/models · https://typesafe.ai/legal/mca · https://evals.typesafe.ai/agent_trace_observability (the trace workflow the paper's § 13.15 describes) |
| L1 · L2 | https://github.com/ollaya-dev/ollaya (README, docs/api.md, releases v0.9.0 via api.github.com) · https://ollaya.dev/results + /data/results/2026-10-01-latency-rtx4090-cpu(-extra).json, -rtx5090-cpu.json, 2026-09-30-public-benchmark-rtx5090-cuda.json, 2026-10-01-parity-rtx4090-windows.json |
| L3 · L4 · L5 | https://github.com/Mapika/decider + huggingface.co/api/models/Mapika/decider-0.8b, -2b · https://github.com/NandhaKishorM/laya + huggingface.co/api/models/convaiinnovations/laya · huggingface.co/api/models/answerdotai/ModernBERT-large |
| L6 · L7 | https://github.com/nokia-applied-research/AnyJev (README) · https://github.com/ggml-org/llama.cpp/releases (b11344) |
| B1 · B2 | https://www.benchmarkheaven.com/jev-models (JevBench v1.5.4, independent) · https://artificialanalysis.ai/leaderboards/models (Intelligence Index and cost per task, read from the page's data) |
| S1 | https://www.developersdigest.tech/blog/codex-usage-limits-pricing-2026 · https://threatfrontier.com/articles/chatgpt-pro-100-vs-200-5-vs-20-usage-explained-2026 (secondary; Pro multiples) |
| Repo | `BUILD-STATUS.md` 2026-10-01 (attempt 2, both parts and end); `src/TradeAgent.AgentRuntime/ListPrices.cs`; `RuntimeManifest.cs:639,671-682,963-971`; `src/TradeAgent.Core/Trading.cs:691`; R02; R10 #14; `docs/queue/U-key-host-pin.md` |
