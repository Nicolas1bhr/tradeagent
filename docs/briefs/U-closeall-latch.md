# U-closeall-latch — the Close-all wave test holds the wave's calls on a latch instead of sleeping 500 ms per call, so its red means the product left something unsettled, never that the runner was slow
**Protects:** the evidence the money path's disposal contract rests on — an agent's `close-all` that disposal lands in leaves no row DISPATCHING, no handler unfinished, and (the wave having had its time) every
position flat — by making its red a product red; and the honesty rule (a red says what it measured). RIG only: one simulator seam in `src/TradeAgent.Connectors.Fake` (null in the product) and one test; no
gateway, connector or deadline change. Light; seat B; NO rung (main 30). Owed under `docs/HOW-WE-BUILD.md:89-95`: `cd7d9f38`'s windows red stays recorded red until this lands.
**Facts (survey S-closeall-red, `fleet/tmp/B-survey-closeall-red.md`, 2026-10-09; pointers re-checked by seat B at `main` `ef88e145`, 2026-10-11 — unchanged).**
- The test: `GatewayPipeBackpressureTests.A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled` (`tests/TradeAgent.IntegrationTests/GatewayPipeBackpressureTests.cs:1401-1446`). START: `UncancellableLatencyMs = 500`
  (:1411), `Task.Delay(200)` decides where disposal lands (:1413), then `DisposeAsync` and three asserts — no DISPATCHING row (:1416), no `handlers_did_not_finish` (:1417), every broker position flat (:1423).
  MID-WAVE: the same over a fresh book, disposed once a placement reached the broker (:1436-1443). Fixture `ReadyForHandlerTable` (:1482-…, E = 12 s at :1485), shared by :1085 and :1237 — not this unit's.
- Why it goes red on windows-latest: the wave is 1 + 3 + 4×3 = 16 serialised 500 ms waits ≈ 8.0 s plus store and scheduling cost, raced against the product's wall-clock E = 12 s
  (`RiskReducingScope.Begin(EmergencyBudget)`); a leg E cuts is refused before the wire (`not-sent`) or recorded UNKNOWN for the reconciler — never left DISPATCHING. In run 37851666182 :1416 and :1417 held
  and :1423 failed (`P-MES`, `P-YM` open): the third assert measured the runner. Six earlier windows first-attempt reds have the same shape; none had a DISPATCHING row since 2026-09-05.
- Disposal (`Gateway/GatewayPipeServer.cs:3904`): stop accepting, close the agent's connection (its reply is lost by design), wait `HandlerDrainTimeout` for the handlers (:3940), then cancel. The drain derives
  from the declared worst case `W` (`FakeConnector.WorstCaseOperationPath`, `Connectors.Fake/FakeConnector.cs:37`, init-only at :48: default `LatencyMs + UncancellableLatencyMs`).
- The waits today: `if (Faults.UncancellableLatencyMs > 0) await Task.Delay(Faults.UncancellableLatencyMs)` at `FakeConnector.cs:112` and :359, ignoring the token. `FaultProfile` (`FakeBroker.cs:220`) already has a
  `Wait` seam (:300) for a LATE but cancellable wait — not this fault; the two must not be confused (its own comment, :286-299).
