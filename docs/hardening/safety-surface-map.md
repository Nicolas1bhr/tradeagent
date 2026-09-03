# TradeAgent safety-surface map — as of commit 283d942 (2026-09-02), with the reviewers' correction

Read-only survey by an Explore leg, persisted by the manager, re-materialised 2026-09-03 after the scratchpad was lost.
All `file:line` references are relative to the repo at **283d942**; U2a/U2b/U2c-1/U2d/U14 have since moved much of
this code. Use it as the reviewers' baseline and the vocabulary (gate numbers G0–G23), not as current line numbers.

## 20-line summary

1. The order path has ~24 named gates; all but a few live in `TradingGateway.TryAuthorizeExecution` + `RiskCheckOrThrow`, each with a distinct `ErrorCode`.
2. Most gates have a test pinning them by reason code; the unpinned ones are listed in §10.
3. Idempotency replay runs BEFORE authorization (`TradingGateway.cs:323`): a replayed request id returns its stored outcome even after the kill switch. Deliberate.
4. `ApproveAsync` (`:488`) re-ran neither `AuthorizeOrThrow` nor `RiskCheckOrThrow` — **fixed by U2b**.
5. Operator authority (mode/kill switch/live activation/approvals/update) is absent from both `Ops` and the `trade` verb map; `VerticalSliceTests.cs:85` enumerates 8 candidate ops.
6. `GatewayHost/Program.cs:51-86` is a second operator surface on stdin — dev/test harness only.
7. `LIVE_AUTONOMOUS` is refused in exactly one place (`:208`) off `ConnectorCapabilities.ReconciliationProvable`.
8. Both bridge ends authenticate (HMAC over a bridge-chosen nonce + Windows image-path check) — **CORRECTED below: the image check is one-directional.**
9. `BridgePipeAuth`'s doc-comment states it is NOT a boundary against a same-user process.
10. The updater verified checksum only; a `null` checksum degraded to download-without-verification (`UpdateService.cs:428-431`) — **fixed by U2d**.
11. Nothing checks Authenticode; the installer is unsigned.
12. `TRADEAGENT_UPDATE_REPO` env var redirects the update source; validated for shape, not identity.
13. Zero off-machine reporting. Outbound = GitHub releases, nodejs.org, atas.net, one nuget.org ping.
14. No auto-start at logon (per-user install, `PrivilegesRequired=lowest`, no Run key, no task).
15. Crash handling is one line: `TaskScheduler.UnobservedTaskException` → UI message (`TradeAgentApp.cs:39`). No `AppDomain.UnhandledException` handler, no crash dump.
16. `material`/`material_note` split is real and enforced at SQL; the agent channel has no write path to `material`.
17. `material`, `material_note` and `execution_request` have no rotation and no cap; `activity`/`engineering_log`/`health_event` are bounded by `LogStore.Rotate`.
18. Zero TODO/FIXME/HACK/NotImplemented/pragma-disable/NoWarn in `src/`. Two "NOT VERIFIED" markers (Modify clone in the collection).
19. `AtasStrategyAdapter.cs` (3197 lines, the money path) is `<Compile Remove>`d off Windows — no CI test; its decidable logic is extracted into `ClientOrderIdProof`/`AdapterTouchedOrders`/`CoidWitness`/`AtasCall` (78 tests).
20. The agent process receives no secrets (env = `PATH`, `TRADEAGENT_SESSION`, `TRADEAGENT_WORKSPACE`) but runs as the same Windows user with no sandbox: `state\` (ipc.token, bridge.auth, tradeagent.db) is readable AND writable by it. **And `TRADEAGENT_SESSION=operator` was operator context on the wire — fixed by U2a.**

## 1. Order path — gates at 283d942

| # | Gate | file:line | Code | Test |
|---|---|---|---|---|
| G0 | hello-first + token (constant-time) | `GatewayPipeServer.cs:86-108` | IPC_UNAUTHENTICATED | VerticalSliceTests.cs:66, :82 |
| G1 | unknown op | `GatewayPipeServer.cs:169` | INVALID_REQUEST | VerticalSliceTests.cs:85 |
| G2 | request id required | `TradingGateway.cs:318` | INVALID_REQUEST | none → U2b pinned |
| G3 | idempotency replay | `:323-329` | stored record / APPROVAL_REQUIRED | FaultTests.cs:36,51,65 |
| G4 | mode allows execution (OBSERVE refused) | `:190` | MODE_FORBIDS_EXECUTION | FaultTests.cs:471/477 |
| G5 | kill switch (`AiTradingStopped && !ctx.IsOperator`) | `:195` | AI_TRADING_STOPPED | FaultTests.cs:394/404; VerticalSliceTests.cs:218 |
| G6 | live activation | `:203` | LIVE_NOT_ACTIVATED | FaultTests.cs:498-524 |
| G7 | autonomy needs provable state | `:208` | AUTONOMY_REQUIRES_PROVABLE_STATE | FaultTests.cs:589/598 |
| G8 | account chosen | `:229` | ACCOUNT_NOT_FOUND | FaultTests.cs:538/553 |
| G9 | unreconciled work pauses trading (flag only) | `:238` | TRADING_PAUSED_UNRECONCILED | FaultTests.cs:92-832 |
| G10 | health chain trustable | `:245` → `Core/Health.cs:69` | TRADING_PERMISSION_UNAVAILABLE | CoreTests.cs:186 |
| G11 | instrument allowlist | `:263` | RISK_LIMIT_EXCEEDED | FaultTests.cs:639 |
| G12 | quantity > 0 | `:266` | INVALID_REQUEST | none → U2b pinned |
| G13 | max order quantity (1) | `:269` | RISK_LIMIT_EXCEEDED | FaultTests.cs:639 |
| G14 | paper ≠ real account | `:274` | MODE_ACCOUNT_MISMATCH | FaultTests.cs:481/493 |
| G15 | rate limit (6/min, counts dispatches) | `:278-285` | RISK_LIMIT_EXCEEDED | FaultTests.cs:660 |
| G16 | max open positions (2) | `:287-292` | RISK_LIMIT_EXCEEDED | FaultTests.cs:639 |
| G17 | quote guard (MaxQuoteAge 30 s) | `:296-301` | MARKET_DATA_UNAVAILABLE | FaultTests.cs:379/386 |
| G18 | notional cap (default 0 = off) × ContractSize | `:305-311` | RISK_LIMIT_EXCEEDED | ContractSize unexercised → U2b pinned |
| G19 | LIVE_CONFIRM parking | `:346`, `:364-367` | APPROVAL_REQUIRED | FaultTests.cs:602-620 |
| G20 | DB unique-key race loser | `Stores.cs:32`; `:356` | stored record | FaultTests.cs:51 |
| G21 | write-ahead DISPATCHING | `:443` | CAS | FaultTests.cs:211 |
| G22 | SupportsModify | `:557` | TRADING_PERMISSION_UNAVAILABLE | none → U2b pinned |
| G23 | decline only pre-send | `:513` | INVALID_REQUEST | FaultTests.cs:879 |

Connector `AtasConnector.cs`: pipe ACL `:188` · Answer challenge `:393` · protocol version `:294` (`_hello` null → all
capabilities false) · unproved hello `:333` · unproved heartbeat `:361` · not connected `:478` · RPC deadline 10 s
`:492-508` (indefinite) · Rejected flag `:497-500`. Bridge `BridgeServer.cs`: Authenticate before anything `:134`/`:96` ·
Windows image check `:150-152` → `AtasConnector.cs:824` ImageVerdict (refuses peers under `Paths.Tools`) · AuthTimeout
10 s · SendRaw WriteTimeout 10 s `:398-422` · refusal classifier single-fault `:319` · `AtasCall.Block` deadline.
Adapter `AtasStrategyAdapter.cs`: `Place(cmd)` `:1261` (one line → `PlaceRoute.Default`); pre-flight refusals
`:1279-1311`; `_submitted` `:161`; `_touched` `:187`/`:1327`; `_witness` write-ahead `:1347` before `OpenOrder`; flagged
overload `:1444`; CallTimeout 5 s + AckTimeout 3 s ("arithmetic, not a measurement"); `WaitFor` `:3163` (timeout is not a
rejection); `LiveOrders()` `:2749`; `Trim()` cap 4096 `:2930`.

## 2. Request state machine (283d942)

`ExecutionState` `Core/Trading.cs:10`; table `OrderStateMachine.cs:12-43`; UNKNOWN → [RECONCILING] only; DISPATCHING
includes CANCELLED (single legal caller `Decline`). CAS in `Stores.cs:85` Transition. `Settle` `TradingGateway.cs:382`
discriminates `illegal_settle` (table refusal) from `already_settled` (race), never throws. `SettleUnknown` `:426`.
`MarkNeedsReconciliation` `Stores.cs:127` is a bare UPDATE (a record can be FILLED and flagged). UNKNOWN produced at the
dispatch catch `:476` (transport/timeout/OCE only) → `ExecutionCapability = PAUSED`. `NeedingReconciliation()` `Stores.cs:75`
reads the flag alone. `ReconcileAsync` `:667` never resubmits; absence = never landed only when `ReconciliationProvable`
and age ≥ AbsenceGrace 15 s. `ForceResolve` `:816`: same-state → clear flag; terminal-and-different → refuse; else hop via
RECONCILING.

## 3. Operator authority (in-process only)

Mode `:106` · kill switch `:90`/`:99` · live activation `:115` · Approve/Decline `:488`/`:500` · ForceResolve `:816` ·
Cancel-all/Close-all `:612`/`:620` (outside the authorization gate on purpose) · risk `Update` `:86` · update install
`UpdateService.cs:285` ← `MainWindow.cs:590`, `SettingsView.cs:145` · connector switch `AppHost.cs:162`. Pipe ops
`Core/Protocol.cs:13-28` and `trade` verbs `TradeCli/Program.cs:117-186` contain none of these.

## 4–8 (condensed)

Capabilities: `ReconciliationProvable => SupportsClientOrderId && SupportsOrderHistory`; `SupportsClientOrderId =
proof.ProvesRoundTrip()` (Distinct|CrossSession); `SupportsOrderHistory = cache.Cache is not null`. Updater: fetch
`releases/latest`, refuse draft/prerelease/downgrade, installer asset required, checksum from `SHA256SUMS.txt` by
filename, `/SILENT /NORESTART /SUPPRESSMSGBOXES /relaunch=1`, no signature. Observability: activity 5,000 / engineering
20,000 / health 20,000 rows rotated every 5 s; `execution_request` never deleted; 12 health components; `trade status`
fields in `GatewayTypes.cs:36-40`; support zip = activity 2000 + environment + engineering 5000 + `Paths.Logs` minus
names containing token/secret. Inbox: `workspace/inbox`, FileLimit 5,000, DepthLimit 12, HashesPerPass 24, no byte cap;
`material` written only by the scanner; AGENTS.md text generated in `WorkspaceBuilder.cs:111-127`. Agent runtime:
`CreateNoWindow` everywhere, headless sign-in, env of three variables, no sandbox, `AgentSupervisor` in-process only,
`SingleInstanceLock` machine-wide.

## 10. Unpinned at 283d942

G2, G12, G18×ContractSize, G22 (all pinned by U2b); `ApproveAsync` re-authorization (U2b); adapter pre-flight/WaitFor/
Trim/LiveOrders (Compile Remove'd); `UpdateSources.Install` flags; `Prune`; support-package redaction; `LogStore.Rotate`
bounds; scanner byte behaviour; the unhandled-exception path.

## Ten places to look first (as briefed to the reviewers)

1. `AtasStrategyAdapter.Place` — zero CI tests. 2. `PlaceAsync` gate ordering (replay before authorization).
3. `ApproveAsync` (fixed). 4. `BridgePipeAuth` same-user boundary. 5. Updater trust chain (fixed in U2d). 6. `Settle`
discrimination. 7. `Allowed[DISPATCHING]` including CANCELLED. 8. `MarkNeedsReconciliation` + flag-only query (U2c-1).
9. `Doctor.CreateSupportPackage` redaction. 10. Unbounded `material`/`execution_request`; scanner byte budget.

## CORRECTION (adversarial review, 2026-09-02)

Summary line 8 and "ten places" item 4 are wrong about the direction of the image check. `ImageVerdict` is called ONLY
in `BridgeServer.cs:151` — the BRIDGE verifies TradeAgent's image. `AtasConnector.cs:145,92` merely displays the peer's
image; TradeAgent never verifies that the process on the other end of the bridge pipe is `OFT.Platform.exe` under the
ATAS install dir. With the shared secret (same-user readable) an impostor bridge can therefore connect to TradeAgent,
present capabilities, and set `ReconciliationProvable`. Since Program Files is not user-writable, a connector-side check
"peer image == `<ATAS install dir>\OFT.Platform.exe`" would be a real boundary against a same-user impostor. Unit U2g.
