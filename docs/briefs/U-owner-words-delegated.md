# U-owner-words-delegated — nothing an agent writes can mark the owner's words handled: a brief naming a lost or finished turn leaves that turn's owner message open and re-raised, and a turn that ended without an outcome hands the words on once
**Protects:** operator authority and the inbox-as-data rule (`CLAUDE.md`: what an agent writes is data, never a grant — an agent-written file must never settle, hide or overwrite the owner's message's disposition);
the owner's words first (`docs/briefs/U-reconcile-wakes.md`, landed: they reach the chair's next admitted turn first, exactly once); measurement vs claim (the `delegated` link stays a measurement the app makes, never
the agent's claim); recovery (a crash between any two writes loses no words). Seat B; heavy (operator authority: opus); **no rung**. Ranked FIRST in seat B's lane by the orchestrator (2026-10-11 ~08:00).
**Depends on** `U-reconcile-wakes` (landed: `RecoverLostOwnerWords`, `SettleLost` + `RetryOnce` in one commit, `DueFor` owner-first, `RoleObjectives.OwnerAnswered`).
**Facts (SOURCE at `u-reconcile-wakes-2` `60c7dee9` = main after that landing; re-check by `grep -n` at dispatch).**
- `AgentRuntime/CouncilRelay.cs:325-357`: a published file's `Attempt` is the id its FILE NAME carries (`AttemptIn`), fenced by `Fence` (:492-515): a row of the same role that is not LAUNCHED (ENDED **or LOST**)
  publishes. Then for a `Brief` (:420-422) `_events.Delegated(p.Attempt!, p.Id)`.
- `Core/Db/MissionEventStore.cs:821-831` `Delegated`: `UPDATE mission_event SET disposition='delegated' … WHERE kind=owner AND consumed_by=$attempt AND (disposition IS NULL OR disposition <> 'delegated')` —
  it OVERWRITES `answered` and `failed` too, for whatever attempt the file names.
- `AgentRuntime/MissionLoop.cs:1640`, `:2498-2560` (`RetryOnce`, `RecoverLostOwnerWords`); `MissionEventStore.cs:666-700` (`SettleLost`). Startup runs `Relay.Reconcile()` (`App/AppHost.cs:785`) BEFORE the loop's
  recovery, so a brief naming a LOST attempt marks its owner message `delegated` first and recovery then skips it: the words are never re-raised.
- `RoleObjectives.cs:108`: the owner objective is the PENDING owner wakes; an owner message consumed by a launch that ENDED without any disposition (or by a launch with no meter row) is neither an objective nor
  re-raised (owed by U-reconcile-wakes' fix report).
Must NOT: add a table, rung, migration, op, verb, argument or pipe-writable setting; let ANY agent-written file (brief, agenda, report, `next.json`, memory) settle, hide, re-raise or overwrite an owner message;
change the quarantine fence's publish/leave/quarantine verdicts for anything but the delegation link; touch the canon template, `Canon.cs`, `WorkspaceBuilder`, `ledger_` tables; drop the owner's words from first place.
Items, one commit each, one-sentence messages:
1. **Delegation is the app's measurement of a FINISHED turn only.** `Delegated` writes only where the named attempt is ENDED (never LOST, never an unknown or another role's), and only over a NULL disposition —
   never over `answered`, `failed` or a retry's row. Ordering at startup: recovery of LOST turns' owner words is not pre-empted by `Relay.Reconcile()` (either order gives the same result).
2. **A finished turn that left the words without an outcome hands them on once.** An owner message consumed by an ENDED launch with no disposition after that launch's relay pass (or by a launch with no meter row)
   is settled `failed` ("ended without an outcome") and re-raised ONCE in the same commit as `SettleLost`/`RetryOnce` do — the once rule held (a retry's own failure is not re-raised again).
3. **The words:** CONTRACTS' wake queue and the brief/delegation paragraph say what delegation now requires; USER-GUIDE if it states it.
Red-first tests (each quoted red at base): (a) `A_brief_naming_a_lost_turn_leaves_its_owner_message_open_and_re_raised` (the orchestrator's: the real startup order — `Relay.Reconcile()` then the loop's recovery);
(b) `A_brief_never_overwrites_an_answered_or_failed_owner_message`; (c) `A_brief_naming_another_roles_or_an_unknown_turn_delegates_nothing`; (d) `A_turn_that_ended_without_an_outcome_hands_the_owners_words_on_once`;
(e) `A_retry_that_ends_without_an_outcome_is_not_re_raised_again`; (f) `A_finished_turns_brief_still_delegates_the_words_it_took` (the legitimate path kept). Mutants, red and quoted: `Delegated`'s NULL-only guard
removed ⇒ (b); the ENDED-only check removed ⇒ (a), (c).
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) and BUILDER-PROMPT-v2 step 3b — rebase on `main` first; Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all platforms; tests box
`ready` once or NOT RUN; names vs `main` 0 removed (both set sizes). `## Report` ≤ 20 lines appended here. No push to `main`, no merge.

## Report
Tip of code 80d9e4a4 (rebased on main c4a6f2d8); items d3c9a1a8, 5243577c, 80d9e4a4; this report commits last.
1. Build: `dotnet build TradeAgent.sln -c Release --no-incremental` → "0 Warning(s)" "0 Error(s)".
2. Touched classes (CouncilLoopTests incl. new CouncilOwnerWordsTests.cs, OwnerDispositionTests) 3× via suite.sh: "Passed! - Failed: 0, Passed: 40" ×3.
3. CI run 38126700131 at 80d9e4a: success on every job (shard-plan, package, package-linux, test windows 1–5, macos, ubuntu, linux-host). Review: `fleet/tmp/B-scratch/u-owner-words-delegated/U-owner-words-delegated-review.md` (not acted on).
4. names.sh main vs branch: "removed: 0", "added: 6"; [Fact]/[Theory] base 2510, tip 2516.
5. Tests box: NOT RUN — `ready` answered NO: the machine does not answer (asleep, off the private network, or the share was removed).
Item 1 DONE: `Delegated` writes only where the named launch is ENDED in `ai_attempt`, its role is the brief publication's role (joined on `publication`, kind brief), and the message is NULL or `blocked`. Deviations: `blocked` counts as no outcome, so a message that waited and was then taken keeps its link, as `LostOwnerWords`/`SettleLost` already treat it; signature unchanged (the role comes from the publication row).
Item 2 DONE: `UnansweredOwnerWords`/`SettleUnanswered` (ENDED launch, or no meter row, still unsettled) + `MissionLoop.RecoverUnansweredOwnerWords`: settled `failed` ("…ended without an outcome…") and re-raised through `RetryOnce` in one `InOneCommit`. A retry is settled and not raised again. Runs only while that role's slot is held (new `Hold`, which takes the turn slot without the card state), so an unrecorded turn in flight is never pre-empted.
Item 3 DONE: CONTRACTS disposition paragraph + wake-queue paragraph. USER-GUIDE does not state delegation, so it is unchanged.
RED at base c4a6f2d8 (tests built on base src): (a) "Expected: \"failed\" Actual: \"delegated\""; (b) "Expected: 0 Actual: 1"; (c) "Expected: 0 Actual: 1"; (d) "Assert.Single() Failure: The collection did not contain any matching items" (only research looks); (e) "Expected: \"failed\" Actual: null". (f) is green at base by design: it guards the legitimate path.
Mutants: the NULL/blocked guard removed ⇒ only (b) red. The ENDED/role EXISTS clause removed ⇒ (a) and (c) red.
Test changed: OwnerDispositionTests `A_message_delegated_into_a_brief_keeps_that_disposition…` now sets up an ENDED attempt and a real brief publication. The link it asserts now requires both, and its assertions are unchanged.
NOT done or verified: no full local suite (CI's); tests box not run; a failed turn that also published a brief still both delegates and retries (pre-existing, untouched).
