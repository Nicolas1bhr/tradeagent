# U-codex-2b — three read-only claims on the pipe and the connector: each turns RED first, or is refuted

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md`, the `## Codex` block of
`docs/REVIEW-2026-09-05b.md` (F6, F7, F13) and the `U-pipe-hello`, `U14` and `U-bridge-reinstall` sections of
`BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/codex-2b`, branch `u-codex-2b` from `main`. These are Codex's
READ-ONLY claims: for each, write the probe Codex names first. RED → fix it and watch one mutant; GREEN → keep the
probe as the refuting test and say in one sentence why the code holds. No box: the fake bridge and the real
`GatewayPipeServer` over real named pipes are your instruments.

1. **F6 — a malformed present optional field collapses to absence** (`Protocol.cs:49`): `limit: "bad"` becomes a
   Market order, an empty `tif` a Day order. Probe: authenticated raw buy frames with malformed `limit`, `stop` and an
   empty `tif` → `INVALID_REQUEST`, zero connector calls. U-pipe-hello refused undefined TIF words; find what it left.
2. **F13 — `IpcRequest` defaults an omitted `v` to the current version** (`Protocol.cs:36`), so a versionless peer
   passes the protocol hello. Probe: a raw hello without `v`, then a buy → refused before authentication, no effect.
3. **F7 — a heartbeat whose `Describe` payload is absent or malformed refreshes liveness while the prior account and
   capability proof are retained** (`AtasConnector.cs:920`), so autonomous eligibility outlives the bridge's ability to
   attest it. Probe: establish provable capabilities, then make every later `Describe` fail while pulses continue →
   capabilities clear, autonomous dispatch refused, the health row says why.

Yours: `Protocol.cs`, `GatewayPipeServer.cs`, `AtasConnector.cs`, `AtasHealth.cs` for the sentence only,
`docs/CONTRACTS.md`, tests. Commit per item, no trailers. Gate: Release `--no-incremental` → 0 warnings; each class 3×;
full suite once to a file (one suite at a time on this Mac; a `Timing` test that fails while another suite runs → that
class alone 3×); names vs `main` 0 removed.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant or REFUTED with the probe's output;
gate counts; what you did NOT do. Verified or NOT VERIFIED.
