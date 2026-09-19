# U-paper-adapter — the app's own PAPER connector: real advancing prices in, simulated fills out, persistent, idempotent, no way to reach a venue
**Arrow closed:** the execution model of forward paper (`manager-prompt.md` § 5 "Actual market observation": "an app paper adapter over live data if that is the
smallest fit"; Astra 2026-09-19: "the adapter must possess only simulated execution capability"). **Observable result:** a connector `paper` the gateway drives
like any other — an order placed through `PlaceAsync` in PAPER mode is accepted at once and FILLED at the next closed bar's open plus declared slippage with a
declared fee; stops and targets fire per bar as the backtest fires them; the book survives a restart; re-processing a bar writes no second fill. No app schema
rung: the adapter keeps its own SQLite file. Not the live money path: this connector cannot reach any venue by construction, and a test proves it.
Read first: `docs/PRINCIPLES.md` § Evidence; `docs/CONTRACTS.md` "The backtest" (2019-2088: fills at the next open, a stop at min(level, open) on a gap, both
touched → the stop, fees on every fill, sizes rounded DOWN); `src/TradeAgent.ConnectorSdk/Contracts.cs:9-19,40-60,78-141,172-235` (`ITradingConnector`,
capabilities, commands, the two exception kinds); `src/TradeAgent.Connectors.Fake/FakeBroker.cs:62-198`, `FakeConnector.cs:10-63,221-281` (the template; it
has NO injectable clock — yours must); `TradingGateway.cs:1544-1546`; `AppHost.cs:499-503,663-667`, `GatewayHost/Program.cs:29-30` (the connector ternaries);
`Core/Data/DatasetReader.cs:7` (`KlineBar`); `Paths.cs:54,81`; the Fake connector's tests for the harness shape. Rebase onto `main` first; `Database.cs` is not yours.
Items, one commit each with a one-sentence message:
1. Project `src/TradeAgent.Connectors.Paper` in `TradeAgent.sln`, referenced by `TradeAgent.App` and `TradeAgent.GatewayHost` as Fake is: `PaperConnector :
   ITradingConnector`, `Id = "paper"`, capabilities `IsPaper, SupportsClientOrderId, SupportsOrderHistory, SupportsModify, SupportsClosePosition` true,
   `SupportsStreaming` false; one account `PAPER-1` (`IsSimulated` true; currency and starting equity declared at creation, 10,000 USDT by default); instruments
   from the venue catalogue's verified spot rows, increment honoured; a `Func<DateTimeOffset>` clock; an `IPaperBarSource` (`SinceAsync(symbol,
   openTimeExclusive)` + a `BarClosed` event — the forward-bars ledger will implement it later; tests use an in-memory one). It references NO HTTP client and no
   venue SDK; a test reflects over the assembly's references and says so.
2. The book, persistent and idempotent: its own SQLite file `state/paper-<account>.db` (`Paths.State`), versioned inside the file — orders (client order id as
   the key, symbol, side, type, quantity, limit, stop, tif, state, placed_at, filled bar), fills (UNIQUE (order, bar_open_time), price, fee, at) and the
   average-cost position per symbol. Every SDK read (`GetOrdersAsync`, `GetExecutionsAsync` since a timestamp, `GetPositionsAsync`, `GetAccountAsync` with equity
   = starting + realised − fees, `UnrealizedPnl` off the last closed bar and NULL when there is none) comes off that file: a new instance over the same file
   answers the same book.
3. Settlement per closed bar, in the backtest's order: a MARKET order placed at t fills at the open of the first closed bar with open_time > t plus adverse
   slippage; a STOP at the open on a gap-through, else at its level; a LIMIT at its level; a bar touching both a position's stop and target counts the STOP; the
   declared fee fraction on every fill; sizes rounded DOWN to the increment; no fee and no slippage declared = FRICTIONLESS, said on every fill and in the health
   detail. Fee and slippage are `TradeAgentSettings.PaperFeeFraction` / `PaperSlippageFraction` (default 0, stated). Modify and cancel are immediate while
   WORKING; `ClosePositionAsync` is a market order the other way. `ConnectorRejectedException` only for a definite refusal (unknown symbol, size below the
   increment, cancelling a filled order); anything else propagates.
4. `Connectors.Create(id)` replaces both ternaries (`fake` | `atas` | `paper`); the connector picker offers "TradeAgent paper — real prices, simulated fills"
   beside the simulator; `CONTRACTS.md` new section "The paper connector" (a paper fill is a declared simulation at the next open, never evidence that the price
   was executable; what the connector cannot do); `USER-GUIDE.md` one paragraph.
Red-first tests (Fault/Integration, the in-memory bar source, an injected clock): (a) `A_market_order_fills_at_the_next_closed_bars_open_plus_slippage_never_at_
the_bar_already_closed`; (b) `A_stop_fills_at_the_open_on_a_gap_a_target_at_its_level_and_both_touched_is_the_stop`; (c) `A_new_instance_over_the_same_file_reads_
the_same_book_and_re_processing_a_bar_writes_no_second_fill`; (d) `Orders_and_executions_since_a_timestamp_reach_back_to_it`; (e) `The_client_order_id_round_
trips`; (f) `The_gateway_in_PAPER_mode_places_through_the_paper_connector_and_a_live_mode_refuses_its_account` (through `PlaceAsync`, the real path);
(g) `The_assembly_references_no_http_client`. Mutants to watch red and quote: (i) fill at the placing bar's close → (a) red; (ii) the fills' UNIQUE key dropped
→ (c) red. Nothing removed or renamed; `FakeConnector` untouched.
Gate and report as `docs/HOW-WE-BUILD.md`: `--no-incremental` Release build 0 warnings; the three suites 0 failed; touched classes 3×; names vs `main` 0
removed; `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
