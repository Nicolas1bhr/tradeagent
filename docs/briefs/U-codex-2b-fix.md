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
