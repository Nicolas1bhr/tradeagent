# U-inbox-order — an agent's file is never recorded as the owner's: order agent presence against scan passes by something a wall-clock step cannot reorder, and let no scan run beside a launching turn
**Protects:** `CLAUDE.md`'s measurement-vs-claim rule and the inbox as data — `material` is the scanner's measurement, written ONCE, and `Inbox` claims the
ACCOUNT OWNER put a file there, said only across a window with no live agent (`docs/COUNCIL.md` rule 7; `AgentPresence`'s summary: "a wrong 'no agent ran' is
the failure that matters"). A file an agent wrote, recorded as the owner's, is the false claim the ledger exists to prevent. **Seat P, the first of its slots
to free** (the orchestrator, 2026-10-03), from `U-fix-inbox-boundary`'s two read-only findings (its record in `BUILD-STATUS.md`, merge `16af7c4`).
**Today (SOURCE at `84a4664`, read by seat P, NOT runtime-verified — re-check these anchors at dispatch):** an agent's window stamps
`_lastAliveAt = DateTimeOffset.UtcNow` at `Enter` (`Core/AgentPresence.cs:56`) and again at `Window.Dispose` (`:84`); `NoneSince(since)` is `_live == 0 &&
(_lastAliveAt is null || _lastAliveAt < since)` (`:65-67`, strict `<`); a pass's window opens at the previous complete pass's `now = DateTimeOffset.UtcNow`
(`Core/MaterialScanner.cs:129`), read back from `material_scan_at` (`:136`) and stored at the pass's end (`:227`). Both sides are wall-clock reads: a clock
step BACKWARD after a pass stores its `now` and before an agent's window closes leaves the exit stamp earlier than that `now`, and the next pass's `NoneSince`
then says no agent ran across a window an agent ran in — a file that agent wrote reads `Inbox`. Presence is per process; `material_scan_at` is persisted.
**Item 2, today:** `AppHost.ScanMaterials` (`App/AppHost.cs:1088-1095`) runs from the background loop every sixth 5 s pass (`:1002`) and from the Inbox page
(`App/InboxView.cs:160`) without the loop's single-writer guard (`AgentRuntime/MissionLoop.cs:1092` `_passing`; leases refused while it is set, `:1195`;
`PassAsync` `:1235-1241`); only the mission host's own pass goes through it (`AppHost.cs:1446`). So a role can launch beside those two scans, and a sighting
they take while it runs spends the weaker word on an owner's file for good.
**Observable result:** no ordering of wall-clock readings — a backward step, a forward step, equal ticks, or an app restart between an agent's work and a
pass — can make a pass attest a window an agent was alive in; where the app cannot prove the order, it records `InboxUnattested`, never `Inbox`; and every
scan the app runs takes the loop's guard (or one equivalent single-writer guard), so no role launches while any pass is measuring.
**Design constraint (the orchestrator's):** order presence against passes by something a wall-clock step cannot reorder — a monotonic sequence the app
issues, e.g. a ledger row id, or a counter persisted with each window and each pass — and answer "unsure" for a pair it cannot order (for instance across a
restart, where an earlier process's agent may have written after the last pass). Wall time stays on the rows for people to read, never for this decision.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `docs/COUNCIL.md` rule 7; `AgentPresence.cs` (whole); `MaterialScanner.cs:100-240` and
`InboxHoldsUnrecorded`; `MaterialStore.Observe`; `MissionLoop.cs:1080-1260, 1540-1760`; `AppHost.cs:990-1010, 1084-1100, 1440-1450`; `InboxView.cs:150-170`;
the property's tests (`MaterialOriginAttestationTests`, `QuiescenceBarrierTests`, `MissionLoopTests` and `CouncilLoopTests` drop tests, `InboxWakeTests`).
Must NOT: make `NoneSince` (or its replacement) say yes in any case it says no today; weaken "unsure is false" or any assert; remove a pass or a yield; add
a sleep, a retry or a raised timeout; let an agent-reachable surface (the pipe, `state/`, an inbox file) move the sequence — the only way to move it stays
starting a process or running a pass (`AgentPresence`: "there is no setter").
Items, one commit each, one-sentence messages:
1. The order by sequence, not wall time (presence windows and passes, persisted as needed for a restart), with an injected clock seam for the tests.
2. The background pass and the Inbox page's scan take the loop's guard (or one equivalent), refused or deferred while a role launches, never beside it.
Red-first tests, written FIRST and quoted red at base: (a) `A_backward_clock_step_across_an_agents_window_never_records_its_file_as_the_owners` — one
injected clock for presence and scanner, stepped back between a pass and an agent's exit; the agent's file reads `InboxUnattested` (at base: `Inbox`);
(b) a restart between an agent's last write and the next pass records `InboxUnattested`, unless your evidence shows base already does — then a GUARD;
(c) `No_scan_runs_while_a_role_is_launching` — deterministic, a held seam, no sleeps. Mutant to watch red and quote: the comparison back on wall time ⇒ (a) red.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit and
Fault 0 failed; touched classes 3×; names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
