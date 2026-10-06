# U-flatten-absence — where a connector's closes provably carry our client id, a lost loss-budget close that a complete history never saw is settled as never sent, and the book is closed again
**Protects:** `CLAUDE.md` money rules 1–3: absence is proof only where the id provably round-trips on a CLOSE and the history provably reaches back; everywhere else a lost close
stays undecided, for the owner. Split from `U-flatten-confirm` at its dispatch (seat P, 2026-10-06; the survey's item 3 and decision 1, accepted by the orchestrator on the
owner's behalf). **Re-checked by seat P against `main` `32d654b7`, after `U-flatten-confirm` landed (merge `f461b19f`, record `2952c285`).** Fresh builder, seat P; no rung.
**Facts (SOURCE at `32d654b7`; NOT runtime-verified; `TG` = `src/TradeAgent.Gateway/TradingGateway.cs`).** The confirm step is `ConfirmLostClosesAsync` (`TG:9200`); per leg it reads
`since = CreatedAt − 5 min` (`:9362`) and today ends a leg neither found nor filled with "… on its own that proves nothing" (`:9394`, undecided); `DecidesALostClose` `:9403`. `ReconcileAsync`'s
absence rule for an UNKNOWN order that is not a press row (`TG:10686-10693`): no order by id in `GetOrdersAsync(acct, true, since)` (`:10652`) and no fill by id, past
`AbsenceCountsFrom` (`:1794`; a press leg has no `HoldDispatch`, so dispatch + `DispatchStrandedAfter`, `:2821`) + `AbsenceGrace` 15 s (`GatewayTypes.cs:47`) → CANCELLED; only
if `ReconciliationProvable` (`ConnectorSdk/Contracts.cs:124`); a read that throws decides nothing. A `since` read answers completely or THROWS (ATAS refuses an uncovered window
and checks nothing for a null `since`, `AtasBridge/AtasStrategyAdapter.cs:1015-1026`; the Fake throws under `HideOrderHistory`, `Connectors.Fake/FakeConnector.cs:304`).
*The trap this unit is built around:* `SupportsClientOrderId` is proven on PLACE orders; an ATAS CLOSE carries our id only as a best-effort label written after identification
onto an empty comment (`AtasStrategyAdapter.cs:1927-1931`, `:2097`, `:2123-2126`, read back `:2941`; a box run's filled close bore ATAS's own "Close position", `:2027`) — on
ATAS, no order under our id proves nothing. The Fake and Paper close under the given id (`FakeConnector.cs:419-426`, `Connectors.Paper/PaperConnector.cs:445-456`).
Drivers: `FakeConnector.cs:347-369` (`DropBeforeBrokerAccept`), `FakeBroker.CountByClientOrderId :31`.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `U-flatten-confirm`'s record (`BUILD-STATUS.md`, 2026-10-06); `FaultTests/LossFlattenConfirmTests.cs`; `ConnectorSdk/Contracts.cs:116-124`.
Must NOT: read "absent" off a read that threw, a null `since`, fills, inside the grace, or for a connector that does not claim its closes carry the id; set the claim on ATAS or
on any connector without a test proving its close carries the given id; change `SupportsClientOrderId` or `ReconcileAsync`; fake an id or a claim; change anything else
`U-flatten-confirm` decides; raise a timeout, retry, widen a budget, drive a UI, add a shim.
Item, one commit, one-sentence message: `ConnectorCapabilities` gains init-only `ClosesCarryClientOrderId` (default false; true on the Fake and Paper, each with a test proving its
close carries the given id; unset on ATAS, its doc saying why with the lines above); with it and `ReconciliationProvable`, no order and no fill under the leg's id on a `since` read
that returned, past the grace, decides the leg CANCELLED — "never reached the platform" — in `U-flatten-confirm`'s confirm step, which then proceeds as for any decided leg
(flat → confirmed; open → one fresh close). CONTRACTS and the connector SDK's docs say so.
Proof, in `LossFlattenConfirmTests`; RED before, quoted: (ii) `DropBeforeBrokerAccept` → nothing inside the grace, then CANCELLED and one close under a new id
(`CountByClientOrderId`), flat, none after. The `"plain"` case of `A_lost_close_the_platform_never_saw_decides_nothing_past_the_grace` (`LossFlattenConfirmTests.cs:311`) becomes (ii) —
the one assertion this unit flips, named in the report; its old claim lives on as (vii); its other two cases are (iii) and (iv). Guards, green before and after, on (ii)'s drive past the grace: (iii) `HideOrderHistory`, (iv) a `since` read that throws, (vii) the claim
off → `Closes` 1, UNKNOWN flagged, unresolved. ONE mutant, quoted: "history unavailable read as absent" → (iii) red, a second close on the wire. ATAS: NOT VERIFIED, said.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed; touched
classes 3×; branch CI on all three platforms; tests box or NOT RUN; names vs `main` 0 removed (both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
