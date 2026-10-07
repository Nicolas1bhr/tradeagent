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
