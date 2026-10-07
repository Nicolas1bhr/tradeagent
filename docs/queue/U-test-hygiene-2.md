# U-test-hygiene-2 — main's CI stops going red on the runner's clock: five hosted-runner reds fixed as a class, the simulator's clocks made seams, one atomic ipc token
**Protects:** CI on `main` as a clean signal (windows-only reds in tests no diff reached cost a reading at every landing and a 50-minute re-run on a branch), the simulator's
deadline (MONEY PATH: a connector) and the pipe's shared secret (`IpcToken.Ensure` is an unlocked read-then-write). Light; fresh builder, seat P; no rung. `docs/HOW-WE-BUILD.md`
step 6: `Timing` membership is argued AT THE TEST with measured numbers, never granted to what went red, and no assert is loosened to get in. Prefer a seam (a latch, an injected clock).
**Facts (SOURCE at `3564dee7`, (f) at `1828a188`; read by seat P or its survey; readings in `fleet/ci-ledger.md`).**
- (a) `src/TradeAgent.Security/SecretStore.cs:62-69`: `Ensure` reads, and when absent or short writes a fresh token (`File.WriteAllBytes` `:29`), restricting the mode only AFTER
  (`:30`, `:51-56`); a DPAPI read that throws is regenerated (`:44-47`). Windows run 37443989301: `VenueOpsTests.The_catalogue_read_is_in_the_deadline_table_at_zero` threw IOException
  "being used by another process" — `VenueOpsTests:83`, `DataOpsTests:127`, `CoreTests:265` first-`Ensure` one fresh home in parallel. Product reach (`AppHost.cs:666`, `:900`, `GatewayHost/Program.cs:51`): NOT VERIFIED.
- (b) `tests/TradeAgent.IntegrationTests/CoidWitnessTests.cs:715-737`: `Assert.Equal(5, attempts)` HELD; `clock.ElapsedMilliseconds < 2000` (`:735`) on a Stopwatch went
  red on windows run 37443797669 ("the retry took 2335 ms"), green at `8029b53c` and `003c0a76`. The witness is the MONEY PATH.
- (c) `SweepRequestIdTests.cs:456` (`A_five_order_sweep_carries_a_mix_of_outcomes_in_one_answer`, not `Timing`; `WaveIssueRoom = 750` at `:258`, argued `:246-257`): windows
  run 37494849714 spread the wave past 750 ms (2 legs refused, 1 allowed); the same code green ×3 at `b94fc212` (run 37495039407). The argued numbers did not hold.
- (d) Same file `:306`, `A_sweep_pays_the_emergency_budget_once_not_once_per_rpc` (`Timing`): NullReferenceException at `:333` on macOS runs 37420396771 and 37529866030
  (SECOND sighting) — a clipped book read answers `ok=False` with no Data, and the test casts `Data` without asserting `Ok` (`U-fix-press-budget`'s record).
- (e) `tests/TradeAgent.FaultTests/RiskGateTests.cs:252` reads the loss day by the UTC date while `FakeBroker` stamps with its own `DateTimeOffset.UtcNow` (`src/TradeAgent.Connectors.Fake/FakeBroker.cs:146`,
  `:100`, `:114`, `:126`, `:178`), no seam (latent across 00:00Z). The same split went red: `QuoteClockTests.cs:286-310` (simulator arm), windows run 37521857152, "simulator at 40 s : READY —".
- (f) `EmergencyPressTests.cs:555` (`A_wait_the_simulator_predicted_would_fit_is_still_stopped_by_the_deadline`, `Timing`), windows run 37524459410: "still on the platform 2198 ms after the deadline
  the press itself opened (positions 2198 ms)". `FakeConnector.HonourTheOperationDeadline` (`src/TradeAgent.Connectors.Fake/FakeConnector.cs:174-181`) still PREDICTS: latency > `left` → `await Sleep(left, ct)`
  on the caller's token alone (`:233-234`), so the fixture's late wait (`:566`, +2200 ms) holds the call past the deadline; `TheCancellableWait` (`:209-227`) clips the other branch. Survey RUN: forced 6/6 red, clipped 0/6.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md` step 6; `.github/workflows/build.yml` (`:31`, `:76-94`: how `Timing` runs and retries); each test above and the code it drives.
Must NOT: raise a bound, room, budget, timeout, latency or lateness; add a retry; weaken or delete an assert; skip on a platform; join `Timing` without numbers measured and written at the test; change the
witness's attempts, backoff or budget, or the simulator's fills; read the wall clock in product code for a duration; let a token file exist, even for an instant, readable by another user or half-written.
Items, one commit each, one-sentence messages:
1. **(a) One token, written once:** `Ensure` creates the file atomically (a writer that loses the race returns the winner's token; a reader never sees half a file; on
   non-Windows owner-only from its first byte). RED first: many parallel first `Ensure` calls on a fresh home → one token, every caller holding it, no exception
   (quoted red before). A credential: ONE mutant, the unlocked write restored → red, quoted.
2. **(b) The witness's bound proved where it lives:** the five attempts' sleeps summed on a seam (or an injected clock) and asserted bounded, the count unchanged; the
   Stopwatch upper bound is REPLACED (not dropped) only by a seam assert of the same claim, both quoted. Money path: the new assert RED under an unbounded-backoff mutant.
3. **(c) The wave released by a fact, not a room:** the five legs reach the wire on the fixture's own latch or seam, so the mix of outcomes cannot depend on how a runner
   spreads four issues; or, if the verdict truly needs wall time, `Timing` with numbers measured on windows-latest and why the 750 ms argument failed.
4. **(d) Ok before Data** at `:333`: a clipped read fails in words, never with a NullReferenceException.
5. **(e) The simulator's clock is a seam** (default `DateTimeOffset.UtcNow`, behaviour unchanged); `RiskGateTests` and `QuoteClockTests`' simulator arm pin it to the
   test's clock: RED first with the fill at 23:59:59Z read at 00:00:01Z, and the 40 s quote read after a 15 s stall, quoted.
6. **(f) The simulator's last prediction clipped:** the `wait > left` branch waits on a token cancelled at the deadline, as `TheCancellableWait` does (same sentence, same `PossiblyWritten`; nothing changes
   on time). RED first: the late-wait cancel-all with a counted ~500 ms store call on the press's flow during the orders read (`RiskReducingScope.cs:65-67`), asserting the positions read STARTED inside the
   deadline → "positions ~2200 ms", quoted. Money path: ONE mutant, `await Sleep(left, ct)` restored → red, quoted. `Timing` membership unchanged.
Proof: items 1, 5, 6 RED-before and items 1, 2, 6 mutants, quoted; touched classes 20× locally through `suite.sh`; on CI `fleet/bin/ci-dispatch.sh <WT> 3` (three runs at once on one sha count as three) —
every job green on all three platforms, quoted; each changed assert's before/after quoted in the report.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed; touched classes 3×; branch CI on
all three platforms; tests box or NOT RUN; names vs `main` 0 removed (both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
