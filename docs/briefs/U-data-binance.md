# U-data-binance — the first real dataset: Binance's public 1-minute archives, with provenance the AI cannot edit
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` ("Data", the `U-data-binance` line, rule 2), `docs/RESUME-HERE.md`
step 6. Branch `u-data-binance`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-data-binance`, rebased onto
`main` first. Every guard ships with a test that was RED before it and ONE mutant watched going red, quoted. No box, no ATAS,
no money, no order. The schema number is the next free one when you rebase (`U-wakes` claims 7; then this is 8).

**Why.** No bar type exists in `src/`; `data/` in the agent's workspace is created empty; the loop's first run found one free
sample and could research nothing. The tools exist: `Downloader` (`Provisioning/Downloader.cs`: `Integrity.Pinned`, the bound
part file, `Sha256Async`, `TryGetSmallTextAsync`), the `material` ledger's shape (a row per file version, measurement apart
from claim, `Database.cs:144-178`), the `pnl` five-file pattern for a read-only op (`GatewayPipeServer.cs:1671`, its deadline table `:235-262`).

1. **The download.** Binance publishes monthly spot klines at `https://data.binance.vision/data/spot/monthly/klines/<PAIR>/1m/
   <PAIR>-1m-<YYYY-MM>.zip` with a `.CHECKSUM` sidecar (`sha256  name`) — VERIFY both at build time with ONE real month (quote
   the URL, the sidecar line and the computed hash; the only network outside tests). The owner names the pair in a new
   Settings section "Market data" (default `BTCUSDT`) and presses "Download 12 months" (one press — a public archive grants
   nothing): the twelve most recent COMPLETE months, each through `Downloader` with `Integrity.Pinned(sidecar)`, into
   `state/data/binance/<PAIR>/1m/raw/`, app-owned, never the agent's folder. A 404 month is recorded "not published", not an
   error; a mismatch is thrown away as `Downloader` does. RED: a sidecar that disagrees is accepted. Mutant: `Unverified` used.
2. **The normaliser.** Kline rows (`open_time, open, high, low, close, volume, close_time, …`) become one dataset file
   (`state/data/binance/<PAIR>/1m/<version>.csv`, UTC, LF): the timestamp UNIT decided per file by magnitude (milliseconds
   before 2025-01, microseconds after — never assumed from the date) and recorded; duplicate `open_time`s dropped and counted;
   gaps between first and last bar counted and listed (bounded); a bar whose `close_time` is after the download time is
   INCOMPLETE, excluded and counted; nothing filled in. RED: a mixed-unit pair of months makes one timeline (today nothing);
   mutant (unit assumed from the date → 1970) → red. Gaps RED: 3 missing minutes report 3; mutant (gaps filled) → 0.
3. **Provenance the AI cannot edit** — table `dataset` (app-written, no verb, no pipe op): id, source, pair, interval, months
   attempted/present/not-published; per raw file url, published sha256, computed sha256, bytes, downloaded_at, unit; the
   normalised file's sha256, bar count, first and last bar, gaps, duplicates, incomplete, accepted_at. **Reproducible** (the
   property in `docs/COUNCIL.md`): normalising the same raw files again yields the same sha256, and a raw file whose hash no
   longer matches its row makes the dataset REJECTED, never re-normalised. RED: an altered raw file still reads accepted.
