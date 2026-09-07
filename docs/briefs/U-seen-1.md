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
