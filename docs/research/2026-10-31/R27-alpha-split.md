# R27 — Can α-wealth be split down the mandate tree and keep the global guarantee? (2026-10-31)

**What it is.** The check VISION § 14 owes before the economy is built ("Does splitting α-wealth across units keep the global guarantee? → a check
against R04's procedures"), named `R-alpha-split` in R26 § 3.3 (`docs/research/2026-10-31/R26-roadmap.md`:139–141), which gates `U-alpha-tree`.
Sources: VISION §§ 7.2, 14; R04 §§ 2.6, 4 (`docs/research/2026-10-02/R04-evolution-statistics.md`:119–129, 255, 261–266); the campaigns' α code on
main at `3ab7a586`, read and not run: `src/TradeAgent.Core/Strategy/HoldoutStatistics.cs`:116–246 (`AlphaWealth`),
`src/TradeAgent.Core/Db/RefereeHoldout.cs`:72–75, 111–138, `src/TradeAgent.Core/Db/CampaignStore.cs`:619–623, 1098–1175, 1290–1343,
`src/TradeAgent.Core/Db/CampaignRenewal.cs`:56–58, `src/TradeAgent.Core/Db/OrgStore.Tilt.cs`:62–73, `src/TradeAgent.Core/Data/EvidenceDefaults.cs`:25;
the U-evidence-campaign and U-evidence-breadth landing entries in `BUILD-STATUS.md`.

**Labels.** CODE = read at the cited lines. LIT = the literature as this author knows it, **not re-read on 2026-10-31** (no source was fetched; a
theorem's exact hypotheses are flagged where it matters). INFERENCE = this file's derivation; the arithmetic is reproducible from the constants in
`AlphaWealth`. DESIGN = a proposal. UNKNOWN = not established. This file grants no authority, marks nothing built and changes no constant.

## 0. Verdict

1. **Today's guarantee is per family, not global (CODE + INFERENCE).** The α order is ADDIS at α = 0.25, sealed and gating nothing, run once per
   *(pair × held-back months)* family: other pairs never enter it (U-evidence-breadth) and a successor campaign over fresh months is a new lineage with
   a fresh W0. Five pairs are five families; every forward window adds another. Their union carries no FDR ≤ 0.25 statement.
2. **A split keeps a global FDR bound under one elementary rule: divide the *level*, never create it.** If each sub-stream u runs a valid procedure at
   level s_u·α with Σ s_u ≤ 1 fixed in advance, the union's FDR ≤ α under *any* dependence between sub-streams (§ 2.1). Earnings stay in the stream
   that earned them. Outcome-driven transfers between units keep mFDR (alpha-investing's guarantee) but not, as far as is proven, FDR (§ 2.3).
3. **The smallest U-alpha-tree splits the right to test, not the α.** One ADDIS stream per declared root; the tree's existing verdict-count shares
   decide *who* draws the next α_t; a discovery earns its unit counts, never α. That is a test-selection policy, which the online-FDR guarantee is
   indifferent to, so it needs no new theorem (§ 4).
4. **Unproven, and not fixed by any split:** ADDIS's own FDR proof assumes independent, uniformly conservative null p-values; holdout p-values share
   one market path within a pair and the same months across pairs, and p_h is an asymptotic PSR p-value. R04 :261–266 already says so.

## 1. What the current accounting guarantees

- **The rule (CODE `HoldoutStatistics.cs`:118–136, 143–159).** ADDIS (Tian–Ramdas 2019) with α = 0.25, W0 = α/2, λ = 0.25, τ = 0.5,
  γ_j = j^−1.6/ζ(1.6); α_t = min(λ, (τ − λ)·[W0·γ(·) + (α − W0)·γ(·) at the first rejection + α·Σ γ(·) at later ones]). A rejection needs p_h ≤ α_t
  over a holdout of ≥ 365 known days; otherwise the verdict is `inconclusive` and spends α_t anyway. Wealth = W0 + payouts − Σ α_i/(τ − λ) over
  spending verdicts; it never goes below zero because each spend is W0·γ at a fresh index.
- **The p-value (CODE `RefereeHoldout.cs`:111).** p_h = 1 − PSR(SR_h; 0): the probabilistic Sharpe ratio's normal approximation with the holdout's
  own skew and kurtosis. Super-uniform under the null only asymptotically (INFERENCE).
- **The family (CODE `CampaignStore.cs`:1290–1298, 1332–1343, 619–623).** The history is every judgement whose campaign is in this campaign's renewal
  lineage, or of the same pair with an overlapping read span, in charge (rowid) order; an unknown pair counts as every pair's (only lowers α: more
  spends). A successor opened on fresh months is a new lineage (`CampaignRenewal.cs`:56–58), so its stream starts again at W0.
- **What ADDIS proves (LIT).** FDR ≤ α at every time when null p-values are independent of each other and of the non-nulls, uniformly conservative,
  and α_t is a monotone function of the past (non-decreasing in past rejection and candidate indicators). The mFDR version needs only conditional
  super-uniformity. Same structure as SAFFRON (Ramdas–Zrnic–Wainwright–Jordan 2018) and GAI++/LORD++ (Ramdas–Yang–Wainwright–Jordan 2017), which
  descend from Javanmard–Montanari 2018's generalised alpha-investing. The code's α_t is ADDIS's own monotone rule: the monotonicity condition holds.
- **What it delivers today (INFERENCE).** The default holdout is 3 months (`EvidenceDefaults.cs`:25) and a successor's is ≥ 30 days, both under 365:
  **every verdict is `inconclusive`, no rejection is possible, so FDP = 0 trivially and wealth only drains.** "A real discovery earns it back"
  (VISION § 7.2) is inert until a ≥ 12-month holdout exists. The α order is a ration and a record, not yet a test.
- **Scope of the statement.** Per family: FDR ≤ 0.25 *if* the ADDIS hypotheses held. Over five pairs on the same months: the union bound gives only
  ≤ 5 × 0.25; under the global null with independent pairs, P(any false discovery) = 1 − 0.75⁵ ≈ 0.76. Over time: one more family per window.

## 2. Does a split keep it?

**2.1 Static split of the level — yes, under any cross-unit dependence (INFERENCE, elementary).** Partition the tests into sub-streams u, each a valid
online procedure at level α_u with FDR_u ≤ α_u. With V = Σ V_u and R = Σ R_u, each V_u/(R ∨ 1) ≤ V_u/(R_u ∨ 1), because R ≥ R_u and V_u = 0 when
R_u = 0. So FDR = Σ E[V_u/(R ∨ 1)] ≤ Σ FDR_u ≤ Σ α_u. With α_u = s_u·α fixed before the data and Σ s_u ≤ 1, the union has FDR ≤ α. Nothing is
assumed about how sub-streams depend on each other; each sub-stream must still be valid on its own (§ 3, item 5). Recursion is free: a unit may
divide its s_u among its children by the same rule. For ADDIS, a sub-stream at level s_u·α has W0_u = s_u·W0 and payouts s_u·(α − W0), s_u·α.
**Its price:** α_1 scales with s_u. α_1 = 0.0137 at s = 1 (needs an annual holdout SR ≈ 2.21 over 365 days, 4.42 over 91 — R04 :261's figures);
0.0027 at s = 1/5 (SR ≈ 2.78 / 5.56); 0.0007 at s = 1/20 (SR ≈ 3.20 / 6.41). One-sided normal z, SR_ann = z·√(365/T).

**2.2 Static split of the wealth with alpha-investing's mFDR — yes, by linearity (LIT + INFERENCE).** Foster–Stine 2008 control
mFDR_η = E[V]/(E[R] + η) ≤ α with W(0) = αη, i.e. E[V] ≤ α·E[R] + W(0). Summing units with Σ W_u(0) = W(0) gives the same global inequality, by
linearity of expectation alone — no common filtration needed.

**2.3 Dynamic routing (earnings moved between units, board re-splits) — mFDR yes, FDR unproven.**
- *mFDR (LIT + INFERENCE):* alpha-investing's proof is a submartingale on the global wealth (αR − V + W(0) − W). Moving wealth between units
  without creating any is invisible to it, provided each null p-value is conditionally super-uniform given the **whole organisation's** past and
  every α_j is fixed before its p-value is seen. This is the sense in which VISION § 7.2's "the hierarchy is its test-selection policy" is right.
- *FDR (INFERENCE, UNKNOWN):* the GAI++/SAFFRON/ADDIS proofs need every α_t to be non-decreasing in every past rejection. A transfer triggered by an
  outcome breaks that for someone: wealth moved to A after A's discovery lowers B's α in A's rejection indicator; wealth moved from a fishing B to A
  lowers A's α in B's rejection indicator (had B rejected, A would have received less). Routing that stays monotone — each unit's wealth = a fixed
  fraction of W0 plus fixed, versioned fractions of every payout up its chain, no outcome-driven transfers — is plausibly *one* ADDIS-type procedure
  over the merged stream. Whether Tian–Ramdas's theorem covers an arbitrary monotone rule meeting the global ADDIS constraint, or only its stated
  spending rule, is to be checked against the paper before anyone relies on it. Not established here.

## 3. What breaks it

1. **Widening:** Σ s_u > 1; a new unit, pair, merger, restructure or renewal endowed with a fresh W0 (VISION § 12 already forbids resets).
2. **Each unit its own ADDIS at the full α:** the union inherits up to K·α (the five-pair case in § 1 is exactly this, today, by design).
3. **Outcome-driven transfers under an FDR claim** (§ 2.3): fine for mFDR, outside every FDR proof known to this author.
4. **Peeking:** an α_j or a choice of hypothesis made after its own holdout figure is seen. The code seals before serving (`RefereeHoldout.cs`:95–117)
   and computes the step inside the verdict's write; a tree must keep one charge order across units (concurrent verdicts serialised, as rowid does).
5. **Dependence inside a sub-stream** — the binding problem, untouched by any split: reused months within a pair, one regime across pairs. Remedies
   in the literature: LOND under PRDS (R04 :127), e-LOND under arbitrary dependence (Xu–Ramdas 2024; R04 :128 and E7), asynchronous variants for tests
   that run over time (Zrnic–Ramdas–Jordan 2021). § 2.1's subadditivity composes with e-LOND too; its price compounds (R04: e-LOND's first test at
   α = 0.1 needs E ≥ 187; at s = 1/5, ≥ 935).
6. **An approximate p-value:** PSR's normal approximation over a short window is not exactly super-uniform; ADDIS's conservativeness gain assumes it.

## 4. The smallest U-alpha-tree that keeps it (DESIGN)

1. **Declare the root (owner/orchestrator call, recorded).** Either (a) *per pair*, as built: honest only if every page says "per pair, per window";
   or (b) *one organisation root* with a fixed, versioned level split across pairs (s = 1/5 each, `AlphaWealth.Version` 2), which § 2.1 makes valid
   whatever the cross-pair correlation. Recommended: (b). It costs nothing in present behaviour (no verdict can reject before a 365-day holdout) and
   is the only reading under which "the global guarantee" is a true sentence.
2. **One stream per root across time.** A successor campaign continues its root's α history (index j, wealth, rejections) instead of restarting at W0.
   This needs an α-order predicate distinct from `Counts`, which also scopes the judgement budgets; the budgets keep their current scope.
3. **Units split counts, not α.** The existing `verdict_share` (`CampaignStore.cs`:1098–1175; siblings ≤ the whole, `OrgStore.Tilt.cs`:62–73) is the
   tree: who may draw the next α_t. Each `referee_holdout` row records the requesting chain (from `ai_attempt_funding`, as the share check reads it),
   so α spent and earned per unit is a report and a shadow price (`U-shadow-prices`), not a separate test.
4. **A discovery earns counts.** A `rejected` step credits its chain a fixed, versioned number of verdict counts from the parent's unallocated
   share — never past the campaign's budget, never α. Fishing units exhaust their counts; discoverers earn more. Inert until rejections can occur.
5. **Invariants, red-first (evidence class):** no op creates a stream or W0 (restructure, merge, renewal, a new pair each tested); Σ root levels
   ≤ α; the α history read inside the verdict's write; one charge order across units. A mutant that endows a renewal with W0 must go red.
6. **Not in the smallest design:** per-unit ADDIS sub-ledgers (§ 2.1 inside the tree) — only if a unit's guarantee must survive its siblings'
   dependence, at the § 2.1 price; and any outcome-driven α transfer (§ 2.3) until its FDR proof exists. With items 1–5 the unit looks M, not R26's L.

## 5. What stays unproven

- That holdout p_h are independent and uniformly conservative under the null — false as stated (one path, shared months); every FDR figure here is
  nominal until E7 runs on forward data with an arbitrary-dependence procedure (R04 :266).
- That ADDIS's FDR theorem extends to the monotone fixed-fraction routing of § 2.3 (a proof, or a simulation over the code's own constants).
- That count credits change behaviour as VISION § 7.2 hopes ("departments that fish run dry"): a measurement for M-org runs, not a statistic.
- The literature statements above were not re-read today; before `U-alpha-tree` lands, its builder checks Tian–Ramdas 2019 (ADDIS, Theorem on FDR),
  Foster–Stine 2008 (mFDR_η) and Xu–Ramdas 2024 (e-LOND) for the exact hypotheses relied on in §§ 2.2–2.3 and 3.5.

**Literature (LIT, by author/year).** Foster & Stine 2008, JRSS-B 70(2), α-investing (R04 [36]) · Javanmard & Montanari 2018, Ann. Statist. 46(2),
LORD/LOND and generalised alpha-investing (R04 [37]) · Ramdas, Yang, Wainwright & Jordan 2017, NeurIPS, GAI++/LORD++ · Ramdas, Zrnic, Wainwright &
Jordan 2018, ICML, SAFFRON · Tian & Ramdas 2019, NeurIPS, ADDIS (R04 [39]) · Zrnic, Ramdas & Jordan 2021, JMLR, asynchronous online testing ·
Xu & Ramdas 2024, AISTATS, e-LOND (R04 [40]) · Robertson, Wason & Ramdas 2023, Statist. Sci., a review of online multiple testing.
