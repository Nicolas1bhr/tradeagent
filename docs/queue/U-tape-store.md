# U-tape-store — the app records the market's context as it arrives: point-in-time, never overwritten, every fetch accounted for, each row classed
**Arrow closed:** information → research (`docs/EDGE-FACTORY.md` § 4.1) — the part that cannot be retrofitted. Today the only observation is a 1-minute OHLCV bar of
one pair (R01 § D). **Soft dependency:** reuse `U-runner-forward`'s alignment helper (it lands in W2). **Observable result:** while the app runs, Binance USDⓈ-M
context — premium index with the live funding rate, open interest, the 5-minute long/short and taker ratios, settled funding — for six symbols is stored in
`state/tape.db` with the instant it ARRIVED and the ORIGIN it came from, each row naming its fetch and carrying an app-computed evidence class; nothing here
places an order or holds a credential. Reading it for agents and status is `U-tape-read`; history backfill is `U-tape-archive`.
**Storage:** its own SQLite file — own connection, WAL, `synchronous=FULL`, `busy_timeout 5000` (as `Connectors.Paper/PaperBook.cs:68`) — with two differences from
`PaperBook`: the version is checked BEFORE any migration (PaperBook migrates at `:69` then checks at `:71-75`), and the file has an `if (have < N)` ladder from
version 1, because `U-decision-port` adds rung 2 THROUGH `TapeStore`. A newer file is refused and written as an activity line (the `AppHost.cs:552-556` pattern).
RUN 2026-10-02 from this Mac, no key, all HTTP 200 in 0.31–0.37 s: `GET https://fapi.binance.com/fapi/v1/premiumIndex?symbol=BTCUSDT`, `/fapi/v1/openInterest`,
`/futures/data/openInterestHist?…&period=5m`, `/fapi/v1/fundingRate`, `/futures/data/globalLongShortAccountRatio?…&period=5m`, `/futures/data/takerlongshortRatio?…`.
Read first: `docs/EDGE-FACTORY.md` § 4.1, § 6; `CLAUDE.md`; `ForwardBarStore.cs`; `ForwardBarCollector.cs` and the alignment helper; `CandleSourceCatalog.cs:83-235`;
`PaperBook.cs:39-124`; `DatasetReader.cs:49-86` (required audience); `AppHost.cs:476-560`; `App/SettingsView.cs:178-215` (toggle pattern `:190-196`);
`SuiteReachesNoVendorTests.cs:40-66`; `FakeArchive.cs`.
Items, one commit each, one-sentence messages:
1. `TapeStore` (Core), the ONLY writer of `tape.db` (later rungs included): `tape_fetch` (id, source, series, url, origin, requested_at, received_at, http_status,
   items, body_sha256, note) and `tape_obs` (id, source, series, subject, source_time, received_at, fetch_id, natural_key, revision, payload_sha256, payload =
   canonical JSON keeping the vendor's decimal strings, ≤ 64 KB, evidence_class). Natural keys: premium and open interest `symbol|time`; 5-minute series
   `symbol|timestamp`; funding `symbol|fundingTime`. Same key + same payload sha ⇒ nothing written (counted on the fetch); a different payload ⇒ a new row
   `revision + 1` with its own `received_at` — never `INSERT OR REPLACE`, never an UPDATE. `AsOf(audience, source, series, subject, t)` = the latest revision with
   `received_at ≤ t`; the audience argument is REQUIRED (a later holdout can apply). One transaction per fetch.
2. Evidence class computed per observation from recorded fields, never accepted from a caller: `O-LIVE` iff fetched from the source's BUILT-IN origin AND first
   received within its cadence + 30 s of its `source_time`; everything else `O-ARCH` (an override origin included: `tape-sources.json` is agent-writable); a later
   revision never upgrades. `O-PIT` is `U-tape-archive`'s.
3. `TapeSourceCatalog`: built-in rows `binance-um-premium` (60 s, one call for all symbols, filtered), `binance-um-oi` (60 s per symbol), `binance-um-oi-5m` and
   `binance-um-ratios-5m` (300 s), `binance-um-funding` (900 s); universe BTCUSDT ETHUSDT SOLUSDT BNBUSDT XRPUSDT DOGEUSDT; an optional `tape-sources.json` may
   add UNKEYED rows, whose rows are always `O-ARCH`. Per-row cadence, terms note, doc URL.
4. `TapeCollector` (Provisioning): one loop per row on the shared alignment helper (next cadence multiple + 2 s), 10 s leash, body ≤ 4 MB, backoff to 5 min,
   every attempt a `tape_fetch` row; NO first-start backfill (that is `U-tape-archive`). Started and stopped by `AppHost`, independent of the mission loop; the
   Market data card's toggle "Record market context" (default ON, one press) is the only control; no verb or pipe op starts, stops or writes it. Add
   `fapi.binance.com` to `SuiteReachesNoVendorTests`. `CONTRACTS.md` "The tape" (claimed: as-received, append-only, revisions kept, class rules; NOT claimed:
   completeness while the app is closed, a vendor checksum, evaluation evidence, protection from an agent editing the file before containment),
   `USER-GUIDE.md` (the toggle), `docs/RESEARCH-REQUIRED.md` (endpoints and the RUN above).
Tests (new code; red-first is by mutant; loopback listener, injected clock and timer): (a) `A_rereading_with_a_different_payload_is_a_new_revision_not_an
_overwrite`; (b) `As_of_returns_what_had_arrived_by_then_for_the_named_audience`; (c) `Every_fetch_is_a_row_with_its_origin_and_every_observation_names_its_fetch`;
(d) `A_late_row_or_an_override_origin_is_arch_and_an_on_time_built_in_row_is_live`; (e) `The_tape_has_its_own_file_and_ladder_and_a_newer_file_is_refused_with_an
_activity_line`; (f) `The_collector_backs_off_and_recovers`. Mutants to watch red and quote: (i) `INSERT OR REPLACE` ⇒ (a) red; (ii) `AsOf` ignoring `received_at`
⇒ (b) red; (iii) the origin ignored in the class ⇒ (d) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