Read first: `CLAUDE.md` (the money rules; "Refuse, never guess" is untouched here); `docs/HOW-WE-BUILD.md:89-95`; the survey §§ 2, 5; the test :1384-1460 and its helpers (`Swallow` :1454, `WaitFor` :2007,
`Dispatching`, `ReadEngineering`); `FakeConnector.cs:30-60`, :100-120, :350-365; `FakeBroker.cs:220-300`; `GatewayPipeServer.cs:3904-3960`.
Must NOT: touch `src/TradeAgent.Gateway`, `src/TradeAgent.Core` or any real connector; change E, W's meaning, the drain, any deadline or `Timing` membership; edit `.github/workflows/build.yml` (seat P's tonight);
add a retry, a skip or a `TestTime` scale; weaken, delete or reorder an assert; change `ReadyForHandlerTable` for the other tests; touch the measured `close-all` row's `attempted == 4` (:1290 — a measurement,
the latch does not fit it) or `AgentTreeTests` (the survey's (a), a separate rig red).
Items, one commit each, one-sentence messages:
1. **The hold seam.** `FaultProfile.Hold` (`Func<Task>?`, null = the product's path unchanged): when set, it is awaited where `Task.Delay(UncancellableLatencyMs)` is today (:112, :359) and, like that delay, it ignores the
   token; its doc says what it models (an uninterruptible call whose end the TEST decides) beside `Wait` and `UncancellableLatencyMs`. A unit test pins: null ⇒ the delay path byte-for-byte; set ⇒ the call parks
   until released, a cancelled token does not release it.
2. **The latched wave.** The test parks the wave on the hold instead of sleeping: W declared 500 ms explicitly (a way to set `WorstCaseOperationPath` on this test's connector without changing the fixture for
   :1085/:1237), so the drain stays what it is today (E + 4W + overhead, 15.1 s). START: dispose once the composite's first read is parked (replacing `Task.Delay(200)`); `Assert.False(disposing.IsCompleted)` while
   it is held; release once `sweep` saw the connection close (step 2 done), then let every later call through. MID-WAVE: release calls one at a time until a placement reaches the broker, then the same. :1416,
   :1417, :1423 and :1441-1443 unchanged; the doc comment says what the latch decides and what the wall clock still bounds (the wave's own store work inside E, ≈ 1 s measured on windows).
Red-first (quote each): today's test with `UncancellableLatencyMs = 1000` (16 s of wave against E = 12 s) on this Mac fails :1423 with :1416-1417 green — the CI shape (the survey's prediction, NOT RUN); then the
latched test passes 20× in a row (filter on its name, through `suite.sh`). Mutant, watched red on 3 of 3 runs and quoted: `DisposeAsync` without step 3 (no drain wait, :3940) ⇒ the latched test red at the
`IsCompleted` assert or :1416/:1417; restored after.
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) — Release `--no-incremental` 0 warnings; `GatewayPipeBackpressureTests` and the seam's test class 3×; branch CI on all three platforms (the windows Timing step green
on attempt 1 named in the report); names vs `main` 0 removed (both set sizes); tests box `ready` once or NOT RUN. `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
Not claimed: that E on the wall clock can never cut a wave on a slow enough runner (one clock under the deadline and the drain is a larger, money-path unit); anything about (a) or :1290.

## Report
Tip: see `git log -1` (code tip `650f09fd`; the report commit after it changes only this brief). Seat B's review finding (latch release in a `finally`) is item 3 below; run 38099736941 is superseded.
Gate 1: `dotnet build TradeAgent.sln -c Release --no-incremental` -> 0 Warning(s), 0 Error(s). Gate 4: names.sh main u-closeall-latch -> "removed: 0", sets base 2457 tip 2463 (facts base 2408 tip 2411).
Gate 2: `GatewayPipeBackpressureTests` 3x via suite.sh: Passed 34/34 each (3 m 25-26 s); `FakeConnectorHoldTests` 3x: Passed 3/3 each. The latched test alone, 20x in a row on its full name: 20/20 passed (~350 ms each).
Gate 3: CI run 38100526388 on `650f09fd`, attempt 1: success on all 11 jobs (windows 1-5, macos, ubuntu, linux-host, package x2, shard-plan). `GatewayPipeBackpressureTests` ran in `test (windows-latest, 1)`; its "Test the timing category" step was green on attempt 1.
Item 1 DONE (1b2cc6f3): `FaultProfile.Hold` (Func<Task>?, null = product path), awaited via `FakeConnector.TheUncancellableWait()` at both former `Task.Delay` sites; replaces (not sums with) the delay; ignores the token. Pinned by new `tests/TradeAgent.UnitTests/FakeConnectorHoldTests.cs` (null => delay paid on read and place; set => parks until released, cancelled token does not release it, read and place paths). No Gateway/Core/real-connector change.
Item 2 DONE (4ade082f): the test parks the wave on a `CallLatch`; W declared 500 ms through a new optional `declaredWorstCase` parameter on `ReadyForHandlerTable` (null for every other caller, so :1085/:1237 are unchanged). START: dispose once the first read is parked, `Assert.False(disposing.IsCompleted)` after the connection closed, then ReleaseAll; MID-WAVE: ReleaseOne until a placement reaches the broker, then the same. The flat/DISPATCHING/handlers_did_not_finish asserts are unchanged and in order.
Item 3 DONE (650f09fd, seat B's review): both halves release the latch in a `finally`.
RED-before: the old test with `UncancellableLatencyMs = 1000` on this Mac failed `Assert.DoesNotContain` at :1423 (`P-MES`, `P-YM` open, 12 s), :1416-1417 reached and green (they precede it) - the CI shape. The seam test's red-before is a compile red (no `Hold` before item 1), not a run.
MUTANT (drain wait removed, `GatewayPipeServer.cs` step 3, worktree only): the latched test RED on 3 of 3 runs, at the every-position-flat assert (:1446 after the finally edit), NOT at `IsCompleted` or :1416/:1417 as the brief predicted: with disposal landing at the first read there is no request row yet, so DISPATCHING stays 0 and the book is what shows the wave was cut. Restored with `git checkout -- src`; `git status --short` clean. Mutant was run on the final (finally) test shape.
Tests box: NOT RUN - `win-test.sh ready` said "the machine does not answer (asleep, off Tailscale, or the share was removed)".
Judged by seat B, not a defect: `IsCompleted == false` does not prove disposal reached its drain (a slow runner can only hide a mutant; proving it needs a Gateway seam the brief forbids).
NOT done: no full local Unit/Fault suite (CI's); E on the wall clock can still cut a wave on a slow enough runner (not claimed); (a) AgentTreeTests and :1290 untouched.
