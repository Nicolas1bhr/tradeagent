# U2b — approval re-authorization, approval TTL, one clock — INTEGRATED

On `main` as 8 commits `133c1bd..3f1d8f2` (rebased from branch tip `c0ac430`; the verify records cited the old shas
`1b64130..c0ac430`). Tier 1. Reconstructed 2026-09-03; the 286-line build record and 555-line verify record were lost.

## The defect

`TradingGateway.ApproveAsync` (283d942 `:488-498`) took a parked `AWAITING_APPROVAL` request straight to
`DispatchPlaceAsync`, re-running neither `TryAuthorizeExecution` nor `RiskCheckOrThrow`: mode, kill switch, live
activation, account, health chain, allowlist, quantity/position/rate limits, quote freshness and the notional cap were
whatever they had been when the order was parked. Verifier probes: 10 of 10 changed conditions still dispatched on
approve (kill switch, OBSERVE, PAPER onto a real account, cleared account, switched account, 5-minute-stale quote,
unreconciled pause, FAILED connection, and four risk limits).

## The design (followed as written)

An approval is a dispatch decision, authorized at the moment it is made, all under `_dispatchGate`: (a) record exists and
is `AWAITING_APPROVAL` else `INVALID_REQUEST`; (b) **age first**: `age < 0 || age >= ApprovalTtl` (default 15 min — a
judgment; `0` expires everything; a future timestamp is expired, "TradeAgent cannot tell how old the order is") →
`AWAITING_APPROVAL → CANCELLED` through the state machine with `last_error`, activity line, `StateChanged`, and the new
`APPROVAL_EXPIRED` (catalogue row with all four fields); (c) mode must still be `LIVE_CONFIRM` else
`MODE_FORBIDS_EXECUTION`, record stays parked; (d) `AuthorizeOrThrow` with the PROPOSER's context, never the operator's,
so the kill switch refuses with `AI_TRADING_STOPPED` (re-enable, then approve: two deliberate acts); (e) the platform
must be the one the order was parked on (`Connector.Id == stored.ConnectorId`, refused as `ACCOUNT_NOT_FOUND` — honest,
since after a switch the account genuinely is not on the connected platform) and the account must match; (f)
`RiskCheckOrThrow` exactly as `PlaceAsync`; (g) then log and dispatch. Any refusal but the TTL leaves the record parked.
`GatewayOptions.Clock` (`TimeProvider`) replaced six `DateTimeOffset.UtcNow` reads; the `ExecutionRequestStore` takes the
same clock (only `dispatched_at` had been on the store's own clock). `LogStore`/`OnboardingStore` deliberately stay on
UtcNow (nothing measures a duration across them). Wire: `docs/CONTRACTS.md` and `GatewaySchema.approval` describe the
checks in the order the code runs them; expiry is not on a timer (a request can be past the limit and still listed as
awaiting approval); a replay returns whatever the record now says.

## Verification

Round 1: Codex 3H/2M (connector identity; store clock split; negative age; TTL-0 boundary; await-window atomicity → U2c-2);
Opus FAIL 2M/4L (allowlist and notional×ContractSize invisible to the approval tests; negative age). Round 2: Opus
**PASS WITH LOW** — both MEDs closed, the builder's nine mutants reproduced (A 16 RED, B 5, C 1, D–I 1–2 each), the
store's `Now` pinned by an extra mutant, three doc LOWs fixed in `c0ac430`. Suite 328 → 329 on main after rebase; CI
green on all three OS + package.

## Left for other units

- The await window between `AuthorizeOrThrow` and the wire (both `PlaceAsync` and `ApproveAsync`) → U2c-2.
- A 5-second sweep of expired parked requests (needs `AppHost.cs`) → U2c-2; today expiry is judged only on a press and
  the Dashboard row shows an approve-by time.
- Two LOW observations: `ErrorCatalogueTests` cannot detect a missing catalogue row (`Errors.Get` falls back to
  `UNKNOWN_ERROR`); a sub-second backward NTP step cancels a good proposal (fail-closed trade-off, kept).
