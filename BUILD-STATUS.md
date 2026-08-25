# BUILD-STATUS

**Milestone:** Pass A complete — the full vertical slice runs, with the ATAS adapter as the single
remaining unimplemented layer. Pass B (hardening) partially done: reconciliation, fault tests,
restart recovery and the risk/authority model are in. Pass C (fresh-machine acceptance) not started.

**Built and verified on:** macOS 26 / arm64, .NET SDK 10.0.400.
**Target platform:** Windows 11 x64. **Not yet built or run on Windows.**

---

## What works

Verified by `dotnet test TradeAgent.sln` — **86 tests, 0 failures** (29 unit, 21 integration, 36 fault):

- **The vertical slice, end to end.** Agent → `trade` → named pipe → gateway → connector → account,
  and the answer back. Exercised both in-process and as separate OS processes.
- **Duplicate-order prevention**, with a positive control: the harness first proves it *can* detect a
  duplicate (idempotency disabled → two orders at the broker), then proves the real path prevents it
  (one order). Also holds under five concurrent callers sharing one request id.
- **Reconciliation after a lost acknowledgement.** Order accepted by the broker, acknowledgement
  lost → `UNKNOWN` + trading paused → reconcile → adopts the broker's truth, no second order.
- **Reconciliation refuses to guess.** "Absent from the broker" only becomes "never landed" when the
  backend can prove its own order history *and* a grace window has passed. Both halves are tested,
  including the control where a long grace correctly leaves it unresolved.
- **Restart mid-flight.** Killed between dispatch and acknowledgement, the next start still refuses
  to trade, reconciles against the same broker, and creates no new order.
- **Authority model.** Kill switch, four trading modes, live activation (re-armed on leaving live
  mode), confirm-then-place, and five risk limits — each refusing before anything reaches a broker.
- **Emergency controls are separate.** Stopping the AI does not touch orders or positions; cancel-all
  does not liquidate; close-all is its own two-press control.
- **ATAS bridge protocol, both directions.** The real `BridgeServer` (the code that will run inside
  ATAS) against the real `AtasConnector` over real pipes, with only the ATAS API replaced by
  `LoopbackAtasAdapter`. Covers handshake, capability negotiation, protocol-version rejection,
  rejection-vs-timeout classification, reconnect after TradeAgent restarts, and reconciliation.
- **Desktop app starts.** Launches, acquires its single-instance lock, creates the managed install
  (`tools/ bin/ workspace/ logs/ state/ bridge/`), writes its IPC token, hosts the gateway, logs
  "TradeAgent started". No crash.

## What does not work yet

- **ATAS itself.** `AtasStrategyAdapter.cs` is a skeleton of ~14 `NotImplementedException`s with
  per-method instructions. It is the only file that cannot compile or run without ATAS installed;
  everything it plugs into is tested. **The product cannot trade through ATAS until it is written.**
- **Nothing has run on Windows.** No Windows build, no installer run, no fresh-machine test. The CI
  workflow that would do the first two is written but has never executed.
- **OpenCode and Codex install/sign-in commands are unverified.** They ship as overridable data
  (`RuntimeCatalog`), every entry flagged `Verified = false`, and the Doctor says so out loud.
- **ATAS folder layout is unverified** — same pattern (`AtasLayout`, overridable, `Verified = false`).
- **The window's visual layout has never been looked at.** It compiles and runs; nobody has seen it.
- **The installer has never been built.** Inno Setup was not available on the build host.
- **Live money has never been touched.** Correct for this stage — see the trial sequence below.

## Current blockers

1. **A Windows machine with ATAS installed and a broker connection.** Needed for: the ATAS adapter,
   the folder-layout confirmation, the installer, and fresh-machine acceptance. Nothing else blocks.
2. **An OpenCode or Codex account** to confirm the sign-in flow end to end.

Neither can be manufactured here. Everything not gated on them is done.

## Next integration target

1. On Windows: `dotnet test TradeAgent.sln` — expect 86/86. Any failure here is an OS assumption to fix.
2. Fill in `AtasStrategyAdapter.cs` against current official ATAS docs — see
   [docs/RESEARCH-REQUIRED.md](docs/RESEARCH-REQUIRED.md) item **A1**, working method by method
   against `LoopbackAtasAdapter` as the reference.
