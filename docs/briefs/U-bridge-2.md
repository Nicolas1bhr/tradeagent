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
