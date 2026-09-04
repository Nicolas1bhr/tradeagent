# U14 — ADVERSARIAL-VERIFY RECORD · round 7 (+7b), leg [2], Opus, same verifier as rounds 4–6 (§9.3)

**Sha under test:** `4de7c2529d94fbbb81bc39b14988073d398eb092` = `f8a724c` + 5 commits (round 7's four
plus round 7b's `4de7c25`). Worktree `…-worktrees/u14-verify-r7`, branch `u14-verify-r7-probes`.
Toolchain `PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`, .NET 10, macOS/APFS.
**No box** — Windows stays entirely NOT verified, and every on-box figure from rounds 5 and 6 remains
withdrawn or unrepeated by anyone. Cherry-picked from `u14-verify-r6-probes`: my parked-peer probes
(as `PeerRefusalVerifyR7Probes`) and my unreadable/lease/zero probes.

## Target 5a — the headline figure, reproduced

`dotnet build TradeAgent.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`, tree clean.
`dotnet test TradeAgent.sln` (full-suite run 1 of 2):

```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 904 ms  - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 111, Skipped: 0, Total: 111, Duration: 3 s     - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 253, Skipped: 0, Total: 253, Duration: 1 m 57 s - TradeAgent.IntegrationTests.dll (net10.0)
```

**439 green (75 / 111 / 253) — the builder's Mac figure reproduced exactly.**

---

## Target 1 — V1, BOTH HALVES, ON MY OWN PROBES

**My round-6 probes, carried forward unchanged, now PASS at this sha:**

```
$ dotnet test … --filter "…A_parked_refused_peer_does_not_keep_a_fixed_bridge|…A_fixed_bridge_is_accepted_once…"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 201 ms
```

The v2 peer is dropped inside the read-loop turn, the accept loop offers the instance again, and the
fixed v3 bridge — dialled ONCE, with no retry helper — is accepted. **My round-6 V1 (HIGH) is closed.**

**MD1, my round-6 mutant, is strongly RED.** `Drop` wipes `_incompatible` again:

```
PeerRefusalVerifyR7Probes.A_live_refusal_is_not_masked_by_a_stale_one [FAIL]
PeerRefusalVerifyR7Probes.A_fixed_bridge_is_accepted_once_the_refused_peer_disconnects [FAIL]
PeerRefusalVerifyR7Probes.A_parked_refused_peer_does_not_keep_a_fixed_bridge_off_the_pipe [FAIL]
BridgeRoundTripTests.A_refused_peer_leaves_the_version_and_the_repair_on_the_row [FAIL]
BridgeRoundTripTests.When_an_incompatible_bridge_disconnects_the_status_row_is_told [FAIL]
BridgeRoundTripTests.A_refused_bridge_cannot_clear_its_refusal_with_a_later_hello [FAIL]
BridgeRoundTripTests.A_refused_bridge_that_reconnects_is_refused_again [FAIL]
BridgeRoundTripTests.A_refused_bridge_cannot_set_capabilities_through_a_heartbeat [FAIL]
BridgeRoundTripTests.A_refusal_survives_an_unrelated_disconnect_until_a_good_bridge_arrives [FAIL]
BridgeRoundTripTests.A_bridge_speaking_the_previous_protocol_raises_no_events_into_the_application [FAIL]
BridgeRoundTripTests.A_parked_refused_peer_does_not_keep_a_fixed_bridge_off_the_pipe [FAIL]
BridgeRoundTripTests.An_incompatible_bridge_names_its_version_and_gains_nothing_by_it [FAIL]
```

12 RED — the builder's 9 plus my 3.

**The `Redial` fixture: it models a real flake and does not hide a real failure.** `Redial` retries 8
times at 100 ms (`BridgeRoundTripTests.cs:410-422`) — 800 ms of patience. The REAL bridge
(`BridgeServer.cs:78-121`) dials in an unbounded loop with `ReconnectDelay = 2 s`, so the fixture is
**more impatient than the client it stands in for**, and any window it survives the real bridge also
survives. The stronger evidence is that **my own probes do not use it**: `A_parked_refused_peer…` and
`A_fixed_bridge_is_accepted_once…` construct a `StubBridge` and call `ConnectAsync` ONCE, and both
pass. The fixture is therefore not load-bearing for V1's acceptance. What it does not prove is that
the recycle window is BOUNDED — it shows only that it closes within 800 ms in these runs; my control
measured 146 ms.

---

## Target 2 — ROUND 7B, AND THE HUNT FOR OTHER CLEARERS

**The rule holds where the brief states it.** `_incompatible` is assigned in exactly two places at
this sha: set at `AtasConnector.cs:371` (mismatch) and cleared at `:442` (accepted hello). `Drop` no
longer touches it. `A_refusal_survives_an_unrelated_disconnect_until_a_good_bridge_arrives` drives two
unrelated disconnects and is green in the 253; MD1b and MD1c are the builder's and are recorded RED.

**The hunt for other clearers — enumerated, not assumed.** `grep` over `src/` for every assignment and
for connector lifecycle: the only other way a refused row ends is **constructing a new
`AtasConnector`** — `AppHost.cs:124` and `:184` (`SwitchConnectorAsync`), and `GatewayHost/Program.cs:29`.
That is a fresh object for a fresh session and requires an explicit operator action in Settings. No
health probe, no reconnect path and no `ConnectAsync`/`DisposeAsync` path resets it.
`TradingGateway.cs:72` only READS it (`StatusDetail` when the state is FAILED). **Nothing else clears
it.**

**But making it permanent opened a different hole — finding V4 (MED).** `_unauthenticated` is sticky
for the same reason (cleared only at `:528`, when a peer proves itself), and `StatusDetail` is
`_incompatible?.ToString() ?? Unauthenticated?.ToString()` (`:120`) — which prefers the OLDER of the
two. The `Unauthenticated` getter's own doc claims "the two readings can never both be live and
disagree", but that guard covers only the DERIVED `Silent` reading; the explicit `_unauthenticated` is
returned regardless.

Refutation executed — `A_live_refusal_is_not_masked_by_a_stale_one`: a v2 peer is refused and leaves;
the operator reinstalls; the new bridge reaches the pipe but presents no proof (a wrong
`TRADEAGENT_HOME` or a stale `bridge.auth`, both documented failure modes):

```
a stale protocol refusal is masking a live authentication refusal.
  StatusDetail now = "bridge 0.1.1 speaks protocol 2, this build speaks 3 — reinstall the add-on from TradeAgent"
  Incompatible     = reported=2
  Unauthenticated  = "the ATAS bridge did not authenticate — a peer claiming to be bridge 0.1.2 … said hello
                      without ever presenting the shared secret … if this line survives that, another program
                      has taken the pipe name and TradeAgent will not trade through it"
```

The operator is told to do the thing they have just done, while the live message — which says another
program may have taken the pipe — is held behind it. **This is a regression from round 7b**: before
it, `Drop` cleared `_incompatible` on disconnect, so the new peer's own refusal surfaced.

**Diagnostic mutant MS1 (a diagnosis, not a fix — restored immediately):** clearing `_incompatible`
when a newer explicit refusal is recorded (`:430`) makes the probe pass and leaves
`BridgeRoundTripTests` + my probes at **35/35 green**. One line, no round-7/7b test disturbed.

---

## Target 3 — V2's PAIR-PINNED BOUNDARY: HALF RIGHT

Reproduced all three, and then attacked the claim that the pair is necessary.

| # | Mutant | Builder's result | Mine |
|---|---|---|---|
| MV2b | the degraded guard widened to the whole set | SURVIVED 120/120 | **SURVIVED 123/123**, including my new probe — genuinely inert |
| MV2c | `LastLineWhere` scans every per-writer file | SURVIVED 120/120 | **RED 1/123** against a single new probe |
| MV2d | both at once | RED 1/119 | RED 2/123 (the builder's test and mine) |

**MV2c can be pinned alone; the state was simply never built.** The observable case is the one the
pair-pinning leaves out: the canonical sidecar EXISTS (so the guard passes) but holds only a
diagnostic, while a refused writer's own file holds an unresolved safety event. Under MV2c the
deciding-line scan reaches that file and the machine reports DEGRADED because somebody else's second
bridge was turned away — dropping `SupportsClientOrderId` over a misconfiguration that cost no order.
`A_refused_writers_safety_line_flags_the_zero_without_degrading_the_machine` asserts both halves (the
zero IS flagged, the machine is NOT degraded) and is green on pristine, RED under MV2c.

**MV2b is different and the builder's account of it stands**: `_noted` is computed OUTSIDE the guard
(`CoidWitness.cs:946`, before `:957`) and the deciding-line scan stays canonical-only, so widening the
guard alone changes nothing observable. It is inert, not unpinned. **Finding V5 (MED)** covers MV2c
only.

---

## Targets 4 and 5b — V3, F18, F17 AND R3 AT THIS SHA

**V3's invariant is load-bearing.** `A_rotation_by_this_build_always_leaves_a_deciding_line` is present
as a Theory over both rows (`CoidWitnessTests.cs:2901`). Mutant **MV3** (a quarantine that happens and
then never saves) → **RED on both rows**, 2/2. The F18 guard remains defensive and the reason is now in
the test's own doc, which was the round-6 ask.

**My round-6 probes all hold at this sha** — the four F17 variants (real `chmod 000`, a directory at
the path, a short read, a mid-read failure), the Dispose/lease handover, and
`A_zero_is_flagged_when_the_only_account_of_a_refusal_is_a_per_writer_sidecar`:

```
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 517 ms
```

**My round-6 V2 (MED) is closed** — that last probe was RED at `f8a724c` and is green here.

**R3's per-writer sidecars, re-measured with real processes at this sha** (five writers × 40 claims,
all held alive, exactly 160 refused-writer safety events, three runs):

```
  refusals=160 files=4 lines=160 naming a claim=160 DROPPED=0
  refusals=160 files=4 lines=160 naming a claim=160 DROPPED=0
  refusals=160 files=4 lines=160 naming a claim=160 DROPPED=0
```

---

## Mutants

Production files restored from `cp` copies every time, `touch`ed, never `git checkout --`; the
production tree confirmed byte-identical after each (`git diff --stat 4de7c25 -- src tools` empty).
Pristine shas: `CoidWitness.cs e0c2a3d6…`, `AtasConnector.cs b4c5c60d…`, `AtasHealth.cs 6e8a9f8c…`.

| # | Mutant | Result |
|---|---|---|
| **MD1** (mine, round 6) | `Drop` wipes `_incompatible` unconditionally | **RED 12** — the builder's 9 plus my 3 probes |
| MV2b (builder's) | the degraded guard widened to the whole set | SURVIVED 123/123 — genuinely inert, the builder's account stands |
| **MV2c** (builder's) | the deciding-line scan reads every per-writer file | **RED 1/123** against my new probe — the builder recorded it SURVIVED 120/120 |
| MV2d (builder's) | both at once | RED 2/123 |
| MV3 (builder's) | a quarantine that happens and then never saves | RED 2/2 — both Theory rows |
| **MS1** (mine, *diagnostic only*) | a newer explicit refusal clears the older one | the V4 probe passes; `BridgeRoundTripTests` + my probes 35/35 green — confirms V4's fix expectation |

---

## Findings

| # | Sev | Finding | `file:line` | Exact fix expectation |
|---|---|---|---|---|
| **V4** | MED | A stale protocol refusal masks a live authentication refusal. Round 7b made `_incompatible` permanent until a compatible hello; `_unauthenticated` was already permanent until a peer proves itself; and `StatusDetail` prefers the older of the two. So after a v2 refusal, a reinstalled bridge that fails AUTHENTICATION is still reported as "speaks protocol 2 — reinstall the add-on": the operator is told to repeat what they just did, while the live message ("another program has taken the pipe name and TradeAgent will not trade through it") is held behind it. Measured. The same root cause makes the row outrank live machine state — `AtasHealth.BridgeRow` returns a non-empty refusal ahead of "installed — waiting for ATAS to start", so the reason can now outlive ATAS itself. **CLASS (§9.10): a refusal is kept until its own repair, and nothing asks whether it still describes the peer that is there now.** | `src/TradeAgent.Connectors.Atas/AtasConnector.cs:120` (precedence), `:371` and `:430`/`:502`/`:516` (neither supersedes the other) | Make a newer explained peer supersede an older one: clear `_incompatible` where `_unauthenticated` is set, and clear `_unauthenticated` where `_incompatible` is set. That restores the invariant the `Unauthenticated` getter's own doc at `:101-104` already claims ("the two readings can never both be live and disagree") instead of leaving it as a comment. Verified by MS1: one line at `:430`, 35/35 green. Probe ready at `tests/TradeAgent.IntegrationTests/PeerRefusalVerifyR7Probes.cs` (commit `03f94dc`). |
| **V5** | MED | MV2c has no biting test, and a single test pins it — so the "each blocks the other, the pair pins the boundary" account is half right. The unbuilt state is: the canonical sidecar EXISTS (guard passes) but holds only a diagnostic, while a refused writer's own file holds an unresolved safety event. Under MV2c the machine then reports DEGRADED and drops `SupportsClientOrderId` because somebody else's second bridge was turned away — a misconfiguration that cost no order, which is exactly the boundary the round-6 fix drew. A real behaviour change hides behind an edit that no test would fail. (MV2b is different: `_noted` is computed outside the guard at `:946`, so widening the guard alone is inert — that half of the builder's account stands.) | `src/TradeAgent.AtasBridge/CoidWitness.cs:1446` (the scan), `:957` (the guard) | Add the probe as a permanent test — `A_refused_writers_safety_line_flags_the_zero_without_degrading_the_machine`, ready at `tests/TradeAgent.IntegrationTests/SidecarBoundaryVerifyR7Probes.cs` (commit `58daef3`) — so the boundary is pinned by a test rather than by two mutants that cancel each other. |

**0 HIGH / 2 MED / 0 LOW.**

**Closed this round:** my round-6 **V1** (HIGH — the parked peer; my own probes pass with a single
dial, MD1 RED 12), my round-6 **V2** (MED — the flagged zero; my probe is green here), and my round-6
**V3** (LOW — now pinned by an invariant that MV3 breaks on both rows).

**Refuted / answered, no finding:** the `Redial` fixture models a real flake and is more impatient than
the real bridge, and my own probes reach the acceptance without it; and the hunt for other clearers of
a refused row found only "construct a new `AtasConnector`" (`AppHost.cs:124`, `:184`,
`GatewayHost/Program.cs:29`), which is a fresh session by explicit operator action.

---

## NOT verified, by name

- **Windows: everything.** No box access this round. The on-box suite figures from rounds 5 and 6
  remain **withdrawn**; the bridge compile against real ATAS and the `tools/atas-gate` money-path run
  remain unrepeated by anyone since round 6. **V1 is measured on macOS only** — the one-instance
  mechanism is the same in kind on Windows (`ERROR_PIPE_BUSY`), which is reasoning, not measurement.
- **Which ATAS callback fires on a strategy teardown** (`OnStopping` vs `OnDispose`) — unchanged; no
  test in the 439 reaches `StopBridge` and the adapter is `<Compile Remove>`d off Windows.
- **V4's operator-facing half.** I measured `StatusDetail`; I did not render the dashboard row, so what
  the user literally sees is inferred from `TradingGateway.cs:72` and `AtasHealth.BridgeRow`.
- **R4's Windows half** (a file open without `FILE_SHARE_DELETE` cannot be unlinked) — unchanged.
- **The F8 residual** and **`Quarantine`'s 64 slots** — both still open, neither touched this round.

## What I did NOT do

- **I fixed nothing.** `git diff 4de7c25 -- src tools` is empty and the three production shas are
  unchanged. Every mutant was restored from a `cp` copy and `touch`ed, never `git checkout --`.
- **I did not push, merge or rebase.** Two probe commits on `u14-verify-r7-probes` (`03f94dc`,
  `58daef3`), both under `tests/`.
- **I did not touch the box, or ask for it.**
- **I did not re-run MD1b or MD1c** (the builder's round-7b mutants) — MD1 covers the same guard from
  my own side and is RED 12; the two half-measures are the builder's evidence and I read them.
- **I did not attempt to pin MV2b**, having established it is inert rather than unpinned.
- **I did not exercise `SwitchConnectorAsync` end to end** — I enumerated the constructor call sites
  and read them; no UI path was driven.
- **Full suite run twice, no more.** Run 1 (baseline at `4de7c25`) and run 2 (after every mutant was
  restored, my probe classes filtered out) — both **439 green (75 / 111 / 253)**.

```
$ dotnet test TradeAgent.sln --filter "FullyQualifiedName!~VerifyR6Probes&FullyQualifiedName!~VerifyR7Probes"   # run 2 of 2
EXIT=0
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75 - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 111, Skipped: 0, Total: 111 - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 253, Skipped: 0, Total: 253 - TradeAgent.IntegrationTests.dll (net10.0)
```

The tree is left exactly as it was found.

VERDICT: FAIL — 0H/2M/0L
