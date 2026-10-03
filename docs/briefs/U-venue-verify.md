# U-venue-verify — the app checks an instrument against the venue's own published definition and serves it verified, with nobody editing a file
**Arrow closed:** paper allocation → deployment → sizing, and the cost model's increment (`manager-prompt.md` § 5; `docs/EDGE-FACTORY.md` § 6.9). **Dispatch only
after `U-cost-model` (schema 27) AND `U-tape-store` have landed** — this unit takes the next free rung at its rebase (expected 29), the ladder contiguous; `U-tape-store` and this both edit the Market data card and
`AppHost.StartAsync`. **Today (SOURCE at `0f47db7`, R09/R11-checked):** BTCUSDT ships `Verified = false` (`Core/Data/VenueCatalog.cs:174-186`); the paper connector
offers verified rows only (`PaperConnector.cs:203,215`) and reads the FILE catalogue once at construction (`:101`; options built at `Platforms/Connectors.cs:87-90`);
the runner sizes nothing without a verified row (`ForwardRuns.cs:492-516`); the only route to `Verified = true` is a hand-edited `venues.json` (`VenueCatalog.cs:105`)
that no screen writes, and six texts tell someone to edit it (`USER-GUIDE.md:935-963`, `Backtests.cs:390-396`, `PaperConnector.cs:189-190,326`, `Protocol.cs:54-57`,
`VenueCatalog.cs:150-156`) — a terminal-shaped instruction. That file is also agent-writable (`Containment.cs:43`). **Observable result:** choosing BTCUSDT makes the
app fetch Binance's public instrument definition from the BUILT-IN origin, record the check, and serve BTCUSDT verified with the venue's numbers, URL and instant
to research, the cost model, the paper connector and the runner; a failed or stale check, or a definition URL whose origin is not the built-in one, leaves it
unverified and says why. **The next free rung** (expected 29; `docs/ORGANISATION.md` § 15). `DROP TABLE instrument_check;` joins the roll-back lists at
`VenueCatalogTests.cs:214-249` and `PaperEligibleVerdictTests.cs:259-275` (R18 V2).
RUN 2026-10-02 from this Mac, no key: `GET https://data-api.binance.vision/api/v3/exchangeInfo?symbol=BTCUSDT` → 200 ~0.3 s; `tickSize 0.01`, `stepSize 0.00001
minQty 0.00001`, `NOTIONAL minNotional 5`. The host is already in `SuiteReachesNoVendorTests` (`ForwardHost`, `:66`). Never call a real host from a test.
Read first: `CLAUDE.md`; `VenueCatalog.cs` (whole); `Core/Db/VenueStore.cs` (`Sync` :71-115, called at `TradingGateway.cs:1563`); `PaperConnector.cs:90-110,181-215,
320-330`; `Platforms/Connectors.cs:80-95`; `ForwardRuns.cs:492-516`; `Backtests.cs:341-396`; `ForwardBarCollector.cs` (leash `:46`, body bound `:52`, a row per
attempt `:243-257`); `App/SettingsView.cs:178-221`; `USER-GUIDE.md:935-963`; `FakeArchive.cs`; the `U-key-host-pin` origin rule as landed.
Items, one commit each, one-sentence messages:
1. The rung: `instrument_check` (id, venue_id, symbol, url, origin, requested_at, received_at, http_status, body_sha256, tick_size, quantity_increment,
   min_quantity, min_notional, outcome ∈ {verified, failed, refused-origin}, note) — append-only, a row per attempt, written only by the app.
2. `InstrumentVerifier` (Provisioning): the definition URL shape is a BUILT-IN venue fact; an override whose origin differs is recorded `refused-origin` and
   never verifies (the `U-key-host-pin` rule). It runs when the owner picks or changes the pair, at app start for the configured pair, and on "Check now";
   10 s leash; body ≤ 4 MB; a failure is a row and a status line.
3. ONE Core read, "served instruments" = catalogue rows overlaid by the latest successful check ≤ 7 days old (the VENUE's tick, increment, min quantity, min
   notional; a disagreeing catalogue value kept and shown beside it). Every reader uses it: `ForwardRuns.cs:512`, `Backtests.cs:356`, `venue-list`, and the
   paper connector through a `Func<…>` option in `Platforms/Connectors.cs` in place of its construction-time read, so a check that succeeds — or lapses — reaches
   it without a restart. A `"verified": true` written into `venues.json` by an agent stays advisory until containment: CONTRACTS says so.
4. Owner surface: the Market data card shows "verified against Binance on <date>" or "not verified: <reason>" and a one-press "Check now"; `status` and `venue-list`
   carry it; the six texts above name the in-app check instead of a file. `CONTRACTS.md` (claimed: the venue's published definition at that instant; NOT claimed:
   that every order sized to it is accepted, nor that a size clears the 5 USDT minimum notional, which the v1 cost model ignores), `docs/RESEARCH-REQUIRED.md`.
