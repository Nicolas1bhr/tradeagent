# U-deployment — a paper deployment is an app-owned record: lineage, an execution identity the pipe cannot mint, a bar cursor, write-ahead operations
**Arrow closed:** paper allocation → deployment (`manager-prompt.md` § 5 "Forward execution": an explicit app-owned execution identity with lineage back to
the deployment and the research; persisted progress and unresolved operations so restart, replay, replacement or cancellation cannot duplicate exposure).
**Observable result:** a standing paper allocation starts ONE deployment by app policy; an operation is written before it is dispatched and a crash between
the write and the answer resolves on restart with no second order at the wire; a connector, mode or account switch suspends the deployment and never
retargets it; ending cancels, flattens through the gateway and records why. **Schema 26** (after `U-paper-envelope`'s 25). MONEY PATH: a new in-process
caller of `PlaceAsync` and the close mechanics, PAPER only — a test proves the identity is refused in any live mode.
Read first: `docs/PRINCIPLES.md` §§ boundary, loop; `docs/CONTRACTS.md` "The capital allocation", "U-paper-envelope"; `GatewayTypes.cs:183-240` (`AgentContext`),
`:250-290` (`PlaceIntent`); `TradingGateway.cs:4117-4123`, `:4200`, `:4227-4259`, `:4281-4312` (`ClientOrderIdFor`, `IsSendableId`, 64 chars), `:6713-6790` (how the
flatten gates on connector, mode and account and runs legs on flagged write-ahead rows — the pattern); `Stores.cs:59-81,145`; `AllocationStore.cs` after
`U-paper-envelope`; `MissionLoop.cs:1783-1787` (the periodic seam); `Database.cs` (rung 25 as the pattern). Rebase onto `main` first; resolve conflicts yourself.
Items, one commit each with a one-sentence message:
1. Schema 26: `strategy_deployment` — id (sha over version_id, allocation_id, envelope_id, connector_id, account_id, symbol, started_at), those columns, mode,
   state (`active` | `suspended` | `ended`), cursor_open_time (the last bar whose operations all resolved; NULL at start), started_at, suspended_reason, ended_at,
   end_reason; identity columns immutable, state written only by `Deployments.Start/Suspend/Resume/End`, one `Database.Write` each. `deployment_op` — request_id
   (`dp-<12 of deployment>-<bar open, unix minutes>-<seq>`, sendable, ≤ 64), deployment_id, bar_open_time, kind (`entry` | `exit` | `stop` | `target` | `cancel` |
   `flatten`), intent (the `PlaceIntent` as JSON), state (`planned` → `dispatched` → `resolved` | `refused`), answer, created_at, resolved_at. Rollback undoes 26.
2. The identity: `AgentContext.Deployment(deploymentId)` — in-process only (the pipe builds contexts from launch grants and cannot present this kind; a test says
   so), `MayPlaceOrders` true, refused by `AuthorizeOrThrow` in any live mode (`MODE_FORBIDS_EXECUTION`), session `deployment:<id>` on every `execution_request`
   row beside the version and the allocation. `PlaceAsync` under it passes every existing gate unchanged: freshness, ceiling, loss budgets, kill switch.
3. Policy and lifecycle, app-owned: `Deployments.StartDue(now)` (the periodic seam, after `AllocatePaperDue`) starts one deployment per standing paper
   allocation without one, while the envelope has room. `Reconcile(now)` at start-up and every tick: an op `dispatched` with an `execution_request` row takes
   that row's state (UNKNOWN stays unresolved and blocks the cursor, never re-sent); an op `planned` without a row is dispatched once (`TryCreate` refuses a
   duplicate request id); the cursor advances only when every op of its bar is `resolved` or `refused`. `SuspendIfMoved`: the gateway's (connector, mode,
   account) differing from the deployment's → `suspended` with the reason, nothing dispatched, resumed when they match again after a reconcile. `End(reason)`:
   envelope withdrawn or expired, allocation superseded, standing no longer paper-eligible or promoted, the owner's one-press-plus-confirm "Stop paper
   deployment", or the version's own role asking `trade deployment stop --id` — cancels working orders, flattens through the gateway's existing close mechanics
   under the deployment identity as `flatten` ops, then `ended`; a replacement waits for a flat, reconciled end.
4. `trade deployment list` (read, every role), `status.deployments`, report section 4 one line per deployment; `CONTRACTS.md` "The paper deployment";
   `USER-GUIDE.md` one paragraph.
