# R30 — Where the power goes: the 72-hour run is the gate (2026-11-06)

Written by the orchestrator on the owner's grant of 2026-11-06. It sets the order of work, and the decisions in § 4 stand unless he overrules them.
R25 § 2 still defines the MVP. R28 and R29, landed with this record, are its inputs.

## 1. The state, measured 2026-11-06
- **Repositories.** Local `main` equals `origin/main` (`f5534dc5`), clean. Local work that was never pushed sits in worktrees, not on `main`.
- **Main is RED** on one test on all three platforms: `OrganisationLoopTests.The_organisation_loop_runs_end_to_end…` (F-B2: D11 landed without D12).
- **Finished but unlanded:** `claude/red-main-fix`, `claude/u-turn-reservation` (D13), `claude/u-founding-carry` (D14). None has run CI on three platforms.
- **Stranded at the 2026-11-02 stop:** 13 units (`FAST-STATE` lists them), including D12 (`u-evidence-holdout-length`) and the evaluator bump (`u-backtest-parity-2`).
- **The server** is up and collecting (tape 482 MB, 0 restarts since 2026-11-02), AI paused, one real model attempt ever on it (2026-10-30).
- **Real-model facts so far:** one chief turn. Verdict → paper → fills has never run on any seat with a real model.
- **R28 verdict:** the run as specified cannot close its evidence loop on main.

## 2. The deduction
**Every remaining question waits on the same event: the first real 72-hour unattended run (M-org1 sitting #2).**
- R26 § 3's stage-2 order is set by what the run measures (R26 § 4); the funding decision waits on its cost; MVP-W is the same proof on Windows.
- Puppets have proved the kernel. They cannot say where a real model breaks the loop, what a turn costs, or how fast signal comes.
- Tokens cannot buy the run's 72 hours of wall clock, so **every hour before T0 delays all of that one for one**.
- More organs, more governance and stage-2 units before T0 would be built on guesses.

So all power goes to reaching T0 honestly and soon. MVP-W moves in parallel where it shares nothing with the gate. Nothing else starts before T0.

## 3. The T0 gate (the smallest set that makes the run able to mean something)
| # | What | Finding | Unit |
|---|---|---|---|
| 1 | Main green | F-B2 | `claude/red-main-fix` |
| 2 | A plan turn reserves what it can plausibly cost | B1, F-A3, D13 | `U-turn-reservation` |
| 3 | The old-shape home opens or refuses, carrying its tape; founding positions 15 | D14 | `U-founding-carry` |
| 4 | 36 months with 12 held back; a full exploration pot is spent; holds per pair | E1, E2, E4, D12, D15, D16 | `U-evidence-holdout-length` |
| 5 | Verdicts under the final evaluator; a reused backtest is the caller's | — | `U-backtest-parity-2`, `U-backtest-dedupe-role` |
| 6 | A stop stops; quantity limits fit the envelopes; notes tell the truth | P1, P2, P3, P4, G1, D18, D19 | `U-paper-closure` |
| 7 | One bad stream or hung turn cannot stall a seat; a refused role cannot block the lane | W1, W2, W5, W6, D17 | `U-turn-bounds` |
| 8 | Waits are fair and floored; a restart keeps its work; the owner's words reach the chief | W3, W4, W7, R1, R2, R3, F-A1, F-A2, F-C2 | `U-wake-fairness` |
| 9 | The run's eyes | — | `U-org-digest` |

Anything unfinished at the call may ship as a new kit mid-run, because an install keeps the home. Items 1–6 may not: they decide whether what the run produces is evidence.

## 4. Decided by the orchestrator on the owner's behalf, 2026-11-06 (his to overrule)
- **D15 (E2):** a campaign counts as spent when its exploration pot is full and no parent of it can be Promoted, which arms renewal. PaperEligible does NOT become a
  promotable parent: that would widen the search budget on an unproven basis. Nothing about the 200 trials, 3 verdicts or 50 exploration trials changes.
- **D16 (E4):** a forward hold withholds only its campaign's own pair, because campaigns and renewal are per pair and D15 makes spending sooner. CONTRACTS says plainly
  that correlated pairs still leak some information across a hold. F-B1 is decided separately (§ 5).
- **D17 (W6):** no automatic model switch. A position's model stays an owner's two-press. A vendor's refusal of a model is shown on the page, holds that seat under a
  backoff, charges no further reservation for the same refusal, and the sitting presses "Run on the fallback" under the standing go if it must.
- **D18 (P2):** founding `MaxOrderQuantity` is per pair and equals the pair's envelope quantity ÷ its 3 deployments, so capital binds, not a flat 1.
- **D19 (P1, P4):** an owner's or an organisation's stop ENDS the allocation: no replacement run, and the slot frees once flat. The old "replace once flat" meaning goes.
  Nothing changes for a run that ends by itself.

## 5. After T0, in order
1. Whatever the run breaks pre-empts everything (R26 § 4).
2. **MVP-W in parallel:** `U-atas-home-no-atas-build`, `U-kit-socket-2`, `U-cell-windows-egress`, then the DRAFT 0.2.0 release and the Windows sitting.
3. **R29's other findings:** F-B1 (a design question: serve less, or judge a version only on bars after its own freeze), F-E1, F-D1, F-B3, F-C1.
4. The rest of the stranded units, each re-judged against what the run measured.

*Sets order and records decisions only. It marks nothing fixed.*
