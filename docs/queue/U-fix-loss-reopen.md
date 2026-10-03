# U-fix-loss-reopen — a closed day due to reopen did not reopen in a loss-hold test on hosted Windows: find why; if a real gateway can miss it, fix the product
**Protects:** the loss boundary on the money path (gateway): a day closed by a confirmed loss breach reopens only on the receipt the gateway writes after
looking (`LossReopen`), and when it is due it does reopen — a missed reopen keeps refusing the owner's trading (the safe direction, still a defect), and no
fix may reopen anything early (`CLAUDE.md` money rules; `TradingGateway.LossWatchAsync`'s summary). **A FRESH FIXER, seat P, ranked by the orchestrator.**
**Evidence (RUN, `gh run view`, 2026-10-03):** `main` `3b03041` (the U-paper-settle record), run 37098316726, windows-latest job 111132683376:
`LossHoldSurfacesTests.A_release_is_told_with_the_owners_note_quoted` failed [41 s] with "Assert.Single() Failure: The collection was empty" at
`tests/TradeAgent.UnitTests/LossHoldSurfacesTests.cs:79`, called from `:140`; its stdout says "episode-one  closed : [loss_breach:SIM-001:2026-03-10]", so the
breach closed the day; ubuntu-latest and macos-latest green; first sighting. U-paper-settle's own commits touch only the paper connector; this test drives
`TradingGateway` over the Fake connector — a hosted-runner red until shown otherwise.
**Facts to start from (SOURCE at `3b03041`; facts, not a diagnosis):** the test's helpers — `Ready` (`:46-53`: a `TestClock` at noon, `MaxDailyLoss` 1000,
`GatewayOptions.Clock = clock`), `Breach` (`:55-69`: a buy, the price offset −20, two `LossWatchAsync` passes a tick apart, the day closed) and `Held`
(`:72-84`: after episode one, `clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + 1 min)`, then `Assert.Single((await gw.LossWatchAsync())
.Reopened)` at `:79` — empty on Windows); the gateway's `LossWatchAsync` (`src/TradeAgent.Gateway/TradingGateway.cs:3575` on; a closure needs a second,
distinct pull inside `LossBreachConfirmWithin`; a real `Stopwatch` times each reading and "decides nothing"); `LossReopen` (`src/TradeAgent.Gateway/
LossReopen.cs:32` on, `EligibleAt` `:81`). A test that only steps a substituted clock took 41 s on that runner: where the time went is part of item 1.
**A hypothesis to TEST, not a conclusion (the orchestrator, 2026-10-03):** U-runner-forward (`f7f0b09`) moved every order-path age check onto the gateway's
clock — confirm that `LossWatchAsync`'s reopen path and everything it waits on read the same injected clock (`GatewayOptions.Clock`), and whether a wait on
Windows inside the breach or flatten path explains both the 41 s and the missed reopen.
**THE PRODUCT QUESTION, answered with evidence:** can a real gateway, on a real Windows machine, fail to reopen a day that `LossReopen` says is due — a read,
a wait or a real-time measurement that decides more than its summary allows, or an ordering between the watch passes — or is the cause only in the test's harness or
the runner? If the PRODUCT: fix it with a deterministic red-first test (seams or the injected clock; no sleeps waiting for luck) and one watched mutant,
quoted (money path). If the harness or runner: say exactly where it differs from the product's path and why "a due reopen happens" is still proven.
**Reproduce on CI** (`fleet/bin/ci-dispatch.sh`; read the windows job — green on this Mac proves nothing about Windows). Diagnostic runs may narrow
`.github/workflows/build.yml` on your branch (windows only, the class looped) and print the watch passes' outcomes and timings — branch only, removed before the
proving run, each named in the report. The Windows box is NOT granted.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md` (the fresh-fixer rule; `build.yml`'s `Timing` rule); `LossHoldSurfacesTests.cs` (whole); `LossReopen.cs`;
`TradingGateway.cs` `LossWatchAsync` and what it calls; `TestEnv.Ready` and the Fake connector's broker; the other loss-boundary tests (`LossReopen*`, `Loss*`).
Must NOT: loosen any reopen condition (the receipt after looking, the two-pull confirmation, `EligibleAt`); reopen early; add a retry or a sleep or raise a
timeout until it passes; skip it on Windows; weaken an assert; move it into `Timing` unless it truly asserts a deadline the runner must keep, argued at the
test with measured numbers.
Items, one commit each, one-sentence messages:
1. Name the cause with evidence from a Windows run: each watch pass's outcome in `Held`, why `Reopened` was empty, and where the 41 s went.
2. Fix it where it is wrong: product (red-first, deterministic, the mutant quoted, the protected property named and kept) or harness (the reason above).
Done: the cause quoted; for a product fix the red-first test red before and green after and the mutant red; the full workflow green on all three platforms on
the final tip (run id); any narrowed stress run quoted with its count; the gate. Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder
pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit and Fault 0 failed; touched classes 3×; names vs `main` 0 removed (both set sizes
printed); `## Report` ≤ 20 lines appended here. No push to `main`, no merge; touch nothing in `docs/briefs/` but this file.
