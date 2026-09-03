# Sibling-process survey — what TradeAgent's hardening process is ported from (2026-09-02, re-materialised 2026-09-03)

Read-only survey over `/Users/nicolasbeeckman/Projects/innovision-os` and `/Users/nicolasbeeckman/Projects/venture-agent`.
Paths are absolute so briefs can point at them.

## innovision-os — the standard

`innovision-os/docs/ORCHESTRATION-STANDARD.md` (336 lines), a mandatory read-gate before any delegated build/verify work.
§0 failure-mode ledger L1→L6 → rules R1 (no self-grading) R2 (scope integrity — a diff smaller than the acceptance is
RED) R3 (adjacent-regression sweep) R4 (gates pasted, not claimed) R5 (green ≠ safe) R6 (honest reporting). §3 the
verification triad: builder self-gate → adversarial verify by a DIFFERENT Claude told to refute, default-to-fails → Codex
cross-model read-only review, the latter two concurrently on the same frozen diff; red-first (write and run the exploit
so it goes RED before the fix), both directions (the attack is denied AND legitimate access still works). §4 Codex:
verified command forms; model by tier (`gpt-5.6-sol` T1 round 1); Codex exits 0 on failure — read the captured output;
never tail a raw log into an orchestrator context; §4.4 findings are an INPUT, never auto-applied — real → bounce,
false → recorded with the one-line reason; silent dismissal forbidden; §4.6 a second model attacks the PLAN before it
reaches Nicolas so his gate is a direction decision. §6 honesty contract: "verified by running X → output" or "NOT
verified"; banned words static-verified / should work / looks correct / minor / trivial / I believe / probably /
basically; "I could not verify Y" is a success. §8 pre-Integrate checklist. §9.1 tiers: T1 catastrophic-if-wrong = full
triad, rounds until no HIGH/MED; T2 = [1]+[2], Codex on trigger, rounds capped at 2; T3 mechanical; when in doubt tier UP.
§9.2 only HIGH/MED opens a round, LOW batches. §9.3 builder and verifier persist across rounds. §9.5 full suite on the
self-gate, targeted on bounces, full once more at Integrate. §9.6 escalation comes from independence, not the model dial.
§9.9 every real finding: "can a script/gate catch this class next time?" §9.10 ≥2 findings with one root cause → name the
CLASS and the structural fix. §2.6 concurrency ≤ 2 Claude agents (usage limits).

Records `innovision-os/docs/hardening/records/` (template by example `H-20-build-record.md`: verdict · diff · proof
matrix with an executed mutant at every layer · gates · raw output · adjacent sweep · gaps ✅ only when closed by RUNNING
something · "What I did NOT do"). Mutation-proving the gate ("a tooth is only a tooth once you have watched it bite").
Positive controls (`PS-N8N-record.md:247`). Claims expire when the code under them changes (`HF-A1.5-record.md:2848`).
Re-derive load-bearing quantities from the PRIMARY measurement (`H-20-prod-cardinality-2026-08-31.md`). Anti-drift CI
gate `scripts/check-ci-gate-parity.mjs`; roll-call by name, never exit code. Deploy: preflight, dry-run, verify against
the LIVE system after deploy, skip layers with zero changes.

## venture-agent — the unattended doctrine

`docs/ARCHITECTURE.md:65` three hard boundaries ("maximum freedom inside a boundary it cannot argue its way out of"):
money enforced in the database grant (the agent may INSERT a request, never UPDATE it out of pending; a spend row is
refused without an approved request), network by an iptables chain, law/ethics by a constitution labelled persuasion.
Kill switch in the DATABASE ORed with a file, agent read-only, `/stop` from Telegram; `pause` softer. Fail-closed config
(`supervisor.py:104`: unreadable/empty policy ⇒ kill switch on; missing block ⇒ nothing enabled). Auto-pause after 3
consecutive failures; `notify()` returns a bool and an undelivered alert writes `alert_undelivered` into the append-only
events table; the event row is the ATTEMPT, the alert names the OUTCOME; the failure counter is not reset on
`unrecorded`. `cycles` table = heartbeat + cost record; digest-pinned brain re-verified before exec. Budgets: ceiling is
planned against, not drawn on; absent value ⇒ every cycle `unknown`, never unbounded. Autonomy ratchet starts CLOSED;
self-authored terms removed from the arithmetic. Ledger: integer cents, append-only rules binding even the owner,
`actor` stamped by trigger from `current_user`, "FITNESS MUST BE COMPUTED FROM DATA THE AGENT CANNOT AUTHOR". Migrations
with checksums; drift stops the runner; "you may not move a bar and claim to have cleared it in the same run".
`BUILD-STANDARD.md` §7 "a broken thing produces silence and silence is what a working thing produces too" — recheck
windows, overdue = unproven; anything unattended is at least tier 2; §4 what makes a proof real; §6 silent-success traps.
`SECURITY.md` "Verifying isolation" (both-directions probe) and "Residual risks, stated plainly".

## Shared conventions

`venture-agent/CLAUDE.md:36` points at innovision's standard by absolute path and restates the non-negotiables; two
audiences, two documents (`CLAUDE.md` for the orchestrator, `BUILD-STANDARD.md`/`AGENTS.md` for the agent); records end
with "What I did NOT do"; `FITNESS-INTEGRITY-HANDOFF.md` shape (what · read order · decisions settled · done · next ·
open · gotchas · process rules that paid); one unit ≈ one session; read the state back from the live system; suspect the
instrument first; assert the artefact's identity before consulting it.

## Twelve patterns ported to TradeAgent (ranked for real-money first deployment)

1. Money boundary structural, not procedural. 2. Kill switch durable, agent read-only, reachable remotely. 3. Fail closed
on every unreadable input. 4. Auto-halt on consecutive failures; a failed alert is itself an event. 5. Append-only ledger
with provenance from the credential. 6. Red-first + both directions + positive control on every safety proof.
7. Mutation-prove every guard. 8. Risk tiers with an enforced floor: unattended ≥ T2; order path = T1. 9. Recheck windows.
10. Honesty contract + "what I did NOT do". 11. Cross-model review on T1, triage on the record, design challenge.
12. Re-derive load-bearing numbers; claims expire; §9.9 gate each class.
