# U-test-hygiene-2 — main's CI stops going red on the runner's clock: four hosted-runner reds fixed as a class, a UTC-day seam, one atomic ipc token
**Protects:** CI on `main` as a clean signal (three of the last twelve `main` runs went red on windows-latest only, in tests no diff reached; each costs a reading at
every landing and a 50-minute re-run on a builder's branch), and the pipe's shared secret (`IpcToken.Ensure` is an unlocked read-then-write). Light; fresh builder,
seat P; no rung. `docs/HOW-WE-BUILD.md` step 6: `Timing` is the one place a second attempt exists; membership is argued AT THE TEST with measured numbers, never
granted to whatever went red, and an assertion is never loosened to get in. Prefer a deterministic seam (a latch, an injected clock, a recorded sleep) wherever the
product's verdict does not need the runner's wall clock; `Timing` only where it does.
**Facts (SOURCE at `3564dee7`, read by seat P, NOT runtime-verified; the readings are in `fleet/ci-ledger.md`).**
- (a) `src/TradeAgent.Security/SecretStore.cs:62-69`: `Ensure` reads, and when absent or short writes a fresh token with `File.WriteAllBytes` (`:29`), then restricts the
  mode (`:30`; `:51-56`, non-Windows only, AFTER the bytes are on disk); a DPAPI read that throws is taken as absent and regenerated (`:44-47`). Windows run 37443989301:
  `VenueOpsTests.The_catalogue_read_is_in_the_deadline_table_at_zero` (`tests/TradeAgent.UnitTests/VenueOpsTests.cs:79`, `Ensure` at `:83`) threw IOException "being used
  by another process" in `SecretStore.Write`, 10.5 s into the Unit run: `VenueOpsTests:83`, `DataOpsTests:127`, `CoreTests:265` call `Ensure` in parallel on the process's
  fresh shared home. Product reach, NOT VERIFIED: the app (`AppHost.cs:666`, `:900`) and the host (`GatewayHost/Program.cs:51`) first-`Ensure` one home.
- (b) `tests/TradeAgent.IntegrationTests/CoidWitnessTests.cs:715-737`: `Assert.Equal(5, attempts)` HELD; `clock.ElapsedMilliseconds < 2000` (`:735`) on a Stopwatch went
  red on windows run 37443797669 ("the retry took 2335 ms"), green at `8029b53c` and `003c0a76`. The witness is the MONEY PATH.
- (c) `SweepRequestIdTests.cs:456` (`A_five_order_sweep_carries_a_mix_of_outcomes_in_one_answer`, not `Timing`; `WaveIssueRoom = 750` at `:258`, argued `:246-257`): windows
  run 37494849714 spread the wave past 750 ms (2 legs refused, 1 allowed); the same code green ×3 at `b94fc212` (run 37495039407). The argued numbers did not hold.
- (d) Same file `:306`, `A_sweep_pays_the_emergency_budget_once_not_once_per_rpc` (`Timing`): NullReferenceException at `:333` on macOS run 37420396771 — a book read
  clipped by the deadline answers `ok=False` with no Data, and the test casts `Data` without asserting `Ok` (a 2100 ms control reproduced it; `U-fix-press-budget`'s record).
- (e) `tests/TradeAgent.FaultTests/RiskGateTests.cs:252` (`A_day_past_its_loss_budget_refuses_…`) reads the loss day by the UTC date, while `FakeBroker` stamps fills
  with its own `DateTimeOffset.UtcNow` (`src/TradeAgent.Connectors.Fake/FakeBroker.cs:146`, also `:100`, `:114`, `:126`, `:178`) with no seam: a run across 00:00Z can
  book the fill on one day and judge it on the next (`U-test-hygiene-1`'s record, NOT done; not yet seen red).
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md` step 6; `.github/workflows/build.yml` (`:31`, `:76-94`: how `Timing` runs and retries); each test above and the code it drives.
Must NOT: raise a bound, a room, a budget, a timeout or a latency to get green; add a retry; weaken or delete an assert; skip on a platform; put a test in `Timing`
without the numbers measured and written at the test; change the witness's attempt count, backoff or budget, or the simulator's fills; read the wall clock in product
code for a duration; let a token file exist, even for an instant, readable by another user or holding a half-written token that a reader would replace.
Items, one commit each, one-sentence messages:
1. **(a) One token, written once:** `Ensure` creates the file atomically (a writer that loses the race returns the winner's token; a reader never sees half a file; on
   non-Windows owner-only from its first byte). RED first: many parallel first `Ensure` calls on a fresh home → one token, every caller holding it, no exception
   (quoted red before). A credential: ONE mutant, the unlocked write restored → red, quoted.
2. **(b) The witness's bound proved where it lives:** the five attempts' sleeps summed on a seam (or an injected clock) and asserted bounded, the count unchanged; the
   Stopwatch upper bound is REPLACED (not dropped) only by a seam assert of the same claim, both quoted. Money path: the new assert RED under an unbounded-backoff mutant.
3. **(c) The wave released by a fact, not a room:** the five legs reach the wire on the fixture's own latch or seam, so the mix of outcomes cannot depend on how a runner
   spreads four issues; or, if the verdict truly needs wall time, `Timing` with numbers measured on windows-latest and why the 750 ms argument failed.
4. **(d) Ok before Data** at `:333`: a clipped read fails in words, never with a NullReferenceException.
5. **(e) The simulator's clock is a seam** (default `DateTimeOffset.UtcNow`, behaviour unchanged) and `RiskGateTests` pins it: RED first with the fill booked at
   23:59:59Z and the verdict read at 00:00:01Z, quoted.
Proof: item 1's and item 5's RED-before and item 1's and item 2's mutants, quoted; touched classes 20× locally through `suite.sh`; on CI `fleet/bin/ci-dispatch.sh <WT> 3`
(three runs at once on one sha count as three) — every job green on all three platforms, quoted; each changed assert's before/after quoted in the report.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed;
touched classes 3×; branch CI on all three platforms; tests box or NOT RUN; names vs `main` 0 removed (both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
