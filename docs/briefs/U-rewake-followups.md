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
