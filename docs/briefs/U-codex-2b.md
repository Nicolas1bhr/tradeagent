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

## Report

Tip `98a4da2` (this report commits on top). Rebased onto `main` `896be63`, no conflicts. **The previous builder's three commits are kept as written — I changed no product line in them**; my three add probes and pin one guard.

1. **F6 fixed.** RED (guard reverted, real pipe; 10 failed / 41 passed): `limit='bad' -> ok=True · connector saw: Market limit=none` · `limit='1,5' -> ... Limit limit=15` · `tif='' -> ok=True · connector saw: Day` · `limit=true -> ok=False code=UNKNOWN_ERROR`. GREEN: each `INVALID_REQUEST` naming the field, `Placed` empty. Mutants: `Numeric |= AllowThousands` → only `1,5` red; `NamedValue` reading JSON null as absent → `tif=null -> ok=True · connector saw: Market … tif=Day` red; `InvariantCulture`→`CurrentCulture` → `'limit' is not a number this build can read: "4300.25"` red. Added, because `CONTRACTS.md` claimed them and nothing ran them (both RED pre-fix): JSON `null` on limit/stop/quantity/tif, and `all: ""` → `orders all= : ok=True`. **Corrected a false claim the branch shipped**: `"1,5"` cannot "be 1.5 on a machine whose culture writes decimals with a comma" — `Directory.Build.props` sets `InvariantGlobalization=true`, so `new CultureInfo("de-DE")` throws in this build. Naming the culture stays, re-justified as pinning the reading at the call site rather than to a build property.
2. **F13 fixed.** RED: `hello with no 'v' : {"v":1,"id":"h","ok":true,"data":{"protocol_version":1,…,"compatible":true}}` and a versionless `buy` on a live session `"state":"FILLED"`. GREEN: `INCOMPATIBLE_PROTOCOL`, then `IPC_UNAUTHENTICATED`, no order. Mutant: `NamesNoVersion` loses its `!` → still refused, but as `"frame is not valid JSON"`, red on the reason.
3. **F7 fixed.** The probe as committed died at its `Wait` with nothing to read and left three other gates shut, so I opened them (mode, live activation, all four health rows READY) and made it report. RED: `after 10.0s of pulses that cannot describe the bridge: connected=True coid=True history=True provable=True` · `autonomous dispatch: authorized=True`. GREEN: `provable=False` · `authorized=False code=AUTONOMY_REQUIRES_PROVABLE_STATE` · row `FAILED — …has stopped saying what it can do`. Mutants: refreshing liveness only when attested → the pulse test red; `Attested` dropping `BridgeCompatible` → **whole suite still green**, so I wrote the missing probe (`A_heartbeat_at_a_version_this_build_does_not_speak_attests_nothing`) and then watched it go red.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → `Build succeeded. 0 Warning(s). 0 Error(s).` Full Release suite, one project at a time: Unit 237 + Fault 261 + Integration 610 = **1108, 0 failed, 1 skipped** — the skip is `A_hello_that_omits_the_version_field_is_read_as_the_current_one`, `[Fact(Skip=…)]` naming its replacement, so its name is not removed.
That run's Unit pass first reported 1 failed: `UpdateTrustTests…too_big_is_refused`, `HttpListenerException : Address already in use` thrown in its own `Server` ctor while the `batch-2` worktree ran a second suite on this Mac (`Bind` picks a free port, releases it, the ctor binds again). Not mine, not touched: whole project 3× alone → `Failed: 0, Passed: 237` ×3; `UpdateTrustTests` alone 3× → `Failed: 0, Passed: 89` ×3.
Classes 3×: PipeContractTests `Failed: 0, Passed: 53, Skipped: 1` ×3 · BridgeRoundTripTests `Failed: 0, Passed: 40` ×3 · ProtocolTests `Failed: 0, Passed: 5` ×3. Test names vs `main`: **0 removed, 13 added**.
NOT DONE: `req.V` is still checked only on the `hello` op, so a frame that NAMES a wrong version mid-session is read as this one — a different finding from F13, left alone. No Windows box, no CI run, nothing pushed or merged.
