# U-codex-2b-fix — the connector's capability read races the heartbeat that now clears the hello

Fresh fixer on Opus, ONE item, on the landing branch `u-codex-2b` in the worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/codex-2b` (rebased onto `main` `15b873d`, tip `5390b74`; do not
rebase again, do not touch any other worktree). Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the `## Report` at the end of
`docs/briefs/U-codex-2b.md` (item 3, F7: a heartbeat that cannot describe the bridge clears what it last said), then
the two places below. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`.

**The failure, in the manager's landing gate at `5390b74`** (Release, full suite; the class alone 3× → 40/40 each):
`BridgeRoundTripTests.A_bridge_that_can_describe_itself_again_is_believed_again` [110 ms] —
`System.NullReferenceException` at `AtasConnector.get_Capabilities()`, `src/TradeAgent.Connectors.Atas/AtasConnector.cs:554`.
The getter reads `_hello` twice: `_hello is null ? … : new ConnectorCapabilities(_hello.IsSimulated, …)`. Since this
unit's F7 fix, a heartbeat that cannot attest clears `_hello` (`Attested(f)` → null at `:962`), so a reader can pass the
null check and dereference the cleared field. Before this unit `_hello`, once set, was never cleared; the race is new.

1. **Every read of `_hello` — and of any other field the heartbeat now clears — takes ONE snapshot** (`var h = _hello;`)
   and decides on that; the field is `volatile` or read under the same lock the writer holds, so a cleared hello reads
   as "cannot attest" and never as a null dereference. Sweep `AtasConnector.cs` for the pattern: `Capabilities`,
   `Bridge`, `StatusDetail`, `IsConnectedAsync`, `Describe`, the health row, anything the Dashboard or the gateway reads
   while the pulse thread writes. RED first: a test that interleaves the clear between the check and the dereference
   deterministically (a seam, or a hammer with a hundred readers against a heartbeat flipping attested/unattested at
   1 ms) and fails with the NRE above on the current tip; GREEN; mutant (the snapshot undone at `Capabilities`) → RED.
2. **Say what a reader sees mid-clear** in one sentence in `docs/CONTRACTS.md`'s capability paragraph: the proof is
   either the last attested description or absent, never a torn read.

Commit on `u-codex-2b`, no trailers. Gate: Release `--no-incremental` → 0 warnings; `BridgeRoundTripTests` 3×; the new
test 20× in a loop; full suite once to a file (start it regardless of other test hosts; a `Category=Timing` failure →
that class alone 3×, quoted); names vs `main` 0 removed.

## Report — append here, commit it, ≤12 lines: tip sha; every read you changed; RED (the NRE reproduced) → GREEN →
mutant, quoted; gate counts; what you did NOT do.

## Report

Fixed. Branch `u-codex-2b`, from `5390b74` (not rebased); nothing pushed, merged, or touched outside this worktree.
**Reads changed.** `_hello` is now `volatile` and every reader takes ONE snapshot: `Capabilities` (six reads — the defect), `StatusDetail` (one snapshot passed down; `UnauthenticatedNow`/`Connecting`/`PendingHello` became parameterised and were three more reads), `Unauthenticated`, and `PeerHasGoneQuiet` (`_lastHeartbeat`, read twice). `Bridge` was already one read. Sibling outside the connector: `AtasHealthReporter.Report` read `atas?.StatusDetail` twice, so the bridge row and the repair offer could be built from two instants — one reading each of `Bridge` and `StatusDetail` per pass now. `docs/CONTRACTS.md`'s F7 paragraph says what a reader sees mid-clear.
**RED at `5390b74`, 6 runs of 6.** New test `No_reader_ever_dereferences_a_hello_the_heartbeat_has_cleared`: 32 reader threads against a stub alternating a whole heartbeat with a bare pulse (~1e9 reads and ~2e5 pulses per 3 s run; a real `BridgeServer` cannot beat faster than `Task.Delay` and landed it only 1 in 6). `torn read : NullReferenceException — at TradeAgent.Connectors.Atas.AtasConnector.get_Capabilities() in .../AtasConnector.cs:line 554`. Mechanism read off the tier-0 disassembly under `DOTNET_JitDisasm=get_Capabilities`: `ldr x0, [x0, #0x48]` reloaded before every one of the five field reads — which is why the class alone passed and the full suite went red.
**GREEN 20/20** in the loop, each run with both readings observed (`provable=` and `not-provable=` both non-zero).
**Mutant** (the snapshot at `Capabilities` undone, six reads restored) → **RED 3/3**: same `NullReferenceException at ...get_Capabilities()`, now `AtasConnector.cs:line 593`.
**Gate.** `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s) 0 Error(s)`. `BridgeRoundTripTests` 3× → `Failed: 0, Passed: 41` each. Full suite in Release, one file, exit 0: Unit `Failed: 0, Passed: 254` + Fault `Failed: 0, Passed: 261` + Integration `Failed: 0, Passed: 610, Skipped: 1` = **1126 total, 1125 passed, 0 failed**. No `Timing` failure, so no re-run was owed.
**Names vs `main`:** 0 removed (894 → 909). Added: my test, `StubBridge.BarePulse` (a harness method the name scan counts, not a test), and 13 from this branch's earlier commits.
**NOT done.** `Bridge` and `StatusDetail` are still two separate readings of the connector, so the health row can pair a description from one instant with a sentence from the next — a display inconsistency on a five-second tick, not an NRE, and closing it needs a combined reading property rather than a snapshot. F7 is untouched: a heartbeat that cannot attest still clears the proof, and both F7 tests are green in every run above. Nothing was run on Windows. No test assertion was loosened or removed.
