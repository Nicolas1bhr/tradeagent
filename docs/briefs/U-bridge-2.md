# U-bridge-2 — the bridge half: two claims testable here, four that only the box can settle

Fresh builder on Opus, with the box GRANTED to you alone for items 3–6, but only once `docs/briefs/U-box-precut.md`
carries its report; until then, items 1–2. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the four `IAtasAdapter` rules,
literally), `docs/CONTRACTS.md`, the `## Codex` block and UNVERIFIED 1 and 3 of `docs/REVIEW-2026-09-05b.md`, the U14
sections of `BUILD-STATUS.md`, `tools/README.md` (win-*, atas-gate) and `docs/RESUME-HERE.md`'s box facts and traps
24/35/40/42/43. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/bridge-2`, branch `u-bridge-2` from `main`. `AtasStrategyAdapter.cs`
compiles only on the box (`AtasBridgeBuild=true`); `CoidWitness` and `AdapterTeardown` have extracted tests here.
Credentials are in `~/.tradeagent/win.env`; never print them or a host name. Sim accounts only; stop TradeAgent before a
push; never close ATAS unless capture works; leave the box as you found it, the book flat.

1. **F9 — the witness's `Save` declares success without `Flush(true)`** (`CoidWitness.cs:2431`): `WriteAllText`, rename,
   page-cache readback — an order can be submitted with a write-ahead claim a power loss would not keep. Fix: the stream
   is flushed to the device before the rename and the rename is durable; a test that the flush happens before
   `Submitting` returns true (a seam, or mutant `Flush(true)` → `Flush()` → RED). Testable here.
2. **F14 — `StartBridge` ignores `AdapterTeardown.Started()` returning false** (`AtasStrategyAdapter.cs:457`; the teardown
   half is extracted): a `BridgeServer` starts during Stopping and its witness writes are then refused. Probe on the
   extracted seam: teardown blocked mid-way, `StartBridge`, teardown finishes → no server unless Running. Testable here.
3. **F4 — `Position.Volume`'s sign convention is unproved, and the agent's close chooses Buy/Sell from it** (`:2669`):
   on the box, a known SHORT sim position, an agent close → the order flattens, never doubles. Record the convention in
   `docs/CONTRACTS.md` with the observation.
4. **F11 — `ClosePosition` labels the first newly seen same-symbol order as the close** (`:1863`) without account, side,
   quantity or causal correlation, and writes the press COID onto it: inject an unrelated same-symbol order in the
   window → only the actual closing order is returned and labelled. Correlate on what the adapter itself set.
5. **F8 = UNVERIFIED 1 — `SupportsOrderHistory` from cache presence, the watermark skipped when `ClearCachePeriod <= 0`**
   (`:664`, `:936`): a cold cache missing a known in-window order → `Describe` false, or `GetOrders` refuses until
   coverage reaches the timestamp (rule 2). **6. F10 = UNVERIFIED 3 — the four obsolete synchronous calls** (`:1548`)
   have no deadline on the serial frame loop: stall one behind a shim, issue an emergency → bounded failure, degraded
   health, emergency progress. Each box item: RED on the box first, quoted; then GREEN; then `tools/atas-gate`.

Commit per item, no trailers. Gate here: Release `--no-incremental` → 0 warnings; full suite once (one suite at a time on
this Mac); names vs `main` 0 removed. Gate there: the bridge's Release build with `AtasBridgeBuild=true`, 0 errors, the
warnings counted, and `tools/atas-gate` green on the pushed tree, proven by hash.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant or REFUTED with the outputs, here or
on the box; gate counts both places; the state you left the box in; what you did NOT do.
