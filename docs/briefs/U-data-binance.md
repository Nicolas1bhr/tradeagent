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
