# U-tape-read — every role can read the tape, bounded and read-only, and the owner can see it is recording
**Arrow closed:** tape → agents and owner (`docs/EDGE-FACTORY.md` § 4.1, § 4.9). **Depends on `U-tape-store`.** **Today:** the agent-facing data surface is
`data-list` and `data-bars` (`Core/Protocol.cs:47`), handled in `GatewayPipeServer.cs` (drain table `:331-332`, dispatch `:1233-1234`); a new op needs a
drain-table row (`HandlerPaths`, `:308`), which `GatewayPipeBackpressureTests.cs:1084` and `PnlOverPipeTests.cs:186` enforce. **Observable result:** an agent runs `trade data tape --source binance-um-oi --subject
BTCUSDT --from … --as-of …` and gets at most 5,000 rows, each with source time, arrival time, revision, class and payload; `trade data list` names every
tape series with first and last arrival, rows and last error; `status` and the daily report show whether the tape is recording; nothing can write it.
**No schema change.**
**Since this brief (re-checked after `U-tape-events` and `U-tape-archive` landed; pointers again at `9cd0f5e5`, 2026-10-06 21:55):** (i) an item `TapeScreen` quarantines is
served to EVERY audience WITHOUT its payload and WITH its `Quarantine` (rule, version) — `AsOf` already withholds; no new read path may bypass it; (ii)
announcement and GDELT series key their subjects by a URL digest or a GKG record id, so `--subject` is OPTIONAL: without it, every subject of the series in
from–to, newest arrival first, under the same limit; `data-list` names such series without enumerating subjects; (iii) GDELT's terms require credit with a
link wherever its data is used or shown: every answer and `data-list` entry holding `gdelt-gkg` rows carries the row's `Citation` (`TapeSourceCatalog
.GdeltCitation`), and the CLI prints it; (iv) classes include `O-PIT`; payloads reach 64 KB, so the byte cap governs and a capped answer says so; (v) two
switches record now ("Record market context", "Record GDELT news"): `status` reports each recorder's switch, last arrival, failures and whether GDELT's
daily cap stopped it today.
Read first: `CLAUDE.md`; `Core/Protocol.cs:14-141` (the op table and its comments — "an agent that could edit the provenance…"); `GatewayPipeServer.cs:300-360,
1180-1245,2640-2900` (`data-list` / `data-bars` handlers: the pattern, the caps, the role handling); `Gateway/GatewaySchema.cs`; `TradeCli/Program.cs` (the
`data` verbs); `GatewayTypes.cs:398` (`GatewayStatus`); `TradingGateway.cs:2694` (`StatusAsync`); `Gateway/DailyReport.cs:547-752`; `DatasetReader.cs:49-86`
(the required audience argument); the `TapeStore` as landed (`AsOf` `TapeStore.cs:558`).
Items, one commit each, one-sentence messages:
1. Op `data-tape` (read-only, every role) with a drain-table row: arguments source, series, subject, from, to, as-of, limit (≤ 5,000, default 1,000);
   validation in words for an unknown source/series/subject; the reader takes the caller's AUDIENCE (research for roles) so a later holdout can apply;
   payloads served as recorded; a response cap in bytes like `data-bars`.
2. `trade data tape …` in the CLI with the same arguments; `GatewaySchema` entry; the agents' instructions (`WorkspaceBuilder`) gain three lines: what the
   tape holds, that its rows are measurements with arrival times and classes, and how to read them.
3. `data-list` adds the tape series ("TAPE — recorded by TradeAgent as it arrived; O-LIVE rows only are first-hand; not evaluation evidence"); `status` gains
   `tape {recording, sources, last_received_at per source, failures_last_hour, rows_today}`; the daily report one line (rows, gaps, errors).
4. `CONTRACTS.md` (the op, its bounds, that no op writes the tape), `USER-GUIDE.md` (where the owner sees the tape is recording).
Tests: (a) `Data_tape_serves_bounded_rows_with_arrival_time_revision_and_class_to_every_role`; (b) `As_of_through_the_op_returns_what_had_arrived_by_then`;
(c) `No_pipe_op_writes_the_tape` (enumerate `Protocol` ops); (d) `The_drain_table_names_data_tape` (the backpressure guard); (e) `Status_reports_the_tape
_recording_and_its_last_error`. (f) `Gdelt_rows_carry_their_citation_through_the_op_and_the_cli`;
(g) `A_quarantined_item_reaches_an_agent_without_its_payload_and_with_its_rule`. Mutants to watch red and quote: (i) the limit check removed ⇒ (a) red at
5,001 rows; (ii) the op serving a quarantined payload ⇒ (g) red.
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main` first; Release `--no-incremental` 0 warnings;
Unit and Fault 0 failed locally, touched classes 3×; the full suite on CI via `fleet/bin/ci-dispatch.sh` (run id, every job's verdict); names vs `main` 0 removed
(both set sizes printed); the tests box run or "tests box: NOT RUN — <ready's answer>"; `## Report` ≤ 20 lines appended here. No push to `main`, no merge.

## Report
Tip: the commit carrying this report; code last changed at `70ca3f8c` (branch `u-tape-read`, rebased on `main` `bdf5affa`). `main` has since moved 15 commits (U-inflight-settle, `1828a188`); `git merge-tree` shows no conflict — not rebased again.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` at `70ca3f8c` → `0 Warning(s)`, `0 Error(s)`. Local Release at `70ca3f8c`: Unit `Passed! - Failed: 0, Passed: 1424`; Fault `Passed! - Failed: 0, Passed: 445`. 3×: `TapeReadTests`+`DataOpsTests` 12/12, 12/12, 12/12; `TapeOverPipeTests` 8/8, 8/8, 8/8.
CI run 37529866030 at `70ca3f8c`: **success** — ubuntu-latest, macos-latest, windows-latest (66 min) each Unit 1423/1423, Fault 440/440, Integration 638 passed (639, 1 not run), 0 failed; package success. macOS's Timing category had one red on its first attempt, `SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_not_once_per_rpc` (NullReferenceException at :333), green on the category's second attempt in the same job — a cancel-all path this diff does not reach; a first sighting for seat P. Earlier run 37527064608 at `ec905be7`: ubuntu and macos red on ONE test, `SuiteReachesNoVendorTests` (my test (g) spelled an OKX host), Integration 638 passed and Fault 440/440 on both, and on windows-latest every tape test passed too; fixed in `db8a527f` (run 37529072418 cancelled, superseded).
Names vs `main` (`names.sh`): removed 0, added 13; sets base 2220 tip 2233 ([Fact]/[Theory] 2174 → 2187).
1 done: `data-tape` for every role and a caller with none; drain row at 0; `TapeReader` opens `tape.db` read-only with `query_only`, the gateway holds no `TapeStore`; the one withholding place is `TapeStore.Served`, used by `AsOf` and the range read; limit 1–5,000 (default 1,000, more refused in words), 4 MiB of rows; newest arrival first; app wiring in `AppHost` (start and connector switch).
2 done: `trade data tape …` with a `source credit:` line, `GatewaySchema` entry (and `status.tape` described), three `WorkspaceBuilder` lines, the harness `data` tool carries `data-tape`.
3 done: `data-list.tape` (switch, rows, first/last arrival, last error, symbols or `subject_key`, `citation`); `status.tape` {recording, rows_today, failures_last_hour, market_context, gdelt_news with daily_cap_reached_today, sources}; report § 7 "market context tape" (rows, gaps, errors, GDELT credit).
4 done: `CONTRACTS.md` (IPC bullet, drain row, harness tools, the tape's "THE READ", NOT CLAIMED 12–13), `USER-GUIDE.md` ("Seeing that it is recording").
Deviations: (1) added arg `before`, a row-id cursor — a GDELT file's rows share label AND arrival, so no time can continue a capped answer exactly; (2) the byte cap counts the reply's rows as written (`data-bars` caps bars, not bytes); (3) `GdeltGkg.CapNotePrefix` replaces the recorder's literal (same text) so status reads the cap off the tape; (4) `DataOpsTests.There_is_no_operation_on_this_channel_that_writes_a_dataset` counted 2 `data-` ops: it now names the three (stronger; not renamed).
RED before: (a) with the dispatch arm removed → `unknown operation 'data-tape'`. Mutant (i) the reader's limit check removed → (a) `Expected: 5000 Actual: 5001`. Mutant (ii) `TapeStore.Served` returning the payload → (g) `Expected: Null Actual: String`.
Tests box: NOT RUN — `ready` at 22:35 CEST: "NO - the machine does not answer (…)", exit 1, not retried.
NOT done/verified: no rung (none needed); no Settings-card line (status and report only, per brief); status/report counts stand on `TapeReader.ArrivalSlack` (a clock stepped back > 10 min is not covered); `data-tape` cost on a large tape not measured (grows with the series: no arrival index until a rung adds one); the read-only WAL open on Windows is verified by CI only.
