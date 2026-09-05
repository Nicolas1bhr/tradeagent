# U-press-atomic — one emergency press at a time, and a replay bound to the verb and session that made it

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md`, the U2c1b section of
`BUILD-STATUS.md`, then in `docs/REVIEW-2026-09-05.md`: findings 2 and 4 and UNVERIFIED 5 (EXECUTED — probes P10 and P2
on `review-probes`, lift them) and Codex F7 (read-only: turn it RED first or refute it). `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. No box. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u-press-atomic`, new branch `u-press-atomic` from `main` — after
U-gates has landed (both touch `TradingGateway.cs`; rebase onto that `main`).

1. **The second-press refusal and the first press row are one step (finding 2 = F6, `TradingGateway.cs` ~:1634).** The
   refusal (`RefuseWhileAPressIsOpen`) and the first durable press row are not atomic; P10 reversed a position (long 2
   → short 2, both presses "ok"). RED first, with a barrier: two `OperatorCloseAllAsync` calls released together right after
   the check, both seeing the same open position → expect exactly ONE set of wire calls and one press, the other
   refused with "close-all sent at HH:MM; resolve it first". Fix: the check and the first write are one transaction or
   one lock held across both, and the press row's key is unique per open press. Other direction: a press after the
   previous one is resolved still goes to the wire.
2. **A replay is bound to its verb and session, and looked up before any live read (F7, ~:1355).** A composite's outer
   request id is not bound to the operation and session that created it, and the replay lookup happens after live
   prerequisite reads, so an incomplete Close All composite replayed as Cancel All resumes the wrong verb, and an
   offline replay is not safe. RED first: seed an incomplete Close All composite, replay its id as Cancel All while the
   first book read is empty → expect refusal (wrong verb), zero wire calls; and replay the right verb with the connector
   unreachable → expect the stored outcome with no read attempted. Fix: the composite row carries verb and session;
   the lookup is the first step of every mutating op; a mismatch refuses with INVALID_REQUEST in the replay contract's
   words; `docs/CONTRACTS.md` and `AGENTS.md` state the binding.
3. **The press mints its own id (finding 4, P2).** `rid = $"op-close-{nonce}-{symbol}"` puts a broker symbol into the
   rule-1 client order id: `TA-op-close-…-ES 12-25 [CME Globex Futures]` (58 chars, ' []' outside `[A-Za-z0-9-]`) and a
   65-char id for MES. Fix: the id is minted from the nonce and an index as the agent path's `op-{nonce}-{intent}-{index}`
   is, the symbol lives on the record; the pipe's charset and 61-char budget hold for every id the gateway sends. Also
   UNVERIFIED 5: `OpenPressRow` latches before `TryCreate`; if the create throws the latch names a row that does not
   exist and only a restart clears it — order the two so a latch always has its row.

Yours: `src/TradeAgent.Gateway/TradingGateway.cs` (press and composite regions), `GatewayTypes.cs`, `Core/Db/Stores.cs`
and the schema (bump if a column is added; migrations must be checksummed as the existing ones are), `Errors.cs`,
`docs/CONTRACTS.md`, `AGENTS.md`, tests. Not yours: the pipe server, the updater, the connectors, the authorization
path (U-gates). Every item: RED quoted (or refuted, with the probe), GREEN, one mutant watched red (commit before
mutating; `cp` restore; `touch`). Names vs baseline: nothing removed. Commit per item, no trailers, no push, no other
worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report

Gate run at `052a249` (the last code commit; this report commit is the tip of `u-press-atomic`). 7 commits, 12 files, +956/-59, rebased onto `main` @ `421b5d8`. Every line below was run.
**1 (finding 2 / F6) VERIFIED.** RED `PressAtomicityTests` (barrier: both presses held inside the capture read, released together) reproduced P10 — `press A : ok`, `press B : ok`, `close calls on the wire : 2`, `position after : ES -2`, two press rows. GREEN — `press A : EMERGENCY_PRESS_UNRESOLVED — close-all sent at 09:56; resolve it first`, `close calls : 1`, `position after : flat`, one row; the other direction (resolve, press again) sends. Fix: `ExecutionRequestStore.TryCreateFlagged` writes the flag AND `NOT EXISTS(a flagged row of this control)` in one INSERT, so check and first row are one statement and hold across the two processes that reach the button. Mutant (exclusion clause → `$claim IS NOT NULL`) → red, `Expected: 1 Actual: 2`.
**2 (Codex F7) VERIFIED, one half NOT DONE.** RED (4, `CompositeReplayBindingTests`): `cancel-all with the same id : ACCEPTED — {"op":"close-all","legs":1,"targets":["ES"]}`; `session b replaying session a's id : ACCEPTED, no refusal`; the offline replay threw `ConnectorTransportException : simulator is disconnected`. GREEN: `INVALID_REQUEST — request id 'cr-1' already names a 'close-all'`, `wire calls during the replay : 0`, `leg records under the replay's nonce : 0`, `position reads attempted during the replay : 0`. Over the REAL pipe (`CompositeVerbBindingTests`): close-all `cv-1` ok, cancel-all `cv-1` → `ok=False {"code":"INVALID_REQUEST"…}`, `orders at the broker : FB-1 WORKING`. Fix: `ReplayOf` checks `op` and `agent_session_id` (existing columns, no schema change); `BeginCompositeAsync` takes the capture as a delegate and never runs it on a replay. Mutants: verb check compares `stored.Op` to itself → 3 red; lookup moved back after the read → 1 red.
**NOT DONE:** `GatewayPipeServer.CancelAll` (~:888) and `CloseAll` (~:1399) still read the book before calling the sync `BeginComposite`, so an agent's OFFLINE replay still fails on the read. The verb/session binding does reach them (proved over the pipe). Adopting `BeginCompositeAsync` is one call-site change each; that file is `u-pipe-hello`'s and I was told not to edit it.
**3 (finding 4 / P2 + UNVERIFIED 5) VERIFIED.** RED: `TA-op-close-210936ccdbc24e0c-ES 12-25 [CME Globex Futures]`, `length : 58`, `characters outside [A-Za-z0-9-] : ' []'`; MES `length : 65` (ceiling 64); cancel-all had the same defect with a BROKER ORDER ID (`op-cancel-…-FB-1`), not measured by P2. UNVERIFIED 5 reproduced with the press row's insert failing: `press rows written : 0`, `unresolved press : none`, `reconcile clean : False`, `trading authorized after one reconcile pass : False` — a pause held by a latch naming a row that does not exist. GREEN: `TA-op-close-18f45b889ac34b9e-0`, `length : 30`, `characters outside : ''`, `record : op-close-… instrument=ES 12-25 [CME Globex Futures]`; the latch case reconciles clean and trading resumes. Fix: `PressLegId(kind, nonce, index)` both controls, target stays on the record; `TradingGateway.MaxClientOrderIdChars`/`MaxRequestIdChars`/`IsSendableId`, and `OpenPressRow` refuses an id that breaks them; a reflection test pins the budget to `GatewayPipeServer`'s private one (`61 == 61`). Mutants: target back in the leg id → 2 red (`'op-close-…-ES 12-25 [CME Globex Futures]' is not an id this gateway may put on a broker order`); latch back before the create → 1 red.
**Gate at the tip, Release:** `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings, 0 errors. Full suite → Unit 211 + Fault 218 + Integration 536 = **965, 0 failed**. Test names vs `main`: **0 removed, 11 added**. Secret scan of the whole diff: clean. `docs/CONTRACTS.md` states the atomic claim, the leg-id shape and the replay binding; the `AGENTS.md` template states the binding to the agent.
**Side effect for `U-press-budget`:** the press row is now ONE insert instead of insert + `MarkNeedsReconciliation`, so `OpenPressRow` makes one fewer database write per leg inside the 2 s emergency budget.
**NOT DONE / NOT VERIFIED:** no Windows box, no real ATAS, no real money, no UI run (the Dashboard press card compiles and its `IsPressRecord`/`PressKindOf`/`TargetOf` inputs are unchanged, but I did not run the app); whether ATAS accepts the id shape at all is still box-only (CONTRACTS.md keeps the 64-char ceiling marked a guess); no CI run; nothing pushed; no other worktree entered; `GatewayPipeServer.cs`, the updater, the connectors and the authorization path untouched.