Red-first tests (loopback listener serving canned exchangeInfo JSON): (a) `A_successful_check_serves_the_instrument_verified_with_the_venue_numbers` (red on base);
(b) `A_failed_check_is_a_row_and_leaves_the_instrument_unverified`; (c) `A_check_older_than_seven_days_no_longer_verifies`; (d) `A_definition_url_from_another
_origin_is_refused_and_never_verifies`; (e) `The_running_paper_connector_offers_an_instrument_verified_after_it_started` (red on base); (f) `No_pipe_op_writes_an
_instrument_check`. Mutants to watch red and quote: (i) the 7-day freshness removed ⇒ (c) red; (ii) the origin check removed ⇒ (d) red; (iii) the paper connector
back on its construction-time read ⇒ (e) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.

## Report
**Tip `a39e6583`** (code; on main `35606c56`; rung **29**, main at 28). Gate, Release: `--no-incremental` build **0 Warning(s), 0 Error(s)**; Unit **1345/1345**, Fault **414/414** (suite.sh); 3× each, all green: InstrumentCheckTests + VenueCatalogTests + PaperEligibleVerdictTests + OrgLedgerTests + SuiteReachesNoVendorTests + VenueIncrementTests 55/55, PaperConnectorTests (Fault) 2/2, VenueOverPipeTests (Integration) 5/5.
CI run **37100662391** on `a39e658`: ubuntu success (12 min), macos success (16 min), windows success (41 min), package success (4 min); the superseded run 37100468067 on `07c420c` (same code, three tests fewer): all four success. Names vs `main`: base **2090** → tip **2104**, removed **0**, added 14.
1 — DONE: rung 29 `instrument_check` (the brief's 15 columns); `InstrumentCheckStore` is its only writer (append-only, origin read off the URL, outcome validated); `DROP TABLE instrument_check;` added to VenueCatalogTests, PaperEligibleVerdictTests and `OrgLedgerTests.StampBack` (and that file's crash block).
2 — DONE: `InstrumentVerifier` (Provisioning): the shape is compiled in (`VenueCatalog.DefinitionShape`); a `venues.json` `definition_url` override is honoured on the same origin only, any other origin is recorded `refused-origin` and nothing is sent; 10 s leash, 4 MB bound, no redirects; a failure is a row and an activity line; it runs at start (background loop), on a pair change and on Check now.
3 — DONE: `VenueStore.Instruments/Instrument/Catalogue` are the one served read (the latest verified check of 7 days or less over the catalogue, a disagreeing catalogue number kept beside it), on each reader's clock: Backtests, ForwardRuns, Referee, CampaignStore.Open, TradingGateway (venue-list, status); the paper connector reads `CatalogueNow` at every use, which both hosts pass as `ConnectorChoice.PaperInstruments`.
4 — DONE: the Market data card line, one-press Check now and a check on pair change; `status.instrument_check`; venue-list `checked_at/check_url/min_quantity/min_notional/catalogue_*/says`; the six texts (plus the agent schema, the venue-list note, a CLI comment) name the in-app check; CONTRACTS (claimed / NOT claimed / advisory until containment), USER-GUIDE, RESEARCH-REQUIRED C5c.
Deviations: (a) a checked pair the catalogue holds no row for is served from its check, on a venue it holds — else picking ETHUSDT would record a verified check nothing serves and leave a file as the only route; (b) the app also re-checks every 6 h, so a running app never lapses at 7 days; (c) the served read is VenueStore's own reads, so the cost model (`VenueCostModel.For`) reads it too; (d) a fifth commit adds three tests (redirect, 4 MB, a check-only row).
Assertions changed, none removed or renamed: VenueIncrementTests, PaperConnectorTests (Fault) and VenueOverPipeTests (Integration) asserted `Contains("venues.json")`; each now asserts `Contains("Check now")` and `DoesNotContain("venues.json")`.
RED before (base behaviour on today's API, at item 3): (a) with the served read answering the catalogue's own rows: `InstrumentCheckTests.cs:143 Assert.True() Failure Expected: True Actual: False`; (e) with the connector reading its catalogue once at construction: `InstrumentCheckTests.cs:245 Assert.Single() Failure: The collection was empty`.
Mutants, each reverted: (i) the 7-day freshness removed ⇒ (c) `InstrumentCheckTests.cs:198 a check older than seven days still verified the instrument`; (ii) the origin check removed (at item 2) ⇒ (d) `Assert.Equal() Failure: Strings differ Expected: "refused-origin" Actual: "verified"`; (iii) is (e)'s red-before line.
Scan: three hits, all `leash.Token` (the request leash's CancellationToken), excluded by name (`CI_SCAN_EXCLUDE='leash\.Token'`); no trailers.
NOT done / NOT VERIFIED: the card on screen (app not run: its start would call the real host and could resume the AI); the AppHost triggers (start, six-hourly, pair change, Check now) have no automated test, build only; Windows only through CI; minimum notional recorded, not applied; a `venues.json` `"verified": true` is still honoured (advisory until containment, said in CONTRACTS).
One real request: `GET data-api.binance.vision/api/v3/exchangeInfo?symbol=BTCUSDT` at 2026-10-03 04:48:54Z → 200 in 1.03 s, tick 0.01, step 0.00001, minQty 0.00001, minNotional 5; no test reaches a vendor (SuiteReachesNoVendorTests gained an InstrumentVerifier rule). `main` has moved since by docs only (`a68b14b4`); not rebased, so the CI-tested sha stands.
