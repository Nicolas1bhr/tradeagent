# U-rewake-followups — a holder carries at most one brought-forward mandate at any time, a huge floor cannot overflow the re-wake sweep, and an unplaceable holder's warning survives the deadline clamp
**Source (seat B's cards, due before M-org1):** `U-rewake-one-brought` — the residual seat B accepted when `U-rewake-floor` landed (record `fleet/records/U-rewake-floor.md`
line 9; the re-review `fleet/tmp/B-scratch/u-rewake-floor-fix2-review/rereview.md` defect 1, with its five-step reproduction) — and `U-rewake-followups` — the Codex second
pass `fleet/tmp/B-scratch/U-rewake-floor-codex2/review.md` (P2 overflow, P3 lost warning). Pointers checked against main e317c011, 2026-10-24 — re-grep.
**Protections — BUDGET-001 (an extra turn on the owner's allowance is the defect; a re-wake never fires early), refuse never guess (a floor the sweep cannot compute
must not abort the sweep for every later mandate): red-first and ONE watched mutant.** Seat B; **opus/high** (spend). **No rung.**
**Today:** `HolderAsked` (`src/TradeAgent.Core/Db/OrgStore.Mandates.cs:1155`) returns null only while another mandate's untaken `mandate` wake is due AT OR BEFORE the
holder's earliest self due; a mandate already brought to a LATER self wake is invisible to it (used by the bring-forward at `:1028` and by `RewakeDue` at `:1122`).
`RewakeFloors.After` (`src/TradeAgent.Core/Db/RewakeFloors.cs:64-74`) computes `floor * Cap` before the ceiling applies, so an accepted floor of `int.MaxValue` minutes
throws. The unplaceable-holder warning near `OrgStore.Mandates.cs:1033` is logged only when `due > now`, and the deadline clamp can make it due now.
**Observable result:** the re-review's sequence (m2 brought to self at T+3, m1 waiting its floor at T+15, an owner turn asking a self wake at T+2.75, a launch spanning
T+2.5→T+3.5) leaves no mandate wake due before its floor once `OneMandate(events.DueFor(..., T+3.5m))` takes one; `After(Executor, 1)` with an `int.MaxValue`-minute floor
and a 24-hour ceiling returns 24 hours; the dormant-holder sweep with a deadline ten minutes away names the holder in the activity callback.
Items, one commit each, one-sentence messages:
1. **One brought mandate per holder:** `HolderAsked` (both callers) refuses to bring a mandate forward while ANY other mandate of the holder has an untaken wake due
   before its own floor, at any due — not only ≤ the asked time. Check: `A_holder_carries_at_most_one_brought_mandate_across_two_self_wakes` (the re-review's sequence,
   in `CouncilLoopRewakeTests` or `OrgRewakeFloorTests`, whichever drives `OneMandate`).
2. **No overflow:** `After` clamps in TimeSpan without multiplying past the ceiling (compare before doubling; `Cap` floors computed saturating). Check:
   `A_floor_of_int_max_minutes_reads_its_ceiling_and_the_sweep_continues` (the sweep also reaches the NEXT mandate).
3. **The warning survives the clamp:** the unplaceable-holder warning is raised whenever the holder cannot be placed, whatever `due` is. Check:
   `An_unplaceable_holder_is_named_even_when_the_deadline_clamp_makes_it_due_now`.
Red-first (quote each red at base): items 1–3's checks.
**The watched mutant:** item 1's guard narrowed back to `due_at <= asked` ⇒ item 1's test red. Quote, restore, green.
**Read first:** CLAUDE.md (money rules not touched; BUDGET-001 in ORCHESTRATION § 2.4/2.5 only if needed); the two review files above; `OrgStore.Mandates.cs:1000-1180`,
`RewakeFloors.cs`, `MissionLoop.cs` around `OneMandate`/`DueFor` (`:3010`, `:4189`).
**NOT in scope:** wake policy values; any other re-wake behaviour; the fence ordering (U-org-verbs-d owns `OrgStore.Fences.cs` — do not edit it).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.

## Report
Code tip 35319c0e (base bb4f177e, no rebase needed). One file of code each: `OrgStore.Mandates.cs`, `RewakeFloors.cs`; tests in `OrgRewakeFloorTests.cs` (partial `OrgAssignmentTests`).
- Item 1 (4f52bc16, fix pass 35319c0e): `HolderAsked` (both callers) refuses while another mandate's untaken wake is due at or before the request, OR is due before that mandate's own floor at any due (`Brought`, via the new `FloorDue`, which `RewakeDue` now also uses). Check `A_holder_carries_at_most_one_brought_mandate_across_two_self_wakes`; fix-pass check `A_requested_wake_another_mandate_reaches_at_its_floor_brings_nothing_forward` (Codex's finding: item 1 had REPLACED the `due <= asked` guard instead of adding to it).
- Item 2 (49eac08c): `After` saturates its cap and compares before each doubling; `FloorDue`'s date sums saturate. DEVIATION: the deadline's last-chance margin is `After(level, 1)` (the floor, or the ceiling when the floor is longer), not the raw floor — with a raw int.MaxValue-minute floor the margin underflowed (and, saturated, made every re-wake due now). Identical for every floor ≤ its ceiling. Check `A_floor_of_int_max_minutes_reads_its_ceiling_and_the_sweep_continues`.
- Item 3 (a13cbcf1): the unplaceable-holder warning is raised with every re-raise it writes, whatever `due`; a due-now line says so. Check `An_unplaceable_holder_is_named_even_when_the_deadline_clamp_makes_it_due_now`.
- RED at base: item 1 `Assert.Equal() Failure ... Expected: 0 Actual: 1` (OrgRewakeFloorTests.cs:499, m1 brought to T+2.75); item 2 `System.OverflowException : TimeSpan overflowed because the duration is too long.`; item 3 `Assert.Single() Failure: The collection was empty`. Fix pass RED at a13cbcf1: `Expected: 0 Actual: 1` (:555, m2 brought to T+15.5).
- Mutant (guard narrowed back to `due <= asked` alone): item 1's test `Failed ... Expected: 0 Actual: 1` at :499 — watched at 4f52bc16 and again after the fix pass; restored, green.
- Gate 1: `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s)` `0 Error(s)`.
- Gate 2: `OrgAssignmentTests` 3× via suite.sh → `Passed! - Failed: 0, Passed: 53, Skipped: 0, Total: 53` ×3 (exit 0 each). Neighbouring Sweep callers (OrgVerbs, CouncilRole, OrgDecision, CouncilLoopTests) once: 111/111.
- Gate 3: CI 40113721557 (a13cbcf1) green; CI 40126048817 (35319c0e) `success x12`.
- Gate 3b: Codex review `fleet/tmp/B-scratch/u-rewake-followups/U-rewake-followups-review.md` (one P2, fixed above); re-review `.../U-rewake-followups-rereview.md`: "No defects found."
- Gate 4: names.sh → `removed: 0`, `added: 4`, `[Fact]/[Theory] base: 3153 tip: 3157`.
- tests box: NOT RUN — the machine does not answer
- Not done / not verified: no app launch; no full local suite; the no-ceiling int.MaxValue case still makes a re-wake due now when the floor exceeds the time to the deadline (the existing last-chance rule, unchanged); `OrgStore.Fences.cs` untouched.
