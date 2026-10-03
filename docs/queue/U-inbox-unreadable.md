# U-inbox-unreadable — a material pass that could not read everything is not complete: it does not advance the window, marks nothing missing, and says so
**Protects:** `CLAUDE.md`'s measurement-vs-claim rule and the inbox as data — `Inbox` is said only across a window with no live agent (`docs/COUNCIL.md`
rule 7), and a pass may shorten the next pass's window only if it looked everywhere. Today a pass that could not list a folder still counts as complete, so
the window moves past files it never saw, and a file an agent wrote there can be attested as the OWNER's by a later pass — the dangerous direction.
**Seat P, FIRST in its slot 1 after `U-inbox-order`** (the orchestrator, 2026-10-04; that unit's builder found it, read-only, in its report).
**Today (SOURCE at `4f670f7`, `U-inbox-order`'s code tip — re-check against `main` at dispatch; read by seat P, NOT runtime-verified):**
`MaterialScanner.Walk` (`src/TradeAgent.Core/MaterialScanner.cs:405-413`) calls `Collect` with a local `unreadable` count and DISCARDS it; `Collect`
(`:415-445`) counts a directory whose listing threw `IOException` or `UnauthorizedAccessException` as both `skipped` and `unreadable`; `Scan` (`:141`) sets
`truncated` only when the file budget runs out (`:182`), skips a file whose `FileInfo` throws (`:186-188`), runs `MarkMissing` when the group is
`complete && !truncated` (`:238`) and advances the window — `material_scan_at` and `material_scan_mark` — whenever `!truncated` (`:241-253`).
`InboxHoldsUnrecorded` already treats `unreadable > 0` as "yes, something may be unrecorded" (`:383-386`); `Scan` does not.
**The failure, as a sequence:** an agent alive in window W writes F into `inbox/sub`; pass P1 cannot list `sub`, completes otherwise and advances the window
past W; a later pass, with no agent since P1, can list `sub` and records F as `Inbox` — the owner's. Separately, P1's `MarkMissing` marks every recorded file
under `sub` as removed, a false measurement.
**Observable result:** a pass that could not list a directory, or could not inspect a file it found, is INCOMPLETE: it does not advance either window key,
does not mark anything missing in that group, records what it did see with the words its (unshortened) window allows, and says once — on the activity log,
in plain words naming the folder relative to the workspace, no file contents — that part of the inbox or a home could not be read and will be looked at again;
the line is said when the condition starts and when it clears, not on every tick. A persistently unreadable folder therefore holds the window open and keeps
new inbox files at the weaker word until it is readable again — fail-closed, and visible. Directories skipped ON PURPOSE (noise directories, dot-directories,
past the depth limit) stay "skipped" and do not hold the window.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `docs/COUNCIL.md` rule 7; `MaterialScanner.cs` (whole); `AgentPresence.cs` (the mark); `MaterialStore`
(`Observe`, `MarkMissing`); the attestation tests (`MaterialOriginAttestationTests`, `MaterialPassExclusionTests`); the activity-log writer the scan's callers use
(`AppHost.ScanMaterials`); `BUILD-STATUS.md`'s `U-inbox-order` and `U-fix-inbox-boundary` records.
Must NOT: make any pass say `Inbox` where it says `InboxUnattested` today; let a deliberate skip hold the window (node_modules would hold it for ever); add a
retry, a sleep or a raised limit; change what a complete pass does; print a path outside the workspace or any file's contents; let an agent-reachable surface
(the pipe, `state/`, an inbox file's name or bytes) decide completeness.
Items, one commit each, one-sentence messages:
1. `Walk` returns what it could not read; `Scan` treats any unlisted directory or uninspectable file as incomplete for its group (no `MarkMissing`) and for
   the window (no advance); `ScanResult` carries the count; `InboxHoldsUnrecorded` and `Scan` agree on what "could not read" means.
2. The activity line, said on change only, through the callers' existing log; `docs/CONTRACTS.md` (the material ledger) one sentence.
Red-first tests, written FIRST and quoted red at base, deterministic through a seam on the directory listing (not chmod, which Windows does not honour the same
way): (a) `A_folder_unreadable_during_a_pass_never_lets_an_agents_file_in_it_be_recorded_as_the_owners` — the sequence above ends `InboxUnattested` (at base:
`Inbox`); (b) `A_pass_that_could_not_list_a_folder_marks_nothing_in_it_missing`; (c) a GUARD that a deliberately skipped noise directory still lets the window
advance. Mutant to watch red and quote: `Scan` ignoring the unreadable count again ⇒ (a) red.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit and
Fault 0 failed; touched classes 3×; names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
