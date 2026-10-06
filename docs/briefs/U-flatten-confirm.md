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

## Report
**Tip (code) `ac62ca92`** on `main` `69589886` — rebased once after the first `ci-dispatch.sh` was refused by the scan on a line of R-containment's brief, deleted on `main` meanwhile (no conflict, nothing excluded); commits `0d830a2b` (item 1), `ac62ca92` (item 2), then this report. No rung.
**Gate at `ac62ca92`:** build `-c Release --no-incremental` → 0 Warning(s), 0 Error(s). Unit `Passed! - Failed: 0, Passed: 1393` (7 m 52 s); Fault `Passed! - Failed: 0, Passed: 430` (1 m 50 s); `LossFlattenConfirmTests` 3× `Passed: 10`
each (pre-rebase also LossFlatten/LossFlattenOwed/ValuationLoss/LossReopen/LossRelease 3× `Passed: 28`). CI run 37396439616 at `ac62ca9`: ubuntu-latest success (12 min), macos-latest success (16 min), windows-latest success (52 min),
package success (4 min). Names vs `main`: base 2169 → tip 2177, removed 0, added 7 tests + the helper `Seam`; [Fact]/[Theory] 2125 → 2132. Tests box: NOT RUN — `ready : NO - the machine does not answer` (exit 1, asked once).
**Item 1 — done** (`0d830a2b`): `CancelWhileUnvaluableAsync` and `ExitLostValuationAsync` open `BeginExcludingTheStore`; `RiskReducingScope` doc and CONTRACTS say so. RED before (item reverted): (vi) `Expected: 1 Actual: 0` closes,
"store held by another : 3004 ms against a 2000 ms budget", exit flat=False "… the operation deadline had already passed and nothing was sent to the simulator"; green after.
**Item 2 — done** (`ac62ca92`): `ConfirmLostClosesAsync` at the start of the health pass (after the account read, before the execution row and the watch); per lost leg `ReconcileAsync`'s window and first two questions, no absence,
only where `ReconciliationProvable`; ONE `AddKvOnce` `loss_flatten_confirm:` record (verdicts + read-back) written BEFORE any row is settled, then `SettleTheUnresolved`'s two steps and the outcome's two nonces unflagged; still open →
the sweep runs the same `FlattenForBreachAsync` (`again`, fresh nonce, `loss_flatten_again:` / `loss_flatten_again_owed:`); `LatestFlattenWord` feeds `HeldBy`, `FlattenStateToday`, `FlattenFlagFor`; CONTRACTS, schema, AGENTS.md say so.
RED before (item reverted): (i) `Expected: FILLED Actual: UNKNOWN`; (v) both arms `Expected: FILLED Actual: UNKNOWN` after `FillWorking`; (viii) `Expected: REJECTED Actual: UNKNOWN`, closes 1, ES 1; the one-confirm test the same.
Guards green at base product code (7/7) and after, unchanged: (ii)–(iv), `LossFlattenTests.cs:439`, `LossFlattenOwedTests.cs:372`, `LossReopenTests.cs:194`, `LossReleaseTests.cs:128`. **Mutant:** `DecidesALostClose(s) => IsTerminal(s)
|| s == WORKING` → (v)[LeaveWorking] red `Expected: 1 Actual: 2` closes ("the close at the book : Sell 2 CANCELLED", cancels 0 -> 1); restored, sha256 OK.
**Declared deviations:** (a) beyond the brief: a symbol's closing again that the day's flatten subsumed never runs, so `LatestFlattenWord` gives it no word (as a subsumed first attempt) instead of holding it for ever on a false
"closing again" — own test, RED with that hunk alone `Expected: "flat" Actual: "unresolved"`; (b) `SettleTheUnresolved` takes only the second step for a row already RECONCILING (a killed confirm) — every other caller passes
UNKNOWN rows, unchanged; (c) "only UNKNOWN close legs unresolved" read strictly: a non-final cancel-half row, or a lost leg someone else (the owner) has since settled, means no confirm — that hold stays owed; (d) a USER-GUIDE paragraph.
**NOT done / NOT verified:** ATAS NOT VERIFIED (no box; that its lost closes mostly stay undecided is read from the adapter's code, not run); no test drives a pass killed between record and settle, or a failed `AddKvOnce`;
the data-loss exit's own lost close (`op-valuation-close-`) and absence as proof not built (owed: U-valuation-close-confirm, U-flatten-absence); the Integration suite ran only on CI; the app was not run.
