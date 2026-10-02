# U-venue-verify — the app checks an instrument against the venue's own published definition and serves it verified, with nobody editing a file
**Arrow closed:** paper allocation → deployment → sizing, and the cost model's increment (`manager-prompt.md` § 5; `docs/EDGE-FACTORY.md` § 6.9). **Dispatch only
after `U-cost-model` (schema 27) AND `U-tape-store` have landed** — rung 28 must never land before 27 (R09 § 7), and both edit the Market data card and
`AppHost.StartAsync`. **Today (SOURCE at `0f47db7`, R09/R11-checked):** BTCUSDT ships `Verified = false` (`Core/Data/VenueCatalog.cs:174-186`); the paper connector
offers verified rows only (`PaperConnector.cs:203,215`) and reads the FILE catalogue once at construction (`:101`; options built at `Platforms/Connectors.cs:87-90`);
the runner sizes nothing without a verified row (`ForwardRuns.cs:492-516`); the only route to `Verified = true` is a hand-edited `venues.json` (`VenueCatalog.cs:105`)
that no screen writes, and six texts tell someone to edit it (`USER-GUIDE.md:935-963`, `Backtests.cs:390-396`, `PaperConnector.cs:189-190,326`, `Protocol.cs:54-57`,
`VenueCatalog.cs:150-156`) — a terminal-shaped instruction. That file is also agent-writable (`Containment.cs:43`). **Observable result:** choosing BTCUSDT makes the
app fetch Binance's public instrument definition from the BUILT-IN origin, record the check, and serve BTCUSDT verified with the venue's numbers, URL and instant
to research, the cost model, the paper connector and the runner; a failed or stale check, or a definition URL whose origin is not the built-in one, leaves it
unverified and says why. **Schema 28.**
RUN 2026-10-02 from this Mac, no key: `GET https://data-api.binance.vision/api/v3/exchangeInfo?symbol=BTCUSDT` → 200 ~0.3 s; `tickSize 0.01`, `stepSize 0.00001
minQty 0.00001`, `NOTIONAL minNotional 5`. The host is already in `SuiteReachesNoVendorTests` (`ForwardHost`, `:66`). Never call a real host from a test.
Read first: `CLAUDE.md`; `VenueCatalog.cs` (whole); `Core/Db/VenueStore.cs` (`Sync` :71-115, called at `TradingGateway.cs:1563`); `PaperConnector.cs:90-110,181-215,
320-330`; `Platforms/Connectors.cs:80-95`; `ForwardRuns.cs:492-516`; `Backtests.cs:341-396`; `ForwardBarCollector.cs` (leash `:46`, body bound `:52`, a row per
attempt `:243-257`); `App/SettingsView.cs:178-221`; `USER-GUIDE.md:935-963`; `FakeArchive.cs`; the `U-key-host-pin` origin rule as landed.
Items, one commit each, one-sentence messages:
1. Schema 28: `instrument_check` (id, venue_id, symbol, url, origin, requested_at, received_at, http_status, body_sha256, tick_size, quantity_increment,
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
