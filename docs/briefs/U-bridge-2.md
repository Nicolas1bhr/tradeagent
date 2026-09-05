# U-bridge-2 — the bridge half: the press answers inside its budget and names its own order; four bridge claims settled

Fresh builder on Opus, with the box GRANTED to you alone from the start (`U-box-precut` has reported: its section is at
the end of `BUILD-STATUS.md`). Box items FIRST (3–6), then 1–2 here, then the gate. Read `docs/HOW-WE-BUILD.md`,
`CLAUDE.md` (the four `IAtasAdapter` rules, literally), `docs/CONTRACTS.md`, the `## Codex` block and UNVERIFIED 1 and 3
of `docs/REVIEW-2026-09-05b.md`, `tools/README.md`, and `docs/RESUME-HERE.md`'s box facts and traps 24/35/40/42/43.
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/bridge-2`, branch `u-bridge-2` from `main`. `AtasStrategyAdapter.cs`
compiles only on the box (`AtasBridgeBuild=true`); `CoidWitness` and `AdapterTeardown` have extracted tests here.
Credentials are in `~/.tradeagent/win.env`; never print them or a host name. Sim accounts only; stop TradeAgent before a
push; never close ATAS unless capture works; leave the box as you found it, the book flat. Its app is PAUSED with 2
unconfirmed requests (one predates the session, one is item 3's press): settle them only from the platform's own answer.

1. **F9 — the witness's `Save` declares success without `Flush(true)`** (`CoidWitness.cs:2431`): flush to the device
   before the rename; a test that the flush precedes `Submitting` returning true (seam or mutant → RED).
2. **F14 — `StartBridge` ignores `AdapterTeardown.Started()` returning false** (`AtasStrategyAdapter.cs:457`, the
   extracted teardown): teardown blocked mid-way, `StartBridge`, teardown finishes → no server unless Running.
3. **The emergency close settles UNKNOWN by construction, measured on the box:** a Close All whose close FILLED in 341 ms
   was recorded `'close' is NOT confirmed … The bridge is busy`, because the bridge's `WaitFor(AckTimeout = 3 s)` outlasts
   the connector's 2 s `EmergencyDeadline`, and because `ClosePosition` never offers our id to ATAS — ATAS builds the
   close order itself (`Comment` = "Close position"; ours goes only into an EMPTY comment), so the bridge cannot correlate
   the order it caused. Codex F11 (`:1863`, a bare same-symbol match labelled the close) is the same defect. One class
   fix: the adapter identifies the order it caused by what ATAS lets it set or by a tight causal window over (account,
   symbol, side, quantity, time), never a bare symbol, and answers inside the emergency budget; an unrelated same-symbol
   order in the window is not labelled. RED on the box (UNKNOWN, quoted) → GREEN (a filled close reads `confirmed`).
4. **F4 — `Position.Volume`'s sign convention** (`:2669`): a known SHORT sim position, an agent close → flattens, never
   doubles; record the convention in `docs/CONTRACTS.md` with the observation.
5. **F8 = UNVERIFIED 1 — `SupportsOrderHistory` from cache presence, the watermark skipped when `ClearCachePeriod <= 0`**
   (`:664`, `:936`): a cold cache missing a known in-window order → `Describe` false, or `GetOrders` refuses (rule 2).
6. **F10 = UNVERIFIED 3 — the four obsolete synchronous calls have no deadline** (`:1548`): stall one behind a shim,
   issue an emergency → bounded failure, degraded health, emergency progress. Then U9: apply the report's dispositions
   (`NoWarn` MSB3277 in the bridge csproj naming the vendor; `#pragma warning disable CS0618` at the four call sites with
   the trap-25 reason) and turn `TreatWarningsAsErrors` on for the bridge: its Release build on the box 0 warnings.

Commit per item, no trailers. Gate here: Release `--no-incremental` → 0 warnings; full suite once (one at a time on this
Mac); names vs `main` 0 removed. There: the bridge's Release build, `AtasBridgeBuild=true`, 0 warnings, 0 errors;
`tools/atas-gate` green on the pushed tree, proven by hash.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant or REFUTED with the outputs, here or
on the box; gate counts both places; the state you left the box in; what you did NOT do.

## Report

