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
