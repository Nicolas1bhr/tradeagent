# U-bridge-liveness-clock — the ATAS bridge's liveness is measured on the wall clock, so a clock stepped backwards keeps a silent bridge READY; measure it on the monotonic clock the connector's own deadlines already use
**Protects:** the connector's liveness truth, on the money path (connectors). A dead bridge must not read alive. A silent bridge kept READY is the DANGEROUS direction:
the gateway goes on treating a peer that answers nothing as healthy. A live bridge declared dead is the safe direction, still a defect. `CLAUDE.md` rule 3: an
ambiguous state is UNKNOWN, never a guess. Found by `U-fix-bridge-heartbeat`'s fixer (2026-10-06) and owed BEFORE ANY LIVE USE (the orchestrator).
Fresh builder, seat P; no rung.
**Facts (SOURCE re-checked by seat P at `9cd0f5e5`, NOT runtime-verified; `A` = `src/TradeAgent.Connectors.Atas/AtasConnector.cs`).**
Liveness stamps and comparisons are wall-clock:
- `_lastHeartbeat` (`A:252`) is set to `DateTimeOffset.UtcNow` at `:968` and `:988`; `_peerArrived` (`:280`) at `:639` (reset `:816`).
- `PeerHasGoneQuiet()` drops a quiet peer when `DateTimeOffset.UtcNow - lastHeard > HeartbeatTimeout` (`:549-558`, the comparison at `:557`).
- `GetHealthAsync` answers DEGRADED or READY from `DateTimeOffset.UtcNow - _lastHeartbeat > HeartbeatTimeout` (`:1766-1771`, at `:1769`).
- The auth grace is measured from the same arrival stamp (`:408`, `:458`).
The connector's write and answer deadlines are already monotonic: `Environment.TickCount64` at `:208`, `:216`, `:1186`, `:1283`, `:1354`.
So a backward step (an NTP correction, a clock set by hand) makes `UtcNow − lastHeard` small or negative: a bridge that has gone silent stays READY and is never
dropped. A forward step drops a live one. `src/TradeAgent.AtasBridge/BridgeServer.cs:225` stamps `LastAuthFailure` with `UtcNow` for display; it is not liveness.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `A` (the read loop, `IdlePoll` `:534-541`, `PeerHasGoneQuiet`, `GetHealthAsync`, the pulse handlers around `:959-994`,
the accept path around `:629-649`); `tests/TradeAgent.IntegrationTests/BridgeRoundTripTests.cs` (the heartbeat tests and their `Wait` helper);
`U-fix-bridge-heartbeat`'s record in `BUILD-STATUS.md`.
Must NOT: raise `HeartbeatTimeout`, `AuthGrace`, the pulse interval or a test's delay; add a retry; weaken an assert; skip on a platform; change how a dead bridge
is declared beyond the clock it is measured on; read the wall clock for any duration; drop a wall-clock time the owner is SHOWN (keep it beside the monotonic stamp,
for display only).
Items, one commit each, one-sentence messages:
1. **One monotonic clock for liveness:** every liveness stamp and comparison above (`:557`, `:1769`, `:408`, `:458` and their stamps `:639`, `:968`, `:988`) reads one
   monotonic source, the connector's `TickCount64` or a seam over it for tests. Name every other duration in `A` and `BridgeServer.cs` measured on the wall clock as
   covered or left, with the reason.
2. **Tests through seams, deterministic, no sleeps waiting for luck:** (a) a bridge that goes silent while the wall clock steps BACK one hour → DEGRADED and dropped
   once the MONOTONIC timeout passes; (b) a live bridge pulsing while the wall clock steps FORWARD one hour → stays READY; (c) the auth grace under the same backward step.
Proof: (a) and (c) RED before item 1 (READY / not dropped), quoted; (b) red before or shown green with the reason. ONE mutant, quoted: `GetHealthAsync` back on
`DateTimeOffset.UtcNow` → (a) red. `BridgeRoundTripTests` (whole, including the `Timing` member) unchanged and green.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed;
touched classes 3×; branch CI on all three platforms; tests box or NOT RUN; names vs `main` 0 removed (both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
