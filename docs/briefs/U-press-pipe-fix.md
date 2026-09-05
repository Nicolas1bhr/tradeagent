# U-press-pipe-fix — one test from U-pipe-hello meets U-press-atomic's new press ids

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, then the U-pipe-hello and U-press-atomic sections at the
end of `BUILD-STATUS.md` and the `## Report` of `docs/briefs/U-press-atomic.md` (in the worktree below). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. No box.

Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-press-atomic`, branch `u-press-atomic` @ `a30d12b`
(U-press-atomic, complete and rebased over U-pipe-hello, which is on `main`). First `git rebase main` (main has moved by
docs only). You fix the combination; you do not reopen either unit.

**What happened.** Each unit's gate was green alone. At the rebased tip, the manager's gate fails ONE test, at both a
relocated and the normal output path, in 14–93 ms:
`PipeContractTests.An_operator_press_record_is_not_readable_over_the_agent_channel` —
`System.InvalidOperationException: Sequence contains no matching element` at a `.First(predicate)` inside the test.
U-pipe-hello wrote that test against the press id shape of the day, `op-close-<nonce>-<symbol>`; U-press-atomic then
made the press mint `PressLegId(kind, nonce, index)` (`op-close-<nonce>-0`, the symbol on the record, not in the id).
The test's lookup no longer matches any row.

1. Make the test find the press record under the shape the gateway now mints (through `PressLegId`, or by reading the
   press record the gateway created — never by hard-coding a nonce). Its assertion stays exactly what it proves: an
   operator press record is NOT readable over the agent channel, answered as an id nobody minted; and the agent's own
   ids still resolve. RED is the failure above; GREEN; then the mutant U-pipe-hello used (`MayRead → true`) → RED
   again, proving the product half still bites for the new id shape.
2. If the product itself now leaks a new-shaped press id to the agent channel (the mutant would not go red, or a
   probe shows the row readable), say so first and fix `MayRead` for the new shape red-first; do not weaken the test.

Yours: `tests/TradeAgent.IntegrationTests/PipeContractTests.cs`; `GatewayPipeServer.cs` ONLY if item 2 applies (one
predicate). Not yours: `TradingGateway.cs`, the press ids, anything else. Commit per item, no trailers, no push, no
other worktree. Gate: Release `--no-incremental` → 0 warnings; `PipeContractTests` 3×; the full suite once in Release
(if a run ends with exit 1 and NO summary line, another leg's old app runner killed the host — re-run it).

## Report — append as you go, commit it, ≤12 lines: tip sha; RED → GREEN → mutant; whether item 2 applied; the counts;
what you did NOT do. Verified or NOT VERIFIED.

Code tip `62677ca` on `u-press-atomic` (this report commit is the tip), rebased onto `main` @ `540db0e` with no conflicts. One commit, one file, +11/-2. Every line below was run.
**RED at the rebased tip** (Release, `--filter …An_operator_press_record_is_not_readable_over_the_agent_channel`): `System.InvalidOperationException : Sequence contains no matching element` at `PipeContractTests.cs:575`, 398 ms — and the test's own log names the row it walked past: `press rows : op-close-aeba4e025bc0414a-0`.
**GREEN**: `Passed! - Failed: 0, Passed: 1`, 90 ms. The leg id now comes off the record the press handed back — `press.Targets.First(t => t.Target == "ES").RequestId`, with `Assert.Contains(leg, pressIds)` tying it to the unreconciled operator row. No nonce and no id shape is spelled out anywhere in the lookup, so the next shape change cannot make this test report a leak it did not measure. The comparison id moved to the new shape too (`op-close-deadbeefdeadbeef-0`), so the two replies now differ in nothing but whether the row exists.
**The assertion is unchanged and still proves the same thing**: `trade order op-close-b43ace2b92dd47f4-0 -> ok=True data=null` against `an id nobody minted -> ok=True data=null`.
**Mutant** (`MayRead` → `if (true) return true;`) → **RED**, 2 of 34 failed: this test on `Assert.Equal() Failure: Strings differ`, handing the agent `"agent_session_id":"operator"`, `"client_order_id":"TA-op-close-…-0"`, `"connector_order_id":"FB-3"` and `"last_error":"you pressed Close all positions at 11:23; it is waiting for you on the Dashboard"` — UNVERIFIED 6 verbatim, at the new id shape; plus `Another_sessions_minted_leg_does_not_resolve` on `Assert.Null() Failure`. Mutant reverted, `git status --porcelain` empty.
**Item 2 did NOT apply.** The product does not leak a new-shaped press id. `MayRead`'s first clause tests `AgentSessionId == "operator"`, not the id, so the shape change never reached it; `git diff main HEAD -- src/TradeAgent.Gateway/GatewayPipeServer.cs` is empty — the file is identical to `main`.
**Gate, Release:** `dotnet build TradeAgent.sln -c Release --no-incremental` → **0 warnings, 0 errors**. `PipeContractTests` **3× → 34/34 passed** (536 / 353 / 375 ms). Full suite once → Unit **211** + Fault **218** + Integration **570** = **999, 0 failed**. The builder's 965 predates `PipeContractTests` landing on `main`: 536 + 34 = 570.
**Test names vs `main`: 0 removed, 11 added** — the same 11 U-press-atomic reported. Secret scan of my diff: clean, one test file, no host names or credentials.
**NOT DONE / NOT VERIFIED:** no Windows box, no real ATAS, no real money, no UI run, no CI, nothing pushed, no other worktree entered. I did not touch `TradingGateway.cs`, the press ids, `GatewayPipeServer.cs` or any other test, and I did not reopen either unit. The remaining press-row lookups in `FaultTests` all query `request_id LIKE 'op-close-%'`/`'op-cancel-%'` alongside `instrument=`, so none of them spells out the old `-{target}` suffix — verified by grep and by the green suite.