4. **Read-only to the AI:** ops `data-list` and `data-bars` (`trade data list --json`, `trade data bars --pair --from --to
   --json`, at most 10,000 bars a call, the cap named in the refusal; unreadable dates refused; a declared reply type with
   `JsonIgnoreCondition.Never` on every nullable — the `pnl` lesson); both in the deadline table at `TimeSpan.Zero`; the schema
   text and `CONTRACTS.md` say what the data is not (bars are hypothesis evidence, never fills); one Situation line ("Data:
   BTCUSDT 1m, <first> → <last>, N bars, G gaps" or "no dataset yet"); the mission's `data/` sentences name `trade data`.
   RED: the schema lists no `data-bars`; mutant: the bar cap dropped → a 20,000-bar ask passes.
5. **Tests over a loopback `HttpListener`** serving fake zips and sidecars (`DownloadPartBindingTests.cs:35-90`), never the network.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, gate counts, one line per item with its RED and mutant, the real month's URL and hashes.

## Report

Tip `26d852d`; the gate ran at `d333480` and the only commit after it is a docs-only schema renumber plus this report. Rebased onto `main` `f40f833`: the `dataset` migration renumbered 7 -> 8 inside the rebase (`U-wakes` took 7) in `Database.cs`, `Versioning.cs` and `docs/CONTRACTS.md`, and the exact-version pin moved with it into `DatasetLedgerTests.The_schema_carries_the_dataset_tables_at_version_eight` while `MissionEventTests`'s became a `>= 7` floor — the convention `AiAttemptLedgerTests` already set on `main`.
Gate, Release: `dotnet build TradeAgent.sln -c Release --no-incremental` -> `0 Warning(s)` `0 Error(s)`; unit `Failed: 0, Passed: 539, Skipped: 0, Total: 539`; fault `Failed: 0, Passed: 277, Skipped: 0, Total: 277`; integration `Failed: 0, Passed: 621, Skipped: 1, Total: 622`. Touched classes 3x: unit `Passed: 101` three times, integration `Passed: 144, Skipped: 1` three times. Test names vs `main`: 36 added, 0 removed (`The_schema_carries_the_table_at_version_seven` was RENAMED `..._seven_or_later` and kept, not deleted). Secret scan of the whole diff: 0 hits.
1. Download. RED (sidecar guard stripped): `A_month_whose_sidecar_disagrees_with_the_bytes_is_thrown_away` -> `Expected: ChecksumMismatch / Actual: Collected`. Mutant (`Integrity.Unverified` in place of `Integrity.Pinned(published)`): same test, `Expected: ChecksumMismatch / Actual: Collected`.
2. Normaliser. RED (one unit for the whole collection): `Two_months_written_in_different_units_become_one_continuous_timeline` -> `Expected: 2024-12-31T23:50:00.0000000+00:00 / Actual: 1970-01-21T02:08:09.0000000+00:00`. Mutant (unit assumed from the month's date): `The_unit_is_read_off_the_number_and_not_off_the_month_it_came_from` -> `Expected: 2026-08-01T00:00:00.0000000+00:00 / Actual: 1970-01-21T15:59:02.4000000+00:00`.
   Gaps RED (nothing counts the holes): `Three_missing_minutes_are_reported_as_three_and_nothing_is_filled_in` -> `Expected: 3 / Actual: 0`. Mutant (holes filled forward from the previous bar): same test, `Expected: 3 / Actual: 0`.
3. Provenance. RED (`Checked` re-reads no hash): `An_altered_raw_file_makes_the_dataset_rejected_and_it_is_never_renormalised` -> `Expected: REJECTED / Actual: ACCEPTED`. Mutant (only the normalised file re-hashed, the raw loop skipped): same output, and WATCHED — `Failed: 1, Passed: 4` of that class, so the raw-file half alone is what went red. Reproducibility mutant (the normalised file names itself in its header): `The_same_raw_files_rebuild_to_the_same_normalised_hash` -> `202cf09ccff4408b26ce22630636b363e5f118cac...` vs `676f71391625d27e0485dd7e18d3669e2f8111d22...`.
4. Ops. RED (both schema entries removed): `DataOpsTests` -> `the schema does not name 'data-list'`, `the schema does not name 'data-bars'`, and `There_is_no_operation_on_this_channel_that_writes_a_dataset` `Expected: 2 / Actual: 0`. Mutant (the `OverCap` refusal dropped): `A_window_bigger_than_the_cap_is_refused_and_the_refusal_names_the_cap` -> `Assert.False() Failure / Expected: False / Actual: True` with `reply.Error` `null` — the 20,000-bar ask was served.
5. Loopback. RED (an offending source planted, then deleted): `No_test_names_the_vendors_host_and_none_takes_the_clients_default_base_url` -> `these tests would reach the real archive: RedFirstOffender.cs:8 names the vendor's own host / RedFirstOffender.cs:9 constructs BinanceArchiveClient with no base URL, so it would use the vendor's`. Mutant (the scan narrowed to one project): `The_scan_covers_both_test_projects_and_every_source_in_them` -> `Assert.Contains() Failure: Filter not matched in collection`.
THE ONE REAL DOWNLOAD, made by hand on this Mac on 2026-09-07 (the previous builder left no quotable record of its own, so this is mine): `https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1m/BTCUSDT-1m-2026-08.zip` -> `HTTP/2 200`, `content-type: application/zip`, `content-length: 2084946`. Sidecar at the same URL + `.CHECKSUM`, verbatim and with no trailing newline: `acab442e02745177063031d402929703ae983715010064b40cf811c4beb843d4  BTCUSDT-1m-2026-08.zip`. `shasum -a 256` over the 2084946 bytes: `acab442e02745177063031d402929703ae983715010064b40cf811c4beb843d4` — equal. `BTCUSDT-1m-2026-09.zip` (the month in progress) -> `HTTP 404`. Put through `KlineNormaliser` once: 44,640 bars, unit read off the magnitude as `Microseconds`, `2026-08-01T00:00:00Z` -> `2026-08-31T23:59:00Z`, gaps 0, duplicates 0, incomplete 0, unreadable 0, normalised sha256 `f2dc9adde84d26d5488e5ef3a1f57eca2b5b08f03d3ffc0c3cb3205a748250b8`.
NOT DONE: no Windows box, no ATAS, no order, no money — none was in scope. No app run and no screenshot of the Settings "Market data" section: it is verified only by the code and the tests, not by a press. No second real month and no twelve-month collection against the vendor; every test runs against the loopback `HttpListener`.
NOT MINE AND STILL RED ON `main`: `MissionLoopTests.The_delay_the_ai_asks_for_becomes_an_event_that_survives_the_loop` (from `U-wakes`) failed the first unit run at 23:53:56 CEST with `Range: (00:09:50 - 00:10:00) / Actual: 00:06:03.7870220`, because `MissionLoop.Schedule` raises the Renewal event at `LocalMidnightAfter(now)` and the loop returns the EARLIEST due event — so the test fails for any run started within ten minutes of local midnight. Re-run alone at 00:06 it passed, and the counts above are that run. The product is right and the assertion is wrong; my diff does not touch `Schedule`, `NextWait` or `LocalMidnightAfter`.
