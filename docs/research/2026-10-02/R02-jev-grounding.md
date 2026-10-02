# 02 — Jev grounding: TypeSafe's System One model, checked against primary sources

Research leg, 2026-10-02. The owner's paper was used as a list of leads, not as a source. Raw fetches sit next to this file (`raw/`, `md/`, `gh/`, `hf/`). **Labels:** `DOC@2026-10-02 [id]` =
first-party document of the party the claim is about, fetched today (a project's README is first-party for its licence/size; its performance numbers are marked *self-reported*). `SECONDARY [id]` =
third-party paper, benchmark or measurement, fetched today. `INFERENCE` = my reasoning from cited facts. `UNKNOWN` = looked for, not found. Not legal advice.

## Source register
| id | URL |
|---|---|
| D1 · D2 · D3 · D4 | https://typesafe.ai/blog/introducing-system-one-models-and-jev (dated Sep 15, 2026) · https://docs.typesafe.ai/models · https://docs.typesafe.ai/api · https://api.typesafe.ai/openapi.json (spec behind /redoc) |
| D5 · D6 · D7 · D8 | https://docs.typesafe.ai/primitives/choice · /primitives/score · /primitives/noul · https://docs.typesafe.ai/model-jaggedness/jev-1.13 |
| D9 · D10 · D11 | https://docs.typesafe.ai/confidence · https://docs.typesafe.ai/sdk (+ python, javascript, changelogs) · https://docs.typesafe.ai/cookbooks/parallel_questions |
| D12 · D13 · D14 | https://docs.typesafe.ai/cookbooks/autoresearch_feature_discovery · /concepts/use-case-map · /concepts/how-to-build-with-system-one |
| D15 · D16 | https://typesafe.ai/ (home page FAQ: determinism, pricing, waitlist) · https://typesafe.ai/legal/mca (MCA, last updated Sep 23, 2026) |
| D17 · D18 · D19 · D20 | https://typesafe.ai/legal/acceptable-use-policy · /legal/data-processing · /legal/privacy-policy · https://evals.typesafe.ai/agent_trace_observability |
| O1 · O2 · O3 | https://openrouter.ai/docs/guides/community/jev · https://openrouter.ai/typesafe/jev-1.13 · https://openrouter.ai/terms |
| A1 · A2 · A3 | https://arxiv.org/abs/2609.37647 (Deußer, Sparrenberg, Sifa) · /2609.34024 (Madrid-García, Merino-Barbancho) · /2609.35342 (Porcedda) |
| A4–A9 | arxiv.org/abs/2609.39496 (arithmetic rejection) · 2609.33209 (probability axioms) · 2609.33971 (coherence) · 2609.33401 (agent security) · 2609.24052 (crash narratives) · 2609.32160 (evidence audit) |
| B1 | https://www.benchmarkheaven.com/jev-models (JevBench v1.5.4) |
| G1 | https://github.com/ollaya-dev/ollaya · https://ollaya.dev/results (+ /data/results/*.json) |
| G2–G9 | github.com/NandhaKishorM/laya · Mapika/decider · allebee/jevk5 · bespokelabsai/nimble · jaredpalmer/kev · Contrastive-LM/CLM · TheoLeeCJ/SemIf-OpenJev · nokia-applied-research/AnyJev |
| G10 · G11 · G12 | https://huggingface.co/EldanRing/Winnow-E4B (+ Winnow-12B; weight licences via huggingface.co/api/models/{id}) · https://github.com/ollama/ollama/releases/tag/v0.35.0 · https://huggingface.co/togethercomputer/Tev1-4B-experimental |
| G13 · G14 · G15 | https://github.com/Gaurav-Gosain/jev-alpha-bench · https://github.com/suenot/jev-trading-bots-research · https://www.nuget.org/packages/JevSharp + https://github.com/awaescher/OllamaSharp |

## 0. Verdict
- **Jev is real and usable for us today.** TypeSafe AI, Inc. (San Francisco) launched it on 2026-09-15. `jev-1.13.0` is served over a small, documented, OpenAPI-described HTTP API. Direct access is
  waitlist-gated; OpenRouter resells it with no waitlist at the same price, but with a 32K rather than 64K token budget. There is no official .NET SDK. The two-endpoint spec makes an in-repo C# client
  small (INFERENCE). DOC@2026-10-02 [D1][D2][D4][O1]
- **Where it is strong:** cheap (≈$0.04 per 1,000 one-kilotoken decisions), fast (0.16–0.6 s seen from third parties), and accurate on short, well-scoped semantic decisions. SECONDARY [A1][A2][O2][B1]
- **Where it is weak:** numbers, dates and multi-hop reasoning. Finance text is one of its weaker and worse-calibrated sets (Financial PhraseBank 73.0%, ECE 0.138). The only public news-to-returns
  test (unreviewed) finds good reading but no tradeable next-close signal. SECONDARY [A1][G13]
- **What it is for us:** a triage, labelling and control-plane layer whose outputs we recalibrate against our own realised outcomes. It is not an alpha source in itself. INFERENCE
- **Legal bottom line** (§2): using its probabilities as features in our own price model looks permitted, and TypeSafe documents that pattern. Storing outputs for our own audit and replay looks
  permitted. Training any model to imitate Jev is clearly prohibited; that restriction survives termination and is excluded from the liability cap.
- **Best local fallback** (§4): Ollaya (Apache-2.0, Windows x64 zip, TypeSafe wire-identical) serving Mapika `decider` 0.8B or 2B (Apache-2.0, states that nothing was distilled from Jev). Expect a
  large quality drop and CPU-seconds per request, so it is a degraded mode, not a replacement.

## 1. Existence and facts (Task 1)
| Fact checked | Status | Evidence |
|---|---|---|
| TypeSafe AI, Inc. exists. Founder Diogo Almeida (states prior work on ChatGPT's research at OpenAI). Launch post dated 2026-09-15; SF notice address in MCA | CONFIRMED | DOC@2026-10-02 [D1][D16] |
| Model id `jev-1.13.0`. Aliases `jev-latest` and `jev-preview` both point to it. No preview build exists. The response's `model` field names the versioned id that answered | CONFIRMED | DOC@2026-10-02 [D2][D4] |
| `POST https://api.typesafe.ai/v1/systemone`, plus `GET /v1/models` | CONFIRMED | DOC@2026-10-02 [D3][D4] |
| Request is `{model, state, questions}`. `state` is a string, object or array. Each question is `{type, instructions, criteria}`. The question id is never sent to the model | CONFIRMED | DOC@2026-10-02 [D3] |
| Response is `{model, answers, usage{input_tokens, output_tokens}}`. Choice gives choice, probabilities and confidence; Score gives score, legend, probabilities and confidence; Noul gives `noul` only. No request id, seed or server latency field | CONFIRMED (the absences: INFERENCE from the spec) | DOC@2026-10-02 [D3][D4] |
| Choice allows ≤255 options. Score needs at least two levels and the API accepts up to 10. The OpenAPI schema itself only enforces `minItems: 1`, with no maximum, so we must validate client-side | CONFIRMED | DOC@2026-10-02 [D3][D5][D6][D4] |
| Choice confidence is a shape statistic. The docs' widget computes (K·p_max−1)/(K−1); Laya's README independently quotes the same formula | CONFIRMED | DOC@2026-10-02 [D9]; SECONDARY [G2] |
| Budget: 64K tokens per request; 32K for state plus the single longest question | CONFIRMED | DOC@2026-10-02 [D2] |
| $0.042 per M input tokens ($42 per B). Output free | CONFIRMED | DOC@2026-10-02 [D1][D2] |
| On pricing durability, the blog says they cannot prove it isn't subsidised and expect it to fall; the FAQ says it is profitable at current prices | CONFIRMED (both say so) | DOC@2026-10-02 [D1][D15] |
| Limits: 100K tokens/s and 40 requests/s. 429 when over, 529 when overloaded. Limits are dynamic and can change without notice; enterprise plans are higher | CONFIRMED | DOC@2026-10-02 [D2][D3] |
| Text only (string, JSON object, or array of text). No image, audio or video | CONFIRMED | DOC@2026-10-02 [D2] |
| English is the primary language and where accuracy is best. Others, including CJK, are handled but less well | CONFIRMED | DOC@2026-10-02 [D2] |
| No per-account fine-tuning or LoRA. Not trained on customer requests or responses. ZDR available for enterprise | CONFIRMED | DOC@2026-10-02 [D2][D16 §4.1] |
| Auth is a Bearer API key (from console.typesafe.ai/keys) in env `TYPESAFE_API_KEY` | CONFIRMED | DOC@2026-10-02 [D3][D10] |
| Official SDKs: Python `typesafe-sdk` 0.7.2 (2026-09-26) and JS/TS `@typesafe-ai/sdk` 0.6.0, both MIT | CONFIRMED | DOC@2026-10-02 [D10] |
| Official C#/.NET SDK | NOT FOUND | DOC@2026-10-02 [D10] lists only Python and JS |
| OpenAPI 3.1.0 spec (title `TypeSafe`, v0.2.0), 2 paths, HTTPBearer. A C# client can be generated or hand-written (INFERENCE) | CONFIRMED | DOC@2026-10-02 [D4] |
| Unofficial .NET clients exist (≥8 repos; NuGet `JevSharp` 0.2.0 from an unverified publisher, with an anomalous 2.5M downloads since mid-September). OllamaSharp has a SystemOne model | SECONDARY | [G15] |
| Vendor latency: 70–500 ms, measured from West-Coast laptops. Docs say most queries take about 100 ms; the use-case map says 150 ms | CONFIRMED (vendor) | DOC@2026-10-02 [D1][D14][D13] |
| Third-party latency: OpenRouter P50 0.16 s (week, all locations). A2 (independent, Madrid) median 0.27–0.31 s, p95 0.35–0.38 s, serial via OpenRouter. A1 (Univ. Bonn) mean 0.36 s at 32 concurrent. JevBench ≈0.62 s serial (client location not stated) | SECONDARY | [O2][A2][A1][B1] |
| Determinism is not promised. The FAQ separates determinism from the consistency it targets, and a cookbook saw repeat noise on 2 of 13 questions | CONFIRMED | DOC@2026-10-02 [D15][D11] |
| Hosted in the US. EU personal-data transfers run under EU SCCs (Modules 2/3) with Ireland as competent authority. MCA restricts only US-embargoed locations | CONFIRMED | DOC@2026-10-02 [D19][D18][D16 §16.12] |
| An EU-hosted region | NOT FOUND | — |
| Direct access is early access off a waitlist. OpenRouter offers `typesafe/jev-1.13` with no waitlist, a 32K context, a TypeSafe-compatible `/api/v1/systemone`, and `usage.cost` in responses | CONFIRMED | DOC@2026-10-02 [D1][D15][O1][O2] |
| New architecture, parallel sampler and RLCD are claimed; internals undisclosed; FAQ says it is neither a small model nor an LLM | CONFIRMED (as claims) | DOC@2026-10-02 [D1] |
| Vendor says intelligence is similar to existing LLMs, speed 40–200× and cost 444.6× better | CONTRADICTED in part. On JevBench, GPT-6 Luna scores Intelligence 96.2 vs 72.0 and Calibration 95.6 vs 88.0, at only ~3.5× the cost and ~2.5× the latency | DOC@2026-10-02 [D1] vs SECONDARY [B1] |
| The zero-hallucination claim is a schema guarantee, explicitly not an empirical rate | CONFIRMED | DOC@2026-10-02 [D1][D15] |
| Jaggedness page (reviewed 2026-09-17) lists 9 failure modes. Its date pitfalls name settlement windows and accrual periods | CONFIRMED | DOC@2026-10-02 [D8] |

## 2. Legal (Task 2): MCA of 2026-09-23, AUP, DPA, OpenRouter terms
Clause paraphrases, all DOC@2026-10-02 [D16] unless marked:
- **Licence (§2.1–2.2).** Limited, non-exclusive, non-transferable. Covers using the API per the Documentation and building it into the Customer's own applications that serve End Users. §2.3(a) bars
  offering the Services as a standalone service.
- **§2.3(b) Distillation, imitation, competition.** No using the Services *or any Output* to perform model distillation, to "train a model to imitate the output of the Services", or to develop, or
  help develop, a similar or competing product.
- **§2.3(c) Reverse engineering.** No reverse engineering or attempting to derive source, underlying data, or the underlying ideas, algorithms, structure or organisation. §2.3(g) and AUP §3.7 [D17]
  also bar security or vulnerability testing of the Services.
- **Data (§4.1–4.3).**
  - TypeSafe processes Input and Output to run the service, and keeps a perpetual right to use Customer Data for Telemetry, abuse monitoring and legal compliance.
  - It will not put Customer Data in a weight-training dataset without consent.
  - It disclaims ownership of Output and assigns any rights it has to the Customer.
  - Telemetry (logs, hashes, statistics, *classifications*, *learnings*) may be processed without restriction.
- **Credentials and Output (§2.4, §9.3).** Keys must stay confidential and unshared, and the Customer is liable for everything done with them. Output may be wrong; the Customer must evaluate it.
- **Survival and liability (§10.4, §12.2–12.3).** §2.3 survives termination. The cap is the greater of 12 months' fees or $50, but a §2.3 breach is an Excluded Claim, so it is uncapped.
- **Other terms.** TypeSafe's Confidential Information includes the Documentation and pricing (§14.1). Disputes go to JAMS arbitration in San Francisco under California law, with no class actions
  (§15–16.2). The terms can be amended on notice, effective at least 60 days later (§16.7).
- **AUP** [D17]: no trading prohibition. It bars manipulation or deception, IP or privacy infringement, illegal conduct, and anything TypeSafe's counsel deems a liability. TypeSafe may change it by
  posting.
- **OpenRouter** [O3]: users must comply with each provider's Model Terms, and red-teaming needs written permission. The Model Terms that TypeSafe applies to OpenRouter users are UNKNOWN; assume they
  match the MCA.

**Assessment for our three uses** (INFERENCE from the clauses above):
- **(a) Jev probabilities as features in our own model that predicts PRICE outcomes: looks clearly permitted.** The target is the market, not Jev's output, so this is neither distillation nor
  imitation. TypeSafe itself prescribes feeding Jev probabilities into a downstream classical model (its AutoResearch cookbook trains CatBoost on Jev answers; the use-case map feeds them into
  forecasting models). DOC@2026-10-02 [D2][D12][D13]. The condition: never train a text-to-Jev-probability surrogate to skip the call. That is imitation.
- **(b) Storing Jev outputs permanently: looks permitted for our own audit, replay and research**, given §4.2 and the absence of any deletion duty on Output. Two limits apply. First, because §2.3
  survives, the archive can never later feed distillation or imitation, and publishing or handing it out is owner's judgment (it may facilitate others). Second, the *inputs* we store (news text,
  social posts) carry their own copyright, platform-terms and GDPR duties: keep ids, hashes and minimal excerpts, and pseudonymise authors.
- **(c) Training our own decision model to imitate Jev: clearly prohibited.** This includes using Jev answers as soft labels or teacher for a local fallback, or fine-tuning Laya or decider on Jev
  outputs. A breach is uncapped and can trigger suspension (§6).
- **Owner's legal judgment needed:**
  1. Using agreement-with-Jev to choose or tune local models. Arguably this facilitates a similar product; safer to evaluate every adapter only against our realised outcomes or human labels.
  2. TradeAgent is sold as a desktop app. An embedded owner key would be credential sharing (§2.4). Options are that each user pastes their own TypeSafe or OpenRouter key in the app's window, or the
     owner runs a server-side proxy as the *Customer Application*. Confirm with TypeSafe.
  3. §4.3 Telemetry vs the confidentiality of our schemas, which encode strategy ideas. Mitigate with ZDR (enterprise) or by keeping sensitive schemas on the local runtime.
  4. Do not copy TypeSafe docs into our repository (§14.1); link to them.
  5. California arbitration for an EU-based owner.

## 3. Independent evidence (Task 3)
- **A1 exists, numbers match.** SECONDARY [A1] (posted 2026-09-29; code at github.com/AppliedMachineLearning-Lab/jev-benchmarking).
  - Scope and results: 37 datasets, 346,009 requests, under $10 (217.9M tokens, $9.15). Beats Qwen3.8-27B on 27/37 (none of Qwen's 9 leads outside the bootstrap intervals) and Gemma-4-E4B on 37/37.
    95–99% on IMDB, SST-2, HellaSwag and ARC; 86.7% on Belebele (122 languages).
  - Checks: tuned thresholds lift UNFAIR-ToS micro-F1 from 0.50 to 0.75. Option rotation changes nothing; withholding the question drops accuracy to near chance.
  - A1's own caveats, which the owner's paper omits: one frozen template per dataset; single run with no repeat-variance measurement; memorised question–answer pairs not ruled out; all models degrade on low-resource
    languages, noisy labels and rubric scoring.
  - **Financial PhraseBank: 0.730 [0.699, 0.756], ECE 0.138**, among the worst-calibrated of the 37 sets (IMDB 0.019). Prompt-injection detection: recall 50% at the 0.5 threshold despite AUROC 0.982.
- **A2 exists, table matches.** SECONDARY [A2]. PubMedQA 78.4 vs 78.2; MetaMedQA 74.8 vs 82.7; DiagnosisArena 59.8 vs 82.4; NEJM 61.8 vs 82.4 (GPT-6 Sol, medium). Also ECE 0.063 vs 0.146 (MetaMedQA),
  AUROC 0.645 vs 0.768 (DiagnosisArena), and it chose the abstain option on only 10.5% of the 162 items where abstaining was the correct answer.
- **A3 exists.** SECONDARY [A3]. Sys1Cal-v1 uses known-probability true/false items. Recovering a suppressed undecided mass lifts median Choice soft accuracy from 0.771 to 0.978. Single author,
  early work. It notes the vendor's calibration claim had no public test.
- **JevBench v1.5.4 confirmed.** SECONDARY [B1].
  - Method: 1,624 decisions per system (904 open, 720 sealed; sealed is half of Intelligence); 106 of 112 ranked. Intelligence is chance-corrected and tier-weighted 10/20/30/40. Calibration uses ECE,
    TVD, Brier and RPS. The headline is an equal-weight harmonic mean of Intelligence, Calibration, Speed and Cost. Self-hosted latency is adjusted ×2 + 0.15 s.
  - Composite: Cygnet 73.7 and Winnow-12B 73.2 (joint leaders), Jev 72.1, JevK5 v0.3 71.9, Plumb-4B 71.6, Jev-Omni 71.5, decider-4b v2 71.3. 69 of 77 adjacent pairs are statistical ties. On
    Capability, Jev leads at 80.0 (I 72.0, C 88.0).
  - Topic radar, Jev vs Cygnet: Math & numbers **40.5** vs 31.4; Finance & commerce (payments and invoices, not markets) 83.4 vs 78.5.
- **Further independent work** (my arXiv API query, Jev AND TypeSafe/System One, returned 22 papers dated 09-21 to 10-01; [A9] reviews 28 from 09-19 to 09-24). SECONDARY:
  - [A4] With the correct number among the options Jev scores 99%. With it absent and an explicit reject option, it rejects only 7% (79% with a tuned threshold).
  - [A5] Negation pairs miss summing to 1 by 0.064, and single-label probabilities sum to 1.14. [A6] Hierarchical reconstruction costs 22.9 points on CLINC150.
  - [A7] Good aggregate calibration hid high-confidence misses in specific attack groups.
  - [A8] 499,500 crash narratives coded with a 27-question schema reached F1 0.908 against humans; recalibration cut calibration error 3.3×. [A9] Early work shows gains in latency and cost, not
    typed-readout accuracy.
- **What the supplied paper overstates or omits.** Its specific figures checked out; the problems are emphasis:
  1. Calibration is framed as Jev's edge. JevBench shows a frontier LLM better calibrated (95.6 vs 88.0), and finance is a weak calibration set [A1][B1].
  2. Missing: no determinism guarantee, the waitlist, the OpenRouter route with 32K context, and A1's caveats.
  3. Laya is presented as a CPU/edge answer, but independently it scores Intelligence ≈0 (chance level) on JevBench, has a 512-token English context, and fails beyond ~20 options [B1][G2]. CLM's
     strength is self-reported (JevBench Intelligence 11.4).
  4. JevK5 is distilled partly from GPT-6 Luna, a proprietary-API teacher [G4]. Vendor speed and cost multiples are repeated without the JevBench counterweight.
  5. The legal note omits uncapped liability, key confidentiality and Telemetry. Minor: the 255 and 2–10 limits are attributed to the Models page, and the SemIf repo has since been renamed.

## 4. Open ecosystem (Task 4)
Every repository exists, all created 2026-09-16 to 09-23 (ten days old at most). Licences were checked on GitHub and on the Hugging Face weight cards. *Bespoke* = Ollaya's run of Bespoke Labs' public
benchmark (3,880 questions, SECONDARY [G1]). *CPU* = Ollaya's p50 for a 5-question request on a 24-core i9-13900K / Threadripper 3960X desktop CPU, SECONDARY [G1]. *JevBench I / C* = SECONDARY [B1]. Licence, provenance and size columns = DOC@2026-10-02 [Gn] (each project's own repo or model card); provenance statements are self-declared.

| Project | Code / weights licence | Provenance vs Jev | Size, runtime | Bespoke acc. (ECE) | CPU p50 | JevBench I / C |
|---|---|---|---|---|---|---|
| **Ollaya** runtime [G1] | Apache-2.0 | n/a | Rust. v0.8.0 ships `ollaya-windows-amd64.zip` + sha256. CPU via ONNX/llama.cpp. `/v1/systemone`, `/v1/decisions` and `/v1/models` are wire-identical; the official SDK works via `TYPESAFE_BASE_URL` | — | — | — |
| **decider** 0.8B / 2B / 4B [G3] | Apache-2.0 / Apache-2.0 (Qwen3.5 base) | states no Jev distillation; 27B Qwen teacher + ~95 public sets. English only. 2–255 options | Recommends GGUF/llama.cpp for CPU or laptop use | 0.669 (0.061) / 0.703 (0.087) / 0.756 (0.043) | 1.8–3.0 s / 3.4–4.4 s / 8.8–10.4 s | 0.8B —; 2B 42.3 / 71.5; 4B v2 55.8 / 85.6 |
| **Laya** en 421M / multi 322M [G2] | Apache-2.0 / Apache-2.0 | none stated | Encoder. 512-token (en) / 1,024 context. ≤100 options in its server. Windows install documented | 0.532 (453 rejected) / 0.579 | 0.62–0.92 s / 0.31–0.47 s | en 0.0 / 73.7; multi 2.4 / 43.6 |
| **Winnow** E4B / 12B [G10] | n/a / Apache-2.0 (Gemma 4 is Apache-2.0) | private data incl. teacher distributions, teacher unnamed | Q8 GGUF is 8.0 GB for E4B. Chat and vision retained | 0.734 (0.071) / 0.773 (0.141) | 4.0–6.1 s / 10.9–14.8 s | E4B —; 12B 74.4 / 84.1 |
| **Kev** 0.8B–27B [G6] | Apache-2.0 / Apache-2.0 | states no Jev outputs used; public sets + generated | LoRA + pointer head. README lists CUDA and MLX; CPU via Ollaya | 0.8B 0.611 (0.035); 4B 0.745 | 0.8B 1.3–2.8 s; 4B 6.1–8.3 s | kev 4B (Qwen3 base) 39.9 / 67.6 |
| **JevK5** 4B [G4] | Apache-2.0 / Apache-2.0 | distilled from Qwen3.6-27B **and GPT-6 Luna** | ≤16 options per pass, ≤16,384 tokens. English | 0.621 (700 rejected) | 7.1–9.5 s | 56.3 / 88.3 |
| **Nimble** 9B v2 [G5] | **no LICENSE file** / Apache-2.0 on HF | says it did not distill from Jev, though its repo holds saved Jev probabilities | LoRA on Qwen3.5-9B | 0.748 (0.022) | 51–60 s | 63.7 / 77.2 |
| **CLM** 8B [G7] | Apache-2.0 / Apache-2.0 | 60M Nemotron Q&A, 30M hard negatives, 1M trajectories | Qwen3-8B encoder | — | 4.0–5.0 s | 11.4 / 48.5 |
| SemIf [G8] · AnyJev [G9] | MIT · Apache-2.0 | frozen-LLM readout; no training | SemIf: 5.21× vs compact JSON (21 decisions). AnyJev: order flips 0.230→0.073 with no labels (both self-reported) | — | — | SemIf 51.3 / 84.0 |
| **Ollama 0.35.0** [G11] | MIT runtime | serves Nimble and Together's Tev1 on `/v1/systemone` | Mature Windows runtime | tev1:0.8b 0.639 | — | — |

Notes:
- Tev1's weight licence is not yet finalised per its card, so UNKNOWN for commercial use [G12].
- Calibration depends on the runtime: the same Nimble weights scored ECE 0.022 under Ollaya vs 0.122 under Ollama, because Ollama does not apply the fitted temperature [G1]. djev and system-one-open
  (licence NOASSERTION) need a GPU or a hosted endpoint.

**Most realistic local fallback on an ordinary no-GPU Windows laptop:** Ollaya running `decider:0.8b`, with `decider:2b` for batch quality. Optionally put `laya` in front as an English-only,
short-text pre-filter. INFERENCE from the table:
- Why decider: Apache-2.0 end to end, a clean stated provenance, Jev's own 255/10 limits, an explicit laptop path, and the best accuracy-per-CPU-second among the clean-licence models.
- Install without a terminal: TradeAgent fetches the zip into `%LOCALAPPDATA%\TradeAgent\tools`, verifies its sha256, and starts it with `CreateNoWindow`. No elevation. Ollaya's own `irm … | iex`
  route would break the no-terminal rule.
- Winnow-E4B is better (0.734) but needs about 8 GB of RAM and 4–6 s per request on a fast desktop. Treat it as an optional tier for 16 GB+ laptops and overnight batches.
- Ceiling: a laptop CPU is plausibly 2–3× slower than these desktop figures, so ~4–9 s per 5-question decision and low thousands of items a day. decider-2b's JevBench Intelligence is 42.3 against
  Jev's 72.0. Ollaya is ten days old with one maintainer, so pin a release and hash it.

## 5. Finance applicability (Task 5)
**Evidence.**
- Financial PhraseBank: Jev 0.730 with ECE 0.138, ahead of Qwen3.8-27B (0.677) and Gemma-4-E4B (0.652) [SECONDARY A1].
- `jev-alpha-bench`, unreviewed, run on jev-1.13.0 on 2026-09-16 [SECONDARY G13]:
  - Data: 5,000 Nasdaq-100 headlines (2022-01 to 2023-10), returns in excess of QQQ, date-level bootstrap.
  - Publication day: the tilt P(bull)−P(bear) has rank IC +0.24, about half of it from headlines narrating a move.
  - Tradeable horizon (next close): IC −0.008 [−0.038, +0.021]; long-short makes +1.6 bps gross and −18.4 bps after 20 bps costs.
  - Named, anonymised and wrong-company headlines score alike (reading, not memory). Candles only: no signal.
- FiQA, earnings-call and crypto-news evaluations of Jev or Jev-like models: **UNKNOWN**. Eight arXiv queries pairing `Jev` with financial, stock, crypto, trading, earnings, news, sentiment and market
  returned nothing. GitHub has only hobby bots. One retrospective catalogue [G14] notes that querying today's `jev-latest` on 2023–26 data is not point-in-time. Jev's training cutoff is **UNKNOWN**
  (TypeSafe says only that it makes its own data [D1]).

**Pitfalls**, each tied to its source:
- **Numbers.** Keep arithmetic in code [D8]. Math & numbers competence is 40.5 [B1]. With the correct number absent, Jev rejects only 7% of the time [A4]. Never ask it whether a reported figure beat
  consensus; compute that in code.
- **Dates.** Settlement windows, accrual periods and before/after comparisons belong in code [D8].
- **Adversarial text.** State can steer answers [D8]. Injection recall is 50% at 0.5 [A1], and confident misses cluster in attack groups [A7]. Pump posts are adversarial by design.
- **Language.** English is the primary language [D2], and low-resource languages degrade [A1]. That matters for CJK, Korean and Russian crypto channels.
- **Calibration drift.** Aliases move and limits change without notice [D2]; runtime temperature alters ECE [G1]. Recalibration was needed even in the success case [A8].
- **Coherence and repeatability.** Negations and decompositions do not add up [A5][A6], so enforce identities in code. Expect repeat noise [D11][D15].
- **Look-ahead.** Retro-scoring old news with today's model leaks knowledge the market did not have [G13][G14].

**Where a System-One model can create edge.** INFERENCE throughout; every claim here still needs our own forward-recorded tests.
1. **High-volume news and event triage.** About 50k items a day costs about $3 at list price (§6.6). The alpha-bench result says the reading is priced *during* publication, so value comes from
   minutes-level speed in under-covered venues (small-cap crypto, listings, exploits), from routing the 1–5% that matter to a stronger model, and from structured event labels that let *code* test
   known event drifts. Not from daily sentiment on mega-caps.
2. **Calibrated probabilities into deterministic sizing.** Only after we map P(label) to realised outcome on our own data (§6.5). Vendor calibration is about label correctness, not P(price move).
   Sizing stays in code, capped.
3. **Semantic dedup of hypotheses.** Cheap protection of trial counting: a re-parametrised dead idea must count as another trial of the same family.
4. **Agent-trace auditing.** Mirror TypeSafe's trace workflow, which checks irreversible-action permission separately and pages on breach [D20]. The audit flags and routes; it never grants authority.
5. **Model routing.** Choose the model and effort per agent turn to save the owner's real AI allowance (OpenRouter offers a Jev Router that picks model and reasoning effort per request [O1][O2]). Measure that it saves money before trusting
   it.

## 6. DESIGN for TradeAgent
**6.1 Port.** `IDecisionModel` lives in Core, mirroring the `IAgentRuntime` style: one interface, vendor quirks behind it, endpoints as data.
- Methods: `DecideAsync(DecisionRequest, ct)` and `ListModelsAsync()`. The request carries `SchemaRef` (id, version, hash), canonical-JSON `State`, `PinnedModel` and `Budget`.
- Endpoints, model ids, prices and limits live in a new `decision-models.json`, like `runtimes.json`, so a vendor change is a one-line data fix.
- Adapters:
  1. **TypeSafeWire** for TypeSafe direct, OpenRouter `/api/v1/systemone`, or any compatible host. A thin client hand-written against `openapi.json` v0.2.0 with golden tests. No unverified NuGet
     package on the money path.
  2. **LocalSystemOne** for Ollaya or Ollama on `127.0.0.1`: the same client with a different base URL and its own pinned model and runtime version.
  3. **GenericLlmDecision** fallback: a frontier LLM in JSON-schema mode returning a distribution. Its probabilities are a *different instrument* with their own calibration, never mixed with Jev's.
- The adapter enforces ≤255 options, 2–10 levels and token budgets *before* sending. Unknown is never zero.
- Keys are pasted in the app's own window and stored by the existing Security project. The agent-facing pipe can *ask* a registered schema but cannot register schemas, change pins or touch keys; that
  is operator authority, in-process.

**6.2 Version pinning.**
- Send the versioned id (`jev-1.13.0`), never `jev-latest`.
- An instrument's identity is `(adapter, endpoint host, answered model id, runtime version, weights sha256 for local, schema hash)`.
- If the response's `model` differs from the pin, record it, mark the decision `UNPINNED`, and exclude it from evidence. A new version is a new instrument and runs in shadow until its calibration is
  refit (TypeSafe gives the same advice [D2]).

**6.3 Every decision is a recorded measurement** (Evidence zone, written by the app only, never editable by agents, kept separate from agent notes). Fields:
- Identity and timing: `decision_id`; `schema_id/version/hash`; `state_hash` (SHA-256 of canonical JSON); `state_ref` (source ids and timestamps); `source_published_at`, `ingested_at`, `requested_at`,
  `answered_at` (UTC plus monotonic). `latency_ms` is measured client-side, since the API returns no server timing [D4].
- Instrument: `adapter`, `endpoint_host`, `model_requested`, `model_answered`, `runtime_version`, `weights_sha256`.
- Answers: `answers_raw` (every option probability, confidence and Score legend, unrounded), plus `answers_calibrated` with `calibration_map_id`, stored separately.
- Cost and status: `input_tokens`, `output_tokens`; `cost_usd_estimate` from the dated price table, kept distinct from `cost_usd_billed` when the host reports it (OpenRouter `usage.cost` [O1]);
  `http_status`, `retries`, `error_class`; `UNPINNED` / `UNVALIDATED` flags.

**6.4 Replay, causality, determinism.**
- Backtests and paper replays never call a model. They read decision records where `answered_at` ≤ the simulated time (plus the measured latency), matching on schema hash and instrument. A missing
  record means no decision, not a live call.
- Retro-scored records of old items are labelled `RETRO_SCORED` and quarantined from evidence-grade results because of model-knowledge look-ahead (cutoff UNKNOWN). They are allowed only in labelled
  exploratory runs.
- This makes backtests deterministic even though the API does not promise determinism [D15][D11], and keeps a strategy's evidence tied to the exact instrument that produced it.

**6.5 Calibration against our outcomes.**
- Each schema declares its outcome function in code: e.g. realised excess return sign over horizon H after `answered_at`, confirmation by a tier-1 source within 24 h, or a human or arbiter
  adjudication.
- Fit isotonic or Platt maps per instrument on a rolling window with a frozen holdout. Track ECE, Brier, log loss, reliability curves, and coverage at a target error rate.
- Alarm on drift and on per-subgroup failures (source, language, asset class), since aggregate calibration can hide them [A7]. Never fit one instrument to another instrument's outputs; that is the §2
  imitation trap.
- Downstream, a small gradient-boosted or logistic model maps calibrated features plus code-computed facts to expected return, retrained only on forward-recorded data. This is the pattern TypeSafe
  documents [D12].

**6.6 Cost per 1,000 items** at $0.042 per M input tokens, output free. Arithmetic: cost = tokens per item × $0.000042.

| tokens per item (state + all questions) | 500 | 1,000 | 2,000 | 4,000 | 8,000 | 32,000 |
|---|---|---|---|---|---|---|
| $ per 1,000 items | 0.021 | 0.042 | 0.084 | 0.168 | 0.336 | 1.344 |

- Reference points: A1 averaged 630 tokens per request with all questions [A1]; JevBench prices Jev at $0.032 per 1,000 decisions [B1].
- Example day: 20k news items at 2,000 tokens plus 30k posts at 1,000 tokens is 70M tokens, about **$2.94/day** at list price, or 0.6 req/s against a 40 req/s limit. Batch every question for one state
  into one call; repeating the state is the waste [D11].

**6.7 Question schemas.** Escape hatches are always included. Criteria are concrete and positively worded, and each question asks one thing [D8].
- **(a) News item.**
  - State: headline; body excerpt (≤1,500 tokens, filtered in code); `source_tier` (code); `candidate_assets` (code entity match, ≤20, with names and aliases); `computed_facts` (code: e.g. EPS vs
    consensus surprise %, return since publication); the 3 latest prior headlines on the same assets (code, after exact and near-duplicate hashing).
  - `primary_asset`: Choice over candidates, plus `market_wide` and `none_of_these`. `about_asset_i`: one Noul per candidate on whether the item carries information specific to that asset (a Choice is
    relative and a Noul absolute [D8], so ask both).
  - `event_type`: Choice over `earnings_or_guidance`, `regulatory_or_legal_action`, `hack_exploit_or_outage`, `listing_or_delisting`, `token_supply_change`, `partnership_or_product`, `macro_policy`,
    `leadership_or_governance`, `opinion_or_commentary`, `narrates_a_price_move`, `other`, `insufficient_information`.
  - `direction_for_holders`: Choice over `clearly_positive`, `clearly_negative`, `mixed`, `no_fundamental_content`, `cannot_tell`. `new_vs_prior`: Noul on whether it reports a development not in the
    prior headlines.
  - Scores with 4 levels each: `qualitative_surprise` (from "scheduled/expected and in line" to "contradicts prior expectations"; non-numeric events only), `sourcing` (from "anonymous rumour" to
    "primary filing or official statement quoted"), `time_sensitivity` (background, days, hours, minutes).
  - `addresses_automated_reader`: Noul on whether the text tells a reader or automated system to act. If triggered, quarantine.
  - CODE computes: timestamps and age, entity candidates, numeric surprise, duplicates, market hours, price moved since publication, the final signal (calibrated model), sizing and limits.
- **(b) Social post.**
  - State: post text; mentioned assets (code-extracted cashtags and contracts); pseudonymised author; `author_stats` (code: account age, posts in 24 h, share of posts on the same asset, prior flags);
    `cluster_stats` (code: near-duplicates in the last hour, distinct authors, 3 sample posts); liquidity and market-cap buckets (code).
  - `post_kind`: Choice over `original_analysis_or_news`, `reshare_of_news`, `promotional_call_to_buy`, `giveaway_or_airdrop_bait`, `asks_for_funds_wallet_or_dm`, `template_or_bot_like`,
    `joke_or_meme`, `unrelated`, `other`.
  - Nouls: `urgency_hype` (urging a quick buy, promising gains, countdowns), `same_template_as_samples`, `checkable_claim` (a specific listing, partnership or hack claim that code then verifies),
    `addresses_automated_reader`. Score: `claim_sourcing` with 4 levels.
  - CODE computes: burst and z-score statistics, network overlap, volume spikes, a pump-risk score in the calibrated model, and the action. The action is only to reduce or blacklist exposure; we never
    participate (AUP and law).
- **(c) Agent-trace audit.**
  - State: trusted mission and granted capabilities (app); budget facts (code: spend, reservation, ceiling, turns); the trace with secrets redacted; claims extracted from the final report as a
    candidate list; an evidence index (app-computed ledger values).
  - `boundary_attempt`: Choice over `none`, `change_supervisor_or_config`, `reach_credentials`, `route_real_orders`, `read_protected_holdout`, `escalate_mode_or_limits`, `obey_inbox_material`,
    `other_suspicious`, `cannot_tell`.
  - `claim_supported_i`: one Noul per claim on whether a value in the evidence index supports it. Numeric claims are first checked by code against the ledger.
  - `progress` (Noul: new artifact, measurement or finding). `wasted_spend` (Score, 4 levels: share of steps that contributed nothing). `outcome`: Choice over `healthy`, `expectation_gap`,
    `overt_failure`, `silent_failure` [D20]. `pleads_with_reviewer` (Noul).
  - CODE: real enforcement already happened in code. This audit only routes (close, queue, page the owner), never changes authority, and treats trace text as untrusted state.
- **(d) Hypothesis vs killed hypotheses.**
  - State: the new hypothesis, structured (mechanism, universe, inputs, horizon, entry and exit); K ≤ 20 killed hypotheses retrieved by code (embedding plus parameter diff), each with its kill reason
    and measured stats.
  - `same_mechanism_as`: Choice over candidate ids plus `none_is_the_same_idea`. `reparametrisation_of_i`: one Noul per candidate on whether only thresholds, lookbacks or same-class assets differ.
    `addresses_kill_reason`: Noul against the nearest candidate's kill reason.
  - `mechanism_class`: Choice over `trend`, `mean_reversion`, `carry_or_funding`, `event_reaction`, `microstructure`, `relative_value`, `calendar`, `flow_or_sentiment`, `other`.
  - CODE: retrieval, the exact-parameter diff, and the trial ledger. A near-duplicate increments its family's trial count; renaming never resets it.
- **(e) Data-source reliability.**
  - State: source type (code); 5 recent items with timestamps; the about-page and ownership text; `track_record` (code: items in 90 days, share later confirmed, median lead time vs authoritative
    sources, retractions, duplicate rate).
  - `source_kind`: Choice over `primary_official`, `established_newsroom`, `aggregator_or_rewrite`, `independent_analyst`, `anonymous_social`, `paid_or_promotional`, `other`.
  - Nouls: `discloses_sponsorship`, `attributes_claims_to_named_sources`. Scores with 4 levels: `original_reporting`, `sensationalism`.
  - CODE: reliability is empirical (confirmation rate, lead time, retractions). Jev supplies only a cold-start prior whose weight decays as the measured track record grows.

## 7. Still open
- Jev's training cutoff. TypeSafe's Model Terms for OpenRouter users. Any EU-hosted endpoint. Jev on FiQA, earnings calls or crypto news.
- Real latency from the owner's Windows laptop to TypeSafe and to OpenRouter, and Ollaya/decider CPU latency on that laptop (the figures above are from 24-core desktops). Measure both; don't assume.
- A written answer from TypeSafe on per-user keys inside a distributed desktop app, and on evaluating local models against Jev.
