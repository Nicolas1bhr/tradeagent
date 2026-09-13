# U-venue-catalog — the instrument as a recorded fact with a source, not a number an agent typed into a request
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md:145,152` (bars "with instrument increments"; size "rounded down to the increment, then
the gateway's limits"), `:95` (calendars per connector and per instrument), `:239` (venue capabilities before unattended real money),
`docs/DECISIONS.md:73-78` (vendor facts are data, not code, with a `Verified` flag), then `Core/Strategy/Backtest.cs:21-25,67,89-133` (the run's
increment "because the dataset has none"; `ExecutionModel.Canonical`), `Data/BarFeed.cs:65-69`, `Gateway/Backtests.cs:85`, `Db/DatasetStore.cs`
and `Database.cs:444-481,747-748`, `ConnectorSdk/Contracts.cs:12-13` (`InstrumentInfo`: `TickSize`, `ContractSize`, nothing else),
`Connectors.Fake/FakeBroker.cs:45-60` (four hard-coded futures; `TickSize` null for an unknown symbol), `GatewayPipeServer.cs` (`data-list`,
read ops), `App/SettingsView.cs:161-208` (Market data and the holdout card). Branch `u-venue-catalog`, worktree `~/Projects/ai-trading-
software-for-mihael-worktrees/U-venue-catalog`, rebased onto `main` first. Every item ships with a test RED before it and ONE mutant,
quoted. NOT the money path: nothing here reaches `PlaceAsync` (`U-freshness` does). No box, no ATAS, no money. **Schema 17.**

**Why.** COUNCIL requires an increment on the bars and a size rounded down to it, and no increment exists anywhere: every backtest's
increment is a number the agent typed (`Backtests.cs:85`), defaulting to `Frictionless` 1 on a pair whose real step is 0.00001. The word
"venue" exists only in comments: no table, no type, no calendar kind. Until an instrument is a recorded fact with a source and a verified
flag, a backtest's size and a later live intent have nothing in common to be bound to (`U-freshness`, `U-referee-2`'s promotion).

1. **`venue` and `venue_instrument`, app-owned data:** venue id, display name, calendar kind (`continuous`/`sessioned`); symbol, `tick_size`,
   `quantity_increment`, `source`, `recorded_at`, `verified` — shipped as a built-in catalogue (the Binance spot pair the app collects, the
   simulator's four futures) overridable by `venues.json` under the app's data folder, `Verified=false` unless the row says who verified it.
   RED: `no such table: venue_instrument`. Mutant (an unverified row served as verified): a catalogue with `Verified=false` serves silently.
2. **No pipe op writes it; `venue-list` reads it** (a read op, not in `Ops.Mutating`, the `data-list` shape) and `trade venue list` shows it. RED:
   `the schema does not name 'venue-list'`. Mutant (`venue-list` in `Ops.Mutating`): refused for the one role that needs it.
3. **A dataset names its venue and instrument:** `dataset.venue_id`, `dataset.instrument_symbol`, nullable, backfilled `binance-spot`/pair for
   existing rows; `data-list` and section 8 carry them. RED: `data-list` carries no venue for a recorded dataset. Mutant (the venue taken from
   the request, not the row): a caller renames the source of its own evidence.
4. **A run's increment comes from the catalogue when the request omits it; a declared one still wins and only the declared model is hashed**,
   so no existing run id moves. RED: a request with no `--increment` runs at 1 on a pair the catalogue records at 0.00001. Mutant (the catalogue
   value folded into `Canonical`): `BacktestDeterminismTests` red — every run id changes.
5. **A refusal, never a guess:** an instrument the catalogue does not hold, or holds unverified, is refused in words when no increment was
   declared (the `FakeBroker.TickSize` null and `ContractSizeOrThrow` shape). RED: an unknown symbol silently runs at increment 1. Mutant (the
   refusal returning 1): the same. `CONTRACTS.md` and the guide describe the catalogue and what it does not claim (no fee, no min notional —
   COUNCIL is silent; fees stay declared per run, `:155`).

Not this unit: `PlaceAsync` or the gateway's risk pass (`U-freshness`); fees or min notional as fields; prop-firm rulebooks; live instrument
reads from ATAS; the box; a real venue.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, what you did NOT do.
