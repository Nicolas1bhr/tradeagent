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

## Report
**Tip** `6e56f86a` (code), rebased onto `2acb64bf` (main with part a). Gate: (1) `dotnet build -c Release --no-incremental` → `0 Warning(s)`, `0 Error(s)`. (2) OrgAssignmentTests (incl. OrgFenceTests), PuppetLivesTests, OrgRightsTests and ErrorCatalogueTests 3× → `Passed: 69` each time; OrgLedgerTests 3× → `Passed: 7`. (3) CI 39873453334 RED on all three platforms, at `OrgLedgerTests.No_pipe_op_writes_an_org_table`: the guard lists OrgStore's public methods, and three new reads were not declared there. Fixed in 6e56f86a. CI **39877403166 green**: package, package-linux, ubuntu, macos, windows 1–5, linux-host. (4) `names.sh` → `removed: 0`, base 3042, tip 3048. (5) tests box: NOT RUN — busy (ready: "a run is in progress").
1. DONE: `OrgStore.FencedSince` reads the mandate's latest non-claim event when it is cancelled or rescoped at or after the attempt's start, or a later `org_event fenced` naming the position. Adds `OrgEventKind.Fenced` and `NeverFenced` (legacy pair, null role, app principals).
2. DONE: `Handle` checks the fence after building ctx and before the role/order gate and every op's own gates. It refuses with a new `ErrorCode.ATTEMPT_FENCED` that names the launch, mandate, cause and reason. A failed fence read refuses the op. The legacy guard is checked on ctx.Role before any read.
3. DONE: after a cancel or rescope, `TradingGateway.Fenced()` fires `FenceWritten` (wired by AppHost), which calls `MissionLoop.EndFencedAsync`. That cancels the fenced position's running conversation through `CancelAsync`. The turn commits as usual: ENDED, reservation kept as cost. It is not retried and not counted as a failure, and the next prompt carries the fence's words. Puppet test: the turn ended 0.1 s after the cancel.
4. DONE: `CouncilRelay.Land` holds a fenced launch's staged output whatever its recipients, with a disposition naming the fence. A failed read also holds it.
5. DONE: Cancel and Rescope writes are the fence. `mandate-show` gains `fenced`: launches under the mandate that started before the transition and had not ended by it (`OrgStore.FencedBy`).
Deviations: (a) "recorded as fenced" is derived from the fence row plus the ENDED attempt. I added no ledger column and no context field (no rung). (b) Items 1 and 5 add `FencedBy` and `NeverFenced`. (c) New error code instead of INVALID_REQUEST, because the repair differs.
RED at base f4679c7e: item 1 did not compile (`'OrgStore' does not contain a definition for 'FencedSince'`). Item 2: `'status' was served to a fenced launch`. Item 3: `not within 3 minutes … pos-fe11ce000002 LAUNCHED`. Item 4: `Sub-string not found "a position's work reaches its mandate's i"`. Item 5: `KeyNotFoundException` (no `fenced`).
Mutants, each watched red and then restored: `at >= started` → `at < started` turned items 2 and 3 red (`'status' was served to a fenced launch`; the member's turn `LAUNCHED` at 3 min). Guard: removing `CouncilRoles.IsKnown` from `NeverFenced` made `An_org_fence_never_refuses_an_operations_op` fail with `operations's 'status' was fenced`.
Codex review of 857eb14e is at `fleet/tmp/B-scratch/u-org-verbs-b/U-org-verbs-b-review.md`, NOT acted on. It reports 2 P1 races in EndFencedAsync (a launch admitted before it is tracked; cancelling a successor turn) and 1 P2 (mandate-show's "turn ended" wording). A re-review of 6e56f86a is running, detached, writing `…-review-2.md`.
NOT VERIFIED: the race windows Codex names. Nothing was run on Windows outside CI. No dispatcher-tick recheck of fences exists, so the signal is the only trigger. Parts c–d's writers are out of scope.

## Fix pass 1
**Tip** code `0a5d021e` on main `6701416a` (rebased clean). Gate: `--no-incremental` Release → `0 Warning(s)`, `0 Error(s)`; PuppetLivesTests+OrgAssignmentTests+OrgLedgerTests 3× → `Passed: 71` each (guard `An_org_fence_never_refuses_an_operations_op` included); MissionLoop neighbours 1× → `Passed: 233`; names.sh `removed: 0`, base 3043 → tip 3052; CI **39889333189 green** (success:12). Codex re-review (`fleet/tmp/B-scratch/u-org-verbs-b-fix1/rereview.md`): "No defects found. All three points appear closed".
1. DONE `c3ad12cf` (P1-1): the loop tracks the launch FIRST, then reads its fence; a fenced launch's own linked token is cancelled before its send, so it starts no process. The legacy pair are never tracked or read. RED (`A_fence_committed_before_the_loop_tracks_the_launch_still_ends_it`, seam `AfterAdmission`): `not within 3 minutes … pos-fe11ce000004 LAUNCHED`. Green: it ended 0.2 s after the loop started, with 0 puppet turns started.
2. DONE `1d9c4c3f` (P1-2): each tracked launch is its own `RunningTurn`, checked by reference. The fence's identity check and its cancel (the launch's own token, then the conversation's teardown) run inside a per-launch `HandOver` semaphore. The turn's commit takes the same semaphore before it hands over, so the successor cannot begin until the cancel is done. RED (`A_fence_never_cancels_the_turn_that_follows_the_one_it_fenced`): `the successor … was cancelled by the fence on …: exit -1, cost 2.56 of 2.56 reserved; it had started while the fence held its check: True`. WATCHED MUTANT: moving the identity check (and the seam after it) outside the hand-over failed it with the same words. Restored, then green.
3. DONE `0a5d021e` (P2): `FencedBy` → `FencedLaunch(Attempt, Ended)`, where Ended is measured from the row's `ended_at`. `mandate-show` lists `{attempt, turn: "ending"|"ended"}` and its note now claims no end. RED (`Mandate_show_says_a_fenced_turn_is_ending_until_its_launch_is_closed`): a bare id under the note `… each was refused from then on and its turn ended.`
Tests box: NOT RUN — busy (exit 4).
NOT VERIFIED (new, for seat B): `FencedSince` compares wall-clock `at >= started`. A cancel stamped just before a concurrent admission's `started_at` but committed after it would read as not fenced. Both timestamps are taken before their writes, and this pass did not test that ordering. Windows was not run outside CI.
