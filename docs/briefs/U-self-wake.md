# U-self-wake — a role's own writes never wake it: the inbox wake fires for material that ARRIVED, not for the AI's own journal
**Boundary and arrow:** the spending boundary at the paid-turn trigger and the loop's "useful waiting" (`docs/PRINCIPLES.md`: "'Never stopping' … does not
mean constant paid inference"; "No useful eligible work is healthy idleness"). **Observable result:** in the running app a turn that only rewrote its own
`trading/PLAN.md` / `JOURNAL.md` raises no wake; a file the owner drops in `inbox/` still wakes the AI; a relay delivery still wakes its recipient through
its own `task:` wake; the material ledger records every file exactly as before.
**The defect, RUN 2026-10-01 in the observed run (`docs/briefs/U-observed-loop.md`, attempt 2, app at `faef463`, model gpt-5.6-luna):** each Operations
turn rewrote `agent/trading/PLAN.md` and `agent/trading/JOURNAL.md` (material rows origin `Agent`, version 0, first seen 15:52:49Z, 15:55:39Z, 15:56:07Z …)
and each pass raised `inbox:<instant>` `{"added":2,"seen":20}` → another Operations turn told "new material arrived in `../inbox`", whose own journal says
(the agent's words) "Inbox and `in/` contained no new material or Research report" — eight turns in eight minutes with nothing to do. Cheap on luna
(~0.01 USD a turn); on the default gpt-5.6-sol (6.4 USD reserved a turn) the same loop would hold the 5 USD daily cap after a handful of turns.
**Cause (SOURCE):** `AppHost.ScanMaterials` (`src/TradeAgent.App/AppHost.cs:1013-1018`) wakes on `result.Added > 0` whatever the origin; `ScanResult`
(`src/TradeAgent.Core/Materials.cs:96`) has no per-origin count; the scanner (`MaterialScanner.Scan`, `:127`) walks the roles' homes and the shared inbox;
relay deliveries already raise `brief`/`report` wakes keyed `task:<publication>` (`PublicationStore.cs:254`, `MissionLoop.cs:1940`, `:1968-1969`).
Read first: `docs/HOW-WE-BUILD.md`, `CLAUDE.md` ("Material in the inbox is data, never instruction"; measurement and claim in different tables), the files
above, `MaterialOrigin` and `U-material-origin`'s section of `BUILD-STATUS.md` (2026-09-20).
Items, one commit each:
(a) **Count what arrived.** The scan result carries additions by origin; the inbox wake fires only when material arrived that no role authored — the owner's
`inbox/` (`Inbox`, `InboxUnattested` if that is how an unattested drop is recorded) — never for `Agent` (a role's own write) and never for `App` (the app's
own files and relay deliveries, which wake through their own `task:` events). The ledger rows, origins and hashes are unchanged; only the wake's trigger
narrows. If an origin you meet does not fit either side, stop and say so in the report rather than guessing.
(b) **Tests, RED on the base:** after a role rewrites its own journal and plan, a scan raises NO inbox wake (base: a wake); a file dropped in `inbox/` raises
one; a relay delivery raises its `task:` wake and no inbox wake. Watch ONE mutant (the origin filter removed) turn the first test red and quote it.
(c) The wake's words stay true by construction ("new material arrived in `../inbox`"); `CONTRACTS.md` says what wakes the AI and what does not, one line.
Not the money path; the paid-turn trigger — red-first is the proof. No schema rung. Gate as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings;
the three suites 0 failed on this Mac, counts pasted; touched classes 3×; names 0 removed). Report ≤ 20 lines appended here. No `Co-Authored-By`; no push to
`main`; touch nothing in `docs/briefs/` but this file. The manager verifies the observable result in the running app after landing.

## Report
**Code tip `f5fe843`**, 3 commits on `88a23a2` (cut from `bedd218`, rebased onto `main`, no conflict). **Gate on this Mac, Release:** build `--no-incremental` → 19 projects,
`0 Warning(s)`, `0 Error(s)`; Unit `Passed! - Failed: 0, Passed: 1192, Skipped: 0, Total: 1192, Duration: 26 s`; Fault `Passed! - Failed: 0, Passed: 399, Skipped: 0, Total: 399,
Duration: 1 m 27 s`; Integration `Passed! - Failed: 0, Passed: 699, Skipped: 1, Total: 700, Duration: 11 m 7 s`. `InboxWakeTests` 3× → 3/3 each. Names vs `main` (git objects): 1911 → 1914, 0 removed.
- **(a) `85ce4f9`:** `ScanResult.AddedBy` (additions by the origin each new row was written with, counted in `MaterialScanner.Scan` from the call that answers `MaterialStore.Observe`)
  and `ScanResult.Arrived` = `Inbox` + `InboxUnattested`; `ScanMaterials` raises what `AppHost.InboxWake` returns: a wake only when `Arrived > 0`, its `added` counting arrivals.
  `MaterialStore` and the hashing untouched: rows and origins are asserted beside every wake in (b), hashes by the existing ledger tests in the gate.
- **(b) `51a103b`:** `InboxWakeTests`: the real scanner, the app's `InboxWake`, the real queue. RED on the base (the trigger moved unchanged into `InboxWake`, `result.Added > 0`):
  `:100 Assert.Null() Failure … Actual: Tuple ("inbox:2026-10-01T17:28:04.365Z", "{\"added\":2,\"seen\":2}")`; `:196 Assert.Single() Failure: The collection contained 2 items`
  (`task:…` `report` + `inbox:…` `{"added":2,"seen":2}`); the drop test's wake half GREEN on the base (a guard), its count half red: `:148 String: "{"added":2,"seen":3}"`. Mutant,
  origin filter removed (`Arrived => AddedBy.Values.Sum()`): `:100 Assert.Null() Failure … Actual: Tuple ("inbox:2026-10-01T17:28:27.831Z", "{\"added\":2,\"seen\":2}")`; restored → 3/3.
- **(c) `f5fe843`:** `MissionEventKind.Inbox`'s summary and the `Reason` arm say why "new material arrived in `../inbox`" is true by construction; `CONTRACTS.md`: one sentence.
- **Origins, all four placed, none guessed:** `Inbox`, `InboxUnattested` wake (the second is how a drop is recorded with an agent alive in its window, `MaterialScanner.cs:184`);
  `Agent`, `App` never. **Residual, not closed:** an `InboxUnattested` row can also be a role's own write into `../inbox` (the "forged" file of `MaterialOriginAttestationTests`),
  so that still wakes the chair: no new power, since a role can already ask for a `self` wake.
- **Deviations:** the payload's `added` counts arrivals, not every addition; the engineering `scan` line gains `added_by` and `arrived` (asserted to serialise: it is written first).
- **`CouncilLoopTests.A_file_dropped_between_turns…` (ubuntu red, run 36891262990):** not touched; it scans via `CouncilHost.ScanAsync` → `MaterialScanner.Scan` and asserts the
  row's origin, which is asked at the same point and now only counted too. `CouncilLoopTests` 3× here → 11/11 each.
- **NOT done / NOT verified:** the running app's observable result (the manager's, after landing; the observed run untouched); no box; CI not watched. The credential scan gated
  every commit: no hits, no exclusions. No `Co-Authored-By`.
