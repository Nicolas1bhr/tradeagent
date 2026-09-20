# U-material-origin — a file the app wrote into a role's home is measured as the app's, never as the agent's
**Boundary protected:** the evidence zone's measurement (`CLAUDE.md`: `material` is written only by the scanner; `docs/PRINCIPLES.md` § Evidence: distinguish
measurements, external claims and agent interpretations). **Defect (SOURCE, found by `U-language-in-home`):** `MaterialScanner.cs:66,81,135-136` records every
file under a role's tracked directories (`trading`, `research`, `strategies`, `data`, `scripts`, `in`, `out`) as `MaterialOrigin.Agent` — so the reference the app
writes to `research/STRATEGY-LANGUAGE.md`, the three programs under `strategies/examples/` and the deliveries the app puts in `in/` are measured as "the agent
produced it". A false origin in the one table the agent cannot edit is a measurement error, not a formatting one. **Observable result:** after a start, those
files carry an origin that says the app wrote them; a role's edit of one is measured as the agent's; nothing the agent writes can claim the app's origin. Not the
money path. No schema rung unless the origin column needs a new value (it is an enum stored as text — check `Materials.cs` and `MaterialStore.cs` first).
Read first: `src/TradeAgent.Core/Materials.cs` (`MaterialOrigin`), `MaterialScanner.cs:1-140` (the pass, the inbox attestation, the role paths), `Db/MaterialStore.cs`;
`src/TradeAgent.AgentRuntime/WorkspaceBuilder.cs` and `ResearchLibrary` (what the app writes and when); `CouncilRelay.cs` (what it delivers into `in/`);
`tests/…/MaterialScannerTests.cs`. Rebase onto `main` first; resolve conflicts yourself. Off-limits: `TradingGateway.cs`, `Database.cs`, `Versioning.cs`,
`GatewayPipeServer.cs` (the deployment leg's).
Items, one commit each with a one-sentence message:
1. `MaterialOrigin.App`: the app records what it writes into a role home — path relative to the home and sha256 — in one app-owned manifest (`state/`, written by
   `WorkspaceBuilder`/`ResearchLibrary`/the relay at write time, never by a role; say where and why in `CONTRACTS.md`). The scanner, meeting a file whose path AND
   hash match the manifest, records `App`; a file at a manifested path with another hash is `Agent` (the role changed it); a path the manifest does not name is
   what it is today. The inbox attestation is untouched.
2. `material-list` and the owner's report print the origin word for `App` files as "written by TradeAgent" and never count them as the agent's work; `AGENTS.md`'s
   sentence about materials names the three origins in one line.
Red-first tests: (a) `The_language_reference_and_the_examples_are_measured_as_the_apps_after_a_start` (RED today: `Agent`); (b) `A_roles_edit_of_an_app_file_is_
measured_as_the_agents`; (c) `A_file_the_agent_writes_at_an_unmanifested_path_cannot_be_measured_as_the_apps`; (d) `A_delivery_the_relay_writes_into_in_is_the_apps`.
Mutant to watch red and quote: the hash check dropped (path match alone → `App`) → (b) red. Nothing removed or renamed; exact column-list assertions extended.
Gate and report as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings; three suites 0 failed; classes 3×; names 0 removed; `## Report` ≤ 20 lines here).
No push, no merge; touch nothing in `docs/briefs/` but this file.
