# The two independent reviews of commit 283d942 (2026-09-02) and the manager's triage

Two legs on separate detached worktrees at the 2026-09-01 handoff commit: Codex cross-model read-only review
(`gpt-5.6-sol`, 31 min, 15 findings) and a Claude adversarial verifier (48 executed probes with positive controls, 612-line
record, 16 findings). Both raw records were lost with the scratchpad; the probe file `AdversarialProbes-283d942.cs`
(1009 lines) is lost too — its Probe_C1..C4 were later extracted into `tests/TradeAgent.FaultTests/DispatchRecoveryTests.cs`
on the U2c-1 branch. Findings are inputs, never auto-applied (§4.4); every row was triaged.

| Finding (source) | Sev | Where | Disposition |
|---|---|---|---|
| Crash after write-ahead strands DISPATCHING unflagged; startup and G9 look only at the flag (Codex 1 = adv 5, C1 confirmed) | HIGH | TradingGateway.cs:443 | U2c-1 |
| Codex CLI launched `--dangerously-bypass-approvals-and-sandbox`, same user, unsandboxed → can rewrite state/DB/install (Codex 2) | HIGH | RuntimeManifest.cs:381 | U12 design (same-user class) |
| Same-user helper rewrites bridge.auth, squats the bridge pipe during shutdown, sends `place` to the adapter (Codex 3) | HIGH | AtasConnector.cs:679 | U12 |
| **`TRADEAGENT_SESSION=operator` skips LIVE_CONFIRM parking and the kill switch** (Codex 4; confirmed from source by the manager; proven over the pipe by the adversarial leg) | HIGH | GatewayPipeServer.cs:141 | **U2a round 1 — fixed** |
| Unlisted connector return states settled ACKNOWLEDGED; Modify settled ACKNOWLEDGED unconditionally (Codex 5 = adv 9, C2) | HIGH | TradingGateway.cs:455 | U2c-1 |
| Exceptions outside the catch taxonomy strand DISPATCHING (Codex 6 = adv 10, C3) | HIGH | TradingGateway.cs:476 | U2c-1 |
| Authority checked before async work, not at commit-to-wire; approval re-checks nothing (Codex 7 = adv 1) | HIGH | TradingGateway.cs:331/488 | approval half → U2b (integrated); commit-to-wire half → U2c-2 |
| CrossSession counted as broker round-trip proof though it cannot separate broker from ATAS-local rehydration (Codex 8) | HIGH | ClientOrderIdProof.cs:63 | ACCEPTED RISK with reason: known bound decided 2026-08-30/09-01, printed in the verdict; not exploitable while `SupportsOrderHistory` is false; presence-in-ATAS-not-broker fails safe; revisit with a broker |
| Sync ATAS calls can wedge the serial loop while the heartbeat says READY (Codex 9) | HIGH | AtasStrategyAdapter.cs:1383 | PARTIALLY ADDRESSED (AtasCall.Block deadlines since 08-29); remaining = health reflects command-loop responsiveness → U2e |
| OperatorCloseAll sends market closes with no record; retry double-closes (Codex 10 = adv 11, C4 confirmed: 4 contracts sold against a 2-contract long) | HIGH | TradingGateway.cs:620 | U2c-1 |
| One click LIVE_CONFIRM→LIVE_AUTONOMOUS keeps LiveActivated (Codex 11 = adv 12, C5) | HIGH/MED | DashboardView.cs:493 | U2c-2 (not reachable today: G7 refuses LIVE_AUTONOMOUS) |
| Detached child survives Stop-the-AI (Codex 12) | MED | AgentSession.cs:199 | U13 Job Object |
| Checksumless install proceeds; a test pins it (Codex 13 = adv 14) | MED | UpdateService.cs:306 | U2d |
| Connector pipe write has no deadline before the RPC timeout (Codex 14) | MED | AtasConnector.cs:488 | U2a |
| Response writes without deadline; malformed pre-auth frames answered (Codex 15 = adv 7) | LOW/MED | GatewayPipeServer.cs:137 | U2a |
| `cancel-all` derives `{rid}-{i}` ids colliding with agent ids, reports cancelled=1 while WORKING (adv 2) | HIGH | GatewayPipeServer.cs:207 | U2a round 3 |
| Settings load once and silently default on parse failure (kill switch → OFF); a second same-user connection can rewrite them to LIVE_AUTONOMOUS (adv 3) | HIGH | TradingGateway.cs:72-77 | U2c-2 (fail closed) + U12 |
| Clearing `needs_reconciliation` externally unpauses live trading while the record stays UNKNOWN (adv 4) | HIGH | Stores.cs:75 + :238 | U2c-2 (G9 reads state) |
| Support-package redaction is filename-only and its comment is false (adv 6) | MED | Doctor.cs:270 | U6 |
| Kill switch throws and is never persisted under a DB lock (31 s); `RefreshHealthAsync` throws after 62 s (adv 8, 13) | MED | TradingGateway.cs:90-99, :873 | U2c-2 |
| Five ways a checksum silently degrades + first-asset-wins; Settings install button bypasses the hard stop (adv 14, 15) | MED | UpdateService.cs, SettingsView.cs:149 | U2d |
| **The peer image check is ONE-DIRECTIONAL** — only the bridge verifies TradeAgent's image; an impostor bridge with the secret can present capabilities and unlock autonomy (adv 16; corrects the safety map) | HIGH | AtasConnector.cs:145,92 | U2g (connector-side check: peer image == `OFT.Platform.exe` under Program Files — not user-writable, so a real boundary) |

Negative results recorded honestly: no double dispatch from concurrent approves (CAS holds); a pipe stall does not starve
the operator side. Codex's own NOT CHECKED list matched the "not executable on macOS" set.

Classes named by Codex, kept as the structural map: DISPATCH RECOVERY GAP → U2c-1/U2a; SAME-USER AUTHORITY COLLAPSE →
U12/U13; CLIENT-SUPPLIED TRUST CONTEXT → U2a; STALE AUTHORIZATION SNAPSHOT → U2b/U2c-2; UNPROVEN CAPABILITY EVIDENCE →
accepted, documented; SERIAL IPC WITHOUT DEADLINES → U2a/U2e; UPDATE TRUST → U2d.
