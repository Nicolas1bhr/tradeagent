# U-interlock — the updater counts everything that could still be on the wire, on whichever gateway is live

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the U2d and U2c1a/b/c sections of `BUILD-STATUS.md`
(U2d deferred its "item 10" until U2c-1 landed: the provider must count every wire-touched record through U2c-1's
store query — U2c-1 has landed), then finding 3 in `docs/REVIEW-2026-09-05.md`, EXECUTED by the reviewer (probes P4
and P5 on `review-probes`, `tests/TradeAgent.UnitTests/ReviewUpdateProbes.cs` — lift them): `UnconfirmedWork = () =>
gateway.Requests.NeedingReconciliation()` runs without `strandedDispatchBefore`, so a DISPATCHING order stops trading
and does not stop an install; Codex's F5 says the same. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`;
no `timeout`; full suite 8–12 min in Release. No box. Fresh worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-interlock`, new branch
`u-interlock` from `main`.

**The claim (F5, `src/TradeAgent.Diagnostics/UpdateTradingInterlock.cs` ~:47).** The updater's "unconfirmed work"
provider counts only persisted reconciliation flags. It omits the in-memory unconfirmed latch (a persist failure after
the wire that U2c1a latches) and young DISPATCHING rows (an order handed to the wire seconds ago, not yet settled);
and switching connectors creates a new gateway that the interlock is never attached to, so `InstallAsync`'s hard stop
reads zero and replaces the program holding the owner's open orders.

1. **Everything wire-touched counts.** RED first: hold an outcome-write failure latched but unflagged and invoke
   `InstallAsync` → expect refusal, and it installs today; a fresh DISPATCHING row younger than the aged bound → expect
   refusal. Fix: one query, the gateway's own "unconfirmed work" (U2c-1's: DISPATCHING, UNKNOWN, RECONCILING, flagged,
   the in-memory latch), is what the provider returns — the same answer Dashboard, Doctor, status and authorization
   read, never a second count. `UpdateService.cs:255-265`'s doc comment becomes true and says so.
2. **The interlock follows the live gateway.** RED first: switch connectors during a background check → the new gateway
   has unconfirmed work, `InstallAsync` proceeds today. Fix: `UpdateTradingInterlock.Attach` binds to whatever gateway
   is live (re-attached on switch, or reads through a single indirection), with a test against a real gateway swap.
3. **Both directions.** With nothing on the wire, an update still installs; the refusal reason names the count and
   reaches the strip and Settings in the app's words (U2d's refusal path).

Yours: `src/TradeAgent.Diagnostics/UpdateTradingInterlock.cs`, `src/TradeAgent.Provisioning/UpdateService.cs`, one
query method on `TradingGateway.cs` if none exposes the set already (name it, keep it to that), `AppHost.cs` wiring for
the switch, tests. Not yours: the authorization path, the press, the pipe server, the connectors. Every item: the RED
quoted (or the finding refuted, with the probe), GREEN, one mutant watched red (commit before mutating; `cp` restore;
`touch`). Test-name diff vs baseline: nothing removed. Commit per item, no trailers, no push, no other worktree. Gate:
Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED (or refuted) → GREEN → mutant;
final counts; what you did NOT do. Verified or NOT VERIFIED.

Gate ran at `5f7c690` (this report is the commit on top), rebased onto `main` `a8b8b9b`; 3 commits, 6 files,
+690/−17. All figures macOS, Release.

1. **Everything wire-touched counts.** RED 4 of 6, quoted: on the wire *right now* — "record state while installing:
   DISPATCHING / updater UnconfirmedWork(): 0 / InstallAsync returned: True / Setup launched: 1 time(s)"; stranded —
   "gateway Unreconciled(): 1 / gateway will trade: False (TRADING_PAUSED_UNRECONCILED) / updater UnconfirmedWork(): 0
   / InstallAsync returned: True"; latched — "place threw STATE_DATABASE_CORRUPT / row DISPATCHING,
   needs_reconciliation=False / gateway HasUnconfirmedWork: True / updater UnconfirmedWork(): 0". GREEN: one query,
   `TradingGateway.WireTouched()` (flagged OR DISPATCHING at any age OR UNKNOWN OR RECONCILING, plus the in-memory
   latch), is what the provider returns; `UpdateService`'s doc comment now says so. It is a strict SUPERSET of
   `Unreconciled()`, not a second count, and a test pins that direction — widening the trading gate itself would stop
   trading during every ordinary placement. Mutant (SQL back to `needs_reconciliation=1`) → 4 red.
2. **Follows the live gateway.** RED 2, attached as `AppHost` attached it and then swapped: "store says 0 flagged, 0
   dispatching / B HasUnconfirmedWork(): True / updater UnconfirmedWork(): 0 / InstallAsync returned: True (Setup
   launched 1 time(s))"; and "B will trade during install: True". GREEN: `Attach(Func<TradingGateway?>, …)` re-read at
   every question, wiring `InstallInProgress` onto each gateway it first sees; `AppHost` passes `() => Gateway`, so
   `SwitchConnectorAsync` has no re-attach to forget; a null source answers −1 = refuse. Mutant (`_wired is null`,
   bind once) → 1 red.
3. **Both directions.** RED 1 (the refusal test installed instead). GREEN: a quiet wire still installs (Setup launched
   once, `Refused` false); the refusal is `Refused` + `RefusedPendingWork` + Failed, names the count in the app's words
   ("an order's outcome is" / "2 orders' outcomes are"), and is written once into the activity history the strip and
   the Settings card render. Mutant (count dropped from the sentence) → 3 red.

P4/P5 lifted and turned the right way up; run verbatim on the fix they fail at `Assert.Equal(0, updaterSays)` —
"updater UnconfirmedWork(): 1 / InstallAsync returned: False / Setup launched: 0 time(s)" — then deleted.
Gate: Release `--no-incremental` → 0 warnings, 0 errors. Unit 211 + Fault 195 = 406, 0 failed. **Integration NOT
VERIFIED at this tip:** the run has been starved for 40+ min by 14 orphaned CPU busy-loops another leg left running on
this Mac (test host at 0.6% CPU); the same suite passed 530/530 on this branch's product code before the rebase, and
the rebase brought only `main`'s own commits, and no Integration or Fault test names any of the four changed
classes (`grep -rln 'UpdateTradingInterlock|UpdateService|WireTouched'` over both projects → no files). Test names vs `main`: 0 removed, 10 added. Secret scan of the whole diff
clean. **NOT DONE:** `MainWindow`'s pre-press cosmetic line still reads the narrower `status.UnreconciledRequests`, so
the strip can offer an update seconds before the press is refused (the refusal itself is right); `Doctor` and
`GatewayHost` still ask `HasUnconfirmedWork()` — the trading question — on purpose; `TradingGateway.cs` touched by the
one added query method and nothing else. Nothing on Windows, no UI, no real ATAS.
