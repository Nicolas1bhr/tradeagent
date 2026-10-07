# U-tape-holdout — no caller on the agent-facing pipe reads a tape row from inside any dataset's holdout window; the referee still does
**Protects (evidence: the holdout, `Holdout.cs:37-76, 80-138`, `docs/COUNCIL.md` "holdout data the research process cannot reach"):** the orchestrator's ruling, 2026-10-07
12:02 — `U-tape-read` (`e2f4daf8`) made the tape readable by every role, and no tape holdout exists (`TapeStore.cs:548`); premium-index rows carry `markPrice`
(`TapeCollectorTests.cs:53`), so once a dataset's holdout overlaps the tape's recording, any role reads prices from inside the window with `trade data tape`.
Latent today (the tape is days old; existing holdouts end in August; M0's `1828a188` has no `data-tape`) — a leak class on `main`, closed before v2a reads
features from the tape. Fresh builder, seat A. **No schema change.** Brief written by seat A's manager from the code at `b73b8855`.
**Today:** bars — `Holdout.Refusal(set, audience, from, to)` runs INSIDE `DatasetReader.Read` (`DatasetReader.cs:80-85`): a window reaching `holdout_from` is
REFUSED in words, never clipped; an unbounded window reaches it; `BarAudience.Pipe(role)` never passes, the internal `BarAudience.Referee` always does.
Tape — `TapeReader.Window(BarAudience, TapeQuery, cost)` (`TapeReader.cs:176`; `From`/`To` on source time, `TapeQuery` `:14-26`) and `TapeStore.AsOf(audience,
…)` (`TapeStore.cs:559`) take an audience and apply none (`:548`); `data-tape` passes `BarAudience.Pipe(ctx.Role)` (`GatewayPipeServer.cs:2969`). Cutoffs are
`dataset.holdout_from` (`DatasetStore.cs:142`; `FirstBar`/`LastBar`/`Interval` on `DatasetRecord`), one campaign per holdout dataset (`CampaignStore.cs:340-356`).
**The rule (decided here, stated in CONTRACTS):** every dataset holding a cutoff — the rows `Holdout.Refusal` protects, open campaign or not, because the bars
rule reads the dataset — holds a TAPE window `[holdout_from, LastBar + one Interval)`. For an audience that may not read the holdout, a tape read whose asked
SOURCE-time window reaches any such window is REFUSED in words naming the dataset, its cutoff and the window — never clipped — whatever the source, series or
subject (a feature over any series is evaluation evidence inside the window; GDELT items included); an unbounded end or start reaches every window beyond it;
an as-of read is refused when the row it would serve has a source time inside a window. The referee's audience reads everything. `data-list`, `status` and the
report keep their counts (no values). Arrival time is not the test: a late revision of a pre-window datum says nothing about the window's market.
**Observable result:** with a dataset whose holdout window overlaps recorded rows, `trade data tape --source binance-um-premium --series premium-index --from <in
the window>` answers a refusal naming the cutoff for every role and a caller with none; the same read ending before the cutoff is served; the referee's
in-process read of the window is served; no tape reader can be called without the windows.
Read first: `CLAUDE.md`; `Holdout.cs` (whole, 138 lines); `DatasetReader.cs:49-98`; `TapeReader.cs:1-250`; `TapeStore.cs:540-610`; `GatewayPipeServer.cs:2890-3060`;
`DatasetStore.cs:100-145, 420-430` (`All()`); `HoldoutLedgerTests` (the public-doors guard); the `u-features` branch's `FeatureSeries.Read` (it calls `Window`).
Items, one commit each, one-sentence messages:
1. `TapeHoldout` in `Core/Data/` beside `Holdout`: the windows from the dataset ledger (each dataset with `HoldoutFrom`), `Refusal(audience, from, to)` and the
   row test for an as-of read, the refusal's words in `Holdout.Refusal`'s family; built for the pipe from `DatasetStore.All()` at each read (a cutoff set a
   second ago counts); the referee's pass-through `internal`, like `BarAudience.Referee`.
2. The readers apply it INSIDE: `TapeReader.Window` and `TapeStore.AsOf` take it as a REQUIRED argument (as the audience is — a caller that forgets gets a
   refusal, never a row); `data-tape` hands the pipe's; a refusal reaches the agent as the op's error in words. Whichever of this unit and `U-features` lands
   second threads it through `FeatureSeries.Read` (a rebase conflict there is expected and is the builder's).
3. Docs: `CONTRACTS.md` (the tape's read gains THE HOLDOUT; its NOT CLAIMED list updated), the comment at `TapeStore.cs:548`, one line in the agents' tape
   paragraph (`WorkspaceBuilder`: rows inside a holdout window are not served, and why), `GatewaySchema`'s `data-tape` text.
Tests: (a) `A_tape_read_reaching_a_holdout_window_is_refused_for_every_role` (a dataset with a cutoff over recorded premium-index rows, another symbol's rows
and a GDELT row; each role and a caller with none; RED on the base: rows served); (b) `A_tape_read_ending_before_every_cutoff_is_served`; (c) `An_unbounded
_read_that_reaches_a_holdout_is_refused_not_clipped`; (d) `An_as_of_read_that_would_serve_a_row_inside_a_window_is_refused`; (e) `The_referee_reads_the
_tape_inside_its_holdout`; (f) `Every_dataset_with_a_cutoff_holds_its_window_open_campaign_or_not`; (g) `No_tape_reader_can_be_called_without_its_holdout`
(reflection over `TapeReader` and `TapeStore`'s public reads, as `HoldoutLedgerTests` holds the bar doors). (a) and (d) RED on the base, quoted.
**Mutant**, quoted red: the check inside `TapeReader.Window` removed ⇒ (a). **Answer first, in the report:** does any in-process tape read besides the referee's need it?
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main` first; Release `--no-incremental` 0 warnings; Unit,
Fault 0 failed, touched classes 3×; CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed (both sizes); the tests box run or its NOT RUN line;
`## Report` ≤ 20 lines appended here. No push to `main`, no merge.

## Report
**Answer first — no in-process tape read besides the referee's needs it:** `GdeltRecorder`'s `SourceTimes`/`Fetches`/`Revisions`/`PayloadBytes` are the recorder's own bookkeeping, and `Series`/`Recording`/`Day`/`Sources`/`SeriesOf`/`Holds`/`Subjects` give data-list, status and the report counts, names and arrivals, never a value; the one other in-process reader of values, `FeatureSeries.Read` (U-features, landed first), now requires it. The referee reads no tape today; its pass-through `TapeHoldout.Referee` is `internal` and tested.
**Tip:** code `3c160413` on main `18a7ab10`; this report is the last commit. No rung: schema 30, tape.db 1.
**Gate (all on 3c160413):** `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s)`, `0 Error(s)` · Unit `Passed: 1458, Failed: 0` · Fault `Passed: 463, Failed: 0` · 3× TapeHoldoutTests, TapeStoreTests, TapeReadTests, TapeAnnouncementTests, GdeltRecorderTests, FeatureSeriesTests → `Passed: 36, Failed: 0` each run.
**CI:** 37613808298 at 88c51702 — ubuntu, macos, windows, package: all success. 37635095478 at 3c160413 — ubuntu success, macos success, windows FAILURE in `BridgeLivenessClockTests.A_bridge_pulsing_while_the_wall_clock_steps_forward_an_hour_stays_ready` only ("System.InvalidOperationException : The stream is currently in use by a previous operation on the stream." at `StubBridge.Heartbeat`, Harness.cs:193), package skipped; seat A judged it the stub bridge's writer race (RIG, owed in U-test-hygiene-3 (b)) and ruled no re-dispatch. 88c51702→3c160413 changed one sentence in FeatureSeries.cs (the `Bounded` words), one ETHUSDT assertion in FeatureSeriesTests and one CONTRACTS clause.
**Names:** vs merge-base 18a7ab10 — removed 0, added 9 (sets 2278 → 2287). Vs main d33d3419 the 9 "removed" are U-vendor-limit-quote's VendorLimitQuoteTests, landed after my rebase (seat A rebases at landing; `git merge-tree` shows no conflict).
**Item 1 — done** (80d3d183): `TapeHoldout`/`TapeHoldoutWindow` in Core/Data — every dataset with a cutoff, whatever its state, class or campaign, holds `[holdout_from, LastBar + one bar)` in source time, read from `DatasetStore.All()` at each read; `Refusal(from, to)` and the as-of row test in `Holdout.Refusal`'s words.
**Item 2 — done** (a44b896f): `TapeReader.Window` and `TapeStore.AsOf` require it and refuse inside with no row (`TapeWindow.Refusal`, new `TapeAsOf`); `data-tape` hands `TapeHoldout.Pipe(role, gateway.Datasets)` and answers `HOLDOUT_WITHHELD`. Landed second after U-features: `FeatureSeries.Read` takes it (44cad061) — a range reaching a window is refused with no point, and the clean-history search stops at the latest window's close and SAYS so in `FeatureCleanStart.Bounded`, found or absent (87c49e33, 3c160413: seat A's ask, tested, in CONTRACTS).
**Item 3 — done** (88c51702): CONTRACTS (the tape's THE HOLDOUT; NOT CLAIMED (3) and (14); Features; the bars' holdout points to it), the `AsOf` comment (in a44b896f), the agents' tape line in WorkspaceBuilder, data-tape's schema text.
**Deviations:** (1) one argument, not two — `TapeHoldout` carries the audience with the ledger and replaces `BarAudience` on the tape readers (seat A's note), so a pipe audience never travels without its windows; its audience getter is internal, so HoldoutLedgerTests' door list is unchanged. (2) A dataset holding no bar, or whose cutoff is at or past its last bar's close, holds no window; one with no recorded last bar or an unreadable interval is held with no end. (3) By the brief's unbounded rule, while any cutoff is set a `data-tape` read with no window is refused; agents are told how to ask in the workspace line and the schema.
**Tests:** (a)–(g) as briefed, plus (h) `No_public_door_in_core_hands_out_a_tape_holdout_that_reads_inside_a_window` and `FeatureSeriesTests.A_feature_read_reaching_a_holdout_window_is_refused`; TapeStoreTests, TapeReadTests, TapeAnnouncementTests, GdeltRecorderTests and FeatureSeriesTests moved to the new argument over a ledger with no cutoff — no test renamed or deleted, no assertion weakened.
**RED before** (6ca2a54a, (a) and (d) in a base-API form): (a) "a tape read reaching dataset 1's holdout window was served to operations" — 8 rows served; (d) "an as-of read served operations a row stamped 2026-08-15 12:01:00Z, inside the holdout window".
**Mutant** (the refusal line inside `TapeReader.Window` commented out) → (a) red: "a tape read reaching dataset 1's holdout window was served to operations", "served 10 rows"; guard restored, (a) green.
**Tests box:** NOT RUN — `ready : NO - his own OFT.Platform is open; nothing of ours runs beside it` (exit 5 at 17:14; at 13:38 it was `ready : NO - the machine does not answer (…)`).
**NOT done / NOT verified:** no named-pipe test of the refusal — (a) goes through `GatewayPipeServer.CallAsync`, the same handler; no every-op sweep for tape rows; the HOLDOUT_WITHHELD owner text in Errors.cs still says "bars"; the referee has no tape door yet (U-language-v2a's or the referee's); CI windows on 3c160413 is not green (above).
