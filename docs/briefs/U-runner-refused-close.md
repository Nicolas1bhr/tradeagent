# U-runner-refused-close — an order refused before the wire stops freezing a paper run: it is over, a refused exit is tried again on its next eligible minute, and no stop or target rests under no position
**Arrow closed:** an M0 paper run survives its first refusal (orchestrator, 2026-10-04: a NEW PRE-M0 unit); **depends on `U-timeframe-b`** (landing; SOURCE is its tip). Money path, the runner only: `TradingGateway.cs` unchanged.
**Protects (`CLAUDE.md`):** rule 3 — only an op the gateway REFUSED over no request row or one still `CREATED` (never sent: `DISPATCHING` is durable first, `TradingGateway.cs:6209-6211`) is over or followed by a new op;
an op over UNKNOWN or any live row stays in flight, holds the frontier (`ForwardRuns.cs:164-167`, `:351-355`), is never re-sent. Rule 1 — each attempt is a new op under that minute's `dp-` id, its own `TA-` client order id
(`DeploymentStore.cs:213-216`); a replayed minute collapses on its op (`TradingGateway.cs:1057-1066`). No double exit — a re-send follows only a refused exit still the run's latest position-moving op, and the stale-close
read (`:5994-6006`) still refuses a close the venue's position does not match. No gate skipped, no verb, no pipe op.
**Today (SOURCE at `9d8e1d88`, read by the survey leg):** refused BEFORE `TryCreate` — authorization (kill switch, mode, install, unconfirmed work), risk check (allowlist, size, quote age, early rate limit), position gates
(`TradingGateway.cs:5492-5536`) — no row; AFTER it, before `DISPATCHING`, the row stays `CREATED`: `POSITION_MOVED` or a failed position read in it (closes; `:6178`), the re-authorization (`:6198`), `DECISION_EXPIRED` (entries,
rule exits, not the max hold's flatten; `:6203`), the reservation's `RISK_LIMIT_EXCEEDED` (`:6205`); the op is `refused` either way (`:1105-1106`, `:1116-1117`). `Books` counts an entry, exit or flatten in flight while its ROW is
non-terminal, never asking if its op was refused (`ForwardRuns.cs:729-732`); a `CREATED` row never fills, so `RunBooks.At` reads `pending` at every later minute (`:839-840`) and the evaluator suppresses every exit while long and
every entry while flat (`StrategyEvaluator.cs:434-438`, `:536-538`); only the stop/target and the max hold still close, and the run holds its envelope's one slot (`TradingGateway.cs:279`, `:635-656`) until ended: permanent,
read, not run. **Every max-hold close sets it off:** guard (f) pins it and PASSES (RUN 2026-10-04, that worktree's Release build of 10-03 08:07, made after code tip `22d83f47`: `ForwardRunnerTests` 16/16): minute 10's close
fills at 12's open, minute 11 turns live with the books long, the max hold asks again, `POSITION_MOVED` leaves `+11#0` `CREATED`, m12 b reads `pending=True` (`ForwardRunnerDeclaredBarsTests.cs:483`, `:494`); test (d)'s
`DECISION_EXPIRED` entry does the same, unexamined (`ForwardRunnerTests.cs:459-482`); unfrozen, a refused `bars 1h` rule exit waits for the next declared close (`ForwardRuns.cs:291-305`). **Second finding, by reading:** fills
are stamped at settlement (`PaperConnector.cs:584`) and a pass's own first connector read can settle the minute it decides, so a stop or target filled there is first seen next pass; if this pass's refused op moved the cursor
onto it, that minute is not live, the loser's cancel is skipped, the pair forgotten (`ForwardRuns.cs:237-246`); the loser's fill opens a paper short (`PaperBook.cs:510`) the books cannot spell (`ForwardRuns.cs:829-832`).
**Observable result:** a refusal that sent nothing is over: the next minute reads no order in flight and the program enters and exits by its rules again; a refused exit on declared bars goes out again on the next live minute
under its own id while its decision is inside its own bounds; the max hold re-asks every minute; no stop or target of the run is working on a live minute whose books read flat. **No schema change.**
Read first: `CLAUDE.md`; `ForwardRuns.cs`; `TradingGateway.cs:831-871`, `:1053-1124`, `:5477-5560`, `:5960-6211`; `DeploymentStore.cs:39-66`; `StrategyEvaluator.cs:411-445`; `PaperConnector.cs:520-590`; both runner test files.
Items, one commit each, one-sentence messages:
1. `Books` counts an entry, exit or flatten in flight only while its op is not `refused` and its row non-terminal (`ForwardRuns.cs:729-732`): `refused`, written only over no row or a `CREATED` one (`TradingGateway.cs:850-853`,
   `:1105-1106`, `:1116-1117`), is the terminal state; the row stays `CREATED` (outside the gateway's own open set too, `Stores.cs:263-264`; not `REJECTED`, "refused by the broker" at `DashboardView.cs:915`); an op still
   `dispatched` stays in flight. (f)'s transcript is re-captured here on purpose (`ForwardRunnerDeclaredBarsTests.cs:403-406`): by reading only m12 b and a minute-12 entry's op and order change; diff quoted; items 2–3 keep it.
2. On a live minute that closes no declared bar (never on `1m`: there the unfrozen program exits again itself, after its one-bar hold, `StrategyEvaluator.cs:138-146`) while the books read long and the run's latest entry,
   exit or flatten op is an EXIT the gateway refused, that exit's intent, read back from its op with its decision block unchanged, is sized from the books (`ForwardRuns.cs:555-572`) and sent as an `exit` under this
   minute's id, only while `IntentDecision.AgeAt` is inside both its bounds (`GatewayTypes.cs:363-383`; `DECISION_EXPIRED` still decides); then the minute reads `RunBooks.Flat` as after the max hold (`ForwardRuns.cs:286-288`).
3. On every live minute whose books read flat, first, each stop or target of the run still working (row `WORKING`/`ACKNOWLEDGED`, a connector order id) is cancelled under that minute's id (`CancelDeploymentOrderAsync`),
   whatever minute the closing fill landed on and whether an earlier cancel was refused; `ForwardRuns.cs:239-246` stays.
4. `CONTRACTS.md` "The runner" (`:3291-3320`): a refusal before the wire is over, its row stays `CREATED`; the re-send and its bound; the sweep; NOT claimed: a re-send past its bounds, a refused entry re-sent, the race below.
Red-first tests (a `ForwardRunnerTests` partial in `IntegrationTests`): (a) `A_close_refused_before_the_wire_is_not_in_flight_and_the_run_enters_again` — (f)'s minutes and two more: `+11#0` `refused` over `CREATED`,
m12 b `pending=False`, a minute-12 entry filled at 14 (RED: `pending=True`, no op after `+11#0`, as (f) pins); (b) `A_stale_entry_refused_DECISION_EXPIRED_does_not_stop_the_run_entering_again` — test (d)'s
refusal, then fresh minutes that signal: an entry dispatched and filled (RED by reading: none); (c) `A_stop_fill_seen_after_its_minute_settled_still_cancels_the_target_and_no_paper_short_opens` —
resting protection, then a minute appended unannounced and unrefreshed, low through the stop, close under 90, so the exit's own read settles the stop and `POSITION_MOVED` refuses it; a rise through the target finds it
`CANCELLED` and nothing short (RED by reading: −1; if not reproduced, no item 3, (c) a guard, reported); (d) `A_refused_exit_on_declared_bars_is_sent_again_next_minute_while_its_decision_is_fresh` — `EnteredAsync`,
the 13:00 hour closing at 89, its exit refused by a gate staged for that pass (`InstallInProgress`, `TradingGateway.cs:1376`): to minute 123 the exits are `+119` refused, `+120` sent, own `TA-` id, same decision (RED: no `+120`).
Mutants to watch red and quote: (i) refused ops over `CREATED` rows in flight again ⇒ (a), (b) red; (ii) item 3 removed ⇒ (c) red, −1; (iii) re-send after any refused exit, not the latest only ⇒ (d) red, a third exit at 121.
Seen, not in this unit (read, not run): a rule exit goes out with the stop and target still resting — only the max hold cancels first (`ForwardRuns.cs:282-287` vs `:347`) — so a protective fill before the exit's own fill opens
a paper short; an END whose flatten a gate refused is never re-sent (`TradingGateway.cs:1029-1031` sees a flatten op) and reads reconciled; a crash between `TryCreate` and `DISPATCHING` leaves its op unsettled for good (`:861`).
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.

## Report
**Tip `52ad65fb`** (last code commit) on `main` `c15040a5` (`main` has since moved by docs only). Build Release `--no-incremental`: 0 Warning(s), 0 Error(s). Unit 1360 passed, 0 failed (7 m 39 s); Fault 415 passed, 0 failed (1 m 52 s);
`ForwardRunnerTests` (all partials, Integration) 22/22 three times. CI 37169881900 on `52ad65fb`: ubuntu success (13 min), macos success (16 min), windows success (48 min), package success. Names vs `main`: base 2124, tip 2130, removed 0,
added 6.
**Condition: a paper short FREEZES the run, so 3b is folded in.** A sell into a flat book makes −1 (`PaperBook.cs:510`); the run's books close `Math.Min(0, q)` and stay flat (`ForwardRuns.cs:966`, base `:830`); the next entry passes as a
reduce (`TradingGateway.cs:5317`): venue 0, books long 1; every close is then refused `POSITION_MOVED` (`:6033`). Seen on the base by a scratch probe (deleted, never committed): the stop, the target and the max hold's close on each of
minutes 14–20 refused `POSITION_MOVED`, `pending=True` throughout.
1 done, DEVIATION (stricter): out of flight only when `refused` AND the row is absent or still `CREATED` (`RefusedBeforeTheWire`), so a refused op whose row moved on (another process's `PlaceAsync` racing `SettleDeploymentOps`) is read off
its row. (f) re-captured on purpose: m12 b `pending=True intents:3 whilePending:4` → `pending=False intents:4 whilePending:3`, plus `op +12#0 entry dispatched request=WORKING — ` and its `wire +12#0 … WORKING`; items 2–3b keep it.
2 done, DEVIATION in (d) only: the 14:00 hour (exits `+179` refused, `+180` sent, filled at 182), since the evaluator's one-bar hold covers the 13:00 close after the 12:00 entry (the base wrote no exit at 119). The runner asks both bounds
before re-sending; `DECISION_EXPIRED` still decides at the gateway.
3 done: each live flat minute cancels every stop/target of the run still working, read off its own ops. 3b done: every exit (the program's or one sent again) cancels them once sized, before it goes; the tracked pair is not cleared. 4 done:
`CONTRACTS.md` "The runner" (the minute's list, one new paragraph). (c) stores its minutes unannounced since `52ad65fb` (a queued announcement could settle minute 5 before the pass); re-verified red on the base code.
RED before (base): (a) `Assert.False() Failure Expected: False Actual: True` (m12 b); (b) `Assert.Single() Failure: The collection did not contain any matching items` (no minute-13 entry); (c), (c2), (e) `Expected: 0 Actual: -1.000`; (d)
`Expected ["+179#0", "+180#0"] Actual ["+179#0"]`; each also red at the commit before its own fix.
Mutants, all watched again at `52ad65fb`: (i) refused-over-CREATED in flight again → (a) `Actual: True`, (b) no entry, (f) `pending=True`; (ii) item 3 removed → (c2) `Actual: -1.000`; (iii) re-send after any refused exit → (d) `Actual
["+179#0", "+180#0", "+181#0"]`, `+181` refused `POSITION_MOVED`; (iv) 3b's cancel removed → (e) `Actual: -1.000`. (c) stays green under (ii) or (iv) alone, red under both.
NOT done / NOT VERIFIED: protection put back after an exit refused once its cancel went through (stated NOT claimed); an END's refused flatten and the `TryCreate`→`DISPATCHING` crash window (`U-runner-exit-hygiene`); `STRATEGY-LANGUAGE.md`
and the agents' text untouched (nothing there made false); the rest of Integration ran on CI only; no box run; `TradingGateway.cs` unchanged.
