# U-tape-read — every role can read the tape, bounded and read-only, and the owner can see it is recording
**Arrow closed:** tape → agents and owner (`docs/EDGE-FACTORY.md` § 4.1, § 4.9). **Depends on `U-tape-store`.** **Today:** the agent-facing data surface is
`data-list` and `data-bars` (`Core/Protocol.cs:47`), handled in `GatewayPipeServer.cs` (drain table `:331-333`, dispatch `:1228-1230`); a new op needs a
drain-table row (`HandlerPaths`, `:308-353`), which `GatewayPipeBackpressureTests.cs:1083` and `PnlOverPipeTests.cs:188` enforce. **Observable result:** an agent runs `trade data tape --source binance-um-oi --subject
BTCUSDT --from … --as-of …` and gets at most 5,000 rows, each with source time, arrival time, revision, class and payload; `trade data list` names every
tape series with first and last arrival, rows and last error; `status` and the daily report show whether the tape is recording; nothing can write it.
**No schema change.**
Read first: `CLAUDE.md`; `Core/Protocol.cs:14-141` (the op table and its comments — "an agent that could edit the provenance…"); `GatewayPipeServer.cs:300-360,
1180-1240,2587-2770` (`data-list` / `data-bars` handlers: the pattern, the caps, the role handling); `Gateway/GatewaySchema.cs`; `TradeCli/Program.cs` (the
`data` verbs); `GatewayTypes.cs:397` (`GatewayStatus`); `TradingGateway.cs:2335-2365` (`StatusAsync`); `Gateway/DailyReport.cs:547-752`; `DatasetReader.cs:49-86`
(the required audience argument); the `TapeStore` as landed.
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
_recording_and_its_last_error`. Mutant to watch red and quote: the limit check removed ⇒ (a) red at 5,001 rows.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
