# U-data-2 — a second candle source, a bar that says what it is, and validated data arrival as a wake
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md:145` (quality flags on bars), `:164-172` (Binance first; Revolut X public candles, five
minutes, a ninety-day target, actual depth recorded, a candle WITHOUT volume midpoint-derived and flagged, never trade evidence; Binance results
hypothesis evidence, not execution evidence), `:89-91` ("validated data arrival" among the persisted wakes), then `Core/Data/BinanceArchive.cs`
(`Source`, `Interval = "1m"`, `MonthsWanted = 12`, the checksum sidecar, never the month in progress), `KlineNormaliser.cs` (`Header` six columns, gaps
never filled), `DatasetReader.cs:7-13` (`KlineBar`), `BarFeed.cs`, `Provisioning/BinanceDataService.cs`, `BinanceArchiveClient.cs`, `Downloader.cs`
(the injectable request timeout), `Db/DatasetStore.cs`, `Db/MissionEventStore.cs:11-44` (kinds: no `data`), `App/SettingsView.cs:161-182`, the
`## 2026-09-08 — U-data-binance landed` and `U-archive-win landed` sections of `BUILD-STATUS.md` (the loopback harness, the host scan, the
one real fetch as evidence), `docs/RESEARCH-REQUIRED.md:170` (Revolut X: only the SIGNED trading base is recorded; no public-candles path, no
sandbox). Branch `u-data-2`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-data-2`, rebased onto `main` AFTER `U-venue-catalog`
lands. Every item ships with a test RED before it and ONE mutant, quoted. NOT the money path. No box, no ATAS, no money, no network. **Schema 18.**

**Why.** One source, one interval (a `const`), one coverage target and a bar with five numbers and no flag: the pipeline's Binance-specific
assumptions cannot be told from its general ones, and COUNCIL names a second source whose candles may carry no volume. Nothing wakes a role
when validated data arrives. THE FACT the builder must carry: this repository records NO Revolut X public-candles endpoint; the source's URL
shape is DATA (`sources.json`, the `runtimes.json` pattern), marked unverified, built and proved against the loopback harness only, and the one
real fetch is the owner's to authorise once the endpoint is supplied — say so in `CONTRACTS.md` and in the report.

1. **The collector is an interface:** `CandleSource` (id, interval, coverage target, URL shape, whether the vendor publishes a checksum, whether
   candles carry volume); Binance is one implementation, its dataset rows byte-identical to today's. RED: a source declaring five minutes and
   ninety days produces a `1m`/12-month row. Mutant (the interval read from `BinanceArchive.Interval`): the same.
2. **A per-bar quality flag** (`traded` / `midpoint_derived`) in the normalised file — header versioned so an old file still reads — and counted
   on the dataset row. RED: a volume-less candle normalises to `volume=0` and nothing says so. Mutant (the flag written, not counted): the row
   claims depth it does not have.
3. **A volume-less candle is never trade evidence:** `data-bars`, `BarFeed`, a backtest over such a dataset and section 8 say so in words. RED:
   a run over midpoint candles reports like a run over traded bars. Mutant (said only on `data-list`): the backtest reply silent.
4. **The second source end to end against the loopback harness:** the source's marks per request, the host scan extended to its host, provenance
   identical (url, published hash where the vendor publishes one, computed hash, bytes, download time), the dataset naming its venue
   (`U-venue-catalog`). RED: a second source's dataset records no raw file. Mutant (the published-hash check skipped where none is published):
   a source with no checksum accepted as verified.
5. **`data` is a wake:** an ACCEPTED dataset raises one deduplicated `MissionEventKind.Data` event for Research; a re-verification raises
   nothing; the Situation names the new dataset. RED: collecting twelve months wakes nobody. Mutant (keyed by the collection attempt): a
   rebuild buys a paid turn every press.

Not this unit: the Revolut X trading connector or any credential; `U-bars` (ATAS futures, the box); Databento (a paid key); live bars.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, the endpoint fact restated, what you did NOT do.
