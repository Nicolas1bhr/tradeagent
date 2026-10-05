# U-flatten-confirm — a loss-budget close whose answer was lost is settled from the platform's order history where that history finds it, and closed again only once every leg is decided and the book is still open; the data-loss exit stops charging its own store to its budget
**Protects:** `CLAUDE.md` money rules 1–3 on the money path — never a blind re-send, never "absent" off a history not shown to reach back, never a faked id — and the budget's
purpose: no closed day sits for ever over a book TradeAgent could close. Owed: `U-fix-loss-reopen`'s judgements 2–3 (`BUILD-STATUS.md` 2026-10-04). Fresh builder, seat P; no rung.
**Split at dispatch (seat P, 2026-10-06):** "absence as proof" is its own unit, `U-flatten-absence`, after this one — HERE absence decides nothing. **Rulings (the orchestrator,
on the owner's behalf):** the conservative decisions below are accepted as written; a book still open once every leg is decided IS closed again (the flatten re-reads before the wire).
**Facts (SOURCE at `2a12951c`; read by a survey, spot-checked by seat P, NOT runtime-verified; `TG` = `src/TradeAgent.Gateway/TradingGateway.cs`).** *The possibly-sent close:*
`FlattenForBreachAsync` (`TG:8072`, one `TransportRecord` `:8180`) writes its leg `op-budget-close-{nonce}-{i}` flagged before the wire (`:7624`), carrying `TA-{requestId}`
(`:5626`) into `ClosePositionAsync` (`:9663`); a throw or a null is UNKNOWN + flagged (`:9670`, `:9680`). Any dispatch makes `wire.Outcome` non-null (`:8249`), so the write-once
`loss_flatten:` outcome says `Flat=false` (`:8290`) and nothing moves it: the sweep returns on `HasUnconfirmedWork()` and keys on that outcome's absence (`:3791`, `:3811-3816`),
the flatten returns on it (`:8138`), `ReconcileAsync` skips press rows (`:9854`), `HeldBy` holds on `!Flat` (`:4091`). *History settles an UNKNOWN today, never a press row:*
`ReconcileAsync` (`:9747`), only if `ReconciliationProvable` (`:9873`; `ConnectorSdk/Contracts.cs:124`): by client id in `GetOrdersAsync(acct, true, CreatedAt − 5 min)`
(`:9882`) → `Adopt`, definite states only (`:10139`); else fills by id → FILLED; else absence → CANCELLED (`:9933-9942`, NOT used here); a throw decides nothing;
`SettleTheUnresolved` (`:7927`) is the two-step settle. A `since` read answers completely or THROWS (ATAS refuses an uncovered window and checks nothing for a null `since`,
`AtasBridge/AtasStrategyAdapter.cs:1015-1026`; the Fake throws under `HideOrderHistory`, `Connectors.Fake/FakeConnector.cs:304`); fills prove presence only (`TG:1781-1787`).
*Why ATAS will often stay undecided:* `SupportsClientOrderId` is proven on PLACE orders; an ATAS CLOSE carries our id only as a best-effort label written after identification
onto an empty comment (`AtasStrategyAdapter.cs:1927-1931`, `:2097`, `:2123-2126`, read back `:2941`; a box run's filled close bore ATAS's own "Close position", `:2027`) — a lost
close found by neither id nor fills stays flagged, for the owner, as today. The Fake and Paper close under the given id (`FakeConnector.cs:419-426`, `Connectors.Paper/
PaperConnector.cs:445-456`). *Drivers:* `FakeConnector.cs:347-369` (`RejectNext` is recorded UNKNOWN, `FaultTests/LossFlattenTests.cs:425-432`); `FakeBroker.CountByClientOrderId :31`,
`FillWorking :169`; `tests/Shared/RecordingConnector.cs:200` reads history ungated (knobs `:131`, `:173`). *The exit:* `ExitLostValuationAsync` (`TG:9125`), `Begin` `:9156`, cancel `:9064`.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `docs/CONTRACTS.md:1224-1304`, `:3695-3736`; every `TG` line above; `LossFlatten.cs`; `LossFlattenOwedTests.cs` (whole).
Must NOT: re-send a possibly-sent close blind; read "absent" at all (a leg neither found nor filled is undecided); decide off a read that threw or a null `since`; fake an id or a
claim; touch an owner's press row, an `op-valuation-` row, the breach row or a `loss_flatten:` row; send while anything else is unconfirmed; change `ReconcileAsync`, the 2 s
value, the owner's presses, the sweeps' rules, `EligibleAt` or two-pull confirmation; raise a timeout, retry, widen a budget, drive a UI, add a shim.
Items, one commit each, one-sentence messages: 1. **The exit's budget:** `:9156`, `:9064` open `BeginExcludingTheStore` as `:8173` does; its doc (`RiskReducingScope.cs:130`), CONTRACTS say so.
2. **Confirm from history**, at the START of the health pass (before `:10370`), for a standing closure of this connector, mode and account whose final outcome is `!Flat` with no
   unsettled opener and only UNKNOWN close legs of its own nonce unresolved. Per leg, `ReconcileAsync`'s rules without absence: found by id and terminal → its state and fill;
   fills → FILLED; found live (WORKING, ACK, PARTIAL, CANCEL_PENDING), not found, or anything else → undecided: nothing settled, cancelled or sent, asked again next pass. All
   decided → each settled by `SettleTheUnresolved`'s steps, its nonces' flags cleared as `:8748-8755`, ONE write-once (`AddKvOnce`) record in its own family (as
   `LossFlatten.OwedPrefix`): each verdict and a read-back — flat → `Flat`, nothing sent; open → "closing again": the sweep, while nothing is unconfirmed, runs the SAME
   `FlattenForBreachAsync` (fresh nonce), outcome and owed note in own families, owed and every-pass as `U-fix-loss-reopen`. A failed write sends nothing; a killed pass finishes
   on the next; ONE confirm per breach (a second lost answer stays for the owner). `HeldBy`, `FlattenStateToday` (`:2147`), `FlattenFlagFor` (`:4944`) read the latest word via
   ONE accessor; CONTRACTS (the app now settles its own press rows from history), AGENTS.md (`WorkspaceBuilder.cs:412`), the schema (`GatewaySchema.cs:126`) say so.
Proof — new `FaultTests/LossFlattenConfirmTests.cs` on `LossFlattenOwedTests`'s 2 s fixture; RED before its item, quoted: [1] (vi) `ValuationLossTests` at 2 s, the store held by
a second writer past it (`LossFlattenOwedTests.cs:151-173`) → the exit's close goes out, flat; [2] (i) `DropAfterBrokerAccept` → FILLED, confirmed flat, `Closes` 1, unflagged,
still closed; (viii) `RejectNext` → REJECTED, one fresh close, flat; (v) `LeaveWorking`/`PartialFill` untouched, then `FillWorking` → as (i). Guards, green before and after:
`DropBeforeBrokerAccept` past the grace — plain, under `HideOrderHistory`, a `since` read that throws → `Closes` 1, UNKNOWN flagged, unresolved; `LossFlattenTests.cs:439`,
`LossFlattenOwedTests.cs:372`, `LossReopenTests.cs:194`, `LossReleaseTests.cs:128` unchanged. ONE mutant, quoted: "a leg found WORKING counted as decided" → (v) red, a second
close on the wire. ATAS: NOT VERIFIED (no box for this unit), said.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed; touched
classes 3×; branch CI on all three platforms; tests box or NOT RUN; names vs `main` 0 removed (both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