Red-first tests (Fault/Integration, `TestEnv.Ready()`): (a) `A_standing_paper_allocation_starts_one_deployment_and_a_second_waits_while_max_deployments_is_full`;
(b) `An_operation_written_before_dispatch_resolves_on_restart_with_no_second_order_at_the_wire` (kill between the write and the answer; restart; wire count 1);
(c) `A_connector_mode_or_account_switch_suspends_and_never_retargets` (three arms; ids unchanged; nothing sent); (d) `Ending_cancels_flattens_through_the_
gateway_and_records_the_reason_while_the_allocation_row_survives`; (e) `The_deployment_identity_cannot_be_presented_over_the_pipe_and_is_refused_in_a_live_mode`;
(f) `Request_ids_are_sendable_and_unique_per_deployment_bar_and_sequence`. Mutants to watch red and quote: (i) the op written after dispatch → (b) red;
(ii) `SuspendIfMoved` reading the mode alone → (c) red. Nothing removed or renamed; column-list assertions extended and still exact.
Gate and report as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings; three suites 0 failed; classes 3×; names 0 removed; `## Report` ≤ 20 lines
here). No push, no merge; touch nothing in `docs/briefs/` but this file.


## Report
Code tip `41d3759` (this report is the one commit on top of it), rebased onto `main` `3746774` — three times as `main` moved, **no conflicts** on any of them; only my four commits replay, the base's envelope commits being on `main` already under other shas. Gate on that tip, after `U-language-in-home` landed: `dotnet build TradeAgent.sln -c Release --no-incremental` → **0 warnings, 0 errors**; Unit **1181 passed / 0 failed / 0 skipped**, Fault **399 / 0 / 0**, Integration **687 / 0 / 1 skipped** (11m12). Names vs `main`: **1919 → 1925, 0 removed**, the six below added. 3× each: `PaperDeploymentTests` 6/6/6, `VenueCatalogTests`+`PaperEligibleVerdictTests` 18/18/18, `ReplayedSweepSendsNothingTests` 4/4/4. Secret scan of the whole diff: 0 hits.
1 schema 26 — **DONE**. `strategy_deployment` (seven identity facts hashed into the id, immutable; state only through `Start/Suspend/Resume/End`, one guarded `UPDATE` each) and `deployment_op` keyed by the sendable `dp-…` request id; both rollback fixtures drop 26.
2 identity — **DONE**. `AgentContext.Deployment`; `ForAgent` cannot build one, the pipe refuses the reserved name and serves none of its rows, and both live modes refuse it `MODE_FORBIDS_EXECUTION` above the allocation gate and again at dispatch.
3 policy — **DONE**. `StartPaperDeploymentsDue` on the mission seam after `AllocatePaperDue`; `ReconcilePaperDeploymentsAsync` in the app's and the gateway host's loops; `EndPaperDeploymentAsync` cancels, then closes through `CloseAsync`, and leaves the allocation and the grant standing.
4 surfaces — **DONE**. `trade deployment list` (read, any role) and `stop --id` (in `Ops.Mutating`), `status.deployments`, report §4 *running forward on paper*, the Safety card's one press plus confirm, CONTRACTS.md "The paper deployment", one USER-GUIDE.md paragraph.
RED on the base `71dd3a9`: the class does not compile — 57 errors, e.g. `PaperDeploymentTests.cs(164,23): error CS1061: 'TradingGateway' does not contain a definition for 'Deployments'`.
(e) also RED behaviourally with the live-mode guard removed: `Expected start: "MODE_FORBIDS_EXECUTION"` / `String: "request dp-live-1 is waiting for your app"···` — the order was PARKED for approval instead.
Mutant (i), the op written after dispatch → (b) RED: `Assert.Single() Failure: The collection was empty` at line 257, log `written before the wire: 0 — none / orders at the wire : 1 then 0`.
Mutant (ii), `DeploymentMovedFrom` on the mode alone → (c) RED: `Assert.Equal() Failure: Strings differ  Expected: "suspended"  Actual: "active"` at line 329. Both put back; 0 warnings after.
Deviations: the brief's `Deployments.StartDue/Reconcile/SuspendIfMoved/End` are **gateway** methods — they need the connector, the mode and `PlaceAsync`; the `Deployments` ledger owns `Start/Suspend/Resume/End`. `CloseAsync` gained an optional `strategyVersionId` so the flatten names the run's version: without it the flatten is refused `ENVELOPE_ACCOUNT_RESERVED` on the envelope's own account, which is that guard working. `Every_mutating_op_dispatches_once_for_one_request_id` gained an arm that writes a run for `deployment-stop` to end — the test's own comment asks for exactly that; nothing removed or renamed.
NOT DONE: no runner emits an intent, so `entry`/`exit`/`stop`/`target` ops are writable and never produced — the only ops this build dispatches are an end's `flatten` and a `cancel` per working order of the run. No Windows box, no provider, no venue, no order anywhere real.
