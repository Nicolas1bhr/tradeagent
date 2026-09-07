# U-seen-1 — three things the loop's first run on a screen showed (2026-09-07)
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the top block of `docs/RESUME-HERE.md`. Branch `u-seen-1`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-seen-1`, rebased onto `main` first. Every item ships with a test
that was RED before it and one mutant watched going red, quoted. No box, no ATAS, no money, no schema change.

**What happened.** The four units landed on 2026-09-06 were run on this Mac for the first time (a seeded dev home,
the practice simulator, codex 0.153.4). The loop took four turns and the cap paused it at 5.07 USD. Three things
were wrong or wasteful, none of them in the money path:

1. **The AI card says the cap "cannot stop it" before the first start, while the Safety page prices the same runtime.**
   `AppHost.cs:228` wires the meter's probe as `runtimeId: () => Agent.Current?.Id`; `AppHost.cs:106` already falls back
   to `Gateway.Settings.SelectedRuntimeId`. Make the meter fall back the same way. RED: a `TurnMeter` built with no
   running agent and a chosen runtime reports `CanPrice` true and `DashboardView.MissionCost` contains "of" not "cannot
   stop it" (`MissionCostSurfacesTests.cs` pattern). Mutant: the fallback removed.
2. **The built-in simulator's quotes are off the tick grid.** `FakeBroker.BasePrice` yields cents (MES 107.31/107.81
   against a 0.25 grid) and the AI rightly refused them as evidence. `FakeBroker.Quote` snaps the mid to the symbol's
   `TickSize` from the instrument list (`FakeConnector.cs:266-269`; YM's grid is 1), bid and ask one tick either side,
   still deterministic, still never moving. Tests that hard-code the old prices are updated to the snapped values and
   named in the report; nothing is removed. RED: every quote for the four symbols is a multiple of its tick.
3. **The mission does not say what the simulator is, so the AI spent its first turn (5.5 min, 1.48 USD) discovering
   it.** When the connector is the built-in simulator, `WorkspaceBuilder.Instructions` carries one paragraph: its prices
   are fixed fixtures on the tick grid and not a market; use it to prove order mechanics and the ledger, never to find
   an edge or to report a result; the owner's allowlist on the Safety page decides what it may touch. RED
   (`MissionInstructionsTests.cs` pattern): the paragraph present for the fake connector, absent for ATAS. No other
   sentence of the mission changes.

Not this unit: the model the AI runs on (it inherits `~/.codex/config.toml`; here that was `gpt-6-astra`), queued as
`U-model`; anything on the Windows box.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×. Commit per item, one sentence each, no trailers. Append `## Report` (≤20 lines): tip sha,
the gate counts pasted, one line per item with its RED and mutant output, and what you did NOT do.

## Report
Code tip `6406dd9`, this report committed on top; rebased onto `main` `95d6db6` (docs-only, no conflict). The killed
leg's three commits were re-verified here, not taken on trust: every RED and mutant quoted below was run in this session.
Gate (Release, overlapping the `U-loss` leg's own suite on this Mac; no `Timing` failure, so no re-run was owed):
`dotnet build TradeAgent.sln -c Release --no-incremental` → `Build succeeded. 0 Warning(s) 0 Error(s)`; UnitTests
`Failed: 0, Passed: 437`; FaultTests `Failed: 0, Passed: 261`; IntegrationTests `Failed: 0, Passed: 615, Skipped: 1,
Total: 616`. Touched classes 3×: the four unit classes `Failed: 0, Passed: 45` three times, `ApprovalReauthorizationTests`
`Failed: 0, Passed: 30` three times. Test names against `main`: 13 added, 0 removed.
1. DONE `80c849d`. RED (fallback reverted to the pre-fix probe, 4 of 5 red): `Before_the_first_start_the_cap_is_priced_by_the_runtime_the_owner_chose` → `Assert.True() Failure Expected: True Actual: False`; `The_meter_asks_the_same_question_the_safety_page_asks` → `Assert.Contains() Failure: Sub-string not found … Not found: "runtimeId: () => PricedRuntimeId(Agent.Cu"···`. Mutant (precedence inverted): `What_is_actually_running_prices_the_turns_rather_than_what_was_chosen` → `Assert.Equal() Failure: Strings differ Expected: "codex" Actual: "custom"`.
2. DONE `3f64400` + `6406dd9`. RED (the snap reverted, 3 of 5 red): `Every_quoted_price_sits_on_the_instruments_own_tick_grid` → `Assert.Equal() Failure: Values differ Expected: 0 Actual: 0.24`. Mutant (`spread = 0.25m` regardless of the grid): same test → `Expected: 0 Actual: 0.75`.
   No test hard-coded an old price. `BasePrice` has three call sites outside `FakeBroker.cs`, all live reads, all green
   before and after the snap. One was updated and it is the only one:
   `ApprovalReauthorizationTests.G18_the_notional_cap_multiplies_by_contract_size` now reads
   `conn.Broker.Quote("ES", …).Last` — the field the gateway compares the cap against (`TradingGateway.cs:1049`) — from
   the broker in play. Verified it also passed unchanged, so this is clarity, not repair. Left alone and green:
   `The_notional_cap_on_an_approval_multiplies_by_contract_size`, `RiskGateTests.A_notional_cap_that_cannot_be_multiplied_refuses_before_the_wire` (their caps are bands, not equalities).
3. DONE `ed1a181`. RED (the paragraph never emitted): `The_built_in_simulator_is_described_as_a_fixture_and_not_as_a_market` → `Assert.Contains() Failure: Sub-string not found … Not found: "built-in simulator, and it is not a marke"···`. Mutant (condition widened to `c.ConnectorIsPaper`): `Nothing_but_the_built_in_simulator_is_described_that_way` → `Assert.DoesNotContain() Failure: Sub-string found … Found: "not a market"`.
NOT DONE: no Windows box, no ATAS, no real money, no schema change, no UI colour/size/gap touched, `U-model` untouched.
`AppHost`'s `ConnectorIsBuiltInSimulator: Connector.Id == FakeConnector.ConnectorId` — the line that decides item 3
applies at all — has no test of its own, where item 1's wiring is read from source: NOT VERIFIED beyond compiling.