3. Confirm `AtasLayout` paths and set `Verified = true`.
4. Confirm the runtime manifests and set `Verified = true`.
5. `pwsh packaging/build.ps1 -AtasInstallDir "..."`, install on a clean VM, walk the whole journey.
6. Then, and only then, the live trial sequence: paper → extended paper run → one tiny live order →
   disconnect/recovery test → autonomous live permission.

## Decisions changed from the brief

| Brief said | Built instead | Why |
|---|---|---|
| WinUI / WPF / Avalonia, pick after comparison | **Avalonia**, code-built UI, no XAML | Only option that builds and runs on the dev/CI host as well as Windows; ships self-contained with no runtime prerequisite. |
| Notional cap as a core risk limit | **Opt-in, default off** | One ES future is a six-figure notional on a four-figure margin. Any naively chosen cap refuses every legitimate futures order. Contract count (`MaxOrderQuantity`) is the limit that means something; the notional cap remains available. |
| — | **Added: risk limits, borrowed from venture-agent's `policy.yaml`** | The brief had modes and a kill switch but no bound on size. Five limits (quantity, notional, open positions, orders/minute, instrument allowlist) enforced before the wire. |
| — | **Added: a fresh price is required for every order** | An agent sizing a market order from a stale quote was reachable. Now refused. |
| — | **Added: autonomous live trading refused on unprovable backends** | If a connector cannot round-trip a client order id and serve order history, post-disconnect state is unknowable, so `LIVE_AUTONOMOUS` is refused rather than risked. |
| 4 test projects (Unit/Integration/E2E/Fault) | **3** — E2E folded into Integration | The E2E cases are the integration cases; a fourth project would have been a directory with no distinct contents. |
| `TradeAgent.sln` | **Both `.sln` and `.slnx`** | .NET 10 emits `.slnx`; Visual Studio before 17.13 cannot open it. Both are kept in sync. |
| Operator control over IPC | **Operator authority is in-process only** | The agent-facing pipe carries reads and orders. Mode, kill switch, live activation and approvals are not reachable from it, so an agent wanting more permission has nowhere to ask. |
| Install AI runtimes from code | **Install/auth commands are overridable data** | These vendors change their CLIs on their own schedule. A wrong command is a one-line fix in `runtimes.json`, not a rebuild. |

## Defects found and fixed while building

Recorded because each was found by a test that could have been written to pass instead:

1. **Dispatcher/stream race.** A connector raising `OrderChanged` from inside `PlaceOrderAsync` moved
   the request to `FILLED` before the dispatcher recorded its own outcome, and the dispatcher then
   threw. Fixed at the cause: whoever owns a request writes its outcome; the stream stays out.
2. **Reconciler/stream race.** Same class, one layer up — an event landing during reconciliation made
   a resolved request report as inconclusive, which would have paused trading indefinitely.
3. **Bridge payload field mismatch.** The connector sent `args`; the bridge read `data`. Every
   argument silently vanished. Would have broken the real ATAS bridge on the first order.
4. **Kill switch could not be undone.** `StopAiTrading` expressed itself through the health registry
   as well as settings; `EnableAiTrading` cleared only the setting, so trading stayed blocked. One
   fact now has one owner.
5. **Rate limiter charged idempotent replays.** A safe retry could be refused as a new order.
6. **Default risk limits blocked every order** in the built-in practice simulator (see the notional
   decision above). First-run practice mode was unusable.
7. **`AtasConnector.DisposeAsync` threw on second call**, turning tidy shutdown into a crash.
8. **The store serialised writes but not reads**, on one shared `SqliteConnection`. The gateway reads
   it from the event stream, the background loop and the UI thread simultaneously, so a read could
   race a live transaction — surfacing as a `NullReferenceException` inside the SQLite provider while
   closing the connection. All access now goes through one gate, with a concurrency test.
9. **The agent was told "at most 0 order value"** once the notional cap defaulted to off, which reads
   as "you may not trade at all". Found by reading the generated `AGENTS.md` rather than by a test.
10. **High-severity advisory** in the SQLite native library pulled in by `Microsoft.Data.Sqlite 10.0.0`
   (GHSA-2m69-gcr7-jv3q). Pinned forward.

## Honest note on scope

Two things this cannot change, and the brief does not claim otherwise:

- Retail latency and information access do not compete with firms running colocated systems and paid
  news feeds. This product's value is a safe, auditable, controllable execution chain — not an edge.
- Every safety property above is proven against a simulator and a loopback bridge. They are *designed*
  to hold against ATAS and a real broker; they are not yet *proven* to. That is what the Windows pass
  and the staged live trial are for.
