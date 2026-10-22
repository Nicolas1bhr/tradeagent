# U-org-watcher-b — the owner-only Oversight page, the immediate in-app alert for a severe finding, the oversight cap beside the organisation's, and a structured record of every refusal at the pipe
**Arrow closed:** ORGANISATION § 9 (:279-298 — "asks the watcher a question in its owner-only window"; "an immediate in-app alert for a severe detector"; "its spend has its own owner-set oversight cap
BESIDE, never inside"), § 13 items 4, 10, 11 (:362-374); ORCHESTRATION § 2.15 (:385-390, "The watcher's page is his alone"), § 5's invisibility row (:425-441), § 8 (:459-479); § 16's 2026-10-02 answer
(:552-554, code checks only). Survey `fleet/tmp/A-org/org-watcher/survey.md` (main `1f3d2e17`, stale where this differs); part a: `git show 52a09706^:docs/briefs/U-org-watcher-a.md`, `fleet/records/U-org-watcher-a.md`.
**Protections — AUTH-001 (in-process only: no op, verb, pipe field or CLI verb reaches the page, the cap or an acknowledgement), BUDGET-001 (the cap is the owner's, beside the organisation's, never read
by `AiAttemptStore.Begin` (:407) or any agent-reachable total), invisibility (part a's guards stay green and grow), measurement vs claim (no agent-supplied string reaches the page or digest), two presses
(raising the cap), append-only (an acknowledgement is a row), recovery (a crash between rung 2's statements and its stamp reopens), no terminal, `Theme.cs` only: red-first and ONE watched mutant.**
Seat A; **opus/high**, a protection unit. **No rung of `tradeagent.db`** (schema stays 44): rung 2 of `oversight.db`'s own ladder.
**Depends: U-org-watcher-a (LANDED 52a09706), O5b `U-org-decisions` (LANDED e94ee00d, rung 41)** — item 4's "after O5b" is met: O5b's eight ops reach the same handler.
**Today (main 3d2196c0):** findings and digests sit in `oversight.db` (`OversightStore.cs`: `Schema = 1` :81, `Migrate` :156, `if (have < 1)` :160 — rung 2 free) and nothing shows them; the line is
AppHost's private `_oversight` (`AppHost.cs:50`, :1077-1078, :2540), its store private in `OversightLine` (`OversightSweep.cs:128`; `Refusal` "for the owner's page" :125) — the page needs one App-internal
accessor. `MainWindow`: `enum Page` :39, nav :400-412, `BuildPages` :467-492, shell rows :57 and the strips' order :244-249, `UpdateShell` (5 s) :507; `BuildApprovalBanner` :317-336 (refreshed :533-538)
is the alert's model; `ReportWords`/`ReportPage` (`ReportView.cs:16`, :38, `Words` :170) split words from controls. Cap models: `PerceptionCard.BuildSaveBudget` (:493, `Errors.cs:556-560`) and
`SafetyPage.BuildSaveDailyCap` (`DashboardView.cs:1334-1357`, disarmed on edit; `Errors.cs:269`). `Handle` (`GatewayPipeServer.cs:1241`) is the one handler the read loop (:893), `CallAsync` (:1200) and
the door's `CallAsGrantAsync` (:1227) reach; the role gate leaves an `activity` TEXT line only (:1300-1319), other refusals no row (`catch` :1408-1416). Part a's `refusals` reads only
`OrgSnapshot.PipeRefusalEvents` (`OrgSnapshot.cs:61`, four events all written before `Handle`), its BLIND sentence naming this gap (`Detectors.cs:254-255`). `LogStore.Rotate` prunes (`Stores.cs:570`).
**Observable result:** a nav entry "Oversight" (the owner's word — never shown to an agent) opens a page, built once and updated in place: the latest digest; findings newest first by unit with cited ids,
measured values and BLIND sentence; the line's failures and `Refusal`; each seat class's invisibility row (ORCH § 5: advisory on unconfined CLI, hard on app paths, mediated broker-only, hard in cells;
"the public repository names this line"); the thresholds (`OversightPolicy`); the cap ("no model layer — code checks only; this line spends nothing today"). A severe finding raises a banner in the
chrome within one sweep (60 s + the 5 s refresh — said so); "Open" jumps to the page; "Acknowledge" appends a row.
Items, one commit each, one-sentence messages:
1. **Rung 2 of the oversight ladder** (`Schema = 2`, `if (have < 2)`, one transaction with its version row): `oversight_cap` (append-only: amount decimal TEXT, currency, set_at — the latest row is the
   cap; none = 0) and `oversight_ack` (finding_id, at); both refuse UPDATE/DELETE by trigger (part a's pattern, :207-225).
2. **The page (App):** `OversightWords` (pure, tested without Avalonia) + `OversightPage` (controls), `Page.Oversight`; it reads `oversight.db` only, through the line's store (an App-internal accessor
   on `AppHost`); every figure from `Theme.cs`; no press removes anything.
3. **The cap's press:** `Labels.SaveOversightCap`; raising is two presses naming the figure (the perception budget's rule, disarmed when the box changes), lowering or zero one press; read by nothing
   but this page until `U-org-watcher-model` — said on the page.
4. **The refusal record at `Handle` — seat A RULED (b), 2026-10-22:** no closed `ErrorCode` set exists — every organisation-verb refusal (`OrgVerbCaller`, `GatewayPipeServer.Mandates.cs:24-45`;
   `OrgStore`'s `Refused`, `OrgStore.Mandates.cs:1102`) is `INVALID_REQUEST`, a malformed frame's code. Build (b): a refusal MARKER carried in-process on the exception (or result) at every rights/
   authority refusal site, read at `Handle`'s one choke point; NO wire change — every answer stays byte-identical (part a's invisibility guards prove it), no new `ErrorCode`, a malformed frame is not a refusal. Then `engineering_log` `Ipc`/`op_refused`, metadata {op — a known `Ops` const or
   "unknown"; code; role; attempt}, no payload or request text; `op_refused` joins `PipeRefusalEvents`, scoped by its attempt's unit (`Detectors.cs:258-263`), the BLIND gap closed; info unless ruled severe.
5. **The banner:** `BuildOversightBanner` in its own shell row AFTER the approval banner (money first), shown while an unacknowledged severe finding exists; its sentence is `DigestComposer.Sentence`
   (`Digest.cs:46`), cited; never a toast, sound or OS notification.
Red-first (each red at base quoted): (a) `OversightWords_show_the_digest_findings_blind_lines_and_the_seat_class_table`; (b) `A_severe_finding_raises_the_banner_and_an_acknowledgement_lowers_it_without_deleting`;
(c) `Raising_the_oversight_cap_takes_two_presses_and_lowering_one`; (d) `The_cap_is_read_by_no_admission_gate_and_no_agent_reachable_total` (GUARD: `OversightInvisibilityTests`'
`No_agent_reachable_surface_answers_differently_when_the_line_has_run` :118 and `Every_total_an_agent_can_read_is_on_the_enumerated_list` :241 re-run with a cap and an ack set);
(e) `Every_refusal_at_the_pipe_leaves_one_structured_row` (red: a Research `buy`; an unconfined attempt's `mandate-assign`; by the read loop AND the door); (e1) GUARD: an ended grant keeps its ONE
`grant_rejected` row (:875, green at base); (e2) `The_refusal_row_holds_no_request_payload` (GUARD, an unknown op's name included); (f) `No_agent_reachable_assembly_or_source_names_the_line` (:264)
extended with the page's and the cap's names; (g) `The_page_rebuilds_nothing_on_refresh`; (h) `A_crash_between_rung_two_and_its_stamp_reopens` + a rung-1 file climbs.
**The watched mutant:** the cap's raise armed with one press (`ConfirmIf`'s sentence returning null) ⇒ (c) red. Quoted red, restored, green.
**Evidence beyond CI:** the macOS UI loop (`tools/mac-run.sh` + `mac-shot.sh`; one severe and two info findings; page and banner, light and dark) HELD while the owner's observed run is live on this
Mac — the seat says when, else NOT RUN with the reason; Windows UI NOT RUN unless granted. No model, no paid run.
Out of scope: layer 2 and asking the line in words (`U-org-watcher-model`); department scopes to their managers (`U-org-audit`); detectors needing O6/O8/audit (`U-org-watcher-c`).
**Conflicts:** `U-execution-environment` (seat A, in flight): `AppHost.cs` (its hunks :502, :1122-1146) and `Canon.cs` (read by guard (f)) — whichever lands second rebases and re-runs (f). Seat B in
flight: `U-org-packets-b` (`AppHost.cs`, `Errors.cs`), `U-rewake-floor` (`AppHost.cs`, `OrgStore.Mandates.cs`) — whichever lands second rebases. O7 `U-org-surface` (CARD): no link here.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; names vs `main` 0 removed; tests box line). Codex review:
write its prompt — "re-review HELD for the observed run" while it is live. `## Report` ≤ 20 lines appended here.
Re-checked 2026-10-22 against 3d2196c0:
- O5b LANDED (e94ee00d); U-org-lanes, U-thinking-seats-c landed — out of Conflicts; U-execution-environment, U-org-packets-b, U-rewake-floor in.
- Item 4: no closed `ErrorCode` set separates org-verb refusals from malformed requests (all `INVALID_REQUEST`) — a seam for the seat; `op` recorded only as a known const.
- Item 4: `op_refused` must join `OrgSnapshot.PipeRefusalEvents` — "no new detector" holds, but part a's snapshot, scoping and BLIND sentence change.
- Test (e)'s ended grant is GREEN at base (`grant_rejected` :875, before `Handle`) — now guard (e1); the door path added to (e).
- Pointers moved (`BuildPages` :467-492, role gate :1300-1319); the page needs an accessor (`_oversight`, the store private); "at once" → within one sweep.
- Cap model: the perception budget's box-bound press beside `Errors.cs:269`; (h) crash-before-stamp added (CLAUDE.md recovery).
- Guards cited by their landed names; the mac UI loop and the Codex re-review HELD while the observed run is live.

## Report
Code tip 38c69d54 (rebased onto efd8e399; the CI run and the names check cover this sha). Gate: build Release --no-incremental "0 Warning(s) 0 Error(s)"; OversightTests+OversightPageTests "Passed: 28" ×3, OversightInvisibilityTests+PipeRefusalRecordTests "Passed: 9" ×3; CI 39740011002 success on every job (windows ×5, macos, ubuntu, package, package-linux, linux-host); names "sets: base 3077 tip 3088 / removed: 0".
1 rung 2 (oversight_cap, oversight_ack; append-only by trigger; one transaction with its stamp; internal ladder seam) — done. RED (h): CS1739 no 'climbTo' / CS1061 no 'Cap'.
2 page: OversightWords (pure) + OversightPage (built once, cards kept by id), Page.Oversight nav, root scope only, App-internal accessors on AppHost — done. RED (a)(g): CS0246 'OversightSources' not found.
3 cap press: Labels.SaveOversightCap; raising takes two presses naming the figure and is disarmed by an edit; lowering or zero takes one press; read only by the page — done. RED (c): CS1061 no 'CapBox'. (d) guard: the differential re-run with a cap and an ack; only OversightStore.cs and OversightView.cs name the cap.
MUTANT (c): the armed sentence `() => null` ⇒ "Assert.Null() Failure: Value is not null / Actual: OversightCap { Amount = 3 …}" — restored, green.
4 refusal record (ruling b): RefusalMark (Exception.Data, in process) on OrgVerbCaller ×3, OrgStore.Refused, ledger author-rights ×3, mandate/decision show-of-another's; the role gate is recorded directly; read at Handle → engineering_log Ipc/op_refused {op|"unknown", code, role, attempt}, no payload, info; joins PipeRefusalEvents, scoped by the attempt's unit; template refusals/op; BLIND gap closed — done. RED (e): "Expected: 4 Actual: 0"; (e2): "Assert.Single() … empty"; (e3): "did not contain any matching items"; (e1) green at base.
DEVIATION 4: the gateway's own rule refusals (mode, limits, allocation, loss budget, expired approval/decision, holdout) are recognised at Handle by a closed code list (RuleRefusals) instead of ~30 marks in TradingGateway, because no malformed frame uses those codes. APPROVAL_REQUIRED is excluded. Pinned template count 10→11 and rung-1 schema assert → Schema: both changed on purpose.
5 banner: its own shell row after the approval banner; the newest unacknowledged severe finding's DigestComposer.Sentence; Open goes to the page; Acknowledge appends rows only for the findings it counted — done. RED (b): CS0246 'OversightBanner' not found.
Part a's invisibility guards are unchanged and green, with no wire change; guard (f) is extended with the page, cap and banner names.
Tests box (run 20261022-092021-6065718d, on 6065718d — one commit before the tip; the last commit only changes a catch clause and a comment): GREEN — Unit 2090/0 failed, Integration 737/0, Fault 498/0, timing 6+91+20/0; his files unchanged.
review HELD for the observed run — prompt written (fleet/tmp/A-org/u-org-watcher-b-build/U-org-watcher-b-review-prompt.md).
NOT done or verified: UI NOT VERIFIED visually (the observed run is live) — no mac-run/mac-shot and no Windows UI. No local full suite; no model call.