Code tip `7e62b73` (rebased onto `main`; 8 commits, this report the ninth and the branch tip). Pushed tree proven by hash both sides: **162 files,
`b9bc86c52936f0e2794d2f66a765cc1c09ae0d59cac8df067d01cd63a1bfb5d3`** (src+tests+tools+packaging, sha256 of the sorted
per-file digest list). **Kept all five inherited commits**; changed three things: `WaitFor`'s `<summary>` had been stranded
on `BoundedCall` (moved back), `AdapterTeardown.Started()` had a second copy of the transition (`=> Start(() => { })`), and
item 3 needed the fill (below).
1. **F9 flush.** Mutant (flush deleted) → RED `Expected ["write","flush(flushToDisk:true)","rename","Submitting returned true"] / Actual ["write","rename","Submitting returned true"]`; restored → 167/167 green.
2. **F14 mid-teardown start.** Mutant (the `Stopping` refusal deleted) → RED `a start mid-teardown was allowed`; restored → green.
3. **The emergency close.** RED, same box, `U-box-precut`: `'close' is NOT confirmed … The bridge is busy` (caller gave up at 2.0 s inside the bridge's 3 s wait). With commit `05828f5` it answered inside the budget but read `0 of the 0 order(s) ATAS added match Buy 1 BTCUSDT on CRYPTO5EB41` — **a filled market close is in NONE of the three order collections** (`ITradingManager.Orders`/`ChartStrategy.Orders`/`Connector.Orders`); its fill is in `MyTrades` carrying `MyTrade.Order`. Causal window widened to fills, terms unchanged → GREEN on the box: `the platform filled it; BTCUSDT is now flat`, `TradeAgent thinks: filled`, `Broker reference: 12063701` = `trade executions`' `connector_order_id 12063701, Buy 1 BTCUSDT @ 79747.7`; position `0`. Mutant A (bare same-symbol) → gate RED, a stranger's `Buy 7 ES on SOMEBODY-ELSE` returned as the close and labelled `TA-CLOSE-STRANGER`.
4. **`Position.Volume` sign.** Re-measured 2026-09-06: ATAS's own dialog `BTCUSDT Perpetual · Sell/Short · Market · 1 Lots` → `quantity: -1, average_price: 79739.1`; the press bought 1 and left `0` — flattened, never doubled. In `docs/CONTRACTS.md` with the fill reading.
5. **F8 order history.** Mutant B, the pre-fix shape (presence alone + the watermark skipped when retention is unstated) → gate RED `SupportsOrderHistory=True, GetOrders → 0 order(s)` against a cold cache. (My first attempt at B did NOT go red: with retention zero the remaining branch refuses anyway — the fix is doubly guarded.) GREEN both ways: unstated → refused, `keeps=30.00:00:00` → answered inside, refused outside.
6. **F10 + U9.** Mutant C (deadline deleted) → gate RED `still running after 12005 ms — OpenOrder is still inside ATAS`; GREEN `5021 ms → AtasCallTimeoutException`, `calls=stalled(OpenOrder@5000ms)`, and the close-all behind it still reached ATAS in 1235 ms. Mutant D (the four `#pragma`s deleted) → **4 × `error CS0618`** under `TreatWarningsAsErrors`; overriding `MSBuildWarningsAsMessages` brings MSB3277 back. Both dispositions are load-bearing.
**Gate — here:** Release `--no-incremental` → 0 warnings, 0 errors; full suite Release → Unit 219 + Fault 250 + Integration 587 = **1056, 0 failed**; test names vs `main` → **0 removed**, 3 added. **There:** bridge Release `-p:AtasBridgeBuild=true --no-incremental` → **0 warnings, 0 errors**; `tools/atas-gate` → **23 checks, GATE PASSED** on the proven tree.
**Box found:** reachable, console session active, ATAS 8.0.14.397 running, capture WORKS — but TradeAgent **stopped** (`trade status` → `IPC_UNAVAILABLE`), `C:\ta\repo\src` pushed and its Release `TradeAgent.exe` missing: killed between a push and a rebuild. The store held **0** unconfirmed requests, not the 2 the record expects. **Box left:** my tip build running, ATAS up, the bridge started at protocol 3 (`connected · bridge 8.0.14, protocol 3`), every health row READY, book flat (`quantity: 0`), `0` open / `0` unreconciled — the three press records I made were settled from ATAS's own `executions`, quoted in each note. Mode `LIVE_CONFIRM`, live not activated, kill switch untouched.
**NOT done:** no installer, no release, no update of the installed 0.1.1; ATAS 8.0.14.398 declined again; nothing pushed or merged. `SupportsClientOrderId`/`SupportsOrderHistory` stay false with no broker, so `ReconciliationProvable` is false and **every** emergency press is still flagged for a human by design — that is what the residual "1 of 1 record(s) … waiting for you" banner is, not the close being unidentified. The gate proves the deadline and the degraded surface, not the frame loop's serialisation: it has no real `BridgeServer`.
