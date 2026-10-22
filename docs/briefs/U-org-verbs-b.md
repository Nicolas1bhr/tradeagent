# U-org-verbs-b — fences: an authority-changing transition ends every attempt it touches — refused on its next operation, its turn and process tree ended, its outputs held — starting with a cancelled or re-scoped mandate
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.3 *Fences* (:173-178: "bumps the generation of every grant it touches; the broker checks the generation on every
operation; a running attempt under a stale generation is ended with its process tree, and its outputs are quarantined at completion, never published"; fences
"never touch trading … anything about `operations` stays his presses"), § 2.9 *Restructuring is a transaction* (:328-330), § 10 (:493 "O6: the lattice, fences");
§ 15 O6 (:427), part b of 5. Survey: `fleet/tmp/B-scratch/org-verbs/survey.md` (main `25bae9bc`) — re-grep every pointer. Pointers re-checked against 46f7bbd3 (main 4b2af1e7 + U-org-verbs-a), 2026-10-23.
**Protections — AUTH-001 and ORDER-001 (a fence only takes away; it never touches `operations`, an order op or operator authority), UNKNOWN-001 (no order turn is
ever cut by an org fence), BUDGET-001 (a fenced attempt keeps its reservation until it closes; a LOST one keeps it), INBOX-001/COUNCIL rule 7 (a fenced output is
held, never published), refuse never guess (a fence read that fails refuses the op): red-first and ONE watched mutant.** Seat B; **opus/high** (the agent
boundary, the gateway's per-op path). **No rung.**
**Depends:** part a (`mandate-rescope`) LANDED; `U-org-packets` LANDED (both touch `MissionLoop`).
**Applied pending ORCHESTRATOR (survey Q13):** fences are DB facts, restart-safe and atomic with the transition — a mandate-level fence is the mandate's own
`cancelled`/`rescoped` event; a position-wide fence is an `org_event` of kind `fenced` (position, cause in app-composed detail) that parts c–d write — never an
in-memory counter a crash could lose between the commit and the bump.
**Today:** no generation exists (`Security/AgentGrants.cs:45-99`; no fence in `OrgStore*.cs` or `AgentGrants.cs` — elsewhere the word is only in comments); O5a's `Cancel` fences nothing (`OrgStore.Mandates.cs:427-436`);
`ai_attempt.mandate_id` records the mandate a launch serves (`Database.cs:2589-2590, 2511-2512`); the one per-op caller seam is `GatewayPipeServer.cs:1288-1301` (the door reaches
it through `CallAsGrantAsync`, `BrokerDoor.cs:178-182`); Pause ends a conversation's whole tree through `CancelAsync` (`AgentRuntime/MissionLoop.cs:2376-2401`); a
non-legacy output is already held (`AgentRuntime/CouncilRelay.cs:400-416`, `HeldWhy` :680).
**Observable result:** a puppet executor is mid-turn on mandate M; its head cancels (or re-scopes) M; the executor's next `trade` call — any op — is refused in
words naming the fence, its turn and process tree end within one loop tick, the attempt closes recorded as fenced with its reservation settled, and nothing it
staged is published; `operations` and `research` are never fenced and their turns run on.
Items, one commit each, one-sentence messages:
1. **The fence read** (`OrgStore.FencedSince(attemptId)`): an attempt is fenced when its mandate's latest event is `cancelled` or `rescoped` at or after the
   attempt started, or an `org_event` `fenced` names its position at or after that; `OrgEventKind.Fenced` added; a read that throws is a refusal. Check:
   `An_attempt_is_fenced_by_a_later_cancel_and_not_by_an_earlier_one`.
2. **The broker refuses** at the per-op seam, BEFORE the op's own gates, every op of a fenced attempt — org verbs and pipe ops alike — in words naming the
   fence and its cause; the legacy pair are never read as fenced (a guard, not a branch any verb can reach). Check: `A_fenced_attempts_next_operation_is_refused`;
   GUARD `An_org_fence_never_refuses_an_operations_op` (red with the exemption removed).
3. **The loop ends it:** a fencing write signals the loop (an app event `AppHost` composes; no pipe op), which ends that position's running conversation through
   `CancelAsync` (its process tree, as Pause does) and closes the attempt with the fence's words; wakes kept or withdrawn by the transition, never retried here.
   Check: puppet `A_cancel_mid_turn_ends_the_holders_turn_and_tree` (a `signal`/`await` script pair, `PuppetLife`).
4. **Outputs held:** a fenced attempt's `mandate-deliver` is refused (item 2) and its staged `out/` is held with a disposition naming the fence, never published.
   Check: `A_fenced_attempts_output_is_held_never_published`.
5. **Cancel and rescope fence** (O5a's `Cancel`, part a's `Rescope`): their transactions are the fence (item 1 reads them); `mandate-show` says "fenced: <attempt>".
Red-first (quote each red at base): items 1–4's checks.
**The watched mutant:** item 1 compares `at > started` as `at < started` (only earlier transitions fence) ⇒ item 2's and item 3's tests red. Quote, restore, green.
**Read first:** CLAUDE.md; ORCHESTRATION §§ 2.3, 2.5, 2.9, 8; the survey § 1; `U-grant-tree` and `U-unjobbed-presence` records (process trees, presence);
`GatewayPipeServer.cs:1240-1310`; `MissionLoop.cs` Pause and lanes; `CouncilRelay.cs:395-420`.
**NOT in scope:** the transitions that write position-wide fences (retire, close, merge, veto — part d; sleep — later); generations on `AgentGrant`; helpers.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
