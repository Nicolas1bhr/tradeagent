# U-tape-read — every role can read the tape, bounded and read-only, and the owner can see it is recording
**Arrow closed:** tape → agents and owner (`docs/EDGE-FACTORY.md` § 4.1, § 4.9). **Depends on `U-tape-store`.** **Today:** the agent-facing data surface is
`data-list` and `data-bars` (`Core/Protocol.cs:47`), handled in `GatewayPipeServer.cs` (drain table `:331-332`, dispatch `:1228-1229`); a new op needs a
drain-table row (`HandlerPaths`, `:308`), which `GatewayPipeBackpressureTests.cs:1083` and `PnlOverPipeTests.cs:188` enforce. **Observable result:** an agent runs `trade data tape --source binance-um-oi --subject
BTCUSDT --from … --as-of …` and gets at most 5,000 rows, each with source time, arrival time, revision, class and payload; `trade data list` names every
tape series with first and last arrival, rows and last error; `status` and the daily report show whether the tape is recording; nothing can write it.
**No schema change.**
**Since this brief (re-checked at dispatch, 2026-10-06, after `U-tape-events` and `U-tape-archive` landed):** (i) an item `TapeScreen` quarantines is
served to EVERY audience WITHOUT its payload and WITH its `Quarantine` (rule, version) — `AsOf` already withholds; no new read path may bypass it; (ii)
announcement and GDELT series key their subjects by a URL digest or a GKG record id, so `--subject` is OPTIONAL: without it, every subject of the series in
from–to, newest arrival first, under the same limit; `data-list` names such series without enumerating subjects; (iii) GDELT's terms require credit with a
link wherever its data is used or shown: every answer and `data-list` entry holding `gdelt-gkg` rows carries the row's `Citation` (`TapeSourceCatalog
.GdeltCitation`), and the CLI prints it; (iv) classes include `O-PIT`; payloads reach 64 KB, so the byte cap governs and a capped answer says so; (v) two
switches record now ("Record market context", "Record GDELT news"): `status` reports each recorder's switch, last arrival, failures and whether GDELT's
daily cap stopped it today.
Read first: `CLAUDE.md`; `Core/Protocol.cs:14-141` (the op table and its comments — "an agent that could edit the provenance…"); `GatewayPipeServer.cs:300-360,
1180-1240,2640-2820` (`data-list` / `data-bars` handlers: the pattern, the caps, the role handling); `Gateway/GatewaySchema.cs`; `TradeCli/Program.cs` (the
`data` verbs); `GatewayTypes.cs:398` (`GatewayStatus`); `TradingGateway.cs:2631` (`StatusAsync`); `Gateway/DailyReport.cs:547-752`; `DatasetReader.cs:49-86`
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
