# U-tape-gaps — the tape's daily line counts what it did not record across days and restarts, names each stretch by what the tape holds inside it, never reads an unrecorded day as zero gaps; and a daily report reaches disk whole or not at all
**Arrow closed:** EDGE § 9 M1, "the tape has recorded seven days with gaps accounted" (`EDGE-FACTORY.md:440-441`), and "unknown is never zero" (`PRINCIPLES.md:27`). Today a day's line counts a source's gaps
only "between its first and last of the day" (`CONTRACTS.md:3169-3171`), so a night the app was down, a restart across midnight or a whole unrecorded day counts nothing — while `USER-GUIDE.md:409-413`
already tells the owner the gaps include "while TradeAgent was closed". **Depends on** `U-tape-chain` (LANDED `e493578c`). Re-checked against main `a2c4a218` and ORGANISATION § 15 (2026-10-10, seat A).
**Protects (EDGE § 6):** **6.2/6.3:** the measure is app-computed from the tape's own rows by the read-only reader (`TapeReader.cs:87-96`); nothing is written to the tape. "Nothing fills a gap" stays
(`DailyReports.cs:715`). **Evidence stays true about what ran:** the report is "the record the AI's own work is judged by" (`CONTRACTS.md:2371-2374`); reports on disk are not rewritten (`Owed` skips a
day that has a file, `DailyReports.cs:175`), and from this unit a file on disk is a whole report (item 3). **An unknown is never a zero:** a stretch with no delivery is counted and named, never omitted.
**Measured (RUN 2026-10-08, `sqlite3 -readonly` on attempt 3's tape):** 6,891 rows, 14:38:49Z–20:43:03Z on 2026-10-07; on the owner's local day `Day`'s rule gives **5 gaps, the longest 3 min**, while every
source was unrecorded for the day's first 16.65 h and last 1.28–1.5 h — about 18 of 24 h, none of it counted.
**Today (main `a2c4a218`):** `TapeReader.Day` (`TapeReader.cs:483-541`) reads only attempts that arrived inside `[from, to)` (:488-508), keys delivered times by source (:505-507) and counts successive
deliveries more than twice the cadence plus 30 s apart (:523-538); a source with no delivery that day adds nothing, and its comment "a stretch the app was not running is one too" (:481) holds only inside
one day. `DailyReports.TapeLine` prints "N gaps" (`DailyReports.cs:691-726`); its switch notes describe the switches now, not that day's (:707-708). The tape keeps no record of its own runs: a look
while switched off writes nothing (`TapeCollector.cs:234`), and a start or stop is an activity line (`AppHost.cs:751`, `:2042`) that rotation deletes (`Stores.cs:567`). `TapeReadTests.cs:237-276` pins
"a row on another day, which this day's line does not count" (:261, "1 gaps" :269). Seek pattern: `TapeReader.cs:550-553` on `ix_tape_fetch_series` (`TapeStore.cs:201`). **The report's write:**
`DailyReports.Write` writes the day's file in place (`File.WriteAllText`, :147) and `Owed` (:175) skips any day whose file exists, so a crash, a `kill -9` (systemd's restart on the Linux host) or a full
disk mid-write leaves a partial report that `Read` (:201), the page and `trade report` serve as that day's record forever.
**The rule (CONTRACTS "The tape", replacing the parenthesis at :3169-3171):**
- A source's gap is a stretch longer than twice its cadence plus 30 s with no delivery from it, from its last delivery before to its first after, across midnights and restarts; one still open ends at the report's instant.
- A day's line counts every gap that overlaps the day, clipped to the day, and states each catalogue source's recorded share of the day (of its elapsed part, today). A source with no delivery that day
  is one gap of the whole day. Before a source's first delivery ever, nothing is a gap; the line says when that source's recording began.
- Each gap is named by what the tape holds inside it: "nothing asked — TradeAgent was not running or the switch was off; the tape cannot tell which", or "asked N times, all failed: <the newest note>".
**Observable result:** the report for a day after a night down reads, per source, "recorded 16 h 48 min of 24 h", with each gap's bounds, length and cause (attempt 3's 2026-10-07: about 6 h of 24 per
source); a report on disk is the whole text or no file. `status.tape` and `data-list` are unchanged. Items, one commit each, one-sentence messages:
1. **`TapeReader.Day`.** `TapeDay` gains per-source coverage and gaps (from, to, clipped, cause, attempts inside, newest failure). The deliveries just outside the day (`note IS NULL`) are found per
   (source, series) on `ix_tape_fetch_series`, backward from `from` and forward from `to`, bounded at eight days back (then "no delivery since before <date>"). GDELT's tasks stay per series.
2. **`DailyReports.TapeLine` and the contract.** The line carries the coverage and gap words — at most three gaps listed per source, the rest counted, the longest named with its bounds — inside the
   report's 120-line bound. CONTRACTS "The tape" is rewritten; `USER-GUIDE.md:409-413` follows.
3. **The report published whole.** `Write` writes the whole text to a temp beside the day's file that `Days()` never lists and `Owed` never counts, flushes it to disk, then renames it over the day's file
   (reuse `OwnerOnlyFile.cs:61-80`'s publish — the rename a Windows reader does not refuse — or follow `AppFiles.cs:130-139`); a stale temp is removed at the next write; CONTRACTS :2371-2376 says so.
**Red-first tests** (`TapeReadTests`, a `TapeStore` on a temp file, days from `TestEnv.LocalNoon`; item 3 in `DailyReportTests`): (a) `A_night_the_app_was_down_is_a_gap_on_both_days_it_touches`; (b)
`A_day_with_no_delivery_reads_as_one_gap_never_as_zero`; (c) `A_restart_across_midnight_is_counted_once_in_each_day`; (d) `A_gap_with_failed_attempts_is_named_failing_and_one_with_none_not_asked`; (e)
`A_day_still_open_counts_its_tail_to_the_reports_instant`; (f) `Before_a_sources_first_delivery_nothing_is_a_gap`; (g) `The_daily_report_says_in_one_line_what_the_tape_recorded` rewritten to the rule
(its row two days earlier now opens the day's first gap; named in the report); (h) `A_report_whose_write_fails_midway_leaves_no_file_and_its_day_stays_owed`; (i) `A_rewrite_that_fails_leaves_the_earlier_report_whole`
(a seam that fails after part of the text). Mutants, each quoted red: the look-back seek removed ⇒ (a) and (c); the in-place write restored ⇒ (h).
**NOT claimed:** why nothing was asked (app down, switch off or a stalled recorder) — that needs the recorder's durable record of its runs and switch flips, a `tape.db` rung (next free: 3) and a follow-up
unit; per-symbol completeness inside a delivery; a clock stepped back beyond `ArrivalSlack` (`TapeReader.cs:338`); the `linux-host` CI job is not extended (its 30 s restart is inside every cadence's
allowance); a partial report written before this unit stays as it is. **No schema change** (main 30, `tape.db` 2). AGENTS.md unchanged — if it must change, each role's rendered AGENTS.md (± the simulator
paragraph) stays net ≤ 0 bytes against the base, both sizes quoted.
**Gate and report** per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main`; Release `--no-incremental` 0 warnings; Unit, Fault, Integration 0 failed, touched classes 3×;
CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed; the tests box run or its NOT RUN line; `## Report` ≤ 20 lines; no push to `main`.
